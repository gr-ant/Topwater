"""In-memory Unix-style filesystem."""
from __future__ import annotations

import posixpath
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


@dataclass
class _Dir:
    children: dict = field(default_factory=dict)
    modified: datetime = field(default_factory=datetime.now)


@dataclass
class _File:
    content: str = ""
    modified: datetime = field(default_factory=datetime.now)


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


class VirtualFileSystem:
    """In-memory filesystem. Apps can "mount" data for the agent with fs.write_text("/data/orders.json", json).

    shortcut: text-only files and no locking; add bytes content / a lock if binary data or threaded shells are needed.
    """

    def __init__(self):
        self._root = _Dir()

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

    def exists(self, path: str) -> bool:
        return self._find(path) is not None

    def is_dir(self, path: str) -> bool:
        return isinstance(self._find(path), _Dir)

    def is_file(self, path: str) -> bool:
        return isinstance(self._find(path), _File)

    def stat(self, path: str) -> FileStat:
        n = self._find(path)
        if isinstance(n, _Dir):
            return FileStat(True, 4096, n.modified)
        if isinstance(n, _File):
            return FileStat(False, len(n.content.encode()), n.modified)
        raise ShellError(f"{path}: No such file or directory")

    def read_text(self, path: str) -> str:
        n = self._find(path)
        if isinstance(n, _File):
            return n.content
        if isinstance(n, _Dir):
            raise ShellError(f"{path}: Is a directory")
        raise ShellError(f"{path}: No such file or directory")

    def write_text(self, path: str, content: str) -> None:
        self._write(path, content, append=False)

    def append_text(self, path: str, content: str) -> None:
        self._write(path, content, append=True)

    def _write(self, path: str, content: str, append: bool) -> None:
        path = normalize("/", path)
        if path == "/dev/null" and self._find(path) is not None:
            return
        d = self._parent_dir(path)
        name = get_name(path)
        existing = d.children.get(name)
        if existing is not None:
            if not isinstance(existing, _File):
                raise ShellError(f"{path}: Is a directory")
            existing.content = existing.content + content if append else content
            existing.modified = datetime.now()
        else:
            d.children[name] = _File(content)
        d.modified = datetime.now()

    def touch(self, path: str) -> None:
        n = self._find(path)
        if n is not None:
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
        d.children[get_name(path)] = _Dir()
        d.modified = datetime.now()

    def listdir(self, path: str) -> list[str]:
        n = self._find(path)
        if isinstance(n, _Dir):
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
        del self._parent_dir(path).children[get_name(path)]

    def copy(self, source: str, dest: str) -> None:
        n = self._find(source)
        if n is None:
            raise ShellError(f"{source}: No such file or directory")
        dest = normalize("/", dest)
        if isinstance(n, _Dir) and (dest + "/").startswith(normalize("/", source) + "/"):
            raise ShellError(f"cannot copy a directory, '{source}', into itself, '{dest}'")
        if isinstance(self._find(dest), _Dir) and isinstance(n, _File):
            raise ShellError(f"{dest}: Is a directory")
        self._parent_dir(dest).children[get_name(dest)] = _clone(n)

    def move(self, source: str, dest: str) -> None:
        source, dest = normalize("/", source), normalize("/", dest)
        n = self._find(source)
        if n is None:
            raise ShellError(f"{source}: No such file or directory")
        if (dest + "/").startswith(source + "/"):
            raise ShellError(f"cannot move '{source}' to a subdirectory of itself, '{dest}'")
        if isinstance(self._find(dest), _Dir):
            raise ShellError(f"{dest}: Is a directory")
        target = self._parent_dir(dest)
        del self._parent_dir(source).children[get_name(source)]
        target.children[get_name(dest)] = n


def _clone(n):
    if isinstance(n, _File):
        return _File(n.content)
    return _Dir({k: _clone(v) for k, v in n.children.items()})
