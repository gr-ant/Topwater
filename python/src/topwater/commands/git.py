"""git: a local version control system over the virtual filesystem (see gitcore.py for the formats). No network, no
remotes; the .git directory it writes is a real repository."""
from __future__ import annotations

import inspect
import re
import zlib
from datetime import timezone

from .. import bytetext
from .._spec import COMMANDS
from ..vfs import ShellError, get_parent, normalize, utc_now
from . import spec_format, spec_message, spec_sub_message
from .text import posix_regex_error, posix_to_python
from .gitpack import MAX_NAMES, MAX_PACKS, Corrupt, ObjectCache, Pack
from .gitcore import (EMPTY_TREE, timed, HEX, MAX_DEPTH, MAX_WALK, Commit, Config, Entry, GitError, IgnoreRules, c_quote, canonical_key,
                      check_path, config_bool, count_changes, fatal, format_date, glob_regex, is_binary, local_time, merge_lines,
                      msg, myers, object_id, parse_date, parse_ident, parse_tree, read_index, split_lines, tree_bytes, unified,
                      valid_key, write_index)

SPEC = COMMANDS["git"]


def sub_msg(sub: str, key: str, /, **values) -> str:
    return spec_sub_message("git", sub, key, **values)


def sub_fatal(sub: str, key: str, /, code: int = 128, **values) -> GitError:
    return GitError(sub_msg(sub, key, **values), code)


# ---------- option parsing ----------

class Opts:
    def __init__(self):
        self.flags: dict[str, int] = {}
        self.values: dict[str, list[str]] = {}
        self.args: list[str] = []  # operands before "--"
        self.paths: list[str] = []  # operands after "--"
        self.dashdash = False

    def has(self, k: str) -> bool:
        return k in self.flags

    def val(self, k: str, default=None):
        v = self.values.get(k)
        return v[-1] if v else default

    def vals(self, k: str) -> list[str]:
        return self.values.get(k, [])


def usage_error(sub: str, text: str) -> GitError:
    return GitError(text + "\n" + SPEC.subcommands[sub].help.rstrip("\n"), 129)


def parse(sub: str, args: list[str]) -> Opts:
    flags, valued, longs = SPEC.subcommands[sub].options
    mapping = dict(x.split("=", 1) for x in longs.split(",") if x)
    if sub in ("diff", "log", "show"):  # --color[=when] is listed as custom: accepted and ignored (output is never colored)
        args = [a for a in args if not re.fullmatch(r"--color(=.*)?", a)]
    o = Opts()
    i = 0
    while i < len(args):
        a = args[i]
        i += 1
        if a == "--":
            o.dashdash = True
            o.paths = args[i:]
            break
        if a.startswith("--"):
            name, eq, inline = a[2:].partition("=")
            neg = name.startswith("no-") and name not in mapping and name[3:] in mapping
            key = mapping.get(name[3:] if neg else name)
            if key is None:
                raise usage_error(sub, msg("unknownOption", option=name))
            if neg:
                o.flags.pop(key, None)
                o.values.pop(key, None)
                o.flags["!" + key] = 1
                continue
            if key in valued:
                if not eq:
                    if i >= len(args):
                        raise GitError(msg("optionNeedsValue", option=name), 129)
                    inline = args[i]
                    i += 1
                o.values.setdefault(key, []).append(inline)
                o.flags[key] = o.flags.get(key, 0) + 1
            else:
                if eq:
                    raise GitError(msg("optionTakesNoValue", option=name), 129)
                o.flags[key] = o.flags.get(key, 0) + 1
            continue
        if len(a) < 2 or a[0] != "-":
            o.args.append(a)
            continue
        k = 1
        while k < len(a):
            c = a[k]
            if c in valued:
                v = a[k + 1:]
                if not v:
                    if i >= len(args):
                        raise GitError(msg("switchNeedsValue", option=c), 129)
                    v = args[i]
                    i += 1
                o.values.setdefault(c, []).append(v)
                o.flags[c] = o.flags.get(c, 0) + 1
                break
            if c not in flags:
                raise usage_error(sub, msg("unknownSwitch", option=c))
            o.flags[c] = o.flags.get(c, 0) + 1
            k += 1
    return o


# ---------- the repository ----------

def packed_name_ok(name: str) -> bool:
    """A packed-refs name git could have written: under refs/, the characters loose refs allow, no empty, '.'-led or
    '..' component and no .lock name, so nothing read from the file can name a path outside refs/."""
    return (name.startswith("refs/") and re.fullmatch(r"[A-Za-z0-9_./-]+", name) is not None
            and all(c and not c.startswith(".") and not c.endswith(".lock") for c in name.split("/")))


class Repo:
    def __init__(self, g: "Git", top: str, gitdir: str):
        self.g, self.ctx, self.fs = g, g.ctx, g.ctx.fs
        self.top, self.git = top, gitdir
        self.limit = g.ctx.shell.max_string_bytes
        self._cache = ObjectCache()  # objects, commits and pack delta bases, under one byte budget
        self._packs: list[Pack] | None = None
        self._config = None
        self._ignores: dict[str, IgnoreRules | None] = {}
        self.prefix = ""  # the cwd below the top ("sub/dir/"), for relative paths

    # --- files under .git, never through a symbolic link ---
    def _gpath(self, rel: str) -> str:
        p = self.git
        for part in rel.split("/"):
            if part in ("", ".", ".."):
                raise fatal("invalidPath", path=rel)
            p += "/" + part
            if self.fs.is_symlink(p):
                raise fatal("symlinkInGitDir", path=p)
        return p

    def gread(self, rel: str) -> bytes | None:
        p = self._gpath(rel)
        if not self.fs.is_file(p, follow=False):
            return None
        if self.fs.stat(p).size > self.limit:
            raise fatal("tooLarge", what=p)
        return self.fs.read_bytes(p)

    def gtext(self, rel: str) -> str | None:
        b = self.gread(rel)
        return None if b is None else b.decode("utf-8", "replace")

    def gwrite(self, rel: str, data: bytes) -> None:
        p = self._gpath(rel)
        d = p.rsplit("/", 1)[0]
        if not self.fs.is_dir(d, follow=False):
            self.fs.mkdir(d, parents=True)
        self.fs.write_bytes(p, data)

    def gremove(self, rel: str) -> None:
        p = self._gpath(rel)
        if self.fs.exists(p, follow=False):
            self.fs.delete(p)

    def gexists(self, rel: str) -> bool:
        return self.fs.exists(self._gpath(rel), follow=False)

    # --- objects ---
    def obj(self, sha: str) -> tuple[str, bytes]:
        hit = self._cache.get(sha)
        if hit:
            return hit
        if any(sha in p.offsets for p in self.packs()):  # packs first, as git does: a dictionary lookup, no file walk
            return self._packed(sha)
        raw = self.gread(f"objects/{sha[:2]}/{sha[2:]}") if re.fullmatch(r"[0-9a-f]{40}", sha) else None
        if raw is None:
            return self._packed(sha)
        try:
            d = zlib.decompressobj()
            data = d.decompress(raw, self.limit + 64)
            if d.unconsumed_tail:
                raise fatal("tooLarge", what=f"object {sha}")
        except zlib.error:
            raise fatal("corruptObject", object=sha) from None
        nul = data.find(b"\0")
        head = data[:nul].decode("ascii", "replace").split(" ") if nul > 0 else []
        if len(head) != 2 or head[0] not in ("blob", "tree", "commit", "tag") or not head[1].isdigit() or int(head[1]) != len(data) - nul - 1:
            raise fatal("corruptObject", object=sha)
        result = (head[0], data[nul + 1:])
        if object_id(*result) != sha:
            raise fatal("corruptObject", object=sha)
        self._cache.put(sha, result, len(result[1]))
        return result

    def packs(self) -> list[Pack]:
        """The packs under objects/pack that have an index, loaded and verified on first use."""
        if self._packs is None:
            self._packs = []
            d = "objects/pack"
            names = sorted(n for n in self.fs.listdir(self._gpath(d)) if n.endswith(".idx")) if self.fs.is_dir(self._gpath(d), follow=False) else []
            if len(names) > MAX_PACKS:
                raise fatal("tooLarge", what=self._gpath(d))
            for name in names:
                pack = self.gread(f"{d}/{name[:-4]}.pack")
                if pack is not None:  # like git, an index without its pack is skipped
                    self._packs.append(Pack(self._gpath(f"{d}/{name[:-4]}.pack"), self.gread(f"{d}/{name}") or b"", pack, self.limit,
                                            self.ctx.check_time, self._cache))
                    if sum(len(p.names) for p in self._packs) > MAX_NAMES:
                        raise fatal("tooLarge", what=self._gpath(d))
        return self._packs

    def _packed(self, sha: str) -> tuple[str, bytes]:
        for pack in self.packs():
            if sha in pack.offsets:
                try:
                    result = pack.read(sha)
                except Corrupt:
                    raise fatal("corruptObject", object=sha) from None
                if object_id(*result) != sha:
                    raise fatal("corruptObject", object=sha)
                self._cache.put(sha, result, len(result[1]))
                return result
        raise fatal("missingObject", object=sha)

    def has(self, sha: str) -> bool:
        return any(sha in p.offsets for p in self.packs()) or self.gexists(f"objects/{sha[:2]}/{sha[2:]}")

    def put(self, kind: str, data: bytes) -> str:
        sha = object_id(kind, data)
        if not self.has(sha):
            self.gwrite(f"objects/{sha[:2]}/{sha[2:]}", zlib.compress(f"{kind} {len(data)}\0".encode() + data))
        return sha

    def typed(self, sha: str, kind: str) -> bytes:
        k, data = self.obj(sha)
        if k != kind:
            raise fatal("wrongType", object=sha, kind=kind)
        return data

    def commit(self, sha: str) -> Commit:
        c = self._cache.get(("commit", sha))
        if c:
            return c
        data = self.typed(sha, "commit").decode("utf-8", "surrogateescape")
        head, _, message = data.partition("\n\n")
        tree, parents, author, committer = None, [], "", ""
        for line in head.split("\n"):
            k, _, v = line.partition(" ")
            if k == "tree":
                tree = v
            elif k == "parent":
                parents.append(v)
            elif k == "author":
                author = v
            elif k == "committer":
                committer = v
        if not tree or not re.fullmatch(r"[0-9a-f]{40}", tree) or any(not re.fullmatch(r"[0-9a-f]{40}", p) for p in parents):
            raise fatal("corruptObject", object=sha)
        c = Commit(sha, tree, parents, author, committer, message)
        self._cache.put(("commit", sha), c, len(data) + 200)
        return c

    def tree(self, sha: str):
        return parse_tree(self.typed(sha, "tree"), sha)

    def flatten(self, tree_sha: str | None, prefix: str = "", depth: int = 0, out=None) -> dict[str, tuple[str, str]]:
        """{path: (mode, sha)} for every file and link in a tree."""
        out = {} if out is None else out
        if tree_sha is None or tree_sha == EMPTY_TREE:
            return out
        if depth > MAX_DEPTH:
            raise fatal("tooDeep", object=tree_sha)
        for mode, name, sha in self.tree(tree_sha):
            self.ctx.check_time()
            if mode == "040000":
                self.flatten(sha, prefix + name + "/", depth + 1, out)
            elif mode != "160000":
                out[prefix + name] = (mode, sha)
            if len(out) > 1_000_000:
                raise fatal("tooLarge", what=f"tree {tree_sha}")
        return out

    def write_tree(self, files: dict[str, tuple[str, str]]) -> str:
        root: dict = {}
        for path, (mode, sha) in files.items():
            node = root
            parts = path.split("/")
            for p in parts[:-1]:
                node = node.setdefault(p, {})
                if not isinstance(node, dict):
                    raise fatal("invalidPath", path=path)
            node[parts[-1]] = (mode, sha)

        def build(node) -> str:
            entries = []
            for name, v in node.items():
                if isinstance(v, dict):
                    entries.append(("040000", name, build(v)))
                else:
                    entries.append((v[0], name, v[1]))
            return self.put("tree", tree_bytes(entries))
        return build(root)

    # --- refs ---
    def read_ref(self, name: str, depth: int = 0) -> str | None:
        """The object a ref (HEAD, refs/heads/x, ...) points to, following symbolic refs; None when it doesn't exist."""
        if depth > 5:
            return None
        if not re.fullmatch(r"[A-Za-z0-9_./-]+", name) or ".." in name or name.startswith("/") or name.endswith("/"):
            return None
        text = self.gtext(name) if not self.fs.is_dir(self._gpath(name), follow=False) else None
        if text is not None:
            text = text.strip()
            if text.startswith("ref:"):
                return self.read_ref(text[4:].strip(), depth + 1)
            return text if re.fullmatch(r"[0-9a-f]{40}", text) else None
        return self.packed_refs().get(name)

    def packed_refs(self) -> dict[str, str]:
        text = self.gtext("packed-refs") or ""
        out = {}
        for line in text.split("\n"):
            parts = line.split(" ")
            if len(parts) == 2 and re.fullmatch(r"[0-9a-f]{40}", parts[0]) and packed_name_ok(parts[1]):
                out[parts[1]] = parts[0]
        return out

    def head_ref(self) -> str | None:
        """The branch HEAD names (refs/heads/x), or None when detached."""
        text = (self.gtext("HEAD") or "").strip()
        if not text.startswith("ref:"):
            return None
        target = text[4:].strip()
        if not valid_head_target(target):
            raise fatal("badSymbolicRef", path="HEAD", target=target)
        return target

    def head(self) -> str | None:
        return self.read_ref("HEAD")

    def branch(self) -> str | None:
        r = self.head_ref()
        return r[len("refs/heads/"):] if r and r.startswith("refs/heads/") else r

    def update_ref(self, name: str, sha: str, log: str | None = None) -> None:
        if name == "HEAD" and self.head_ref():
            name = self.head_ref()
        old = self.read_ref(name) or "0" * 40
        self.gwrite(name, (sha + "\n").encode())
        if log is not None:
            self.reflog(name, old, sha, log)
            if self.head_ref() == name:
                self.reflog("HEAD", old, sha, log)
        elif name == "HEAD":
            pass

    def reflog(self, name: str, old: str, new: str, message: str) -> None:
        who = self.g.ident("committer", required=False)
        line = f"{old} {new} {who}\t{message}\n".encode("utf-8", "surrogateescape")
        rel = "logs/" + name
        prev = self.gread(rel) or b""
        self.gwrite(rel, prev + line)

    def set_head(self, target: str, log: str | None = None, detach: bool = False) -> None:
        """Points HEAD at a branch (refs/heads/x) or, detached, at a commit."""
        old = self.head() or "0" * 40
        self.gwrite("HEAD", (target + "\n" if detach else f"ref: {target}\n").encode())
        if log is not None:
            self.reflog("HEAD", old, target if detach else (self.read_ref(target) or "0" * 40), log)

    def delete_ref(self, name: str) -> None:
        self.gremove(name)
        self.gremove("logs/" + name)
        packed = self.gtext("packed-refs")
        if packed and f" {name}" in packed:
            lines = packed.split("\n")
            out, skip = [], False
            for line in lines:
                if skip and line.startswith("^"):
                    continue
                skip = line.endswith(" " + name)
                if not skip:
                    out.append(line)
            self.gwrite("packed-refs", "\n".join(out).encode())

    def refs(self, prefix: str) -> list[tuple[str, str]]:
        """[(name, sha)] under prefix (refs/heads/), loose and packed, sorted by name."""
        found = {k: v for k, v in self.packed_refs().items() if k.startswith(prefix)}
        base = self._gpath(prefix.rstrip("/"))
        if self.fs.is_dir(base, follow=False):
            stack = [prefix.rstrip("/")]
            while stack:
                rel = stack.pop()
                for name in self.fs.listdir(self._gpath(rel)):
                    self.ctx.check_time()
                    p = rel + "/" + name
                    full = self._gpath(p)
                    if self.fs.is_dir(full, follow=False):
                        stack.append(p)
                    elif self.fs.is_file(full, follow=False):
                        v = (self.gtext(p) or "").strip()
                        if re.fullmatch(r"[0-9a-f]{40}", v):
                            found[p] = v
                    if len(found) > 100_000:
                        raise fatal("tooLarge", what="refs")
        return sorted(found.items(), key=lambda kv: kv[0].encode())

    # --- the index ---
    def index(self) -> list[Entry]:
        data = self.gread("index")
        if data is None:
            return []
        return read_index(data, 1_000_000)

    def save_index(self, entries) -> None:
        self.gwrite("index", write_index(list(entries)))

    def stage0(self, entries=None) -> dict[str, Entry]:
        return {e.path: e for e in (self.index() if entries is None else entries) if e.stage == 0}

    def unmerged(self, entries=None) -> dict[str, dict[int, Entry]]:
        out: dict[str, dict[int, Entry]] = {}
        for e in (self.index() if entries is None else entries):
            if e.stage:
                out.setdefault(e.path, {})[e.stage] = e
        return out

    # --- config ---
    def config(self) -> Config:
        if self._config is None:
            self._config = Config(self.gtext("config") or "")
        return self._config

    def save_config(self) -> None:
        self.gwrite("config", self.config().text().encode())

    # --- the work tree ---
    def wt(self, rel: str) -> str:
        return self.top.rstrip("/") + "/" + rel if rel else self.top

    def wt_reachable(self, rel: str) -> bool:
        """Whether rel's parent directories are real directories (git never looks through a symbolic link)."""
        p = self.top.rstrip("/")
        for k, part in enumerate(rel.split("/")[:-1]):
            p += "/" + part
            if k % 32 == 31:
                self.ctx.check_time()
            if not self.fs.is_dir(p, follow=False):
                return False
        return True

    def wt_stat(self, rel: str):
        """(mode, data) of a work tree file or link, or None when there is none."""
        if not self.wt_reachable(rel):
            return None
        p = self.wt(rel)
        if self.fs.is_symlink(p):
            return "120000", self.fs.read_link(p).encode("utf-8", "surrogateescape")
        if not self.fs.is_file(p, follow=False):
            return None
        st = self.fs.stat(p)
        if st.size > self.limit:
            raise fatal("tooLarge", what=rel)
        return ("100755" if st.mode & 0o100 else "100644"), self.fs.read_bytes(p)

    def wt_id(self, rel: str):
        """(mode, blob id) of a work tree path, or None."""
        s = self.wt_stat(rel)
        return None if s is None else (s[0], object_id("blob", s[1]))

    def wt_write(self, rel: str, mode: str, sha: str) -> None:
        check_path(rel)
        data = self.typed(sha, "blob")
        p = self.top
        for part in rel.split("/")[:-1]:
            p += "/" + part
            if self.fs.is_symlink(p) or (self.fs.exists(p, follow=False) and not self.fs.is_dir(p, follow=False)):
                self.fs.delete(p)
            if not self.fs.exists(p, follow=False):
                self.fs.mkdir(p)
        full = self.wt(rel)
        if self.fs.is_symlink(full) or self.fs.is_dir(full, follow=False):
            self.fs.delete(full, recursive=True)
        if mode == "120000":
            if self.fs.exists(full, follow=False):
                self.fs.delete(full)
            self.fs.create_symlink(full, data.decode("utf-8", "surrogateescape"))
            return
        self.fs.write_bytes(full, data)
        want = (0o777 if mode == "100755" else 0o666) & ~self.ctx.shell.umask
        if self.fs.stat(full).mode & 0o777 != want:
            self.fs.chmod(full, want)

    def wt_remove(self, rel: str) -> None:
        if not self.wt_reachable(rel):
            return
        full = self.wt(rel)
        if self.fs.exists(full, follow=False) and not self.fs.is_dir(full, follow=False):
            self.fs.delete(full)
        # empty parent directories go too
        parts = rel.split("/")[:-1]
        while parts:
            d = self.wt("/".join(parts))
            if self.g.cwd == d or self.g.cwd.startswith(d + "/"):
                break  # the directory git runs in stays
            if self.fs.is_dir(d, follow=False) and not self.fs.listdir(d):
                self.fs.delete(d)
            else:
                break
            parts.pop()

    def entry_for(self, rel: str, mode: str, sha: str) -> Entry:
        p = self.wt(rel)
        try:
            st = self.fs.stat(p, follow=False)
            return Entry(rel, sha, mode, 0, int(st.modified.replace(tzinfo=timezone.utc).timestamp()), st.size)
        except ShellError:
            return Entry(rel, sha, mode)

    def walk(self, base: str = "", untracked_only_in=None):
        """Every file and link under base (relative to the top), not descending into .git, symbolic links or nested
        repositories. Yields (rel, is_dir_marker): nested repositories come back as 'dir/' entries."""
        stack = [base]
        count = 0
        while stack:
            rel = stack.pop()
            full = self.wt(rel)
            try:
                names = sorted(self.fs.listdir(full))
            except ShellError:
                continue
            for name in names:
                count += 1
                if count % 256 == 0:
                    self.ctx.check_time()
                if name == ".git":
                    continue
                r = rel + "/" + name if rel else name
                p = self.wt(r)
                if self.fs.is_dir(p, follow=False):
                    if self.fs.exists(p + "/.git", follow=False):
                        yield r + "/"
                    else:
                        stack.append(r)
                else:
                    yield r

    def ignore_rules(self, d: str) -> IgnoreRules | None:
        if d not in self._ignores:
            rules = None
            p = self.wt((d + "/" if d else "") + ".gitignore")
            if self.wt_reachable((d + "/" if d else "") + ".gitignore") and self.fs.is_file(p, follow=False) and self.fs.stat(p).size <= 1 << 20:
                rules = IgnoreRules(self.fs.read_bytes(p).decode("utf-8", "replace"), d)
            self._ignores[d] = rules
        return self._ignores[d]

    def exclude_rules(self) -> list[IgnoreRules]:
        if "\0exclude" not in self._ignores:
            rules = []
            t = self.gtext("info/exclude")
            if t:
                rules.append(IgnoreRules(t, ""))
            self._ignores["\0exclude"] = rules  # type: ignore[assignment]
        return self._ignores["\0exclude"]  # type: ignore[return-value]

    def ignored(self, rel: str, is_dir: bool = False) -> bool:
        """Whether an untracked path is ignored, by .gitignore files from its own directory up, then info/exclude. A path
        inside an ignored directory is ignored too."""
        parts = rel.split("/")
        for i in range(1, len(parts)):
            if self._ignored_here("/".join(parts[:i]), True):
                return True
        return self._ignored_here(rel, is_dir)

    def _ignored_here(self, rel: str, is_dir: bool) -> bool:
        parts = rel.split("/")
        for i in range(len(parts) - 1, -1, -1):
            r = self.ignore_rules("/".join(parts[:i]))
            if r:
                m = r.match(rel, is_dir)
                if m is not None:
                    return m
        for r in self.exclude_rules():
            m = r.match(rel, is_dir)
            if m is not None:
                return m
        return False

    # --- paths as the user gives and sees them ---
    def from_user(self, path: str) -> str:
        """A path the user typed (relative to the cwd) -> relative to the top; error when outside the repository."""
        full = normalize(self.g.cwd, path)
        if full == self.top:
            return ""
        top = self.top.rstrip("/")
        if not full.startswith(top + "/"):
            raise fatal("outsideRepo", path=path, top=self.top)
        return full[len(top) + 1:]

    def show(self, rel: str) -> str:
        """rel (from the top) as seen from the cwd."""
        if not self.prefix:
            return rel
        pre = self.prefix.rstrip("/").split("/")
        trailing = rel.endswith("/")
        parts = rel.rstrip("/").split("/")
        k = 0
        while k < len(pre) and k < len(parts) and pre[k] == parts[k]:
            k += 1
        out = "/".join([".."] * (len(pre) - k) + parts[k:])
        return (out + "/" if trailing and out else out) or "./"


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


# ---------- init ----------

def git_init(g: Git, args) -> int:
    o = parse("init", args)
    if len(o.args + o.paths) > 1:
        raise usage_error("init", msg("tooManyArguments"))
    target = normalize(g.cwd, (o.args + o.paths)[0]) if o.args + o.paths else g.cwd
    fs = g.ctx.fs
    if not fs.exists(target):
        fs.mkdir(target, parents=True)
    gd = target.rstrip("/") + "/.git"
    if fs.is_symlink(gd):
        raise fatal("symlinkInGitDir", path=gd)
    shown = gd + "/"
    if fs.is_dir(gd, follow=False) and fs.is_file(gd + "/HEAD", follow=False):
        if not o.has("q"):
            g.out(sub_msg("init", "reinitialized", path=shown) + "\n")
        return 0
    branch = o.val("b") or g.config("init.defaultbranch")
    for d in ("objects/info", "objects/pack", "refs/heads", "refs/tags", "info", "hooks", "branches"):
        fs.mkdir(gd + "/" + d, parents=True)
    if not branch:
        branch = "master"
    if not re.fullmatch(r"[A-Za-z0-9._/-]+", branch) or ".." in branch or branch.endswith((".", "/", ".lock")) or branch.startswith(("-", "/")):
        raise sub_fatal("init", "badBranchName", name=branch)
    fs.write_text(gd + "/HEAD", f"ref: refs/heads/{branch}\n")
    fs.write_text(gd + "/config", "[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n\tlogallrefupdates = true\n")
    fs.write_text(gd + "/description", "Unnamed repository; edit this file 'description' to name the repository.\n")
    fs.write_text(gd + "/info/exclude", "# git ls-files --others --exclude-from=.git/info/exclude\n# Lines that start with '#' are comments.\n")
    if not o.has("q"):
        g.out(sub_msg("init", "initialized", path=shown) + "\n")
    return 0


# ---------- status ----------

def git_status(g: Git, args) -> int:
    args = ["--porcelain" if a in ("--porcelain=v1", "--porcelain=1") else a for a in args]
    umode = "normal"
    rest = []
    for a in args:
        m = re.fullmatch(r"(?:-u|--untracked-files)(?:=?(no|normal|all))?", a)
        if m:
            umode = m.group(1) or "all"
        else:
            rest.append(a)
    o = parse("status", rest)
    repo = g.repo()
    specs = user_specs(repo, o.args + o.paths)
    s = Status(repo, umode)
    if specs is not None:
        s.staged = [c for c in s.staged if pathspec_match(specs, c[2] or c[1])]
        s.unstaged = [c for c in s.unstaged if pathspec_match(specs, c[1])]
        s.untracked = [u for u in s.untracked if pathspec_match(specs, u.rstrip("/")) or any(x.startswith(u) for x in specs)]
        s.unmerged = {k: v for k, v in s.unmerged.items() if pathspec_match(specs, k)}
    porcelain = o.has("\x01")
    if o.has("s") or porcelain:
        if o.has("b"):
            g.out("## " + branch_line(repo) + "\n")
        show = (lambda p: short_quote(p)) if porcelain else (lambda p: short_quote(repo.show(p)))
        for line in short_lines(s, show):
            g.out(line + "\n")
        return 0
    g.out(long_status(repo, s))
    return 0


def short_quote(p: str) -> str:
    q = c_quote(p)
    return f'"{p}"' if q == p and " " in p else q


def branch_line(repo: Repo) -> str:
    b = repo.branch()
    if b is None:
        return "HEAD (no branch)"
    if not repo.head():
        return sub_msg("status", "noCommitsOnBranch", branch=b)
    t = tracking_brief(repo, b, repo.ctx.check_time)
    return b if t is None else f"{b}...{t[0]}" + (f" [{t[1]}]" if t[1] else "")


UNMERGED_CODES = {(1, 2, 3): "UU", (2, 3): "AA", (1,): "DD", (1, 2): "UD", (1, 3): "DU", (2,): "AU", (3,): "UA"}
UNMERGED_LABELS = {"UU": "bothModified", "AA": "bothAdded", "DD": "bothDeleted", "UD": "deletedByThem", "DU": "deletedByUs",
                   "AU": "addedByUs", "UA": "addedByThem"}


def short_lines(s: Status, show) -> list[str]:
    rows: dict[str, list] = {}
    for st, old, new in s.staged:
        p = new or old
        rows[p] = [st[0], " ", old if st[0] == "R" else None]
    for st, old, _ in s.unstaged:
        rows.setdefault(old, [" ", " ", None])[1] = st
    for p, stages in s.unmerged.items():
        rows[p] = [UNMERGED_CODES[tuple(sorted(stages))], "", None]
    out = []
    for p in sorted(rows, key=lambda x: x.encode("utf-8", "surrogateescape")):
        x, y, old = rows[p]
        out.append(f"{x}{y} " + (f"{show(old)} -> {show(p)}" if old else show(p)))
    out += [f"?? {show(u)}" for u in s.untracked]
    return out


def long_status(repo: Repo, s: Status, committing: bool = False) -> str:
    M = lambda key, **v: sub_msg("status", key, **v)
    out = []
    b = repo.branch()
    if b is None:
        at, name = detached_name(repo)
        out.append(M("detachedAt" if at else "detachedFrom", commit=name))
    else:
        out.append(M("onBranch", branch=b))
        t = tracking_text(repo, b, repo.ctx.check_time) if s.head else None
        if t:
            out += [t, ""]
    merging = repo.gexists("MERGE_HEAD")
    if s.head is None:
        out += ["", M("initialCommit") if committing else M("noCommitsYet"), ""]
    if merging:
        out.append(M("unmergedHeader") if s.unmerged else M("mergeFixed"))
        out.append("")
    label = lambda key: (M(key) + " ").ljust(12)
    show = lambda p: c_quote(repo.show(p))
    if s.staged:
        out.append(M("toBeCommitted"))
        if not merging:
            out.append(M("hintUnstageNoCommits") if s.head is None else M("hintUnstage"))
        for st, old, new in s.staged:
            key = {"A": "labelNew", "M": "labelModified", "D": "labelDeleted", "R": "labelRenamed", "T": "labelTypechange"}[st[0]]
            out.append("\t" + label(key) + (f"{show(old)} -> {show(new)}" if st[0] == "R" else show(new or old)))
        out.append("")
    if s.unmerged:
        out.append(M("unmergedPaths"))
        if not merging:
            out.append(M("hintUnstage"))
        out.append(M("hintResolve"))
        for p, stages in sorted(s.unmerged.items()):
            code = UNMERGED_CODES[tuple(sorted(stages))]
            out.append("\t" + (M(UNMERGED_LABELS[code]) + " ").ljust(17) + show(p))
        out.append("")
    if s.unstaged:
        out.append(M("notStaged"))
        out.append(M("hintAddRm") if any(c[0] == "D" for c in s.unstaged) else M("hintAdd"))
        out.append(M("hintRestore"))
        for st, old, _ in s.unstaged:
            key = {"M": "labelModified", "D": "labelDeleted", "T": "labelTypechange"}[st]
            out.append("\t" + label(key) + show(old))
        out.append("")
    if s.untracked:
        out.append(M("untrackedHeader"))
        out.append(M("hintTrack"))
        for u in s.untracked:
            out.append("\t" + show(u))
        out.append("")
    if not s.staged:
        if s.unstaged or s.unmerged:
            out.append(M("noChangesAdded"))
        elif s.untracked:
            out.append(M("nothingAddedUntracked"))
        elif s.head is None:
            out.append(M("nothingToCommitInitial"))
        else:
            out.append(M("nothingToCommitClean"))
    return "\n".join(out) + "\n"


# ---------- add / rm / mv ----------

def git_add(g: Git, args) -> int:
    o = parse("add", args)
    repo = g.repo()
    items = o.args + o.paths
    if not items and not o.has("A") and not o.has("u"):
        g.err(sub_msg("add", "nothingSpecified") + "\n")
        return 0
    specs = user_specs(repo, items) if items else [""]
    entries = repo.index()
    idx = {e.path: e for e in entries if e.stage == 0}
    unmerged = repo.unmerged(entries)
    tracked = set(idx) | set(unmerged)
    # every spec must match something: a tracked path or a work tree file
    ignored_hits = []
    candidates = set()
    for spec, raw in zip(specs, items or ["."]):
        hit = False
        full = repo.wt(spec)
        if any(pathspec_match([spec], p) for p in tracked):
            hit = True
        if spec == "" or repo.fs.exists(full, follow=False):
            hit = True
        if any(c in spec for c in "*?["):
            for p in repo.walk(""):
                if pathspec_match([spec], p.rstrip("/")):
                    hit = True
                    break
        if not hit:
            raise fatal("pathspecNoMatch", path=raw)
        is_dir = repo.fs.is_dir(full, follow=False)
        if spec and repo.fs.exists(full, follow=False) and not any(pathspec_match([spec], p) for p in tracked) and repo.ignored(spec, is_dir):
            if not o.has("f"):
                ignored_hits.append(spec)
    if ignored_hits:
        g.err(sub_msg("add", "ignoredPaths", paths="\n".join(repo.show(p) for p in ignored_hits)) + "\n")
        return 1
    changed = []
    # tracked paths: update or remove
    for p in sorted(tracked):
        if not pathspec_match(specs, p):
            continue
        w = repo.wt_stat(p)
        if w is None:
            idx.pop(p, None)
            unmerged.pop(p, None)
            changed.append(("remove", p))
            continue
        sha = object_id("blob", w[1])
        if p in unmerged or idx[p].sha != sha or idx[p].mode != w[0]:
            changed.append(("add", p))
            if not o.has("n"):
                repo.put("blob", w[1])
            unmerged.pop(p, None)
            idx[p] = repo.entry_for(p, w[0], sha)
    if not o.has("u"):
        for spec in specs:
            full = repo.wt(spec)
            if spec and not repo.fs.is_dir(full, follow=False) and not any(c in spec for c in "*?["):
                files = [spec] if repo.fs.exists(full, follow=False) and repo.wt_reachable(spec) else []
            else:
                files = [p for p in repo.walk(spec if spec and repo.fs.is_dir(full, follow=False) else "") if not p.endswith("/")]
                files = [p for p in files if pathspec_match([spec], p) and not repo.ignored(p)]
                for p in repo.walk(spec if spec and repo.fs.is_dir(full, follow=False) else ""):
                    if p.endswith("/") and pathspec_match([spec], p.rstrip("/")):
                        raise sub_fatal("add", "nestedRepo", path=p.rstrip("/"))
            for p in files:
                if p in idx or p in unmerged:
                    continue
                check_path(p)
                w = repo.wt_stat(p)
                if w is None:
                    continue
                sha = repo.put("blob", w[1]) if not o.has("n") else object_id("blob", w[1])
                idx[p] = repo.entry_for(p, w[0], sha)
                changed.append(("add", p))
    if o.has("v") or o.has("n"):
        for what, p in sorted(set(changed), key=lambda c: c[1]):
            g.out(f"{what} '{repo.show(p)}'\n")
    if not o.has("n"):
        repo.save_index(list(idx.values()) + [e for st in unmerged.values() for e in st.values()])
    return 0


def git_rm(g: Git, args) -> int:
    o = parse("rm", args)
    repo = g.repo()
    items = o.args + o.paths
    if not items:
        raise usage_error("rm", sub_msg("rm", "noPathspec"))
    entries = repo.index()
    idx = {e.path: e for e in entries if e.stage == 0}
    unmerged = repo.unmerged(entries)
    tracked = sorted(set(idx) | set(unmerged))
    head = tree_files(repo, repo.head())
    targets = []
    for raw in items:
        spec = repo.from_user(raw)
        hits = [p for p in tracked if pathspec_match([spec], p)]
        if not hits:
            raise fatal("pathspecNoMatch", path=raw)
        if not o.has("r") and any(p != spec for p in hits) and not any(c in spec for c in "*?["):
            raise sub_fatal("rm", "notRecursive", path=raw)
        targets += [p for p in hits if p not in targets]
    if not o.has("f"):
        staged, local, both = [], [], []
        for p in targets:
            e = idx.get(p)
            if e is None:
                continue
            w = repo.wt_id(p)
            in_head = head.get(p)
            staged_change = in_head != (e.mode, e.sha)
            local_change = w is not None and w != (e.mode, e.sha)
            if o.has("\x01"):  # --cached: only refuses when the file matches neither HEAD nor the work tree
                if staged_change and local_change and in_head is not None:
                    both.append(p)
                continue
            if staged_change and local_change:
                both.append(p)
            elif staged_change:
                staged.append(p)
            elif local_change:
                local.append(p)
        for keys, paths in ((("stagedAndLocal", "stagedAndLocalMany"), both), (("staged", "stagedMany"), staged), (("local", "localMany"), local)):
            if paths:
                raise GitError(sub_msg("rm", keys[len(paths) > 1], paths="\n".join("    " + repo.show(p) for p in paths)), 1)
    for p in targets:
        if not o.has("q"):
            g.out(f"rm '{p}'\n")
        if o.has("n"):
            continue
        idx.pop(p, None)
        unmerged.pop(p, None)
        if not o.has("\x01"):
            repo.wt_remove(p)
    if not o.has("n"):
        repo.save_index(list(idx.values()) + [e for st in unmerged.values() for e in st.values()])
    return 0


def git_mv(g: Git, args) -> int:
    o = parse("mv", args)
    repo = g.repo()
    items = o.args + o.paths
    if len(items) < 2:
        raise usage_error("mv", sub_msg("mv", "usage"))
    entries = repo.index()
    idx = {e.path: e for e in entries if e.stage == 0}
    dest_raw = items[-1]
    dest = repo.from_user(dest_raw)
    dest_is_dir = repo.fs.is_dir(repo.wt(dest), follow=False)
    if len(items) > 2 and not dest_is_dir:
        raise sub_fatal("mv", "destinationNotDir", path=dest_raw)
    moves = []
    for raw in items[:-1]:
        src = repo.from_user(raw)
        target = (dest + "/" if dest else "") + src.rsplit("/", 1)[-1] if dest_is_dir else dest
        full = repo.wt(src)
        if not repo.fs.exists(full, follow=False):
            raise sub_fatal("mv", "badSource", source=raw, destination=dest_raw)
        under = [p for p in idx if p.startswith(src + "/")]
        if src not in idx and not under:
            raise sub_fatal("mv", "notUnderControl", source=raw, destination=dest_raw)
        if repo.fs.exists(repo.wt(target), follow=False) and not o.has("f"):
            raise sub_fatal("mv", "destinationExists", source=raw, destination=dest_raw)
        moves.append((src, target, under))
    for src, target, under in moves:
        if o.has("v") or o.has("n"):
            g.out(f"Renaming {repo.show(src)} to {repo.show(target)}\n")
        if o.has("n"):
            continue
        if repo.fs.exists(repo.wt(target), follow=False):
            repo.fs.delete(repo.wt(target), recursive=True)
        parent = target.rsplit("/", 1)[0] if "/" in target else ""
        if parent and not repo.fs.is_dir(repo.wt(parent), follow=False):
            raise sub_fatal("mv", "destinationNotDir", path=parent)
        repo.fs.move(repo.wt(src), repo.wt(target))
        for p in ([src] if src in idx else []) + under:
            e = idx.pop(p)
            np = target + p[len(src):]
            check_path(np)
            e.path = np
            idx[np] = e
    if not o.has("n"):
        repo.save_index(list(idx.values()) + [e for e in entries if e.stage])
    return 0


# ---------- commit ----------

def clean_message(text: str, strip_comments: bool = False) -> str:
    out = []
    for line in text.split("\n"):
        line = line.rstrip()
        if strip_comments and line.startswith("#"):
            continue
        if line == "" and (not out or out[-1] == ""):
            continue
        out.append(line)
    while out and out[-1] == "":
        out.pop()
    return "\n".join(out) + "\n" if out else ""


def read_message_file(g: Git, path: str) -> str:
    if path == "-":
        return g.ctx.stdin
    full = normalize(g.cwd, path)
    if not g.ctx.fs.is_file(full):
        raise GitError(sub_msg("commit", "cannotReadMessage", file=path), 128)
    return g.ctx.fs.read_bytes(full).decode("utf-8", "replace")


def git_commit(g: Git, args) -> int:
    o = parse("commit", args)
    repo = g.repo()
    entries = repo.index()
    if repo.unmerged(entries):
        g.err(sub_msg("commit", "unmergedFiles") + "\n")
        g.out("".join(f"U\t{repo.show(p)}\n" for p in sorted(repo.unmerged(entries))))
        return 128
    head = repo.head()
    amend = o.has("\x01")
    if amend and head is None:
        raise sub_fatal("commit", "nothingToAmend")
    if amend and repo.gexists("MERGE_HEAD"):
        raise sub_fatal("commit", "amendDuringMerge")
    idx = {e.path: e for e in entries}
    paths = o.args + o.paths
    if paths and o.has("a"):
        raise sub_fatal("commit", "pathsWithAll")
    if o.has("a"):
        for p, e in list(idx.items()):
            w = repo.wt_stat(p)
            if w is None:
                del idx[p]
            elif object_id("blob", w[1]) != e.sha or w[0] != e.mode:
                idx[p] = repo.entry_for(p, w[0], repo.put("blob", w[1]))
    files = {p: (e.mode, e.sha) for p, e in idx.items()}
    if paths:
        specs = user_specs(repo, paths)
        base = tree_files(repo, head)
        tracked = set(idx) | set(base)
        for raw, spec in zip(paths, specs):
            if not any(pathspec_match([spec], p) for p in tracked):
                raise GitError(sub_msg("commit", "pathspecUnknown", path=raw), 1)
        files = dict(base)
        for p in sorted(tracked):
            if pathspec_match(specs, p):
                w = repo.wt_stat(p)
                if w is None:
                    files.pop(p, None)
                    idx.pop(p, None)
                else:
                    sha = repo.put("blob", w[1])
                    files[p] = (w[0], sha)
                    idx[p] = repo.entry_for(p, w[0], sha)
    # the message
    merge_heads = [x for x in (repo.gtext("MERGE_HEAD") or "").split() if re.fullmatch(r"[0-9a-f]{40}", x)]
    old = repo.commit(head) if amend else None
    if o.vals("m"):
        message = "\n\n".join(m for m in o.vals("m"))
    elif o.val("F") is not None:
        message = read_message_file(g, o.val("F"))
    elif o.has("\x04") and old is not None:
        message = old.message
    elif o.has("\x04") and merge_heads:
        message = repo.gtext("MERGE_MSG") or ""
    else:
        message = g.editor_error(old.message if old is not None else (repo.gtext("MERGE_MSG") or "") if merge_heads else None)
    message = clean_message(message, strip_comments=o.val("F") is None and not o.vals("m"))
    if not message and not o.has("\x03"):
        raise GitError(sub_msg("commit", "emptyMessage"), 1)
    tree = repo.write_tree(files)
    parents = list(old.parents) if old else ([head] if head else []) + merge_heads
    if not amend and not merge_heads and not o.has("\x02"):
        before = repo.commit(head).tree if head else EMPTY_TREE
        if tree == before:
            repo.save_index(list(idx.values()))
            st = Status(repo)
            g.out(long_status(repo, st, committing=True))
            return 1
    # identities
    a_name = a_email = None
    if o.val("\x05") is not None:
        m = re.fullmatch(r"\s*(.*?)\s*<([^<>]*)>\s*", o.val("\x05"))
        if not m:
            raise sub_fatal("commit", "badAuthor", author=o.val("\x05"))
        a_name, a_email = m.group(1), m.group(2)
    if old and a_name is None and o.val("\x06") is None:
        author = old.author
    else:
        if old and a_name is None:
            n, e, _, _ = parse_ident(old.author)
            a_name, a_email = n, e
        author = g.ident("author", name=a_name, email=a_email, date=o.val("\x06"))
    committer = g.ident("committer")
    body = f"tree {tree}\n" + "".join(f"parent {p}\n" for p in parents) + f"author {author}\ncommitter {committer}\n\n{message}"
    sha = repo.put("commit", body.encode("utf-8", "surrogateescape"))
    c = repo.commit(sha)
    kind = "commit (amend)" if amend else "commit (merge)" if merge_heads else "commit" if parents else "commit (initial)"
    repo.update_ref("HEAD", sha, f"{kind}: {c.subject}")
    if not repo.head_ref():
        repo.gwrite("HEAD", (sha + "\n").encode())
    repo.save_index(list(idx.values()))
    for f in ("MERGE_HEAD", "MERGE_MSG", "MERGE_MODE"):
        repo.gremove(f)
    if not o.has("q"):
        g.out(commit_summary(g, repo, c, root=not parents and not amend, amend=amend))
    return 0


def commit_summary(g: Git, repo: Repo, c: Commit, root: bool, amend: bool = False) -> str:
    b = repo.branch()
    where = b if b is not None else "detached HEAD"
    out = [f"[{where}{' (root-commit)' if root else ''} {abbrev(c.sha)}] {c.subject}"]
    an, ae, at, az = parse_ident(c.author)
    cn, ce, ct, cz = parse_ident(c.committer)
    if (an, ae) != (cn, ce):
        out.append(f" Author: {an} <{ae}>")
    if amend or (at, az) != (ct, cz):
        out.append(f" Date: {format_date(at, az)}")
    a = tree_files(repo, c.parents[0]) if c.parents else {}
    b_files = repo.flatten(c.tree)
    changes = diff_files(a, b_files, load=tree_loader(repo, a, b_files))
    text = []
    if changes:
        write_stat(text.append, stat_entries(g, repo, a, b_files, changes), short=True)
    return "\n".join(out) + "\n" + "".join(text) + "".join(l + "\n" for l in summary_lines(changes, a, b_files))


# ---------- log / show ----------

def decorations(repo: Repo) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    head_ref = repo.head_ref()
    refs = repo.refs("refs/heads/") + repo.refs("refs/tags/") + [("refs/remotes/" + n, sha) for n, sha, _ in remote_branches(repo) if sha]
    for name, sha in sorted(refs, key=lambda kv: kv[0].encode(), reverse=True):
        if name == head_ref:
            continue
        target = peel(repo, sha, "commit") if name.startswith("refs/tags/") else sha
        label = ("tag: " + name[10:]) if name.startswith("refs/tags/") else name[13:] if name.startswith("refs/remotes/") else name[11:]
        out.setdefault(target or sha, []).append(label)
    head = repo.head()
    if head:
        b = repo.branch()
        out.setdefault(head, []).insert(0, f"HEAD -> {b}" if b is not None else "HEAD")
    return out


def expand_format(g: Git, repo: Repo, fmt: str, c: Commit, decor, date_style: str) -> str:
    an, ae, at, az = parse_ident(c.author)
    cn, ce, ct, cz = parse_ident(c.committer)
    if decor is None and ("%d" in fmt or "%D" in fmt):
        decor = g.decor if g.decor is not None else decorations(repo)
        g.decor = decor
    d = decor.get(c.sha, []) if decor is not None else []

    def date(epoch, zone, style):
        return format_date(epoch, zone, style, g.now())

    simple = {
        "H": c.sha, "h": abbrev(c.sha), "T": c.tree, "t": abbrev(c.tree), "P": " ".join(c.parents), "p": " ".join(abbrev(p) for p in c.parents),
        "an": an, "ae": ae, "al": ae.split("@")[0], "ad": date(at, az, date_style), "aD": date(at, az, "rfc"), "ar": date(at, az, "relative"),
        "at": str(at), "ai": date(at, az, "iso"), "aI": date(at, az, "iso-strict"), "as": date(at, az, "short"),
        "cn": cn, "ce": ce, "cl": ce.split("@")[0], "cd": date(ct, cz, date_style), "cD": date(ct, cz, "rfc"), "cr": date(ct, cz, "relative"),
        "ct": str(ct), "ci": date(ct, cz, "iso"), "cI": date(ct, cz, "iso-strict"), "cs": date(ct, cz, "short"),
        "s": c.subject, "b": c.body if c.body.endswith("\n") or not c.body else c.body + "\n", "B": c.message,
        "d": f" ({', '.join(d)})" if d else "", "D": ", ".join(d), "n": "\n", "%": "%",
        "Cred": "", "Cgreen": "", "Cblue": "", "Creset": "",
    }
    out, i = [], 0
    while i < len(fmt):
        ch = fmt[i]
        if ch != "%":
            out.append(ch)
            i += 1
            continue
        if fmt.startswith("%C(", i):
            j = fmt.find(")", i)
            if j > 0:
                i = j + 1
                continue
        m = re.match(r"x([0-9a-fA-F]{2})", fmt[i + 1:])
        if m:
            out.append(chr(int(m.group(1), 16)))
            i += 4
            continue
        for k in sorted(simple, key=len, reverse=True):
            if fmt.startswith(k, i + 1):
                out.append(simple[k])
                i += 1 + len(k)
                break
        else:
            out.append("%")
            i += 1
    return "".join(out)


PRETTY = ("oneline", "short", "medium", "full", "fuller", "raw")


def commit_header(g: Git, repo: Repo, c: Commit, pretty: str, fmt: str | None, decor, date_style: str, abbrev_commit: bool,
                  parents: bool = False) -> str:
    """The commit as log prints it, ending in a newline (except a separator-style format). parents: --parents."""
    if pretty in ("format", "tformat"):
        return expand_format(g, repo, fmt or "", c, decor, date_style) + ("\n" if pretty == "tformat" else "")
    d = decor.get(c.sha, []) if decor is not None else []
    dtext = f" ({', '.join(d)})" if d else ""
    sha = " ".join(abbrev(x) if abbrev_commit else x for x in [c.sha] + (c.parents if parents else []))
    if pretty == "oneline":
        return f"{sha}{dtext} {c.subject}\n"
    an, ae, at, az = parse_ident(c.author)
    cn, ce, ct, cz = parse_ident(c.committer)
    lines = [f"commit {sha}{dtext}"]
    if len(c.parents) > 1:
        lines.append("Merge: " + " ".join(abbrev(p) for p in c.parents))
    if pretty == "raw":
        lines = [f"commit {sha}", f"tree {c.tree}"] + [f"parent {p}" for p in c.parents] + [f"author {c.author}", f"committer {c.committer}"]
    elif pretty == "fuller":
        lines += [f"Author:     {an} <{ae}>", f"AuthorDate: {format_date(at, az, date_style, g.now())}",
                  f"Commit:     {cn} <{ce}>", f"CommitDate: {format_date(ct, cz, date_style, g.now())}"]
    else:
        lines.append(f"Author: {an} <{ae}>")
        if pretty == "medium":
            lines.append(f"Date:   {format_date(at, az, date_style, g.now())}")
        if pretty == "full":
            lines.append(f"Commit: {cn} <{ce}>")
    lines.append("")
    text = c.message.rstrip("\n")
    msg_lines = [c.subject] if pretty == "short" else (text.split("\n") if text else [])
    lines += ["    " + l if l else "    " for l in msg_lines]
    return "\n".join(lines) + "\n"


def pretty_choice(o: Opts) -> tuple[str, str | None]:
    if o.has("\x0e"):
        return "oneline", None
    v = o.val("\x10")
    if v is None:
        return "medium", None
    if v in PRETTY:
        return v, None
    if v.startswith("format:"):
        return "format", v[7:]
    if v.startswith("tformat:"):
        return "tformat", v[8:]
    if "%" in v or v == "":
        return "tformat", v
    raise GitError(msg("badPretty", format=v), 128)


def diff_wanted(o: Opts, default_patch: bool) -> bool:
    if o.has("s"):
        return False
    return default_patch or any(o.has(k) for k in ("p", "u", "\x01", "\x02", "\x03", "\x04", "\x06", "\x1f"))


def commit_diff(g: Git, repo: Repo, c: Commit, o: Opts, specs, merges: bool = False) -> str:
    """The commit's changes against its first parent, as text, in the format the options ask for (patch by default).
    A merge shows nothing, except to show, which lists its stat or names against the first parent (never a patch)."""
    if len(c.parents) > 1 and not (merges and any(o.has(k) for k in ("\x01", "\x02", "\x03", "\x04", "\x06", "\x1f"))):
        return ""
    a = tree_files(repo, c.parents[0]) if c.parents else {}
    b = repo.flatten(c.tree)
    buf: list[str] = []
    write_diff(g, repo, a, b, o, specs, out=buf.append)
    return "".join(buf)


def revisions_and_paths(repo: Repo, o: Opts, default_head: bool = True):
    """Splits operands into revisions (with ^x / a..b exclusions) and paths, the way log and diff tell them apart."""
    include, exclude, paths = [], [], list(o.paths)
    for k, a in enumerate(o.args):
        if a.startswith("^") and len(a) > 1:
            exclude.append(commit_of(repo, a[1:]))
            continue
        if "..." in a:
            left, right = a.split("...", 1)
            lc, rc = commit_of(repo, left or "HEAD"), commit_of(repo, right or "HEAD")
            include += [lc, rc]
            common = ancestors(repo, [lc], repo.ctx.check_time) & ancestors(repo, [rc], repo.ctx.check_time)
            exclude += sorted(common)
            continue
        if ".." in a and "..." not in a:
            left, right = a.split("..", 1)
            exclude.append(commit_of(repo, left or "HEAD"))
            include.append(commit_of(repo, right or "HEAD"))
            continue
        sha = try_resolve(repo, a)
        if sha is not None:
            c = peel(repo, sha, "commit")
            if c is None:
                raise fatal("notCommit", rev=a)
            include.append(c)
            continue
        if repo.fs.exists(normalize(repo.g.cwd, a), follow=False) and not o.dashdash:
            paths += o.args[k:]
            break
        raise fatal("badRevision", rev=a)
    return include, exclude, paths


def git_log(g: Git, args) -> int:
    args = [f"-n{a[1:]}" if re.fullmatch(r"-\d+", a) else a for a in args]
    o = parse("log", args)
    repo = g.repo()
    include, exclude, paths = revisions_and_paths(repo, o)
    if o.has("\x12"):
        include += [peel(repo, s, "commit") for _, s in repo.refs("refs/") if peel(repo, s, "commit")]
        if repo.head():
            include.append(repo.head())
    if not include and not o.has("\x12"):
        h = repo.head()
        if h is None:
            b = repo.branch()
            raise sub_fatal("log", "noCommitsYet", branch=b or "HEAD")
        include = [h]
    specs = user_specs(repo, paths)
    limit = int(o.val("n")) if o.val("n") is not None and re.fullmatch(r"-?\d+", o.val("n")) else -1
    skip = int(o.val("\x1b", "0")) if (o.val("\x1b", "0") or "").isdigit() else 0
    pretty, fmt = pretty_choice(o)
    date_style = o.val("\x19", "default")
    format_date(0, "+0000", date_style)  # rejects an unknown style before any output
    decor = decorations(repo) if o.has("\x11") else None
    abbrev_commit = o.has("\x1a") or o.has("\x0e")
    excluded = ancestors(repo, exclude, g.ctx.check_time) if exclude else set()
    if o.has("\x16"):
        order = []
        c = include[0] if include else None
        while c and c not in excluded and len(order) <= MAX_WALK:
            order.append(c)
            ps = repo.commit(c).parents
            c = ps[0] if ps else None
    else:
        order = [c for c in walk_order(repo, list(dict.fromkeys(include)), g.ctx.check_time) if c not in excluded]
    since = until = None
    for key, name in (("\x1d", "since"), ("\x1e", "until")):
        if o.val(key) is not None:
            try:
                when = parse_date(o.val(key), g.now())[0]
            except ValueError:
                raise GitError(msg("badDate", date=o.val(key)), 128) from None
            since, until = (when, until) if name == "since" else (since, when)
    author_rx = user_regex(o.val("\x14"))
    grep_rx = user_regex(o.val("\x15"))
    pickaxe = o.val("S")
    pickaxe_rx = None
    if o.val("G") is not None:
        error = posix_regex_error(o.val("G"), True, grep=False)
        if error:
            raise GitError(sub_msg("log", "badPickaxeRegex", error=error), 128)
        pickaxe_rx = timed(posix_to_python(o.val("G"), True), re.MULTILINE)
    shown = []
    hits: dict[str, list[str]] = {}  # commit -> the paths the pickaxe found, which its diff is limited to
    for sha in order:
        c = repo.commit(sha)
        if o.has("\x17") and len(c.parents) > 1 or o.has("\x18") and len(c.parents) < 2:
            continue
        if since is not None and commit_time(repo, sha) < since or until is not None and commit_time(repo, sha) > until:
            continue
        if author_rx and not author_rx.search(c.author.rsplit(">", 1)[0] + ">"):
            continue
        if grep_rx and not grep_rx.search(c.message):
            continue
        if specs is not None:
            a = tree_files(repo, c.parents[0]) if c.parents else {}
            if not diff_files(a, repo.flatten(c.tree), specs):
                continue
        if pickaxe is not None or pickaxe_rx is not None:
            if len(c.parents) > 1:
                continue  # a merge has no diff of its own to search
            found = pickaxe_paths(g, repo, c, specs, pickaxe, pickaxe_rx)
            if not found:
                continue
            hits[sha] = found
        if skip:
            skip -= 1
            continue
        if limit >= 0 and len(shown) >= limit:
            break
        shown.append(c)
    if o.has("\x13"):
        shown.reverse()
    write_log(g, repo, shown, o, pretty, fmt, decor, date_style, abbrev_commit, specs, default_patch=False, graph=o.has("\x0f"),
              hits=hits)
    return 0


def pickaxe_paths(g: Git, repo: Repo, c: Commit, specs, needle: str | None, rx) -> list[str]:
    """The files whose change in c alters the count of needle (-S), or adds or removes a line rx matches (-G)."""
    a = tree_files(repo, c.parents[0]) if c.parents else {}
    b = repo.flatten(c.tree)
    out = []
    for st, old, new in diff_files(a, b, specs):
        g.ctx.check_time()
        x = repo.typed(a[old][1], "blob") if old else b""
        y = repo.typed(b[new][1], "blob") if new else b""
        if is_binary(x) or is_binary(y):
            continue
        if needle is not None:
            n = needle.encode("utf-8", "surrogateescape")
            if n and x.count(n) != y.count(n):
                out += [p for p in (old, new) if p and p not in out]
            continue
        lx, ly = split_lines(x), split_lines(y)
        changed = [l for s1, e1, s2, e2 in myers(lx, ly, g.ctx.check_time) for l in lx[s1:e1] + ly[s2:e2]]
        if any(rx.search(l.decode("utf-8", "surrogateescape").rstrip("\n")) for l in changed):
            out += [p for p in (old, new) if p and p not in out]
    return out


def write_log(g, repo, commits, o, pretty, fmt, decor, date_style, abbrev_commit, specs, default_patch, graph=False, merges=False,
              hits=None) -> None:
    if graph:
        for i, c in enumerate(commits):
            if len(c.parents) > 1 or (i + 1 < len(commits) and c.parents != [commits[i + 1].sha]):
                raise fatal("graphNotLinear")  # a straight line would misdraw it
    wants_diff = diff_wanted(o, default_patch)
    for i, c in enumerate(commits):
        g.ctx.check_time()
        # shortcut: with paths, --parents prints the real parents, not git's rewritten (simplified) ones
        head = commit_header(g, repo, c, pretty, fmt, decor, date_style, abbrev_commit, parents=o.has("\x05"))
        diff = commit_diff(g, repo, c, o, hits[c.sha] if hits else specs, merges) if wants_diff else ""
        multi_line = pretty not in ("oneline", "format", "tformat")
        text = ""
        if i and multi_line:
            text += "\n"
        if pretty == "format":
            text += ("\n" if i else "") + head
            if diff:
                text += "\n" + diff
        else:
            text += head
            if diff:
                text += ("\n" if pretty != "oneline" else "") + diff
        if graph:
            last = i == len(commits) - 1
            lines = text.split("\n")
            if lines and lines[-1] == "":
                lines.pop()
                end = "\n"
            else:
                end = ""
            if i and multi_line and lines and lines[0] == "":
                lines[0] = "|"
                lines[0] = "| "
            start = 1 if i and multi_line else 0
            out = lines[:start]
            for k, line in enumerate(lines[start:]):
                out.append(("* " if k == 0 else ("  " if last else "| ")) + line)
            text = "\n".join(out) + end
        g.out(text)


def git_show(g: Git, args) -> int:
    o = parse("show", args)
    repo = g.repo()
    targets = o.args or ["HEAD"]
    pretty, fmt = pretty_choice(o)
    date_style = o.val("\x19", "default")
    decor = decorations(repo) if o.has("\x11") else None
    first = True
    seen = set()
    for t in targets:
        sha = try_resolve(repo, t)
        if sha is None:
            if t == "HEAD" and repo.head() is None:
                raise fatal("badDefaultRevision")
            missing_path(repo, t)
            raise fatal("badRevision", rev=t)
        if sha in seen:
            continue
        seen.add(sha)
        kind, data = repo.obj(sha)
        if kind == "blob":
            g.out(bytetext.decode(data))
            continue
        if kind == "tree":
            g.out(f"tree {t}\n\n" + "".join(n + ("/" if m == "040000" else "") + "\n" for m, n, _ in repo.tree(sha)))
            continue
        if kind == "tag":
            head, _, body = data.decode("utf-8", "surrogateescape").partition("\n\n")
            fields = dict(l.split(" ", 1) for l in head.split("\n") if " " in l)
            tn, te, tt, tz = parse_ident(fields.get("tagger", ""))
            g.out(f"tag {fields.get('tag', '')}\nTagger: {tn} <{te}>\nDate:   {format_date(tt, tz, date_style, g.now())}\n\n{body.rstrip(chr(10))}\n\n")
            sha = peel(repo, sha, "commit")
            if sha is None:
                continue
        c = repo.commit(sha)
        if not first and pretty not in ("oneline", "format", "tformat"):
            g.out("\n")
        first = False
        write_log(g, repo, [c], o, pretty, fmt, decor, date_style, o.has("\x1a") or o.has("\x0e"), user_specs(repo, o.paths), default_patch=True,
                  merges=True)
    return 0


# ---------- diff ----------

def git_diff(g: Git, args) -> int:
    o = parse("diff", args)
    repo = g.repo()
    cached = o.has("\x08")
    revs, paths = [], list(o.paths)
    for k, a in enumerate(o.args):
        if "..." in a:
            left, right = a.split("...", 1)
            lc, rc = commit_of(repo, left or "HEAD"), commit_of(repo, right or "HEAD")
            base = merge_base(repo, lc, rc, g.ctx.check_time)
            revs += [base or lc, rc]
            continue
        if ".." in a:
            left, right = a.split("..", 1)
            revs += [commit_of(repo, left or "HEAD"), commit_of(repo, right or "HEAD")]
            continue
        sha = try_resolve(repo, a)
        if sha is not None:
            revs.append(sha)
            continue
        if repo.fs.exists(normalize(g.cwd, a), follow=False) and not o.dashdash:
            paths += o.args[k:]
            break
        raise fatal("badRevision", rev=a)
    specs = user_specs(repo, paths)
    entries = repo.index()
    idx = index_files(entries)
    for p, stages in repo.unmerged(entries).items():
        if 2 in stages:
            idx[p] = (stages[2].mode, stages[2].sha)

    def tree_of(rev):
        t = peel(repo, rev, "tree")
        if t is None:
            raise fatal("notTree", rev=rev)
        return repo.flatten(t)
    if len(revs) > 2:
        raise usage_error("diff", sub_msg("diff", "tooManyRevisions"))
    if len(revs) == 2:
        a, b, bw = tree_of(revs[0]), tree_of(revs[1]), False
    elif cached:
        base = revs[0] if revs else repo.head()
        a, b, bw = (tree_of(base) if base else {}), idx, False
    elif revs:
        a = tree_of(revs[0])
        b, bw = worktree_files(repo, set(idx) | set(a)), True
    else:
        a, b, bw = idx, worktree_files(repo, idx), True
    if o.has("\x10") and (o.has("\x03") or o.has("\x04")):
        raise sub_fatal("diff", "checkWith")
    if o.has("\x10") and not o.has("\x05"):
        changes = diff_files(a, b, specs)
        bad = whitespace_check(g, repo, a, b, changes, bw)
        return (2 if bad else 0) | (1 if changes and o.has("\x07") else 0)
    changed = write_diff(g, repo, a, b, o, specs, b_worktree=bw)
    return 1 if changed and (o.has("\x07") or o.has("\x05")) else 0


def whitespace_check(g: Git, repo: Repo, a: dict, b: dict, changes, b_worktree: bool) -> bool:
    """diff --check: prints added lines with whitespace problems (git's default core.whitespace); True when any."""
    def blank(line: bytes) -> bool:
        return not line.strip(b" \t\r\n\v\f")

    def trailing_blanks(lines) -> int:
        n = 0
        while n < len(lines) and blank(lines[-1 - n]):
            n += 1
        return n
    bad = False
    for st, old, new in changes:
        if not new or st == "T":
            continue
        x = blob_data(repo, a.get(old) if old else None, old or "", False)
        y = blob_data(repo, b[new], new, b_worktree)
        if is_binary(x) or is_binary(y):
            continue
        lx, ly = split_lines(x), split_lines(y)
        name = c_quote(new)
        for _, _, s2, e2 in myers(lx, ly, g.ctx.check_time):
            for j in range(s2, e2):
                text = ly[j].rstrip(b"\n")
                problems = []
                if text and text[-1:] in b" \t\r":
                    problems.append(sub_msg("diff", "checkTrailing"))
                indent = text[:len(text) - len(text.lstrip(b" \t"))]
                if len(indent) < len(text) and b" \t" in indent:
                    problems.append(sub_msg("diff", "checkSpaceTab"))
                if problems:
                    bad = True
                    g.out(sub_msg("diff", "checkLine", file=name, line=str(j + 1), problems=", ".join(problems)) + "\n")
                    g.out("+" + text.decode("utf-8", "surrogateescape") + "\n")
        tail = trailing_blanks(ly)
        if tail > trailing_blanks(lx):
            bad = True
            g.out(sub_msg("diff", "checkBlankEof", file=name, line=str(len(ly) - tail + 1)) + "\n")
    return bad


# ---------- moving between commits ----------

def valid_branch_name(name: str) -> bool:
    if not name or name in ("HEAD", "@") or name.startswith(("-", "/", ".")) or name.endswith(("/", ".", ".lock")):
        return False
    if ".." in name or "//" in name or "@{" in name or "/." in name:
        return False
    return not any(c in name for c in " ~^:?*[\\\x7f") and all(ord(c) >= 32 for c in name)


def switch_trees(g: Git, repo: Repo, old: str | None, new: str | None, force: bool, verb: str = "checkout", keep_specs=None) -> list[str]:
    """Moves the index and work tree from commit old to commit new, keeping local changes to paths the two do not
    differ in. Refuses (exit 1) when that would lose a local change or an untracked file. Returns the paths that
    stay locally changed."""
    a, b = tree_files(repo, old), tree_files(repo, new)
    entries = repo.index()
    if repo.unmerged(entries) and not force:
        raise GitError(sub_msg("checkout", "resolveIndexFirst", paths="\n".join(msg("needsMerge", path=p) for p in sorted(repo.unmerged(entries)))), 1)
    idx = index_files(entries)
    if force:
        idx_entries = {}
        for p, (mode, sha) in b.items():
            if repo.wt_id(p) != (mode, sha):
                repo.wt_write(p, mode, sha)
            idx_entries[p] = repo.entry_for(p, mode, sha)
        for p in set(idx) | set(a) | set(repo.unmerged(entries)):
            if p not in b:
                repo.wt_remove(p)
        repo.save_index(list(idx_entries.values()))
        return []
    plan, dirty, clobber = [], [], []
    for p in sorted(set(a) | set(b)):
        if a.get(p) == b.get(p):
            continue
        g.ctx.check_time()
        i, w = idx.get(p), repo.wt_id(p)
        if i == b.get(p) and w == i:
            continue  # already as the target has it
        if p not in a and i is None:
            if w is not None and not repo.ignored(p):
                clobber.append(p)
        elif i != a.get(p) or (i is not None and w != i):
            dirty.append(p)
        plan.append(p)
    if dirty:
        raise GitError(sub_msg("checkout", "localChanges", verb=verb, paths="".join(f"\t{p}\n" for p in dirty).rstrip("\n"),
                               advice=sub_msg("checkout", "localChangesAdviceMerge" if verb == "merge" else "localChangesAdvice")), 1)
    if clobber:
        raise GitError(sub_msg("checkout", "untrackedOverwritten", verb=verb, paths="".join(f"\t{p}\n" for p in clobber).rstrip("\n"),
                               advice=sub_msg("checkout", "untrackedAdviceMerge" if verb == "merge" else "untrackedAdvice")), 1)
    by_path = {e.path: e for e in entries if e.stage == 0}
    for p in plan:
        t = b.get(p)
        if t is None:
            repo.wt_remove(p)
            by_path.pop(p, None)
        else:
            repo.wt_write(p, *t)
            by_path[p] = repo.entry_for(p, *t)
    repo.save_index(list(by_path.values()))
    # what stays changed locally, against the new commit
    out = []
    now = index_files(by_path.values())
    for p in sorted(set(now) | set(b)):
        if now.get(p) != b.get(p) or (p in now and repo.wt_id(p) != now[p]):
            out.append(("D" if p not in now or repo.wt_id(p) is None else "A" if p not in b else "M") + "\t" + p)
    return out


def subject_of(repo: Repo, sha: str) -> str:
    return f"{abbrev(sha)} {repo.commit(sha).subject}"


def move_head(g: Git, repo: Repo, target_ref: str | None, target_sha: str | None, label: str, force: bool, quiet: bool,
              new_branch: str | None = None, reset_branch: bool = False, stay: bool = False, track: str | None = None) -> int:
    old = repo.head()
    old_branch = repo.branch()
    M = lambda key, **v: sub_msg("checkout", key, **v)
    if target_ref is not None and target_ref == repo.head_ref() and new_branch is None and not stay:
        if not quiet:
            g.err(M("alreadyOn", branch=label) + "\n")
            report_tracking(g, repo)
        return 0
    carried = switch_trees(g, repo, old, target_sha, force)
    if force:
        for f in ("MERGE_HEAD", "MERGE_MSG", "MERGE_MODE"):
            repo.gremove(f)  # a forced switch gives up a merge in progress
    if stay:
        if not quiet:
            for line in carried:
                g.out(line + "\n")
        return 0
    if new_branch is not None:
        if target_sha:
            repo.update_ref("refs/heads/" + new_branch, target_sha, f"branch: Created from {label}")
        if track:
            set_tracking(g, repo, new_branch, track, quiet)
    from_name = old_branch if old_branch is not None else (old or "")
    if target_ref is not None:
        repo.set_head(target_ref, f"checkout: moving from {from_name} to {new_branch or label}")
    else:
        repo.set_head(target_sha, f"checkout: moving from {from_name} to {label}", detach=True)
    if quiet:
        return 0
    if old_branch is None and old and (target_ref is not None or old != target_sha):
        g.err(M("previousPosition", commit=subject_of(repo, old)) + "\n")
    if target_ref is None:
        g.err(M("headNowAt", commit=subject_of(repo, target_sha)) + "\n")
    elif new_branch is not None:
        g.err(M("switchedResetBranch" if reset_branch else "switchedNewBranch", branch=new_branch) + "\n")
    else:
        g.err(M("switchedBranch", branch=label) + "\n")
    for line in carried:
        g.out(line + "\n")
    if target_ref is not None and new_branch is None:
        report_tracking(g, repo)
    return 0


def report_tracking(g: Git, repo: Repo) -> None:
    t = tracking_text(repo, repo.branch() or "", g.ctx.check_time) if repo.branch() else None
    if t:
        g.out(t + "\n")


def remote_start(repo: Repo, start: str | None) -> str | None:
    """The remote-tracking branch a start point names (origin/x or refs/remotes/origin/x), for setting up tracking."""
    if start is None or try_resolve(repo, start) is None:
        return None
    for ref in (start, "refs/" + start, "refs/remotes/" + start):
        if ref.startswith("refs/remotes/") and not ref.endswith("/HEAD") and repo.read_ref(ref) and not repo.read_ref("refs/heads/" + start):
            return ref
    return None


def set_tracking(g: Git, repo: Repo, branch: str, ref: str, quiet: bool) -> None:
    """Records ref (refs/remotes/<remote>/<name>) as branch's upstream, as git does for a branch started from it."""
    remote, _, name = ref[13:].partition("/")
    cfg = repo.config()
    cfg.set(f"branch.{branch}.remote", remote)
    cfg.set(f"branch.{branch}.merge", "refs/heads/" + name)
    repo.save_config()
    if not quiet:
        g.out(sub_msg("checkout", "trackingSetUp", branch=branch, upstream=ref[13:]) + "\n")


def guess_remote(repo: Repo, name: str) -> str | None:
    """refs/remotes/<remote>/<name> when exactly one remote has a branch called name (git checkout <name> then creates it)."""
    if not valid_branch_name(name):
        return None
    configured = {k[7:].rpartition(".")[0] for k, _, _ in repo.config().items() if k.startswith("remote.") and k.endswith(".fetch")}
    hits = [n for n, sha, sym in remote_branches(repo) if sym is None and n.partition("/")[2] == name and n.partition("/")[0] in configured]
    if len(hits) > 1:
        raise sub_fatal("checkout", "matchedMultiple", name=name, count=str(len(hits)))
    return "refs/remotes/" + hits[0] if hits else None


def is_work_path(repo: Repo, name: str) -> bool:
    """name is a file in the work tree or the index, so 'git checkout name' could mean it."""
    try:
        rel = repo.from_user(name)
    except GitError:
        return False
    return rel in repo.stage0() or repo.fs.exists(repo.wt(rel), follow=False)


def checkout_paths(g: Git, repo: Repo, source: str | None, items: list[str], sub: str, staged: bool = False, worktree: bool = True,
                   quiet: bool = False) -> int:
    """checkout [tree-ish] -- paths / restore: copies paths from a commit (or the index) into the index and/or work tree."""
    entries = repo.index()
    idx = {e.path: e for e in entries if e.stage == 0}
    unmerged = repo.unmerged(entries)
    src = tree_files(repo, source) if source else None
    specs = [repo.from_user(p) for p in items]
    pool = set(src) if src is not None else set(idx) | set(unmerged)
    if staged and src is not None:
        pool |= set(idx)
    count = 0
    for raw, spec in zip(items, specs):
        if not any(pathspec_match([spec], p) for p in pool):
            raise GitError(msg("pathspecNoMatchKnown", path=raw), 1)
    for p in sorted(pool):
        if not pathspec_match(specs, p):
            continue
        g.ctx.check_time()
        t = src.get(p) if src is not None else ((idx[p].mode, idx[p].sha) if p in idx else None)
        if t is None and src is None and p in unmerged:
            raise GitError(sub_msg(sub, "unmergedPath", path=p), 1)
        if staged:
            if t is None:
                idx.pop(p, None)
            else:
                old = idx.get(p)
                idx[p] = old if old and (old.mode, old.sha) == t else Entry(p, t[1], t[0])
        if worktree:
            if t is None:
                repo.wt_remove(p)
            else:
                if repo.wt_id(p) != t:
                    repo.wt_write(p, *t)
                if src is not None and not staged and sub == "checkout":
                    idx[p] = repo.entry_for(p, *t)
            count += 1
    repo.save_index(list(idx.values()) + [e for st in unmerged.values() for e in st.values()])
    if sub == "checkout" and not quiet:
        key = (("updatedFromIndexMany", "updatedFromIndex"), ("updatedFromCommitMany", "updatedFromCommit"))[source is not None][count == 1]
        g.err(sub_msg("checkout", key, count=str(count), commit=abbrev(source or "")) + "\n")
    return 0


def git_checkout(g: Git, args) -> int:
    o = parse("checkout", args)
    repo = g.repo()
    quiet, force = o.has("q"), o.has("f")
    new = o.val("b") or o.val("B")
    if o.dashdash and new is None:
        if len(o.args) > 1:
            raise GitError(msg("pathspecNoMatchKnown", path=o.args[1]), 1)
        source = commit_of(repo, o.args[0]) if o.args else None
        if not o.paths:
            return 0
        return checkout_paths(g, repo, source, o.paths, "checkout", staged=source is not None, quiet=True)
    target = o.args[0] if o.args else None
    if target == "-":
        prev = previous_branch(repo, 1)
        if prev is None:
            raise sub_fatal("checkout", "noPreviousBranch")
        target = prev
    if new is not None:
        if not valid_branch_name(new):
            raise sub_fatal("branch", "invalidName", name=new)
        exists = repo.read_ref("refs/heads/" + new) is not None
        if exists and o.val("B") is None:
            raise sub_fatal("branch", "exists", name=new)
        start = commit_of(repo, target) if target else repo.head()
        if start is None and target is None:
            repo.set_head("refs/heads/" + new)
            if not quiet:
                g.err(sub_msg("checkout", "switchedNewBranch", branch=new) + "\n")
            return 0
        return move_head(g, repo, "refs/heads/" + new, start, target or "HEAD", force, quiet, new_branch=new, reset_branch=exists,
                         track=remote_start(repo, target))
    if target is None:
        if o.has("\x01"):
            return move_head(g, repo, None, repo.head(), "HEAD", force, quiet)
        return 0
    if not o.has("\x01") and target in ("HEAD", "@") and repo.head_ref() and len(o.args) == 1:
        return move_head(g, repo, repo.head_ref(), repo.head(), repo.branch(), force, quiet, stay=True)
    if not o.has("\x01") and repo.read_ref("refs/heads/" + target) and len(o.args) == 1:
        return move_head(g, repo, "refs/heads/" + target, repo.read_ref("refs/heads/" + target), target, force, quiet)
    sha = try_resolve(repo, target)
    c = peel(repo, sha, "commit") if sha else None
    if c is not None:
        if len(o.args) > 1:
            return checkout_paths(g, repo, c, o.args[1:], "checkout", staged=True, quiet=quiet)
        return move_head(g, repo, None, c, target, force, quiet)
    if o.has("\x01"):
        raise fatal("badRevision", rev=target)
    guess = guess_remote(repo, target) if len(o.args) == 1 and not o.dashdash else None
    if guess and is_work_path(repo, target):
        raise sub_fatal("checkout", "fileOrBranch", name=target, upstream=guess[13:])
    if guess:
        return move_head(g, repo, "refs/heads/" + target, repo.read_ref(guess), guess[13:], force, quiet, new_branch=target, track=guess)
    return checkout_paths(g, repo, None, o.args, "checkout", quiet=quiet)


def git_switch(g: Git, args) -> int:
    o = parse("switch", args)
    repo = g.repo()
    quiet, force = o.has("q"), o.has("f")
    new = o.val("c") or o.val("C")
    target = o.args[0] if o.args else None
    if len(o.args) > 1:
        raise usage_error("switch", sub_msg("switch", "tooManyArguments"))
    if target == "-":
        target = previous_branch(repo, 1)
        if target is None:
            raise sub_fatal("checkout", "noPreviousBranch")
    if new is not None:
        if not valid_branch_name(new):
            raise sub_fatal("branch", "invalidName", name=new)
        exists = repo.read_ref("refs/heads/" + new) is not None
        if exists and o.val("C") is None:
            raise sub_fatal("branch", "exists", name=new)
        start = commit_of(repo, target) if target else repo.head()
        if start is None:
            repo.set_head("refs/heads/" + new)
            if not quiet:
                g.err(sub_msg("checkout", "switchedNewBranch", branch=new) + "\n")
            return 0
        return move_head(g, repo, "refs/heads/" + new, start, target or "HEAD", force, quiet, new_branch=new, reset_branch=exists,
                         track=remote_start(repo, target))
    if o.has("d"):
        c = commit_of(repo, target or "HEAD")
        return move_head(g, repo, None, c, target or "HEAD", force, quiet)
    if target is None:
        raise sub_fatal("switch", "missingBranch")
    if repo.read_ref("refs/heads/" + target):
        return move_head(g, repo, "refs/heads/" + target, repo.read_ref("refs/heads/" + target), target, force, quiet)
    if target in ("HEAD", "@") and repo.head_ref():
        raise sub_fatal("switch", "expectedBranchRef", rev=repo.head_ref())
    if try_resolve(repo, target):
        raise sub_fatal("switch", "expectedBranch", rev=target)
    guess = guess_remote(repo, target)
    if guess:
        return move_head(g, repo, "refs/heads/" + target, repo.read_ref(guess), guess[13:], force, quiet, new_branch=target, track=guess)
    raise sub_fatal("switch", "invalidReference", rev=target)


def git_restore(g: Git, args) -> int:
    o = parse("restore", args)
    repo = g.repo()
    items = o.args + o.paths
    if not items:
        raise sub_fatal("restore", "noPaths")
    staged, worktree = o.has("S"), o.has("W") or not o.has("S")
    src = o.val("s")
    source = commit_of(repo, src) if src else (repo.head() if staged else None)
    if staged and source is None and src is None:
        source = None
        # unborn branch: restoring the index means emptying it for those paths
        specs = [repo.from_user(p) for p in items]
        entries = [e for e in repo.index() if not pathspec_match(specs, e.path)]
        repo.save_index(entries)
        return 0
    return checkout_paths(g, repo, source, items, "restore", staged=staged, worktree=worktree)


def git_reset(g: Git, args) -> int:
    o = parse("reset", args)
    repo = g.repo()
    modes = [m for m, k in (("soft", "\x01"), ("mixed", "\x02"), ("hard", "\x03")) if o.has(k)]
    if len(modes) > 1:
        raise sub_fatal("reset", "conflictingModes")
    mode = modes[0] if modes else "mixed"
    rev, paths = "HEAD", list(o.paths)
    if o.args:
        if try_resolve(repo, o.args[0]) is not None:
            rev = o.args[0]
            paths = o.args[1:] + paths
        elif repo.head() is None and o.args[0] in ("HEAD", "@"):
            paths = o.args[1:] + paths
        elif not o.dashdash and all(repo.fs.exists(normalize(g.cwd, a), follow=False) for a in o.args):
            paths = o.args + paths
        else:
            raise fatal("badRevision", rev=o.args[0])
    head = repo.head()
    target = None if (rev in ("HEAD", "@") and head is None) else commit_of(repo, rev)
    if paths:
        if mode != "mixed":
            raise sub_fatal("reset", "pathsWithMode", mode=mode)
        specs = [repo.from_user(p) for p in paths]
        src = tree_files(repo, target)
        entries = repo.index()
        idx = {e.path: e for e in entries if e.stage == 0}
        for p in set(src) | set(idx) | set(repo.unmerged(entries)):
            if pathspec_match(specs, p):
                t = src.get(p)
                if t is None:
                    idx.pop(p, None)
                elif p not in idx or (idx[p].mode, idx[p].sha) != t:
                    idx[p] = Entry(p, t[1], t[0])
        keep = [e for e in entries if e.stage and not pathspec_match(specs, e.path)]
        repo.save_index(list(idx.values()) + keep)
        unstaged_report(g, repo, o.has("q"))
        return 0
    if head:
        repo.gwrite("ORIG_HEAD", (head + "\n").encode())
    if mode == "hard":
        switch_trees(g, repo, head, target, force=True)
    elif mode == "mixed":
        src = tree_files(repo, target)
        old = {e.path: e for e in repo.index() if e.stage == 0}
        repo.save_index([old[p] if p in old and (old[p].mode, old[p].sha) == t else Entry(p, t[1], t[0]) for p, t in src.items()])
    if target is not None:
        repo.update_ref("HEAD", target, f"reset: moving to {rev}")
        if not repo.head_ref():
            repo.gwrite("HEAD", (target + "\n").encode())
    for f in ("MERGE_HEAD", "MERGE_MSG", "MERGE_MODE"):
        repo.gremove(f)
    if mode == "hard" and not o.has("q") and target:
        g.out(sub_msg("reset", "headNowAt", commit=subject_of(repo, target)) + "\n")
    if mode == "mixed":
        unstaged_report(g, repo, o.has("q"))
    return 0


def unstaged_report(g: Git, repo: Repo, quiet: bool) -> None:
    if quiet:
        return
    idx = index_files(repo.index())
    lines = []
    for p in sorted(idx):
        w = repo.wt_id(p)
        if w is None:
            lines.append(f"D\t{repo.show(p)}")
        elif w != idx[p]:
            lines.append(f"M\t{repo.show(p)}")
    if lines:
        g.out(sub_msg("reset", "unstagedAfterReset") + "\n" + "\n".join(lines) + "\n")


# ---------- branch / tag ----------

def git_branch(g: Git, args) -> int:
    o = parse("branch", args)
    repo = g.repo()
    M = lambda key, **v: sub_msg("branch", key, **v)
    if o.has("\x01"):
        b = repo.branch()
        if b is not None:
            g.out(b + "\n")
        return 0
    if o.has("d") or o.has("D"):
        if not o.args:
            raise sub_fatal("branch", "nameRequired")
        code = 0
        head = repo.head()
        for name in o.args:
            sha = repo.read_ref("refs/heads/" + name)
            if sha is None:
                g.err(M("notFound", name=name) + "\n")
                code = 1
                continue
            if repo.head_ref() == "refs/heads/" + name:
                g.err(M("checkedOut", name=name, path=repo.top) + "\n")
                code = 1
                continue
            if not (o.has("D") or o.has("f")) and (head is None or sha not in ancestors(repo, [head], g.ctx.check_time)):
                g.err(M("notMerged", name=name) + "\n")
                code = 1
                continue
            repo.delete_ref("refs/heads/" + name)
            g.out(M("deleted", name=name, commit=abbrev(sha)) + "\n")
        return code
    if o.has("m") or o.has("M"):
        names = o.args
        if len(names) == 1:
            old, new = repo.branch(), names[0]
        elif len(names) == 2:
            old, new = names
        else:
            raise sub_fatal("branch", "renameArgs")
        if old is None:
            raise sub_fatal("branch", "renameDetached")
        if not valid_branch_name(new):
            raise sub_fatal("branch", "invalidName", name=new)
        sha = repo.read_ref("refs/heads/" + old)
        if sha is None and repo.head_ref() != "refs/heads/" + old:
            raise sub_fatal("branch", "noSuchBranch", name=old)
        if repo.read_ref("refs/heads/" + new) and new != old:
            if not o.has("M"):
                raise sub_fatal("branch", "exists", name=new)
            if repo.head_ref() == "refs/heads/" + new:
                raise sub_fatal("branch", "forceCurrent", name=new)
        if new == old:
            return 0
        current = repo.head_ref() == "refs/heads/" + old
        nested = new.startswith(old + "/") or old.startswith(new + "/")
        log = repo.gread("logs/refs/heads/" + old) if sha is not None else None
        if sha is not None and nested:
            repo.delete_ref("refs/heads/" + old)  # one name is a directory of the other: the old one has to go first
        # otherwise the new name and HEAD are written before the old name goes, so a failed write loses nothing
        if sha is not None:
            repo.gwrite("refs/heads/" + new, (sha + "\n").encode())
            if log:
                repo.gwrite("logs/refs/heads/" + new, log)
        if current:
            repo.gwrite("HEAD", f"ref: refs/heads/{new}\n".encode())
        if sha is not None and not nested:
            repo.delete_ref("refs/heads/" + old)
        return 0
    listing = o.has("\x02") or o.has("a") or o.has("r") or o.has("v") or not o.args
    if listing:
        pattern = [timed(glob_regex(p, False) + r"\Z", re.S) for p in o.args] if o.has("\x02") and o.args else None
        rows = []  # (mark, name, commit, symbolic target, local branch)
        if not o.has("r") or o.has("a"):
            head_ref = repo.head_ref()
            if head_ref is None and repo.head():
                at, label = detached_name(repo)
                if not pattern:
                    rows.append(("*", M("detachedLabel" if at else "detachedFromLabel", commit=label), repo.head(), None, None))
            for name, sha in repo.refs("refs/heads/"):
                short = name[11:]
                if pattern and not any(p.match(short) for p in pattern):
                    continue
                rows.append(("*" if name == head_ref else " ", short, sha, None, short))
        if o.has("r") or o.has("a"):
            prefix = "remotes/" if o.has("a") else ""
            for short, sha, sym in remote_branches(repo):
                if pattern and not any(p.match(short) or p.match(prefix + short) for p in pattern):
                    continue
                rows.append((" ", prefix + short, sha, sym, None))
        width = max((len(r[1]) for r in rows), default=0)
        for mark, name, sha, sym, local in rows:
            if sym is not None:
                g.out(f"{mark} {name.ljust(width)} -> {sym}\n" if o.has("v") else f"{mark} {name} -> {sym}\n")
            elif o.has("v") and sha:
                up = tracking_brief(repo, local, g.ctx.check_time) if local and o.flags.get("v", 0) > 1 else None
                track = "" if up is None else f"[{up[0]}{': ' + up[1] if up[1] else ''}] "
                g.out(f"{mark} {name.ljust(width)} {abbrev(sha)} {track}{repo.commit(sha).subject}\n")
            else:
                g.out(f"{mark} {name}\n")
        return 0
    # create
    name = o.args[0]
    if len(o.args) > 2:
        raise sub_fatal("branch", "tooManyArguments")
    if not valid_branch_name(name):
        raise sub_fatal("branch", "invalidName", name=name)
    exists = repo.read_ref("refs/heads/" + name)
    if exists and not o.has("f"):
        raise sub_fatal("branch", "exists", name=name)
    if exists and o.has("f") and repo.head_ref() == "refs/heads/" + name:
        raise sub_fatal("branch", "forceCurrent", name=name)
    start = o.args[1] if len(o.args) > 1 else "HEAD"
    sha = try_resolve(repo, start)
    sha = peel(repo, sha, "commit") if sha else None
    if sha is None:
        raise sub_fatal("branch", "notValidObject", name=repo.branch() or "HEAD" if start == "HEAD" else start)
    repo.update_ref("refs/heads/" + name, sha, f"branch: Created from {start}")
    track = remote_start(repo, start)
    if track:
        set_tracking(g, repo, name, track, False)
    return 0


def git_tag(g: Git, args) -> int:
    o = parse("tag", args)
    repo = g.repo()
    M = lambda key, **v: sub_msg("tag", key, **v)
    if o.has("d"):
        code = 0
        for name in o.args:
            sha = repo.read_ref("refs/tags/" + name)
            if sha is None:
                g.err(M("notFound", name=name) + "\n")
                code = 1
                continue
            repo.delete_ref("refs/tags/" + name)
            g.out(M("deleted", name=name, commit=abbrev(sha)) + "\n")
        return code
    if o.has("l") or not o.args:
        pats = [timed(glob_regex(p, False) + r"\Z", re.S) for p in o.args]
        for name, _ in repo.refs("refs/tags/"):
            short = name[10:]
            if not pats or any(p.match(short) for p in pats):
                g.out(short + "\n")
        return 0
    name = o.args[0]
    if len(o.args) > 2:
        raise usage_error("tag", M("tooManyArguments"))
    if not valid_branch_name(name):
        raise sub_fatal("tag", "invalidName", name=name)
    old = repo.read_ref("refs/tags/" + name)
    if old and not o.has("f"):
        raise sub_fatal("tag", "exists", name=name)
    target = resolve(repo, o.args[1]) if len(o.args) > 1 else repo.head()
    if target is None:
        raise fatal("badRevision", rev="HEAD")
    annotated = o.has("a") or o.vals("m") or o.val("F") is not None
    if annotated:
        if o.vals("m"):
            text = "\n\n".join(o.vals("m"))
        elif o.val("F") is not None:
            text = read_message_file(g, o.val("F"))
        else:
            text = g.editor_error()
        text = clean_message(text)
        kind = repo.obj(target)[0]
        body = f"object {target}\ntype {kind}\ntag {name}\ntagger {g.ident('committer')}\n\n{text}"
        target = repo.put("tag", body.encode("utf-8", "surrogateescape"))
    repo.update_ref("refs/tags/" + name, target)
    if old and old != target:
        g.out(M("updated", name=name, commit=abbrev(old)) + "\n")
    return 0


# ---------- merge ----------

def merge_label(repo: Repo, arg: str, sha: str) -> str:
    if repo.read_ref("refs/heads/" + arg):
        return f"branch '{arg}'"
    if repo.read_ref("refs/tags/" + arg):
        return f"tag '{arg}'"
    return f"commit '{arg}'"


def three_way(g: Git, repo: Repo, base: str | None, ours: str, theirs: str, their_label: str, out_lines: list[str]):
    """Merges theirs into ours (the index and work tree hold ours). Returns (files, conflicted paths, index entries)."""
    B, O, T = tree_files(repo, base), tree_files(repo, ours), tree_files(repo, theirs)
    result: dict[str, tuple[str, str]] = {}
    stages: list[Entry] = []
    conflicts = []
    worktree: dict[str, tuple[str, bytes]] = {}
    for p in sorted(set(B) | set(O) | set(T), key=lambda x: x.encode("utf-8", "surrogateescape")):
        g.ctx.check_time()
        b, o, t = B.get(p), O.get(p), T.get(p)
        if o == t:
            if o:
                result[p] = o
            continue
        if b == o:
            if t:
                result[p] = t
            continue
        if b == t:
            if o:
                result[p] = o
            continue
        if o and t and o[0] != "120000" and t[0] != "120000":
            od, td = repo.typed(o[1], "blob"), repo.typed(t[1], "blob")
            bd = repo.typed(b[1], "blob") if b else b""
            out_lines.append(msg("autoMerging", path=p))
            mode = o[0] if o[0] == t[0] else (t[0] if b and b[0] == o[0] else o[0])
            if is_binary(od) or is_binary(td) or is_binary(bd):
                out_lines.append(msg("binaryConflict", path=p, ours="HEAD", theirs=their_label))
                merged, conflict = od, True
            else:
                merged, conflict = merge_lines(bd, od, td, "HEAD", their_label, g.ctx.check_time)
            if not conflict:
                result[p] = (mode, repo.put("blob", merged))
                continue
            out_lines.append(msg("conflictAddAdd" if b is None else "conflictContent", path=p))
            conflicts.append(p)
            for n, side in ((1, b), (2, o), (3, t)):
                if side:
                    stages.append(Entry(p, side[1], side[0], n))
            worktree[p] = (mode, merged)
            continue
        # deleted on one side, changed on the other (or a symbolic link changed both ways)
        if o and t:
            out_lines.append(msg("conflictContent", path=p))
            keep = o
        elif o:
            out_lines.append(msg("conflictModifyDelete", path=p, deleted=their_label, modified="HEAD"))
            keep = o
        else:
            out_lines.append(msg("conflictModifyDelete", path=p, deleted="HEAD", modified=their_label))
            keep = t
        conflicts.append(p)
        for n, side in ((1, b), (2, o), (3, t)):
            if side:
                stages.append(Entry(p, side[1], side[0], n))
        worktree[p] = (keep[0], repo.typed(keep[1], "blob"))
    return result, conflicts, stages, worktree


def git_merge(g: Git, args) -> int:
    o = parse("merge", args)
    repo = g.repo()
    M = lambda key, **v: sub_msg("merge", key, **v)
    if o.has("\x04"):  # --abort
        if not repo.gexists("MERGE_HEAD"):
            raise sub_fatal("merge", "noMergeToAbort")
        switch_trees(g, repo, repo.head(), repo.head(), force=True)
        for f in ("MERGE_HEAD", "MERGE_MSG", "MERGE_MODE"):
            repo.gremove(f)
        return 0
    if o.has("\x05"):  # --continue
        if not repo.gexists("MERGE_HEAD"):
            raise sub_fatal("merge", "noMergeInProgress")
        return git_commit(g, ["--no-edit"])
    entries = repo.index()
    if repo.unmerged(entries):
        raise GitError(M("unmergedFiles"), 128)
    if repo.gexists("MERGE_HEAD"):
        raise GitError(M("mergeHeadExists"), 128)
    if not o.args:
        raise sub_fatal("merge", "noRemote")
    if len(o.args) > 1:
        raise sub_fatal("merge", "octopus")
    arg = o.args[0]
    sha = try_resolve(repo, arg)
    theirs = peel(repo, sha, "commit") if sha else None
    if theirs is None:
        raise GitError(M("notSomething", name=arg), 1)
    head = repo.head()
    quiet = o.has("q")
    if head is None:
        switch_trees(g, repo, None, theirs, force=False, verb="merge")
        repo.update_ref("HEAD", theirs, f"merge {arg}: Fast-forward")
        return 0
    if theirs == head or theirs in ancestors(repo, [head], g.ctx.check_time):
        if not quiet:
            g.out(M("upToDate") + "\n")
        return 0
    base = merge_base(repo, head, theirs, g.ctx.check_time)
    ff = base == head
    if o.has("\x02") and not ff:
        raise sub_fatal("merge", "notFastForward")
    cur = repo.branch()
    label = merge_label(repo, arg, theirs)
    message = "\n\n".join(o.vals("m")) if o.vals("m") else f"Merge {label}" + (f" into {cur}" if cur and cur not in ("master", "main") else "")
    if ff and not o.has("\x01"):
        if not quiet:
            g.out(M("updating", old=abbrev(head), new=abbrev(theirs)) + "\n")
        switch_trees(g, repo, head, theirs, force=False, verb="merge")
        repo.gwrite("ORIG_HEAD", (head + "\n").encode())
        repo.update_ref("HEAD", theirs, f"merge {arg}: Fast-forward")
        if not quiet:
            g.out(M("fastForward") + "\n")
            print_diffstat(g, repo, head, theirs)
        return 0
    # a real merge: refuse when the index has staged changes, or local changes touch merged paths
    idx = index_files(entries)
    head_files = tree_files(repo, head)
    if idx != head_files:
        dirty = [p for p in sorted(set(idx) | set(head_files)) if idx.get(p) != head_files.get(p)]
        raise GitError(sub_msg("checkout", "localChanges", verb="merge", paths="".join(f"\t{p}\n" for p in dirty).rstrip("\n"),
                               advice=sub_msg("checkout", "localChangesAdviceMerge")), 2)
    lines: list[str] = []
    result, conflicts, stages, worktree = three_way(g, repo, base, head, theirs, arg, lines)
    touched = [p for p in sorted(set(result) | set(head_files)) if result.get(p) != head_files.get(p)] + conflicts
    dirty = [p for p in touched if p in idx and repo.wt_id(p) != idx[p]]
    clobber = [p for p in touched if p not in idx and repo.wt_id(p) is not None and not repo.ignored(p)]
    if dirty:
        g.err(sub_msg("checkout", "localChanges", verb="merge", paths="".join(f"\t{p}\n" for p in dirty).rstrip("\n"),
                      advice=sub_msg("checkout", "localChangesAdviceMerge")) + "\n")
        g.out(M("strategyFailed") + "\n")
        return 2
    if clobber:
        raise GitError(sub_msg("checkout", "untrackedOverwritten", verb="merge", paths="".join(f"\t{p}\n" for p in clobber).rstrip("\n"),
                               advice=sub_msg("checkout", "untrackedAdviceMerge")), 2)
    for line in lines:
        g.out(line + "\n")
    repo.gwrite("ORIG_HEAD", (head + "\n").encode())
    # write the result into the work tree and index
    new_entries = []
    for p in sorted(set(head_files) | set(result) | set(worktree)):
        if p in worktree:
            mode, data = worktree[p]
            sha = repo.put("blob", data)
            repo.wt_write(p, mode if mode != "120000" else "100644", sha)
            continue
        t = result.get(p)
        if t is None:
            repo.wt_remove(p)
        else:
            if head_files.get(p) != t or repo.wt_id(p) != t:
                repo.wt_write(p, *t)
            new_entries.append(repo.entry_for(p, *t))
    repo.save_index(new_entries + stages)
    if conflicts:
        repo.gwrite("MERGE_HEAD", (theirs + "\n").encode())
        repo.gwrite("MERGE_MODE", b"no-ff\n" if o.has("\x01") else b"")
        repo.gwrite("MERGE_MSG", (message + "\n\n# Conflicts:\n" + "".join(f"#\t{p}\n" for p in conflicts)).encode())
        g.out(M("failed") + "\n")
        return 1
    tree = repo.write_tree(result)
    body = (f"tree {tree}\nparent {head}\nparent {theirs}\nauthor {g.ident('author')}\ncommitter {g.ident('committer')}\n\n"
            + clean_message(message))
    new = repo.put("commit", body.encode("utf-8", "surrogateescape"))
    repo.update_ref("HEAD", new, f"merge {arg}: Merge made by the 'ort' strategy.")
    if not quiet:
        g.out(M("madeByOrt") + "\n")
        print_diffstat(g, repo, head, new)
    return 0


def print_diffstat(g: Git, repo: Repo, old: str, new: str) -> None:
    a, b = tree_files(repo, old), tree_files(repo, new)
    changes = diff_files(a, b, load=tree_loader(repo, a, b))
    if changes:
        write_stat(g.out, stat_entries(g, repo, a, b, changes))
    for line in summary_lines(changes, a, b):
        g.out(line + "\n")


# ---------- stash ----------

def stash_entries(repo: Repo) -> list[tuple[str, str]]:
    """[(commit, message)], newest first, from the stash's reflog."""
    out = []
    for line in (repo.gtext("logs/refs/stash") or "").split("\n"):
        m = re.match(r"[0-9a-f]{40} ([0-9a-f]{40}) [^\t]*\t(.*)$", line)
        if m:
            out.append((m.group(1), m.group(2)))
    return out[::-1]


def write_stash(repo: Repo, entries) -> None:
    if not entries:
        repo.gremove("refs/stash")
        repo.gremove("logs/refs/stash")
        return
    lines, prev = [], "0" * 40
    who = repo.g.ident("committer", required=False)
    for sha, message in reversed(entries):
        lines.append(f"{prev} {sha} {who}\t{message}\n")
        prev = sha
    repo.gwrite("logs/refs/stash", "".join(lines).encode("utf-8", "surrogateescape"))
    repo.gwrite("refs/stash", (entries[0][0] + "\n").encode())


def stash_ref(repo: Repo, arg: str | None) -> tuple[int, str]:
    entries = stash_entries(repo)
    if not entries:
        raise GitError(sub_msg("stash", "noEntries"), 1)
    n = 0
    if arg is not None:
        m = re.fullmatch(r"(?:stash@\{)?(\d+)\}?", arg)
        if not m:
            raise GitError(sub_msg("stash", "notStash", name=arg), 1)
        n = int(m.group(1))
    if n >= len(entries):
        raise GitError(sub_msg("stash", "noSuchEntry", name=f"stash@{{{n}}}"), 1)
    return n, entries[n][0]


def git_stash(g: Git, args) -> int:
    action = "push"
    if args and not args[0].startswith("-"):
        action, args = args[0], args[1:]
    if action not in ("push", "save", "list", "show", "pop", "apply", "drop", "clear"):
        raise usage_error("stash", sub_msg("stash", "unknownAction", action=action))
    o = parse("stash", args)
    repo = g.repo()
    M = lambda key, **v: sub_msg("stash", key, **v)
    if action in ("push", "save"):
        head = repo.head()
        if head is None:
            raise GitError(M("noInitialCommit"), 1)
        if repo.unmerged():
            raise GitError(M("unmerged"), 1)
        given = "\n\n".join(o.vals("m")) if o.vals("m") else (" ".join(o.args) if action == "save" and o.args else None)
        st = Status(repo, "all" if o.has("u") else "no")
        specs = user_specs(repo, o.args + o.paths) if action == "push" else None
        head_files = tree_files(repo, head)
        if specs is not None:
            known = set(index_files(repo.index())) | set(head_files) | set(st.untracked)
            for raw, spec in zip(o.args + o.paths, specs):
                if not any(pathspec_match([spec], p) for p in known):
                    raise GitError(msg("pathspecNoMatchKnown", path=raw), 1)
            st.staged = [c for c in st.staged if pathspec_match(specs, c[2] or c[1]) or (c[1] and pathspec_match(specs, c[1]))]
            st.unstaged = [c for c in st.unstaged if pathspec_match(specs, c[1])]
            st.untracked = [u for u in st.untracked if pathspec_match(specs, u)]
        if not st.staged and not st.unstaged and not (o.has("u") and st.untracked):
            if not o.has("q"):
                g.out(M("noLocalChanges") + "\n")
            return 0
        b = repo.branch() or "(no branch)"
        desc = f"{b}: {subject_of(repo, head)}"
        message = M("onMessage", branch=b, message=given) if given else M("wipMessage", description=desc)
        ident = g.ident("committer")
        author = g.ident("author")
        idx_files = index_files(repo.index())
        if specs is not None:  # only the named paths change: everything else stays as HEAD has it
            chosen = {p for p in set(idx_files) | set(head_files) if pathspec_match(specs, p)}
            idx_files = {**{p: v for p, v in head_files.items() if p not in chosen}, **{p: v for p, v in idx_files.items() if p in chosen}}
        itree = repo.write_tree(idx_files)
        icommit = repo.put("commit", f"tree {itree}\nparent {head}\nauthor {author}\ncommitter {ident}\n\n{M('indexMessage', description=desc)}\n".encode("utf-8", "surrogateescape"))
        work = dict(idx_files)
        for k, p in enumerate(idx_files):
            if k % 256 == 255:
                g.ctx.check_time()
            if specs is not None and p not in chosen:
                continue
            w = repo.wt_stat(p)
            if w is None:
                del work[p]
            else:
                work[p] = (w[0], repo.put("blob", w[1]))
        parents = [head, icommit]
        if o.has("u") and st.untracked:
            uf = {}
            for k, p in enumerate(st.untracked):
                if k % 256 == 255:
                    g.ctx.check_time()
                w = repo.wt_stat(p)
                if w:
                    uf[p] = (w[0], repo.put("blob", w[1]))
            utree = repo.write_tree(uf)
            parents.append(repo.put("commit", f"tree {utree}\nauthor {author}\ncommitter {ident}\n\n{M('untrackedMessage', description=desc)}\n".encode("utf-8", "surrogateescape")))
        wtree = repo.write_tree(work)
        wcommit = repo.put("commit", (f"tree {wtree}\n" + "".join(f"parent {p}\n" for p in parents)
                                      + f"author {author}\ncommitter {ident}\n\n{message}").encode("utf-8", "surrogateescape"))
        write_stash(repo, [(wcommit, message)] + stash_entries(repo))
        if specs is None:
            switch_trees(g, repo, head, head, force=True)
        else:
            by_path = {e.path: e for e in repo.index() if e.stage == 0}
            for p in sorted(chosen):
                h = head_files.get(p)
                if h is None:
                    by_path.pop(p, None)
                    repo.wt_remove(p)
                    continue
                if repo.wt_id(p) != h:
                    repo.wt_write(p, *h)
                by_path[p] = repo.entry_for(p, *h)
            repo.save_index(list(by_path.values()))
        if o.has("u"):
            for p in st.untracked:
                repo.wt_remove(p)
        if not o.has("q"):
            g.out(M("saved", message=message) + "\n")
        return 0
    if action == "list":
        for i, (sha, message) in enumerate(stash_entries(repo)):
            g.out(f"stash@{{{i}}}: {message}\n")
        return 0
    if action == "clear":
        write_stash(repo, [])
        return 0
    arg = o.args[0] if o.args else None
    n, sha = stash_ref(repo, arg)
    if action == "show":
        c = repo.commit(sha)
        a, b = tree_files(repo, c.parents[0]), repo.flatten(c.tree)
        if not (o.has("p") or any(o.has(k) for k in ("\x03", "\x04"))):
            o.flags["\x01"] = 1
        write_diff(g, repo, a, b, o)
        return 0
    if action == "drop":
        return stash_drop(g, repo, n, sha, o.has("q"), arg)
    # apply / pop
    c = repo.commit(sha)
    base = c.parents[0]
    head = repo.head()
    lines: list[str] = []
    stash_files, base_files = repo.flatten(c.tree), tree_files(repo, base)
    untracked_files = repo.flatten(repo.commit(c.parents[2]).tree) if len(c.parents) > 2 else {}
    for p in untracked_files:
        if repo.wt_stat(p) is not None:
            raise GitError(M("untrackedExists", path=p), 1)
    entries = repo.index()
    idx = {e.path: e for e in entries if e.stage == 0}
    cur = index_files(entries)
    work_now = {p: repo.wt_id(p) for p in set(stash_files) | set(base_files)}
    dirty = [p for p in sorted(set(stash_files) | set(base_files)) if stash_files.get(p) != base_files.get(p)
             and p in cur and work_now.get(p) != cur.get(p)]
    if dirty:
        raise GitError(sub_msg("checkout", "localChanges", verb="merge", paths="".join(f"\t{p}\n" for p in dirty).rstrip("\n"),
                               advice=sub_msg("checkout", "localChangesAdviceMerge")) + "\n" + M("cannotApply"), 1)
    conflicts = []
    if c.tree == repo.commit(base).tree:
        g.out(M("upToDate") + "\n")
    for p in sorted(set(stash_files) | set(base_files)):
        s, b = stash_files.get(p), base_files.get(p)
        if s == b:
            continue
        mine = cur.get(p)
        if mine == b or mine == s:
            if s is None:
                repo.wt_remove(p)
                idx.pop(p, None)
            else:
                repo.wt_write(p, *s)
                if b is None:
                    idx[p] = repo.entry_for(p, *s)
            continue
        if s is None or mine is None:
            up, stashed = M("labelUpstream"), M("labelStashed")
            lines.append(msg("conflictModifyDelete", path=p, deleted=up if mine is None else stashed, modified=stashed if mine is None else up))
            conflicts.append(p)
            continue
        lines.append(msg("autoMerging", path=p))
        merged, conflict = merge_lines(repo.typed(b[1], "blob") if b else b"", repo.typed(mine[1], "blob"), repo.typed(s[1], "blob"),
                                       M("labelUpstream"), M("labelStashed"), g.ctx.check_time)
        repo.wt_write(p, s[0], repo.put("blob", merged))
        if conflict:
            lines.append(msg("conflictContent", path=p))
            conflicts.append(p)
            idx.pop(p, None)
            for k, side in ((1, b), (2, mine), (3, s)):
                if side:
                    entries.append(Entry(p, side[1], side[0], k))
    for p, t in untracked_files.items():
        repo.wt_write(p, *t)
    repo.save_index(list(idx.values()) + [e for e in entries if e.stage and e.path in conflicts])
    for line in lines:
        g.out(line + "\n")
    if conflicts:
        g.out(long_status(repo, Status(repo)))
        if action == "pop":
            g.out(M("keptEntry") + "\n")
        return 1
    if not o.has("q"):
        g.out(long_status(repo, Status(repo)))
    if action == "pop":
        return stash_drop(g, repo, n, sha, o.has("q"), arg)
    return 0


def stash_drop(g: Git, repo: Repo, n: int, sha: str, quiet: bool, given: str | None = None) -> int:
    entries = stash_entries(repo)
    del entries[n]
    write_stash(repo, entries)
    if not quiet:
        g.out(sub_msg("stash", "dropped", name=given or f"refs/stash@{{{n}}}", commit=sha) + "\n")
    return 0


# ---------- plumbing and inspection ----------

def git_rev_parse(g: Git, args) -> int:
    repo = g.find_repo()
    short, abbrev_ref, verify, quiet, symbolic = None, False, False, False, False
    outputs: list[str] = []
    revs = 0
    for a in args:
        if a in ("--is-inside-work-tree", "--is-inside-git-dir", "--show-toplevel", "--git-dir", "--show-prefix", "--absolute-git-dir",
                 "--show-cdup", "--is-bare-repository", "--git-common-dir"):
            r = g.repo()
            outputs.append({"--is-inside-work-tree": "true", "--is-inside-git-dir": "false", "--show-toplevel": r.top,
                            "--git-dir": ".git" if not r.prefix else r.git, "--absolute-git-dir": r.git, "--git-common-dir": ".git" if not r.prefix else r.git,
                            "--show-prefix": r.prefix, "--is-bare-repository": "false",
                            "--show-cdup": "../" * r.prefix.count("/")}[a])
            continue
        m = re.fullmatch(r"--short(?:=(\d+))?", a)
        if m:
            short = max(4, min(40, int(m.group(1) or 7)))
            continue
        if a == "--abbrev-ref":
            abbrev_ref = True
            continue
        if a == "--symbolic-full-name":
            symbolic = True
            continue
        if a == "--verify":
            verify = True
            continue
        if a in ("-q", "--quiet"):
            quiet = True
            continue
        if a == "--":
            continue
        if a.startswith("-"):
            outputs.append(a)
            continue
        r = g.repo()
        revs += 1
        sha = try_resolve(r, a)
        if sha is None:
            if verify:
                if quiet:
                    g.out("".join(x + "\n" for x in outputs))
                    return 1
                raise sub_fatal("rev-parse", "needSingleRevision")
            g.err(msg("badRevision", rev=a) + "\n")
            g.out("".join(x + "\n" for x in outputs) + a + "\n")
            return 128
        if abbrev_ref or symbolic:
            name = None
            prev = re.fullmatch(r"@\{-(\d+)\}", a)
            if prev and abbrev_ref:
                outputs.append(previous_branch(r, int(prev.group(1))) or a)
                continue
            up_of = re.fullmatch(r"(.*)@\{(?:u|upstream)\}", a)
            if up_of:
                up = upstream(r, upstream_branch(r, up_of.group(1)))
                name = (up[0] if abbrev_ref else up[1]) if up else None
            elif a in ("HEAD", "@"):
                ref = r.head_ref()
                name = (ref[11:] if abbrev_ref and ref and ref.startswith("refs/heads/") else ref) if ref else "HEAD"
            elif r.read_ref("refs/heads/" + a):
                name = a if abbrev_ref else "refs/heads/" + a
            elif r.read_ref("refs/tags/" + a):
                name = ("tags/" + a if abbrev_ref and r.read_ref("refs/heads/" + a) else a) if abbrev_ref else "refs/tags/" + a
            elif r.read_ref("refs/remotes/" + a):
                name = a if abbrev_ref else "refs/remotes/" + a
            outputs.append(name if name is not None else (a if abbrev_ref else ""))
            continue
        outputs.append(sha[:short] if short else sha)
    if verify and revs != 1:
        raise sub_fatal("rev-parse", "needSingleRevision")
    g.out("".join(x + "\n" for x in outputs))
    return 0


def git_ls_files(g: Git, args) -> int:
    o = parse("ls-files", args)
    repo = g.repo()
    specs = user_specs(repo, o.args + o.paths)
    if specs is None and repo.prefix:
        specs = [repo.prefix.rstrip("/")]
    if o.has("\x02"):
        repo.prefix = ""
    entries = repo.index()
    out = []
    modes = [k for k in ("c", "o", "m", "d", "u", "s") if o.has(k)]
    if not modes or o.has("c") or (o.has("s") and not o.has("o")):
        for e in entries:
            if pathspec_match(specs, e.path) and (not o.has("u") or e.stage):
                out.append(f"{e.mode} {e.sha} {e.stage}\t{c_quote(repo.show(e.path))}" if o.has("s") or o.has("u") else c_quote(repo.show(e.path)))
        if not o.has("s") and not o.has("u"):
            out = list(dict.fromkeys(out))
    if o.has("u") and not o.has("c") and not o.has("s"):
        out = [f"{e.mode} {e.sha} {e.stage}\t{c_quote(repo.show(e.path))}" for e in entries if e.stage and pathspec_match(specs, e.path)]
    if o.has("d") or o.has("m"):
        for p, (mode, sha) in index_files(entries).items():
            if not pathspec_match(specs, p):
                continue
            w = repo.wt_id(p)
            if (o.has("d") and w is None) or (o.has("m") and w != (mode, sha)):
                out.append(c_quote(repo.show(p)))
    if o.has("o"):
        tracked = {e.path for e in entries}
        for p in repo.walk(""):
            if p in tracked or not pathspec_match(specs, p.rstrip("/")):
                continue
            if o.has("\x01") and repo.ignored(p.rstrip("/"), p.endswith("/")):
                continue
            out.append(c_quote(repo.show(p)))
        out.sort(key=lambda x: x.encode("utf-8", "surrogateescape"))
    for line in out:
        g.out(line + "\n")
    return 0


def git_cat_file(g: Git, args) -> int:
    o = parse("cat-file", args)
    repo = g.repo()
    modes = [k for k in ("t", "s", "p", "e") if o.has(k)]
    if len(modes) > 1 or (not modes and len(o.args) != 2) or (modes and len(o.args) != 1):
        raise usage_error("cat-file", sub_msg("cat-file", "badUsage"))
    name = o.args[-1]
    sha = try_resolve(repo, name)
    if sha is None:
        if modes == ["e"]:
            return 1
        raise sub_fatal("cat-file", "notValid", name=name)
    kind, data = repo.obj(sha)
    if not modes:
        want = o.args[0]
        if want not in ("blob", "tree", "commit", "tag"):
            raise sub_fatal("cat-file", "badType", kind=want)
        sha2 = peel(repo, sha, want)
        if sha2 is None:
            raise sub_fatal("cat-file", "badFile", name=name)
        g.out(bytetext.decode(repo.obj(sha2)[1]))
        return 0
    if o.has("e"):
        return 0
    if o.has("t"):
        g.out(kind + "\n")
    elif o.has("s"):
        g.out(f"{len(data)}\n")
    elif kind == "tree":
        for mode, n, s in repo.tree(sha):
            t = "tree" if mode == "040000" else "commit" if mode == "160000" else "blob"
            g.out(f"{mode} {t} {s}\t{c_quote(n)}\n")
    else:
        g.out(bytetext.decode(data))
    return 0


def git_rev_list(g: Git, args) -> int:
    args = [f"-n{a[1:]}" if re.fullmatch(r"-\d+", a) else a for a in args]
    o = parse("rev-list", args)
    repo = g.repo()
    include, exclude, paths = revisions_and_paths(repo, o)
    if o.has("\x02"):
        include += [peel(repo, s, "commit") for _, s in repo.refs("refs/") if peel(repo, s, "commit")]
        if repo.head():
            include.append(repo.head())
    if not include:
        raise usage_error("rev-list", sub_msg("rev-list", "noRevisions"))
    excluded = ancestors(repo, exclude, g.ctx.check_time) if exclude else set()
    order = [c for c in walk_order(repo, list(dict.fromkeys(include)), g.ctx.check_time) if c not in excluded]
    specs = user_specs(repo, paths)
    if specs is not None:
        order = [c for c in order if diff_files(tree_files(repo, (repo.commit(c).parents or [None])[0]), repo.flatten(repo.commit(c).tree), specs)]
    if o.val("n") is not None and o.val("n").isdigit():
        order = order[:int(o.val("n"))]
    if o.has("\x03"):
        order.reverse()
    if o.has("\x01"):
        g.out(f"{len(order)}\n")
    else:
        g.out("".join(" ".join([c] + (repo.commit(c).parents if o.has("\x05") else [])) + "\n" for c in order))
    return 0


def git_clean(g: Git, args) -> int:
    o = parse("clean", args)
    repo = g.repo()
    force_cfg = config_bool(g.config("clean.requireforce"))
    if not (o.has("f") or o.has("n")) and force_cfg is not False:
        raise sub_fatal("clean", "requireForce")
    specs = user_specs(repo, o.args + o.paths)
    entries = repo.index()
    tracked = {e.path for e in entries}
    if o.has("x") or o.has("X"):
        saved = repo.ignored
        if o.has("X"):
            cands = [p for p in untracked_all(repo, tracked) if saved(p.rstrip("/"), p.endswith("/"))]
        else:
            repo.ignored = lambda *a: False  # type: ignore[method-assign]
            cands = untracked(repo, tracked, "normal")
            repo.ignored = saved  # type: ignore[method-assign]
    else:
        cands = untracked(repo, tracked, "normal")
    for p in cands:
        if not pathspec_match(specs, p.rstrip("/")):
            continue
        if p.endswith("/") and not o.has("d"):
            # without -d only the untracked files directly in tracked directories go
            continue
        if p.endswith("/") and repo.fs.exists(repo.wt(p) + ".git", follow=False):
            continue
        if o.has("n"):
            g.out(sub_msg("clean", "wouldRemove", path=repo.show(p)) + "\n")
            continue
        if not o.has("q"):
            g.out(sub_msg("clean", "removing", path=repo.show(p)) + "\n")
        repo.fs.delete(repo.wt(p.rstrip("/")), recursive=True)
    return 0


def untracked_all(repo: Repo, tracked) -> list[str]:
    return [p for p in repo.walk("") if p not in tracked]


# ---------- config ----------

def git_config(g: Git, args) -> int:
    o = parse("config", args)
    M = lambda k, **v: sub_msg("config", k, **v)
    scope = "global" if o.has("\x01") else "local" if o.has("\x02") else None
    if o.has("l"):
        if scope == "global" or scope is None:
            for n, v, _ in g.global_config()[1].items():
                g.out(f"{n}={v}\n")
        if scope != "global":
            r = g.find_repo()
            if r is None and scope == "local":
                raise sub_fatal("config", "notInGitDir")
            if r is not None:
                for n, v, _ in r.config().items():
                    g.out(f"{n}={v}\n")
        if scope is None:
            for n, v in g.overrides:
                g.out(f"{n}={v}\n")
        return 0
    if o.has("\x03"):  # --unset
        if len(o.args) != 1:
            raise usage_error("config", M("wrongArgs"))
        return config_write(g, scope, canonical_key(o.args[0]), None)
    get = o.has("\x04")
    if not o.args or len(o.args) > 2:
        raise usage_error("config", M("wrongArgs"))
    name = o.args[0]
    if not valid_key(name):
        g.err(M("noSection" if "." not in name else "invalidKey", name=name) + "\n")
        return 1
    key = canonical_key(name)
    if len(o.args) == 1 or get:
        if scope == "global":
            v = g.global_config()[1].get(key)
        elif scope == "local":
            r = g.find_repo()
            if r is None:
                raise sub_fatal("config", "notInGitDir")
            v = r.config().get(key)
        else:
            v = g.config(key)
        if v is None:
            return 1
        if o.has("\x05"):
            b = config_bool(v)
            if b is None:
                raise sub_fatal("config", "badBool", value=v, name=key)
            v = "true" if b else "false"
        g.out(v + "\n")
        return 0
    return config_write(g, scope, key, o.args[1])


def config_write(g: Git, scope, key: str, value) -> int:
    if scope == "global":
        path, cfg = g.global_config()
        changed = cfg.set(key, value)
        if value is None and not changed:
            return 5
        g.ctx.fs.write_bytes(path, cfg.text().encode())
        return 0
    r = g.find_repo()
    if r is None:
        raise sub_fatal("config", "notInGitDir")
    changed = r.config().set(key, value)
    if value is None and not changed:
        return 5
    r.save_config()
    return 0


def git_remote(g: Git, args) -> int:
    o = parse("remote", args)
    repo = g.repo()
    if o.args:
        raise sub_fatal("remote", "noRemotes")
    remotes: dict[str, dict[str, str]] = {}  # the remotes a copied clone names, in config order
    for key, value, _ in repo.config().items():
        if key.startswith("remote.") and key.count(".") >= 2:
            name, _, field = key[7:].rpartition(".")
            remotes.setdefault(name, {}).setdefault(field, value)
    for name, fields in remotes.items():
        if o.has("v"):
            g.out(f"{name}\t{fields.get('url', '')} (fetch)\n{name}\t{fields.get('pushurl', fields.get('url', ''))} (push)\n")
        else:
            g.out(name + "\n")
    return 0


def git_merge_base(g: Git, args) -> int:
    o = parse("merge-base", args)
    repo = g.repo()
    if len(o.args) < (1 if o.has("\x01") else 2) and not (o.has("\x02") and o.args):
        raise GitError(SPEC.subcommands["merge-base"].help.rstrip("\n"), 129)
    if o.has("\x02") and len(o.args) != 2:
        raise sub_fatal("merge-base", "isAncestorArgs")
    commits = []
    for a in o.args:
        sha = try_resolve(repo, a)
        c = peel(repo, sha, "commit") if sha else None
        if c is None:
            raise sub_fatal("merge-base", "notValid", name=a)
        commits.append(c)
    check = g.ctx.check_time
    if o.has("\x02"):
        return 0 if commits[0] in ancestors(repo, [commits[1]], check) else 1
    if o.has("\x01"):  # common to all of them
        common = ancestors(repo, [commits[0]], check)
        for c in commits[1:]:
            common &= ancestors(repo, [c], check)
    else:  # the first against a merge of the others
        common = ancestors(repo, [commits[0]], check) & ancestors(repo, commits[1:], check)
    if not common:
        return 1
    best = common - ancestors(repo, [p for c in common for p in repo.commit(c).parents], check)
    order = walk_order(repo, sorted(best), check)
    g.out("".join(c + "\n" for c in (order if o.has("a") else order[:1])))
    return 0


def git_fsck(g: Git, args) -> int:
    o = parse("fsck", args)
    repo = g.repo()
    check = g.ctx.check_time
    present: dict[str, str] = {}  # every stored object -> its path (loose) or pack
    code = 0
    names = []
    base = repo._gpath("objects")
    for d in sorted(repo.fs.listdir(base)) if repo.fs.is_dir(base, follow=False) else []:
        if re.fullmatch(r"[0-9a-f]{2}", d) and repo.fs.is_dir(f"{base}/{d}", follow=False):
            names += [(d + n, f".git/objects/{d}/{n}") for n in sorted(repo.fs.listdir(f"{base}/{d}")) if re.fullmatch(r"[0-9a-f]{38}", n)]
    for pack in repo.packs():
        names += [(sha, pack.path) for sha in pack.offsets]
    if len(names) > MAX_WALK:
        raise fatal("tooLarge", what="objects")
    kinds: dict[str, str] = {}
    links: dict[str, list[tuple[str, str]]] = {}  # object -> [(kind, id)] it points to
    for sha, path in names:
        check()
        if sha in present:
            continue
        present[sha] = path
        try:
            kind, data = repo.obj(sha)
            kinds[sha] = kind
            if kind == "commit":
                c = repo.commit(sha)
                links[sha] = [("tree", c.tree)] + [("commit", p) for p in c.parents]
            elif kind == "tree":
                links[sha] = [("commit" if m == "160000" else "tree" if m == "040000" else "blob", x)
                              for m, _, x in parse_tree(data, sha) if m != "160000"]
            elif kind == "tag":
                m = re.search(r"^object ([0-9a-f]{40})\ntype (\w+)", data.decode("utf-8", "replace"), re.M)
                links[sha] = [(m.group(2), m.group(1))] if m else []
        except GitError:
            g.err(sub_msg("fsck", "corrupt", object=sha, path=path) + "\n")
            code |= 1
    roots: list[tuple[str, str]] = []
    for e in repo.index():
        if e.mode != "160000":
            roots.append(("blob", e.sha))
    head = repo.head()
    if head:
        roots.append(("commit", head))
    roots += [("commit", s) for _, s in repo.refs("refs/")]
    logs = repo._gpath("logs")
    stack = [logs] if repo.fs.is_dir(logs, follow=False) else []
    while stack:
        d = stack.pop()
        for n in sorted(repo.fs.listdir(d)):
            full = d + "/" + n
            if repo.fs.is_dir(full, follow=False):
                stack.append(full)
            elif repo.fs.is_file(full, follow=False):
                for line in bytetext.decode(repo.fs.read_bytes(full)).split("\n"):
                    roots += [("commit", x) for x in line.split(" ")[:2] if re.fullmatch(r"[0-9a-f]{40}", x) and x != "0" * 40]
    reachable: set[str] = set()
    missing: dict[str, str] = {}
    stack2 = [(k, x, None) for k, x in reversed(roots)]
    while stack2:
        kind, sha, parent = stack2.pop()
        if sha in reachable:
            continue
        reachable.add(sha)
        if len(reachable) % 256 == 0:
            check()
        if sha not in kinds:
            if sha not in present:
                if parent is not None:
                    g.out(sub_msg("fsck", "brokenLink", fromKind=kinds[parent].rjust(7), **{"from": parent}, toKind=kind.rjust(7), to=sha) + "\n")
                missing[sha] = kind
            continue
        stack2 += [(k, x, sha) for k, x in reversed(links.get(sha, []))]
    if missing:
        code |= 2
    referenced = {x for ls in links.values() for _, x in ls}
    report = [(sha, sub_msg("fsck", "missing", kind=k, object=sha)) for sha, k in missing.items()]
    if not o.has("!\x01"):
        report += [(sha, sub_msg("fsck", "dangling", kind=kinds[sha], object=sha))
                   for sha in kinds if sha not in reachable and sha not in referenced]
    # shortcut: git prints these in its hash table's order; sorted by id here
    g.out("".join(line + "\n" for _, line in sorted(report)))
    return code


# ---------- patches: apply, am, format-patch ----------

class FilePatch:
    """One file's part of a unified diff. old/new are paths (None: /dev/null); hunks are
    (old_start, old_count, new_start, new_count, [(sign, line bytes)])."""

    def __init__(self):
        self.old: str | None = None
        self.new: str | None = None
        self.old_mode: str | None = None
        self.new_mode: str | None = None
        self.similarity = 100
        self.renamed = False
        self.binary = False
        self.hunks: list = []

    def counts(self) -> tuple[int, int]:
        return (sum(1 for h in self.hunks for s, _ in h[4] if s == "+"), sum(1 for h in self.hunks for s, _ in h[4] if s == "-"))

    def reverse(self) -> None:
        self.old, self.new, self.old_mode, self.new_mode = self.new, self.old, self.new_mode, self.old_mode
        self.hunks = [(ns, nc, os_, oc, [({"+": "-", "-": "+"}.get(s, s), t) for s, t in lines]) for os_, oc, ns, nc, lines in self.hunks]


def c_unquote(s: str) -> str:
    """A name as git quotes it ("a\\tb", octal escapes for other bytes) back to the name."""
    if not (len(s) >= 2 and s[0] == '"' and s[-1] == '"'):
        return s
    out, i, body = bytearray(), 0, s[1:-1]
    while i < len(body):
        c = body[i]
        if c == "\\" and i + 1 < len(body):
            n = body[i + 1]
            if n in "01234567" and re.fullmatch(r"[0-7]{3}", body[i + 1:i + 4]):
                out.append(int(body[i + 1:i + 4], 8))
                i += 4
                continue
            out += {"a": b"\a", "b": b"\b", "t": b"\t", "n": b"\n", "v": b"\v", "f": b"\f", "r": b"\r"}.get(n, n.encode())
            i += 2
            continue
        out += c.encode("utf-8", "surrogateescape")
        i += 1
    return out.decode("utf-8", "surrogateescape")


def parse_patch(data: bytes, strip: int) -> list[FilePatch]:
    """The file patches in a unified diff (git's extended headers or plain diff -u). Text around them, such as a
    mail's message or signature, is skipped. A hunk cut short or with a stray line is a corrupt patch."""
    lines = split_lines(data)
    n = len(lines)

    def text(k: int) -> str:
        return lines[k].decode("utf-8", "surrogateescape").rstrip("\n")

    def name(raw: str, cut: int) -> str | None:
        raw = raw.split("\t", 1)[0] if not raw.startswith('"') else raw
        raw = c_unquote(raw.rstrip())
        if raw == "/dev/null":
            return None
        parts = raw.split("/")
        return "/".join(parts[cut:]) if len(parts) > cut else raw

    def corrupt(k: int) -> GitError:
        return sub_fatal("apply", "corrupt", line=str(k + 1))
    out = []
    i = 0
    while i < n:
        t = text(i)
        if t.startswith("diff --git "):
            fp = FilePatch()
            rest = t[11:]
            m = re.fullmatch(r'("(?:[^"\\]|\\.)*"|\S+) ("(?:[^"\\]|\\.)*"|\S+)', rest) or re.fullmatch(r"(a/.*) (b/.*)", rest)
            if m:
                fp.old, fp.new = name(m.group(1), strip), name(m.group(2), strip)
            i += 1
            while i < n and not text(i).startswith(("diff --git ", "--- ", "@@ ")):
                h = text(i)
                if h.startswith("new file mode "):
                    fp.old, fp.new_mode = None, h[14:]
                elif h.startswith("deleted file mode "):
                    fp.new, fp.old_mode = None, h[18:]
                elif h.startswith("old mode "):
                    fp.old_mode = h[9:]
                elif h.startswith("new mode "):
                    fp.new_mode = h[9:]
                elif h.startswith(("rename from ", "copy from ")):
                    fp.old, fp.renamed = c_unquote(h.split(" ", 2)[2]), h.startswith("rename")
                elif h.startswith(("rename to ", "copy to ")):
                    fp.new = c_unquote(h.split(" ", 2)[2])
                elif h.startswith("similarity index "):
                    fp.similarity = int(h[17:].rstrip("%")) if h[17:].rstrip("%").isdigit() else 0
                elif h.startswith(("Binary files ", "GIT binary patch")):
                    fp.binary = True
                elif not h.startswith(("index ", "dissimilarity index ")):
                    break
                i += 1
            if i < n and text(i).startswith("--- "):
                if i + 1 >= n or not text(i + 1).startswith("+++ "):
                    raise corrupt(i + 1)
                old, new = name(text(i)[4:], strip), name(text(i + 1)[4:], strip)
                fp.old = old if old is None or not fp.renamed else fp.old
                fp.new = new if new is None or not fp.renamed else fp.new
                i += 2
        elif t.startswith("--- ") and i + 1 < n and text(i + 1).startswith("+++ "):
            fp = FilePatch()
            old, new = name(t[4:], strip), name(text(i + 1)[4:], strip)
            fp.old = None if old is None else (new or old)  # plain diffs patch the +++ file
            fp.new = None if new is None else new
            i += 2
        else:
            i += 1
            continue
        while i < n and text(i).startswith("@@ "):
            m = re.match(r"@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", text(i))
            if not m:
                raise corrupt(i)
            os_, oc, ns, nc = int(m.group(1)), int(m.group(2) or 1), int(m.group(3)), int(m.group(4) or 1)
            i += 1
            body, ro, rn = [], oc, nc
            while ro > 0 or rn > 0:
                if i >= n:
                    raise corrupt(i)
                line = lines[i]
                sign = "" if line in (b"\n", b"\r\n") else line[:1].decode("latin-1")
                if sign == "\\" and body:
                    body[-1] = (body[-1][0], body[-1][1].rstrip(b"\n"))
                    i += 1
                    continue
                if sign in ("", " "):
                    ro, rn = ro - 1, rn - 1
                elif sign == "-":
                    ro -= 1
                elif sign == "+":
                    rn -= 1
                else:
                    raise corrupt(i)
                if ro < 0 or rn < 0:
                    raise corrupt(i)
                body.append((sign or " ", line[1:] if sign else line))
                i += 1
            if i < n and lines[i].startswith(b"\\") and body:
                body[-1] = (body[-1][0], body[-1][1].rstrip(b"\n"))
                i += 1
            fp.hunks.append((os_, oc, ns, nc, body))
        out.append(fp)
    return out


class ApplyFailed(Exception):
    pass


def apply_hunks(g: Git, data: bytes, fp: FilePatch, path: str, verbose: bool) -> bytes:
    """data with fp's hunks applied. Each hunk's lines must match exactly, though it may sit at an offset from the line
    the patch names (nearest first); there is no fuzz."""
    lines = split_lines(data)
    out, pos, offset = [], 0, 0
    for k, (os_, oc, ns, nc, body) in enumerate(fp.hunks):
        g.ctx.check_time()
        pre = [t for s, t in body if s != "+"]
        post = [t for s, t in body if s != "-"]
        start = (os_ - 1 if oc else os_) + offset
        found = None
        for d in range(max(len(lines), 1) + 1):
            for p in ((start + d, start - d) if d else (start,)):
                if pos <= p <= len(lines) - len(pre) and lines[p:p + len(pre)] == pre:
                    found = p
                    break
            if found is not None:
                break
        if found is None:
            if verbose:
                g.err(sub_msg("apply", "searching", lines="".join(x.decode("utf-8", "surrogateescape") for x in pre)) + "\n")
            g.err(sub_msg("apply", "patchFailed", path=path, line=str(os_)) + "\n")
            raise ApplyFailed()
        moved = found - (os_ - 1 if oc else os_)
        if moved and verbose:
            g.err(sub_msg("apply", "hunkOffset", number=str(k + 1), line=str(found + 1),
                          offset=f"{moved} line{'s' if abs(moved) != 1 else ''}") + "\n")
        out += lines[pos:found] + post
        pos, offset = found + len(pre), moved
    return b"".join(out + lines[pos:])


def apply_patches(g: Git, repo, top: str, patches: list[FilePatch], index: bool, worktree: bool, verbose: bool, check: bool) -> bool:
    """Checks every file patch against the work tree and/or the index, then writes them all, or nothing when one fails
    (printing why). index and worktree: git apply --index; index only: --cached."""
    fs = g.ctx.fs
    entries = {e.path: e for e in repo.index() if e.stage == 0} if repo and index else {}
    state: dict[str, tuple[str, bytes] | None] = {}  # path -> (mode, data) after the patches so far; None: removed

    def current(p: str):
        if p in state:
            return state[p]
        if worktree:
            full = top.rstrip("/") + "/" + p
            if repo:
                return repo.wt_stat(p)
            if fs.is_file(full):
                return ("100755" if fs.stat(full).mode & 0o100 else "100644"), fs.read_bytes(full)
            return None
        e = entries.get(p)
        return (e.mode, repo.typed(e.sha, "blob")) if e else None
    ok = True
    for fp in patches:
        path = fp.new or fp.old or ""
        for p in (fp.old, fp.new):
            if p is not None:
                check_path(p)  # both sides: a rename must not reach outside the work tree either
        if verbose:
            g.err(sub_msg("apply", "checking", path=path) + "\n")
        if fp.binary:
            g.err(sub_msg("apply", "binary", path=path) + "\n")
            ok = False
            continue
        try:
            if fp.old is None:
                if current(path) is not None or (index and worktree and path in entries):
                    g.err(sub_msg("apply", "alreadyExists" if worktree else "alreadyInIndex", path=path) + "\n")
                    raise ApplyFailed()
                old_mode, old_data = None, b""
            else:
                have = current(fp.old)
                if have is None:
                    g.err(sub_msg("apply", "noSuchFile" if worktree else "notInIndex", path=fp.old) + "\n")
                    raise ApplyFailed()
                if index and worktree and fp.old not in state:
                    e = entries.get(fp.old)
                    if e is None or object_id("blob", have[1]) != e.sha:
                        g.err(sub_msg("apply", "notMatchIndex", path=fp.old) + "\n")
                        raise ApplyFailed()
                old_mode, old_data = have
        except ApplyFailed:
            ok = False
            continue
        try:
            new_data = apply_hunks(g, old_data, fp, fp.old or path, verbose)
        except ApplyFailed:
            g.err(sub_msg("apply", "doesNotApply", path=fp.old or path) + "\n")
            ok = False
            continue
        if fp.old is not None and fp.old != fp.new:
            state[fp.old] = None
        if fp.new is not None:
            state[fp.new] = (fp.new_mode or old_mode or "100644", new_data)
    if not ok or check:
        return ok
    for fp in patches:
        if verbose:
            g.err(sub_msg("apply", "appliedCleanly", path=fp.new or fp.old) + "\n")
    for p, v in state.items():
        g.ctx.check_time()
        if worktree:
            full = top.rstrip("/") + "/" + p
            if v is None:
                if repo:
                    repo.wt_remove(p)
                elif fs.exists(full, follow=False):
                    fs.delete(full)
            else:
                fs.mkdir(get_parent(full), parents=True)
                fs.write_bytes(full, v[1])
                fs.chmod(full, (0o777 if v[0] == "100755" else 0o666) & ~g.ctx.shell.umask)
        if index:
            if v is None:
                entries.pop(p, None)
            else:
                sha = repo.put("blob", v[1])
                entries[p] = repo.entry_for(p, v[0], sha) if worktree else Entry(p, sha, v[0])
    if index:
        repo.save_index(sorted(entries.values(), key=lambda e: e.path.encode("utf-8", "surrogateescape")))
    return True


def apply_stat(g: Git, patches: list[FilePatch], numstat: bool, stat: bool, summary: bool) -> None:
    def label(fp):
        return f"{fp.old} => {fp.new}" if fp.old and fp.new and fp.old != fp.new else c_quote(fp.new or fp.old)
    if numstat:
        for fp in patches:
            a, d = fp.counts()
            g.out(("-\t-\t" if fp.binary else f"{a}\t{d}\t") + label(fp) + "\n")
    if stat:
        names = [label(fp) for fp in patches]
        width = max((len(x) for x in names), default=0)
        width = min(width, 50)
        room = 70 - width
        top_change = max((sum(fp.counts()) for fp in patches), default=0)
        for fp, nm in zip(patches, names):
            a, d = fp.counts()
            if top_change > room:
                a, d = -(-a * room // top_change), -(-d * room // top_change)  # rounded up: a change never vanishes
            g.out(f" {nm.ljust(width)} |{sum(fp.counts()):5d} {'+' * a}{'-' * d}\n")
        ins = sum(fp.counts()[0] for fp in patches)
        dels = sum(fp.counts()[1] for fp in patches)
        files = len(patches)
        g.out(f" {files} file{'s' if files != 1 else ''} changed, {ins} insertion{'s' if ins != 1 else ''}(+), "
              f"{dels} deletion{'s' if dels != 1 else ''}(-)\n")
    if summary:
        for fp in patches:
            if fp.old is None:
                g.out(f" create mode {fp.new_mode or '100644'} {c_quote(fp.new)}\n")
            elif fp.new is None:
                g.out(f" delete mode {fp.old_mode or '100644'} {c_quote(fp.old)}\n")
            else:
                if fp.old != fp.new:
                    g.out(f" rename {rename_label(fp.old, fp.new)} ({fp.similarity}%)\n")
                if fp.old_mode and fp.new_mode and fp.old_mode != fp.new_mode:
                    g.out(f" mode change {fp.old_mode} => {fp.new_mode} {c_quote(fp.new)}\n")


def git_apply(g: Git, args) -> int:
    o = parse("apply", args)
    repo = g.find_repo()
    if repo is None and (o.has("\x05") or o.has("\x06")):
        repo = g.repo()  # the index needs a repository: fails with git's message
    top = repo.top if repo else g.cwd
    strip = int(o.val("p")) if (o.val("p") or "").isdigit() else 1
    patches = []
    for f in o.args or ["-"]:
        if f == "-":
            data = bytetext.encode(g.ctx.stdin)
        else:
            path = normalize(g.cwd, f)
            if not g.ctx.fs.is_file(path):
                raise GitError(sub_msg("apply", "cannotOpen", file=f, errno="Is a directory" if g.ctx.fs.is_dir(path) else "No such file or directory"), 128)
            data = g.ctx.fs.read_bytes(path)
        patches += parse_patch(data, strip)
    if not patches:
        raise sub_fatal("apply", "noPatches")
    if repo and g.cwd != repo.top:
        # from a subdirectory, files outside it are left alone, as git does
        here = g.cwd[len(repo.top.rstrip("/")) + 1:] + "/"
        patches = [fp for fp in patches if (fp.new or fp.old or "").startswith(here)]
    if o.has("R"):
        for fp in patches:
            fp.reverse()
    informative = o.has("\x01") or o.has("\x02") or o.has("\x03")
    apply_stat(g, patches, o.has("\x02"), o.has("\x01"), o.has("\x03"))
    if informative and not o.has("\x07"):
        return 0  # --stat and friends only describe the patch, even with --check
    cached = o.has("\x06")
    ok = apply_patches(g, repo, top, patches, index=cached or o.has("\x05"), worktree=not cached, verbose=o.has("v"), check=o.has("\x04"))
    return 0 if ok else 1


# ---------- am ----------

def parse_mail(text: str) -> tuple[str, str, str | None, str, str, bytes]:
    """(name, email, date, subject, body, patch) from one message as git format-patch writes it."""
    head, _, rest = text.partition("\n\n")
    headers: dict[str, str] = {}
    last = None
    for line in head.split("\n"):
        if line[:1] in (" ", "\t") and last:
            headers[last] += " " + line.strip()
        elif ":" in line and not line.startswith("From "):
            last, _, v = line.partition(":")
            last = last.strip().lower()
            headers[last] = v.strip()
    m = re.fullmatch(r"\s*(.*?)\s*<([^<>]*)>\s*", headers.get("from", ""))
    name, email = (m.group(1).strip('"'), m.group(2)) if m else (headers.get("from", ""), headers.get("from", ""))
    subject = headers.get("subject", "")
    subject = re.sub(r"^(\s*(\[[^\]]*\]|re:)\s*)+", "", subject, flags=re.I)
    subject = re.sub(r"\s+", " ", subject).strip()
    body_lines, patch_at = [], None
    lines = rest.split("\n")
    for k, line in enumerate(lines):
        if line == "---" or line.startswith(("diff --git ", "Index: ")) or (line.startswith("--- ") and k + 1 < len(lines) and lines[k + 1].startswith("+++ ")):
            patch_at = k
            break
        body_lines.append(line)
    body = "\n".join(body_lines).strip("\n")
    patch = "\n".join(lines[patch_at:]) if patch_at is not None else ""
    return name, email, headers.get("date"), subject, body, patch.encode("utf-8", "surrogateescape")


def split_mbox(text: str) -> list[str]:
    """The messages in a mailbox: each starts at a line beginning 'From '; a file without one is a single message."""
    if not text.startswith("From "):
        return [text] if re.search(r"^(From|Subject):", text, re.M) else []
    out, cur = [], None
    for line in text.split("\n"):
        if line.startswith("From ") and re.match(r"From \S+ \w{3} \w{3} [ \d]\d \d\d:\d\d:\d\d \d{4}", line):
            if cur is not None:
                out.append("\n".join(cur))
            cur = []
            continue
        if cur is not None:
            cur.append(line)
    if cur is not None:
        out.append("\n".join(cur))
    return out


def git_am(g: Git, args) -> int:
    show = None
    rest = []
    for a in args:
        if re.fullmatch(r"--show-current-patch(=.*)?", a):
            show = a.partition("=")[2] or "raw"
        else:
            rest.append(a)
    o = parse("am", rest)
    repo = g.repo()
    st = "rebase-apply"
    busy = repo.gexists(f"{st}/next")

    def mail(k: int) -> str:
        return bytetext.decode(repo.gread(f"{st}/{k:04d}") or b"")

    def number() -> int:
        return int((repo.gtext(f"{st}/next") or "1").strip() or 1)

    def finish() -> None:
        repo.fs.delete(repo._gpath(st), recursive=True)
    if o.has("\x03") or o.has("\x02") or o.has("\x01") or show:
        if not busy:
            raise sub_fatal("am", "notInProgress")
        if show:
            text = mail(number())
            g.out(bytetext.decode(parse_mail(text)[5]) if show == "diff" else text)
            return 0
        head = repo.head()
        if o.has("\x03"):
            orig = (repo.gtext("ORIG_HEAD") or "").strip() or head
            if (repo.gtext(f"{st}/abort-safety") or "").strip() != (head or ""):
                g.err(sub_msg("am", "movedHead") + "\n")  # HEAD moved since the failure: only the session is dropped
            else:
                switch_trees(g, repo, head, orig, force=True)
                if orig and orig != head:
                    repo.update_ref("HEAD", orig, "am --abort")
            finish()
            return 0
        if o.has("\x02"):
            switch_trees(g, repo, head, head, force=True)
            repo.gwrite(f"{st}/next", f"{number() + 1}\n".encode())
        else:
            name, email, date, subject, body, _ = parse_mail(mail(number()))
            tree = repo.write_tree(index_files(repo.index()))
            if tree == (repo.commit(head).tree if head else EMPTY_TREE):
                g.out(sub_msg("am", "applying", subject=subject) + "\n")
                g.out(sub_msg("am", "noChanges") + "\n" + sub_msg("am", "resolveHint") + "\n")
                return 128
            if not o.has("q"):
                g.out(sub_msg("am", "applying", subject=subject) + "\n")
            am_commit(g, repo, tree, name, email, date, subject, body, o.has("q"))
            repo.gwrite(f"{st}/next", f"{number() + 1}\n".encode())
    else:
        if busy:
            raise sub_fatal("am", "inProgress")
        texts = []
        for f in o.args or ["-"]:
            if f == "-":
                texts.append(g.ctx.stdin)
                continue
            path = normalize(g.cwd, f)
            if not g.ctx.fs.is_file(path):
                raise sub_fatal("am", "cannotOpen", file=f, errno="No such file or directory")
            texts.append(bytetext.decode(g.ctx.fs.read_bytes(path)))
        mails = [m for t in texts for m in split_mbox(t)]
        if not mails:
            raise GitError(sub_msg("am", "notPatch"), 128)
        head = repo.head()
        for k, m in enumerate(mails, 1):
            repo.gwrite(f"{st}/{k:04d}", m.encode("utf-8", "surrogateescape"))
        repo.gwrite(f"{st}/last", f"{len(mails)}\n".encode())
        repo.gwrite(f"{st}/next", b"1\n")
        repo.gwrite(f"{st}/abort-safety", ((head or "") + "\n").encode())
        if head:
            repo.gwrite("ORIG_HEAD", (head + "\n").encode())
        staged = diff_files(tree_files(repo, head), index_files(repo.index()))
        if staged:  # like git, the session is already set up and stays behind
            raise sub_fatal("am", "dirtyIndex", paths=", ".join(new or old for _, old, new in staged))
    last = int((repo.gtext(f"{st}/last") or "0").strip() or 0)
    while number() <= last:
        k = number()
        name, email, date, subject, body, patch = parse_mail(mail(k))
        if not o.has("q"):
            g.out(sub_msg("am", "applying", subject=subject) + "\n")
        try:
            patches = parse_patch(patch, 1)
        except GitError as e:
            g.err(str(e) + "\n")
            patches = None
        if patches is not None and not patches:
            repo.gwrite(f"{st}/abort-safety", ((repo.head() or "") + "\n").encode())
            g.out(sub_msg("am", "emptyPatch") + "\n" + sub_msg("am", "resolveHint") + "\n")
            return 128
        if patches is None or not apply_patches(g, repo, repo.top, patches, index=True, worktree=True, verbose=False, check=False):
            g.err(sub_msg("am", "showHint") + "\n")
            repo.gwrite(f"{st}/abort-safety", ((repo.head() or "") + "\n").encode())
            g.out(sub_msg("am", "patchFailedAt", number=f"{k:04d}", subject=subject) + "\n" + sub_msg("am", "resolveHint") + "\n")
            return 128
        tree = repo.write_tree(index_files(repo.index()))
        am_commit(g, repo, tree, name, email, date, subject, body, o.has("q"))
        repo.gwrite(f"{st}/next", f"{k + 1}\n".encode())
    finish()
    return 0


def am_commit(g: Git, repo: Repo, tree: str, name: str, email: str, date: str | None, subject: str, body: str, quiet: bool) -> None:
    head = repo.head()
    author = g.ident("author", name=name, email=email, date=date)
    committer = g.ident("committer")
    message = subject + ("\n\n" + body if body else "") + "\n"
    data = f"tree {tree}\n" + (f"parent {head}\n" if head else "") + f"author {author}\ncommitter {committer}\n\n{message}"
    sha = repo.put("commit", data.encode("utf-8", "surrogateescape"))
    repo.update_ref("HEAD", sha, f"am: {subject}")


# ---------- format-patch ----------

def git_format_patch(g: Git, args) -> int:
    args = [f"-n{a[1:]}" if re.fullmatch(r"-\d+", a) else a for a in args]
    o = parse("format-patch", args)
    repo = g.repo()
    limit = int(o.val("n")) if (o.val("n") or "").isdigit() else None
    include, exclude = [], []
    for a in o.args:
        if ".." in a:
            left, right = a.split("..", 1)
            exclude.append(commit_of(repo, left or "HEAD"))
            include.append(commit_of(repo, right or "HEAD"))
        elif limit is not None:
            include.append(commit_of(repo, a))
        else:
            exclude.append(commit_of(repo, a))  # <since>: the commits after it, up to HEAD
    if not include:
        if repo.head() is None:
            raise fatal("badDefaultRevision")
        include = [repo.head()]
    excluded = ancestors(repo, exclude, g.ctx.check_time) if exclude else set()
    order = [c for c in walk_order(repo, include, g.ctx.check_time) if c not in excluded and len(repo.commit(c).parents) < 2]
    if limit is not None:
        order = order[:limit]
    order.reverse()
    total = len(order)
    out_dir = o.val("o")
    stat_opts, patch_opts = Opts(), Opts()
    stat_opts.flags = {"\x01": 1, "\x1f": 1}
    for k, sha in enumerate(order, 1):
        g.ctx.check_time()
        c = repo.commit(sha)
        an, ae, at, az = parse_ident(c.author)
        lines = c.message.rstrip("\n").split("\n")
        subject_end = next((i for i, l in enumerate(lines) if not l.strip()), len(lines))
        subject = " ".join(l.strip() for l in lines[:subject_end])
        body = "\n".join(lines[subject_end:]).strip("\n")
        a, b = tree_files(repo, c.parents[0] if c.parents else None), repo.flatten(c.tree)
        stat, patch = [], []
        write_diff(g, repo, a, b, stat_opts, out=stat.append)  # shortcut: the stat is drawn 80 columns wide, real git uses 72
        write_diff(g, repo, a, b, patch_opts, out=patch.append)
        prefix = f"[PATCH {k}/{total}]" if total > 1 else "[PATCH]"
        text = (f"From {sha} Mon Sep 17 00:00:00 2001\nFrom: {an} <{ae}>\nDate: {format_date(at, az, 'rfc')}\n"
                f"Subject: {prefix} {subject}\n\n" + (body + "\n" if body else "") + "---\n" + "".join(stat) + "\n"
                + "".join(patch) + "-- \n" + SPEC.version.rsplit(" ", 1)[-1] + "\n\n")
        if o.has("\x01"):
            g.out(text)
            continue
        slug = re.sub(r"[^A-Za-z0-9._]+", "-", subject).strip("-.")[:52].rstrip("-.")
        name = f"{k:04d}-{slug}.patch"
        shown = (out_dir.rstrip("/") + "/" + name) if out_dir else name
        target = normalize(g.cwd, shown)
        g.ctx.fs.mkdir(get_parent(target), parents=True)
        g.ctx.fs.write_bytes(target, text.encode("utf-8", "surrogateescape"))
        g.out(shown + "\n")
    return 0


# ---------- bisect ----------

def bisect_marks(repo: Repo, kind: str) -> list[str]:
    return [s for n, s in repo.refs("refs/bisect/") if n.startswith(f"refs/bisect/{kind}-")]


def bisect_log(repo: Repo, text: str) -> None:
    repo.gwrite("BISECT_LOG", (repo.gread("BISECT_LOG") or b"") + text.encode("utf-8", "surrogateescape"))


def bisect_mark(g: Git, repo: Repo, kind: str, sha: str) -> None:
    c = repo.commit(sha)
    repo.update_ref("refs/bisect/bad" if kind == "bad" else f"refs/bisect/{kind}-{sha}", sha)
    bisect_log(repo, f"# {kind}: [{sha}] {c.subject}\ngit bisect {kind} {sha}\n")


def bisect_steps(n: int) -> int:
    """About how many more steps n candidates take: log2, rounded down when n is close to a power of two."""
    if n < 3:
        return 0
    p = n.bit_length() - 1
    return p if (1 << p) < 3 * (n - (1 << p)) else p - 1


def bisect_next(g: Git, repo: Repo) -> str:
    """Checks out the next commit to test, or reports the first bad one. Returns 'wait', 'next', 'found' or 'skipped'."""
    bad = repo.read_ref("refs/bisect/bad")
    goods = bisect_marks(repo, "good")
    if not bad or not goods:
        line = (sub_msg("bisect", "statusBoth") if not bad and not goods else sub_msg("bisect", "statusGood") if bad
                else sub_msg("bisect", "statusBad" if len(goods) == 1 else "statusBadMany", count=str(len(goods))))
        g.out(line + "\n")
        bisect_log(repo, f"# {line}\n")
        return "wait"
    check = g.ctx.check_time
    candidates = ancestors(repo, [bad], check) - ancestors(repo, goods, check)
    if not candidates:
        raise GitError(sub_msg("bisect", "bothGoodAndBad", commit=bad), 1)
    skipped = set(bisect_marks(repo, "skip"))
    order = walk_order(repo, [bad], check, limit_set=candidates)
    order.reverse()  # oldest first: of equally good splits, the older one is tested
    total = len(candidates)
    best, best_score, reach = None, -1, 0
    if total > 1:
        # shortcut: counts reachable candidates per commit (quadratic); fine for the histories a machine holds
        for c in order:
            if c in skipped or c == bad:
                continue
            w = len(ancestors(repo, [c], check) & candidates)
            if min(w, total - w) > best_score:
                best, best_score, reach = c, min(w, total - w), w
    if best is None:
        if total > 1 or bad in skipped:
            left = [c for c in reversed(order) if c in skipped and c != bad] + [bad]
            g.out(sub_msg("bisect", "onlySkipped", commits="\n".join(left)) + "\n")
            return "skipped"
        c = repo.commit(bad)
        g.out(sub_msg("bisect", "firstBad", commit=bad) + "\n")
        git_show(g, ["--stat", bad])
        bisect_log(repo, sub_msg("bisect", "logFirstBad", commit=bad, subject=c.subject) + "\n")
        return "found"
    left, steps = total - reach - 1, bisect_steps(total)
    move_head(g, repo, None, best, best, force=False, quiet=True)
    g.out(sub_msg("bisect", "bisecting", revisions=f"{left} revision{'s' if left != 1 else ''}",
                  steps=f"{steps} step{'s' if steps != 1 else ''}") + "\n")
    g.out(sub_msg("bisect", "current", commit=best, subject=repo.commit(best).subject) + "\n")
    return "next"


def bisect_clear(repo: Repo) -> None:
    for name, _ in repo.refs("refs/bisect/"):
        repo.delete_ref(name)
    for f in ("BISECT_START", "BISECT_LOG"):
        repo.gremove(f)


async def git_bisect(g: Git, args) -> int:
    repo = g.repo()
    if not args:
        raise GitError(sub_msg("bisect", "needCommand") + "\n\n" + SPEC.subcommands["bisect"].help.rstrip("\n"), 129)
    action, rest = args[0], args[1:]
    if action.startswith("-"):
        parse("bisect", [action])  # bisect has no options: the usual unknown-option error
    busy = repo.gexists("BISECT_START")

    def commit_arg(rev: str) -> str:
        sha = try_resolve(repo, rev)
        c = peel(repo, sha, "commit") if sha else None
        if c is None:
            raise GitError(sub_msg("bisect", "badRev", rev=rev), 1)
        return c
    if action == "start":
        revs = [commit_arg(r) for r in rest if r != "--"]
        if busy:
            bisect_clear(repo)
        repo.gwrite("BISECT_START", ((repo.branch() or repo.head() or "") + "\n").encode())
        bisect_log(repo, "git bisect start" + "".join(f" '{r}'" for r in rest if r != "--") + "\n")
        if revs:
            bisect_mark(g, repo, "bad", revs[0])
        for r in revs[1:]:
            bisect_mark(g, repo, "good", r)
        bisect_next(g, repo)
        return 0
    if action in ("good", "bad", "old", "new", "skip"):
        if not busy:
            g.err(sub_msg("bisect", "needStart") + "\n")
            return 1
        kind = {"old": "good", "new": "bad"}.get(action, action)
        for sha in [commit_arg(r) for r in rest] or [commit_arg("HEAD")]:
            bisect_mark(g, repo, kind, sha)
        result = bisect_next(g, repo)
        return 2 if result == "skipped" else 0
    if action == "reset":
        if not busy:
            g.out(sub_msg("bisect", "notBisecting") + "\n")
            return 0
        start = (repo.gtext("BISECT_START") or "").strip()
        target = rest[0] if rest else start
        if repo.read_ref("refs/heads/" + target) is not None:
            move_head(g, repo, "refs/heads/" + target, repo.read_ref("refs/heads/" + target), target, force=False, quiet=False)
        elif target:
            sha = commit_arg(target)
            move_head(g, repo, None, sha, target, force=False, quiet=False)
        bisect_clear(repo)
        return 0
    if action == "log":
        if not busy:
            raise GitError(sub_msg("bisect", "logNotBisecting"), 1)
        g.out(bytetext.decode(repo.gread("BISECT_LOG") or b""))
        return 0
    if action == "run":
        if not busy:
            g.err(sub_msg("bisect", "needStart") + "\n")
            return 1
        if not rest:
            raise GitError(sub_msg("bisect", "needCommand"), 1)
        if not repo.read_ref("refs/bisect/bad") or not bisect_marks(repo, "good"):
            bisect_next(g, repo)
            return 1
        shown = " ".join("'" + a.replace("'", "'\\''") + "'" for a in rest)
        while True:
            g.out(sub_msg("bisect", "running", command=shown) + "\n")
            code = await g.ctx.shell._invoke(list(rest), g.ctx.io)
            g.cwd = g.ctx.shell.cwd if g.ctx.fs.is_dir(g.cwd) is False else g.cwd
            if code < 0 or code >= 128:
                g.err(sub_msg("bisect", "runFailed", code=str(code), command=shown) + "\n")
                return 127
            head = repo.head()
            bisect_mark(g, repo, "skip" if code == 125 else "good" if code == 0 else "bad", head)
            if code == 125:
                pass
            result = bisect_next(g, repo)
            if result == "found":
                g.out(sub_msg("bisect", "runFound") + "\n")
                return 0
            if result == "skipped":
                g.err(sub_msg("bisect", "runCannotContinue") + "\n")
                return 2
            if result == "wait":
                return 1
    g.err(sub_msg("bisect", "notAvailable", action=action) + "\n")
    return 1


HANDLERS = {
    "apply": git_apply, "am": git_am, "format-patch": git_format_patch, "bisect": git_bisect,
    "merge-base": git_merge_base, "fsck": git_fsck,
    "remote": git_remote,
    "init": git_init, "status": git_status, "add": git_add, "rm": git_rm, "mv": git_mv, "commit": git_commit, "log": git_log,
    "show": git_show, "diff": git_diff, "branch": git_branch, "checkout": git_checkout, "switch": git_switch, "restore": git_restore,
    "reset": git_reset, "tag": git_tag, "merge": git_merge, "stash": git_stash, "rev-parse": git_rev_parse, "ls-files": git_ls_files,
    "cat-file": git_cat_file, "config": git_config, "clean": git_clean, "rev-list": git_rev_list,
}


def register(shell) -> None:
    shell.register_command("git", _git)
