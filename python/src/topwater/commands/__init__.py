"""Built-in commands and the helpers they share."""
from __future__ import annotations

import re
from datetime import datetime, timezone

from ..vfs import ShellError


class Args:
    """Small getopt-style parser shared by the built-ins."""

    def __init__(self):
        self.flags: set[str] = set()
        self.values: dict[str, str] = {}
        self.positional: list[str] = []

    def has(self, c: str) -> bool:
        return c in self.flags

    def value(self, c: str) -> str | None:
        return self.values.get(c)

    @staticmethod
    def parse(args, flags: str = "", valued: str = "", longs: str = "") -> "Args":
        """flags: allowed boolean short flags ("rfi"); valued: short options taking a value ("n");
        longs: long aliases ("recursive=r,ignore-case=i")."""
        mapping = dict(x.split("=") for x in longs.split(",") if x)
        r = Args()
        i = 0
        while i < len(args):
            a = args[i]
            if a == "--":
                r.positional.extend(args[i + 1:])
                break
            if a.startswith("--"):
                name, eq, inline = a[2:].partition("=")
                if name not in mapping:
                    raise ShellError(f"unrecognized option '{a}'", 2)
                c = mapping[name]
                if c in valued:
                    if eq:
                        r.values[c] = inline
                    elif i + 1 < len(args):
                        i += 1
                        r.values[c] = args[i]
                    else:
                        raise ShellError(f"option '--{name}' requires an argument", 2)
                else:
                    r.flags.add(c)
                i += 1
                continue
            if len(a) < 2 or a[0] != "-":
                r.positional.append(a)
                i += 1
                continue
            for k in range(1, len(a)):
                c = a[k]
                if c in valued:
                    if k + 1 < len(a):
                        r.values[c] = a[k + 1:]
                    elif i + 1 < len(args):
                        i += 1
                        r.values[c] = args[i]
                    else:
                        raise ShellError(f"option requires an argument -- '{c}'", 2)
                    break
                if c not in flags:
                    raise ShellError(f"invalid option -- '{c}'", 2)
                r.flags.add(c)
            i += 1
        return r

    @staticmethod
    def numeric_shorthand(args) -> list[str]:
        """Rewrites "-5" into "-n 5" (head/tail style)."""
        out = []
        for a in args:
            if len(a) > 1 and a[0] == "-" and a[1:].isdigit():
                out += ["-n", a[1:]]
            else:
                out.append(a)
        return out


_BASH_VERSION = "GNU bash, version 5.2.21(1)-release (x86_64-pc-linux-gnu)"
_VERSIONS = {
    "grep": "grep (GNU grep) 3.11", "sed": "sed (GNU sed) 4.9", "find": "find (GNU findutils) 4.9.0",
    "xargs": "xargs (GNU findutils) 4.9.0", "diff": "diff (GNU diffutils) 3.10", "jq": "jq-1.7.1", "tree": "tree v2.1.1",
    "bash": _BASH_VERSION, "sh": _BASH_VERSION, "file": "file-5.45", "sudo": "Sudo version 1.9.15p5", "less": "less 590 (GNU regular expressions)",
    "ps": "ps from procps-ng 4.0.4", "free": "free from procps-ng 4.0.4", "uptime": "uptime from procps-ng 4.0.4",
    "column": "column from util-linux 2.39.3", "rev": "rev from util-linux 2.39.3", "more": "more from util-linux 2.39.3",
}
_NO_STANDARD_OPTIONS = {"sudo", "su", "echo", "printf", "test", "[", "true", "false", "awk", "help", "exit", "return", "break", "continue",
                        "set", "eval", "command", "type", "alias", "export", "local", "declare", "read", "cd"}


def standard_option(ctx, cmd, arg: str) -> bool:
    """`tool --version` / `tool --help` for built-ins, so probing them looks like a real system."""
    if arg not in ("--version", "--help") or cmd.name in _NO_STANDARD_OPTIONS:
        return False
    if arg == "--version":
        ctx.print(_VERSIONS.get(cmd.name, f"{cmd.name} (GNU coreutils) 9.4"))
    else:
        ctx.print(f"Usage: {cmd.name} [OPTION]... [ARG]...\n{cmd.description}.")
    return True


def split_lines(text: str) -> list[str]:
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    return lines


def write_lines(w, lines) -> None:
    for line in lines:
        w.write(line + "\n")


def inputs(ctx, files, on_error, raw: bool = False):
    """Yields (name, text) for each file argument (or stdin when there are none / "-"), reporting missing files.
    With raw, yields the bytes instead (exact for binary files)."""
    stdin = ctx.stdin.encode() if raw else ctx.stdin
    if not files:
        yield "(standard input)", stdin
        return
    for f in files:
        if f == "-":
            yield "(standard input)", stdin
            continue
        try:
            text = (ctx.fs.read_bytes if raw else ctx.fs.read_text)(ctx.resolve(f))
        except ShellError as e:
            ctx.stderr.write(f"{ctx.name}: {f}: {str(e).rsplit(': ', 1)[-1]}\n")
            on_error()
            continue
        yield f, text


_ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "\\": "\\", "0": "\0", "a": "\a", "e": "\x1b", '"': '"'}


def unescape(s: str, keep_unknown: bool = False) -> str:
    out = []
    i = 0
    while i < len(s):
        if s[i] == "\\" and i + 1 < len(s):
            n = s[i + 1]
            out.append(_ESCAPES.get(n, ("\\" + n) if keep_unknown else n))
            i += 2
        else:
            out.append(s[i])
            i += 1
    return "".join(out)


def register_all(shell) -> None:
    from . import awk, binary, documents, files, jq, more, text

    files.register(shell)
    text.register(shell)
    _register_shell_commands(shell)
    _register_system_commands(shell)
    jq.register(shell)
    more.register(shell)
    awk.register(shell)
    documents.register(shell)
    binary.register(shell)


# ---------- shell builtins ----------

def _echo(ctx):
    args = list(ctx.args)
    newline, escapes = True, False
    while args and len(args[0]) > 1 and args[0][0] == "-" and set(args[0][1:]) <= set("neE"):
        if "n" in args[0]:
            newline = False
        if "e" in args[0]:
            escapes = True
        if "E" in args[0]:
            escapes = False
        args.pop(0)
    text = " ".join(args)
    ctx.stdout.write((unescape(text, keep_unknown=True) if escapes else text) + ("\n" if newline else ""))


def _cd(ctx):
    env = ctx.shell.env
    if not ctx.args:
        target = env.get("HOME", "/")
    elif ctx.args[0] == "-":
        target = env.get("OLDPWD", ctx.shell.cwd)
    else:
        target = ctx.args[0]
    path = ctx.resolve(target)
    if not ctx.fs.exists(path):
        raise ShellError(f"{target}: No such file or directory")
    if not ctx.fs.is_dir(path):
        raise ShellError(f"{target}: Not a directory")
    env["OLDPWD"] = ctx.shell.cwd
    ctx.shell.cwd = path
    if ctx.args == ["-"]:
        ctx.print(path)


def _export(ctx):
    args = [a for a in ctx.args if a != "-p"]
    if not args:
        for k in sorted(ctx.shell.env):
            ctx.print(f'declare -x {k}="{ctx.shell.env[k]}"')
        return
    for a in args:
        k, eq, v = a.partition("=")
        if eq:
            ctx.shell.env[k] = v
        else:
            ctx.shell.env.setdefault(k, "")


def _alias(ctx):
    code = 0
    aliases = ctx.shell.aliases
    if not ctx.args:
        for k in sorted(aliases):
            ctx.print(f"alias {k}='{aliases[k]}'")
    for a in ctx.args:
        k, eq, v = a.partition("=")
        if eq:
            aliases[k] = v
        elif k in aliases:
            ctx.print(f"alias {k}='{aliases[k]}'")
        else:
            ctx.error(f"alias: {k}: not found")
            code = 1
    return code


def _which(ctx):
    code = 0
    for a in ctx.args:
        if a.startswith("-"):
            continue
        if a in ctx.shell.commands:
            ctx.print(f"/usr/bin/{a}")
        else:
            code = 1
    return code


def _type(ctx):
    code = 0
    opts = Args.parse(ctx.args, "atpPf")
    for a in opts.positional:
        if opts.has("t") or opts.has("p") or opts.has("P"):
            sh = ctx.shell
            kind = "alias" if a in sh.aliases else "function" if a in sh.functions else "file" if a in sh.commands else None
            if kind is None:
                code = 1
            else:
                ctx.print(kind if opts.has("t") else f"/usr/bin/{a}" if kind == "file" else "")
            continue
        if a in ctx.shell.aliases:
            ctx.print(f"{a} is aliased to `{ctx.shell.aliases[a]}'")
        elif a in ctx.shell.functions:
            ctx.print(f"{a} is a function")
        elif a in ctx.shell.commands:
            ctx.print(f"{a} is /usr/bin/{a}")
        else:
            ctx.error(f"type: {a}: not found")
            code = 1
    return code


def test_unary(shell, op: str, arg: str) -> bool:
    path = shell.resolve(arg)
    fs = shell.fs
    if op == "-z":
        return arg == ""
    if op == "-n":
        return arg != ""
    if op == "-e":
        return fs.exists(path)
    if op == "-r":
        return fs.can_read(path)
    if op == "-w":
        return fs.can_write(path)
    if op == "-x":
        return fs.can_execute(path)
    if op == "-f":
        return fs.is_file(path)
    if op == "-d":
        return fs.is_dir(path)
    if op == "-s":
        return fs.is_file(path) and fs.stat(path).size > 0
    if op == "-L":
        return False
    raise ShellError(f"{op}: unary operator expected", 2)


def evaluate_test(ctx, a: list[str]) -> bool:
    if a and a[0] == "!":
        return not evaluate_test(ctx, a[1:])
    if len(a) == 0:
        return False
    if len(a) == 1:
        return a[0] != ""
    if len(a) == 2:
        return test_unary(ctx.shell, a[0], a[1])
    if len(a) == 3:
        left, op, right = a
        if op in ("=", "=="):
            return left == right
        if op == "!=":
            return left != right
        try:
            l, r = int(left), int(right)
        except ValueError:
            raise ShellError("integer expression expected", 2) from None
        ops = {"-eq": l == r, "-ne": l != r, "-lt": l < r, "-le": l <= r, "-gt": l > r, "-ge": l >= r}
        if op not in ops:
            raise ShellError(f"{op}: binary operator expected", 2)
        return ops[op]
    raise ShellError("too many arguments (-a/-o are not supported; use && or ||)", 2)


def _bracket(ctx):
    if not ctx.args or ctx.args[-1] != "]":
        raise ShellError("missing `]'", 2)
    return 0 if evaluate_test(ctx, ctx.args[:-1]) else 1


async def _run_script(ctx):
    from ..shell import StdinBuffer, _Io

    io_ = ctx.io
    args = list(ctx.args)
    is_bash = ctx.name in ("bash", "sh")
    while is_bash and args and args[0].startswith("-") and args[0] != "-c":
        args.pop(0)  # -e, -x, -l ...
    if is_bash and len(args) >= 2 and args[0] == "-c":
        return await ctx.shell._run_script_text(args[1], io_, subshell=True, positional=args[3:])
    if not args:
        if not is_bash:
            raise ShellError("filename argument required", 2)
        return await ctx.shell._run_script_text(ctx.stdin, _Io(StdinBuffer(), ctx.stdout, ctx.stderr), subshell=True)
    text = ctx.fs.read_text(ctx.resolve(args[0]))
    positional = args[1:] if len(args) > 1 or is_bash else None
    return await ctx.shell._run_script_text(text, io_, subshell=is_bash, positional=positional)


def _unset(ctx):
    target = ctx.shell.functions if "-f" in ctx.args else ctx.shell.env
    for a in ctx.args:
        if not a.startswith("-"):
            target.pop(a, None)


def _help(ctx):
    cmds = ctx.shell.commands
    if ctx.args:
        c = cmds.get(ctx.args[0])
        if c is None:
            raise ShellError(f"no help topics match '{ctx.args[0]}'")
        ctx.print(c.usage or f"{c.name}: {c.description}")
        return
    width = max(len(n) for n in cmds) + 2
    ctx.print("Available commands (run '<command> --help' for details on non-standard ones):")
    for name in sorted(cmds):
        ctx.print(f"  {name.ljust(width)}{cmds[name].description}")


_PRINTF_SPEC = re.compile(r"%([-0 +#]*)(\d*)(?:\.(\d+))?([sdifxXoc%])")


def printf_format(fmt: str, args: list[str]) -> str:
    fmt = unescape(fmt, keep_unknown=True)
    out = []
    nxt = 0

    def num(s: str) -> int:
        try:
            return int(s)
        except ValueError:
            return 0

    def sub(m: re.Match) -> str:
        nonlocal nxt
        flags, width, prec, conv = m.group(1), m.group(2), m.group(3), m.group(4)
        if conv == "%":
            return "%"
        arg = args[nxt] if nxt < len(args) else ""
        nxt += 1
        w = int(width) if width else 0
        if conv == "s":
            s = arg[: int(prec)] if prec else arg
        elif conv == "c":
            s = arg[:1]
        elif conv in "di":
            s = str(num(arg))
        elif conv == "x":
            s = format(num(arg), "x")
        elif conv == "X":
            s = format(num(arg), "X")
        elif conv == "o":
            s = format(num(arg), "o")
        else:
            try:
                f = float(arg)
            except ValueError:
                f = 0.0
            s = f"{f:.{int(prec) if prec else 6}f}"
        if len(s) >= w:
            return s
        if "-" in flags:
            return s.ljust(w)
        return s.rjust(w, "0") if "0" in flags and conv != "s" else s.rjust(w)

    while True:
        before = nxt
        out.append(_PRINTF_SPEC.sub(sub, fmt))
        if nxt == before or nxt >= len(args):
            break
    return "".join(out)


def _printf(ctx):
    if not ctx.args:
        raise ShellError("usage: printf format [arguments]", 2)
    ctx.stdout.write(printf_format(ctx.args[0], ctx.args[1:]))


def _register_shell_commands(shell) -> None:
    r = shell.register_command
    r("true", lambda ctx: 0, "Do nothing, successfully")
    r("false", lambda ctx: 1, "Do nothing, unsuccessfully")
    r("echo", _echo, "Print arguments")
    r("printf", _printf, "Format and print data")
    r("cd", _cd, "Change the working directory")
    r("pwd", lambda ctx: ctx.print(ctx.shell.cwd), "Print the working directory")
    r("export", _export, "Set environment variables")
    r("unset", _unset, "Remove variables or functions (-f)")
    r("env", lambda ctx: [ctx.print(f"{k}={ctx.shell.env[k]}") for k in sorted(ctx.shell.env)] and 0, "Print the environment")
    r("alias", _alias, "Define or list aliases")
    r("unalias", lambda ctx: [ctx.shell.aliases.pop(a, None) for a in ctx.args] and 0, "Remove aliases")
    r("history", lambda ctx: [ctx.print(f"{i + 1:5}  {h}") for i, h in enumerate(ctx.shell.history)] and 0, "Show command history")
    r("which", _which, "Locate a command")
    r("type", _type, "Describe a command")
    r("test", lambda ctx: 0 if evaluate_test(ctx, list(ctx.args)) else 1, "Evaluate a conditional expression")
    r("[", _bracket, "Evaluate a conditional expression")
    r("bash", _run_script, "Run a command string (-c) or script file")
    r("sh", _run_script, "Run a command string (-c) or script file")
    r("source", _run_script, "Run a script in the current shell")
    r(".", _run_script, "Run a script in the current shell")
    r("help", _help, "List available commands")


# ---------- fake system info ----------

def _uname(ctx):
    a = Args.parse(ctx.args, "asnrvmo", longs="all=a")
    every = a.has("a")
    parts = []
    if every or a.has("s") or not a.flags:
        parts.append("Linux")
    if every or a.has("n"):
        parts.append(ctx.shell.hostname)
    if every or a.has("r"):
        parts.append("6.8.0-generic")
    if every or a.has("v"):
        parts.append("#1 SMP PREEMPT_DYNAMIC")
    if every or a.has("m"):
        parts.append("x86_64")
    if every or a.has("o"):
        parts.append("GNU/Linux")
    ctx.print(" ".join(parts))


def _date(ctx):
    a = Args.parse(ctx.args, "uI", longs="utc=u,iso-8601=I")
    now = datetime.now(timezone.utc) if a.has("u") else datetime.now()
    fmt = next((p for p in a.positional if p.startswith("+")), None)
    if a.has("I"):
        ctx.print(now.strftime("%Y-%m-%d"))
    elif fmt:
        ctx.print(now.strftime(fmt[1:].replace("%s", str(int(now.timestamp())))))
    else:
        ctx.print(now.strftime("%a %b %-d %H:%M:%S %Y"))


def _register_system_commands(shell) -> None:
    r = shell.register_command
    r("whoami", lambda ctx: ctx.print(ctx.shell.user), "Print the current user")
    r("hostname", lambda ctx: ctx.print(ctx.shell.hostname), "Print the host name")
    r("uname", _uname, "Print system information")
    r("date", _date, "Print the date and time")
    r("ps", lambda ctx: ctx.stdout.write("    PID TTY          TIME CMD\n      1 pts/0    00:00:00 bash\n   4242 pts/0    00:00:00 ps\n"), "List processes")
