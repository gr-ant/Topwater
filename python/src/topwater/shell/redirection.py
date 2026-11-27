"""Shell: redirections, file descriptors and output writers. See docs/architecture.md."""
from __future__ import annotations

import io
from dataclasses import replace
from typing import TYPE_CHECKING

from .._spec import DIAGNOSTICS, ERRNO, diagnostic
from ..vfs import ShellError, VirtualFileSystem
from .types import StdinBuffer, _Io, _Null

if TYPE_CHECKING:
    from . import Shell


# ---------- writers ----------

class CappedWriter(io.StringIO):
    """Buffers a command's output (a pipeline stage, $(...), the call's stdout); past the string limit the call stops."""

    def __init__(self, shell: "Shell", limit: int | None = None):
        super().__init__()
        self._shell = shell
        self._limit = limit if limit is not None else shell.max_string_bytes
        self._len = 0

    def full(self) -> None:
        raise self._shell.too_large("output", "write to a file in smaller pieces or filter earlier in the pipeline")

    def write(self, s: str) -> int:
        if self._len + len(s) > self._limit:
            self.full()
        self._len += len(s)
        return super().write(s)

    def clear(self) -> None:
        self.seek(0)
        self.truncate()
        self._len = 0


class FileSink(CappedWriter):
    """A redirect's file: buffered, but written through every megabyte, so the filesystem's cap (not the string limit) bounds it."""

    def __init__(self, shell: "Shell", path: str):
        super().__init__(shell, 1 << 20)
        self.path = path

    def full(self) -> None:
        text = self.getvalue()
        self.clear()  # dropped even if the disk is full, so a loop writing to a full disk doesn't grow the buffer
        self._shell.fs.append_text(self.path, text)

    def write(self, s: str) -> int:
        if len(s) < 1 << 20:
            return super().write(s)
        self.full()  # a big write (cat of a big file) goes straight to the file instead of through the buffer
        self._shell.fs.append_text(self.path, s)
        return len(s)


class FileAppender:
    """A file opened by `exec >file`: written through, so later commands in the same call can read it."""

    def __init__(self, fs: VirtualFileSystem, path: str):
        self.fs, self.path = fs, path

    def write(self, s: str) -> int:
        if s:
            self.fs.append_text(self.path, s)
        return len(s)


class SwitchWriter:
    """A shell's stdout or stderr as `exec` sees it: what it points at can be changed for the rest of the shell."""

    def __init__(self, target):
        self.target = target

    def write(self, s: str) -> int:
        return self.target.write(s)

    def getvalue(self) -> str:
        return self.target.getvalue()


class RedirectionPart:
    # ---------- redirections ----------

    async def _apply_redirects(self, redirects, io_: _Io, sinks) -> _Io | None:
        for r in redirects:
            try:
                if r.op in ("<<", "<<<", "<"):
                    inp = await self._open_input(r, io_)
                    io_ = replace(io_, fds={**(io_.fds or {}), r.fd: inp}) if r.fd >= 3 else replace(io_, stdin=inp)
                elif r.op in (">&", "<&"):
                    target = await self._expand_single(r.target, io_)
                    if target == "-":
                        dup = StdinBuffer() if r.op == "<&" else _Null()
                    elif target.isdigit() and self._descriptor(int(target), io_) is not None:
                        dup = self._descriptor(int(target), io_)
                    else:
                        raise ShellError(diagnostic("redirectFailed", target=target, reason=ERRNO["EBADF"] if target.isdigit() else DIAGNOSTICS["ambiguousRedirect"]))
                    io_ = _with_fd(io_, r.fd, dup)
                else:  # > >> >| &> &>>
                    def open_sink(path: str):
                        sink = FileSink(self, path)
                        sinks.append(sink)
                        return sink

                    w = await self._open_output(r, io_, open_sink)
                    io_ = replace(io_, out=w, err=w) if r.op.startswith("&") else _with_fd(io_, r.fd, w)
            except ShellError as e:
                io_.err.write(f"{self.err_prefix}{e}\n")
                return None
        return io_

    async def _open_input(self, r, io_: _Io) -> StdinBuffer:
        if r.op == "<<":
            return StdinBuffer(await self._expand_single(r.here.body, io_))
        if r.op == "<<<":
            return StdinBuffer(await self._expand_single(r.target, io_) + "\n")
        target = await self._expand_single(r.target, io_)
        if self._mounts.items:
            await self._mounts.before("<", [], io_.err, redirect=target)
        try:
            return StdinBuffer(self.fs.read_pipe_text(self.resolve(target)))
        except ShellError as e:
            raise ShellError(f"{target}: {str(e).rsplit(': ', 1)[-1]}") from None

    async def _open_output(self, r, io_: _Io, open_):
        """The writer for > >> >| &> &>>: /dev/null, /dev/stdout, /dev/stderr, or a file made by open_."""
        target = await self._expand_single(r.target, io_)
        path = self.resolve(target)
        if path == "/dev/null":
            return _Null()
        if path in ("/dev/stderr", "/dev/stdout"):
            return io_.err if path == "/dev/stderr" else io_.out
        if path.startswith("/dev/fd/") and path[8:].isdigit():
            fw = self._descriptor(int(path[8:]), io_)
            if fw is not None and not isinstance(fw, StdinBuffer):
                return fw
        if self.fs.is_dir(path):
            raise ShellError(f"{target}: Is a directory")
        if self.noclobber and r.op in (">", "&>") and self.fs.is_file(path):
            raise ShellError(diagnostic("noclobber", target=target))
        if r.op.endswith(">>") and self._mounts.items:
            await self._mounts.before(">>", [], io_.err, redirect=target)
        try:
            if not r.op.endswith(">>") or not self.fs.exists(path):
                self.fs.write_text(path, "")  # truncate on open, like bash
        except ShellError as e:
            raise ShellError(f"{target}: {str(e).rsplit(': ', 1)[-1]}") from None
        return open_(path)

    def _descriptor(self, n: int, io_: _Io):
        """What fd n refers to: 0 a StdinBuffer, 1 and 2 writers, 3+ whatever `exec` or this command opened; None if closed."""
        if n == 0:
            return io_.stdin
        if n == 1:
            return io_.out
        if n == 2:
            return io_.err
        return (io_.fds or {}).get(n) or self.fds.get(n)

    async def _exec_redirects(self, redirects, io_: _Io) -> int:
        """`exec` with only redirections: they last for the rest of the shell. fds 1 and 2 are retargeted through the
        SwitchWriter each (sub)shell starts with; files opened this way are written through as text arrives.
        shortcut: inside a command that has its own redirections (`{ exec >f; } 2>x`) the change is ignored."""
        for r in redirects:
            try:
                if r.op in ("<<", "<<<", "<"):
                    target = await self._open_input(r, io_)
                elif r.op in (">&", "<&"):
                    t = await self._expand_single(r.target, io_)
                    if t == "-":
                        target = None
                    elif t.isdigit() and self._descriptor(int(t), io_) is not None:
                        d = self._descriptor(int(t), io_)
                        target = d.target if isinstance(d, SwitchWriter) else d
                    else:
                        raise ShellError(diagnostic("redirectFailed", target=t, reason=ERRNO["EBADF"]))
                else:
                    target = await self._open_output(r, io_, lambda path: FileAppender(self.fs, path))
                if r.fd_variable is None:
                    named = r.fd
                elif target is None:  # {fd}>&- closes the fd it names
                    v = self.get_variable(r.fd_variable)
                    named = int(v) if v.isdigit() else -1
                else:
                    named = next(n for n in range(10, 110) if n not in self.fds)
                if r.fd_variable is not None and target is not None:
                    self.set_var(r.fd_variable, str(named))
                for fd in ([1, 2] if r.op.startswith("&") else [named]):
                    if fd < 0:
                        continue
                    if fd == 0:
                        io_.stdin.reset(target.read_all() if isinstance(target, StdinBuffer) else "")
                    elif fd <= 2:
                        w = io_.out if fd == 1 else io_.err
                        if isinstance(w, SwitchWriter):
                            w.target = target if target is not None and not isinstance(target, StdinBuffer) else _Null()
                    elif target is None:
                        self.fds.pop(fd, None)
                    else:
                        self.fds[fd] = target
            except ShellError as e:
                io_.err.write(f"{self.err_prefix}{e}\n")
                return 1
        return 0

    def input_fd(self, fd: int) -> StdinBuffer | None:
        """`read -u N` / `mapfile -u N`: the input opened on fd N by `exec N<file`, or None."""
        v = self.fds.get(fd)
        return v if isinstance(v, StdinBuffer) else None


# ---------- helpers ----------

def _with_fd(io_: _Io, fd: int, target) -> _Io:
    if fd == 0:
        return replace(io_, stdin=target) if isinstance(target, StdinBuffer) else io_
    if fd in (1, 2):
        if isinstance(target, StdinBuffer):
            return io_
        return replace(io_, out=target) if fd == 1 else replace(io_, err=target)
    return replace(io_, fds={**(io_.fds or {}), fd: target})
