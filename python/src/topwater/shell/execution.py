"""Shell: scripts, lists, jobs, pipelines, compound commands and [[ ]]. See docs/architecture.md."""
from __future__ import annotations

import re
import time
from dataclasses import replace
from typing import TYPE_CHECKING

from .. import arith
from .._spec import DIAGNOSTICS, diagnostic
from ..glob import TimedPattern, _cached, glob_to_regex
from ..parser import (
    ArithCommand, ArithForCommand, CaseCommand, CondCommand, ForCommand, FunctionDef, GroupCommand, IfCommand, LoopCommand,
    ParseError, Parser, Pipeline, Script, SimpleCommand, Word, is_cond_binary,
)
from ..vfs import ShellError
from .redirection import CappedWriter, FileSink, SwitchWriter
from .types import ExitSignal, LimitExceeded, LoopControl, ReturnSignal, ShellArray, StdinBuffer, _Io, _LineAbort, _NewProcess

if TYPE_CHECKING:
    from . import Shell


class ExecutionPart:
    # ---------- scripts ----------

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
                if process is not None and process.login:
                    await self._source_login_files(io_)
                try:
                    return await self._exec_script(script, io_, top=process is not None)
                finally:
                    await self._run_exit_trap(io_)
            except ExitSignal as e:
                return e.code
            except _LineAbort:
                return 1  # $(...): the subshell ends
            finally:
                self._restore_state(saved)
        finally:
            self._script_depth -= 1
            self._arg0 = saved_arg0
            del self._sources[sources:]
            del self._err_where[where:]
            self._lineno, self._line_base = line, base

    async def _source_login_files(self, io_: _Io) -> None:
        home = self.env.get("HOME", "")
        readable = lambda f: self.fs.is_file(f) and self.fs.can_read(f)
        personal = next((home + f for f in ("/.bash_profile", "/.bash_login", "/.profile") if readable(home + f)), None)
        for file in ("/etc/profile", personal):
            if file is None or not readable(file):
                continue
            self.source_depth += 1
            try:
                await self._run_script_text(self.fs.read_text(file), io_, subshell=False, sourced=file)
            except ReturnSignal:
                pass
            finally:
                self.source_depth -= 1

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
        if process.login:
            self.shopts.add("login_shell")
        else:
            self.shopts.discard("login_shell")
        if process.setup:
            process.setup()

    async def _run_exit_trap(self, io_: _Io) -> None:
        action = self.traps.pop("EXIT", None)
        if action:
            saved_code = self.last_exit_code
            try:
                await self._run_script_text(action, io_, subshell=False)
            except (LoopControl, ReturnSignal, ExitSignal, _LineAbort):
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

    # ---------- lists ----------

    async def _exec_script(self, script: Script, io_: _Io, top: bool = False) -> int:
        """top: the script a bash process reads line by line, where a _LineAbort drops the rest of its line only."""
        code = 0
        skip_to = 0
        for item in script.items:
            if item.end_line and item.end_line <= skip_to:
                continue
            if top:
                try:
                    code = await self._exec_script(Script([item]), io_)
                except _LineAbort:
                    code = self.last_exit_code = 1
                    skip_to = item.end_line
                continue
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

    # ---------- jobs ----------

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

    # ---------- pipelines ----------

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

    # ---------- compound commands ----------

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
            if self._mounts.items and time.monotonic() < self._deadline:
                await self._mounts.settle(io_.err)  # live mode: what the command and its redirections wrote
        return code

    async def _exec_subshell(self, body: Script, io_: _Io) -> int:
        saved = self._save_state()
        try:
            return await self._exec_script(body, replace(io_, out=SwitchWriter(io_.out), err=SwitchWriter(io_.err)))
        except ExitSignal as e:
            return e.code
        except _LineAbort:
            return 1
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


# ---------- conditional expressions ----------

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
            from ..commands.scripting import test_unary

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
                from ..commands.text import posix_to_python
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
                from ..commands.scripting import newer
                return newer(sh, lv, rv) if op == "-nt" else newer(sh, rv, lv)
            if op == "-ef":
                return sh.fs.same_file(sh.resolve(lv), sh.resolve(rv))
            a, b = arith.evaluate(sh, lv), arith.evaluate(sh, rv)
            return {"-eq": a == b, "-ne": a != b, "-lt": a < b, "-le": a <= b, "-gt": a > b, "-ge": a >= b}.get(op, False)
        return bool(await sh._expand_single(left, self.io))
