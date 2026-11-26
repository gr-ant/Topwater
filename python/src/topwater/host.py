"""Run a real program from the host as a command inside the simulation.

    from topwater.host import register_host_command
    register_host_command(shell, "git", "/usr/bin/git")
    register_host_command(shell, "officecli", "/usr/local/bin/officecli")

The agent's files live in memory, and real programs need real files. So each registered program gets a private
mirror directory on disk. On every call the workspace (the agent's home by default) and the shared directories
(/tmp) are copied out to the mirror, the program runs there with the agent's arguments, and whatever it created,
changed or deleted is copied back. Unchanged files are neither read nor written.

THIS OPENS A DOOR IN THE SANDBOX. The program is real and runs with your process's permissions. Topwater keeps
it pointed at the mirror (working directory, HOME, and path-like arguments are redirected there), but that is
best effort, not a security boundary: a program that can run other programs, read arbitrary paths or reach the
network can still do so. Wrap only tools you would let the agent use directly, and pass `launcher=` (for example
`bubblewrap()`) to run them inside a real sandbox when the stakes call for it.
"""
from __future__ import annotations

import asyncio
import atexit
import os
import shutil
import signal
import subprocess
import tempfile
import threading
from datetime import timezone
from pathlib import Path
from typing import Callable, Iterable, Sequence

from . import bytetext
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


def _within(path: str, directory: str) -> bool:
    return path == directory or path.startswith(directory.rstrip("/") + "/")


class HostCommand:
    """One real program exposed to the agent. Create it with `register_host_command`."""

    def __init__(self, shell, name: str, program: str, *, workspace: str | None = None, env: dict[str, str] | None = None,
                 timeout: float = 120.0, exclude: Iterable[str] = (), shared_directories: Iterable[str] = ("/tmp",),
                 launcher: Callable[[list[str], str], Sequence[str]] | None = None,
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
        # every directory mirrored to disk: the workspace first, then the extras (agents put scratch files in /tmp)
        self.shared = [self.workspace]
        for d in (normalize("/", d) for d in shared_directories):
            if not any(_within(d, x) for x in self.shared):
                self.shared.append(d)
        self.root = Path(tempfile.mkdtemp(prefix=f"topwater-{name}-"))  # mirror of "/" for this program
        self._private_tmp = Path(tempfile.mkdtemp(prefix=f"topwater-{name}-tmp-"))
        # what each file looked like on both sides when it was last synced, so unchanged files are neither read nor written
        self._synced: dict[str, tuple] = {}
        self._restore: Callable[[], None] | None = None  # puts back what the name ran before register_host_command
        atexit.register(self.close)

    def close(self) -> None:
        """Removes the mirror directories and puts back what the name ran before (the built-in, or nothing)."""
        if self._restore is not None:
            self._restore()
            self._restore = None
        for d in (self.root, self._private_tmp):
            shutil.rmtree(d, ignore_errors=True)

    # ---------- mirror ----------

    def _real(self, vfs_path: str) -> Path:
        return self.root / vfs_path.lstrip("/")

    def _skip(self, name: str) -> bool:
        return name in self.exclude

    # shortcut: a file counts as unchanged while its size and modification time match on both sides. Each file we
    # write to the mirror is stamped with the virtual file's own (microsecond-precise) time, which a program's write
    # can't land on, so a quick same-size rewrite is still seen. The gap is `mv`, which keeps the moved file's time,
    # onto a path whose old file had the same size and time. Compare content if that matters.
    def _unchanged(self, vpath: str, real: Path, st=None) -> bool:
        rec = self._synced.get(vpath)
        if rec is None:
            return False
        try:
            r = real.stat()
        except OSError:
            return False
        return (r.st_size, r.st_mtime_ns) == rec[2:] and (st is None or (st.modified, st.size) == rec[:2])

    def _remember(self, vpath: str, real: Path, st) -> None:
        try:
            ns = int(st.modified.replace(tzinfo=timezone.utc).timestamp() * 1_000_000) * 1000 + 1  # odd nanoseconds: never a kernel clock value
            os.utime(real, ns=(ns, ns))
        except OSError:
            pass
        r = real.stat()
        self._synced[vpath] = (st.modified, st.size, r.st_size, r.st_mtime_ns)

    def _export(self, ws: str) -> set[str]:
        """Copies one shared directory out to the mirror; returns the relative paths of the files exported."""
        fs = self.shell.fs
        files: set[str] = set()
        dirs: set[str] = {""}
        ws_real = fs.real_path(ws)[0] or ws

        def walk(vpath: str, rel: str) -> None:
            if not fs.can_read(vpath):
                return
            for entry in fs.listdir(vpath):
                if self._skip(entry):
                    continue
                child, crel = normalize(vpath, entry), f"{rel}/{entry}".lstrip("/")
                # a symbolic link is never followed into a directory (it could lead back up, or out of the workspace), and
                # goes out as a copy of its file only when that file is inside the workspace; no real link is ever made
                if fs.is_symlink(child):
                    real = fs.real_path(child)[0]
                    if fs.is_file(child) and real is not None and real.startswith(ws_real + "/") and fs.can_read(child):
                        files.add(crel)
                elif fs.is_dir(child):
                    dirs.add(crel)
                    walk(child, crel)
                elif fs.can_read(child):
                    files.add(crel)

        if fs.is_dir(ws):
            walk(ws, "")
        root = self._real(ws)
        root.mkdir(parents=True, exist_ok=True)

        def prune(real: Path, rel: str) -> None:
            # remove what no longer exists in the virtual filesystem. A link the program made (git checkout, npm)
            # stays, unless the agent now has a real entry of that name; it is never followed.
            with os.scandir(real) as it:
                entries = list(it)
            for e in entries:
                if self._skip(e.name):
                    continue
                crel = f"{rel}/{e.name}".lstrip("/")
                if e.is_symlink():
                    if crel in dirs or crel in files:
                        os.remove(e.path)
                elif e.is_dir():
                    if crel not in dirs:
                        shutil.rmtree(e.path, ignore_errors=True)
                    else:
                        prune(Path(e.path), crel)
                elif crel not in files or crel in dirs:
                    os.remove(e.path)

        prune(root, "")
        for d in dirs:
            (root / d).mkdir(parents=True, exist_ok=True)
        for rel in files:
            target = root / rel
            vpath = normalize(ws, rel)
            st = fs.stat(vpath)
            if not self._unchanged(vpath, target, st):
                target.write_bytes(_read_bytes(fs, vpath))
                self._remember(vpath, target, st)
            if st.mode & 0o100:  # scripts the agent made executable must be executable for the program too
                os.chmod(target, os.stat(target).st_mode | 0o111)
        return files

    def _import(self, ws: str, before: set[str], err) -> None:
        """Copies changes the program made to one shared directory back into the virtual filesystem."""
        fs = self.shell.fs
        root = self._real(ws)
        after: set[str] = set()

        def walk(real: Path, rel: str) -> None:
            vdir = normalize(ws, rel) if rel else ws
            try:
                if not fs.exists(vdir):
                    fs.mkdir(vdir, parents=True)
            except ShellError as e:
                err.write(f"{self.name}: {e}\n")
                return
            try:
                with os.scandir(real) as it:
                    entries = sorted(it, key=lambda e: e.name)
            except OSError:
                return
            for e in entries:
                if self._skip(e.name) or e.is_symlink():
                    continue
                crel = f"{rel}/{e.name}".lstrip("/")
                if e.is_dir():
                    walk(Path(e.path), crel)
                    continue
                if not e.is_file():
                    continue  # sockets, pipes
                after.add(crel)
                vfile = normalize(vdir, e.name)
                if crel in before and self._unchanged(vfile, Path(e.path)):
                    continue  # untouched by the program: whatever the virtual file holds now stays
                try:
                    data = Path(e.path).read_bytes()
                except OSError:
                    continue
                try:
                    if crel not in before or _read_bytes(fs, vfile) != data:
                        _write_file(fs, vfile, data)
                        if os.stat(e.path).st_mode & 0o100:
                            fs.chmod(vfile, fs.stat(vfile).mode | 0o111)
                    self._remember(vfile, Path(e.path), fs.stat(vfile))
                except ShellError as ex:
                    err.write(f"{self.name}: {ex}\n")

        walk(root, "")
        for rel in before - after:
            try:
                fs.delete(normalize(ws, rel))
            except ShellError:
                pass

        def prune(vpath: str, rel: str) -> None:  # directories the program removed
            if not fs.can_read(vpath):
                return
            for entry in fs.listdir(vpath):
                child, crel = normalize(vpath, entry), f"{rel}/{entry}".lstrip("/")
                if self._skip(entry) or not fs.is_dir(child, follow=False):  # a link to a directory was never exported
                    continue
                if not (root / crel).is_dir():
                    try:
                        fs.delete(child, recursive=True)
                    except ShellError:
                        pass
                else:
                    prune(child, crel)

        if fs.is_dir(ws):
            prune(ws, "")

    # ---------- arguments ----------

    def _map_arg(self, arg: str) -> str:
        """Redirects path-like arguments into the mirror so they can't point at the host's own files."""
        if not self.rewrite_paths:
            return arg
        prefix, value = "", arg
        # --out=/path and key=/path (e.g. `--prop src=/tmp/a.md`) carry a path after the first '='
        eq = arg.find("=")
        if eq > 0 and "/" not in arg[:eq]:
            prefix, value = arg[: eq + 1], arg[eq + 1:]
        # -o/path and -C/path: a short option with the path attached
        if not prefix and len(arg) > 3 and arg[0] == "-" and arg[1] != "-" and arg[2] == "/":
            prefix, value = arg[:2], arg[2:]
        if value == "/":
            return prefix + str(self.root)
        fs = self.shell.fs
        if value.startswith("/"):
            top = "/" + value.strip("/").split("/")[0]
            # a real location (in the simulation or on the host) is redirected; other things that merely start
            # with "/" (XPath-like selectors such as /slide[1], regexes) are left alone.
            # shortcut: a path whose top folder exists nowhere (-o /report.pdf) is therefore passed through as typed;
            # pass a launcher with a real sandbox if the program must not be able to write outside the mirror.
            if fs.exists(top) or os.path.exists(top):
                path = normalize("/", value)
                # only the shared folders are copied to disk; say so instead of running on a file that isn't there
                if fs.exists(top) and not any(_within(path, x) or _within(x, path) for x in self.shared):
                    raise ShellError(f"{value}: {self.name} can only use files under {' and '.join(self.shared)}; copy it there first")
                return prefix + str(self._real(path))
        elif ".." in value.split("/"):
            return prefix + str(self._real(self.shell.resolve(value)))
        return arg

    # ---------- run ----------

    async def __call__(self, ctx) -> int:
        before = {ws: self._export(ws) for ws in self.shared}
        cwd = self.shell.cwd
        inside = any(_within(cwd, s) for s in self.shared)
        real_cwd = self._real(cwd if inside else self.workspace)
        real_cwd.mkdir(parents=True, exist_ok=True)
        argv = [self.program] + [self._map_arg(a) for a in ctx.args]
        if self.launcher is not None:
            argv = list(self.launcher(argv, str(self.root)))
        home = self._real(self.shell.env.get("HOME", self.workspace))
        home.mkdir(parents=True, exist_ok=True)
        self._real("/tmp").mkdir(parents=True, exist_ok=True)
        # the agent's own variables (GIT_AUTHOR_NAME=x git commit) come first; the fixed ones below win,
        # except the ones that make the dynamic loader run a library of the agent's choosing in any program
        env = {k: v for k, v in self.shell.env.items() if not k.startswith(("LD_", "DYLD_")) and k not in ("PWD", "OLDPWD", "SHELL")}
        env.update({"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": str(home), "LANG": "C.UTF-8", "TERM": "dumb",
                    "USER": self.shell.user, "LOGNAME": self.shell.user,
                    "TMPDIR": str(self._private_tmp)})  # the program's own temp files stay out of the agent's /tmp
        env.update(self.env)
        stdin = bytetext.encode(ctx.stdin)

        try:
            code, out, err, timed_out = await asyncio.to_thread(_run, argv, real_cwd, env, stdin, self.timeout, self.max_output)
        except OSError as e:
            ctx.error(f"{self.name}: cannot execute: {e.strerror or e}")
            return 126

        def clean(data: bytes) -> str:
            # show virtual paths, not mirror paths
            return data.decode("utf-8", "replace").replace(str(self.root) + "/", "/").replace(str(self.root), "/")

        for ws in self.shared:
            self._import(ws, before[ws], ctx.stderr)
        ctx.stdout.write(clean(out))
        ctx.stderr.write(clean(err))
        if timed_out:
            ctx.error(f"{self.name}: timed out after {self.timeout:g}s")
            return 124
        return code


def _run(argv, cwd, env, stdin: bytes, timeout: float, cap: int):
    """Runs the program; returns (exit code, stdout, stderr, timed out). A child it leaves running can't hang the call."""
    proc = subprocess.Popen(argv, cwd=cwd, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            start_new_session=True)
    bufs = (bytearray(), bytearray())

    def pump(stream, buf):
        while chunk := stream.read1(16384):
            if len(buf) < cap:
                buf += chunk[: cap - len(buf)]

    def feed():
        try:
            proc.stdin.write(stdin)
            proc.stdin.close()
        except OSError:  # the program closed stdin early
            pass

    threads = [threading.Thread(target=pump, args=(proc.stdout, bufs[0]), daemon=True),
               threading.Thread(target=pump, args=(proc.stderr, bufs[1]), daemon=True),
               threading.Thread(target=feed, daemon=True)]
    for t in threads:
        t.start()
    timed_out = False
    try:
        proc.wait(timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except OSError:
            proc.kill()
        proc.wait()
    # a child the program left running may hold the pipes open for good; take what has arrived and move on
    for t in threads[:2]:
        t.join(2)
    code = proc.returncode if proc.returncode >= 0 else 128 - proc.returncode
    return code, bytes(bufs[0]), bytes(bufs[1]), timed_out


_ETC = ["/etc/passwd", "/etc/group", "/etc/nsswitch.conf", "/etc/hosts", "/etc/host.conf", "/etc/resolv.conf", "/etc/gai.conf",
        "/etc/ssl", "/etc/ca-certificates", "/etc/pki", "/etc/alternatives", "/etc/ld.so.cache", "/etc/ld.so.conf",
        "/etc/ld.so.conf.d", "/etc/localtime", "/etc/mime.types", "/etc/ssh/ssh_config", "/etc/ssh/ssh_config.d"]


def bubblewrap(network: bool = False, read_only_paths: Iterable[str] = ()) -> Callable[[list[str], str], list[str]]:
    """A `launcher` that runs the program under bubblewrap (Linux). It sees only the mirror (read-write, at its real
    path, so HOME, the working directory and redirected arguments still resolve), a fresh private /tmp, /usr and the
    /bin and /lib directories, a few files from /etc needed to run and resolve names, the program itself and
    `read_only_paths`. Your home directory and the rest of the disk are not there; the network is off unless
    `network` (git clone/push need it). Raises if bwrap is missing or can't make a sandbox here."""
    bwrap = shutil.which("bwrap")
    if bwrap is None:
        raise FileNotFoundError("bwrap is not installed (apt install bubblewrap); it is needed for bubblewrap()")
    # fail now, not on the agent's first call: unprivileged user namespaces may be disabled (e.g. by AppArmor)
    probe = subprocess.run([bwrap, "--unshare-all", "--ro-bind", "/", "/", "true"], capture_output=True)
    if probe.returncode != 0:
        raise RuntimeError(f"bwrap cannot create a sandbox on this machine: {probe.stderr.decode(errors='replace').strip()}")
    extra = list(read_only_paths)

    def launcher(argv: Sequence[str], root: str) -> list[str]:
        a = [bwrap, "--unshare-all", "--die-with-parent", "--new-session", "--proc", "/proc", "--dev", "/dev",
             "--tmpfs", "/tmp", "--setenv", "TMPDIR", "/tmp", "--ro-bind", "/usr", "/usr"]
        if network:
            a.append("--share-net")
        # merged-/usr systems have /bin -> usr/bin; recreate the link rather than binding through it
        for d in ("/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32"):
            if os.path.islink(d):
                a += ["--symlink", os.readlink(d), d]
            elif os.path.isdir(d):
                a += ["--ro-bind", d, d]
        for path in [*_ETC, *extra, argv[0]]:
            if not path.startswith("/usr/"):
                a += ["--ro-bind-try", path, path]
        return a + ["--bind", root, root, "--", *argv]

    return launcher


def register_host_command(shell, name: str, program: str | None = None, *, description: str | None = None, **options) -> HostCommand:
    """Exposes a real host program to the agent as `name`. See the module docstring for what this does and risks.

    Options: workspace (virtual directory to mirror, default the agent's home), env (extra environment variables),
    timeout (seconds), exclude (file/directory names kept out of the sync in both directions), shared_directories
    (other virtual directories the program can see besides the workspace, default /tmp), launcher
    (callable(argv, mirror_root) -> argv, e.g. `bubblewrap()`), rewrite_paths, max_output.
    """
    cmd = HostCommand(shell, name, program or name, **options)
    cmd._restore = shell._register_replacing(name, cmd, description or f"{name} (host program)")
    return cmd
