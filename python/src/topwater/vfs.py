"""In-memory Unix-style filesystem with owners and permission bits."""
from __future__ import annotations

import posixpath
from contextlib import contextmanager
from contextvars import ContextVar
from dataclasses import dataclass, field
from datetime import datetime, timezone

from . import bytetext


class ShellError(Exception):
    """Raised by commands and the filesystem; becomes "cmd: message" on stderr with the given exit code."""

    def __init__(self, message: str, exit_code: int = 1):
        super().__init__(message)
        self.exit_code = exit_code


class UsageException(ShellError):
    """A bad command line: printed with GNU's "Try 'cmd --help'" hint; exits 1, or 2 for ls, grep, sort and diff."""

    def __init__(self, message: str):
        super().__init__(message, 1)


@dataclass(frozen=True)
class FileStat:
    is_dir: bool
    size: int
    modified: datetime
    mode: int  # Unix permission bits, e.g. 0o755
    owner: str
    links: int = 1  # hard links: 1 for a file; 2 plus one per subdirectory for a directory, as on ext4

    @property
    def blocks(self) -> int:
        """512-byte blocks in use, as `stat %b` and `ls -s` count them: whole 4 KiB filesystem blocks."""
        return (self.size + 4095) // 4096 * 8


NO_SPACE = "No space left on device"


def utc_now() -> datetime:
    """The simulated machine's one clock: UTC (naive), for file times, date, uptime and find -mmin alike."""
    return datetime.now(timezone.utc).replace(tzinfo=None)


DEFAULT_FILE_MODE = 0o644
DEFAULT_DIR_MODE = 0o755


@dataclass
class _Dir:
    children: dict = field(default_factory=dict)
    modified: datetime = field(default_factory=utc_now)
    owner: str = "root"
    mode: int = DEFAULT_DIR_MODE


@dataclass
class _File:
    content: str = ""
    modified: datetime = field(default_factory=utc_now)
    owner: str = "root"
    mode: int = DEFAULT_FILE_MODE
    data: bytes | None = None  # set for binary files (written with write_bytes); content is then unused
    appended: list[str] = field(default_factory=list)  # `>>` chunks not yet joined, so append loops stay linear
    size: int = 0  # real bytes held, as counted against the filesystem's capacity
    reported_size: int | None = None  # what `ls -l` and `stat` show for a program stub, whose real content is a few bytes

    def text(self) -> str:
        if self.appended:
            self.content, self.appended = self.content + "".join(self.appended), []
        return self.content

    def raw(self) -> bytes:
        return self.text().encode() if self.data is None else self.data

    def store(self, content: str | bytes, append: bool, reserve=None) -> None:
        """reserve(delta) is called with the change in size before anything changes; it raises to refuse the write."""
        if isinstance(content, str) and bytetext.has_escapes(content):
            content = bytetext.encode(content)  # binary that came through a pipe
        if isinstance(content, bytes) or (append and self.data is not None):
            new = content if isinstance(content, bytes) else content.encode()
            size = self.size + len(new) if append else len(new)
            if reserve:
                reserve(size - self.size)
            self.data = self.raw() + new if append else new
            self.content, self.appended = "", []
        else:
            size = (self.size if append else 0) + len(content.encode("utf-8", "surrogatepass"))
            if reserve:
                reserve(size - self.size)
            if append:
                self.appended.append(content)
            else:
                self.content, self.data, self.appended = content, None, []
        self.size = size


def _tree_size(n) -> int:
    return n.size if isinstance(n, _File) else sum(_tree_size(c) for c in n.children.values())


def normalize(cwd: str, path: str) -> str:
    """Resolves path against cwd and collapses '.' and '..'."""
    full = path if path.startswith("/") else cwd.rstrip("/") + "/" + path
    stack: list[str] = []
    for seg in full.split("/"):
        if seg in ("", "."):
            continue
        if seg == "..":
            if stack:
                stack.pop()
            continue
        stack.append(seg)
    return "/" + "/".join(stack)


def get_name(path: str) -> str:
    p = normalize("/", path)
    return "/" if p == "/" else posixpath.basename(p)


def get_parent(path: str) -> str:
    return posixpath.dirname(normalize("/", path)) or "/"


def format_mode(is_dir: bool, mode: int) -> str:
    """'drwxr-xr-x' style rendering of a mode."""
    chars = ["d" if is_dir else "-"] + [c if mode & (1 << (8 - i)) else "-" for i, c in enumerate("rwxrwxrwx")]
    if mode & 0o4000:  # setuid
        chars[3] = "s" if chars[3] == "x" else "S"
    if mode & 0o2000:  # setgid
        chars[6] = "s" if chars[6] == "x" else "S"
    if mode & 0o1000:  # sticky bit
        chars[9] = "t" if chars[9] == "x" else "T"
    return "".join(chars)


class VirtualFileSystem:
    """In-memory filesystem with owners and permission bits.

    Calls from your app are unrestricted (root). While the shell runs the agent's commands it sets ``run_as``,
    so the agent is an ordinary user: it can't write to root-owned places such as /etc or /usr/bin.
    Apps can "mount" data for the agent with fs.write_text("/data/orders.json", json); new files belong to the
    owner of the directory they're created in.

    Binary files (write_bytes / read_bytes / add_document) keep their exact bytes; the text API reads them as lossy UTF-8.
    shortcut: no groups, no locking; add them if group permissions or threaded shells are needed.
    """

    def __init__(self):
        self._root = _Dir()
        self.capacity = 1 << 62  # most bytes the files may hold; the agent's writes past it fail (the Shell sets it)
        self.used_bytes = 0  # bytes all files hold now (what `df` reports as used)
        # context-local, so your app's own calls stay unrestricted while an agent command is awaiting
        self._run_as: ContextVar[str | None] = ContextVar("run_as", default=None)

    @property
    def run_as(self) -> str | None:
        """User the agent's commands run as; None = unrestricted (your app). Flows with the async call that sets it."""
        return self._run_as.get()

    @run_as.setter
    def run_as(self, user: str | None) -> None:
        self._run_as.set(user)

    def _reserve(self, delta: int, path: str) -> None:
        """Counts a change in stored bytes, refusing growth past capacity for the agent; your app's writes always go through."""
        if delta > 0 and self.run_as is not None and self.used_bytes + delta > self.capacity:
            raise ShellError(f"{path}: {NO_SPACE}")
        self.used_bytes += delta

    @property
    def _restricted(self) -> bool:
        return self.run_as not in (None, "root")

    @contextmanager
    def as_root(self):
        """Temporarily lifts run_as restrictions (for shell internals)."""
        saved, self.run_as = self.run_as, None
        try:
            yield
        finally:
            self.run_as = saved

    def _find(self, path: str):
        return self._walk(path)[0]

    def _walk(self, path: str):
        """Walks `path`; like Linux, each directory passed through needs search (x) permission. Returns (node, why):
        why says why a missing node can't be reached: "Permission denied", "Not a directory" or "No such file or directory"."""
        cur = self._root
        user = self.run_as if self._restricted else None
        for seg in normalize("/", path).split("/"):
            if not seg:
                continue
            if not isinstance(cur, _Dir):
                return None, "Not a directory"
            if user is not None and not self._permits(cur, user, 1):
                return None, "Permission denied"
            if seg not in cur.children:
                return None, "No such file or directory"
            cur = cur.children[seg]
        return cur, ""

    @staticmethod
    def _permits(n, user: str | None, bits: int) -> bool:
        return user is None or ((n.mode >> (6 if n.owner == user else 0)) & bits) == bits

    def why_missing(self, path: str) -> str:
        """Why `path` can't be reached: "Permission denied", "Not a directory" or "No such file or directory"."""
        node, why = self._walk(path)
        return why if node is None else "No such file or directory"

    def _missing(self, path: str) -> ShellError:
        return ShellError(f"{path}: {self.why_missing(path)}")

    def _require_not_sticky(self, d: _Dir, entry, path: str) -> None:
        if self._restricted and d.mode & 0o1000 and entry.owner != self.run_as and d.owner != self.run_as:
            raise ShellError(f"{path}: Operation not permitted")

    def _parent_dir(self, path: str) -> _Dir:
        """The directory an entry is created in, removed from or renamed in; it needs search permission too."""
        node, why = self._walk(get_parent(path))
        if isinstance(node, _Dir):
            self._require(node, 1, path)
            return node
        if node is None:
            raise ShellError(f"{path}: {why}")
        raise ShellError(f"{path}: Not a directory")

    def _allowed(self, n, bit: int) -> bool:
        return not self._restricted or self._permits(n, self.run_as, bit)

    def _require(self, n, bit: int, path: str) -> None:
        if not self._allowed(n, bit):
            raise ShellError(f"{path}: Permission denied")

    def exists(self, path: str) -> bool:
        return self._find(path) is not None

    def is_dir(self, path: str) -> bool:
        return isinstance(self._find(path), _Dir)

    def is_file(self, path: str) -> bool:
        return isinstance(self._find(path), _File)

    def can_read(self, path: str) -> bool:
        n = self._find(path)
        return n is not None and self._allowed(n, 4)

    def can_write(self, path: str) -> bool:
        n = self._find(path)
        return n is not None and self._allowed(n, 2)

    def can_execute(self, path: str) -> bool:
        n = self._find(path)
        return n is not None and self._allowed(n, 1)

    def stat(self, path: str) -> FileStat:
        n = self._find(path)
        if isinstance(n, _Dir):
            return FileStat(True, 4096, n.modified, n.mode, n.owner, 2 + sum(isinstance(c, _Dir) for c in n.children.values()))
        if isinstance(n, _File):
            return FileStat(False, n.reported_size if n.reported_size is not None else len(n.raw()), n.modified, n.mode, n.owner)
        raise self._missing(path)

    def read_text(self, path: str) -> str:
        n = self._read(path)
        return n.text() if n.data is None else n.data.decode("utf-8", "replace")

    def read_pipe_text(self, path: str) -> str:
        """What `cat` and `<` put on a pipe: like read_text, but a binary file's bytes survive (see bytetext)."""
        n = self._read(path)
        return n.text() if n.data is None else bytetext.decode(n.data)

    def read_bytes(self, path: str) -> bytes:
        """The file's exact bytes (UTF-8 for files written as text)."""
        return self._read(path).raw()

    def _read(self, path: str) -> _File:
        n = self._find(path)
        if isinstance(n, _File):
            self._require(n, 4, path)
            return n
        if isinstance(n, _Dir):
            raise ShellError(f"{path}: Is a directory")
        raise self._missing(path)

    def write_bytes(self, path: str, data: bytes) -> None:
        self._write(path, bytes(data), append=False)

    def add_document(self, path: str, source, also_markdown: bool = False) -> None:
        """Mounts a real document for the agent: source is a host path (str/PathLike) or bytes.
        Parent directories are created; with also_markdown, the converted text is written to "<path>.md" too."""
        if isinstance(source, (bytes, bytearray, memoryview)):
            data = bytes(source)
        else:
            with open(source, "rb") as f:
                data = f.read()
        self.mkdir(get_parent(path), parents=True)
        self.write_bytes(path, data)
        if also_markdown:
            from .convert import to_markdown
            self.write_text(path + ".md", to_markdown(data, get_name(path)))

    def write_text(self, path: str, content: str) -> None:
        self._write(path, content, append=False)

    def append_text(self, path: str, content: str) -> None:
        self._write(path, content, append=True)

    def _write(self, path: str, content: str | bytes, append: bool) -> None:
        path = normalize("/", path)
        if path == "/dev/null" and self._find(path) is not None:
            return
        d = self._parent_dir(path)
        name = get_name(path)
        existing = d.children.get(name)
        if existing is not None:
            if not isinstance(existing, _File):
                raise ShellError(f"{path}: Is a directory")
            self._require(existing, 2, path)
            existing.store(content, append, lambda delta: self._reserve(delta, path))
            existing.modified = utc_now()
        else:
            self._require(d, 2, path)
            f = _File(owner=self.run_as or d.owner)
            f.store(content, False, lambda delta: self._reserve(delta, path))
            d.children[name] = f
        d.modified = utc_now()

    def set_modified(self, path: str, when: datetime, recursive: bool = False) -> None:
        """Back-dates a path (and optionally everything under it), so system files don't all look brand new."""
        n = self._find(path)

        def walk(node):
            node.modified = when
            if recursive and isinstance(node, _Dir):
                for c in node.children.values():
                    walk(c)

        if n is not None:
            walk(n)

    def set_reported_size(self, path: str, size: int) -> None:
        n = self._find(path)
        if isinstance(n, _File):
            n.reported_size = size

    def set_modified_time(self, path: str, when: datetime) -> None:
        """Sets a modification time (tar and unzip restoring one); as with utime(2), only the owner may."""
        n = self._find(path)
        if n is None:
            raise self._missing(path)
        if self._restricted and n.owner != self.run_as:
            raise ShellError(f"{path}: Operation not permitted")
        n.modified = when

    def touch(self, path: str) -> None:
        n, why = self._walk(path)
        if n is not None:
            self._require(n, 2, path)
            n.modified = utc_now()
        elif why == "Permission denied":
            raise ShellError(f"{path}: Permission denied")
        else:
            self.write_text(path, "")

    def mkdir(self, path: str, parents: bool = False) -> None:
        path = normalize("/", path)
        existing = self._find(path)
        if existing is not None:
            if parents and isinstance(existing, _Dir):
                return
            raise ShellError(f"cannot create directory '{path}': File exists")
        if parents and not self.exists(get_parent(path)):
            self.mkdir(get_parent(path), True)
        d = self._parent_dir(path)
        self._require(d, 2, path)
        d.children[get_name(path)] = _Dir(owner=self.run_as or d.owner)
        d.modified = utc_now()

    def listdir(self, path: str) -> list[str]:
        n = self._find(path)
        if isinstance(n, _Dir):
            self._require(n, 4, path)
            return sorted(n.children)
        if isinstance(n, _File):
            raise ShellError(f"{path}: Not a directory")
        raise self._missing(path)

    def delete(self, path: str, recursive: bool = False) -> None:
        path = normalize("/", path)
        if path == "/":
            raise ShellError("it is dangerous to operate recursively on '/'")
        n = self._find(path)
        if n is None:
            raise self._missing(path)
        if isinstance(n, _Dir) and n.children and not recursive:
            raise ShellError(f"{path}: Directory not empty")
        parent = self._parent_dir(path)
        if isinstance(n, _Dir):
            self._require_tree_removable(n, path)
        self._require(parent, 2, path)
        self._require_not_sticky(parent, n, path)
        del parent.children[get_name(path)]
        self.used_bytes -= _tree_size(n)

    # shortcut: all or nothing, naming the first entry that can't go; real rm -r removes what it can.
    def _require_tree_removable(self, d: _Dir, path: str) -> None:
        if not d.children:
            return
        first = next(iter(d.children))
        self._require(d, 4, path)
        self._require(d, 3, f"{path}/{first}")
        for name, child in d.children.items():
            self._require_not_sticky(d, child, f"{path}/{name}")
            if isinstance(child, _Dir):
                self._require_tree_removable(child, f"{path}/{name}")

    def copy(self, source: str, dest: str, denied=None) -> None:
        """Copies a file or tree. Entries the user can't read are passed to `denied(path, is_dir)` and skipped, like
        `cp -r`; without a callback the whole copy fails."""
        source, dest = normalize("/", source), normalize("/", dest)
        n = self._find(source)
        if n is None:
            raise self._missing(source)
        if isinstance(n, _Dir) and (dest + "/").startswith(source + "/"):
            raise ShellError(f"cannot copy a directory, '{source}', into itself, '{dest}'")
        target = self._find(dest)
        if isinstance(target, _Dir) and isinstance(n, _File):
            raise ShellError(f"{dest}: Is a directory")
        parent = self._parent_dir(dest)
        self._require(target if target is not None else parent, 2, dest)
        user = self.run_as if self._restricted else None
        owner = self.run_as or parent.owner

        def clone(node, p: str):
            if not self._permits(node, user, 5 if isinstance(node, _Dir) else 4):
                if denied is None:
                    raise ShellError(f"{p}: Permission denied")
                denied(p, isinstance(node, _Dir))
                return None
            if isinstance(node, _File):
                return _File(node.text(), owner=owner, mode=node.mode, data=node.data, size=node.size)
            c = _Dir(owner=owner, mode=node.mode)
            for k, v in node.children.items():
                if (cc := clone(v, f"{p}/{k}")) is not None:
                    c.children[k] = cc
            return c

        copied = clone(n, source)
        if copied is None:
            return
        if isinstance(target, _File) and isinstance(copied, _File):  # onto an existing file: write into it, keeping owner and mode
            target.store(copied.raw(), False, lambda delta: self._reserve(delta, dest))
            target.modified = utc_now()
        else:
            self._reserve(_tree_size(copied) - (0 if target is None else _tree_size(target)), dest)
            parent.children[get_name(dest)] = copied

    def move(self, source: str, dest: str) -> None:
        source, dest = normalize("/", source), normalize("/", dest)
        n = self._find(source)
        if n is None:
            raise self._missing(source)
        if (dest + "/").startswith(source + "/"):
            raise ShellError(f"cannot move '{source}' to a subdirectory of itself, '{dest}'")
        existing = self._find(dest)
        if isinstance(existing, _Dir):
            raise ShellError(f"{dest}: Is a directory")
        target, origin = self._parent_dir(dest), self._parent_dir(source)
        self._require(origin, 2, source)
        self._require(target, 2, dest)
        if isinstance(n, _Dir) and origin is not target:
            self._require(n, 2, source)  # its ".." entry changes
        self._require_not_sticky(origin, n, source)
        if existing is not None:
            self._require_not_sticky(target, existing, dest)
        del origin.children[get_name(source)]
        if existing is not None:
            self.used_bytes -= _tree_size(existing)
        target.children[get_name(dest)] = n

    def chmod(self, path: str, mode: int) -> None:
        """Changes permission bits. While restricted, only the owner may do this."""
        n = self._find(path)
        if n is None:
            raise ShellError(f"cannot access '{path}': {self.why_missing(path)}")
        if self._restricted and n.owner != self.run_as:
            raise ShellError(f"changing permissions of '{path}': Operation not permitted")
        n.mode = mode & 0o7777

    def set_owner(self, path: str, owner: str, recursive: bool = False) -> None:
        """Changes the owner (and, with recursive, everything below). Unrestricted callers only."""
        n = self._find(path)
        if n is None:
            raise ShellError(f"cannot access '{path}': {self.why_missing(path)}")
        if self._restricted:
            raise ShellError(f"changing ownership of '{path}': Operation not permitted")

        def apply(node):
            node.owner = owner
            if recursive and isinstance(node, _Dir):
                for c in node.children.values():
                    apply(c)

        apply(n)

    # ---------- snapshots (see snapshot.py) ----------

    def entries(self):
        """Every path, parents first: (path, is_dir, mode, owner, modified, text, data, reported_size). Contents are immutable str/bytes."""
        stack = [("/", self._root)]
        while stack:
            path, n = stack.pop()
            if isinstance(n, _File):
                yield path, False, n.mode, n.owner, n.modified, n.text() if n.data is None else None, n.data, n.reported_size
                continue
            yield path, True, n.mode, n.owner, n.modified, None, None, None
            for name in reversed(list(n.children)):
                stack.append(("/" + name if path == "/" else f"{path}/{name}", n.children[name]))

    def replace_all(self, entries) -> None:
        """Replaces every file with `entries` (as entries() gives them, parents first): a snapshot restore."""
        root, used = _Dir(), 0
        for path, is_dir, mode, owner, modified, text, data, reported_size in entries:
            if is_dir:
                node = root if path == "/" else _Dir()
            elif path == "/":
                raise ShellError("/: Not a directory")
            else:
                node = _File(text or "", data=data, reported_size=reported_size)
                node.size = len(data) if data is not None else len(node.content.encode("utf-8", "surrogatepass"))
                used += node.size
            node.mode, node.owner, node.modified = mode, owner, modified
            if path == "/":
                continue
            parent = root
            for seg in path.split("/")[1:-1]:
                parent = parent.children.get(seg) if isinstance(parent, _Dir) else None
            if not isinstance(parent, _Dir):
                raise ShellError(f"{path}: parent is not a directory")
            name = get_name(path)
            if name in parent.children:
                raise ShellError(f"{path}: File exists")
            parent.children[name] = node
        self._root, self.used_bytes = root, used
