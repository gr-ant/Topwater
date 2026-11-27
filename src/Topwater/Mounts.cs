// Mounted sources: outside stores of files shown as directories in the machine (docs/mounts.md). See docs/architecture.md.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Dir = Topwater.VirtualFileSystem.Dir;
using FileNode = Topwater.VirtualFileSystem.FileNode;
using Node = Topwater.VirtualFileSystem.Node;

namespace Topwater;

/// <summary>What the agent's changes to a mount do: nothing (read-only), a change set the host applies (staged), or writes
/// applied after every command (live). Saved as "read-only", "staged" and "live".</summary>
public enum MountMode { ReadOnly, Staged, Live }

// ---------- sources ----------

/// <summary>One entry a source lists: <paramref name="Name"/> (one path segment), whether it is a directory, its size in bytes
/// (null: not known until read), when it was modified (UTC; null: unknown) and <paramref name="Version"/>, an opaque string that
/// changes when the entry does (an ETag, a revision, a modification time).</summary>
public sealed record MountEntry(string Name, bool IsDir, long? Size = 0, DateTime? Modified = null, string Version = "");

/// <summary>A source throws it when the expected version no longer matches what it holds.</summary>
public class MountConflictException(string message) : Exception(message);

/// <summary>A source throws it with a message the agent may see ("page.md: the page is locked"). Anything else a source throws
/// is shown to the agent as "the source failed"; its text goes to the host only.</summary>
public class MountSourceException(string message) : Exception(message);

/// <summary>Which changes a source can make; None for a read-only source. Staged and live mounts need Write.</summary>
[Flags]
public enum MountCapabilities { None = 0, Write = 1, Delete = 2, Rename = 4, Mkdir = 8 }

/// <summary>
/// An outside store of files (docs/mounts.md). Paths are canonical and absolute within the source: "/" is its root.
/// <see cref="Type"/> and <see cref="Id"/> are saved in snapshots, so they must not hold secrets. Every method gets a token
/// that is cancelled at the call's time limit. A read-only source implements only the listing and reading members; the
/// others default to refusing.
/// </summary>
public interface IMountSource
{
    string Type { get; }
    string Id { get; }
    MountCapabilities Capabilities { get; }
    /// <summary>A directory's entries; more than <paramref name="maxEntries"/> are not used, so a source that pages may stop there.</summary>
    Task<IReadOnlyList<MountEntry>> ListAsync(string path, int maxEntries, CancellationToken cancellationToken);
    /// <summary>One entry, or null when missing.</summary>
    Task<MountEntry?> StatAsync(string path, CancellationToken cancellationToken);
    /// <summary>A file's bytes; may refuse past <paramref name="maxBytes"/>.</summary>
    Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken);
    /// <summary>Creates the file (<paramref name="expectedVersion"/> null: it must not exist) or replaces it; returns its new version.</summary>
    Task<string> WriteAsync(string path, byte[] data, string? expectedVersion, CancellationToken cancellationToken) => throw new NotSupportedException("the source cannot write");
    Task DeleteAsync(string path, string? expectedVersion, CancellationToken cancellationToken) => throw new NotSupportedException("the source cannot delete");
    /// <summary>Returns the entry's new version (null: unchanged).</summary>
    Task<string?> RenameAsync(string path, string to, string? expectedVersion, CancellationToken cancellationToken) => throw new NotSupportedException("the source cannot rename");
    /// <summary>Returns the new directory's version (null: none).</summary>
    Task<string?> MkdirAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException("the source cannot mkdir");
}

/// <summary>
/// A directory on the host (an Obsidian vault, a folder of notes). Read-only unless <c>writable</c>. Only regular files and
/// directories are listed: symbolic links, FIFOs and devices are left out, never followed or opened (a file is opened without
/// following a link and without blocking). Names with <c>\</c> (and on Windows <c>:</c>, reserved device names and a trailing
/// dot or space) are refused. Each name is checked for a link just before use, which leaves a small window if someone else
/// changes the folder meanwhile (docs/mounts.md). Errors name paths within the folder, never the host's path. The version is
/// the modification time and size.
/// </summary>
public sealed class LocalFolderSource : IMountSource
{
    readonly string _root;
    static readonly HashSet<string> WindowsReserved = ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })];

    public LocalFolderSource(string root, bool writable = false, string? id = null)
    {
        var full = Path.GetFullPath(root);
        _root = (new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full).TrimEnd(Path.DirectorySeparatorChar);
        if (_root.Length == 0) _root = Path.DirectorySeparatorChar.ToString();
        if (!Directory.Exists(_root)) throw new ArgumentException($"{root}: not a directory");
        Id = id ?? Path.GetFileName(_root);
        Capabilities = writable ? MountCapabilities.Write | MountCapabilities.Delete | MountCapabilities.Rename | MountCapabilities.Mkdir : MountCapabilities.None;
    }

    public string Type => "local-folder";
    public string Id { get; }
    public MountCapabilities Capabilities { get; }

    static string[] Names(string path)
    {
        if (VirtualFileSystem.Normalize("/", path) != path || path.Contains('\0')) throw new MountSourceException($"{path}: not a canonical path");
        var names = path == "/" ? [] : path.Split('/')[1..];
        foreach (var n in names)
            if (n.Contains('\\') || (OperatingSystem.IsWindows() && (n.Contains(':') || n != n.TrimEnd(' ', '.') || WindowsReserved.Contains(n.Split('.')[0].ToUpperInvariant()))))
                throw new MountSourceException($"{path}: not a name a file can have here");
        return names;
    }

    /// <summary>The host path of <paramref name="names"/>: no symbolic link on the way (the last name too when <paramref name="last"/>),
    /// and never outside the root.</summary>
    string Host(string path, string[] names, bool last)
    {
        var cur = _root;
        for (int i = 0; i < names.Length; i++)
        {
            cur = Path.Join(cur, names[i]);
            if ((last || i < names.Length - 1) && new FileInfo(cur).LinkTarget != null) throw new MountSourceException($"{path}: {Errno.ENOENT}");
        }
        var full = Path.GetFullPath(cur);
        if (full != _root && !full.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new MountSourceException($"{path}: {Errno.ENOENT}");
        return full;
    }

    static class Errno
    {
        public const string ENOENT = "No such file or directory";
    }

    /// <summary>What the agent may see of a host error: the path within the folder and the system's reason, never the host path.</summary>
    static MountSourceException Fail(string path, Exception e) => e switch
    {
        MountSourceException m => m,
        UnauthorizedAccessException => new($"{path}: Permission denied"),
        FileNotFoundException or DirectoryNotFoundException => new($"{path}: {Errno.ENOENT}"),
        IOException { HResult: > 0 and < 4096 } io when !OperatingSystem.IsWindows() => new($"{path}: {Marshal.GetPInvokeErrorMessage(io.HResult)}"),
        _ => new($"{path}: failed"),
    };

    T Guard<T>(string path, Func<T> run)
    {
        try { return run(); }
        catch (MountConflictException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or MountSourceException or ArgumentException or NotSupportedException) { throw Fail(path, e); }
    }

    static MountEntry Entry(string name, FileSystemInfo f)
    {
        bool dir = f is DirectoryInfo;
        long size = dir ? 0 : ((FileInfo)f).Length;
        var t = f.LastWriteTimeUtc;
        return new(name, dir, size, t, $"{t.Ticks}:{size}");
    }

    /// <summary>A regular file or directory at a host path (not a link, FIFO or device), or null.</summary>
    static FileSystemInfo? Plain(string full)
    {
        var fi = new FileInfo(full);
        if (fi.LinkTarget != null) return null;
        var type = Posix.Type(full);
        if (type == 0 || type is int t && t != Posix.Regular && t != Posix.Directory) return null;
        return Directory.Exists(full) ? new DirectoryInfo(full) : fi.Exists ? fi : null;
    }

    FileSystemInfo? Check(string path, string full, string? expected)
    {
        var st = Plain(full);
        var now = st is null ? null : Entry("", st).Version;
        if (now != expected && !(st is DirectoryInfo && expected != null))
            throw new MountConflictException($"{path}: version {now ?? "None"}, expected {expected ?? "None"}");
        return st;
    }

    public Task<IReadOnlyList<MountEntry>> ListAsync(string path, int maxEntries, CancellationToken cancellationToken) => Task.FromResult(Guard(path, () =>
    {
        var output = new List<MountEntry>();
        foreach (var f in new DirectoryInfo(Host(path, Names(path), true)).EnumerateFileSystemInfos().Take(maxEntries * 2 + 64)) // links and devices are skipped, not counted
        {
            if (Plain(f.FullName) is not { } plain) continue;
            output.Add(Entry(f.Name, plain));
            if (output.Count >= maxEntries) break;
        }
        return (IReadOnlyList<MountEntry>)output;
    }));

    public Task<MountEntry?> StatAsync(string path, CancellationToken cancellationToken) => Task.FromResult(path == "/" ? new MountEntry("", true) : Guard(path, () =>
        Plain(Host(path, Names(path), false)) is { } f ? new MountEntry(path[(path.LastIndexOf('/') + 1)..], f is DirectoryInfo, Entry("", f).Size, f.LastWriteTimeUtc, Entry("", f).Version) : null));

    public Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken) => Task.FromResult(Guard(path, () =>
    {
        var full = Host(path, Names(path), false);
        // never blocks on a FIFO, never follows a link; then only a regular file is read
        using var stream = Posix.OpenRead(full, path) is { } handle ? new FileStream(handle, FileAccess.Read, 1)
            : Plain(full) is FileInfo ? new FileStream(full, FileMode.Open, FileAccess.Read) : throw new MountSourceException($"{path}: not a regular file");
        if (!stream.CanSeek) throw new MountSourceException($"{path}: not a regular file");
        if (stream.Length > maxBytes) throw new MountSourceException(CommandSpec.Message("mount", "tooLarge"));
        var buffer = new byte[Math.Min(stream.Length, maxBytes) + 1];
        int n = 0, got;
        while (n < buffer.Length && (got = stream.Read(buffer, n, buffer.Length - n)) > 0) n += got;
        return buffer[..n];
    }));

    public Task<string> WriteAsync(string path, byte[] data, string? expectedVersion, CancellationToken cancellationToken) => Task.FromResult(Guard(path, () =>
    {
        var names = Names(path);
        if (names.Length == 0) throw new MountSourceException("/: the root itself");
        var full = Host(path, names, true);
        Check(path, full, expectedVersion);
        var tmp = Path.Join(Path.GetDirectoryName(full), $".tw-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}.tmp");
        try
        {
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write)) f.Write(data); // exclusive: never through a link
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return Entry("", new FileInfo(full)).Version;
    }));

    public Task DeleteAsync(string path, string? expectedVersion, CancellationToken cancellationToken) => Task.FromResult(Guard(path, () =>
    {
        var full = Host(path, Names(path), true);
        var st = Check(path, full, expectedVersion);
        if (st is DirectoryInfo) Directory.Delete(full);
        else if (st is null) throw new MountSourceException($"{path}: {Errno.ENOENT}");
        else File.Delete(full);
        return true;
    }));

    public Task<string?> RenameAsync(string path, string to, string? expectedVersion, CancellationToken cancellationToken) => Task.FromResult(Guard(path, () =>
    {
        var from = Host(path, Names(path), true);
        var target = Host(to, Names(to), true);
        if (Plain(target) != null || new FileInfo(target).LinkTarget != null) throw new MountConflictException($"{to}: already exists");
        var st = Check(path, from, expectedVersion);
        if (st is DirectoryInfo) Directory.Move(from, target); else File.Move(from, target);
        return (string?)null;
    }));

    public Task<string?> MkdirAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Guard(path, () =>
    {
        var full = Host(path, Names(path), true);
        if (Directory.Exists(full) || File.Exists(full)) throw new MountSourceException($"{path}: File exists");
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new MountSourceException($"{path}: {Errno.ENOENT}");
        Directory.CreateDirectory(full);
        return (string?)null;
    }));

    /// <summary>The few POSIX calls .NET doesn't offer: the type of a file (lstat) and opening one without following a link or
    /// blocking. Linux (x64, arm64) and macOS (arm64); elsewhere null, and the managed checks above stand alone.</summary>
    static class Posix
    {
        public const int Regular = 0x8000, Directory = 0x4000;
        static readonly bool Linux = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;
        static readonly bool Mac = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        static readonly bool Arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        [DllImport("libc", EntryPoint = "lstat", SetLastError = true)] static extern int LStat(string path, byte[] buf);
        [DllImport("libc", EntryPoint = "__lxstat", SetLastError = true)] static extern int LXStat(int version, string path, byte[] buf);
        [DllImport("libc", EntryPoint = "open", SetLastError = true)] static extern int Open(string path, int flags);

        static int ModeOf(byte[] buf) => Mac ? BitConverter.ToUInt16(buf, 4) : BitConverter.ToInt32(buf, Arm ? 16 : 24);

        /// <summary>The S_IFMT bits of a path, not following a last link; 0 when missing; null when this platform can't tell.</summary>
        public static int? Type(string path)
        {
            if (!Linux && !Mac) return null;
            var buf = new byte[512];
            int r;
            try { r = LStat(path, buf); }
            catch (EntryPointNotFoundException) { r = LXStat(Arm ? 0 : 1, path, buf); } // glibc before 2.33
            return r != 0 ? 0 : ModeOf(buf) & 0xF000;
        }

        /// <summary>A file opened read-only without following a link or blocking (O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC), or null
        /// where this platform isn't covered.</summary>
        public static SafeFileHandle? OpenRead(string full, string path)
        {
            if (!Linux && !Mac) return null;
            int flags = Mac ? 0x100 | 0x4 | 0x1000000 : (Arm ? 0x8000 : 0x20000) | 0x800 | 0x80000;
            int fd = Open(full, flags);
            if (fd < 0) throw new MountSourceException($"{path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
            var handle = new SafeFileHandle(fd, ownsHandle: true);
            // shortcut: the type is read by path (lstat) after the open, not from the descriptor (fstat, whose struct layout varies);
            // the open itself followed no link, and the caller also refuses a stream that can't seek
            if (Type(full) is int t && t != Regular)
            {
                handle.Dispose();
                throw new MountSourceException($"{path}: not a regular file");
            }
            return handle;
        }
    }
}

/// <summary>
/// Files kept in memory: for tests, and as a test double. <c>files</c> maps paths ("notes/a.md"; a trailing "/" for an
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
    public MountCapabilities Capabilities { get; set; } = MountCapabilities.Write | MountCapabilities.Delete | MountCapabilities.Rename | MountCapabilities.Mkdir;
    public Func<string, string, Task>? Hook { get; set; }
    public List<(string Op, string Path)> Calls { get; } = [];

    string NextVersion() => Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);

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
        lock (Calls) Calls.Add((op, path));
        if (Hook != null) await Hook(op, path);
    }

    MountEntry? EntryAt(string path) => _items.TryGetValue(path, out var item)
        ? new(path[(path.LastIndexOf('/') + 1)..], item.Data == null, item.Data?.Length ?? 0, Modified, item.Version) : null;

    bool IsDir(string path) => _items.TryGetValue(path, out var item) && item.Data == null;

    static MountSourceException Missing(string path) => new($"{path}: No such file or directory");

    void Check(string path, string? expected)
    {
        var now = _items.TryGetValue(path, out var item) ? item.Version : null;
        if (now != expected) throw new MountConflictException($"{path}: version {now ?? "None"}, expected {expected ?? "None"}");
    }

    public async Task<IReadOnlyList<MountEntry>> ListAsync(string path, int maxEntries, CancellationToken cancellationToken)
    {
        await Enter("list", path);
        if (!IsDir(path)) throw Missing(path);
        var prefix = path.TrimEnd('/') + "/";
        return [.. _items.Keys.Where(p => p != "/" && p.StartsWith(prefix, StringComparison.Ordinal) && !p[prefix.Length..].Contains('/')).Take(maxEntries).Select(p => EntryAt(p)!)];
    }

    public async Task<MountEntry?> StatAsync(string path, CancellationToken cancellationToken)
    {
        await Enter("stat", path);
        return EntryAt(path);
    }

    public async Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        await Enter("read", path);
        return _items.TryGetValue(path, out var item) && item.Data != null ? item.Data : throw Missing(path);
    }

    public async Task<string> WriteAsync(string path, byte[] data, string? expectedVersion, CancellationToken cancellationToken)
    {
        await Enter("write", path);
        Check(path, expectedVersion);
        var parent = VirtualFileSystem.GetParent(path);
        if (!IsDir(parent)) throw Missing(parent);
        var version = NextVersion();
        _items[path] = ((byte[])data.Clone(), version);
        return version;
    }

    public async Task DeleteAsync(string path, string? expectedVersion, CancellationToken cancellationToken)
    {
        await Enter("delete", path);
        if (IsDir(path))
        {
            if (_items.Keys.Any(p => p.StartsWith(path + "/", StringComparison.Ordinal))) throw new MountSourceException($"{path}: Directory not empty");
        }
        else Check(path, expectedVersion);
        if (!_items.Remove(path)) throw Missing(path);
    }

    public async Task<string?> RenameAsync(string path, string to, string? expectedVersion, CancellationToken cancellationToken)
    {
        await Enter("rename", path);
        if (_items.ContainsKey(to)) throw new MountConflictException($"{to}: already exists");
        if (!IsDir(path)) Check(path, expectedVersion);
        var parent = VirtualFileSystem.GetParent(to);
        if (!IsDir(parent)) throw Missing(parent);
        foreach (var p in _items.Keys.Where(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            _items[to + p[path.Length..]] = _items[p];
            _items.Remove(p);
        }
        return null;
    }

    public async Task<string?> MkdirAsync(string path, CancellationToken cancellationToken)
    {
        await Enter("mkdir", path);
        if (_items.ContainsKey(path)) throw new MountConflictException($"{path}: already exists");
        var parent = VirtualFileSystem.GetParent(path);
        if (!IsDir(parent)) throw Missing(parent);
        var version = NextVersion();
        _items[path] = (null, version);
        return version;
    }
}

// ---------- changes ----------

public enum MountChangeKind { Write, Delete, Rename, Mkdir }

/// <summary>One operation that makes the source match the mount: <see cref="Path"/> (and <see cref="To"/> for a rename) within
/// the source; <see cref="Version"/>: what the entry was when read (null: it must not exist); <see cref="Content"/> for a write.
/// ApplyChangesAsync matches changes by value, content included.</summary>
public sealed record MountChange(MountChangeKind Kind, string Path, string? To = null, string? Version = null, byte[]? Content = null)
{
    public bool Equals(MountChange? other) => other is not null && Kind == other.Kind && Path == other.Path && To == other.To && Version == other.Version
        && (Content == null ? other.Content == null : other.Content != null && Content.AsSpan().SequenceEqual(other.Content));

    public override int GetHashCode() => HashCode.Combine(Kind, Path, To, Version, Content?.Length);

    /// <summary>"write", "delete", "rename" or "mkdir", as files and the mount command name it.</summary>
    internal string KindName => Kind.ToString().ToLowerInvariant();
}

/// <summary>What ApplyChangesAsync did: the changes applied, the one it stopped at (null if none) with the error (the source's
/// full text: for the host, not the agent), whether that was a conflict (the source changed since it was read), and in
/// <see cref="NotTried"/> the changes after it.</summary>
public sealed record MountApplyResult(IReadOnlyList<MountChange> Applied, MountChange? Failed = null, string? Error = null, bool Conflict = false)
{
    public IReadOnlyList<MountChange> NotTried { get; init; } = [];
}

/// <summary>A mount as <see cref="Shell.Mounts"/> lists it. Error: why it is unavailable or its listing failed (the source's
/// full text; null when it works); Warnings: what its listings left out; Held: how many changes from a loaded snapshot wait
/// for <see cref="Shell.AcceptSnapshotChangesAsync"/>.</summary>
public sealed record MountInfo(string Path, MountMode Mode, string Type, string Id, string? Error, IReadOnlyList<string> Warnings, int Held = 0);

/// <summary>One question a recording kept and its answer: list (Entries), stat (Entry; null: missing), read (Body), write,
/// rename and mkdir (Version), delete; Error (as the agent may see it) and Conflict when the source refused.</summary>
public sealed record MountExchange(string Mount, string Op, string Path, string? To = null, IReadOnlyList<MountEntry>? Entries = null,
    MountEntry? Entry = null, byte[]? Body = null, string? Version = null, string? Error = null, bool Conflict = false);

/// <summary>A mount as a snapshot keeps it: its definition, the agent's changes, a loaded snapshot's held changes and (in
/// memory only, never saved) its listing, so restoring it in this process needs no source call.</summary>
internal sealed record SavedMount(string Path, MountMode Mode, string Type, string Id, IReadOnlyList<MountChange> Changes,
    IReadOnlyList<MountChange> Held, IReadOnlyList<KeyValuePair<string, (MountEntry Entry, bool Unlisted)>>? Listing);

// ---------- mounts ----------

/// <summary>What the mount read from the source at one path: the entry, the node it became, and its content's hash once fetched.</summary>
sealed class MountBase(MountEntry entry, Node? node, byte[]? digest = null)
{
    public MountEntry Entry = entry;
    public Node? Node = node;
    public byte[]? Digest = digest;
    public long? Clock; // the node's change clock right after it was fetched: unchanged since means clean, can be evicted
}

/// <summary>A source call that failed: <see cref="Safe"/> for the agent and recordings, <see cref="Full"/> for the host.</summary>
sealed class SourceFailure(string safe, string full) : Exception(safe)
{
    public string Safe { get; } = safe;
    public string Full { get; } = full;
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
    public bool Stale; // restored from a file: listed at the start of the next call
    public List<MountChange> Held = []; // changes not in the tree: from a loaded snapshot (quarantined), or while unavailable
    public string? Error; // the full text, for the host
    public string? Shown; // what the agent sees of it
    public List<string> Warnings = [];
    List<Node> _misses = []; // files and directories used before they were fetched or listed
    readonly Dictionary<FileNode, string> _failed = new(ReferenceEqualityComparer.Instance); // why a file could not be fetched (safe to show)
    List<MountBase> _fetched = []; // in fetch order, for eviction
    public List<MountChange> Stuck = []; // live mode: changes that failed, not tried again until they change
    public bool Busy; // applying: a pipeline's other stage leaves it to the end of the call
    public bool Trusted = true; // held changes may be replayed at the next listing (a replay's), not only by the host
    public string? LastSafe; // what the agent may see of the last apply's error

    VirtualFileSystem Fs => Mounts.Shell.Fs;

    public bool Available => Error == null && !Stale && Source != null;

    public MountInfo Info() => new(Path, Mode, Type, Id, Error, [.. Warnings], Held.Count);

    public string Machine(string p) => p == "/" ? Path : Path + p;

    public MountCapabilities Capabilities() => Source?.Capabilities ?? MountCapabilities.None;

    bool Current => Mounts.Items.Find(m => m.Path == Path) == this;

    // ----- talking to the source -----

    /// <summary>Calls the source, bounded by the call's time limit (and cancelled through its token), and keeps the exchange
    /// while recording. Errors come out as <see cref="MountConflictException"/>, or <see cref="SourceFailure"/>.</summary>
    async Task<T> Call<T>(string op, string path, Func<IMountSource, CancellationToken, Task<T>> run, Func<T, MountExchange> answer, string? to = null)
    {
        var shell = Mounts.Shell;
        var x = new MountExchange(Path, op, path, to);
        var left = shell.CallDeadline - DateTime.UtcNow;
        bool bounded = shell.CallDeadline != DateTime.MaxValue && left < TimeSpan.FromDays(40);
        using var cts = bounded ? new CancellationTokenSource(left > TimeSpan.Zero ? left : TimeSpan.Zero) : new CancellationTokenSource();
        T r;
        try
        {
            var task = run(Source ?? throw new SourceFailure(Shown ?? "unavailable", Error ?? "unavailable"), cts.Token);
            r = bounded ? await task.WaitAsync(cts.Token) : await task;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Mounts.Log(x with { Error = Msg("timedOut") });
            throw new SourceFailure(Msg("timedOut"), Msg("timedOut"));
        }
        catch (MountConflictException e)
        {
            Mounts.Log(x with { Error = e.Message is { Length: > 0 } m ? m : "conflict", Conflict = true });
            throw;
        }
        catch (Exception e) when (e is OperationCanceledException or ControlFlow) { throw; }
        catch (Exception e)
        {
            var safe = MountManager.SafeText(e);
            Mounts.Log(x with { Error = safe });
            throw new SourceFailure(safe, e is SourceFailure f ? f.Full : e.Message is { Length: > 0 } m ? m : e.GetType().Name);
        }
        var logged = answer(r);
        Mounts.Log(logged with { Mount = Path, Op = op, Path = path, To = to });
        return r;
    }

    static readonly MountExchange Blank = new("", "", "");

    static string? Clip(string? v) => v is { Length: > MountManager.MaxVersion } ? v[..MountManager.MaxVersion] : v;

    public Task<IReadOnlyList<MountEntry>> ListAsync(string p, int maxEntries) =>
        Call("list", p, async (s, ct) => (IReadOnlyList<MountEntry>)[.. (await s.ListAsync(p, maxEntries, ct) ?? []).Take(maxEntries + 64)], // a few bad names may be skipped; never read without end
            r => Blank with { Entries = r });

    public Task<MountEntry?> StatAsync(string p) => Call("stat", p, (s, ct) => s.StatAsync(p, ct), r => Blank with { Entry = r });

    public Task<byte[]> ReadAsync(string p, long maxBytes) =>
        Call("read", p, async (s, ct) => await s.ReadAsync(p, maxBytes, ct) ?? throw new SourceFailure(Msg("notBytes"), Msg("notBytes")), r => Blank with { Body = r });

    public Task<string?> WriteAsync(string p, byte[] data, string? version) =>
        Call("write", p, async (s, ct) => Clip(await s.WriteAsync(p, data, version, ct)), r => Blank with { Version = r });

    public Task<bool> DeleteAsync(string p, string? version) => Call("delete", p, async (s, ct) => { await s.DeleteAsync(p, version, ct); return true; }, _ => Blank);

    public Task<string?> RenameAsync(string p, string to, string? version) =>
        Call("rename", p, async (s, ct) => Clip(await s.RenameAsync(p, to, version, ct)), r => Blank with { Version = r }, to);

    public Task<string?> MkdirAsync(string p) => Call("mkdir", p, async (s, ct) => Clip(await s.MkdirAsync(p, ct)), r => Blank with { Version = r });

    /// <summary>Lists the unlisted directories in <paramref name="queue"/> (source path, node, depth), breadth first, then (deep)
    /// what they hold; no new directory is started past <paramref name="budget"/> entries or <see cref="MountManager.ListSeconds"/>,
    /// and what is left stays "not listed yet".</summary>
    public async Task GrowAsync(List<(string Path, Dir Node, int Depth)> queue, int budget, bool deep = true)
    {
        var shell = Mounts.Shell;
        int limit = shell.Options.MaxExtractEntries, seen = 0;
        var clock = Stopwatch.StartNew();
        while (queue.Count > 0)
        {
            var (bp, node, depth) = queue[0];
            queue.RemoveAt(0);
            if (node.Unlisted == null) continue;
            int left = limit - (Base.Count - 1); // a directory is listed whole (up to the entry limit), never in part
            if (left <= 0 || seen >= budget || clock.Elapsed > MountManager.ListSeconds)
            {
                if (Base.Count >= limit) Warn(Msg("truncated", ("dir", Path), ("limit", limit.ToString(CultureInfo.InvariantCulture))));
                return;
            }
            var got = await ListAsync(bp, left);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var good = new List<MountEntry>();
            int skipped = 0;
            foreach (var e in got)
            {
                if (!MountManager.Valid(e) || !names.Add(e.Name)) { skipped++; continue; }
                good.Add(e);
            }
            if (skipped > 0) Warn(Msg("skipped", ("dir", Machine(bp)), ("count", skipped.ToString(CultureInfo.InvariantCulture))));
            node.Unlisted = null;
            good = [.. good.OrderBy(e => e.Name, StringComparer.Ordinal)];
            if (good.Count > left)
            {
                good = good[..left];
                Warn(Msg("truncated", ("dir", Path), ("limit", limit.ToString(CultureInfo.InvariantCulture))));
            }
            foreach (var e in good)
            {
                var child = Place(node, MountManager.Join(bp, e.Name), e, depth + 1 < MountManager.MaxDepth);
                seen++;
                if (e.IsDir && deep && depth + 1 < MountManager.MaxDepth) queue.Add((MountManager.Join(bp, e.Name), (Dir)child, depth + 1));
            }
            shell.CheckDeadline();
        }
    }

    void Warn(string w)
    {
        if (!Warnings.Contains(w)) Warnings.Add(w);
    }

    Node Place(Dir parent, string p, MountEntry e, bool listable)
    {
        bool ro = Mode == MountMode.ReadOnly;
        var owner = ro ? "root" : Mounts.Shell.Options.User;
        var when = e.Modified ?? Fs.Clock();
        Node node = e.IsDir
            ? new Dir { Modified = when, Owner = owner, Mode = ro ? Shell.Oct("555") : Shell.Oct("755"), Unlisted = listable ? NotListed : null }
            : new FileNode { Modified = when, Owner = owner, Mode = ro ? Shell.Oct("444") : Shell.Oct("644"), ReportedSize = e.Size ?? 0, Loader = Miss };
        parent.Children[p[(p.LastIndexOf('/') + 1)..]] = node;
        Base[p] = new(e, node);
        return node;
    }

    // ----- the files in the machine -----

    /// <summary>Replaces the mount point's tree: the root, unlisted, or the saved <paramref name="entries"/> (source path, (entry,
    /// still unlisted)), parents first.</summary>
    public Dir Build(IEnumerable<KeyValuePair<string, (MountEntry Entry, bool Unlisted)>>? entries = null)
    {
        bool ro = Mode == MountMode.ReadOnly;
        Dir root;
        using (Fs.AsRoot())
        {
            MountManager.DropChildren(Fs, Path);
            Fs.CreateDirectory(Path, parents: true);
            root = (Dir)Fs.Find(Path)!;
        }
        (root.Owner, root.Mode) = ro ? ("root", Shell.Oct("555")) : (Mounts.Shell.Options.User, Shell.Oct("755"));
        root.Unlisted = entries == null ? NotListed : null;
        Base = new(StringComparer.Ordinal) { ["/"] = new(new MountEntry("", true), root) };
        _fetched = []; _misses = []; _failed.Clear();
        _expected = new(StringComparer.Ordinal); _absent = new(StringComparer.Ordinal);
        foreach (var (p, (e, unlisted)) in entries ?? [])
        {
            if (p == "/" || !Base.TryGetValue(VirtualFileSystem.GetParent(p), out var parent) || !parent.Entry.IsDir) continue;
            var node = Place((Dir)parent.Node!, p, e, unlisted);
            if (!unlisted && node is Dir d) d.Unlisted = null;
        }
        Error = Shown = null;
        Stale = false;
        return root;
    }

    /// <summary>An empty mount point the agent can't open; the changes are kept for a later mount.</summary>
    public void Unavailable(string? why, string? shown = null)
    {
        using (Fs.AsRoot())
        {
            MountManager.DropChildren(Fs, Path);
            Fs.CreateDirectory(Path, parents: true);
            Fs.Chmod(Path, 0);
        }
        Base = new(StringComparer.Ordinal);
        Error = why;
        Shown = shown ?? why;
    }

    /// <summary>Lists the unlisted directories on the way to a machine path, and the path itself if it is one (deep: all below).</summary>
    public async Task RevealAsync(string path, bool deep, int budget = -1)
    {
        if (budget < 0) budget = MountManager.ListBudget;
        var node = Fs.Find(Path, follow: false);
        var at = new Dictionary<Node, string>(ReferenceEqualityComparer.Instance);
        foreach (var (p, b) in Base) if (b.Node != null) at[b.Node] = p;
        var rest = path != Path ? path[Path.Length..].Split('/')[1..] : [];
        foreach (var name in rest.Append(null))
        {
            if (node is not Dir d) return;
            var bp = at.GetValueOrDefault(d);
            if (d.Unlisted != null && bp != null) await GrowAsync([(bp, d, bp != "/" ? bp.Count(c => c == '/') : 0)], budget, deep: deep && name == null);
            else if (name == null && deep)
                await GrowAsync([.. MountManager.DirsUnder(d).Where(n => n.Unlisted != null && at.ContainsKey(n)).Select(n => (at[n], n, 0))], budget);
            if (name == null) return;
            node = d.Children.GetValueOrDefault(name);
        }
    }

    /// <summary>Makes <paramref name="changes"/> again on the tree, remembering the versions they were made on.</summary>
    public void Replay(IReadOnlyList<MountChange> changes)
    {
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
            catch (ShellException e) { Warn($"{path}: {e.Message}"); }
            if (c.Kind == MountChangeKind.Write && c.Version == null) _absent.Add(c.Path);
            else if (c.Kind != MountChangeKind.Mkdir) _expected.TryAdd(c.Path, c.Version);
        }
    }

    public async Task RevealForAsync(IEnumerable<MountChange> changes)
    {
        foreach (var c in changes)
            foreach (var p in new[] { c.Path, c.To })
                if (p != null) await RevealAsync(Machine(VirtualFileSystem.GetParent(p)), false);
    }

    void AddMiss(Node n)
    {
        if (!_misses.Contains(n)) _misses.Add(n);
    }

    /// <summary>A placeholder was read before it was fetched: it is fetched after the command.</summary>
    void Miss(FileNode node, string path)
    {
        if (!Current) throw new ShellException($"{path}: {Msg("removed")}");
        AddMiss(node);
        var why = _failed.GetValueOrDefault(node);
        if (why == Msg("tooLarge")) throw new ShellException($"{path}: {why}");
        throw new ShellException($"{path}: {(why != null ? why + Msg("retry") : Msg("notFetched"))}");
    }

    /// <summary>A directory nothing listed yet was looked into: it is listed after the command.</summary>
    string NotListed(Dir node)
    {
        if (Current) AddMiss(node);
        return Msg("notListed");
    }

    public bool HasMisses => _misses.Count > 0;

    public List<Node> TakeMisses()
    {
        var m = _misses;
        _misses = [];
        return m;
    }

    /// <summary>Fetches placeholders, <see cref="MountManager.FetchAtOnce"/> at a time, making room by evicting clean fetched files;
    /// failures are kept for Miss. When the call's time runs out first, says how far it got.</summary>
    public async Task FetchAsync(IEnumerable<Node> nodes, TextWriter? err = null, string where = "")
    {
        var shell = Mounts.Shell;
        var at = new Dictionary<Node, (string Path, MountBase Base)>(ReferenceEqualityComparer.Instance);
        foreach (var (p, b) in Base) if (b.Node != null) at[b.Node] = (p, b);
        var todo = nodes.OfType<FileNode>().Where(n => n.Loader != null && at.ContainsKey(n)).ToList();
        int done = 0;
        for (int i = 0; i < todo.Count; i += MountManager.FetchAtOnce)
        {
            if (DateTime.UtcNow >= shell.CallDeadline)
            {
                err?.WriteLine("mount: " + Msg("progress", ("count", done.ToString(CultureInfo.InvariantCulture)), ("total", todo.Count.ToString(CultureInfo.InvariantCulture)), ("dir", where)));
                return;
            }
            var batch = todo.Skip(i).Take(MountManager.FetchAtOnce).ToList();
            long room = Fs.Capacity - Fs.UsedBytes;
            long need = batch.Sum(n => at[n].Base.Entry.Size ?? 0);
            if (need > room) room += Evict(need - room);
            long share = Math.Max(room / batch.Count, 0);
            var tasks = batch.Select(n => FetchOneAsync(at[n].Path, at[n].Base, share)).ToArray();
            try { await Task.WhenAll(tasks); }
            catch (Exception) { } // each task's own outcome is looked at below
            for (int k = 0; k < batch.Count; k++)
            {
                var node = batch[k];
                var t = tasks[k];
                if (t.IsCanceled) throw new OperationCanceledException();
                if (t.Exception?.InnerException is { } e)
                {
                    if (e is OperationCanceledException or ControlFlow) ExceptionDispatchInfo.Throw(e);
                    _failed[node] = e is SourceFailure f ? f.Safe : e.Message;
                    continue;
                }
                var b = at[node].Base;
                if (node.Loader == null) continue; // written while we waited
                var data = t.Result;
                if (data.Length > Fs.Capacity - Fs.UsedBytes) { _failed[node] = Msg("tooLarge"); continue; }
                MountManager.Fill(Fs, node, data);
                b.Digest = SHA256.HashData(data);
                b.Clock = node.Changed;
                _failed.Remove(node);
                _fetched.Add(b);
                done++;
            }
        }
    }

    async Task<byte[]> FetchOneAsync(string p, MountBase b, long room)
    {
        if ((b.Entry.Size ?? 0) > room) throw new SourceFailure(Msg("tooLarge"), Msg("tooLarge"));
        var data = await ReadAsync(p, room);
        if (data.Length > room) throw new SourceFailure(Msg("tooLarge"), Msg("tooLarge"));
        return data;
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
            n.ReportedSize = b.Entry.Size ?? 0;
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

    /// <summary>The agent's changes, in an order that applies one after another: deletes of names about to be replaced, renames
    /// and mkdirs as the new tree has them (parents first, moving a name out of the way through a temporary name when it is
    /// taken), then writes, then the remaining deletes (children first). A file still only a placeholder somewhere in the
    /// machine (moved out of the mount) is not deleted until it is fetched.</summary>
    public List<MountChange> Changes()
    {
        if (!Available) return [];
        if (Fs.Find(Path, follow: false) is not Dir root || Base.Count == 0) return [];
        HashSet<Node> inTree = MountManager.Ids(root);
        var at = new Dictionary<Node, string>(ReferenceEqualityComparer.Instance);
        foreach (var (p, b) in Base) if (p != "/" && b.Node != null) at[b.Node] = p;
        var where = new Dictionary<Node, string>(ReferenceEqualityComparer.Instance); // node -> its path in the new tree
        foreach (var (n, p) in MountManager.WalkPaths(root, "/")) where.TryAdd(n, p);
        bool InTree(Node? n) => n != null && inTree.Contains(n);
        List<string> gone = Base.Where(kv => kv.Key != "/" && !InTree(kv.Value.Node)).Select(kv => kv.Key).OrderByDescending(p => p, StringComparer.Ordinal).ToList();
        var held = gone.Where(p => Base[p].Node is FileNode { Nlink: > 0, Loader: not null }).ToHashSet(StringComparer.Ordinal);
        foreach (var p in held) AddMiss(Base[p].Node!); // fetched after the next command, then deleted
        held.UnionWith(gone.Where(p => held.Any(q => q.StartsWith(p + "/", StringComparison.Ordinal))).ToList());
        HashSet<string> dropped = new(held, StringComparer.Ordinal);
        var visited = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var movedDone = new HashSet<string>(StringComparer.Ordinal);
        List<MountChange> ops = [], writes = [];
        List<(string From, string To)> renames = []; // as applied so far, for where a base path is now
        int swap = 0;

        string NowAt(string p)
        {
            foreach (var (f, t) in renames)
                if (p == f || p.StartsWith(f + "/", StringComparison.Ordinal)) p = t + p[f.Length..];
            return p;
        }

        // the base path whose entry is at source path p now (renames undone)
        string BaseAt(string p)
        {
            for (int i = renames.Count - 1; i >= 0; i--)
            {
                var (f, t) = renames[i];
                if (p == t || p.StartsWith(t + "/", StringComparison.Ordinal)) p = f + p[t.Length..];
            }
            return p;
        }

        string? WriteVersion(string p) => _absent.Contains(p) ? null : OldVersion(p);

        // q is gone and something else stands where it was: delete it, and what was below it, first. Entries below it that
        // still exist elsewhere and move later go to a temporary name at the top first.
        void Drop(string q)
        {
            var keep = Base.Where(kv => kv.Key.StartsWith(q + "/", StringComparison.Ordinal) && InTree(kv.Value.Node) && !movedDone.Contains(kv.Key))
                .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            foreach (var g in keep)
            {
                if (keep.Any(k => g.StartsWith(k + "/", StringComparison.Ordinal))) continue;
                var tmp = $"/.topwater-move-{++swap}";
                var from = NowAt(g);
                ops.Add(new(MountChangeKind.Rename, from, To: tmp, Version: OldVersion(g)));
                renames.Add((from, tmp));
            }
            foreach (var g in gone)
                if ((g == q || g.StartsWith(q + "/", StringComparison.Ordinal)) && dropped.Add(g))
                    ops.Add(new(MountChangeKind.Delete, NowAt(g), Version: OldVersion(g)));
        }

        // source path p is about to be taken: a base entry still there that moves later goes to a temporary name
        void Clear(string p)
        {
            var q = BaseAt(p);
            if (Base.TryGetValue(q, out var b) && !movedDone.Contains(q) && InTree(b.Node) && where.GetValueOrDefault(b.Node!) != p)
            {
                var tmp = $"{p}.topwater-swap-{++swap}";
                ops.Add(new(MountChangeKind.Rename, p, To: tmp, Version: OldVersion(q)));
                renames.Add((p, tmp));
            }
        }

        void Walk(Dir d, string path, string? was)
        {
            foreach (var (name, n) in d.Children)
            {
                if (n is not (Dir or FileNode)) continue; // symbolic links are not part of a change set
                var p = MountManager.Join(path, name);
                var bp = at.GetValueOrDefault(n);
                if (!visited.Add(n)) bp = null; // a second hard link
                var orig = was != null ? MountManager.Join(was, name) : null;
                var old = orig != null ? Base.GetValueOrDefault(orig) : null;
                bool replaced = old != null && !InTree(old.Node);
                if (bp != null)
                {
                    if (bp != orig)
                    {
                        if (replaced) Drop(orig!);
                        Clear(p);
                        var from = NowAt(bp);
                        ops.Add(new(MountChangeKind.Rename, from, To: p, Version: OldVersion(bp)));
                        renames.Add((from, p));
                    }
                    movedDone.Add(bp);
                    var b = Base[bp];
                    if (n is Dir sub) Walk(sub, p, bp);
                    else if (n is FileNode f && f.Loader == null && f.Changed != b.Clock && !SHA256.HashData(f.Raw).AsSpan().SequenceEqual(b.Digest ?? []))
                        writes.Add(new(MountChangeKind.Write, p, Version: WriteVersion(bp), Content: f.Raw));
                    continue;
                }
                if (replaced && (n is FileNode) == !old!.Entry.IsDir)
                {
                    dropped.Add(orig!); // a file replaced by a file is a write; a directory made again is the same one
                    if (n is Dir sub) Walk(sub, p, orig);
                    else if (n is FileNode { Loader: null } f) writes.Add(new(MountChangeKind.Write, p, Version: WriteVersion(orig!), Content: f.Raw));
                    continue;
                }
                if (replaced) Drop(orig!);
                if (n is Dir made)
                {
                    Clear(p); // a write needs no room yet: writes come after every rename
                    ops.Add(new(MountChangeKind.Mkdir, p));
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
        var deletes = gone.Where(p => !dropped.Contains(p)).Select(p => new MountChange(MountChangeKind.Delete, NowAt(p), Version: OldVersion(p))).ToList();
        return [.. ops, .. writes, .. deletes];
    }

    public async Task<MountApplyResult> ApplyAsync(IEnumerable<MountChange>? changes)
    {
        var current = Changes();
        var todo = changes == null ? current : changes.Where(current.Contains).ToList();
        if (!Available) return new([], todo.FirstOrDefault(), Error ?? Msg("notListedYet")) { NotTried = [.. todo.Skip(1)] };
        var caps = Capabilities();
        var applied = new List<MountChange>();
        Busy = true;
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                var c = todo[i];
                string? version = null;
                try
                {
                    if (Mode == MountMode.ReadOnly) throw new SourceFailure(Msg("readOnly"), Msg("readOnly"));
                    if (!caps.HasFlag(MountManager.Needs(c.Kind))) throw new SourceFailure(Msg("cannot", ("kind", c.KindName)), Msg("cannot", ("kind", c.KindName)));
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
                        case MountChangeKind.Rename: version = await RenameAsync(c.Path, c.To!, c.Version); break;
                        default: version = await MkdirAsync(c.Path); break;
                    }
                }
                catch (MountConflictException e)
                {
                    LastSafe = null;
                    return new(applied, c, e.Message is { Length: > 0 } m ? m : "conflict", true) { NotTried = [.. todo.Skip(i + 1)] };
                }
                catch (SourceFailure e)
                {
                    LastSafe = e.Safe;
                    return new(applied, c, e.Full) { NotTried = [.. todo.Skip(i + 1)] };
                }
                Applied(c, version);
                applied.Add(c);
            }
        }
        finally { Busy = false; }
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
                var moved = Base[p];
                Base.Remove(p);
                Base[c.To + p[c.Path.Length..]] = moved;
            }
            if (Base.TryGetValue(c.To!, out var rb))
                rb.Entry = rb.Entry with { Name = c.To![(c.To!.LastIndexOf('/') + 1)..], Version = version ?? rb.Entry.Version };
            return;
        }
        var node = Fs.Find(Machine(c.Path), follow: false);
        var name = c.Path[(c.Path.LastIndexOf('/') + 1)..];
        if (c.Kind == MountChangeKind.Mkdir)
        {
            Base[c.Path] = new(new MountEntry(name, true, Modified: Fs.Clock(), Version: version ?? ""), node);
            return;
        }
        var data = c.Content ?? [];
        var nb = Base[c.Path] = new(new MountEntry(name, false, data.Length, Fs.Clock(), version ?? ""), node, SHA256.HashData(data));
        if (node is FileNode f) nb.Clock = SHA256.HashData(f.Raw).AsSpan().SequenceEqual(nb.Digest) ? f.Changed : null;
    }

    /// <summary>Each path and (entry, still unlisted), parents first: what a snapshot taken in this process restores from.</summary>
    public List<KeyValuePair<string, (MountEntry Entry, bool Unlisted)>> Listing() =>
        [.. Base.Where(kv => kv.Key != "/").OrderBy(kv => kv.Key, StringComparer.Ordinal) // ordinal order puts every directory before what is below it
            .Select(kv => new KeyValuePair<string, (MountEntry, bool)>(kv.Key, (kv.Value.Entry, kv.Value.Node is Dir { Unlisted: not null })))];

    static string Msg(string key, params (string Name, string Value)[] values) => CommandSpec.Message("mount", key, values);
}

/// <summary>A shell's mounts (Shell.MountAsync and friends call these), the sources the host mounted (by type and id, with the
/// mode it chose), and, while recording, the source exchanges of the current call.</summary>
sealed class MountManager(Shell shell)
{
    internal const int MaxDepth = 32; // directories below the mount point that are listed
    const int MaxNameBytes = 255;
    internal const int MaxVersion = 1024; // characters in a version
    internal static int ListBudget = 2000; // entries one listing pass reads before it leaves the directories still to go as "not listed yet"
    internal static readonly TimeSpan ListSeconds = TimeSpan.FromSeconds(5); // and the time it may take
    internal const int FetchAtOnce = 4; // files fetched at the same time

    // commands that never read a file's content: nothing is fetched before them (they may still list a directory)
    static readonly HashSet<string> NoContent = ["ls", "find", "stat", "du", "df", "tree", "cd", "pwd", "test", "[", "mkdir", "rmdir", "rm", "mv", "touch",
        "mount", "chmod", "chown", "chgrp", "ln", "realpath", "readlink", "basename", "dirname", "echo", "printf", "true", "false", "date", "sleep", "seq"];
    // commands that walk a directory they are given: everything below it is listed first
    static readonly HashSet<string> Deep = ["find", "du", "tree", "grep", "egrep", "fgrep", "rg", "cp", "tar", "zip", "rm", "diff", "chmod", "chown", "chgrp",
        "to-markdown", "wc", "cat"];

    public readonly Shell Shell = shell;
    public readonly List<MountPoint> Items = [];
    public Dictionary<(string Type, string Id), (IMountSource Source, MountMode Mode)> Sources = []; // the host's sources and the modes it chose
    public List<MountExchange>? ReplayAnswers; // a replay's recorded exchanges: answered instead of any source
    List<MountExchange> _log = [];

    public void Log(MountExchange x)
    {
        if (Shell.Recorder != null) lock (_log) _log.Add(x);
    }

    public List<MountExchange> TakeLog()
    {
        lock (_log)
        {
            var log = _log;
            _log = [];
            return log;
        }
    }

    /// <summary>The mount holding a machine path.</summary>
    public MountPoint? Find(string path) => Items.Find(m => path == m.Path || path.StartsWith(m.Path + "/", StringComparison.Ordinal));

    public MountPoint Get(string path) =>
        Items.Find(m => m.Path == VirtualFileSystem.Normalize("/", path)) ?? throw new ArgumentException($"{path}: not a mount point");

    /// <summary>The source and mode a restored mount gets: the host's (never the file's mode), or a replay's answers.</summary>
    (IMountSource? Source, MountMode Mode) SourceFor(string type, string id, string path, MountMode mode) =>
        ReplayAnswers != null ? (new ReplaySource(ReplayAnswers, path, type, id), mode)
        : Sources.TryGetValue((type, id), out var s) ? (s.Source, s.Mode) : (null, mode);

    // ----- the host's API -----

    public async Task MountAsync(string path, IMountSource source, MountMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException($"{mode}: the mode is one of read-only, staged, live");
        if (path is null || !path.StartsWith('/')) throw new ArgumentException($"{path}: a mount point is an absolute path other than /");
        path = VirtualFileSystem.Normalize("/", path);
        if (path == "/") throw new ArgumentException($"{path}: a mount point is an absolute path other than /");
        if (mode != MountMode.ReadOnly && !source.Capabilities.HasFlag(MountCapabilities.Write))
            throw new ArgumentException($"{path}: the source can't write, so it can only be mounted read-only");
        var old = Items.Find(m => m.Path == path);
        if (old != null)
        {
            if (old.Source != null || (old.Type, old.Id) != (source.Type ?? "", source.Id ?? "")) throw new ArgumentException($"{path}: already a mount point; unmount it first");
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
        if (old != null) mount.Held = old.Held; // still the host's to accept
        var root = mount.Build();
        try { await mount.GrowAsync([("/", root, 0)], ListBudget); }
        catch (SourceFailure e)
        {
            Cleanup(mount, old);
            throw new ArgumentException($"{path}: {e.Full}");
        }
        catch
        {
            Cleanup(mount, old);
            throw;
        }
        if (old != null) Items[Items.IndexOf(old)] = mount; else Items.Add(mount);
        Sources[(mount.Type, mount.Id)] = (source, mode);
    }

    void Cleanup(MountPoint m, MountPoint? old)
    {
        if (old != null) { old.Unavailable(old.Error, old.Shown); return; }
        using var _ = Shell.Fs.AsRoot();
        DropChildren(Shell.Fs, m.Path);
    }

    public void Unmount(string path)
    {
        var m = Get(path);
        Items.Remove(m);
        if (!Items.Any(o => (o.Type, o.Id) == (m.Type, m.Id))) Sources.Remove((m.Type, m.Id));
        using var _ = Shell.Fs.AsRoot();
        DropChildren(Shell.Fs, m.Path);
        Shell.Fs.Chmod(m.Path, Shell.Oct("755"));
    }

    public async Task RefreshAsync(string path)
    {
        var m = Get(path);
        if (m.Source == null) throw new SourceFailure(m.Shown ?? "unavailable", m.Error ?? "unavailable");
        var changes = m.Changes();
        var root = m.Build();
        await m.GrowAsync([("/", root, 0)], ListBudget);
        await m.RevealForAsync(changes);
        m.Replay(changes);
    }

    /// <summary>Drops the agent's changes (and, with <paramref name="held"/>, a loaded snapshot's): the files are the source's again.</summary>
    public void Discard(string path, bool held = true)
    {
        var m = Get(path);
        if (held) m.Held = [];
        if (m.Available) m.Build(m.Listing());
    }

    /// <summary>Makes a loaded snapshot's changes the agent's: in the files, and in PendingChanges.</summary>
    public async Task AcceptAsync(string path)
    {
        var m = Get(path);
        if (!m.Available) throw new ArgumentException($"{path}: {m.Error ?? "not listed yet"}; mount it, or run a call first");
        var changes = m.Held;
        m.Held = [];
        await m.RevealForAsync(changes);
        m.Replay(changes);
    }

    // ----- around commands -----

    /// <summary>At the start of a call: lists the mounts a restore brought back, within the listing budget. A listing that fails
    /// leaves the mount unavailable for this call (tried again at the next); the call itself goes on.</summary>
    public async Task PrepareAsync()
    {
        foreach (var m in Items.ToList())
        {
            if (!(m.Stale && m.Source != null)) continue;
            try
            {
                var root = m.Build();
                await m.GrowAsync([("/", root, 0)], ListBudget);
            }
            catch (SourceFailure e)
            {
                m.Unavailable(e.Full, e.Safe);
                m.Stale = true;
                continue;
            }
            if (m.Trusted && m.Held.Count > 0)
            {
                var changes = m.Held;
                m.Held = [];
                await m.RevealForAsync(changes);
                m.Replay(changes);
            }
        }
    }

    List<string> Named(string w)
    {
        var output = new List<string>();
        int eq = w.IndexOf('=');
        foreach (var part in w.StartsWith('-') && eq >= 0 ? new[] { w, w[(eq + 1)..] } : [w])
        {
            if (part.Length == 0) continue;
            var p = Shell.ResolvePath(part);
            output.Add(Shell.Fs.RealPath(p, out _, existing: false) ?? p);
        }
        return output;
    }

    /// <summary>Lists and fetches what a command is about to use: the paths it names (a directory: one level, or everything below
    /// for a command that walks it), the file it redirects from or appends to, and the working directory only for a recursive
    /// grep given no path. Commands that read no content only list. A file moved out of its mount by `mv` is fetched first,
    /// so its content is never lost.</summary>
    public async Task BeforeAsync(string name, IReadOnlyList<string> argv, TextWriter? err = null, string? redirect = null)
    {
        var fs = Shell.Fs;
        var words = redirect != null ? [redirect] : argv.Skip(1);
        var paths = words.SelectMany(Named).ToList();
        if (redirect == null && name is "grep" or "egrep" or "fgrep" or "rg" && Recursive(argv) && !paths.Any(p => Find(p) != null && fs.Exists(p, follow: false)))
            paths.Add(fs.RealPath(Shell.Cwd, out _) ?? Shell.Cwd);
        if (redirect == null && argv[0].Contains('/')) paths.AddRange(Named(argv[0])); // a script run by its path
        bool deep = Deep.Contains(name) || Recursive(argv);
        bool content = redirect != null || !NoContent.Contains(name);
        var outOf = new HashSet<string>(StringComparer.Ordinal);
        if (name == "mv" && paths.Count >= 2)
        {
            var dest = Find(paths[^1]);
            foreach (var p in paths.Take(paths.Count - 1)) if (Find(p) is { } m && m != dest) outOf.Add(p);
        }
        foreach (var p in paths)
        {
            var m = Find(p);
            if (m is not { Available: true }) continue;
            await m.RevealAsync(p, deep || outOf.Contains(p));
            if ((content || outOf.Contains(p)) && fs.Exists(p, follow: false)) await m.FetchAsync(m.StubsUnder(p), err, p);
        }
    }

    /// <summary>After a command: lists and fetches what it missed.</summary>
    public async Task AfterAsync()
    {
        foreach (var m in Items.ToList())
        {
            if (!m.HasMisses || !m.Available) continue;
            var nodes = m.TakeMisses();
            foreach (var n in nodes)
                if (n is Dir { Unlisted: not null } d && m.Base.FirstOrDefault(kv => kv.Value.Node == d).Key is { } bp)
                    await m.GrowAsync([(bp, d, bp.Count(c => c == '/'))], ListBudget, deep: false);
            await m.FetchAsync(nodes.OfType<FileNode>());
        }
    }

    /// <summary>Live mode: applies the changes made so far (after a command's redirections are written, and when the call ends).
    /// A change that fails is reported once and left pending; the changes after it are still tried, and it is tried again only
    /// once it changes.</summary>
    public async Task SettleAsync(TextWriter err)
    {
        foreach (var m in Items.ToList())
        {
            if (m.Mode != MountMode.Live || !m.Available || m.Busy) continue;
            var todo = m.Changes().Where(c => !m.Stuck.Contains(c)).ToList();
            while (todo.Count > 0)
            {
                var r = await m.ApplyAsync(todo);
                if (r.Failed == null) break;
                m.Stuck.Add(r.Failed);
                var where = m.Machine(r.Failed.Path);
                var reason = m.LastSafe ?? CommandSpec.Message("mount", "sourceFailed");
                err.WriteLine("mount: " + (r.Conflict ? CommandSpec.Message("mount", "conflict", ("path", where), ("dir", m.Path))
                    : CommandSpec.Message("mount", "failed", ("path", where), ("reason", reason), ("dir", m.Path))));
                todo = [.. r.NotTried];
            }
            var current = m.Changes();
            m.Stuck = [.. m.Stuck.Where(current.Contains)]; // applied, discarded or changed since: no longer stuck
        }
    }

    // ----- snapshots and recordings -----

    /// <summary>The filesystem (a copy for a snapshot) without what is inside a mount: a snapshot keeps the mount's definition instead.</summary>
    public VirtualFileSystem Outside(VirtualFileSystem copy)
    {
        using var _ = copy.AsRoot();
        foreach (var m in Items)
            if (copy.Exists(m.Path, follow: false)) copy.Delete(m.Path, recursive: true); // a copy's directories are all listed
        return copy;
    }

    /// <summary>Each mount's definition and changes, and (in memory only, never saved) its listing, so restoring the snapshot in
    /// this process needs no source call and keeps the agent's changes.</summary>
    public List<SavedMount> Capture() => [.. Items.Select(m => new SavedMount(m.Path, m.Mode, m.Type, m.Id, m.Changes(), [.. m.Held], m.Available ? m.Listing() : null))];

    /// <summary>After the files were replaced: the snapshot's mounts with the host's sources and modes. A snapshot taken in this
    /// process brings its listing and changes back as they were; one loaded from a file is listed at the next call, and its
    /// changes wait for the host (AcceptSnapshotChangesAsync), never applied or shown as the agent's on their own.</summary>
    public void Restore(IReadOnlyList<SavedMount> saved)
    {
        Items.Clear();
        foreach (var d in saved)
        {
            var (source, mode) = SourceFor(d.Type, d.Id, d.Path, d.Mode);
            var m = new MountPoint(this, d.Path, source, mode) { Type = d.Type, Id = d.Id };
            Items.Add(m);
            m.Held = [.. d.Held, .. d.Listing != null && source != null ? [] : d.Changes];
            m.Trusted = ReplayAnswers != null; // a replay's sources are its recorded answers: nothing real to protect
            if (source == null) m.Unavailable(CommandSpec.Message("mount", "unavailable", ("source", $"{m.Type}:{m.Id}")));
            else if (d.Listing != null)
            {
                m.Build(d.Listing);
                m.Replay(d.Changes);
            }
            else
            {
                m.Unavailable(null);
                m.Stale = true;
            }
        }
    }

    /// <summary>What the mounts hold when a recording starts, as the exchanges a replay's first listing and reads answer from.</summary>
    public List<MountExchange> StartExchanges()
    {
        var output = new List<MountExchange>();
        foreach (var m in Items)
        {
            if (!m.Available) continue;
            var paths = m.Base.Keys.Order(StringComparer.Ordinal).ToList();
            var kids = paths.Where(p => m.Base[p].Entry.IsDir && m.Base[p].Node is not Dir { Unlisted: not null })
                .ToDictionary(p => p, _ => new List<MountEntry>(), StringComparer.Ordinal);
            foreach (var p in paths)
                if (p != "/" && kids.TryGetValue(VirtualFileSystem.GetParent(p), out var list)) list.Add(m.Base[p].Entry);
            foreach (var p in paths)
            {
                var b = m.Base[p];
                if (kids.TryGetValue(p, out var entries)) output.Add(new(m.Path, "list", p, Entries: entries));
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

    /// <summary>What the agent may see of a source's error: a <see cref="MountSourceException"/>'s text, else "the source failed".</summary>
    internal static string SafeText(Exception e) => e switch
    {
        MountSourceException m => m.Message is { Length: > 0 } t ? t : CommandSpec.Message("mount", "sourceFailed"),
        SourceFailure f => f.Safe,
        _ => CommandSpec.Message("mount", "sourceFailed"),
    };

    static bool Recursive(IReadOnlyList<string> argv) =>
        argv.Skip(1).Any(a => a is "--recursive" or "--dereference-recursive" || (a.StartsWith('-') && !a.StartsWith("--") && (a.Contains('r') || a.Contains('R'))));

    /// <summary>An entry a source listed that can be a file: a plain name, sane size and version.</summary>
    internal static bool Valid(MountEntry? e) =>
        e is { Name: { } n, Version: { } v } && n is not ("" or "." or "..") && !n.Contains('/') && !n.Contains('\0')
        && Encoding.UTF8.GetByteCount(n) <= MaxNameBytes && e.Size is null or >= 0 && v.Length <= MaxVersion;

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

    internal static IEnumerable<(Node Node, string Path)> WalkPaths(Dir d, string path)
    {
        foreach (var (name, n) in d.Children)
        {
            var p = Join(path, name);
            yield return (n, p);
            if (n is Dir sub) foreach (var x in WalkPaths(sub, p)) yield return x;
        }
    }

    /// <summary>A directory and every directory below it, breadth first.</summary>
    internal static List<Dir> DirsUnder(Dir d)
    {
        var output = new List<Dir>();
        var queue = new Queue<Dir>();
        queue.Enqueue(d);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            output.Add(n);
            foreach (var c in n.Children.Values) if (c is Dir sub) queue.Enqueue(sub);
        }
        return output;
    }

    /// <summary>Empties a directory (the caller is root), whatever is listed or not in it.</summary>
    internal static void DropChildren(VirtualFileSystem fs, string path)
    {
        var n = fs.Find(path, follow: false);
        if (n is Dir d)
        {
            d.Unlisted = null;
            foreach (var name in d.Children.Keys.ToList())
            {
                if (d.Children[name] is Dir child) foreach (var sub in DirsUnder(child)) sub.Unlisted = null;
                fs.Delete(Join(path, name), recursive: true);
            }
        }
        else if (n != null) fs.Delete(path, recursive: true);
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
        lock (_answers)
        {
            if (!_answers.TryGetValue((op, path, to), out var q) || q.Count == 0)
                throw new MountSourceException(CommandSpec.Message("mount", "replay", ("op", op), ("path", path)));
            var x = q[0];
            if (q.Count > 1) q.RemoveAt(0); // the last answer repeats
            if (x.Conflict) throw new MountConflictException(x.Error ?? "conflict");
            if (x.Error != null) throw new MountSourceException(x.Error);
            return x;
        }
    }

    public Task<IReadOnlyList<MountEntry>> ListAsync(string path, int maxEntries, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MountEntry>>([.. Answer("list", path).Entries ?? []]);
    public Task<MountEntry?> StatAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Answer("stat", path).Entry);
    public Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken) => Task.FromResult(Answer("read", path).Body ?? []);
    public Task<string> WriteAsync(string path, byte[] data, string? expectedVersion, CancellationToken cancellationToken) => Task.FromResult(Answer("write", path).Version ?? "");
    public Task DeleteAsync(string path, string? expectedVersion, CancellationToken cancellationToken) { Answer("delete", path); return Task.CompletedTask; }
    public Task<string?> RenameAsync(string path, string to, string? expectedVersion, CancellationToken cancellationToken) => Task.FromResult(Answer("rename", path, to).Version);
    public Task<string?> MkdirAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Answer("mkdir", path).Version);
}
