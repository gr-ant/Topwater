"""Shell: snapshots, fork, save and load, and the start of a recording. See docs/architecture.md."""
from __future__ import annotations

from typing import TYPE_CHECKING

from ..vfs import utc_now
from .types import EXECUTABLE_STUB

if TYPE_CHECKING:
    from . import Shell
    from ..recording import ShellRecorder
    from ..snapshot import ShellSnapshot


SNAPSHOT_SET_OPTIONS = ("errexit", "pipefail", "nounset", "noglob", "xtrace", "noclobber")  # the set -o options a shell can change


class SessionPart:
    def snapshot(self) -> "ShellSnapshot":
        """Captures the machine (files and session) as an immutable ShellSnapshot. Call it between calls."""
        from ..snapshot import ShellSnapshot
        return ShellSnapshot(self._mounts.outside(self.fs.entries()), self._capture_state(), self.user, self.hostname, utc_now())

    def restore(self, snapshot: "ShellSnapshot") -> None:
        """Puts the machine back as `snapshot` captured it. This shell keeps its own registered commands (and their
        /usr/bin entries); the snapshot is unchanged and can be restored again."""
        self.fs.replace_all(snapshot.entries)
        self._mounts.restore(snapshot.state.get("mounts", []))
        self._apply_state(snapshot.state)
        self.hashed = {}  # the restored machine's programs may be elsewhere

    def fork(self) -> "Shell":
        """A new shell with a copy of this machine that shares nothing mutable with it. It shares the registered command
        handlers, except host programs (register_host_command), which are bound to this shell: register them again."""
        from ..host import HostCommand
        from . import Shell
        fork = Shell(self.user, self.hostname, max_loop_iterations=self.max_loop_iterations, max_call_duration=self.max_call_duration,
                     max_file_system_bytes=self.max_file_system_bytes, max_string_bytes=self.max_string_bytes,
                     max_extract_bytes=self.max_extract_bytes, max_extract_entries=self.max_extract_entries)
        fork.commands = {n: c for n, c in self.commands.items() if not isinstance(c.handler, HostCommand)}
        fork._builtin_names = None if self._builtin_names is None else set(self._builtin_names)
        fork._mounts.sources = dict(self._mounts.sources)  # the fork's mounts use the same sources
        fork.restore(self.snapshot())
        fork.umask = self.umask  # shortcut: snapshots do not hold the umask yet, so only fork() keeps it
        return fork

    def save(self, file) -> None:
        """Saves the machine to a path or binary file; same as snapshot().save(file)."""
        self.snapshot().save(file)

    @staticmethod
    def load(file, **options) -> "Shell":
        """A new shell from a saved snapshot (a path, binary file or bytes), checked as untrusted input (ShellSnapshot.load).
        `options` are Shell() arguments; user and hostname default to the snapshot's. Only the built-ins are registered:
        register your commands again, or restore into a shell you configured: shell.restore(ShellSnapshot.load(f, shell))."""
        from types import SimpleNamespace
        from ..snapshot import ShellSnapshot
        from . import Shell
        snap = ShellSnapshot.load(file, SimpleNamespace(**options))
        options.setdefault("user", snap.user)
        options.setdefault("hostname", snap.hostname)
        shell = Shell(**options)
        shell.restore(snap)
        return shell

    def start_recording(self, snapshot_every: int = 0, metadata: dict | None = None) -> "ShellRecorder":
        """Records every run()/run_async() call from now until stop(): the machine now is the start. snapshot_every=N keeps a
        checkpoint after every Nth call, so shell_at() jumps there without re-running from the start; the machine after
        the last call is always kept. Stop it before your own checks, which also go through run()."""
        from ..recording import ShellRecorder
        if self._recorder is not None:
            raise RuntimeError("this shell is already recording; stop that recording first")
        self._recorder = ShellRecorder(self, snapshot_every, metadata)
        return self._recorder

    def _capture_state(self) -> dict:
        self._epoch += 1  # the arrays are now shared with the snapshot: each side copies one before its next write
        return {
            "cwd": self._cwd, "variables": dict(self.env), "attributes": dict(self.var_attrs), "unexported": frozenset(self._unexported),
            "arrays": dict(self.arrays), "functions": dict(self.functions), "exported_functions": frozenset(self.exported_functions),
            "aliases": dict(self.aliases), "traps": dict(self.traps),
            "set": [o for o in SNAPSHOT_SET_OPTIONS if getattr(self, o)], "ifs_unset": self._ifs_unset, "shopt": sorted(self.shopts),
            "positional": list(self.positional), "dir_stack": list(self.dir_stack), "history": list(self.history),
            "last_exit_code": self.last_exit_code, "seconds_base": self._seconds_base, "last_arg": self._last_arg,
            "pipe_status": list(self._pipe_status), "getopts": tuple(self.getopts_state), "jobs": dict(self._jobs),
            "last_bg": self._last_bg, "next_pid": self._next_pid, "mounts": self._mounts.capture(),
        }

    def _apply_state(self, s: dict) -> None:
        """Copies a snapshot's session in, so the snapshot stays as it was, and lines /usr/bin up with this shell's commands."""
        self._epoch = max([self._epoch, *(a.epoch for a in s["arrays"].values())]) + 1
        self.env = dict(s["variables"])
        self.var_attrs = dict(s["attributes"])
        self._unexported = set(s["unexported"])
        self.arrays = dict(s["arrays"])
        self.functions = dict(s["functions"])
        self.exported_functions = set(s["exported_functions"])
        self.aliases = dict(s["aliases"])
        self.traps = dict(s["traps"])
        for o in SNAPSHOT_SET_OPTIONS:
            setattr(self, o, o in s["set"])
        self._ifs_unset = s["ifs_unset"]
        self.shopts = set(s["shopt"])
        self.positional = list(s["positional"])
        self.dir_stack = list(s["dir_stack"])
        self.history = list(s["history"])
        self.last_exit_code = s["last_exit_code"]
        self._seconds_base = s["seconds_base"]
        self._last_arg = s["last_arg"]
        self._pipe_status = list(s["pipe_status"])
        self.getopts_state = tuple(s["getopts"])
        self._jobs = dict(s["jobs"])
        self._last_bg = s["last_bg"]
        self._next_pid = s["next_pid"]
        self._variable_chars = sum(len(k) + len(v) for k, v in self.env.items()) + \
            sum(len(k) + len(v) for a in self.arrays.values() for k, v in a.items.items())
        self._cwd = s["cwd"] if self.fs.is_dir(s["cwd"]) else "/"
        # program files for exactly this shell's commands: the snapshot may come from a shell (or port) with others
        with self.fs.as_root():
            for d in ("/usr/bin/", "/bin/"):
                if self.fs.is_dir(d):
                    for name in self.fs.listdir(d):
                        if name not in self.commands and self.fs.is_file(d + name) and self.fs.read_text(d + name) == EXECUTABLE_STUB:
                            self.fs.delete(d + name)
            for name in self.commands:
                if not self.fs.is_file("/usr/bin/" + name):
                    self._write_program_file(name, self._builtin_names is not None and name in self._builtin_names)
