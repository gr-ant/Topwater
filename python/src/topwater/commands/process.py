"""Processes and time: ps, wait, jobs, disown, kill, ulimit, times, umask, sleep, timeout, time. See docs/architecture.md."""
from __future__ import annotations

import asyncio
import re
import time

from ..shell import ExitSignal
from ..vfs import ShellError, UsageException
from . import spec_message


def _duration(s: str) -> float:
    m = re.fullmatch(r"(\d+(?:\.\d+)?)([smhd]?)", s)
    if not m:
        raise ShellError(spec_message("sleep", "invalidTimeInterval", value=s))
    return float(m.group(1)) * {"": 1, "s": 1, "m": 60, "h": 3600, "d": 86400}[m.group(2)]


_SIGNALS = {"HUP": 1, "INT": 2, "QUIT": 3, "KILL": 9, "TERM": 15}


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


def _ulimit(ctx):
    if any(not a.startswith("-") for a in ctx.args):
        return 0  # setting a limit: accepted
    ctx.print("1024" if "-n" in ctx.args else "8192" if "-s" in ctx.args else "0" if "-c" in ctx.args else "unlimited")
    return 0


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


def register(shell) -> None:
    r = shell.register_command
    r("ps", lambda ctx: ctx.stdout.write("    PID TTY          TIME CMD\n      1 pts/0    00:00:00 bash\n   4242 pts/0    00:00:00 ps\n") and 0)
    r("wait", _wait)
    r("jobs", lambda ctx: 0)
    r("disown", lambda ctx: 0)
    r("kill", _kill)
    r("ulimit", _ulimit)
    r("times", lambda ctx: ctx.stdout.write("0m0.010s 0m0.005s\n0m0.000s 0m0.000s\n") and 0)
    r("umask", _umask)
    r("sleep", _sleep)
    r("timeout", _timeout)
    r("time", _time)
