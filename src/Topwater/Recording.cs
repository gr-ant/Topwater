using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Topwater;

/// <summary>
/// A recorded session: the options and starting machine, then every tool call with what it returned. Replay it against a
/// fresh shell, another starting machine, another Topwater version or a real Linux box to see what changed.
/// See "Recordings" in docs/reference.md for the file format.
/// </summary>
public sealed class Recording
{
    /// <summary>The version of the file format this Topwater writes; <see cref="Load(Stream, Func{string, Stream}?, ShellOptions?)"/> refuses newer ones.</summary>
    public const int FormatVersion = 1;
    const string FormatName = "topwater-recording";

    public Recording(ShellSnapshot start, ShellOptions options)
    {
        Start = start;
        User = options.User; Hostname = options.Hostname; StartTime = options.StartTime; RandomSeed = options.RandomSeed;
        MaxLoopIterations = options.MaxLoopIterations; MaxExtractBytes = options.MaxExtractBytes; MaxExtractEntries = options.MaxExtractEntries;
        MaxCallDuration = options.MaxCallDuration; MaxFileSystemBytes = options.MaxFileSystemBytes; MaxStringBytes = options.MaxStringBytes;
    }

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The Topwater version that made the recording (informational).</summary>
    public string? TopwaterVersion { get; set; } = CurrentVersion;
    /// <summary>Free-form facts about the run: model, task, harness, outcome, ...</summary>
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);
    /// <summary>The app's own commands the shell had (not the built-ins): a replay target needs them registered too.</summary>
    public List<string> AppCommands { get; init; } = [];
    /// <summary>The machine before the first call.</summary>
    public ShellSnapshot Start { get; set; }
    public List<RecordedCall> Calls { get; init; } = [];

    // the ShellOptions that shape behaviour (not FileSystem, which the snapshot replaces)
    public string User { get; set; }
    public string Hostname { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public int? RandomSeed { get; set; }
    public int MaxLoopIterations { get; set; }
    public long MaxExtractBytes { get; set; }
    public int MaxExtractEntries { get; set; }
    public TimeSpan MaxCallDuration { get; set; }
    public long MaxFileSystemBytes { get; set; }
    public long MaxStringBytes { get; set; }

    /// <summary>True when the recording ran on a virtual clock with a random seed, so a replay on the same Topwater must match exactly.</summary>
    public bool IsDeterministic => StartTime != null && RandomSeed != null;

    /// <summary>Options for a shell that behaves like the recorded one.</summary>
    public ShellOptions CreateOptions() => new()
    {
        User = User, Hostname = Hostname, StartTime = StartTime, RandomSeed = RandomSeed, MaxLoopIterations = MaxLoopIterations,
        MaxExtractBytes = MaxExtractBytes, MaxExtractEntries = MaxExtractEntries, MaxCallDuration = MaxCallDuration,
        MaxFileSystemBytes = MaxFileSystemBytes, MaxStringBytes = MaxStringBytes,
    };

    /// <summary>A fresh shell at the recording's start: its options, then <paramref name="configure"/> (register your commands), then the start snapshot.</summary>
    public Shell CreateShell(Action<Shell>? configure = null, ShellSnapshot? start = null)
    {
        var shell = new Shell(CreateOptions());
        configure?.Invoke(shell);
        shell.Restore(start ?? Start);
        return shell;
    }

    /// <summary>
    /// The machine as it was after call <paramref name="index"/> (-1: before the first): restored from the nearest snapshot at or
    /// before it, then the calls after that run again. Exact for a deterministic recording on the same Topwater.
    /// </summary>
    public async Task<Shell> ShellAtAsync(int index, Action<Shell>? configure = null, CancellationToken cancellationToken = default)
    {
        if (index < -1 || index >= Calls.Count) throw new ArgumentOutOfRangeException(nameof(index));
        int from = index;
        while (from >= 0 && Calls[from].SnapshotAfter == null) from--;
        var shell = CreateShell(configure, from >= 0 ? Calls[from].SnapshotAfter : null);
        for (int i = from + 1; i <= index; i++) await shell.RunAsync(Calls[i].Command, cancellationToken);
        return shell;
    }

    /// <summary>Runs every call against <paramref name="target"/> and compares what it returned with the recording.</summary>
    public async Task<ReplayReport> ReplayAsync(IReplayTarget target, ReplayOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ReplayOptions();
        var results = new List<ReplayedCall>();
        int differing = 0;
        foreach (var call in Calls)
        {
            var actual = await target.RunAsync(call.Command, cancellationToken);
            var replayed = new ReplayedCall(call, actual, Compare(call, actual, options));
            results.Add(replayed);
            options.OnCall?.Invoke(replayed);
            if (replayed.Differences.Count > 0 && ++differing >= options.StopAfter) break;
        }
        var warnings = new List<string>();
        if (target is ShellTarget { Shell: var shell } && AppCommands.Where(c => !shell.Commands.ContainsKey(c)).ToList() is { Count: > 0 } missing)
            warnings.Add($"the recording used app commands the target doesn't have: {string.Join(", ", missing)}");
        return new ReplayReport(target.Name, Calls.Count, results, warnings) { Normalize = options.Normalize };
    }

    /// <summary>Replays on a fresh Topwater shell at the recording's start (see <see cref="CreateShell"/>).</summary>
    public async Task<ReplayReport> ReplayAsync(ReplayOptions? options = null, Action<Shell>? configure = null, CancellationToken cancellationToken = default)
    {
        await using var target = new ShellTarget(CreateShell(configure));
        return await ReplayAsync(target, options, cancellationToken);
    }

    static List<ReplayDifference> Compare(RecordedCall expected, ShellResult actual, ReplayOptions o)
    {
        var diffs = new List<ReplayDifference>();
        string N(string? s) => o.Normalize is { } f && s != null ? f(s) : s ?? "";
        void Check(ReplayFields field, string name, string? e, string? a)
        {
            if ((o.Fields & field) != 0 && N(e) != N(a)) diffs.Add(new(expected.Index, expected.Command, name, e ?? "", a ?? ""));
        }
        Check(ReplayFields.ExitCode, "exitCode", expected.ExitCode.ToString(CultureInfo.InvariantCulture), actual.ExitCode.ToString(CultureInfo.InvariantCulture));
        Check(ReplayFields.Stdout, "stdout", expected.Stdout, actual.Stdout);
        Check(ReplayFields.Stderr, "stderr", expected.Stderr, actual.Stderr);
        Check(ReplayFields.Trailer, "trailer", expected.Trailer, actual.Trailer);
        return diffs;
    }

    /// <summary>
    /// A copy whose calls hold what <paramref name="target"/> returns now: for when behaviour changed on purpose. The header,
    /// metadata and start are kept; a call that had a snapshot gets a fresh one when the target is a <see cref="ShellTarget"/>.
    /// </summary>
    public async Task<Recording> RerecordAsync(IReplayTarget target, CancellationToken cancellationToken = default)
    {
        var copy = new Recording(Start, CreateOptions()) { Created = DateTimeOffset.UtcNow, Metadata = new(Metadata, StringComparer.Ordinal), AppCommands = [.. AppCommands] };
        foreach (var call in Calls)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var r = await target.RunAsync(call.Command, cancellationToken);
            copy.Calls.Add(RecordedCall.From(call.Index, call.Command, r, watch.Elapsed) with
            {
                SnapshotAfter = call.SnapshotAfter != null && target is ShellTarget t ? t.Shell.Snapshot() : null,
            });
        }
        return copy;
    }

    // ---------- the file ----------

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // command output, never embedded in HTML
    };

    /// <summary>Writes the recording as JSON Lines with every snapshot embedded (base64 zip), so one file holds the whole run.</summary>
    public void Save(Stream stream) => Write(stream, null);

    /// <summary>
    /// Writes the recording to <paramref name="path"/>. With <paramref name="sidecarSnapshots"/>, snapshots go next to it as
    /// zip files in <c>&lt;name&gt;.snapshots/</c> and the JSON Lines refer to them, which keeps the .jsonl small and diffable.
    /// </summary>
    public void Save(string path, bool sidecarSnapshots = false)
    {
        string? dir = null;
        if (sidecarSnapshots)
        {
            dir = Path.GetFileNameWithoutExtension(path) + ".snapshots";
            var full = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, dir);
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            Directory.CreateDirectory(full);
            dir = full;
        }
        using var file = File.Create(path);
        Write(file, dir);
    }

    void Write(Stream stream, string? sidecarDir)
    {
        SnapshotRef Ref(ShellSnapshot s, string name)
        {
            if (sidecarDir == null)
            {
                var ms = new MemoryStream();
                s.Save(ms);
                return new SnapshotRef { Embedded = Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length) };
            }
            using (var f = File.Create(Path.Combine(sidecarDir, name + ".zip"))) s.Save(f);
            return new SnapshotRef { File = Path.GetFileName(sidecarDir) + "/" + name + ".zip" };
        }
        var header = new Header
        {
            Format = FormatName, Version = FormatVersion, Created = Created.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), Topwater = TopwaterVersion,
            Options = new OptionsEntry
            {
                User = User, Hostname = Hostname, StartTime = StartTime?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), RandomSeed = RandomSeed,
                MaxLoopIterations = MaxLoopIterations, MaxExtractBytes = MaxExtractBytes, MaxExtractEntries = MaxExtractEntries,
                MaxCallDurationMs = (long)MaxCallDuration.TotalMilliseconds, MaxFileSystemBytes = MaxFileSystemBytes, MaxStringBytes = MaxStringBytes,
            },
            AppCommands = AppCommands.Count > 0 ? AppCommands : null,
            Metadata = Metadata.Count > 0 ? Metadata : null,
            Start = Ref(Start, "start"),
        };
        using var w = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        w.WriteLine(JsonSerializer.Serialize(header, Json));
        foreach (var c in Calls)
            w.WriteLine(JsonSerializer.Serialize(new CallEntry
            {
                Index = c.Index, Command = c.Command, Stdout = c.Stdout, Stderr = c.Stderr, ExitCode = c.ExitCode, Trailer = c.Trailer,
                ElapsedMs = Math.Round(c.Elapsed.TotalMilliseconds, 3),
                Snapshot = c.SnapshotAfter is { } s ? Ref(s, $"call-{c.Index:D4}") : null,
            }, Json));
    }

    /// <summary>Reads a recording from a file; side-car snapshot references resolve next to it.</summary>
    public static Recording Load(string path, ShellOptions? limits = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        using var file = File.OpenRead(path);
        return Load(file, rel => File.OpenRead(SafeJoin(dir, rel)), limits);
    }

    static string SafeJoin(string dir, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(dir, rel));
        return full.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : throw Bad($"snapshot path {rel} leaves the recording's directory");
    }

    /// <summary>Most snapshots one recording may hold (the start included).</summary>
    public const int MaxSnapshots = 1000;
    /// <summary>The files of all a recording's snapshots together may hold at most this many times the limits' MaxFileSystemBytes.</summary>
    public const int MaxSnapshotsFileSystems = 4;

    // a side-car name is only ever a relative file name: no rooted path, drive (C:), URL (x://), backslash or .. segment
    static bool IsPlainRelativePath(string rel) =>
        rel.Length > 0 && !rel.StartsWith('/') && !rel.Contains('\\') && !rel.Contains(':') && !rel.Split('/').Contains("..");

    const string Invalid = "not a valid Topwater recording: ";
    static InvalidDataException Bad(string why) => new(Invalid + why);

    /// <summary>
    /// Reads a recording written by <see cref="Save(Stream)"/>. <paramref name="openSnapshot"/> opens a side-car snapshot by the
    /// relative path the file names; it is only ever a relative file name (no drive, URL, backslash or ..), and the opener must
    /// treat it as nothing else. Snapshots are loaded as untrusted (see <see cref="ShellSnapshot.Load"/>) under
    /// <paramref name="limits"/>, or else the default limits (never the ones the file names); the replay shell's limits are the file's
    /// clamped to the same bound, so a recording can lower limits but never raise them. Throws <see cref="InvalidDataException"/> for a malformed
    /// file and <see cref="NotSupportedException"/> for a newer format version.
    /// </summary>
    public static Recording Load(Stream stream, Func<string, Stream>? openSnapshot = null, ShellOptions? limits = null)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var first = reader.ReadLine() ?? throw Bad("the file is empty");
        Header h;
        try { h = JsonSerializer.Deserialize<Header>(first, Json) ?? throw Bad("no header"); }
        catch (JsonException e) { throw Bad("the header is not JSON: " + e.Message); }
        if (h.Format != FormatName) throw Bad("the first line is not a topwater-recording header");
        if (h.Version > FormatVersion)
            throw new NotSupportedException($"recording format version {h.Version} is newer than this version of Topwater supports ({FormatVersion}); upgrade Topwater to read it");
        if (h.Version < 1) throw Bad($"unknown format version {h.Version}");
        var o = h.Options ?? throw Bad("options are missing");
        var d = new ShellOptions();
        limits ??= d; // never the file's own limits: a hostile header could raise them
        // the file's limits can lower the caller's (or the default) limits, never raise them
        static long Cap(long? file, long max) => Math.Clamp(file ?? max, 0, max);
        var options = new ShellOptions
        {
            User = o.User ?? d.User, Hostname = o.Hostname ?? d.Hostname,
            StartTime = o.StartTime is { } st ? DateTimeOffset.Parse(st, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null,
            RandomSeed = o.RandomSeed,
            MaxLoopIterations = (int)Cap(o.MaxLoopIterations, limits.MaxLoopIterations), MaxExtractBytes = Cap(o.MaxExtractBytes, limits.MaxExtractBytes),
            MaxExtractEntries = (int)Cap(o.MaxExtractEntries, limits.MaxExtractEntries),
            MaxCallDuration = TimeSpan.FromMilliseconds(Cap(o.MaxCallDurationMs, (long)limits.MaxCallDuration.TotalMilliseconds)),
            MaxFileSystemBytes = Cap(o.MaxFileSystemBytes, limits.MaxFileSystemBytes), MaxStringBytes = Cap(o.MaxStringBytes, limits.MaxStringBytes),
        };
        // each snapshot is capped by limits; these cap them all together
        int snapshots = 0;
        long snapshotBytes = 0, maxSnapshotBytes = MaxSnapshotsFileSystems * limits.MaxFileSystemBytes;
        ShellSnapshot Snap(SnapshotRef? r, string what)
        {
            if (++snapshots > MaxSnapshots) throw Bad($"more than {MaxSnapshots} snapshots");
            var snap = Read();
            if ((snapshotBytes += snap.Fs.UsedBytes) > maxSnapshotBytes)
                throw Bad($"the snapshots hold more than {maxSnapshotBytes} bytes of files together ({MaxSnapshotsFileSystems} x MaxFileSystemBytes)");
            return snap;
            ShellSnapshot Read()
            {
                if (r?.Embedded is { } b64)
                {
                    try { return ShellSnapshot.Load(new MemoryStream(Convert.FromBase64String(b64)), limits); }
                    catch (FormatException) { throw Bad($"{what}: the embedded snapshot is not base64"); }
                }
                if (r?.File is { } rel)
                {
                    if (openSnapshot == null) throw Bad($"{what} is in a side-car file ({rel}); load the recording from its path");
                    if (!IsPlainRelativePath(rel)) throw Bad($"snapshot path {rel} is not a plain relative path inside the recording's directory");
                    using var s = openSnapshot(rel);
                    return ShellSnapshot.Load(s, limits);
                }
                throw Bad($"{what} has no snapshot");
            }
        }
        var rec = new Recording(Snap(h.Start, "the start"), options)
        {
            Created = h.Created is { } c ? DateTimeOffset.Parse(c, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : default,
            TopwaterVersion = h.Topwater,
            Metadata = h.Metadata ?? new(StringComparer.Ordinal),
            AppCommands = h.AppCommands ?? [],
        };
        int lineNo = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNo++;
            if (line.Length == 0) continue;
            CallEntry e;
            try { e = JsonSerializer.Deserialize<CallEntry>(line, Json) ?? throw Bad($"line {lineNo} is null"); }
            catch (JsonException ex) { throw Bad($"line {lineNo} is not JSON: {ex.Message}"); }
            if (e.Command == null) throw Bad($"line {lineNo} has no command");
            rec.Calls.Add(new RecordedCall(e.Index, e.Command, e.Stdout ?? "", e.Stderr ?? "", e.ExitCode, e.Trailer, TimeSpan.FromMilliseconds(e.ElapsedMs))
            {
                SnapshotAfter = e.Snapshot == null ? null : Snap(e.Snapshot, $"call {e.Index}"),
            });
        }
        return rec;
    }

    static string? CurrentVersion => typeof(Shell).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    sealed class Header
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Created { get; set; }
        public string? Topwater { get; set; }
        public OptionsEntry? Options { get; set; }
        public List<string>? AppCommands { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        public SnapshotRef? Start { get; set; }
    }

    sealed class OptionsEntry
    {
        public string? User { get; set; }
        public string? Hostname { get; set; }
        public string? StartTime { get; set; }
        public int? RandomSeed { get; set; }
        public int? MaxLoopIterations { get; set; }
        public long? MaxExtractBytes { get; set; }
        public int? MaxExtractEntries { get; set; }
        public long? MaxCallDurationMs { get; set; }
        public long? MaxFileSystemBytes { get; set; }
        public long? MaxStringBytes { get; set; }
    }

    sealed class SnapshotRef
    {
        public string? Embedded { get; set; }
        public string? File { get; set; }
    }

    sealed class CallEntry
    {
        public int Index { get; set; }
        public string? Command { get; set; }
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
        public int ExitCode { get; set; }
        public string? Trailer { get; set; }
        public double ElapsedMs { get; set; }
        public SnapshotRef? Snapshot { get; set; }
    }
}

/// <summary>One tool call: the command and what the shell returned. <see cref="Elapsed"/> is wall time and never compared.</summary>
public sealed record RecordedCall(int Index, string Command, string Stdout, string Stderr, int ExitCode, string? Trailer, TimeSpan Elapsed)
{
    /// <summary>The machine right after this call, when the recorder took a snapshot here.</summary>
    public ShellSnapshot? SnapshotAfter { get; init; }

    public ShellResult Result => new(Stdout, Stderr, ExitCode) { Trailer = Trailer };

    public static RecordedCall From(int index, string command, ShellResult r, TimeSpan elapsed) =>
        new(index, command, r.Stdout, r.Stderr, r.ExitCode, r.Trailer, elapsed);
}

public sealed class RecordingOptions
{
    /// <summary>Free-form facts to store in the header: model, task, ...</summary>
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Take a snapshot after every Nth call (0: never), so a viewer can jump to a turn without re-running from the start.</summary>
    public int SnapshotEvery { get; init; }
}

/// <summary>Records every <see cref="Shell.RunAsync"/> call (so also <see cref="BashTool.ExecuteAsync"/> and AsAIFunction) until stopped.</summary>
public sealed class ShellRecorder : IDisposable
{
    readonly Shell _shell;
    readonly int _every;

    internal ShellRecorder(Shell shell, RecordingOptions options)
    {
        _shell = shell;
        _every = options.SnapshotEvery;
        Recording = new Recording(shell.Snapshot(), shell.Options)
        {
            Metadata = new(options.Metadata, StringComparer.Ordinal),
            AppCommands = [.. shell.AppCommandNames()],
        };
    }

    /// <summary>The recording so far; it keeps growing until <see cref="Stop"/>.</summary>
    public Recording Recording { get; }

    internal void Add(string command, ShellResult result, TimeSpan elapsed)
    {
        int index = Recording.Calls.Count;
        Recording.Calls.Add(RecordedCall.From(index, command, result, elapsed) with
        {
            SnapshotAfter = _every > 0 && (index + 1) % _every == 0 ? _shell.Snapshot() : null,
        });
    }

    /// <summary>Snapshots the machine now and attaches it to the last call (a mark a viewer can jump to).</summary>
    public void SnapshotNow()
    {
        if (Recording.Calls.Count == 0) { Recording.Start = _shell.Snapshot(); return; }
        Recording.Calls[^1] = Recording.Calls[^1] with { SnapshotAfter = _shell.Snapshot() };
    }

    /// <summary>Stops recording; later calls are not added.</summary>
    public void Stop() { if (_shell.Recorder == this) _shell.Recorder = null; }
    public void Dispose() => Stop();

    public void Save(Stream stream) => Recording.Save(stream);
    public void Save(string path, bool sidecarSnapshots = false) => Recording.Save(path, sidecarSnapshots);
}

public sealed partial class Shell
{
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
}

/// <summary>Something a recording can be replayed against: a Topwater shell (<see cref="ShellTarget"/>), a real container, ...</summary>
public interface IReplayTarget : IAsyncDisposable
{
    /// <summary>A short name for reports, e.g. "topwater 0.1.0" or "docker ubuntu:24.04".</summary>
    string Name { get; }
    /// <summary>Runs one tool call; state (files, cwd, variables) carries over to the next.</summary>
    Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default);
}

/// <summary>Replays on a Topwater shell (this version). Build one with <see cref="Recording.CreateShell"/> for the recording's start, or from any shell.</summary>
public sealed class ShellTarget(Shell shell, string? name = null) : IReplayTarget
{
    public Shell Shell { get; } = shell;
    public string Name { get; } = name ?? "topwater " + typeof(Shell).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
    public Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default) => Shell.RunAsync(command, cancellationToken);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[Flags]
public enum ReplayFields { None = 0, Stdout = 1, Stderr = 2, ExitCode = 4, Trailer = 8, All = Stdout | Stderr | ExitCode | Trailer }

public sealed class ReplayOptions
{
    /// <summary>What to compare; all four by default.</summary>
    public ReplayFields Fields { get; init; } = ReplayFields.All;
    /// <summary>Applied to both sides' stdout, stderr and trailer before comparing (null: exact). See <see cref="ReplayNormalizers"/>.</summary>
    public Func<string, string>? Normalize { get; init; }
    /// <summary>Stop after this many differing calls (the rest of the recording isn't run).</summary>
    public int StopAfter { get; init; } = int.MaxValue;
    /// <summary>Called after each call is replayed, for progress output.</summary>
    public Action<ReplayedCall>? OnCall { get; init; }
}

/// <summary>Normalizers for <see cref="ReplayOptions.Normalize"/>: things that differ between runs without meaning anything.</summary>
public static class ReplayNormalizers
{
    static readonly (System.Text.RegularExpressions.Regex Pattern, string Replacement)[] VolatileRules =
    [
        (new(@"\b\d{4}-\d\d-\d\d[T ]\d\d:\d\d(:\d\d(\.\d+)?)?(Z|\s?[+-]\d\d:?\d\d)?"), "<time>"),
        (new(@"\b(Mon|Tue|Wed|Thu|Fri|Sat|Sun),? +(\d\d? +)?(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)( +\d\d?)?( +\d{4})? +\d\d:\d\d:\d\d( +[A-Z]{3,4}| +[+-]\d{4})?( +\d{4})?"), "<date>"),
        (new(@"\b(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) +\d\d? +(\d\d:\d\d|\d{4})\b"), "<mtime>"),
        (new(@"\b1[5-9]\d{8}(\.\d+)?\b"), "<epoch>"),
        (new(@"/tmp/tmp\.[A-Za-z0-9]{10}\b"), "/tmp/tmp.<random>"),
        (new(@"(real|user|sys)\t\d+m\d+\.\d+s"), "$1\t<duration>"),
        (new(@"\bup +\d+ (days?|min)[^,\n]*,( +\d+:\d\d,)?"), "up <uptime>,"),
    ];

    /// <summary>Times and dates (ISO, `date`, `ls -l`), epoch seconds, mktemp names, `time` durations and uptimes become placeholders.</summary>
    public static string Volatile(string text)
    {
        foreach (var (pattern, replacement) in VolatileRules) text = pattern.Replace(text, replacement);
        return text;
    }

    /// <summary>Line endings and trailing whitespace on each line, and trailing blank lines.</summary>
    public static string Whitespace(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).TrimEnd('\n');

    /// <summary>Applies each normalizer in turn.</summary>
    public static Func<string, string> Chain(params Func<string, string>[] normalizers) => s => normalizers.Aggregate(s, (t, f) => f(t));
}

public sealed record ReplayDifference(int Index, string Command, string Field, string Expected, string Actual);

public sealed record ReplayedCall(RecordedCall Expected, ShellResult Actual, IReadOnlyList<ReplayDifference> Differences);

/// <summary>The outcome of a replay: per call, what came back and how it differed from the recording.</summary>
public sealed class ReplayReport(string target, int recordedCalls, IReadOnlyList<ReplayedCall> calls, IReadOnlyList<string> warnings)
{
    public string Target { get; } = target;
    public int RecordedCalls { get; } = recordedCalls;
    /// <summary>The calls that ran (fewer than recorded if <see cref="ReplayOptions.StopAfter"/> cut it short).</summary>
    public IReadOnlyList<ReplayedCall> Calls { get; } = calls;
    public IReadOnlyList<string> Warnings { get; } = warnings;
    public IEnumerable<ReplayDifference> Differences => Calls.SelectMany(c => c.Differences);
    public bool Matches => Calls.Count == RecordedCalls && Calls.All(c => c.Differences.Count == 0);
    /// <summary>The normalizer the comparison used, so the summary points at a line that really differs.</summary>
    public Func<string, string>? Normalize { get; init; }

    /// <summary>A readable summary: one block per differing call, showing the first line that differs in each field.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        int bad = Calls.Count(c => c.Differences.Count > 0);
        sb.Append(CultureInfo.InvariantCulture, $"replay on {Target}: {Calls.Count - bad}/{RecordedCalls} calls match");
        if (Calls.Count < RecordedCalls) sb.Append(CultureInfo.InvariantCulture, $" (stopped after {Calls.Count})");
        sb.Append('\n');
        foreach (var w in Warnings) sb.Append("warning: ").Append(w).Append('\n');
        foreach (var c in Calls.Where(c => c.Differences.Count > 0))
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n#{c.Expected.Index} $ {Clip(c.Expected.Command.Replace("\n", "\\n"), 200)}\n");
            foreach (var d in c.Differences) sb.Append(Describe(d));
        }
        return sb.ToString();
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    string Describe(ReplayDifference d)
    {
        if (d.Field == "exitCode") return $"  exit code: expected {d.Expected}, got {d.Actual}\n";
        var e = d.Expected.Split('\n');
        var a = d.Actual.Split('\n');
        int i = 0;
        while (i < e.Length && i < a.Length && (e[i] == a[i] || Normalize is { } n && n(e[i]) == n(a[i]))) i++;
        string Line(string[] lines) => i < lines.Length ? JsonSerializer.Serialize(Clip(lines[i], 300)) : "(end)";
        return $"  {d.Field} line {i + 1}: expected {Line(e)}\n  {new string(' ', d.Field.Length)} {new string(' ', (i + 1).ToString(CultureInfo.InvariantCulture).Length + 5)}   got {Line(a)}\n";
    }
}
