"""Built-in commands and the helpers they share."""
from __future__ import annotations

import re
from datetime import datetime, timedelta, timezone

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
        out: list[str] = []
        for a in args:
            if len(a) > 1 and a[0] == "-" and a[1:].isdigit() and not (out and out[-1] in ("-n", "-c", "--lines", "--bytes")):
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
_NO_STANDARD_OPTIONS = {"sudo", "su", "echo", "printf", "test", "[", "true", "false", ":", "awk", "help", "exit", "return", "break", "continue",
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


def inputs(ctx, files, on_error, raw: bool = False, quiet: bool = False):
    """Yields (name, text) for each file argument (or stdin when there are none / "-"), reporting missing files
    (unless quiet). With raw, yields the bytes instead (exact for binary files)."""
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
            if not quiet:
                ctx.stderr.write(f"{ctx.name}: {f}: {str(e).rsplit(': ', 1)[-1]}\n")
            on_error()
            continue
        yield f, text


_ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "\\": "\\", "a": "\a", "b": "\b", "f": "\f", "v": "\v", "e": "\x1b", '"': '"'}


def unescape(s: str, keep_unknown: bool = False, echo: bool = False) -> str:
    """Backslash escapes: \\n \\t ..., \\xHH, and octal (\\NNN, or \\0NNN for echo -e)."""
    out = []
    i = 0
    while i < len(s):
        if s[i] != "\\" or i + 1 >= len(s):
            out.append(s[i])
            i += 1
            continue
        n = s[i + 1]
        if n in "01234567" and (not echo or n == "0"):
            j = i + 2 if echo else i + 1
            k = j
            while k < len(s) and k < j + 3 and s[k] in "01234567":
                k += 1
            out.append(chr(int(s[j:k] or "0", 8) & 0xFF))
            i = k
        elif n == "x" and i + 2 < len(s) and s[i + 2] in "0123456789abcdefABCDEF":
            k = i + 2
            while k < len(s) and k < i + 4 and s[k] in "0123456789abcdefABCDEF":
                k += 1
            out.append(chr(int(s[i + 2:k], 16)))
            i = k
        else:
            out.append(_ESCAPES.get(n, ("\\" + n) if keep_unknown else n))
            i += 2
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
    if escapes and "\\c" in text:  # \c: print nothing more, not even the newline
        text, newline = text[:text.index("\\c")], False
    ctx.stdout.write((unescape(text, keep_unknown=True, echo=True) if escapes else text) + ("\n" if newline else ""))


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


# commands that are part of bash itself rather than programs in /usr/bin (for `type` and `command -v`)
SHELL_BUILTINS = {
    "cd", "echo", "printf", "read", "export", "unset", "alias", "unalias", "set", "shift", "local", "declare", "typeset",
    "eval", "exit", "return", "break", "continue", "source", ".", "test", "[", "true", "false", "pwd", "type", "command",
    "let", "history", "help", "exec", "wait", "trap", "umask", "readonly", "getopts", "hash", "jobs", "kill", "shopt", ":",
}


def is_shell_builtin(shell, name: str) -> bool:
    return name in SHELL_BUILTINS and name in shell.commands and shell._builtin_names is not None and name in shell._builtin_names


def _type(ctx):
    code = 0
    opts = Args.parse(ctx.args, "atpPf")
    for a in opts.positional:
        sh = ctx.shell
        if opts.has("t") or opts.has("p") or opts.has("P"):
            kind = ("alias" if a in sh.aliases else "function" if a in sh.functions else "builtin" if is_shell_builtin(sh, a)
                    else "file" if a in sh.commands else None)
            if kind is None:
                code = 1
            elif opts.has("t"):
                ctx.print(kind)
            elif kind == "file" or (opts.has("P") and a in sh.commands):
                ctx.print(f"/usr/bin/{a}")
            continue
        if a in ctx.shell.aliases:
            ctx.print(f"{a} is aliased to `{ctx.shell.aliases[a]}'")
        elif a in ctx.shell.functions:
            ctx.print(f"{a} is a function")
        elif is_shell_builtin(sh, a):
            ctx.print(f"{a} is a shell builtin")
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


_TEST_BINARY = {"=", "==", "!=", "<", ">", "-eq", "-ne", "-lt", "-le", "-gt", "-ge", "-nt", "-ot"}


def _test_binary(left: str, op: str, right: str) -> bool:
    if op in ("=", "=="):
        return left == right
    if op == "!=":
        return left != right
    if op == "<":
        return left < right
    if op == ">":
        return left > right
    if op in ("-nt", "-ot"):
        return False
    try:
        l, r = int(left.strip()), int(right.strip())
    except ValueError:
        raise ShellError(f"{left if not _is_int(left) else right}: integer expression expected", 2) from None
    return {"-eq": l == r, "-ne": l != r, "-lt": l < r, "-le": l <= r, "-gt": l > r, "-ge": l >= r}[op]


def _is_int(s: str) -> bool:
    try:
        int(s.strip())
        return True
    except ValueError:
        return False


def evaluate_test(ctx, a: list[str]) -> bool:
    """test / [ ]: POSIX rules for up to 3 arguments, then ! ( ) -a -o with the usual precedence."""
    n = len(a)
    if n == 0:
        return False
    if n == 1:
        return a[0] != ""
    if n == 2:
        if a[0] == "!":
            return a[1] == ""
        return test_unary(ctx.shell, a[0], a[1])
    if n == 3:
        if a[1] in _TEST_BINARY:
            return _test_binary(a[0], a[1], a[2])
        if a[1] in ("-a", "-o"):
            return (a[0] != "" and a[2] != "") if a[1] == "-a" else (a[0] != "" or a[2] != "")
        if a[0] == "!":
            return not evaluate_test(ctx, a[1:])
        if a[0] == "(" and a[2] == ")":
            return a[1] != ""
        raise ShellError(f"{a[1]}: binary operator expected", 2)
    if n == 4 and a[0] == "!":
        return not evaluate_test(ctx, a[1:])
    pos = 0

    def peek() -> str | None:
        return a[pos] if pos < n else None

    def or_() -> bool:
        nonlocal pos
        v = and_()
        while peek() == "-o":
            pos += 1
            v = and_() or v
        return v

    def and_() -> bool:
        nonlocal pos
        v = not_()
        while peek() == "-a":
            pos += 1
            v = not_() and v
        return v

    def not_() -> bool:
        nonlocal pos
        if peek() == "!":
            pos += 1
            return not not_()
        return primary()

    def primary() -> bool:
        nonlocal pos
        t = peek()
        if t is None:
            raise ShellError("argument expected", 2)
        if t == "(":
            pos += 1
            v = or_()
            if peek() != ")":
                raise ShellError("`)' expected", 2)
            pos += 1
            return v
        if pos + 2 < n and a[pos + 1] in _TEST_BINARY:
            pos += 3
            return _test_binary(t, a[pos - 2], a[pos - 1])
        if t.startswith("-") and len(t) == 2 and pos + 1 < n:
            pos += 2
            return test_unary(ctx.shell, t, a[pos - 1])
        pos += 1
        return t != ""

    v = or_()
    if pos < n:
        raise ShellError("too many arguments", 2)
    return v


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


_PRINTF_SPEC = re.compile(r"%([-0 +#]*)(\*|\d*)(?:\.(\*|\d*))?([sdifxXoceEgGbq%])")
_SHELL_SAFE = re.compile(r"[A-Za-z0-9_./+:,=@%^-]+")


def _c_integer(s: str) -> tuple[int, bool]:
    """Parses a printf integer argument like bash: 0x1f, 017 (octal), 'c (character code). Returns (value, valid)."""
    t = s.strip()
    if t[:1] in ("'", '"'):
        return (ord(t[1]) if len(t) > 1 else 0), True
    m = re.match(r"[+-]?(0[xX][0-9a-fA-F]+|0[0-7]*|[1-9][0-9]*)", t)
    if not m:
        return 0, t == ""
    body = m.group(1)
    v = int(body, 16) if body[:2].lower() == "0x" else int(body, 8) if body.startswith("0") else int(body)
    return (-v if t.startswith("-") else v), m.end() == len(t)


def _c_float(s: str) -> tuple[float, bool]:
    t = s.strip()
    m = re.match(r"[+-]?(\d+\.?\d*([eE][+-]?\d+)?|\.\d+([eE][+-]?\d+)?|inf|nan)", t, re.I)
    if not m:
        return 0.0, t == ""
    return float(m.group(0)), m.end() == len(t)


def shell_quote(s: str) -> str:
    """printf %q: quotes a string so bash would read it back as one word."""
    if s == "":
        return "''"
    if _SHELL_SAFE.fullmatch(s):
        return s
    if any(ord(c) < 32 or ord(c) == 127 for c in s):
        body = s.replace("\\", "\\\\").replace("'", "\\'")
        for c, e in (("\n", "\\n"), ("\t", "\\t"), ("\r", "\\r"), ("\x1b", "\\E")):
            body = body.replace(c, e)
        return "$'" + re.sub(r"[\x00-\x1f\x7f]", lambda m: f"\\{ord(m.group(0)):03o}", body) + "'"
    return re.sub(r"([^A-Za-z0-9_./+:,=@%^-])", r"\\\1", s)


def printf_format(fmt: str, args: list[str], errors: list[str] | None = None) -> str:
    """bash printf: C conversions, the format is reused while arguments remain; bad numbers are reported in errors."""
    fmt = unescape(fmt, keep_unknown=True)
    out = []
    nxt = 0

    def take() -> str:
        nonlocal nxt
        arg = args[nxt] if nxt < len(args) else ""
        nxt += 1
        return arg

    def sub(m: re.Match) -> str:
        flags, width, prec, conv = m.group(1), m.group(2), m.group(3), m.group(4)
        if conv == "%":
            return "%"
        if width == "*":
            w, _ = _c_integer(take())
            flags, width = (flags + "-", str(-w)) if w < 0 else (flags, str(w))
        if prec == "*":
            prec = str(max(0, _c_integer(take())[0]))
        arg = take()
        spec = "%" + flags + width + ("." + (prec or "0") if prec is not None else "")
        if conv in "sbq":
            text = unescape(arg, keep_unknown=True, echo=True) if conv == "b" else shell_quote(arg) if conv == "q" else arg
            return (spec + "s") % text
        if conv == "c":
            return (spec + "s") % arg[:1]
        if conv in "dioxX":
            v, ok = _c_integer(arg)
            if not ok and errors is not None:
                errors.append(f"printf: {arg}: invalid number")
            if conv == "o" and "#" in flags:  # C's %#o is a leading 0, Python's is 0o
                return (spec.replace("#", "") + "s") % ("0" + format(v, "o") if v else "0")
            return (spec + conv.replace("i", "d")) % v
        v, ok = _c_float(arg)
        if not ok and errors is not None:
            errors.append(f"printf: {arg}: invalid number")
        return (spec + conv) % v

    while True:
        before = nxt
        out.append(_PRINTF_SPEC.sub(sub, fmt))
        if nxt == before or nxt >= len(args):
            break
    return "".join(out)


def _printf(ctx):
    args = list(ctx.args)
    if args and args[0] == "--":
        args.pop(0)
    if not args:
        raise ShellError("usage: printf format [arguments]", 2)
    errors: list[str] = []
    ctx.stdout.write(printf_format(args[0], args[1:], errors))
    for e in errors:
        ctx.error(e)
    return 1 if errors else 0


def _register_shell_commands(shell) -> None:
    r = shell.register_command
    r("true", lambda ctx: 0, "Do nothing, successfully")
    r(":", lambda ctx: 0, "Do nothing, successfully (the null command)")
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


_DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]
_MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November",
           "December"]


def strftime(t: datetime, fmt: str) -> str:
    """GNU date's format directives (the common ones, with the - flag for no padding). Mirrored in C#."""
    out = []
    i = 0
    while i < len(fmt):
        c = fmt[i]
        if c != "%" or i + 1 >= len(fmt):
            out.append(c)
            i += 1
            continue
        no_pad = fmt[i + 1] == "-" and i + 2 < len(fmt)
        d = fmt[i + 2] if no_pad else fmt[i + 1]
        i += 3 if no_pad else 2
        hour12 = t.hour % 12 or 12
        num = {"Y": (t.year, 4), "m": (t.month, 2), "d": (t.day, 2), "e": (t.day, -2), "H": (t.hour, 2), "k": (t.hour, -2),
               "I": (hour12, 2), "l": (hour12, -2), "M": (t.minute, 2), "S": (t.second, 2), "y": (t.year % 100, 2),
               "j": (t.timetuple().tm_yday, 3), "u": (t.isoweekday(), 1), "w": (t.isoweekday() % 7, 1)}
        if d == "c":
            out.append(strftime(t, "%a %b %e %H:%M:%S %Y"))
            continue
        if d in num:
            v, width = num[d]
            out.append(str(v) if no_pad else str(v).rjust(abs(width), "0" if width > 0 else " "))
            continue
        out.append({
            "F": f"{t.year:04}-{t.month:02}-{t.day:02}", "T": f"{t.hour:02}:{t.minute:02}:{t.second:02}",
            "D": f"{t.month:02}/{t.day:02}/{t.year % 100:02}", "R": f"{t.hour:02}:{t.minute:02}",
            "a": _DAYS[t.weekday()][:3], "A": _DAYS[t.weekday()], "b": _MONTHS[t.month - 1][:3], "h": _MONTHS[t.month - 1][:3],
            "B": _MONTHS[t.month - 1], "p": "AM" if t.hour < 12 else "PM", "s": str(int(t.timestamp())),
            "N": f"{t.microsecond * 1000:09}", "Z": "UTC", "z": "+0000", "n": "\n", "t": "\t", "%": "%",
        }.get(d, "%" + ("-" if no_pad else "") + d))
    return "".join(out)


_UNITS = {"second": 1, "sec": 1, "minute": 60, "min": 60, "hour": 3600, "day": 86400, "week": 604800, "fortnight": 1209600}


def parse_date(text: str, now: datetime) -> datetime:
    """date -d: @EPOCH, YYYY-MM-DD[ HH:MM[:SS]], now/today/yesterday/tomorrow, and "[+-]N unit[s] [ago]" offsets."""
    s = text.strip().lower()
    m = re.fullmatch(r"@(-?\d+(?:\.\d+)?)", s)
    if m:
        return datetime.fromtimestamp(float(m.group(1)), timezone.utc)
    base = now
    m = re.match(r"(\d{4})-(\d{1,2})-(\d{1,2})(?:[t ](\d{1,2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?)?(z|utc|[+-]00:?00)?", s)
    if m:
        try:
            base = datetime(int(m.group(1)), int(m.group(2)), int(m.group(3)), int(m.group(4) or 0), int(m.group(5) or 0),
                            int(m.group(6) or 0), tzinfo=timezone.utc)
        except ValueError:
            raise ShellError(f"invalid date '{text}'") from None
        s = s[m.end():].strip()
    else:
        for word, days in (("yesterday", -1), ("tomorrow", 1), ("today", 0), ("now", 0)):
            if s.startswith(word):
                base = now + timedelta(days=days)
                s = s[len(word):].strip()
                break
    for m in re.finditer(r"(next|last|[+-]?\s*\d+)?\s*(second|sec|minute|min|hour|day|week|fortnight|month|year)s?(\s+ago)?|(\S+)", s):
        if m.group(4) or not m.group(2):
            raise ShellError(f"invalid date '{text}'")
        word = (m.group(1) or "1").replace(" ", "")
        n = 1 if word == "next" else -1 if word == "last" else int(word)
        if m.group(3):
            n = -n
        unit = m.group(2)
        if unit in ("month", "year"):
            months = base.month - 1 + n * (12 if unit == "year" else 1)
            # like GNU date, a day past the end of the month rolls over (Jan 31 + 1 month = Mar 2 or 3)
            base = base.replace(year=base.year + months // 12, month=months % 12 + 1, day=1) + timedelta(days=base.day - 1)
        else:
            base = base + timedelta(seconds=n * _UNITS[unit])
    return base


def _date(ctx):
    # the simulated machine's clock is UTC (TZ is unset), so output never depends on the host's time zone
    a = Args.parse(ctx.args, "uIR", "dr", "utc=u,universal=u,iso-8601=I,date=d,reference=r,rfc-email=R")
    now = datetime.now(timezone.utc)
    if a.value("d") is not None:
        now = parse_date(a.value("d"), now)
    elif a.value("r") is not None:
        now = ctx.fs.stat(ctx.resolve(a.value("r"))).modified.astimezone(timezone.utc)
    fmt = next((p for p in a.positional if p.startswith("+")), None)
    if a.has("I"):
        ctx.print(strftime(now, "%Y-%m-%d"))
    elif a.has("R"):
        ctx.print(strftime(now, "%a, %d %b %Y %H:%M:%S +0000"))
    else:
        ctx.print(strftime(now, fmt[1:] if fmt else "%a %b %e %H:%M:%S %Z %Y"))


def _register_system_commands(shell) -> None:
    r = shell.register_command
    r("whoami", lambda ctx: ctx.print(ctx.shell.user), "Print the current user")
    r("hostname", lambda ctx: ctx.print(ctx.shell.hostname), "Print the host name")
    r("uname", _uname, "Print system information")
    r("date", _date, "Print the date and time")
    r("ps", lambda ctx: ctx.stdout.write("    PID TTY          TIME CMD\n      1 pts/0    00:00:00 bash\n   4242 pts/0    00:00:00 ps\n"), "List processes")
