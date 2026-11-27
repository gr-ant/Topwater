"""git: revisions, the command and its dispatcher, and the pieces the subcommands share (comparing trees, the index and the work tree). See docs/architecture.md."""
from __future__ import annotations

import inspect
import re
from datetime import timezone

from ..vfs import ShellError, normalize, utc_now
from .git import Opts, Repo, SPEC, sub_msg
from .gitcore import (
    Config, GitError, HEX, MAX_WALK, c_quote, canonical_key, count_changes, fatal, glob_regex, is_binary, msg, parse_date,
    parse_ident, split_lines, timed, unified, valid_key,
)


# ---------- revisions ----------

def resolve(repo: Repo, spec: str, sub: str | None = None) -> str:
    """A revision -> object id. Raises GitError('badRevision') when it names nothing."""
    sha = try_resolve(repo, spec)
    if sha is None:
        missing_path(repo, spec)
        raise fatal("badRevision", rev=spec)
    return sha


def missing_path(repo: Repo, spec: str) -> None:
    """rev:path where rev exists but path does not gets its own message."""
    rev, colon, path = spec.partition(":")
    if colon and rev and try_resolve(repo, rev) is not None:
        raise fatal("pathNotInRevision", path=path, rev=rev)


def try_resolve(repo: Repo, spec: str) -> str | None:
    if not spec:
        return None
    if ":" in spec and not spec.startswith(":/"):
        rev, _, path = spec.partition(":")
        if rev == "":
            e = repo.stage0().get(path.lstrip("/"))
            return e.sha if e else None
        tree = peel(repo, try_resolve(repo, rev), "tree")
        if tree is None:
            return None
        if path in ("", "."):
            return tree
        p = path[2:] if path.startswith("./") else path
        if path.startswith("./") or path.startswith("../"):
            try:
                p = repo.from_user(path)
            except GitError:
                return None
        return lookup_path(repo, tree, p.strip("/"))
    m = re.fullmatch(r"(.*?)((?:[~^]\d*|\^\{\w*\})*)", spec, re.S)
    base, suffix = m.group(1), m.group(2)
    sha = resolve_base(repo, base)
    if sha is None:
        return None
    for op in re.findall(r"~\d*|\^\{\w*\}|\^\d*", suffix):
        if op.startswith("^{"):
            want = op[2:-1]
            sha = peel(repo, sha, want or "any")
        elif op[0] == "~":
            n = int(op[1:] or 1)
            for _ in range(n):
                c = peel(repo, sha, "commit")
                if c is None:
                    return None
                ps = repo.commit(c).parents
                sha = ps[0] if ps else None
                if sha is None:
                    return None
        else:
            n = int(op[1:] or 1)
            c = peel(repo, sha, "commit")
            if c is None:
                return None
            if n == 0:
                sha = c
                continue
            ps = repo.commit(c).parents
            sha = ps[n - 1] if len(ps) >= n else None
        if sha is None:
            return None
    return sha


def resolve_base(repo: Repo, name: str) -> str | None:
    if name in ("@", "HEAD") or name == "":
        return repo.head() if name else None
    m = re.fullmatch(r"@\{-(\d+)\}", name)
    if m:
        b = previous_branch(repo, int(m.group(1)))
        return repo.read_ref("refs/heads/" + b) if b else None
    m = re.fullmatch(r"(.*)@\{(?:u|upstream)\}", name)
    if m:
        up = upstream(repo, upstream_branch(repo, m.group(1)))
        return repo.read_ref(up[1]) if up else None
    for r in (name, "refs/" + name, "refs/tags/" + name, "refs/heads/" + name, "refs/remotes/" + name, "refs/remotes/" + name + "/HEAD"):
        if r == name and not (name.isupper() or name.startswith("refs/")):
            continue
        sha = repo.read_ref(r)
        if sha:
            return sha
    if HEX.fullmatch(name):
        if len(name) == 40:
            return name if repo.has(name) else None
        return unique_prefix(repo, name)
    return None


def unique_prefix(repo: Repo, prefix: str) -> str | None:
    d = repo._gpath(f"objects/{prefix[:2]}")
    hits = {prefix[:2] + n for n in repo.fs.listdir(d) if (prefix[:2] + n).startswith(prefix) and len(n) == 38} if repo.fs.is_dir(d, follow=False) else set()
    for pack in repo.packs():
        hits.update(pack.with_prefix(prefix))
    hits = sorted(hits)
    if len(hits) > 1:
        raise fatal("ambiguousPrefix", prefix=prefix)
    return hits[0] if hits else None


def detached_name(repo: Repo) -> tuple[bool, str]:
    """(HEAD is still there, what to call it) for a detached HEAD: the ref the last checkout named, else the commit it
    went to, from the HEAD reflog; git says 'detached at' while HEAD has not moved since and 'detached from' after."""
    head = repo.head() or ""
    for line in reversed((repo.gtext("logs/HEAD") or "").split("\n")):
        m = re.fullmatch(r"[0-9a-f]{40} ([0-9a-f]{40}) [^\t]*\tcheckout: moving from \S+ to (\S+)", line)
        if not m:
            continue
        for ref in (m.group(2), "refs/tags/" + m.group(2), "refs/heads/" + m.group(2), "refs/remotes/" + m.group(2)):
            sha = repo.read_ref(ref) if ref.startswith("refs/") else None
            if sha:
                short = re.sub(r"^refs/(heads|tags|remotes)/", "", ref)
                return peel(repo, sha, "commit") == head, short
        return m.group(1) == head, abbrev(m.group(1))
    return True, abbrev(head)


def previous_branch(repo: Repo, n: int) -> str | None:
    text = repo.gtext("logs/HEAD") or ""
    seen = 0
    for line in reversed(text.split("\n")):
        m = re.search(r"\tcheckout: moving from (\S+) to (\S+)$", line)
        if m:
            seen += 1
            if seen == n:
                return m.group(1)
    return None


def peel(repo: Repo, sha: str | None, want: str) -> str | None:
    """Follows tags (and commit -> tree) until an object of kind want ('commit', 'tree', 'any' = not a tag)."""
    for _ in range(16):
        if sha is None:
            return None
        kind, data = repo.obj(sha)
        if kind == want or (want == "any" and kind != "tag"):
            return sha
        if want == "tag":
            return None
        if kind == "tag":
            m = re.match(rb"object ([0-9a-f]{40})", data)
            sha = m.group(1).decode() if m else None
        elif kind == "commit" and want == "tree":
            sha = repo.commit(sha).tree
        else:
            return None
    return None


def lookup_path(repo: Repo, tree: str, path: str) -> str | None:
    sha = tree
    for part in [p for p in path.split("/") if p]:
        if repo.obj(sha)[0] != "tree":
            return None
        found = [s for mode, n, s in repo.tree(sha) if n == part]
        if not found:
            return None
        sha = found[0]
    return sha


def commit_of(repo: Repo, spec: str) -> str:
    sha = peel(repo, resolve(repo, spec), "commit")
    if sha is None:
        raise fatal("notCommit", rev=spec)
    return sha


def tree_files(repo: Repo, commit: str | None) -> dict[str, tuple[str, str]]:
    return repo.flatten(repo.commit(commit).tree) if commit else {}


def ancestors(repo: Repo, start, check) -> set[str]:
    seen, stack = set(), list(start)
    while stack:
        c = stack.pop()
        if c in seen:
            continue
        seen.add(c)
        if len(seen) > MAX_WALK:
            raise fatal("tooLarge", what="history")
        if len(seen) % 256 == 0:
            check()
        stack.extend(repo.commit(c).parents)
    return seen


def merge_base(repo: Repo, a: str, b: str, check) -> str | None:
    """A best common ancestor: a shared ancestor that is not itself an ancestor of another shared one (newest if several)."""
    common = ancestors(repo, [a], check) & ancestors(repo, [b], check)
    if not common:
        return None
    best = common - ancestors(repo, [p for c in common for p in repo.commit(c).parents], check)
    return walk_order(repo, sorted(best), check)[0] if best else None


def upstream(repo: Repo, branch: str) -> tuple[str, str] | None:
    """(name to show, ref) of a branch's upstream, from branch.<name>.remote and .merge (as git clone sets them)."""
    cfg = repo.config()
    remote, merge = cfg.get(f"branch.{branch}.remote"), cfg.get(f"branch.{branch}.merge")
    if not remote or not merge or not merge.startswith("refs/heads/"):
        return None
    if remote == ".":
        return merge[11:], merge
    # shortcut: assumes the default fetch refspec (refs/remotes/<remote>/*) that clone writes; read remote.<name>.fetch if others show up
    return f"{remote}/{merge[11:]}", f"refs/remotes/{remote}/{merge[11:]}"


def tracking(repo: Repo, branch: str, check) -> tuple[str, int, int, bool] | None:
    """(upstream name, commits ahead, commits behind, upstream gone) for a branch with an upstream."""
    up = upstream(repo, branch)
    ours = repo.read_ref("refs/heads/" + branch)
    if up is None or ours is None:
        return None
    theirs = repo.read_ref(up[1])
    if theirs is None:
        return up[0], 0, 0, True
    a, b = ancestors(repo, [ours], check), ancestors(repo, [theirs], check)
    return up[0], len(a - b), len(b - a), False


def tracking_text(repo: Repo, branch: str, check) -> str | None:
    """The 'Your branch is ...' lines git status and checkout print."""
    t = tracking(repo, branch, check)
    if t is None:
        return None
    name, ahead, behind, gone = t
    M = lambda key, **v: sub_msg("status", key, **v)
    count = lambda n: M("oneCommit") if n == 1 else M("commits", n=str(n))
    if gone:
        return M("upstreamGone", upstream=name)
    if ahead and behind:
        return M("diverged", upstream=name, ahead=str(ahead), behind=str(behind))
    if ahead:
        return M("ahead", upstream=name, count=count(ahead))
    if behind:
        return M("behind", upstream=name, count=count(behind))
    return M("upToDate", upstream=name)


def tracking_brief(repo: Repo, branch: str, check) -> tuple[str, str] | None:
    """(upstream name, 'ahead 1, behind 2' or 'gone' or '') for status -sb and branch -vv."""
    t = tracking(repo, branch, check)
    if t is None:
        return None
    name, ahead, behind, gone = t
    M = lambda key, **v: sub_msg("status", key, **v)
    if gone:
        return name, M("trackGone")
    return name, ", ".join(([M("trackAhead", n=str(ahead))] if ahead else []) + ([M("trackBehind", n=str(behind))] if behind else []))


def remote_branches(repo: Repo) -> list[tuple[str, str | None, str | None]]:
    """[(name below refs/remotes/, commit, symbolic target below refs/remotes/ or None)] sorted by name."""
    rows = [(name[13:], sha, None) for name, sha in repo.refs("refs/remotes/")]
    base = "refs/remotes"
    if repo.fs.is_dir(repo._gpath(base), follow=False):
        for remote in repo.fs.listdir(repo._gpath(base)):
            rel = f"{base}/{remote}/HEAD"
            if repo.fs.is_dir(repo._gpath(f"{base}/{remote}"), follow=False) and repo.fs.is_file(repo._gpath(rel), follow=False):
                text = (repo.gtext(rel) or "").strip()
                if text.startswith("ref: refs/remotes/"):
                    rows.append((f"{remote}/HEAD", repo.read_ref(rel), text[18:]))
    return sorted(rows, key=lambda r: r[0].encode())


def upstream_branch(repo: Repo, name: str) -> str:
    """The branch <name>@{u} asks about (HEAD and '' mean the current one); refuses as git does when there is none."""
    if name in ("", "HEAD", "@"):
        b = repo.branch()
        if b is None:
            raise fatal("headNotBranch")
    else:
        b = name
        if repo.read_ref("refs/heads/" + b) is None:
            raise fatal("noSuchBranch", name=b)
    if upstream(repo, b) is None:
        raise fatal("noUpstream", branch=b)
    return b


def walk_order(repo: Repo, starts: list[str], check, limit_set=None) -> list[str]:
    """Commits reachable from starts, newest commit date first (ties: first seen first), each once."""
    import heapq
    heap, seen, out, counter = [], set(), [], 0
    for s in starts:
        if s not in seen:
            seen.add(s)
            heapq.heappush(heap, (-commit_time(repo, s), counter, s))
            counter += 1
    while heap:
        _, _, c = heapq.heappop(heap)
        out.append(c)
        if len(out) > MAX_WALK:
            raise fatal("tooLarge", what="history")
        if len(out) % 256 == 0:
            check()
        for p in repo.commit(c).parents:
            if p not in seen and (limit_set is None or p in limit_set):
                seen.add(p)
                heapq.heappush(heap, (-commit_time(repo, p), counter, p))
                counter += 1
    return out


def commit_time(repo: Repo, sha: str) -> int:
    return parse_ident(repo.commit(sha).committer)[2]


# ---------- the command ----------

def valid_head_target(target: str) -> bool:
    return target.startswith("refs/") and all(part not in ("", ".", "..") for part in target.split("/"))


def valid_head(fs, path: str) -> bool:
    """A HEAD that names neither a commit nor a ref under refs/ makes the directory not a repository, as in git."""
    if fs.stat(path).size > 4096:
        return False
    text = fs.read_bytes(path).decode("utf-8", "replace").strip()
    return bool(re.fullmatch(r"[0-9a-f]{40}", text)) or (text.startswith("ref:") and valid_head_target(text[4:].strip()))


class Git:
    """One git invocation: the shell context, the directory it works from (after -C) and -c overrides."""

    def __init__(self, ctx, cwd: str, overrides: list[tuple[str, str]]):
        self.ctx, self.cwd, self.overrides = ctx, cwd, overrides
        self._repo: Repo | None = None
        self.decor = None

    def out(self, s: str) -> None:
        self.ctx.stdout.write(s)

    def err(self, s: str) -> None:
        self.ctx.stderr.write(s)

    def env(self, name: str) -> str | None:
        sh = self.ctx.shell
        return sh.env.get(name) if name in sh.env and sh.is_exported(name) else None

    def find_repo(self) -> Repo | None:
        if self._repo is None:
            fs = self.ctx.fs
            d = self.cwd
            while True:
                g = d.rstrip("/") + "/.git"
                if fs.is_dir(g, follow=False) and fs.is_file(g + "/HEAD", follow=False) and valid_head(fs, g + "/HEAD"):
                    self._repo = Repo(self, d.rstrip("/") or "/", g)
                    rel = self.cwd[len(d.rstrip("/")):].strip("/")
                    self._repo.prefix = rel + "/" if rel else ""
                    break
                if fs.is_symlink(g):
                    raise fatal("symlinkInGitDir", path=g)
                if d == "/":
                    break
                d = d.rsplit("/", 1)[0] or "/"
        return self._repo

    def repo(self) -> Repo:
        r = self.find_repo()
        if r is None:
            raise fatal("notRepo")
        return r

    def global_config(self) -> tuple[str, Config]:
        path = normalize("/", (self.env("HOME") or "/") + "/.gitconfig")
        fs = self.ctx.fs
        text = fs.read_bytes(path).decode("utf-8", "replace") if fs.is_file(path) and fs.stat(path).size <= 1 << 20 else ""
        return path, Config(text)

    def config(self, name: str) -> str | None:
        for k, v in reversed(self.overrides):
            if k == name:
                return v
        r = self.find_repo()
        if r is not None:
            v = r.config().get(name)
            if v is not None:
                return v
        return self.global_config()[1].get(name)

    def now(self):
        return utc_now()

    def ident(self, kind: str, required: bool = True, name: str | None = None, email: str | None = None, date: str | None = None) -> str:
        k = kind.upper()
        name = name or self.env(f"GIT_{k}_NAME") or self.config("user.name") or self.ctx.shell.user
        email = email if email is not None else self.env(f"GIT_{k}_EMAIL") or self.config("user.email") or self.env("EMAIL")
        if email is None:
            if required:
                raise GitError(msg("identityUnknown", guess=f"{self.ctx.shell.user}@{self.ctx.shell.hostname}.(none)"), 128)
            email = f"{self.ctx.shell.user}@{self.ctx.shell.hostname}.(none)"
        when = date or self.env(f"GIT_{k}_DATE")
        name, email = clean_ident(name), clean_ident(email)
        if not name and required:
            raise GitError(msg("emptyIdentName", email=email), 128)
        if when:
            try:
                epoch, zone = parse_date(when, self.now())
            except ValueError:
                raise GitError(msg("badDate", date=when), 128) from None
        else:
            epoch, zone = int(self.now().replace(tzinfo=timezone.utc).timestamp()), "+0000"
        return f"{name} <{email}> {epoch} {zone}"

    def editor_error(self, existing: str | None = None) -> str:
        """No message was given: with GIT_EDITOR / core.editor / VISUAL / EDITOR set to true or :, keep the existing
        message (amend, merge); otherwise say why no editor can open."""
        editor = self.env("GIT_EDITOR") or self.config("core.editor") or self.env("VISUAL") or self.env("EDITOR")
        if editor is not None and editor.strip() in ("true", ":") and existing is not None:
            return existing
        raise GitError(sub_msg("commit", "editorCannotRun" if editor else "noEditor"), 1)


def clean_ident(s: str) -> str:
    """Newlines and angle brackets would break the commit header, so they go, as in git."""
    return "".join(c for c in s if c not in "<>\n\r").strip()


async def _git(ctx) -> int:
    args = list(ctx.args)
    cwd = ctx.shell.cwd
    overrides: list[tuple[str, str]] = []
    i = 0
    while i < len(args) and args[i].startswith("-"):
        a = args[i]
        if a in ("--version", "-v"):
            ctx.print(SPEC.version)
            return 0
        if a in ("--help", "-h"):
            ctx.stdout.write(SPEC.help)
            return 0
        if a in ("-C", "-c") and i + 1 >= len(args):
            ctx.stderr.write(msg("globalNeedsValue", option=a) + "\n")
            return 129
        if a == "-C":
            path = args[i + 1]
            if path:
                target = normalize(cwd, path)
                if not ctx.fs.is_dir(target):
                    ctx.stderr.write(msg("cannotChdir", path=path) + "\n")
                    return 128
                cwd = target
            i += 2
        elif a == "-c":
            k, eq, v = args[i + 1].partition("=")
            if not valid_key(k):
                ctx.stderr.write(msg("badConfigKey", key=k) + "\n")
                return 129
            overrides.append((canonical_key(k), v if eq else "true"))
            i += 2
        elif a in ("--no-pager", "-P", "--paginate", "-p", "--no-replace-objects", "--literal-pathspecs", "--no-optional-locks"):
            i += 1
        else:
            ctx.stderr.write(msg("unknownGlobalOption", option=a) + "\n" + msg("globalUsage") + "\n")
            return 129
    if i >= len(args):
        ctx.stdout.write(SPEC.help)
        return 1
    sub, rest = args[i], args[i + 1:]
    s = SPEC.subcommands.get(sub)
    if s is not None and "--help" in rest[:rest.index("--") if "--" in rest else len(rest)]:
        ctx.stdout.write(s.help)
        return 0
    if sub == "version":
        ctx.print(SPEC.version)
        return 0
    if sub == "help":
        topic = next((a for a in rest if not a.startswith("-")), None)
        if topic is None:
            ctx.stdout.write(SPEC.help)
            return 0
        s = SPEC.subcommands.get(topic)
        if s is None:
            ctx.stderr.write(msg("noHelp", name=topic) + "\n")
            return 1
        ctx.stdout.write(s.help)
        return 0
    if s is None:
        ctx.stderr.write(msg("unknownCommand", name=sub) + "\n")
        return 1
    if s.unsupported:
        ctx.stderr.write(sub_msg(sub, "unsupported", name=sub) + "\n")
        return 128
    if rest == ["-h"]:
        ctx.stdout.write(s.help)
        return 129
    g = Git(ctx, cwd, overrides)
    from .gitpatch import HANDLERS
    try:
        result = HANDLERS[sub](g, rest)
        if inspect.isawaitable(result):  # bisect run awaits the commands it runs
            result = await result
        return result or 0
    except GitError as e:
        text = str(e)
        if text:
            ctx.stderr.write(text if text.endswith("\n") else text + "\n")
        return e.code
    except ShellError as e:
        ctx.stderr.write(msg("ioError", error=str(e)) + "\n")
        return 128


def register(shell) -> None:
    shell.register_command("git", _git)


# ---------- shared pieces: comparing trees, the index and the work tree ----------

def pathspec_match(specs: list[str] | None, path: str) -> bool:
    """specs are paths from the top ('' = everything); a spec matches itself, anything below it, or as a glob."""
    if specs is None:
        return True
    for s in specs:
        if s == "" or path == s or path.startswith(s.rstrip("/") + "/"):
            return True
        if any(c in s for c in "*?[") and glob_match(s, path):
            return True
    return False


_GLOBS: dict = {}


def glob_match(pattern: str, path: str) -> bool:
    rx = _GLOBS.get(pattern)
    if rx is None:
        try:
            rx = timed(glob_regex(pattern, False) + r"\Z", re.S)
        except re.error:
            rx = False  # a malformed class matches nothing
        if len(_GLOBS) < 256:
            _GLOBS[pattern] = rx
    return bool(rx) and rx.match(path) is not None


def user_regex(pattern: str | None):
    if pattern is None:
        return None
    try:
        return timed(pattern)
    except re.error:
        raise GitError(msg("badRegex", pattern=pattern), 128) from None


def user_specs(repo: Repo, items: list[str]) -> list[str] | None:
    if not items:
        return None
    return [repo.from_user(p) for p in items]


def diff_files(a: dict, b: dict, specs=None, load=None) -> list[tuple[str, str | None, str | None]]:
    """[(status, old path, new path)] between two {path: (mode, sha)} maps, sorted by path. A removed and an added path
    with the same contents pair up as a rename ("R"); with load(side, path) -> bytes, so do ones at least half alike
    ("R075": the similarity in percent)."""
    out = []
    for p in sorted(set(a) | set(b), key=lambda x: x.encode("utf-8", "surrogateescape")):
        if not pathspec_match(specs, p):
            continue
        x, y = a.get(p), b.get(p)
        if x is None:
            out.append(["A", None, p])
        elif y is None:
            out.append(["D", p, None])
        elif x != y:
            kind = lambda m: "l" if m == "120000" else "f"
            out.append(["T" if kind(x[0]) != kind(y[0]) else "M", p, p])
    added = {}
    for r in out:
        if r[0] == "A":
            added.setdefault(b[r[2]], []).append(r)
    for r in [r for r in out if r[0] == "D"]:
        cands = added.get(a[r[1]])
        if cands:
            n = cands.pop(0)
            n[0], n[1] = "R", r[1]
            out.remove(r)
    if load is not None:
        gone = [r for r in out if r[0] == "D"]
        new = [r for r in out if r[0] == "A"]
        if gone and new and len(gone) * len(new) <= RENAME_PAIRS:
            src = {r[1]: chunk_counts(load("a", r[1])) for r in gone}
            dst = {r[2]: chunk_counts(load("b", r[2])) for r in new}
            scores = []
            for r in gone:
                for n in new:
                    s = similarity(src[r[1]], dst[n[2]])
                    if s >= 50:
                        scores.append((-s, n[2], r[1], r, n))
            used = set()
            for s, _, _, r, n in sorted(scores, key=lambda x: (x[0], x[1].encode("utf-8", "surrogateescape"), x[2].encode("utf-8", "surrogateescape"))):
                if id(r) in used or id(n) in used:
                    continue
                used |= {id(r), id(n)}
                n[0], n[1] = f"R{-s:03d}", r[1]
                out.remove(r)
    return [tuple(r) for r in out]


RENAME_PAIRS = 10_000  # removed x added paths compared for inexact renames


def chunk_counts(data: bytes) -> tuple[dict[bytes, int], int]:
    """Content split into pieces that end at a newline or after 64 bytes, counted, for rename similarity."""
    counts: dict[bytes, int] = {}
    i = 0
    while i < len(data):
        nl = data.find(b"\n", i, i + 64)
        end = nl + 1 if nl >= 0 else min(i + 64, len(data))
        piece = data[i:end]
        counts[piece] = counts.get(piece, 0) + 1
        i = end
    return counts, len(data)


def similarity(x, y) -> int:
    (cx, nx), (cy, ny) = x, y
    if max(nx, ny) == 0 or min(nx, ny) * 2 < max(nx, ny):
        return 0
    copied = sum(min(n, cy.get(k, 0)) * len(k) for k, n in cx.items())
    return copied * 100 // max(nx, ny)


def worktree_files(repo: Repo, paths) -> dict[str, tuple[str, str]]:
    out = {}
    for p in paths:
        w = repo.wt_id(p)
        if w is not None:
            out[p] = w
    return out


def index_files(entries) -> dict[str, tuple[str, str]]:
    return {e.path: (e.mode, e.sha) for e in entries if e.stage == 0}


class Status:
    def __init__(self, repo: Repo, untracked_mode: str = "normal"):
        self.repo = repo
        self.head = repo.head()
        self.entries = repo.index()
        idx = index_files(self.entries)
        self.unmerged = repo.unmerged(self.entries)
        head_files = tree_files(repo, self.head)
        self.staged = [c for c in diff_files(head_files, idx, load=lambda side, p: repo.typed((head_files if side == "a" else idx)[p][1], "blob"))
                       if (c[2] or c[1]) not in self.unmerged]
        work = worktree_files(repo, idx)
        self.unstaged = []
        for p in sorted(idx, key=lambda x: x.encode("utf-8", "surrogateescape")):
            if p in self.unmerged:
                continue
            w = work.get(p)
            if w is None:
                self.unstaged.append(("D", p, None))
            elif w != idx[p]:
                kind = lambda m: "l" if m == "120000" else "f"
                self.unstaged.append(("T" if kind(w[0]) != kind(idx[p][0]) else "M", p, p))
        tracked = set(idx) | set(self.unmerged)
        self.untracked = untracked(repo, tracked, untracked_mode) if untracked_mode != "no" else []


def untracked(repo: Repo, tracked: set[str], mode: str) -> list[str]:
    dirs = set()
    for p in tracked:
        parts = p.split("/")
        for k in range(1, len(parts)):
            dirs.add("/".join(parts[:k]))
    out = []
    count = [0]

    def has_files(rel: str) -> bool:
        for p in repo.walk(rel):
            if not repo.ignored(p.rstrip("/"), p.endswith("/")):
                return True
        return False

    pending = [""]  # a stack, not recursion: work trees can be deeper than Python's recursion limit
    while pending:
        rel = pending.pop()
        try:
            names = sorted(repo.fs.listdir(repo.wt(rel)), key=lambda x: x.encode("utf-8", "surrogateescape"))
        except ShellError:
            continue
        for name in names:
            count[0] += 1
            if count[0] % 256 == 0:
                repo.ctx.check_time()
            if name == ".git":
                continue
            r = rel + "/" + name if rel else name
            full = repo.wt(r)
            if repo.fs.is_dir(full, follow=False):
                if repo.ignored(r, True) or r in tracked:
                    continue
                if repo.fs.exists(full + "/.git", follow=False) and r not in dirs:
                    out.append(r + "/")
                elif r in dirs or mode == "all":
                    pending.append(r)
                elif has_files(r):
                    out.append(r + "/")
            elif r not in tracked and not repo.ignored(r, False):
                out.append(r)
    return sorted(out, key=lambda x: x.encode("utf-8", "surrogateescape"))


def blob_data(repo: Repo, side: tuple | None, path: str, from_worktree: bool) -> bytes:
    if side is None:
        return b""
    if from_worktree:
        s = repo.wt_stat(path)
        return s[1] if s else b""
    return repo.typed(side[1], "blob")


def abbrev(sha: str) -> str:
    return sha[:7]


def rename_label(a: str, b: str) -> str:
    """'a => b', or with the shared directories factored out: 'dir/{a => b}/f'."""
    pre = 0
    for i in range(min(len(a), len(b))):
        if a[i] != b[i]:
            break
        if a[i] == "/":
            pre = i + 1
    suf = 0
    for i in range(1, min(len(a), len(b)) - pre + 1):
        if a[-i] != b[-i]:
            break
        if a[-i] == "/":
            suf = i
    if pre or suf:
        return f"{a[:pre]}{{{a[pre:len(a) - suf]} => {b[pre:len(b) - suf]}}}{a[len(a) - suf:]}"
    return f"{a} => {b}"


def write_diff(g: Git, repo: Repo, a: dict, b: dict, opts: Opts, specs=None, b_worktree=False, a_worktree=False, out=None) -> bool:
    """Prints the differences between two {path: (mode, sha)} maps in the format opts asks for; True when any."""
    write = out or g.out
    changes = diff_files(a, b, specs, load=lambda side, p: blob_data(repo, (a if side == "a" else b)[p], p, a_worktree if side == "a" else b_worktree))
    if not changes:
        return False
    if opts.has("\x05"):  # --quiet
        return True
    if opts.has("\x03"):  # --name-only
        for st, old, new in changes:
            write(c_quote(new or old) + "\n")
        return True
    if opts.has("\x04"):  # --name-status
        for st, old, new in changes:
            write(f"R{score(st):03d}\t{c_quote(old)}\t{c_quote(new)}\n" if st[0] == "R" else f"{st}\t{c_quote(new or old)}\n")
        return True
    stats = []
    for st, old, new in changes:
        x = blob_data(repo, a.get(old) if old else None, old or "", a_worktree)
        y = blob_data(repo, b.get(new) if new else None, new or "", b_worktree)
        if is_binary(x) or is_binary(y):
            stats.append((st, old, new, None, len(x), len(y)))
        else:
            ins, dels = count_changes(x, y, g.ctx.check_time) if x != y else (0, 0)
            stats.append((st, old, new, (ins, dels), len(x), len(y)))
    if opts.has("\x06"):  # --numstat
        for st, old, new, n, _, _ in stats:
            name = f"{old} => {new}" if st[0] == "R" else (new or old)
            write(("-\t-\t" if n is None else f"{n[0]}\t{n[1]}\t") + name + "\n")
        return True
    if opts.has("\x01") or opts.has("\x02") or opts.has("\x1f"):  # --stat / --shortstat / --summary
        if opts.has("\x01") or opts.has("\x02"):
            write_stat(write, stats, short=not opts.has("\x01"))
        if opts.has("\x1f"):
            for line in summary_lines(changes, a, b):
                write(line + "\n")
        return True
    context = int(opts.val("U", "3")) if opts.val("U", "3").isdigit() else 3
    for st, old, new in changes:
        if st == "T":  # a file became a link or the other way: shown as a deletion and an addition
            write_patch(g, repo, write, "D", old, None, a.get(old), None, context, a_worktree, b_worktree)
            write_patch(g, repo, write, "A", None, new, None, b.get(new), context, a_worktree, b_worktree)
            continue
        write_patch(g, repo, write, st, old, new, a.get(old) if old else None, b.get(new) if new else None, context, a_worktree, b_worktree)
    return True


def write_stat(write, stats, short=False, summary=None) -> None:
    files = len(stats)
    ins = sum(s[3][0] for s in stats if s[3])
    dels = sum(s[3][1] for s in stats if s[3])
    if not short:
        names = [rename_label(old, new) if st[0] == "R" else c_quote(new or old) for st, old, new, *_ in stats]
        counts = [(str(s[3][0] + s[3][1]) if s[3] else "Bin") for s in stats]
        cw = max(len(c) for c in counts)
        max_change = max((s[3][0] + s[3][1]) for s in stats if s[3]) if any(s[3] for s in stats) else 0
        nw = max(len(n) for n in names)
        limit = 80 - cw - 6
        nw = min(nw, limit)
        gw = 80 - nw - cw - 6
        for (st, old, new, n, sa, sb), name, count in zip(stats, names, counts):
            if len(name) > nw:
                name = "..." + name[len(name) - nw + 3:]
            if n is None:
                write(f" {name.ljust(nw)} | {'Bin'.rjust(cw)} {sa} -> {sb} bytes\n" if sa != sb or True else "")
                continue
            plus, minus = n
            if max_change > gw > 0:
                plus = 1 + plus * (gw - 1) // max_change if plus else 0
                minus = 1 + minus * (gw - 1) // max_change if minus else 0
            graph = "+" * plus + "-" * minus
            write(f" {name.ljust(nw)} | {count.rjust(cw)}" + (f" {graph}" if graph else "") + "\n")
    line = f" {files} file{'s' if files != 1 else ''} changed"
    if ins or not dels:
        line += f", {ins} insertion{'s' if ins != 1 else ''}(+)"
    if dels or not ins:
        line += f", {dels} deletion{'s' if dels != 1 else ''}(-)"
    write(line + "\n")


def write_patch(g, repo, write, st, old, new, x, y, context, a_worktree, b_worktree) -> None:
    an, bn = old or new, new or old
    write(f"diff --git {c_quote('a/' + an)} {c_quote('b/' + bn)}\n")
    xd = blob_data(repo, x, old or "", a_worktree)
    yd = blob_data(repo, y, new or "", b_worktree)
    xs = x[1] if x else "0" * 40
    ys = y[1] if y else "0" * 40
    if st == "A":
        write(f"new file mode {y[0]}\n")
    elif st == "D":
        write(f"deleted file mode {x[0]}\n")
    elif x[0] != y[0]:
        write(f"old mode {x[0]}\nnew mode {y[0]}\n")
    if st[0] == "R":
        write(f"similarity index {score(st)}%\nrename from {c_quote(old)}\nrename to {c_quote(new)}\n")
    if xs == ys:
        return
    same_mode = x and y and x[0] == y[0]
    write(f"index {abbrev(xs)}..{abbrev(ys)}" + (f" {x[0]}" if same_mode else "") + "\n")
    if is_binary(xd) or is_binary(yd):
        write(msg("binaryDiffer", a="/dev/null" if st == "A" else c_quote("a/" + an), b="/dev/null" if st == "D" else c_quote("b/" + bn)) + "\n")
        return
    write(f"--- {'/dev/null' if st == 'A' else c_quote('a/' + an)}\n+++ {'/dev/null' if st == 'D' else c_quote('b/' + bn)}\n")
    for line in unified(split_lines(xd), split_lines(yd), context, check=g.ctx.check_time):
        write(line + "\n")


def score(st: str) -> int:
    return int(st[1:]) if len(st) > 1 else 100


def summary_lines(changes, a, b) -> list[str]:
    out = []
    for st, old, new in changes:
        if st == "A":
            out.append(f" create mode {b[new][0]} {c_quote(new)}")
        elif st == "D":
            out.append(f" delete mode {a[old][0]} {c_quote(old)}")
        elif st[0] == "R":
            out.append(f" rename {rename_label(old, new)} ({score(st)}%)")
        elif a[old][0] != b[new][0] and st == "M":
            out.append(f" mode change {a[old][0]} => {b[new][0]} {c_quote(new)}")
    return out


def tree_loader(repo, a, b):
    return lambda side, p: repo.typed((a if side == "a" else b)[p][1], "blob")


def stat_entries(g, repo, a, b, changes):
    stats = []
    for st, old, new in changes:
        x = repo.typed(a[old][1], "blob") if old else b""
        y = repo.typed(b[new][1], "blob") if new else b""
        if is_binary(x) or is_binary(y):
            stats.append((st, old, new, None, len(x), len(y)))
        else:
            stats.append((st, old, new, count_changes(x, y, g.ctx.check_time) if x != y else (0, 0), len(x), len(y)))
    return stats
