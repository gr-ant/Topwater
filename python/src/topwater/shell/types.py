"""The types the shell and its commands share. See docs/architecture.md."""
from __future__ import annotations

import io
from dataclasses import dataclass
from typing import TYPE_CHECKING, Any, Awaitable, Callable, Union

from .._spec import diagnostic
from ..vfs import ShellError, VirtualFileSystem

if TYPE_CHECKING:
    from . import Shell


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
    """${...} bash cannot read; the word expanding it reports it. fatal: a bad @ operator on a set variable, which ends
    the script with 127; anything else drops the rest of the line (_LineAbort)."""

    def __init__(self, expr: str, fatal: bool = False):
        super().__init__(diagnostic("badSubstitution", text="${" + expr + "}"))
        self.fatal = fatal


class _ControlFlow(Exception):
    pass


class _LineAbort(_ControlFlow):
    """An error after which bash drops the rest of the line it is running (status 1) and reads on; in a subshell it ends
    the subshell."""


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
        self.shell.check_deadline()  # a command given many paths (touch $(seq 600000)) stops at the call's time limit
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


class _NewProcess:
    """A new bash process (bash -c, bash file, ./script.sh): $0, the file it runs (if any), and its own options (bash -e)."""

    def __init__(self, arg0: str, file: str | None = None, setup: Callable[[], None] | None = None, login: bool = False):
        # login: bash -l reads /etc/profile and the first of ~/.bash_profile, ~/.bash_login and ~/.profile first
        self.arg0, self.file, self.setup, self.login = arg0, file, setup, login


def default_op(rest: str):
    """${p-w} ${p:-w} ${p=w} ${p:=w} ${p+w} ${p:+w} ${p?w} ${p:?w}: (operator, word); None for any other operator."""
    n = 2 if rest[:1] == ":" else 1
    return (rest[:n], rest[n:]) if len(rest) >= n and rest[n - 1] in "-=+?" else None
