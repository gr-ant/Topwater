"""Variable builtins: export, unset, env, alias, declare and its family, read, mapfile, printenv. See docs/architecture.md."""
from __future__ import annotations

import re

from .._spec import COMMANDS, DIAGNOSTICS, ERRNO, GETOPT
from ..parser import is_name
from ..vfs import ShellError, UsageException
from . import Args, exec_failure, spec_format, spec_message


def _flag_args(args) -> tuple[str, list[str]]:
    """Leading -x style arguments joined, and the rest."""
    i = 0
    while i < len(args) and args[i].startswith("-"):
        i += 1
    return "".join(args[:i]), list(args[i:])


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


def _export(ctx):
    from ..parser import is_name
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
                raise ShellError(spec_format(DIAGNOSTICS["notValidIdentifier"], name=a))
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


async def _unset(ctx):
    flags, args = _flag_args(ctx.args)
    sh = ctx.shell
    code = 0
    for arg in args:
        try:
            if "f" not in flags and not is_name(arg.split("[")[0]):
                if "v" in flags:
                    raise ShellError(spec_format(DIAGNOSTICS["notValidIdentifier"], name=arg))
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


async def _env(ctx):
    """env [-i] [-u NAME] [-C DIR] [-0] [NAME=VALUE]... [COMMAND [ARG]...]: the exported variables, or COMMAND run with
    changed ones."""
    sh = ctx.shell
    args = list(ctx.args)
    sets: list[tuple[str, str]] = []
    unsets: list[str] = []
    clear = null = False
    chdir: str | None = None
    longs = {"ignore-environment": "i", "unset": "u", "chdir": "C", "null": "0"}
    options = True
    while args:
        a = args[0]
        if not options or not a.startswith("-"):
            if a.find("=") <= 0:
                break
            k, _, v = args.pop(0).partition("=")
            sets.append((k, v))
            options = False  # options end at the first operand, as with getopt's "+"
            continue
        if a == "--":
            args.pop(0)
            options = False  # assignments may still follow
            continue
        if a == "-":
            clear = True
            args.pop(0)
        elif a.startswith("--"):
            name, eq, inline = a[2:].partition("=")
            c = longs.get(name)
            if c is None:
                raise UsageException(spec_format(GETOPT["unrecognizedOption"], option=a))
            args.pop(0)
            if c in "uC":
                if not eq:
                    if not args:
                        raise UsageException(spec_format(GETOPT["missingLongArgument"], option=name))
                    inline = args.pop(0)
                if c == "u":
                    unsets.append(inline)
                else:
                    chdir = inline
            elif c == "i":
                clear = True
            else:
                null = True
        elif a.startswith("-") and len(a) > 1:
            args.pop(0)
            k = 1
            while k < len(a):
                c = a[k]
                if c in "uC":
                    value = a[k + 1:] or (args.pop(0) if args else None)
                    if value is None:
                        raise UsageException(spec_format(GETOPT["missingShortArgument"], option=c))
                    if c == "u":
                        unsets.append(value)
                    else:
                        chdir = value
                    break
                if c == "i":
                    clear = True
                elif c == "0":
                    null = True
                else:
                    raise UsageException(spec_format(GETOPT["invalidOption"], option=c))
                k += 1

    def show():
        for k in sorted(sh.env):
            if sh.is_exported(k):
                ctx.stdout.write(f"{k}={sh.env[k]}" + ("\0" if null else "\n"))

    if not args and not sets and not clear and not unsets and chdir is None:
        show()
        return 0
    saved = (sh.env, sh.var_attrs, sh.cwd)
    sh.env = {} if clear else {k: v for k, v in saved[0].items() if sh.is_exported(k) and k not in unsets}
    sh.var_attrs = {k: "x" for k in sh.env}
    for k, v in sets:
        sh.env[k] = v
        sh.var_attrs[k] = "x"
    try:
        if chdir is not None:
            target = ctx.resolve(chdir)
            if not ctx.fs.is_dir(target):
                why = ctx.fs.why_missing(target) if not ctx.fs.exists(target) else ERRNO["ENOTDIR"]
                raise ShellError(spec_message("env", "cannotChdir", dir=chdir, errno=why), 125)
            sh._cwd = target  # the command's working directory only; PWD stays as it was, as with a real chdir
        if not args:
            show()
            return 0
        if "PATH" not in sh.env and "/" not in args[0]:  # with no PATH, env searches /bin and /usr/bin, as execvp does
            args[0] = next((d + args[0] for d in ("/bin/", "/usr/bin/") if ctx.fs.is_file(d + args[0])), args[0])
        failure = exec_failure(sh, args[0])
        if failure is not None:
            raise ShellError(spec_message("env", "runFailed", command=args[0], errno=failure[0]), failure[1])
        return await sh._invoke(args, ctx.io, skip_functions=True)
    finally:
        sh.env, sh.var_attrs, sh._cwd = saved


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
            ctx.error(f"{ctx.shell.err_prefix}alias: " + spec_message("alias", "notFound", name=k))
            code = 1
    return code


def _unalias(ctx):
    args = list(ctx.args)
    all_ = False
    while args and args[0].startswith("-") and len(args[0]) > 1:
        a = args.pop(0)
        if a == "--":
            break
        bad = next((c for c in a[1:] if c != "a"), None)
        if bad is not None:
            ctx.error(f"{ctx.shell.err_prefix}unalias: " + spec_message("unalias", "builtinInvalidOption", option="-" + bad))
            ctx.error("unalias: " + spec_message("unalias", "usage"))
            return 2
        all_ = True
    if all_:
        ctx.shell.aliases.clear()
        return 0
    if not args:
        ctx.error("unalias: " + spec_message("unalias", "usage"))
        return 2
    code = 0
    for a in args:
        if ctx.shell.aliases.pop(a, None) is None:
            ctx.error(f"{ctx.shell.err_prefix}unalias: " + spec_message("unalias", "notFound", name=a))
            code = 1
    return code


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


def register(shell) -> None:
    r = shell.register_command
    r("export", _export)
    r("unset", _unset)
    r("env", _env)
    r("alias", _alias)
    r("unalias", _unalias)
    r("local", _declare)
    r("declare", _declare)
    r("typeset", _declare)
    r("readonly", _declare)
    r("read", _read)
    r("mapfile", _mapfile)
    r("readarray", _mapfile)
    r("printenv", _printenv)
