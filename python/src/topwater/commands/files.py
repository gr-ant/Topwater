"""Filesystem commands: ls cd-free file ops, find, tree."""
from __future__ import annotations

import math
import re
from datetime import datetime, timedelta, timezone

from ..shell import glob_to_regex
from .._spec import COMMANDS, ERRNO
from ..vfs import NO_SPACE, ShellError, UsageException, format_mode, get_name, get_parent, normalize, utc_now
from . import Args, inputs, parse_date, quote_always, quote_l, spec_message, split_lines, strftime


def typed(message: str, arg: str, abs_path: str) -> tuple[str, str]:
    """A filesystem error "/abs/dir/x: reason" as ("arg/x", reason): the path shown the way the user typed it."""
    path, _, reason = message.rpartition(": ")
    base = abs_path.rstrip("/")
    if not path or path == abs_path:
        return (arg if path else abs_path), reason
    return (arg.rstrip("/") + path[len(base):] if path.startswith(base + "/") else path), reason


def touch_stamp(t: str, now: datetime) -> datetime | None:
    """touch -t [[CC]YY]MMDDhhmm[.ss]; a two-digit year 69-99 is 19YY, else 20YY."""
    m = re.fullmatch(r"(\d{2}|\d{4})?(\d{2})(\d{2})(\d{2})(\d{2})(?:\.(\d{2}))?", t)
    if not m:
        return None
    y = m.group(1)
    year = now.year if not y else int(y) if len(y) == 4 else int(y) + (1900 if int(y) >= 69 else 2000)
    try:
        return datetime(year, int(m.group(2)), int(m.group(3)), int(m.group(4)), int(m.group(5)), int(m.group(6) or 0))
    except ValueError:
        return None


def _human(n: int) -> str:
    """ls -h: powers of 1024, rounded up; one decimal below 10 (4.0K, 1.1M), whole numbers above (121K)."""
    if n < 1024:
        return str(n)
    v, u = float(n), -1
    while True:
        v /= 1024
        u += 1
        if v < 1024 or u >= 5:
            break
    tenths = math.ceil(v * 10) / 10
    if tenths < 10:
        return f"{tenths:.1f}" + "KMGTPE"[u]
    v = math.ceil(v)
    return "1.0" + "KMGTPE"[u + 1] if v >= 1024 and u < 5 else f"{v:.0f}" + "KMGTPE"[u]


def _ls_date(t: datetime, now: datetime, style: str | None = None) -> str:
    """ls -l's date: "Nov 25 16:52" within the last six months, else "Jan  2  2020" (also for future times);
    or in a --time-style: full-iso, long-iso, iso, or +FORMAT (FORMAT for old times, NEWLINE FORMAT for recent ones)."""
    recent = timedelta(0) <= now - t < timedelta(seconds=15778476)
    if style == "full-iso":
        return strftime(t, "%Y-%m-%d %H:%M:%S.%N %z")
    if style == "long-iso":
        return strftime(t, "%Y-%m-%d %H:%M")
    if style == "iso":
        return strftime(t, "%m-%d %H:%M" if recent else "%Y-%m-%d ")
    if style and style[0] == "+":
        f = style[1:].split("\n", 1)
        return strftime(t, f[1] if recent and len(f) > 1 else f[0])
    return f"{t:%b} {t.day:2} {t:%H:%M}" if recent else f"{t:%b} {t.day:2}  {t:%Y}"


_TIME_STYLES = ["full-iso", "long-iso", "iso", "locale", "posix-full-iso", "posix-long-iso", "posix-iso", "posix-locale"]


def _time_style(arg: str) -> str:
    """ls --time-style: a style name, a unique abbreviation of one, or +FORMAT; posix- styles act as the plain ones,
    since the locale is not POSIX."""
    if arg.startswith("+"):
        return arg
    hits = [arg] if arg in _TIME_STYLES else [x for x in _TIME_STYLES if x.startswith(arg)]
    if len(hits) != 1:
        raise ShellError(spec_message("ls", "badTimeStyle", problem="ambiguous" if hits else "invalid", value=arg), 2)
    return hits[0].removeprefix("posix-")


def _block_size(st, human: bool) -> str:
    """ls -s: the space a file takes, in KiB (or -h units)."""
    return _human(st.blocks * 512) if human else str(st.blocks // 2)


def _long_entries(ctx, entries, human: bool, width_from=(), style: str | None = None, blocks: bool = False, follow: bool = False, classify=None):
    """ls -l rows, each column as wide as its widest entry (owners left-aligned, numbers right-aligned).
    width_from: more paths that count toward the widths (ls sizes the columns for its file operands over all operands)."""
    now = utc_now()

    def size(st):
        return _human(st.size) if human else str(st.size)

    # broken: -L meets a link leading nowhere, shown as a row of question marks
    rows = [(ctx.fs.stat(p, follow and not broken), name, p, broken) for p, name in entries for broken in [follow and not ctx.fs.exists(p)]]
    if not rows:
        return []
    every = [st for st, _, _, broken in rows if not broken] + [ctx.fs.stat(p, follow) for p in width_from]

    def width(f):
        return max((len(f(st)) for st in every), default=1)

    lw, ow, sw, bw = (width(f) for f in (lambda st: str(st.links), lambda st: st.owner, size, lambda st: _block_size(st, human)))
    dw = width(lambda st: _ls_date(st.modified, now, style))
    out = []
    for st, name, p, broken in rows:
        if broken:
            out.append(("?".rjust(bw) + " " if blocks else "") + f"l????????? {'?':>{lw}} {'?':<{ow}} {'?':<{ow}} {'?':>{sw}} {'?':>{dw}} {name}")
            continue
        # a link shows where it points; -F marks that target's type
        if st.is_symlink:
            name += " -> " + st.link_target + (classify(ctx.fs.stat(p)) if classify and ctx.fs.exists(p) else "")
        elif classify:
            name += classify(st)
        out.append((_block_size(st, human).rjust(bw) + " " if blocks else "")
                   + f"{format_mode(st.is_dir, st.mode, st.is_symlink)} {st.links:>{lw}} {st.owner:<{ow}} {st.owner:<{ow}} {size(st):>{sw}} {_ls_date(st.modified, now, style)} {name}")
    return out


def _ls(ctx):
    # \x01: --time-style=STYLE, \x02: --full-time
    a = Args.parse(ctx.args, COMMANDS["ls"])
    paths = a.positional or ["."]
    lng, human, blocks, deref, fs = a.has("l") or a.has("\x02"), a.has("h"), a.has("s"), a.has("L"), ctx.fs
    style = "full-iso" if a.has("\x02") else _time_style(a.value("\x01")) if lng and a.value("\x01") is not None else None
    code = 0

    def classify(st):  # -F: what each kind of entry gets; a link itself gets @ (unless -L follows it)
        return "/" if st.is_dir else "@" if st.is_symlink else "*" if st.mode & 0o111 else ""

    def show(entries, follow, width_from=()):
        if lng:
            lines = _long_entries(ctx, entries, human, width_from, style, blocks, follow, classify if a.has("F") else None)
        else:
            named = [(p, n + (classify(fs.stat(p, follow)) if a.has("F") and fs.exists(p, follow) else "")) for p, n in entries]
            if blocks:
                sizes = ["?" if follow and not fs.exists(p) else _block_size(fs.stat(p, follow), human) for p, _ in named]
                lines = [sz.rjust(max(map(len, sizes))) + " " + n for sz, (_, n) in zip(sizes, named)]
            else:
                lines = [n for _, n in named]
        for line in lines:
            ctx.print(line)

    # a link named on the command line is followed to a directory, unless -l, -d or -F shows the link itself
    def follow_operand(p):
        return deref or a.has("H") or p.endswith("/") or not (lng or a.has("d") or a.has("F"))

    def exists(p):
        return fs.exists(ctx.resolve(p), deref or p.endswith("/"))

    def stat_of(p):
        return fs.stat(ctx.resolve(p), deref or a.has("H") or p.endswith("/"))

    for p in paths:
        if not exists(p):
            ctx.error("ls: " + spec_message("ls", "cannotAccess", file=p, errno=fs.why_missing(ctx.resolve(p), deref or p.endswith("/"))))
            code = 2
    def ordered(ps):  # operands are listed like directory entries: byte order (C locale), or by -t / -S, reversed by -r
        ps = sorted(ps, key=lambda p: p.encode("utf-8", "surrogateescape"))
        if a.has("t"): ps = sorted(ps, key=lambda p: stat_of(p).modified, reverse=True)
        if a.has("S"): ps = sorted(ps, key=lambda p: stat_of(p).size, reverse=True)
        return ps[::-1] if a.has("r") else ps

    files = ordered([p for p in paths if exists(p) and (a.has("d") or not fs.is_dir(ctx.resolve(p), follow_operand(p)))])
    groups: dict[bool, list[str]] = {}
    for f in files:
        groups.setdefault(follow_operand(f), []).append(f)
    for follow, group in groups.items():
        show([(ctx.resolve(f), f) for f in group], follow, [ctx.resolve(p) for p in paths if p not in files and exists(p)])
    dirs = ordered([p for p in paths if p not in files and fs.is_dir(ctx.resolve(p))])
    first = [not files]
    listed: set[str] = set()  # real paths, so -RL stops at a link back up the tree

    def list_dir(path, label):
        nonlocal code
        path = fs.real_path(path)[0] or path  # where it really is, so "." and ".." are the directories themselves
        if a.has("R") and deref:
            if path in listed:
                ctx.error("ls: " + spec_message("ls", "alreadyListed", file=label))
                code = 2
                return
            listed.add(path)
        if len(paths) > 1 or a.has("R"):
            ctx.print(("" if first[0] else "\n") + label + ":")
        first[0] = False
        if not fs.can_read(path):
            ctx.error("ls: " + spec_message("ls", "cannotOpenDirectory", file=label, errno="Permission denied"))
            code = 2
            return
        entries = [n for n in fs.listdir(path) if a.has("a") or a.has("A") or not n.startswith(".")]
        if deref and (lng or blocks):  # a link leading nowhere: an error, then a row of question marks
            for n in [n for n in entries if not fs.exists(normalize(path, n))]:
                ctx.error("ls: " + spec_message("ls", "cannotAccess", file=("" if label == "." else label.rstrip("/") + "/") + n, errno=fs.why_missing(normalize(path, n))))
                code = 1

        def st(n):
            child = normalize(path, n)
            return fs.stat(child, deref and fs.exists(child))

        if a.has("t"):
            entries.sort(key=lambda n: st(n).modified, reverse=True)
        if a.has("S"):
            entries.sort(key=lambda n: st(n).size, reverse=True)
        if a.has("a"):
            entries = [".", ".."] + entries
        if a.has("r"):
            entries.reverse()
        if lng or blocks:
            kib = sum(st(n).blocks // 2 for n in entries)
            ctx.print(f"total {_human(kib * 1024) if human else kib}")
        show([(normalize(path, n), n) for n in entries], deref)
        if a.has("R"):
            for n in entries:
                child = normalize(path, n)
                if n not in (".", "..") and fs.is_dir(child, deref):
                    list_dir(child, label.rstrip("/") + "/" + n)

    for d in dirs:
        list_dir(ctx.resolve(d), d)
    return code


def _show_nonprinting(line: str) -> str:
    return "".join(c if c == "\t" or 32 <= ord(c) < 127 or ord(c) > 159 else "^?" if ord(c) == 127
                   else "^" + chr(ord(c) + 64) if ord(c) < 32 else "M-^" + chr(ord(c) - 64) for c in line)


def _cat(ctx):
    a = Args.parse(ctx.args, COMMANDS["cat"])
    ends, tabs, nonprint = a.has("A") or a.has("E") or a.has("e"), a.has("A") or a.has("T"), a.has("A") or a.has("v") or a.has("e")
    code = 0
    line_no = 0

    def fail():
        nonlocal code
        code = 1

    plain = not (a.has("n") or a.has("b") or a.has("s") or ends or tabs or nonprint)
    prev_blank = False
    for _, text in inputs(ctx, a.positional, fail):
        if plain:
            ctx.stdout.write(text)
            continue
        out = []
        for line in split_lines(text):
            if a.has("s") and line == "" and prev_blank:
                continue
            prev_blank = line == ""
            if nonprint:
                line = _show_nonprinting(line)
            if tabs:
                line = line.replace("\t", "^I")
            if a.has("b") and line == "" or not (a.has("n") or a.has("b")):
                prefix = ""
            else:
                line_no += 1
                prefix = f"{line_no:6}\t"
            out.append(prefix + line + ("$" if ends else "") + "\n")
        if out and not text.endswith("\n"):  # an unterminated last line gets neither a newline nor a $
            out[-1] = out[-1][:-2] if ends else out[-1][:-1]
        ctx.stdout.write("".join(out))
    return code


def _mkdir(ctx):
    a = Args.parse(ctx.args, COMMANDS["mkdir"])
    if not a.positional:
        raise UsageException(spec_message("mkdir", "missingOperand"))
    code = 0
    for p in a.positional:
        try:
            ctx.fs.mkdir(ctx.resolve(p), a.has("p"))
        except ShellError as e:
            ctx.error("mkdir: " + spec_message("mkdir", "cannotCreateDirectory", file=p, reason=str(e).rsplit(": ", 1)[-1]))
            code = 1
    return code


def _touch(ctx):
    # shortcut: one modification time per file; -a and -m both set it
    a = Args.parse(ctx.args, COMMANDS["touch"])
    if not a.positional:
        raise UsageException(spec_message("touch", "missingFileOperand"))
    when = None
    if (reference := a.value("r")) is not None:
        ref = ctx.resolve(reference)
        if not ctx.fs.exists(ref):
            raise ShellError(spec_message("touch", "failedToGetAttributes", file=reference, reason=ctx.fs.why_missing(ref)))
        when = ctx.fs.stat(ref).modified
    if (d := a.value("d")) is not None:
        try:
            when = parse_date(d, (when or utc_now()).replace(tzinfo=timezone.utc)).replace(tzinfo=None)
        except ShellError:
            raise ShellError(spec_message("touch", "invalidDateFormat", value=d)) from None
    if (t := a.value("t")) is not None:
        when = touch_stamp(t, utc_now())
        if when is None:
            raise ShellError(spec_message("touch", "invalidDateFormat", value=t))
    code = 0
    for p in a.positional:
        path = ctx.resolve(p)
        if a.has("c") and not ctx.fs.exists(path):
            continue
        try:
            if a.has("h") and ctx.fs.is_symlink(path):
                ctx.fs.set_modified_time(path, when or utc_now(), follow=False)
            elif when is None:
                ctx.fs.touch(path)
            else:  # an explicit time needs ownership, not write permission
                if not ctx.fs.exists(path):
                    ctx.fs.touch(path)
                elif not ctx.fs.can_write(path) and ctx.fs.stat(path).owner != ctx.fs.run_as:
                    raise ShellError(f"{path}: {ERRNO['EACCES']}")  # touch opens someone else's file for writing first
                ctx.fs.set_modified_time(path, when)
        except ShellError as e:
            ctx.error("touch: " + spec_message("touch", "cannotTouch", file=p, reason=typed(str(e), p, path)[1]))
            code = 1
    return code


def _rm(ctx):
    a = Args.parse(ctx.args, COMMANDS["rm"])
    recursive = a.has("r") or a.has("R")
    if not a.positional and not a.has("f"):
        raise UsageException(spec_message("rm", "missingOperand"))
    code = 0
    for p in a.positional:
        path = ctx.resolve(p)
        if not ctx.fs.exists(path, follow=False):
            why = ctx.fs.why_missing(path, follow=False)
            if not a.has("f") or why != "No such file or directory":
                ctx.error("rm: " + spec_message("rm", "cannotRemove", file=p, reason=why))
                code = 1
            continue
        if ctx.fs.is_dir(path, follow=p.endswith("/")) and not recursive and not a.has("d"):
            ctx.error("rm: " + spec_message("rm", "cannotRemove", file=p, reason="Is a directory"))
            code = 1
            continue
        if p.endswith("/") and ctx.fs.is_symlink(path) and not ctx.fs.is_dir(path):  # "link/" names what it leads to, which is no directory
            if not a.has("f"):
                ctx.error("rm: " + spec_message("rm", "cannotRemove", file=p, reason="Not a directory"))
                code = 1
            continue
        if recursive and p.endswith("/") and ctx.fs.is_symlink(path) and ctx.fs.is_dir(path):
            # like GNU rm: "link/" is the directory it leads to, which is emptied; the link itself is no directory to remove
            for n in ctx.fs.listdir(path):
                try:
                    ctx.fs.delete(path + "/" + n, True)
                except ShellError as e:
                    shown, why = typed(str(e), p.rstrip("/") + "/" + n, path + "/" + n)
                    ctx.error("rm: " + spec_message("rm", "cannotRemove", file=shown, reason=why))
            if not a.has("f"):
                ctx.error("rm: " + spec_message("rm", "cannotRemove", file=p, reason="Not a directory"))
                code = 1
            continue
        try:
            ctx.fs.delete(path, recursive)
        except ShellError as e:
            shown, why = typed(str(e), p, path)
            ctx.error("rm: " + spec_message("rm", "cannotRemove", file=shown, reason=why))
            code = 1
    return code


def _rmdir(ctx):
    a = Args.parse(ctx.args, COMMANDS["rmdir"])
    if not a.positional:
        raise UsageException(spec_message("rmdir", "missingOperand"))
    code = 0
    for arg in a.positional:
        if arg.endswith("/") and ctx.fs.is_symlink(ctx.resolve(arg)):
            ctx.error("rmdir: " + spec_message("rmdir", "failedToRemove", file=arg, reason="Symbolic link not followed"))
            code = 1
            continue
        p = arg.rstrip("/")
        while p:  # -p: a/b/c, then a/b, then a
            path = ctx.resolve(p)
            why = None if ctx.fs.is_dir(path, follow=False) else "Not a directory" if ctx.fs.exists(path, follow=False) else ctx.fs.why_missing(path)
            if why is None:
                try:
                    ctx.fs.delete(path)
                    if a.has("v"):
                        ctx.print(f"rmdir: removing directory, {quote_always(p)}")
                except ShellError as e:
                    why = typed(str(e), p, path)[1]
            if why is not None:
                ctx.error("rmdir: " + spec_message("rmdir", "failedToRemove", file=p, reason=why))
                code = 1
                break
            i = p.rfind("/")
            p = p[:i].rstrip("/") if a.has("p") and i > 0 else ""
    return code


def _copy_into_itself(ctx, src: str, to: str, created: set[str]) -> bool:
    """cp -r d d/x copies d into d/x, entry by entry, until it meets a directory this copy created; there it stops.
    Returns False once stopped. shortcut: entries go in name order, where GNU goes in the directory's own order."""
    if not ctx.fs.exists(to):
        ctx.fs.mkdir(to, False)
        created.add(to)
    for name in ctx.fs.listdir(src):
        child = normalize(src, name)
        if child in created:
            return False
        if not ctx.fs.is_dir(child):
            ctx.fs.copy(child, normalize(to, name))
        elif not _copy_into_itself(ctx, child, normalize(to, name), created):
            return False
    return True


def _backup_name(fs, target: str, mode: str, suffix: str) -> str:
    """FILE~ (simple), FILE.~N~ one past the highest (numbered), or numbered only when FILE already has numbered backups (existing).
    shortcut: files inside a directory that cp -r merges into are overwritten without a backup"""
    name = get_name(target)
    numbers = 0
    for n in fs.listdir(get_parent(target)):
        middle = n[len(name) + 2:-1]
        if n.startswith(name + ".~") and n.endswith("~") and len(n) > len(name) + 2 and re.fullmatch(r"\d+", middle) and int(middle) > 0:
            numbers = max(numbers, int(middle))
    return target + suffix if mode == "simple" or (mode == "existing" and numbers == 0) else f"{target}.~{numbers + 1}~"


def _copy_or_move(ctx):
    move = ctx.name == "mv"
    # cp --backup[=CONTROL] takes an optional value, so it is read here, before the shared parser
    control = None
    args = []
    for x in ctx.args:
        if move or not (x == "--backup" or x.startswith("--backup=")):
            args.append(x)
        else:
            control = x[9:]
    a = Args.parse(args, COMMANDS[ctx.name])
    if a.has("b") and control is None:
        control = ""
    if control == "":
        control = ctx.shell.env.get("VERSION_CONTROL", "existing")
    backup = {None: None, "none": None, "off": None, "simple": "simple", "never": "simple", "existing": "existing", "nil": "existing",
              "numbered": "numbered", "t": "numbered"}.get(control, "invalid")
    if backup == "invalid":
        raise UsageException(spec_message("cp", "invalidBackupType", value=control))
    suffix = a.value("S") if a.value("S") is not None else ctx.shell.env.get("SIMPLE_BACKUP_SUFFIX", "~")
    # cp follows a symbolic link it is given unless copying recursively (-r, -a) or told not to; -L follows them all
    follow_top = not move and not (a.has("P") or a.has("d") or a.has("a")) and (a.has("L") or a.has("H") or not (a.has("r") or a.has("R")))
    if len(a.positional) < 2:
        raise UsageException(spec_message(ctx.name, "missingFileOperand") if not a.positional else spec_message(ctx.name, "missingDestination", file=a.positional[0]))
    *sources, dest_arg = a.positional
    dest = ctx.resolve(dest_arg)
    dest_is_dir = ctx.fs.is_dir(dest)
    if (len(sources) > 1 or dest_arg.endswith("/") and not ctx.fs.is_dir(ctx.resolve(sources[0]))) and not dest_is_dir:
        if len(sources) == 1 and not ctx.fs.exists(ctx.resolve(sources[0])):
            raise ShellError(spec_message(ctx.name, "cannotStat", file=sources[0], errno="No such file or directory"))
        if len(sources) > 1:
            raise ShellError(spec_message(ctx.name, "targetFailed", file=dest_arg, errno="Not a directory" if ctx.fs.exists(dest) else "No such file or directory"))
        raise ShellError(spec_message("mv", "cannotMove", source=sources[0], dest=dest_arg, errno="Not a directory") if move
                         else spec_message("cp", "cannotCreate", kind="regular file", file=dest_arg, errno="Not a directory"))
    code = 0
    for s in sources:
        src = ctx.resolve(s)
        if not ctx.fs.exists(src, follow_top):
            ctx.error(f"{ctx.name}: " + spec_message(ctx.name, "cannotStat", file=s, errno=ctx.fs.why_missing(src, follow_top)))
            code = 1
            continue
        if not move and ctx.fs.is_dir(src, follow_top) and not (a.has("r") or a.has("R") or a.has("a")):
            ctx.error("cp: " + spec_message("cp", "omittingDirectory", file=s))
            code = 1
            continue
        target = normalize(dest, get_name(src)) if dest_is_dir else dest
        if target != src and not move and follow_top and ctx.fs.same_file(src, target):  # through a link or another hard link; the plain case is a clash below
            ctx.error("cp: " + spec_message("cp", "sameFile", source=s, dest=dest_arg.rstrip('/') + '/' + get_name(src) if dest_is_dir else dest_arg))
            code = 1
            continue
        if not move and ctx.fs.is_file(src, follow_top) and ctx.fs.is_symlink(target) and not ctx.fs.exists(target):
            ctx.error("cp: " + spec_message("cp", "danglingSymlink", file=dest_arg.rstrip('/') + '/' + get_name(src) if dest_is_dir else dest_arg))
            code = 1
            continue
        if target == "/dev/null" and not move:
            continue  # copying to /dev/null discards the data
        shown = dest_arg.rstrip("/") + "/" + get_name(src) if dest_is_dir else dest_arg
        src_dir = ctx.fs.is_dir(src, follow_top)
        # mv onto another name of the same file, or onto the file a link names, loses it; mv refuses as GNU mv does
        same = target == src or move and (ctx.fs.same_file(src, target, follow=False)
                                          or ctx.fs.is_symlink(src) and not ctx.fs.is_symlink(target) and ctx.fs.same_file(src, target))
        if same:
            clash = "sameFile"
        elif src_dir and target.startswith(src + "/"):
            clash = "intoItself"
        elif not ctx.fs.exists(target):
            clash = None
        elif src_dir and not ctx.fs.is_dir(target):
            clash = "overwriteNonDirectory"
        elif not src_dir and ctx.fs.is_dir(target):
            clash = "overwriteDirectory"
        elif move and src_dir and ctx.fs.listdir(target):
            clash = "notEmpty"
        else:
            clash = None
        if clash:
            ctx.error(f"{ctx.name}: " + spec_message(ctx.name, clash, source=s, dest=shown))
            code = 1
            # cp creates the copy and fills it until it reaches the copy itself
            if not move and clash == "intoItself":
                _copy_into_itself(ctx, src, target, set())
            continue
        if backup is not None and ctx.fs.is_file(target, follow=False):
            ctx.fs.move(target, _backup_name(ctx.fs, target, backup, suffix))
        if move and src_dir and ctx.fs.is_dir(target):
            ctx.fs.delete(target)  # an empty directory is replaced
        nonlocal_code = [code]

        def denied(p, is_dir, s=s, src=src):
            shown = typed(p + ": ", s, src)[0]
            ctx.error("cp: " + spec_message("cp", "cannotAccess" if is_dir else "cannotOpen", file=shown, errno="Permission denied"))
            nonlocal_code[0] = 1

        def broken(p, why, s=s, src=src):
            shown = typed(p + ": ", s, src)[0]
            ctx.error("cp: " + (spec_message("cp", "cyclicLink", file=shown) if why == "cyclic" else spec_message("cp", "cannotStat", file=shown, errno=why)))
            nonlocal_code[0] = 1

        try:
            ctx.fs.move(src, target) if move else ctx.fs.copy(src, target, denied, follow_top, a.has("L"), broken, ctx.check_time,
                                                                   preserve=a.has("p") or a.has("a"))
        except ShellError as e:
            shown = dest_arg.rstrip("/") + "/" + get_name(src) if dest_is_dir else dest_arg
            if " into itself, " in str(e) or " to a subdirectory of itself, " in str(e):
                ctx.error(f"{ctx.name}: " + spec_message(ctx.name, "intoItselfPlain", source=s, dest=shown))
                code = 1
                continue
            if str(e).endswith(NO_SPACE):
                ctx.error(f"{ctx.name}: " + spec_message(ctx.name, "errorWriting", file=shown, errno=NO_SPACE))
                code = 1
                continue
            if not (str(e).endswith("Permission denied") or str(e).endswith("Operation not permitted")):
                raise
            why = typed(str(e), s, src)[1]
            ctx.error("mv: " + spec_message("mv", "cannotMove", source=s, dest=shown, errno=why) if move
                      else "cp: " + spec_message("cp", "cannotCreate", kind="directory" if ctx.fs.is_dir(src) else "regular file", file=shown, errno=why))
            code = 1
            continue
        code = nonlocal_code[0]
        if a.has("v"):
            ctx.print(f"{'renamed ' if move else ''}'{s}' -> '{shown}'")
    return code


def _ln(ctx):
    a = Args.parse(ctx.args, COMMANDS["ln"])
    fs, sym, ops, t_opt = ctx.fs, a.has("s"), list(a.positional), a.value("t")
    into = t_opt
    if t_opt is None and not a.has("T") and (len(ops) > 2 or len(ops) == 2 and fs.is_dir(ctx.resolve(ops[-1]), not a.has("n"))):
        into = ops[-1]
    if not ops or (len(ops) == 1 and a.has("T") and into is None):
        raise UsageException(spec_message("ln", "missingFileOperand") if not ops else spec_message("ln", "missingDestination", file=ops[0]))
    if into is not None and t_opt is None:
        ops = ops[:-1]
    if into is not None and not fs.is_dir(ctx.resolve(into)):
        exists = fs.exists(ctx.resolve(into))
        raise ShellError(spec_message("ln", "failedToAccess", file=into, errno=fs.why_missing(ctx.resolve(into))) if t_opt is not None and not exists
                         else spec_message("ln", "targetNotDirectory", file=into) if exists else spec_message("ln", "targetFailed", file=into, errno="No such file or directory"))
    # (what the link points to, the link's name): `ln f` links into ".", `ln f d` into d when it is a directory
    if into is not None or len(ops) == 1:
        pairs = [(t, (into.rstrip("/") + "/" if into is not None else "") + get_name(t)) for t in ops]
    else:
        pairs = [(ops[0], ops[1])]
    code = 0
    for target, name in pairs:
        shown = "./" + name if into is None and len(ops) == 1 else name
        path, src = ctx.resolve(name), ctx.resolve(target)

        def fail(why, arrow=True):
            tail = "" if not arrow else (" -> ''" if not target else "") if sym else " => " + quote_always(target)
            return "ln: " + spec_message("ln", "createFailed", kind="symbolic" if sym else "hard", file=shown, arrow=tail, errno=why)

        try:
            if not sym and not fs.exists(src, a.has("L")):
                ctx.error("ln: " + spec_message("ln", "failedToAccess", file=target, errno=fs.why_missing(src, a.has("L"))))
                code = 1
                continue
            if not sym and fs.is_dir(src):
                ctx.error("ln: " + spec_message("ln", "hardLinkDirectory", file=target))
                code = 1
                continue
            if fs.exists(path, follow=False):
                if not a.has("f"):
                    ctx.error(fail("File exists", arrow=False))
                    code = 1
                    continue
                if fs.is_dir(path, follow=False):
                    ctx.error("ln: " + spec_message("ln", "overwriteDirectory", file=name))
                    code = 1
                    continue
                if fs.same_file(path, normalize(get_parent(path), target) if sym else src):
                    ctx.error("ln: " + spec_message("ln", "sameFile", source=target, dest=name))
                    code = 1
                    continue
                fs.delete(path)
            if not sym:
                fs.create_hard_link(src, path, a.has("L"))
            else:
                stored = target
                if a.has("r") and target:
                    # -r: from the link's real directory to the target's real path
                    to = (fs.real_path(src, existing=False)[0] or src).split("/")[1:]
                    d = get_parent(path)
                    frm = (fs.real_path(d)[0] or d).split("/")[1:]
                    to, frm = [x for x in to if x], [x for x in frm if x]
                    common = 0
                    while common < min(len(to), len(frm)) and to[common] == frm[common]:
                        common += 1
                    stored = "/".join([".."] * (len(frm) - common) + to[common:]) or "."
                fs.create_symlink(path, stored)
            if a.has("v"):
                ctx.print(f"{quote_always(shown)} {'->' if sym else '=>'} {quote_always(target)}")
        except ShellError as e:
            why = str(e).rsplit(": ", 1)[-1]
            ctx.error(fail(why, arrow=why != "File exists"))
            code = 1
    return code


class _Find:
    """find with real expressions: ( ) ! -not -a -o, tests, and actions (-print -print0 -exec -delete)."""

    def __init__(self, ctx):
        self.ctx = ctx
        self.args: list[str] = []
        self.p = 0
        self.max_depth, self.min_depth = 10**9, 0
        self.has_action = False
        self.denied = False
        self.deletes: list[str] = []
        self.above: list[tuple[str, str]] = []  # (real path, display) of the directories being walked, so -L stops at a link back up
        self.batches: list[tuple[list[str], list[str]]] = []
        self.prune = False

    def is_(self, s: str) -> bool:
        return self.p < len(self.args) and self.args[self.p] == s

    async def run(self) -> int:
        ctx = self.ctx
        args = list(ctx.args)
        # -version (or --version) anywhere in the expression: print the version, run nothing
        if "-version" in args or "--version" in args:
            ctx.print(COMMANDS["find"].version)
            return 0
        # -P (the default) never follows symbolic links, -H only those named as starting points, -L all of them
        options = []
        while args and args[0] in ("-P", "-H", "-L"):
            options.append(args.pop(0))
        mode = options[-1][1] if options else "P"
        fs = ctx.fs
        starts = []
        while args and not args[0].startswith("-") and args[0] not in ("!", "("):
            starts.append(args.pop(0))
        self.args = args
        starts = starts or ["."]

        async def always(e):
            return True

        expr = self.parse_or() if args else always
        if self.p < len(args):
            raise ShellError(spec_message("find", "unexpected", token=args[self.p]))

        async def walk(path, display, depth, start, follow):
            ctx.check_time()  # -L can make the walk far bigger than the tree: the call's time limit
            self.prune = False
            is_dir = fs.is_dir(path, follow)
            real = (fs.real_path(path)[0] or path) if is_dir and follow else path
            if is_dir and follow and (loop := next((d for r, d in self.above if r == real), None)) is not None:
                ctx.error("find: " + spec_message("find", "loop", dir=display, loop=loop))
                self.denied = True
                return
            if depth >= self.min_depth and await expr((path, get_name(path), display, depth, start, follow)) and not self.has_action:
                ctx.print(display)
            if self.prune or depth >= self.max_depth or not is_dir:
                return
            try:
                names = fs.listdir(path)
            except ShellError:
                ctx.error("find: " + spec_message("find", "cannotRead", file=display, errno="Permission denied"))
                self.denied = True
                return
            self.above.append((real, display))
            for name in names:
                await walk(normalize(path, name), display + name if display.endswith("/") else display + "/" + name, depth + 1, start, mode == "L")
            self.above.pop()

        code = 0
        try:
            for s in starts:
                path = ctx.resolve(s)
                follow = mode != "P" or s.endswith("/")
                if not fs.exists(path, follow) and not fs.exists(path, follow=False):
                    ctx.error("find: " + spec_message("find", "cannotRead", file=s, errno=fs.why_missing(path, follow)))
                    code = 1
                    continue
                await walk(path, s, 0, s, follow)
        except _FindQuit:
            pass
        for cmd, paths in self.batches:
            if paths and await ctx.invoke([y for x in cmd for y in (paths if x == "{}" else [x])]) != 0:
                code = 1
        if self.denied:
            code = 1
        for path in sorted(self.deletes, key=len, reverse=True):
            if fs.exists(path, follow=False):
                fs.delete(path, recursive=True)
        return code

    def parse_or(self):
        left = self.parse_and()
        while self.is_("-o") or self.is_("-or"):
            self.p += 1
            if self.p >= len(self.args):
                raise ShellError(spec_message("find", "expectedExpression", token=self.args[self.p - 1]))
            l, r = left, self.parse_and()

            async def either(e, l=l, r=r):
                return await l(e) or await r(e)
            left = either
        return left

    def parse_and(self):
        left = self.parse_not()
        while self.p < len(self.args) and not (self.is_("-o") or self.is_("-or") or self.is_(")")):
            if self.is_("-a") or self.is_("-and"):
                self.p += 1
                if self.p >= len(self.args):
                    raise ShellError(spec_message("find", "expectedExpression", token=self.args[self.p - 1]))
            l, r = left, self.parse_not()

            async def both(e, l=l, r=r):
                return await l(e) and await r(e)
            left = both
        return left

    def parse_not(self):
        if self.is_("!") or self.is_("-not"):
            self.p += 1
            inner = self.parse_not()

            async def negate(e):
                return not await inner(e)
            return negate
        if self.is_("("):
            self.p += 1
            inner = self.parse_or()
            if not self.is_(")"):
                raise ShellError(spec_message("find", "expectedParen"))
            self.p += 1
            return inner
        return self.primary()

    def primary(self):
        ctx, fs = self.ctx, self.ctx.fs
        tok = self.args[self.p]
        self.p += 1

        def arg():
            if self.p >= len(self.args):
                raise ShellError(spec_message("find", "missingArgument", predicate=tok))
            self.p += 1
            return self.args[self.p - 1]

        def pred(fn):
            async def run(e):
                return fn(e[0], e[1], e[2])
            return run

        def pred_e(fn):
            async def run(e):
                return fn(e)
            return run

        def stat(e):  # what a link leads to when following, unless it leads nowhere (then the link, as find -L does)
            return _find_stat(fs, e[0], e[5])

        if tok in ("-name", "-iname"):
            rx = glob_to_regex(arg(), ignore_case=tok == "-iname")
            return pred(lambda p, n, d: bool(rx.match(n)))
        if tok in ("-path", "-ipath", "-wholename"):
            rx = glob_to_regex(arg(), ignore_case=tok == "-ipath", slashes=True)
            return pred(lambda p, n, d: bool(rx.match(d)))
        if tok == "-regex":
            rx = re.compile("(?:" + arg() + r")\Z")
            return pred(lambda p, n, d: bool(rx.match(d)))
        if tok in ("-type", "-xtype"):
            t = arg()
            if t not in ("f", "d", "l"):
                raise ShellError(spec_message("find", "unknownType", predicate=tok, value=t))
            # -xtype: the other view of a link (what it leads to under -P, the link itself under -L)
            return pred_e(lambda e: _find_type(stat(e if tok == "-type" else e[:5] + (not e[5],))) == t)
        if tok in ("-maxdepth", "-mindepth"):
            v = int(arg())
            if tok == "-maxdepth":
                self.max_depth = v
            else:
                self.min_depth = v
            return pred(lambda p, n, d: True)
        if tok in ("-mmin", "-mtime"):
            spec = arg()
            if not re.fullmatch(r"\d+\.?\d*|\.\d+", spec[1:] if spec[:1] in ("+", "-") else spec):
                raise ShellError(spec_message("find", "invalidArgument", value=spec, predicate=tok))
            unit, num = (60 if tok == "-mmin" else 86400), float(spec.lstrip("+-"))

            def age_ok(e):
                age = (utc_now() - stat(e).modified).total_seconds() / unit
                return age < num if spec[0] == "-" else age > num if spec[0] == "+" else int(age) == num
            return pred_e(age_ok)
        if tok == "-newer":
            reference = arg()
            if not fs.exists(ctx.resolve(reference)):
                raise ShellError(spec_message("find", "cannotRead", file=reference, errno=fs.why_missing(ctx.resolve(reference))))
            ref = fs.stat(ctx.resolve(reference)).modified
            return pred_e(lambda e: stat(e).modified > ref)
        if tok == "-size":
            spec = arg()
            m = re.fullmatch(r"([+-]?)(\d+)([cwbkMG]?)", spec)
            if not m and (bad := re.match(r"[+-]?\d+(\D)", spec)):
                raise ShellError(spec_message("find", "invalidSizeType", unit=bad.group(1)))
            if not m:
                raise ShellError(spec_message("find", "invalidArgument", value=spec, predicate="-size"))
            unit = {"c": 1, "w": 2, "k": 1024, "M": 1 << 20, "G": 1 << 30}.get(m.group(3), 512)
            num = int(m.group(2))

            def size_ok(e):
                st = stat(e)
                if st.is_dir:
                    return False
                size = (st.size + unit - 1) // unit
                return size > num if m.group(1) == "+" else size < num if m.group(1) == "-" else size == num
            return pred_e(size_ok)
        if tok == "-links":
            spec = arg()
            if not re.fullmatch(r"[+-]?\d+", spec):
                raise ShellError(spec_message("find", "invalidArgument", value=spec, predicate="-links"))
            num = int(spec.lstrip("+-"))
            return pred_e(lambda e: (lambda n: n > num if spec[0] == "+" else n < num if spec[0] == "-" else n == num)(stat(e).links))
        if tok == "-empty":
            return pred_e(lambda e: not fs.listdir(e[0]) if stat(e).is_dir else not stat(e).is_symlink and stat(e).size == 0)
        if tok in ("-true", "-false"):
            return pred(lambda p, n, d: tok == "-true")
        if tok in ("-print", "-print0"):
            self.has_action = True
            end = "\n" if tok == "-print" else "\0"
            return pred(lambda p, n, d: ctx.stdout.write(d + end) is not None)
        if tok == "-printf":
            self.has_action = True
            fmt = arg()

            async def run_printf(e):
                ctx.stdout.write(_find_printf(fmt, e, fs))
                return True
            return run_printf
        if tok == "-quit":
            async def quit_(e):
                raise _FindQuit()
            return quit_
        if tok == "-prune":
            def prune(p, n, d):
                self.prune = True
                return True
            return pred(prune)
        if tok == "-delete":
            self.has_action = True
            return pred(lambda p, n, d: self.deletes.append(p) is None)
        if tok in ("-exec", "-execdir", "-ok"):
            self.has_action = True
            cmd: list[str] = []
            while self.p < len(self.args) and self.args[self.p] != ";" and not (self.args[self.p] == "+" and cmd and cmd[-1] == "{}"):
                cmd.append(self.args[self.p])
                self.p += 1
            if self.p >= len(self.args):
                raise ShellError(spec_message("find", "missingArgument", predicate=tok))
            if not cmd:
                raise ShellError(spec_message("find", "invalidArgument", value=self.args[self.p], predicate=tok))
            terminator = self.args[self.p]
            self.p += 1
            if terminator == "+":
                batch: list[str] = []
                self.batches.append((cmd, batch))
                return pred(lambda p, n, d: batch.append(d) is None)

            async def run_exec(e):
                return await ctx.invoke([x.replace("{}", e[2]) for x in cmd]) == 0
            return run_exec
        raise ShellError(spec_message("find", "unknownPredicate", predicate=tok))


class _FindQuit(Exception):
    pass


_FIND_TIME = {"Y": "%Y", "m": "%m", "d": "%d", "H": "%H", "M": "%M", "S": "%S.0000000000", "T": "%H:%M:%S.0000000000",
              "F": "%Y-%m-%d", "+": "%Y-%m-%d+%H:%M:%S.0000000000", "a": "%a", "b": "%b", "y": "%y", "D": "%m/%d/%y", "R": "%H:%M"}


def _find_stat(fs, path: str, follow: bool):
    return fs.stat(path, follow and fs.exists(path))


def _find_type(st) -> str:
    return "l" if st.is_symlink else "d" if st.is_dir else "f"


def _find_printf(fmt: str, e, fs) -> str:
    """find -printf: %p %f %h %P %s %d %y %m %M %u %g %k %t %T@ %Tk ... with optional width, plus \\n \\t \\0 escapes."""
    path, name, display, depth, start, follow = e
    st = _find_stat(fs, path, follow)
    out: list[str] = []
    i, n = 0, len(fmt)
    while i < n:
        c = fmt[i]
        if c == "\\" and i + 1 < n:
            out.append({"n": "\n", "t": "\t", "0": "\0", "\\": "\\", "r": "\r"}.get(fmt[i + 1], "\\" + fmt[i + 1]))
            i += 2
            continue
        if c != "%" or i + 1 >= n:
            out.append(c)
            i += 1
            continue
        m = re.match(r"%([-+ #0]*)(\d*)(?:\.(\d+))?", fmt[i:])
        j = i + m.end()
        if j >= n:
            out.append(fmt[i:])
            break
        d = fmt[j]
        j += 1
        if d == "%":
            out.append("%")
            i = j
            continue
        if d in "TAC":
            k = fmt[j] if j < n else ""
            j += 1
            value = (f"{st.modified.replace(tzinfo=timezone.utc).timestamp():.10f}" if k == "@" else st.modified.strftime(_FIND_TIME[k]) if k in _FIND_TIME
                     else "%" + d + k)
        elif d == "t":
            value = st.modified.strftime("%a %b %d %H:%M:%S.0000000000 %Y")
        else:
            base = display.rstrip("/").rsplit("/", 1)[-1] or "/"
            values = {
                "p": display, "f": base if depth == 0 else name,
                "h": (display.rsplit("/", 1)[0] or "/") if "/" in display.rstrip("/") else ".",
                "P": display[len(start):].lstrip("/") if depth else "", "H": start, "s": str(st.size), "d": str(depth),
                "y": _find_type(st), "Y": _find_type(_find_stat(fs, path, True)), "l": st.link_target or "", "m": format(st.mode & 0o7777, "o"),
                "M": format_mode(st.is_dir, st.mode, st.is_symlink), "u": st.owner, "g": st.owner, "n": str(st.links),
                "k": str((st.size + 4095) // 4096 * 4), "b": str((st.size + 4095) // 4096 * 8),
            }
            value = values.get(d, "%" + d)
        width = int(m.group(2)) if m.group(2) else 0
        out.append(value.ljust(width) if "-" in m.group(1) else value.rjust(width))
        i = j
    return "".join(out)


async def _find(ctx):
    return await _Find(ctx).run()


def _tree(ctx):
    a = Args.parse(ctx.args, COMMANDS["tree"])
    max_depth = int(a.value("L")) if a.value("L") else 10**9
    root = a.positional[0] if a.positional else "."
    path = ctx.resolve(root)
    if not ctx.fs.is_dir(path):
        raise ShellError(spec_message("tree", "errorOpeningDir", dir=root))
    counts = {"dirs": 0, "files": 0}
    out = [root + "\n"]

    def walk(d, prefix, depth):
        entries = [n for n in ctx.fs.listdir(d) if (a.has("a") or not n.startswith("."))
                   and (not a.has("d") or ctx.fs.is_dir(normalize(d, n), follow=False))]
        for i, name in enumerate(entries):
            last = i == len(entries) - 1
            child = normalize(d, name)
            out.append(prefix + ("└── " if last else "├── ") + name + (" -> " + ctx.fs.read_link(child) if ctx.fs.is_symlink(child) else "") + "\n")
            if ctx.fs.is_dir(child, follow=False):
                counts["dirs"] += 1
                if depth < max_depth:
                    walk(child, prefix + ("    " if last else "│   "), depth + 1)
            else:
                counts["files"] += 1

    walk(path, "", 1)
    dirs, files = counts["dirs"], counts["files"]
    out.append(f"\n{dirs} director{'y' if dirs == 1 else 'ies'}")
    out.append("\n" if a.has("d") else f", {files} file{'' if files == 1 else 's'}\n")
    ctx.stdout.write("".join(out))


def register(shell) -> None:
    r = shell.register_command
    r("ls", _ls)
    r("cat", _cat)
    r("mkdir", _mkdir)
    r("touch", _touch)
    r("rm", _rm)
    r("rmdir", _rmdir)
    r("cp", _copy_or_move)
    r("mv", _copy_or_move)
    r("ln", _ln)
    r("find", _find)
    r("tree", _tree)
