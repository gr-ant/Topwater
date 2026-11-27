// Shell: snapshot, restore, fork, save, load and recording. See docs/architecture.md.

namespace Topwater;

public sealed partial class Shell
{
    /// <summary>Captures the machine (files and session) as an immutable <see cref="ShellSnapshot"/>. Cheap: file contents
    /// and arrays are shared, not copied. Call it between calls, not from inside a running command.</summary>
    public ShellSnapshot Snapshot() => new(Fs.Clone(), CaptureState(), Options.User, Options.Hostname, Now);

    /// <summary>Puts the machine back as <paramref name="snapshot"/> captured it. This shell keeps its own registered commands
    /// (and the /usr/bin entries that list them); the snapshot is unchanged and can be restored again.</summary>
    public void Restore(ShellSnapshot snapshot)
    {
        Fs.ReplaceWith(snapshot.Fs);
        ApplyState(snapshot.State);
        SyncProgramFiles();
        Hashed.Clear(); // the restored machine's programs may be elsewhere
    }

    /// <summary>
    /// A new shell with a copy of this machine that shares nothing mutable with it. It shares the registered command handlers
    /// (a stateful one, such as an MCP mount's connection, is then used by both), except host programs
    /// (<see cref="HostCommandExtensions.RegisterHostCommand"/>), which are bound to this shell's files: register them again on the fork.
    /// </summary>
    public Shell Fork()
    {
        var fork = new Shell(Options, Fs.Clone());
        foreach (var (name, command) in _commands)
            if (command.Handler.Target is not HostCommand) fork._commands[name] = command;
        fork._builtinNames = _builtinNames == null ? null : new(_builtinNames);
        fork.ApplyState(CaptureState());
        fork.SyncProgramFiles();
        return fork;
    }

    /// <summary>Saves the machine; same as <c>Snapshot().Save(stream)</c>.</summary>
    public void Save(Stream stream) => Snapshot().Save(stream);

    /// <summary>
    /// A new shell from a saved snapshot (see <see cref="ShellSnapshot.Load(Stream, ShellOptions)"/> for the checks). It has the built-ins but none of
    /// your registered commands: register them again, or configure a shell first and call
    /// <c>shell.Restore(ShellSnapshot.Load(stream, shell.Options))</c>. Without <paramref name="options"/>, the snapshot's user and
    /// host name are used with the default limits.
    /// </summary>
    public static Shell Load(Stream stream, ShellOptions? options = null)
    {
        var snapshot = ShellSnapshot.Load(stream, options);
        var shell = new Shell(options ?? new ShellOptions { User = snapshot.User, Hostname = snapshot.Hostname });
        shell.Restore(snapshot);
        return shell;
    }

    /// <summary>
    /// Starts recording this shell's calls: the machine now becomes the recording's start and every <see cref="RunAsync"/> call
    /// until <see cref="ShellRecorder.Stop"/> is added. For a recording that replays exactly, create the shell with
    /// <see cref="ShellOptions.StartTime"/> and <see cref="ShellOptions.RandomSeed"/>. Stop it before your own checks, which also go through RunAsync.
    /// </summary>
    public ShellRecorder StartRecording(RecordingOptions? options = null)
    {
        if (Recorder != null) throw new InvalidOperationException("this shell is already recording; stop that recording first");
        return Recorder = new ShellRecorder(this, options ?? new RecordingOptions());
    }

    /// <summary>The registered commands that aren't built-ins: what a replay target must register again.</summary>
    internal IEnumerable<string> AppCommandNames() => _commands.Keys.Where(k => _builtinNames?.Contains(k) != true).Order(StringComparer.Ordinal);
    ShellState CaptureState()
    {
        _epoch = NextEpoch(); // the arrays are now shared with the snapshot: copy each before its next write
        return new()
        {
            Cwd = _cwd, Variables = new(Environment, StringComparer.Ordinal), Attributes = new(VarAttrs, StringComparer.Ordinal),
            Unexported = new(_unexported, StringComparer.Ordinal), Arrays = new(Arrays, StringComparer.Ordinal),
            Functions = new(Functions, StringComparer.Ordinal), ExportedFunctions = new(ExportedFunctions, StringComparer.Ordinal),
            Aliases = new(Aliases, StringComparer.Ordinal), Traps = new(Traps, StringComparer.Ordinal),
            SetOptions = [.. OptionList.Where(o => o.On && SnapshotSetOptions.Contains(o.Name)).Select(o => o.Name)], IfsUnset = _ifsUnset, Shopts = [.. Shopts],
            Positional = [.. Positional], DirStack = [.. DirStack], History = [.. _history], LastExitCode = LastExitCode,
            SecondsBase = _secondsBase, LastArg = _lastArg, PipeStatus = [.. _pipeStatus], Getopts = GetoptsState, Jobs = new(_jobs),
            LastBackgroundPid = _lastBg, NextPid = _nextPid, Clock = _virtualNow, Random = _random,
        };
    }

    /// <summary>The set -o options a shell can change (the others in <see cref="OptionList"/> are fixed).</summary>
    internal static readonly HashSet<string> SnapshotSetOptions = ["errexit", "pipefail", "nounset", "noglob", "xtrace", "noclobber"];

    /// <summary>Copies <paramref name="s"/> in, so the snapshot it belongs to stays as it was.</summary>
    void ApplyState(ShellState s)
    {
        _epoch = NextEpoch();
        Environment = new(s.Variables, StringComparer.Ordinal);
        VarAttrs = new(s.Attributes, StringComparer.Ordinal);
        _unexported.Clear(); _unexported.UnionWith(s.Unexported);
        Arrays = new(s.Arrays, StringComparer.Ordinal);
        Functions = new(s.Functions, StringComparer.Ordinal);
        ExportedFunctions.Clear(); ExportedFunctions.UnionWith(s.ExportedFunctions);
        Aliases = new(s.Aliases, StringComparer.Ordinal);
        Traps = new(s.Traps, StringComparer.Ordinal);
        (Errexit, Pipefail, Nounset, Noglob, Xtrace, Noclobber) = (On("errexit"), On("pipefail"), On("nounset"), On("noglob"), On("xtrace"), On("noclobber"));
        bool On(string name) => s.SetOptions.Contains(name);
        _ifsUnset = s.IfsUnset;
        Shopts = [.. s.Shopts];
        Positional = [.. s.Positional];
        DirStack.Clear(); DirStack.AddRange(s.DirStack);
        _history.Clear(); _history.AddRange(s.History);
        LastExitCode = s.LastExitCode;
        _secondsBase = s.SecondsBase;
        _lastArg = s.LastArg;
        _pipeStatus = [.. s.PipeStatus];
        GetoptsState = s.Getopts;
        _jobs.Clear(); foreach (var (pid, code) in s.Jobs) _jobs[pid] = code;
        _lastBg = s.LastBackgroundPid;
        _nextPid = s.NextPid;
        // a deterministic shell picks up the time and random state where the snapshot left them; another keeps its own
        if (_virtualNow != null && s.Clock is { } clock) _virtualNow = clock;
        if (_random != null && s.Random is { } random) _random = random;
        _variableChars = Environment.Sum(e => (long)e.Key.Length + e.Value.Length) + Arrays.Values.Sum(a => a.Items.Sum(e => (long)e.Key.Length + e.Value.Length));
        _cwd = Fs.IsDirectory(s.Cwd) ? s.Cwd : "/";
    }
}
