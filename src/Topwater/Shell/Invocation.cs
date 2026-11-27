// Shell: simple commands, Invoke, script files, PATH and hash lookup, functions. See docs/architecture.md.
using System.Text.RegularExpressions;
using Topwater.Parsing;

namespace Topwater;

public sealed partial class Shell
{
    // ---------- simple commands ----------

    static bool IsDeclaration(Word w) => w.UnquotedLiteral is "local" or "declare" or "typeset" or "readonly" or "export";

    async Task<int> ExecSimpleAsync(SimpleCommand c, Io io, CancellationToken ct)
    {
        _lineno = c.Line;
        if (Traps.Count > 0 && !_inDebugTrap && Traps.TryGetValue("DEBUG", out var debug) && debug.Length > 0)
        {
            // shortcut: the DEBUG trap runs before simple commands only, not before (( )), [[ ]], for and case
            _inDebugTrap = true;
            var status = LastExitCode;
            try { await RunScriptTextAsync(debug, io, subshell: false, ct); }
            finally { _inDebugTrap = false; LastExitCode = status; }
        }
        var words = c.Words;
        bool declaration = words.Count > 1 && IsDeclaration(words[0]);
        if (declaration && !words[0].Is("export") && await DeclareArraysAsync(words, io, ct) is { } rest)
        {
            if (rest.Count == 1) return 0;
            words = rest;
        }
        var argv = declaration ? await ExpandDeclarationAsync(words, io, ct) : await ExpandWordsAsync(words, io, ct);
        var assigned = new List<(string Name, string Value, bool Append)>();
        foreach (var a in c.Assignments)
        {
            try
            {
                if (a.Elements != null || a.Index != null) { await AssignArrayAsync(a, io, ct); continue; }
                var v = await ExpandAssignmentAsync(a.Value, io, ct);
                if (Xtrace) io.Err.WriteLine($"+ {a.Name}{(a.Append ? "+=" : "=")}{TraceQuote(v)}");
                if (words.Count > 0) { assigned.Add((a.Name, v, a.Append)); continue; }
                // with no command, assignments apply left to right: IFS=, parts=($csv) splits on the comma
                SetVar(a.Name, v, a.Append);
            }
            catch (ShellException e) when (words.Count == 0 && e.Message.EndsWith("readonly variable"))
            {
                io.Err.WriteLine($"{ErrPrefix}{e.Message}");
                throw new ExitSignal(1); // like bash, assigning a read-only variable ends the script
            }
        }

        if (argv.Count == 0)
        {
            foreach (var (k, v, append) in assigned) SetVar(k, v, append);
            // only a command substitution leaves its exit code behind; `$empty` alone is a successful no-op
            return c.Words.Concat(c.Assignments.Select(a => a.Value)).Any(w => w.Parts.Any(p => p is CommandSubPart)) ? LastExitCode : 0;
        }

        if (Aliases.TryGetValue(argv[0], out var alias))
        {
            // an alias holding a pipeline or list is shell text, not just words
            if (assigned.Count == 0 && alias.IndexOfAny(['|', ';', '&', '<', '>']) >= 0 && Aliases.Remove(argv[0]))
                try { return await RunScriptTextAsync(alias + " " + string.Join(' ', argv.Skip(1).Select(Printf.ShellQuote)), io, subshell: false, ct); }
                finally { Aliases[argv[0]] = alias; }   // removed while it runs so it can't expand itself
            argv = [.. await ExpandWordsAsync(Parser.ParseWords(alias), io, ct), .. argv.Skip(1)];
        }
        if (Xtrace) io.Err.WriteLine("+ " + string.Join(' ', argv.Select(TraceQuote)));

        try
        {
            if (assigned.Count == 0) return await InvokeAsync(argv, io, ct);

            // VAR=x cmd: the variable only exists for that command
            var saved = assigned.ToDictionary(a => a.Name, a => (Value: Environment.TryGetValue(a.Name, out var v) ? v : null, Attrs: VarAttrs.GetValueOrDefault(a.Name)));
            foreach (var (k, v, append) in assigned)
            {
                Environment[k] = append ? Environment.GetValueOrDefault(k) + v : v;
                SetAttrs(k, "x"); // exported to the command
            }
            try { return await InvokeAsync(argv, io, ct); }
            finally
            {
                foreach (var (k, (v, attrs)) in saved)
                {
                    if (v == null) Environment.Remove(k); else Environment[k] = v;
                    if (attrs == null) VarAttrs.Remove(k); else VarAttrs[k] = attrs;
                }
            }
        }
        finally { _lastArg = argv[^1]; }
    }

    /// <summary>`local x="a b" y=$(cmd)`: arguments that look like assignments are expanded as assignments (no splitting).</summary>
    async Task<List<string>> ExpandDeclarationAsync(List<Word> words, Io io, CancellationToken ct)
    {
        var argv = new List<string>();
        foreach (var w in words)
        {
            if (argv.Count > 0 && Parser.TryAssignment(w) is { Elements: null, Index: null } a)
                argv.Add(a.Name + (a.Append ? "+=" : "=") + await ExpandAssignmentAsync(a.Value, io, ct));
            else argv.AddRange(await ExpandWordsAsync([w], io, ct));
        }
        return argv;
    }

    /// <summary>bash's own "command not found" line, and nothing else (the tool description already points at `help`).</summary>
    void ReportNotFound(Io io, string name) =>
        io.Err.WriteLine(ErrPrefix + CommandSpec.Format(CommandSpec.Diagnostics.CommandNotFound, ("command", name)));

    // ---------- invoke ----------

    /// <param name="skipFunctions">`command ls` / `builtin cd`: run the command itself even when a function has its name.</param>
    internal async Task<int> InvokeAsync(IReadOnlyList<string> argv, Io io, CancellationToken ct, bool skipFunctions = false)
    {
        var name = argv[0];
        if (!skipFunctions && Functions.TryGetValue(name, out var fn)) return await CallFunctionAsync(fn.Body, argv, io, ct);
        if (name.Contains('/'))
        {
            var path = ResolvePath(name);
            if (VirtualFileSystem.GetParent(path) is "/usr/bin" or "/bin" or "/usr/local/bin" && _commands.ContainsKey(VirtualFileSystem.GetName(path)))
                name = VirtualFileSystem.GetName(path);
            else return await RunScriptFileAsync(name, path, argv, io, ct);
        }
        else if (!Builtins.IsShellBuiltin(this, name) && !IsUnfiled(name))
        {
            if (HashLookup(name) is not { } found)
            {
                found = SearchPath(name);
                if (found == null)
                {
                    if (Environment.GetValueOrDefault("PATH", "") == "") io.Err.WriteLine($"{ErrPrefix}{name}: No such file or directory");
                    else ReportNotFound(io, name);
                    return 127;
                }
                if (found.Contains('/')) Hashed[name] = (found, 1); // bash leaves a name found through an empty PATH entry out of the table
            }
            if (StubCommand(found) is not { } registered) return await RunScriptFileAsync(found, ResolvePath(found), [found, .. argv.Skip(1)], io, ct);
            name = registered;
        }
        if (!_commands.TryGetValue(name, out var cmd))
        {
            ReportNotFound(io, argv[0]);
            return 127;
        }
        var ctx = new CommandContext(this, name, argv.Skip(1).ToList(), io.Stdin, io.Out, io.Err, ct) { Fds = io.Fds };
        if (argv.Count == 2 && _builtinNames?.Contains(name) == true && Builtins.StandardOption(ctx, cmd, argv[1])) return 0;
        var mounts = _mounts.Items.Count > 0 ? _mounts : null;
        if (mounts != null) await mounts.BeforeAsync(name, argv);
        try { return await RunHandlerAsync(cmd, ctx, name, io); }
        finally { if (mounts != null && DateTime.UtcNow < _callDeadline) await mounts.AfterAsync(io.Err); }
    }

    async Task<int> RunHandlerAsync(RegisteredCommand cmd, CommandContext ctx, string name, Io io)
    {
        try
        {
            return await cmd.Handler(ctx);
        }
        catch (OperationCanceledException) { throw; }
        catch (ControlFlow) { throw; }
        catch (UsageException e) when (!Builtins.IsShellBuiltin(this, name))
        {
            io.Err.Write(Builtins.UsageError(name, e.Message));
            return CommandSpec.Commands.TryGetValue(name, out var spec) ? spec.UsageExitCode : 1;
        }
        catch (ShellException e)
        {
            io.Err.WriteLine($"{(e is UsageException || Builtins.IsShellBuiltin(this, name) ? ErrPrefix : "")}{name}: {e.Message}");
            return e is UsageException ? 2 : e.ExitCode;
        }
        catch (RegexMatchTimeoutException) when (DateTime.UtcNow >= _callDeadline - TimeSpan.FromMilliseconds(50))
        {
            throw new OperationCanceledException();  // the match ran into the call's time limit
        }
        catch (RegexMatchTimeoutException) { throw new LimitExceeded("pattern matching took too long"); }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            io.Err.WriteLine($"{name}: invalid number{(BadNumber.Match(e.Message) is { Success: true } m ? $": '{m.Groups[1].Value}'" : "")}");
            return 1;
        }
        catch (Exception e)
        {
            io.Err.WriteLine($"{name}: {e.Message}");
            return 1;
        }
    }

    static readonly Regex BadNumber = new("input string '(.*)' was not", RegexOptions.Singleline);

    // ---------- script files ----------

    async Task<int> RunScriptFileAsync(string name, string path, IReadOnlyList<string> argv, Io io, CancellationToken ct)
    {
        if (Fs.IsDirectory(path)) { io.Err.WriteLine($"{ErrPrefix}{name}: Is a directory"); return 126; }
        if (!Fs.IsFile(path)) { var why = Fs.WhyMissing(path); io.Err.WriteLine($"{ErrPrefix}{name}: {why}"); return why == "Permission denied" ? 126 : 127; }
        if (!Fs.CanExecute(path) || !Fs.CanRead(path)) { io.Err.WriteLine($"{ErrPrefix}{name}: Permission denied"); return 126; }
        var text = Fs.ReadAllText(path);
        if (text.StartsWith(ExecutableStub[..4]))
        {
            if (StubCommand(path) is { } stub) return await InvokeAsync([stub, .. argv.Skip(1)], io, ct, skipFunctions: true);
            io.Err.WriteLine(ErrPrefix + CommandSpec.Format(CommandSpec.Diagnostics.CannotExecuteBinary, ("name", name)));
            return 126;
        }
        if (text.StartsWith("#!"))
        {
            var line = text[2..(text.IndexOf('\n') is var nl and >= 0 ? nl : text.Length)].Trim();
            if (!Regex.IsMatch(line, @"(^|/|\s)(ba)?sh(\s|$)"))
            {
                // the hand-off can lead back to this script (#!/usr/bin/env self), so it counts as a nesting level; no command
                // runs between levels, so ExecCommandAsync's stack check would never see this recursion
                if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
                    throw new LimitExceeded("maximum nesting level exceeded");
                if (++_scriptDepth > 100) throw new LimitExceeded("maximum script nesting level exceeded (100)");
                try { return await RunInterpreterAsync(name, line, argv, io, ct); }
                finally { _scriptDepth--; }
            }
        }
        return await RunScriptTextAsync(text, io, subshell: true, ct, argv.Skip(1).ToList(), new NewProcess(name, name));
    }

    /// <summary>The script a script file's #! line hands it to (resolved), or null when that is a program or there is none.</summary>
    string? ScriptInterpreter(string path)
    {
        if (!Fs.CanRead(path) || StubCommand(path) != null) return null;
        var text = Fs.ReadAllText(path);
        if (!text.StartsWith("#!")) return null;
        var first = text[2..(text.IndexOf('\n') is var nl and >= 0 ? nl : text.Length)].Trim().Split([' ', '\t'], 2)[0];
        return first.Length > 0 && ResolvePath(first) is var abs && Fs.IsFile(abs) && Fs.CanExecute(abs) && StubCommand(abs) == null ? abs : null;
    }

    /// <summary>A #! line naming another program: like the kernel, run it with its one optional argument, the script and the
    /// script's arguments, when it is one of the shell's commands.</summary>
    async Task<int> RunInterpreterAsync(string name, string line, IReadOnlyList<string> argv, Io io, CancellationToken ct)
    {
        var parts = line.Split([' ', '\t'], 2);
        var opt = parts.Length > 1 ? parts[1].Trim() : "";
        // an interpreter that is itself a script runs as one; like Linux, at most four such levels (a loop never ends otherwise)
        if (StubCommand(parts[0]) == null && ResolvePath(parts[0]) is var interp && Fs.IsFile(interp) && Fs.CanExecute(interp))
        {
            int levels = 0;
            for (var next = interp; next != null && ++levels <= 4;) next = ScriptInterpreter(next);
            if (levels > 4)
            {
                io.Err.WriteLine("bash: " + CommandSpec.Format(CommandSpec.Diagnostics.BadInterpreter, ("name", name), ("interpreter", parts[0]), ("reason", CommandSpec.Errno.ELOOP)));
                return 126;
            }
            return await RunScriptFileAsync(parts[0], interp, [parts[0], .. opt == "" ? [] : new[] { opt }, name, .. argv.Skip(1)], io, ct);
        }
        if (StubCommand(parts[0]) is not { } program)
        {
            io.Err.WriteLine(ErrPrefix + CommandSpec.Format(CommandSpec.Diagnostics.RequiredFileNotFound, ("name", name)));
            return 127;
        }
        List<string> words = opt == "" ? [] : program == "env" && opt.StartsWith("-S") ? [.. opt[2..].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries)] : [opt];
        if (program != "env") return await InvokeAsync([program, .. words, name, .. argv.Skip(1)], io, ct, skipFunctions: true);
        // shortcut: env in a #! line only looks the program up on PATH; env's own options are not supported
        if (words.Count == 0 || !IsUnfiled(words[0]) && !SearchPathAll(words[0]).Any())
        {
            io.Err.WriteLine($"{parts[0]}: \u2018{(words.Count > 0 ? words[0] : "")}\u2019: No such file or directory");
            if (opt.Contains(' ')) io.Err.WriteLine($"{parts[0]}: use -[v]S to pass options in shebang lines");
            return 127;
        }
        return await InvokeAsync([.. words, name, .. argv.Skip(1)], io, ct, skipFunctions: true);
    }

    // ---------- lookup ----------

    /// <summary>A registered command with no program file (a shell-only builtin, or an app command added where there is no
    /// /usr/bin): it runs whatever PATH says.</summary>
    internal bool IsUnfiled(string name) => _commands.ContainsKey(name) && !name.Contains('/') && !Fs.IsFile("/usr/bin/" + name) && !Fs.IsFile("/bin/" + name);

    /// <summary>The registered command a program file stands for, or null when <paramref name="path"/> is not one of the stubs.</summary>
    internal string? StubCommand(string path)
    {
        var abs = ResolvePath(path);
        if (!Fs.IsFile(abs) || !Fs.CanRead(abs) || Fs.ReadAllText(abs) != ExecutableStub) return null;
        var name = VirtualFileSystem.GetName(Fs.RealPath(abs, out _) ?? abs);
        return _commands.ContainsKey(name) ? name : null;
    }

    /// <summary>Where PATH would have <paramref name="name"/>, directory by directory, as written; an empty entry is the cwd, and a
    /// leading ~ is expanded as bash does during the search.
    /// A wholly empty PATH searches the cwd for the bare name, which bash runs as typed and leaves out of the hash table.</summary>
    IEnumerable<string> PathCandidates(string name) =>
        Environment.GetValueOrDefault("PATH", "") == "" ? [name] : Environment.GetValueOrDefault("PATH", "").Split(':').Select(d => TildePrefix(d) is { } t ? t.Home + d[t.Length..] : d)
            .Select(d => d.Length == 0 ? "./" + name : d.TrimEnd('/') + "/" + name);

    /// <summary>PATH search as bash does it: the first executable regular file named <paramref name="name"/> in a directory the
    /// agent can search, else the first one that isn't executable (running it says "Permission denied"), else null.</summary>
    internal string? SearchPath(string name)
    {
        string? denied = null;
        foreach (var p in PathCandidates(name))
        {
            var abs = ResolvePath(p);
            if (!Fs.IsFile(abs)) continue;
            if (Fs.CanExecute(abs)) return p;
            denied ??= p;
        }
        return denied;
    }

    /// <summary>`type -a`, `which -a`: every executable regular file named <paramref name="name"/> on PATH.</summary>
    internal IEnumerable<string> SearchPathAll(string name) =>
        PathCandidates(name).Where(p => ResolvePath(p) is var abs && Fs.IsFile(abs) && Fs.CanExecute(abs));

    /// <summary>The hash table's entry for <paramref name="name"/>, counting a hit; the table empties first if PATH changed.</summary>
    internal string? HashLookup(string name)
    {
        SyncHashed();
        if (!Hashed.TryGetValue(name, out var h)) return null;
        Hashed[name] = (h.Path, h.Hits + 1);
        return h.Path;
    }

    internal void SyncHashed()
    {
        var path = Environment.GetValueOrDefault("PATH");
        if (path != _hashedFor) { Hashed.Clear(); _hashedFor = path; }
    }

    /// <summary>`type`, `command -v`: the program <paramref name="name"/> runs, from the hash table (counting a hit) or PATH; an
    /// app command with no program file is reported in /usr/bin as before.</summary>
    internal (string Path, bool Hashed)? Locate(string name)
    {
        if (name.Contains('/')) return ResolvePath(name) is var abs && Fs.IsFile(abs) && Fs.CanExecute(abs) ? (name, false) : null;
        if (HashLookup(name) is { } h) return (h, true);
        if (SearchPath(name) is { } p) return (p.Contains('/') ? p : ResolvePath(p), false);
        return IsAppOnly(name) ? ("/usr/bin/" + name, false) : null;
    }

    /// <summary>`type -a`, `which`: every program file <paramref name="name"/> could run, in PATH order.</summary>
    internal List<string> LocateAll(string name)
    {
        if (name.Contains('/')) return Locate(name) is { } l ? [l.Path] : [];
        var all = SearchPathAll(name).Select(p => p.Contains('/') ? p : ResolvePath(p)).ToList();
        if (all.Count == 0 && IsAppOnly(name)) all.Add("/usr/bin/" + name);
        return all;
    }

    /// <summary>A command the host registered without a program file (not a built-in that only lives in the shell, like hash or time).</summary>
    internal bool IsAppOnly(string name) =>
        IsUnfiled(name) && !(CommandSpec.Commands.TryGetValue(name, out var c) && (c.ShellBuiltin || !c.ProgramFile) && IsBuiltin(name));

    // ---------- functions ----------

    async Task<int> CallFunctionAsync(Command body, IReadOnlyList<string> argv, Io io, CancellationToken ct)
    {
        if (_locals.Count >= 200) throw new LimitExceeded($"{argv[0]}: maximum function nesting level exceeded (200)");
        var savedPos = Positional;
        var onReturn = Traps.GetValueOrDefault("RETURN");
        Positional = argv.Skip(1).ToList();
        _locals.Push([]);
        _localArrays.Push(new Dictionary<string, ShellArray?>());
        _funcNames.Add(argv[0]);
        var where = _funcWhere.GetValueOrDefault(argv[0], "environment");
        _errWhere.Add((where, where != "environment"));
        int code, line = _lineno;
        try { code = await ExecCommandAsync(body, io, ct); }
        catch (ReturnSignal r) { code = r.Code; }
        finally
        {
            _errWhere.RemoveAt(_errWhere.Count - 1);
            _lineno = line;
            // shadowed values come back without passing ChargeVariable, so they count again
            foreach (var (k, v) in _localArrays.Pop())
                if (v == null) Arrays.Remove(k); else { Arrays[k] = v; _variableChars += v.Items.Sum(e => (long)e.Key.Length + e.Value.Length); }
            foreach (var (k, (v, attrs)) in _locals.Pop())
            {
                if (v == null) Environment.Remove(k); else { Environment[k] = v; _variableChars += v.Length; }
                if (attrs == null) VarAttrs.Remove(k); else VarAttrs[k] = attrs;
            }
            Positional = savedPos;
            _funcNames.RemoveAt(_funcNames.Count - 1);
        }
        // a RETURN trap set inside the function runs as it returns
        if (Traps.TryGetValue("RETURN", out var trap) && trap != onReturn && trap.Length > 0)
        {
            Traps.Remove("RETURN");
            await RunScriptTextAsync(trap, io, subshell: false, ct);
        }
        return LastExitCode = code;
    }

    internal bool InFunction => _locals.Count > 0;

    /// <summary>True for a built-in command (not one the app registered or replaced).</summary>
    internal bool IsBuiltin(string name) => _builtinNames?.Contains(name) == true;

    /// <summary>set -x: an argument as bash traces it, quoted only when it needs to be.</summary>
    static string TraceQuote(string s) => s.Length > 0 && s.All(ch => char.IsAsciiLetterOrDigit(ch) || "_./=:,+@%^-".Contains(ch)) ? s : QuoteValue(s);

    /// <summary>${x@Q}: single quotes, or $'...' when the text holds control characters.</summary>
    internal static string QuoteValue(string s) =>
        s.Any(ch => ch < 32 || ch == 127) ? Printf.ShellQuote(s) : "'" + s.Replace("'", "'\\''") + "'";
}
