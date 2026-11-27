"""Shell: simple commands, invoking a command, PATH and hash lookup, script files and functions. See docs/architecture.md."""
from __future__ import annotations

import asyncio
import inspect
import re
import time

from .._spec import COMMANDS, ERRNO, diagnostic
from ..parser import CommandSub, Parser, SimpleCommand, try_assignment
from ..vfs import ShellError, UsageException, get_name, get_parent
from .types import CommandContext, EXECUTABLE_STUB, ExitSignal, LimitExceeded, ReturnSignal, _ControlFlow, _Io, _NewProcess


_BAD_INT = re.compile(r"invalid literal for int\(\) with base \d+: '(.*)'$", re.S)


class InvocationPart:
    # ---------- simple commands ----------

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
                from ..commands import shell_quote
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

    def _report_not_found(self, io_: _Io, name: str) -> None:
        """bash's own "command not found" line, and nothing else (the tool description already points at `help`)."""
        io_.err.write(self.err_prefix + diagnostic("commandNotFound", command=name) + "\n")

    # ---------- invoke ----------

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
            from ..commands.scripting import is_shell_builtin

            if not is_shell_builtin(self, name) and not self.is_unfiled(name):
                found = self.hash_lookup(name)
                if found is None:
                    found = self.search_path(name)
                    if found is None:
                        if not self.env.get("PATH"):
                            io_.err.write(f"{self.err_prefix}{name}: No such file or directory\n")
                        else:
                            self._report_not_found(io_, name)
                        return 127
                    if "/" in found:  # bash does not remember what an empty PATH found
                        self.hashed[name] = [found, 1]
                registered = self.stub_command(found)
                if registered is None:
                    return await self._run_script_file(found, self.resolve(found), [found, *argv[1:]], io_)
                name = registered
        cmd = self.commands.get(name)
        if cmd is None:
            self._report_not_found(io_, argv[0])
            return 127
        self.check_deadline()
        ctx = CommandContext(self, name, list(argv[1:]), io_.stdin, io_.out, io_.err, io_.fds)
        if len(argv) == 2 and self._builtin_names and name in self._builtin_names:
            from ..commands import standard_option

            if standard_option(ctx, cmd, argv[1]):
                return 0
        mounts = self._mounts if self._mounts.items else None
        if mounts is not None:
            await mounts.before(name, argv, io_.err)
        try:
            return await self._run_handler(cmd, ctx, name, io_)
        finally:
            if mounts is not None and time.monotonic() < self._deadline:
                await mounts.after()

    async def _run_handler(self, cmd, ctx, name: str, io_: _Io) -> int:
        from ..commands import usage_error
        from ..commands.scripting import is_shell_builtin

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

    # ---------- script files ----------

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

    def _interpreter_chain_too_long(self, path: str) -> bool:
        """Whether the #! lines from the script at path lead through more scripts than Linux follows (four)."""
        for _ in range(4):
            try:
                first = self.fs.read_text(path).split("\n", 1)[0]
            except ShellError:
                return False
            if not first.startswith("#!") or not first[2:].split():
                return False
            program = first[2:].split()[0]
            path = self.resolve(program)
            if self.stub_command(program) is not None or not (self.fs.is_file(path) and self.fs.can_execute(path)):
                return False
        return True

    async def _run_interpreter(self, name: str, line: str, argv: list[str], io_: _Io) -> int:
        """A #! line naming another program: like the kernel, run it with its one optional argument, the script and the
        script's arguments, when it is one of the shell's commands."""
        parts = re.split(r"[ \t]", line, maxsplit=1)
        program = self.stub_command(parts[0])
        if program is None:
            interpreter = self.resolve(parts[0])
            if self.fs.is_file(interpreter) and self.fs.can_execute(interpreter):
                # another script as the interpreter, as Linux allows for a few levels; a longer chain or a loop fails
                if self._interpreter_chain_too_long(interpreter):
                    io_.err.write("bash: " + diagnostic("badInterpreter", name=name, interpreter=parts[0], reason=ERRNO["ELOOP"]) + "\n")
                    return 126
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

    # ---------- lookup ----------

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
        from ..commands.scripting import is_shell_builtin
        spec = COMMANDS.get(name)
        shell_only = spec is not None and not spec.program_file and self._builtin_names is not None and name in self._builtin_names
        return self.is_unfiled(name) and not is_shell_builtin(self, name) and not shell_only

    # ---------- functions ----------

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

    def is_builtin(self, name: str) -> bool:
        return bool(self._builtin_names) and name in self._builtin_names


# ---------- helpers ----------

def trace_quote(s: str) -> str:
    """set -x: an argument as bash traces it, quoted only when it needs to be."""
    return s if s and all((c.isascii() and c.isalnum()) or c in "_./=:,+@%^-" for c in s) else quote_value(s)


def quote_value(s: str) -> str:
    """${x@Q}: single quotes, or $'...' when the text holds control characters."""
    if any(ord(c) < 32 or ord(c) == 127 for c in s):
        from ..commands import shell_quote
        return shell_quote(s)
    return "'" + s.replace("'", "'\\''") + "'"
