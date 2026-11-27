"""Built-in commands and the helpers they share."""
from __future__ import annotations

import re
from datetime import datetime, timezone

from .. import bytetext
from .._spec import COMMANDS, CommandSpec, DEFAULT_MESSAGES, ERRNO, GETOPT
from ..vfs import ShellError, UsageException


class Args:
    """Small getopt-style parser shared by the built-ins."""

    def __init__(self):
        self.flags: set[str] = set()
        self.values: dict[str, str] = {}
        self.every: dict[str, list[str]] = {}  # each value of a repeated option, in order
        self.positional: list[str] = []

    def has(self, c: str) -> bool:
        return c in self.flags

    def value(self, c: str) -> str | None:
        return self.values.get(c)

    def all(self, c: str) -> list[str]:
        return self.every.get(c, [])

    def put(self, c: str, v: str) -> None:
        self.values[c] = v
        self.every.setdefault(c, []).append(v)

    @staticmethod
    def parse(args, flags: "str | CommandSpec" = "", valued: str = "", longs: str = "") -> "Args":
        """flags: allowed boolean short flags ("rfi"); valued: short options taking a value ("n");
        longs: long aliases ("recursive=r,ignore-case=i"). Or flags is a command's spec entry: its option table."""
        if isinstance(flags, CommandSpec):
            if flags.options is None:
                raise ValueError(f"{flags.name} parses its own arguments")
            flags, valued, longs = flags.options
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
                    raise UsageException(spec_format(GETOPT["unrecognizedOption"], option=a))
                c = mapping[name]
                if c in valued:
                    if eq:
                        r.put(c, inline)
                    elif i + 1 < len(args):
                        i += 1
                        r.put(c, args[i])
                    else:
                        raise UsageException(spec_format(GETOPT["missingLongArgument"], option=name))
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
                        r.put(c, a[k + 1:])
                    elif i + 1 < len(args):
                        i += 1
                        r.put(c, args[i])
                    else:
                        raise UsageException(spec_format(GETOPT["missingShortArgument"], option=c))
                    break
                if c not in flags:
                    raise UsageException(spec_format(GETOPT["invalidOption"], option=c))
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


def standard_option(ctx, cmd, arg: str) -> bool:
    """`tool --version` / `tool --help` for built-ins, so probing them looks like a real system."""
    spec = COMMANDS.get(cmd.name)
    if arg not in ("--version", "--help") or spec is None or not spec.standard_options:
        return False
    if arg == "--version":
        ctx.print(spec.version)
    else:
        ctx.stdout.write(cmd.usage or f"Usage: {cmd.name} [OPTION]... [ARG]...\n{cmd.description}.\n")
    return True


def split_lines(text: str) -> list[str]:
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    return lines


def write_lines(w, lines) -> None:
    for line in lines:
        w.write(line + "\n")


def usage_error(cmd: str, message: str) -> str:
    """A bad command line the way GNU tools report one: the problem, then a pointer to --help."""
    spec = COMMANDS.get(cmd)
    shown, try_help = (spec.reports_as, spec.try_help) if spec else (cmd, f"Try '{cmd} --help' for more information.\n")
    return (f"{shown}: {message}\n" if message else "") + try_help


def cannot_read(cmd: str, f: str, why: str) -> str:
    """The message after "NAME: " when an input file can't be read, worded the way each GNU tool words it."""
    isdir = why == "Is a directory"
    return spec_message(cmd, "readDirectory" if isdir and "readDirectory" in COMMANDS[cmd].messages else "openFailed", file=f, errno=why)


_PLACEHOLDER = re.compile(r"\{([A-Za-z]\w*)(?::([\w-]+))?\}")


def spec_format(template: str, /, **values: str) -> str:
    """Fills a spec template: {x} as given, {x:shell} / {x:shell-always} shell-quoted, {x:locale} in ‘curly quotes’."""
    def one(m):
        v = values.get(m.group(1))
        if v is None:
            return m.group(0)
        how = m.group(2)
        return quote(v) if how == "shell" else quote_always(v) if how == "shell-always" else quote_l(v) if how == "locale" else v
    return _PLACEHOLDER.sub(one, template)


def spec_message(command: str, key: str, /, **values: str) -> str:
    """The command's message `key` from spec/ (or the shared default), filled in."""
    spec = COMMANDS.get(command)
    return spec_format(spec.messages[key] if spec and key in spec.messages else DEFAULT_MESSAGES[key], **values)


def spec_sub_message(command: str, sub: str, key: str, /, **values: str) -> str:
    """A subcommand's message `key` (else the command's, else the shared default), filled in."""
    s = COMMANDS[command].subcommands.get(sub)
    return spec_format(s.messages[key], **values) if s and key in s.messages else spec_message(command, key, **values)


def quote(s: str) -> str:
    """A file name in a GNU message: as is when plain, else shell-quoted like quote_always."""
    plain = s and s[0] not in "~#" and not any(c < " " or c == "\x7f" or c in " !\"$&'()*;<>?[\\^`|=:" for c in s)
    return s if plain else quote_always(s)


def quote_always(s: str) -> str:
    """A file name shell-quoted even when plain: 'nope', "it's", 'a'$'\\t''b'."""
    if any(c < " " or c == "\x7f" for c in s):
        # each control character becomes its own $'\t' piece between quoted runs
        out = []
        for run in re.split("([\x00-\x1f\x7f])", s):
            if len(run) == 1 and (run < " " or run == "\x7f"):
                out.append("$'\\" + {"\t": "t", "\n": "n", "\r": "r"}.get(run, f"{ord(run):03o}") + "'")
            elif run:
                out.append(quote_always(run))
        return "".join(out)
    if "'" in s and not any(c in "\"$`\\!" for c in s):
        return f'"{s}"'
    return "'" + s.replace("'", "'\\''") + "'"


def quote_l(s: str) -> str:
    """A value quoted the way GNU tools quote one in a UTF-8 locale: ‘x’."""
    return f"‘{s}’"


def inputs(ctx, files, on_error, raw: bool = False, quiet: bool = False):
    """Yields (name, text) for each file argument (or stdin when there are none / "-"), reporting missing files
    (unless quiet). With raw, yields the bytes instead (exact for binary files)."""
    stdin = bytetext.encode(ctx.stdin) if raw else ctx.stdin
    if not files:
        yield "(standard input)", stdin
        return
    for f in files:
        if f == "-":
            yield "(standard input)", stdin
            continue
        try:
            text = (ctx.fs.read_bytes if raw else ctx.fs.read_pipe_text)(ctx.resolve(f))
        except ShellError as e:
            if not quiet:
                ctx.stderr.write(f"{ctx.name}: {cannot_read(ctx.name, f, str(e).rsplit(': ', 1)[-1])}\n")
            on_error()
            continue
        yield f, text


_ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "\\": "\\", "a": "\a", "b": "\b", "f": "\f", "v": "\v", "e": "\x1b", '"': '"'}


def _byte_char(b: int) -> str:
    """A byte value as a character: ASCII as is, 0x80-0xff as the escaped raw byte (bytetext), so `printf '\\xd5'` writes one byte."""
    return chr(b) if b < 0x80 else chr(0xDC00 + b)


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
            out.append(_byte_char(int(s[j:k] or "0", 8) & 0xFF))
            i = k
        elif n == "x" and i + 2 < len(s) and s[i + 2] in "0123456789abcdefABCDEF":
            k = i + 2
            while k < len(s) and k < i + 4 and s[k] in "0123456789abcdefABCDEF":
                k += 1
            out.append(_byte_char(int(s[i + 2:k], 16)))
            i = k
        else:
            out.append(_ESCAPES.get(n, ("\\" + n) if keep_unknown else n))
            i += 2
    return bytetext.rejoin("".join(out))


def register_all(shell) -> None:
    from . import archive, awk, binary, documents, files, gitrevisions, jq, paths, process, scripting, system, text, variables, web

    scripting.register(shell)
    variables.register(shell)
    process.register(shell)
    system.register(shell)
    paths.register(shell)
    files.register(shell)
    text.register(shell)
    awk.register(shell)
    jq.register(shell)
    binary.register(shell)
    archive.register(shell)
    documents.register(shell)
    web.register(shell)
    gitrevisions.register(shell)


def exec_failure(sh, name: str) -> tuple[str, int] | None:
    """Why a program-running command (env, timeout) could not start `name`: (errno text, exit status), or None."""
    if "/" not in name:
        # an app command with no program file runs; a shell-only builtin (cd, type) is no program at all
        spec = COMMANDS.get(name)
        if sh.search_path(name) is not None or sh.is_unfiled(name) and not (spec is not None and not spec.program_file):
            return None
        return ERRNO["ENOENT"], 127
    path = sh.resolve(name)
    if not sh.fs.exists(path):
        return ERRNO["ENOENT"], 127
    if sh.fs.is_dir(path) or not sh.fs.can_execute(path):
        return ERRNO["EACCES"], 126
    return None


_PRINTF_SPEC = re.compile(r"%([-0 +#]*)(\*|\d*)(?:\.(\*|\d*))?(?:\(([^)]*)\))?([sdiufxXoceEgGbqT%])")


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
    return re.sub(r"([^A-Za-z0-9_./+:,=@%^\x80-\U0010ffff-])", r"\\\1", s)


def printf_format(fmt: str, args: list[str], errors: list[str] | None = None) -> str:
    """bash printf: C conversions, the format is reused while arguments remain; bad numbers are reported in errors."""
    fmt = unescape(fmt, keep_unknown=True)
    out = []
    nxt = 0
    stop = False  # a \c in a %b argument ends all output, the rest of the format included
    mark = "\ue001"

    def take() -> str:
        nonlocal nxt
        arg = args[nxt] if nxt < len(args) else ""
        nxt += 1
        return arg

    def sub(m: re.Match) -> str:
        nonlocal stop
        flags, width, prec, tfmt, conv = m.group(1), m.group(2), m.group(3), m.group(4), m.group(5)
        if conv == "%":
            return "%"
        if conv == "u":
            conv = "d"
        if (tfmt is not None) != (conv == "T"):
            return m.group(0)
        if width == "*":
            w, _ = _c_integer(take())
            flags, width = (flags + "-", str(-w)) if w < 0 else (flags, str(w))
        if prec == "*":
            prec = str(max(0, _c_integer(take())[0]))
        # a huge width or precision is capped, as the C# port does, so `printf %0999999999d` can't exhaust memory
        width = str(min(int(width), 10_000)) if width else width
        prec = str(min(int(prec), 10_000)) if prec else prec
        arg = take()
        spec = "%" + flags + width + ("." + (prec or "0") if prec is not None else "")
        if conv in "sbqcT" and stop:
            return ""
        if conv == "b" and "\\c" in arg:
            stop = True
            return unescape(arg[:arg.index("\\c")], keep_unknown=True, echo=True) + mark
        if conv == "T":
            # %(fmt)T: a time in seconds since the epoch; -1 or nothing is now (UTC, like the rest of this machine)
            from datetime import datetime, timedelta, timezone
            when = datetime.now(timezone.utc) if arg in ("", "-1", "-2") else datetime(1970, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=_c_integer(arg)[0])
            return (spec + "s") % strftime(when.replace(tzinfo=None), tfmt)
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
        if stop:
            out[-1] = out[-1][:out[-1].index(mark)]
            break
        if nxt == before or nxt >= len(args):
            break
    return "".join(out)


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
        # %:z +hh:mm, %::z +hh:mm:ss, %:::z +hh (the clock is UTC)
        colons = 0
        while i + 1 + colons < len(fmt) and fmt[i + 1 + colons] == ":":
            colons += 1
        if 0 < colons <= 3 and i + 1 + colons < len(fmt) and fmt[i + 1 + colons] == "z":
            out.append({1: "+00:00", 2: "+00:00:00"}.get(colons, "+00"))
            i += colons + 2
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
            "B": _MONTHS[t.month - 1], "p": "AM" if t.hour < 12 else "PM", "s": str(int((t if t.tzinfo else t.replace(tzinfo=timezone.utc)).timestamp())),
            "N": f"{t.microsecond * 1000:09}", "Z": "UTC", "z": "+0000", "n": "\n", "t": "\t", "%": "%",
        }.get(d, "%" + ("-" if no_pad else "") + d))
    return "".join(out)
