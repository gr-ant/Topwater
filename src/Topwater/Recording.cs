using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Topwater;

/// <summary>
/// A recorded session: the options and starting machine, then every tool call with what it returned. Replay it against a
/// fresh shell, another starting machine, another Topwater version or a real Linux box to see what changed.
/// See docs/snapshots-and-recordings.md for the file format.
/// </summary>
public sealed class Recording
{
    /// <summary>The version of history.jsonl this Topwater writes; <see cref="Load(Stream, Func{string, Stream}?, ShellOptions?)"/> refuses newer ones.</summary>
    public const int FormatVersion = 1;
    const string HistoryFormat = "topwater-history";
    const string LegacyFormat = "topwater-recording"; // the JSON Lines recordings before .topwater files

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
    /// <summary>The web access the shell had (<see cref="Shell.AllowWeb"/>), without its credentials or handler; null: none.
    /// A replay serves the recorded responses under it and never touches the network.</summary>
    public WebPolicy? Web { get; set; }

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
    public Shell CreateShell(Action<Shell>? configure = null, ShellSnapshot? start = null) => CreateShell(configure, start, 0);

    /// <param name="firstCall">The call the shell runs first, so its web requests get that call's recorded responses.</param>
    public Shell CreateShell(Action<Shell>? configure, ShellSnapshot? start, int firstCall)
    {
        var shell = new Shell(CreateOptions());
        configure?.Invoke(shell);
        // always after configure, which may have allowed the web: a replay serves what was recorded and never goes live
        var w = Web ?? WebSession.NoNetwork;
        shell.AllowWeb(new WebSession(new WebPolicy
        {
            Domains = w.Domains, Methods = w.Methods, AllowHttp = w.AllowHttp, MaxResponseBytes = w.MaxResponseBytes, MaxRequestsPerCall = w.MaxRequestsPerCall,
            MaxRequests = w.MaxRequests, Timeout = w.Timeout, MaxRedirects = w.MaxRedirects, ExplainBlocked = w.ExplainBlocked,
            Credentials = w.Credentials.Keys.ToDictionary(h => h, _ => (Func<string>)(() => ""), StringComparer.OrdinalIgnoreCase),
        }, [.. Calls.Skip(firstCall).SelectMany(c => c.Web ?? [])]));
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
        var shell = CreateShell(configure, from >= 0 ? Calls[from].SnapshotAfter : null, from + 1);
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
    /// metadata and start are kept; on a <see cref="ShellTarget"/> a call that had a snapshot (and the last) gets a fresh one, and
    /// each call its time and changed files.
    /// </summary>
    public async Task<Recording> RerecordAsync(IReplayTarget target, CancellationToken cancellationToken = default)
    {
        var copy = new Recording(Start, CreateOptions()) { Created = DateTimeOffset.UtcNow, Metadata = new(Metadata, StringComparer.Ordinal), AppCommands = [.. AppCommands], Web = Web };
        var shell = (target as ShellTarget)?.Shell;
        var before = shell?.Snapshot();
        for (int i = 0; i < Calls.Count; i++)
        {
            var call = Calls[i];
            DateTimeOffset? started = shell?.NowUtc;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var r = await target.RunAsync(call.Command, cancellationToken);
            var after = shell?.Snapshot();
            copy.Calls.Add(RecordedCall.From(call.Index, call.Command, r, watch.Elapsed) with
            {
                Time = started, Files = after == null ? null : ShellSnapshot.ChangedFiles(before!, after), Web = shell?.Web?.TakeLog() is { Count: > 0 } w ? w : null,
                SnapshotAfter = call.SnapshotAfter != null || i == Calls.Count - 1 ? after : null,
            });
            before = after;
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

    /// <summary>
    /// Writes the recording as a .topwater file (spec/topwater-file): manifest.json is the machine after the last call,
    /// history.jsonl the calls, checkpoints/&lt;n&gt;.json the start (0) and the machine after each call that has a snapshot
    /// (n = calls run), all sharing one set of blobs.
    /// </summary>
    public void Save(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var written = new HashSet<string>(StringComparer.Ordinal);
        (Calls.LastOrDefault(c => c.SnapshotAfter != null)?.SnapshotAfter ?? Start).WriteTo(zip, "manifest.json", written);
        var header = new Header
        {
            Format = HistoryFormat, Version = FormatVersion, Created = Iso(Created), Topwater = TopwaterVersion,
            Options = new OptionsEntry
            {
                User = User, Hostname = Hostname, StartTime = StartTime is { } st ? Iso(st) : null, RandomSeed = RandomSeed,
                MaxLoopIterations = MaxLoopIterations, MaxExtractBytes = MaxExtractBytes, MaxExtractEntries = MaxExtractEntries,
                MaxCallDurationMs = MaxCallDuration == Timeout.InfiniteTimeSpan ? -1 : (long)MaxCallDuration.TotalMilliseconds,
                MaxFileSystemBytes = MaxFileSystemBytes, MaxStringBytes = MaxStringBytes,
            },
            AppCommands = AppCommands.Count > 0 ? AppCommands : null,
            Metadata = Metadata.Count > 0 ? Metadata : null,
            Web = Web is { } p ? new WebEntry
            {
                Domains = [.. p.Domains], Methods = [.. p.Methods], AllowHttp = p.AllowHttp, MaxResponseBytes = p.MaxResponseBytes,
                MaxRequestsPerCall = p.MaxRequestsPerCall, MaxRequests = p.MaxRequests, TimeoutMs = (long)p.Timeout.TotalMilliseconds,
                MaxRedirects = p.MaxRedirects, ExplainBlocked = p.ExplainBlocked, Credentials = [.. p.Credentials.Keys.Order(StringComparer.Ordinal)],
            } : null,
        };
        // the response bodies' blobs first: a zip writes one entry at a time
        var lines = Calls.Select(c => JsonSerializer.Serialize(new CallEntry
        {
            Index = c.Index, Command = c.Command, Stdout = c.Stdout, Stderr = c.Stderr, ExitCode = c.ExitCode, Trailer = c.Trailer,
            ElapsedMs = Math.Round(c.Elapsed.TotalMilliseconds, 3), Time = c.Time is { } t ? Iso(t) : null,
            Files = c.Files is { Count: > 0 } f ? f : null,
            Web = c.Web is { Count: > 0 } web ? [.. web.Select(x => ExchangeEntry.From(x, zip, written))] : null,
        }, Json)).ToList();
        using (var w = new StreamWriter(zip.CreateEntry("history.jsonl", CompressionLevel.Fastest).Open(), new UTF8Encoding(false)) { NewLine = "\n" })
        {
            w.WriteLine(JsonSerializer.Serialize(header, Json));
            foreach (var line in lines) w.WriteLine(line);
        }
        Start.WriteTo(zip, "checkpoints/0.json", written);
        for (int i = 0; i < Calls.Count; i++) Calls[i].SnapshotAfter?.WriteTo(zip, $"checkpoints/{i + 1}.json", written);
    }

    static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Writes the recording to <paramref name="path"/> as a .topwater file (see <see cref="Save(Stream)"/>).</summary>
    public void Save(string path)
    {
        // through a temporary file, so a failed save never leaves a half-written file where the old one was
        var temp = path + ".tmp";
        try
        {
            using (var file = File.Create(temp)) Save(file);
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    /// <summary>Reads a .topwater file, or an older JSON Lines recording, whose side-car snapshots resolve next to it.</summary>
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

    /// <summary>Most snapshots (checkpoints) one recording may hold, the start included.</summary>
    public const int MaxSnapshots = 1000;
    /// <summary>The files of all a recording's snapshots together may hold at most this many times the limits' MaxFileSystemBytes.</summary>
    public const int MaxSnapshotsFileSystems = 4;

    // an old recording's side-car name: plain segments of safe characters, nothing a resolver could read as a root, drive,
    // URL, query, escape or parent
    static readonly System.Text.RegularExpressions.Regex SideCarName = new(@"^(?!\.+(/|\z))[A-Za-z0-9._-]+(/(?!\.+(/|\z))[A-Za-z0-9._-]+)*\z");

    const string Invalid = "not a valid Topwater recording: ";
    static InvalidDataException Bad(string why) => new(Invalid + why);

    /// <summary>
    /// Reads a .topwater file written by <see cref="Save(Stream)"/> (one without history, such as a snapshot, gives a recording
    /// with no calls), or an older JSON Lines recording. <paramref name="openSnapshot"/> opens such a recording's side-car
    /// snapshot by the relative name it gives, only ever plain segments of letters, digits, '.', '_' and '-'; the opener must treat
    /// it as nothing else. Snapshots are loaded as untrusted (see <see cref="ShellSnapshot.Load(Stream, ShellOptions)"/>) under <paramref name="limits"/>,
    /// or else the default limits (never the ones the file names); the replay shell's limits are the file's clamped to the same
    /// bound, so a recording can lower limits but never raise them. Throws <see cref="InvalidDataException"/> for a malformed
    /// file and <see cref="NotSupportedException"/> for a newer format version.
    /// </summary>
    public static Recording Load(Stream stream, Func<string, Stream>? openSnapshot = null, ShellOptions? limits = null)
    {
        limits ??= new ShellOptions();
        long maxFile = MaxSnapshotsFileSystems * ShellSnapshot.MaxArchiveBytes(limits);
        if (!stream.CanSeek)
        {
            var copy = new MemoryStream();
            try { ShellSnapshot.ReadCapped(stream, copy, maxFile, "the file"); }
            catch (InvalidDataException) { throw Bad($"the file is larger than {maxFile} bytes"); }
            copy.Position = 0;
            stream = copy;
        }
        long at = stream.Position;
        bool zip = stream.ReadByte() == 'P' && stream.ReadByte() == 'K';
        stream.Position = at;
        return zip ? ShellSnapshot.Checked(() => LoadTopwater(stream, limits, maxFile), Invalid) : LoadJsonLines(stream, openSnapshot, limits);
    }

    static Recording LoadTopwater(Stream stream, ShellOptions limits, long maxFile)
    {
        long start = stream.Position;
        using var zip = ShellSnapshot.OpenZip(stream, limits, maxFile);
        if (zip.GetEntry("history.jsonl") is not { } history)
        {
            var machine = ShellSnapshot.ReadManifest(zip, "manifest.json", limits);
            return new Recording(machine, new ShellOptions
            {
                User = machine.User, Hostname = machine.Hostname, MaxLoopIterations = limits.MaxLoopIterations, MaxExtractBytes = limits.MaxExtractBytes,
                MaxExtractEntries = limits.MaxExtractEntries, MaxCallDuration = limits.MaxCallDuration, MaxFileSystemBytes = limits.MaxFileSystemBytes,
                MaxStringBytes = limits.MaxStringBytes,
            });
        }
        // read as it inflates, never whole: deflate shrinks a run of newlines a thousandfold
        using var reader = new StreamReader(new CappedStream(history.Open(), MaxHistoryBytes(limits)), new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false);
        var h = ReadHeader(reader, HistoryFormat, FormatVersion);

        // the checkpoints, checked by name before any is read
        var checkpoints = new SortedDictionary<int, string>();
        foreach (var e in zip.Entries)
        {
            if (!e.FullName.StartsWith("checkpoints/", StringComparison.Ordinal)) continue;
            if (CheckpointName.Match(e.FullName) is not { Success: true } m) throw Bad($"unexpected entry {JsonSerializer.Serialize(e.FullName)}");
            if (checkpoints.Count >= MaxSnapshots) throw Bad($"more than {MaxSnapshots} checkpoints");
            if (!checkpoints.TryAdd(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), e.FullName)) throw Bad($"{e.FullName} appears twice");
        }
        if (!checkpoints.ContainsKey(0)) throw Bad("checkpoints/0.json (the start) is missing");
        var budget = new SnapshotBudget(limits);
        var rec = NewRecording(h, budget.Add(ShellSnapshot.ReadManifest(zip, checkpoints[0], limits, budget.Files)), limits);
        long webBytes = 0;
        ReadCalls(reader, rec, (_, _) => null, limits, (blob, size, where) =>
        {
            // every response body together within one machine's worth of files
            if ((webBytes += size) > limits.MaxFileSystemBytes) throw Bad($"{where}: the recorded responses hold more than {limits.MaxFileSystemBytes} bytes (MaxFileSystemBytes)");
            if (size == 0) return [];
            if (!ShellSnapshot.Blob.IsMatch(blob ?? "")) throw Bad($"{where}: bad blob {JsonSerializer.Serialize(blob)}");
            var entry = zip.GetEntry("blobs/" + blob) ?? throw Bad($"{where}: blob {blob} is missing");
            var raw = new MemoryStream();
            using (var st = entry.Open()) ShellSnapshot.ReadCapped(st, raw, size, "blob " + blob);
            var b = raw.ToArray();
            if (b.Length != size || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant() != blob)
                throw Bad($"{where}: blob {blob} doesn't hold the {size} bytes it should");
            return b;
        });

        // the machine of the file is the one after the last checkpoint: a reader of either must see the same
        var (lastN, lastName) = checkpoints.Last();
        if (lastN > rec.Calls.Count) throw Bad($"{lastName} is past the last call ({rec.Calls.Count})");
        var last = ShellSnapshot.ReadManifestBytes(zip, lastName, limits);
        if (!SameJson(ShellSnapshot.ReadManifestBytes(zip, "manifest.json", limits), last)) throw Bad($"manifest.json is not the machine of {lastName}, the last checkpoint");

        // the other checkpoints are checked against the limits now and built when a replay needs one; the file stays in memory
        // for that, compressed, instead of every machine in it built
        if (checkpoints.Count == 1) return rec;
        stream.Position = start;
        var file = new MemoryStream();
        stream.CopyTo(file);
        var kept = new ShellSnapshot.Kept(4);
        ZipArchive? reread = null;
        foreach (var (n, name) in checkpoints.Skip(1))
        {
            var snap = ShellSnapshot.ReadOutline(n == lastN ? last : ShellSnapshot.ReadManifestBytes(zip, name, limits, check: false), name, limits, budget.Files, budget.Bytes, () => ShellSnapshot.Checked(() =>
            {
                reread ??= new ZipArchive(new MemoryStream(file.GetBuffer(), 0, (int)file.Length, writable: false), ZipArchiveMode.Read);
                return ShellSnapshot.ReadManifest(reread, name, limits);
            }, Invalid), kept);
            budget.Count();
            rec.Calls[n - 1] = rec.Calls[n - 1] with { SnapshotAfter = snap };
        }
        return rec;
    }

    static bool SameJson(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b) || JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));

    /// <summary>The most bytes history.jsonl may inflate to: twice what the session's variables may hold.</summary>
    internal static long MaxHistoryBytes(ShellOptions o) => 2 * o.MaxStringBytes;

    /// <summary>Reads at most a set number of bytes from a stream, failing past it whatever the source claims.</summary>
    sealed class CappedStream(Stream inner, long max) : Stream
    {
        long _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, (int)Math.Min(count, max + 1 - _read));
            if ((_read += n) > max) throw Bad($"history.jsonl is larger than {max} bytes (2 x MaxStringBytes)");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    static readonly System.Text.RegularExpressions.Regex CheckpointName = new(@"^checkpoints/(0|[1-9][0-9]{0,8})\.json\z");

    static Recording LoadJsonLines(Stream stream, Func<string, Stream>? openSnapshot, ShellOptions limits)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var h = ReadHeader(reader, LegacyFormat, 1);
        var budget = new SnapshotBudget(limits);
        ShellSnapshot Snap(SnapshotRef? r, string what)
        {
            if (r?.Embedded is { } b64)
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(b64); }
                catch (FormatException) { throw Bad($"{what}: the embedded snapshot is not base64"); }
                return budget.Add(ShellSnapshot.Load(new MemoryStream(bytes), limits, budget.Files));
            }
            if (r?.File is { } rel)
            {
                if (!SideCarName.IsMatch(rel)) throw Bad($"snapshot path {JsonSerializer.Serialize(rel)} is not a plain relative name");
                if (openSnapshot == null) throw Bad($"{what} is in a side-car file ({rel}); load the recording from its path");
                budget.Count();
                using var s = openSnapshot(rel);
                return budget.Add(ShellSnapshot.Load(s, limits, budget.Files), counted: true);
            }
            throw Bad($"{what} has no snapshot");
        }
        var rec = NewRecording(h, Snap(h.Start, "the start"), limits);
        ReadCalls(reader, rec, Snap, limits);
        return rec;
    }

    /// <summary>Counts snapshots and their files' bytes: each snapshot is capped by the limits, this caps them all together.</summary>
    sealed class SnapshotBudget(ShellOptions limits)
    {
        int _count;
        long _bytes, _files;
        readonly long _maxBytes = MaxSnapshotsFileSystems * limits.MaxFileSystemBytes;
        readonly long _maxFiles = (long)MaxSnapshotsFileSystems * limits.MaxExtractEntries;

        /// <summary>Charges a snapshot's entries, from its manifest's list, before any is built: directories and empty files cost no bytes.</summary>
        public void Files(int n)
        {
            if ((_files += n) > _maxFiles)
                throw Bad($"the snapshots hold more than {_maxFiles} files and directories together ({MaxSnapshotsFileSystems} x MaxExtractEntries)");
        }

        public void Count() { if (++_count > MaxSnapshots) throw Bad($"more than {MaxSnapshots} snapshots"); }

        public ShellSnapshot Add(ShellSnapshot snap, bool counted = false)
        {
            if (!counted) Count();
            Bytes(snap.Fs.UsedBytes);
            return snap;
        }

        public void Bytes(long n)
        {
            if ((_bytes += n) > _maxBytes)
                throw Bad($"the snapshots hold more than {_maxBytes} bytes of files together ({MaxSnapshotsFileSystems} x MaxFileSystemBytes)");
        }
    }

    static Header ReadHeader(TextReader reader, string format, int version)
    {
        var first = reader.ReadLine() ?? throw Bad("the file is empty");
        // the version first, so a newer format fails with that, not with whatever it changed
        Probe p;
        try { p = JsonSerializer.Deserialize<Probe>(first, Json) ?? throw Bad("no header"); }
        catch (JsonException e) { throw Bad("the header is not JSON: " + ShellSnapshot.JsonError(e)); }
        if (p.Format != format) throw Bad($"the first line is not a {format} header");
        if (p.Version > version)
            throw new NotSupportedException($"{(format == HistoryFormat ? "history" : "recording")} format version {p.Version} is newer than this version of Topwater supports ({version}); upgrade Topwater to read it");
        if (p.Version < 1) throw Bad($"unknown format version {p.Version}");
        try { return JsonSerializer.Deserialize<Header>(first, Json)!; }
        catch (JsonException e) { throw Bad("bad header: " + ShellSnapshot.JsonError(e)); }
    }

    static Recording NewRecording(Header h, ShellSnapshot start, ShellOptions limits)
    {
        var o = h.Options ?? throw Bad("options are missing");
        if (o.User != null && !ShellSnapshot.UserName.IsMatch(o.User)) throw Bad($"bad user {JsonSerializer.Serialize(o.User)}");
        if (o.Hostname != null && !ShellSnapshot.HostName.IsMatch(o.Hostname)) throw Bad($"bad hostname {JsonSerializer.Serialize(o.Hostname)}");
        var d = new ShellOptions();
        // the file's limits can lower the caller's (or the default) limits, never raise them
        static long Cap(long? file, long max) => Math.Clamp(file ?? max, 0, max);
        return new Recording(start, new ShellOptions
        {
            User = o.User ?? d.User, Hostname = o.Hostname ?? d.Hostname,
            StartTime = o.StartTime is { } st ? Time(st, "startTime") : null,
            RandomSeed = o.RandomSeed,
            MaxLoopIterations = (int)Cap(o.MaxLoopIterations, limits.MaxLoopIterations), MaxExtractBytes = Cap(o.MaxExtractBytes, limits.MaxExtractBytes),
            MaxExtractEntries = (int)Cap(o.MaxExtractEntries, limits.MaxExtractEntries),
            MaxCallDuration = CallDuration(o.MaxCallDurationMs, limits.MaxCallDuration),
            MaxFileSystemBytes = Cap(o.MaxFileSystemBytes, limits.MaxFileSystemBytes), MaxStringBytes = Cap(o.MaxStringBytes, limits.MaxStringBytes),
        })
        {
            Created = h.Created is { } c ? Time(c, "created") : default,
            TopwaterVersion = h.Topwater,
            Metadata = h.Metadata ?? new(StringComparer.Ordinal),
            AppCommands = h.AppCommands ?? [],
            Web = h.Web?.ToPolicy(),
        };
    }

    /// <summary>The file's time limit (-1 or absent: none) under the caller's (<see cref="Timeout.InfiniteTimeSpan"/>: none).</summary>
    static TimeSpan CallDuration(long? fileMs, TimeSpan limit)
    {
        if (fileMs is not { } file || file < 0) return limit;
        // int.MaxValue ms is the longest a cancellation timer takes
        return TimeSpan.FromMilliseconds(limit == Timeout.InfiniteTimeSpan ? Math.Min(file, int.MaxValue) : Math.Min(file, (long)limit.TotalMilliseconds));
    }

    static void ReadCalls(TextReader reader, Recording rec, Func<SnapshotRef?, string, ShellSnapshot?> snap, ShellOptions limits, Func<string?, long, string, byte[]>? blob = null)
    {
        int lineNo = 1;
        while (reader.ReadLine() is { } line)
        {
            // every line counts, blank ones too, so a file of newlines stops here as early as one of calls
            if (++lineNo > limits.MaxExtractEntries + 1) throw Bad($"more than {limits.MaxExtractEntries} calls (MaxExtractEntries)");
            if (line.Length == 0) continue;
            CallEntry e;
            try { e = JsonSerializer.Deserialize<CallEntry>(line, Json) ?? throw Bad($"line {lineNo} is null"); }
            catch (JsonException ex) { throw Bad($"line {lineNo} is not JSON: {ShellSnapshot.JsonError(ex)}"); }
            if (e.Command == null) throw Bad($"line {lineNo} has no command");
            if (e.Index < 0) throw Bad($"line {lineNo}: bad index");
            if (!(e.ElapsedMs is >= 0 and <= 1e12)) throw Bad($"line {lineNo}: bad elapsedMs");
            if (e.Files?.Contains(null!) == true) throw Bad($"line {lineNo}: null in files");
            rec.Calls.Add(new RecordedCall(e.Index, e.Command, e.Stdout ?? "", e.Stderr ?? "", e.ExitCode, e.Trailer, TimeSpan.FromMilliseconds(e.ElapsedMs))
            {
                Time = e.Time is { } t ? Time(t, "time", $"line {lineNo}: ") : null,
                Files = e.Files,
                SnapshotAfter = e.Snapshot == null ? null : snap(e.Snapshot, $"call {e.Index}"),
                Web = e.Web?.Select((x, i) => x?.ToExchange(blob ?? ((_, _, w) => throw Bad($"{w}: web responses need a .topwater file")), $"line {lineNo} web {i}")
                    ?? throw Bad($"line {lineNo}: null in web")).ToList(),
            });
        }
    }

    static DateTimeOffset Time(string s, string what, string where = "")
    {
        try { return new(ShellSnapshot.ParseTime(s, what), TimeSpan.Zero); }
        catch (InvalidDataException) { throw Bad($"{where}bad {what} {JsonSerializer.Serialize(s)}"); }
    }

    static string? CurrentVersion => typeof(Shell).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    sealed class Probe
    {
        public string? Format { get; set; }
        public int Version { get; set; }
    }

    sealed class Header
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Created { get; set; }
        public string? Topwater { get; set; }
        public OptionsEntry? Options { get; set; }
        public List<string>? AppCommands { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        public WebEntry? Web { get; set; }
        // old JSON Lines recordings only
        public SnapshotRef? Start { get; set; }
    }

    /// <summary>The policy in the header: everything but the credentials' values and the handler.</summary>
    sealed class WebEntry
    {
        public List<string>? Domains { get; set; }
        public List<string>? Methods { get; set; }
        public bool AllowHttp { get; set; }
        public long MaxResponseBytes { get; set; }
        public int MaxRequestsPerCall { get; set; }
        public int MaxRequests { get; set; }
        public long TimeoutMs { get; set; }
        public int MaxRedirects { get; set; }
        public bool ExplainBlocked { get; set; }
        public List<string>? Credentials { get; set; }

        public WebPolicy ToPolicy()
        {
            var policy = new WebPolicy
            {
                Domains = Domains ?? [], Methods = Methods ?? ["GET"], AllowHttp = AllowHttp, MaxResponseBytes = MaxResponseBytes,
                MaxRequestsPerCall = MaxRequestsPerCall, MaxRequests = MaxRequests, Timeout = TimeSpan.FromMilliseconds(Math.Clamp(TimeoutMs, 0, int.MaxValue)),
                MaxRedirects = MaxRedirects, ExplainBlocked = ExplainBlocked,
                Credentials = (Credentials ?? []).ToDictionary(h => h, _ => (Func<string>)(() => ""), StringComparer.OrdinalIgnoreCase),
            };
            try { _ = new WebSession(policy); }
            catch (ArgumentException e) { throw Bad("bad web policy: " + e.Message); }
            return policy;
        }
    }

    /// <summary>One request of a call and its response; the body is a blob.</summary>
    sealed class ExchangeEntry
    {
        public string? Method { get; set; }
        public string? Url { get; set; }
        public int? Status { get; set; }
        public string? Reason { get; set; }
        public List<List<string>>? Headers { get; set; }
        public string? Body { get; set; }
        public long? Size { get; set; }
        public string? Error { get; set; }

        public static ExchangeEntry From(WebExchange x, ZipArchive zip, HashSet<string> written)
        {
            string? blob = null;
            if (x.Body.Length > 0)
            {
                blob = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(x.Body)).ToLowerInvariant();
                if (written.Add(blob)) using (var s = zip.CreateEntry("blobs/" + blob, CompressionLevel.Fastest).Open()) s.Write(x.Body);
            }
            return x.Error != null ? new() { Method = x.Method, Url = x.Url, Error = x.Error }
                : new() { Method = x.Method, Url = x.Url, Status = x.Status, Reason = x.Reason, Headers = [.. x.Headers.Select(h => new List<string> { h.Key, h.Value })], Body = blob, Size = x.Body.Length };
        }

        public WebExchange ToExchange(Func<string?, long, string, byte[]> blob, string where)
        {
            if (Method is not { Length: > 0 } m || Url is not { Length: > 0 } u) throw Bad($"{where}: method and url are required");
            if (Error != null)
                return Error is "resolve" or "blocked" or "connect" or "timeout" or "tls" or "recv" ? new(m, u, 0, "", [], []) { Error = Error } : throw Bad($"{where}: unknown error {JsonSerializer.Serialize(Error)}");
            if (Status is not (>= 100 and <= 999) || Size is not >= 0 || Headers?.Any(h => h is not { Count: 2 } || h.Contains(null!)) == true) throw Bad($"{where}: bad response");
            return new(m, u, Status.Value, Reason ?? "", [.. (Headers ?? []).Select(h => new KeyValuePair<string, string>(h[0], h[1]))], blob(Body, Size.Value, where));
        }
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
        public string? Time { get; set; }
        public IReadOnlyList<string>? Files { get; set; }
        public List<ExchangeEntry?>? Web { get; set; }
        // old JSON Lines recordings only
        public SnapshotRef? Snapshot { get; set; }
    }
}

/// <summary>One tool call: the command and what the shell returned. <see cref="Elapsed"/> is wall time and never compared.</summary>
public sealed record RecordedCall(int Index, string Command, string Stdout, string Stderr, int ExitCode, string? Trailer, TimeSpan Elapsed)
{
    /// <summary>The machine right after this call, when the recording keeps a checkpoint here (always after the last call).</summary>
    public ShellSnapshot? SnapshotAfter { get; init; }
    /// <summary>The shell's clock when the call started: the virtual clock on a deterministic shell.</summary>
    public DateTimeOffset? Time { get; init; }
    /// <summary>The paths the call added, changed or removed, sorted (null when not known, e.g. an older recording).</summary>
    public IReadOnlyList<string>? Files { get; init; }
    /// <summary>The web requests the call made and what came back, in order (null: none).</summary>
    public IReadOnlyList<WebExchange>? Web { get; init; }

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
    readonly VirtualFileSystem.ChangeTracker _changes; // the paths each call changed, without a snapshot per call
    bool _keepLast; // whether the last call's snapshot stays once another call follows

    internal ShellRecorder(Shell shell, RecordingOptions options)
    {
        _shell = shell;
        _every = options.SnapshotEvery;
        _changes = new(shell.Fs);
        Recording = new Recording(shell.Snapshot(), shell.Options)
        {
            Metadata = new(options.Metadata, StringComparer.Ordinal),
            AppCommands = [.. shell.AppCommandNames()],
        };
    }

    /// <summary>The recording so far; it keeps growing until <see cref="Stop"/>.</summary>
    public Recording Recording { get; }

    internal void Add(string command, ShellResult result, TimeSpan elapsed, DateTimeOffset started, List<WebExchange>? web = null)
    {
        Recording.Web ??= _shell.Web?.Policy;
        int index = Recording.Calls.Count;
        // the last call always has a snapshot, so a saved recording holds the end state; it is taken when first used (or
        // when recording stops), since the next call usually replaces it.
        // shortcut: a change made to the shell outside RunAsync after the last call, before then, is in it too; snapshot
        // every call again if a host needs that
        if (index > 0 && !_keepLast) Recording.Calls[^1] = Recording.Calls[^1] with { SnapshotAfter = null };
        _keepLast = _every > 0 && (index + 1) % _every == 0;
        var after = _keepLast ? _shell.Snapshot() : new ShellSnapshot(_shell.Snapshot, _shell.Options.User, _shell.Options.Hostname, _shell.Now);
        Recording.Calls.Add(RecordedCall.From(index, command, result, elapsed) with { Time = started, Files = _changes.Changes(), SnapshotAfter = after, Web = web is { Count: > 0 } ? web : null });
    }

    /// <summary>Snapshots the machine now and attaches it to the last call (a mark a viewer can jump to).</summary>
    public void SnapshotNow()
    {
        var now = _shell.Snapshot();
        _changes.Changes(); // the next call's files are measured from here
        if (Recording.Calls.Count == 0) { Recording.Start = now; return; }
        Recording.Calls[^1] = Recording.Calls[^1] with { SnapshotAfter = now };
        _keepLast = true;
    }

    /// <summary>Stops recording; later calls are not added.</summary>
    public void Stop()
    {
        if (_shell.Recorder != this) return;
        _shell.Recorder = null;
        if (Recording.Calls.Count > 0) Recording.Calls[^1].SnapshotAfter?.Build();
    }

    public void Dispose() => Stop();

    public void Save(Stream stream) => Recording.Save(stream);
    public void Save(string path) => Recording.Save(path);
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
