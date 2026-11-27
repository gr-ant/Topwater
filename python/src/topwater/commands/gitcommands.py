"""git: init, status, add, rm, mv, commit, log, show and diff. See docs/architecture.md."""
from __future__ import annotations

import re

from .. import bytetext
from ..vfs import normalize
from .git import Opts, Repo, parse, sub_fatal, sub_msg, usage_error
from .gitcore import (
    Commit, EMPTY_TREE, GitError, MAX_WALK, c_quote, check_path, fatal, format_date, is_binary, msg, myers, object_id,
    parse_date, parse_ident, split_lines, timed,
)
from .gitrevisions import (
    Git, Status, abbrev, ancestors, blob_data, commit_of, commit_time, detached_name, diff_files, index_files, merge_base,
    missing_path, pathspec_match, peel, remote_branches, stat_entries, summary_lines, tracking_brief, tracking_text,
    tree_files, tree_loader, try_resolve, user_regex, user_specs, walk_order, worktree_files, write_diff, write_stat,
)
from .text import posix_regex_error, posix_to_python


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
    if repo.gexists("rebase-apply/next"):
        out += [M("amInProgress"), ""]
    if repo.gexists("BISECT_START"):
        out += [M("bisecting", branch=(repo.gtext("BISECT_START") or "").strip()), ""]
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
                if re.match(rb"(<{7}|={7}|>{7})(?:[ \t\r]|$)", text):
                    bad = True
                    g.out(sub_msg("diff", "checkMarker", file=name, line=str(j + 1)) + "\n")
                    continue
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
