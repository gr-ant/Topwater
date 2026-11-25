"""Built-in commands and the helpers they share."""
from __future__ import annotations

import re
from dataclasses import replace
from datetime import datetime, timedelta, timezone

from .. import bytetext
from ..parser import is_name
from ..vfs import ShellError, UsageException, normalize


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
                    raise UsageException(f"unrecognized option '{a}'")
                c = mapping[name]
                if c in valued:
                    if eq:
                        r.values[c] = inline
                    elif i + 1 < len(args):
                        i += 1
                        r.values[c] = args[i]
                    else:
                        raise UsageException(f"option '--{name}' requires an argument")
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
                        raise UsageException(f"option requires an argument -- '{c}'")
                    break
                if c not in flags:
                    raise UsageException(f"invalid option -- '{c}'")
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
    "egrep": "grep (GNU grep) 3.11", "fgrep": "grep (GNU grep) 3.11",
    "tar": "tar (GNU tar) 1.35", "gzip": "gzip 1.12", "gunzip": "gzip 1.12", "zcat": "gzip 1.12",
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
    head = f"{cmd}: {message}\n" if message else ""
    if cmd == "grep":
        return head + "Usage: grep [OPTION]... PATTERNS [FILE]...\nTry 'grep --help' for more information.\n"
    if cmd == "diff":
        return head + "diff: Try 'diff --help' for more information.\n"
    return head + f"Try '{cmd} --help' for more information.\n"


def cannot_read(cmd: str, f: str, why: str) -> str:
    """The message after "NAME: " when an input file can't be read, worded the way each GNU tool words it."""
    isdir = why == "Is a directory"
    if cmd == "sed":
        return f"read error on {f}: {why}" if isdir else f"can't read {f}: {why}"
    if cmd in ("head", "tail"):
        return f"error reading {quote_always(f)}: {why}" if isdir else f"cannot open {quote_always(f)} for reading: {why}"
    if cmd == "uniq" and isdir:
        return f"error reading {quote_always(f)}: {why}"
    if cmd == "tac":
        return f"{quote(f)}: read error: Invalid argument" if isdir else f"failed to open {quote_always(f)} for reading: {why}"
    if cmd == "sort":
        return f"read failed: {quote(f)}: {why}" if isdir else f"cannot read: {quote(f)}: {why}"
    if cmd == "base64" and isdir:
        return f"read error: {why}"
    if cmd in ("grep", "egrep", "fgrep"):
        return f"{f}: {why}"  # grep prints the name raw
    return f"{quote(f)}: {why}"


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
    from . import archive, awk, binary, documents, files, jq, more, text

    files.register(shell)
    text.register(shell)
    _register_shell_commands(shell)
    _register_system_commands(shell)
    jq.register(shell)
    more.register(shell)
    awk.register(shell)
    documents.register(shell)
    binary.register(shell)
    archive.register(shell)


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
    args = list(ctx.args)
    flags = []
    while args and args[0] in ("-L", "-P", "-e", "-@"):
        flags.append(args.pop(0))
    if args and args[0] == "--":
        args.pop(0)
    if len(args) > 1:
        raise ShellError("too many arguments")
    if not args:
        target = env.get("HOME", "/")
    elif args[0] == "-":
        target = env.get("OLDPWD", ctx.shell.cwd)
    else:
        target = args[0]
    # like bash: the new directory is the old one's logical path plus the operand, ".." removing a name (-L, the default);
    # if that isn't a directory, or with -P, the physical path, every symbolic link resolved
    physical = [f for f in flags if f in ("-L", "-P")][-1:] == ["-P"]
    path = normalize(ctx.shell.cwd, target)
    if physical or not ctx.fs.is_dir(path):
        path = ctx.resolve(target)
    if not ctx.fs.exists(path):
        raise ShellError(f"{target}: {ctx.fs.why_missing(path)}")
    if not ctx.fs.is_dir(path):
        raise ShellError(f"{target}: Not a directory")
    if not ctx.fs.can_execute(path):
        raise ShellError(f"{target}: Permission denied")
    if physical:
        path = ctx.fs.real_path(path)[0] or path
    env["OLDPWD"] = ctx.shell.cwd
    ctx.shell.cwd = path
    if args == ["-"]:
        ctx.print(path)


def _pwd(ctx):
    # -P: the physical path, symbolic links resolved; -L (the default): the logical one cd kept
    physical = [x for x in ctx.args if x in ("-L", "-P")][-1:] == ["-P"]
    ctx.print((ctx.fs.real_path(ctx.shell.cwd)[0] if physical else None) or ctx.shell.cwd)


def _flag_args(args) -> tuple[str, list[str]]:
    """Leading -x style arguments joined, and the rest."""
    i = 0
    while i < len(args) and args[i].startswith("-"):
        i += 1
    return "".join(args[:i]), list(args[i:])


def _export(ctx):
    from ..parser import is_name
    from .more import declare_line
    sh = ctx.shell
    flags, args = _flag_args(ctx.args)
    remove = "n" in flags
    if "f" in flags:
        # export -f: functions a child bash also gets
        for f in args:
            if remove:
                sh.exported_functions.discard(f)
            elif f in sh.functions:
                sh.exported_functions.add(f)
        return 0
    if not args:
        for k in sorted(set(sh.env) | set(sh.arrays)):
            if sh.is_exported(k):
                line = declare_line(sh, k)
                if line is not None:
                    ctx.print(line)
        return 0
    code = 0
    for a in args:
        eq = a.find("=")
        append = eq > 0 and a[eq - 1] == "+"
        name = a[:eq - 1 if append else eq] if eq > 0 else a
        try:
            if not is_name(name):
                raise ShellError(f"`{a}': not a valid identifier")
            if eq > 0:
                sh.set_var(name, a[eq + 1:], append)
            else:
                target = sh.resolve_name(name)
                if target not in sh.arrays and target not in sh.env:
                    sh.mark_shell_variable(target)
                    sh.env[target] = ""
            if remove:
                sh.unexport(sh.resolve_name(name))
            else:
                sh.set_attrs(sh.resolve_name(name), "x")
        except ShellError as e:
            ctx.error(f"{ctx.shell.err_prefix}export: {e}")
            code = 1
    return code


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
            ctx.error(f"{ctx.shell.err_prefix}alias: {k}: not found")
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
    "pushd", "popd", "dirs", "mapfile", "readarray", "ulimit", "builtin", "caller", "times", "disown",
}


def is_shell_builtin(shell, name: str) -> bool:
    return name in SHELL_BUILTINS and name in shell.commands and shell._builtin_names is not None and name in shell._builtin_names


def _type(ctx):
    from .more import SHELL_KEYWORDS, describe
    code = 0
    opts = Args.parse(ctx.args, "atpPf")
    for a in opts.positional:
        sh = ctx.shell
        if opts.has("t") or opts.has("p") or opts.has("P"):
            kind = ("alias" if a in sh.aliases else "keyword" if a in SHELL_KEYWORDS else "function" if a in sh.functions
                    else "builtin" if is_shell_builtin(sh, a) else "file" if a in sh.commands else None)
            if kind is None:
                code = 1
            elif opts.has("t"):
                ctx.print(kind)
            elif kind == "file" or (opts.has("P") and a in sh.commands):
                ctx.print(f"/usr/bin/{a}")
            continue
        if not describe(ctx, a):
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
    if op in ("-L", "-h"):
        return fs.is_symlink(path)
    if op in ("-b", "-c", "-p", "-S", "-t", "-g", "-u", "-k"):
        return False  # no devices, pipes, sockets, terminals or special bits
    if op in ("-O", "-G", "-N"):
        return fs.exists(path)
    if op == "-v":
        return shell.is_variable_set(arg)
    if op == "-o":
        return any(n == arg and on for n, on in shell.option_list)
    raise ShellError(f"{op}: unary operator expected", 2)


def newer(sh, left: str, right: str) -> bool:
    """-nt / -ot: a missing file is older than any existing one, as in bash."""
    def mtime(p: str):
        path = sh.resolve(p)
        return sh.fs.stat(path).modified if sh.fs.exists(path) else None

    lt, rt = mtime(left), mtime(right)
    return lt is not None and (rt is None or lt > rt)


_TEST_BINARY = {"=", "==", "!=", "<", ">", "-eq", "-ne", "-lt", "-le", "-gt", "-ge", "-nt", "-ot", "-ef"}


def _test_binary(sh, left: str, op: str, right: str) -> bool:
    if op in ("=", "=="):
        return left == right
    if op == "!=":
        return left != right
    if op == "<":
        return left < right
    if op == ">":
        return left > right
    if op == "-nt":
        return newer(sh, left, right)
    if op == "-ot":
        return newer(sh, right, left)
    if op == "-ef":
        return sh.fs.same_file(sh.resolve(left), sh.resolve(right))
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
            return _test_binary(ctx.shell, a[0], a[1], a[2])
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
            return _test_binary(ctx.shell, t, a[pos - 2], a[pos - 1])
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


_BASH_LETTERS = {"e": "errexit", "u": "nounset", "f": "noglob", "x": "xtrace", "C": "noclobber"}


async def _run_script(ctx):
    from ..shell import ReturnSignal, StdinBuffer, _NewProcess

    io_ = ctx.io
    sh = ctx.shell
    args = list(ctx.args)
    is_bash = ctx.name in ("bash", "sh")
    command = False
    # bash's own options: -c, and -e -u -x -o pipefail ... which the new shell starts with
    options: list[tuple[str, bool]] = []
    i = 0
    while is_bash and i < len(args) and len(args[i]) > 1 and args[i][0] in "-+":
        f = args[i]
        if f == "--":
            i += 1
            break
        if not f.startswith("--"):  # --norc, --noprofile, --login are skipped
            for c in f[1:]:
                if c == "c":
                    command = True
                elif c == "o" and i + 1 < len(args):
                    i += 1
                    options.append((args[i], f[0] == "-"))
                elif c in _BASH_LETTERS:
                    options.append((_BASH_LETTERS[c], f[0] == "-"))
        i += 1
    args = args[i:]

    def setup():
        for name, on in options:
            sh.set_option(name, on)

    if command:
        if not args:
            raise ShellError("-c: option requires an argument", 2)
        return await sh._run_script_text(args[0], io_, subshell=True, positional=args[2:],
                                         process=_NewProcess(args[1] if len(args) > 1 else ctx.name, None, setup))
    if not args:
        if not is_bash:
            raise ShellError("filename argument required", 2)
        return await sh._run_script_text(ctx.stdin, replace(io_, stdin=StdinBuffer()), subshell=True, positional=[],
                                         process=_NewProcess(ctx.name, None, setup))
    try:
        text = ctx.fs.read_text(ctx.resolve(args[0]))
    except ShellError as e:
        # a new bash reports its missing script itself, so without a line; source reports it at its own line
        ctx.error(f"{'bash: ' if is_bash else ctx.shell.err_prefix}{args[0]}: {str(e).rsplit(': ', 1)[-1]}")
        return 127 if is_bash else 1
    if is_bash:
        return await sh._run_script_text(text, io_, subshell=True, positional=args[1:], process=_NewProcess(args[0], args[0], setup))
    sh.source_depth += 1
    try:
        return await sh._run_script_text(text, io_, subshell=False, positional=args[1:] if len(args) > 1 else None, sourced=args[0])
    except ReturnSignal as r:
        return r.code  # `return` in a sourced file ends that file only
    finally:
        sh.source_depth -= 1


async def _unset(ctx):
    flags, args = _flag_args(ctx.args)
    sh = ctx.shell
    code = 0
    for arg in args:
        try:
            if "f" not in flags and not is_name(arg.split("[")[0]):
                if "v" in flags:
                    raise ShellError(f"`{arg}': not a valid identifier")
                continue  # plain unset passes over what can be neither a variable nor a function, silently
            a = await sh.expand_subscript(arg, ctx.io)
            # with neither -v nor -f, a name that is no variable but a function unsets the function
            if "f" in flags or ("v" not in flags and not sh.is_variable_set(a) and a in sh.functions):
                sh.functions.pop(a, None)
            else:
                sh.unset(a, nameref="n" in flags)
        except ShellError as e:
            ctx.error(f"{sh.err_prefix}unset: {e}")
            code = 1
    return code


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


def _printf(ctx):
    args = list(ctx.args)
    var = None
    if len(args) >= 2 and args[0] == "-v":
        var, args = args[1], args[2:]
    if args and args[0] == "--":
        args.pop(0)
    if not args:
        raise ShellError("usage: printf [-v var] format [arguments]", 2)
    errors: list[str] = []
    text = printf_format(args[0], args[1:], errors)
    for e in errors:  # bash reports them as it goes, then writes the text
        ctx.error(ctx.shell.err_prefix + e)
    if var is None:
        ctx.stdout.write(text)
    else:
        ctx.shell.set_var(var, text)
    return 1 if errors else 0


def _register_shell_commands(shell) -> None:
    r = shell.register_command
    r("true", lambda ctx: 0, "Do nothing, successfully")
    r(":", lambda ctx: 0, "Do nothing, successfully (the null command)")
    r("false", lambda ctx: 1, "Do nothing, unsuccessfully")
    r("echo", _echo, "Print arguments")
    r("printf", _printf, "Format and print data")
    r("cd", _cd, "Change the working directory")
    r("pwd", _pwd, "Print the working directory")
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
            "B": _MONTHS[t.month - 1], "p": "AM" if t.hour < 12 else "PM", "s": str(int((t if t.tzinfo else t.replace(tzinfo=timezone.utc)).timestamp())),
            "N": f"{t.microsecond * 1000:09}", "Z": "UTC", "z": "+0000", "n": "\n", "t": "\t", "%": "%",
        }.get(d, "%" + ("-" if no_pad else "") + d))
    return "".join(out)


_UNITS = {"second": 1, "sec": 1, "minute": 60, "min": 60, "hour": 3600, "day": 86400, "week": 604800, "fortnight": 1209600}

# our own summary of what this date supports (not GNU's help text)
_DATE_USAGE = """\
Usage: date [OPTION]... [+FORMAT]
Print the current date and time, or the one given with -d, in UTC.

  -d, --date=STRING        show the time described by STRING instead of now
  -r, --reference=FILE     show FILE's last modification time
  -u, --utc, --universal   use UTC (the clock is already UTC)
  -I, --iso-8601           print the date as YYYY-MM-DD
  -R, --rfc-email          print like: Fri, 20 Nov 2026 13:30:00 +0000

STRING can combine, in any order:
  dates      2026-11-20   11/20/2026   20 Nov 2026   Nov 20, 2026   20261120
  times      13:30   13:30:05.25   1:30pm   (2026-11-20T13:30:00 also works)
  zones      UTC  Z  GMT  EST  PDT  +0100  -05:00  UTC+2   (converted to UTC)
  weekdays   Monday, fri   (moves forward to that day)
  words      now  today  yesterday  tomorrow
  relative   3 days ago   +2 hours   next week   last month   1 year
  epoch      @1795168800

FORMAT controls the output: %Y %m %d %H %M %S %F %T %s %a %b %A %B %j %Z %z %n %%,
and %-d etc. for no padding. Example: date -d '20 Nov 2026 13:30 +0100' +%s

"""

_MONTH_ABBR = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"]
_DAY_ABBR = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"]
_ZONE_HOURS = {"utc": 0, "gmt": 0, "ut": 0, "z": 0, "est": -5, "edt": -4, "cst": -6, "cdt": -5, "mst": -7, "mdt": -6, "pst": -8, "pdt": -7}
_MONTHS_RE = r"(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?"


def parse_date(text: str, now: datetime) -> datetime:
    """date -d, from GNU date's documented input formats: items in any order, each optional. Calendar dates
    (2026-11-20, 11/20/2026, 20 Nov 2026, Nov 20, 2026, 20261120), times (13:30[:00[.5]], 1pm), zones
    (UTC, Z, EST..., +0100, +01:00), a day of the week, today/now/yesterday/tomorrow, @EPOCH, and relative items
    ("3 days ago", "next week", "+2 hours") applied last. Results are UTC, the simulated machine's zone."""
    bad = ShellError(f"invalid date {quote_l(text)}")
    s = text.strip().lower()
    m = re.fullmatch(r"@(-?\d+(?:\.\d+)?)", s)
    if m:
        return datetime.fromtimestamp(float(m.group(1)), timezone.utc)

    year = month = day = zone = weekday = day_shift = None  # zone in minutes east of UTC
    time: timedelta | None = None
    relative: list[tuple[int, str]] = []
    m = None

    def eat(pattern: str) -> bool:
        nonlocal s, m
        m = re.match("(?:" + pattern + ")", s)
        if not m:
            return False
        s = s[m.end():].lstrip(" \t,")
        return True

    def g(n: int) -> int:
        return int(m.group(n))

    def month_of(n: int) -> int:
        return _MONTH_ABBR.index(m.group(n)[:3]) + 1

    def full_year(y: int, digits: int) -> int:
        return y if digits > 2 else 2000 + y if y < 69 else 1900 + y

    def set_date(y, mo, d):
        nonlocal year, month, day
        if day is not None:
            raise bad
        year, month, day = (y if y is not None else year), mo, d

    def set_zone(minutes: int):
        nonlocal zone
        if zone is not None:
            raise bad
        zone = minutes

    after_time = False
    while s:
        # a signed number right after a time of day is its zone ("10:00 +2" is UTC+2), anywhere else it's relative
        if after_time and eat(r"([+-])\s*(\d{1,2}:\d{2}|\d{1,4})\b"):
            after_time = False
            v = m.group(2).replace(":", "")
            minutes = int(v) * 60 if len(v) <= 2 else int(v[:-2]) * 60 + int(v[-2:])
            set_zone(-minutes if m.group(1) == "-" else minutes)
            continue
        was_time = False
        if eat(r"(\d{4})-(\d{1,2})-(\d{1,2})(?:t(?=\d))?"):
            set_date(g(1), g(2), g(3))
        elif eat(r"(\d{1,2})/(\d{1,2})(?:/(\d{2}|\d{4}))?\b"):
            set_date(full_year(g(3), len(m.group(3))) if m.group(3) else None, g(1), g(2))
        elif eat(r"(\d{4})(\d{2})(\d{2})\b"):
            set_date(g(1), g(2), g(3))
        elif eat(r"(\d{1,2}):(\d{2})(?::(\d{2})(?:[.,](\d+))?)?\s*(?:(a|p)\.?m\.?\b)?"):
            if time is not None:
                raise bad
            h = g(1)
            if m.group(5):
                if not 1 <= h <= 12:
                    raise bad
                h = h % 12 + (12 if m.group(5) == "p" else 0)
            frac = float("0." + m.group(4)) if m.group(4) else 0
            if h > 23 or g(2) > 59 or (m.group(3) and g(3) > 60):
                raise bad
            time = timedelta(hours=h, minutes=g(2), seconds=(g(3) if m.group(3) else 0) + frac)
            was_time = True
        elif eat(r"(\d{1,2})\s*(a|p)\.?m\.?\b"):
            if time is not None or not 1 <= g(1) <= 12:
                raise bad
            time = timedelta(hours=g(1) % 12 + (12 if m.group(2) == "p" else 0))
            was_time = True
        elif eat(r"(\d{1,2})(?:st|nd|rd|th)?[\s-]*" + _MONTHS_RE + r"(?:[\s-]+(\d{4})\b)?"):
            set_date(g(3) if m.group(3) else None, month_of(2), g(1))
        elif eat(_MONTHS_RE + r"\s*(\d{1,2})(?:st|nd|rd|th)?\b(?:,?\s*(\d{4})\b)?"):
            set_date(g(3) if m.group(3) else None, month_of(1), g(2))
        elif eat(r"(next|last|this|[+-]?\s*\d+)?\s*(second|sec|minute|min|hour|day|week|fortnight|month|year)s?\b(\s+ago\b)?"):
            word = m.group(1).replace(" ", "") if m.group(1) else "1"
            n = {"next": 1, "last": -1, "this": 0}.get(word)
            n = int(word) if n is None else n
            relative.append((-n if m.group(3) else n, m.group(2)))
        elif eat(r"(utc|gmt|ut|z|[ecmp][sd]t)\b(?:([+-])(\d{1,2})(?::?(\d{2}))?)?"):
            offset = _ZONE_HOURS[m.group(1)] * 60
            if m.group(2):
                offset += (-1 if m.group(2) == "-" else 1) * (g(3) * 60 + (g(4) if m.group(4) else 0))
            set_zone(offset)
        elif eat(r"([+-])(\d{2}):?(\d{2})\b"):
            set_zone((-1 if m.group(1) == "-" else 1) * (g(2) * 60 + g(3)))
        elif eat(r"(sun|mon|tue|wed|thu|fri|sat)[a-z]*\.?\b"):
            if weekday is not None:
                raise bad
            weekday = _DAY_ABBR.index(m.group(1))
        elif eat(r"(today|now|yesterday|tomorrow)\b"):
            if day_shift is not None:
                raise bad
            day_shift = {"yesterday": -1, "tomorrow": 1}.get(m.group(1), 0)
        elif year is None and day is not None and eat(r"(\d{4})\b"):  # "Fri Nov 20 13:30:00 UTC 2026", date's own output
            year = g(1)
        else:
            raise bad
        after_time = was_time

    # absolute items set the date and time; anything not given comes from now, and a date alone means midnight
    absolute = day is not None or time is not None or weekday is not None
    try:
        t = datetime(year or now.year, month or now.month, day or now.day, tzinfo=timezone.utc)
    except ValueError:
        raise bad from None
    t += time if time is not None else timedelta(0) if absolute else timedelta(
        hours=now.hour, minutes=now.minute, seconds=now.second, microseconds=now.microsecond)
    if zone is not None:
        t -= timedelta(minutes=zone)
    if weekday is not None:
        t += timedelta(days=(weekday - (t.weekday() + 1) % 7) % 7)
    if day_shift is not None:
        t += timedelta(days=day_shift)
    for n, unit in relative:
        if unit in ("month", "year"):
            # like GNU date, a day past the end of the month rolls over (Jan 31 + 1 month = Mar 2 or 3)
            y, mo = divmod(t.month - 1 + n * (12 if unit == "year" else 1), 12)
            try:
                t = t.replace(year=t.year + y, month=mo + 1, day=1) + timedelta(days=t.day - 1)
            except (ValueError, OverflowError):
                raise bad from None
        else:
            t += timedelta(seconds=n * _UNITS[unit])
    return t


def _date(ctx):
    # the simulated machine's clock is UTC (TZ is unset), so output never depends on the host's time zone
    a = Args.parse(ctx.args, "uIR", "dr", "utc=u,universal=u,iso-8601=I,date=d,reference=r,rfc-email=R")
    now = datetime.now(timezone.utc)
    if a.value("d") is not None:
        now = parse_date(a.value("d"), now)
    elif a.value("r") is not None:
        now = ctx.fs.stat(ctx.resolve(a.value("r"))).modified.replace(tzinfo=timezone.utc)
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
    r("date", _date, "Print the date and time", _DATE_USAGE)
    r("ps", lambda ctx: ctx.stdout.write("    PID TTY          TIME CMD\n      1 pts/0    00:00:00 bash\n   4242 pts/0    00:00:00 ps\n"), "List processes")
