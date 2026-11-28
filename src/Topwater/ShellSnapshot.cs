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
/// same state. Registered commands are not part of it. See docs/snapshots-and-recordings.md.
/// </summary>
public sealed class ShellSnapshot
{
    /// <summary>The newest version of the saved format this Topwater writes; <see cref="Load(Stream, ShellOptions)"/> refuses newer ones. Version 2 added
    /// symbolic links (type "symlink" with a "target") and hard links (a "link" naming the entry's first path); a snapshot
    /// without links is still written as version 1, so older Topwater can load it. Version 3 added mounts (a reader that doesn't
    /// know them would load the machine without them); only a snapshot with mounts is written as version 3.</summary>
    public const int FormatVersion = 3;
    const string FormatName = "topwater-snapshot";

    internal ShellSnapshot(VirtualFileSystem fs, ShellState state, string user, string hostname, DateTime created)
    {
        _fs = fs; _state = state; User = user; Hostname = hostname; Created = created;
    }

    /// <summary>A snapshot whose machine is built by <paramref name="load"/> when first used: a recording's checkpoint, read
    /// from the file when a replay needs it (and dropped again when <paramref name="kept"/> holds too many), or the machine
    /// after a recorder's last call.</summary>
    internal ShellSnapshot(Func<ShellSnapshot> load, string user, string hostname, DateTime created, Kept? kept = null)
    {
        _load = load; _kept = kept; User = user; Hostname = hostname; Created = created;
    }

    VirtualFileSystem? _fs;
    ShellState? _state;
    Func<ShellSnapshot>? _load;
    readonly Kept? _kept;

    // never handed out: Restore copies from it
    internal VirtualFileSystem Fs => _fs ?? Built()._fs!;
    internal ShellState State => _state ?? Built()._state!;

    /// <summary>Builds the machine now if it is still to be read.</summary>
    internal void Build() => _ = Fs;

    ShellSnapshot Built()
    {
        lock ((object?)_kept ?? this)
        {
            if (_fs != null) return this;
            var built = _load!();
            (_fs, _state) = (built._fs!, built._state!);
            if (_kept == null) _load = null; // built once for good
            else _kept.Add(this);
            return built;
        }
    }

    /// <summary>The few lazily read snapshots of one file that stay built; older ones are dropped and read again when needed.</summary>
    internal sealed class Kept(int capacity)
    {
        readonly Queue<ShellSnapshot> _built = new();
        public void Add(ShellSnapshot s)
        {
            _built.Enqueue(s);
            while (_built.Count > capacity) { var old = _built.Dequeue(); (old._fs, old._state) = (null, null); }
        }
    }

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
        Converters = { new EscapedStringConverter() }, // escaped bytes as \udcXX, as the other ports write them
    };

    /// <summary>Writes the snapshot as a .topwater file without history: a zip of manifest.json (format, version, files' metadata,
    /// session) and blobs/&lt;sha256&gt; (file contents, stored once each). See spec/topwater-file.</summary>
    public void Save(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        WriteTo(zip, "manifest.json", new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>Writes this machine's manifest as <paramref name="name"/> and the blobs not yet in <paramref name="written"/>,
    /// so the manifest and checkpoints of a .topwater file share one set of blobs.</summary>
    internal void WriteTo(ZipArchive zip, string name, HashSet<string> written)
    {
        var files = new List<FileEntry>();
        bool links = false;
        foreach (var e in Fs.Entries())
        {
            var entry = new FileEntry
            {
                Path = e.Path, Type = e.IsDirectory ? "dir" : e.LinkTarget != null ? "symlink" : "file", Mode = Convert.ToString(e.Mode, 8).PadLeft(4, '0'),
                Owner = e.Owner, Mtime = FormatTime(e.Modified), Target = e.LinkTarget, Link = e.HardLink,
            };
            links |= e.LinkTarget != null || e.HardLink != null;
            if (entry.Type == "file")
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
            Format = FormatName, Version = st.Mounts.Count > 0 ? 3 : links ? 2 : 1, Created = FormatTime(Created), User = User, Hostname = Hostname, Cwd = st.Cwd,
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
            Mounts = st.Mounts.Count > 0 ? [.. st.Mounts.Select(d => MountJson.From(d, zip, written))] : null,
        };
        using var m = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        JsonSerializer.Serialize(m, manifest, Json);
    }

    /// <summary>
    /// Reads a snapshot written by <see cref="Save"/>, or the machine (manifest.json) of any .topwater file, treating it as untrusted: <paramref name="limits"/>' MaxFileSystemBytes,
    /// MaxStringBytes and MaxExtractEntries bound it, and nothing is written outside memory. Throws <see cref="InvalidDataException"/>
    /// for anything malformed or over a limit, <see cref="NotSupportedException"/> for a newer format version.
    /// </summary>
    public static ShellSnapshot Load(Stream stream, ShellOptions? limits = null) => Load(stream, limits, null);

    internal static ShellSnapshot Load(Stream stream, ShellOptions? limits, Action<int>? countFiles)
    {
        var o = limits ?? new ShellOptions();
        return Checked(() =>
        {
            // any .topwater file: a recording's checkpoints make it up to four times a snapshot; the machine read stays one
            using var zip = OpenZip(stream, o, Recording.MaxSnapshotsFileSystems * MaxArchiveBytes(o));
            return ReadManifest(zip, "manifest.json", o, countFiles);
        });
    }

    /// <summary>Whether the stream is a .topwater file with history (a recording), as opposed to a snapshot; false for
    /// anything that isn't a zip. The zip is opened with the same checks as <see cref="Load(Stream, ShellOptions?)"/>.</summary>
    public static bool HasHistory(Stream stream, ShellOptions? limits = null)
    {
        limits ??= new ShellOptions();
        long at = stream.Position;
        bool zip = stream.ReadByte() == 'P' && stream.ReadByte() == 'K';
        stream.Position = at;
        return zip && Checked(() => { using var z = OpenZip(stream, limits, Recording.MaxSnapshotsFileSystems * MaxArchiveBytes(limits)); return z.GetEntry("history.jsonl") != null; });
    }

    /// <summary>Runs a read, turning whatever a malformed file makes it throw into <see cref="InvalidDataException"/>.</summary>
    internal static T Checked<T>(Func<T> read, string invalid = Invalid)
    {
        try { return read(); }
        catch (Exception e) when (e is not (NotSupportedException or OperationCanceledException or OutOfMemoryException) && !e.Message.StartsWith("not a valid Topwater "))
        {
            throw new InvalidDataException(invalid + (e is JsonException j ? JsonError(j) : e.Message), e);
        }
    }

    // \z, not $: $ also matches before a final newline
    internal static readonly Regex UserName = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}\z");
    internal static readonly Regex HostName = new(@"^[A-Za-z0-9][A-Za-z0-9.-]{0,63}\z");
    static readonly Regex VarName = new(@"^[A-Za-z_][A-Za-z0-9_]*\z");
    static readonly Regex Word = new(@"^[A-Za-z0-9_+-]{1,32}\z");
    internal static readonly Regex Blob = new(@"^[0-9a-f]{64}\z");

    const string Invalid = "not a valid Topwater snapshot: ";
    static InvalidDataException Bad(string why) => new(Invalid + why);

    /// <summary>The paths whose entry differs between two machines (added, removed, or changed in type, mode, owner, mtime,
    /// content or link), sorted. Contents are shared between snapshots, so an unchanged file compares by reference.</summary>
    internal static List<string> ChangedFiles(ShellSnapshot before, ShellSnapshot after)
    {
        var old = before.Fs.Entries().ToDictionary(e => e.Path, StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var e in after.Fs.Entries())
            if (!old.Remove(e.Path, out var o) || o != e) changed.Add(e.Path);
        changed.AddRange(old.Keys);
        changed.Sort(StringComparer.Ordinal);
        return changed;
    }

    /// <summary>The most bytes one manifest (or checkpoint, or history.jsonl) may take.</summary>
    internal static long MaxManifestBytes(ShellOptions o) => 4 * o.MaxStringBytes + 1024L * o.MaxExtractEntries;
    /// <summary>The largest snapshot file: its files' content (twice, for a zip that stores instead of compressing) and a manifest.</summary>
    internal static long MaxArchiveBytes(ShellOptions o) => 2 * o.MaxFileSystemBytes + MaxManifestBytes(o);

    /// <summary>
    /// Opens a .topwater (or snapshot) zip of at most <paramref name="maxBytes"/>. The entry count is read from the end of
    /// the archive and checked before the zip reader lists the entries, so a forged directory never gets materialised.
    /// </summary>
    internal static ZipArchive OpenZip(Stream stream, ShellOptions o, long maxBytes)
    {
        var input = stream;
        if (!stream.CanSeek)
        {
            input = new MemoryStream();
            ReadCapped(stream, input, maxBytes, "the file");
            input.Position = 0;
        }
        else if (stream.Length - stream.Position > maxBytes) throw Bad($"the file is larger than {maxBytes} bytes");
        long maxEntries = (long)Recording.MaxSnapshotsFileSystems * o.MaxExtractEntries + Recording.MaxSnapshots + 2;
        if (ZipEntryCount(input, maxEntries) is var count && count > maxEntries) throw Bad($"{(count == maxEntries + 1 ? "more than " + maxEntries : count)} zip entries, more than {maxEntries} (4 x MaxExtractEntries + 1002)");
        var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: input == stream);
        // one name, one entry: readers that pick the first or the last of two must never see different files
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in zip.Entries) if (!names.Add(e.FullName)) { zip.Dispose(); throw Bad($"entry {JsonSerializer.Serialize(e.FullName)} appears twice"); }
        return zip;
    }

    /// <summary>
    /// The number of zip entries: what the end-of-central-directory record states (zip64 included), or more if the
    /// directory itself holds more, counted by walking it and stopping once past <paramref name="cap"/>. -1 when there is
    /// no end record (the zip reader then fails).
    /// </summary>
    internal static long ZipEntryCount(Stream s, long cap)
    {
        long start = s.Position, length = s.Length - start;
        var tail = new byte[(int)Math.Min(length, 22 + 65535)];
        s.Position = start + length - tail.Length;
        s.ReadExactly(tail);
        try
        {
            for (int i = tail.Length - 22; i >= 0; i--)
            {
                if (BitConverter.ToUInt32(tail, i) != 0x06054b50) continue;
                long stated = BitConverter.ToUInt16(tail, i + 10), directory = BitConverter.ToUInt32(tail, i + 16);
                // zip64: the locator just before points at the zip64 record, which holds the real count and offset
                if ((stated == 0xFFFF || directory == 0xFFFFFFFF) && i >= 20 && BitConverter.ToUInt32(tail, i - 20) == 0x07064b50)
                {
                    var offset = BitConverter.ToInt64(tail, i - 12);
                    if (offset < 0 || offset > length - 56) return long.MaxValue;
                    var record = new byte[56];
                    s.Position = start + offset;
                    s.ReadExactly(record);
                    if (BitConverter.ToUInt32(record, 0) != 0x06064b50) return long.MaxValue;
                    stated = (long)Math.Min(BitConverter.ToUInt64(record, 32), long.MaxValue);
                    directory = (long)Math.Min(BitConverter.ToUInt64(record, 48), long.MaxValue);
                }
                if (stated > cap) return stated;
                // the stated count is the file's word: count the directory's own entries too
                long walked = 0;
                if (directory >= 0 && directory < length)
                {
                    s.Position = start + directory;
                    var reader = new BufferedStream(s, 1 << 16);
                    var head = new byte[46];
                    var skip = new byte[3 * 65535];
                    while (walked <= cap && reader.ReadAtLeast(head, 46, throwOnEndOfStream: false) == 46 && BitConverter.ToUInt32(head, 0) == 0x02014b50)
                    {
                        walked++;
                        int rest = BitConverter.ToUInt16(head, 28) + BitConverter.ToUInt16(head, 30) + BitConverter.ToUInt16(head, 32);
                        if (reader.ReadAtLeast(skip.AsSpan(0, rest), rest, throwOnEndOfStream: false) < rest) break;
                    }
                }
                return Math.Max(stated, walked);
            }
            return -1;
        }
        finally { s.Position = start; }
    }

    /// <summary>Reads the manifest <paramref name="name"/> (manifest.json or a checkpoint) and the blobs it names.</summary>
    internal static ShellSnapshot ReadManifest(ZipArchive zip, string name, ShellOptions o, Action<int>? countFiles = null)
    {
        var m = JsonSerializer.Deserialize<Manifest>(ReadManifestBytes(zip, name, o), Json)!;

        var user = Check(m.User, UserName, "user");
        var hostname = Check(m.Hostname, HostName, "hostname");
        countFiles?.Invoke(m.Files?.Count ?? 0); // before any entry is built
        var fs = ReadFiles(zip, m.Files ?? [], o);
        var state = ReadState(m, o);
        state.Mounts = ReadMounts(m.Mounts, zip, o);
        return new ShellSnapshot(fs, state, user, hostname, ParseTime(m.Created, "created"));
    }

    /// <summary>A manifest's bytes (capped), its format and version checked: the version first, so a newer format fails with
    /// that, not with whatever it changed.</summary>
    internal static byte[] ReadManifestBytes(ZipArchive zip, string name, ShellOptions o, bool check = true)
    {
        var manifestEntry = zip.GetEntry(name) ?? throw Bad($"{name} is missing");
        var json = new MemoryStream();
        using (var s = manifestEntry.Open()) ReadCapped(s, json, MaxManifestBytes(o), name);
        var bytes = json.ToArray();
        if (check) CheckVersion(JsonSerializer.Deserialize<Probe>(bytes, Json), name);
        return bytes;
    }

    /// <summary>A manifest as its reader sees it, written again: the same machine gives the same bytes whatever the key order.</summary>
    internal static byte[] CanonicalManifest(byte[] manifest) => JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<Manifest>(manifest, Json), Json);

    static void CheckVersion(Probe? probe, string name)
    {
        if (probe?.Format != FormatName) throw Bad($"{name} is not a Topwater snapshot manifest");
        if (probe.Version > FormatVersion)
            throw new NotSupportedException($"snapshot format version {probe.Version} is newer than this version of Topwater supports ({FormatVersion}); upgrade Topwater to load it");
        if (probe.Version < 1) throw Bad($"unknown format version {probe.Version}");
    }

    /// <summary>
    /// A checkpoint (<paramref name="what"/>) checked without building it: format, version, user, hostname, time, and its entries and bytes against
    /// the limits (<paramref name="countFiles"/> and <paramref name="countBytes"/> charge them to the file's budget). The
    /// rest (each entry, blob and the session) is checked when <paramref name="load"/> first builds it.
    /// </summary>
    internal static ShellSnapshot ReadOutline(byte[] bytes, string what, ShellOptions o, Action<int> countFiles, Action<long> countBytes, Func<ShellSnapshot> load, Kept kept)
    {
        // read as a stream of tokens: building 100,000 entries only to count them costs what building the machine does
        string? user = null, hostname = null, created = null;
        var probe = new Probe();
        var files = new List<FileEntry>();
        var r = new Utf8JsonReader(bytes);
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) throw Bad("the manifest is not an object");
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            var name = r.GetString();
            r.Read();
            if (name == "format") probe.Format = r.TokenType == JsonTokenType.String ? r.GetString() : null;
            else if (name == "version") probe.Version = r.TokenType == JsonTokenType.Number && r.TryGetInt32(out var v) ? v : throw Bad($"{what}: bad version");
            else if (name is "user" or "hostname" or "created")
            {
                var value = r.TokenType == JsonTokenType.String ? r.GetString() : null;
                if (name == "user") user = value; else if (name == "hostname") hostname = value; else created = value;
            }
            else if (name == "files" && r.TokenType == JsonTokenType.StartArray)
            {
                files.Clear(); // a repeated name: the last one counts, as when the machine is built
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    var f = new FileEntry();
                    files.Add(f);
                    if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                    while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                    {
                        if (r.ValueTextEquals("type")) { r.Read(); f.Type = r.TokenType == JsonTokenType.String ? r.GetString() : null; }
                        else if (r.ValueTextEquals("size")) { r.Read(); f.Size = r.TokenType == JsonTokenType.Number && r.TryGetInt64(out var n) ? n : throw Bad("a size is not a whole number"); }
                        else if (r.ValueTextEquals("link")) { r.Read(); f.Link = r.TokenType == JsonTokenType.Null ? null : ""; }
                        else { r.Read(); r.Skip(); }
                    }
                }
            }
            else r.Skip();
        }
        CheckVersion(probe, what);
        var u = Check(user, UserName, "user");
        var h = Check(hostname, HostName, "hostname");
        var t = ParseTime(created, "created");
        countFiles(files.Count);
        countBytes(CheckSizes(files, o));
        return new ShellSnapshot(load, u, h, t, kept);
    }

    /// <summary>Checks a manifest's entry count and file bytes against the limits; returns the bytes held once (hard links
    /// share their first name's).</summary>
    static long CheckSizes(List<FileEntry> files, ShellOptions o)
    {
        if (files.Count > o.MaxExtractEntries) throw Bad($"more than {o.MaxExtractEntries} files and directories (MaxExtractEntries)");
        long total = 0, held = 0;
        foreach (var f in files)
        {
            if (f?.Type != "file") continue;
            total += f.Size >= 0 ? f.Size : throw Bad($"{f.Path}: negative size");
            if (f.Link == null) held += f.Size;
            if (total > o.MaxFileSystemBytes) throw Bad($"the files hold more than {o.MaxFileSystemBytes} bytes (MaxFileSystemBytes)");
        }
        return held;
    }

    static VirtualFileSystem ReadFiles(ZipArchive zip, List<FileEntry> files, ShellOptions o)
    {
        CheckSizes(files, o);
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
            if (f.Link != null && (f.Link is not { Length: > 0 and <= 4096 } || f.Link[0] != '/' || VirtualFileSystem.Normalize("/", f.Link) != f.Link || f.Type == "dir"))
                throw Bad($"{path}: bad link {JsonSerializer.Serialize(f.Link)}");
            if (f.Type == "symlink" && f.Target is not { Length: > 0 and <= 4096 } || f.Target?.Contains('\0') == true) throw Bad($"{path}: bad symlink target");
            if (f.Type == "file" && f.Link == null) // another name of a file shares the first one's content
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
            else if (f.Type is not ("dir" or "file" or "symlink")) throw Bad($"{path}: unknown type {JsonSerializer.Serialize(f.Type)}");
            fs.Put(new(path, f.Type == "dir", Convert.ToInt32(mode, 8), owner, mtime, text, data, f.ReportedSize, f.Type == "symlink" ? f.Target : null, f.Link));
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

    /// <summary>The mounts' definitions and pending changes (docs/mounts.md), checked like files: canonical paths, sizes within
    /// MaxFileSystemBytes, at most MaxExtractEntries changes, blobs present and matching.</summary>
    static List<SavedMount> ReadMounts(List<MountJson?>? raw, ZipArchive zip, ShellOptions o)
    {
        var output = new List<SavedMount>();
        long total = 0, count = 0;
        static string Canonical(string? p, string what) =>
            p is { Length: > 0 and <= 4096 } && p[0] == '/' && !p.Contains('\0') && VirtualFileSystem.Normalize("/", p) == p && p != "/" ? p : throw Bad($"bad {what} {JsonSerializer.Serialize(p)}");
        foreach (var d in raw ?? [])
        {
            if (d == null) throw Bad("a mount is not an object");
            var path = Canonical(d.Path, "mount path");
            if (output.Any(x => path == x.Path || path.StartsWith(x.Path + "/", StringComparison.Ordinal) || x.Path.StartsWith(path + "/", StringComparison.Ordinal)))
                throw Bad($"{path}: mounts overlap");
            var mode = MountManager.ParseMode(d.Mode) ?? throw Bad($"{path}: bad mount mode {JsonSerializer.Serialize(d.Mode)}");
            foreach (var (v, what) in new[] { (d.Type, "type"), (d.Id, "id") })
                if (v == null || v.Length > 256 || v.Any(c => c < 32)) throw Bad($"{path}: bad source {what}");
            var changes = new List<MountChange>();
            foreach (var c in d.Changes ?? [])
            {
                if (++count > o.MaxExtractEntries) throw Bad($"more than {o.MaxExtractEntries} mount changes (MaxExtractEntries)");
                MountChangeKind kind = c?.Kind switch
                {
                    "write" => MountChangeKind.Write, "delete" => MountChangeKind.Delete, "rename" => MountChangeKind.Rename, "mkdir" => MountChangeKind.Mkdir,
                    _ => throw Bad($"{path}: bad mount change"),
                };
                var cp = Canonical(c!.Path, "change path");
                var to = kind == MountChangeKind.Rename ? Canonical(c.To, "change target") : null;
                if (c.Version is { Length: > MountManager.MaxVersion }) throw Bad($"{cp}: bad version");
                byte[]? content = null;
                if (kind == MountChangeKind.Write)
                {
                    total += c.Size;
                    if (c.Size < 0 || total > o.MaxFileSystemBytes) throw Bad($"mount changes hold more than {o.MaxFileSystemBytes} bytes (MaxFileSystemBytes)");
                    content = [];
                    if (c.Size > 0)
                    {
                        var blob = Check(c.Blob, Blob, cp + ": blob");
                        var entry = zip.GetEntry("blobs/" + blob) ?? throw Bad($"{cp}: blob {blob} is missing");
                        var bytes = new MemoryStream();
                        using (var st = entry.Open()) ReadCapped(st, bytes, c.Size, "blob " + blob);
                        content = bytes.ToArray();
                        if (content.Length != c.Size || Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() != blob)
                            throw Bad($"{cp}: blob {blob} doesn't match its size and hash");
                    }
                }
                changes.Add(new(kind, cp, to, kind != MountChangeKind.Mkdir ? c.Version : null, content));
            }
            output.Add(new(path, mode, d.Type!, d.Id!, changes, [], null));
        }
        return output;
    }

    static string Check(string? value, Regex shape, string what) =>
        value != null && shape.IsMatch(value) ? value : throw Bad($"bad {what} {JsonSerializer.Serialize(value)}");

    /// <summary>Copies at most <paramref name="max"/> bytes, failing past it whatever the source claims its size is.</summary>
    internal static void ReadCapped(Stream from, Stream to, long max, string what)
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
    internal static string FormatTime(DateTime t) =>
        (t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>An ISO 8601 date and time (no zone: UTC) as UTC; the same shapes every port reads.</summary>
    internal static DateTime ParseTime(string? s, string what) =>
        s != null && IsoTime.IsMatch(s.Trim()) && DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t : throw Bad($"bad {what} {JsonSerializer.Serialize(s)}");

    static readonly Regex IsoTime = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}[T ][0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,7})?)?(Z|[+-][0-9]{2}:?[0-9]{2})?\z");

    /// <summary>A JSON error's message without .NET type names: what a file's author can act on.</summary>
    internal static string JsonError(JsonException e) => JsonTypeName.Replace(e.Message, "has the wrong type");

    static readonly Regex JsonTypeName = new(@"could not be converted to \S+(?=\. Path:)");

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
        // added for mounted sources: each mount's definition and pending changes, never the source's files
        public List<MountJson?>? Mounts { get; set; }
    }

    sealed class MountJson
    {
        public string? Path { get; set; }
        public string? Mode { get; set; }
        public string? Type { get; set; }
        public string? Id { get; set; }
        public List<ChangeJson?>? Changes { get; set; }

        public static MountJson From(SavedMount d, ZipArchive zip, HashSet<string> written) => new()
        {
            Path = d.Path, Mode = MountManager.ModeName(d.Mode), Type = d.Type, Id = d.Id,
            Changes = [.. d.Held.Concat(d.Changes).Select(c => // every change in a file waits for the host when loaded
            {
                var e = new ChangeJson { Kind = c.KindName, Path = c.Path, To = c.To, Version = c.Version };
                if (c.Kind == MountChangeKind.Write && c.Content is { Length: > 0 } data)
                {
                    e.Size = data.Length;
                    e.Blob = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                    if (written.Add(e.Blob)) using (var s = zip.CreateEntry("blobs/" + e.Blob, CompressionLevel.Fastest).Open()) s.Write(data);
                }
                return e;
            })],
        };
    }

    sealed class ChangeJson
    {
        public string? Kind { get; set; }
        public string? Path { get; set; }
        public string? To { get; set; }
        public string? Version { get; set; }
        public long Size { get; set; }
        public string? Blob { get; set; }
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
        // added in version 2: a symbolic link's target, and for another name of a file or link, the path it was first saved at
        public string? Target { get; set; }
        public string? Link { get; set; }
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
    /// <summary>The mounts' definitions and pending changes (Mounts.cs); their files are not in the snapshot.</summary>
    public List<SavedMount> Mounts { get; set; } = [];

    public bool IsExported(string name) =>
        Attributes.TryGetValue(name, out var a) && a.Contains('x') || Variables.ContainsKey(name) && !Unexported.Contains(name);
}
