"""Run a real program from the host as a command inside the simulation.

    from topwater.host import register_host_command
    register_host_command(shell, "git", "/usr/bin/git")
    register_host_command(shell, "officecli", "/usr/local/bin/officecli")

The agent's files live in memory, and real programs need real files. So each registered program gets a private
mirror directory on disk. On every call the workspace (the agent's home by default) is copied out to the mirror,
the program runs there with the agent's arguments, and whatever it created, changed or deleted is copied back.

THIS OPENS A DOOR IN THE SANDBOX. The program is real and runs with your process's permissions. Topwater keeps
it pointed at the mirror (working directory, HOME, and path-like arguments are redirected there), but that is
best effort, not a security boundary: a program that can run other programs, read arbitrary paths or reach the
network can still do so. Wrap only tools you would let the agent use directly, and pass `launcher=` to run them
inside a real sandbox (bubblewrap, firejail, a container) when the stakes call for it.
"""
from __future__ import annotations

import asyncio
import atexit
import os
import shutil
import subprocess
import tempfile
from pathlib import Path
from typing import Callable, Iterable, Sequence

from .vfs import ShellError, normalize


def _read_bytes(fs, path: str) -> bytes:
    return fs.read_bytes(path) if hasattr(fs, "read_bytes") else fs.read_text(path).encode()


def _write_file(fs, path: str, data: bytes) -> None:
    try:
        text = data.decode("utf-8")
        if "\x00" in text:
            raise UnicodeDecodeError("utf-8", data, 0, 1, "binary")
        fs.write_text(path, text)
    except UnicodeDecodeError:
        if not hasattr(fs, "write_bytes"):
            raise ShellError(f"{path}: binary output needs a filesystem with write_bytes") from None
        fs.write_bytes(path, data)


class HostCommand:
    """One real program exposed to the agent. Create it with `register_host_command`."""

    def __init__(self, shell, name: str, program: str, *, workspace: str | None = None, env: dict[str, str] | None = None,
                 timeout: float = 120.0, exclude: Iterable[str] = (), launcher: Callable[[list[str], str], Sequence[str]] | None = None,
                 rewrite_paths: bool = True, max_output: int = 1_000_000):
        resolved = shutil.which(program)
        if resolved is None:
            raise FileNotFoundError(f"host program not found: {program}")
        self.shell, self.name, self.program = shell, name, resolved
        self.workspace = normalize("/", workspace or shell.env.get("HOME", "/home/agent"))
        self.env = dict(env or {})
        self.timeout = timeout
        self.exclude = set(exclude)
        self.launcher = launcher
        self.rewrite_paths = rewrite_paths
        self.max_output = max_output
        self.root = Path(tempfile.mkdtemp(prefix=f"topwater-{name}-"))  # mirror of "/" for this program
        atexit.register(self.close)

    def close(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)

    # ---------- mirror ----------

    def _real(self, vfs_path: str) -> Path:
        return self.root / vfs_path.lstrip("/")

    def _skip(self, name: str) -> bool:
        return name in self.exclude

    def _export(self) -> dict[str, bytes]:
        """Copies the workspace out to the mirror; returns {relative path: bytes} as exported."""
        fs = self.shell.fs
        seen: dict[str, bytes] = {}
        dirs: set[str] = {""}

        def walk(vpath: str, rel: str) -> None:
            if not fs.can_read(vpath):
                return
            for entry in fs.listdir(vpath):
                if self._skip(entry):
                    continue
                child, crel = f"{vpath.rstrip('/')}/{entry}", f"{rel}/{entry}".lstrip("/")
                if fs.is_dir(child):
                    dirs.add(crel)
                    walk(child, crel)
                elif fs.can_read(child):
                    seen[crel] = _read_bytes(fs, child)

        if fs.is_dir(self.workspace):
            walk(self.workspace, "")
        base = self._real(self.workspace)
        base.mkdir(parents=True, exist_ok=True)
        # remove what no longer exists in the virtual filesystem, then write what changed
        for dirpath, dirnames, filenames in os.walk(base, topdown=False):
            rel_dir = os.path.relpath(dirpath, base).replace(os.sep, "/")
            rel_dir = "" if rel_dir == "." else rel_dir
            if any(self._skip(part) for part in rel_dir.split("/")):
                continue
            for f in filenames:
                if f"{rel_dir}/{f}".lstrip("/") not in seen and not self._skip(f):
                    os.remove(os.path.join(dirpath, f))
            for d in dirnames:
                if f"{rel_dir}/{d}".lstrip("/") not in dirs and not self._skip(d):
                    shutil.rmtree(os.path.join(dirpath, d), ignore_errors=True)
        for d in dirs:
            (base / d).mkdir(parents=True, exist_ok=True)
        for rel, data in seen.items():
            target = base / rel
            if not target.is_file() or target.read_bytes() != data:
                target.write_bytes(data)
        return seen

    def _import(self, before: dict[str, bytes], err) -> None:
        """Copies changes made by the program back into the virtual filesystem."""
        fs = self.shell.fs
        base = self._real(self.workspace)
        after: set[str] = set()
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [d for d in dirnames if not self._skip(d) and not os.path.islink(os.path.join(dirpath, d))]
            rel_dir = os.path.relpath(dirpath, base).replace(os.sep, "/")
            rel_dir = "" if rel_dir == "." else rel_dir
            vdir = f"{self.workspace.rstrip('/')}/{rel_dir}".rstrip("/") or "/"
            try:
                if not fs.exists(vdir):
                    fs.mkdir(vdir, parents=True)
            except ShellError as e:
                err.write(f"{self.name}: {e}\n")
                continue
            for f in filenames:
                real = os.path.join(dirpath, f)
                if self._skip(f) or os.path.islink(real) or not os.path.isfile(real):
                    continue
                rel = f"{rel_dir}/{f}".lstrip("/")
                after.add(rel)
                data = Path(real).read_bytes()
                if before.get(rel) != data:
                    try:
                        _write_file(fs, f"{vdir.rstrip('/')}/{f}", data)
                    except ShellError as e:
                        err.write(f"{self.name}: {e}\n")
        for rel in before.keys() - after:
            try:
                fs.delete(f"{self.workspace.rstrip('/')}/{rel}")
            except ShellError:
                pass
        # directories the program removed
        def prune(vpath: str, rel: str) -> None:
            if not fs.can_read(vpath):
                return
            for entry in fs.listdir(vpath):
                child, crel = f"{vpath.rstrip('/')}/{entry}", f"{rel}/{entry}".lstrip("/")
                if self._skip(entry) or not fs.is_dir(child):
                    continue
                if not (base / crel).is_dir():
                    try:
                        fs.delete(child, recursive=True)
                    except ShellError:
                        pass
                else:
                    prune(child, crel)

        if fs.is_dir(self.workspace):
            prune(self.workspace, "")

    # ---------- arguments ----------

    def _map_arg(self, arg: str) -> str:
        """Redirects path-like arguments into the mirror so they can't point at the host's own files."""
        if not self.rewrite_paths:
            return arg
        prefix, value = "", arg
        if arg.startswith("-") and "=" in arg:
            prefix, _, value = arg.partition("=")
            prefix += "="
        fs = self.shell.fs
        if value.startswith("/") and value != "/":
            top = "/" + value.strip("/").split("/")[0]
            # a real location (in the simulation or on the host) is redirected; other things that merely start
            # with "/" (XPath-like selectors such as /slide[1], regexes) are left alone
            if fs.exists(top) or os.path.exists(top):
                return prefix + str(self._real(normalize("/", value)))
        elif ".." in value.split("/"):
            return prefix + str(self._real(self.shell.resolve(value)))
        return arg

    # ---------- run ----------

    async def __call__(self, ctx) -> int:
        before = self._export()
        cwd = self.shell.cwd
        inside = cwd == self.workspace or cwd.startswith(self.workspace.rstrip("/") + "/")
        real_cwd = self._real(cwd if inside else self.workspace)
        real_cwd.mkdir(parents=True, exist_ok=True)
        argv = [self.program] + [self._map_arg(a) for a in ctx.args]
        if self.launcher is not None:
            argv = list(self.launcher(argv, str(self.root)))
        home = self._real(self.shell.env.get("HOME", self.workspace))
        home.mkdir(parents=True, exist_ok=True)
        env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": str(home), "LANG": "C.UTF-8", "TERM": "dumb",
               "USER": self.shell.user, "LOGNAME": self.shell.user, "TMPDIR": str(self._real("/tmp"))}
        self._real("/tmp").mkdir(parents=True, exist_ok=True)
        env.update(self.env)
        stdin = ctx.stdin.encode()

        def run():
            return subprocess.run(argv, cwd=real_cwd, env=env, input=stdin, capture_output=True, timeout=self.timeout)

        try:
            proc = await asyncio.to_thread(run)
        except subprocess.TimeoutExpired:
            self._import(before, ctx.stderr)
            ctx.error(f"{self.name}: timed out after {self.timeout:g}s")
            return 124
        except OSError as e:
            ctx.error(f"{self.name}: cannot execute: {e.strerror or e}")
            return 126
        self._import(before, ctx.stderr)

        def clean(data: bytes) -> str:
            text = data[: self.max_output].decode("utf-8", "replace")
            # show virtual paths, not mirror paths
            return text.replace(str(self.root) + "/", "/").replace(str(self.root), "/")

        ctx.stdout.write(clean(proc.stdout))
        ctx.stderr.write(clean(proc.stderr))
        return proc.returncode if proc.returncode >= 0 else 128 - proc.returncode


def register_host_command(shell, name: str, program: str | None = None, *, description: str | None = None, **options) -> HostCommand:
    """Exposes a real host program to the agent as `name`. See the module docstring for what this does and risks.

    Options: workspace (virtual directory to mirror, default the agent's home), env (extra environment variables),
    timeout (seconds), exclude (file/directory names kept out of the sync in both directions), launcher
    (callable(argv, mirror_root) -> argv, e.g. to prefix a sandbox), rewrite_paths, max_output.
    """
    cmd = HostCommand(shell, name, program or name, **options)
    shell.register_command(name, cmd, description or f"{name} (host program)")
    return cmd
