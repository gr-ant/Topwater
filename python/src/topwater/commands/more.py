"""Scripting built-ins (set, read, local, exit, ...) and everyday utilities (seq, diff, stat, ...)."""
from __future__ import annotations

import asyncio
import base64
import difflib
import hashlib
import json
import math
import random
import re
import string
import time
from datetime import datetime, timezone

from .. import arith, bytetext
from ..convert import file_type
from ..shell import ExitSignal, LoopControl, ReturnSignal
from .._spec import COMMANDS, DIAGNOSTICS
from ..vfs import ShellError, UsageException, format_mode, get_parent, normalize, utc_now
from . import Args, inputs, is_shell_builtin, printf_format, quote, quote_always, quote_l, spec_format, spec_message, split_lines, unescape, write_lines


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
        raise ShellError(spec_message(ctx.name, "numericArgument", value=ctx.args[0]), 2) from None


def _duration(s: str) -> float:
    m = re.fullmatch(r"(\d+(?:\.\d+)?)([smhd]?)", s)
    if not m:
        raise ShellError(spec_message("sleep", "invalidTimeInterval", value=s))
    return float(m.group(1)) * {"": 1, "s": 1, "m": 60, "h": 3600, "d": 86400}[m.group(2)]


# ---------- scripting ----------

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


_SET_LETTERS = {"e": "errexit", "u": "nounset", "f": "noglob", "x": "xtrace", "C": "noclobber"}


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


def _trap_name(sig: str) -> str:
    name = sig.upper().removeprefix("SIG")
    return "EXIT" if name == "0" else name


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


_SHOPTS = ["assoc_expand_once", "autocd", "cdable_vars", "cdspell", "checkhash", "checkjobs", "checkwinsize", "cmdhist",
           "complete_fullquote", "direxpand", "dirspell", "dotglob", "execfail", "expand_aliases", "extdebug", "extglob", "extquote",
           "failglob", "force_fignore", "globasciiranges", "globskipdots", "globstar", "gnu_errfmt", "histappend", "histreedit",
           "histverify", "hostcomplete", "huponexit", "inherit_errexit", "interactive_comments", "lastpipe", "lithist",
           "localvar_inherit", "localvar_unset", "login_shell", "mailwarn", "no_empty_cmd_completion", "nocaseglob", "nocasematch",
           "noexpand_translation", "nullglob", "patsub_replacement", "progcomp", "progcomp_alias", "promptvars", "restricted_shell",
           "shift_verbose", "sourcepath", "varredir_close", "xpg_echo"]


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


def _print_functions(ctx, names: list[str], names_only: bool) -> int:
    """declare -f / -F: function definitions (as written) or names."""
    fns = ctx.shell.functions
    code = 0
    for n in names or sorted(fns):
        if n not in fns:
            code = 1
            continue
        ctx.print((f"declare -f {n}" if not names else n) if names_only else f"{n} () \n{fns[n].source}")
    return code


def attribute_flags(sh, name: str) -> str:
    """A variable's attribute letters in bash's order (a A i n r t x l u)."""
    attrs = sh.var_attrs.get(name, "")
    array = sh.arrays.get(name)
    out = []
    for c in "aAinrtxlu":
        on = (array is not None and not array.assoc) if c == "a" else (array is not None and array.assoc) if c == "A" \
            else sh.is_exported(name) if c == "x" else c in attrs
        if on:
            out.append(c)
    return "".join(out)


def _declare_quote(v: str) -> str:
    from . import shell_quote
    if any(ord(c) < 32 or ord(c) == 127 for c in v):
        return shell_quote(v)
    return '"' + v.replace("\\", "\\\\").replace('"', '\\"').replace("$", "\\$").replace("`", "\\`") + '"'


def declare_line(sh, name: str) -> str | None:
    """`declare -p NAME`: a line that recreates the variable when evaluated; None when there is no such variable."""
    flags = attribute_flags(sh, name)
    head = f"declare -{flags or '-'} {name}"
    a = sh.arrays.get(name)
    if a is not None:
        if not a.items:
            return head + "=()"
        items = [f"[{_declare_quote(k) if a.assoc and not all((c.isascii() and c.isalnum()) or c == '_' for c in k) else k}]={_declare_quote(v)}"
                 for k, v in a.pairs()]
        return head + "=(" + ("".join(i + " " for i in items) if a.assoc else " ".join(items)) + ")"
    if name in sh.env:
        return head + "=" + _declare_quote(sh.env[name])
    return head if flags else None


def _declare(ctx):
    from ..parser import is_name
    from . import shell_quote
    sh = ctx.shell
    on = "r" if ctx.name == "readonly" else ""
    off = list_only = ""
    pr = functions = function_names = glob = False
    names: list[str] = []
    for a in ctx.args:
        if names or len(a) < 2 or a[0] not in "-+" or a == "--":
            if a != "--" or names:
                names.append(a)
            continue
        for c in a[1:]:
            if c == "p":
                pr = True
            elif c == "f":
                functions = True
            elif c == "F":
                function_names = True
            elif c == "g":
                glob = True
            elif c in "aA":
                list_only += c  # arrays were declared before the built-in ran
            elif c == "t":
                pass
            elif c in "irxlun":
                if a[0] == "-":
                    on += c
                else:
                    off += c
            else:
                raise ShellError(spec_message(ctx.name, "builtinInvalidOption", option="-" + c), 2)
    if functions or function_names:
        return _print_functions(ctx, names, function_names)
    if not names:
        # declare / declare -p / readonly / declare -x ...: list what has those attributes
        for n in sorted(set(sh.env) | set(sh.arrays)):
            if all(c in attribute_flags(sh, n) for c in on + list_only):
                line = declare_line(sh, n)
                if line is None:
                    continue
                if pr or on or list_only:
                    ctx.print(line)
                else:
                    ctx.print(f"{n}={line[line.index('=') + 1:] if n in sh.arrays else shell_quote(sh.env[n])}")
        return 0
    code = 0
    if pr:
        for n in names:
            line = declare_line(sh, n)
            if line is not None:
                ctx.print(line)
            else:
                ctx.error(f"{ctx.shell.err_prefix}{ctx.name}: " + spec_message(ctx.name, "notFound", name=n))
                code = 1
        return code
    local = ctx.name == "local" or (ctx.name in ("declare", "typeset") and sh.in_function and not glob)
    for arg in names:
        eq = arg.find("=")
        append = eq > 0 and arg[eq - 1] == "+"
        name = arg[:eq - 1 if append else eq] if eq > 0 else arg
        value = arg[eq + 1:] if eq > 0 else None
        try:
            if not is_name(name):
                raise ShellError(spec_format(DIAGNOSTICS["notValidIdentifier"], name=arg))
            if local:
                sh.declare_local(name, None)  # shortcut: `local x` is empty here, unset in bash
            if "n" in on:
                # a nameref holds the name of the variable it stands for
                sh.set_attrs(name, "n")
                sh.mark_shell_variable(name)
                if value is not None:
                    sh.env[name] = value
                continue
            target = sh.resolve_name(name)
            sh.set_attrs(target, on.replace("r", ""), off)
            if value is not None:
                sh.set_var(name, value, append)
            elif target not in sh.env and target not in sh.arrays:
                sh.mark_shell_variable(target)
                sh.env[target] = ""
            if "r" in on:
                sh.set_attrs(target, "r")
        except ShellError as e:
            ctx.error(f"{ctx.shell.err_prefix}{ctx.name}: {e}")
            code = 1
    return code


def _ends_with_escape(line: str) -> bool:
    return (len(line) - len(line.rstrip("\\"))) % 2 == 1


def read_fields(line: str, ifs: str, count: int) -> list[str]:
    """`read a b c`: the fields for each name. The last name gets the rest of the line less trailing IFS whitespace,
    and a single trailing IFS delimiter is dropped when the rest is just one field ("x,y," gives b=y, as in bash)."""
    ws = "".join(c for c in " \t\n" if c in ifs)
    fields: list[str] = []
    i, n = 0, len(line)
    while i < n and line[i] in ws:
        i += 1
    while len(fields) < count - 1 and i < n:
        start = i
        while i < n and line[i] not in ifs:
            i += 1
        fields.append(line[start:i])
        while i < n and line[i] in ws:
            i += 1
        if i < n and line[i] in ifs and line[i] not in ws:
            i += 1
            while i < n and line[i] in ws:
                i += 1

    def trim(t: str) -> str:
        return t.rstrip(ws) if ws else t

    rest = trim(line[i:])
    if rest and rest[-1] in ifs and rest[-1] not in ws and not any(c in ifs for c in trim(rest[:-1])):
        rest = trim(rest[:-1])
    if i < n or len(fields) < count:
        fields.append(rest)
    return fields


def _input_fd(ctx, u: str | None):
    if u is None or u == "0":
        return ctx.stdin_buffer
    fd = int(u) if u.isdigit() else -1
    from ..shell import StdinBuffer
    v = (ctx.fds or {}).get(fd)
    buf = v if isinstance(v, StdinBuffer) else ctx.shell.input_fd(fd)
    if buf is None:
        raise ShellError(spec_message(ctx.name, "badFileDescriptor", fd=u))
    return buf


def _read(ctx):
    from ..shell import split_fields
    a = Args.parse(ctx.args, COMMANDS["read"])
    sh = ctx.shell
    names = a.positional or ["REPLY"]
    inp = _input_fd(ctx, a.value("u"))
    d = a.value("d")
    delim = "\n" if d is None else (d[:1] or "\0")
    if a.value("N") is not None:
        line = inp.read_chars(int(a.value("N")), None)
    elif a.value("n") is not None:
        line = inp.read_chars(int(a.value("n")), delim)
    else:
        line = inp.read_line(delim)
        # without -r, a backslash at the end of a line joins it to the next one
        while not a.has("r") and line is not None and inp.last_terminated and delim == "\n" and _ends_with_escape(line):
            more = inp.read_line()
            if more is None:
                break
            line = line[:-1] + more
    if line is None:
        for n in names:
            sh.set_var(n, "")
        return 1
    if not a.has("r"):
        line = re.sub(r"\\(.)", r"\1", line)
    ifs = sh.ifs
    if a.has("a"):
        # read -a NAME: every field becomes an element
        sh.unset(names[0])
        array = sh.writable(names[0])
        for f in split_fields(line, ifs):
            sh.charge_variable(names[0], len(f), 0)
            array.set(str(array.next), f)
    elif not a.positional:
        sh.set_var("REPLY", line)
    else:
        fields = read_fields(line, ifs, len(names))
        for i, n in enumerate(names):
            sh.set_var(n, fields[i] if i < len(fields) else "")
    # like bash, a final line with no trailing newline is still assigned, but read reports end of input
    return 0 if inp.last_terminated else 1


def _mapfile(ctx):
    a = Args.parse(ctx.args, COMMANDS[ctx.name])
    sh = ctx.shell
    name = a.positional[0] if a.positional else "MAPFILE"
    inp = _input_fd(ctx, a.value("u"))
    d = a.value("d")
    delim = "\n" if d is None else (d[:1] or "\0")
    mx = int(a.value("n")) if a.value("n") else 0
    skip = int(a.value("s")) if a.value("s") else 0
    index = int(a.value("O")) if a.value("O") else 0
    if a.value("O") is None:
        sh.unset(name)  # -O keeps what the array already holds
    array = sh.writable(name)
    count = 0
    while mx == 0 or count < mx:
        line = inp.read_line(delim)
        if line is None:
            break
        if skip > 0:
            skip -= 1
            continue
        item = line if a.has("t") or not inp.last_terminated else line + delim
        sh.charge_variable(name, len(item), 0)
        array.set(str(index), item)
        index += 1
        count += 1
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


def _wait(ctx):
    # background jobs ran to completion when they were started: wait just reports their status
    code = 0
    ids = [a for a in ctx.args if not a.startswith("-")]
    if "-n" in ctx.args and not ids:
        return ctx.shell.wait_next()
    if not ids:
        ctx.shell.forget_jobs()
    for i in ids:
        if i.startswith("%"):
            code = 0
            continue
        if not i.isdigit():
            ctx.error(f"{ctx.shell.err_prefix}wait: " + spec_message("wait", "notPid", id=i))
            code = 2
            continue
        code = ctx.shell.wait_for(int(i))
        if code == 127:
            ctx.error(f"{ctx.shell.err_prefix}wait: " + spec_message("wait", "notChild", pid=str(i)))
    return code


_SIGNALS = {"HUP": 1, "INT": 2, "QUIT": 3, "KILL": 9, "TERM": 15}


def _kill(ctx):
    if ctx.args and ctx.args[0] == "-l":
        ctx.print(" 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 9) SIGKILL\t15) SIGTERM")
        return 0
    signal, code = 15, 0
    targets: list[str] = []
    i = 0
    while i < len(ctx.args):
        a = ctx.args[i]
        if a in ("-s", "-n") and i + 1 < len(ctx.args):
            i += 1
            a = "-" + ctx.args[i]
        if a.startswith("-") and not targets:
            sig = a[1:].upper().replace("SIG", "")
            signal = int(sig) if sig.isdigit() else _SIGNALS.get(sig, 15)
            i += 1
            continue
        targets.append(a)
        i += 1
    if not targets:
        raise ShellError(spec_message("kill", "usage"), 2)
    for t in targets:
        if t == "4242":  # the shell itself
            if signal != 0:
                raise ExitSignal(128 + signal)
            continue
        ctx.error(f"{ctx.shell.err_prefix}kill: " + (spec_message("kill", "noSuchJob", job=t) if t.startswith("%") else spec_message("kill", "noSuchProcess", pid=t)))
        code = 1
    return code


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


def _dirs_line(sh) -> str:
    home = sh.env.get("HOME", "")

    def short(d: str) -> str:
        return "~" + d[len(home):] if len(home) > 1 and (d == home or d.startswith(home + "/")) else d

    return " ".join(short(d) for d in [sh.cwd] + sh.dir_stack)


def _change_dir(ctx, d: str) -> None:
    path = ctx.resolve(d)
    if not ctx.fs.is_dir(path):
        raise ShellError(spec_message("pushd", "noDirectory", dir=d))
    ctx.shell.env["OLDPWD"] = ctx.shell.cwd
    ctx.shell.cwd = path


def _pushd(ctx):
    sh = ctx.shell
    args = [a for a in ctx.args if a != "-n"]
    old = sh.cwd
    if not args:
        if not sh.dir_stack:
            raise ShellError(spec_message("pushd", "noOtherDirectory"))
        _change_dir(ctx, sh.dir_stack[0])
        sh.dir_stack[0] = old
    else:
        _change_dir(ctx, args[0])
        sh.dir_stack.insert(0, old)
    ctx.print(_dirs_line(sh))
    return 0


def _popd(ctx):
    sh = ctx.shell
    if not sh.dir_stack:
        raise ShellError(spec_message("popd", "stackEmpty"))
    _change_dir(ctx, sh.dir_stack[0])
    sh.dir_stack.pop(0)
    ctx.print(_dirs_line(sh))
    return 0


def _dirs(ctx):
    if "-c" in ctx.args:
        ctx.shell.dir_stack.clear()
        return 0
    line = _dirs_line(ctx.shell)
    if any(a in ("-p", "-v") for a in ctx.args):
        for i, d in enumerate(line.split(" ")):
            ctx.print(f"{i:>2}  {d}" if "-v" in ctx.args else d)
    else:
        ctx.print(line)
    return 0


def _ulimit(ctx):
    if any(not a.startswith("-") for a in ctx.args):
        return 0  # setting a limit: accepted
    ctx.print("1024" if "-n" in ctx.args else "8192" if "-s" in ctx.args else "0" if "-c" in ctx.args else "unlimited")
    return 0


async def _eval(ctx):
    return await ctx.shell._run_script_text(" ".join(ctx.args), ctx.io, subshell=False, eval_=True)


def _let(ctx):
    if not ctx.args:
        raise ShellError(spec_message("let", "expressionExpected"))
    v = 0
    for expr in ctx.args:
        v = arith.evaluate(ctx.shell, expr)
    return 0 if v != 0 else 1


_DEFAULT_PATH = ("/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin")


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


def _umask_symbolic(mask: int) -> str:
    perms = ~mask & 0o777
    return ",".join(f"{who}={''.join(c for c, bit in zip('rwx', (4, 2, 1)) if perms >> shift & bit)}"
                    for who, shift in (("u", 6), ("g", 3), ("o", 0)))


def _umask_parse(text: str, mask: int) -> int:
    """An octal mask, or symbolic clauses like u=rwx,g+w,o- applied to the permissions the current mask leaves."""
    def bad(c: str):
        return ShellError(spec_message("umask", "invalidSymbolic", char=c))

    if text[:1].isdigit():
        if any(c not in "01234567" for c in text) or int(text, 8) > 0o7777:
            raise ShellError(spec_message("umask", "octalOutOfRange", value=text))
        return int(text, 8) & 0o777
    perms = ~mask & 0o777
    for clause in text.split(","):
        i = 0
        who = 0
        while i < len(clause) and clause[i] in "ugoa":
            who |= {"u": 0o700, "g": 0o070, "o": 0o007, "a": 0o777}[clause[i]]
            i += 1
        who = who or 0o777
        if i >= len(clause) or clause[i] not in "+-=":
            raise ShellError(spec_message("umask", "invalidOperator", char=clause[i] if i < len(clause) else " "))
        while i < len(clause) and clause[i] in "+-=":
            op = clause[i]
            i += 1
            bits = 0
            while i < len(clause) and clause[i] not in "+-=":
                if clause[i] not in "rwx":
                    raise bad(clause[i])
                bits |= {"r": 0o444, "w": 0o222, "x": 0o111}[clause[i]]
                i += 1
            bits &= who
            perms = perms | bits if op == "+" else perms & ~bits if op == "-" else perms & ~who | bits
    return ~perms & 0o777


def _umask(ctx):
    sh = ctx.shell
    args = list(ctx.args)
    symbolic = reuse = False
    while args and args[0].startswith("-") and len(args[0]) > 1:
        a = args.pop(0)
        if a == "--":
            break
        for c in a[1:]:
            if c == "S":
                symbolic = True
            elif c == "p":
                reuse = True
            else:
                ctx.error(f"{sh.err_prefix}umask: " + spec_message("umask", "builtinInvalidOption", option="-" + c))
                ctx.error("umask: " + spec_message("umask", "usage"))
                return 2
    if args:
        sh.umask = ctx.fs.umask = _umask_parse(args[0], sh.umask)
        if not symbolic:
            return 0
    shown = _umask_symbolic(sh.umask) if symbolic else f"{sh.umask:04o}"
    ctx.print(("umask -S " if symbolic else "umask ") + shown if reuse else shown)
    return 0


def _printenv(ctx):
    sh = ctx.shell
    env = {k: v for k, v in sh.env.items() if sh.is_exported(k)}
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
        raise UsageException(spec_message("sleep", "missingOperand"))
    total = sum(_duration(a) for a in ctx.args)
    sh = ctx.shell
    # the call's deadline (or a `timeout`) cuts the sleep short
    await asyncio.sleep(max(0.0, min(total, sh._deadline - time.monotonic())))
    sh.check_deadline()


async def _timeout(ctx):
    from ..shell import CallTimeout
    args = list(ctx.args)
    while args and args[0].startswith("-"):
        args.pop(0)
    if len(args) < 2:
        raise ShellError(spec_message("timeout", "missingOperand"), 125)
    sh = ctx.shell
    from . import exec_failure
    failure = exec_failure(sh, args[1])
    if failure is not None:
        raise ShellError(spec_message("timeout", "failedToRun", command=args[1], errno=failure[0]), failure[1])
    outer = sh._deadline
    mine = time.monotonic() + _duration(args[0])
    sh._deadline = min(outer, mine)
    try:
        return await asyncio.wait_for(sh._invoke(args[1:], ctx.io), max(0.0, mine - time.monotonic()) + 0.5)
    except (CallTimeout, asyncio.TimeoutError):
        if time.monotonic() >= outer:
            raise CallTimeout() from None
        return 124
    finally:
        sh._deadline = outer


async def _time(ctx):
    start = time.perf_counter()
    code = await ctx.shell._invoke(list(ctx.args), ctx.io) if ctx.args else 0
    t = time.perf_counter() - start
    ctx.stderr.write(f"\nreal\t{int(t // 60)}m{t % 60:.3f}s\nuser\t0m0.000s\nsys\t0m0.000s\n")
    return code


# ---------- utilities ----------

def _basename(ctx):
    a = Args.parse(ctx.args, COMMANDS["basename"])
    if not a.positional:
        raise UsageException(spec_message("basename", "missingOperand"))
    suffix = a.value("s") or (a.positional[1] if not a.has("a") and len(a.positional) == 2 else None)
    names = a.positional if a.has("a") or a.value("s") else a.positional[:1]
    for n in names:
        t = n.rstrip("/")
        b = "/" if not t else t[t.rfind("/") + 1:]
        if suffix and b.endswith(suffix) and b != suffix:
            b = b[: -len(suffix)]
        ctx.print(b)


def _dirname(ctx):
    a = Args.parse(ctx.args, COMMANDS["dirname"])
    if not a.positional:
        raise UsageException(spec_message("dirname", "missingOperand"))
    for n in a.positional:
        t = n.rstrip("/")
        i = t.rfind("/")
        ctx.stdout.write(("/" if not t else "." if i < 0 else "/" if i == 0 else (t[:i].rstrip("/") or "/")) + ("\0" if a.has("z") else "\n"))


def _realpath(ctx):
    a = Args.parse(ctx.args, COMMANDS[ctx.name])
    readlink = ctx.name == "readlink"
    plain = readlink and not (a.has("f") or a.has("e") or a.has("m"))
    report = a.has("v") if readlink else not a.has("q")  # readlink is silent about failures unless -v; realpath reports them unless -q
    if not a.positional and readlink:
        raise UsageException(spec_message("readlink", "missingOperand"))
    end = "\0" if a.has("z") else "" if readlink and a.has("n") and len(a.positional) == 1 else "\n"
    code = 0
    for p in a.positional or ["."]:
        path = ctx.resolve(p)
        why = ""
        if plain:
            try:
                result = ctx.fs.read_link(path)
            except ShellError as e:
                result, why = None, str(e).rsplit(": ", 1)[-1]
        elif not readlink and a.has("s"):
            result = normalize(ctx.shell.cwd, p)  # -s: no links resolved
        else:
            result, why = ctx.fs.real_path(path, existing=a.has("e"))
            if result is None and a.has("m"):
                result = path
        if result is None:
            if report:
                ctx.error(f"{ctx.name}: " + spec_message(ctx.name, "failed", file=p, errno=why))
            code = 1
            continue
        ctx.stdout.write(result + end)
    return code


def _stat(ctx):
    a = Args.parse(ctx.args, COMMANDS["stat"])
    user = ctx.shell.user
    code = 0
    for p in a.positional:
        path = ctx.resolve(p)
        follow = a.has("L")
        if not ctx.fs.exists(path, follow):
            ctx.error("stat: " + spec_message("stat", "cannotStat", file=p, errno=ctx.fs.why_missing(path, follow)))
            code = 1
            continue
        st = ctx.fs.stat(path, follow)
        kind = "symbolic link" if st.is_symlink else "directory" if st.is_dir else "regular empty file" if st.size == 0 else "regular file"
        named = f"{quote_always(p)} -> {quote_always(st.link_target)}" if st.is_symlink else quote_always(p)
        mode = f"{st.mode:03o}"
        rwx = format_mode(st.is_dir, st.mode, st.is_symlink)
        user = st.owner
        uid = "0" if user == "root" else "1000"
        mtime = st.modified.strftime("%Y-%m-%d %H:%M:%S.%f000 +0000")
        if a.value("c") is not None:
            values = {
                "n": p, "N": named, "s": str(st.size), "F": kind, "a": mode, "A": rwx, "U": user, "G": user, "u": uid, "g": uid,
                "y": mtime, "Y": str(int(st.modified.replace(tzinfo=timezone.utc).timestamp())), "b": str(st.blocks), "B": "512", "h": str(st.links), "%": "%",
            }
            ctx.print(re.sub(r"%(.)", lambda m: values.get(m.group(1), m.group(0)), a.value("c")))
            continue
        ctx.stdout.write(f"  File: {p}{' -> ' + st.link_target if st.is_symlink else ''}\n  Size: {st.size:<10}\tBlocks: {st.blocks:<10} IO Block: 4096   {kind}\n"
                         f"Access: ({mode:0>4}/{rwx})  Uid: ({uid:>5}/{user:>8})   Gid: ({uid:>5}/{user:>8})\n"
                         f"Modify: {mtime}\nChange: {mtime}\n")
    return code


def _du(ctx):
    a = Args.parse(ctx.args, COMMANDS["du"])
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
        if not ctx.fs.is_dir(path, follow=False):
            kb = ctx.fs.stat(path, follow=False).blocks // 2
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
        if not ctx.fs.exists(path, follow=False):
            ctx.error("du: " + spec_message("du", "cannotAccess", file=p, errno="No such file or directory"))
            code = 1
            continue
        grand += walk(path, p, 0)
    if a.has("c"):
        ctx.print(f"{fmt(grand)}\ttotal")
    return code


def _df(ctx):
    human = any(x.startswith("-") and "h" in x for x in ctx.args)
    # the disk is the filesystem's capacity and what its files really hold
    size, used = ctx.fs.capacity, ctx.fs.used_bytes
    avail = max(0, size - used)
    pct = 0 if size == 0 else math.ceil(used * 100 / size)

    def h(b: int) -> str:
        if b < 1024:
            return str(b)
        v, unit = float(b), 0
        while True:
            v /= 1024
            unit += 1
            if not (math.ceil(v) >= 1024 and unit < 4):
                break
        return (f"{math.ceil(v * 10) / 10:.1f}" if v < 10 else f"{math.ceil(v):.0f}") + "KMGT"[unit - 1]  # df -h rounds up

    ctx.stdout.write(
        f"Filesystem      Size  Used Avail Use% Mounted on\n/dev/sda1      {h(size):>5} {h(used):>5} {h(avail):>5} {pct:>3}% /\n"
        "tmpfs           3.9G     0  3.9G   0% /dev/shm\n"
        if human else
        f"Filesystem     1K-blocks    Used Available Use% Mounted on\n/dev/sda1      {size // 1024:>10} {(used + 1023) // 1024:>7} "
        f"{avail // 1024:>9} {pct:>3}% /\ntmpfs            4007440       0   4007440   0% /dev/shm\n")


def chmod_parse(spec: str, mode: int, is_dir: bool, umask: int = 0) -> int:
    """Octal (755) or symbolic (u+x, go-w, a=r, +x, +t) mode; a clause with no who-letter (+w, =rw) leaves alone the bits
    the umask takes away."""
    if re.fullmatch(r"[0-7]{1,4}", spec):
        return int(spec, 8)
    for clause in spec.split(","):
        m = re.fullmatch(r"([ugoa]*)([-+=])([rwxXt]*)", clause)
        if not m:
            raise ShellError(spec_message("chmod", "invalidMode", mode=spec))
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
        if not m.group(1):
            mask &= ~umask
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
        elif arg.startswith("--") and len(arg) > 2:
            raise UsageException(spec_message("chmod", "unrecognizedOption", option=arg))
        elif len(arg) > 1 and arg[0] == "-" and (bad := next((c for c in arg if c not in "-rwxXstugoa+=,01234567"), None)):
            raise UsageException(spec_message("chmod", "invalidOption", option=bad))
        elif spec is None:
            spec = arg  # may look like an option, e.g. -x or -w
        else:
            files.append(arg)
    if spec is None or not files:
        raise UsageException(spec_message("chmod", "missingOperand") if spec is None else spec_message("chmod", "missingOperandAfter", operand=spec))
    code = 0

    def fmt(st, m):
        return f"{m:04o} ({format_mode(st.is_dir, m)[1:]})"

    def apply(display, path):
        nonlocal code
        st = ctx.fs.stat(path)
        mode = chmod_parse(spec, st.mode, st.is_dir, ctx.shell.umask)
        try:
            ctx.fs.chmod(path, mode)
        except ShellError:
            if not quiet:
                ctx.error("chmod: " + spec_message("chmod", "cannotChangePermissions", file=display))
            code = 1
            return
        if mode != st.mode and (verbose or changes):
            ctx.print(f"mode of '{display}' changed from {fmt(st, st.mode)} to {fmt(st, mode)}")
        elif mode == st.mode and verbose:
            ctx.print(f"mode of '{display}' retained as {fmt(st, mode)}")
        if recursive and st.is_dir and ctx.fs.can_read(path):
            for n in ctx.fs.listdir(path):
                shown = display.rstrip("/") + "/" + n
                if not ctx.fs.is_symlink(normalize(path, n)):  # like chmod -R, links met on the way are left alone
                    apply(shown, normalize(path, n))
                elif verbose:
                    ctx.print(f"neither symbolic link '{shown}' nor referent has been changed")

    for f in files:
        path = ctx.resolve(f)
        if not ctx.fs.exists(path):
            ctx.error("chmod: " + spec_message("chmod", "cannotAccess", file=f, reason=ctx.fs.why_missing(path)))
            code = 1
            continue
        apply(f, path)
    return code


def _chown(ctx):
    operands = [x for x in ctx.args if not x.startswith("-")]
    files = operands[1:]  # first operand is the owner
    if not files:
        raise UsageException(spec_message(ctx.name, "missingOperand"))
    owner = operands[0].split(":")[0]
    code = 0
    for f in files:
        path = ctx.resolve(f)
        if not ctx.fs.exists(path):
            ctx.error(f"{ctx.name}: " + spec_message(ctx.name, "cannotAccess", file=f, reason=ctx.fs.why_missing(path)))
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
        ctx.error(spec_message("sudo", "usage"))
        return 1
    if ctx.args[0] in ("-l", "--list"):
        ctx.error(spec_message("sudo", "listDenied", user=user, host=ctx.shell.hostname))
        return 1
    ctx.error(spec_message("sudo", "notSudoer", user=user))
    return 1


def _su(ctx):
    ctx.error(spec_message("su", "authenticationFailure"))
    return 1


def _nl(ctx):
    a = Args.parse(ctx.args, COMMANDS["nl"])
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
            desc, code = spec_message("file", "cannotOpen", file=p), 1
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
    a = Args.parse(ctx.args, COMMANDS["paste"])
    delims = unescape(a.value("d") if a.value("d") is not None else "\t") or "\0"

    def join(cells):
        out = []
        for i, cell in enumerate(cells):
            if i and delims[(i - 1) % len(delims)] != "\0":
                out.append(delims[(i - 1) % len(delims)])
            out.append(cell)
        return "".join(out)

    errors = _Errors()
    if a.positional.count("-") > 1 and not a.has("s"):
        # several "-" operands take turns reading lines from stdin (seq 6 | paste - - -)
        stdin_lines = split_lines(ctx.stdin)
        n = a.positional.count("-")
        columns, k = [], 0
        for f in a.positional:
            if f == "-":
                columns.append(stdin_lines[k::n])
                k += 1
            else:
                columns.extend(split_lines(t) for _, t in inputs(ctx, [f], errors))
    else:
        columns = [split_lines(t) for _, t in inputs(ctx, a.positional, errors)]
    if a.has("s"):
        for col in columns:
            ctx.print(join(col))
    else:
        for r in range(max((len(c) for c in columns), default=0)):
            ctx.print(join([c[r] if r < len(c) else "" for c in columns]))
    return 1 if errors.hit else 0


def _column(ctx):
    a = Args.parse(ctx.args, COMMANDS["column"])
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
    a = Args.parse(ctx.args, COMMANDS["base64"])
    errors = _Errors()
    data = b"".join(t for _, t in inputs(ctx, a.positional, errors, raw=True))
    if a.has("d"):
        try:
            ctx.stdout.write(bytetext.decode(base64.b64decode(b"".join(data.split()), validate=True)))
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
    a = Args.parse(ctx.args, COMMANDS["mktemp"])
    template = a.positional[0] if a.positional else "tmp.XXXXXXXXXX"
    chars = string.ascii_letters + string.digits
    name = re.sub(r"X{3,}$", lambda m: "".join(random.choice(chars) for _ in m.group(0)), template)
    path = ctx.resolve(name) if "/" in template and a.value("p") is None and not a.has("t") else normalize(a.value("p") or "/tmp", name)
    if a.has("d"):
        ctx.fs.mkdir(path)
    else:
        ctx.fs.write_text(path, "")
    ctx.print(path)


def _seq(ctx):
    nums, sep, equal_width, fmt = [], "\n", False, None
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
                raise ShellError(spec_message("seq", "optionRequiresArgument", option="s"))
            i += 1
            sep = args[i]
        elif x.startswith("-s"):
            sep = x[2:]
        elif x.startswith("--separator="):
            sep = x[12:]
        elif x in ("-f", "--format") or x.startswith(("-f", "--format=")):
            if x in ("-f", "--format"):
                if i + 1 >= len(args):
                    raise ShellError(spec_message("seq", "optionRequiresArgument", option="f"))
                i += 1
                fmt = args[i]
            else:
                fmt = x[2:] if x.startswith("-f") else x[9:]
        else:
            raise ShellError(spec_message("seq", "invalidOption", option=x.lstrip("-")))
        i += 1
    if not nums or len(nums) > 3:
        raise UsageException(spec_message("seq", "missingOperand") if not nums else spec_message("seq", "extraOperand", operand=nums[3]))
    v = [float(x) for x in nums]
    first = v[0] if len(v) > 1 else 1
    step = v[1] if len(v) == 3 else 1
    last = v[-1]
    if step == 0:
        raise ShellError(spec_message("seq", "zeroIncrement"))
    decimals = max((len(x) - x.index(".") - 1 if "." in x else 0) for x in nums)
    items, k = [], 0
    while True:
        x = first + k * step  # not a running sum, so 0.1 steps don't drift
        if not ((x <= last + 1e-9) if step > 0 else (x >= last - 1e-9)):
            break
        items.append(printf_format(fmt, [repr(x)]) if fmt else f"{x:.{decimals}f}")
        if len(items) > 1_000_000:
            raise ShellError(spec_message("seq", "tooManyNumbers"))
        k += 1
    if equal_width and items:
        w = max(len(s) for s in items)
        items = [s.zfill(w) for s in items]
    if items:
        ctx.stdout.write(unescape(sep).join(items) + "\n")


def _diff(ctx):
    a = Args.parse(ctx.args, COMMANDS["diff"])
    if len(a.positional) != 2:
        raise UsageException(spec_message("diff", "missingOperandAfter", operand=a.positional[-1] if a.positional else "diff")
                             if len(a.positional) < 2 else spec_message("diff", "extraOperand", operand=a.positional[2]))

    def read(p):
        if p == "-":
            return ctx.stdin
        path = ctx.resolve(p)
        if not ctx.fs.exists(path):
            raise ShellError(spec_message("diff", "fileNotFound", file=p), 2)
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
    a = Args.parse(ctx.args, COMMANDS["id"])
    ctx.print((u if a.has("n") else "1000") if a.has("u") or a.has("g") else f"uid=1000({u}) gid=1000({u}) groups=1000({u})")


def _uptime(ctx):
    ctx.print("up 3 days, 4 hours, 12 minutes" if "-p" in ctx.args
              else f" {utc_now():%H:%M:%S} up 3 days,  4:12,  1 user,  load average: 0.08, 0.03, 0.01")


def _free(ctx):
    human = any("h" in x for x in ctx.args)
    ctx.stdout.write(
        "               total        used        free      shared  buff/cache   available\n"
        + ("Mem:           7.6Gi       1.9Gi       3.8Gi        12Mi       2.1Gi       5.5Gi\nSwap:          2.0Gi          0B       2.0Gi\n"
           if human else
           "Mem:         8014880     1992344     3984512       12288     2201024     5748660\nSwap:        2097148           0     2097148\n"))


def register(shell) -> None:
    r = shell.register_command
    r("exit", _exit)
    r("return", _return)
    r("break", _break)
    r("continue", _break)
    r("shift", _shift)
    r("trap", _trap)
    r("shopt", _shopt)
    r("set", _set)
    r("local", _declare)
    r("declare", _declare)
    r("typeset", _declare)
    r("readonly", _declare)
    r("read", _read)
    r("mapfile", _mapfile)
    r("readarray", _mapfile)
    r("getopts", _getopts)
    r("exec", _exec)
    r("wait", _wait)
    r("jobs", lambda ctx: 0)
    r("disown", lambda ctx: 0)
    r("kill", _kill)
    r("hash", _hash)
    r("builtin", _builtin)
    r("pushd", _pushd)
    r("popd", _popd)
    r("dirs", _dirs)
    r("ulimit", _ulimit)
    r("times", lambda ctx: ctx.stdout.write("0m0.010s 0m0.005s\n0m0.000s 0m0.000s\n") and 0)
    r("umask", _umask)
    r("eval", _eval)
    r("let", _let)
    r("command", _command)
    r("printenv", _printenv)
    r("sleep", _sleep)
    r("timeout", _timeout)
    r("time", _time)
    r("clear", lambda ctx: 0)
    r("basename", _basename)
    r("dirname", _dirname)
    r("realpath", _realpath)
    r("readlink", _realpath)
    r("stat", _stat)
    r("du", _du)
    r("df", _df)
    r("chmod", _chmod)
    r("chown", _chown)
    r("chgrp", _chown)
    r("sudo", _sudo)
    r("su", _su)
    r("nl", _nl)
    r("less", _pager)
    r("more", _pager)
    r("file", _file)
    r("rev", _rev)
    r("tac", _tac)
    r("paste", _paste)
    r("column", _column)
    r("base64", _base64)
    for name, algo in (("md5sum", "md5"), ("sha1sum", "sha1"), ("sha256sum", "sha256"), ("sha512sum", "sha512")):
        r(name, _hasher(algo))
    r("mktemp", _mktemp)
    r("seq", _seq)
    r("diff", _diff)
    r("id", _id)
    r("uptime", _uptime)
    r("free", _free)
    r("nproc", lambda ctx: ctx.print("4"))
