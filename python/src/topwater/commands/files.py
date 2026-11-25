"""Filesystem commands: ls cd-free file ops, find, tree."""
from __future__ import annotations

import math
import re
from datetime import datetime, timedelta, timezone

from ..shell import glob_to_regex
from ..vfs import NO_SPACE, ShellError, UsageException, format_mode, get_name, normalize, utc_now
from . import Args, inputs, parse_date, quote_always, quote_l, split_lines


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


def _ls_date(t: datetime, now: datetime) -> str:
    """ls -l's date: "Nov 25 16:52" within the last six months, else "Jan  2  2020" (also for future times)."""
    if timedelta(0) <= now - t < timedelta(seconds=15778476):
        return f"{t:%b} {t.day:2} {t:%H:%M}"
    return f"{t:%b} {t.day:2}  {t:%Y}"


def _long_entries(ctx, entries, human: bool, width_from=()):
    """ls -l rows, each column as wide as its widest entry (owners left-aligned, numbers right-aligned).
    width_from: more paths that count toward the widths (ls sizes the columns for its file operands over all operands)."""
    now = utc_now()

    def size(st):
        return _human(st.size) if human else str(st.size)

    rows = [(ctx.fs.stat(p), name) for p, name in entries]
    if not rows:
        return []
    every = [st for st, _ in rows] + [ctx.fs.stat(p) for p in width_from]
    lw, ow, sw = (max(len(f(st)) for st in every) for f in (lambda st: str(st.links), lambda st: st.owner, size))
    return [f"{format_mode(st.is_dir, st.mode)} {st.links:>{lw}} {st.owner:<{ow}} {st.owner:<{ow}} {size(st):>{sw}} {_ls_date(st.modified, now)} {name}"
            for st, name in rows]


def _ls(ctx):
    a = Args.parse(ctx.args, "alhA1dFrtRSZ", longs="all=a,almost-all=A,human-readable=h,directory=d,reverse=r,recursive=R")
    paths = a.positional or ["."]
    code = 0

    def show(entries, width_from=()):
        named = [(p, n + "/" if a.has("F") and ctx.fs.is_dir(p) and n not in (".", "..") else n) for p, n in entries]
        for line in _long_entries(ctx, named, a.has("h"), width_from) if a.has("l") else [n for _, n in named]:
            ctx.print(line)

    for p in paths:
        if not ctx.fs.exists(ctx.resolve(p)):
            ctx.error(f"ls: cannot access '{p}': {ctx.fs.why_missing(ctx.resolve(p))}")
            code = 2
    def ordered(ps):  # operands are listed like directory entries: byte order (C locale), or by -t / -S, reversed by -r
        ps = sorted(ps, key=lambda p: p.encode("utf-8", "surrogateescape"))
        if a.has("t"): ps = sorted(ps, key=lambda p: ctx.fs.stat(ctx.resolve(p)).modified, reverse=True)
        if a.has("S"): ps = sorted(ps, key=lambda p: ctx.fs.stat(ctx.resolve(p)).size, reverse=True)
        return ps[::-1] if a.has("r") else ps

    files = ordered([p for p in paths if ctx.fs.exists(ctx.resolve(p)) and (a.has("d") or not ctx.fs.is_dir(ctx.resolve(p)))])
    show([(ctx.resolve(f), f) for f in files], [ctx.resolve(p) for p in paths if p not in files and ctx.fs.exists(ctx.resolve(p))])
    dirs = ordered([p for p in paths if p not in files and ctx.fs.is_dir(ctx.resolve(p))])
    first = [not files]

    def list_dir(path, label):
        if len(paths) > 1 or a.has("R"):
            ctx.print(("" if first[0] else "\n") + label + ":")
        first[0] = False
        if not ctx.fs.can_read(path):
            ctx.error(f"ls: cannot open directory '{label}': Permission denied")
            nonlocal code
            code = 2
            return
        entries = [n for n in ctx.fs.listdir(path) if a.has("a") or a.has("A") or not n.startswith(".")]
        if a.has("t"):
            entries.sort(key=lambda n: ctx.fs.stat(normalize(path, n)).modified, reverse=True)
        if a.has("S"):
            entries.sort(key=lambda n: ctx.fs.stat(normalize(path, n)).size, reverse=True)
        if a.has("a"):
            entries = [".", ".."] + entries
        if a.has("r"):
            entries.reverse()
        if a.has("l"):
            kib = sum(ctx.fs.stat(normalize(path, n)).blocks // 2 for n in entries)
            ctx.print(f"total {_human(kib * 1024) if a.has('h') else kib}")
        show([(normalize(path, n), n) for n in entries])
        if a.has("R"):
            for n in entries:
                child = normalize(path, n)
                if n not in (".", "..") and ctx.fs.is_dir(child):
                    list_dir(child, label.rstrip("/") + "/" + n)

    for d in dirs:
        list_dir(ctx.resolve(d), d)
    return code


def _show_nonprinting(line: str) -> str:
    return "".join(c if c == "\t" or 32 <= ord(c) < 127 or ord(c) > 159 else "^?" if ord(c) == 127
                   else "^" + chr(ord(c) + 64) if ord(c) < 32 else "M-^" + chr(ord(c) - 64) for c in line)


def _cat(ctx):
    a = Args.parse(ctx.args, "nbsAETveTu", longs="number=n,number-nonblank=b,squeeze-blank=s,show-all=A,show-ends=E,"
                                                 "show-tabs=T,show-nonprinting=v")
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
    a = Args.parse(ctx.args, "pvZ", longs="parents=p,verbose=v")
    if not a.positional:
        raise UsageException("missing operand")
    code = 0
    for p in a.positional:
        try:
            ctx.fs.mkdir(ctx.resolve(p), a.has("p"))
        except ShellError as e:
            ctx.error(f"mkdir: cannot create directory {quote_l(p)}: {str(e).rsplit(': ', 1)[-1]}")
            code = 1
    return code


def _touch(ctx):
    # shortcut: one modification time per file; -a and -m both set it
    a = Args.parse(ctx.args, "acmhf", "dtr", "no-create=c,date=d,reference=r,time=f,no-dereference=h")
    if not a.positional:
        raise UsageException("missing file operand")
    when = None
    if (reference := a.value("r")) is not None:
        ref = ctx.resolve(reference)
        if not ctx.fs.exists(ref):
            raise ShellError(f"failed to get attributes of {quote_always(reference)}: {ctx.fs.why_missing(ref)}")
        when = ctx.fs.stat(ref).modified
    if (d := a.value("d")) is not None:
        try:
            when = parse_date(d, (when or utc_now()).replace(tzinfo=timezone.utc)).replace(tzinfo=None)
        except ShellError:
            raise ShellError(f"invalid date format {quote_l(d)}") from None
    if (t := a.value("t")) is not None:
        when = touch_stamp(t, utc_now())
        if when is None:
            raise ShellError(f"invalid date format {quote_l(t)}")
    code = 0
    for p in a.positional:
        path = ctx.resolve(p)
        if a.has("c") and not ctx.fs.exists(path):
            continue
        try:
            if when is None:
                ctx.fs.touch(path)
            else:  # an explicit time needs ownership, not write permission
                if not ctx.fs.exists(path):
                    ctx.fs.touch(path)
                ctx.fs.set_modified_time(path, when)
        except ShellError as e:
            ctx.error(f"touch: cannot touch {quote_always(p)}: {typed(str(e), p, path)[1]}")
            code = 1
    return code


def _rm(ctx):
    a = Args.parse(ctx.args, "rRfvd", longs="recursive=r,force=f")
    recursive = a.has("r") or a.has("R")
    if not a.positional and not a.has("f"):
        raise UsageException("missing operand")
    code = 0
    for p in a.positional:
        path = ctx.resolve(p)
        if not ctx.fs.exists(path):
            why = ctx.fs.why_missing(path)
            if not a.has("f") or why != "No such file or directory":
                ctx.error(f"rm: cannot remove '{p}': {why}")
                code = 1
            continue
        if ctx.fs.is_dir(path) and not recursive and not a.has("d"):
            ctx.error(f"rm: cannot remove '{p}': Is a directory")
            code = 1
            continue
        try:
            ctx.fs.delete(path, recursive)
        except ShellError as e:
            shown, why = typed(str(e), p, path)
            ctx.error(f"rm: cannot remove '{shown}': {why}")
            code = 1
    return code


def _rmdir(ctx):
    a = Args.parse(ctx.args, "pv", longs="parents=p,verbose=v")
    if not a.positional:
        raise UsageException("missing operand")
    code = 0
    for arg in a.positional:
        p = arg.rstrip("/")
        while p:  # -p: a/b/c, then a/b, then a
            path = ctx.resolve(p)
            why = None if ctx.fs.is_dir(path) else "Not a directory" if ctx.fs.exists(path) else ctx.fs.why_missing(path)
            if why is None:
                try:
                    ctx.fs.delete(path)
                    if a.has("v"):
                        ctx.print(f"rmdir: removing directory, {quote_always(p)}")
                except ShellError as e:
                    why = typed(str(e), p, path)[1]
            if why is not None:
                ctx.error(f"rmdir: failed to remove {quote_always(p)}: {why}")
                code = 1
                break
            i = p.rfind("/")
            p = p[:i].rstrip("/") if a.has("p") and i > 0 else ""
    return code


def _copy_or_move(ctx):
    move = ctx.name == "mv"
    a = Args.parse(ctx.args, "fvniZ" if move else "rRfvanpZ", longs="recursive=r,force=f")
    if len(a.positional) < 2:
        raise UsageException("missing file operand" if not a.positional else f"missing destination file operand after {quote_always(a.positional[0])}")
    *sources, dest_arg = a.positional
    dest = ctx.resolve(dest_arg)
    dest_is_dir = ctx.fs.is_dir(dest)
    if (len(sources) > 1 or dest_arg.endswith("/")) and not dest_is_dir:
        if len(sources) == 1 and not ctx.fs.exists(ctx.resolve(sources[0])):
            raise ShellError(f"cannot stat '{sources[0]}': No such file or directory")
        if len(sources) > 1:
            raise ShellError(f"target '{dest_arg}': No such file or directory" if not ctx.fs.exists(dest)
                             else f"target '{dest_arg}' is not a directory")
        raise ShellError(f"cannot move '{sources[0]}' to '{dest_arg}': Not a directory" if move
                         else f"cannot create regular file '{dest_arg}': Not a directory")
    code = 0
    for s in sources:
        src = ctx.resolve(s)
        if not ctx.fs.exists(src):
            ctx.error(f"{ctx.name}: cannot stat '{s}': {ctx.fs.why_missing(src)}")
            code = 1
            continue
        if not move and ctx.fs.is_dir(src) and not (a.has("r") or a.has("R") or a.has("a")):
            ctx.error(f"cp: -r not specified; omitting directory '{s}'")
            code = 1
            continue
        target = normalize(dest, get_name(src)) if dest_is_dir else dest
        if target == src:
            ctx.error(f"{ctx.name}: '{s}' and '{dest_arg}' are the same file")
            code = 1
            continue
        if target == "/dev/null" and not move:
            continue  # copying to /dev/null discards the data
        nonlocal_code = [code]

        def denied(p, is_dir, s=s, src=src):
            shown = typed(p + ": ", s, src)[0]
            ctx.error(f"cp: cannot access '{shown}': Permission denied" if is_dir else f"cp: cannot open '{shown}' for reading: Permission denied")
            nonlocal_code[0] = 1

        try:
            ctx.fs.move(src, target) if move else ctx.fs.copy(src, target, denied)
        except ShellError as e:
            shown = dest_arg.rstrip("/") + "/" + get_name(src) if dest_is_dir else dest_arg
            if str(e).endswith(NO_SPACE):
                ctx.error(f"{ctx.name}: error writing '{shown}': {NO_SPACE}")
                code = 1
                continue
            if not (str(e).endswith("Permission denied") or str(e).endswith("Operation not permitted")):
                raise
            why = typed(str(e), s, src)[1]
            ctx.error(f"mv: cannot move '{s}' to '{shown}': {why}" if move
                      else f"cp: cannot create {'directory' if ctx.fs.is_dir(src) else 'regular file'} '{shown}': {why}")
            code = 1
            continue
        code = nonlocal_code[0]
        if a.has("v"):
            shown = dest_arg.rstrip("/") + "/" + get_name(src) if dest_is_dir else dest_arg
            ctx.print(f"{'renamed ' if move else ''}'{s}' -> '{shown}'")
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
        self.batches: list[tuple[list[str], list[str]]] = []
        self.prune = False

    def is_(self, s: str) -> bool:
        return self.p < len(self.args) and self.args[self.p] == s

    async def run(self) -> int:
        ctx = self.ctx
        args = list(ctx.args)
        starts = []
        while args and not args[0].startswith("-") and args[0] not in ("!", "("):
            starts.append(args.pop(0))
        self.args = args
        starts = starts or ["."]

        async def always(e):
            return True

        expr = self.parse_or() if args else always
        if self.p < len(args):
            raise ShellError(f"unexpected `{args[self.p]}'")

        async def walk(path, display, depth, start):
            self.prune = False
            if depth >= self.min_depth and await expr((path, get_name(path), display, depth, start)) and not self.has_action:
                ctx.print(display)
            if self.prune or depth >= self.max_depth or not ctx.fs.is_dir(path):
                return
            try:
                names = ctx.fs.listdir(path)
            except ShellError:
                ctx.error(f"find: {quote_l(display)}: Permission denied")
                self.denied = True
                return
            for name in names:
                await walk(normalize(path, name), display + name if display.endswith("/") else display + "/" + name, depth + 1, start)

        code = 0
        try:
            for s in starts:
                path = ctx.resolve(s)
                if not ctx.fs.exists(path):
                    ctx.error(f"find: {quote_l(s)}: {ctx.fs.why_missing(ctx.resolve(s))}")
                    code = 1
                    continue
                await walk(path, s, 0, s)
        except _FindQuit:
            pass
        for cmd, paths in self.batches:
            if paths and await ctx.invoke([y for x in cmd for y in (paths if x == "{}" else [x])]) != 0:
                code = 1
        if self.denied:
            code = 1
        for path in sorted(self.deletes, key=len, reverse=True):
            if ctx.fs.exists(path):
                ctx.fs.delete(path, recursive=True)
        return code

    def parse_or(self):
        left = self.parse_and()
        while self.is_("-o") or self.is_("-or"):
            self.p += 1
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
                raise ShellError("invalid expression; expected `)'")
            self.p += 1
            return inner
        return self.primary()

    def primary(self):
        ctx, fs = self.ctx, self.ctx.fs
        tok = self.args[self.p]
        self.p += 1

        def arg():
            if self.p >= len(self.args):
                raise ShellError(f"missing argument to `{tok}'")
            self.p += 1
            return self.args[self.p - 1]

        def pred(fn):
            async def run(e):
                return fn(e[0], e[1], e[2])
            return run

        if tok in ("-name", "-iname"):
            rx = glob_to_regex(arg(), ignore_case=tok == "-iname")
            return pred(lambda p, n, d: bool(rx.match(n)))
        if tok in ("-path", "-ipath", "-wholename"):
            rx = glob_to_regex(arg(), ignore_case=tok == "-ipath", slashes=True)
            return pred(lambda p, n, d: bool(rx.match(d)))
        if tok == "-regex":
            rx = re.compile("(?:" + arg() + r")\Z")
            return pred(lambda p, n, d: bool(rx.match(d)))
        if tok == "-type":
            t = arg()
            if t not in ("f", "d", "l"):
                raise ShellError(f"Unknown argument to -type: {t}")
            return pred(lambda p, n, d: t != "l" and fs.is_dir(p) == (t == "d"))
        if tok in ("-maxdepth", "-mindepth"):
            v = int(arg())
            if tok == "-maxdepth":
                self.max_depth = v
            else:
                self.min_depth = v
            return pred(lambda p, n, d: True)
        if tok in ("-mmin", "-mtime"):
            spec = arg()
            unit, num = (60 if tok == "-mmin" else 86400), float(spec.lstrip("+-"))

            def age_ok(p, n, d):
                age = (utc_now() - fs.stat(p).modified).total_seconds() / unit
                return age < num if spec[0] == "-" else age > num if spec[0] == "+" else int(age) == num
            return pred(age_ok)
        if tok == "-newer":
            ref = fs.stat(ctx.resolve(arg())).modified
            return pred(lambda p, n, d: fs.stat(p).modified > ref)
        if tok == "-size":
            m = re.fullmatch(r"([+-]?)(\d+)([cwbkMG]?)", arg())
            if not m:
                raise ShellError("invalid -size argument")
            unit = {"c": 1, "w": 2, "k": 1024, "M": 1 << 20, "G": 1 << 30}.get(m.group(3), 512)
            num = int(m.group(2))

            def size_ok(p, n, d):
                if fs.is_dir(p):
                    return False
                size = (fs.stat(p).size + unit - 1) // unit
                return size > num if m.group(1) == "+" else size < num if m.group(1) == "-" else size == num
            return pred(size_ok)
        if tok == "-empty":
            return pred(lambda p, n, d: not fs.listdir(p) if fs.is_dir(p) else fs.stat(p).size == 0)
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
                raise ShellError(f"missing argument to `{tok}'")
            if not cmd:
                raise ShellError(f"invalid argument `{self.args[self.p]}' to `{tok}'")
            terminator = self.args[self.p]
            self.p += 1
            if terminator == "+":
                batch: list[str] = []
                self.batches.append((cmd, batch))
                return pred(lambda p, n, d: batch.append(d) is None)

            async def run_exec(e):
                return await ctx.invoke([x.replace("{}", e[2]) for x in cmd]) == 0
            return run_exec
        raise ShellError(f"unknown predicate `{tok}' (supported: -name -iname -path -regex -type -size -mmin -mtime -newer "
                         "-empty -maxdepth -mindepth -prune -print -print0 -printf -exec -delete -quit ! -not -a -o ( ))")


class _FindQuit(Exception):
    pass


_FIND_TIME = {"Y": "%Y", "m": "%m", "d": "%d", "H": "%H", "M": "%M", "S": "%S.0000000000", "T": "%H:%M:%S.0000000000",
              "F": "%Y-%m-%d", "+": "%Y-%m-%d+%H:%M:%S.0000000000", "a": "%a", "b": "%b", "y": "%y", "D": "%m/%d/%y", "R": "%H:%M"}


def _find_printf(fmt: str, e, fs) -> str:
    """find -printf: %p %f %h %P %s %d %y %m %M %u %g %k %t %T@ %Tk ... with optional width, plus \\n \\t \\0 escapes."""
    path, name, display, depth, start = e
    st = fs.stat(path)
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
                "y": "d" if st.is_dir else "f", "Y": "d" if st.is_dir else "f", "m": format(st.mode & 0o7777, "o"),
                "M": format_mode(st.is_dir, st.mode), "u": st.owner, "g": st.owner, "n": "1",
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
    a = Args.parse(ctx.args, "ad", "L")
    max_depth = int(a.value("L")) if a.value("L") else 10**9
    root = a.positional[0] if a.positional else "."
    path = ctx.resolve(root)
    if not ctx.fs.is_dir(path):
        raise ShellError(f"{root} [error opening dir]")
    counts = {"dirs": 0, "files": 0}
    out = [root + "\n"]

    def walk(d, prefix, depth):
        entries = [n for n in ctx.fs.listdir(d) if (a.has("a") or not n.startswith("."))
                   and (not a.has("d") or ctx.fs.is_dir(normalize(d, n)))]
        for i, name in enumerate(entries):
            last = i == len(entries) - 1
            child = normalize(d, name)
            out.append(prefix + ("└── " if last else "├── ") + name + "\n")
            if ctx.fs.is_dir(child):
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
    r("ls", _ls, "List directory contents")
    r("cat", _cat, "Print files")
    r("mkdir", _mkdir, "Create directories")
    r("touch", _touch, "Create empty files / update timestamps")
    r("rm", _rm, "Remove files or directories")
    r("rmdir", _rmdir, "Remove empty directories")
    r("cp", _copy_or_move, "Copy files and directories")
    r("mv", _copy_or_move, "Move or rename files")
    r("find", _find, "Search for files")
    r("tree", _tree, "Show a directory tree")
