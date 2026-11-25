using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Topwater.Parsing;

namespace Topwater;

/// <summary>
/// A shell's whole machine state at one moment: the filesystem and the session (variables, functions, aliases, cwd,
/// options, traps, history, ...). Immutable: later changes to the shell never reach it, and restoring it twice gives the
/// same state. Registered commands are not part of it. See "Snapshots" in docs/reference.md.
/// </summary>
public sealed class ShellSnapshot
{
    /// <summary>The version of the saved format this Topwater writes; <see cref="Load"/> refuses newer ones.</summary>
    public const int FormatVersion = 1;
    const string FormatName = "topwater-snapshot";

    internal ShellSnapshot(VirtualFileSystem fs, ShellState state, string user, string hostname, DateTime created)
    {
        Fs = fs; State = state; User = user; Hostname = hostname; Created = created;
    }

    // never handed out: Restore copies from it
    internal VirtualFileSystem Fs { get; }
    internal ShellState State { get; }

    /// <summary>The agent's user name on the machine the snapshot was taken from.</summary>
    public string User { get; }
    public string Hostname { get; }
    /// <summary>When the snapshot was taken (UTC).</summary>
    public DateTime Created { get; }

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // file data, never embedded in HTML
    };

    /// <summary>Writes the snapshot as a zip: manifest.json (format, version, files' metadata, session) and blobs/&lt;sha256&gt; (file contents, stored once each).</summary>
    public void Save(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var written = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<FileEntry>();
        foreach (var e in Fs.Entries())
        {
            var entry = new FileEntry { Path = e.Path, Type = e.IsDirectory ? "dir" : "file", Mode = Convert.ToString(e.Mode, 8).PadLeft(4, '0'), Owner = e.Owner, Mtime = FormatTime(e.Modified) };
            if (!e.IsDirectory)
            {
                var bytes = e.Data ?? Encoding.UTF8.GetBytes(e.Text!);
                entry.Size = bytes.Length;
                entry.Binary = e.Data != null;
                entry.ReportedSize = e.ReportedSize;
                if (bytes.Length > 0)
                {
                    entry.Blob = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    if (written.Add(entry.Blob))
                        using (var s = zip.CreateEntry("blobs/" + entry.Blob, CompressionLevel.Fastest).Open()) s.Write(bytes);
                }
            }
            files.Add(entry);
        }
        var st = State;
        var names = new SortedSet<string>(st.Variables.Keys.Concat(st.Arrays.Keys).Concat(st.Attributes.Keys), StringComparer.Ordinal);
        var manifest = new Manifest
        {
            Format = FormatName, Version = FormatVersion, Created = FormatTime(Created), User = User, Hostname = Hostname, Cwd = st.Cwd,
            Files = files,
            Variables = names.Select(n => new VariableEntry
            {
                Name = n,
                Value = st.Variables.GetValueOrDefault(n),
                Attrs = st.Attributes.GetValueOrDefault(n),
                Exported = st.IsExported(n),
                Array = st.Arrays.TryGetValue(n, out var a) ? new ArrayEntry { Assoc = a.Assoc, Next = a.Next, Items = a.Items.Select(i => new[] { i.Key, i.Value }).ToList() } : null,
            }).ToList(),
            Functions = st.Functions.Values.Select(f => new FunctionEntry { Name = f.Name, Body = f.Source, Exported = st.ExportedFunctions.Contains(f.Name) }).ToList(),
            Aliases = st.Aliases, Set = [.. st.SetOptions], IfsUnset = st.IfsUnset, Shopt = [.. st.Shopts], Traps = st.Traps,
            Positional = st.Positional, DirStack = st.DirStack, History = st.History, LastExitCode = st.LastExitCode,
            SecondsBase = FormatTime(st.SecondsBase), LastArg = st.LastArg, PipeStatus = st.PipeStatus,
            Getopts = [st.Getopts.Optind, st.Getopts.Pos], Jobs = st.Jobs.Select(j => new[] { j.Key, j.Value }).ToList(),
            LastBackgroundPid = st.LastBackgroundPid, NextPid = st.NextPid,
            Clock = st.Clock is { } clock ? FormatTime(clock) : null, Random = st.Random?.ToString(CultureInfo.InvariantCulture),
        };
        using var m = zip.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
        JsonSerializer.Serialize(m, manifest, Json);
    }

    /// <summary>
    /// Reads a snapshot written by <see cref="Save"/>, treating it as untrusted: <paramref name="limits"/>' MaxFileSystemBytes,
    /// MaxStringBytes and MaxExtractEntries bound it, and nothing is written outside memory. Throws <see cref="InvalidDataException"/>
    /// for anything malformed or over a limit, <see cref="NotSupportedException"/> for a newer format version.
    /// </summary>
    public static ShellSnapshot Load(Stream stream, ShellOptions? limits = null)
    {
        try { return Read(stream, limits ?? new ShellOptions()); }
        catch (Exception e) when (e is not (NotSupportedException or OperationCanceledException or OutOfMemoryException) && !e.Message.StartsWith(Invalid))
        {
            throw new InvalidDataException(Invalid + e.Message, e);
        }
    }

    static readonly Regex UserName = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}$");
    static readonly Regex HostName = new(@"^[A-Za-z0-9][A-Za-z0-9.-]{0,63}$");
    static readonly Regex VarName = new(@"^[A-Za-z_][A-Za-z0-9_]*$");
    static readonly Regex Word = new(@"^[A-Za-z0-9_+-]{1,32}$");
    static readonly Regex Blob = new(@"^[0-9a-f]{64}$");

    const string Invalid = "not a valid Topwater snapshot: ";
    static InvalidDataException Bad(string why) => new(Invalid + why);

    static ShellSnapshot Read(Stream stream, ShellOptions o)
    {
        long maxManifest = 4 * o.MaxStringBytes + 1024L * o.MaxExtractEntries;
        long maxArchive = 2 * o.MaxFileSystemBytes + maxManifest;
        var input = stream;
        if (!stream.CanSeek)
        {
            input = new MemoryStream();
            ReadCapped(stream, input, maxArchive, "the snapshot");
            input.Position = 0;
        }
        else if (stream.Length - stream.Position > maxArchive) throw Bad($"the snapshot is larger than {maxArchive} bytes");

        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > o.MaxExtractEntries + 1) throw Bad($"more than {o.MaxExtractEntries} entries (MaxExtractEntries)");
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw Bad("manifest.json is missing");
        var json = new MemoryStream();
        using (var s = manifestEntry.Open()) ReadCapped(s, json, maxManifest, "manifest.json");
        var bytes = json.GetBuffer().AsSpan(0, (int)json.Length);

        // the version first, so a newer format fails with that, not with whatever it changed
        var probe = JsonSerializer.Deserialize<Probe>(bytes, Json);
        if (probe?.Format != FormatName) throw Bad("manifest.json is not a Topwater snapshot manifest");
        if (probe.Version > FormatVersion)
            throw new NotSupportedException($"snapshot format version {probe.Version} is newer than this version of Topwater supports ({FormatVersion}); upgrade Topwater to load it");
        if (probe.Version < 1) throw Bad($"unknown format version {probe.Version}");
        var m = JsonSerializer.Deserialize<Manifest>(bytes, Json)!;

        var user = Check(m.User, UserName, "user");
        var hostname = Check(m.Hostname, HostName, "hostname");
        var fs = ReadFiles(zip, m.Files ?? [], o);
        var state = ReadState(m, o);
        return new ShellSnapshot(fs, state, user, hostname, ParseTime(m.Created, "created"));
    }

    static VirtualFileSystem ReadFiles(ZipArchive zip, List<FileEntry> files, ShellOptions o)
    {
        if (files.Count > o.MaxExtractEntries) throw Bad($"more than {o.MaxExtractEntries} files and directories (MaxExtractEntries)");
        long total = 0;
        foreach (var f in files)
        {
            if (f?.Type == "file") total += f.Size >= 0 ? f.Size : throw Bad($"{f.Path}: negative size");
            if (total > o.MaxFileSystemBytes) throw Bad($"the files hold more than {o.MaxFileSystemBytes} bytes (MaxFileSystemBytes)");
        }
        var fs = new VirtualFileSystem();
        var cache = new Dictionary<(string, bool), object>(); // identical contents load once and are shared, as in memory
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        foreach (var f in files)
        {
            if (f?.Path is not { Length: > 0 and <= 4096 } path || path[0] != '/' || path.Contains('\0') || VirtualFileSystem.Normalize("/", path) != path)
                throw Bad($"bad path {JsonSerializer.Serialize(f?.Path)}");
            if (f.Mode is not { Length: >= 1 and <= 4 } mode || !mode.All(c => c is >= '0' and <= '7')) throw Bad($"{path}: bad mode");
            var owner = Check(f.Owner, UserName, path + ": owner");
            var mtime = ParseTime(f.Mtime, path + ": mtime");
            string? text = null;
            byte[]? data = null;
            if (f.Type == "file")
            {
                if (f.ReportedSize < 0) throw Bad($"{path}: negative reportedSize");
                if (f.Size == 0) { if (f.Binary) data = []; else text = ""; }
                else
                {
                    var blob = Check(f.Blob, Blob, path + ": blob");
                    if (!cache.TryGetValue((blob, f.Binary), out var content))
                    {
                        var entry = zip.GetEntry("blobs/" + blob) ?? throw Bad($"{path}: blob {blob} is missing");
                        var raw = new MemoryStream();
                        using (var s = entry.Open()) ReadCapped(s, raw, f.Size, "blob " + blob);
                        var b = raw.ToArray();
                        if (b.Length != f.Size) throw Bad($"{path}: blob {blob} holds {b.Length} bytes, not {f.Size}");
                        if (Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant() != blob) throw Bad($"{path}: blob {blob} doesn't match its hash");
                        cache[(blob, f.Binary)] = content = f.Binary ? b : strict.GetString(b);
                    }
                    if (f.Binary) data = (byte[])content; else text = (string)content;
                }
            }
            else if (f.Type != "dir") throw Bad($"{path}: unknown type {JsonSerializer.Serialize(f.Type)}");
            fs.Put(new(path, f.Type == "dir", Convert.ToInt32(mode, 8), owner, mtime, text, data, f.ReportedSize));
        }
        return fs;
    }

    static ShellState ReadState(Manifest m, ShellOptions o)
    {
        long chars = 0;
        void Charge(string what, long n)
        {
            if ((chars += n) > o.MaxStringBytes) throw Bad($"{what}: variables hold more than {o.MaxStringBytes} characters (MaxStringBytes)");
        }
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var arrays = new Dictionary<string, ShellArray>(StringComparer.Ordinal);
        var unexported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in m.Variables ?? [])
        {
            var name = Check(v?.Name, VarName, "variable name");
            if (variables.ContainsKey(name) || arrays.ContainsKey(name) || attributes.ContainsKey(name)) throw Bad($"variable {name} appears twice");
            if (v!.Attrs is { Length: > 0 } attrs) attributes[name] = attrs.Length <= 16 && attrs.All(char.IsAsciiLetter) ? attrs : throw Bad($"{name}: bad attributes");
            if (!v.Exported) unexported.Add(name);
            if (v.Value != null) { Charge(name, name.Length + v.Value.Length); variables[name] = v.Value; }
            if (v.Array is { } a)
            {
                var array = new ShellArray(a.Assoc);
                foreach (var item in a.Items ?? [])
                {
                    if (item is not [{ } key, { } value]) throw Bad($"{name}: an element is not a [key, value] pair");
                    if (!a.Assoc && !(long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index.ToString(CultureInfo.InvariantCulture) == key))
                        throw Bad($"{name}: bad index {JsonSerializer.Serialize(key)}");
                    if (array.Items.ContainsKey(key)) throw Bad($"{name}: element {JsonSerializer.Serialize(key)} appears twice");
                    Charge(name, key.Length + value.Length);
                    array.Set(key, value);
                }
                array.Next = a.Assoc ? 0 : a.Next >= array.Next ? a.Next : throw Bad($"{name}: next is below the highest index");
                arrays[name] = array;
            }
        }
        var functions = new Dictionary<string, FunctionDef>(StringComparer.Ordinal);
        var exportedFunctions = new HashSet<string>(StringComparer.Ordinal);
        long functionChars = 0;
        foreach (var f in m.Functions ?? [])
        {
            if (f?.Name is not { Length: > 0 } name || f.Body is not { } body) throw Bad("a function without a name or body");
            if ((functionChars += name.Length + body.Length) > o.MaxStringBytes) throw Bad($"functions hold more than {o.MaxStringBytes} characters (MaxStringBytes)");
            // the body is parsed again, as `name() body`, and must come back as exactly that one function
            Script parsed;
            try { parsed = Parser.Parse(name + "() " + body + "\n"); }
            catch (ParseException e) { throw Bad($"function {name}: {e.Message}"); }
            if (parsed.Items is not [{ Rest.Count: 0, Background: false, First: { Negate: false, Timed: false, Commands: [FunctionDef def] } }] || def.Name != name)
                throw Bad($"function {JsonSerializer.Serialize(name)} is not one function definition");
            if (!functions.TryAdd(name, def)) throw Bad($"function {name} appears twice");
            if (f.Exported) exportedFunctions.Add(name);
        }
        foreach (var (k, v) in m.Aliases ?? []) if (v == null || k.Length == 0 || k.Any(c => char.IsWhiteSpace(c) || c is '=' or '/' or '\0')) throw Bad($"bad alias {JsonSerializer.Serialize(k)}");
        foreach (var (k, v) in m.Traps ?? []) if (v == null) throw Bad($"trap {k} has no action"); else Check(k, Word, "trap signal");
        foreach (var s in m.Set ?? []) if (!Shell.SnapshotSetOptions.Contains(s)) throw Bad($"unknown set option {JsonSerializer.Serialize(s)}");
        foreach (var s in m.Shopt ?? []) Check(s, Word, "shopt name");
        if (m.Cwd is not { Length: > 0 } cwd || cwd[0] != '/' || VirtualFileSystem.Normalize("/", cwd) != cwd) throw Bad("bad cwd");
        if (m.LastExitCode is < 0 or > 255 || (m.PipeStatus ?? []).Any(c => c is < 0 or > 255)) throw Bad("exit codes must be 0-255");
        if (m.Getopts is { } g && (g.Length != 2 || g[0] < 0 || g[1] < 0)) throw Bad("bad getopts state");
        var jobs = new Dictionary<int, int>();
        foreach (var j in m.Jobs ?? []) if (j is not [var pid, var code] || code is < 0 or > 255 || !jobs.TryAdd(pid, code)) throw Bad("bad job");
        static List<string> Strings(List<string>? list, string what) => list?.Contains(null!) == true ? throw Bad($"null in {what}") : list ?? [];
        return new ShellState
        {
            Cwd = cwd, Variables = variables, Attributes = attributes, Unexported = unexported, Arrays = arrays,
            Functions = functions, ExportedFunctions = exportedFunctions,
            Aliases = new(m.Aliases ?? [], StringComparer.Ordinal), Traps = new(m.Traps ?? [], StringComparer.Ordinal),
            SetOptions = [.. m.Set ?? []], IfsUnset = m.IfsUnset, Shopts = [.. m.Shopt ?? []],
            Positional = Strings(m.Positional, "positional"), DirStack = Strings(m.DirStack, "dirStack"), History = Strings(m.History, "history"),
            LastExitCode = m.LastExitCode, SecondsBase = ParseTime(m.SecondsBase, "secondsBase"), LastArg = m.LastArg ?? "",
            PipeStatus = m.PipeStatus ?? [], Getopts = m.Getopts is [var i, var p] ? (i, p) : default, Jobs = jobs,
            LastBackgroundPid = m.LastBackgroundPid, NextPid = m.NextPid,
            Clock = m.Clock is null ? null : ParseTime(m.Clock, "clock"),
            Random = m.Random is null ? null : ulong.TryParse(m.Random, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : throw Bad("bad random state"),
        };
    }

    static string Check(string? value, Regex shape, string what) =>
        value != null && shape.IsMatch(value) ? value : throw Bad($"bad {what} {JsonSerializer.Serialize(value)}");

    /// <summary>Copies at most <paramref name="max"/> bytes, failing past it whatever the source claims its size is.</summary>
    static void ReadCapped(Stream from, Stream to, long max, string what)
    {
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            if ((total += n) > max) throw Bad($"{what} is larger than {max} bytes");
            to.Write(buffer, 0, n);
        }
    }

    // an instant in UTC; a local time (the back-dated system files) is converted, an unspecified one taken as UTC
    static string FormatTime(DateTime t) =>
        (t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    static DateTime ParseTime(string? s, string what) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : throw Bad($"bad {what} {JsonSerializer.Serialize(s)}");

    // ---------- the manifest (format version 1) ----------

    sealed class Probe
    {
        public string? Format { get; set; }
        public int Version { get; set; }
    }

    sealed class Manifest
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Created { get; set; }
        public string? User { get; set; }
        public string? Hostname { get; set; }
        public string? Cwd { get; set; }
        public List<FileEntry>? Files { get; set; }
        public List<VariableEntry>? Variables { get; set; }
        public List<FunctionEntry>? Functions { get; set; }
        public Dictionary<string, string>? Aliases { get; set; }
        public List<string>? Set { get; set; }
        public bool IfsUnset { get; set; }
        public List<string>? Shopt { get; set; }
        public Dictionary<string, string>? Traps { get; set; }
        public List<string>? Positional { get; set; }
        public List<string>? DirStack { get; set; }
        public List<string>? History { get; set; }
        public int LastExitCode { get; set; }
        public string? SecondsBase { get; set; }
        public string? LastArg { get; set; }
        public int[]? PipeStatus { get; set; }
        public int[]? Getopts { get; set; }
        public List<int[]>? Jobs { get; set; }
        public int LastBackgroundPid { get; set; }
        public int NextPid { get; set; }
        // added in version 1 as optional fields: set only by a shell with a virtual clock / a random seed
        public string? Clock { get; set; }
        public string? Random { get; set; }
    }

    sealed class FileEntry
    {
        public string? Path { get; set; }
        public string? Type { get; set; }
        public string? Mode { get; set; }
        public string? Owner { get; set; }
        public string? Mtime { get; set; }
        public long Size { get; set; }
        public bool Binary { get; set; }
        public long? ReportedSize { get; set; }
        public string? Blob { get; set; }
    }

    sealed class VariableEntry
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
        public string? Attrs { get; set; }
        public bool Exported { get; set; }
        public ArrayEntry? Array { get; set; }
    }

    sealed class ArrayEntry
    {
        public bool Assoc { get; set; }
        public long Next { get; set; }
        public List<string[]>? Items { get; set; }
    }

    sealed class FunctionEntry
    {
        public string? Name { get; set; }
        public string? Body { get; set; }
        public bool Exported { get; set; }
    }
}

/// <summary>The session part of a snapshot: copies of the shell's collections. Arrays and function definitions are
/// shared (arrays are copied on their next write, definitions never change).</summary>
internal sealed class ShellState
{
    public required string Cwd { get; init; }
    public required Dictionary<string, string> Variables { get; init; }
    /// <summary>declare attributes by name (see Shell.VarAttrs).</summary>
    public required Dictionary<string, string> Attributes { get; init; }
    /// <summary>Variables the shell created and didn't export (see Shell.IsExported).</summary>
    public required HashSet<string> Unexported { get; init; }
    public required Dictionary<string, ShellArray> Arrays { get; init; }
    public required Dictionary<string, FunctionDef> Functions { get; init; }
    public required HashSet<string> ExportedFunctions { get; init; }
    public required Dictionary<string, string> Aliases { get; init; }
    public required Dictionary<string, string> Traps { get; init; }
    public required HashSet<string> SetOptions { get; init; }
    public bool IfsUnset { get; init; }
    public required HashSet<string> Shopts { get; init; }
    public required List<string> Positional { get; init; }
    public required List<string> DirStack { get; init; }
    public required List<string> History { get; init; }
    public int LastExitCode { get; init; }
    public DateTime SecondsBase { get; init; }
    public required string LastArg { get; init; }
    public required int[] PipeStatus { get; init; }
    public (int Optind, int Pos) Getopts { get; init; }
    public required Dictionary<int, int> Jobs { get; init; }
    public int LastBackgroundPid { get; init; }
    public int NextPid { get; init; }
    /// <summary>The virtual clock's time (ShellOptions.StartTime) and the seeded random generator's state, when the shell has them.</summary>
    public DateTime? Clock { get; init; }
    public ulong? Random { get; init; }

    public bool IsExported(string name) =>
        Attributes.TryGetValue(name, out var a) && a.Contains('x') || Variables.ContainsKey(name) && !Unexported.Contains(name);
}

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
    /// A new shell from a saved snapshot (see <see cref="ShellSnapshot.Load"/> for the checks). It has the built-ins but none of
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
