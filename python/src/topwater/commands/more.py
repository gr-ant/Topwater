"""Scripting built-ins (set, read, local, exit, ...) and everyday utilities (seq, diff, stat, ...)."""
from __future__ import annotations

import asyncio
import base64
import difflib
import hashlib
import json
import random
import re
import string
import time
from datetime import datetime

from .. import arith
from ..convert import file_type
from ..shell import ExitSignal, LoopControl, ReturnSignal
from ..vfs import ShellError, format_mode, get_parent, normalize
from . import Args, inputs, split_lines, unescape, write_lines


class _Errors:
    def __init__(self):
        self.hit = False

    def __call__(self):
        self.hit = True


def _count(ctx, fallback: int) -> int:
    if not ctx.args:
        return fallback
    try:
        return int(ctx.args[0])
    except ValueError:
        raise ShellError(f"{ctx.args[0]}: numeric argument required", 2) from None


def _duration(s: str) -> float:
    m = re.fullmatch(r"(\d+(?:\.\d+)?)([smhd]?)", s)
    if not m:
        raise ShellError(f"invalid time interval '{s}'")
    return float(m.group(1)) * {"": 1, "s": 1, "m": 60, "h": 3600, "d": 86400}[m.group(2)]


# ---------- scripting ----------

def _exit(ctx):
    raise ExitSignal(_count(ctx, ctx.shell.last_exit_code) & 255)


def _return(ctx):
    raise ReturnSignal(_count(ctx, ctx.shell.last_exit_code) & 255)


def _break(ctx):
    raise LoopControl(ctx.name == "break", max(1, _count(ctx, 1)))


def _shift(ctx):
    n = _count(ctx, 1)
    if n > len(ctx.shell.positional):
        return 1
    ctx.shell.positional = ctx.shell.positional[n:]


def _set(ctx):
    args = list(ctx.args)
    if not args:
        for k in sorted(ctx.shell.env):
            ctx.print(f"{k}={ctx.shell.env[k]}")
        return
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--":
            ctx.shell.positional = args[i + 1:]
            return
        if len(a) < 2 or a[0] not in "-+":
            break
        on = a[0] == "-"
        for c in a[1:]:
            if c == "e":
                ctx.shell.errexit = on
            elif c == "o":
                if i + 1 >= len(args):
                    raise ShellError("-o: option name required", 2)
                i += 1
                if args[i] == "pipefail":
                    ctx.shell.pipefail = on
                elif args[i] == "errexit":
                    ctx.shell.errexit = on
                # nounset, xtrace, noclobber...: accepted, not enforced (bash-like, simplified)
        i += 1
    if i < len(args):
        ctx.shell.positional = args[i:]


def _declare(ctx):
    for a in ctx.args:
        if a.startswith(("-", "+")):
            continue
        name, eq, value = a.partition("=")
        val = value if eq else None
        if ctx.name == "local" or (ctx.name in ("declare", "typeset") and ctx.shell.in_function):
            ctx.shell.declare_local(name, val)
        else:
            ctx.shell.env[name] = val if val is not None else ctx.shell.env.get(name, "")


def _read(ctx):
    a = Args.parse(ctx.args, "rsea", "ptdnu")
    names = a.positional or ["REPLY"]
    env = ctx.shell.env
    d = a.value("d")
    line = ctx.read_line("\n" if d is None else (d[:1] or "\0"))
    if line is None:
        for n in names:
            env[n] = ""
        return 1
    if not a.has("r"):
        line = re.sub(r"\\(.)", r"\1", line)
    if not a.positional:
        env["REPLY"] = line
        return 0
    ifs = env.get("IFS", " \t\n")
    seps = [c for c in ifs if not c.isspace()]
    if seps:
        fields = [f.strip() for f in re.split("|".join(map(re.escape, seps)), line, maxsplit=len(names) - 1)]
    else:
        fields = line.strip().split(None, len(names) - 1)
    for i, n in enumerate(names):
        env[n] = fields[i].strip() if i < len(fields) else ""
    return 0


async def _eval(ctx):
    return await ctx.shell._run_script_text(" ".join(ctx.args), ctx.io, subshell=False)


def _let(ctx):
    if not ctx.args:
        raise ShellError("expression expected")
    v = 0
    for expr in ctx.args:
        v = arith.evaluate(ctx.shell, expr)
    return 0 if v != 0 else 1


async def _command(ctx):
    args = list(ctx.args)
    if not args or args[0] not in ("-v", "-V"):
        return await ctx.shell._invoke(args, ctx.io) if args else 0
    code = 0
    for n in args[1:]:
        if n in ctx.shell.aliases:
            ctx.print(f"alias {n}='{ctx.shell.aliases[n]}'")
        elif n in ctx.shell.functions:
            ctx.print(n)
        elif n in ctx.shell.commands:
            ctx.print(f"/usr/bin/{n}")
        else:
            code = 1
    return code


def _printenv(ctx):
    env = ctx.shell.env
    if not ctx.args:
        for k in sorted(env):
            ctx.print(f"{k}={env[k]}")
        return 0
    code = 0
    for n in ctx.args:
        if n in env:
            ctx.print(env[n])
        else:
            code = 1
    return code


async def _sleep(ctx):
    if not ctx.args:
        raise ShellError("missing operand")
    await asyncio.sleep(sum(_duration(a) for a in ctx.args))


async def _timeout(ctx):
    args = [a for a in ctx.args]
    while args and args[0].startswith("-"):
        args.pop(0)
    if len(args) < 2:
        raise ShellError("missing operand", 125)
    try:
        return await asyncio.wait_for(ctx.shell._invoke(args[1:], ctx.io), _duration(args[0]))
    except asyncio.TimeoutError:
        return 124


async def _time(ctx):
    start = time.perf_counter()
    code = await ctx.shell._invoke(list(ctx.args), ctx.io) if ctx.args else 0
    t = time.perf_counter() - start
    ctx.stderr.write(f"\nreal\t{int(t // 60)}m{t % 60:.3f}s\nuser\t0m0.000s\nsys\t0m0.000s\n")
    return code


# ---------- utilities ----------

def _basename(ctx):
    a = Args.parse(ctx.args, "az", "s", "multiple=a,suffix=s,zero=z")
    if not a.positional:
        raise ShellError("missing operand")
    suffix = a.value("s") or (a.positional[1] if not a.has("a") and len(a.positional) == 2 else None)
    names = a.positional if a.has("a") or a.value("s") else a.positional[:1]
    for n in names:
        t = n.rstrip("/")
        b = "/" if not t else t[t.rfind("/") + 1:]
        if suffix and b.endswith(suffix) and b != suffix:
            b = b[: -len(suffix)]
        ctx.print(b)


def _dirname(ctx):
    if not ctx.args:
        raise ShellError("missing operand")
    for n in ctx.args:
        t = n.rstrip("/")
        i = t.rfind("/")
        ctx.print("/" if not t else "." if i < 0 else "/" if i == 0 else (t[:i].rstrip("/") or "/"))


def _realpath(ctx):
    a = Args.parse(ctx.args, "fesmqnz", longs="canonicalize=f,canonicalize-existing=e,canonicalize-missing=m")
    if ctx.name == "readlink" and not (a.has("f") or a.has("e") or a.has("m")):
        return 1  # nothing is a symlink
    code = 0
    for p in a.positional or ["."]:
        path = ctx.resolve(p)
        if not a.has("m") and not ctx.fs.exists(path) and (a.has("e") or ctx.name == "realpath" or not ctx.fs.is_dir(get_parent(path))):
            ctx.error(f"{ctx.name}: {p}: No such file or directory")
            code = 1
            continue
        ctx.print(path)
    return code


def _stat(ctx):
    a = Args.parse(ctx.args, "Lt", "c", "format=c,dereference=L,terse=t")
    user = ctx.shell.user
    code = 0
    for p in a.positional:
        path = ctx.resolve(p)
        if not ctx.fs.exists(path):
            ctx.error(f"stat: cannot statx '{p}': No such file or directory")
            code = 1
            continue
        st = ctx.fs.stat(path)
        kind = "directory" if st.is_dir else "regular empty file" if st.size == 0 else "regular file"
        mode = f"{st.mode:03o}"
        rwx = format_mode(st.is_dir, st.mode)
        user = st.owner
        uid = "0" if user == "root" else "1000"
        mtime = st.modified.strftime("%Y-%m-%d %H:%M:%S.%f000 +0000")
        if a.value("c") is not None:
            values = {
                "n": p, "s": str(st.size), "F": kind, "a": mode, "A": rwx, "U": user, "G": user, "u": uid, "g": uid,
                "y": mtime, "Y": str(int(st.modified.timestamp())), "b": str((st.size + 511) // 512), "%": "%",
            }
            ctx.print(re.sub(r"%(.)", lambda m: values.get(m.group(1), m.group(0)), a.value("c")))
            continue
        blocks = (st.size + 511) // 512 * 8
        ctx.stdout.write(f"  File: {p}\n  Size: {st.size:<10}\tBlocks: {blocks:<10} IO Block: 4096   {kind}\n"
                         f"Access: ({mode:0>4}/{rwx})  Uid: ({uid:>5}/{user:>8})   Gid: ({uid:>5}/{user:>8})\n"
                         f"Modify: {mtime}\nChange: {mtime}\n")
    return code


def _du(ctx):
    a = Args.parse(ctx.args, "shacSk", "d", "summarize=s,human-readable=h,all=a,total=c,max-depth=d")
    max_depth = 0 if a.has("s") else int(a.value("d")) if a.value("d") else 10**9

    def fmt(kb: int) -> str:
        if not a.has("h"):
            return str(kb)
        if kb < 10:
            return f"{kb:.1f}K"
        if kb < 1024:
            return f"{kb}K"
        if kb < 1024 * 1024:
            return f"{kb / 1024:.1f}".rstrip("0").rstrip(".") + "M"
        return f"{kb / 1048576:.1f}".rstrip("0").rstrip(".") + "G"

    def walk(path, display, depth):
        if not ctx.fs.is_dir(path):
            kb = (ctx.fs.stat(path).size + 4095) // 4096 * 4
            if (a.has("a") and depth <= max_depth) or depth == 0:
                ctx.print(f"{fmt(kb)}\t{display}")
            return kb
        total = 4 + sum(walk(normalize(path, n), display.rstrip("/") + "/" + n, depth + 1) for n in ctx.fs.listdir(path))
        if depth <= max_depth:
            ctx.print(f"{fmt(total)}\t{display}")
        return total

    code, grand = 0, 0
    for p in a.positional or ["."]:
        path = ctx.resolve(p)
        if not ctx.fs.exists(path):
            ctx.error(f"du: cannot access '{p}': No such file or directory")
            code = 1
            continue
        grand += walk(path, p, 0)
    if a.has("c"):
        ctx.print(f"{fmt(grand)}\ttotal")
    return code


def _df(ctx):
    human = any(x.startswith("-") and "h" in x for x in ctx.args)
    ctx.stdout.write(
        "Filesystem      Size  Used Avail Use% Mounted on\n/dev/sda1        40G  7.8G   30G  21% /\ntmpfs           3.9G     0  3.9G   0% /dev/shm\n"
        if human else
        "Filesystem     1K-blocks    Used Available Use% Mounted on\n/dev/sda1       41152736 8123456  31016272  21% /\ntmpfs            4007440       0   4007440   0% /dev/shm\n")


def chmod_parse(spec: str, mode: int, is_dir: bool) -> int:
    """Octal (755) or symbolic (u+x, go-w, a=r, +x, +t) mode."""
    if re.fullmatch(r"[0-7]{1,4}", spec):
        return int(spec, 8)
    for clause in spec.split(","):
        m = re.fullmatch(r"([ugoa]*)([-+=])([rwxXt]*)", clause)
        if not m:
            raise ShellError(f"invalid mode: '{spec}'")
        who = "ugo" if m.group(1) in ("", "a") else m.group(1).replace("a", "ugo")
        bits, sticky = 0, False
        for c in m.group(3):
            if c == "r":
                bits |= 4
            elif c == "w":
                bits |= 2
            elif c == "x" or (c == "X" and (is_dir or mode & 0o111)):
                bits |= 1
            elif c == "t":
                sticky = True
        mask = clear = 0
        for w in who:
            shift = {"u": 6, "g": 3, "o": 0}[w]
            mask |= bits << shift
            clear |= 7 << shift
        sticky_bit = 0o1000 if sticky else 0
        op = m.group(2)
        mode = mode | mask | sticky_bit if op == "+" else mode & ~(mask | sticky_bit) if op == "-" else (mode & ~clear) | mask | sticky_bit
    return mode


def _chmod(ctx):
    recursive = verbose = changes = quiet = False
    spec, files = None, []
    for arg in ctx.args:
        if arg in ("-R", "--recursive"):
            recursive = True
        elif arg in ("-v", "--verbose"):
            verbose = True
        elif arg in ("-c", "--changes"):
            changes = True
        elif arg in ("-f", "--silent", "--quiet"):
            quiet = True
        elif spec is None:
            spec = arg  # may look like an option, e.g. -x or -w
        else:
            files.append(arg)
    if spec is None or not files:
        raise ShellError("missing operand" if spec is None else f"missing operand after '{spec}'")
    code = 0

    def fmt(st, m):
        return f"{m:04o} ({format_mode(st.is_dir, m)[1:]})"

    def apply(display, path):
        nonlocal code
        st = ctx.fs.stat(path)
        mode = chmod_parse(spec, st.mode, st.is_dir)
        try:
            ctx.fs.chmod(path, mode)
        except ShellError:
            if not quiet:
                ctx.error(f"chmod: changing permissions of '{display}': Operation not permitted")
            code = 1
            return
        if mode != st.mode and (verbose or changes):
            ctx.print(f"mode of '{display}' changed from {fmt(st, st.mode)} to {fmt(st, mode)}")
        elif mode == st.mode and verbose:
            ctx.print(f"mode of '{display}' retained as {fmt(st, mode)}")
        if recursive and st.is_dir and ctx.fs.can_read(path):
            for n in ctx.fs.listdir(path):
                apply(display.rstrip("/") + "/" + n, normalize(path, n))

    for f in files:
        path = ctx.resolve(f)
        if not ctx.fs.exists(path):
            ctx.error(f"chmod: cannot access '{f}': No such file or directory")
            code = 1
            continue
        apply(f, path)
    return code


def _chown(ctx):
    operands = [x for x in ctx.args if not x.startswith("-")]
    files = operands[1:]  # first operand is the owner
    if not files:
        raise ShellError("missing operand")
    owner = operands[0].split(":")[0]
    code = 0
    for f in files:
        path = ctx.resolve(f)
        if not ctx.fs.exists(path):
            ctx.error(f"{ctx.name}: cannot access '{f}': No such file or directory")
            code = 1
            continue
        try:
            ctx.fs.set_owner(path, owner or ctx.fs.stat(path).owner, "-R" in ctx.args)
        except ShellError:
            ctx.error(f"{ctx.name}: changing ownership of '{f}': Operation not permitted")
            code = 1
    return code


def _sudo(ctx):
    """The agent is a regular user: privilege escalation fails the way it does on a real server."""
    user = ctx.shell.user
    if ctx.args in (["-V"], ["--version"]):
        ctx.print("Sudo version 1.9.15p5")
        return 0
    if not ctx.args:
        ctx.error("usage: sudo -h | -K | -k | -V\nusage: sudo [-AbEHknPS] [-u user] [command [arg ...]]")
        return 1
    if ctx.args[0] in ("-l", "--list"):
        ctx.error(f"Sorry, user {user} may not run sudo on {ctx.shell.hostname}.")
        return 1
    ctx.error(f"{user} is not in the sudoers file.")
    return 1


def _su(ctx):
    ctx.error("Password: \nsu: Authentication failure")
    return 1


def _nl(ctx):
    a = Args.parse(ctx.args, "", "bsw")
    every = a.value("b") == "a"
    width = int(a.value("w") or 6)
    sep = a.value("s") or "\t"
    errors, n = _Errors(), 0
    for _, text in inputs(ctx, a.positional, errors):
        for line in split_lines(text):
            if not line and not every:
                ctx.print("")
                continue
            n += 1
            ctx.print(f"{str(n).rjust(width)}{sep}{line}")
    return 1 if errors.hit else 0


def _pager(ctx):
    errors = _Errors()
    for _, text in inputs(ctx, [x for x in ctx.args if not x.startswith(("-", "+"))], errors):
        ctx.stdout.write(text)
    return 1 if errors.hit else 0


def _looks_json(t: str) -> bool:
    s = t.lstrip()
    if not s or s[0] not in "{[":
        return False
    try:
        json.loads(t)
        return True
    except ValueError:
        return False


def _file(ctx):
    code = 0
    for p in ctx.args:
        if p.startswith("-"):
            continue
        path = ctx.resolve(p)
        if not ctx.fs.exists(path):
            desc, code = f"cannot open `{p}' (No such file or directory)", 1
        elif ctx.fs.is_dir(path):
            desc = "directory"
        else:
            data = ctx.fs.read_bytes(path)
            t = data.decode("utf-8", "replace")
            if not t:
                desc = "empty"
            elif file_type(data):
                desc = file_type(data)
            elif t.startswith("\x7fELF"):
                desc = "ELF 64-bit LSB pie executable, x86-64, version 1 (SYSV), dynamically linked, stripped"
            elif t.startswith("#!"):
                desc = ("Python script" if "python" in t.split("\n", 1)[0] else "Bourne-Again shell script") + ", ASCII text executable"
            elif _looks_json(t):
                desc = "JSON text data"
            elif "\0" in t or t.encode() != data:  # NUL bytes or invalid UTF-8
                desc = "data"
            elif any(ord(c) > 127 for c in t):
                desc = "Unicode text, UTF-8 text"
            else:
                desc = "ASCII text"
        ctx.print(f"{p}: {desc}")
    return code


def _rev(ctx):
    errors = _Errors()
    for _, text in inputs(ctx, ctx.args, errors):
        for line in split_lines(text):
            ctx.print(line[::-1])
    return 1 if errors.hit else 0


def _tac(ctx):
    errors = _Errors()
    for _, text in inputs(ctx, ctx.args, errors):
        write_lines(ctx.stdout, reversed(split_lines(text)))
    return 1 if errors.hit else 0


def _paste(ctx):
    a = Args.parse(ctx.args, "s", "d", "serial=s,delimiters=d")
    delims = unescape(a.value("d") if a.value("d") is not None else "\t") or "\0"

    def join(cells):
        out = []
        for i, cell in enumerate(cells):
            if i and delims[(i - 1) % len(delims)] != "\0":
                out.append(delims[(i - 1) % len(delims)])
            out.append(cell)
        return "".join(out)

    errors = _Errors()
    columns = [split_lines(t) for _, t in inputs(ctx, a.positional, errors)]
    if a.has("s"):
        for col in columns:
            ctx.print(join(col))
    else:
        for r in range(max((len(c) for c in columns), default=0)):
            ctx.print(join([c[r] if r < len(c) else "" for c in columns]))
    return 1 if errors.hit else 0


def _column(ctx):
    a = Args.parse(ctx.args, "tn", "so", "table=t,separator=s,output-separator=o")
    errors = _Errors()
    lines = [l for _, t in inputs(ctx, a.positional, errors) for l in split_lines(t)]
    if not a.has("t"):
        write_lines(ctx.stdout, lines)
        return 1 if errors.hit else 0
    sep = a.value("s")
    rows = []
    for l in lines:
        if not l.strip():
            continue
        rows.append([c for c in re.split("[" + re.escape(unescape(sep)) + "]", l) if c] if sep else l.split())
    out_sep = a.value("o") or "  "
    widths = [max((len(r[c]) for r in rows if c < len(r)), default=0) for c in range(max((len(r) for r in rows), default=0))]
    for r in rows:
        ctx.print(out_sep.join(cell if i == len(r) - 1 else cell.ljust(widths[i]) for i, cell in enumerate(r)))
    return 1 if errors.hit else 0


def _base64(ctx):
    a = Args.parse(ctx.args, "di", "w", "decode=d,ignore-garbage=i,wrap=w")
    errors = _Errors()
    data = b"".join(t for _, t in inputs(ctx, a.positional, errors, raw=True))
    if a.has("d"):
        try:
            ctx.stdout.write(base64.b64decode(b"".join(data.split()), validate=True).decode("utf-8", "replace"))
        except ValueError:
            raise ShellError("invalid input") from None
        return 1 if errors.hit else 0
    enc = base64.b64encode(data).decode()
    wrap = int(a.value("w") or 76)
    if wrap > 0:
        enc = "\n".join(enc[i:i + wrap] for i in range(0, len(enc), wrap))
    ctx.print(enc)
    return 1 if errors.hit else 0


def _hasher(algo):
    def run(ctx):
        files = [x for x in ctx.args if not x.startswith("-") or x == "-"]
        errors = _Errors()
        for name, data in inputs(ctx, files, errors, raw=True):
            ctx.print(f"{hashlib.new(algo, data).hexdigest()}  {name if files else '-'}")
        return 1 if errors.hit else 0
    return run


def _mktemp(ctx):
    a = Args.parse(ctx.args, "dqu", "pt", "directory=d,quiet=q,tmpdir=p")
    template = a.positional[0] if a.positional else "tmp.XXXXXXXXXX"
    chars = string.ascii_letters + string.digits
    name = re.sub(r"X{3,}$", lambda m: "".join(random.choice(chars) for _ in m.group(0)), template)
    path = ctx.resolve(name) if "/" in template and a.value("p") is None else normalize(a.value("p") or "/tmp", name)
    if a.has("d"):
        ctx.fs.mkdir(path)
    else:
        ctx.fs.write_text(path, "")
    ctx.print(path)


def _seq(ctx):
    nums, sep, equal_width = [], "\n", False
    args = ctx.args
    i = 0
    while i < len(args):
        x = args[i]
        if re.fullmatch(r"[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?", x):
            nums.append(x)
        elif x in ("-w", "--equal-width"):
            equal_width = True
        elif x == "-s":
            if i + 1 >= len(args):
                raise ShellError("option requires an argument -- 's'")
            i += 1
            sep = args[i]
        elif x.startswith("-s"):
            sep = x[2:]
        elif x.startswith("--separator="):
            sep = x[12:]
        else:
            raise ShellError(f"invalid option -- '{x.lstrip('-')}'")
        i += 1
    if not nums or len(nums) > 3:
        raise ShellError("missing operand" if not nums else f"extra operand '{nums[3]}'")
    v = [float(x) for x in nums]
    first = v[0] if len(v) > 1 else 1
    step = v[1] if len(v) == 3 else 1
    last = v[-1]
    if step == 0:
        raise ShellError("invalid Zero increment value: '0'")
    decimals = max((len(x) - x.index(".") - 1 if "." in x else 0) for x in nums)
    items, x = [], first
    while (x <= last + 1e-9) if step > 0 else (x >= last - 1e-9):
        items.append(f"{x:.{decimals}f}")
        if len(items) > 1_000_000:
            raise ShellError("too many numbers")
        x += step
    if equal_width and items:
        w = max(len(s) for s in items)
        items = [s.zfill(w) for s in items]
    if items:
        ctx.stdout.write(unescape(sep).join(items) + "\n")


def _diff(ctx):
    a = Args.parse(ctx.args, "uqsiwbBNr", "U", "unified=u,brief=q,report-identical-files=s,ignore-case=i,"
                                               "ignore-all-space=w,ignore-space-change=b,recursive=r")
    if len(a.positional) != 2:
        raise ShellError(f"missing operand after '{a.positional[-1] if a.positional else 'diff'}'"
                         if len(a.positional) < 2 else f"extra operand '{a.positional[2]}'", 2)

    def read(p):
        if p == "-":
            return ctx.stdin
        path = ctx.resolve(p)
        if not ctx.fs.exists(path):
            raise ShellError(f"{p}: No such file or directory", 2)
        if ctx.fs.is_dir(path):
            raise ShellError(f"{p}: Is a directory (directory diff is not supported)", 2)
        return ctx.fs.read_text(path)

    an, bn = a.positional
    x, y = split_lines(read(an)), split_lines(read(bn))

    def key(line):
        if a.has("w"):
            line = "".join(line.split())
        elif a.has("b"):
            line = re.sub(r"\s+", " ", line.rstrip())
        return line.lower() if a.has("i") else line

    sm = difflib.SequenceMatcher(None, [key(l) for l in x], [key(l) for l in y], autojunk=False)
    opcodes = sm.get_opcodes()
    if all(tag == "equal" for tag, *_ in opcodes):
        if a.has("s"):
            ctx.print(f"Files {an} and {bn} are identical")
        return 0
    if a.has("q"):
        ctx.print(f"Files {an} and {bn} differ")
        return 1

    def rng(start, count):
        return str(start) if count == 1 else f"{start},{start + count - 1}"

    if a.has("u") or a.value("U") is not None:
        n = int(a.value("U") or 3)
        ctx.print(f"--- {an}")
        ctx.print(f"+++ {bn}")
        for group in sm.get_grouped_opcodes(n):
            i1, i2, j1, j2 = group[0][1], group[-1][2], group[0][3], group[-1][4]
            ctx.print(f"@@ -{i1 + (1 if i2 > i1 else 0)},{i2 - i1} +{j1 + (1 if j2 > j1 else 0)},{j2 - j1} @@")
            for tag, a1, a2, b1, b2 in group:
                if tag == "equal":
                    write_lines(ctx.stdout, (" " + l for l in x[a1:a2]))
                    continue
                write_lines(ctx.stdout, ("-" + l for l in x[a1:a2]))
                write_lines(ctx.stdout, ("+" + l for l in y[b1:b2]))
        return 1
    for tag, a1, a2, b1, b2 in opcodes:
        if tag == "equal":
            continue
        if tag == "replace":
            ctx.print(f"{rng(a1 + 1, a2 - a1)}c{rng(b1 + 1, b2 - b1)}")
        elif tag == "delete":
            ctx.print(f"{rng(a1 + 1, a2 - a1)}d{b1}")
        else:
            ctx.print(f"{a1}a{rng(b1 + 1, b2 - b1)}")
        write_lines(ctx.stdout, ("< " + l for l in x[a1:a2]))
        if tag == "replace":
            ctx.print("---")
        write_lines(ctx.stdout, ("> " + l for l in y[b1:b2]))
    return 1


# ---------- fake system info ----------

def _id(ctx):
    u = ctx.shell.user
    a = Args.parse(ctx.args, "ugnGr")
    ctx.print((u if a.has("n") else "1000") if a.has("u") or a.has("g") else f"uid=1000({u}) gid=1000({u}) groups=1000({u})")


def _uptime(ctx):
    ctx.print("up 3 days, 4 hours, 12 minutes" if "-p" in ctx.args
              else f" {datetime.now():%H:%M:%S} up 3 days,  4:12,  1 user,  load average: 0.08, 0.03, 0.01")


def _free(ctx):
    human = any("h" in x for x in ctx.args)
    ctx.stdout.write(
        "               total        used        free      shared  buff/cache   available\n"
        + ("Mem:           7.6Gi       1.9Gi       3.8Gi        12Mi       2.1Gi       5.5Gi\nSwap:          2.0Gi          0B       2.0Gi\n"
           if human else
           "Mem:         8014880     1992344     3984512       12288     2201024     5748660\nSwap:        2097148           0     2097148\n"))


def register(shell) -> None:
    r = shell.register_command
    r("exit", _exit, "Exit the shell")
    r("return", _return, "Return from a function")
    r("break", _break, "Exit a loop")
    r("continue", _break, "Next loop iteration")
    r("shift", _shift, "Shift positional parameters")
    r("set", _set, "Set shell options (-e, -o pipefail) or positional parameters")
    r("local", _declare, "Declare function-local variables")
    r("declare", _declare, "Declare variables")
    r("typeset", _declare, "Declare variables")
    r("readonly", _declare, "Declare variables (not enforced)")
    r("read", _read, "Read a line from stdin into variables")
    r("eval", _eval, "Run arguments as a shell command")
    r("let", _let, "Evaluate arithmetic expressions")
    r("command", _command, "Run a command, or describe it with -v")
    r("printenv", _printenv, "Print environment variables")
    r("sleep", _sleep, "Pause for a number of seconds")
    r("timeout", _timeout, "Run a command with a time limit")
    r("time", _time, "Time a command")
    r("clear", lambda ctx: 0, "Clear the terminal")
    r("basename", _basename, "Strip directory and suffix from a path")
    r("dirname", _dirname, "Strip the last component from a path")
    r("realpath", _realpath, "Print the absolute path")
    r("readlink", _realpath, "Print resolved path (-f)")
    r("stat", _stat, "Show file status")
    r("du", _du, "Estimate disk usage")
    r("df", _df, "Report filesystem disk space")
    r("chmod", _chmod, "Change file mode bits")
    r("chown", _chown, "Change file owner")
    r("chgrp", _chown, "Change file group")
    r("sudo", _sudo, "Execute a command as another user")
    r("su", _su, "Run a command with a substitute user ID")
    r("nl", _nl, "Number lines")
    r("less", _pager, "View files (non-interactive: prints them)")
    r("more", _pager, "View files (non-interactive: prints them)")
    r("file", _file, "Determine file type")
    r("rev", _rev, "Reverse each line")
    r("tac", _tac, "Print lines in reverse order")
    r("paste", _paste, "Merge lines of files")
    r("column", _column, "Format input into aligned columns (-t)")
    r("base64", _base64, "Base64 encode/decode")
    for name, algo in (("md5sum", "md5"), ("sha1sum", "sha1"), ("sha256sum", "sha256"), ("sha512sum", "sha512")):
        r(name, _hasher(algo), f"Compute {algo.upper()} checksums")
    r("mktemp", _mktemp, "Create a temporary file or directory")
    r("seq", _seq, "Print a sequence of numbers")
    r("diff", _diff, "Compare files line by line (-u for unified)")
    r("id", _id, "Print user and group IDs")
    r("uptime", _uptime, "Show how long the system has been running")
    r("free", _free, "Display memory usage")
    r("nproc", lambda ctx: ctx.print("4"), "Print the number of processors")
