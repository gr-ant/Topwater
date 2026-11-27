"""python3: CPython compiled to WebAssembly, run over this machine's files (wasi.py). Opt-in: it exists only after the
host calls Shell.allow_python. See docs/python.md."""
from __future__ import annotations

import asyncio
import contextvars
import functools
import re
import threading
import time
from dataclasses import dataclass

from .. import bytetext, wasi
from .._spec import COMMANDS
from . import spec_message


@dataclass(frozen=True)
class PythonOptions:
    """What Shell.allow_python takes. max_memory: bytes of guest memory per call (the interpreter itself starts at about
    40 MB; a program past the limit gets MemoryError). artifacts: the folder holding python.wasm.xz and python314.zip
    (None: the topwater-python add-on's). cache_dir: a folder of yours where the compiled interpreter is kept between
    processes, so only the first process pays the compile (seconds); None compiles once per process. Trust it like your
    own code: a compiled module is machine code."""
    max_memory: int = 256 << 20
    artifacts: str | None = None
    cache_dir: str | None = None


def register(shell, options: PythonOptions | None = None) -> None:
    """Registers python3 when the host enabled it; register_all calls this without options, which registers nothing."""
    if options is None:
        return
    wasi.runtime(options.artifacts, options.cache_dir)  # fails here, in the host's code, when the add-on can't run
    spec = COMMANDS["python3"]
    shell.register_command("python3", functools.partial(_python3, options=options), spec.summary, spec.help)


_STACK_LOCK = threading.Lock()
CANCEL_WAIT = 5.0  # seconds a cancelled call waits for its guest to stop (it stops at the next 10 ms tick or host call)


def _runs(output: list[tuple[int, bytes]]):
    """Consecutive writes to one stream joined, so a multi-byte character split across writes decodes whole."""
    which, chunk = 0, bytearray()
    for w, data in output:
        if w != which and chunk:
            yield which, bytes(chunk)
            chunk = bytearray()
        which = w
        chunk += data
    if chunk:
        yield which, bytes(chunk)


def _in_thread(fn) -> asyncio.Future:
    """Runs fn on a new thread with a stack large enough for the guest, in this context (the agent's user, the running
    shell); the event loop stays free for the HTTP bridge."""
    loop = asyncio.get_running_loop()
    fut = loop.create_future()
    context = contextvars.copy_context()

    def work() -> None:
        try:
            result, error = context.run(fn), None
        except BaseException as e:  # handed to the awaiting command, which raises it there
            result, error = None, e

        def done() -> None:
            if not fut.done():
                fut.set_exception(error) if error is not None else fut.set_result(result)
        try:
            loop.call_soon_threadsafe(done)
        except RuntimeError:  # the loop is gone: nobody waits for the result
            pass

    with _STACK_LOCK:
        previous = threading.stack_size(wasi.THREAD_STACK)
        try:
            threading.Thread(target=work, name="topwater-python3", daemon=True).start()
        finally:
            threading.stack_size(previous)
    return fut


def _bridge(web, loop, deadline: float):
    """The HTTP bridge's sender: each request goes through the shell's web access (its policy, credentials, limits and
    recording), on the event loop, one hop at a time; the guest follows redirects itself, each checked again."""
    from ..commands.web import _NO_NETWORK
    from ..shell.types import CallTimeout
    from ..webaccess import WebFailure, WebUrl

    session = web or _NO_NETWORK

    async def send(method, url, headers, body, timeout):
        try:
            u = WebUrl.parse(url)
            r = await session.send(u, method, headers, body if body or method in ("POST", "PUT", "PATCH") else None,
                                   timeout, deadline)
        except WebFailure as f:
            # curl's wording for the same failure (bad_url: badUrl, ...)
            key = "blocked" if f.blocked else re.sub("_(.)", lambda m: m.group(1).upper(), f.kind)
            reason = spec_message("curl", key, reason=f.blocked or "", host=f.host, port=str(f.port), ms=f.detail or "0",
                                  bytes="0", max=f.detail, scheme=f.host, request=f.detail) \
                if key in COMMANDS["curl"].messages else f.kind
            failed = spec_message("python3", "httpFailed", method=method, url=url, reason=reason)
            return (wasi.HTTP_TIMEOUT if f.kind == "timeout" else wasi.HTTP_NETWORK), failed.encode()
        except CallTimeout:
            return timed_out(method, url, timeout)
        if r.truncated:
            reason = spec_message("curl", "tooLarge")
            return wasi.HTTP_NETWORK, spec_message("python3", "httpFailed", method=method, url=url, reason=reason).encode()
        return wasi.HTTP_OK, wasi.encode_response(r.status, r.headers, r.body)

    def timed_out(method, url, timeout):
        reason = spec_message("curl", "timeout", ms=str(int(timeout * 1000)), bytes="0")
        return wasi.HTTP_TIMEOUT, spec_message("python3", "httpFailed", method=method, url=url, reason=reason).encode()

    def http(method, url, headers, body, timeout):
        try:
            return _on_loop(send(method, url, headers, body, timeout), loop, deadline)
        except TimeoutError:
            return timed_out(method, url, timeout)
    return http


def _on_loop(coro, loop, deadline: float):
    """Runs coro on the shell's event loop from the guest's thread and waits for it, never much past the deadline (a loop
    that stopped must not hold the thread forever)."""
    import concurrent.futures

    fut = asyncio.run_coroutine_threadsafe(coro, loop)
    try:
        return fut.result(timeout=max(0.0, deadline - time.monotonic()) + 5)
    except concurrent.futures.TimeoutError:
        fut.cancel()
        raise TimeoutError() from None


async def _python3(ctx, options: PythonOptions) -> int:
    sh, spec = ctx.shell, COMMANDS["python3"]
    args = ctx.args
    if args[:1] in (["-h"], ["-?"], ["--help"], ["--help-all"]):  # the spec's help, in our words
        ctx.stdout.write(spec.help)
        return 0
    if args[:1] in (["-V"], ["--version"]):
        ctx.print(spec.version)
        return 0
    try:
        rt = wasi.runtime(options.artifacts, options.cache_dir)
    except wasi.PythonUnavailable as e:
        ctx.stderr.write(f"python3: {spec_message('python3', 'unavailable', reason=str(e))}\n")
        return 1
    env = sorted((k, v) for k, v in sh.env.items() if k != "PWD" and sh.is_exported(k)) + [("PWD", sh.cwd)]
    deadline = min(sh._deadline, time.monotonic() + sh.max_call_duration)
    loop = asyncio.get_running_loop()
    mounts = sh._mounts

    def fetch() -> bool:
        """A mounted file or folder the program reached before it was fetched: fetch it now (the loop is free while the
        guest waits on its thread), unlike other commands, which say "run the command again"."""
        if not any(m.misses for m in mounts.items.values()):
            return False
        try:
            _on_loop(mounts.after(), loop, deadline)
        except TimeoutError:
            return False
        return True

    guest = wasi.Guest(sh.fs, ["python3", *args], env, bytetext.encode(ctx.stdin), sh.max_string_bytes, options.max_memory,
                       rt.stdlib, deadline, _bridge(sh._web, loop, deadline), fetch if mounts.items else None)
    work = _in_thread(lambda: wasi.run(rt, guest))
    try:
        outcome = await asyncio.shield(work)
    except asyncio.CancelledError:
        # the guest stops at its next host call or epoch tick and saves nothing more; wait for it, so no write of this
        # call lands after the next one started
        guest.cancelled = True
        guest.wake.set()
        try:
            await asyncio.wait_for(asyncio.shield(work), CANCEL_WAIT)
        except BaseException:  # noqa: BLE001 (its outcome no longer matters; the cancel is what we pass on)
            pass
        raise
    except wasi.OutputFull:
        raise sh.too_large("output", "write to a file in smaller pieces or filter earlier in the pipeline") from None
    for which, data in _runs(guest.output):  # in the order written, so 2>&1 interleaves them as a real process's
        (ctx.stdout if which == 1 else ctx.stderr).write(bytetext.decode(data))
    for note in dict.fromkeys(guest.notes):  # a file a mount had not fetched yet: the agent should run it again
        ctx.stderr.write(f"python3: {note}\n")
    for path, errno in guest.write_back_errors:
        ctx.stderr.write(f"python3: {spec_message('python3', 'writeBack', file=path, errno=wasi.errno_text(errno))}\n")
    sh.check_deadline()  # past the call's (or `timeout`'s) limit, whatever the program saw: ends it as for any command
    if outcome.timed_out:
        ctx.stderr.write(f"python3: {spec_message('python3', 'stopped')}\n")
        return 1
    if outcome.stack_overflow:
        ctx.stderr.write(f"python3: {spec_message('python3', 'stackOverflow')}\n")
        return 1
    if outcome.aborted:
        ctx.stderr.write(f"python3: {spec_message('python3', 'aborted')}\n")
        return 134
    if outcome.trap is not None:
        ctx.stderr.write(f"python3: {spec_message('python3', 'fatal', reason=outcome.trap)}\n")
        return 1
    code = outcome.code & 0xFF
    return code or (1 if guest.write_back_errors else 0)
