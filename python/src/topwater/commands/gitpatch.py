"""git: apply, am, format-patch and bisect, and the table of subcommands. See docs/architecture.md."""
from __future__ import annotations

import re

from .. import bytetext
from ..vfs import get_parent, normalize
from .git import Opts, Repo, SPEC, parse, sub_fatal, sub_msg
from .gitcommands import git_add, git_commit, git_diff, git_init, git_log, git_mv, git_rm, git_show, git_status
from .gitcore import EMPTY_TREE, Entry, GitError, c_quote, check_path, fatal, format_date, object_id, parse_ident, split_lines
from .gitmore import (
    git_branch, git_cat_file, git_checkout, git_clean, git_config, git_fsck, git_ls_files, git_merge, git_merge_base,
    git_remote, git_reset, git_restore, git_rev_list, git_rev_parse, git_stash, git_switch, git_tag, move_head,
    switch_trees,
)
from .gitrevisions import (
    Git, ancestors, commit_of, diff_files, index_files, peel, rename_label, tree_files, try_resolve, walk_order,
    write_diff,
)


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


def find_lines(lines: list, pre: list, pos: int, check) -> list[int]:
    """Every index at or after pos where pre occurs in lines, in linear time (Knuth-Morris-Pratt over the lines), so
    a file of identical lines cannot make the search quadratic."""
    if not pre:
        return list(range(pos, len(lines) + 1))
    fail, k = [0] * len(pre), 0
    for i in range(1, len(pre)):
        while k and pre[i] != pre[k]:
            k = fail[k - 1]
        if pre[i] == pre[k]:
            k += 1
        fail[i] = k
    hits, k = [], 0
    for i in range(pos, len(lines)):
        if i % 4096 == 0:
            check()
        while k and lines[i] != pre[k]:
            k = fail[k - 1]
        if lines[i] == pre[k]:
            k += 1
        if k == len(pre):
            hits.append(i - k + 1)
            k = fail[k - 1]
    return hits


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
        hits = find_lines(lines, pre, pos, g.ctx.check_time)
        found = min(hits, key=lambda p: (abs(p - start), p < start), default=None)
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
            if fs.is_symlink(full):
                return "120000", fs.read_link(full).encode("utf-8", "surrogateescape")
            if fs.is_file(full):
                return ("100755" if fs.stat(full).mode & 0o100 else "100644"), fs.read_bytes(full)
            return None
        e = entries.get(p)
        return (e.mode, repo.typed(e.sha, "blob")) if e else None

    def beyond_link(p: str) -> bool:
        """Whether a directory above p is a symbolic link (or a file or link these patches make), which git refuses
        to write through."""
        parts = p.split("/")[:-1]
        for k in range(1, len(parts) + 1):
            above = "/".join(parts[:k])
            if state.get(above) is not None or (worktree and fs.is_symlink(top.rstrip("/") + "/" + above)):
                return True
        return False
    ok = True
    for fp in patches:
        path = fp.new or fp.old or ""
        for p in (fp.old, fp.new):
            if p is not None:
                try:
                    check_path(p)  # both sides: a rename must not reach outside the work tree either
                except GitError:
                    raise sub_fatal("apply", "invalidPath", path=p) from None
        if verbose:
            g.err(sub_msg("apply", "checking", path=path) + "\n")
        if fp.binary:
            g.err(sub_msg("apply", "binary", path=path) + "\n")
            ok = False
            continue
        if fp.old_mode and fp.new_mode and (fp.old_mode == "120000") != (fp.new_mode == "120000"):
            g.err(sub_msg("apply", "modeMismatch", path=path, new=fp.new_mode, old=fp.old_mode) + "\n")
            ok = False
            continue
        linked = next((p for p in (fp.old, fp.new) if p is not None and beyond_link(p)), None)
        if linked is not None:
            g.err(sub_msg("apply", "beyondLink", path=linked) + "\n")
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
                    g.err(sub_msg("apply", "notInIndex" if index else "noSuchFile", path=fp.old) + "\n")
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
            mode = fp.new_mode or old_mode or "100644"
            if mode != "120000":  # any executable bit makes an executable file, as git reads modes
                mode = "100755" if re.fullmatch(r"[0-7]+", mode) and int(mode, 8) & 0o111 else "100644"
            state[fp.new] = (mode, new_data)
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
                if fs.is_symlink(full) or (v[0] == "120000" and fs.exists(full, follow=False)):
                    fs.delete(full)  # a link is replaced, never written through
                if v[0] == "120000":
                    fs.create_symlink(full, v[1].decode("utf-8", "surrogateescape"))
                else:
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

def decode_words(value: str) -> str:
    """RFC 2047 encoded words (=?UTF-8?q?J=C3=BCrgen?=, =?UTF-8?b?...?=) in a mail header, as git format-patch writes
    non-ASCII names and subjects; the space between two encoded words goes."""
    import base64

    def one(m: re.Match) -> str:
        charset, kind, text = m.group(1), m.group(2).lower(), m.group(3)
        try:
            raw = (base64.b64decode(text + "=" * (-len(text) % 4)) if kind == "b"
                   else re.sub(rb"=([0-9A-Fa-f]{2})", lambda h: bytes([int(h.group(1), 16)]), text.replace("_", " ").encode("latin-1")))
            return raw.decode(charset if charset.lower() in ("utf-8", "us-ascii", "iso-8859-1", "latin-1") else "utf-8", "replace")
        except (ValueError, UnicodeError):
            return m.group(0)
    word = r"=\?([^?\s]+)\?([QqBb])\?([^?\s]*)\?="
    value = re.sub(rf"({word})\s+(?={word})", r"\1", value)
    return re.sub(word, one, value)


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
    for k in ("from", "subject"):
        if k in headers:
            headers[k] = decode_words(headers[k])
    m = re.fullmatch(r"\s*(.*?)\s*<([^<>]*)>\s*", headers.get("from", ""))
    name, email = (m.group(1).replace('"', ""), m.group(2)) if m else (headers.get("from", ""), headers.get("from", ""))
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
        inside = {c: [p for p in repo.commit(c).parents if p in candidates] for c in order}
        linear = all(len(ps) <= 1 for ps in inside.values())
        weight: dict[str, int] = {}
        for c in order:  # oldest first, so a parent's count is known
            if linear and all(p in weight for p in inside[c]):  # itself plus what its one parent reaches: linear time
                weight[c] = 1 + (weight[inside[c][0]] if inside[c] else 0)
            else:
                # shortcut: with merges, counts reachable candidates per commit (quadratic); fine for the histories a machine holds
                weight[c] = len(ancestors(repo, [c], check) & candidates)
            check()
        for c in order:
            if c in skipped or c == bad:
                continue
            w = weight[c]
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
        bisect_clear(repo)  # marks left without a session (bisect reset run from bisect run) go too
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
            if not repo.gexists("BISECT_START"):
                return 1  # the command ended the session (git bisect reset): nothing left to mark
            head = repo.head()
            bisect_mark(g, repo, "skip" if code == 125 else "good" if code == 0 else "bad", head)
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
