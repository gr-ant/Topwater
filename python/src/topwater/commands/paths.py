"""Directories and path names: cd, pwd, pushd, popd, dirs, basename, dirname, realpath, mktemp. See docs/architecture.md."""
from __future__ import annotations

import random
import re
import string

from .._spec import COMMANDS
from ..vfs import ShellError, UsageException, normalize
from . import Args, spec_message


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


def _cd(ctx):
    env = ctx.shell.env
    args = list(ctx.args)
    flags = []
    while args and args[0] in ("-L", "-P", "-e", "-@"):
        flags.append(args.pop(0))
    if args and args[0] == "--":
        args.pop(0)
    if len(args) > 1:
        raise ShellError(spec_message("cd", "tooManyArguments"))
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
        raise ShellError(spec_message("cd", "failed", dir=target, errno=ctx.fs.why_missing(path)))
    if not ctx.fs.is_dir(path):
        raise ShellError(spec_message("cd", "failed", dir=target, errno="Not a directory"))
    if not ctx.fs.can_execute(path):
        raise ShellError(spec_message("cd", "failed", dir=target, errno="Permission denied"))
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


def register(shell) -> None:
    r = shell.register_command
    r("cd", _cd)
    r("pwd", _pwd)
    r("pushd", _pushd)
    r("popd", _popd)
    r("dirs", _dirs)
    r("basename", _basename)
    r("dirname", _dirname)
    r("realpath", _realpath)
    r("readlink", _realpath)
    r("mktemp", _mktemp)
