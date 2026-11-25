"""In-memory Unix-style filesystem with owners and permission bits."""
from __future__ import annotations

import posixpath
from contextlib import contextmanager
from contextvars import ContextVar
from dataclasses import dataclass, field
from typing import NamedTuple
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
    links: int = 1  # hard links: 1 for a file (more after `ln`); 2 plus one per subdirectory for a directory, as on ext4
    link_target: str | None = None  # for a symbolic link (a stat that didn't follow it): the path it holds; size is that path's length in bytes

    @property
    def is_symlink(self) -> bool:
        return self.link_target is not None

    @property
    def blocks(self) -> int:
        """512-byte blocks in use, as `stat %b` and `ls -s` count them: whole 4 KiB filesystem blocks."""
        return 0 if self.is_symlink else (self.size + 4095) // 4096 * 8  # a short link lives in its inode: no blocks


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
    nlink: int = 1  # names this file has (hard links)
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


@dataclass
class _Link:
    target: str = ""
    modified: datetime = field(default_factory=utc_now)
    owner: str = "root"
    mode: int = 0o777
    nlink: int = 1


class _Spot(NamedTuple):
    """Where a path leads: the directory holding (or that would hold) its last entry, that entry's name and node (None if
    absent), its physical path, and why it can't be reached. parent is None when it can't be, and for "/"."""
    parent: _Dir | None
    name: str
    node: object
    real: str
    why: str


MAX_LINK_HOPS = 40  # symbolic links followed in one lookup before it fails with LOOP, as on Linux
LOOP = "Too many levels of symbolic links"


def _tree_size(n) -> int:
    return n.size if isinstance(n, _File) else 0 if isinstance(n, _Link) else sum(_tree_size(c) for c in n.children.values())


def _unlink(n) -> int:
    """Drops one name of each node in a tree being removed; returns the bytes freed (a file goes with its last name)."""
    if isinstance(n, _Dir):
        return sum(_unlink(c) for c in n.children.values())
    n.nlink -= 1
    return n.size if n.nlink <= 0 and isinstance(n, _File) else 0


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


def format_mode(is_dir: bool, mode: int, symlink: bool = False) -> str:
    """'drwxr-xr-x' style rendering of a mode ('l' first for a symbolic link)."""
    chars = ["l" if symlink else "d" if is_dir else "-"] + [c if mode & (1 << (8 - i)) else "-" for i, c in enumerate("rwxrwxrwx")]
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

    def _find(self, path: str, follow: bool = True):
        return self._walk(path, follow).node

    def _find_why(self, path: str, follow: bool = True):
        """(node, why): why says why a missing node can't be reached."""
        spot = self._walk(path, follow)
        return spot.node, spot.why or "No such file or directory"

    def _walk(self, path: str, follow: bool = True) -> _Spot:
        """Walks `path`; like Linux, each directory passed through needs search (x) permission, symbolic links in the middle
        are followed (a relative one from the directory holding it), and the last one only when `follow`. ".." goes up from
        where the walk really is. A loop fails after 40 links, never hangs."""
        user = self.run_as if self._restricted else None
        chain = [("", self._root)]  # the directories walked into, for ".." and the real path
        todo = path.split("/")[::-1]  # a stack: the next segment is last
        hops, on_file = 0, False  # on_file: a file was reached with more of the path to go

        def real(name: str) -> str:
            return "".join("/" + c[0] for c in chain[1:]) + "/" + name

        def fail(why: str) -> _Spot:
            return _Spot(None, "", None, "", why)

        while todo:
            seg = todo.pop()
            if on_file:
                return fail("Not a directory")
            if seg in ("", "."):
                continue
            d = chain[-1][1]
            if not self._permits(d, user, 1):
                return fail("Permission denied")
            if seg == "..":
                if len(chain) > 1:
                    chain.pop()
                continue
            final = all(t in ("", ".") for t in todo)
            bare = not todo  # no trailing slash, which would mean a directory
            nxt = d.children.get(seg)
            if nxt is None:
                return _Spot(d if bare else None, seg, None, real(seg), "No such file or directory") if final else fail("No such file or directory")
            if isinstance(nxt, _Link) and (not bare or follow):
                hops += 1
                if hops > MAX_LINK_HOPS:
                    return fail(LOOP)
                if nxt.target.startswith("/"):
                    del chain[1:]
                todo.extend(nxt.target.split("/")[::-1])
                continue
            if bare:
                return _Spot(d, seg, nxt, real(seg), "")
            if isinstance(nxt, _Dir):
                chain.append((seg, nxt))
            else:
                on_file = True
        if len(chain) == 1:
            return _Spot(None, "/", self._root, "/", "")
        return _Spot(chain[-2][1], chain[-1][0], chain[-1][1], real("")[:-1], "")

    @staticmethod
    def _permits(n, user: str | None, bits: int) -> bool:
        return user is None or ((n.mode >> (6 if n.owner == user else 0)) & bits) == bits

    def why_missing(self, path: str, follow: bool = True) -> str:
        """Why `path` can't be reached: "Permission denied", "Not a directory", "Too many levels of symbolic links" or
        "No such file or directory"."""
        node, why = self._find_why(path, follow)
        return why if node is None else "No such file or directory"

    def _missing(self, path: str) -> ShellError:
        return ShellError(f"{path}: {self.why_missing(path)}")

    def _require_not_sticky(self, d: _Dir, entry, path: str) -> None:
        if self._restricted and d.mode & 0o1000 and entry.owner != self.run_as and d.owner != self.run_as:
            raise ShellError(f"{path}: Operation not permitted")

    def _place(self, path: str) -> _Spot:
        """Where an entry is created, removed or renamed: its directory (which needs search permission) and name. The last
        name is not followed, so this is the link itself when it is one."""
        spot = self._walk(path, follow=False)
        if spot.parent is None:
            raise ShellError(f"{path}: {'Device or resource busy' if spot.node is not None else spot.why}")
        return spot

    def _allowed(self, n, bit: int) -> bool:
        return not self._restricted or self._permits(n, self.run_as, bit)

    def _require(self, n, bit: int, path: str) -> None:
        if not self._allowed(n, bit):
            raise ShellError(f"{path}: Permission denied")

    def exists(self, path: str, follow: bool = True) -> bool:
        """follow=False: a symbolic link counts itself (lstat), even one whose target is missing."""
        return self._find(path, follow) is not None

    def is_dir(self, path: str, follow: bool = True) -> bool:
        return isinstance(self._find(path, follow), _Dir)

    def is_file(self, path: str, follow: bool = True) -> bool:
        return isinstance(self._find(path, follow), _File)

    def is_symlink(self, path: str) -> bool:
        return isinstance(self._find(path, follow=False), _Link)

    def can_read(self, path: str) -> bool:
        n = self._find(path)
        return n is not None and self._allowed(n, 4)

    def can_write(self, path: str) -> bool:
        n = self._find(path)
        return n is not None and self._allowed(n, 2)

    def can_execute(self, path: str) -> bool:
        n = self._find(path)
        return n is not None and self._allowed(n, 1)

    def same_file(self, a: str, b: str, follow: bool = True) -> bool:
        """Whether two paths lead to the same file or directory (`test -ef`): through hard or symbolic links."""
        n = self._find(a, follow)
        return n is not None and n is self._find(b, follow)

    def stat(self, path: str, follow: bool = True) -> FileStat:
        """follow=False: a symbolic link's own status (lstat), with link_target set."""
        n, why = self._find_why(path, follow)
        if isinstance(n, _Dir):
            return FileStat(True, 4096, n.modified, n.mode, n.owner, 2 + sum(isinstance(c, _Dir) for c in n.children.values()))
        if isinstance(n, _File):
            return FileStat(False, n.reported_size if n.reported_size is not None else len(n.raw()), n.modified, n.mode, n.owner, n.nlink)
        if isinstance(n, _Link):
            return FileStat(False, len(n.target.encode("utf-8", "surrogatepass")), n.modified, n.mode, n.owner, n.nlink, n.target)
        raise ShellError(f"{path}: {why}")

    def read_link(self, path: str) -> str:
        """The path a symbolic link holds (`readlink`); "Invalid argument" for anything else."""
        n, why = self._find_why(path, follow=False)
        if isinstance(n, _Link):
            return n.target
        raise ShellError(f"{path}: {why if n is None else 'Invalid argument'}")

    def real_path(self, path: str, existing: bool = True) -> tuple[str | None, str]:
        """(physical absolute path with every symbolic link resolved, why): `realpath`, `pwd -P`. With existing False the last
        name may be missing (`readlink -f`). The path is None, with why, when it can't be resolved."""
        spot = self._walk(path)
        ok = spot.node is not None or (not existing and spot.parent is not None)
        return (spot.real if ok else None), spot.why or "No such file or directory"

    def normalize_physical(self, cwd: str, path: str) -> str:
        """Like normalize, but ".." leaves where the path so far really is, after its symbolic links, as the kernel resolves
        it (the shell's cwd itself stays logical, as bash keeps it)."""
        full = path if path.startswith("/") else cwd.rstrip("/") + "/" + path
        # "." or ".." last names a directory itself, never a link to it (a logical cwd may be reached through one)
        if ([s for s in path.split("/") if s] or ["."])[-1] in (".", ".."):
            return self.real_path(full)[0] or normalize("/", full)
        if ".." not in full.split("/"):
            return normalize("/", full)
        p = "/"
        for seg in full.split("/"):
            if seg in ("", "."):
                continue
            p = get_parent(self.real_path(p)[0] or p) if seg == ".." else "/" + seg if p == "/" else p + "/" + seg
        return p

    def create_symlink(self, path: str, target: str) -> None:
        """Creates a symbolic link at `path` holding `target` (`ln -s`), which need not exist."""
        if not target:
            raise ShellError(f"{path}: No such file or directory")
        spot = self._place(path)
        if spot.node is not None:
            raise ShellError(f"{path}: File exists")
        self._require(spot.parent, 2, path)
        spot.parent.children[spot.name] = _Link(target, owner=self.run_as or spot.parent.owner)
        spot.parent.modified = utc_now()

    def create_hard_link(self, existing: str, path: str, follow: bool = False) -> None:
        """Gives `existing` a second name, `path` (`ln`). With follow a symbolic link is followed, else linked itself. Like
        Linux with protected_hardlinks, the agent may only link files it owns or can read and write."""
        node, why = self._find_why(existing, follow)
        if node is None:
            raise ShellError(f"{existing}: {why}")
        if isinstance(node, _Dir):
            raise ShellError(f"{existing}: hard link not allowed for directory")
        spot = self._place(path)
        if spot.node is not None:
            raise ShellError(f"{path}: File exists")
        self._require(spot.parent, 2, path)
        if self._restricted and node.owner != self.run_as and not self._allowed(node, 6):
            raise ShellError(f"{path}: Operation not permitted")
        node.nlink += 1
        spot.parent.children[spot.name] = node
        spot.parent.modified = utc_now()

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
        spot = self._walk(path)  # through a symbolic link, even a dangling one: its target is written or created
        d, name, existing = spot.parent, spot.name, spot.node
        if d is None:
            raise ShellError(f"{path}: {'Is a directory' if existing is not None else spot.why}")
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

    def set_modified_time(self, path: str, when: datetime, follow: bool = True) -> None:
        """Sets a modification time (tar and unzip restoring one); as with utime(2), only the owner may."""
        n, why = self._find_why(path, follow)
        if n is None:
            raise ShellError(f"{path}: {why}")
        if self._restricted and n.owner != self.run_as:
            raise ShellError(f"{path}: Operation not permitted")
        n.modified = when

    def touch(self, path: str) -> None:
        n, why = self._find_why(path)
        if n is not None:
            self._require(n, 2, path)
            n.modified = utc_now()
        elif why == "Permission denied":
            raise ShellError(f"{path}: Permission denied")
        else:
            self.write_text(path, "")

    def mkdir(self, path: str, parents: bool = False) -> None:
        path = normalize("/", path)
        if self._find(path, follow=False) is not None:  # a symbolic link counts, even a dangling one
            if parents and self.is_dir(path):
                return
            raise ShellError(f"cannot create directory '{path}': File exists")
        if parents and not self.exists(get_parent(path)):
            self.mkdir(get_parent(path), True)
        spot = self._place(path)
        d = spot.parent
        self._require(d, 2, path)
        d.children[spot.name] = _Dir(owner=self.run_as or d.owner)
        d.modified = utc_now()

    def listdir(self, path: str) -> list[str]:
        n, why = self._find_why(path)
        if isinstance(n, _Dir):
            self._require(n, 4, path)
            return sorted(n.children)
        raise ShellError(f"{path}: {why if n is None else 'Not a directory'}")

    def delete(self, path: str, recursive: bool = False) -> None:
        """Removes an entry (a symbolic link itself, never what it leads to); recursive for a directory with entries,
        whose links are removed, not followed."""
        path = normalize("/", path)
        if path == "/":
            raise ShellError("it is dangerous to operate recursively on '/'")
        n, why = self._find_why(path, follow=False)
        if n is None:
            raise ShellError(f"{path}: {why}")
        if isinstance(n, _Dir) and n.children and not recursive:
            raise ShellError(f"{path}: Directory not empty")
        spot = self._place(path)
        parent = spot.parent
        if isinstance(n, _Dir):
            self._require_tree_removable(n, path)
        self._require(parent, 2, path)
        self._require_not_sticky(parent, n, path)
        del parent.children[spot.name]
        self.used_bytes -= _unlink(n)

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

    def copy(self, source: str, dest: str, denied=None, follow_top: bool = True, follow_all: bool = False, broken=None, tick=None) -> None:
        """Copies a file or tree. Entries the user can't read are passed to `denied(path, is_dir)` and skipped, like
        `cp -r`; without a callback the whole copy fails. follow_top: copy what `source` leads to when it is a symbolic link
        (cp without -r, or -H), rather than the link. follow_all: copy what every link in the tree leads to (cp -L); a link
        back into a directory being copied goes to `broken(path, "cyclic")`, one leading nowhere to `broken(path, reason)`
        (None to raise). tick: called for each entry copied (the caller's time limit: links can make a copy far bigger than the tree)."""
        source, dest = normalize("/", source), normalize("/", dest)
        n, why = self._find_why(source, follow_top or follow_all)
        if n is None:
            raise ShellError(f"{source}: {why}")
        place = self._place(dest)
        if isinstance(n, _Dir) and (place.real + "/").startswith(self.real_path(source)[0] + "/"):
            raise ShellError(f"cannot copy a directory, '{source}', into itself, '{dest}'")
        target = self._find(dest) if isinstance(place.node, _Link) else place.node  # a file is copied through a link onto its target
        if isinstance(target, _Dir) and not isinstance(n, _Dir):
            raise ShellError(f"{dest}: Is a directory")
        parent = place.parent
        self._require(target if target is not None else parent, 2, dest)
        user = self.run_as if self._restricted else None
        owner = self.run_as or parent.owner
        copying: set[int] = set()  # directories on the way down, so -L can't loop
        cloned = 0

        def clone(node, p: str):
            if isinstance(node, _Link):
                if not follow_all:
                    return _Link(node.target, owner=owner, mode=node.mode)
                to, reason = self._find_why(p)
                if to is None:
                    if broken is None:
                        raise ShellError(f"{p}: {reason}")
                    broken(p, reason)
                    return None
                node = to
            # a directory already being copied further up, reached through a link (even one higher up), as cp -L reports it
            if isinstance(node, _Dir) and id(node) in copying:
                if broken is None:
                    raise ShellError(f"{p}: cyclic")
                broken(p, "cyclic")
                return None
            if tick:
                tick()
            if not self._permits(node, user, 5 if isinstance(node, _Dir) else 4):
                if denied is None:
                    raise ShellError(f"{p}: Permission denied")
                denied(p, isinstance(node, _Dir))
                return None
            if isinstance(node, _File):
                # the copy is built before it is stored: links can make it far bigger than the tree, so charge it as it grows
                nonlocal cloned
                cloned += node.size
                if self.run_as is not None and self.used_bytes + cloned > self.capacity:
                    raise ShellError(f"{dest}: {NO_SPACE}")
                return _File(node.text(), owner=owner, mode=node.mode, data=node.data, size=node.size)
            c = _Dir(owner=owner, mode=node.mode)
            copying.add(id(node))
            for k, v in node.children.items():
                if (cc := clone(v, f"{p}/{k}")) is not None:
                    c.children[k] = cc
            copying.discard(id(node))
            return c

        copied = clone(n, source)
        if copied is None:
            return
        if isinstance(target, _File) and isinstance(copied, _File):  # onto an existing file: write into it, keeping owner and mode
            target.store(copied.raw(), False, lambda delta: self._reserve(delta, dest))
            target.modified = utc_now()
        else:
            old = place.node
            old_size = 0 if old is None else _tree_size(old)
            self._reserve(_tree_size(copied) - old_size, dest)
            if old is not None:
                self.used_bytes += old_size - _unlink(old)
            parent.children[place.name] = copied

    def move(self, source: str, dest: str) -> None:
        """Renames an entry; a symbolic link is moved itself, and a link at `dest` is replaced, not followed."""
        source, dest = normalize("/", source), normalize("/", dest)
        n, why = self._find_why(source, follow=False)
        if n is None:
            raise ShellError(f"{source}: {why}")
        origin, to = self._place(source), self._place(dest)
        # by real paths: through a link, a directory could otherwise be moved into itself and cut off from the tree
        if (to.real + "/").startswith(origin.real + "/"):
            raise ShellError(f"cannot move '{source}' to a subdirectory of itself, '{dest}'")
        existing, target = to.node, to.parent
        if isinstance(existing, _Dir):
            raise ShellError(f"{dest}: Is a directory")
        if existing is n:
            return  # two hard links of one file: rename(2) leaves both
        self._require(origin.parent, 2, source)
        self._require(target, 2, dest)
        if isinstance(n, _Dir) and origin.parent is not target:
            self._require(n, 2, source)  # its ".." entry changes
        self._require_not_sticky(origin.parent, n, source)
        if existing is not None:
            self._require_not_sticky(target, existing, dest)
        del origin.parent.children[origin.name]
        if existing is not None:
            self.used_bytes -= _unlink(existing)
        target.children[to.name] = n

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
        """Every path, parents first: (path, is_dir, mode, owner, modified, text, data, reported_size, link_target, hard_link).
        link_target: a symbolic link's path; hard_link: an earlier entry's path that this one is another name of (it still
        carries the content). Contents are immutable str/bytes."""
        first: dict[int, str] = {}  # where each hard-linked node was seen first
        stack = [("/", self._root)]
        while stack:
            path, n = stack.pop()
            hard = None
            if not isinstance(n, _Dir) and n.nlink > 1 and first.setdefault(id(n), path) != path:
                hard = first[id(n)]
            if isinstance(n, _File):
                yield path, False, n.mode, n.owner, n.modified, n.text() if n.data is None else None, n.data, n.reported_size, None, hard
                continue
            if isinstance(n, _Link):
                yield path, False, n.mode, n.owner, n.modified, None, None, None, n.target, hard
                continue
            yield path, True, n.mode, n.owner, n.modified, None, None, None, None, None
            for name in reversed(list(n.children)):
                stack.append(("/" + name if path == "/" else f"{path}/{name}", n.children[name]))

    def replace_all(self, entries) -> None:
        """Replaces every file with `entries` (as entries() gives them, parents first): a snapshot restore. An entry's parent
        is reached without symbolic links, so an entry can't land anywhere but its own path."""
        root, used = _Dir(), 0

        def plain(path: str):
            cur = root
            for seg in filter(None, path.split("/")):
                cur = cur.children.get(seg) if isinstance(cur, _Dir) else None
            return cur

        for path, is_dir, mode, owner, modified, text, data, reported_size, link_target, hard_link in entries:
            if path != "/" and not isinstance(parent := plain(path.rsplit("/", 1)[0]), _Dir):
                raise ShellError(f"{path}: parent is not a directory")
            if hard_link is not None:
                same = plain(hard_link)
                if same is None or isinstance(same, _Dir):
                    raise ShellError(f"{path}: hard link to {hard_link}, which is not an earlier file")
                if get_name(path) in parent.children:
                    raise ShellError(f"{path}: File exists")
                same.nlink += 1
                parent.children[get_name(path)] = same
                continue
            if is_dir:
                node = root if path == "/" else _Dir()
            elif path == "/":
                raise ShellError("/: Not a directory")
            elif link_target is not None:
                node = _Link(link_target)
            else:
                node = _File(text or "", data=data, reported_size=reported_size)
                node.size = len(data) if data is not None else len(node.content.encode("utf-8", "surrogatepass"))
                used += node.size
            node.mode, node.owner, node.modified = mode, owner, modified
            if path == "/":
                continue
            name = get_name(path)
            if name in parent.children:
                raise ShellError(f"{path}: File exists")
            parent.children[name] = node
        self._root, self.used_bytes = root, used
