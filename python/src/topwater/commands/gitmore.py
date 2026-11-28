"""git: checkout, switch, restore, reset, branch, tag, merge, stash, plumbing and inspection, and config. See docs/architecture.md."""
from __future__ import annotations

import re

from .. import bytetext
from ..vfs import normalize
from .git import Repo, SPEC, parse, sub_fatal, sub_msg, usage_error
from .gitcommands import clean_message, git_commit, long_status, read_message_file, revisions_and_paths
from .gitcore import (
    Entry, GitError, MAX_WALK, c_quote, canonical_key, config_bool, fatal, glob_regex, is_binary, merge_lines, msg,
    parse_tree, timed, valid_key,
)
from .gitrevisions import (
    Git, Status, abbrev, ancestors, commit_of, detached_name, diff_files, index_files, merge_base, pathspec_match, peel,
    previous_branch, remote_branches, resolve, stat_entries, summary_lines, tracking_brief, tracking_text, tree_files,
    tree_loader, try_resolve, untracked, upstream, upstream_branch, user_specs, walk_order, write_diff, write_stat,
)


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
    if not o.has("\x01") and valid_branch_name(target) and repo.read_ref("refs/heads/" + target) and len(o.args) == 1:
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
    linked = 0  # all kept until the end: as many as objects one walk may visit
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
        linked += len(links.get(sha, ()))
        if linked > MAX_WALK:
            raise fatal("tooLarge", what="the object graph")
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
