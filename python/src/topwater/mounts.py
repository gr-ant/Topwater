"""Mounted sources: outside stores of files shown as directories in the machine (docs/mounts.md). See docs/architecture.md."""
from __future__ import annotations

import asyncio
import hashlib
import inspect
import os
import stat as _stat
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone

from ._spec import COMMANDS
from .vfs import ShellError, _Dir, _File, get_parent, normalize, utc_now

MODES = ("read-only", "staged", "live")
CAPABILITIES = frozenset({"write", "delete", "rename", "mkdir"})
MAX_DEPTH = 32  # directories below the mount point that are listed
MAX_NAME_BYTES = 255
MAX_VERSION = 1024  # characters in a version
# commands that never read a file's content: nothing is fetched before them
NO_CONTENT = frozenset({"ls", "find", "stat", "du", "df", "tree", "cd", "pwd", "test", "[", "mkdir", "rmdir", "rm", "mv", "touch",
                        "mount", "chmod", "chown", "chgrp", "ln", "realpath", "readlink", "basename", "dirname", "echo", "printf"})


def _msg(key: str, **values: str) -> str:
    from .commands import spec_format
    return spec_format(COMMANDS["mount"].messages[key], **values)


def _join(d: str, name: str) -> str:
    return "/" + name if d == "/" else d + "/" + name


# ---------- sources ----------

@dataclass(frozen=True)
class MountEntry:
    """One entry a source lists: name (one path segment), is_dir, size in bytes, modified (UTC, naive; None: unknown) and
    version, an opaque string that changes when the entry does (an ETag, a revision, a modification time)."""
    name: str
    is_dir: bool
    size: int = 0
    modified: datetime | None = None
    version: str = ""


class MountConflictError(Exception):
    """A source raises it when the expected version no longer matches what it holds."""


class MountSource:
    """What a source implements (docs/mounts.md). Every method may be a coroutine. Paths are canonical and absolute within
    the source: "/" is its root. `type` and `id` are saved in snapshots, so they must not hold secrets; `capabilities` says
    which of write, delete, rename and mkdir it supports (none: read-only)."""
    type = "source"
    id = ""
    capabilities: frozenset = frozenset()

    def list(self, path: str) -> list[MountEntry]:
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

    def rename(self, path: str, to: str, expected_version: str | None) -> None:
        raise NotImplementedError

    def mkdir(self, path: str) -> None:
        raise NotImplementedError


class LocalFolderSource(MountSource):
    """A directory on the host (an Obsidian vault, a folder of notes). Read-only unless `writable`. Symbolic links in it are
    left out and never followed; nothing outside `root` is reached. The version is the modification time and size."""
    type = "local-folder"

    def __init__(self, root: str | os.PathLike, writable: bool = False, id: str | None = None):
        self._root = os.path.realpath(root)
        if not os.path.isdir(self._root):
            raise ValueError(f"{root}: not a directory")
        self.id = id if id is not None else os.path.basename(self._root)
        self.capabilities = CAPABILITIES if writable else frozenset()

    def _host(self, path: str) -> str:
        if normalize("/", path) != path or "\0" in path:
            raise ValueError(f"{path}: not a canonical path")
        full = self._root + path if path != "/" else self._root
        # no symbolic link on the way, the last name included: one swapped in after listing could lead out of root
        cur = self._root
        for seg in path.split("/")[1:] if path != "/" else ():
            cur = os.path.join(cur, seg)
            if os.path.islink(cur):
                raise FileNotFoundError(path)
        return full

    @staticmethod
    def _entry(name: str, st: os.stat_result) -> MountEntry:
        is_dir = _stat.S_ISDIR(st.st_mode)
        return MountEntry(name, is_dir, 0 if is_dir else st.st_size,
                          datetime.fromtimestamp(st.st_mtime, timezone.utc).replace(tzinfo=None), f"{st.st_mtime_ns}:{st.st_size}")

    def _stat(self, path: str) -> os.stat_result | None:
        try:
            st = os.lstat(self._host(path))
        except FileNotFoundError:
            return None
        return st if _stat.S_ISDIR(st.st_mode) or _stat.S_ISREG(st.st_mode) else None

    def _check(self, path: str, expected: str | None) -> None:
        st = self._stat(path)
        now = None if st is None else self._entry("", st).version
        if now != expected:
            raise MountConflictError(f"{path}: version {now}, expected {expected}")

    def list(self, path: str) -> list[MountEntry]:
        out = []
        with os.scandir(self._host(path)) as it:
            for e in it:
                st = e.stat(follow_symlinks=False)
                if _stat.S_ISDIR(st.st_mode) or _stat.S_ISREG(st.st_mode):
                    out.append(self._entry(e.name, st))
        return out

    def stat(self, path: str) -> MountEntry | None:
        st = self._stat(path)
        return None if st is None else self._entry(path.rsplit("/", 1)[-1], st)

    def read(self, path: str, max_bytes: int) -> bytes:
        fd = os.open(self._host(path), os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
        with os.fdopen(fd, "rb") as f:
            if os.fstat(f.fileno()).st_size > max_bytes:
                raise ValueError(_msg("tooLarge"))
            return f.read(max_bytes + 1)

    def write(self, path: str, data: bytes, expected_version: str | None) -> str:
        self._check(path, expected_version)
        full = self._host(path)
        tmp = f"{full}.topwater-{os.getpid()}.tmp"
        with os.fdopen(os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o644), "wb") as f:
            f.write(data)
        os.replace(tmp, full)
        return self._entry("", os.lstat(full)).version

    def delete(self, path: str, expected_version: str | None) -> None:
        st = self._stat(path)
        if st is None or not _stat.S_ISDIR(st.st_mode):
            self._check(path, expected_version)
        (os.rmdir if st is not None and _stat.S_ISDIR(st.st_mode) else os.remove)(self._host(path))

    def rename(self, path: str, to: str, expected_version: str | None) -> None:
        if self._stat(to) is not None:
            raise MountConflictError(f"{to}: already exists")
        st = self._stat(path)
        if st is None or not _stat.S_ISDIR(st.st_mode):
            self._check(path, expected_version)
        os.rename(self._host(path), self._host(to))

    def mkdir(self, path: str) -> None:
        os.mkdir(self._host(path))


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

    async def list(self, path: str) -> list[MountEntry]:
        await self._enter("list", path)
        if self._items.get(path, (b"",))[0] is not None:
            raise FileNotFoundError(path)
        prefix = path.rstrip("/") + "/"
        return [self._entry(p) for p in self._items if p.startswith(prefix) and "/" not in p[len(prefix):] and p != "/"]

    async def stat(self, path: str) -> MountEntry | None:
        await self._enter("stat", path)
        return self._entry(path)

    async def read(self, path: str, max_bytes: int) -> bytes:
        await self._enter("read", path)
        data = self._items.get(path, (None,))[0]
        if data is None:
            raise FileNotFoundError(path)
        return data

    async def write(self, path: str, data: bytes, expected_version: str | None) -> str:
        await self._enter("write", path)
        self._check(path, expected_version)
        if self._items.get(get_parent(path), (b"",))[0] is not None:
            raise FileNotFoundError(get_parent(path))
        version = self._version()
        self._items[path] = (bytes(data), version)
        return version

    async def delete(self, path: str, expected_version: str | None) -> None:
        await self._enter("delete", path)
        item = self._items.get(path)
        if item is not None and item[0] is None:
            if any(p.startswith(path + "/") for p in self._items):
                raise OSError(f"{path}: Directory not empty")
        else:
            self._check(path, expected_version)
        del self._items[path]

    async def rename(self, path: str, to: str, expected_version: str | None) -> None:
        await self._enter("rename", path)
        if to in self._items:
            raise MountConflictError(f"{to}: already exists")
        item = self._items.get(path)
        if item is None or item[0] is not None:
            self._check(path, expected_version)
        if self._items.get(get_parent(to), (b"",))[0] is not None:
            raise FileNotFoundError(get_parent(to))
        for p in [p for p in self._items if p == path or p.startswith(path + "/")]:
            self._items[to + p[len(path):]] = self._items.pop(p)

    async def mkdir(self, path: str) -> None:
        await self._enter("mkdir", path)
        if path in self._items:
            raise MountConflictError(f"{path}: already exists")
        if self._items.get(get_parent(path), (b"",))[0] is not None:
            raise FileNotFoundError(get_parent(path))
        self._items[path] = (None, self._version())


# ---------- changes ----------

@dataclass(frozen=True)
class MountChange:
    """One operation that makes the source match the mount: kind "write", "delete", "rename" or "mkdir"; path (and `to`
    for a rename) within the source; version: what the entry was when read (None: it must not exist); content for a write."""
    kind: str
    path: str
    to: str | None = None
    version: str | None = None
    content: bytes | None = None


@dataclass(frozen=True)
class MountApplyResult:
    """What apply_changes did: the changes applied, the one it stopped at (None if none) with the error, and whether that
    was a conflict (the source changed since it was read)."""
    applied: list
    failed: MountChange | None = None
    error: str | None = None
    conflict: bool = False


@dataclass(frozen=True)
class MountInfo:
    """A mount as `shell.mounts` lists it. error: why it is unavailable (None when it works); warnings: what its last
    listing left out."""
    path: str
    mode: str
    type: str
    id: str
    error: str | None = None
    warnings: tuple = ()


@dataclass(frozen=True)
class MountExchange:
    """One question a recording kept and its answer: list (entries), stat (entry; None: missing), read (body), write
    (version), delete, rename, mkdir; error and conflict when the source refused."""
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
        self.pending: list[MountChange] | None = None  # the changes while not built (after a restore, or unavailable)
        self.error: str | None = None
        self.warnings: list[str] = []
        self.misses: dict[int, tuple] = {}  # files read before they were fetched: fetched after the command
        self.failed: dict[int, str] = {}  # why a file could not be fetched
        self.fetched: dict[int, _Base] = {}  # in fetch order, for eviction
        self.stuck: list | None = None  # live mode: the changes left when applying last failed, not retried until they change

    @property
    def fs(self):
        return self.mounts.shell.fs

    def info(self) -> MountInfo:
        return MountInfo(self.path, self.mode, self.type, self.id, self.error, tuple(self.warnings))

    def machine(self, p: str) -> str:
        return self.path if p == "/" else self.path + p

    # ----- talking to the source -----

    async def call(self, op: str, path: str, *args, to: str | None = None):
        """Calls the source, bounded by the call's time limit, and keeps the exchange while recording."""
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
            raise ShellError(_msg("timedOut")) from None
        except MountConflictError as e:
            self.mounts.log(MountExchange(**x, error=str(e) or "conflict", conflict=True))
            raise
        except (asyncio.CancelledError, KeyboardInterrupt):
            raise
        except Exception as e:
            self.mounts.log(MountExchange(**x, error=str(e) or type(e).__name__))
            raise
        if op == "list":
            r = tuple(r or ())
            self.mounts.log(MountExchange(**x, entries=r))
        elif op == "stat":
            self.mounts.log(MountExchange(**x, entry=r))
        elif op == "read":
            self.mounts.log(MountExchange(**x, body=bytes(r)))
        else:
            self.mounts.log(MountExchange(**x, version=r if op == "write" else None))
        return r

    async def list_tree(self) -> dict[str, MountEntry]:
        """Every entry, parents first, bounded by the entry limit and MAX_DEPTH; hostile names are skipped."""
        limit = self.mounts.shell.max_extract_entries
        out: dict[str, MountEntry] = {}
        self.warnings, skipped = [], 0
        queue = [("/", 0)]
        while queue:
            d, depth = queue.pop(0)
            names = set()
            good = []
            for e in await self.call("list", d):
                if not _valid(e) or e.name in names:
                    skipped += 1
                    continue
                names.add(e.name)
                good.append(e)
            for e in sorted(good, key=lambda e: e.name):
                if len(out) >= limit:
                    self.warnings.append(_msg("truncated", dir=self.path, limit=str(limit)))
                    queue = []
                    break
                p = _join(d, e.name)
                out[p] = e
                if e.is_dir and depth + 1 < MAX_DEPTH:
                    queue.append((p, depth + 1))
            self.mounts.shell.check_deadline()
        if skipped:
            self.warnings.append(_msg("skipped", dir=self.path, count=str(skipped)))
        return out

    # ----- the files in the machine -----

    def build(self, entries: dict[str, MountEntry]) -> None:
        """Replaces the mount point's tree with placeholders for `entries` (source path -> entry, parents first)."""
        fs, shell = self.fs, self.mounts.shell
        ro = self.mode == "read-only"
        owner = "root" if ro else shell.user
        fmode, dmode = (0o444, 0o555) if ro else (0o644, 0o755)
        with fs.as_root():
            if fs.exists(self.path, follow=False):
                fs.delete(self.path, recursive=True)
            fs.mkdir(self.path, parents=True)
        root = fs._find(self.path)
        root.owner, root.mode = owner, dmode
        self.base = {"/": _Base(MountEntry("", True), root)}
        self.fetched, self.misses, self.failed = {}, {}, {}
        for p, e in entries.items():
            if p == "/":
                continue
            parent = self.base.get(get_parent(p))
            if parent is None or not parent.entry.is_dir:
                continue
            when = e.modified or utc_now()
            if e.is_dir:
                node = _Dir(modified=when, owner=owner, mode=dmode)
            else:
                node = _File(modified=when, owner=owner, mode=fmode, reported_size=e.size, loader=self.miss)
            parent.node.children[p.rsplit("/", 1)[1]] = node
            self.base[p] = _Base(e, node)
        self.pending, self.error = None, None

    def unavailable(self, why: str | None) -> None:
        """An empty mount point the agent can't open; the changes are kept for a later mount."""
        fs = self.fs
        with fs.as_root():
            if fs.exists(self.path, follow=False):
                fs.delete(self.path, recursive=True)
            fs.mkdir(self.path, parents=True)
            fs.chmod(self.path, 0)
        self.base, self.error = {}, why

    def replay(self, changes: list[MountChange]) -> None:
        """Makes `changes` again on a freshly built tree, remembering the versions they were made on."""
        fs = self.fs
        self.expected, self.absent = {}, set()
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
                    self.warnings.append(f"{path}: {e}")
                if c.kind == "write" and c.version is None:
                    self.absent.add(c.path)
                elif c.kind != "mkdir":
                    self.expected[c.path] = c.version

    def miss(self, node, path: str) -> None:
        """A placeholder was read before it was fetched: it is fetched after the command."""
        why = self.failed.get(id(node))
        if why is None:
            self.misses[id(node)] = node
            why = _msg("notFetched")
        raise ShellError(f"{path}: {why}")

    def source_path(self, node) -> str | None:
        for p, b in self.base.items():
            if b.node is node:
                return p
        return None

    async def fetch(self, nodes: list) -> None:
        """Fetches placeholders, making room by evicting clean fetched files; failures are kept for miss()."""
        fs = self.fs
        where = {id(b.node): (p, b) for p, b in self.base.items() if b.node is not None}
        for node in nodes:
            if node.loader is None or id(node) not in where:
                continue
            p, b = where[id(node)]
            room = fs.capacity - fs.used_bytes
            if b.entry.size > room:
                room += self.evict(b.entry.size - room)
            try:
                if b.entry.size > room:
                    raise ShellError(_msg("tooLarge"))
                data = await self.call("read", p, room)
                if not isinstance(data, (bytes, bytearray, memoryview)):
                    raise ShellError("the source returned something other than bytes")
                data = bytes(data)
                if len(data) > room:
                    raise ShellError(_msg("tooLarge"))
            except (asyncio.CancelledError, KeyboardInterrupt):
                raise
            except Exception as e:
                self.failed[id(node)] = str(e) or type(e).__name__
                continue
            if node.loader is None:  # written while we waited
                continue
            _fill(fs, node, data)
            b.digest, b.clock = hashlib.sha256(data).digest(), node.changed
            self.failed.pop(id(node), None)
            self.fetched[id(node)] = b

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
            n.reported_size, n.loader = b.entry.size, self.miss
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
        if self.pending is not None:
            return list(self.pending)
        root = self.fs._find(self.path, follow=False)
        if not isinstance(root, _Dir) or not self.base:
            return []
        in_tree = _ids(root)
        at = {id(b.node): p for p, b in self.base.items() if p != "/"}
        gone = sorted((p for p, b in self.base.items() if p != "/" and id(b.node) not in in_tree), reverse=True)
        dropped: set[str] = set()
        visited: set[int] = set()
        first, renames, mkdirs, writes = [], [], [], []

        def write_version(p: str) -> str | None:
            return None if p in self.absent else self.old_version(p)

        def drop(p: str) -> None:
            """p is gone and something else stands there now: delete it, and what was below it, first."""
            for q in gone:
                if (q == p or q.startswith(p + "/")) and q not in dropped:
                    dropped.add(q)
                    first.append(MountChange("delete", q, version=self.old_version(q)))

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
                old = self.base.get(p)
                replaced = old is not None and id(old.node) not in in_tree
                if bp is not None:
                    if bp != (_join(was, name) if was is not None else None):
                        if replaced:
                            drop(p)
                        renames.append(MountChange("rename", bp, to=p, version=self.old_version(bp)))
                    b = self.base[bp]
                    if isinstance(n, _Dir):
                        walk(n, p, bp)
                    elif n.loader is None and n.changed != b.clock and _digest(n) != b.digest:
                        writes.append(MountChange("write", p, version=write_version(bp), content=n.raw()))
                    continue
                if replaced and isinstance(n, _File) == (not old.entry.is_dir):
                    dropped.add(p)  # a file replaced by a file is a write; a directory made again is the same one
                    if isinstance(n, _Dir):
                        walk(n, p, p)
                    elif n.loader is None:
                        writes.append(MountChange("write", p, version=write_version(p), content=n.raw()))
                    continue
                if replaced:
                    drop(p)
                if isinstance(n, _Dir):
                    mkdirs.append(MountChange("mkdir", p))
                    walk(n, p, None)
                elif n.loader is None:  # shortcut: a placeholder moved in from another mount is left out
                    version = None if p in self.base or p in self.absent else self.expected.get(p)
                    writes.append(MountChange("write", p, version=version, content=n.raw()))

        walk(root, "/", "/")
        deletes = [MountChange("delete", p, version=self.old_version(p)) for p in gone if p not in dropped]
        return first + renames + mkdirs + writes + deletes

    async def apply(self, changes: list[MountChange] | None) -> MountApplyResult:
        current = self.changes()
        todo = current if changes is None else [c for c in changes if c in current]
        if self.pending is not None or self.error is not None:
            return MountApplyResult([], todo[0] if todo else None, self.error or "the mount is not listed yet", False)
        caps = frozenset(getattr(self.source, "capabilities", ()) or ())
        applied = []
        for c in todo:
            version = None
            try:
                if self.mode == "read-only":
                    raise ShellError("the mount is read-only")
                if c.kind not in caps:
                    raise ShellError(f"the source cannot {c.kind}")
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
                    await self.call("rename", c.path, c.version, to=c.to)
                else:
                    await self.call("mkdir", c.path)
            except MountConflictError as e:
                return MountApplyResult(applied, c, str(e) or "conflict", True)
            except (asyncio.CancelledError, KeyboardInterrupt):
                raise
            except Exception as e:
                return MountApplyResult(applied, c, str(e) or type(e).__name__, False)
            self.applied(c, version)
            applied.append(c)
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
            return
        node = self.fs._find(self.machine(c.path), follow=False)
        if c.kind == "mkdir":
            self.base[c.path] = _Base(MountEntry(c.path.rsplit("/", 1)[1], True, modified=utc_now()), node)
            return
        data = c.content or b""
        b = self.base[c.path] = _Base(MountEntry(c.path.rsplit("/", 1)[1], False, len(data), utc_now(), str(version or "")), node,
                                      hashlib.sha256(data).digest())
        if isinstance(node, _File):
            b.clock = node.changed if _digest(node) == b.digest else None


class Mounts:
    """A shell's mounts (Shell.mount and friends call these), the sources it knows by (type, id), and, while recording,
    the source exchanges of the current call."""

    def __init__(self, shell):
        self.shell = shell
        self.items: dict[str, _Mount] = {}
        self.sources: dict[tuple[str, str], object] = {}  # every source mounted, for restore and fork
        self.replay: list[MountExchange] | None = None  # a replay's recorded exchanges: answered instead of any source
        self._log: list[MountExchange] = []

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

    def source_for(self, type_: str, id_: str, path: str):
        if self.replay is not None:
            return _ReplaySource(self.replay, path, type_, id_)
        return self.sources.get((type_, id_))

    # ----- the host's API -----

    async def mount(self, path: str, source, mode: str = "read-only") -> None:
        if mode not in MODES:
            raise ValueError(f"{mode}: the mode is one of {', '.join(MODES)}")
        path = normalize("/", path)
        if path == "/" or not path.startswith("/"):
            raise ValueError(f"{path}: a mount point is an absolute path other than /")
        if mode != "read-only" and "write" not in (getattr(source, "capabilities", ()) or ()):
            raise ValueError(f"{path}: the source can't write, so it can only be mounted read-only")
        old = self.items.get(path)
        changes: list[MountChange] = []
        if old is not None:
            if old.error is None or (old.type, old.id) != (str(getattr(source, "type", "")), str(getattr(source, "id", ""))):
                raise ValueError(f"{path}: already a mount point; unmount it first")
            changes = old.changes()
        else:
            if any(path.startswith(p + "/") or p.startswith(path + "/") for p in self.items):
                raise ValueError(f"{path}: inside another mount, or holds one")
            fs = self.shell.fs
            if fs.exists(path, follow=False) and not (fs.is_dir(path, follow=False) and not fs.listdir(path)):
                raise ValueError(f"{path}: exists and is not an empty directory")
        m = _Mount(self, path, source, mode)
        entries = await m.list_tree()
        self.items[path] = m
        self.sources[(m.type, m.id)] = source
        m.build(entries)
        m.replay(changes)

    def unmount(self, path: str) -> None:
        m = self.get(path)
        del self.items[m.path]
        with self.shell.fs.as_root():
            if self.shell.fs.exists(m.path, follow=False):
                self.shell.fs.delete(m.path, recursive=True)
            self.shell.fs.mkdir(m.path)

    async def refresh(self, path: str) -> None:
        m = self.get(path)
        if m.source is None:
            raise ShellError(m.error or "unavailable")
        changes = m.changes()
        entries = await m.list_tree()
        m.build(entries)
        m.replay(changes)

    def discard(self, path: str) -> None:
        m = self.get(path)
        if m.pending is not None:
            m.pending = []
            return
        m.build({p: b.entry for p, b in m.base.items()})
        m.expected, m.absent = {}, set()

    # ----- around commands -----

    async def prepare(self) -> None:
        """At the start of a call: lists the mounts a restore brought back."""
        for m in list(self.items.values()):
            if m.pending is not None and m.source is not None:
                changes = m.pending
                try:
                    entries = await m.list_tree()
                except (asyncio.CancelledError, KeyboardInterrupt):
                    raise
                except Exception as e:
                    m.error = str(e) or type(e).__name__
                    continue
                m.build(entries)
                m.replay(changes)

    async def before(self, name: str, argv: list[str], redirect: str | None = None) -> None:
        """Fetches what a command is about to read: the paths it names (a directory: everything below), or the working directory."""
        if name in NO_CONTENT and redirect is None:
            return
        shell = self.shell
        paths = []
        for w in ([redirect] if redirect is not None else argv):
            for part in ((w, w.split("=", 1)[1]) if w.startswith("-") and "=" in w else (w,)):
                if part:
                    p = shell.resolve(part)
                    paths.append(shell.fs.real_path(p)[0] or p)
        found = [(m, p) for p in paths if (m := self.find(p)) is not None]
        if not found and redirect is None:
            cwd = shell.fs.real_path(shell._cwd)[0] or shell._cwd
            found = [(m, cwd)] if (m := self.find(cwd)) is not None else []
        for m, p in found:
            if m.error is None and m.pending is None and shell.fs.exists(p, follow=False):
                await m.fetch(m.stubs_under(p))

    async def after(self, err) -> None:
        """After a command: fetches what it missed, and in live mode applies its changes."""
        for m in list(self.items.values()):
            if m.misses:
                nodes, m.misses = list(m.misses.values()), {}
                await m.fetch(nodes)
            if m.mode == "live" and m.error is None and m.pending is None:
                changes = m.changes()
                if not changes or changes == m.stuck:
                    continue  # nothing new since the last attempt failed
                r = await m.apply(None)
                m.stuck = m.changes() if r.failed is not None else None
                if r.failed is not None:
                    where = m.machine(r.failed.path)
                    err.write("mount: " + (_msg("conflict", path=where) if r.conflict else _msg("failed", path=where, reason=r.error)) + "\n")

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
        return [{"path": m.path, "mode": m.mode, "type": m.type, "id": m.id, "changes": m.changes()} for m in self.items.values()]

    def restore(self, saved: list[dict]) -> None:
        """After the files were replaced: the snapshot's mounts, listed at the start of the next call."""
        self.items = {}
        for d in saved:
            source = self.source_for(d["type"], d["id"], d["path"])
            m = _Mount(self, d["path"], source, d["mode"])
            m.type, m.id = d["type"], d["id"]
            self.items[m.path] = m
            m.unavailable(None if source is not None else _msg("unavailable", source=f"{m.type}:{m.id}"))
            m.pending = list(d["changes"])

    def start_exchanges(self) -> list[MountExchange]:
        """What the mounts hold when a recording starts, as the exchanges a replay's first listing and reads answer from."""
        out = []
        for m in self.items.values():
            if m.pending is not None or m.error is not None:
                continue
            kids: dict[str, list] = {p: [] for p, b in m.base.items() if b.entry.is_dir}
            for p, b in m.base.items():
                if p != "/" and get_parent(p) in kids:
                    kids[get_parent(p)].append(b.entry)
            for p, b in m.base.items():
                if b.entry.is_dir:
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
            raise ShellError(_msg("replay", op=op, path=path))
        x = q.pop(0) if len(q) > 1 else q[0]  # the last answer repeats
        if x.conflict:
            raise MountConflictError(x.error)
        if x.error is not None:
            raise ShellError(x.error)
        return x

    def list(self, path):
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
        self._answer("rename", path, to)

    def mkdir(self, path):
        self._answer("mkdir", path)


# ---------- helpers ----------

def _valid(e) -> bool:
    """An entry a source listed that can be a file: a plain name, sane size and version."""
    if not isinstance(e, MountEntry) or not isinstance(e.name, str) or not isinstance(e.version, str):
        return False
    n = e.name
    return (n not in ("", ".", "..") and "/" not in n and "\0" not in n and len(n.encode("utf-8", "surrogatepass")) <= MAX_NAME_BYTES
            and isinstance(e.size, int) and e.size >= 0 and len(e.version) <= MAX_VERSION
            and (e.modified is None or isinstance(e.modified, datetime)))


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
