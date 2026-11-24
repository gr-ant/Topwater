"""The simulated bash session."""
from __future__ import annotations

import asyncio
import inspect
import io
import random
import re
from dataclasses import dataclass, replace
from typing import Any, Awaitable, Callable, Union

from . import arith
from .parser import (
    ArithCommand, ArithForCommand, ArithSub, CaseCommand, CommandSub, CondCommand, ForCommand, FunctionDef, GroupCommand,
    IfCommand, Lexer, Literal, LoopCommand, Parser, ParseError, Pipeline, ProcSub, Script, SimpleCommand, Variable, Word,
    is_cond_binary,
)
from .vfs import ShellError, VirtualFileSystem, get_name, get_parent, normalize

_BAD_INT = re.compile(r"invalid literal for int\(\) with base \d+: '(.*)'$", re.S)
EXECUTABLE_STUB = "\x7fELF\x02\x01\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00"


@dataclass(frozen=True)
class ShellResult:
    stdout: str
    stderr: str
    exit_code: int

    def to_tool_output(self, max_chars: int = 30_000) -> str:
        """Formats the result as the text returned to the model from the bash tool."""
        text = self.stdout + self.stderr
        if self.exit_code != 0:
            text += ("" if not text or text.endswith("\n") else "\n") + f"[exit code {self.exit_code}]"
        if not text:
            return "(no output)"
        if len(text) <= max_chars:
            return text
        return text[:max_chars] + f"\n... [output truncated, {len(text) - max_chars} more characters]"


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


@dataclass(frozen=True)
class _Io:
    stdin: StdinBuffer
    out: Any
    err: Any


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


class CommandContext:
    """Everything a command needs: its arguments, stdio, and the shell it runs in."""

    def __init__(self, shell: "Shell", name: str, args: list[str], stdin: StdinBuffer, stdout, stderr):
        self.shell = shell
        self.name = name
        self.args = args
        self._stdin = stdin
        self._stdin_text: str | None = None
        self.stdout = stdout
        self.stderr = stderr

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
    def io(self) -> _Io:
        return _Io(self._stdin, self.stdout, self.stderr)

    @property
    def fs(self) -> VirtualFileSystem:
        return self.shell.fs

    def resolve(self, path: str) -> str:
        return self.shell.resolve(path)

    def print(self, *values: Any, end: str = "\n") -> None:
        self.stdout.write(" ".join(str(v) for v in values) + end)

    def error(self, *values: Any) -> None:
        self.stderr.write(" ".join(str(v) for v in values) + "\n")

    async def invoke(self, argv: list[str], stdin: str = "") -> int:
        """Runs another command with this command's stdout/stderr (used by xargs)."""
        return await self.shell._invoke(argv, _Io(StdinBuffer(stdin), self.stdout, self.stderr))


class Shell:
    """A simulated bash session. Give the model a single "bash" tool and pass its command to run()/run_async().

    Shell state (cwd, environment, aliases, functions, files) persists between calls.
    """

    def __init__(self, user: str = "agent", hostname: str = "topwater", fs: VirtualFileSystem | None = None,
                 max_loop_iterations: int = 100_000):
        self.user = user
        self.hostname = hostname
        self.fs = fs or VirtualFileSystem()
        self.max_loop_iterations = max_loop_iterations
        self.commands: dict[str, RegisteredCommand] = {}
        self.history: list[str] = []
        self.aliases: dict[str, str] = {}
        self.functions: dict[str, Any] = {}
        self.positional: list[str] = []
        self.errexit = False
        self.pipefail = False
        self.last_exit_code = 0
        self._locals: list[dict[str, str | None]] = []
        self._cond_depth = 0
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
        return normalize(self._cwd, path)

    # ---------- registration ----------

    def register_command(self, name: str, handler: Handler, description: str = "", usage: str | None = None) -> "Shell":
        """Registers a handler(ctx) -> exit code (sync or async)."""
        self.commands[name] = RegisteredCommand(name, description, usage, handler)
        if self._builtin_names is not None:
            self._builtin_names.discard(name)  # an app command replacing a built-in owns its own --help/--version
        if "/" not in name and name not in (".", "..") and self.fs.is_dir("/usr/bin"):
            with self.fs.as_root():
                for d in ("/usr/bin/", "/bin/"):
                    if self.fs.is_dir(d):
                        self.fs.write_text(d + name, EXECUTABLE_STUB)
                        self.fs.chmod(d + name, 0o755)
        return self

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

    # ---------- execution ----------

    def run(self, command_line: str) -> ShellResult:
        """Synchronous wrapper for run_async (do not call from inside a running event loop)."""
        return asyncio.run(self.run_async(command_line))

    async def run_async(self, command_line: str) -> ShellResult:
        self.history.append(command_line)
        out, err = io.StringIO(), io.StringIO()
        previous_user, self.fs.run_as = self.fs.run_as, self.user  # the agent's commands run as a regular user
        try:
            code = await self._exec_script(Parser.parse(command_line), _Io(StdinBuffer(), out, err))
        except ParseError as e:
            err.write(f"bash: {e}\n")
            code = 2
        except (ExitSignal, ReturnSignal) as e:
            code = e.code
        except LoopControl:
            code = 0
        except (asyncio.CancelledError, KeyboardInterrupt):
            raise
        except Exception as e:  # an interpreter bug must not take down the host's agent loop
            err.write(f"bash: internal error: {type(e).__name__}: {e}\n")
            code = 1
        finally:
            # shortcut: `set -e` / `set -o pipefail` last for one tool call, so a stray `set -e` can't poison the session.
            self.errexit = self.pipefail = False
            self.fs.run_as = previous_user
            if self._proc_sub_count and self.fs.is_dir("/dev/fd"):
                with self.fs.as_root():
                    for f in self.fs.listdir("/dev/fd"):
                        self.fs.delete("/dev/fd/" + f, recursive=True)
            self._proc_sub_count = 0
        self.last_exit_code = code
        return ShellResult(out.getvalue(), err.getvalue(), code)

    async def _run_script_text(self, text: str, io_: _Io, subshell: bool, positional: list[str] | None = None) -> int:
        try:
            script = Parser.parse(text)
        except ParseError as e:
            io_.err.write(f"bash: {e}\n")
            return 2
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
        if positional is not None:
            self.positional = positional
        try:
            return await self._exec_script(script, io_)
        except ExitSignal as e:
            return e.code
        finally:
            self._restore_state(saved)

    def _save_state(self):
        return (self._cwd, dict(self.env), dict(self.aliases), dict(self.functions), self.positional, self.errexit, self.pipefail)

    def _restore_state(self, saved) -> None:
        cwd, self.env, self.aliases, self.functions, self.positional, self.errexit, self.pipefail = saved
        self._cwd = cwd if self.fs.is_dir(cwd) else "/"

    async def _exec_script(self, script: Script, io_: _Io) -> int:
        code = 0
        for item in script.items:
            code = await self._exec_pipeline(item.first, io_)
            last_run, negated = 0, item.first.negate
            for k, (op, nxt) in enumerate(item.rest):
                if (op == "&&") != (code == 0):
                    continue
                code = await self._exec_pipeline(nxt, io_)
                last_run, negated = k + 1, nxt.negate
            # set -e: only the final command of an && / || list counts, and never inside if/while conditions
            if self.errexit and code != 0 and self._cond_depth == 0 and last_run == len(item.rest) and not negated:
                raise ExitSignal(code)
        return code

    async def _exec_pipeline(self, p: Pipeline, io_: _Io) -> int:
        data = io_.stdin
        code = failed = 0
        for k, cmd in enumerate(p.commands):
            last = k == len(p.commands) - 1
            # shortcut: pipeline stages run one after another with buffered text; stream if huge outputs matter.
            out = io_.out if last else io.StringIO()
            code = await self._exec_command(cmd, replace(io_, stdin=data, out=out))
            if code != 0:
                failed = code
            if not last:
                data = StdinBuffer(out.getvalue())
        if self.pipefail:
            code = failed
        if p.negate:
            code = 1 if code == 0 else 0
        self.last_exit_code = code
        return code

    async def _exec_command(self, cmd, io_: _Io) -> int:
        sinks: list[tuple[str, io.StringIO]] = []
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
                code = await self._exec_for(cmd, rio)
            elif isinstance(cmd, ArithForCommand):
                code = await self._exec_arith_for(cmd, rio)
            elif isinstance(cmd, CaseCommand):
                code = await self._exec_case(cmd, rio)
            elif isinstance(cmd, FunctionDef):
                self.functions[cmd.name] = cmd.body
                code = 0
            elif isinstance(cmd, ArithCommand):
                code = 0 if await self._arith(cmd.expr, rio) != 0 else 1
            else:
                code = 0 if await _CondEvaluator(self, cmd.items, rio).evaluate() else 1
        except ShellError as e:
            rio.err.write(f"bash: {e}\n")
            code = 2 if isinstance(cmd, CondCommand) else e.exit_code
        finally:
            for path, sink in sinks:
                try:
                    self.fs.append_text(path, sink.getvalue())
                except ShellError as e:
                    io_.err.write(f"bash: {e}\n")
        return code

    async def _exec_subshell(self, body: Script, io_: _Io) -> int:
        saved = self._save_state()
        try:
            return await self._exec_script(body, io_)
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
        try:
            return await self._exec_script(body, io_), False
        except LoopControl as lc:
            if lc.levels > 1:
                raise LoopControl(lc.is_break, lc.levels - 1) from None
            return 0, lc.is_break

    def _check_iterations(self, n: int) -> int:
        if n + 1 > self.max_loop_iterations:
            raise ShellError(f"loop aborted after {self.max_loop_iterations} iterations")
        return n + 1

    async def _exec_loop(self, c: LoopCommand, io_: _Io) -> int:
        code = n = 0
        while True:
            n = self._check_iterations(n)
            if (await self._condition(c.cond, io_) == 0) == c.until:
                break
            code, brk = await self._loop_body(c.body, io_)
            if brk:
                break
        return code

    async def _exec_for(self, c: ForCommand, io_: _Io) -> int:
        items = list(self.positional) if c.items is None else await self._expand_words(c.items, io_)
        code = n = 0
        for item in items:
            n = self._check_iterations(n)
            self.env[c.var] = item
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
        for patterns, body in c.clauses:
            for pattern in patterns:
                if glob_to_regex(await self._pattern(pattern, io_), slashes=True).match(subject):
                    return await self._exec_script(body, io_)
        return 0

    async def _apply_redirects(self, redirects, io_: _Io, sinks) -> _Io | None:
        for r in redirects:
            try:
                if r.op == "<<":
                    io_ = replace(io_, stdin=StdinBuffer(await self._expand_single(r.here.body, io_)))
                elif r.op == "<<<":
                    io_ = replace(io_, stdin=StdinBuffer(await self._expand_single(r.target, io_) + "\n"))
                elif r.op == "<":
                    src = await self._expand_single(r.target, io_)
                    io_ = replace(io_, stdin=StdinBuffer(self.fs.read_text(self.resolve(src))))
                elif r.op == ">&":
                    fd = await self._expand_single(r.target, io_)
                    if r.fd == 2 and fd == "1":
                        io_ = replace(io_, err=io_.out)
                    elif r.fd == 1 and fd == "2":
                        io_ = replace(io_, out=io_.err)
                else:  # > >> &> &>>
                    target = await self._expand_single(r.target, io_)
                    path = self.resolve(target)
                    if path == "/dev/null":
                        w = _Null()
                    elif path in ("/dev/stdout", "/dev/stderr"):
                        w = io_.err if path == "/dev/stderr" else io_.out
                    else:
                        if self.fs.is_dir(path):
                            raise ShellError(f"{target}: Is a directory")
                        if not r.op.endswith(">>") or not self.fs.exists(path):
                            self.fs.write_text(path, "")  # truncate on open, like bash
                        w = io.StringIO()
                        sinks.append((path, w))
                    if r.op.startswith("&"):
                        io_ = replace(io_, out=w, err=w)
                    elif r.fd == 2:
                        io_ = replace(io_, err=w)
                    else:
                        io_ = replace(io_, out=w)
            except ShellError as e:
                io_.err.write(f"bash: {e}\n")
                return None
        return io_

    async def _exec_simple(self, c: SimpleCommand, io_: _Io) -> int:
        argv = await self._expand_words(c.words, io_)
        assigned = [(name, await self._expand_single(value, io_)) for name, value in c.assignments]
        if not argv:
            self.env.update(assigned)
            has_sub = any(isinstance(p, CommandSub) for _, w in c.assignments for p in w.parts)
            return self.last_exit_code if c.words or has_sub else 0
        if argv[0] in self.aliases:
            argv = await self._expand_words(Parser.parse_words(self.aliases[argv[0]]), io_) + argv[1:]
        if not assigned:
            return await self._invoke(argv, io_)
        # VAR=x cmd: the variable only exists for that command
        saved = {k: self.env.get(k) for k, _ in assigned}
        self.env.update(assigned)
        try:
            return await self._invoke(argv, io_)
        finally:
            for k, v in saved.items():
                if v is None:
                    self.env.pop(k, None)
                else:
                    self.env[k] = v

    async def _invoke(self, argv: list[str], io_: _Io) -> int:
        name = argv[0]
        if name in self.functions:
            return await self._call_function(self.functions[name], argv, io_)
        if "/" in name:
            path = self.resolve(name)
            if get_parent(path) in ("/usr/bin", "/bin", "/usr/local/bin") and get_name(path) in self.commands:
                name = get_name(path)
            else:
                return await self._run_script_file(name, path, argv, io_)
        cmd = self.commands.get(name)
        if cmd is None:
            io_.err.write(f"{argv[0]}: command not found (run 'help' to list available commands)\n")
            return 127
        ctx = CommandContext(self, name, list(argv[1:]), io_.stdin, io_.out, io_.err)
        if len(argv) == 2 and self._builtin_names and name in self._builtin_names:
            from .commands import standard_option

            if standard_option(ctx, cmd, argv[1]):
                return 0
        try:
            result = cmd.handler(ctx)
            if inspect.isawaitable(result):
                result = await result
            return int(result or 0)
        except (asyncio.CancelledError, KeyboardInterrupt, _ControlFlow):
            raise
        except ShellError as e:
            io_.err.write(f"{name}: {e}\n")
            return e.exit_code
        except Exception as e:  # a tool crashing must not crash the agent loop
            m = _BAD_INT.match(str(e)) if isinstance(e, ValueError) else None
            io_.err.write(f"{name}: invalid number: '{m.group(1)}'\n" if m else f"{name}: {e}\n")
            return 1

    async def _run_script_file(self, name: str, path: str, argv: list[str], io_: _Io) -> int:
        if self.fs.is_dir(path):
            io_.err.write(f"bash: {name}: Is a directory\n")
            return 126
        if not self.fs.is_file(path):
            io_.err.write(f"bash: {name}: No such file or directory\n")
            return 127
        if not (self.fs.can_execute(path) and self.fs.can_read(path)):
            io_.err.write(f"bash: {name}: Permission denied\n")
            return 126
        text = self.fs.read_text(path)
        if text.startswith(EXECUTABLE_STUB[:4]):
            io_.err.write(f"bash: {name}: cannot execute binary file\n")
            return 126
        if text.startswith("#!"):
            interp = text[2:].split("\n", 1)[0].strip()
            if not re.search(r"(^|/|\s)(ba)?sh(\s|$)", interp):
                io_.err.write(f"bash: {name}: {interp.split(' ')[0]}: bad interpreter: No such file or directory\n")
                return 126
        return await self._run_script_text(text, io_, subshell=True, positional=list(argv[1:]))

    async def _call_function(self, body, argv: list[str], io_: _Io) -> int:
        if len(self._locals) >= 200:
            raise ShellError(f"{argv[0]}: maximum function nesting level exceeded (200)")
        saved_pos = self.positional
        self.positional = list(argv[1:])
        self._locals.append({})
        try:
            return await self._exec_command(body, io_)
        except ReturnSignal as r:
            return r.code
        finally:
            for k, v in self._locals.pop().items():
                if v is None:
                    self.env.pop(k, None)
                else:
                    self.env[k] = v
            self.positional = saved_pos

    @property
    def in_function(self) -> bool:
        return bool(self._locals)

    def declare_local(self, name: str, value: str | None) -> None:
        """`local NAME[=VALUE]`: remembers the outer value so it is restored when the function returns."""
        if not self._locals:
            raise ShellError("can only be used in a function")
        self._locals[-1].setdefault(name, self.env.get(name))
        self.env[name] = value if value is not None else self.env.get(name, "")

    # ---------- expansion ----------

    def get_variable(self, name: str) -> str:
        if name.isdigit():
            n = int(name)
            return "bash" if n == 0 else self.positional[n - 1] if n <= len(self.positional) else ""
        special = {
            "?": lambda: str(self.last_exit_code), "$": lambda: "4242", "!": lambda: "", "#": lambda: str(len(self.positional)),
            "@": lambda: " ".join(self.positional), "*": lambda: " ".join(self.positional), "PWD": lambda: self._cwd,
            "RANDOM": lambda: str(random.randrange(32768)),
        }
        return special[name]() if name in special else self.env.get(name, "")

    def _is_set(self, name: str) -> bool:
        if name in ("?", "$", "#", "@", "*", "PWD", "RANDOM", "0"):
            return True
        if name.isdigit():
            return int(name) <= len(self.positional)
        return name in self.env

    async def _expand_part(self, part, io_: _Io) -> str:
        if isinstance(part, Literal):
            return part.text
        if isinstance(part, Variable):
            return await self._expand_parameter(part.name, io_)
        if isinstance(part, ArithSub):
            return str(await self._arith(part.expr, io_))
        if isinstance(part, ProcSub):
            # <(cmd): write cmd's output to /dev/fd/N (removed after the tool call) and substitute that path
            out = io.StringIO()
            await self._run_script_text(part.source, replace(io_, stdin=StdinBuffer(), out=out), subshell=True)
            path = f"/dev/fd/{63 + self._proc_sub_count}"
            self._proc_sub_count += 1
            with self.fs.as_root():
                self.fs.write_text(path, out.getvalue())
            return path
        out = io.StringIO()
        self.last_exit_code = await self._run_script_text(part.source, replace(io_, stdin=StdinBuffer(), out=out), subshell=True)
        return out.getvalue().rstrip("\n")

    async def _arith(self, expr: str, io_: _Io) -> int:
        """Expands $(cmd), `cmd` and ${...} inside an arithmetic expression, then evaluates it."""
        if "$" in expr or "`" in expr:
            expr = await self._expand_single(Word(Lexer(expr).lex_quoted(None)), io_)
        return arith.evaluate(self, expr)

    _PARAM = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!])(.*)$", re.S)

    async def _expand_parameter(self, expr: str, io_: _Io) -> str:
        """${name}, ${#name}, ${name:-x}, ${name%pat}, ${name/a/b}, ${name:1:2}, ${name^^} ..."""
        if len(expr) > 1 and expr[0] == "#":
            return str(len(self.positional)) if expr[1:] in ("@", "*") else str(len(self.get_variable(expr[1:])))
        m = self._PARAM.match(expr)
        if not m:
            raise ShellError(f"${{{expr}}}: bad substitution")
        name, rest = m.group(1), m.group(2)
        value = self.get_variable(name)
        if not rest:
            return value

        async def arg(text: str) -> str:
            if len(text) >= 2 and text[0] in "\"'" and text[-1] == text[0]:
                text = text[1:-1]
            return await self._expand_single(Word(Lexer(text).lex_quoted(None)), io_)

        async def pattern(text: str) -> str:
            # pattern text is unquoted, so its wildcards stay active
            parts = [Literal(p.text, False) if isinstance(p, Literal) else p for p in Lexer(text).lex_quoted(None)]
            return await self._pattern(Word(parts), io_)

        for op in (":-", ":=", ":+", ":?", "-", "=", "+", "?"):
            if not rest.startswith(op):
                continue
            missing = not value if op[0] == ":" else not self._is_set(name)
            a = rest[len(op):]
            kind = op.lstrip(":")
            if kind == "-":
                return await arg(a) if missing else value
            if kind == "=":
                if not missing:
                    return value
                v = await arg(a)
                self.env[name] = v
                return v
            if kind == "+":
                return "" if missing else await arg(a)
            if missing:
                raise ShellError(f"{name}: {await arg(a) if a else 'parameter null or not set'}")
            return value
        if rest[0] in "#%":
            longest = len(rest) > 1 and rest[1] == rest[0]
            rx = glob_to_regex(await pattern(rest[2 if longest else 1:]), slashes=True)
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
            slash, i = -1, 0
            while i < len(body):
                if body[i] == "\\":
                    i += 2
                    continue
                if body[i] == "/":
                    slash = i
                    break
                i += 1
            pat = glob_to_regex_body(await pattern(body if slash < 0 else body[:slash]), slashes=True)
            rep = "" if slash < 0 else await arg(body[slash + 1:])
            if not pat:
                return value
            rx = re.compile(("^" + pat) if mode == "#" else (pat + "$") if mode == "%" else pat, re.S)
            return rx.sub(lambda _m: rep, value, count=0 if mode == "/" else 1)
        if rest == "^^":
            return value.upper()
        if rest == ",,":
            return value.lower()
        if rest == "^":
            return value[:1].upper() + value[1:]
        if rest == ",":
            return value[:1].lower() + value[1:]
        if rest[0] == ":":
            spec = rest[1:].split(":", 1)
            offset = arith.evaluate(self, spec[0])
            if offset < 0:
                offset = max(0, len(value) + offset)
            if offset > len(value):
                return ""
            tail = value[offset:]
            if len(spec) == 1:
                return tail
            length = arith.evaluate(self, spec[1])
            if length < 0:
                length = max(0, len(tail) + length)
            return tail[:length]
        raise ShellError(f"${{{expr}}}: bad substitution")

    def _expand_tilde(self, text: str) -> str:
        if text == "~" or text.startswith("~/"):
            return self.env.get("HOME", "/") + text[1:]
        return text

    async def _expand_single(self, word: Word, io_: _Io) -> str:
        """Expands a word without field splitting or globbing (redirect targets, assignments, heredocs)."""
        out = []
        for i, part in enumerate(word.parts):
            text = await self._expand_part(part, io_)
            if i == 0 and isinstance(part, Literal) and not part.quoted:
                text = self._expand_tilde(text)
            out.append(text)
        return "".join(out)

    async def _pattern(self, word: Word, io_: _Io) -> str:
        """Expands a word into a glob pattern: quoted text is escaped, unquoted text keeps its wildcards."""
        out = []
        for part in word.parts:
            text = await self._expand_part(part, io_)
            out.append(glob_escape(text) if part.quoted else text)
        return "".join(out)

    async def _expand_words(self, words, io_: _Io) -> list[str]:
        result: list[str] = []
        for word in [w for word in words for w in brace_expand(word)]:
            fields: list[_Field] = []
            cur = _Field()

            def end():
                nonlocal cur
                if cur.has_content:
                    fields.append(cur)
                cur = _Field()

            def add(s: str, quoted: bool):
                cur.text.append(s)
                cur.pattern.append(glob_escape(s) if quoted else s)
                if not quoted and has_wildcard(s):
                    cur.has_glob = True
                cur.has_content = True

            for i, part in enumerate(word.parts):
                if isinstance(part, Variable) and part.name == "@" and part.quoted:
                    for k, p in enumerate(self.positional):  # "$@": one field per positional parameter
                        if k:
                            end()
                        add(p, True)
                    continue
                text = await self._expand_part(part, io_)
                if isinstance(part, Literal):
                    if i == 0 and not part.quoted and (text == "~" or text.startswith("~/")):
                        add(self.env.get("HOME", "/"), True)  # home path is never globbed
                        text = text[1:]
                        if not text:
                            continue
                    add(text, part.quoted)
                    continue
                if part.quoted:
                    add(text, True)
                    continue
                # unquoted $VAR / $(cmd) / $((...)): split into fields on $IFS
                ifs = self.env.get("IFS", " \t\n")
                pieces = split_fields(text, ifs)
                if not pieces:
                    if text and ifs:
                        end()
                    continue
                if text[0] in ifs:
                    end()
                for k, piece in enumerate(pieces):
                    if k:
                        end()
                    add(piece, False)
                if text[-1] in ifs:
                    end()
            end()

            for f in fields:
                matches = glob_expand(self, "".join(f.pattern)) if f.has_glob else []
                result.extend(matches or ["".join(f.text)])
        return result


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
    fields = re.split(sep, text, maxsplit=max(0, max_fields - 1))
    if max_fields <= 0 and other and fields and fields[-1] == "":
        fields.pop()  # "a,b," is two fields, not three
    return fields


class _Field:
    def __init__(self):
        self.text: list[str] = []
        self.pattern: list[str] = []
        self.has_glob = False
        self.has_content = False


class _CondEvaluator:
    """Evaluates [[ ... ]]."""

    UNARY = {"-z", "-n", "-e", "-f", "-d", "-s", "-r", "-w", "-x", "-L", "-h", "-a", "-v"}

    def __init__(self, shell: Shell, items: list[Word], io_: _Io):
        self.sh, self.items, self.io, self.p = shell, items, io_, 0

    def is_op(self, s: str) -> bool:
        return self.p < len(self.items) and self.items[self.p].is_(s)

    async def evaluate(self) -> bool:
        if not self.items:
            raise ShellError("syntax error in conditional expression", 2)
        v = await self.or_()
        if self.p < len(self.items):
            raise ShellError(f"syntax error in conditional expression near `{self.items[self.p]}'", 2)
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
        items = self.items
        if self.is_op("("):
            self.p += 1
            v = await self.or_()
            if not self.is_op(")"):
                raise ShellError("syntax error in conditional expression: expected `)'", 2)
            self.p += 1
            return v
        if self.p >= len(items):
            raise ShellError("syntax error in conditional expression: unexpected end", 2)
        u = items[self.p].unquoted_literal
        if u in self.UNARY and self.p + 1 < len(items) and not (self.p + 2 < len(items) and is_cond_binary(items[self.p + 1])):
            arg = await self.sh._expand_single(items[self.p + 1], self.io)
            self.p += 2
            if u == "-v":
                return arg in self.sh.env
            from .commands import test_unary

            return test_unary(self.sh, {"-a": "-e", "-h": "-L"}.get(u, u), arg)
        left = items[self.p]
        self.p += 1
        if self.p < len(items) and is_cond_binary(items[self.p]):
            op = items[self.p].unquoted_literal
            self.p += 1
            if self.p >= len(items):
                raise ShellError(f"unexpected argument to conditional binary operator `{op}'", 2)
            right = items[self.p]
            self.p += 1
            lv = await self.sh._expand_single(left, self.io)
            if op in ("==", "=", "!="):
                match = bool(glob_to_regex(await self.sh._pattern(right, self.io), slashes=True).match(lv))
                return not match if op == "!=" else match
            if op == "=~":
                parts = [await self.sh._expand_part(p, self.io) for p in right.parts]
                rv = "".join(re.escape(t) if p.quoted else t for p, t in zip(right.parts, parts))
                try:
                    m = re.search(rv, lv)
                except re.error:
                    raise ShellError(f"invalid regular expression `{rv}'", 2) from None
                if m:
                    self.sh.env["BASH_REMATCH"] = m.group(0)
                return m is not None
            rv = await self.sh._expand_single(right, self.io)
            if op == "<":
                return lv < rv
            if op == ">":
                return lv > rv
            a, b = arith.evaluate(self.sh, lv), arith.evaluate(self.sh, rv)
            return {"-eq": a == b, "-ne": a != b, "-lt": a < b, "-le": a <= b, "-gt": a > b, "-ge": a >= b}.get(op, False)
        return bool(await self.sh._expand_single(left, self.io))


# ---------- brace expansion ----------

_MAX_BRACE = 10_000


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
        pad = any(len(g.lstrip("-")) > 1 and g.lstrip("-").startswith("0") for g in (m.group(1), m.group(2)))
        width = max(len(m.group(1)), len(m.group(2)))
        if abs(b - a) // step > _MAX_BRACE:
            raise ShellError("brace expansion: too many items")
        rng = range(a, b + 1, step) if a <= b else range(a, b - 1, -step)
        return [str(v).zfill(width) if pad else str(v) for v in rng]
    m = re.fullmatch(r"([a-zA-Z])\.\.([a-zA-Z])", inner)
    if m:
        a, b = ord(m.group(1)), ord(m.group(2))
        return [chr(c) for c in (range(a, b + 1) if a <= b else range(a, b - 1, -1))]
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


def brace_expand(w: Word) -> list[Word]:
    """Brace expansion: a{b,c}d, {1..10}, {01..05}, {a..e}, {0..10..2}."""
    for pi, part in enumerate(w.parts):
        if not isinstance(part, Literal) or part.quoted:
            continue
        text = part.text
        open_ = text.find("{")
        while open_ >= 0:
            close = _brace_close(text, open_)
            alts = _brace_alternatives(text[open_ + 1:close]) if close >= 0 else None
            if alts is not None:
                results = []
                for alt in alts:
                    parts = list(w.parts)
                    parts[pi] = Literal(text[:open_] + alt + text[close + 1:], False)
                    results.extend(brace_expand(Word(parts)))
                    if len(results) > _MAX_BRACE:
                        raise ShellError("brace expansion: too many items")
                return results
            open_ = text.find("{", open_ + 1)
    return [w]


# ---------- globbing ----------

def has_wildcard(s: str) -> bool:
    return any(c in s for c in "*?[")


def glob_escape(s: str) -> str:
    return "".join("\\" + c if c in "*?[]\\" else c for c in s)


def _has_unescaped_wildcard(seg: str) -> bool:
    i = 0
    while i < len(seg):
        if seg[i] == "\\":
            i += 2
            continue
        if seg[i] in "*?[":
            return True
        i += 1
    return False


def glob_to_regex_body(pattern: str, slashes: bool = False) -> str:
    out = []
    i = 0
    while i < len(pattern):
        c = pattern[i]
        if c == "\\" and i + 1 < len(pattern):
            i += 1
            out.append(re.escape(pattern[i]))
        elif c == "*":
            out.append(".*" if slashes else "[^/]*")
        elif c == "?":
            out.append("." if slashes else "[^/]")
        elif c == "[":
            close = pattern.find("]", i + 2) if i + 2 <= len(pattern) else -1
            if close < 0:
                out.append(r"\[")
            else:
                chars = pattern[i + 1:close]
                if chars.startswith("!"):
                    chars = "^" + chars[1:]
                out.append("[" + chars.replace("\\", "\\\\") + "]")
                i = close
        else:
            out.append(re.escape(c))
        i += 1
    return "".join(out)


def glob_to_regex(pattern: str, ignore_case: bool = False, slashes: bool = False) -> re.Pattern:
    """slashes=True for case/[[ ]]/${x%pat} matching, where * also matches '/'."""
    return re.compile("^" + glob_to_regex_body(pattern, slashes) + r"\Z", (re.IGNORECASE if ignore_case else 0) | re.S)


def _join(display: str, name: str) -> str:
    return name if display == "" else "/" + name if display == "/" else display + "/" + name


def glob_expand(shell: Shell, pattern: str) -> list[str]:
    """Matches a glob against the virtual filesystem; returns paths in the same form (relative/absolute) as the pattern."""
    absolute = pattern.startswith("/")
    current = [("/" if absolute else shell.cwd, "/" if absolute else "")]
    for seg in [s for s in pattern.split("/") if s]:
        nxt = []
        for abs_, display in current:
            if not shell.fs.is_dir(abs_) or not shell.fs.can_read(abs_):
                continue
            if seg in (".", ".."):
                nxt.append((normalize(abs_, seg), _join(display, seg)))
            elif _has_unescaped_wildcard(seg):
                rx = glob_to_regex(seg)
                for name in shell.fs.listdir(abs_):
                    if rx.match(name) and (not name.startswith(".") or seg.startswith(".")):
                        nxt.append((normalize(abs_, name), _join(display, name)))
            else:
                name = re.sub(r"\\(.)", r"\1", seg)
                child = normalize(abs_, name)
                if shell.fs.exists(child):
                    nxt.append((child, _join(display, name)))
        current = nxt
    if pattern.endswith("/"):  # `echo */` lists directories only
        return sorted(_join(d, "") if d != "/" else d for a, d in current if d and shell.fs.is_dir(a))
    return sorted(d for _, d in current if d)
