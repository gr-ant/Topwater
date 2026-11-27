"""Scripting builtins: echo, printf, test, bash, source, help, type, exit, set, trap, eval, command... See docs/architecture.md."""
from __future__ import annotations

import re
from dataclasses import replace

from .. import arith
from .._spec import COMMANDS, DIAGNOSTICS
from ..shell import ExitSignal, LoopControl, ReturnSignal
from ..vfs import ShellError, UsageException
from . import Args, printf_format, spec_format, spec_message, unescape


def is_shell_builtin(shell, name: str) -> bool:
    spec = COMMANDS.get(name)
    return spec is not None and spec.shell_builtin and name in shell.commands and shell._builtin_names is not None and name in shell._builtin_names


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
    raise ShellError(spec_message("test", "unaryExpected", op=op), 2)


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
        raise ShellError(spec_message("test", "integerExpected", value=left if not _is_int(left) else right), 2) from None
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
        raise ShellError(spec_message("test", "binaryExpected", op=a[1]), 2)
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
            raise ShellError(spec_message("test", "argumentExpected"), 2)
        if t == "(":
            pos += 1
            v = or_()
            if peek() != ")":
                raise ShellError(spec_message("test", "parenExpected"), 2)
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
        raise ShellError(spec_message("test", "tooManyArguments"), 2)
    return v


_BASH_LETTERS = {"e": "errexit", "u": "nounset", "f": "noglob", "x": "xtrace", "C": "noclobber"}


def _count(ctx, fallback: int) -> int:
    if not ctx.args:
        return fallback
    try:
        return int(ctx.args[0])
    except ValueError:
        raise ShellError(spec_message(ctx.name, "numericArgument", value=ctx.args[0]), 2) from None


_SET_LETTERS = {"e": "errexit", "u": "nounset", "f": "noglob", "x": "xtrace", "C": "noclobber"}


def _trap_name(sig: str) -> str:
    name = sig.upper().removeprefix("SIG")
    return "EXIT" if name == "0" else name


_SHOPTS = ["assoc_expand_once", "autocd", "cdable_vars", "cdspell", "checkhash", "checkjobs", "checkwinsize", "cmdhist",
           "complete_fullquote", "direxpand", "dirspell", "dotglob", "execfail", "expand_aliases", "extdebug", "extglob", "extquote",
           "failglob", "force_fignore", "globasciiranges", "globskipdots", "globstar", "gnu_errfmt", "histappend", "histreedit",
           "histverify", "hostcomplete", "huponexit", "inherit_errexit", "interactive_comments", "lastpipe", "lithist",
           "localvar_inherit", "localvar_unset", "login_shell", "mailwarn", "no_empty_cmd_completion", "nocaseglob", "nocasematch",
           "noexpand_translation", "nullglob", "patsub_replacement", "progcomp", "progcomp_alias", "promptvars", "restricted_shell",
           "shift_verbose", "sourcepath", "varredir_close", "xpg_echo"]


SHELL_KEYWORDS = {"if", "then", "else", "elif", "fi", "case", "esac", "for", "select", "while", "until", "do", "done", "in",
                  "function", "time", "{", "}", "!", "[[", "]]", "coproc"}


def describe(ctx, a: str, all_: bool = False) -> bool:
    """`type NAME` / `command -V NAME`: what the name runs, or False (with a message) when nothing."""
    sh = ctx.shell
    lines = []
    if a in sh.aliases:
        lines.append(f"{a} is aliased to `{sh.aliases[a]}'")
    if a in SHELL_KEYWORDS:
        lines.append(f"{a} is a shell keyword")
    if a in sh.functions:
        lines.append(f"{a} is a function\n{a} () \n{sh.functions[a].source}")
    if is_shell_builtin(sh, a):
        lines.append(f"{a} is a shell builtin")
    if all_:
        lines += [f"{a} is {p}" for p in sh.locate_all(a)]
    elif not lines:
        found = sh.locate(a)
        if found:
            p, hashed = found
            lines.append(f"{a} is hashed ({'' if p.startswith(('/', './')) else './'}{p})" if hashed else f"{a} is {p}")
    if not lines:
        ctx.error(f"{ctx.shell.err_prefix}type: " + spec_message("type", "notFound", name=a))
        return False
    for line in lines if all_ else lines[:1]:
        ctx.print(line)
    return True


_DEFAULT_PATH = ("/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin")


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


def _printf(ctx):
    args = list(ctx.args)
    var = None
    if len(args) >= 2 and args[0] == "-v":
        var, args = args[1], args[2:]
    if args and args[0] == "--":
        args.pop(0)
    if not args:
        raise ShellError(spec_message("printf", "usage"), 2)
    errors: list[str] = []
    text = printf_format(args[0], args[1:], errors)
    for e in errors:  # bash reports them as it goes, then writes the text
        ctx.error(ctx.shell.err_prefix + e)
    if var is None:
        ctx.stdout.write(text)
    else:
        ctx.shell.set_var(var, text)
    return 1 if errors else 0


def _which(ctx):
    code = 0
    for a in ctx.args:
        if a.startswith("-"):
            continue
        found = ctx.shell.locate_all(a)
        if not found:
            code = 1
        for p in found if "-a" in ctx.args else found[:1]:
            ctx.print(p)
    return code


def _type(ctx):
    code = 0
    opts = Args.parse(ctx.args, COMMANDS["type"])
    for a in opts.positional:
        sh = ctx.shell
        if opts.has("t") or opts.has("p") or opts.has("P"):
            kind = ("alias" if a in sh.aliases else "keyword" if a in SHELL_KEYWORDS else "function" if a in sh.functions
                    else "builtin" if is_shell_builtin(sh, a) else None)
            paths: list[str] = []
            if kind is None or opts.has("P"):
                found = sh.locate(a)
                paths = sh.locate_all(a) if opts.has("a") else [found[0]] if found else []
            kind = kind or ("file" if paths else None)
            if kind is None:
                code = 1
            elif opts.has("t"):
                ctx.print(kind)
            else:
                for p in paths:
                    ctx.print(p)
            continue
        if not describe(ctx, a, opts.has("a")):
            code = 1
    return code


def _bracket(ctx):
    if not ctx.args or ctx.args[-1] != "]":
        raise ShellError(spec_message("[", "missingBracket"), 2)
    return 0 if evaluate_test(ctx, ctx.args[:-1]) else 1


async def _run_script(ctx):
    from ..shell import ReturnSignal, StdinBuffer, _NewProcess

    io_ = ctx.io
    sh = ctx.shell
    args = list(ctx.args)
    is_bash = ctx.name in ("bash", "sh")
    command = login = False
    # bash's own options: -c, and -e -u -x -o pipefail ... which the new shell starts with
    options: list[tuple[str, bool]] = []
    i = 0
    while is_bash and i < len(args) and len(args[i]) > 1 and args[i][0] in "-+":
        f = args[i]
        if f == "--":
            i += 1
            break
        if f.startswith("--"):  # --norc, --noprofile are skipped
            login |= f == "--login"
        else:
            for c in f[1:]:
                if c == "c":
                    command = True
                elif c == "l" and f[0] == "-":
                    login = True
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
            raise ShellError(spec_message(ctx.name, "cOptionArgument"), 2)
        return await sh._run_script_text(args[0], io_, subshell=True, positional=args[2:],
                                         process=_NewProcess(args[1] if len(args) > 1 else ctx.name, None, setup, login))
    if not args:
        if not is_bash:
            raise ShellError(spec_message(ctx.name, "filenameRequired"), 2)
        return await sh._run_script_text(ctx.stdin, replace(io_, stdin=StdinBuffer()), subshell=True, positional=[],
                                         process=_NewProcess(ctx.name, None, setup, login))
    try:
        text = ctx.fs.read_text(ctx.resolve(args[0]))
    except ShellError as e:
        # a new bash reports its missing script itself, so without a line; source reports it at its own line
        ctx.error(f"{'bash: ' if is_bash else ctx.shell.err_prefix}{args[0]}: {str(e).rsplit(': ', 1)[-1]}")
        return 127 if is_bash else 1
    if is_bash:
        return await sh._run_script_text(text, io_, subshell=True, positional=args[1:], process=_NewProcess(args[0], args[0], setup, login))
    sh.source_depth += 1
    try:
        return await sh._run_script_text(text, io_, subshell=False, positional=args[1:] if len(args) > 1 else None, sourced=args[0])
    except ReturnSignal as r:
        return r.code  # `return` in a sourced file ends that file only
    finally:
        sh.source_depth -= 1


def _help(ctx):
    cmds = ctx.shell.commands
    if ctx.args:
        c = cmds.get(ctx.args[0])
        if c is None:
            raise ShellError(spec_message("help", "noTopics", topic=ctx.args[0]))
        ctx.print(c.usage or f"{c.name}: {c.description}")
        return
    width = max(len(n) for n in cmds) + 2
    ctx.print("Available commands (run '<command> --help' for details on non-standard ones):")
    for name in sorted(cmds):
        ctx.print(f"  {name.ljust(width)}{cmds[name].description}")


def _exit(ctx):
    if ctx.args and not re.fullmatch(r"[+-]?\d+", ctx.args[0]):
        # bash still exits, with status 2
        ctx.error(f"{ctx.shell.err_prefix}exit: " + spec_message("exit", "numericArgument", value=ctx.args[0]))
        raise ExitSignal(2)
    raise ExitSignal(_count(ctx, ctx.shell.last_exit_code) & 255)


def _return(ctx):
    if not ctx.shell.in_function and ctx.shell.source_depth == 0:
        raise ShellError(spec_message("return", "notInFunction"), 2)
    raise ReturnSignal(_count(ctx, ctx.shell.last_exit_code) & 255)


def _break(ctx):
    if ctx.shell.loop_depth > 0:
        raise LoopControl(ctx.name == "break", max(1, _count(ctx, 1)))
    ctx.error(f"{ctx.shell.err_prefix}{ctx.name}: " + spec_message(ctx.name, "onlyInLoop"))
    return 0


def _shift(ctx):
    n = _count(ctx, 1)
    if n > len(ctx.shell.positional):
        return 1
    ctx.shell.positional = ctx.shell.positional[n:]


def _trap(ctx):
    """EXIT runs when the tool call or script ends, ERR after a failing command, RETURN as a function returns, DEBUG
    before each simple command; other signals never arrive here."""
    from ..shell import quote_value
    args = [a for a in ctx.args if a != "--"]
    traps = ctx.shell.traps
    if not args or args[0] == "-p":
        only = {_trap_name(s) for s in args[1:]}
        for sig, action in traps.items():
            if not only or sig in only:
                ctx.print(f"trap -- {quote_value(action)} {sig}")
        return 0
    if args[0] == "-l":
        ctx.print(" 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 9) SIGKILL\t15) SIGTERM")
        return 0
    action, signals = (None, args) if len(args) == 1 else (args[0], args[1:])
    for sig in signals:
        if action in (None, "-"):
            traps.pop(_trap_name(sig), None)
        else:
            traps[_trap_name(sig)] = action
    return 0


def _shopt(ctx):
    a = Args.parse(ctx.args, COMMANDS["shopt"])
    sh = ctx.shell
    if a.has("o"):
        # shopt -o: the set -o options
        for n in a.positional:
            if not sh.set_option(n, a.has("s")) and (a.has("s") or a.has("u")):
                raise ShellError(spec_message("shopt", "invalidOptionName", name=n))
        if a.has("s") or a.has("u"):
            return 0
        for n, on in sh.option_list:
            if not a.positional or n in a.positional:
                ctx.print(f"{n:<15}\t{'on' if on else 'off'}")
        return 0
    names = a.positional or _SHOPTS
    if a.has("s") or a.has("u"):
        for n in names:
            if n not in _SHOPTS:
                raise ShellError(spec_message("shopt", "invalidShellOptionName", name=n))
            (sh.shopts.add if a.has("s") else sh.shopts.discard)(n)
        return 0
    code = 0
    for n in names:
        if n not in _SHOPTS:
            ctx.error("shopt: " + spec_message("shopt", "invalidShellOptionName", name=n))
            code = 1
            continue
        on = n in sh.shopts
        if code == 0 and not on:
            code = 1
        if not a.has("q"):
            ctx.print(f"{n:<15}\t{'on' if on else 'off'}")
    return code


def _set(ctx):
    args = list(ctx.args)
    sh = ctx.shell
    if not args:
        for k in sorted(sh.env):
            ctx.print(f"{k}={sh.env[k]}")
        return 0
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--":
            sh.positional = args[i + 1:]
            return 0
        if a == "-":
            i += 1
            break
        if len(a) < 2 or a[0] not in "-+":
            break
        on = a[0] == "-"
        for c in a[1:]:
            if c == "o":
                if i + 1 >= len(args):
                    # set -o / set +o alone: list the options (as commands for +o)
                    for n, value in sh.option_list:
                        ctx.print(f"{n:<15}\t{'on' if value else 'off'}" if on else f"set {'-' if value else '+'}o {n}")
                    continue
                i += 1
                if not sh.set_option(args[i], on):
                    raise ShellError(spec_message("set", "invalidOptionName", name=args[i]), 2)
            elif c in _SET_LETTERS:
                sh.set_option(_SET_LETTERS[c], on)
            elif c not in "abhkmnpvBEHPT":  # accepted, no effect here
                raise ShellError(spec_message("set", "builtinInvalidOption", option=a[0] + c), 2)
        i += 1
    if i < len(args):
        sh.positional = args[i:]
    return 0


def _getopts(ctx):
    if len(ctx.args) < 2:
        raise ShellError(spec_message("getopts", "usage"), 2)
    sh = ctx.shell
    spec, name = ctx.args[0], ctx.args[1]
    args = ctx.args[2:] if len(ctx.args) > 2 else sh.positional
    silent = spec.startswith(":")
    oi = sh.get_variable("OPTIND")
    optind = int(oi) if oi.isdigit() and int(oi) > 0 else 1
    # where we are inside a group of options like -abc; restarts when the script resets OPTIND
    pos = sh.getopts_state[1] if sh.getopts_state[0] == optind else 1

    def done(code: int, result: str) -> int:
        sh.set_var(name, result)
        sh.getopts_state = (optind, pos)
        sh.set_var("OPTIND", str(optind))
        return code

    if optind > len(args):
        return done(1, "?")
    arg = args[optind - 1]
    if pos == 1:
        if arg == "--":
            optind += 1
            return done(1, "?")
        if len(arg) < 2 or arg[0] != "-":
            return done(1, "?")
    c = arg[pos]
    at = -1 if c == ":" else spec.find(c)
    pos += 1
    if pos >= len(arg):
        optind += 1
        pos = 1
    if at < 0:
        if silent:
            sh.set_var("OPTARG", c)
        else:
            ctx.error(spec_message("getopts", "illegalOption", script=sh.get_variable('0'), option=c))
            sh.unset("OPTARG")
        return done(0, "?")
    if at + 1 >= len(spec) or spec[at + 1] != ":":
        sh.unset("OPTARG")
        return done(0, c)
    if pos > 1:
        sh.set_var("OPTARG", arg[pos:])
        optind += 1
        pos = 1
    elif optind <= len(args):
        sh.set_var("OPTARG", args[optind - 1])
        optind += 1
    elif silent:
        sh.set_var("OPTARG", c)
        return done(0, ":")
    else:
        ctx.error(spec_message("getopts", "missingArgument", script=sh.get_variable('0'), option=c))
        sh.unset("OPTARG")
        return done(0, "?")
    return done(0, c)


async def _exec(ctx):
    # exec with only redirections is handled by the shell; with a command, the command's status ends the shell
    args = list(ctx.args)
    while args and args[0] in ("-c", "-l"):
        args.pop(0)
    if not args:
        return 0
    raise ExitSignal(await ctx.shell._invoke(args, ctx.io, skip_functions=True))


def _hash(ctx):
    sh = ctx.shell
    sh.sync_hashed()
    args = list(ctx.args)
    code = i = 0
    show = delete = listing = False
    path = None
    while i < len(args) and len(args[i]) > 1 and args[i][0] == "-":
        if args[i] == "--":
            i += 1
            break
        for ch in args[i][1:]:
            if ch == "r":
                sh.hashed.clear()
            elif ch == "t":
                show = True
            elif ch == "d":
                delete = True
            elif ch == "p":
                if i + 1 >= len(args):
                    raise UsageException("-p: option requires an argument")
                i += 1
                path = args[i]
                break
            elif ch == "l":
                listing = True
            else:
                raise UsageException(spec_message("hash", "builtinInvalidOption", option="-" + ch))
        i += 1
    names = args[i:]
    if (show or delete) and not names:
        ctx.error(f"{sh.err_prefix}hash: -{'t' if show else 'd'}: option requires an argument")
        return 1

    def reusable(name, path):  # -l: each entry as the command that would hash it again
        return f"builtin hash -p {path} {name}"

    if listing and not names:
        ctx.print("".join(reusable(k, h[0]) + "\n" for k, h in sh.hashed.items()), end="")
    elif i == 0 and not names:
        if not sh.hashed:
            ctx.print("hash: hash table empty")
        else:
            ctx.print("hits\tcommand\n" + "".join(f"{h[1]:4}\t{h[0]}\n" for h in sh.hashed.values()), end="")
    for n in names:
        hashed = sh.hash_lookup(n) if show else None
        if hashed is not None:
            ctx.print(reusable(n, hashed) if listing else f"{n}\t{hashed}" if len(names) > 1 else hashed)
        elif delete and sh.hashed.pop(n, None) is not None:
            pass
        elif path is not None:
            sh.hashed[n] = [path, 0]
        elif show or delete:
            ctx.error(f"{sh.err_prefix}hash: " + spec_message("hash", "notFound", name=n))
            code = 1
        elif "/" in n or is_shell_builtin(sh, n) or n in sh.functions or sh.is_unfiled(n):
            pass
        elif sh.search_path_all(n):
            sh.hashed[n] = [sh.search_path_all(n)[0], 0]
        else:
            ctx.error(f"{sh.err_prefix}hash: " + spec_message("hash", "notFound", name=n))
            code = 1
    return code


async def _builtin(ctx):
    if not ctx.args:
        return 0
    if not is_shell_builtin(ctx.shell, ctx.args[0]):
        raise ShellError(spec_message("builtin", "notBuiltin", name=ctx.args[0]))
    return await ctx.shell._invoke(list(ctx.args), ctx.io, skip_functions=True)


async def _eval(ctx):
    return await ctx.shell._run_script_text(" ".join(ctx.args), ctx.io, subshell=False, eval_=True)


def _let(ctx):
    if not ctx.args:
        raise ShellError(spec_message("let", "expressionExpected"))
    v = 0
    for expr in ctx.args:
        v = arith.evaluate(ctx.shell, expr)
    return 0 if v != 0 else 1


async def _command(ctx):
    args = list(ctx.args)
    default_path = False
    while args and args[0] == "-p":
        args.pop(0)
        default_path = True
    sh = ctx.shell
    if default_path and args and args[0] not in ("-v", "-V") and "/" not in args[0] and not is_shell_builtin(sh, args[0]) \
            and not sh.is_unfiled(args[0]):
        # -p: look the program up on bash's default PATH instead of $PATH
        found = next((f"{d}/{args[0]}" for d in _DEFAULT_PATH
                      if ctx.fs.is_file(f"{d}/{args[0]}") and ctx.fs.can_execute(f"{d}/{args[0]}")), None)
        if found is None:
            ctx.error(spec_format(DIAGNOSTICS["commandNotFound"], command=args[0]))
            return 127
        args[0] = found
    if not args or args[0] not in ("-v", "-V"):
        return await sh._invoke(args, ctx.io, skip_functions=True) if args else 0
    code = 0
    sh = ctx.shell
    for n in args[1:]:
        if args[0] == "-V":
            if not describe(ctx, n):
                code = 1
            continue
        if n in sh.aliases:
            ctx.print(f"alias {n}='{sh.aliases[n]}'")
        elif n in SHELL_KEYWORDS or n in sh.functions or is_shell_builtin(sh, n):
            ctx.print(n)
        elif found := sh.locate(n):
            ctx.print(found[0])
        else:
            code = 1
    return code


def register(shell) -> None:
    r = shell.register_command
    r("true", lambda ctx: 0)
    r(":", lambda ctx: 0)
    r("false", lambda ctx: 1)
    r("echo", _echo)
    r("printf", _printf)
    r("history", lambda ctx: [ctx.print(f"{i + 1:5}  {h}") for i, h in enumerate(ctx.shell.history)] and 0)
    r("which", _which)
    r("type", _type)
    r("test", lambda ctx: 0 if evaluate_test(ctx, list(ctx.args)) else 1)
    r("[", _bracket)
    r("bash", _run_script)
    r("sh", _run_script)
    r("source", _run_script)
    r(".", _run_script)
    r("help", _help)
    r("exit", _exit)
    r("return", _return)
    r("break", _break)
    r("continue", _break)
    r("shift", _shift)
    r("trap", _trap)
    r("shopt", _shopt)
    r("set", _set)
    r("getopts", _getopts)
    r("exec", _exec)
    r("hash", _hash)
    r("builtin", _builtin)
    r("eval", _eval)
    r("let", _let)
    r("command", _command)
