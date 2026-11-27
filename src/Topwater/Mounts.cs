// Mounted sources: outside stores of files shown as directories in the machine (docs/mounts.md). See docs/architecture.md.
using System.Security.Cryptography;
using System.Text;
using Dir = Topwater.VirtualFileSystem.Dir;
using FileNode = Topwater.VirtualFileSystem.FileNode;
using Node = Topwater.VirtualFileSystem.Node;

namespace Topwater;

/// <summary>What the agent's changes to a mount do: nothing (read-only), a change set the host applies (staged), or writes
/// applied after every command (live). Saved as "read-only", "staged" and "live".</summary>
public enum MountMode { ReadOnly, Staged, Live }

// ---------- sources ----------

/// <summary>One entry a source lists: <paramref name="Name"/> (one path segment), whether it is a directory, its size in bytes,
/// when it was modified (UTC; null: unknown) and <paramref name="Version"/>, an opaque string that changes when the entry does
/// (an ETag, a revision, a modification time).</summary>
public sealed record MountEntry(string Name, bool IsDir, long Size = 0, DateTime? Modified = null, string Version = "");

/// <summary>A source throws it when the expected version no longer matches what it holds.</summary>
public class MountConflictException(string message) : Exception(message);

/// <summary>Which changes a source can make; None for a read-only source. Staged and live mounts need Write.</summary>
[Flags]
public enum MountCapabilities { None = 0, Write = 1, Delete = 2, Rename = 4, Mkdir = 8 }

/// <summary>
/// An outside store of files (docs/mounts.md). Paths are canonical and absolute within the source: "/" is its root.
/// <see cref="Type"/> and <see cref="Id"/> are saved in snapshots, so they must not hold secrets. A read-only source
/// implements only the listing and reading members; the others default to refusing.
/// </summary>
public interface IMountSource
{
    string Type { get; }
    string Id { get; }
    MountCapabilities Capabilities { get; }
    Task<IReadOnlyList<MountEntry>> ListAsync(string path);
    /// <summary>One entry, or null when missing.</summary>
    Task<MountEntry?> StatAsync(string path);
    /// <summary>A file's bytes; may refuse past <paramref name="maxBytes"/>.</summary>
    Task<byte[]> ReadAsync(string path, long maxBytes);
    /// <summary>Creates the file (<paramref name="expectedVersion"/> null: it must not exist) or replaces it; returns its new version.</summary>
    Task<string> WriteAsync(string path, byte[] data, string? expectedVersion) => throw new NotSupportedException("the source cannot write");
    Task DeleteAsync(string path, string? expectedVersion) => throw new NotSupportedException("the source cannot delete");
    Task RenameAsync(string path, string to, string? expectedVersion) => throw new NotSupportedException("the source cannot rename");
    Task MkdirAsync(string path) => throw new NotSupportedException("the source cannot mkdir");
}

/// <summary>A directory on the host (an Obsidian vault, a folder of notes). Read-only unless <c>writable</c>. Symbolic links
/// in it are left out and never followed; nothing outside the root is reached. The version is the modification time and size.</summary>
public sealed class LocalFolderSource : IMountSource
{
    readonly string _root;

    public LocalFolderSource(string root, bool writable = false, string? id = null)
    {
        var full = Path.GetFullPath(root);
        _root = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        if (!Directory.Exists(_root)) throw new ArgumentException($"{root}: not a directory");
        Id = id ?? Path.GetFileName(_root.TrimEnd(Path.DirectorySeparatorChar));
        Capabilities = writable ? MountCapabilities.Write | MountCapabilities.Delete | MountCapabilities.Rename | MountCapabilities.Mkdir : MountCapabilities.None;
    }

    public string Type => "local-folder";
    public string Id { get; }
    public MountCapabilities Capabilities { get; }

    string Host(string path)
    {
        if (VirtualFileSystem.Normalize("/", path) != path || path.Contains('\0')) throw new ArgumentException($"{path}: not a canonical path");
        // no symbolic link on the way, the last name included: one swapped in after listing could lead out of root
        var cur = _root;
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = Path.Join(cur, seg);
            if (new FileInfo(cur).LinkTarget != null) throw new FileNotFoundException(path);
        }
        return cur;
    }

    static MountEntry Entry(string name, FileSystemInfo f)
    {
        bool dir = f is DirectoryInfo;
        long size = dir ? 0 : ((FileInfo)f).Length;
        var t = f.LastWriteTimeUtc;
        return new(name, dir, size, t, $"{t.Ticks}:{size}");
    }

    // shortcut: a FIFO or device file counts as a file (.NET can't tell them apart without lstat); reading one blocks until the
    // call's time limit, so keep such files out of a mounted folder
    FileSystemInfo? Info(string path)
    {
        string full;
        try { full = Host(path); }
        catch (FileNotFoundException) { return null; }
        return Directory.Exists(full) ? new DirectoryInfo(full) : File.Exists(full) ? new FileInfo(full) : null;
    }

    void Check(string path, string? expected)
    {
        var now = Info(path) is { } f ? Entry("", f).Version : null;
        if (now != expected) throw new MountConflictException($"{path}: version {now ?? "None"}, expected {expected ?? "None"}");
    }

    public Task<IReadOnlyList<MountEntry>> ListAsync(string path)
    {
        var list = new List<MountEntry>();
        foreach (var f in new DirectoryInfo(Host(path)).EnumerateFileSystemInfos())
            if (f.LinkTarget == null) list.Add(Entry(f.Name, f));
        return Task.FromResult<IReadOnlyList<MountEntry>>(list);
    }

    public Task<MountEntry?> StatAsync(string path) =>
        Task.FromResult(Info(path) is { } f ? Entry(path[(path.LastIndexOf('/') + 1)..], f) : null);

    public Task<byte[]> ReadAsync(string path, long maxBytes)
    {
        using var f = new FileStream(Host(path), FileMode.Open, FileAccess.Read);
        if (f.Length > maxBytes) throw new IOException(CommandSpec.Message("mount", "tooLarge"));
        var buffer = new byte[Math.Min(f.Length, maxBytes) + 1];
        int n = 0, got;
        while (n < buffer.Length && (got = f.Read(buffer, n, buffer.Length - n)) > 0) n += got;
        return Task.FromResult(buffer[..n]);
    }

    public Task<string> WriteAsync(string path, byte[] data, string? expectedVersion)
    {
        Check(path, expectedVersion);
        var full = Host(path);
        var tmp = $"{full}.topwater-{System.Environment.ProcessId}.tmp";
        using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write)) f.Write(data);
        File.Move(tmp, full, overwrite: true);
        return Task.FromResult(Entry("", new FileInfo(full)).Version);
    }

    public Task DeleteAsync(string path, string? expectedVersion)
    {
        var st = Info(path);
        if (st is not DirectoryInfo) Check(path, expectedVersion);
        if (st is DirectoryInfo) Directory.Delete(Host(path));
        else if (st == null) throw new FileNotFoundException(path);
        else File.Delete(Host(path));
        return Task.CompletedTask;
    }

    public Task RenameAsync(string path, string to, string? expectedVersion)
    {
        if (Info(to) != null) throw new MountConflictException($"{to}: already exists");
        var st = Info(path);
        if (st is not DirectoryInfo) Check(path, expectedVersion);
        if (st is DirectoryInfo) Directory.Move(Host(path), Host(to));
        else File.Move(Host(path), Host(to));
        return Task.CompletedTask;
    }

    public Task MkdirAsync(string path)
    {
        var full = Host(path);
        if (Directory.Exists(full) || File.Exists(full)) throw new IOException($"{path}: File exists");
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new DirectoryNotFoundException(VirtualFileSystem.GetParent(path));
        Directory.CreateDirectory(full);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Files kept in memory: for tests, and as an example of a source. <c>files</c> maps paths ("notes/a.md"; a trailing "/" for an
/// empty directory) to a string or bytes. <see cref="Set"/> and <see cref="Remove"/> change it from outside; each change takes
/// the next version ("1", "2", ...). <see cref="Hook"/>, called before every operation, can wait or throw to simulate latency,
/// failures and conflicts. <see cref="Calls"/> lists every (op, path) asked.
/// </summary>
public sealed class MemorySource : IMountSource
{
    readonly Dictionary<string, (byte[]? Data, string Version)> _items = new(StringComparer.Ordinal) { ["/"] = (null, "0") }; // Data null: a directory
    int _next;

    public MemorySource(IReadOnlyDictionary<string, object?>? files = null, string id = "memory", DateTime? modified = null)
    {
        Id = id;
        Modified = modified ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var (p, v) in (files ?? new Dictionary<string, object?>()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (p.EndsWith('/')) Mkdirs(VirtualFileSystem.Normalize("/", p));
            else Set(p, v ?? throw new ArgumentException($"{p}: no content"));
        }
    }

    public string Type => "memory";
    public string Id { get; }
    public DateTime Modified { get; }
    public MountCapabilities Capabilities => MountCapabilities.Write | MountCapabilities.Delete | MountCapabilities.Rename | MountCapabilities.Mkdir;
    public Func<string, string, Task>? Hook { get; set; }
    public List<(string Op, string Path)> Calls { get; } = [];

    string NextVersion() => (++_next).ToString(System.Globalization.CultureInfo.InvariantCulture);

    void Mkdirs(string path)
    {
        if (_items.ContainsKey(path)) return;
        Mkdirs(VirtualFileSystem.GetParent(path));
        _items[path] = (null, NextVersion());
    }

    /// <param name="content">A string (stored as UTF-8) or bytes.</param>
    public void Set(string path, object content)
    {
        path = VirtualFileSystem.Normalize("/", path);
        Mkdirs(VirtualFileSystem.GetParent(path));
        _items[path] = (content switch { string s => Encoding.UTF8.GetBytes(s), byte[] b => (byte[])b.Clone(), _ => throw new ArgumentException("content is a string or bytes") }, NextVersion());
    }

    public void Remove(string path)
    {
        path = VirtualFileSystem.Normalize("/", path);
        foreach (var p in _items.Keys.Where(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal)).ToList()) _items.Remove(p);
    }

    /// <summary>Every path below the root and its content (null: a directory), sorted.</summary>
    public SortedDictionary<string, byte[]?> Tree() =>
        new(_items.Where(kv => kv.Key != "/").ToDictionary(kv => kv.Key, kv => kv.Value.Data, StringComparer.Ordinal), StringComparer.Ordinal);

    async Task Enter(string op, string path)
    {
        Calls.Add((op, path));
        if (Hook != null) await Hook(op, path);
    }

    MountEntry? EntryAt(string path) => _items.TryGetValue(path, out var item)
        ? new(path[(path.LastIndexOf('/') + 1)..], item.Data == null, item.Data?.Length ?? 0, Modified, item.Version) : null;

    bool IsDir(string path) => _items.TryGetValue(path, out var item) && item.Data == null;

    void Check(string path, string? expected)
    {
        var now = _items.TryGetValue(path, out var item) ? item.Version : null;
        if (now != expected) throw new MountConflictException($"{path}: version {now ?? "None"}, expected {expected ?? "None"}");
    }

    public async Task<IReadOnlyList<MountEntry>> ListAsync(string path)
    {
        await Enter("list", path);
        if (!IsDir(path)) throw new FileNotFoundException(path);
        var prefix = path.TrimEnd('/') + "/";
        return [.. _items.Keys.Where(p => p != "/" && p.StartsWith(prefix, StringComparison.Ordinal) && !p[prefix.Length..].Contains('/')).Select(p => EntryAt(p)!)];
    }

    public async Task<MountEntry?> StatAsync(string path)
    {
        await Enter("stat", path);
        return EntryAt(path);
    }

    public async Task<byte[]> ReadAsync(string path, long maxBytes)
    {
        await Enter("read", path);
        return _items.TryGetValue(path, out var item) && item.Data != null ? item.Data : throw new FileNotFoundException(path);
    }

    public async Task<string> WriteAsync(string path, byte[] data, string? expectedVersion)
    {
        await Enter("write", path);
        Check(path, expectedVersion);
        var parent = VirtualFileSystem.GetParent(path);
        if (!IsDir(parent)) throw new FileNotFoundException(parent);
        var version = NextVersion();
        _items[path] = ((byte[])data.Clone(), version);
        return version;
    }

    public async Task DeleteAsync(string path, string? expectedVersion)
    {
        await Enter("delete", path);
        if (IsDir(path))
        {
            if (_items.Keys.Any(p => p.StartsWith(path + "/", StringComparison.Ordinal))) throw new IOException($"{path}: Directory not empty");
        }
        else Check(path, expectedVersion);
        if (!_items.Remove(path)) throw new FileNotFoundException(path);
    }

    public async Task RenameAsync(string path, string to, string? expectedVersion)
    {
        await Enter("rename", path);
        if (_items.ContainsKey(to)) throw new MountConflictException($"{to}: already exists");
        if (!IsDir(path)) Check(path, expectedVersion);
        var parent = VirtualFileSystem.GetParent(to);
        if (!IsDir(parent)) throw new FileNotFoundException(parent);
        foreach (var p in _items.Keys.Where(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            _items[to + p[path.Length..]] = _items[p];
            _items.Remove(p);
        }
    }

    public async Task MkdirAsync(string path)
    {
        await Enter("mkdir", path);
        if (_items.ContainsKey(path)) throw new MountConflictException($"{path}: already exists");
        var parent = VirtualFileSystem.GetParent(path);
        if (!IsDir(parent)) throw new FileNotFoundException(parent);
        _items[path] = (null, NextVersion());
    }
}

// ---------- changes ----------

public enum MountChangeKind { Write, Delete, Rename, Mkdir }

/// <summary>One operation that makes the source match the mount: <see cref="Path"/> (and <see cref="To"/> for a rename) within
/// the source; <see cref="Version"/>: what the entry was when read (null: it must not exist); <see cref="Content"/> for a write.</summary>
public sealed record MountChange(MountChangeKind Kind, string Path, string? To = null, string? Version = null, byte[]? Content = null)
{
    public bool Equals(MountChange? other) => other is not null && Kind == other.Kind && Path == other.Path && To == other.To && Version == other.Version
        && (Content == null ? other.Content == null : other.Content != null && Content.AsSpan().SequenceEqual(other.Content));

    public override int GetHashCode() => HashCode.Combine(Kind, Path, To, Version, Content?.Length);

    /// <summary>"write", "delete", "rename" or "mkdir", as files and the mount command name it.</summary>
    internal string KindName => Kind.ToString().ToLowerInvariant();
}

/// <summary>What ApplyChangesAsync did: the changes applied, the one it stopped at (null if none) with the error, and whether
/// that was a conflict (the source changed since it was read).</summary>
public sealed record MountApplyResult(IReadOnlyList<MountChange> Applied, MountChange? Failed = null, string? Error = null, bool Conflict = false);

/// <summary>A mount as <see cref="Shell.Mounts"/> lists it. Error: why it is unavailable (null when it works); Warnings: what
/// its last listing left out.</summary>
public sealed record MountInfo(string Path, MountMode Mode, string Type, string Id, string? Error, IReadOnlyList<string> Warnings);

/// <summary>One question a recording kept and its answer: list (Entries), stat (Entry; null: missing), read (Body), write
/// (Version), delete, rename, mkdir; Error and Conflict when the source refused.</summary>
public sealed record MountExchange(string Mount, string Op, string Path, string? To = null, IReadOnlyList<MountEntry>? Entries = null,
    MountEntry? Entry = null, byte[]? Body = null, string? Version = null, string? Error = null, bool Conflict = false);

/// <summary>A mount as a snapshot keeps it: its definition and pending changes.</summary>
internal sealed record SavedMount(string Path, MountMode Mode, string Type, string Id, IReadOnlyList<MountChange> Changes);

// ---------- mounts ----------

/// <summary>What the mount read from the source at one path: the entry, the node it became, and its content's hash once fetched.</summary>
sealed class MountBase(MountEntry entry, Node? node, byte[]? digest = null)
{
    public MountEntry Entry = entry;
    public Node? Node = node;
    public byte[]? Digest = digest;
    public long? Clock; // the node's change clock right after it was fetched: unchanged since means clean, can be evicted
}

sealed class MountPoint(MountManager mounts, string path, IMountSource? source, MountMode mode)
{
    public readonly MountManager Mounts = mounts;
    public readonly string Path = path;
    public readonly IMountSource? Source = source;
    public readonly MountMode Mode = mode;
    public string Type = source?.Type ?? "", Id = source?.Id ?? "";
    public Dictionary<string, MountBase> Base = new(StringComparer.Ordinal);
    Dictionary<string, string?> _expected = new(StringComparer.Ordinal); // versions a replayed change was made on, which win over Base's
    HashSet<string> _absent = new(StringComparer.Ordinal); // paths a replayed write expects not to exist yet
    public List<MountChange>? Pending; // the changes while not built (after a restore, or unavailable)
    public string? Error;
    public List<string> Warnings = [];
    List<FileNode> _misses = []; // files read before they were fetched: fetched after the command
    Dictionary<FileNode, string> _failed = new(ReferenceEqualityComparer.Instance); // why a file could not be fetched
    List<MountBase> _fetched = []; // in fetch order, for eviction
    public List<MountChange>? Stuck; // live mode: the changes left when applying last failed, not retried until they change

    VirtualFileSystem Fs => Mounts.Shell.Fs;

    public MountInfo Info() => new(Path, Mode, Type, Id, Error, [.. Warnings]);

    public string Machine(string p) => p == "/" ? Path : Path + p;

    // ----- talking to the source -----

    /// <summary>Calls the source, bounded by the call's time limit, and keeps the exchange while recording.</summary>
    async Task<T> Call<T>(string op, string path, Func<IMountSource, Task<T>> run, Func<T, MountExchange> answer, string? to = null)
    {
        var shell = Mounts.Shell;
        var x = new MountExchange(Path, op, path, to);
        Task<T>? task = null;
        T r;
        try
        {
            task = run(Source ?? throw new ShellException(Error ?? "unavailable"));
            var left = shell.CallDeadline - DateTime.UtcNow;
            r = shell.CallDeadline == DateTime.MaxValue || left > TimeSpan.FromDays(40) ? await task : await task.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }
        catch (TimeoutException) when (task is { IsCompleted: false })
        {
            Mounts.Log(x with { Error = Msg("timedOut") });
            throw new ShellException(Msg("timedOut"));
        }
        catch (MountConflictException e)
        {
            Mounts.Log(x with { Error = e.Message is { Length: > 0 } m ? m : "conflict", Conflict = true });
            throw;
        }
        catch (Exception stop) when (stop is OperationCanceledException or ControlFlow) { throw; }
        catch (Exception e)
        {
            Mounts.Log(x with { Error = e.Message is { Length: > 0 } m ? m : e.GetType().Name });
            throw;
        }
        Mounts.Log(answer(r) with { Mount = Path, Op = op, Path = path, To = to });
        return r;
    }

    static readonly MountExchange Blank = new("", "", "");

    public Task<IReadOnlyList<MountEntry>> ListAsync(string p) =>
        Call("list", p, async s => await s.ListAsync(p) ?? [], r => Blank with { Entries = [.. r] });

    public Task<MountEntry?> StatAsync(string p) => Call("stat", p, s => s.StatAsync(p), r => Blank with { Entry = r });

    public Task<byte[]> ReadAsync(string p, long maxBytes) =>
        Call("read", p, async s => await s.ReadAsync(p, maxBytes) ?? throw new ShellException("the source returned something other than bytes"), r => Blank with { Body = r });

    public Task<string> WriteAsync(string p, byte[] data, string? version) => Call("write", p, s => s.WriteAsync(p, data, version), r => Blank with { Version = r });

    public Task<bool> DeleteAsync(string p, string? version) => Call("delete", p, async s => { await s.DeleteAsync(p, version); return true; }, _ => Blank);

    public Task<bool> RenameAsync(string p, string to, string? version) => Call("rename", p, async s => { await s.RenameAsync(p, to, version); return true; }, _ => Blank, to);

    public Task<bool> MkdirAsync(string p) => Call("mkdir", p, async s => { await s.MkdirAsync(p); return true; }, _ => Blank);

    /// <summary>Every entry, parents first, bounded by the entry limit and <see cref="MountManager.MaxDepth"/>; hostile names are skipped.</summary>
    public async Task<List<KeyValuePair<string, MountEntry>>> ListTreeAsync()
    {
        var shell = Mounts.Shell;
        int limit = shell.Options.MaxExtractEntries, skipped = 0;
        var output = new List<KeyValuePair<string, MountEntry>>();
        Warnings = [];
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue(("/", 0));
        while (queue.Count > 0)
        {
            var (d, depth) = queue.Dequeue();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var good = new List<MountEntry>();
            foreach (var e in await ListAsync(d))
            {
                if (!MountManager.Valid(e) || !names.Add(e.Name)) { skipped++; continue; }
                good.Add(e);
            }
            foreach (var e in good.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (output.Count >= limit)
                {
                    Warnings.Add(Msg("truncated", ("dir", Path), ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                    queue.Clear();
                    break;
                }
                var p = MountManager.Join(d, e.Name);
                output.Add(new(p, e));
                if (e.IsDir && depth + 1 < MountManager.MaxDepth) queue.Enqueue((p, depth + 1));
            }
            shell.CheckDeadline();
        }
        if (skipped > 0) Warnings.Add(Msg("skipped", ("dir", Path), ("count", skipped.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        return output;
    }

    // ----- the files in the machine -----

    /// <summary>Replaces the mount point's tree with placeholders for <paramref name="entries"/> (source path, entry; parents first).</summary>
    public void Build(IEnumerable<KeyValuePair<string, MountEntry>> entries)
    {
        bool ro = Mode == MountMode.ReadOnly;
        var owner = ro ? "root" : Mounts.Shell.Options.User;
        int fmode = ro ? Shell.Oct("444") : Shell.Oct("644"), dmode = ro ? Shell.Oct("555") : Shell.Oct("755");
        Dir root;
        using (Fs.AsRoot())
        {
            if (Fs.Exists(Path, follow: false)) Fs.Delete(Path, recursive: true);
            Fs.CreateDirectory(Path, parents: true);
            root = (Dir)Fs.Find(Path)!;
        }
        root.Owner = owner;
        root.Mode = dmode;
        Base = new(StringComparer.Ordinal) { ["/"] = new(new MountEntry("", true), root) };
        _fetched = []; _misses = []; _failed.Clear();
        foreach (var (p, e) in entries)
        {
            if (p == "/") continue;
            if (!Base.TryGetValue(VirtualFileSystem.GetParent(p), out var parent) || !parent.Entry.IsDir) continue;
            var when = e.Modified ?? Fs.Clock();
            Node node = e.IsDir ? new Dir { Modified = when, Owner = owner, Mode = dmode }
                : new FileNode { Modified = when, Owner = owner, Mode = fmode, ReportedSize = e.Size, Loader = Miss };
            ((Dir)parent.Node!).Children[p[(p.LastIndexOf('/') + 1)..]] = node;
            Base[p] = new(e, node);
        }
        Pending = null;
        Error = null;
    }

    /// <summary>An empty mount point the agent can't open; the changes are kept for a later mount.</summary>
    public void Unavailable(string? why)
    {
        using (Fs.AsRoot())
        {
            if (Fs.Exists(Path, follow: false)) Fs.Delete(Path, recursive: true);
            Fs.CreateDirectory(Path, parents: true);
            Fs.Chmod(Path, 0);
        }
        Base = new(StringComparer.Ordinal);
        Error = why;
    }

    /// <summary>Makes <paramref name="changes"/> again on a freshly built tree, remembering the versions they were made on.</summary>
    public void Replay(IReadOnlyList<MountChange> changes)
    {
        _expected = new(StringComparer.Ordinal);
        _absent = new(StringComparer.Ordinal);
        using var _ = Fs.AsRoot();
        foreach (var c in changes)
        {
            var path = Machine(c.Path);
            try
            {
                switch (c.Kind)
                {
                    case MountChangeKind.Write: Fs.CreateDirectory(VirtualFileSystem.GetParent(path), parents: true); MountManager.Store(Fs, path, c.Content ?? []); break;
                    case MountChangeKind.Mkdir: Fs.CreateDirectory(path, parents: true); break;
                    case MountChangeKind.Rename: Fs.Move(path, Machine(c.To!)); break;
                    case MountChangeKind.Delete: Fs.Delete(path, recursive: true); break;
                }
            }
            catch (ShellException e) { Warnings.Add($"{path}: {e.Message}"); }
            if (c.Kind == MountChangeKind.Write && c.Version == null) _absent.Add(c.Path);
            else if (c.Kind != MountChangeKind.Mkdir) _expected[c.Path] = c.Version;
        }
    }

    /// <summary>A placeholder was read before it was fetched: it is fetched after the command.</summary>
    void Miss(FileNode node, string path)
    {
        if (!_failed.TryGetValue(node, out var why))
        {
            if (!_misses.Contains(node)) _misses.Add(node);
            why = Msg("notFetched");
        }
        throw new ShellException($"{path}: {why}");
    }

    public List<FileNode> TakeMisses()
    {
        var m = _misses;
        _misses = [];
        return m;
    }

    public bool HasMisses => _misses.Count > 0;

    /// <summary>Fetches placeholders, making room by evicting clean fetched files; failures are kept for Miss.</summary>
    public async Task FetchAsync(IEnumerable<FileNode> nodes)
    {
        var where = new Dictionary<Node, (string Path, MountBase Base)>(ReferenceEqualityComparer.Instance);
        foreach (var (p, b) in Base) if (b.Node != null) where[b.Node] = (p, b);
        foreach (var node in nodes)
        {
            if (node.Loader == null || !where.TryGetValue(node, out var at)) continue;
            var (p, b) = at;
            long room = Fs.Capacity - Fs.UsedBytes;
            if (b.Entry.Size > room) room += Evict(b.Entry.Size - room);
            byte[] data;
            try
            {
                if (b.Entry.Size > room) throw new ShellException(Msg("tooLarge"));
                data = await ReadAsync(p, room);
                if (data.Length > room) throw new ShellException(Msg("tooLarge"));
            }
            catch (Exception stop) when (stop is OperationCanceledException or ControlFlow) { throw; }
            catch (Exception e)
            {
                _failed[node] = e.Message is { Length: > 0 } m ? m : e.GetType().Name;
                continue;
            }
            if (node.Loader == null) continue; // written while we waited
            MountManager.Fill(Fs, node, data);
            b.Digest = SHA256.HashData(data);
            b.Clock = node.Changed;
            _failed.Remove(node);
            _fetched.Add(b);
        }
    }

    /// <summary>Turns the oldest unchanged fetched files back into placeholders; returns the bytes freed.</summary>
    long Evict(long need)
    {
        long freed = 0;
        while (_fetched.Count > 0 && freed < need)
        {
            var b = _fetched[0];
            _fetched.RemoveAt(0);
            if (b.Node is not FileNode n || n.Loader != null || n.Changed != b.Clock || n.Nlink != 1) continue;
            freed += n.Size;
            Fs.UsedBytes -= n.Size;
            n.Content = "";
            n.Data = null;
            n.Size = 0;
            n.ReportedSize = b.Entry.Size;
            n.Loader = Miss;
            b.Digest = null;
            b.Clock = null;
        }
        return freed;
    }

    /// <summary>The placeholders at or below a machine path (no symbolic link is followed).</summary>
    public List<FileNode> StubsUnder(string path)
    {
        var output = new List<FileNode>();
        var stack = new Stack<Node>();
        if (Fs.Find(path, follow: false) is { } start) stack.Push(start);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is FileNode { Loader: not null } f) output.Add(f);
            else if (n is Dir d) foreach (var child in d.Children.Values.Reverse()) stack.Push(child);
        }
        return output;
    }

    // ----- the change set -----

    /// <summary>The version a base path was read with (or that a replayed change was made on).</summary>
    string? OldVersion(string p) => _expected.TryGetValue(p, out var v) ? v : Base.TryGetValue(p, out var b) ? b.Entry.Version : null;

    public List<MountChange> Changes()
    {
        if (Pending != null) return [.. Pending];
        if (Fs.Find(Path, follow: false) is not Dir root || Base.Count == 0) return [];
        var inTree = MountManager.Ids(root);
        var at = new Dictionary<Node, string>(ReferenceEqualityComparer.Instance);
        foreach (var (p, b) in Base) if (p != "/" && b.Node != null) at[b.Node] = p;
        var gone = Base.Where(kv => kv.Key != "/" && (kv.Value.Node == null || !inTree.Contains(kv.Value.Node))).Select(kv => kv.Key)
            .OrderByDescending(p => p, StringComparer.Ordinal).ToList();
        HashSet<string> dropped = new(StringComparer.Ordinal);
        var visited = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        List<MountChange> first = [], renames = [], mkdirs = [], writes = [];

        string? WriteVersion(string p) => _absent.Contains(p) ? null : OldVersion(p);

        // p is gone and something else stands there now: delete it, and what was below it, first
        void Drop(string p)
        {
            foreach (var q in gone)
                if ((q == p || q.StartsWith(p + "/", StringComparison.Ordinal)) && dropped.Add(q))
                    first.Add(new(MountChangeKind.Delete, q, Version: OldVersion(q)));
        }

        void Walk(Dir d, string path, string? was)
        {
            foreach (var (name, n) in d.Children)
            {
                if (n is not (Dir or FileNode)) continue; // symbolic links are not part of a change set
                var p = MountManager.Join(path, name);
                var bp = at.GetValueOrDefault(n);
                if (!visited.Add(n)) bp = null; // a second hard link
                Base.TryGetValue(p, out var old);
                bool replaced = old != null && (old.Node == null || !inTree.Contains(old.Node));
                if (bp != null)
                {
                    if (bp != (was != null ? MountManager.Join(was, name) : null))
                    {
                        if (replaced) Drop(p);
                        renames.Add(new(MountChangeKind.Rename, bp, To: p, Version: OldVersion(bp)));
                    }
                    var b = Base[bp];
                    if (n is Dir sub) Walk(sub, p, bp);
                    else if (n is FileNode f && f.Loader == null && f.Changed != b.Clock && !SHA256.HashData(f.Raw).AsSpan().SequenceEqual(b.Digest ?? []))
                        writes.Add(new(MountChangeKind.Write, p, Version: WriteVersion(bp), Content: f.Raw));
                    continue;
                }
                if (replaced && (n is FileNode) == !old!.Entry.IsDir)
                {
                    dropped.Add(p); // a file replaced by a file is a write; a directory made again is the same one
                    if (n is Dir sub) Walk(sub, p, p);
                    else if (n is FileNode { Loader: null } f) writes.Add(new(MountChangeKind.Write, p, Version: WriteVersion(p), Content: f.Raw));
                    continue;
                }
                if (replaced) Drop(p);
                if (n is Dir made)
                {
                    mkdirs.Add(new(MountChangeKind.Mkdir, p));
                    Walk(made, p, null);
                }
                else if (n is FileNode { Loader: null } f) // shortcut: a placeholder moved in from another mount is left out
                {
                    var version = Base.ContainsKey(p) || _absent.Contains(p) ? null : _expected.GetValueOrDefault(p);
                    writes.Add(new(MountChangeKind.Write, p, Version: version, Content: f.Raw));
                }
            }
        }

        Walk(root, "/", "/");
        // where a path is once the renames before the deletes are applied (its directory may have been renamed)
        string Moved(string p)
        {
            foreach (var r in renames)
                if (p.StartsWith(r.Path + "/", StringComparison.Ordinal))
                    p = r.To + p[r.Path.Length..];
            return p;
        }

        var deletes = gone.Where(p => !dropped.Contains(p)).Select(p => new MountChange(MountChangeKind.Delete, Moved(p), Version: OldVersion(p)));
        return [.. first, .. renames, .. mkdirs, .. writes, .. deletes];
    }

    public async Task<MountApplyResult> ApplyAsync(IEnumerable<MountChange>? changes)
    {
        var current = Changes();
        var todo = changes == null ? current : changes.Where(current.Contains).ToList();
        if (Pending != null || Error != null) return new([], todo.FirstOrDefault(), Error ?? "the mount is not listed yet");
        var caps = Source?.Capabilities ?? MountCapabilities.None;
        var applied = new List<MountChange>();
        foreach (var c in todo)
        {
            string? version = null;
            try
            {
                if (Mode == MountMode.ReadOnly) throw new ShellException("the mount is read-only");
                if (!caps.HasFlag(MountManager.Needs(c.Kind))) throw new ShellException($"the source cannot {c.KindName}");
                var now = await StatAsync(c.Path);
                if (c.Kind == MountChangeKind.Mkdir || (c.Kind != MountChangeKind.Write && now is { IsDir: true }))
                {
                    if ((now != null) != (c.Kind != MountChangeKind.Mkdir)) // directories: only whether they exist
                        throw new MountConflictException($"{c.Path}: {(now != null ? "already exists" : "missing")}");
                }
                else if (now?.Version != c.Version) throw new MountConflictException($"{c.Path}: version {now?.Version ?? "None"}, expected {c.Version ?? "None"}");
                if (c.Kind == MountChangeKind.Rename && await StatAsync(c.To!) != null) throw new MountConflictException($"{c.To}: already exists");
                switch (c.Kind)
                {
                    case MountChangeKind.Write: version = await WriteAsync(c.Path, c.Content ?? [], c.Version); break;
                    case MountChangeKind.Delete: await DeleteAsync(c.Path, c.Version); break;
                    case MountChangeKind.Rename: await RenameAsync(c.Path, c.To!, c.Version); break;
                    default: await MkdirAsync(c.Path); break;
                }
            }
            catch (MountConflictException e) { return new(applied, c, e.Message is { Length: > 0 } m ? m : "conflict", true); }
            catch (Exception stop) when (stop is OperationCanceledException or ControlFlow) { throw; }
            catch (Exception e) { return new(applied, c, e.Message is { Length: > 0 } m ? m : e.GetType().Name); }
            Applied(c, version);
            applied.Add(c);
        }
        return new(applied);
    }

    /// <summary>What a change applied to the source becomes the mount's new starting point.</summary>
    void Applied(MountChange c, string? version)
    {
        foreach (var p in new[] { c.Path, c.To })
            if (p != null) { _expected.Remove(p); _absent.Remove(p); }
        if (c.Kind == MountChangeKind.Delete) { Base.Remove(c.Path); return; }
        if (c.Kind == MountChangeKind.Rename)
        {
            foreach (var p in Base.Keys.Where(p => p == c.Path || p.StartsWith(c.Path + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList())
            {
                var b = Base[p];
                Base.Remove(p);
                Base[c.To + p[c.Path.Length..]] = b;
            }
            return;
        }
        var node = Fs.Find(Machine(c.Path), follow: false);
        var name = c.Path[(c.Path.LastIndexOf('/') + 1)..];
        if (c.Kind == MountChangeKind.Mkdir)
        {
            Base[c.Path] = new(new MountEntry(name, true, Modified: Fs.Clock()), node);
            return;
        }
        var data = c.Content ?? [];
        var nb = Base[c.Path] = new(new MountEntry(name, false, data.Length, Fs.Clock(), version ?? ""), node, SHA256.HashData(data));
        if (node is FileNode f) nb.Clock = SHA256.HashData(f.Raw).AsSpan().SequenceEqual(nb.Digest) ? f.Changed : null;
    }

    static string Msg(string key, params (string Name, string Value)[] values) => CommandSpec.Message("mount", key, values);
}

/// <summary>A shell's mounts (Shell.MountAsync and friends call these), the sources it knows by (type, id), and, while
/// recording, the source exchanges of the current call.</summary>
sealed class MountManager(Shell shell)
{
    internal const int MaxDepth = 32; // directories below the mount point that are listed
    const int MaxNameBytes = 255;
    internal const int MaxVersion = 1024; // characters in a version

    // commands that never read a file's content: nothing is fetched before them
    static readonly HashSet<string> NoContent = ["ls", "find", "stat", "du", "df", "tree", "cd", "pwd", "test", "[", "mkdir", "rmdir", "rm", "mv", "touch",
        "mount", "chmod", "chown", "chgrp", "ln", "realpath", "readlink", "basename", "dirname", "echo", "printf"];

    public readonly Shell Shell = shell;
    public readonly List<MountPoint> Items = [];
    public Dictionary<(string Type, string Id), IMountSource> Sources = []; // every source mounted, for restore and fork
    public List<MountExchange>? ReplayAnswers; // a replay's recorded exchanges: answered instead of any source
    List<MountExchange> _log = [];

    public void Log(MountExchange x)
    {
        if (Shell.Recorder != null) _log.Add(x);
    }

    public List<MountExchange> TakeLog()
    {
        var log = _log;
        _log = [];
        return log;
    }

    /// <summary>The mount holding a machine path.</summary>
    public MountPoint? Find(string path) => Items.Find(m => path == m.Path || path.StartsWith(m.Path + "/", StringComparison.Ordinal));

    public MountPoint Get(string path) =>
        Items.Find(m => m.Path == VirtualFileSystem.Normalize("/", path)) ?? throw new ArgumentException($"{path}: not a mount point");

    IMountSource? SourceFor(string type, string id, string path) =>
        ReplayAnswers != null ? new ReplaySource(ReplayAnswers, path, type, id) : Sources.GetValueOrDefault((type, id));

    // ----- the host's API -----

    public async Task MountAsync(string path, IMountSource source, MountMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException($"{mode}: the mode is one of read-only, staged, live");
        if (!path.StartsWith('/')) throw new ArgumentException($"{path}: a mount point is an absolute path other than /");
        path = VirtualFileSystem.Normalize("/", path);
        if (path == "/") throw new ArgumentException($"{path}: a mount point is an absolute path other than /");
        if (mode != MountMode.ReadOnly && !source.Capabilities.HasFlag(MountCapabilities.Write))
            throw new ArgumentException($"{path}: the source can't write, so it can only be mounted read-only");
        var old = Items.Find(m => m.Path == path);
        List<MountChange> changes = [];
        if (old != null)
        {
            if (old.Error == null || (old.Type, old.Id) != (source.Type ?? "", source.Id ?? "")) throw new ArgumentException($"{path}: already a mount point; unmount it first");
            changes = old.Changes();
        }
        else
        {
            if (Items.Any(m => path.StartsWith(m.Path + "/", StringComparison.Ordinal) || m.Path.StartsWith(path + "/", StringComparison.Ordinal)))
                throw new ArgumentException($"{path}: inside another mount, or holds one");
            var fs = Shell.Fs;
            using var _ = fs.AsRoot();
            if (fs.Exists(path, follow: false) && !(fs.IsDirectory(path, follow: false) && fs.List(path).Count == 0))
                throw new ArgumentException($"{path}: exists and is not an empty directory");
        }
        var mount = new MountPoint(this, path, source, mode);
        var entries = await mount.ListTreeAsync();
        if (old != null) Items[Items.IndexOf(old)] = mount; else Items.Add(mount);
        Sources[(mount.Type, mount.Id)] = source;
        mount.Build(entries);
        mount.Replay(changes);
    }

    public void Unmount(string path)
    {
        var m = Get(path);
        Items.Remove(m);
        using var _ = Shell.Fs.AsRoot();
        if (Shell.Fs.Exists(m.Path, follow: false)) Shell.Fs.Delete(m.Path, recursive: true);
        Shell.Fs.CreateDirectory(m.Path);
    }

    public async Task RefreshAsync(string path)
    {
        var m = Get(path);
        if (m.Source == null) throw new ShellException(m.Error ?? "unavailable");
        var changes = m.Changes();
        var entries = await m.ListTreeAsync();
        m.Build(entries);
        m.Replay(changes);
    }

    public void Discard(string path)
    {
        var m = Get(path);
        if (m.Pending != null) { m.Pending = []; return; }
        // ordinal order puts every directory before what is below it
        m.Build(m.Base.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new KeyValuePair<string, MountEntry>(kv.Key, kv.Value.Entry)).ToList());
        m.Replay([]);
    }

    // ----- around commands -----

    /// <summary>At the start of a call: lists the mounts a restore brought back.</summary>
    public async Task PrepareAsync()
    {
        foreach (var m in Items.ToList())
        {
            if (m.Pending == null || m.Source == null) continue;
            var changes = m.Pending;
            List<KeyValuePair<string, MountEntry>> entries;
            try { entries = await m.ListTreeAsync(); }
            catch (Exception stop) when (stop is OperationCanceledException or ControlFlow) { throw; }
            catch (Exception e) { m.Error = e.Message is { Length: > 0 } msg ? msg : e.GetType().Name; continue; }
            m.Build(entries);
            m.Replay(changes);
        }
    }

    /// <summary>Fetches what a command is about to read: the paths it names (a directory: everything below), or the working directory.</summary>
    public async Task BeforeAsync(string name, IReadOnlyList<string> argv, string? redirect = null)
    {
        if (NoContent.Contains(name) && redirect == null) return;
        var fs = Shell.Fs;
        var paths = new List<string>();
        foreach (var w in redirect != null ? [redirect] : argv)
        {
            int eq = w.IndexOf('=');
            foreach (var part in w.StartsWith('-') && eq >= 0 ? new[] { w, w[(eq + 1)..] } : [w])
            {
                if (part.Length == 0) continue;
                var p = Shell.ResolvePath(part);
                paths.Add(fs.RealPath(p, out _) ?? p);
            }
        }
        var found = paths.Select(p => (Mount: Find(p), Path: p)).Where(x => x.Mount != null && fs.Exists(x.Path, follow: false)).ToList();
        if (found.Count == 0 && redirect == null)
        {
            var cwd = fs.RealPath(Shell.Cwd, out _) ?? Shell.Cwd;
            if (Find(cwd) is { } m) found.Add((m, cwd));
        }
        foreach (var (m, p) in found)
            if (m!.Error == null && m.Pending == null && fs.Exists(p, follow: false)) await m.FetchAsync(m.StubsUnder(p));
    }

    /// <summary>After a command: fetches what it missed, and in live mode applies its changes.</summary>
    public async Task AfterAsync(TextWriter err)
    {
        foreach (var m in Items.ToList())
        {
            if (m.HasMisses) await m.FetchAsync(m.TakeMisses());
            if (m.Mode != MountMode.Live || m.Error != null || m.Pending != null) continue;
            var changes = m.Changes();
            if (changes.Count == 0 || (m.Stuck != null && changes.SequenceEqual(m.Stuck))) continue; // nothing new since the last attempt failed
            var r = await m.ApplyAsync(null);
            m.Stuck = r.Failed != null ? m.Changes() : null;
            if (r.Failed != null)
            {
                var where = m.Machine(r.Failed.Path);
                err.WriteLine("mount: " + (r.Conflict ? CommandSpec.Message("mount", "conflict", ("path", where))
                    : CommandSpec.Message("mount", "failed", ("path", where), ("reason", r.Error ?? ""))));
            }
        }
    }

    // ----- snapshots and recordings -----

    /// <summary>The filesystem (a copy for a snapshot) without what is inside a mount: a snapshot keeps the mount's definition instead.</summary>
    public VirtualFileSystem Outside(VirtualFileSystem copy)
    {
        using var _ = copy.AsRoot();
        foreach (var m in Items)
            if (copy.Exists(m.Path, follow: false)) copy.Delete(m.Path, recursive: true);
        return copy;
    }

    public List<SavedMount> Capture() => [.. Items.Select(m => new SavedMount(m.Path, m.Mode, m.Type, m.Id, m.Changes()))];

    /// <summary>After the files were replaced: the snapshot's mounts, listed at the start of the next call.</summary>
    public void Restore(IReadOnlyList<SavedMount> saved)
    {
        Items.Clear();
        foreach (var d in saved)
        {
            var source = SourceFor(d.Type, d.Id, d.Path);
            var m = new MountPoint(this, d.Path, source, d.Mode) { Type = d.Type, Id = d.Id };
            Items.Add(m);
            m.Unavailable(source != null ? null : CommandSpec.Message("mount", "unavailable", ("source", $"{m.Type}:{m.Id}")));
            m.Pending = [.. d.Changes];
        }
    }

    /// <summary>What the mounts hold when a recording starts, as the exchanges a replay's first listing and reads answer from.</summary>
    public List<MountExchange> StartExchanges()
    {
        var output = new List<MountExchange>();
        foreach (var m in Items)
        {
            if (m.Pending != null || m.Error != null) continue;
            var paths = m.Base.Keys.Order(StringComparer.Ordinal).ToList();
            var kids = paths.Where(p => m.Base[p].Entry.IsDir).ToDictionary(p => p, _ => new List<MountEntry>(), StringComparer.Ordinal);
            foreach (var p in paths)
                if (p != "/" && kids.TryGetValue(VirtualFileSystem.GetParent(p), out var list)) list.Add(m.Base[p].Entry);
            foreach (var p in paths)
            {
                var b = m.Base[p];
                if (b.Entry.IsDir) output.Add(new(m.Path, "list", p, Entries: kids[p]));
                else if (b.Digest != null && b.Node is FileNode { Loader: null } f && SHA256.HashData(f.Raw).AsSpan().SequenceEqual(b.Digest))
                    output.Add(new(m.Path, "read", p, Body: f.Raw));
            }
        }
        return output;
    }

    // ----- helpers -----

    internal static string Join(string d, string name) => d == "/" ? "/" + name : d + "/" + name;

    internal static MountCapabilities Needs(MountChangeKind kind) => kind switch
    {
        MountChangeKind.Write => MountCapabilities.Write,
        MountChangeKind.Delete => MountCapabilities.Delete,
        MountChangeKind.Rename => MountCapabilities.Rename,
        _ => MountCapabilities.Mkdir,
    };

    internal static string ModeName(MountMode m) => m switch { MountMode.ReadOnly => "read-only", MountMode.Staged => "staged", _ => "live" };

    internal static MountMode? ParseMode(string? s) => s switch { "read-only" => MountMode.ReadOnly, "staged" => MountMode.Staged, "live" => MountMode.Live, _ => null };

    /// <summary>An entry a source listed that can be a file: a plain name, sane size and version.</summary>
    internal static bool Valid(MountEntry? e) =>
        e is { Name: { } n, Version: { } v } && n is not ("" or "." or "..") && !n.Contains('/') && !n.Contains('\0')
        && Encoding.UTF8.GetByteCount(n) <= MaxNameBytes && e.Size >= 0 && v.Length <= MaxVersion;

    internal static HashSet<Node> Ids(Dir root)
    {
        var output = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            output.Add(n);
            if (n is Dir d) foreach (var c in d.Children.Values) stack.Push(c);
        }
        return output;
    }

    static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

    /// <summary>Puts fetched content into a placeholder without marking it changed by the agent.</summary>
    internal static void Fill(VirtualFileSystem fs, FileNode node, byte[] data)
    {
        try { node.Content = Strict.GetString(data); node.Data = null; }
        catch (DecoderFallbackException) { node.Content = ""; node.Data = data; }
        node.Size = data.Length;
        node.Loader = null;
        node.ReportedSize = null;
        fs.UsedBytes += data.Length;
    }

    internal static void Store(VirtualFileSystem fs, string path, byte[] data)
    {
        string text;
        try { text = Strict.GetString(data); }
        catch (DecoderFallbackException) { fs.WriteAllBytes(path, data); return; }
        fs.WriteAllText(path, text);
    }
}

/// <summary>Answers a replay's questions from the recorded exchanges of one mount, never the real source.</summary>
sealed class ReplaySource : IMountSource
{
    readonly Dictionary<(string Op, string Path, string? To), List<MountExchange>> _answers = [];

    public ReplaySource(IEnumerable<MountExchange> exchanges, string mount, string type, string id)
    {
        Type = type;
        Id = id;
        foreach (var x in exchanges)
        {
            if (x.Mount != mount) continue;
            if (!_answers.TryGetValue((x.Op, x.Path, x.To), out var q)) _answers[(x.Op, x.Path, x.To)] = q = [];
            q.Add(x);
        }
    }

    public string Type { get; }
    public string Id { get; }
    public MountCapabilities Capabilities => MountCapabilities.Write | MountCapabilities.Delete | MountCapabilities.Rename | MountCapabilities.Mkdir;

    MountExchange Answer(string op, string path, string? to = null)
    {
        if (!_answers.TryGetValue((op, path, to), out var q) || q.Count == 0)
            throw new ShellException(CommandSpec.Message("mount", "replay", ("op", op), ("path", path)));
        var x = q[0];
        if (q.Count > 1) q.RemoveAt(0); // the last answer repeats
        if (x.Conflict) throw new MountConflictException(x.Error ?? "conflict");
        if (x.Error != null) throw new ShellException(x.Error);
        return x;
    }

    public Task<IReadOnlyList<MountEntry>> ListAsync(string path) => Task.FromResult<IReadOnlyList<MountEntry>>([.. Answer("list", path).Entries ?? []]);
    public Task<MountEntry?> StatAsync(string path) => Task.FromResult(Answer("stat", path).Entry);
    public Task<byte[]> ReadAsync(string path, long maxBytes) => Task.FromResult(Answer("read", path).Body ?? []);
    public Task<string> WriteAsync(string path, byte[] data, string? expectedVersion) => Task.FromResult(Answer("write", path).Version ?? "");
    public Task DeleteAsync(string path, string? expectedVersion) { Answer("delete", path); return Task.CompletedTask; }
    public Task RenameAsync(string path, string to, string? expectedVersion) { Answer("rename", path, to); return Task.CompletedTask; }
    public Task MkdirAsync(string path) { Answer("mkdir", path); return Task.CompletedTask; }
}
