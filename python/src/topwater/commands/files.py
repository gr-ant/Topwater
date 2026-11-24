"""Filesystem commands: ls cd-free file ops, find, tree."""
from __future__ import annotations

import re
from datetime import datetime

from ..shell import glob_to_regex
from ..vfs import ShellError, get_name, normalize
from . import Args, inputs, split_lines


def _human(n: int) -> str:
    if n < 1024:
        return str(n)
    if n < 1024 * 1024:
        return f"{n / 1024:.1f}".rstrip("0").rstrip(".") + "K"
    return f"{n / (1024 * 1024):.1f}".rstrip("0").rstrip(".") + "M"


def _long_entry(ctx, path: str, name: str, human: bool) -> str:
    st = ctx.fs.stat(path)
    user = ctx.shell.user
    size = _human(st.size) if human else str(st.size)
    perms = "drwxr-xr-x" if st.is_dir else "-rw-r--r--"
    return f"{perms} 1 {user} {user} {size:>6} {st.modified:%b %d %H:%M} {name}"


def _ls(ctx):
    a = Args.parse(ctx.args, "alhA1dFrtRS", longs="all=a,almost-all=A,human-readable=h,directory=d,reverse=r,recursive=R")
    paths = a.positional or ["."]
    code = 0

    def show(path, name):
        text = _long_entry(ctx, path, name, a.has("h")) if a.has("l") else name
        if a.has("F") and ctx.fs.is_dir(path) and name not in (".", ".."):
            text += "/"
        return text

    for p in paths:
        if not ctx.fs.exists(ctx.resolve(p)):
            ctx.error(f"ls: cannot access '{p}': No such file or directory")
            code = 2
    files = [p for p in paths if ctx.fs.exists(ctx.resolve(p)) and (a.has("d") or not ctx.fs.is_dir(ctx.resolve(p)))]
    for f in files:
        ctx.print(show(ctx.resolve(f), f))
    dirs = [p for p in paths if p not in files and ctx.fs.is_dir(ctx.resolve(p))]
    first = [not files]

    def list_dir(path, label):
        if len(paths) > 1 or a.has("R"):
            ctx.print(("" if first[0] else "\n") + label + ":")
        first[0] = False
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
            ctx.print(f"total {len(entries) * 4}")
        for n in entries:
            ctx.print(show(normalize(path, n), n))
        if a.has("R"):
            for n in entries:
                child = normalize(path, n)
                if n not in (".", "..") and ctx.fs.is_dir(child):
                    list_dir(child, label.rstrip("/") + "/" + n)

    for d in dirs:
        list_dir(ctx.resolve(d), d)
    return code


def _cat(ctx):
    a = Args.parse(ctx.args, "n", longs="number=n")
    code = 0
    line_no = 0

    def fail():
        nonlocal code
        code = 1

    for _, text in inputs(ctx, a.positional, fail):
        if not a.has("n"):
            ctx.stdout.write(text)
            continue
        for line in split_lines(text):
            line_no += 1
            ctx.stdout.write(f"{line_no:6}\t{line}\n")
    return code


def _mkdir(ctx):
    a = Args.parse(ctx.args, "pv", longs="parents=p")
    if not a.positional:
        raise ShellError("missing operand")
    for p in a.positional:
        ctx.fs.mkdir(ctx.resolve(p), a.has("p"))


def _touch(ctx):
    if not ctx.args:
        raise ShellError("missing file operand")
    for p in ctx.args:
        if not p.startswith("-"):
            ctx.fs.touch(ctx.resolve(p))


def _rm(ctx):
    a = Args.parse(ctx.args, "rRfvd", longs="recursive=r,force=f")
    recursive = a.has("r") or a.has("R")
    if not a.positional and not a.has("f"):
        raise ShellError("missing operand")
    code = 0
    for p in a.positional:
        path = ctx.resolve(p)
        if not ctx.fs.exists(path):
            if not a.has("f"):
                ctx.error(f"rm: cannot remove '{p}': No such file or directory")
                code = 1
            continue
        if ctx.fs.is_dir(path) and not recursive and not a.has("d"):
            ctx.error(f"rm: cannot remove '{p}': Is a directory")
            code = 1
            continue
        ctx.fs.delete(path, recursive)
    return code


def _rmdir(ctx):
    for p in ctx.args:
        path = ctx.resolve(p)
        if not ctx.fs.is_dir(path):
            raise ShellError(f"failed to remove '{p}': Not a directory")
        ctx.fs.delete(path)


def _copy_or_move(ctx):
    move = ctx.name == "mv"
    a = Args.parse(ctx.args, "fvni" if move else "rRfvanp", longs="recursive=r,force=f")
    if len(a.positional) < 2:
        raise ShellError("missing file operand" if not a.positional else f"missing destination file operand after '{a.positional[0]}'")
    *sources, dest_arg = a.positional
    dest = ctx.resolve(dest_arg)
    dest_is_dir = ctx.fs.is_dir(dest)
    if len(sources) > 1 and not dest_is_dir:
        raise ShellError(f"target '{dest_arg}' is not a directory")
    code = 0
    for s in sources:
        src = ctx.resolve(s)
        if not ctx.fs.exists(src):
            ctx.error(f"{ctx.name}: cannot stat '{s}': No such file or directory")
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
        (ctx.fs.move if move else ctx.fs.copy)(src, target)
    return code


class _Find:
    """find with real expressions: ( ) ! -not -a -o, tests, and actions (-print -print0 -exec -delete)."""

    def __init__(self, ctx):
        self.ctx = ctx
        self.args: list[str] = []
        self.p = 0
        self.max_depth, self.min_depth = 10**9, 0
        self.has_action = False
        self.deletes: list[str] = []
        self.batches: list[tuple[list[str], list[str]]] = []

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

        async def walk(path, display, depth):
            if depth >= self.min_depth and await expr((path, get_name(path), display)) and not self.has_action:
                ctx.print(display)
            if depth >= self.max_depth or not ctx.fs.is_dir(path):
                return
            for name in ctx.fs.listdir(path):
                await walk(normalize(path, name), display + name if display.endswith("/") else display + "/" + name, depth + 1)

        code = 0
        for s in starts:
            path = ctx.resolve(s)
            if not ctx.fs.exists(path):
                ctx.error(f"find: '{s}': No such file or directory")
                code = 1
                continue
            await walk(path, s, 0)
        for cmd, paths in self.batches:
            if paths and await ctx.invoke([y for x in cmd for y in (paths if x == "{}" else [x])]) != 0:
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
                return fn(*e)
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
                age = (datetime.now() - fs.stat(p).modified).total_seconds() / unit
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
                         "-empty -maxdepth -mindepth -print -print0 -exec -delete ! -not -a -o ( ))")


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
