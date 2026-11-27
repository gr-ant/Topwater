"""WASI preview1 over the shell's virtual filesystem, for the python3 command (commands/python.py).

The guest is bashkit's CPython build (assets/python-wasm). It reaches only what this module hands it: the virtual
filesystem as the agent's user, the command's stdin and output, a clock, random bytes, and HTTP through a callback the
command supplies. There is no host filesystem, socket, process or thread API: sockets answer ENOTSUP. The wasmtime
package is imported only when the first python3 call needs it, so the core package keeps no dependency.
See docs/python.md and .team/design/python3-wasm.md (the contract the C# and TypeScript ports follow).
"""
from __future__ import annotations

import hashlib
import os
import struct
import threading
import time
from pathlib import Path

from . import bytetext
from ._spec import ERRNO
from .vfs import ShellError, get_parent


# ---------- artifacts and runtime ----------

ARTIFACT_SHA256 = {  # assets/python-wasm/MANIFEST
    "python.wasm.xz": "93b66c1e1357a3b77481fee8e14448b55ca83a0dec4b05ec0284b33b58099b75",
    "python314.zip": "a2fbfc15b11fe851fffe3a84369f4d8c73992904ea23bbc045d89373737e15ea",
}
STDLIB_PATH = "/usr/local/lib/python314.zip"  # where the snapshot expects the stdlib; served from memory, read-only
# CPython recurses in C (parser, repr, nested containers): the default 512 KB overflows before RecursionError. 2 MB is the
# most wasmtime-py accepts (its async stack size, not settable from Python); more crashes the compile.
MAX_WASM_STACK = 2 << 20
THREAD_STACK = 32 << 20  # the native stack of the thread the guest runs on: wasm's stack plus the host's frames
EPOCH_TICK = 0.01  # seconds between epoch ticks: how late a timed-out guest may notice


class PythonUnavailable(Exception):
    """The add-on can't run: wasmtime missing, artifacts missing or altered."""


def find_artifacts(folder: str | os.PathLike | None) -> Path:
    if folder is not None:
        return Path(folder)
    try:
        import topwater_python  # the add-on distribution (python/addons/topwater-python)
    except ImportError:
        raise PythonUnavailable("install it with pip install 'topwater[python]'") from None
    return Path(topwater_python.ARTIFACTS)


class Runtime:
    """One compiled interpreter per process and folder: the engine, the module, and a linker whose functions serve the
    guest of the thread that calls them. Each call gets a fresh store and instance."""

    def __init__(self, folder: Path, cache_dir: str | os.PathLike | None):
        try:
            import wasmtime
        except ImportError:
            raise PythonUnavailable("the wasmtime package is not installed") from None
        try:
            import lzma
        except ImportError:  # a Python built without liblzma
            raise PythonUnavailable("this Python has no lzma module to unpack python.wasm.xz") from None

        self.wasmtime = wasmtime
        wasm_xz = _verified(folder / "python.wasm.xz")
        self.stdlib = _verified(folder / "python314.zip")
        config = wasmtime.Config()
        config.epoch_interruption = True
        config.max_wasm_stack = MAX_WASM_STACK
        self.engine = wasmtime.Engine(config)
        digest = ARTIFACT_SHA256["python.wasm.xz"][:16]
        cached = Path(cache_dir) / f"python-{digest}-wasmtime-{_version(wasmtime)}.cwasm" if cache_dir is not None else None
        self.module = None
        if cached is not None and cached.is_file():
            try:  # the host's own cache directory: as trusted as the host's code
                self.module = wasmtime.Module.deserialize_file(self.engine, str(cached))
            except Exception:
                self.module = None
        if self.module is None:
            self.module = wasmtime.Module(self.engine, lzma.decompress(wasm_xz))
            if cached is not None:
                cached.parent.mkdir(parents=True, exist_ok=True)
                tmp = cached.with_suffix(f".{os.getpid()}.tmp")
                tmp.write_bytes(self.module.serialize())
                os.replace(tmp, cached)
        self.linker = wasmtime.Linker(self.engine)
        _define(self.linker, wasmtime)
        self._calls = 0
        self._lock = threading.Lock()

    def ticking(self):
        """Keeps the epoch ticking while a call runs (a store's deadline is counted in ticks); stops when none does."""
        runtime = self

        class _Ticking:
            def __enter__(self):
                with runtime._lock:
                    runtime._calls += 1
                    if runtime._calls == 1:
                        threading.Thread(target=runtime._tick, name="topwater-python-epoch", daemon=True).start()

            def __exit__(self, *exc):
                with runtime._lock:
                    runtime._calls -= 1

        return _Ticking()

    def _tick(self) -> None:
        while True:
            time.sleep(EPOCH_TICK)
            with self._lock:
                if self._calls == 0:
                    return
            self.engine.increment_epoch()


_RUNTIMES: dict[tuple, Runtime] = {}
_RUNTIMES_LOCK = threading.Lock()


def runtime(folder: str | os.PathLike | None, cache_dir: str | os.PathLike | None = None) -> Runtime:
    """The process's compiled interpreter for these artifacts: the first call compiles it (seconds), later calls reuse it."""
    path = find_artifacts(folder).resolve()
    key = (str(path), None if cache_dir is None else str(cache_dir))
    with _RUNTIMES_LOCK:
        rt = _RUNTIMES.get(key)
        if rt is None:
            rt = _RUNTIMES[key] = Runtime(path, cache_dir)
        return rt


def _verified(path: Path) -> bytes:
    try:
        data = path.read_bytes()
    except OSError as e:
        raise PythonUnavailable(f"{path.name}: {e.strerror}") from None
    if hashlib.sha256(data).hexdigest() != ARTIFACT_SHA256[path.name]:
        raise PythonUnavailable(f"{path.name}: not the expected file (its sha256 differs)")
    return data


def _version(wasmtime) -> str:
    try:
        from importlib.metadata import version
        return version("wasmtime")
    except Exception:
        return getattr(wasmtime, "__version__", "unknown")


# ---------- guest state ----------

# WASI preview1 errno values (not Linux's)
SUCCESS, E2BIG, EACCES, EBADF, EEXIST, EFAULT, EFBIG, EINVAL, EIO, EISDIR, ELOOP, EMFILE = 0, 1, 2, 8, 20, 21, 22, 28, 29, 31, 32, 33
ENAMETOOLONG, ENOENT, ENOSPC, ENOTDIR, ENOTEMPTY, ENOTSUP, EPERM, EROFS, ESPIPE = 37, 44, 51, 54, 55, 58, 63, 69, 70
_BY_TEXT = {ERRNO["ENOENT"]: ENOENT, ERRNO["EACCES"]: EACCES, ERRNO["EPERM"]: EPERM, ERRNO["ENOTDIR"]: ENOTDIR,
            ERRNO["EISDIR"]: EISDIR, ERRNO["EEXIST"]: EEXIST, ERRNO["ENOTEMPTY"]: ENOTEMPTY, ERRNO["EINVAL"]: EINVAL,
            ERRNO["ENOSPC"]: ENOSPC, ERRNO["EBADF"]: EBADF, ERRNO["ELOOP"]: ELOOP}
_TEXT = {v: k for k, v in _BY_TEXT.items()}

FILETYPE_UNKNOWN, FILETYPE_DIRECTORY, FILETYPE_REGULAR_FILE, FILETYPE_SYMBOLIC_LINK = 0, 3, 4, 7
OFLAGS_CREAT, OFLAGS_DIRECTORY, OFLAGS_EXCL, OFLAGS_TRUNC = 1, 2, 4, 8
FDFLAGS_APPEND = 1
LOOKUP_SYMLINK_FOLLOW = 1
RIGHTS_FD_READ, RIGHTS_FD_WRITE, RIGHTS_FD_READDIR, RIGHTS_ALL = 1 << 1, 1 << 6, 1 << 14, (1 << 30) - 1
FSTFLAGS_MTIM, FSTFLAGS_MTIM_NOW = 1 << 2, 1 << 3
CLOCK_REALTIME, CLOCK_MONOTONIC = 0, 1
EVENTTYPE_CLOCK, EVENTTYPE_FD_READ, EVENTTYPE_FD_WRITE = 0, 1, 2
SUBCLOCKFLAGS_ABSTIME = 1
ROOT_FD = 3  # the one preopen, "/": the snapshot's wasi-libc cached exactly this table
MAX_FDS = 1024
MAX_PATH = 4096
MAX_IOVS = 1024
MAX_RANDOM = 1 << 20
MAX_SLEEP_SLICE = 0.25


class WasiError(Exception):
    def __init__(self, errno: int):
        super().__init__(errno)
        self.errno = errno


class ProcExit(Exception):
    """proc_exit: the guest ends the call with this status."""

    def __init__(self, code: int):
        super().__init__(code)
        self.code = code


class Interrupted(Exception):
    """A sleep reached the call's deadline: the guest is stopped as the epoch would stop a busy one."""


class OutputFull(Exception):
    """stdout plus stderr passed the shell's string limit: the guest is stopped and the call ends as for any command."""


def _fs(fn, *args, **kwargs):
    """A filesystem call for the guest: its result, or WasiError with the errno. A mounted file or folder the source has not
    been asked for yet is fetched (the host waits, within the call's time) and the call tried once more."""
    try:
        return fn(*args, **kwargs)
    except ShellError as e:
        g = getattr(_local, "guest", None)
        if _BY_TEXT.get(str(e).rsplit(": ", 1)[-1]) is None and g is not None and g.fetch is not None and g.fetch():
            try:
                return fn(*args, **kwargs)
            except ShellError as again:
                raise WasiError(errno_of(again)) from None
        raise WasiError(errno_of(e)) from None


def errno_of(e: ShellError) -> int:
    """The WASI errno for a filesystem error, by its text ("path: Permission denied"). Anything else (a mounted file not
    fetched yet) is EIO, and its text is kept for the command to print after the program."""
    errno = _BY_TEXT.get(str(e).rsplit(": ", 1)[-1])
    if errno is None:
        g = getattr(_local, "guest", None)
        if g is not None:
            g.notes.append(str(e))
        return EIO
    return errno


class _File:
    __slots__ = ("path", "data", "pos", "read", "write", "append", "dirty", "stored", "mtime")

    def __init__(self, path: str, data, read: bool, write: bool, append: bool, stored: int, mtime: int):
        self.path, self.data, self.pos = path, data, 0
        self.read, self.write, self.append, self.dirty, self.stored, self.mtime = read, write, append, False, stored, mtime


class _Dir:
    __slots__ = ("path", "entries")

    def __init__(self, path: str):
        self.path, self.entries = path, None


_STDIN, _STDOUT, _STDERR, _ROOT = "stdin", "stdout", "stderr", "root"


class Guest:
    """Everything one python3 call's instance can reach. http(method, url, headers, body, timeout) -> (code, payload)
    sends a request for the HTTP bridge; None means no network."""

    def __init__(self, fs, args: list[str], env: list[tuple[str, str]], stdin: bytes, output_cap: int, max_memory: int,
                 stdlib: bytes, deadline: float, http=None, fetch=None):
        self.fs = fs
        self.args = [bytetext.encode(a).replace(b"\0", b"") for a in args]
        self.env = [bytetext.encode(f"{k}={v}") for k, v in env if k and "=" not in k and "\0" not in k + v]
        self.stdin, self.stdin_pos = stdin, 0
        self.stdout, self.stderr = bytearray(), bytearray()
        self.output_cap = output_cap
        self.max_memory = max_memory
        self.stdlib = stdlib
        self.deadline = deadline
        self.started = time.perf_counter_ns()
        self.fds: list = [_STDIN, _STDOUT, _STDERR, _ROOT]
        self.buffered = 0  # bytes held in open files' buffers, at most max_memory in all
        self.http, self.pending = http, None
        self.fetch = fetch  # fetch() -> bool: fetches what mounts were asked for and missed; None: no mounts
        self.write_back_errors: list[tuple[str, int]] = []
        self.notes: list[str] = []
        self.memory = None

    # -- descriptors --

    def alloc(self, entry) -> int:
        for i in range(ROOT_FD + 1, len(self.fds)):
            if self.fds[i] is None:
                self.fds[i] = entry
                return i
        if len(self.fds) >= MAX_FDS:
            raise WasiError(EMFILE)
        self.fds.append(entry)
        return len(self.fds) - 1

    def fd(self, fd: int):
        if 0 <= fd < len(self.fds) and self.fds[fd] is not None:
            return self.fds[fd]
        raise WasiError(EBADF)

    def resolve(self, dirfd: int, raw: bytes) -> str:
        """The absolute path for a guest path relative to a directory descriptor; ".." stops at "/" (the whole sandbox)."""
        if not raw:
            raise WasiError(ENOENT)
        if len(raw) > MAX_PATH:
            raise WasiError(ENAMETOOLONG)
        if b"\0" in raw:
            raise WasiError(EINVAL)
        d = self.fd(dirfd)
        base = "/" if d is _ROOT else d.path if isinstance(d, _Dir) else None
        if base is None:
            raise WasiError(ENOTDIR)
        return self.fs.normalize_physical(base, bytetext.decode(raw))

    # -- files --

    def reserve(self, extra: int) -> None:
        if self.buffered + extra > self.max_memory:
            raise WasiError(ENOSPC)
        self.buffered += extra

    def room(self, f: _File) -> int:
        """How large the file may grow: what the filesystem has free, plus what it holds of this file already."""
        return self.fs.capacity - self.fs.used_bytes + f.stored if self.fs.run_as is not None else 1 << 62

    def save(self, f: _File) -> None:
        if f.dirty:
            _fs(self.fs.write_bytes, f.path, bytes(f.data))
            f.dirty, f.stored = False, len(f.data)

    def flush_all(self) -> None:
        """At exit: files the program wrote but never closed are saved, as a real process's are."""
        for entry in self.fds:
            if isinstance(entry, _File) and entry.dirty:
                try:
                    self.save(entry)
                except WasiError as e:
                    self.write_back_errors.append((entry.path, e.errno))

    def write_output(self, which: bytearray, data: bytes) -> None:
        if len(self.stdout) + len(self.stderr) + len(data) > self.output_cap:
            raise OutputFull()
        which += data


def errno_text(errno: int) -> str:
    return _TEXT.get(errno, "Input/output error")


# ---------- wasi functions ----------

_local = threading.local()  # the guest of the call running on this thread: the linker is shared by every call


def _guest() -> Guest:
    return _local.guest


def _mem(caller):
    g = _guest()
    if g.memory is None:
        g.memory = caller.get("memory")
        if g.memory is None:
            raise WasiError(EFAULT)
    return g.memory


def _check(caller, ptr: int, n: int) -> None:
    if (ptr & 0xFFFFFFFF) + (n & 0xFFFFFFFF) > _mem(caller).data_len(caller):
        raise WasiError(EFAULT)


def _read(caller, ptr: int, n: int) -> bytes:
    _check(caller, ptr, n)
    ptr &= 0xFFFFFFFF
    return bytes(_mem(caller).read(caller, ptr, ptr + (n & 0xFFFFFFFF)))


def _write(caller, ptr: int, data: bytes) -> None:
    mem = _mem(caller)
    ptr &= 0xFFFFFFFF
    if ptr + len(data) > mem.data_len(caller):
        raise WasiError(EFAULT)
    if data:
        mem.write(caller, data, ptr)


def _u32(caller, ptr: int, v: int) -> None:
    _write(caller, ptr, struct.pack("<I", v & 0xFFFFFFFF))


def _u64(caller, ptr: int, v: int) -> None:
    _write(caller, ptr, struct.pack("<Q", v & 0xFFFFFFFFFFFFFFFF))


def _path(caller, ptr: int, n: int) -> bytes:
    if n & 0xFFFFFFFF > MAX_PATH:
        raise WasiError(ENAMETOOLONG)
    return _read(caller, ptr, n)


def _iovs(caller, ptr: int, count: int) -> list[tuple[int, int]]:
    if not 0 <= count <= MAX_IOVS:
        raise WasiError(EINVAL)
    raw = _read(caller, ptr, count * 8)
    return [struct.unpack_from("<II", raw, i * 8) for i in range(count)]


def _string_table(caller, items: list[bytes], ptrs: int, buf: int) -> None:
    offsets, blob, at = [], bytearray(), buf & 0xFFFFFFFF
    for item in items:
        offsets.append(at + len(blob))
        blob += item + b"\0"
    _write(caller, ptrs, struct.pack(f"<{len(offsets)}I", *offsets))
    _write(caller, buf, bytes(blob))


def _ino(path: str) -> int:
    return int.from_bytes(hashlib.blake2b(path.encode("utf-8", "surrogateescape"), digest_size=8).digest(), "little") | 1


def _filestat(filetype: int, size: int, mtime: int, ino: int, nlink: int = 1) -> bytes:
    return struct.pack("<QQB7xQQQQQ", 1, ino, filetype, nlink, size, mtime, mtime, mtime)


def _ns(when) -> int:
    from datetime import timezone
    return int(when.replace(tzinfo=timezone.utc).timestamp() * 1_000_000_000) if when is not None else 0


def _stat_path(g: Guest, path: str, follow: bool) -> bytes:
    if path == STDLIB_PATH:
        return _filestat(FILETYPE_REGULAR_FILE, len(g.stdlib), 0, _ino(path))
    st = _fs(g.fs.stat, path, follow)
    kind = FILETYPE_SYMBOLIC_LINK if st.is_symlink else FILETYPE_DIRECTORY if st.is_dir else FILETYPE_REGULAR_FILE
    return _filestat(kind, st.size, _ns(st.modified), _ino(path), st.links)


def _errno(fn):
    """A WASI function: WasiError becomes its errno, success 0. Exits, timeouts and full output pass through."""
    def wrapper(caller, *args):
        try:
            fn(caller, *args)
            return SUCCESS
        except WasiError as e:
            return e.errno
    wrapper.__name__ = fn.__name__
    return wrapper


@_errno
def args_sizes_get(c, argc, size):
    g = _guest()
    _u32(c, argc, len(g.args))
    _u32(c, size, sum(len(a) + 1 for a in g.args))


@_errno
def args_get(c, argv, buf):
    _string_table(c, _guest().args, argv, buf)


@_errno
def environ_sizes_get(c, count, size):
    g = _guest()
    _u32(c, count, len(g.env))
    _u32(c, size, sum(len(e) + 1 for e in g.env))


@_errno
def environ_get(c, env, buf):
    _string_table(c, _guest().env, env, buf)


@_errno
def clock_res_get(c, _id, out):
    _u64(c, out, 1000)


@_errno
def clock_time_get(c, clock, _precision, out):
    if clock == CLOCK_REALTIME:
        now = time.time_ns()
    elif clock == CLOCK_MONOTONIC:
        now = time.monotonic_ns()
    else:  # process and thread CPU time: time since this call started
        now = time.perf_counter_ns() - _guest().started
    _u64(c, out, now)


@_errno
def random_get(c, buf, n):
    if not 0 <= n <= MAX_RANDOM:
        raise WasiError(EINVAL)
    _write(c, buf, os.urandom(n))


def sched_yield(c):
    return SUCCESS


def proc_exit(c, code):
    raise ProcExit(code)


def _unsupported(*_):
    return ENOTSUP


@_errno
def fd_prestat_get(c, fd, buf):
    if _guest().fd(fd) is not _ROOT:
        raise WasiError(EBADF)
    _write(c, buf, struct.pack("<B3xI", 0, 1))


@_errno
def fd_prestat_dir_name(c, fd, buf, n):
    if _guest().fd(fd) is not _ROOT:
        raise WasiError(EBADF)
    if n < 1:
        raise WasiError(EINVAL)
    _write(c, buf, b"/")


@_errno
def fd_fdstat_get(c, fd, buf):
    e = _guest().fd(fd)
    if e is _STDIN:  # pipes, not terminals: isatty() is false, as in a bash pipeline
        kind, flags, rights = FILETYPE_UNKNOWN, 0, RIGHTS_FD_READ
    elif e in (_STDOUT, _STDERR):
        kind, flags, rights = FILETYPE_UNKNOWN, 0, RIGHTS_FD_WRITE
    elif isinstance(e, _File):
        kind, flags, rights = FILETYPE_REGULAR_FILE, FDFLAGS_APPEND if e.append else 0, RIGHTS_ALL
    else:
        kind, flags, rights = FILETYPE_DIRECTORY, 0, RIGHTS_ALL
    _write(c, buf, struct.pack("<BxH4xQQ", kind, flags, rights, rights))


@_errno
def fd_fdstat_set_flags(c, fd, flags):
    e = _guest().fd(fd)
    if isinstance(e, _File):
        e.append = bool(flags & FDFLAGS_APPEND)


@_errno
def fd_filestat_get(c, fd, buf):
    g = _guest()
    e = g.fd(fd)
    if isinstance(e, _File):
        data = _filestat(FILETYPE_REGULAR_FILE, len(e.data), e.mtime, _ino(e.path))
    elif e is _ROOT:
        data = _filestat(FILETYPE_DIRECTORY, 4096, 0, _ino("/"))
    elif isinstance(e, _Dir):
        data = _stat_path(g, e.path, True)
    else:
        data = _filestat(FILETYPE_UNKNOWN, 0, 0, fd + 1)
    _write(c, buf, data)


@_errno
def fd_filestat_set_size(c, fd, size):
    g = _guest()
    f = g.fd(fd)
    if not isinstance(f, _File):
        raise WasiError(EINVAL)
    if not f.write:
        raise WasiError(EBADF)
    if size < 0:
        raise WasiError(EINVAL)
    if size > g.room(f):
        raise WasiError(EFBIG)
    current = len(f.data)
    g.reserve(max(0, size - current))
    g.buffered -= max(0, current - size)
    if not isinstance(f.data, bytearray):
        f.data = bytearray(f.data)
    if size < current:
        del f.data[size:]
    else:
        f.data += bytes(size - current)
    f.dirty = True


def fd_filestat_set_times(c, fd, atim, mtim, flags):
    return SUCCESS


@_errno
def fd_advise(c, fd, offset, length, advice):
    _guest().fd(fd)


def _read_into(c, fd: int, iovs_ptr: int, n: int, offset: int | None) -> int:
    g = _guest()
    iovs = _iovs(c, iovs_ptr, n)
    want = sum(length for _, length in iovs)
    e = g.fd(fd)
    if e is _STDIN:
        if offset is not None:
            raise WasiError(ESPIPE)
        chunk = g.stdin[g.stdin_pos:g.stdin_pos + want]
        g.stdin_pos += len(chunk)
    elif isinstance(e, _File):
        if not e.read:
            raise WasiError(EBADF)
        start = e.pos if offset is None else offset
        chunk = bytes(e.data[start:start + want])
        if offset is None:
            e.pos = start + len(chunk)
    elif e in (_STDOUT, _STDERR):
        raise WasiError(EBADF)
    else:
        raise WasiError(EISDIR)
    done = 0
    for ptr, length in iovs:
        if done >= len(chunk):
            break
        piece = chunk[done:done + length]
        _write(c, ptr, piece)
        done += len(piece)
    return done


@_errno
def fd_read(c, fd, iovs, n, nread):
    _u32(c, nread, _read_into(c, fd, iovs, n, None))


@_errno
def fd_pread(c, fd, iovs, n, offset, nread):
    if offset < 0:
        raise WasiError(EINVAL)
    _u32(c, nread, _read_into(c, fd, iovs, n, offset))


def _write_from(c, fd: int, iovs_ptr: int, n: int, offset: int | None) -> int:
    g = _guest()
    iovs = _iovs(c, iovs_ptr, n)
    for ptr, length in iovs:
        _check(c, ptr, length)  # every range checked before anything changes
    total = sum(length for _, length in iovs)
    # copied only once the destination has room: 1024 vectors over the whole memory would be terabytes
    data = lambda: b"".join(_read(c, ptr, length) for ptr, length in iovs)  # noqa: E731
    e = g.fd(fd)
    if e in (_STDOUT, _STDERR):
        if offset is not None:
            raise WasiError(ESPIPE)
        if len(g.stdout) + len(g.stderr) + total > g.output_cap:
            raise OutputFull()
        g.write_output(g.stdout if e is _STDOUT else g.stderr, data())
        return total
    if not isinstance(e, _File):
        raise WasiError(EISDIR if e is _ROOT or isinstance(e, _Dir) else EBADF)
    if not e.write:
        raise WasiError(EBADF)
    start = len(e.data) if e.append else e.pos if offset is None else offset
    end = start + total
    if end > len(e.data):
        if end > g.room(e):
            raise WasiError(ENOSPC)
        g.reserve(end - len(e.data))
    if not isinstance(e.data, bytearray):
        e.data = bytearray(e.data)
    if start > len(e.data):
        e.data += bytes(start - len(e.data))
    e.data[start:end] = data()
    e.dirty, e.mtime = True, time.time_ns()
    if offset is None:
        e.pos = end
    return total


@_errno
def fd_write(c, fd, iovs, n, nwritten):
    _u32(c, nwritten, _write_from(c, fd, iovs, n, None))


@_errno
def fd_pwrite(c, fd, iovs, n, offset, nwritten):
    if offset < 0:
        raise WasiError(EINVAL)
    _u32(c, nwritten, _write_from(c, fd, iovs, n, offset))


@_errno
def fd_seek(c, fd, offset, whence, out):
    e = _guest().fd(fd)
    if e in (_STDIN, _STDOUT, _STDERR):
        raise WasiError(ESPIPE)
    if not isinstance(e, _File):
        raise WasiError(EBADF)
    base = {0: 0, 1: e.pos, 2: len(e.data)}.get(whence)
    if base is None or base + offset < 0:
        raise WasiError(EINVAL)
    e.pos = base + offset
    _u64(c, out, e.pos)


@_errno
def fd_tell(c, fd, out):
    e = _guest().fd(fd)
    if e in (_STDIN, _STDOUT, _STDERR):
        raise WasiError(ESPIPE)
    if not isinstance(e, _File):
        raise WasiError(EBADF)
    _u64(c, out, e.pos)


@_errno
def fd_close(c, fd):
    g = _guest()
    e = g.fd(fd)
    g.fds[fd] = None
    if isinstance(e, _File):
        if e.data is not g.stdlib:
            g.buffered -= len(e.data)
        g.save(e)


@_errno
def fd_sync(c, fd):
    g = _guest()
    e = g.fd(fd)
    if isinstance(e, _File):
        g.save(e)


def _entries(g: Guest, d) -> list[tuple[bytes, int, int]]:
    path = "/" if d is _ROOT else d.path
    out = [(b".", FILETYPE_DIRECTORY, _ino(path)), (b"..", FILETYPE_DIRECTORY, _ino(get_parent(path)))]
    names = _fs(g.fs.listdir, path)
    if path == get_parent(STDLIB_PATH) and STDLIB_PATH.rsplit("/", 1)[1] not in names:
        names = sorted(names + [STDLIB_PATH.rsplit("/", 1)[1]])
    for name in names:
        child = path.rstrip("/") + "/" + name
        if child == STDLIB_PATH:
            kind = FILETYPE_REGULAR_FILE
        else:
            try:
                st = g.fs.stat(child, follow=False)
                kind = FILETYPE_SYMBOLIC_LINK if st.is_symlink else FILETYPE_DIRECTORY if st.is_dir else FILETYPE_REGULAR_FILE
            except ShellError:
                kind = FILETYPE_UNKNOWN
        out.append((bytetext.encode(name), kind, _ino(child)))
    return out


@_errno
def fd_readdir(c, fd, buf, n, cookie, used):
    g = _guest()
    e = g.fd(fd)
    if not (e is _ROOT or isinstance(e, _Dir)):
        raise WasiError(ENOTDIR)
    if isinstance(e, _Dir):
        if e.entries is None:
            e.entries = _entries(g, e)
        entries = e.entries
    else:
        entries = _entries(g, e)
    if cookie < 0 or n < 0:
        raise WasiError(EINVAL)
    out = bytearray()
    for i in range(cookie, len(entries)):
        if len(out) >= n:
            break
        name, kind, ino = entries[i]
        out += struct.pack("<QQIB3x", i + 1, ino, len(name), kind) + name
    del out[n:]
    _write(c, buf, bytes(out))
    _u32(c, used, len(out))


@_errno
def path_open(c, dirfd, _dirflags, path_ptr, path_len, oflags, rights, _inheriting, fdflags, out):
    g = _guest()
    path = g.resolve(dirfd, _path(c, path_ptr, path_len))
    want_dir, creat, excl, trunc = oflags & OFLAGS_DIRECTORY, oflags & OFLAGS_CREAT, oflags & OFLAGS_EXCL, oflags & OFLAGS_TRUNC
    append = bool(fdflags & FDFLAGS_APPEND)
    write = bool(rights & RIGHTS_FD_WRITE) or bool(trunc) or append
    read = bool(rights & (RIGHTS_FD_READ | RIGHTS_FD_READDIR)) or not write
    fs = g.fs
    if path == STDLIB_PATH:
        if write or creat:
            raise WasiError(EROFS)
        if want_dir:
            raise WasiError(ENOTDIR)
        _u32(c, out, g.alloc(_File(path, g.stdlib, True, False, False, len(g.stdlib), 0)))
        return
    try:
        st = _fs(fs.stat, path)
    except WasiError as e:
        st, why = None, e.errno
    if st is not None and st.is_dir:
        if creat and excl:
            raise WasiError(EEXIST)
        if write:
            raise WasiError(EISDIR)
        _u32(c, out, g.alloc(_Dir(path)))
        return
    if st is not None:
        if want_dir:
            raise WasiError(ENOTDIR)
        if creat and excl:
            raise WasiError(EEXIST)
        if write and not fs.can_write(path) or read and not fs.can_read(path):
            raise WasiError(EACCES)
        if trunc:
            if st.size:
                _fs(fs.write_bytes, path, b"")  # O_TRUNC empties the file now, as open(2) does
            data, stored = bytearray(), 0
        else:
            raw = _fs(fs.read_bytes, path)
            data, stored = (bytearray(raw) if write else raw), len(raw)
        g.reserve(len(data))
        _u32(c, out, g.alloc(_File(path, data, read, write, append, stored, _ns(st.modified))))
        return
    if why != ENOENT or not creat or want_dir:
        raise WasiError(why)
    _fs(fs.write_bytes, path, b"")  # the parent must exist and be writable: the filesystem checks it as for any command
    _u32(c, out, g.alloc(_File(path, bytearray(), read, write, append, 0, time.time_ns())))


@_errno
def path_filestat_get(c, dirfd, flags, path_ptr, path_len, buf):
    g = _guest()
    path = g.resolve(dirfd, _path(c, path_ptr, path_len))
    _write(c, buf, _stat_path(g, path, bool(flags & LOOKUP_SYMLINK_FOLLOW)))


@_errno
def path_filestat_set_times(c, dirfd, flags, path_ptr, path_len, _atim, mtim, fst):
    from datetime import datetime, timezone
    g = _guest()
    path = g.resolve(dirfd, _path(c, path_ptr, path_len))
    if path == STDLIB_PATH:
        raise WasiError(EROFS)
    if fst & FSTFLAGS_MTIM_NOW:
        when = datetime.now(timezone.utc)
    elif fst & FSTFLAGS_MTIM:
        when = datetime.fromtimestamp(mtim / 1e9, timezone.utc)
    else:
        when = None
    _fs(g.fs.stat, path)
    if when is not None:
        _fs(g.fs.set_modified_time, path, when.replace(tzinfo=None), follow=bool(flags & LOOKUP_SYMLINK_FOLLOW))




@_errno
def path_create_directory(c, dirfd, path_ptr, path_len):
    g = _guest()
    path = g.resolve(dirfd, _path(c, path_ptr, path_len))
    if path == STDLIB_PATH or g.fs.exists(path, follow=False):
        raise WasiError(EEXIST)
    _fs(g.fs.mkdir, path)


@_errno
def path_remove_directory(c, dirfd, path_ptr, path_len):
    g = _guest()
    path = g.resolve(dirfd, _path(c, path_ptr, path_len))
    if path == "/":
        raise WasiError(EACCES)
    if not _fs(g.fs.stat, path, follow=False).is_dir:
        raise WasiError(ENOTDIR)
    _fs(g.fs.delete, path)  # refuses a directory with entries (ENOTEMPTY)


@_errno
def path_unlink_file(c, dirfd, path_ptr, path_len):
    g = _guest()
    path = g.resolve(dirfd, _path(c, path_ptr, path_len))
    if path == STDLIB_PATH:
        raise WasiError(EROFS)
    if _fs(g.fs.stat, path, follow=False).is_dir:
        raise WasiError(EISDIR)
    _fs(g.fs.delete, path)


@_errno
def path_rename(c, fd1, old_ptr, old_len, fd2, new_ptr, new_len):
    g = _guest()
    src = g.resolve(fd1, _path(c, old_ptr, old_len))
    dst = g.resolve(fd2, _path(c, new_ptr, new_len))
    if STDLIB_PATH in (src, dst):
        raise WasiError(EROFS)
    fs = g.fs
    s = _fs(fs.stat, src, follow=False)
    d = _fs(fs.stat, dst, follow=False) if fs.exists(dst, follow=False) else None
    if d is not None and d.is_dir:  # rename(2): a directory replaces an empty one; anything else can't
        if not s.is_dir:
            raise WasiError(EISDIR)
        if src == dst:
            return
        _fs(fs.delete, dst)
    elif d is not None and s.is_dir:
        raise WasiError(ENOTDIR)
    _fs(fs.move, src, dst)


@_errno
def path_symlink(c, old_ptr, old_len, dirfd, new_ptr, new_len):
    g = _guest()
    target = bytetext.decode(_path(c, old_ptr, old_len))
    link = g.resolve(dirfd, _path(c, new_ptr, new_len))
    if link == STDLIB_PATH:
        raise WasiError(EEXIST)
    _fs(g.fs.create_symlink, link, target)


@_errno
def path_readlink(c, dirfd, path_ptr, path_len, buf, n, used):
    g = _guest()
    path = g.resolve(dirfd, _path(c, path_ptr, path_len))
    target = bytetext.encode(_fs(g.fs.read_link, path))
    target = target[:max(0, n)]
    _write(c, buf, target)
    _u32(c, used, len(target))


@_errno
def path_link(c, fd1, flags, old_ptr, old_len, fd2, new_ptr, new_len):
    g = _guest()
    src = g.resolve(fd1, _path(c, old_ptr, old_len))
    dst = g.resolve(fd2, _path(c, new_ptr, new_len))
    if STDLIB_PATH in (src, dst):
        raise WasiError(EROFS)
    _fs(g.fs.create_hard_link, src, dst, bool(flags & LOOKUP_SYMLINK_FOLLOW))


@_errno
def poll_oneoff(c, subs, events, n, nevents):
    g = _guest()
    if not 0 < n <= 64:
        raise WasiError(EINVAL)
    raw = _read(c, subs, n * 48)
    ready, sleep = [], None
    for i in range(n):
        userdata, kind = struct.unpack_from("<QB", raw, i * 48)
        if kind == EVENTTYPE_CLOCK:
            clock, timeout, _precision, flags = struct.unpack_from("<I4xQQH", raw, i * 48 + 16)
            if flags & SUBCLOCKFLAGS_ABSTIME:
                now = time.time_ns() if clock == CLOCK_REALTIME else time.monotonic_ns()
                timeout = max(0, timeout - now)
            if sleep is None or timeout < sleep[0]:
                sleep = (timeout, userdata)
        elif kind in (EVENTTYPE_FD_READ, EVENTTYPE_FD_WRITE):
            fd = struct.unpack_from("<I", raw, i * 48 + 16)[0]
            try:
                e = g.fd(fd)
                error = SUCCESS
                nbytes = len(g.stdin) - g.stdin_pos if e is _STDIN else max(0, len(e.data) - e.pos) if isinstance(e, _File) else 0
            except WasiError as err:
                error, nbytes = err.errno, 0
            ready.append((userdata, error, kind, nbytes))
        else:
            raise WasiError(EINVAL)
    if not ready and sleep is not None:
        # a real sleep, in slices, never past the call's deadline (the epoch then stops the guest)
        until = time.monotonic() + sleep[0] / 1e9
        while (now := time.monotonic()) < until:
            if now >= g.deadline:
                raise Interrupted()
            time.sleep(min(until, g.deadline) - now if min(until, g.deadline) - now < MAX_SLEEP_SLICE else MAX_SLEEP_SLICE)
        ready.append((sleep[1], SUCCESS, EVENTTYPE_CLOCK, 0))
    _write(c, events, b"".join(struct.pack("<QHB5xQ8x", u, err, k, nb) for u, err, k, nb in ready))
    _u32(c, nevents, len(ready))


_SIGNATURES = {  # name: (parameter types, i for i32 and I for i64), all returning an i32 errno but proc_exit
    "args_get": "ii", "args_sizes_get": "ii", "environ_get": "ii", "environ_sizes_get": "ii", "clock_res_get": "ii",
    "clock_time_get": "iIi", "fd_advise": "iIIi", "fd_close": "i", "fd_datasync": "i", "fd_fdstat_get": "ii",
    "fd_fdstat_set_flags": "ii", "fd_filestat_get": "ii", "fd_filestat_set_size": "iI", "fd_filestat_set_times": "iIIi",
    "fd_pread": "iiiIi", "fd_prestat_get": "ii", "fd_prestat_dir_name": "iii", "fd_pwrite": "iiiIi", "fd_read": "iiii",
    "fd_readdir": "iiiIi", "fd_seek": "iIii", "fd_sync": "i", "fd_tell": "ii", "fd_write": "iiii",
    "path_create_directory": "iii", "path_filestat_get": "iiiii", "path_filestat_set_times": "iiiiIIi",
    "path_link": "iiiiiii", "path_open": "iiiiiIIii", "path_readlink": "iiiiii", "path_remove_directory": "iii",
    "path_rename": "iiiiii", "path_symlink": "iiiii", "path_unlink_file": "iii", "poll_oneoff": "iiii", "proc_exit": "i",
    "sched_yield": "", "random_get": "ii", "sock_accept": "iii", "sock_recv": "iiiiii", "sock_send": "iiiii",
    "sock_shutdown": "ii",
}


def _define(linker, wasmtime) -> None:
    i32, i64 = wasmtime.ValType.i32(), wasmtime.ValType.i64()
    funcs = globals()
    for name, sig in _SIGNATURES.items():
        params = [i64 if t == "I" else i32 for t in sig]
        results = [] if name == "proc_exit" else [i32]
        fn = _unsupported if name.startswith("sock_") else fd_sync if name == "fd_datasync" else funcs[name]
        linker.define_func("wasi_snapshot_preview1", name, wasmtime.FuncType(params, results), fn, access_caller=True)
    linker.define_func("bashkit", "http_request", wasmtime.FuncType([i32, i32, i64, i32], [i32]), http_request, access_caller=True)
    linker.define_func("bashkit", "http_take", wasmtime.FuncType([i32, i32], [i32]), http_take, access_caller=True)


# ---------- http bridge ----------

# bashkit.http_request(req_ptr, req_len, timeout_ms, out_len_ptr) -> status; bashkit.http_take(buf, len) -> status.
# The request is length-prefixed little-endian: method, url, u32 header count, (name, value)..., body (each a u32
# length then the bytes). A success payload is u32 status, u32 header count, (name, value)..., body; a failure's is the
# error text. http_request stores the payload and writes its size; http_take copies it into a buffer of exactly that size.
HTTP_OK, HTTP_NETWORK, HTTP_TIMEOUT, HTTP_INVALID, HTTP_FAULT = 0, 1, 2, 3, 4
HTTP_METHODS = ("GET", "POST", "PUT", "DELETE", "HEAD", "PATCH")
MAX_URL = 8192
MAX_HEADERS = 128
MAX_HEADER_BYTES = 64 * 1024
MAX_REQUEST_BODY = 16 << 20
MAX_ERROR_TEXT = 512


def decode_request(buf: bytes) -> tuple[str, str, list[tuple[str, str]], bytes]:
    """(method, url, headers, body) from a guest request record. Raises ValueError with the reason."""
    at = 0

    def take(limit: int, what: str) -> bytes:
        nonlocal at
        if at + 4 > len(buf):
            raise ValueError("truncated request")
        n = struct.unpack_from("<I", buf, at)[0]
        at += 4
        if n > limit:
            raise ValueError(f"{what} too long")
        if at + n > len(buf):
            raise ValueError("truncated request")
        at += n
        return buf[at - n:at]

    def text(limit: int, what: str) -> str:
        try:
            return take(limit, what).decode("utf-8")
        except UnicodeDecodeError:
            raise ValueError(f"{what} is not UTF-8") from None

    method = text(16, "method").upper()
    if method not in HTTP_METHODS:
        raise ValueError(f"unsupported HTTP method: {method}")
    url = text(MAX_URL, "URL")
    if any(ch <= " " or ch == "\x7f" for ch in url):
        raise ValueError("URL contains control characters or spaces")
    if at + 4 > len(buf):
        raise ValueError("truncated request")
    count = struct.unpack_from("<I", buf, at)[0]
    at += 4
    if count > MAX_HEADERS:
        raise ValueError("too many headers")
    headers, size = [], 0
    for _ in range(count):
        name, value = text(MAX_HEADER_BYTES, "header name"), text(MAX_HEADER_BYTES, "header value")
        size += len(name) + len(value)
        if size > MAX_HEADER_BYTES:
            raise ValueError("headers too large")
        if not name or any(not (ch.isascii() and (ch.isalnum() or ch in "!#$%&'*+-.^_`|~")) for ch in name):
            raise ValueError("invalid header name")
        if any(ch in "\r\n\0" for ch in value):
            raise ValueError("invalid header value")
        headers.append((name, value.strip()))
    body = take(MAX_REQUEST_BODY, "request body")
    if at != len(buf):
        raise ValueError("trailing bytes in request")
    return method, url, headers, body


def encode_response(status: int, headers: list[tuple[str, str]], body: bytes) -> bytes:
    out = [struct.pack("<II", status, len(headers))]
    for k, v in headers:
        kb, vb = k.encode("utf-8", "replace"), v.encode("utf-8", "replace")
        out += [struct.pack("<I", len(kb)), kb, struct.pack("<I", len(vb)), vb]
    out += [struct.pack("<I", len(body)), body]
    return b"".join(out)


def error_payload(text: str) -> bytes:
    return text.encode("utf-8", "replace")[:MAX_ERROR_TEXT]


def http_request(c, ptr, n, timeout_ms, out_len):
    g = _guest()
    try:
        raw = _read(c, ptr, n)
    except WasiError:
        return HTTP_FAULT
    g.pending = None
    try:
        method, url, headers, body = decode_request(raw)
    except ValueError as e:
        code, payload = HTTP_INVALID, error_payload(str(e))
    else:
        left = max(0.0, g.deadline - time.monotonic())
        timeout = left if timeout_ms < 0 else min(left, timeout_ms / 1000)
        code, payload = g.http(method, url, headers, body, timeout)  # the web policy counts and limits requests
        if code != HTTP_OK:
            payload = error_payload(payload.decode("utf-8", "replace"))
    try:
        _u32(c, out_len, len(payload))
    except WasiError:
        return HTTP_FAULT
    g.pending = payload
    return code


def http_take(c, buf, n):
    g = _guest()
    payload, g.pending = g.pending, None
    if payload is None or n != len(payload):
        return HTTP_FAULT
    try:
        _write(c, buf, payload)
    except WasiError:
        return HTTP_FAULT
    return HTTP_OK


# ---------- running a call ----------

class Outcome:
    """How a call ended: code (the exit status) unless timed_out, stack_overflow, or trap (another fatal error's text)."""

    def __init__(self, code: int = 1, timed_out: bool = False, stack_overflow: bool = False, trap: str | None = None):
        self.code, self.timed_out, self.stack_overflow, self.trap = code, timed_out, stack_overflow, trap


def run(rt: Runtime, guest: Guest) -> Outcome:
    """Runs bashkit_run on a fresh instance, on this thread; saves the files the program left open. Raises OutputFull."""
    wasmtime = rt.wasmtime
    store = wasmtime.Store(rt.engine)
    store.set_limits(memory_size=guest.max_memory, instances=1, tables=4, memories=1)
    store.set_epoch_deadline(max(1, int((guest.deadline - time.monotonic()) / EPOCH_TICK) + 1))
    _local.guest = guest
    try:
        with rt.ticking():
            instance = rt.linker.instantiate(store, rt.module)
            exports = instance.exports(store)
            guest.memory = exports["memory"]
            if "_initialize" in exports:
                exports["_initialize"](store)
            return Outcome(exports["bashkit_run"](store))
    except ProcExit as e:
        return Outcome(e.code)
    except Interrupted:
        return Outcome(timed_out=True)
    except wasmtime.Trap as e:
        code = getattr(e, "trap_code", None)
        if code == wasmtime.TrapCode.INTERRUPT or time.monotonic() >= guest.deadline:
            return Outcome(timed_out=True)
        if code == wasmtime.TrapCode.STACK_OVERFLOW:
            return Outcome(stack_overflow=True)
        return Outcome(trap=str(e).split("\n", 1)[0][:200])
    except wasmtime.WasmtimeError as e:  # instantiation: the memory limit is below the snapshot's own memory
        return Outcome(trap=str(e).split("\n", 1)[0][:200])
    finally:
        _local.guest = None
        guest.flush_all()
        guest.memory = None
        store.close() if hasattr(store, "close") else None
