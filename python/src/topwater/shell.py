"""The simulated bash session."""
from __future__ import annotations

import asyncio
import functools
import inspect
import io
import random
import re
import signal
import threading
import time
from dataclasses import dataclass, replace
from datetime import datetime, timedelta, timezone
from typing import Any, Awaitable, Callable, Union

from . import arith, bytetext
from ._spec import COMMANDS, DIAGNOSTICS, ERRNO, diagnostic
from .parser import (
    ArithCommand, ArithForCommand, ArithSub, Assignment, CaseCommand, CommandSub, CondCommand, ExpandedText, ForCommand,
    FunctionDef, GroupCommand, IfCommand, Lexer, Literal, LoopCommand, Parser, ParseError, Pipeline, ProcSub, Script,
    SimpleCommand, Variable, Word, is_cond_binary, is_name, split_subscript, try_assignment,
)
from .vfs import ShellError, UsageException, VirtualFileSystem, get_name, get_parent, normalize, utc_now

_BAD_INT = re.compile(r"invalid literal for int\(\) with base \d+: '(.*)'$", re.S)
EXECUTABLE_STUB = "\x7fELF\x02\x01\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00"


@dataclass(frozen=True)
class ShellResult:
    stdout: str
    stderr: str
    exit_code: int
    trailer: str | None = None  # set when a safety limit ended the call: a note to_tool_output always shows last, never cut

    def to_tool_output(self, max_chars: int = 30_000) -> str:
        """Formats the result as the text returned to the model from the bash tool."""
        # each stream is cut on its own, so a long stdout can't push the error message and exit code out of view
        def clip(s: str, n: int) -> str:
            return s if len(s) <= n else s[:n] + f"\n... [output truncated, {len(s) - n} more characters]\n"

        text = clip(self.stdout, max_chars) + clip(self.stderr, max(1, max_chars // 6))
        if self.exit_code != 0:
            text += ("" if not text or text.endswith("\n") else "\n") + f"[exit code {self.exit_code}]"
        if self.trailer is not None:
            text += ("" if not text or text.endswith("\n") else "\n") + self.trailer
        return text or "(no output)"


Handler = Callable[["CommandContext"], Union[int, None, Awaitable[Union[int, None]]]]


@dataclass(frozen=True)
class RegisteredCommand:
    name: str
    description: str
    usage: str | None
    handler: Handler


class _Null(io.StringIO):
    def write(self, s: str) -> int:
        return len(s)


class StdinBuffer:
    """A command's stdin. Shared by every command in a loop body so `while read` consumes it line by line."""

    def __init__(self, text: str = ""):
        self.text = text
        self.pos = 0
        self.last_terminated = True  # False when read_line hit end of input before the delimiter

    def reset(self, text: str) -> None:
        """`exec <file`: the shell's stdin becomes the file's contents."""
        self.text, self.pos = text, 0

    def read_all(self) -> str:
        r = self.text[self.pos:]
        self.pos = len(self.text)
        return r

    def read_line(self, delimiter: str = "\n") -> str | None:
        if self.pos >= len(self.text):
            return None
        nl = self.text.find(delimiter, self.pos)
        line = self.text[self.pos:] if nl < 0 else self.text[self.pos:nl]
        self.pos = len(self.text) if nl < 0 else nl + 1
        self.last_terminated = nl >= 0
        return line

    def read_chars(self, count: int, delimiter: str | None) -> str | None:
        """read -n / -N: up to count characters, stopping after the delimiter unless it is None."""
        if self.pos >= len(self.text):
            return None
        end = min(len(self.text), self.pos + count)
        stop = self.text.find(delimiter, self.pos, end) if delimiter is not None else -1
        chunk = self.text[self.pos:stop if stop >= 0 else end]
        self.pos = stop + 1 if stop >= 0 else end
        self.last_terminated = stop >= 0 or len(chunk) == count
        return chunk


@dataclass(frozen=True)
class _Io:
    stdin: StdinBuffer
    out: Any
    err: Any
    fds: dict | None = None  # fds 3 and up redirected for one command (3>&1); None when none are


class _BadSubstitution(ShellError):
    """${...} with an operator bash does not know; the word expanding it turns this into the fatal error."""

    def __init__(self, expr: str):
        super().__init__(diagnostic("badSubstitution", text="${" + expr + "}"))


class _ControlFlow(Exception):
    pass


class LoopControl(_ControlFlow):
    def __init__(self, is_break: bool, levels: int):
        super().__init__()
        self.is_break, self.levels = is_break, levels


class ReturnSignal(_ControlFlow):
    def __init__(self, code: int):
        super().__init__()
        self.code = code


class ExitSignal(_ControlFlow):
    def __init__(self, code: int):
        super().__init__()
        self.code = code


class LimitExceeded(_ControlFlow):
    """A safety limit was hit; ends the whole tool call so an outer loop or caller can't carry on."""

    def __init__(self, message: str):
        super().__init__(message)
        self.message = message


class CallTimeout(_ControlFlow):
    """The call's (or a `timeout` command's) deadline passed."""


class CommandContext:
    """Everything a command needs: its arguments, stdio, and the shell it runs in."""

    def __init__(self, shell: "Shell", name: str, args: list[str], stdin: StdinBuffer, stdout, stderr, fds: dict | None = None):
        self.shell = shell
        self.name = name
        self.args = args
        self._stdin = stdin
        self._stdin_text: str | None = None
        self.stdout = stdout
        self.stderr = stderr
        self.fds = fds  # fds 3 and up this command was given (`read -u 3 x 3<file`)

    @property
    def stdin(self) -> str:
        """Everything piped or redirected in ("" when nothing was). Reading it consumes stdin."""
        if self._stdin_text is None:
            self._stdin_text = self._stdin.read_all()
        return self._stdin_text

    def read_line(self, delimiter: str = "\n") -> str | None:
        """Reads one line of stdin (without the delimiter), or None at end of input."""
        return self._stdin.read_line(delimiter)

    @property
    def stdin_buffer(self) -> StdinBuffer:
        return self._stdin

    @property
    def io(self) -> _Io:
        return _Io(self._stdin, self.stdout, self.stderr, self.fds)

    @property
    def fs(self) -> VirtualFileSystem:
        return self.shell.fs

    def resolve(self, path: str) -> str:
        return self.shell.resolve(path)

    def print(self, *values: Any, end: str = "\n") -> None:
        self.stdout.write(" ".join(str(v) for v in values) + end)

    def error(self, *values: Any) -> None:
        self.stderr.write(" ".join(str(v) for v in values) + "\n")

    def check_time(self) -> None:
        """Long-running commands call this in their loops so the call's time limit (and `timeout`) can stop them."""
        self.shell.check_deadline()

    async def invoke(self, argv: list[str], stdin: str = "") -> int:
        """Runs another command with this command's stdout/stderr (used by xargs)."""
        return await self.shell._invoke(argv, _Io(StdinBuffer(stdin), self.stdout, self.stderr))


class ShellArray:
    """A bash array. Indexed arrays keep their elements in index order; associative ones in insertion order."""

    def __init__(self, assoc: bool, epoch: int = 0):
        self.assoc = assoc
        self.items: dict[str, str] = {}
        self.next = 0  # the index `+=(x)` appends at: one past the highest index ever set
        self.epoch = epoch  # which shell state owns this object; a (sub)shell copies an array before changing one it doesn't own
        self._ordered = True

    def get(self, key: str) -> str | None:
        return self.items.get(key)

    def set(self, key: str, value: str) -> None:
        if not self.assoc and key not in self.items and self.items and int(key) < self.next:
            self._ordered = False
        self.items[key] = value
        if not self.assoc:
            self.next = max(self.next, int(key) + 1)

    def remove(self, key: str) -> None:
        self.items.pop(key, None)

    def _sort(self) -> None:
        if not self._ordered:
            self.items = dict(sorted(self.items.items(), key=lambda kv: int(kv[0])))
            self._ordered = True

    def keys(self) -> list[str]:
        self._sort()
        return list(self.items)

    def values(self) -> list[str]:
        self._sort()
        return list(self.items.values())

    def pairs(self) -> list[tuple[str, str]]:
        self._sort()
        return list(self.items.items())

    def clone(self, epoch: int) -> "ShellArray":
        a = ShellArray(self.assoc, epoch)
        a.items, a.next, a._ordered = dict(self.items), self.next, self._ordered
        return a


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


class _NewProcess:
    """A new bash process (bash -c, bash file, ./script.sh): $0, the file it runs (if any), and its own options (bash -e)."""

    def __init__(self, arg0: str, file: str | None = None, setup: Callable[[], None] | None = None):
        self.arg0, self.file, self.setup = arg0, file, setup


# bash builtins that have no program file on a real system: listing them in /bin makes a model suspicious
SNAPSHOT_SET_OPTIONS = ("errexit", "pipefail", "nounset", "noglob", "xtrace", "noclobber")  # the set -o options a shell can change
_DEFAULT_SHOPTS = {"cmdhist", "complete_fullquote", "extquote", "force_fignore", "globasciiranges", "globskipdots", "hostcomplete",
                   "interactive_comments", "patsub_replacement", "progcomp", "promptvars", "sourcepath"}
_BASH_VERSINFO = ["5", "2", "21", "1", "release", "x86_64-pc-linux-gnu"]
_SCRATCH = "__topwater_element"
_PARAM = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!-])(.*)$", re.S)
_INDIRECT = re.compile(r"^!([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!-])(.*)$", re.S)
_PREFIX = re.compile(r"^!([A-Za-z_][A-Za-z0-9_]*)[*@]$")
_ASSIGNMENT_TILDE = re.compile(r"(?:(?<=^)|(?<=:))~([A-Za-z0-9_.-]*)(?=/|:|$)")


def size_text(n: int) -> str:
    """"32 MB", "512 KB" or "1000 bytes": a limit as messages print it."""
    return f"{n >> 20} MB" if n % (1 << 20) == 0 else f"{n >> 10} KB" if n % 1024 == 0 else f"{n} bytes"


def _seconds(t: float) -> str:
    return f"{t:.3f}".rstrip("0").rstrip(".") + " s"


def _stopped(why: str) -> str:
    return f"[stopped: {why}. Running it again will hit the same limit. Files and variables changed before this point are kept.]"


def default_op(rest: str):
    """${p-w} ${p:-w} ${p=w} ${p:=w} ${p+w} ${p:+w} ${p?w} ${p:?w}: (operator, word); None for any other operator."""
    n = 2 if rest[:1] == ":" else 1
    return (rest[:n], rest[n:]) if len(rest) >= n and rest[n - 1] in "-=+?" else None


def array_ref(expr: str):
    """Splits name[sub]rest (or !name[sub] for the keys) into (keys, name, sub, tail); None when not that shape."""
    start = 1 if expr.startswith("!") else 0
    open_ = expr.find("[")
    if open_ <= start or not is_name(expr[start:open_]):
        return None
    depth, k = 0, open_
    while k < len(expr):
        if expr[k] == "[":
            depth += 1
        elif expr[k] == "]":
            depth -= 1
            if depth == 0:
                break
        k += 1
    return (start == 1, expr[start:open_], expr[open_ + 1:k], expr[k + 1:]) if k < len(expr) else None


def _unquote(s: str) -> str:
    return s[1:-1] if len(s) >= 2 and s[0] in "\"'" and s[-1] == s[0] else s


def trace_quote(s: str) -> str:
    """set -x: an argument as bash traces it, quoted only when it needs to be."""
    return s if s and all((c.isascii() and c.isalnum()) or c in "_./=:,+@%^-" for c in s) else quote_value(s)


def quote_value(s: str) -> str:
    """${x@Q}: single quotes, or $'...' when the text holds control characters."""
    if any(ord(c) < 32 or ord(c) == 127 for c in s):
        from .commands import shell_quote
        return shell_quote(s)
    return "'" + s.replace("'", "'\\''") + "'"


class Shell:
    """A simulated bash session. Give the model a single "bash" tool and pass its command to run()/run_async().

    Shell state (cwd, environment, aliases, functions, files) persists between calls.

    Limits: max_loop_iterations bounds any single loop (and 10x that all loops in one call); max_call_duration (seconds)
    ends a call with exit code 124; max_file_system_bytes caps what files may hold ("No space left on device");
    max_string_bytes caps one string (an output buffer, a variable, an expansion) and all variables together;
    max_extract_bytes / max_extract_entries bound one archive extraction.
    """

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
        home = f"/home/{user}"
        for d in (home, "/tmp", "/var/tmp", "/usr/bin", "/bin", "/usr/local/bin", "/opt", "/etc", "/dev/fd", "/var/log"):
            self.fs.mkdir(d, parents=True)
        if not self.fs.exists("/etc/os-release"):
            self.fs.write_text("/etc/os-release", 'PRETTY_NAME="Ubuntu 24.04.1 LTS"\nNAME="Ubuntu"\nVERSION_ID="24.04"\n'
                                                  'VERSION="24.04.1 LTS (Noble Numbat)"\nVERSION_CODENAME=noble\nID=ubuntu\n'
                                                  'ID_LIKE=debian\nHOME_URL="https://www.ubuntu.com/"\n')
        self.fs.write_text("/etc/hostname", hostname + "\n")
        if not self.fs.exists("/etc/issue"):
            self.fs.write_text("/etc/issue", "Ubuntu 24.04.1 LTS \\n \\l\n\n")
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
        from .commands import register_all

        register_all(self)
        self._builtin_names = set(self.commands)
        # the system was "installed" months ago; only the agent's home and the temp directories are fresh
        for d in ("/bin", "/usr", "/etc", "/dev", "/opt", "/root", "/var/log", "/var/lib", "/var/cache", "/boot", "/lib",
                  "/lib64", "/media", "/mnt", "/sbin", "/srv"):
            self.fs.set_modified(d, self.installed_at, recursive=True)
        for p in ("/", "/home", "/var", home + "/.bashrc", home + "/.profile", home + "/.bash_logout"):
            self.fs.set_modified(p, self.installed_at)

    @property
    def installed_at(self) -> datetime:
        today = datetime.now(timezone.utc).replace(tzinfo=None, hour=0, minute=0, second=0, microsecond=0)
        return today - timedelta(days=142) + timedelta(hours=9, minutes=13)

    def _seed_system_files(self, user: str, home: str) -> None:
        """The ordinary clutter of a Linux install. A model that looks around and finds four files in /etc, no dotfiles
        and no /proc starts wondering what kind of machine it is on, and spends its turns investigating."""
        for d in ("/boot", "/lib", "/lib64", "/media", "/mnt", "/proc", "/run", "/sbin", "/srv", "/sys", "/usr/lib", "/usr/share",
                  "/usr/sbin", "/var/lib", "/var/cache"):
            self.fs.mkdir(d, parents=True)

        def seed(path: str, content: str) -> None:
            if not self.fs.exists(path):
                self.fs.write_text(path, content)

        h = self.hostname
        seed("/etc/hosts", f"127.0.0.1\tlocalhost\n127.0.1.1\t{h}\n\n::1\tip6-localhost ip6-loopback\nfe00::0\tip6-localnet\n"
                           "ff02::1\tip6-allnodes\nff02::2\tip6-allrouters\n")
        seed("/etc/group", f"root:x:0:\nadm:x:4:\nsudo:x:27:\nusers:x:100:\n{user}:x:1000:\n")
        seed("/etc/shells", "# /etc/shells: valid login shells\n/bin/sh\n/bin/bash\n/usr/bin/bash\n/usr/bin/sh\n")
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
        seed(home + "/.profile", '# ~/.profile: executed by the command interpreter for login shells.\n\nif [ -n "$BASH_VERSION" ]; then\n'
                                 '    if [ -f "$HOME/.bashrc" ]; then\n\t. "$HOME/.bashrc"\n    fi\nfi\n\nif [ -d "$HOME/bin" ] ; then\n'
                                 '    PATH="$HOME/bin:$PATH"\nfi\n')
        seed(home + "/.bash_logout", '# ~/.bash_logout: executed by bash(1) when login shell exits.\n\nif [ "$SHLVL" = 1 ]; then\n'
                                     "    [ -x /usr/bin/clear_console ] && /usr/bin/clear_console -q\nfi\n")
        seed(home + "/.bashrc", "# ~/.bashrc: executed by bash(1) for non-login shells.\n\n# If not running interactively, don't do anything\n"
                                "case $- in\n    *i*) ;;\n      *) return;;\nesac\n\nHISTCONTROL=ignoreboth\nHISTSIZE=1000\nHISTFILESIZE=2000\n\n"
                                "shopt -s checkwinsize\n\nPS1='\\u@\\h:\\w\\$ '\n\nalias ls='ls --color=auto'\nalias ll='ls -alF'\n"
                                "alias la='ls -A'\nalias l='ls -CF'\n")

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
        from .binder import register_targets

        register_targets(self, targets)
        return self

    def unregister(self, name: str) -> bool:
        with self.fs.as_root():
            for d in ("/usr/bin/", "/bin/"):
                if self.fs.is_file(d + name):
                    self.fs.delete(d + name)
        return self.commands.pop(name, None) is not None

    # ---------- snapshots (see snapshot.py and "Snapshots" in docs/reference.md) ----------

    def snapshot(self) -> "ShellSnapshot":
        """Captures the machine (files and session) as an immutable ShellSnapshot. Call it between calls."""
        from .snapshot import ShellSnapshot
        return ShellSnapshot(list(self.fs.entries()), self._capture_state(), self.user, self.hostname, utc_now())

    def restore(self, snapshot: "ShellSnapshot") -> None:
        """Puts the machine back as `snapshot` captured it. This shell keeps its own registered commands (and their
        /usr/bin entries); the snapshot is unchanged and can be restored again."""
        self.fs.replace_all(snapshot.entries)
        self._apply_state(snapshot.state)
        self.hashed = {}  # the restored machine's programs may be elsewhere

    def fork(self) -> "Shell":
        """A new shell with a copy of this machine that shares nothing mutable with it. It shares the registered command
        handlers, except host programs (register_host_command), which are bound to this shell: register them again."""
        from .host import HostCommand
        fork = Shell(self.user, self.hostname, max_loop_iterations=self.max_loop_iterations, max_call_duration=self.max_call_duration,
                     max_file_system_bytes=self.max_file_system_bytes, max_string_bytes=self.max_string_bytes,
                     max_extract_bytes=self.max_extract_bytes, max_extract_entries=self.max_extract_entries)
        fork.commands = {n: c for n, c in self.commands.items() if not isinstance(c.handler, HostCommand)}
        fork._builtin_names = None if self._builtin_names is None else set(self._builtin_names)
        fork.restore(self.snapshot())
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
        from .snapshot import ShellSnapshot
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
        from .recording import ShellRecorder
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
            "last_bg": self._last_bg, "next_pid": self._next_pid,
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

    def _arm_alarm(self):
        """Ticks SIGALRM while the call runs, so a regex that backtracks past the time limit is stopped (see TimedPattern).
        Returns the handler to put back, or None where that is not possible: off the main thread, without setitimer
        (Windows), or when the host has a timer of its own running."""
        if (threading.current_thread() is not threading.main_thread() or not hasattr(signal, "setitimer")
                or signal.getitimer(signal.ITIMER_REAL) != (0.0, 0.0)):
            return None

        def tick(signum, frame):
            if _matching and time.monotonic() >= self._deadline:
                raise CallTimeout()

        previous = signal.signal(signal.SIGALRM, tick)
        signal.setitimer(signal.ITIMER_REAL, 0.1, 0.1)
        return previous if previous is not None else signal.SIG_DFL

    def check_deadline(self) -> None:
        if time.monotonic() >= self._deadline:
            raise CallTimeout()

    # ---------- execution ----------

    def run(self, command_line: str) -> ShellResult:
        """Synchronous wrapper for run_async (do not call from inside a running event loop)."""
        return asyncio.run(self.run_async(command_line))

    async def run_async(self, command_line: str) -> ShellResult:
        started, clock = utc_now(), time.perf_counter()
        result = await self._run_call(command_line)
        if self._recorder is not None:
            self._recorder._add(command_line, result, (time.perf_counter() - clock) * 1000, started)
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
        io_ = _Io(StdinBuffer(), SwitchWriter(out), SwitchWriter(err))

        def stop_err(msg: str) -> None:
            err.seek(0, io.SEEK_END)
            io.StringIO.write(err, msg)  # past the cap on purpose: the note must get through

        alarm = self._arm_alarm()
        try:
            try:
                code = await self._exec_script(Parser.parse(command_line), io_)
            finally:
                # shortcut: EXIT traps fire when the tool call ends (like `set -e`, they last one call), not at logout
                if time.monotonic() < self._deadline:
                    await self._run_exit_trap(io_)
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
            if alarm is not None:
                signal.setitimer(signal.ITIMER_REAL, 0)
                signal.signal(signal.SIGALRM, alarm)
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

    @property
    def err_prefix(self) -> str:
        """bash's prefix for its own errors: "bash: line 3: ", "./s.sh: line 2: ", or "environment: line 1: " in a
        function defined in a bash -c string."""
        return f"{self._err_where[-1][0] if self._err_where else 'bash'}: line {max(self._lineno, 1)}: "

    async def _run_script_text(self, text: str, io_: _Io, subshell: bool, positional: list[str] | None = None,
                               process: _NewProcess | None = None, sourced: str | None = None, eval_: bool = False) -> int:
        """process is set for a new bash process (it sees only exported variables and functions); sourced is the file
        `source` is running, for BASH_SOURCE. eval_: report syntax errors as eval's, at their line in the text."""
        try:
            script = Parser.parse(text)
        except ParseError as e:
            if not eval_:
                io_.err.write(f"bash: {e}\n")
                return 2
            # shortcut: lexer errors (unmatched quotes) are reported on the text's first line
            prefix = f"{self._err_where[-1][0] if self._err_where else 'bash'}: eval: line {max(self._lineno, 1) + (e.line or 1) - 1}: "
            io_.err.write(f"{prefix}{e}\n")
            if e.echo is not None:
                io_.err.write(prefix + diagnostic("sourceLine", line=e.echo) + "\n")
            return 2
        self._script_depth += 1
        if self._script_depth > 100:
            raise LimitExceeded("maximum script nesting level exceeded (100)")
        saved_arg0 = self._arg0
        sources, where, line, base = len(self._sources), len(self._err_where), self._lineno, self._line_base
        # eval and $(...) text counts on from the line it is on; a new bash or a sourced file counts from 1
        self._line_base = 0 if process is not None or sourced is not None else max(line, 1) - 1
        file = (process.file if process else None) or sourced
        if file is not None:
            self._sources.append(file)
        if process is not None:
            self._err_where.append((process.file or process.arg0, process.file is not None))
        elif sourced is not None:
            self._err_where.append((sourced, True))
        try:
            if not subshell:
                saved_pos = self.positional
                if positional is not None:
                    self.positional = positional
                try:
                    return await self._exec_script(script, io_)
                finally:
                    if positional is not None:
                        self.positional = saved_pos
            saved = self._save_state()
            self.traps = {}
            io_ = replace(io_, out=SwitchWriter(io_.out), err=SwitchWriter(io_.err))  # `exec >file` in a script ends with it
            if process is not None:
                self._become_new_process(process)
            if positional is not None:
                self.positional = positional
            try:
                try:
                    return await self._exec_script(script, io_)
                finally:
                    await self._run_exit_trap(io_)
            except ExitSignal as e:
                return e.code
            finally:
                self._restore_state(saved)
        finally:
            self._script_depth -= 1
            self._arg0 = saved_arg0
            del self._sources[sources:]
            del self._err_where[where:]
            self._lineno, self._line_base = line, base

    def _become_new_process(self, process: _NewProcess) -> None:
        """What a child bash inherits: exported variables (not their other attributes) and exported functions."""
        self.env = {k: v for k, v in self.env.items() if self.is_exported(k)}
        self.var_attrs = {k: "x" for k in self.env}
        self.arrays = {}
        self.aliases = {}
        self.functions = {k: v for k, v in self.functions.items() if k in self.exported_functions}
        self.errexit = self.pipefail = self.nounset = self.xtrace = self.noglob = self.noclobber = False
        self.hashed = {}
        self._subshell = 0  # a new bash is no subshell: a fatal error ends it with its own status (127 for set -u)
        self._arg0 = process.arg0
        if process.setup:
            process.setup()

    async def _run_exit_trap(self, io_: _Io) -> None:
        action = self.traps.pop("EXIT", None)
        if action:
            saved_code = self.last_exit_code
            try:
                await self._run_script_text(action, io_, subshell=False)
            except (LoopControl, ReturnSignal, ExitSignal):
                pass
            self.last_exit_code = saved_code

    def _save_state(self):
        # arrays are shared with the saved state and copied on first write (see writable), so a subshell or
        # pipeline stage doesn't copy every element just to start
        self._epoch += 1
        self._subshell += 1
        return (self._subshell - 1, self.umask, self._cwd, dict(self.env), dict(self.aliases), dict(self.functions), self.positional, self.errexit, self.pipefail,
                self.nounset, set(self.shopts), dict(self.traps), dict(self.arrays),
                (self.noglob, self.xtrace, self.noclobber, self._ifs_unset), dict(self.var_attrs), self._variable_chars,
                {k: list(v) for k, v in self.hashed.items()})

    def _restore_state(self, saved) -> None:
        self._epoch += 1
        (self._subshell, self.umask, cwd, self.env, self.aliases, self.functions, self.positional, self.errexit, self.pipefail, self.nounset, self.shopts,
         self.traps, self.arrays, flags, self.var_attrs, self._variable_chars, self.hashed) = saved
        self.noglob, self.xtrace, self.noclobber, self._ifs_unset = flags
        self._cwd = cwd if self.fs.is_dir(cwd) else "/"
        self.fs.umask = self.umask

    async def _exec_script(self, script: Script, io_: _Io) -> int:
        code = 0
        for item in script.items:
            if item.background:
                code = await self._exec_background(item, io_)
                continue
            # every command but the last of an && / || list is a condition: set -e and the ERR trap ignore it
            code = await self._exec_list_pipeline(item.first, bool(item.rest), io_)
            last_run, negated = 0, item.first.negate
            for k, (op, nxt) in enumerate(item.rest):
                if (op == "&&") != (code == 0):
                    continue
                code = await self._exec_list_pipeline(nxt, k < len(item.rest) - 1, io_)
                last_run, negated = k + 1, nxt.negate
            if code != 0 and self._cond_depth == 0 and last_run == len(item.rest) and not negated:
                on_error = self.traps.get("ERR")
                if on_error and not self._in_err_trap:
                    self._in_err_trap = True
                    try:
                        await self._run_script_text(on_error, io_, subshell=False)
                    finally:
                        self._in_err_trap = False
                        self.last_exit_code = code
                if self.errexit:
                    raise ExitSignal(code)  # set -e: never inside if/while conditions
        return code

    async def _exec_list_pipeline(self, p: Pipeline, condition: bool, io_: _Io) -> int:
        if not condition:
            return await self._exec_pipeline(p, io_)
        self._cond_depth += 1
        try:
            return await self._exec_pipeline(p, io_)
        finally:
            self._cond_depth -= 1

    async def _exec_background(self, item, io_: _Io) -> int:
        """cmd &: runs now, to completion, in a subshell; $! gets a job id that `wait` reports its status for.
        shortcut: jobs run in order, not concurrently; run them truly in parallel if agents start relying on overlap."""
        saved = self._save_state()
        try:
            code = await self._exec_script(Script([replace(item, background=False)]), replace(io_, stdin=StdinBuffer()))
        except ExitSignal as e:
            code = e.code
        finally:
            self._restore_state(saved)
        self._next_pid += 1
        self._last_bg = self._next_pid
        self._jobs[self._last_bg] = code
        self.last_exit_code = 0
        return 0

    def wait_for(self, pid: int) -> int:
        """`wait [id]`: the job's exit status (it has already finished), 127 for an unknown id."""
        return self._jobs.pop(pid, 127)

    def wait_next(self) -> int:
        return 127 if not self._jobs else self.wait_for(min(self._jobs))

    def forget_jobs(self) -> None:
        self._jobs.clear()

    async def _exec_pipeline(self, p: Pipeline, io_: _Io) -> int:
        if p.timed:
            start = time.monotonic()
            status = await self._exec_pipeline(replace(p, timed=False), io_) if p.commands else 0
            t = time.monotonic() - start
            io_.err.write(f"real {t:.2f}\nuser 0.00\nsys 0.00\n" if p.posix_time
                          else f"\nreal\t{int(t // 60)}m{t % 60:.3f}s\nuser\t0m0.000s\nsys\t0m0.000s\n")
            self.last_exit_code = status
            return status
        if not p.negate:
            return await self._run_pipeline(p, io_)
        self._cond_depth += 1  # like a condition, a negated pipeline is exempt from set -e
        try:
            code = await self._run_pipeline(p, io_)
        finally:
            self._cond_depth -= 1
        self.last_exit_code = 1 if code == 0 else 0
        return self.last_exit_code

    async def _run_pipeline(self, p: Pipeline, io_: _Io) -> int:
        self.check_deadline()
        data = io_.stdin
        code = failed = 0
        statuses = []
        n = len(p.commands)
        for k, cmd in enumerate(p.commands):
            last = k == n - 1
            # shortcut: pipeline stages run one after another with buffered text; stream if huge outputs matter.
            out = io_.out if last else CappedWriter(self)
            stage = replace(io_, stdin=data, out=out)
            if n == 1:
                code = await self._exec_command(cmd, stage)
            elif last:
                # shortcut: the last stage keeps its variables (bash's lastpipe), so `cmd | while read` loops can count
                try:
                    code = await self._exec_command(cmd, stage)
                except ExitSignal as e:
                    code = e.code
            else:
                # every stage but the last is a subshell: `exit`, `cd` and assignments stay inside it
                saved = self._save_state()
                try:
                    code = await self._exec_command(cmd, stage)
                except ExitSignal as e:
                    code = e.code
                finally:
                    self._restore_state(saved)
            if code != 0:
                failed = code
            statuses.append(code)
            if not last:
                data = StdinBuffer(out.getvalue())
        self._pipe_status = statuses
        if self.pipefail:
            code = failed
        self.last_exit_code = code
        return code

    async def _exec_command(self, cmd, io_: _Io) -> int:
        if isinstance(cmd, SimpleCommand) and not cmd.assignments and len(cmd.words) == 1 and cmd.words[0].is_("exec"):
            return await self._exec_redirects(cmd.redirects, io_)
        if getattr(cmd, "line", 0) > 0:
            self._lineno = self._line_base + cmd.line  # a failed redirection reports this line too
        sinks: list[FileSink] = []
        rio = await self._apply_redirects(cmd.redirects, io_, sinks)
        if rio is None:
            return 1
        try:
            if isinstance(cmd, SimpleCommand):
                code = await self._exec_simple(cmd, rio)
            elif isinstance(cmd, GroupCommand):
                code = await (self._exec_subshell(cmd.body, rio) if cmd.subshell else self._exec_script(cmd.body, rio))
            elif isinstance(cmd, IfCommand):
                code = await self._exec_if(cmd, rio)
            elif isinstance(cmd, LoopCommand):
                code = await self._exec_loop(cmd, rio)
            elif isinstance(cmd, ForCommand):
                code = await (self._exec_select(cmd, rio) if cmd.select else self._exec_for(cmd, rio))
            elif isinstance(cmd, ArithForCommand):
                code = await self._exec_arith_for(cmd, rio)
            elif isinstance(cmd, CaseCommand):
                code = await self._exec_case(cmd, rio)
            elif isinstance(cmd, FunctionDef):
                self.functions[cmd.name] = cmd
                self._func_where[cmd.name] = self._err_where[-1][0] if self._err_where and self._err_where[-1][1] else "environment"
                code = 0
            elif isinstance(cmd, ArithCommand):
                code = 0 if await self._arith(cmd.expr, rio) != 0 else 1
            else:
                code = 0 if await _CondEvaluator(self, cmd.items, rio).evaluate() else 1
        except ShellError as e:
            rio.err.write(f"{self.err_prefix}{e}\n")
            code = 2 if isinstance(cmd, CondCommand) else e.exit_code
        finally:
            for sink in sinks:
                try:
                    self.fs.append_text(sink.path, sink.getvalue())
                except ShellError as e:
                    io_.err.write(f"{self.err_prefix}{e}\n")
                    code = 1
        return code

    async def _exec_subshell(self, body: Script, io_: _Io) -> int:
        saved = self._save_state()
        try:
            return await self._exec_script(body, replace(io_, out=SwitchWriter(io_.out), err=SwitchWriter(io_.err)))
        except ExitSignal as e:
            return e.code
        finally:
            self._restore_state(saved)

    async def _condition(self, cond: Script, io_: _Io) -> int:
        self._cond_depth += 1
        try:
            return await self._exec_script(cond, io_)
        finally:
            self._cond_depth -= 1

    async def _exec_if(self, c: IfCommand, io_: _Io) -> int:
        for cond, body in c.branches:
            if await self._condition(cond, io_) == 0:
                return await self._exec_script(body, io_)
        return await self._exec_script(c.else_body, io_) if c.else_body is not None else 0

    async def _loop_body(self, body: Script, io_: _Io) -> tuple[int, bool]:
        """Runs a loop body; returns (exit code, True if `break`)."""
        self.loop_depth += 1
        try:
            return await self._exec_script(body, io_), False
        except LoopControl as lc:
            if lc.levels > 1 and self.loop_depth > 1:
                raise LoopControl(lc.is_break, lc.levels - 1) from None
            return 0, lc.is_break
        finally:
            self.loop_depth -= 1

    def _check_iterations(self, n: int) -> int:
        self.check_deadline()
        # nested loops multiply, so one tool call also has a total budget (10x the single-loop cap)
        instead = "process the whole input with one command (awk, sed, xargs) instead of a shell loop"
        n += 1
        if n > self.max_loop_iterations:
            raise LimitExceeded(f"loop aborted after {self.max_loop_iterations} iterations (the limit for one loop); {instead}")
        self._total_iterations += 1
        if self._total_iterations > self.max_loop_iterations * 10:
            raise LimitExceeded(f"loop aborted after {self.max_loop_iterations * 10} iterations in this call "
                                f"(the limit for all loops in one call); {instead}")
        return n

    async def _exec_loop(self, c: LoopCommand, io_: _Io) -> int:
        code = n = 0
        while True:
            if (await self._condition(c.cond, io_) == 0) == c.until:
                break
            n = self._check_iterations(n)  # counts body runs, so a loop of exactly the limit still finishes
            code, brk = await self._loop_body(c.body, io_)
            if brk:
                break
        return code

    async def _exec_for(self, c: ForCommand, io_: _Io) -> int:
        items = list(self.positional) if c.items is None else await self._expand_words(c.items, io_)
        code = n = 0
        for item in items:
            n = self._check_iterations(n)
            self.set_var(c.var, item)
            code, brk = await self._loop_body(c.body, io_)
            if brk:
                break
        return code

    async def _exec_select(self, c: ForCommand, io_: _Io) -> int:
        """select: the menu and the PS3 prompt go to stderr, choices come from stdin, until `break` or end of input."""
        items = list(self.positional) if c.items is None else await self._expand_words(c.items, io_)
        code = n = 0
        menu = True
        while True:
            if menu:
                for k, item in enumerate(items):
                    io_.err.write(f"{k + 1}) {item}\n")
            io_.err.write(self.get_variable("PS3") or "#? ")
            line = io_.stdin.read_line()
            if line is None:
                io_.err.write("\n")
                break
            n = self._check_iterations(n)
            menu = not line.strip()
            if menu:
                continue  # an empty answer shows the menu again
            self.set_var("REPLY", line)
            choice = line.strip()
            self.set_var(c.var, items[int(choice) - 1] if choice.isdigit() and 1 <= int(choice) <= len(items) else "")
            code, brk = await self._loop_body(c.body, io_)
            if brk:
                break
        return code

    async def _exec_arith_for(self, c: ArithForCommand, io_: _Io) -> int:
        code = n = 0
        if c.init:
            await self._arith(c.init, io_)
        while not c.cond or await self._arith(c.cond, io_) != 0:
            n = self._check_iterations(n)
            code, brk = await self._loop_body(c.body, io_)
            if brk:
                break
            if c.step:
                await self._arith(c.step, io_)
        return code

    async def _exec_case(self, c: CaseCommand, io_: _Io) -> int:
        subject = await self._expand_single(c.subject, io_)
        code = 0
        fall_through = False
        nocase = "nocasematch" in self.shopts
        for patterns, body, terminator in c.clauses:
            if not fall_through:
                matched = False
                for pattern in patterns:
                    if glob_to_regex(await self._pattern(pattern, io_), nocase, slashes=True).match(subject):
                        matched = True
                        break
                if not matched:
                    continue
            code = await self._exec_script(body, io_)
            if terminator == ";;":
                return code
            fall_through = terminator == ";&"  # ;;& goes on testing the next patterns
        return code

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
        if not r.op.endswith(">>") or not self.fs.exists(path):
            self.fs.write_text(path, "")  # truncate on open, like bash
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

    async def _exec_simple(self, c: SimpleCommand, io_: _Io) -> int:
        self._lineno = self._line_base + c.line
        debug = self.traps.get("DEBUG") if self.traps and not self._in_debug_trap else None
        if debug:
            # shortcut: the DEBUG trap runs before simple commands only, not before (( )), [[ ]], for and case
            self._in_debug_trap = True
            status = self.last_exit_code
            try:
                await self._run_script_text(debug, io_, subshell=False)
            finally:
                self._in_debug_trap = False
                self.last_exit_code = status
        words = c.words
        declaration = len(words) > 1 and words[0].unquoted_literal in ("local", "declare", "typeset", "readonly", "export")
        if declaration and not words[0].is_("export"):
            rest = await self._declare_arrays(words, io_)
            if rest is not None:
                if len(rest) == 1:
                    return 0
                words = rest
        argv = await (self._expand_declaration(words, io_) if declaration else self._expand_words(words, io_))
        assigned: list[tuple[str, str, bool]] = []
        for a in c.assignments:
            try:
                if a.elements is not None or a.index is not None:
                    await self._assign_array(a, io_)
                    continue
                v = await self._expand_assignment(a.value, io_)
                if self.xtrace:
                    io_.err.write(f"+ {a.name}{'+=' if a.append else '='}{trace_quote(v)}\n")
                if words:
                    assigned.append((a.name, v, a.append))
                    continue
                # with no command, assignments apply left to right: IFS=, parts=($csv) splits on the comma
                self.set_var(a.name, v, a.append)
            except ShellError as e:
                if words or not str(e).endswith("readonly variable"):
                    raise
                io_.err.write(f"{self.err_prefix}{e}\n")
                raise ExitSignal(1) from None  # like bash, assigning a read-only variable ends the script

        if not argv:
            for k, v, append in assigned:
                self.set_var(k, v, append)
            # only a command substitution leaves its exit code behind; `$empty` alone is a successful no-op
            subs = any(isinstance(p, CommandSub) for w in list(c.words) + [a.value for a in c.assignments] for p in w.parts)
            return self.last_exit_code if subs else 0

        alias = self.aliases.get(argv[0])
        if alias is not None:
            # an alias holding a pipeline or list is shell text, not just words
            if not assigned and any(ch in alias for ch in "|;&<>"):
                from .commands import shell_quote
                name = argv[0]
                del self.aliases[name]  # removed while it runs so it can't expand itself
                try:
                    return await self._run_script_text(alias + " " + " ".join(shell_quote(x) for x in argv[1:]), io_, subshell=False)
                finally:
                    self.aliases[name] = alias
            argv = await self._expand_words(Parser.parse_words(alias), io_) + argv[1:]
        if self.xtrace:
            io_.err.write("+ " + " ".join(trace_quote(x) for x in argv) + "\n")

        try:
            if not assigned:
                return await self._invoke(argv, io_)
            # VAR=x cmd: the variable only exists for that command
            saved = {k: (self.env.get(k), self.var_attrs.get(k)) for k, _, _ in assigned}
            for k, v, append in assigned:
                self.env[k] = self.env.get(k, "") + v if append else v
                self.set_attrs(k, "x")  # exported to the command
            try:
                return await self._invoke(argv, io_)
            finally:
                for k, (v, attrs) in saved.items():
                    if v is None:
                        self.env.pop(k, None)
                    else:
                        self.env[k] = v
                    if attrs is None:
                        self.var_attrs.pop(k, None)
                    else:
                        self.var_attrs[k] = attrs
        finally:
            self._last_arg = argv[-1]

    async def _expand_declaration(self, words, io_: _Io) -> list[str]:
        """`local x="a b" y=$(cmd)`: arguments that look like assignments are expanded as assignments (no splitting)."""
        argv: list[str] = []
        for w in words:
            a = try_assignment(w) if argv else None
            if a is not None and a.elements is None and a.index is None:
                argv.append(a.name + ("+=" if a.append else "=") + await self._expand_assignment(a.value, io_))
            else:
                argv.extend(await self._expand_words([w], io_))
        return argv

    async def _invoke(self, argv: list[str], io_: _Io, skip_functions: bool = False) -> int:
        """skip_functions: `command ls` / `builtin cd` run the command itself even when a function has its name."""
        name = argv[0]
        if not skip_functions and name in self.functions:
            return await self._call_function(self.functions[name].body, argv, io_)
        if "/" in name:
            path = self.resolve(name)
            if get_parent(path) in ("/usr/bin", "/bin", "/usr/local/bin") and get_name(path) in self.commands:
                name = get_name(path)
            else:
                return await self._run_script_file(name, path, argv, io_)
        else:
            from .commands import is_shell_builtin

            if not is_shell_builtin(self, name) and not self.is_unfiled(name):
                found = self.hash_lookup(name)
                if found is None:
                    found = self.search_path(name)
                    if found is None:
                        io_.err.write(f"{self.err_prefix}{name}: No such file or directory\n" if not self.env.get("PATH")
                                      else diagnostic("commandNotFound", command=argv[0]) + "\n")
                        return 127
                    if "/" in found:  # bash does not remember what an empty PATH found
                        self.hashed[name] = [found, 1]
                registered = self.stub_command(found)
                if registered is None:
                    return await self._run_script_file(found, self.resolve(found), [found, *argv[1:]], io_)
                name = registered
        cmd = self.commands.get(name)
        if cmd is None:
            io_.err.write(diagnostic("commandNotFound", command=argv[0]) + "\n")
            return 127
        self.check_deadline()
        ctx = CommandContext(self, name, list(argv[1:]), io_.stdin, io_.out, io_.err, io_.fds)
        if len(argv) == 2 and self._builtin_names and name in self._builtin_names:
            from .commands import standard_option

            if standard_option(ctx, cmd, argv[1]):
                return 0
        from .commands import is_shell_builtin, usage_error

        try:
            result = cmd.handler(ctx)
            if inspect.isawaitable(result):
                result = await result
            return int(result or 0)
        except (asyncio.CancelledError, KeyboardInterrupt, _ControlFlow, RecursionError):
            raise
        except UsageException as e:
            if is_shell_builtin(self, name):
                io_.err.write(f"{self.err_prefix}{name}: {e}\n")
                return 2
            io_.err.write(usage_error(name, str(e)))
            return COMMANDS[name].usage_exit_code if name in COMMANDS else 1
        except ShellError as e:
            io_.err.write(f"{self.err_prefix if is_shell_builtin(self, name) else ''}{name}: {e}\n")
            return e.exit_code
        except Exception as e:  # a tool crashing must not crash the agent loop
            m = _BAD_INT.match(str(e)) if isinstance(e, ValueError) else None
            io_.err.write(f"{name}: invalid number: '{m.group(1)}'\n" if m else f"{name}: {e}\n")
            return 1

    async def _run_script_file(self, name: str, path: str, argv: list[str], io_: _Io) -> int:
        if self.fs.is_dir(path):
            io_.err.write(f"{self.err_prefix}{name}: Is a directory\n")
            return 126
        if not self.fs.is_file(path):
            why = self.fs.why_missing(path)
            io_.err.write(f"{self.err_prefix}{name}: {why}\n")
            return 126 if why == "Permission denied" else 127
        if not (self.fs.can_execute(path) and self.fs.can_read(path)):
            io_.err.write(f"{self.err_prefix}{name}: Permission denied\n")
            return 126
        text = self.fs.read_text(path)
        if text.startswith(EXECUTABLE_STUB[:4]):
            stub = self.stub_command(path)
            if stub is not None:
                return await self._invoke([stub, *argv[1:]], io_, skip_functions=True)
            io_.err.write(self.err_prefix + diagnostic("cannotExecuteBinary", name=name) + "\n")
            return 126
        if text.startswith("#!"):
            line = text[2:].split("\n", 1)[0].strip()
            if not re.search(r"(^|/|\s)(ba)?sh(\s|$)", line):
                # the hand-off can lead back to this script (#!/usr/bin/env self), so it counts as a nesting level
                self._script_depth += 1
                if self._script_depth > 100:
                    raise LimitExceeded("maximum script nesting level exceeded (100)")
                try:
                    return await self._run_interpreter(name, line, argv, io_)
                finally:
                    self._script_depth -= 1
        return await self._run_script_text(text, io_, subshell=True, positional=list(argv[1:]), process=_NewProcess(name, name))

    async def _run_interpreter(self, name: str, line: str, argv: list[str], io_: _Io) -> int:
        """A #! line naming another program: like the kernel, run it with its one optional argument, the script and the
        script's arguments, when it is one of the shell's commands."""
        parts = re.split(r"[ \t]", line, maxsplit=1)
        program = self.stub_command(parts[0])
        if program is None:
            interpreter = self.resolve(parts[0])
            if self.fs.is_file(interpreter) and self.fs.can_execute(interpreter):
                # another script as the interpreter, as Linux allows; each hand-off counts toward the nesting limit
                words = [parts[1].strip()] if len(parts) > 1 and parts[1].strip() else []
                return await self._run_script_file(parts[0], interpreter, [parts[0], *words, name, *argv[1:]], io_)
            io_.err.write(self.err_prefix + diagnostic("requiredFileNotFound", name=name) + "\n")
            return 127
        opt = parts[1].strip() if len(parts) > 1 else ""
        words = [] if not opt else opt[2:].split() if program == "env" and opt.startswith("-S") else [opt]
        if program != "env":
            return await self._invoke([program, *words, name, *argv[1:]], io_, skip_functions=True)
        # shortcut: env in a #! line only looks the program up on PATH; env's own options are not supported
        if not words or not self.is_unfiled(words[0]) and not self.search_path_all(words[0]):
            io_.err.write(f"{parts[0]}: \u2018{words[0] if words else ''}\u2019: No such file or directory\n")
            if " " in opt:
                io_.err.write(f"{parts[0]}: use -[v]S to pass options in shebang lines\n")
            return 127
        return await self._invoke([*words, name, *argv[1:]], io_, skip_functions=True)

    def is_unfiled(self, name: str) -> bool:
        """A registered command with no program file (a shell-only builtin, or an app command added where there is no
        /usr/bin): it runs whatever PATH says."""
        return name in self.commands and "/" not in name and not self.fs.is_file("/usr/bin/" + name) and not self.fs.is_file("/bin/" + name)

    def stub_command(self, path: str) -> str | None:
        """The registered command a program file stands for, or None when `path` is not one of the stubs."""
        p = self.resolve(path)
        if not self.fs.is_file(p) or not self.fs.can_read(p) or self.fs.read_text(p) != EXECUTABLE_STUB:
            return None
        name = get_name(self.fs.real_path(p)[0] or p)
        return name if name in self.commands else None

    def _path_candidates(self, name: str) -> list[str]:
        """Where PATH would have `name`, directory by directory, as written; an empty entry is the cwd, and a leading ~ is
        expanded as bash does during the search."""
        dirs = [tp[0] + d[tp[1]:] if (tp := self._tilde_prefix(d)) else d for d in self.env.get("PATH", "").split(":")]
        if dirs == [""]:
            return [name]  # PATH= : bash runs the cwd's `name` as it is, and neither hashes it nor calls it ./name
        return [d.rstrip("/") + "/" + name if d else "./" + name for d in dirs]

    def search_path(self, name: str) -> str | None:
        """PATH search as bash does it: the first executable regular file named `name` in a directory the agent can
        search, else the first one that isn't executable (running it says "Permission denied"), else None."""
        denied = None
        for p in self._path_candidates(name):
            a = self.resolve(p)
            if self.fs.is_file(a):
                if self.fs.can_execute(a):
                    return p
                denied = denied or p
        return denied

    def search_path_all(self, name: str) -> list[str]:
        """`type -a`, `which -a`: every executable regular file named `name` on PATH."""
        return [p for p in self._path_candidates(name) if self.fs.is_file(self.resolve(p)) and self.fs.can_execute(self.resolve(p))]

    def hash_lookup(self, name: str) -> str | None:
        """The hash table's entry for `name`, counting a hit; the table empties first if PATH changed."""
        self.sync_hashed()
        h = self.hashed.get(name)
        if h is None:
            return None
        h[1] += 1
        return h[0]

    def sync_hashed(self) -> None:
        path = self.env.get("PATH")
        if path != self._hashed_for:
            self.hashed = {}
            self._hashed_for = path

    def locate(self, name: str) -> tuple[str, bool] | None:
        """`type`, `command -v`: the program `name` runs, from the hash table (counting a hit) or PATH; an app command with
        no program file is reported in /usr/bin as before."""
        if "/" in name:
            a = self.resolve(name)
            return (name, False) if self.fs.is_file(a) and self.fs.can_execute(a) else None
        h = self.hash_lookup(name)
        if h is not None:
            return h, True
        p = self.search_path(name)
        if p is not None:
            return (p if "/" in p else self.resolve(p)), False
        return ("/usr/bin/" + name, False) if self._is_app_only(name) else None

    def locate_all(self, name: str) -> list[str]:
        """`type -a`, `which`: every program file `name` could run, in PATH order."""
        if "/" in name:
            found = self.locate(name)
            return [found[0]] if found else []
        found = self.search_path_all(name)
        return found or (["/usr/bin/" + name] if self._is_app_only(name) else [])

    def _is_app_only(self, name: str) -> bool:
        from .commands import is_shell_builtin
        spec = COMMANDS.get(name)
        shell_only = spec is not None and not spec.program_file and self._builtin_names is not None and name in self._builtin_names
        return self.is_unfiled(name) and not is_shell_builtin(self, name) and not shell_only

    async def _call_function(self, body, argv: list[str], io_: _Io) -> int:
        if len(self._locals) >= 200:
            raise LimitExceeded(f"{argv[0]}: maximum function nesting level exceeded (200)")
        saved_pos = self.positional
        on_return = self.traps.get("RETURN")
        self.positional = list(argv[1:])
        self._locals.append({})
        self._local_arrays.append({})
        self._func_names.append(argv[0])
        where = self._func_where.get(argv[0], "environment")
        self._err_where.append((where, where != "environment"))
        line, base, self._line_base = self._lineno, self._line_base, 0  # shortcut: a function defined inside eval counts from its text's line 1
        try:
            code = await self._exec_command(body, io_)
        except ReturnSignal as r:
            code = r.code
        finally:
            # shadowed values come back without passing charge_variable, so they count again
            for k, v in self._local_arrays.pop().items():
                if v is None:
                    self.arrays.pop(k, None)
                else:
                    self.arrays[k] = v
                    self._variable_chars += sum(len(a) + len(b) for a, b in v.items.items())
            for k, (v, attrs) in self._locals.pop().items():
                if v is None:
                    self.env.pop(k, None)
                else:
                    self.env[k] = v
                    self._variable_chars += len(v)
                if attrs is None:
                    self.var_attrs.pop(k, None)
                else:
                    self.var_attrs[k] = attrs
            self.positional = saved_pos
            self._func_names.pop()
            self._err_where.pop()
            self._lineno, self._line_base = line, base
        # a RETURN trap set inside the function runs as it returns
        trap = self.traps.get("RETURN")
        if trap and trap != on_return:
            del self.traps["RETURN"]
            await self._run_script_text(trap, io_, subshell=False)
        self.last_exit_code = code
        return code

    @property
    def in_function(self) -> bool:
        return bool(self._locals)

    def is_variable_set(self, expr: str) -> bool:
        """[[ -v name ]], [[ -v a[k] ]]: whether the variable or element is set."""
        r = array_ref(expr)
        if r is not None and not r[0] and r[3] == "":
            if r[2] in ("@", "*"):
                a = self.get_array(r[1])
                return a is not None and bool(a.items)
            return self.get_element(r[1], r[2]) is not None
        return self._is_set(expr) if is_name(expr) or (expr and expr.isdigit()) else False

    @property
    def option_list(self) -> list[tuple[str, bool]]:
        """The set -o options this shell has, and whether each is on."""
        return [
            ("allexport", False), ("braceexpand", True), ("emacs", False), ("errexit", self.errexit), ("errtrace", False),
            ("functrace", False), ("hashall", True), ("histexpand", False), ("history", False), ("ignoreeof", False),
            ("interactive-comments", True), ("keyword", False), ("monitor", False), ("noclobber", self.noclobber),
            ("noexec", False), ("noglob", self.noglob), ("nolog", False), ("notify", False), ("nounset", self.nounset),
            ("onecmd", False), ("physical", False), ("pipefail", self.pipefail), ("posix", False), ("privileged", False),
            ("verbose", False), ("vi", False), ("xtrace", self.xtrace)]

    def set_option(self, name: str, on: bool) -> bool:
        """set -o NAME / set +o NAME; False for an unknown name."""
        if name in ("errexit", "nounset", "pipefail", "xtrace", "noglob", "noclobber"):
            setattr(self, name, on)
            return True
        return any(n == name for n, _ in self.option_list)  # accepted, no effect here

    def input_fd(self, fd: int) -> StdinBuffer | None:
        """`read -u N` / `mapfile -u N`: the input opened on fd N by `exec N<file`, or None."""
        v = self.fds.get(fd)
        return v if isinstance(v, StdinBuffer) else None

    def is_builtin(self, name: str) -> bool:
        return bool(self._builtin_names) and name in self._builtin_names

    def declare_local(self, name: str, value: str | None) -> None:
        """`local NAME[=VALUE]`: remembers the outer value and attributes so they are restored when the function returns."""
        if not self._locals:
            raise ShellError(DIAGNOSTICS["onlyInFunction"])
        frame = self._locals[-1]
        if name not in frame:
            frame[name] = (self.env.get(name), self.var_attrs.get(name))
            self.var_attrs.pop(name, None)
        self.mark_shell_variable(name)
        self.env[name] = value or ""

    # ---------- variables ----------

    def has_attr(self, name: str, attr: str) -> bool:
        return bool(self.var_attrs) and attr in self.var_attrs.get(name, "")

    def is_exported(self, name: str) -> bool:
        """Whether a child bash (bash -c, a script) sees the variable: declared -x, or put in env by the host."""
        return self.has_attr(name, "x") or (name in self.env and name not in self._unexported)

    def mark_shell_variable(self, name: str) -> None:
        """Records that the shell (not the host) is about to create name, so it is not exported until `export`."""
        if name not in self.env:
            self._unexported.add(name)

    def unexport(self, name: str) -> None:
        self.set_attrs(name, "", "x")
        self._unexported.add(name)

    def set_attrs(self, name: str, on: str, off: str = "") -> None:
        f = self.var_attrs.get(name, "")
        if "l" in on:
            f = f.replace("u", "")
        if "u" in on:
            f = f.replace("l", "")
        for c in on:
            if c not in f:
                f += c
        for c in off:
            f = f.replace(c, "")
        if f:
            self.var_attrs[name] = f
        else:
            self.var_attrs.pop(name, None)

    def resolve_name(self, name: str) -> str:
        """Follows namerefs (declare -n ref=name) to the variable they stand for."""
        for _ in range(10):
            if not self.has_attr(name, "n") or not self.env.get(name):
                break
            name = self.env[name]
        return name

    def set_var(self, name: str, value: str, append: bool = False) -> None:
        """Assigns a variable the way bash does: through namerefs, refusing readonly ones, and applying -i (arithmetic),
        -l and -u. name[sub] sets an element; a plain assignment to an array sets element 0."""
        name = self.resolve_name(name)
        r = array_ref(name)
        if r is not None and not r[0] and r[3] == "" and r[2] not in ("@", "*"):
            self._check_writable(r[1])
            self.set_element(r[1], r[2], self._converted(self.resolve_name(r[1]), value, self.get_element(r[1], r[2]) if append else None))
            return
        if not is_name(name):
            raise ShellError(diagnostic("notValidIdentifier", name=name))
        self._check_writable(name)
        if name == "PATH":
            self.hashed = {}  # even to the same value
        if name == "SECONDS" and re.fullmatch(r"-?\d+", value):
            self._seconds_base = time.time() - int(value)
            return
        array = name in self.arrays
        value = self._converted(name, value, None if not append else self.get_element(name, "0") if array else self.env.get(name))
        if array:
            self.set_element(name, "0", value)
        else:
            self.charge_variable(name, len(value), len(self.env.get(name, "")))
            self.mark_shell_variable(name)
            self.env[name] = value

    def _converted(self, name: str, value: str, old: str | None) -> str:
        f = self.var_attrs.get(name) if self.var_attrs else None
        if not f:
            return (old or "") + value
        if "i" in f:
            return str(arith.evaluate(self, value) + (arith.evaluate(self, old) if old else 0))
        value = (old or "") + value
        return value.lower() if "l" in f else value.upper() if "u" in f else value

    def _check_writable(self, name: str) -> None:
        if self.has_attr(name, "r"):
            raise ShellError(diagnostic("readonlyVariable", name=name))

    def get_array(self, name: str) -> ShellArray | None:
        """The array called name (through namerefs), including the shell's own FUNCNAME, PIPESTATUS and BASH_VERSINFO."""
        name = self.resolve_name(name)
        a = self.arrays.get(name)
        if a is not None:
            return a
        if name == "FUNCNAME" and self._func_names:
            items = list(reversed(self._func_names))
        elif name == "BASH_SOURCE" and self._sources:
            items = list(reversed(self._sources))
        elif name == "PIPESTATUS":
            items = [str(c) for c in self._pipe_status]
        elif name == "BASH_VERSINFO":
            items = _BASH_VERSINFO
        else:
            return None
        built = ShellArray(False)
        for item in items:
            built.set(str(built.next), item)
        return built

    def _shell_variable(self, name: str) -> str | None:
        """Shell variables bash sets itself; they are not in the environment `env` prints."""
        if name == "IFS":
            return None if self._ifs_unset else " \t\n"
        fixed = {"OPTIND": "1", "BASH": "/usr/bin/bash", "BASH_VERSION": "5.2.21(1)-release", "PPID": "4241", "BASHPID": "4242",
                 "SHLVL": "1", "OSTYPE": "linux-gnu", "MACHTYPE": "x86_64-pc-linux-gnu", "HOSTTYPE": "x86_64"}
        if name in fixed:
            return fixed[name]
        if name in ("UID", "EUID"):
            return "0" if self.user == "root" else "1000"
        if name == "LINENO":
            return str(self._lineno)
        if name == "SECONDS":
            return str(int(time.time() - self._seconds_base))
        if name == "EPOCHSECONDS":
            return str(int(time.time()))
        if name == "EPOCHREALTIME":
            return f"{time.time():.6f}"
        return None

    @property
    def _option_flags(self) -> str:
        """$-: the single-letter options that are on."""
        return (("e" if self.errexit else "") + ("f" if self.noglob else "") + "h" + ("u" if self.nounset else "")
                + ("x" if self.xtrace else "") + "B" + ("C" if self.noclobber else "") + "c")

    # ---------- arrays ----------

    def writable(self, name: str, assoc: bool = False) -> ShellArray:
        """The array to change: created if missing, copied first if a saved state still shares it."""
        name = self.resolve_name(name)
        self._check_writable(name)
        a = self.arrays.get(name)
        if a is None:
            a = ShellArray(assoc, self._epoch)
            if name in self.env:
                a.set("0", self.env.pop(name))  # a plain variable becomes element 0
            self.arrays[name] = a
            return a
        if a.epoch != self._epoch:
            a = self.arrays[name] = a.clone(self._epoch)
        return a

    def declare_array(self, name: str, assoc: bool, local: bool) -> None:
        """`local arr` / `declare -A m`: a fresh array, restored to the caller's when the function returns."""
        self._check_writable(name)
        if local and self._local_arrays:
            self._local_arrays[-1].setdefault(name, self.arrays.get(name))
        elif name in self.arrays and self.arrays[name].assoc == assoc:
            return
        if local and self._locals:
            self.declare_local(name, None)
        self.env.pop(name, None)
        self.arrays[name] = ShellArray(assoc, self._epoch)

    def array_key(self, array: ShellArray | None, subscript: str, name: str = "") -> str:
        """The storage key for a subscript: the text itself for an associative array, else its arithmetic value."""
        if array is not None and array.assoc:
            return subscript
        i = arith.evaluate(self, subscript)
        if i < 0:
            i += array.next if array is not None else 1
        if i < 0:
            raise ShellError(diagnostic("badArraySubscript", name=name))
        return str(i)

    def get_element(self, name: str, subscript: str) -> str | None:
        """One element, or None if unset. A plain variable answers as element 0."""
        name = self.resolve_name(name)
        a = self.get_array(name)
        if a is not None:
            return a.get(self.array_key(a, subscript, name))
        scalar = self.env.get(name)
        return scalar if scalar is not None and self.array_key(None, subscript, name) == "0" else None

    def set_element(self, name: str, subscript: str, value: str) -> None:
        a = self.writable(name)
        key = self.array_key(a, subscript, name)
        old = a.get(key)
        self.charge_variable(name, len(key) + len(value), len(key) + len(old) if old is not None else 0)
        a.set(key, value)

    def unset(self, target: str, nameref: bool = False) -> None:
        """unset NAME or unset 'NAME[sub]'; nameref unsets a nameref itself, not what it names."""
        if not nameref:
            target = self.resolve_name(target)
        r = array_ref(target)
        if r is not None and not r[0] and r[3] == "" and r[1] in self.arrays and r[2] not in ("@", "*"):
            a = self.writable(r[1])
            a.remove(self.array_key(a, _unquote(r[2]), r[1]))
            return
        name = r[1] if r is not None else target
        if self.has_attr(name, "r"):
            raise ShellError(diagnostic("cannotUnsetReadonly", name=name))
        self.env.pop(name, None)
        self.arrays.pop(name, None)
        self.var_attrs.pop(name, None)
        self._unexported.discard(name)
        if name == "IFS":
            self._ifs_unset = True

    async def _array_elements(self, expr: str, io_: _Io, quoted: bool = False) -> list[str] | None:
        """The elements ${name[@]...} stands for (keys for ${!name[@]}); None if expr is not a whole-array expansion."""
        r = array_ref(expr)
        if r is None or r[2] not in ("@", "*"):
            return None
        keys_, rname, sub, tail = r
        a = self.get_array(rname)
        name = self.resolve_name(rname)
        if a is None:
            scalar = self.env.get(name)
            items = [] if scalar is None else ["0" if keys_ else scalar]
        else:
            items = a.keys() if keys_ else a.values()
        if keys_ or not tail:
            return items
        d = default_op(tail)
        if d is not None:
            op, word = d
            # ${a[@]:-word}: the array as a whole is tested; empty means no elements (or, with ':', nothing but empty text)
            missing = not items or (op[0] == ":" and not (self._ifs_joiner if sub == "*" else " ").join(items))
            if op.endswith("?") and missing:
                msg = await self._param_word_expand(word, quoted, io_) if word else "parameter null or not set"
                raise self._fatal(io_, f"{rname}[{sub}]: {msg}", 127)
            if op.endswith("=") and missing:
                raise ShellError(diagnostic("badArraySubscript", name=f"{rname}[{sub}]"))
            if op.endswith("-"):
                return [await self._param_word_expand(word, quoted, io_)] if missing else items
            if op.endswith("+"):
                return [] if missing else [await self._param_word_expand(word, quoted, io_)]
            return items
        if tail[0] == ":":
            spec = tail[1:].split(":", 1)
            frm = await self._arith(spec[0], io_)
            count = (1 << 62) if len(spec) == 1 else await self._arith(spec[1], io_)
            if count < 0:
                raise ShellError(diagnostic("substringNegative", expression=spec[1].strip()))
            # an indexed array is sliced by index, so a sparse array skips its gaps: offsets count from the first index >= from
            if a is not None and not a.assoc:
                ks = [int(k) for k in a.keys()]
                if frm < 0:
                    frm += 0 if not ks else ks[-1] + 1
                if frm < 0:
                    return []
                first = next((i for i, k in enumerate(ks) if k >= frm), -1)
                return [] if first < 0 else items[first:first + count]
            if frm < 0:
                frm += len(items)
            if frm < 0 or frm > len(items):
                return []
            return items[frm:frm + count]
        # any other operator applies to each element: ${files[@]%.txt}, ${names[@]^^}
        return [await self._element_op(item, tail, io_, quoted) for item in items]

    async def _element_op(self, value: str | None, rest: str, io_: _Io, quoted: bool = False) -> str:
        """Applies a ${...} operator to one value by expanding it through a scratch variable."""
        if not rest:
            return value or ""
        if value is None:
            self.env.pop(_SCRATCH, None)
        else:
            self.env[_SCRATCH] = value
        try:
            return await self._expand_parameter(_SCRATCH + rest, io_, quoted)
        finally:
            self.env.pop(_SCRATCH, None)

    async def expand_subscript(self, target: str, io_: _Io) -> str:
        """`unset 'a[${#a[@]}-1]'`: name[sub] with the subscript's expansions done (the subscript stays text otherwise)."""
        r = array_ref(target)
        if r is not None and not r[0] and r[3] == "" and ("$" in r[2] or "`" in r[2]):
            return f"{r[1]}[{await self._subscript(r[2], io_)}]"
        return target

    async def _subscript(self, sub: str, io_: _Io) -> str:
        if not any(c in sub for c in "$`\"'"):
            return sub
        return await self._expand_single(Word(Lexer(_unquote(sub)).lex_quoted(None)), io_)

    async def _assign_array(self, a: Assignment, io_: _Io) -> None:
        name = self.resolve_name(a.name)
        if a.elements is None:
            key = await self._expand_single(a.index, io_)
            value = await self._expand_assignment(a.value, io_)
            if self.xtrace:
                io_.err.write(f"+ {name}[{key}]{'+=' if a.append else '='}{trace_quote(value)}\n")
            try:
                self.set_var(f"{name}[{key}]", value, a.append)
            except arith.ArithError as e:
                raise self._fatal(io_, str(e), 1) from None
            return
        if a.append:
            target = self.writable(name)
        else:
            self._check_writable(name)
            old = self.arrays.get(name)
            target = ShellArray(old is not None and old.assoc, self._epoch)
            self.env.pop(name, None)
        for w in Parser.parse_words(a.elements):
            # [key]=value sets that element; anything else is appended, after the usual splitting and globbing
            first = w.parts[0] if w.parts else None
            split = split_subscript(w, 1) if isinstance(first, Literal) and not first.quoted and first.text.startswith("[") else None
            if split is not None:
                target.set(self.array_key(target, await self._expand_single(split[0], io_)), await self._expand_single(split[1], io_))
            else:
                for f in await self._expand_words([w], io_):
                    target.set(str(target.next), f)
        self.arrays[name] = target

    async def _declare_arrays(self, words, io_: _Io):
        """`local a=(x y)`, `declare -A m`, `declare -a list=()`: the array arguments are handled here, because a built-in
        only receives expanded text. Returns the words left for the built-in, or None if none were array declarations."""
        flags = "".join(t for t in (w.unquoted_literal for w in words[1:]) if t and t.startswith("-"))
        if any(f in flags for f in "pfF"):
            return None
        local = words[0].is_("local") or (self.in_function and "g" not in flags)
        assoc = "A" in flags
        any_ = False
        attrs = "".join(c for c in flags if c in "rx") + ("r" if words[0].is_("readonly") else "")
        keep = [words[0]]
        for w in words[1:]:
            lit = w.unquoted_literal
            if lit and lit.startswith("-"):
                continue
            assignment = try_assignment(w)
            if assignment is not None and assignment.elements is not None:
                self.declare_array(assignment.name, assoc, local)
                await self._assign_array(assignment, io_)
            elif assignment is None and (assoc or "a" in flags) and lit and is_name(lit):
                self.declare_array(lit, assoc, local)
            else:
                keep.append(w)
                continue
            if attrs:
                self.set_attrs(assignment.name if assignment is not None else lit, attrs)
            any_ = True
        return keep if any_ else None

    # ---------- expansion ----------

    def get_variable(self, name: str) -> str:
        if name and name[0].isdigit() and name.isdigit():
            n = int(name)
            return self._arg0 if n == 0 else self.positional[n - 1] if n <= len(self.positional) else ""
        if name == "?":
            return str(self.last_exit_code)
        if name == "$":
            return "4242"
        if name == "!":
            return "" if self._last_bg == 0 else str(self._last_bg)
        if name == "#":
            return str(len(self.positional))
        if name == "@":
            return " ".join(self.positional)
        if name == "*":
            return self._ifs_joiner.join(self.positional)
        if name == "-":
            return self._option_flags
        if name == "_":
            return self._last_arg
        if name == "PWD":
            return self._cwd
        if name == "RANDOM":
            return str(random.randrange(32768))
        v = self._look_up(name)
        return "" if v is None else v

    def _look_up(self, name: str) -> str | None:
        """A named variable's value (element 0 of an array), or None when it is unset."""
        name = self.resolve_name(name)
        a = self.get_array(name)
        if a is not None:
            return a.get("0")
        v = self.env.get(name)
        return v if v is not None else self._shell_variable(name)

    @property
    def ifs(self) -> str:
        """The IFS that splitting uses: the default when it is unset."""
        return self.env.get("IFS", " \t\n")

    @property
    def _ifs_joiner(self) -> str:
        """"$*" and "${a[*]}" join with the first character of IFS (a space when IFS is unset)."""
        ifs = self.env.get("IFS")
        return " " if ifs is None else ifs[:1]

    def _is_set(self, name: str) -> bool:
        if name in ("?", "$", "#", "@", "*", "-", "_", "PWD", "RANDOM", "0"):
            return True
        if name and name[0].isdigit():
            return name.isdigit() and int(name) <= len(self.positional)
        if name == "!":
            return self._last_bg != 0
        return self._look_up(name) is not None

    async def _expand_part(self, part, io_: _Io) -> str:
        if isinstance(part, (Literal, ExpandedText)):
            return part.text
        if isinstance(part, Variable):
            try:
                return await self._expand_parameter(part.name, io_, part.quoted)
            except _BadSubstitution:
                raise self._fatal(io_, diagnostic("badSubstitution", text=self._bad_substitution_text(part)), 127) from None
        if isinstance(part, CommandSub):
            return await self._capture(part.source, io_)
        if isinstance(part, ArithSub):
            return str(await self._arith_expansion(part.expr, io_))
        if isinstance(part, ProcSub):
            return await self._process_substitution(part.source, io_)
        return ""

    async def _arith(self, expr: str, io_: _Io) -> int:
        """Expands $(cmd), `cmd` and ${...} inside an arithmetic expression, then evaluates it."""
        # each level of $(( $(( ... )) )) re-reads what is inside it, so unbounded nesting is quadratic work
        self._arith_depth += 1
        if self._arith_depth > 100:
            self._arith_depth = 0
            raise LimitExceeded("arithmetic is nested too deeply")
        try:
            # re-lexing each level costs O(n), so measure deep $(( $(( ... )) )) in one pass before expanding it
            if self._arith_depth == 1 and expr.count("$((") >= 100 and _arith_nesting(expr) > 100:
                raise LimitExceeded("arithmetic is nested too deeply")
            if "$" in expr or "`" in expr:
                expr = await self._expand_single(Word(Lexer(expr).lex_quoted(None)), io_)
            try:
                return arith.evaluate(self, expr)
            except RecursionError:
                raise ShellError(DIAGNOSTICS["expressionNested"]) from None
        finally:
            if self._arith_depth > 0:
                self._arith_depth -= 1

    async def _arith_expansion(self, expr: str, io_: _Io) -> int:
        """$(( )): unlike (( )) and let, an error here ends the script, as in bash."""
        try:
            return await self._arith(expr, io_)
        except ShellError as e:
            raise self._fatal(io_, str(e), 1) from None

    def _bad_substitution_text(self, part: Variable) -> str:
        """What bash names in a bad substitution: the double-quoted string around it, or else the whole word."""
        parts = self._cur_word.parts if self._cur_word is not None else []
        at = next((i for i, p in enumerate(parts) if p is part), None)
        if at is None:
            return "${" + part.name + "}"
        if not part.quoted:
            return str(self._cur_word)
        # shortcut: a run of quoted parts stands for the "..." string ('a'"b" joins), and $x prints as ${x}
        lo, hi = at, at + 1
        while lo > 0 and parts[lo - 1].quoted:
            lo -= 1
        while hi < len(parts) and parts[hi].quoted:
            hi += 1
        return str(Word(parts[lo:hi]))

    def _fatal(self, io_: _Io, message: str, code: int) -> ExitSignal:
        """An expansion error that ends a non-interactive bash: the shell exits (a subshell with status 1)."""
        io_.err.write(f"{self.err_prefix}{message}\n")
        return ExitSignal(1 if self._subshell > 0 else code)

    def _indirect_target(self, expr: str) -> str | None:
        """${!name...}: the expression with name's value in its place; None if expr is not an indirection."""
        if len(expr) < 2 or expr[0] != "!" or _PREFIX.match(expr):
            return None
        r = array_ref(expr)
        if r is not None and r[0]:
            return None
        m = _INDIRECT.match(expr)
        if not m:
            return None
        name = m.group(1)
        if self.has_attr(name, "n"):
            return None  # ${!ref} of a nameref is the name it refers to
        target = self.get_variable(name)
        if not target:
            raise ShellError(diagnostic("invalidIndirect", name=name))
        return target + m.group(2)

    def _param_word(self, text: str, quoted: bool) -> Word:
        """The word of ${x op word}, lexed once per distinct text: quotes in it apply, and inside "..." a ' is literal."""
        key = (text, quoted)
        w = self._param_words.get(key)
        if w is not None:
            return w
        if len(self._param_words) >= 512:
            self._param_words.clear()
        try:
            w = Word(Lexer(text).lex_param_word(quoted))
        except ParseError as e:
            raise ShellError(str(e)) from None
        self._param_words[key] = w
        return w

    async def _param_word_expand(self, text: str, quoted: bool, io_: _Io) -> str:
        return await self._expand_single(self._param_word(text, quoted), io_)

    async def _replacement(self, text: str, quoted: bool, io_: _Io) -> list:
        """${x/pat/rep}'s replacement as pieces: text, or None where an unquoted & stands for the match (bash 5.2)."""
        pieces: list = []
        first = True
        for part in self._param_word(text, quoted).parts:
            t = await self._expand_part(part, io_)
            if first and isinstance(part, Literal) and not part.quoted:
                tp = self._tilde_prefix(t)
                if tp:
                    t = tp[0] + t[tp[1]:]
            first = False
            if part.quoted or "&" not in t:
                pieces.append(t)
            else:
                for k, chunk in enumerate(t.split("&")):
                    if k:
                        pieces.append(None)
                    pieces.append(chunk)
        return pieces

    async def _expand_parameter(self, expr: str, io_: _Io, quoted: bool = False) -> str:
        """${name}, ${#name}, ${name:-x}, ${name%pat}, ${name/a/b}, ${name:1:2}, ${name^^}, ${name@Q}, ${!name} ..."""
        if len(expr) > 1 and expr[0] == "!":
            pm = _PREFIX.match(expr)
            if pm:
                return " ".join(sorted({k for k in list(self.env) + list(self.arrays) if k.startswith(pm.group(1)) and k != _SCRATCH}))
            if is_name(expr[1:]) and self.has_attr(expr[1:], "n"):
                return self.env.get(expr[1:], "")
            target = self._indirect_target(expr)
            if target is not None:
                return await self._expand_parameter(target, io_, quoted)
        if len(expr) > 1 and expr[0] == "#":
            counted = array_ref(expr[1:])
            if counted is not None and not counted[0] and counted[3] == "":
                if counted[2] in ("@", "*"):
                    a = self.get_array(counted[1])
                    return str(len(a.items) if a is not None else 1 if self._look_up(counted[1]) is not None else 0)
                return str(len(self.get_element(counted[1], await self._subscript(counted[2], io_)) or ""))
        # as one string, ${a[*]} joins with the first character of IFS and ${a[@]} with a space
        all_ = await self._array_elements(expr, io_, quoted)
        if all_ is not None:
            return (self._ifs_joiner if array_ref(expr)[2] == "*" else " ").join(all_)
        one = array_ref(expr)
        if one is not None and not one[0]:
            try:
                element = self.get_element(one[1], await self._subscript(one[2], io_))
            except arith.ArithError as e:
                raise self._fatal(io_, str(e), 1) from None
            except ShellError as e:
                io_.err.write(f"{self.err_prefix}{e}\n")  # like bash: reported, and the expansion is empty
                element = None
            return await self._element_op(element, one[3], io_, quoted)
        if len(expr) > 1 and expr[0] == "#":
            if expr[1:] in ("@", "*"):
                return str(len(self.positional))
            self._check_set(expr[1:], io_)
            return str(len(self.get_variable(expr[1:])))
        m = _PARAM.match(expr)
        if not m:
            raise _BadSubstitution(expr)
        name, rest = m.group(1), m.group(2)
        value = self.get_variable(name)
        d = default_op(rest)
        if d is None:
            self._check_set(name, io_)  # defaults handle unset variables
        if not rest:
            return value

        if d is not None:
            op, word = d
            missing = not value if op[0] == ":" else not self._is_set(name)
            kind = op[-1]
            if kind == "-":
                return await self._param_word_expand(word, quoted, io_) if missing else value
            if kind == "=":
                if not missing:
                    return value
                if not is_name(name):
                    raise ShellError(diagnostic("cannotAssign", name=name))
                self.set_var(name, await self._param_word_expand(word, quoted, io_))
                return self.get_variable(name)
            if kind == "+":
                return "" if missing else await self._param_word_expand(word, quoted, io_)
            if missing:
                msg = (await self._param_word_expand(word, quoted, io_) if word
                       else "parameter null or not set" if op[0] == ":" else "parameter not set")
                raise self._fatal(io_, f"{name}: {msg}", 127)
            return value
        if rest[0] in "#%":
            longest = len(rest) > 1 and rest[1] == rest[0]
            rx = glob_to_regex(await self._pattern(self._param_word(rest[2 if longest else 1:], quoted), io_), slashes=True)
            n = len(value)
            if rest[0] == "#":
                for i in (range(n, -1, -1) if longest else range(n + 1)):
                    if rx.match(value[:i]):
                        return value[i:]
            else:
                for i in (range(n + 1) if longest else range(n, -1, -1)):
                    if rx.match(value[i:]):
                        return value[:i]
            return value
        if rest[0] == "/":
            body = rest[1:]
            mode = body[0] if body and body[0] in "/#%" else ""
            if mode:
                body = body[1:]
            # the pattern ends at the first unquoted '/'; after `//` a leading '/' is part of the pattern
            slash = -1
            i = 1 if mode == "/" and body.startswith("/") else 0
            while i < len(body):
                ch = body[i]
                if ch == "\\":
                    i += 2
                    continue
                if ch == "'" and not quoted:
                    q = body.find("'", i + 1)
                    i = (q if q >= 0 else len(body)) + 1
                    continue
                if ch == '"':
                    i += 1
                    while i < len(body) and body[i] != '"':
                        if body[i] == "\\":
                            i += 1
                        i += 1
                    i += 1
                    continue
                if ch == "/":
                    slash = i
                    break
                i += 1
            pat = glob_to_regex_body(await self._pattern(self._param_word(body if slash < 0 else body[:slash], quoted), io_), slashes=True)
            rep = [] if slash < 0 else await self._replacement(body[slash + 1:], quoted, io_)

            def sub(m: re.Match) -> str:
                return "".join(m.group(0) if p is None else p for p in rep)

            # an empty pattern matches nothing, except at an anchor: ${x/#/pre-} and ${x/%/-post} add to the ends
            if not pat:
                plain = "".join("" if p is None else p for p in rep)
                return plain + value if mode == "#" else value + plain if mode == "%" else value
            rx = _cached(("^(?:" + pat + ")") if mode == "#" else ("(?:" + pat + ")$") if mode == "%" else pat, re.S)
            # ${x//?/$x} grows with the square of x: refuse before building it
            rep_len = sum(len(p) for p in rep if p is not None)
            if mode == "/" and len(value) + rep_len * (len(value) + 1) > self.max_string_bytes \
                    and len(value) + rep_len * len(rx.findall(value)) > self.max_string_bytes:
                raise self.too_large("expansion", "write the data to a file instead")
            return rx.sub(sub, value, count=0 if mode == "/" else 1)
        if rest[0] in "^,~":
            # ${x^} ${x^^} ${x,} ${x,,} ${x~} ${x~~}, optionally only for characters matching a pattern: ${x^^[aeiou]}
            every = len(rest) > 1 and rest[1] == rest[0]
            tail = rest[2 if every else 1:]
            only = glob_to_regex(await self._pattern(self._param_word(tail, quoted), io_), slashes=True) if tail else None
            chars = list(value)
            for i in range(len(chars)):
                if not every and i > 0:
                    break
                if only is None or only.match(chars[i]):
                    up = rest[0] == "^" or (rest[0] == "~" and chars[i].islower())
                    chars[i] = chars[i].upper() if up else chars[i].lower()
            return "".join(chars)
        if len(rest) == 2 and rest[0] == "@":
            from .commands import unescape
            from .commands.more import attribute_flags
            if rest[1] == "a":
                return attribute_flags(self, self.resolve_name(name))
            if not self._is_set(name):
                return ""
            c = rest[1]
            if c in "QK":
                return quote_value(value)
            if c == "E":
                return unescape(value, keep_unknown=True)
            if c == "U":
                return value.upper()
            if c == "L":
                return value.lower()
            if c == "u":
                return value[:1].upper() + value[1:]
            if c == "A":
                flags = attribute_flags(self, self.resolve_name(name))
                return f"declare -{flags} {name}={quote_value(value)}" if flags else f"{name}={quote_value(value)}"
            if c == "P":
                return value
            raise _BadSubstitution(expr)
        if rest[0] == ":":
            spec = rest[1:].split(":", 1)
            offset = await self._arith(spec[0], io_)
            if offset < 0:
                offset += len(value)
            if offset < 0 or offset > len(value):
                return ""
            tail = value[offset:]
            if len(spec) == 1:
                return tail
            length = await self._arith(spec[1], io_)
            if length < 0:
                length += len(tail)
                if length < 0:
                    raise ShellError(diagnostic("substringNegative", expression=spec[1].strip()))
            return tail[:length]
        raise _BadSubstitution(expr)

    def _check_set(self, name: str, io_: _Io) -> None:
        """set -u: expanding an unset variable is an error that ends the (sub)shell, like bash."""
        if not self.nounset or name in ("@", "*") or self._is_set(name):
            return
        raise self._fatal(io_, diagnostic("unboundVariable", name=name), 127)

    async def _run_substitution(self, source: str, io_: _Io) -> int:
        """$(...) and <(...) run in a subshell that, as in bash, does not inherit set -e (unless shopt inherit_errexit)."""
        errexit = self.errexit
        if "inherit_errexit" not in self.shopts:
            self.errexit = False
        try:
            return await self._run_script_text(source, io_, subshell=True)
        finally:
            self.errexit = errexit

    async def _process_substitution(self, source: str, io_: _Io) -> str:
        """<(cmd): writes cmd's output to /dev/fd/N (removed after the tool call) and returns that path."""
        out = CappedWriter(self)
        await self._run_substitution(source, replace(io_, stdin=StdinBuffer(), out=out))
        path = f"/dev/fd/{63 + self._proc_sub_count}"
        self._proc_sub_count += 1
        with self.fs.as_root():
            self.fs.write_text(path, out.getvalue())
        return path

    async def _capture(self, source: str, io_: _Io) -> str:
        file_word = _read_file_word(source)
        if file_word is not None:  # $(< file) is the file's contents
            target = await self._expand_single(file_word, io_)
            try:
                text = self.fs.read_text(self.resolve(target))
                self.last_exit_code = 0
            except ShellError as e:
                io_.err.write(f"{self.err_prefix}{target}: {str(e).rsplit(': ', 1)[-1]}\n")
                text, self.last_exit_code = "", 1
            return text.rstrip("\n")
        out = CappedWriter(self)
        self.last_exit_code = await self._run_substitution(source, replace(io_, stdin=StdinBuffer(), out=out))
        return out.getvalue().rstrip("\n")

    async def _expand_single(self, word: Word, io_: _Io) -> str:
        """Expands a word without field splitting or globbing (redirect targets, heredocs, case subjects)."""
        out = []
        total = 0
        saved, self._cur_word = self._cur_word, word
        for i, part in enumerate(word.parts):
            text = await self._expand_part(part, io_)
            self._cur_word = word
            if i == 0 and isinstance(part, Literal) and not part.quoted:
                tp = self._tilde_prefix(text)
                if tp:
                    text = tp[0] + text[tp[1]:]
            total += len(text)
            if total > self.max_string_bytes:
                raise self.too_large("expansion", "write the data to a file instead")
            out.append(text)
        self._cur_word = saved
        return "".join(out)

    async def _expand_assignment(self, word: Word, io_: _Io) -> str:
        """An assignment's value: one string, with ~ expanded at the start and after each ':' (PATH=~/bin:~/x)."""
        out: list[str] = []
        for i, part in enumerate(word.parts):
            self._cur_word = word
            text = await self._expand_part(part, io_)
            if isinstance(part, Literal) and not part.quoted and "~" in text:
                at_start = i == 0 or "".join(out).endswith(":")

                def tilde(m: re.Match) -> str:
                    if m.start() == 0 and not at_start:
                        return m.group(0)
                    v = self._tilde_value(m.group(1))
                    return m.group(0) if v is None else v

                text = _ASSIGNMENT_TILDE.sub(tilde, text)
            out.append(text)
        return "".join(out)

    async def _pattern(self, word: Word, io_: _Io) -> str:
        """Expands a word into a glob pattern: quoted text is escaped, unquoted text keeps its wildcards."""
        out = []
        for part in word.parts:
            text = await self._expand_part(part, io_)
            out.append(glob_escape(text) if part.quoted else text)
        return "".join(out)

    def _tilde_value(self, user: str) -> str | None:
        """~, ~user, ~+ and ~-: the directory they name, or None for an unknown user."""
        if user == "":
            return self.env.get("HOME", "/")
        if user == "+":
            return self._cwd
        if user == "-":
            return self.env.get("OLDPWD")
        if user == "root":
            return "/root"
        if user == self.user:
            return "/home/" + user
        return None

    def _tilde_prefix(self, text: str):
        """A leading ~ or ~user (up to the first '/'): (its directory, how many characters it spans)."""
        if not text.startswith("~"):
            return None
        end = text.find("/")
        if end < 0:
            end = len(text)
        home = self._tilde_value(text[1:end])
        return (home, end) if home is not None else None

    async def _alternative_word(self, expr: str, io_: _Io):
        """An unquoted ${p:-word} or ${p:+word} whose word applies: the word's parts, so quotes inside the word still keep
        it from being split (${v:-"a b"} is one field). None when p's own value applies."""
        element = array_ref(expr)
        match = _PARAM.match(expr) if element is None else None
        if element is not None and not element[0] and element[2] not in ("@", "*"):
            rest = element[3]
        elif match:
            rest = match.group(2)
        else:
            rest = ""
        d = default_op(rest)
        if d is None or d[0][-1] not in "-+":
            return None
        op, word = d
        if element is not None:
            value = self.get_element(element[1], await self._subscript(element[2], io_))
        else:
            value = self.get_variable(match.group(1)) if self._is_set(match.group(1)) else None
        missing = not value if op[0] == ":" else value is None
        if (not missing) if op[-1] == "-" else missing:
            return None if op[-1] == "-" else []
        # the word's unquoted text is split like any expansion's
        return [ExpandedText(p.text) if isinstance(p, Literal) and not p.quoted else p for p in self._param_word(word, False).parts]

    async def _expand_words(self, words, io_: _Io) -> list[str]:
        result: list[str] = []
        total = 0  # all the words together, so {1..10000}$big can't multiply a big value
        for word in [w for word in words for w in brace_expand(word)]:
            fields: list[_Field] = []
            cur = _Field()

            def end():
                nonlocal cur
                if cur.has_content:
                    fields.append(cur)
                cur = _Field()

            def add(s: str, quoted: bool):
                nonlocal total
                total += len(s)
                if total > self.max_string_bytes:
                    raise self.too_large("expansion", "write the data to a file instead")
                cur.text.append(s)
                cur.pattern.append(glob_escape(s) if quoted else s)
                if not quoted and has_wildcard(s):
                    cur.has_glob = True
                cur.has_content = True

            def add_split(text: str):
                # unquoted $VAR / $(cmd) / $((...)): split into fields on $IFS
                ifs = self.ifs
                pieces = split_fields(text, ifs)
                if not pieces:
                    if text and ifs:
                        end()
                    return
                if text[0] in ifs:
                    end()
                for k, piece in enumerate(pieces):
                    if k:
                        end()
                    add(piece, False)
                if text[-1] in ifs:
                    end()

            def add_each(items, quoted: bool):
                # "$@" / "${a[@]}": one field per item, and none at all when there are none; unquoted, each item is split
                for k, item in enumerate(items):
                    if k:
                        end()
                    if quoted:
                        add(item, True)
                    else:
                        add_split(item)

            parts = list(word.parts)
            i = 0
            while i < len(parts):
                part = parts[i]
                if isinstance(part, Variable):
                    name = self._indirect_target(part.name) or part.name
                    if name == "@" or (name == "*" and not part.quoted):
                        add_each(self.positional, part.quoted)
                        i += 1
                        continue
                    if len(name) > 1 and name[0] in "@*" and name[1] == ":" and default_op(name[1:]) is None:
                        # ${@:2} and ${@:2:3} slice the parameters, not the characters; ${@:0} starts at $0
                        spec = name[2:].split(":", 1)
                        all_ = ["bash"] + self.positional
                        frm = await self._arith(spec[0], io_)
                        if frm < 0:
                            frm += len(all_)
                        count = len(all_) if len(spec) == 1 else await self._arith(spec[1], io_)
                        items = [] if frm < 0 or frm > len(all_) else all_[frm:frm + max(0, min(count, len(all_)))]
                        if part.quoted and name[0] == "*":
                            add(self._ifs_joiner.join(items), True)
                        else:
                            add_each(items, part.quoted)
                        i += 1
                        continue
                    ar = array_ref(name)
                    if ar is not None and ar[2] in ("@", "*") and (ar[2] == "@" or not part.quoted):
                        elements = await self._array_elements(name, io_, part.quoted)
                        if elements is not None:
                            add_each(elements, part.quoted)
                            i += 1
                            continue
                    if not part.quoted and ("-" in name or "+" in name):
                        alternative = await self._alternative_word(name, io_)
                        if alternative is not None:
                            parts[i:i + 1] = alternative
                            continue
                    if name != part.name:
                        part = Variable(name, part.quoted)
                self._cur_word = word
                text = await self._expand_part(part, io_)
                if isinstance(part, Literal):
                    if i == 0 and not part.quoted:
                        tp = self._tilde_prefix(text)
                        if tp:
                            add(tp[0], True)  # the home path is never globbed
                            text = text[tp[1]:]
                            if not text:
                                i += 1
                                continue
                    add(text, part.quoted)
                elif part.quoted:
                    add(text, True)
                else:
                    add_split(text)
                i += 1
            end()

            for f in fields:
                glob = f.has_glob and not self.noglob
                matches = glob_expand(self, "".join(f.pattern)) if glob else []
                if matches:
                    result.extend(matches)
                elif not (glob and "nullglob" in self.shopts):
                    result.append("".join(f.text))
        return result


def _arith_nesting(expr: str) -> int:
    """How deeply $(( ... )) nest in expr (parentheses inside them are counted as they open and close)."""
    depth = deepest = 0
    stack: list[bool] = []
    for m in re.finditer(r"\$\(\(|\(|\)", expr):
        if m.group() == ")":
            if stack and stack.pop():
                depth -= 1
        else:
            if m.group() == "(":
                stack.append(False)
            else:  # $(( opens two parentheses; the outer one's close ends the arithmetic
                stack += [True, False]
                depth += 1
                deepest = max(deepest, depth)
    return deepest


def _with_fd(io_: _Io, fd: int, target) -> _Io:
    if fd == 0:
        return replace(io_, stdin=target) if isinstance(target, StdinBuffer) else io_
    if fd in (1, 2):
        if isinstance(target, StdinBuffer):
            return io_
        return replace(io_, out=target) if fd == 1 else replace(io_, err=target)
    return replace(io_, fds={**(io_.fds or {}), fd: target})


def split_fields(text: str, ifs: str, max_fields: int = 0) -> list[str]:
    """Splits text on IFS like bash: IFS whitespace runs collapse and are trimmed at the ends, every other IFS
    character delimits one field. max_fields > 0 leaves the rest of the line in the last field (`read a b`)."""
    if not ifs:
        return [text] if text else []
    ws = "".join(c for c in " \t\n" if c in ifs)
    other = "".join(c for c in ifs if c not in ws)
    text = text.strip(ws) if ws else text
    if not text:
        return []
    ws_rx = f"[{re.escape(ws)}]" if ws else ""
    if not other:
        sep = ws_rx + "+"
    elif not ws:
        sep = f"[{re.escape(other)}]"
    else:
        sep = f"{ws_rx}*[{re.escape(other)}]{ws_rx}*|{ws_rx}+"
    fields = [text] if max_fields == 1 else _cached(sep, 0).split(text, maxsplit=max(0, max_fields - 1))
    if max_fields <= 0 and other and fields and fields[-1] == "":
        fields.pop()  # "a,b," is two fields, not three
    return fields


def _read_file_word(source: str):
    """The file word of `$(< file)`, or None when the substitution is anything else."""
    if "<" not in source:
        return None
    try:
        script = Parser.parse(source)
    except ParseError:
        return None
    if len(script.items) != 1 or script.items[0].rest or len(script.items[0].first.commands) != 1:
        return None
    cmd = script.items[0].first.commands[0]
    if isinstance(cmd, SimpleCommand) and not cmd.words and not cmd.assignments and len(cmd.redirects) == 1 \
            and cmd.redirects[0].op == "<" and not script.items[0].first.negate:
        return cmd.redirects[0].target
    return None


class _Field:
    def __init__(self):
        self.text: list[str] = []
        self.pattern: list[str] = []
        self.has_glob = False
        self.has_content = False


class _CondEvaluator:
    """Evaluates [[ ... ]]."""

    UNARY = {"-z", "-n", "-e", "-f", "-d", "-s", "-r", "-w", "-x", "-L", "-h", "-a", "-v", "-o", "-t", "-b", "-c", "-p", "-S",
             "-g", "-u", "-k", "-O", "-G", "-N"}

    def __init__(self, shell: Shell, items: list[Word], io_: _Io):
        self.sh, self.items, self.io, self.p = shell, items, io_, 0

    def is_op(self, s: str) -> bool:
        return self.p < len(self.items) and self.items[self.p].is_(s)

    async def evaluate(self) -> bool:
        if not self.items:
            raise ShellError(DIAGNOSTICS["conditionalSyntax"], 2)
        v = await self.or_()
        if self.p < len(self.items):
            raise ShellError(diagnostic("conditionalSyntaxNear", token=str(self.items[self.p])), 2)
        return v

    async def or_(self) -> bool:
        left = await self.and_()
        while self.is_op("||"):
            self.p += 1
            right = await self.and_()
            left = left or right
        return left

    async def and_(self) -> bool:
        left = await self.not_()
        while self.is_op("&&"):
            self.p += 1
            right = await self.not_()
            left = left and right
        return left

    async def not_(self) -> bool:
        if self.is_op("!"):
            self.p += 1
            return not await self.not_()
        return await self.primary()

    async def primary(self) -> bool:
        items, sh = self.items, self.sh
        if self.is_op("("):
            self.p += 1
            v = await self.or_()
            if not self.is_op(")"):
                raise ShellError(DIAGNOSTICS["conditionalParen"], 2)
            self.p += 1
            return v
        if self.p >= len(items):
            raise ShellError(DIAGNOSTICS["conditionalEnd"], 2)
        u = items[self.p].unquoted_literal
        if u in self.UNARY and self.p + 1 < len(items) and not (self.p + 2 < len(items) and is_cond_binary(items[self.p + 1])):
            arg = await sh._expand_single(items[self.p + 1], self.io)
            self.p += 2
            from .commands import test_unary

            return test_unary(sh, {"-a": "-e", "-h": "-L"}.get(u, u), arg)
        left = items[self.p]
        self.p += 1
        if self.p < len(items) and is_cond_binary(items[self.p]):
            op = items[self.p].unquoted_literal
            self.p += 1
            if self.p >= len(items):
                raise ShellError(diagnostic("conditionalBinaryArgument", op=op), 2)
            right = items[self.p]
            self.p += 1
            lv = await sh._expand_single(left, self.io)
            nocase = "nocasematch" in sh.shopts
            if op in ("==", "=", "!="):
                match = bool(glob_to_regex(await sh._pattern(right, self.io), nocase, slashes=True).match(lv))
                return not match if op == "!=" else match
            if op == "=~":
                # quoted parts of the regex match literally
                parts = [await sh._expand_part(p, self.io) for p in right.parts]
                rv = "".join(re.escape(t) if p.quoted else t for p, t in zip(right.parts, parts))
                from .commands.text import posix_to_python
                try:
                    m = TimedPattern(_cached(posix_to_python(rv, True), re.IGNORECASE if nocase else 0)).search(lv)
                except (re.error, ShellError):
                    raise ShellError(f"invalid regular expression `{rv}'", 2) from None
                # BASH_REMATCH: the whole match, then each group; emptied when nothing matches
                groups = ShellArray(False)
                if m:
                    for g in range(len(m.groups()) + 1):
                        groups.set(str(g), m.group(g) or "")
                sh.env.pop("BASH_REMATCH", None)
                sh.arrays["BASH_REMATCH"] = groups
                return m is not None
            rv = await sh._expand_single(right, self.io)
            if op == "<":
                return lv < rv
            if op == ">":
                return lv > rv
            if op in ("-nt", "-ot"):
                from .commands import newer
                return newer(sh, lv, rv) if op == "-nt" else newer(sh, rv, lv)
            if op == "-ef":
                return sh.fs.same_file(sh.resolve(lv), sh.resolve(rv))
            a, b = arith.evaluate(sh, lv), arith.evaluate(sh, rv)
            return {"-eq": a == b, "-ne": a != b, "-lt": a < b, "-le": a <= b, "-gt": a > b, "-ge": a >= b}.get(op, False)
        return bool(await sh._expand_single(left, self.io))


# ---------- brace expansion ----------

_MAX_BRACE = 10_000
_PLACEHOLDER = 0xE000


def brace_expand(w: Word) -> list[Word]:
    """Brace expansion: a{b,c}d, {1..10}, {01..05}, {a..e}, {0..10..2}; a brace may span an expansion: {$x,y}."""
    if not any(isinstance(p, Literal) and not p.quoted and "{" in p.text for p in w.parts):
        return [w]
    # everything but unquoted text becomes one placeholder character, so braces work on plain text
    text: list[str] = []
    opaque: list = []
    for p in w.parts:
        if isinstance(p, Literal) and not p.quoted:
            text.append(p.text)
        else:
            text.append(chr(_PLACEHOLDER + len(opaque)))
            opaque.append(p)
    if len(opaque) > 0x1000:
        return [w]
    joined = "".join(text)
    results = _brace_text(joined)
    if len(results) == 1 and results[0] == joined:
        return [w]
    return [_rebuild(t, opaque) for t in results]


def _rebuild(text: str, opaque: list) -> Word:
    parts: list = []
    lit: list[str] = []
    for c in text:
        k = ord(c) - _PLACEHOLDER
        if not 0 <= k < len(opaque):
            lit.append(c)
            continue
        if lit:
            parts.append(Literal("".join(lit), False))
            lit.clear()
        parts.append(opaque[k])
    if lit or not parts:
        parts.append(Literal("".join(lit), False))
    return Word(parts)


def _brace_text(text: str) -> list[str]:
    open_ = text.find("{")
    while open_ >= 0:
        close = _brace_close(text, open_)
        alts = _brace_alternatives(text[open_ + 1:close]) if close >= 0 else None
        if alts is not None:
            results: list[str] = []
            for alt in alts:
                results.extend(_brace_text(text[:open_] + alt + text[close + 1:]))
                if len(results) > _MAX_BRACE:
                    raise ShellError(DIAGNOSTICS["braceTooMany"])
            return results
        open_ = text.find("{", open_ + 1)
    return [text]


def _brace_close(s: str, open_: int) -> int:
    depth = 0
    for i in range(open_, len(s)):
        if s[i] == "{":
            depth += 1
        elif s[i] == "}":
            depth -= 1
            if depth == 0:
                return i
    return -1


def _brace_alternatives(inner: str) -> list[str] | None:
    m = re.fullmatch(r"(-?\d+)\.\.(-?\d+)(?:\.\.(-?\d+))?", inner)
    if m:
        a, b = int(m.group(1)), int(m.group(2))
        step = abs(int(m.group(3))) if m.group(3) else 1
        step = step or 1
        pad = any(len(g) > 1 and g.lstrip("-").startswith("0") for g in (m.group(1), m.group(2)))
        width = max(len(m.group(1)), len(m.group(2)))
        if abs(b - a) // step > _MAX_BRACE:
            raise ShellError(DIAGNOSTICS["braceTooMany"])
        rng = range(a, b + 1, step) if a <= b else range(a, b - 1, -step)
        return [str(v).zfill(width) if pad else str(v) for v in rng]
    m = re.fullmatch(r"([a-zA-Z])\.\.([a-zA-Z])(?:\.\.(-?\d+))?", inner)
    if m:
        a, b = ord(m.group(1)), ord(m.group(2))
        step = max(1, abs(int(m.group(3)))) if m.group(3) else 1
        return [chr(c) for c in (range(a, b + 1, step) if a <= b else range(a, b - 1, -step))]
    parts, depth, start = [], 0, 0
    for i, c in enumerate(inner):
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        elif c == "," and depth == 0:
            parts.append(inner[start:i])
            start = i + 1
    parts.append(inner[start:])
    return parts if len(parts) >= 2 else None


# ---------- globbing ----------

def has_wildcard(s: str) -> bool:
    return any(c in s for c in "*?[") or "@(" in s or "+(" in s or "!(" in s


def glob_escape(s: str) -> str:
    return "".join("\\" + c if c in "*?[]\\()|@!+" else c for c in s)


def _has_unescaped_wildcard(seg: str) -> bool:
    i = 0
    while i < len(seg):
        if seg[i] == "\\":
            i += 2
            continue
        if seg[i] in "*?[" or (seg[i] in "@+!" and i + 1 < len(seg) and seg[i + 1] == "("):
            return True
        i += 1
    return False


_matching = 0  # > 0 while a user's regex runs: only there may the call's time limit interrupt Python code


class TimedPattern:
    """A compiled regex whose matching the call's time limit can stop: Python's re has no timeout, but it checks for
    signals while it backtracks, so the alarm _run_call sets raises CallTimeout from inside it (main thread only)."""
    __slots__ = ("_rx",)

    def __init__(self, rx: re.Pattern):
        self._rx = rx

    def __getattr__(self, name: str):
        return getattr(self._rx, name)

    def _run(self, fn, *args, **kwargs):
        global _matching
        _matching += 1
        try:
            return fn(*args, **kwargs)
        finally:
            _matching -= 1

    def search(self, *a, **k):
        return self._run(self._rx.search, *a, **k)

    def match(self, *a, **k):
        return self._run(self._rx.match, *a, **k)

    def fullmatch(self, *a, **k):
        return self._run(self._rx.fullmatch, *a, **k)

    def sub(self, *a, **k):
        return self._run(self._rx.sub, *a, **k)

    def subn(self, *a, **k):
        return self._run(self._rx.subn, *a, **k)

    def split(self, *a, **k):
        return self._run(self._rx.split, *a, **k)

    def findall(self, *a, **k):
        return self._run(self._rx.findall, *a, **k)

    def finditer(self, *a, **k):
        it = self._rx.finditer(*a, **k)
        while (m := self._run(next, it, None)) is not None:
            yield m


@functools.lru_cache(maxsize=1024)
def _cached(pattern: str, flags: int) -> re.Pattern:
    """A compiled-once regex: patterns repeat (a case in a loop), so building one per match would dominate."""
    return re.compile(pattern, flags)


def glob_to_regex_body(pattern: str, slashes: bool = False) -> str:
    out: list[str] = []
    _glob_body(pattern, 0, out, slashes, False)
    return "".join(out)


def _glob_body(p: str, i: int, out: list, slashes: bool, in_group: bool) -> int:
    """Translates pattern from i, stopping at an unescaped | or ) when inside an extglob group. Returns the stop index."""
    any_ = "." if slashes else "[^/]"
    while i < len(p):
        c = p[i]
        if in_group and c in "|)":
            return i
        if c in "@!*+?" and i + 1 < len(p) and p[i + 1] == "(" and _group_end(p, i + 1) >= 0:
            # extglob: @(a|b) one of, *(..) any number, +(..) at least one, ?(..) optional, !(..) anything else
            i += 2
            alternatives = []
            while True:
                alt: list[str] = []
                i = _glob_body(p, i, alt, slashes, True)
                alternatives.append("".join(alt))
                if i >= len(p) or p[i] == ")":
                    break
                i += 1  # past '|'
            group = "(?:" + "|".join(alternatives) + ")"
            # shortcut: !(..) is exact only at the end of a pattern (rm !(*.txt)); make it a real complement if agents nest it
            out.append({"@": group, "*": group + "*", "+": group + "+", "?": group + "?"}.get(c, f"(?:(?!{group}\\Z){any_}*)"))
            i += 1
            continue
        if c == "\\" and i + 1 < len(p):
            i += 1
            out.append(re.escape(p[i]))
        elif c == "*":
            out.append(any_ + "*")
        elif c == "?":
            out.append(any_)
        elif c == "[" and _bracket_end(p, i) > 0:
            close = _bracket_end(p, i)
            out.append(_bracket(p[i + 1:close]))
            i = close
        else:
            out.append(re.escape(c))
        i += 1
    return i


def _group_end(p: str, i: int) -> int:
    depth = 0
    while i < len(p):
        if p[i] == "\\":
            i += 2
            continue
        if p[i] == "(":
            depth += 1
        elif p[i] == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def _bracket_end(p: str, i: int) -> int:
    """The ']' closing the bracket expression that opens at i (allowing []...] and [[:class:]]), or -1."""
    k = i + 1
    if k < len(p) and p[k] in "!^":
        k += 1
    if k < len(p) and p[k] == "]":
        k += 1
    while k < len(p):
        if p[k] == "[" and k + 1 < len(p) and p[k + 1] in ":.=":
            e = p.find(p[k + 1] + "]", k + 2)
            if e < 0:
                return -1
            k = e + 2
            continue
        if p[k] == "\\":
            k += 2
            continue
        if p[k] == "]":
            return k
        k += 1
    return -1


def _bracket(inner: str) -> str:
    from .commands.text import _CLASSES
    out = ["["]
    k = 0
    if inner and inner[0] in "!^":
        out.append("^")
        k = 1
    while k < len(inner):
        c = inner[k]
        if c == "[" and k + 1 < len(inner) and inner[k + 1] == ":":
            e = inner.find(":]", k + 2)
            if e >= 0:
                out.append(_CLASSES.get(inner[k + 2:e], ""))
                k = e + 2
                continue
        if c == "\\" and k + 1 < len(inner):
            k += 1
            c = inner[k]
        if c in "\\[]^":
            out.append("\\")
        out.append(c)
        k += 1
    out.append("]")
    return "".join(out)


def glob_to_regex(pattern: str, ignore_case: bool = False, slashes: bool = False) -> re.Pattern:
    """slashes=True for case/[[ ]]/${x%pat} matching, where * also matches '/'."""
    return _cached("^" + glob_to_regex_body(pattern, slashes) + r"\Z", (re.IGNORECASE if ignore_case else 0) | re.S)


def _join(display: str, name: str) -> str:
    return name if display == "" else "/" + name if display == "/" else display + "/" + name


def glob_expand(shell: Shell, pattern: str) -> list[str]:
    """Matches a glob against the virtual filesystem; returns paths in the same form (relative/absolute) as the pattern."""
    absolute = pattern.startswith("/")
    current = [("/" if absolute else shell.cwd, "/" if absolute else "")]
    dotglob, globstar = "dotglob" in shell.shopts, "globstar" in shell.shopts
    nocase = "nocaseglob" in shell.shopts
    segs = [s for s in pattern.split("/") if s]
    for k, seg in enumerate(segs):
        nxt = []
        if seg == "**" and globstar:  # zero or more directories; as the last segment, every file and directory below
            last = not pattern.endswith("/") and k == len(segs) - 1
            stack = list(current)
            while stack:
                abs_, display = stack.pop(0)
                is_base = (abs_, display) in current
                if not last:
                    nxt.append((abs_, display))
                elif display and is_base:
                    nxt.append((abs_, _join(display, "")))  # bash lists the base itself as "dir/"
                if shell.fs.is_dir(abs_) and shell.fs.can_read(abs_):
                    for name in shell.fs.listdir(abs_):
                        child = normalize(abs_, name)
                        if dotglob or not name.startswith("."):
                            if shell.fs.is_dir(child, follow=False):  # like bash, ** does not follow links
                                stack.append((child, _join(display, name)))
                            elif last:
                                nxt.append((child, _join(display, name)))
                    if last and not is_base:
                        nxt.append((abs_, display))
            current = nxt
            continue
        for abs_, display in current:
            if not shell.fs.is_dir(abs_) or not shell.fs.can_read(abs_):
                continue
            if seg in (".", ".."):
                nxt.append((normalize(abs_, seg), _join(display, seg)))
            elif _has_unescaped_wildcard(seg):
                rx = glob_to_regex(seg, nocase)
                for name in shell.fs.listdir(abs_):
                    if rx.match(name) and (not name.startswith(".") or seg.startswith(".") or dotglob):
                        nxt.append((normalize(abs_, name), _join(display, name)))
            else:
                name = re.sub(r"\\(.)", r"\1", seg)
                child = normalize(abs_, name)
                if shell.fs.exists(child, follow=False):
                    nxt.append((child, _join(display, name)))
        current = nxt
    if pattern.endswith("/"):  # `echo */` lists directories only
        return sorted(_join(d, "") if d != "/" else d for a, d in current if d and shell.fs.is_dir(a))
    return sorted(d for _, d in current if d)
