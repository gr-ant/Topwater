using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Topwater.Containers.ContainerExport;

namespace Topwater.Containers;

/// <summary>Options for <see cref="ContainerImport"/>.</summary>
public sealed class ContainerImportOptions
{
    /// <summary>Paths imported whole (with everything below them) on top of what the container changed, e.g. /etc, /var/log, /app.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];
    /// <summary>Import real executables (ELF files) as inert files. Off by default: Topwater can't run them, and they're most of a filesystem's bytes.</summary>
    public bool IncludeBinaries { get; init; }
    /// <summary>For an image Topwater exported, re-create the shell session (functions, aliases, options, variables) from its session script,
    /// run inside the new simulated shell. On by default.</summary>
    public bool RestoreSession { get; init; } = true;
    /// <summary>Limits (MaxFileSystemBytes, MaxStringBytes, MaxExtractEntries, ...) and the host name of the new machine. The user comes from the container.</summary>
    public ShellOptions? Limits { get; init; }
        }

/// <summary>What <c>docker inspect</c> says about a container or image: <c>Config.User</c>, <c>Config.WorkingDir</c>, <c>Config.Env</c> ("NAME=value").</summary>
public sealed record ContainerConfig(string? User = null, string? WorkingDir = null, IReadOnlyList<string>? Env = null);

/// <summary>The imported machine and the report of what didn't come over.</summary>
public sealed record ContainerImportResult(ShellSnapshot Snapshot, ContainerReport Report);

/// <summary>
/// Container to snapshot. The container's content is untrusted, like a snapshot file: paths must be canonical, the import is
/// bounded by MaxFileSystemBytes, MaxStringBytes and MaxExtractEntries, and nothing is written to disk. Host APIs only.
/// </summary>
public static class ContainerImport
{
    /// <summary>
    /// Imports a container (running or stopped): what it changed relative to its image (<c>docker diff</c>), plus
    /// <see cref="ContainerImportOptions.Paths"/>, plus, for an image Topwater exported, the files that export put there. They
    /// are layered onto a fresh Topwater machine, so the simulated programs still work. User, environment and working
    /// directory come from <c>docker inspect</c>.
    /// </summary>
    public static async Task<ContainerImportResult> FromContainerAsync(string container, ContainerImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(await Docker.RunAsync(["container", "inspect", container], cancellationToken: cancellationToken));
        var c = doc.RootElement[0].GetProperty("Config");
        string? Str(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var env = c.TryGetProperty("Env", out var e) && e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
        var config = new ContainerConfig(Str("User"), Str("WorkingDir"), env);

        var changes = (await Docker.RunAsync(["container", "diff", container], cancellationToken: cancellationToken)).Split('\n').ToList();
        changes.AddRange(await TopwaterPathsAsync(container, (options?.Limits ?? new()).MaxStringBytes, cancellationToken));

        var importer = new Importer(config, changes, options ?? new());
        await Docker.RunAsync(["container", "export", container], readStdout: s => importer.ReadAsync(s, cancellationToken), cancellationToken: cancellationToken);
        return await importer.FinishAsync();
    }

    /// <summary>Imports an image: a container is created from it (never started), imported with <see cref="FromContainerAsync"/>, and removed.</summary>
    public static async Task<ContainerImportResult> FromImageAsync(string image, ContainerImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        var id = (await Docker.RunAsync(["create", image, "true"], cancellationToken: cancellationToken)).Trim();
        try { return await FromContainerAsync(id, options, cancellationToken); }
        finally
        {
            try { await Docker.RunAsync(["rm", "-f", id]); } catch (ContainerException) { } // don't hide the import's own error
        }
    }

    /// <summary>
    /// The process-free core of an import: a root filesystem as a tar (what <c>docker export</c> writes), the container's config,
    /// and its changes as <c>docker diff</c> prints them ("A /path", "C /path", "D /path"). Only changed paths and
    /// <see cref="ContainerImportOptions.Paths"/> are imported. Throws <see cref="InvalidDataException"/> for a malformed archive or one over a limit.
    /// </summary>
    public static async Task<ContainerImportResult> FromTarAsync(Stream rootfs, ContainerConfig config, IEnumerable<string>? changes = null,
        ContainerImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        var importer = new Importer(config, changes ?? [], options ?? new());
        await importer.ReadAsync(rootfs, cancellationToken);
        return await importer.FinishAsync();
    }

    /// <summary>The paths a Topwater export wrote or deleted, in docker diff's format; empty for other images. They're in the image, not the container's diff.</summary>
    static async Task<List<string>> TopwaterPathsAsync(string container, long max, CancellationToken ct)
    {
        var lines = new List<string>();
        try
        {
            await Docker.RunAsync(["container", "cp", container + ":" + PathsPath, "-"], readStdout: async s =>
            {
                using var tar = new TarReader(s);
                if (await tar.GetNextEntryAsync(cancellationToken: ct) is { EntryType: TarEntryType.RegularFile or TarEntryType.V7RegularFile, DataStream: { } data } entry && entry.Length <= max)
                    lines.AddRange((await new StreamReader(data).ReadToEndAsync(ct)).Split('\n'));
                await s.CopyToAsync(Stream.Null, ct);
            }, cancellationToken: ct);
        }
        catch (ContainerException) { } // not a Topwater image
        return lines;
    }

    static readonly Regex UserName = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}$");
    static readonly Regex VarName = new(@"^[A-Za-z_][A-Za-z0-9_]*$");

    sealed class Item(bool isDirectory, int mode, int uid, DateTime mtime, byte[]? data)
    {
        public bool IsDirectory = isDirectory;
        public int Mode = mode, Uid = uid;
        public DateTime Mtime = mtime;
        public byte[]? Data = data;
        public string? LinkTarget; // a symbolic link: the path it holds
    }

    sealed class Importer
    {
        readonly ContainerConfig _config;
        readonly ContainerImportOptions _o;
        readonly ShellOptions _limits;
        readonly ContainerReport _report = new();
        readonly HashSet<string> _changed = new(StringComparer.Ordinal);
        readonly List<string> _deleted = [], _prefixes = [];
        readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
        readonly Dictionary<string, byte[]> _captured = new(StringComparer.Ordinal); // /etc/passwd, the session script, histories
        long _bytes;
        int _count;

        public Importer(ContainerConfig config, IEnumerable<string> changes, ContainerImportOptions options)
        {
            _config = config;
            _o = options;
            _limits = options.Limits ?? new ShellOptions();
            foreach (var line in changes)
            {
                if (line.Length < 3 || line[1] != ' ' || line[2] != '/') continue;
                var path = line[2..].TrimEnd('\r');
                if (line[0] == 'D') _deleted.Add(path); else _changed.Add(path);
            }
            foreach (var p in options.Paths) _prefixes.Add(VirtualFileSystem.Normalize("/", p));
        }

        bool Selected(string path) => _changed.Contains(path) || _prefixes.Any(p => p == "/" || Under(path, p));

        static string Canonical(string name)
        {
            var n = name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name;
            if (n.EndsWith('/')) n = n[..^1];
            var path = "/" + n;
            if (path.Length > 4096 || path.Contains('\0') || VirtualFileSystem.Normalize("/", path) != path)
                throw new InvalidDataException($"not a canonical path in the container's filesystem: {JsonSerializer.Serialize(name)}");
            return path;
        }

        static bool Captured(string path) =>
            path is "/etc/passwd" or SessionPath || path.EndsWith("/.bash_history", StringComparison.Ordinal) && (path == "/root/.bash_history" || VirtualFileSystem.GetParent(VirtualFileSystem.GetParent(path)) == "/home");

        public async Task ReadAsync(Stream stream, CancellationToken ct)
        {
            try
            {
                using var tar = new TarReader(stream);
                while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } e)
                {
                    await EntryAsync(e, ct);
                    // read what's left ourselves: TarReader can lose its place skipping unread data on a forward-only stream
                    if (e.DataStream != null) await e.DataStream.CopyToAsync(Stream.Null, ct);
                }
                await stream.CopyToAsync(Stream.Null, ct); // the end-of-archive padding, so docker isn't left blocked writing it
            }
            catch (Exception ex) when (ex is not (InvalidDataException or OperationCanceledException or ContainerException))
            {
                throw new InvalidDataException("not a valid container filesystem archive: " + ex.Message, ex);
            }
        }

        async Task EntryAsync(TarEntry e, CancellationToken ct)
        {
            var path = Canonical(e.Name);
            bool file = e.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile;
            bool selected = path != "/" && Selected(path);
            byte[]? data = null;
            if (file && Captured(path) && e.Length <= _limits.MaxStringBytes) _captured[path] = data = await ReadAsync(e, e.Length, ct);
            if (!selected) return;
            if (++_count > _limits.MaxExtractEntries) throw new InvalidDataException($"more than {_limits.MaxExtractEntries} entries to import (MaxExtractEntries)");
            if (Under(path, "/etc/topwater")) return; // a Topwater export's own files
            if (Simulated.Any(s => Under(path, s)) || DockerManaged.Contains(path))
            {
                _report.Skip(path, "Topwater simulates it, or docker writes it at run time; kept Topwater's");
                return;
            }
            var mode = (int)e.Mode & 0xFFF;
            var mtime = e.ModificationTime.UtcDateTime;
            switch (e.EntryType)
            {
                case TarEntryType.Directory:
                    _items[path] = new Item(true, mode, e.Uid, mtime, null);
                    break;
                case var _ when file:
                    if ((_bytes += e.Length) > _limits.MaxFileSystemBytes) throw new InvalidDataException($"the files to import hold more than {_limits.MaxFileSystemBytes} bytes (MaxFileSystemBytes)");
                    data ??= await ReadAsync(e, e.Length, ct);
                    if (!_o.IncludeBinaries && data.AsSpan().StartsWith("\u007fELF"u8))
                    {
                        _report.Skip(path, "an ELF executable; Topwater can't run it (see IncludeBinaries)");
                        _bytes -= e.Length;
                        break;
                    }
                    _items[path] = new Item(false, mode, e.Uid, mtime, data);
                    break;
                case TarEntryType.SymbolicLink:
                    // kept as the link it is: it is only a path, resolved in the machine with the agent's permissions
                    if (e.LinkName is not { Length: > 0 and <= 4096 }) _report.Skip(path, "a symbolic link with an empty or overlong target");
                    else _items[path] = new Item(false, 0x1FF, e.Uid, mtime, []) { LinkTarget = e.LinkName };
                    break;
                case TarEntryType.HardLink:
                    _links[path] = Canonical(e.LinkName);
                    break;
                default:
                    _report.Skip(path, $"a {e.EntryType} entry, not a file or directory");
                    break;
            }
        }

        /// <summary>Reads an entry's data, never more than its header claims.</summary>
        static async Task<byte[]> ReadAsync(TarEntry e, long length, CancellationToken ct)
        {
            if (e.DataStream == null || length == 0) return [];
            var data = new byte[length];
            await e.DataStream.ReadExactlyAsync(data, ct);
            return data;
        }

        public async Task<ContainerImportResult> FinishAsync()
        {
            ResolveLinks();

            // owners by the container's own /etc/passwd
            var accounts = Account.ParsePasswd(_captured.TryGetValue("/etc/passwd", out var pw) ? Encoding.UTF8.GetString(pw) : null);
            string Owner(int uid) => accounts.FirstOrDefault(a => a.Uid == uid)?.Name is { } n && UserName.IsMatch(n) ? n : uid == 0 ? "root" : uid.ToString();

            var user = (_config.User ?? "").Split(':')[0];
            if (user.Length == 0) user = "root";
            else if (int.TryParse(user, System.Globalization.NumberStyles.None, null, out var uid)) user = Owner(uid);
            if (!UserName.IsMatch(user))
            {
                _report.Skip("user " + JsonSerializer.Serialize(user), "not a valid user name; imported as root");
                user = "root";
            }
            var home = accounts.FirstOrDefault(a => a.Name == user)?.Home ?? "/home/" + user;
            var hostname = _limits.Hostname;
            var machine = SnapshotData.Default(user, hostname);
            var fs = machine.Files;

            foreach (var path in _deleted)
            {
                if (_items.ContainsKey(path) || !fs.TryGetValue(path, out var f)) continue;
                if (Simulated.Any(s => Under(path, s)) || IsStub(f) || AccountFiles.Contains(path) || DockerManaged.Contains(path))
                    _report.Skip(path, "deleted in the container, but Topwater's machine needs it");
                else { machine.RemoveTree(path); _report._deleted.Add(path); }
            }

            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            foreach (var (path, item) in _items.OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                // a crafted archive can hold entries below a symbolic link; a real filesystem can't
                var p = VirtualFileSystem.GetParent(path);
                while (p != "/" && fs.GetValueOrDefault(p)?.LinkTarget == null) p = VirtualFileSystem.GetParent(p);
                if (p != "/") { _report.Skip(path, $"below {p}, a symbolic link"); continue; }
                Ensure(machine, VirtualFileSystem.GetParent(path));
                if (fs.TryGetValue(path, out var old) && (old.IsDirectory != item.IsDirectory || item.LinkTarget != null)) machine.RemoveTree(path);
                bool binary = false;
                if (!item.IsDirectory && item.LinkTarget == null) try { strict.GetString(item.Data!); } catch (DecoderFallbackException) { binary = true; }
                fs[path] = new FileItem(path, item.IsDirectory, item.Mode, Owner(item.Uid), item.Mtime, item.Data, binary, LinkTarget: item.LinkTarget);
                _report._carried.Add(path);
            }

            // an image Topwater exported: undo the export's additions, take the history back
            bool topwater = _captured.TryGetValue(SessionPath, out var session);
            var bashrc = home + "/.bashrc";
            if (topwater && machine.Text(bashrc) is { } rc && rc.LastIndexOf(BashrcHook, StringComparison.Ordinal) is var at and >= 0)
                fs[bashrc] = fs[bashrc] with { Data = Encoding.UTF8.GetBytes(rc.Remove(at, BashrcHook.Length)) };
            var history = _captured.TryGetValue(home + "/.bash_history", out var h) ? History(Encoding.UTF8.GetString(h)) : [];
            if (topwater && fs.Remove(home + "/.bash_history")) _report._carried.Remove(home + "/.bash_history");

            foreach (var kv in _config.Env ?? [])
            {
                var eq = kv.IndexOf('=');
                var name = eq > 0 ? kv[..eq] : kv;
                if (eq > 0 && VarName.IsMatch(name) && !ManagedVariables.Contains(name)) machine.SetExported(name, kv[(eq + 1)..]);
                else _report.Skip("environment " + JsonSerializer.Serialize(name), "not a variable a shell can hold");
            }

            var cwd = string.IsNullOrEmpty(_config.WorkingDir) ? "/" : VirtualFileSystem.Normalize("/", _config.WorkingDir);
            Ensure(machine, cwd);
            machine.SetExported("PWD", cwd);
            machine.Manifest["cwd"] = cwd;
            machine.Manifest["history"] = new System.Text.Json.Nodes.JsonArray(history.Select(x => (System.Text.Json.Nodes.JsonNode?)x).ToArray());
            var options = new ShellOptions
            {
                User = user, Hostname = hostname, MaxLoopIterations = _limits.MaxLoopIterations, MaxExtractBytes = _limits.MaxExtractBytes,
                MaxExtractEntries = _limits.MaxExtractEntries, MaxCallDuration = _limits.MaxCallDuration, MaxFileSystemBytes = _limits.MaxFileSystemBytes,
                MaxStringBytes = _limits.MaxStringBytes,
            };
            var snapshot = machine.ToSnapshot(options);

            if (topwater && _o.RestoreSession)
            {
                // the session script runs in the simulated shell, like any agent command: the container's content never reaches the host
                var shell = new Shell(options);
                shell.Restore(snapshot);
                var r = await shell.RunAsync(Encoding.UTF8.GetString(session!));
                if (r.ExitCode != 0) _report.Skip(SessionPath, $"the session script ended with exit code {r.ExitCode}: {r.Stderr.Trim()}");
                var after = SnapshotData.Read(shell.Snapshot());
                after.Manifest["history"] = machine.Manifest["history"]!.DeepClone();
                after.Manifest["cwd"] = cwd;
                foreach (var k in new[] { "lastExitCode", "lastArg", "pipeStatus" }) after.Manifest.Remove(k);
                snapshot = after.ToSnapshot(options);
            }
            _report.Skip("file groups", "Topwater files have an owner but no group; the container's gids are dropped");
            return new ContainerImportResult(snapshot, _report);
        }

        /// <summary>Makes <paramref name="dir"/> and its parents directories, replacing files in the way.</summary>
        void Ensure(SnapshotData machine, string dir)
        {
            if (dir == "/") return;
            Ensure(machine, VirtualFileSystem.GetParent(dir));
            if (machine.Files.TryGetValue(dir, out var f))
            {
                if (f.IsDirectory) return;
                _report.Skip(dir, "a file in Topwater's machine, a directory in the container; replaced");
                machine.Files.Remove(dir);
            }
            machine.Files[dir] = new FileItem(dir, true, 0x1ED, "root", DateTime.UtcNow);
        }

        /// <summary>Hard links whose target was imported become copies of it (a file, or a symbolic link); the others are skipped.</summary>
        void ResolveLinks()
        {
            foreach (var (link, target) in _links)
            {
                var t = target;
                for (int hop = 0; hop < 8 && _links.TryGetValue(t, out var next); hop++) t = next;
                if (!_items.TryGetValue(t, out var item) || item.IsDirectory)
                {
                    _report.Skip(link, $"a hard link to {target}, which wasn't imported");
                    continue;
                }
                _items[link] = item;
            }
        }

        /// <summary>A .bash_history: one command per line, or, with timestamps (#1700000000), one entry per timestamp.</summary>
        static List<string> History(string text)
        {
            var lines = text.Split('\n');
            if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
            var list = new List<string>();
            bool stamped = false;
            foreach (var line in lines)
            {
                if (line.Length > 1 && line[0] == '#' && line[1..].All(char.IsAsciiDigit)) { stamped = true; list.Add(""); continue; }
                if (stamped && list.Count > 0) list[^1] = list[^1].Length == 0 ? line : list[^1] + "\n" + line;
                else list.Add(line);
            }
            return list.Where(e => e.Length > 0).ToList();
        }
    }
}
