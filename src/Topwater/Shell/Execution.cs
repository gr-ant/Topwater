// Shell: scripts, lists, jobs, pipelines, compound commands and [[ ]]. See docs/architecture.md.
using System.Text;
using System.Text.RegularExpressions;
using Topwater.Parsing;

namespace Topwater;

public sealed partial class Shell
{
    // ---------- scripts ----------

    /// <param name="process">Set for a new bash process: it sees only exported variables and functions.</param>
    /// <param name="sourced">The file `source` is running, for BASH_SOURCE.</param>
    internal async Task<int> RunScriptTextAsync(string text, Io io, bool subshell, CancellationToken ct, List<string>? positional = null,
        NewProcess? process = null, string? sourced = null, bool eval = false, bool substitution = false)
    {
        Script script;
        try { script = Parser.Parse(text, eval || substitution ? Math.Max(_lineno, 1) : 1); }
        catch (ParseException e) when (eval)
        {
            var at = $"{(_errWhere.Count > 0 ? _errWhere[^1].Name : "bash")}: eval: line {e.Line}: ";
            io.Err.WriteLine(at + (e.AtEof ? CommandSpec.Diagnostics.UnexpectedEof : e.Message));
            if (e.SourceLine != null) io.Err.WriteLine(at + CommandSpec.Format(CommandSpec.Diagnostics.SourceLine, ("line", e.SourceLine)));
            return 2;
        }
        catch (ParseException e) { io.Err.WriteLine($"bash: {e.Message}"); return 2; }
        if (++_scriptDepth > 100) throw new LimitExceeded("maximum script nesting level exceeded (100)");
        var savedArg0 = _arg0;
        int sources = _sources.Count, where = _errWhere.Count, line = _lineno;
        if ((process?.File ?? sourced) is { } file) _sources.Add(file);
        if (process != null) _errWhere.Add((process.File ?? process.Arg0, process.File != null));
        else if (sourced != null) _errWhere.Add((sourced, true));
        try
        {
            if (!subshell)
            {
                var savedPos = Positional;
                if (positional != null) Positional = positional;
                try { return await ExecScriptAsync(script, io, ct); }
                finally { if (positional != null) Positional = savedPos; }
            }
            var saved = SaveState();
            Traps = [];
            io = io with { Out = new SwitchWriter(io.Out), Err = new SwitchWriter(io.Err) }; // `exec >file` in a script ends with it
            if (process != null) BecomeNewProcess(process);
            if (positional != null) Positional = positional;
            int outerSubshell = _subshell;
            if (process != null) _subshell = 0; // a new bash ends with its own status (127 for set -u), not a subshell's 1
            try
            {
                if (process?.Login == true) await SourceLoginFilesAsync(io, ct);
                try { return process != null ? await ExecTopLevelAsync(script, io, ct) : await ExecScriptAsync(script, io, ct); }
                finally { await RunExitTrapAsync(io, ct); }
            }
            catch (ExitSignal e) { return e.Code; }
            catch (LineAbort) { return 1; } // $(...) ends with the bad substitution
            finally { _subshell = outerSubshell; RestoreState(saved); }
        }
        finally
        {
            _scriptDepth--;
            _arg0 = savedArg0;
            _sources.RemoveRange(sources, _sources.Count - sources);
            _errWhere.RemoveRange(where, _errWhere.Count - where);
            _lineno = line;
        }
    }

    /// <summary>What a child bash inherits: exported variables (not their other attributes) and exported functions, no arrays, aliases or options.</summary>
    async Task SourceLoginFilesAsync(Io io, CancellationToken ct)
    {
        var home = Environment.GetValueOrDefault("HOME", "");
        var personal = new[] { "/.bash_profile", "/.bash_login", "/.profile" }.Select(f => home + f).FirstOrDefault(f => Fs.IsFile(f) && Fs.CanRead(f));
        foreach (var file in new[] { "/etc/profile", personal })
        {
            if (file == null || !Fs.IsFile(file) || !Fs.CanRead(file)) continue;
            SourceDepth++;
            try { await RunScriptTextAsync(Fs.ReadAllText(file), io, subshell: false, ct, sourced: file); }
            catch (ReturnSignal) { }
            finally { SourceDepth--; }
        }
    }

    void BecomeNewProcess(NewProcess process)
    {
        Environment = Environment.Where(e => IsExported(e.Key)).ToDictionary(StringComparer.Ordinal);
        VarAttrs = Environment.Keys.ToDictionary(k => k, _ => "x", StringComparer.Ordinal);
        Arrays = new(StringComparer.Ordinal);
        Aliases = new(StringComparer.Ordinal);
        Functions = Functions.Where(f => ExportedFunctions.Contains(f.Key)).ToDictionary(StringComparer.Ordinal);
        Errexit = Pipefail = Nounset = Xtrace = Noglob = Noclobber = false;
        Hashed = new(StringComparer.Ordinal);
        _arg0 = process.Arg0;
        if (process.Login) Shopts.Add("login_shell"); else Shopts.Remove("login_shell");
        process.Setup?.Invoke();
    }

    async Task RunExitTrapAsync(Io io, CancellationToken ct)
    {
        if (!Traps.Remove("EXIT", out var action) || action.Length == 0) return;
        var savedCode = LastExitCode;
        try { await RunScriptTextAsync(action, io, subshell: false, ct); }
        catch (ControlFlow) { }
        LastExitCode = savedCode;
    }

    sealed record State(string Cwd, Dictionary<string, string> Env, Dictionary<string, string> Aliases,
        Dictionary<string, FunctionDef> Functions, List<string> Positional, bool Errexit, bool Pipefail, bool Nounset,
        HashSet<string> Shopts, Dictionary<string, string> Traps, Dictionary<string, ShellArray> Arrays)
    {
        public required Dictionary<string, (string Path, int Hits)> Hashed { get; init; }
        public required (bool Noglob, bool Xtrace, bool Noclobber, bool IfsUnset) Flags { get; init; }
        public required Dictionary<string, string> Attrs { get; init; }
        public long VariableChars { get; init; }
        public int Umask { get; init; }
    }

    // arrays are shared with the saved state and copied on first write (see Writable), so a subshell or
    // pipeline stage doesn't copy every element just to start
    State SaveState()
    {
        _epoch = NextEpoch();
        _subshell++;
        return new(_cwd, new(Environment), new(Aliases), new(Functions), Positional, Errexit, Pipefail, Nounset, [.. Shopts], new(Traps), new(Arrays))
            { Flags = (Noglob, Xtrace, Noclobber, _ifsUnset), Attrs = new(VarAttrs), VariableChars = _variableChars, Hashed = new(Hashed), Umask = Fs.Umask };
    }

    void RestoreState(State s)
    {
        _epoch = NextEpoch();
        _subshell--;
        Fs.Umask = s.Umask;
        (Noglob, Xtrace, Noclobber, _ifsUnset) = s.Flags;
        _variableChars = s.VariableChars; // was a bound for the state coming back
        VarAttrs = s.Attrs;
        Arrays = s.Arrays;
        Environment = s.Env;
        Aliases = s.Aliases;
        Functions = s.Functions;
        Positional = s.Positional;
        Errexit = s.Errexit;
        Pipefail = s.Pipefail;
        Nounset = s.Nounset;
        Shopts = s.Shopts;
        Traps = s.Traps;
        Hashed = s.Hashed;
        _cwd = Fs.IsDirectory(s.Cwd) ? s.Cwd : "/";
    }

    // ---------- lists ----------

    /// <summary>A script's top level (a call, bash -c, a script file): a bad substitution drops the rest of its line only.</summary>
    async Task<int> ExecTopLevelAsync(Script script, Io io, CancellationToken ct)
    {
        int code = 0, skip = 0;
        static int LineOf(AndOr item) => item.First.Commands.FirstOrDefault()?.Line ?? 0;
        foreach (var item in script.Items)
        {
            if (skip > 0 && LineOf(item) == skip) continue;
            skip = 0;
            try { code = await ExecScriptAsync(new Script([item]), io, ct); }
            catch (LineAbort) { code = LastExitCode = 1; skip = LineOf(item); }
        }
        return code;
    }

    async Task<int> ExecScriptAsync(Script script, Io io, CancellationToken ct)
    {
        int code = 0;
        foreach (var item in script.Items)
        {
            if (item.Background) { code = await ExecBackgroundAsync(item with { Background = false }, io, ct); continue; }
            // every command but the last of an && / || list is a condition: set -e and the ERR trap ignore it
            code = await ExecListPipelineAsync(item.First, item.Rest.Count > 0, io, ct);
            int lastRun = 0;
            bool negated = item.First.Negate;
            for (int k = 0; k < item.Rest.Count; k++)
            {
                var (op, next) = item.Rest[k];
                if ((op == "&&") != (code == 0)) continue;
                code = await ExecListPipelineAsync(next, k < item.Rest.Count - 1, io, ct);
                lastRun = k + 1;
                negated = next.Negate;
            }
            if (code != 0 && _condDepth == 0 && lastRun == item.Rest.Count && !negated)
            {
                if (Traps.TryGetValue("ERR", out var onError) && onError.Length > 0 && !_inErrTrap)
                {
                    _inErrTrap = true;
                    try { await RunScriptTextAsync(onError, io, subshell: false, ct); }
                    finally { _inErrTrap = false; LastExitCode = code; }
                }
                if (Errexit) throw new ExitSignal(code); // set -e: never inside if/while conditions
            }
        }
        return code;
    }

    async Task<int> ExecListPipelineAsync(Pipeline p, bool condition, Io io, CancellationToken ct)
    {
        if (!condition) return await ExecPipelineAsync(p, io, ct);
        _condDepth++;
        try { return await ExecPipelineAsync(p, io, ct); }
        finally { _condDepth--; }
    }

    // ---------- jobs ----------

    /// <summary>
    /// cmd &amp;: runs now, to completion, in a subshell; $! gets a job id that `wait` reports its status for.
    /// shortcut: jobs run in order, not concurrently; run them truly in parallel if agents start relying on overlap.
    /// </summary>
    async Task<int> ExecBackgroundAsync(AndOr item, Io io, CancellationToken ct)
    {
        var saved = SaveState();
        int code;
        try { code = await ExecScriptAsync(new Script([item]), io with { Stdin = new StdinBuffer("") }, ct); }
        catch (ExitSignal e) { code = e.Code; }
        catch (LineAbort) { code = 1; }
        finally { RestoreState(saved); }
        _lastBg = ++_nextPid;
        _jobs[_lastBg] = code;
        LastExitCode = 0;
        return 0;
    }

    /// <summary>`wait [id]`: the job's exit status (it has already finished), 127 for an unknown id.</summary>
    internal int WaitFor(int pid) => _jobs.Remove(pid, out var code) ? code : 127;

    /// <summary>`wait -n`: the status of the next finished job (the oldest), or 127 when there are none.</summary>
    internal int WaitNext() => _jobs.Count == 0 ? 127 : WaitFor(_jobs.Keys.Min());
    internal bool IsJob(int pid) => _jobs.ContainsKey(pid);

    /// <summary>`wait` with no arguments: every job has finished; forget them.</summary>
    internal void ForgetJobs() => _jobs.Clear();

    // ---------- pipelines ----------

    async Task<int> ExecPipelineAsync(Pipeline p, Io io, CancellationToken ct)
    {
        if (p.Timed)
        {
            var elapsed = StartTimer();
            int status = p.Commands.Count == 0 ? 0 : await ExecPipelineAsync(p with { Timed = false }, io, ct);
            var t = elapsed();
            io.Err.Write(p.PosixTime ? FormattableString.Invariant($"real {t.TotalSeconds:0.00}\nuser 0.00\nsys 0.00\n")
                : FormattableString.Invariant($"\nreal\t{(int)t.TotalMinutes}m{t.TotalSeconds % 60:0.000}s\nuser\t0m0.000s\nsys\t0m0.000s\n"));
            return LastExitCode = status;
        }
        if (!p.Negate) return await RunPipelineAsync(p, io, ct);
        _condDepth++; // like a condition, a negated pipeline is exempt from set -e
        int code;
        try { code = await RunPipelineAsync(p, io, ct); }
        finally { _condDepth--; }
        return LastExitCode = code == 0 ? 1 : 0;
    }

    async Task<int> RunPipelineAsync(Pipeline p, Io io, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var input = io.Stdin;
        int code = 0, failed = 0;
        var statuses = new int[p.Commands.Count];
        for (int k = 0; k < p.Commands.Count; k++)
        {
            bool last = k == p.Commands.Count - 1;
            // shortcut: pipeline stages run one after another with buffered text; stream if huge outputs or infinite producers matter.
            var output = last ? io.Out : new CappedWriter(this);
            var stage = io with { Stdin = input, Out = output };
            if (p.Commands.Count == 1) code = await ExecCommandAsync(p.Commands[k], stage, ct);
            else if (last)
            {
                // shortcut: the last stage keeps its variables (bash's lastpipe), so `cmd | while read` loops can count; only `exit` is contained
                try { code = await ExecCommandAsync(p.Commands[k], stage, ct); }
                catch (ExitSignal e) { code = e.Code; }
                catch (LineAbort) { code = 1; }
            }
            else
            {
                // every stage but the last is a subshell: `exit`, `cd` and assignments stay inside it
                var saved = SaveState();
                try { code = await ExecCommandAsync(p.Commands[k], stage, ct); }
                catch (ExitSignal e) { code = e.Code; }
                catch (LineAbort) { code = 1; }
                finally { RestoreState(saved); }
            }
            if (code != 0) failed = code;
            statuses[k] = code;
            if (!last) input = new StdinBuffer(output.ToString()!);
        }
        _pipeStatus = statuses;
        if (Pipefail) code = failed;
        LastExitCode = code;
        return code;
    }

    // ---------- compound commands ----------

    async Task<int> ExecCommandAsync(Command cmd, Io io, CancellationToken ct)
    {
        // a stack overflow can't be caught in .NET and would take the host process down
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new LimitExceeded("maximum nesting level exceeded");
        if (cmd is SimpleCommand { Assignments.Count: 0, Words: [var only] } && only.Is("exec")) return await ExecRedirectsAsync(cmd.Redirects, io, ct);
        if (cmd.Line > 0) _lineno = cmd.Line; // a failed redirection reports this line too
        var sinks = new List<FileSink>();
        var redirected = await ApplyRedirectsAsync(cmd.Redirects, io, sinks, ct);
        if (redirected is not { } rio) return 1;

        int code;
        try
        {
            code = cmd switch
            {
                SimpleCommand s => await ExecSimpleAsync(s, rio, ct),
                GroupCommand { Subshell: false } g => await ExecScriptAsync(g.Body, rio, ct),
                GroupCommand g => await ExecSubshellAsync(g.Body, rio, ct),
                IfCommand i => await ExecIfAsync(i, rio, ct),
                LoopCommand l => await ExecLoopAsync(l, rio, ct),
                ForCommand { Select: true } f => await ExecSelectAsync(f, rio, ct),
                ForCommand f => await ExecForAsync(f, rio, ct),
                ArithForCommand f => await ExecArithForAsync(f, rio, ct),
                CaseCommand c => await ExecCaseAsync(c, rio, ct),
                FunctionDef d => DefineFunction(d),
                ArithCommand a => await EvalArithAsync(a.Expr, rio, ct) != 0 ? 0 : 1,
                CondCommand c => await new CondEvaluator(this, c.Items, rio, ct).EvaluateAsync() ? 0 : 1,
                _ => throw new InvalidOperationException(cmd.GetType().Name),
            };
        }
        catch (BadSubstitution e) { throw BadSubstitutionStop(rio, e.Message, e.Ends); }
        catch (FatalError e) { throw Fatal(rio, e.Message, e.ExitCode); }
        catch (ShellException e)
        {
            rio.Err.WriteLine($"{ErrPrefix}{e.Message}");
            code = cmd is CondCommand ? 2 : e.ExitCode;
        }
        catch (RegexMatchTimeoutException) { throw new LimitExceeded("pattern matching took too long"); }
        finally
        {
            foreach (var sink in sinks)
            {
                try { Fs.AppendAllText(sink.Path, sink.ToString()); }
                catch (ShellException e) { io.Err.WriteLine($"{ErrPrefix}{e.Message}"); code = 1; }
            }
            if (_mounts.Items.Count > 0 && DateTime.UtcNow < _callDeadline) await _mounts.SettleAsync(io.Err); // live mode: what the command and its redirections wrote
        }
        return code;
    }

    int DefineFunction(FunctionDef d)
    {
        Functions[d.Name] = d;
        _funcWhere[d.Name] = _errWhere.Count > 0 && _errWhere[^1].File ? _errWhere[^1].Name : "environment";
        return 0;
    }

    async Task<int> ExecSubshellAsync(Script body, Io io, CancellationToken ct)
    {
        var saved = SaveState();
        try { return await ExecScriptAsync(body, io with { Out = new SwitchWriter(io.Out), Err = new SwitchWriter(io.Err) }, ct); }
        catch (ExitSignal e) { return e.Code; }
        catch (LineAbort) { return 1; }
        finally { RestoreState(saved); }
    }

    async Task<int> ConditionAsync(Script cond, Io io, CancellationToken ct)
    {
        _condDepth++;
        try { return await ExecScriptAsync(cond, io, ct); }
        finally { _condDepth--; }
    }

    async Task<int> ExecIfAsync(IfCommand c, Io io, CancellationToken ct)
    {
        foreach (var (cond, body) in c.Branches)
            if (await ConditionAsync(cond, io, ct) == 0) return await ExecScriptAsync(body, io, ct);
        return c.Else != null ? await ExecScriptAsync(c.Else, io, ct) : 0;
    }

    /// <summary>Runs a loop body; returns (exit code, true if `break`).</summary>
    async Task<(int Code, bool Break)> LoopBodyAsync(Script body, Io io, CancellationToken ct)
    {
        LoopDepth++;
        try { return (await ExecScriptAsync(body, io, ct), false); }
        catch (LoopControl lc)
        {
            if (lc.Levels > 1 && LoopDepth > 1) throw new LoopControl(lc.IsBreak, lc.Levels - 1);
            return (0, lc.IsBreak);
        }
        finally { LoopDepth--; }
    }

    void CheckIterations(ref int n, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // nested loops multiply, so one tool call also has a total budget (10x the single-loop cap)
        const string instead = "process the whole input with one command (awk, sed, xargs) instead of a shell loop";
        if (++n > Options.MaxLoopIterations)
            throw new LimitExceeded($"loop aborted after {Options.MaxLoopIterations} iterations (the limit for one loop); {instead}");
        if (++_totalIterations > Options.MaxLoopIterations * 10L)
            throw new LimitExceeded($"loop aborted after {Options.MaxLoopIterations * 10L} iterations in this call (the limit for all loops in one call); {instead}");
    }

    async Task<int> ExecLoopAsync(LoopCommand l, Io io, CancellationToken ct)
    {
        int code = 0, n = 0;
        while (true)
        {
            if ((await ConditionAsync(l.Cond, io, ct) == 0) == l.Until) break;
            CheckIterations(ref n, ct); // counts body runs, so a loop of exactly the limit still finishes
            var (c, brk) = await LoopBodyAsync(l.Body, io, ct);
            code = c;
            if (brk) break;
        }
        return code;
    }

    async Task<int> ExecForAsync(ForCommand f, Io io, CancellationToken ct)
    {
        var items = f.Items == null ? Positional.ToList() : await ExpandWordsAsync(f.Items, io, ct);
        int code = 0, n = 0;
        foreach (var item in items)
        {
            CheckIterations(ref n, ct);
            SetVar(f.Var, item);
            var (c, brk) = await LoopBodyAsync(f.Body, io, ct);
            code = c;
            if (brk) break;
        }
        return code;
    }

    /// <summary>select: the menu and the PS3 prompt go to stderr, choices come from stdin, until `break` or end of input.</summary>
    async Task<int> ExecSelectAsync(ForCommand f, Io io, CancellationToken ct)
    {
        var items = f.Items == null ? Positional.ToList() : await ExpandWordsAsync(f.Items, io, ct);
        int code = 0, n = 0;
        bool menu = true;
        while (true)
        {
            if (menu) for (int k = 0; k < items.Count; k++) io.Err.WriteLine($"{k + 1}) {items[k]}");
            io.Err.Write(GetVariable("PS3") is { Length: > 0 } ps3 ? ps3 : "#? ");
            if (io.Stdin.ReadLine() is not { } line) { io.Err.WriteLine(); break; }
            CheckIterations(ref n, ct);
            if (menu = line.Trim().Length == 0) continue; // an empty answer shows the menu again
            SetVar("REPLY", line);
            SetVar(f.Var, int.TryParse(line.Trim(), out var choice) && choice >= 1 && choice <= items.Count ? items[choice - 1] : "");
            var (c, brk) = await LoopBodyAsync(f.Body, io, ct);
            code = c;
            if (brk) break;
        }
        return code;
    }

    async Task<int> ExecArithForAsync(ArithForCommand f, Io io, CancellationToken ct)
    {
        int code = 0, n = 0;
        if (f.Init.Length > 0) await EvalArithAsync(f.Init, io, ct);
        while (f.Cond.Length == 0 || await EvalArithAsync(f.Cond, io, ct) != 0)
        {
            CheckIterations(ref n, ct);
            var (c, brk) = await LoopBodyAsync(f.Body, io, ct);
            code = c;
            if (brk) break;
            if (f.Step.Length > 0) await EvalArithAsync(f.Step, io, ct);
        }
        return code;
    }

    async Task<int> ExecCaseAsync(CaseCommand c, Io io, CancellationToken ct)
    {
        var subject = await ExpandSingleAsync(c.Subject, io, ct);
        int code = 0;
        bool fallThrough = false;
        foreach (var (patterns, body, terminator) in c.Clauses)
        {
            if (!fallThrough)
            {
                bool matched = false;
                foreach (var pattern in patterns)
                    if (matched = Glob.ToRegex(await PatternAsync(pattern, io, ct), Shopts.Contains("nocasematch"), slashes: true).IsMatch(subject)) break;
                if (!matched) continue;
            }
            code = await ExecScriptAsync(body, io, ct);
            if (terminator == ";;") return code;
            fallThrough = terminator == ";&"; // ;;& goes on testing the next patterns
        }
        return code;
    }
}

// ---------- conditional expressions ----------

/// <summary>Evaluates [[ ... ]].</summary>
internal sealed class CondEvaluator(Shell shell, List<Word> items, Shell.Io io, CancellationToken ct)
{
    static readonly HashSet<string> Unary = ["-z", "-n", "-e", "-f", "-d", "-s", "-r", "-w", "-x", "-L", "-h", "-a", "-v", "-o", "-t", "-b", "-c", "-p", "-S", "-g", "-u", "-k", "-O", "-G", "-N"];
    int _p;

    bool IsOp(string s) => _p < items.Count && items[_p].Is(s);

    public async Task<bool> EvaluateAsync()
    {
        if (items.Count == 0) throw new ShellException(CommandSpec.Diagnostics.ConditionalSyntax, 2);
        var v = await Or();
        if (_p < items.Count) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.ConditionalSyntaxNear, ("token", items[_p].ToString())), 2);
        return v;
    }

    async Task<bool> Or()
    {
        var l = await And();
        while (IsOp("||")) { _p++; var r = await And(); l = l || r; }
        return l;
    }

    async Task<bool> And()
    {
        var l = await Not();
        while (IsOp("&&")) { _p++; var r = await Not(); l = l && r; }
        return l;
    }

    async Task<bool> Not()
    {
        if (IsOp("!")) { _p++; return !await Not(); }
        return await Primary();
    }

    async Task<bool> Primary()
    {
        if (IsOp("("))
        {
            _p++;
            var v = await Or();
            if (!IsOp(")")) throw new ShellException(CommandSpec.Diagnostics.ConditionalParen, 2);
            _p++;
            return v;
        }
        if (_p >= items.Count) throw new ShellException(CommandSpec.Diagnostics.ConditionalEnd, 2);
        if (items[_p].UnquotedLiteral is { } u && Unary.Contains(u) && _p + 1 < items.Count && !(_p + 2 < items.Count && Parser.IsCondBinary(items[_p + 1])))
        {
            var arg = await shell.ExpandSingleAsync(items[_p + 1], io, ct);
            _p += 2;
            return Test.Unary(shell, u == "-a" ? "-e" : u == "-h" ? "-L" : u, arg);
        }
        var left = items[_p++];
        if (_p < items.Count && Parser.IsCondBinary(items[_p]))
        {
            var op = items[_p++].UnquotedLiteral!;
            if (_p >= items.Count) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.ConditionalBinaryArgument, ("op", op)), 2);
            var right = items[_p++];
            var l = await shell.ExpandSingleAsync(left, io, ct);
            switch (op)
            {
                case "==" or "=" or "!=":
                    bool match = Glob.ToRegex(await shell.PatternAsync(right, io, ct), shell.Shopts.Contains("nocasematch"), slashes: true).IsMatch(l);
                    return op == "!=" ? !match : match;
                case "=~":
                    // quoted parts of the regex match literally
                    var reText = new StringBuilder();
                    foreach (var part in right.Parts)
                    {
                        var t = await shell.ExpandPartAsync(part, io, ct);
                        reText.Append(part.Quoted ? Regex.Escape(t) : t);
                    }
                    var re = reText.ToString();
                    try
                    {
                        var m = Glob.Cached(Builtins.PosixToNet(re, extended: true), shell.Shopts.Contains("nocasematch") ? RegexOptions.IgnoreCase : RegexOptions.None, Shell.RegexTimeout).Match(l);
                        // BASH_REMATCH: the whole match, then each group; emptied when nothing matches
                        var groups = new ShellArray(false);
                        if (m.Success) for (int g = 0; g < m.Groups.Count; g++) groups.Set(g.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Groups[g].Value);
                        shell.Environment.Remove("BASH_REMATCH");
                        shell.Arrays["BASH_REMATCH"] = groups;
                        return m.Success;
                    }
                    catch (ArgumentException) { throw new ShellException($"invalid regular expression `{re}'", 2); }
                case "<": return string.CompareOrdinal(l, await shell.ExpandSingleAsync(right, io, ct)) < 0;
                case ">": return string.CompareOrdinal(l, await shell.ExpandSingleAsync(right, io, ct)) > 0;
                case "-nt": return Test.Newer(shell, l, await shell.ExpandSingleAsync(right, io, ct));
                case "-ot": return Test.Newer(shell, await shell.ExpandSingleAsync(right, io, ct), l);
                case "-ef": return shell.Fs.SameFile(shell.ResolvePath(l), shell.ResolvePath(await shell.ExpandSingleAsync(right, io, ct)));
                default:
                    var a = Arith.Eval(shell, l);
                    var b = Arith.Eval(shell, await shell.ExpandSingleAsync(right, io, ct));
                    return op switch { "-eq" => a == b, "-ne" => a != b, "-lt" => a < b, "-le" => a <= b, "-gt" => a > b, "-ge" => a >= b, _ => false };
            }
        }
        return (await shell.ExpandSingleAsync(left, io, ct)).Length > 0;
    }
}
