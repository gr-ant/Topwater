"""The simulated bash session. See docs/architecture.md."""
from __future__ import annotations

import asyncio
import io
import time
from datetime import datetime, timedelta, timezone
from typing import Any

from .. import bytetext
from .._spec import COMMANDS
from ..glob import _RUNNING
from ..parser import FunctionDef, ParseError, Parser, Word
from ..vfs import ShellError, VirtualFileSystem, utc_now
from .execution import ExecutionPart
from .expansion import ExpansionPart
from .invocation import InvocationPart
from .redirection import CappedWriter, RedirectionPart, SwitchWriter
from .session import SessionPart
from .types import (
    CallTimeout, EXECUTABLE_STUB, ExitSignal, Handler, LimitExceeded, LoopControl, RegisteredCommand, ReturnSignal,
    ShellArray, ShellResult, StdinBuffer, _Io,
)
from .variables import VariablesPart


_DEFAULT_SHOPTS = {"cmdhist", "complete_fullquote", "extquote", "force_fignore", "globasciiranges", "globskipdots", "hostcomplete",
                   "interactive_comments", "patsub_replacement", "progcomp", "promptvars", "sourcepath"}
# Ubuntu's base system, rewritten in our own words with the same effect: a login shell reads /etc/profile, which runs
# /etc/profile.d/*.sh; an interactive one reads ~/.bashrc; a non-interactive one (every tool call) reads neither
SYSTEM_DIRS = ("/boot", "/lib", "/lib64", "/media", "/mnt", "/proc/4242", "/proc/tty/driver", "/run/lock", "/sbin", "/srv", "/sys", "/etc/profile.d", "/etc/skel", "/usr/games", "/usr/include", "/usr/lib64", "/usr/libexec", "/usr/sbin", "/usr/src", "/usr/local/etc", "/usr/local/games", "/usr/local/include", "/usr/local/lib", "/usr/local/sbin", "/usr/local/share/man", "/usr/local/src", "/usr/share/base-files", "/usr/share/bash-completion", "/usr/share/common-licenses", "/usr/share/doc", "/usr/share/info", "/usr/share/locale", "/usr/share/man", "/usr/share/misc", "/usr/share/terminfo", "/var/backups", "/var/cache/apt/archives/partial", "/var/cache/debconf", "/var/cache/ldconfig", "/var/lib/apt", "/var/lib/dpkg", "/var/lib/misc", "/var/lib/pam", "/var/lib/systemd", "/var/local", "/var/mail", "/var/opt", "/var/spool")
OS_RELEASE = "PRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nNAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\nVERSION=\"24.04.1 LTS (Noble Numbat)\"\nVERSION_CODENAME=noble\nID=ubuntu\nID_LIKE=debian\nHOME_URL=\"https://www.ubuntu.com/\"\nSUPPORT_URL=\"https://help.ubuntu.com/\"\nBUG_REPORT_URL=\"https://bugs.launchpad.net/ubuntu/\"\nPRIVACY_POLICY_URL=\"https://www.ubuntu.com/legal/terms-and-policies/privacy-policy\"\nUBUNTU_CODENAME=noble\nLOGO=ubuntu-logo\n"
ETC_PROFILE = "# Read by every login shell (bash -l, sh -l).\n\n# interactive bash: the shared settings in /etc/bash.bashrc\nif [ \"${PS1-}\" ] && [ \"${BASH-}\" ] && [ -f /etc/bash.bashrc ]; then\n  . /etc/bash.bashrc\nfi\n\n# then each script in /etc/profile.d\nif [ -d /etc/profile.d ]; then\n  for i in /etc/profile.d/*.sh; do\n    [ -r \"$i\" ] && . \"$i\"\n  done\n  unset i\nfi\n"
ETC_BASHRC = "# Shared settings for interactive bash shells.\n\n# nothing to do for scripts and bash -c\n[ -z \"${PS1-}\" ] && return\n\nshopt -s checkwinsize\nPS1='\\u@\\h:\\w\\$ '\n"
DOT_PROFILE = "# Read by login shells, unless ~/.bash_profile or ~/.bash_login exists.\n\n# bash: also read ~/.bashrc\nif [ -n \"$BASH_VERSION\" ] && [ -f \"$HOME/.bashrc\" ]; then\n    . \"$HOME/.bashrc\"\nfi\n\n# your own programs, when you have them\nfor d in \"$HOME/bin\" \"$HOME/.local/bin\"; do\n    [ -d \"$d\" ] && PATH=\"$d:$PATH\"\ndone\nunset d\n"
DOT_BASH_LOGOUT = "# Run when a login shell exits.\n"
DOT_BASHRC = "# Settings for interactive bash shells.\n\n# scripts and bash -c stop here\ncase $- in\n    *i*) ;;\n      *) return;;\nesac\n\n# history: skip duplicates and lines that start with a space\nHISTCONTROL=ignoreboth\nHISTSIZE=1000\nHISTFILESIZE=2000\nshopt -s histappend checkwinsize\n\nPS1='\\u@\\h:\\w\\$ '\n\n# colour and shorthands\nalias ls='ls --color=auto'\nalias grep='grep --color=auto'\nalias ll='ls -alF'\nalias la='ls -A'\nalias l='ls -CF'\n\n[ -f ~/.bash_aliases ] && . ~/.bash_aliases\n"


class Shell(SessionPart, ExecutionPart, RedirectionPart, InvocationPart, VariablesPart, ExpansionPart):
    """A simulated bash session. Give the model a single "bash" tool and pass its command to run()/run_async().

    Shell state (cwd, environment, aliases, functions, files) persists between calls.

    Limits: max_loop_iterations bounds any single loop (and 10x that all loops in one call); max_call_duration (seconds)
    ends a call with exit code 124; max_file_system_bytes caps what files may hold ("No space left on device");
    max_string_bytes caps one string (an output buffer, a variable, an expansion) and all variables together;
    max_extract_bytes / max_extract_entries bound one archive extraction.
    """

    # ---------- state and constructor ----------

    def __init__(self, user: str = "agent", hostname: str = "topwater", fs: VirtualFileSystem | None = None,
                 max_loop_iterations: int = 100_000, *, max_call_duration: float = 120.0,
                 max_file_system_bytes: int = 128 << 20, max_string_bytes: int = 32 << 20,
                 max_extract_bytes: int = 64 << 20, max_extract_entries: int = 100_000):
        self.user = user
        self.hostname = hostname
        self.fs = fs or VirtualFileSystem()
        self.max_loop_iterations = max_loop_iterations
        self.max_call_duration = max_call_duration
        self.max_file_system_bytes = max_file_system_bytes
        self.max_string_bytes = max_string_bytes
        self.max_extract_bytes = max_extract_bytes
        self.max_extract_entries = max_extract_entries
        self.fs.capacity = max_file_system_bytes
        self.commands: dict[str, RegisteredCommand] = {}
        self.history: list[str] = []
        self.aliases: dict[str, str] = {}
        self.functions: dict[str, FunctionDef] = {}
        self.hashed: dict[str, list] = {}  # bash's hash table: name -> [path found on PATH, lookups since]
        self._hashed_for: str | None = None  # the PATH the table was filled from: any change to PATH empties it
        self.positional: list[str] = []
        self.errexit = self.pipefail = self.nounset = self.noglob = self.xtrace = self.noclobber = False
        self.shopts: set[str] = set(_DEFAULT_SHOPTS)
        self.traps: dict[str, str] = {}  # EXIT runs when the call (or script) ends, ERR after a failure, RETURN, DEBUG
        self.last_exit_code = 0
        self.var_attrs: dict[str, str] = {}  # declare attributes: i integer, r readonly, l/u case, n nameref, x exported
        self.arrays: dict[str, ShellArray] = {}
        self.source_depth = 0
        self.fds: dict[int, Any] = {}  # fds 3+ opened by `exec 3>file` / `exec 3<file`
        self.dir_stack: list[str] = []
        self.getopts_state = (0, 1)
        self.exported_functions: set[str] = set()
        self.loop_depth = 0
        self._locals: list[dict[str, tuple[str | None, str | None]]] = []
        self._local_arrays: list[dict[str, ShellArray | None]] = []
        self._epoch = 0
        self._cond_depth = 0
        self._ifs_unset = False
        self._subshell = self._lineno = self._last_bg = 0
        self._line_base = 0  # added to the line numbers of the text running now (eval and $(...) text start at 1)
        self._cur_word: Word | None = None  # the word being expanded, for what a bad substitution names
        self.umask = 0o022  # `umask`: taken from the modes of the files and directories the agent creates
        self._next_pid = 4300
        self._last_arg = ""
        self._pipe_status: list[int] = []
        self._func_names: list[str] = []
        self._sources: list[str] = []
        self._err_where: list[tuple[str, bool]] = []  # whose name starts bash's own errors: a script file, bash -c, a function's origin
        self._func_where: dict[str, str] = {}
        self._arg0 = "bash"
        self._jobs: dict[int, int] = {}
        self._seconds_base = time.time()
        self._unexported: set[str] = set()
        self._variable_chars = 0
        self._total_iterations = self._script_depth = self._arith_depth = 0
        self._in_err_trap = self._in_debug_trap = False
        self._deadline = float("inf")
        self._param_words: dict = {}
        self._recorder = None
        self._web = None  # allow_web(): the web access curl and wget get; None: no network
        from ..mounts import Mounts
        self._mounts = Mounts(self)  # mount(): outside sources shown as directories
        home = f"/home/{user}"
        for d in (home, "/tmp", "/var/tmp", "/usr/bin", "/bin", "/usr/local/bin", "/opt", "/etc", "/dev/fd", "/var/log"):
            self.fs.mkdir(d, parents=True)
        if not self.fs.exists("/etc/os-release"):
            self.fs.mkdir("/usr/lib", parents=True)
            self.fs.write_text("/usr/lib/os-release", OS_RELEASE)
            self.fs.create_symlink("/etc/os-release", "../usr/lib/os-release")
        self.fs.write_text("/etc/hostname", hostname + "\n")
        if not self.fs.exists("/etc/issue"):
            self.fs.write_text("/etc/issue", "Ubuntu 24.04.1 LTS \\n \\l\n\n")
        if not self.fs.exists("/etc/issue.net"):
            self.fs.write_text("/etc/issue.net", "Ubuntu 24.04.1 LTS\n")
        if not self.fs.exists("/etc/passwd"):
            self.fs.write_text("/etc/passwd", f"root:x:0:0:root:/root:/bin/bash\n{user}:x:1000:1000:{user}:{home}:/bin/bash\n")
        self.fs.write_text("/dev/null", "")
        self._seed_system_files(user, home)
        # a regular (non-root) account: the agent owns its home; system areas belong to root
        self.fs.mkdir("/root", parents=True)
        self.fs.chmod("/root", 0o700)
        self.fs.set_owner(home, user, recursive=True)
        self.fs.chmod(home, 0o750)
        for d in ("/tmp", "/var/tmp"):
            self.fs.chmod(d, 0o1777)
        self.fs.chmod("/dev/null", 0o666)
        self.env: dict[str, str] = {
            "HOME": home, "USER": user, "LOGNAME": user, "HOSTNAME": hostname, "SHELL": "/bin/bash",
            "PATH": "/usr/local/bin:/usr/bin:/bin", "LANG": "C.UTF-8", "TERM": "xterm-256color",
        }
        self._cwd = "/"
        self.cwd = home
        self._builtin_names: set[str] | None = None
        self._proc_sub_count = 0
        from ..commands import register_all

        register_all(self)
        self._builtin_names = set(self.commands)
        # the system was "installed" months ago; only the agent's home and the temp directories are fresh
        for d in ("/bin", "/usr", "/etc", "/dev", "/opt", "/root", "/var/log", "/var/lib", "/var/cache", "/var/backups",
                  "/var/local", "/var/mail", "/var/opt", "/var/spool", "/run", "/boot", "/lib", "/lib64", "/media", "/mnt", "/sbin", "/srv"):
            self.fs.set_modified(d, self.installed_at, recursive=True)
        for link in ("/var/run", "/var/lock"):
            if self.fs.is_symlink(link):
                self.fs.set_modified_time(link, self.installed_at, follow=False)
        for p in ("/", "/home", "/var", home + "/.bashrc", home + "/.profile", home + "/.bash_logout"):
            self.fs.set_modified(p, self.installed_at)

    @property
    def installed_at(self) -> datetime:
        today = datetime.now(timezone.utc).replace(tzinfo=None, hour=0, minute=0, second=0, microsecond=0)
        return today - timedelta(days=142) + timedelta(hours=9, minutes=13)

    def _seed_system_files(self, user: str, home: str) -> None:
        """The ordinary clutter of a Linux install. A model that looks around and finds four files in /etc, no dotfiles
        and no /proc starts wondering what kind of machine it is on, and spends its turns investigating."""
        for d in SYSTEM_DIRS:
            self.fs.mkdir(d, parents=True)
        # what an ordinary user can't read, so `find /` reports "Permission denied" and exits 1 as on Ubuntu
        for d in ("/var/cache/ldconfig", "/var/cache/apt/archives/partial", "/proc/tty/driver"):
            self.fs.chmod(d, 0o700)
        self.fs.chmod("/run/lock", 0o1777)

        def link(path: str, target: str) -> None:
            if not self.fs.exists(path) and not self.fs.is_symlink(path):
                self.fs.create_symlink(path, target)

        link("/var/run", "/run")
        link("/var/lock", "/run/lock")
        link("/usr/local/man", "share/man")

        def seed(path: str, content: str) -> None:
            if not self.fs.exists(path):
                self.fs.write_text(path, content)

        h = self.hostname
        seed("/etc/hosts", f"127.0.0.1\tlocalhost\n127.0.1.1\t{h}\n\n::1\tip6-localhost ip6-loopback\nfe00::0\tip6-localnet\n"
                           "ff02::1\tip6-allnodes\nff02::2\tip6-allrouters\n")
        seed("/etc/group", f"root:x:0:\nadm:x:4:\nsudo:x:27:\nusers:x:100:\n{user}:x:1000:\n")
        seed("/etc/shells", "# Shells a user may have as a login shell\n/bin/sh\n/usr/bin/sh\n/bin/bash\n/usr/bin/bash\n/bin/rbash\n/usr/bin/rbash\n/usr/bin/dash\n")
        seed("/etc/debian_version", "trixie/sid\n")
        seed("/var/lib/shells.state", "")
        seed("/etc/profile", ETC_PROFILE)
        seed("/etc/bash.bashrc", ETC_BASHRC)
        seed("/etc/profile.d/01-locale-fix.sh", '# Use the C.UTF-8 locale when none is set.\nexport LANG="${LANG:-C.UTF-8}"\n')
        seed("/etc/timezone", "Etc/UTC\n")
        seed("/etc/resolv.conf", "nameserver 127.0.0.53\noptions edns0 trust-ad\n")
        seed("/etc/lsb-release", 'DISTRIB_ID=Ubuntu\nDISTRIB_RELEASE=24.04\nDISTRIB_CODENAME=noble\nDISTRIB_DESCRIPTION="Ubuntu 24.04.1 LTS"\n')
        seed("/etc/fstab", "# <file system> <mount point> <type> <options> <dump> <pass>\n/dev/sda1\t/\text4\tdefaults\t0 1\n")
        # these agree with what uname, nproc, free, df and uptime print
        seed("/proc/version", "Linux version 6.8.0-generic (buildd@lcy02-amd64-047) (x86_64-linux-gnu-gcc-13 (Ubuntu 13.2.0-23ubuntu4) "
                              "13.2.0, GNU ld (GNU Binutils for Ubuntu) 2.42) #1 SMP PREEMPT_DYNAMIC\n")
        seed("/proc/cpuinfo", "".join(
            f"processor\t: {i}\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t\t: 85\nmodel name\t: Intel(R) Xeon(R) Platinum 8259CL "
            f"CPU @ 2.50GHz\ncpu MHz\t\t: 2499.998\ncache size\t: 36608 KB\nphysical id\t: 0\nsiblings\t: 4\ncore id\t\t: {i // 2}\n"
            "cpu cores\t: 2\nbogomips\t: 4999.99\n\n" for i in range(4)))
        seed("/proc/meminfo", "MemTotal:        8014880 kB\nMemFree:         3984512 kB\nMemAvailable:    5748660 kB\nBuffers:          182272 kB\n"
                              "Cached:          2018752 kB\nSwapTotal:       2097148 kB\nSwapFree:        2097148 kB\nShmem:             12288 kB\n")
        seed("/proc/mounts", "/dev/sda1 / ext4 rw,relatime 0 0\nproc /proc proc rw,nosuid,nodev,noexec,relatime 0 0\n"
                             "sysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0\ntmpfs /dev/shm tmpfs rw,nosuid,nodev 0 0\n")
        seed("/proc/uptime", "274320.42 1082431.77\n")
        seed("/proc/loadavg", "0.08 0.03 0.01 1/142 4242\n")
        # the shell's own process: /proc/self points at it ($$ is 4242)
        uid = 0 if user == "root" else 1000
        seed("/proc/4242/comm", "bash\n")
        seed("/proc/4242/cmdline", "bash\0")
        seed("/proc/4242/status", f"Name:\tbash\nState:\tS (sleeping)\nPid:\t4242\nPPid:\t1\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\n"
                                  f"Gid:\t{uid}\t{uid}\t{uid}\t{uid}\n")
        link("/proc/self", "4242")
        for d in ("/etc/skel", home):
            seed(d + "/.profile", DOT_PROFILE)
            seed(d + "/.bash_logout", DOT_BASH_LOGOUT)
            seed(d + "/.bashrc", DOT_BASHRC)

    @property
    def cwd(self) -> str:
        return self._cwd

    @cwd.setter
    def cwd(self, value: str) -> None:
        path = self.resolve(value)
        if not self.fs.is_dir(path):
            raise ShellError(f"{value}: No such file or directory")
        self._cwd = path
        self.env["PWD"] = path

    def resolve(self, path: str) -> str:
        """An absolute path for `path` from the cwd; ".." goes up from where the path really is, past symbolic links, as the kernel resolves it."""
        return self.fs.normalize_physical(self._cwd, path)

    @property
    def err_prefix(self) -> str:
        """bash's prefix for its own errors: "bash: line 3: ", "./s.sh: line 2: ", or "environment: line 1: " in a
        function defined in a bash -c string."""
        return f"{self._err_where[-1][0] if self._err_where else 'bash'}: line {max(self._lineno, 1)}: "

    # ---------- clock and random ----------

    # nothing: Python has no virtual clock yet

    # ---------- registration ----------

    def register_command(self, name: str, handler: Handler, description: str = "", usage: str | None = None) -> "Shell":
        """Registers a handler(ctx) -> exit code (sync or async)."""
        if self._builtin_names is None and not description and usage is None and name in COMMANDS:
            description, usage = COMMANDS[name].summary, COMMANDS[name].help  # built-ins: from spec/ (_spec.py)
        self.commands[name] = RegisteredCommand(name, description, usage, handler)
        if self._builtin_names is not None:
            self._builtin_names.discard(name)  # an app command replacing a built-in owns its own --help/--version
        self._write_program_file(name, self._builtin_names is None)
        return self

    def _write_program_file(self, name: str, builtin: bool) -> None:
        spec = COMMANDS.get(name)
        if not (builtin and spec is not None and not spec.program_file) and "/" not in name and name not in (".", "..", ":") and self.fs.is_dir("/usr/bin"):
            with self.fs.as_root():
                for d in ("/usr/bin/", "/bin/"):
                    if self.fs.is_dir(d):
                        self.fs.write_text(d + name, EXECUTABLE_STUB)
                        self.fs.chmod(d + name, 0o755)
                        self.fs.set_modified(d + name, self.installed_at)
                        # shortcut: a stub is 16 bytes but reports a plausible program size; `wc -c` on it would disagree
                        h = 17
                        for c in name:
                            h = (h * 31 + ord(c)) & 0xFFFFFFFF
                        self.fs.set_reported_size(d + name, 14_000 + h % 180_000 // 8 * 8)
                        self.fs.set_modified(d, self.installed_at)

    def register(self, *targets: Any) -> "Shell":
        """Registers @command-decorated functions, or every @command method on an object, class or module."""
        from ..binder import register_targets

        register_targets(self, targets)
        return self

    def _register_replacing(self, name: str, handler: Handler, description: str):
        """Registers handler and returns what puts back the command it replaced (none: unregisters), unless something
        else has been registered under the name since. HostCommand.close uses it."""
        previous = self.commands.get(name)
        builtin = self._builtin_names is not None and name in self._builtin_names
        self.register_command(name, handler, description)

        def restore() -> None:
            now = self.commands.get(name)
            if now is None or now.handler is not handler:
                return
            if previous is None:
                self.unregister(name)
                return
            self.commands[name] = previous
            if builtin:
                self._builtin_names.add(name)
        return restore

    def allow_web(self, policy) -> "Shell":
        """Lets the agent's curl and wget reach the hosts in `policy` (a WebPolicy), over https unless it allows http. Every
        URL and redirect is checked against it; names are resolved once, private, loopback, link-local and other internal
        addresses are refused, and the connection goes to the address that was checked. A fork does not inherit it."""
        from ..webaccess import WebSession
        self._web = WebSession(policy)
        return self

    def allow_python(self, options=None) -> "Shell":
        """Gives the agent python3: CPython 3.14 compiled to WebAssembly, over this machine's files and nothing else
        (docs/python.md). Needs the add-on: pip install 'topwater[python]'. options: a PythonOptions (memory, artifacts
        folder, compile cache). Compiles the interpreter on the first use in a process (seconds) and raises
        PythonUnavailable here when it can't run. HTTP from Python goes through allow_web's policy, or nowhere."""
        from ..commands import python
        python.register(self, options or python.PythonOptions())
        self._builtin_names.add("python3")  # a built-in from spec/: its --help and --version are the spec's
        return self

    def mount(self, path: str, source, mode: str = "read-only") -> None:
        """Shows `source` (a MountSource) as the directory `path`: mode "read-only", "staged" or "live" (docs/mounts.md).
        Lists the source before it returns. Synchronous wrapper for mount_async."""
        asyncio.run(self.mount_async(path, source, mode))

    async def mount_async(self, path: str, source, mode: str = "read-only") -> None:
        await self._mounts.mount(path, source, mode)

    def unmount(self, path: str) -> None:
        """Removes a mount and its pending changes; the mount point stays as an empty directory."""
        self._mounts.unmount(path)

    @property
    def mounts(self) -> list:
        """Every mount as a MountInfo (path, mode, source type and id, why it is unavailable)."""
        return [m.info() for m in self._mounts.items.values()]

    def pending_changes(self, path: str) -> list:
        """The changes (MountChange) that would make a staged or live mount's source match its files, in order."""
        return self._mounts.get(path).changes()

    def apply_changes(self, path: str, changes: list | None = None):
        """Applies the pending changes, or the ones given (from pending_changes), in order, stopping at the first conflict
        or failure; returns a MountApplyResult. Synchronous wrapper for apply_changes_async."""
        return asyncio.run(self.apply_changes_async(path, changes))

    async def apply_changes_async(self, path: str, changes: list | None = None):
        return await self._mounts.get(path).apply(changes)

    def discard_changes(self, path: str) -> None:
        """Drops a mount's pending changes, and the ones a loaded snapshot brought: its files are the source's again."""
        self._mounts.discard(path)

    def snapshot_changes(self, path: str) -> list:
        """The changes a snapshot loaded from a file brought for this mount (as untrusted as the file): not in the files,
        never applied, until accept_snapshot_changes."""
        return list(self._mounts.get(path).held)

    def accept_snapshot_changes(self, path: str) -> None:
        """Puts a loaded snapshot's changes into the mount's files, where they become pending changes like the agent's.
        Synchronous wrapper for accept_snapshot_changes_async."""
        asyncio.run(self.accept_snapshot_changes_async(path))

    async def accept_snapshot_changes_async(self, path: str) -> None:
        await self._mounts.accept(path)

    def refresh_mount(self, path: str) -> None:
        """Lists the source again, keeping the pending changes. Synchronous wrapper for refresh_mount_async."""
        asyncio.run(self.refresh_mount_async(path))

    async def refresh_mount_async(self, path: str) -> None:
        await self._mounts.refresh(path)

    def unregister(self, name: str) -> bool:
        with self.fs.as_root():
            for d in ("/usr/bin/", "/bin/"):
                if self.fs.is_file(d + name):
                    self.fs.delete(d + name)
        return self.commands.pop(name, None) is not None

    # ---------- run ----------

    def run(self, command_line: str) -> ShellResult:
        """Synchronous wrapper for run_async (do not call from inside a running event loop)."""
        return asyncio.run(self.run_async(command_line))

    async def run_async(self, command_line: str) -> ShellResult:
        started, clock = utc_now(), time.perf_counter()
        result = await self._run_call(command_line)
        if self._recorder is not None:
            self._recorder._add(command_line, result, (time.perf_counter() - clock) * 1000, started,
                                self._web.take_log() if self._web is not None else None, self._mounts.take_log())
        return result

    async def _run_call(self, command_line: str) -> ShellResult:
        self.history.append(command_line)
        out, err = CappedWriter(self), CappedWriter(self)
        trailer = None
        previous_user, self.fs.run_as = self.fs.run_as, self.user  # the agent's commands run as a regular user
        self.fs.umask = self.umask
        self._total_iterations = self._script_depth = self._arith_depth = 0
        self._lineno = self._line_base = 0  # each call is a new bash -c: its lines count from 1
        self._deadline = time.monotonic() + self.max_call_duration
        if self._web is not None:
            self._web.begin_call()
        io_ = _Io(StdinBuffer(), SwitchWriter(out), SwitchWriter(err))

        def stop_err(msg: str) -> None:
            err.seek(0, io.SEEK_END)
            io.StringIO.write(err, msg)  # past the cap on purpose: the note must get through

        running = _RUNNING.set(self)
        try:
            try:
                if self._mounts.items:
                    await self._mounts.prepare()
                code = await self._exec_script(Parser.parse(command_line), io_, top=True)
            finally:
                # shortcut: EXIT traps fire when the tool call ends (like `set -e`, they last one call), not at logout
                if time.monotonic() < self._deadline:
                    await self._run_exit_trap(io_)
                if self._mounts.items and time.monotonic() < self._deadline:
                    await self._mounts.settle(io_.err)
        except ParseError as e:
            stop_err(f"bash: {e}\n")
            code = 2
        except (ExitSignal, ReturnSignal) as e:
            code = e.code
        except LoopControl:
            code = 0
        except LimitExceeded as e:
            stop_err(f"bash: {e.message}\n")
            code, trailer = 1, _stopped(e.message)
        except CallTimeout:
            code = 124  # what `timeout` exits with
            trailer = _stopped(f"this call exceeded the {_seconds(self.max_call_duration)} time limit; split the work into shorter calls")
        except RecursionError:
            stop_err("bash: maximum nesting level exceeded\n")
            code, trailer = 1, _stopped("maximum nesting level exceeded")
        except (asyncio.CancelledError, KeyboardInterrupt):
            raise
        except Exception as e:  # an interpreter bug must not take down the host's agent loop
            stop_err(f"bash: internal error: {type(e).__name__}: {e}\n")
            code = 1
        finally:
            _RUNNING.reset(running)
            # shortcut: `set -e` / `set -o pipefail` last for one tool call, so a stray `set -e` can't poison the session.
            self.errexit = self.pipefail = self.nounset = self.noglob = self.xtrace = self.noclobber = False
            self.fds.clear()
            self._deadline = float("inf")
            self.fs.run_as = previous_user
            if self._proc_sub_count and self.fs.is_dir("/dev/fd"):
                with self.fs.as_root():
                    for f in self.fs.listdir("/dev/fd"):
                        self.fs.delete("/dev/fd/" + f, recursive=True)
            self._proc_sub_count = 0
        self.last_exit_code = code
        return ShellResult(bytetext.printable(out.getvalue()), bytetext.printable(err.getvalue()), code, trailer)

    # ---------- limits ----------

    def too_large(self, what: str, instead: str, limit: int | None = None) -> LimitExceeded:
        return LimitExceeded(f"{what} too large (limit {size_text(self.max_string_bytes if limit is None else limit)}); {instead}")

    def charge_variable(self, name: str, adding: int, replacing: int) -> None:
        """Called before a variable or element takes a value of `adding` characters in place of `replacing`."""
        mx = self.max_string_bytes
        if adding > mx:
            raise self.too_large(f"{name}: value", "write the data to a file instead")
        self._variable_chars += adding
        if self._variable_chars <= mx:
            return
        self._variable_chars = (sum(len(k) + len(v) for k, v in self.env.items())
                                + sum(len(k) + len(v) for a in self.arrays.values() for k, v in a.items.items()) - replacing + adding)
        if self._variable_chars > mx:
            raise self.too_large("variables in total", "keep big data in files and unset variables you no longer need")

    def check_deadline(self) -> None:
        if time.monotonic() >= self._deadline:
            raise CallTimeout()


def size_text(n: int) -> str:
    """"32 MB", "512 KB" or "1000 bytes": a limit as messages print it."""
    return f"{n >> 20} MB" if n % (1 << 20) == 0 else f"{n >> 10} KB" if n % 1024 == 0 else f"{n} bytes"


def _seconds(t: float) -> str:
    return f"{t:.3f}".rstrip("0").rstrip(".") + " s"


def _stopped(why: str) -> str:
    return f"[stopped: {why}. Running it again will hit the same limit. Files and variables changed before this point are kept.]"


# ---------- re-exports ----------

# every name that `from topwater.shell import ...` reached before the split: a host may import any of them
import functools  # noqa: F401
import inspect  # noqa: F401
import random  # noqa: F401
import re  # noqa: F401
import signal  # noqa: F401
import threading  # noqa: F401
from contextvars import ContextVar  # noqa: F401
from dataclasses import dataclass, replace  # noqa: F401
from typing import Awaitable, Callable, Union  # noqa: F401

from .. import arith  # noqa: F401
from .._spec import DIAGNOSTICS, ERRNO, diagnostic  # noqa: F401
from ..glob import (  # noqa: F401
    _MAX_BRACE, _PLACEHOLDER, TimedPattern, _brace_alternatives, _brace_close, _brace_text, _bracket, _bracket_end, _cached,
    _glob_body, _group_end, _has_unescaped_wildcard, _join, _rebuild, brace_expand, glob_escape, glob_expand, glob_to_regex,
    glob_to_regex_body, has_wildcard,
)
from ..parser import (  # noqa: F401
    ArithCommand, ArithForCommand, ArithSub, Assignment, CaseCommand, CommandSub, CondCommand, ExpandedText, ForCommand,
    GroupCommand, IfCommand, Lexer, Literal, LoopCommand, Pipeline, ProcSub, Script, SimpleCommand, Variable, is_cond_binary,
    is_name, split_subscript, try_assignment,
)
from ..vfs import UsageException, get_name, get_parent, normalize  # noqa: F401
from .execution import _CondEvaluator  # noqa: F401
from .expansion import _ASSIGNMENT_TILDE, _INDIRECT, _PARAM, _PREFIX, _Field, _arith_nesting, _read_file_word, split_fields  # noqa: F401
from .invocation import _BAD_INT, quote_value, trace_quote  # noqa: F401
from .redirection import FileAppender, FileSink, _with_fd  # noqa: F401
from .session import SNAPSHOT_SET_OPTIONS  # noqa: F401
from .types import CommandContext, _BadSubstitution, _ControlFlow, _LineAbort, _NewProcess, _Null, default_op  # noqa: F401
from .variables import _BASH_VERSINFO, _SCRATCH, _unquote, array_ref  # noqa: F401
