"""Built-in commands and the helpers they share."""
from __future__ import annotations

import re

from .. import bytetext
from .._spec import COMMANDS, CommandSpec, DEFAULT_MESSAGES, ERRNO, GETOPT
from ..vfs import ShellError, UsageException
from ..format import (  # noqa: F401  the engine moved to topwater.format; these names stay importable from here
    _DAYS, _ESCAPES, _MONTHS, _PRINTF_SPEC, _SHELL_SAFE, _byte_char, _c_float, _c_integer, printf_format, shell_quote, strftime,
    unescape,
)


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


class _Errors:
    """An `on_error` callback for `inputs` that remembers whether any input failed."""

    def __init__(self):
        self.hit = False

    def __call__(self):
        self.hit = True


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
