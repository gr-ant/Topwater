"""In-memory Unix-style filesystem with owners and permission bits."""
from __future__ import annotations

import posixpath
from contextlib import contextmanager
from dataclasses import dataclass, field
from datetime import datetime


class ShellError(Exception):
    """Raised by commands and the filesystem; becomes "cmd: message" on stderr with the given exit code."""

    def __init__(self, message: str, exit_code: int = 1):
        super().__init__(message)
        self.exit_code = exit_code


@dataclass(frozen=True)
class FileStat:
    is_dir: bool
    size: int
    modified: datetime
    mode: int  # Unix permission bits, e.g. 0o755
    owner: str


DEFAULT_FILE_MODE = 0o644
DEFAULT_DIR_MODE = 0o755


@dataclass
class _Dir:
    children: dict = field(default_factory=dict)
    modified: datetime = field(default_factory=datetime.now)
    owner: str = "root"
    mode: int = DEFAULT_DIR_MODE


@dataclass
class _File:
    content: str = ""
    modified: datetime = field(default_factory=datetime.now)
    owner: str = "root"
    mode: int = DEFAULT_FILE_MODE
    data: bytes | None = None  # set for binary files (written with write_bytes); content is then unused

    def raw(self) -> bytes:
        return self.content.encode() if self.data is None else self.data

    def store(self, content: str | bytes, append: bool) -> None:
        if isinstance(content, bytes) or (append and self.data is not None):
            new = content if isinstance(content, bytes) else content.encode()
            self.content, self.data = "", (self.raw() + new if append else new)
        else:
            self.content, self.data = (self.content + content if append else content), None


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
        self.run_as: str | None = None  # user the agent's commands run as; None = unrestricted (your app)

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
        cur = self._root
        for seg in normalize("/", path).split("/"):
            if not seg:
                continue
            if not isinstance(cur, _Dir) or seg not in cur.children:
                return None
            cur = cur.children[seg]
        return cur

    def _parent_dir(self, path: str) -> _Dir:
        node = self._find(get_parent(path))
        if node is None:
            raise ShellError(f"{path}: No such file or directory")
        if not isinstance(node, _Dir):
            raise ShellError(f"{path}: Not a directory")
        return node

    def _allowed(self, n, bit: int) -> bool:
        return not self._restricted or bool((n.mode >> (6 if n.owner == self.run_as else 0)) & bit)

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
            return FileStat(True, 4096, n.modified, n.mode, n.owner)
        if isinstance(n, _File):
            return FileStat(False, len(n.raw()), n.modified, n.mode, n.owner)
        raise ShellError(f"{path}: No such file or directory")

    def read_text(self, path: str) -> str:
        n = self._read(path)
        return n.content if n.data is None else n.data.decode("utf-8", "replace")

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
        raise ShellError(f"{path}: No such file or directory")

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
            existing.store(content, append)
            existing.modified = datetime.now()
        else:
            self._require(d, 2, path)
            d.children[name] = f = _File(owner=self.run_as or d.owner)
            f.store(content, False)
        d.modified = datetime.now()

    def touch(self, path: str) -> None:
        n = self._find(path)
        if n is not None:
            self._require(n, 2, path)
            n.modified = datetime.now()
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
        d.modified = datetime.now()

    def listdir(self, path: str) -> list[str]:
        n = self._find(path)
        if isinstance(n, _Dir):
            self._require(n, 4, path)
            return sorted(n.children)
        if isinstance(n, _File):
            raise ShellError(f"{path}: Not a directory")
        raise ShellError(f"{path}: No such file or directory")

    def delete(self, path: str, recursive: bool = False) -> None:
        path = normalize("/", path)
        if path == "/":
            raise ShellError("it is dangerous to operate recursively on '/'")
        n = self._find(path)
        if n is None:
            raise ShellError(f"{path}: No such file or directory")
        if isinstance(n, _Dir) and n.children and not recursive:
            raise ShellError(f"{path}: Directory not empty")
        parent = self._parent_dir(path)
        self._require(parent, 2, path)
        if isinstance(n, _Dir):
            self._require_tree_writable(n, path)
        del parent.children[get_name(path)]

    def _require_tree_writable(self, d: _Dir, path: str) -> None:
        if not d.children:
            return
        self._require(d, 2, path)
        for name, child in d.children.items():
            if isinstance(child, _Dir):
                self._require_tree_writable(child, path + "/" + name)

    def copy(self, source: str, dest: str) -> None:
        n = self._find(source)
        if n is None:
            raise ShellError(f"{source}: No such file or directory")
        self._require(n, 4, source)
        dest = normalize("/", dest)
        if isinstance(n, _Dir) and (dest + "/").startswith(normalize("/", source) + "/"):
            raise ShellError(f"cannot copy a directory, '{source}', into itself, '{dest}'")
        target = self._find(dest)
        if isinstance(target, _Dir) and isinstance(n, _File):
            raise ShellError(f"{dest}: Is a directory")
        parent = self._parent_dir(dest)
        self._require(target if target is not None else parent, 2, dest)
        parent.children[get_name(dest)] = _clone(n, self.run_as or parent.owner)

    def move(self, source: str, dest: str) -> None:
        source, dest = normalize("/", source), normalize("/", dest)
        n = self._find(source)
        if n is None:
            raise ShellError(f"{source}: No such file or directory")
        if (dest + "/").startswith(source + "/"):
            raise ShellError(f"cannot move '{source}' to a subdirectory of itself, '{dest}'")
        if isinstance(self._find(dest), _Dir):
            raise ShellError(f"{dest}: Is a directory")
        target, origin = self._parent_dir(dest), self._parent_dir(source)
        self._require(origin, 2, source)
        self._require(target, 2, dest)
        del origin.children[get_name(source)]
        target.children[get_name(dest)] = n

    def chmod(self, path: str, mode: int) -> None:
        """Changes permission bits. While restricted, only the owner may do this."""
        n = self._find(path)
        if n is None:
            raise ShellError(f"cannot access '{path}': No such file or directory")
        if self._restricted and n.owner != self.run_as:
            raise ShellError(f"changing permissions of '{path}': Operation not permitted")
        n.mode = mode & 0o7777

    def set_owner(self, path: str, owner: str, recursive: bool = False) -> None:
        """Changes the owner (and, with recursive, everything below). Unrestricted callers only."""
        n = self._find(path)
        if n is None:
            raise ShellError(f"cannot access '{path}': No such file or directory")
        if self._restricted:
            raise ShellError(f"changing ownership of '{path}': Operation not permitted")

        def apply(node):
            node.owner = owner
            if recursive and isinstance(node, _Dir):
                for c in node.children.values():
                    apply(c)

        apply(n)


def _clone(n, owner: str):
    if isinstance(n, _File):
        return _File(n.content, owner=owner, mode=n.mode, data=n.data)
    return _Dir({k: _clone(v, owner) for k, v in n.children.items()}, owner=owner, mode=n.mode)
