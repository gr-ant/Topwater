"""find: walking the tree, its tests, actions and -printf (the command is registered with the file commands). See docs/architecture.md."""
from __future__ import annotations

import re
from datetime import timezone

from .._spec import COMMANDS
from ..shell import glob_to_regex
from ..vfs import ShellError, format_mode, get_name, normalize, utc_now
from . import spec_message


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
