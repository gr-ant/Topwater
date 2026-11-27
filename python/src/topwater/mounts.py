"""Mounted sources: outside stores of files shown as directories in the machine (docs/mounts.md). See docs/architecture.md."""
from __future__ import annotations

import asyncio
import contextlib
import errno as _errno
import hashlib
import inspect
import itertools
import os
import secrets
import stat as _stat
import time
from dataclasses import dataclass
from datetime import datetime, timezone

from ._spec import COMMANDS
from .shell.types import _ControlFlow
from .vfs import ShellError, _Dir, _File, get_parent, normalize, utc_now

MODES = ("read-only", "staged", "live")
CAPABILITIES = frozenset({"write", "delete", "rename", "mkdir"})
MAX_DEPTH = 32  # directories below the mount point that are listed
MAX_NAME_BYTES = 255
MAX_VERSION = 1024  # characters in a version
LIST_BUDGET = 2000  # entries one listing pass reads before it leaves the directories still to go as "not listed yet"
LIST_SECONDS = 5.0  # and the time it may take
FETCH_AT_ONCE = 4  # files fetched at the same time
# commands that never read a file's content: nothing is fetched before them (they may still list a directory)
NO_CONTENT = frozenset({"ls", "find", "stat", "du", "df", "tree", "cd", "pwd", "test", "[", "mkdir", "rmdir", "rm", "mv", "touch",
                        "mount", "chmod", "chown", "chgrp", "ln", "realpath", "readlink", "basename", "dirname", "echo", "printf",
                        "true", "false", "date", "sleep", "seq"})
# commands that walk a directory they are given: everything below it is listed first
DEEP = frozenset({"find", "du", "tree", "grep", "egrep", "fgrep", "rg", "cp", "tar", "zip", "rm", "diff", "chmod", "chown", "chgrp",
                  "to-markdown", "wc", "cat"})
_WINDOWS_RESERVED = frozenset({"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))})


def _msg(key: str, **values: str) -> str:
    from .commands import spec_format
    return spec_format(COMMANDS["mount"].messages[key], **values)


def _join(d: str, name: str) -> str:
    return "/" + name if d == "/" else d + "/" + name


# ---------- sources ----------

@dataclass(frozen=True)
class MountEntry:
    """One entry a source lists: name (one path segment), is_dir, size in bytes (None: not known until read), modified
    (UTC, naive; None: unknown) and version, an opaque string that changes when the entry does (an ETag, a revision)."""
    name: str
    is_dir: bool
    size: int | None = 0
    modified: datetime | None = None
    version: str = ""


class MountConflictError(Exception):
    """A source raises it when the expected version no longer matches what it holds."""


class MountSourceError(Exception):
    """A source raises it with a message the agent may see ("page.md: the page is locked"). Anything else a source raises
    is shown to the agent as "the source failed"; its text goes to the host only."""


class MountSource:
    """What a source implements (docs/mounts.md). Every method may be a coroutine; a remote source should be, because a
    synchronous call cannot be interrupted at the call's time limit. Paths are canonical and absolute within the source:
    "/" is its root. `type` and `id` are saved in snapshots, so they must not hold secrets; `capabilities` says which of
    write, delete, rename and mkdir it supports (none: read-only)."""
    type = "source"
    id = ""
    capabilities: frozenset = frozenset()

    def list(self, path: str, max_entries: int) -> list[MountEntry]:
        """A directory's entries; more than max_entries are not used, so a source that pages may stop there."""
        raise NotImplementedError

    def stat(self, path: str) -> MountEntry | None:
        raise NotImplementedError

    def read(self, path: str, max_bytes: int) -> bytes:
        raise NotImplementedError

    def write(self, path: str, data: bytes, expected_version: str | None) -> str:
        """Creates the file (expected_version None: it must not exist) or replaces it; returns its new version."""
        raise NotImplementedError

    def delete(self, path: str, expected_version: str | None) -> None:
        raise NotImplementedError

    def rename(self, path: str, to: str, expected_version: str | None) -> str | None:
        """Returns the entry's new version (None: unchanged)."""
        raise NotImplementedError

    def mkdir(self, path: str) -> str | None:
        """Returns the new directory's version (None: none)."""
        raise NotImplementedError


class LocalFolderSource(MountSource):
    """A directory on the host (an Obsidian vault, a folder of notes). Read-only unless `writable`. Only regular files and
    directories are listed: symbolic links, FIFOs and devices are left out, never followed or opened. Names with `\\` (and on
    Windows `:`, reserved device names and a trailing dot or space) are refused. On Linux and macOS every name is opened
    relative to its directory without following links, so nothing outside `root` is reached even while someone else
    changes the folder; elsewhere a link swapped in between a check and its use could still be followed (docs/mounts.md).
    The version is the modification time and size."""
    type = "local-folder"

    def __init__(self, root: str | os.PathLike, writable: bool = False, id: str | None = None):
        self._root = os.path.realpath(root)
        if not os.path.isdir(self._root):
            raise ValueError(f"{root}: not a directory")
        self.id = id if id is not None else os.path.basename(self._root)
        self.capabilities = CAPABILITIES if writable else frozenset()

    _AT = os.open in os.supports_dir_fd and hasattr(os, "O_DIRECTORY") and hasattr(os, "O_NOFOLLOW")

    def _names(self, path: str) -> list[str]:
        if normalize("/", path) != path or "\0" in path:
            raise MountSourceError(f"{path}: not a canonical path")
        names = [] if path == "/" else path.split("/")[1:]
        for n in names:
            bad = "\\" in n or (os.name == "nt" and (":" in n or n != n.rstrip(" .") or n.split(".")[0].upper() in _WINDOWS_RESERVED))
            if bad:
                raise MountSourceError(f"{path}: not a name a file can have here")
        return names

    @contextlib.contextmanager
    def _dir(self, names: list[str]):
        """The directory `names` leads to: an open descriptor (no link followed on the way), or a checked path elsewhere."""
        if not self._AT:
            cur = self._root
            for n in names:
                cur = os.path.join(cur, n)
                if os.path.islink(cur):
                    raise FileNotFoundError(_errno.ENOENT, "")
            if os.path.commonpath([cur, self._root]) != self._root:
                raise FileNotFoundError(_errno.ENOENT, "")
            yield cur, None
            return
        fd = os.open(self._root, os.O_RDONLY | os.O_DIRECTORY)
        try:
            for n in names:
                nxt = os.open(n, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                os.close(fd)
                fd = nxt
            yield fd, fd
        finally:
            os.close(fd)

    @contextlib.contextmanager
    def _at(self, path: str):
        """(name, dir_fd) for os calls on `path`'s last name; dir_fd is None where descriptors aren't available."""
        names = self._names(path)
        if not names:
            raise MountSourceError("/: the root itself")
        try:
            with self._dir(names[:-1]) as (where, fd):
                yield (names[-1] if fd is not None else os.path.join(where, names[-1])), fd
        except OSError as e:
            raise MountSourceError(f"{path}: {os.strerror(e.errno) if e.errno else 'failed'}") from None

    @staticmethod
    def _entry(name: str, st: os.stat_result) -> MountEntry:
        is_dir = _stat.S_ISDIR(st.st_mode)
        return MountEntry(name, is_dir, 0 if is_dir else st.st_size,
                          datetime.fromtimestamp(st.st_mtime, timezone.utc).replace(tzinfo=None), f"{st.st_mtime_ns}:{st.st_size}")

    @staticmethod
    def _plain(st) -> bool:
        return _stat.S_ISDIR(st.st_mode) or _stat.S_ISREG(st.st_mode)

    def _lstat(self, name: str, fd) -> os.stat_result | None:
        try:
            st = os.stat(name, dir_fd=fd, follow_symlinks=False)
        except FileNotFoundError:
            return None
        return st if self._plain(st) else None

    def _check(self, path: str, name: str, fd, expected: str | None) -> os.stat_result | None:
        st = self._lstat(name, fd)
        now = None if st is None else self._entry("", st).version
        if now != expected and not (st is not None and _stat.S_ISDIR(st.st_mode) and expected is not None):
            raise MountConflictError(f"{path}: version {now}, expected {expected}")
        return st

    def list(self, path: str, max_entries: int) -> list[MountEntry]:
        out = []
        try:
            with self._dir(self._names(path)) as (where, fd), os.scandir(fd if fd is not None else where) as it:
                for e in itertools.islice(it, max_entries * 2 + 64):  # links and devices are skipped, not counted
                    st = e.stat(follow_symlinks=False)
                    if self._plain(st):
                        out.append(self._entry(e.name, st))
                        if len(out) >= max_entries:
                            break
        except OSError as e:
            raise MountSourceError(f"{path}: {os.strerror(e.errno) if e.errno else 'failed'}") from None
        return out

    def stat(self, path: str) -> MountEntry | None:
        if path == "/":
            return MountEntry("", True)
        with self._at(path) as (name, fd):
            st = self._lstat(name, fd)
        return None if st is None else self._entry(path.rsplit("/", 1)[-1], st)

    def read(self, path: str, max_bytes: int) -> bytes:
        with self._at(path) as (name, fd):
            try:
                # never blocks on a FIFO, never follows a link; then only a regular file is read
                h = os.open(name, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0), dir_fd=fd)
            except OSError as e:
                raise MountSourceError(f"{path}: {os.strerror(e.errno) if e.errno else 'failed'}") from None
            with os.fdopen(h, "rb") as f:
                st = os.fstat(f.fileno())
                if not _stat.S_ISREG(st.st_mode):
                    raise MountSourceError(f"{path}: not a regular file")
                if st.st_size > max_bytes:
                    raise MountSourceError(_msg("tooLarge"))
                return f.read(max_bytes + 1)

    def write(self, path: str, data: bytes, expected_version: str | None) -> str:
        with self._at(path) as (name, fd):
            self._check(path, name, fd, expected_version)
            tmp = f".tw-{secrets.token_hex(4)}.tmp"
            tmp_name = tmp if fd is not None else os.path.join(os.path.dirname(name), tmp)
            try:
                h = os.open(tmp_name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o644, dir_fd=fd)
                with os.fdopen(h, "wb") as f:
                    f.write(data)
                os.replace(tmp_name, name, src_dir_fd=fd, dst_dir_fd=fd)
            except OSError as e:
                with contextlib.suppress(OSError):
                    os.unlink(tmp_name, dir_fd=fd)
                raise MountSourceError(f"{path}: {os.strerror(e.errno) if e.errno else 'failed'}") from None
            return self._entry("", os.stat(name, dir_fd=fd, follow_symlinks=False)).version

    def delete(self, path: str, expected_version: str | None) -> None:
        with self._at(path) as (name, fd):
            st = self._check(path, name, fd, expected_version)
            try:
                (os.rmdir if st is not None and _stat.S_ISDIR(st.st_mode) else os.unlink)(name, dir_fd=fd)
            except OSError as e:
                raise MountSourceError(f"{path}: {os.strerror(e.errno) if e.errno else 'failed'}") from None

    def rename(self, path: str, to: str, expected_version: str | None) -> str | None:
        with self._at(path) as (name, fd), self._at(to) as (to_name, to_fd):
            if self._lstat(to_name, to_fd) is not None:
                raise MountConflictError(f"{to}: already exists")
            self._check(path, name, fd, expected_version)
            try:
                os.rename(name, to_name, src_dir_fd=fd, dst_dir_fd=to_fd)
            except OSError as e:
                raise MountSourceError(f"{path}: {os.strerror(e.errno) if e.errno else 'failed'}") from None
        return None

    def mkdir(self, path: str) -> str | None:
        with self._at(path) as (name, fd):
            try:
                os.mkdir(name, dir_fd=fd)
            except OSError as e:
                raise MountSourceError(f"{path}: {os.strerror(e.errno) if e.errno else 'failed'}") from None
        return None


class MemorySource(MountSource):
    """Files kept in memory: for tests, and as an example of a source. `files` maps paths ("notes/a.md"; a trailing "/" for
    an empty directory) to text or bytes. set() and remove() change it from outside; each change takes the next version
    ("1", "2", ...). hook(op, path), called before every operation (and may be a coroutine), can sleep or raise to simulate
    latency, failures and conflicts. calls lists every (op, path) asked."""
    type = "memory"

    def __init__(self, files: dict | None = None, id: str = "memory", modified: datetime = datetime(2026, 1, 1)):
        self.id, self.modified = id, modified
        self.capabilities = CAPABILITIES
        self.hook = None
        self.calls: list[tuple[str, str]] = []
        self._next = 0
        self._items: dict[str, tuple[bytes | None, str]] = {"/": (None, "0")}  # path -> (content; None: a directory, version)
        for p in sorted(files or {}):
            if p.endswith("/"):
                self._mkdirs(normalize("/", p))
            else:
                self.set(p, files[p])

    def _version(self) -> str:
        self._next += 1
        return str(self._next)

    def _mkdirs(self, path: str) -> None:
        if path not in self._items:
            self._mkdirs(get_parent(path))
            self._items[path] = (None, self._version())

    def set(self, path: str, content: str | bytes) -> None:
        path = normalize("/", path)
        self._mkdirs(get_parent(path))
        self._items[path] = (content.encode() if isinstance(content, str) else bytes(content), self._version())

    def remove(self, path: str) -> None:
        path = normalize("/", path)
        for p in [p for p in self._items if p == path or p.startswith(path + "/")]:
            del self._items[p]

    def tree(self) -> dict[str, bytes | None]:
        """Every path below the root and its content (None: a directory), sorted."""
        return {p: self._items[p][0] for p in sorted(self._items) if p != "/"}

    async def _enter(self, op: str, path: str) -> None:
        self.calls.append((op, path))
        if self.hook is not None:
            r = self.hook(op, path)
            if inspect.isawaitable(r):
                await r

    def _entry(self, path: str) -> MountEntry | None:
        item = self._items.get(path)
        if item is None:
            return None
        data, version = item
        return MountEntry(path.rsplit("/", 1)[-1], data is None, 0 if data is None else len(data), self.modified, version)

    def _check(self, path: str, expected: str | None) -> None:
        now = self._items.get(path)
        if (now[1] if now else None) != expected:
            raise MountConflictError(f"{path}: version {now[1] if now else None}, expected {expected}")

    def _missing(self, path: str) -> MountSourceError:
        return MountSourceError(f"{path}: No such file or directory")

    async def list(self, path: str, max_entries: int) -> list[MountEntry]:
        await self._enter("list", path)
        if self._items.get(path, (b"",))[0] is not None:
            raise self._missing(path)
        prefix = path.rstrip("/") + "/"
        kids = (self._entry(p) for p in self._items if p.startswith(prefix) and "/" not in p[len(prefix):] and p != "/")
        return list(itertools.islice(kids, max_entries))

    async def stat(self, path: str) -> MountEntry | None:
        await self._enter("stat", path)
        return self._entry(path)

    async def read(self, path: str, max_bytes: int) -> bytes:
        await self._enter("read", path)
        data = self._items.get(path, (None,))[0]
        if data is None:
            raise self._missing(path)
        return data

    async def write(self, path: str, data: bytes, expected_version: str | None) -> str:
        await self._enter("write", path)
        self._check(path, expected_version)
        if self._items.get(get_parent(path), (b"",))[0] is not None:
            raise self._missing(get_parent(path))
        version = self._version()
        self._items[path] = (bytes(data), version)
        return version

    async def delete(self, path: str, expected_version: str | None) -> None:
        await self._enter("delete", path)
        item = self._items.get(path)
        if item is not None and item[0] is None:
            if any(p.startswith(path + "/") for p in self._items):
                raise MountSourceError(f"{path}: Directory not empty")
        else:
            self._check(path, expected_version)
        del self._items[path]

    async def rename(self, path: str, to: str, expected_version: str | None) -> str | None:
        await self._enter("rename", path)
        if to in self._items:
            raise MountConflictError(f"{to}: already exists")
        item = self._items.get(path)
        if item is None or item[0] is not None:
            self._check(path, expected_version)
        if self._items.get(get_parent(to), (b"",))[0] is not None:
            raise self._missing(get_parent(to))
        for p in [p for p in self._items if p == path or p.startswith(path + "/")]:
            self._items[to + p[len(path):]] = self._items.pop(p)
        return None

    async def mkdir(self, path: str) -> str | None:
        await self._enter("mkdir", path)
        if path in self._items:
            raise MountConflictError(f"{path}: already exists")
        if self._items.get(get_parent(path), (b"",))[0] is not None:
            raise self._missing(get_parent(path))
        version = self._version()
        self._items[path] = (None, version)
        return version


# ---------- changes ----------

@dataclass(frozen=True)
class MountChange:
    """One operation that makes the source match the mount: kind "write", "delete", "rename" or "mkdir"; path (and `to`
    for a rename) within the source; version: what the entry was when read (None: it must not exist); content for a write.
    apply_changes matches changes by value, content included."""
    kind: str
    path: str
    to: str | None = None
    version: str | None = None
    content: bytes | None = None


@dataclass(frozen=True)
class MountApplyResult:
    """What apply_changes did: the changes applied, the one it stopped at (None if none) with the error (the source's full
    text: for the host, not the agent), whether that was a conflict (the source changed since it was read), and the changes
    after it that were not tried."""
    applied: list
    failed: MountChange | None = None
    error: str | None = None
    conflict: bool = False
    not_tried: tuple = ()


@dataclass(frozen=True)
class MountInfo:
    """A mount as `shell.mounts` lists it. error: why it is unavailable or its listing failed (the source's full text; None
    when it works); warnings: what its listings left out; held: how many changes from a loaded snapshot wait for
    accept_snapshot_changes."""
    path: str
    mode: str
    type: str
    id: str
    error: str | None = None
    warnings: tuple = ()
    held: int = 0


@dataclass(frozen=True)
class MountExchange:
    """One question a recording kept and its answer: list (entries), stat (entry; None: missing), read (body), write, rename
    and mkdir (version), delete; error (as the agent may see it) and conflict when the source refused."""
    mount: str
    op: str
    path: str
    to: str | None = None
    entries: tuple | None = None
    entry: MountEntry | None = None
    body: bytes | None = None
    version: str | None = None
    error: str | None = None
    conflict: bool = False


# ---------- mounts ----------

class _Base:
    """What the mount read from the source at one path: the entry, the node it became, and its content's hash once fetched."""
    __slots__ = ("entry", "node", "digest", "clock")

    def __init__(self, entry: MountEntry, node, digest: bytes | None = None):
        self.entry, self.node, self.digest = entry, node, digest
        self.clock = None  # the node's change clock right after it was fetched: unchanged since means clean, can be evicted


class _Mount:
    def __init__(self, mounts: "Mounts", path: str, source, mode: str):
        self.mounts, self.path, self.source, self.mode = mounts, path, source, mode
        self.type, self.id = str(getattr(source, "type", "")), str(getattr(source, "id", ""))
        self.base: dict[str, _Base] = {}
        self.expected: dict[str, str | None] = {}  # versions a replayed change was made on, which win over base's
        self.absent: set[str] = set()  # paths a replayed write expects not to exist yet
        self.stale = False  # restored from a file: listed at the start of the next call
        self.held: list[MountChange] = []  # changes not in the tree: from a loaded snapshot (quarantined), or while unavailable
        self.error: str | None = None  # the full text, for the host
        self.shown: str | None = None  # what the agent sees of it
        self.warnings: list[str] = []
        self.misses: dict[int, object] = {}  # files and directories used before they were fetched or listed
        self.failed: dict[int, str] = {}  # why a file could not be fetched (safe to show)
        self.fetched: dict[int, _Base] = {}  # in fetch order, for eviction
        self.stuck: list[MountChange] = []  # live mode: changes that failed, not tried again until they change
        self.busy = False  # applying: a pipeline's other stage leaves it to the end of the call
        self.trusted = True  # held changes may be replayed at the next listing (a replay's), not only by the host
        self.last_safe: str | None = None  # what the agent may see of the last apply's error

    @property
    def fs(self):
        return self.mounts.shell.fs

    @property
    def available(self) -> bool:
        return self.error is None and not self.stale and self.source is not None

    def info(self) -> MountInfo:
        return MountInfo(self.path, self.mode, self.type, self.id, self.error, tuple(self.warnings), len(self.held))

    def machine(self, p: str) -> str:
        return self.path if p == "/" else self.path + p

    def capabilities(self) -> frozenset:
        return frozenset(getattr(self.source, "capabilities", ()) or ()) & CAPABILITIES

    # ----- talking to the source -----

    async def call(self, op: str, path: str, *args, to: str | None = None):
        """Calls the source, bounded by the call's time limit, and keeps the exchange while recording. Errors come out as
        MountConflictError, or ShellError with a text the agent may see; the full text is kept in `error_text`."""
        shell = self.mounts.shell
        fn = getattr(self.source, op)
        x = dict(mount=self.path, op=op, path=path, to=to)
        try:
            r = fn(path, to, *args) if op == "rename" else fn(path, *args)
            if inspect.isawaitable(r):
                left = shell._deadline - time.monotonic()
                r = await (asyncio.wait_for(r, max(left, 0)) if left != float("inf") else r)
        except asyncio.TimeoutError:
            self.mounts.log(MountExchange(**x, error=_msg("timedOut")))
            raise _SourceFailure(_msg("timedOut"), _msg("timedOut")) from None
        except MountConflictError as e:
            self.mounts.log(MountExchange(**x, error=str(e) or "conflict", conflict=True))
            raise
        except (asyncio.CancelledError, KeyboardInterrupt, _ControlFlow):
            raise
        except Exception as e:
            safe = _safe(e)
            self.mounts.log(MountExchange(**x, error=safe))
            raise _SourceFailure(safe, str(e) or type(e).__name__) from None
        if op == "list":
            r = tuple(itertools.islice(r or (), args[0] + 64))  # a few bad names may be skipped; never read without end
            self.mounts.log(MountExchange(**x, entries=r))
        elif op == "stat":
            self.mounts.log(MountExchange(**x, entry=r))
        elif op == "read":
            if not isinstance(r, (bytes, bytearray, memoryview)):
                raise _SourceFailure(_msg("notBytes"), _msg("notBytes"))
            r = bytes(r)
            self.mounts.log(MountExchange(**x, body=r))
        else:
            r = None if r is None else str(r)[:MAX_VERSION]
            self.mounts.log(MountExchange(**x, version=r))
        return r

    async def grow(self, queue: list, budget: int, deep: bool = True) -> None:
        """Lists the unlisted directories in `queue` ((source path, node, depth)), breadth first, then (deep) what they hold;
        no new directory is started past `budget` entries or LIST_SECONDS, and what is left stays "not listed yet"."""
        shell = self.mounts.shell
        limit = shell.max_extract_entries
        stop = time.monotonic() + LIST_SECONDS
        seen = 0
        while queue:
            bp, node, depth = queue.pop(0)
            if node.unlisted is None:
                continue
            left = limit - (len(self.base) - 1)  # a directory is listed whole (up to the entry limit), never in part
            if left <= 0 or seen >= budget or time.monotonic() > stop:
                if len(self.base) >= limit:
                    self.warn(_msg("truncated", dir=self.path, limit=str(limit)))
                return
            got = await self.call("list", bp, left)
            names, skipped = set(), 0
            good = []
            for e in got:
                if not _valid(e) or e.name in names:
                    skipped += 1
                    continue
                names.add(e.name)
                good.append(e)
            if skipped:
                self.warn(_msg("skipped", dir=self.machine(bp), count=str(skipped)))
            node.unlisted = None
            if len(good) > left:
                good = sorted(good, key=lambda e: e.name)[:left]
                self.warn(_msg("truncated", dir=self.path, limit=str(limit)))
            for e in sorted(good, key=lambda e: e.name):
                child = self.place(node, _join(bp, e.name), e, depth + 1 < MAX_DEPTH)
                seen += 1
                if e.is_dir and deep and depth + 1 < MAX_DEPTH:
                    queue.append((_join(bp, e.name), child, depth + 1))
            shell.check_deadline()

    def warn(self, w: str) -> None:
        if w not in self.warnings:
            self.warnings.append(w)

    def place(self, parent: _Dir, p: str, e: MountEntry, listable: bool):
        ro = self.mode == "read-only"
        owner = "root" if ro else self.mounts.shell.user
        when = e.modified or self.mounts.shell_now()
        if e.is_dir:
            node = _Dir(modified=when, owner=owner, mode=0o555 if ro else 0o755)
            if listable:
                node.unlisted = self.not_listed
        else:
            node = _File(modified=when, owner=owner, mode=0o444 if ro else 0o644, reported_size=e.size or 0, loader=self.miss)
        parent.children[p.rsplit("/", 1)[1]] = node
        self.base[p] = _Base(e, node)
        return node

    # ----- the files in the machine -----

    def build(self, entries: dict | None = None) -> _Dir:
        """Replaces the mount point's tree: the root, unlisted, or the saved `entries` (source path -> (entry, unlisted))."""
        fs = self.fs
        ro = self.mode == "read-only"
        with fs.as_root():
            _drop_children(fs, self.path)
            fs.mkdir(self.path, parents=True)
        root = fs._find(self.path)
        root.owner, root.mode = ("root", 0o555) if ro else (self.mounts.shell.user, 0o755)
        root.unlisted = self.not_listed if entries is None else None
        self.base = {"/": _Base(MountEntry("", True), root)}
        self.fetched, self.misses, self.failed = {}, {}, {}
        self.expected, self.absent = {}, set()
        for p, (e, unlisted) in (entries or {}).items():
            parent = self.base.get(get_parent(p))
            if p != "/" and parent is not None and parent.entry.is_dir:
                node = self.place(parent.node, p, e, unlisted)
                if not unlisted and isinstance(node, _Dir):
                    node.unlisted = None
        self.error = self.shown = None
        self.stale = False
        return root

    def unavailable(self, why: str | None, shown: str | None = None) -> None:
        """An empty mount point the agent can't open; the changes are kept for a later mount."""
        fs = self.fs
        with fs.as_root():
            _drop_children(fs, self.path)
            fs.mkdir(self.path, parents=True)
            fs.chmod(self.path, 0)
        self.base, self.error, self.shown = {}, why, shown or why

    async def reveal(self, path: str, deep: bool, budget: int = LIST_BUDGET) -> None:
        """Lists the unlisted directories on the way to a machine path, and the path itself if it is one (deep: all below)."""
        node = self.fs._find(self.path, follow=False)
        at = {id(b.node): p for p, b in self.base.items()}
        rest = path[len(self.path):].split("/")[1:] if path != self.path else []
        for name in rest + [None]:
            if not isinstance(node, _Dir):
                return
            bp = at.get(id(node))
            if node.unlisted is not None and bp is not None:
                await self.grow([(bp, node, bp.count("/") if bp != "/" else 0)], budget, deep=deep and name is None)
            elif name is None and deep:
                queue = [(at[id(n)], n, 0) for n in _dirs_under(node) if n.unlisted is not None and id(n) in at]
                await self.grow(queue, budget)
            if name is None:
                return
            node = node.children.get(name)

    def replay(self, changes: list[MountChange]) -> None:
        """Makes `changes` again on the tree, remembering the versions they were made on."""
        fs = self.fs
        with fs.as_root():
            for c in changes:
                path = self.machine(c.path)
                try:
                    if c.kind == "write":
                        fs.mkdir(get_parent(path), parents=True)
                        _store(fs, path, c.content or b"")
                    elif c.kind == "mkdir":
                        fs.mkdir(path, parents=True)
                    elif c.kind == "rename":
                        fs.move(path, self.machine(c.to))
                    elif c.kind == "delete":
                        fs.delete(path, recursive=True)
                except ShellError as e:
                    self.warn(f"{path}: {e}")
                if c.kind == "write" and c.version is None:
                    self.absent.add(c.path)
                elif c.kind != "mkdir":
                    self.expected.setdefault(c.path, c.version)

    async def reveal_for(self, changes: list[MountChange]) -> None:
        for c in changes:
            for p in (c.path, c.to):
                if p is not None:
                    await self.reveal(self.machine(get_parent(p)), False)

    def miss(self, node, path: str) -> None:
        """A placeholder was read before it was fetched: it is fetched after the command."""
        if self.mounts.items.get(self.path) is not self:
            raise ShellError(f"{path}: {_msg('removed')}")
        self.misses[id(node)] = node
        why = self.failed.get(id(node))
        if why == _msg("tooLarge"):
            raise ShellError(f"{path}: {why}")
        raise ShellError(f"{path}: {why + _msg('retry') if why else _msg('notFetched')}")

    def not_listed(self, node) -> str:
        """A directory nothing listed yet was looked into: it is listed after the command."""
        if self.mounts.items.get(self.path) is self:
            self.misses[id(node)] = node
        return _msg("notListed")

    async def fetch(self, nodes: list, err=None, where: str = "") -> None:
        """Fetches placeholders, FETCH_AT_ONCE at a time, making room by evicting clean fetched files; failures are kept for
        miss(). When the call's time runs out first, says how far it got."""
        fs, shell = self.fs, self.mounts.shell
        at = {id(b.node): (p, b) for p, b in self.base.items() if b.node is not None}
        todo = [n for n in nodes if isinstance(n, _File) and n.loader is not None and id(n) in at]
        done = 0
        for i in range(0, len(todo), FETCH_AT_ONCE):
            if time.monotonic() >= shell._deadline:
                if err is not None:
                    err.write("mount: " + _msg("progress", count=str(done), total=str(len(todo)), dir=where) + "\n")
                return
            batch = todo[i:i + FETCH_AT_ONCE]
            room = fs.capacity - fs.used_bytes
            need = sum(at[id(n)][1].entry.size or 0 for n in batch)
            if need > room:
                room += self.evict(need - room)
            share = max(room // len(batch), 0)
            results = await asyncio.gather(*(self.fetch_one(at[id(n)][0], at[id(n)][1], share) for n in batch), return_exceptions=True)
            for node, r in zip(batch, results):
                if isinstance(r, BaseException):
                    if isinstance(r, (_ControlFlow, asyncio.CancelledError, KeyboardInterrupt)):
                        raise r
                    self.failed[id(node)] = r.safe if isinstance(r, _SourceFailure) else str(r)
                    continue
                p, b = at[id(node)]
                if node.loader is None:  # written while we waited
                    continue
                if len(r) > fs.capacity - fs.used_bytes:
                    self.failed[id(node)] = _msg("tooLarge")
                    continue
                _fill(fs, node, r)
                b.digest, b.clock = hashlib.sha256(r).digest(), node.changed
                self.failed.pop(id(node), None)
                self.fetched[id(node)] = b
                done += 1

    async def fetch_one(self, p: str, b: _Base, room: int) -> bytes:
        if (b.entry.size or 0) > room:
            raise _SourceFailure(_msg("tooLarge"), _msg("tooLarge"))
        data = await self.call("read", p, room)
        if len(data) > room:
            raise _SourceFailure(_msg("tooLarge"), _msg("tooLarge"))
        return data

    def evict(self, need: int) -> int:
        """Turns the oldest unchanged fetched files back into placeholders; returns the bytes freed."""
        freed = 0
        for key, b in list(self.fetched.items()):
            if freed >= need:
                break
            del self.fetched[key]
            n = b.node
            if n.loader is not None or n.changed != b.clock or n.nlink != 1:
                continue
            freed += n.size
            self.fs.used_bytes -= n.size
            n.content, n.data, n.appended, n.size = "", None, [], 0
            n.reported_size, n.loader = b.entry.size or 0, self.miss
            b.digest = b.clock = None
        return freed

    def stubs_under(self, path: str) -> list:
        """The placeholders at or below a machine path (no symbolic link is followed)."""
        node = self.fs._find(path, follow=False)
        out, stack = [], [node]
        while stack:
            n = stack.pop()
            if isinstance(n, _File) and n.loader is not None:
                out.append(n)
            elif isinstance(n, _Dir):
                stack.extend(n.children[k] for k in sorted(n.children, reverse=True))
        return out

    # ----- the change set -----

    def old_version(self, p: str) -> str | None:
        """The version a base path was read with (or that a replayed change was made on)."""
        if p in self.expected:
            return self.expected[p]
        b = self.base.get(p)
        return b.entry.version if b is not None else None

    def changes(self) -> list[MountChange]:
        """The agent's changes, in an order that applies one after another: deletes of names about to be replaced, renames
        and mkdirs as the new tree has them (parents first, moving a name out of the way through a temporary name when it is
        taken), then writes, then the remaining deletes (children first). A file still only a placeholder somewhere in the
        machine (moved out of the mount) is not deleted until it is fetched."""
        if not self.available:
            return []
        root = self.fs._find(self.path, follow=False)
        if not isinstance(root, _Dir) or not self.base:
            return []
        in_tree = _ids(root)
        at = {id(b.node): p for p, b in self.base.items() if p != "/"}
        where = {}  # node -> its path in the new tree
        for n, p in _walk_paths(root, "/"):
            where.setdefault(id(n), p)
        gone = sorted((p for p, b in self.base.items() if p != "/" and id(b.node) not in in_tree), reverse=True)
        held = {p for p in gone if isinstance(self.base[p].node, _File) and self.base[p].node.nlink > 0 and self.base[p].node.loader is not None}
        for p in held:
            self.misses[id(self.base[p].node)] = self.base[p].node  # fetched after the next command, then deleted
        held |= {p for p in gone if any(q.startswith(p + "/") for q in held)}
        dropped: set[str] = set(held)
        visited: set[int] = set()
        moved_done: set[str] = set()
        ops, writes = [], []
        renames: list[tuple[str, str]] = []  # (from, to) as applied so far, for where a base path is now
        swap = itertools.count(1)

        def now_at(p: str) -> str:
            for f, t in renames:
                if p == f or p.startswith(f + "/"):
                    p = t + p[len(f):]
            return p

        def base_at(p: str) -> str:
            """The base path whose entry is at source path p now (renames undone)."""
            for f, t in reversed(renames):
                if p == t or p.startswith(t + "/"):
                    p = f + p[len(t):]
            return p

        def write_version(p: str) -> str | None:
            return None if p in self.absent else self.old_version(p)

        def drop(q: str) -> None:
            """q is gone and something else stands where it was: delete it, and what was below it, first."""
            for g in gone:
                if (g == q or g.startswith(q + "/")) and g not in dropped:
                    dropped.add(g)
                    ops.append(MountChange("delete", now_at(g), version=self.old_version(g)))

        def clear(p: str) -> None:
            """Source path p is about to be taken: a base entry still there that moves later goes to a temporary name."""
            q = base_at(p)
            b = self.base.get(q)
            if b is not None and q not in moved_done and id(b.node) in in_tree and where.get(id(b.node)) != p:
                tmp = f"{p}.topwater-swap-{next(swap)}"
                ops.append(MountChange("rename", p, to=tmp, version=self.old_version(q)))
                renames.append((p, tmp))

        def walk(d: _Dir, path: str, was: str | None) -> None:
            for name in sorted(d.children):
                n = d.children[name]
                if not isinstance(n, (_Dir, _File)):
                    continue  # symbolic links are not part of a change set
                p = _join(path, name)
                bp = at.get(id(n))
                if id(n) in visited:
                    bp = None  # a second hard link
                visited.add(id(n))
                orig = _join(was, name) if was is not None else None
                old = self.base.get(orig) if orig is not None else None
                replaced = old is not None and id(old.node) not in in_tree
                if bp is not None:
                    if bp != orig:
                        if replaced:
                            drop(orig)
                        clear(p)
                        ops.append(MountChange("rename", now_at(bp), to=p, version=self.old_version(bp)))
                        renames.append((now_at(bp), p))
                    moved_done.add(bp)
                    b = self.base[bp]
                    if isinstance(n, _Dir):
                        walk(n, p, bp)
                    elif n.loader is None and n.changed != b.clock and _digest(n) != b.digest:
                        writes.append(MountChange("write", p, version=write_version(bp), content=n.raw()))
                    continue
                if replaced and isinstance(n, _File) == (not old.entry.is_dir):
                    dropped.add(orig)  # a file replaced by a file is a write; a directory made again is the same one
                    if isinstance(n, _Dir):
                        walk(n, p, orig)
                    elif n.loader is None:
                        writes.append(MountChange("write", p, version=write_version(orig), content=n.raw()))
                    continue
                if replaced:
                    drop(orig)
                if isinstance(n, _Dir):
                    clear(p)  # a write needs no room yet: writes come after every rename
                    ops.append(MountChange("mkdir", p))
                    walk(n, p, None)
                elif n.loader is None:  # shortcut: a placeholder moved in from another mount is left out
                    version = None if p in self.base or p in self.absent else self.expected.get(p)
                    writes.append(MountChange("write", p, version=version, content=n.raw()))

        walk(root, "/", "/")
        deletes = [MountChange("delete", now_at(p), version=self.old_version(p)) for p in gone if p not in dropped]
        return ops + writes + deletes

    async def apply(self, changes: list[MountChange] | None) -> MountApplyResult:
        current = self.changes()
        todo = current if changes is None else [c for c in changes if c in current]
        if not self.available:
            return MountApplyResult([], todo[0] if todo else None, self.error or _msg("notListedYet"), False, tuple(todo[1:]))
        caps = self.capabilities()
        applied = []
        self.busy = True
        try:
            for i, c in enumerate(todo):
                version = None
                try:
                    if self.mode == "read-only":
                        raise _SourceFailure(_msg("readOnly"), _msg("readOnly"))
                    if c.kind not in caps:
                        raise _SourceFailure(_msg("cannot", kind=c.kind), _msg("cannot", kind=c.kind))
                    now = await self.call("stat", c.path)
                    if c.kind == "mkdir" or (c.kind != "write" and now is not None and now.is_dir):
                        if (now is not None) != (c.kind != "mkdir"):  # directories: only whether they exist
                            raise MountConflictError(f"{c.path}: {'already exists' if now is not None else 'missing'}")
                    elif (None if now is None else now.version) != c.version:
                        raise MountConflictError(f"{c.path}: version {None if now is None else now.version}, expected {c.version}")
                    if c.kind == "rename" and await self.call("stat", c.to) is not None:
                        raise MountConflictError(f"{c.to}: already exists")
                    if c.kind == "write":
                        version = await self.call("write", c.path, c.content or b"", c.version)
                    elif c.kind == "delete":
                        await self.call("delete", c.path, c.version)
                    elif c.kind == "rename":
                        version = await self.call("rename", c.path, c.version, to=c.to)
                    else:
                        version = await self.call("mkdir", c.path)
                except MountConflictError as e:
                    self.last_safe = None
                    return MountApplyResult(applied, c, str(e) or "conflict", True, tuple(todo[i + 1:]))
                except _SourceFailure as e:
                    self.last_safe = e.safe
                    return MountApplyResult(applied, c, e.full, False, tuple(todo[i + 1:]))
                self.applied(c, version)
                applied.append(c)
        finally:
            self.busy = False
        return MountApplyResult(applied)

    def applied(self, c: MountChange, version: str | None) -> None:
        """What a change applied to the source becomes the mount's new starting point."""
        for p in (c.path, c.to):
            if p is not None:
                self.expected.pop(p, None)
                self.absent.discard(p)
        if c.kind == "delete":
            self.base.pop(c.path, None)
            return
        if c.kind == "rename":
            for p in sorted(p for p in self.base if p == c.path or p.startswith(c.path + "/")):
                self.base[c.to + p[len(c.path):]] = self.base.pop(p)
            b = self.base.get(c.to)
            if b is not None:
                b.entry = MountEntry(c.to.rsplit("/", 1)[1], b.entry.is_dir, b.entry.size, b.entry.modified,
                                     b.entry.version if version is None else version)
            return
        node = self.fs._find(self.machine(c.path), follow=False)
        if c.kind == "mkdir":
            self.base[c.path] = _Base(MountEntry(c.path.rsplit("/", 1)[1], True, modified=self.mounts.shell_now(), version=version or ""), node)
            return
        data = c.content or b""
        b = self.base[c.path] = _Base(MountEntry(c.path.rsplit("/", 1)[1], False, len(data), self.mounts.shell_now(), str(version or "")),
                                      node, hashlib.sha256(data).digest())
        if isinstance(node, _File):
            b.clock = node.changed if _digest(node) == b.digest else None


class Mounts:
    """A shell's mounts (Shell.mount and friends call these), the sources the host mounted (by type and id, with the mode it
    chose), and, while recording, the source exchanges of the current call."""

    def __init__(self, shell):
        self.shell = shell
        self.items: dict[str, _Mount] = {}
        self.sources: dict[tuple[str, str], tuple[object, str]] = {}  # (type, id) -> (source, the host's mode)
        self.replay: list[MountExchange] | None = None  # a replay's recorded exchanges: answered instead of any source
        self._log: list[MountExchange] = []

    def shell_now(self) -> datetime:
        return utc_now()

    def log(self, x: MountExchange) -> None:
        if self.shell._recorder is not None:
            self._log.append(x)

    def take_log(self) -> list[MountExchange]:
        log, self._log = self._log, []
        return log

    def find(self, path: str) -> _Mount | None:
        """The mount holding a machine path."""
        for m in self.items.values():
            if path == m.path or path.startswith(m.path + "/"):
                return m
        return None

    def get(self, path: str) -> _Mount:
        m = self.items.get(normalize("/", path))
        if m is None:
            raise ValueError(f"{path}: not a mount point")
        return m

    def source_for(self, type_: str, id_: str, path: str, mode: str) -> tuple[object, str]:
        """The source and mode a restored mount gets: the host's (never the file's mode), or a replay's answers."""
        if self.replay is not None:
            return _ReplaySource(self.replay, path, type_, id_), mode
        return self.sources.get((type_, id_), (None, mode))

    # ----- the host's API -----

    async def mount(self, path: str, source, mode: str = "read-only") -> None:
        if mode not in MODES:
            raise ValueError(f"{mode}: the mode is one of {', '.join(MODES)}")
        if not isinstance(path, str) or not path.startswith("/"):
            raise ValueError(f"{path}: a mount point is an absolute path other than /")
        path = normalize("/", path)
        if path == "/":
            raise ValueError(f"{path}: a mount point is an absolute path other than /")
        if mode != "read-only" and "write" not in (getattr(source, "capabilities", ()) or ()):
            raise ValueError(f"{path}: the source can't write, so it can only be mounted read-only")
        old = self.items.get(path)
        if old is not None:
            if old.source is not None or (old.type, old.id) != (str(getattr(source, "type", "")), str(getattr(source, "id", ""))):
                raise ValueError(f"{path}: already a mount point; unmount it first")
        else:
            if any(path.startswith(p + "/") or p.startswith(path + "/") for p in self.items):
                raise ValueError(f"{path}: inside another mount, or holds one")
            fs = self.shell.fs
            if fs.exists(path, follow=False) and not (fs.is_dir(path, follow=False) and not fs.listdir(path)):
                raise ValueError(f"{path}: exists and is not an empty directory")
        m = _Mount(self, path, source, mode)
        if old is not None:
            m.held = old.held  # still the host's to accept
        root = m.build()
        try:
            await m.grow([("/", root, 0)], LIST_BUDGET)
        except _SourceFailure as e:
            self._cleanup(m, old)
            raise ValueError(f"{path}: {e.full}") from None
        except BaseException:
            self._cleanup(m, old)
            raise
        self.items[path] = m
        self.sources[(m.type, m.id)] = (source, mode)

    def _cleanup(self, m: _Mount, old: _Mount | None) -> None:
        if old is not None:
            old.unavailable(old.error, old.shown)
        else:
            with self.shell.fs.as_root():
                _drop_children(self.shell.fs, m.path)

    def unmount(self, path: str) -> None:
        m = self.get(path)
        del self.items[m.path]
        if not any((o.type, o.id) == (m.type, m.id) for o in self.items.values()):
            self.sources.pop((m.type, m.id), None)
        with self.shell.fs.as_root():
            _drop_children(self.shell.fs, m.path)
            self.shell.fs.chmod(m.path, 0o755)

    async def refresh(self, path: str) -> None:
        m = self.get(path)
        if m.source is None:
            raise _SourceFailure(m.shown or "unavailable", m.error or "unavailable")
        changes = m.changes()
        root = m.build()
        await m.grow([("/", root, 0)], LIST_BUDGET)
        await m.reveal_for(changes)
        m.replay(changes)

    def discard(self, path: str, held: bool = True) -> None:
        """Drops the agent's changes (and, with held, a loaded snapshot's): the files are the source's again."""
        m = self.get(path)
        if held:
            m.held = []
        if m.available:
            m.build({p: (b.entry, isinstance(b.node, _Dir) and b.node.unlisted is not None) for p, b in m.base.items() if p != "/"})

    async def accept(self, path: str) -> None:
        """Makes a loaded snapshot's changes the agent's: in the files, and in pending_changes."""
        m = self.get(path)
        if not m.available:
            raise ValueError(f"{path}: {m.error or 'not listed yet'}; mount it, or run a call first")
        changes, m.held = m.held, []
        await m.reveal_for(changes)
        m.replay(changes)

    # ----- around commands -----

    async def prepare(self) -> None:
        """At the start of a call: lists the mounts a restore brought back, within the listing budget. A listing that fails
        leaves the mount unavailable for this call (tried again at the next); the call itself goes on."""
        for m in list(self.items.values()):
            if not (m.stale and m.source is not None):
                continue
            try:
                root = m.build()
                await m.grow([("/", root, 0)], LIST_BUDGET)
            except _SourceFailure as e:
                m.unavailable(e.full, e.safe)
                m.stale = True
                continue
            if m.trusted and m.held:
                changes, m.held = m.held, []
                await m.reveal_for(changes)
                m.replay(changes)

    def _named(self, w: str) -> list[str]:
        shell = self.shell
        out = []
        for part in ((w, w.split("=", 1)[1]) if w.startswith("-") and "=" in w else (w,)):
            if part:
                p = shell.resolve(part)
                out.append(shell.fs.real_path(p, existing=False)[0] or p)
        return out

    async def before(self, name: str, argv: list[str], err=None, redirect: str | None = None) -> None:
        """Lists and fetches what a command is about to use: the paths it names (a directory: one level, or everything below
        for a command that walks it), the file it redirects from or appends to, and the working directory only for a
        recursive grep given no path. Commands that read no content only list. A file moved out of its mount by `mv` is
        fetched first, so its content is never lost."""
        shell = self.shell
        words = [redirect] if redirect is not None else argv[1:]
        paths = [p for w in words for p in self._named(w)]
        if redirect is None and name in ("grep", "egrep", "fgrep", "rg") and _recursive(argv) \
                and not any(self.find(p) and shell.fs.exists(p, follow=False) for p in paths):
            cwd = shell.fs.real_path(shell._cwd)[0] or shell._cwd
            paths.append(cwd)
        if redirect is None and "/" in argv[0]:
            paths += self._named(argv[0])  # a script run by its path
        deep = name in DEEP or _recursive(argv)
        content = redirect is not None or name not in NO_CONTENT
        out_of = set()
        if name == "mv" and len(paths) >= 2:
            dest = self.find(paths[-1])
            out_of = {p for p in paths[:-1] if (m := self.find(p)) is not None and m is not dest}
        for p in paths:
            m = self.find(p)
            if m is None or not m.available:
                continue
            await m.reveal(p, deep or p in out_of)
            if (content or p in out_of) and shell.fs.exists(p, follow=False):
                await m.fetch(m.stubs_under(p), err, p)

    async def after(self) -> None:
        """After a command: lists and fetches what it missed."""
        for m in list(self.items.values()):
            if m.misses and m.available:
                nodes, m.misses = list(m.misses.values()), {}
                for n in nodes:
                    if isinstance(n, _Dir) and n.unlisted is not None:
                        bp = next((p for p, b in m.base.items() if b.node is n), None)
                        if bp is not None:
                            await m.grow([(bp, n, bp.count("/"))], LIST_BUDGET, deep=False)
                await m.fetch([n for n in nodes if isinstance(n, _File)])

    async def settle(self, err) -> None:
        """Live mode: applies the changes made so far (after a command's redirections are written, and when the call ends).
        A change that fails is reported once and left pending; the changes after it are still tried, and it is tried again
        only once it changes."""
        for m in list(self.items.values()):
            if m.mode != "live" or not m.available or m.busy:
                continue
            todo = [c for c in m.changes() if c not in m.stuck]
            while todo:
                r = await m.apply(todo)
                if r.failed is None:
                    break
                m.stuck.append(r.failed)
                where = m.machine(r.failed.path)
                reason = m.last_safe or _msg("sourceFailed")
                err.write("mount: " + (_msg("conflict", path=where, dir=m.path) if r.conflict else
                                       _msg("failed", path=where, reason=reason, dir=m.path)) + "\n")
                todo = list(r.not_tried)
            current = m.changes()
            m.stuck = [c for c in m.stuck if c in current]  # applied, discarded or changed since: no longer stuck

    # ----- snapshots and recordings -----

    def outside(self, entries) -> list:
        """The filesystem's entries without what is inside a mount (a snapshot keeps the mount's definition instead)."""
        if not self.items:
            return list(entries)
        out = []
        for e in entries:
            if self.find(e[0]) is None:
                out.append(e[:9] + (None,) if e[9] is not None and self.find(e[9]) is not None else e)  # it carries the content
        return out

    def capture(self) -> list[dict]:
        """Each mount's definition and changes, and (in memory only, never saved) its listing, so restoring the snapshot in
        this process needs no source call and keeps the agent's changes."""
        out = []
        for m in self.items.values():
            d = {"path": m.path, "mode": m.mode, "type": m.type, "id": m.id, "changes": m.changes(), "held": list(m.held)}
            if m.available:
                d["listing"] = _listing(m)
            out.append(d)
        return out

    def restore(self, saved: list[dict]) -> None:
        """After the files were replaced: the snapshot's mounts with the host's sources and modes. A snapshot taken in this
        process brings its listing and changes back as they were; one loaded from a file is listed at the next call, and its
        changes wait for the host (accept_snapshot_changes), never applied or shown as the agent's on their own."""
        self.items = {}
        for d in saved:
            source, mode = self.source_for(d["type"], d["id"], d["path"], d["mode"])
            m = _Mount(self, d["path"], source, mode)
            m.type, m.id = d["type"], d["id"]
            self.items[m.path] = m
            listing = d.get("listing")
            m.held = list(d.get("held", ())) + ([] if listing is not None and source is not None else list(d["changes"]))
            m.trusted = self.replay is not None  # a replay's sources are its recorded answers: nothing real to protect
            if source is None:
                m.unavailable(_msg("unavailable", source=f"{m.type}:{m.id}"))
            elif listing is not None:
                m.build(listing)
                m.replay(list(d["changes"]))
            else:
                m.unavailable(None)
                m.stale = True

    def start_exchanges(self) -> list[MountExchange]:
        """What the mounts hold when a recording starts, as the exchanges a replay's first listing and reads answer from."""
        out = []
        for m in self.items.values():
            if not m.available:
                continue
            kids: dict[str, list] = {p: [] for p, b in m.base.items() if b.entry.is_dir and not (isinstance(b.node, _Dir) and b.node.unlisted)}
            for p, b in m.base.items():
                if p != "/" and get_parent(p) in kids:
                    kids[get_parent(p)].append(b.entry)
            for p, b in m.base.items():
                if p in kids:
                    out.append(MountExchange(m.path, "list", p, entries=tuple(kids[p])))
                elif b.digest is not None and isinstance(b.node, _File) and b.node.loader is None and _digest(b.node) == b.digest:
                    out.append(MountExchange(m.path, "read", p, body=b.node.raw()))
        return out


class _ReplaySource:
    """Answers a replay's questions from the recorded exchanges of one mount, never the real source."""
    capabilities = CAPABILITIES

    def __init__(self, exchanges: list[MountExchange], mount: str, type_: str, id_: str):
        self.type, self.id = type_, id_
        self._answers: dict[tuple, list[MountExchange]] = {}
        for x in exchanges:
            if x.mount == mount:
                self._answers.setdefault((x.op, x.path, x.to), []).append(x)

    def _answer(self, op: str, path: str, to: str | None = None) -> MountExchange:
        q = self._answers.get((op, path, to))
        if not q:
            raise MountSourceError(_msg("replay", op=op, path=path))
        x = q.pop(0) if len(q) > 1 else q[0]  # the last answer repeats
        if x.conflict:
            raise MountConflictError(x.error)
        if x.error is not None:
            raise MountSourceError(x.error)
        return x

    def list(self, path, max_entries):
        return list(self._answer("list", path).entries or ())

    def stat(self, path):
        return self._answer("stat", path).entry

    def read(self, path, max_bytes):
        return self._answer("read", path).body or b""

    def write(self, path, data, expected_version):
        return self._answer("write", path).version

    def delete(self, path, expected_version):
        self._answer("delete", path)

    def rename(self, path, to, expected_version):
        return self._answer("rename", path, to).version

    def mkdir(self, path):
        return self._answer("mkdir", path).version


# ---------- helpers ----------

class _SourceFailure(Exception):
    """A source call that failed: `safe` for the agent and recordings, `full` for the host."""

    def __init__(self, safe: str, full: str):
        super().__init__(safe)
        self.safe, self.full = safe, full


def _safe(e: BaseException) -> str:
    if isinstance(e, MountSourceError):
        return str(e) or _msg("sourceFailed")
    if isinstance(e, _SourceFailure):
        return e.safe
    return _msg("sourceFailed")


def _recursive(argv: list[str]) -> bool:
    return any(a in ("--recursive", "--dereference-recursive") or (a.startswith("-") and not a.startswith("--") and ("r" in a or "R" in a))
               for a in argv[1:])


def _valid(e) -> bool:
    """An entry a source listed that can be a file: a plain name, sane size and version."""
    if not isinstance(e, MountEntry) or not isinstance(e.name, str) or not isinstance(e.version, str):
        return False
    n = e.name
    return (n not in ("", ".", "..") and "/" not in n and "\0" not in n and len(n.encode("utf-8", "surrogatepass")) <= MAX_NAME_BYTES
            and (e.size is None or isinstance(e.size, int) and not isinstance(e.size, bool) and e.size >= 0)
            and len(e.version) <= MAX_VERSION and (e.modified is None or isinstance(e.modified, datetime)))


def _digest(node: _File) -> bytes:
    return hashlib.sha256(node.raw()).digest()


def _ids(root: _Dir) -> set[int]:
    out, stack = set(), [root]
    while stack:
        n = stack.pop()
        out.add(id(n))
        if isinstance(n, _Dir):
            stack.extend(n.children.values())
    return out


def _walk_paths(d: _Dir, path: str):
    for name in sorted(d.children):
        n = d.children[name]
        p = _join(path, name)
        yield n, p
        if isinstance(n, _Dir):
            yield from _walk_paths(n, p)


def _dirs_under(d: _Dir) -> list:
    out, stack = [], [d]
    while stack:
        n = stack.pop(0)
        out.append(n)
        stack.extend(c for k, c in sorted(n.children.items()) if isinstance(c, _Dir))
    return out


def _listing(m: _Mount) -> dict:
    return {p: (b.entry, isinstance(b.node, _Dir) and b.node.unlisted is not None) for p, b in m.base.items() if p != "/"}


def _drop_children(fs, path: str) -> None:
    """Empties a directory (as root), whatever is listed or not in it."""
    n = fs._find(path, follow=False)
    if isinstance(n, _Dir):
        n.unlisted = None
        for name in list(n.children):
            child = n.children[name]
            if isinstance(child, _Dir):
                _forget_unlisted(child)
            fs.delete(_join(path, name), recursive=True)
    elif n is not None:
        fs.delete(path, recursive=True)


def _forget_unlisted(d: _Dir) -> None:
    for n in _dirs_under(d):
        n.unlisted = None


def _fill(fs, node: _File, data: bytes) -> None:
    """Puts fetched content into a placeholder without marking it changed by the agent."""
    try:
        node.content, node.data = data.decode("utf-8"), None
    except UnicodeDecodeError:
        node.content, node.data = "", data
    node.appended, node.size = [], len(data)
    node.loader = node.reported_size = None
    fs.used_bytes += len(data)


def _store(fs, path: str, data: bytes) -> None:
    try:
        fs.write_text(path, data.decode("utf-8"))
    except UnicodeDecodeError:
        fs.write_bytes(path, data)
