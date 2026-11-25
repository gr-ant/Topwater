using System.Text;

namespace Topwater.Cli;

/// <summary>What every subcommand accepts: limits, a state file, and copies in from and out to the host.</summary>
sealed class MachineOptions
{
    public string? State { get; set; }
    public string? From { get; set; }
    public List<(string Host, string Vfs)> Imports { get; } = [];
    public List<(string Vfs, string Host)> Exports { get; } = [];
    public double? TimeoutSeconds { get; set; }
    public long? DiskMegabytes { get; set; }

    public ShellOptions ToShellOptions()
    {
        var o = new ShellOptions();
        return new ShellOptions
        {
            MaxCallDuration = TimeoutSeconds is { } t ? TimeSpan.FromSeconds(t) : o.MaxCallDuration,
            MaxFileSystemBytes = DiskMegabytes is { } d ? d << 20 : o.MaxFileSystemBytes,
        };
    }
}

/// <summary>
/// One simulated machine for the length of a command: loaded from <see cref="MachineOptions.State"/> if that file exists
/// (else <see cref="MachineOptions.From"/>, else a fresh machine),
/// with the <c>--import</c> copies applied, saved back after each call, and copied out by <c>--export</c> on <see cref="Close"/>.
/// The host is only touched at those points, and only at the paths the user named.
/// </summary>
sealed class Machine
{
    readonly MachineOptions _options;
    readonly TextWriter _log;
    public Shell Shell { get; }

    Machine(MachineOptions options, Shell shell, TextWriter log) { _options = options; Shell = shell; _log = log; }

    /// <summary>Opens the machine. Throws <see cref="CliException"/> for a bad state file or import.</summary>
    public static Machine Open(MachineOptions options, TextWriter log)
    {
        var shell = new Shell(options.ToShellOptions());
        var start = options.State is { } s && File.Exists(s) ? s : options.From;
        if (start is { } state)
        {
            try
            {
                using var file = File.OpenRead(state);
                shell.Restore(ShellSnapshot.Load(file, shell.Options));
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                throw new CliException($"{state}: {e.Message}");
            }
        }
        foreach (var (host, vfs) in options.Imports) Import(shell, host, vfs);
        return new Machine(options, shell, log);
    }

    /// <summary>Writes the state file, if there is one. Called after every command so a killed process loses at most one.</summary>
    public void Save()
    {
        if (_options.State is not { } state) return;
        try { SaveTo(Shell, state); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.WriteLine($"topwater: could not save {state}: {e.Message}"); }
    }

    /// <summary>Saves the state, then copies each <c>--export</c> path out to the host.</summary>
    public void Close()
    {
        Save();
        foreach (var (vfs, host) in _options.Exports)
        {
            try { Export(Shell, vfs, host, _log); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ShellException) { _log.WriteLine($"topwater: export {vfs} -> {host}: {e.Message}"); }
        }
    }

    /// <summary>Saves through a temporary file, so a crash mid-write never leaves a half-written state file.</summary>
    public static void SaveTo(Shell shell, string path)
    {
        var temp = path + ".tmp";
        using (var file = File.Create(temp)) shell.Save(file);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Copies a host file or directory into the virtual filesystem. Symlinks are skipped, never followed.</summary>
    public static void Import(Shell shell, string host, string vfs)
    {
        var fs = shell.Fs;
        vfs = shell.ResolvePath(vfs);
        var files = new List<(string Host, string Vfs)>();
        var dirs = new List<string>();
        if (File.Exists(host) && new FileInfo(host).LinkTarget == null) files.Add((host, vfs));
        else if (Directory.Exists(host) && new DirectoryInfo(host).LinkTarget == null)
        {
            void Walk(string dir, string target)
            {
                dirs.Add(target);
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (entry.LinkTarget != null) continue;
                    var child = target == "/" ? "/" + entry.Name : target + "/" + entry.Name;
                    if (entry is DirectoryInfo) Walk(entry.FullName, child);
                    else if (entry is FileInfo) files.Add((entry.FullName, child));
                }
            }
            Walk(host, vfs);
        }
        else throw new CliException($"--import: {host}: no such file or directory");

        long total = files.Sum(f => new FileInfo(f.Host).Length);
        if (fs.UsedBytes + total > fs.Capacity)
            throw new CliException($"--import: {host} holds {total} bytes, more than the {fs.Capacity - fs.UsedBytes} free on the simulated disk (raise --disk)");
        try
        {
            foreach (var d in dirs) if (!fs.IsDirectory(d)) fs.CreateDirectory(d, parents: true);
            foreach (var (h, v) in files)
            {
                var parent = VirtualFileSystem.GetParent(v);
                if (!fs.IsDirectory(parent)) fs.CreateDirectory(parent, parents: true);
                WriteFile(fs, v, File.ReadAllBytes(h));
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(h) & UnixFileMode.UserExecute) != 0) fs.Chmod(v, fs.Stat(v).Mode | 0b001_001_001);
            }
            // the agent can edit what it was given
            fs.SetOwner(vfs, shell.Options.User, recursive: true);
        }
        catch (ShellException e) { throw new CliException($"--import: {e.Message}"); }
    }

    static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Text when it is valid UTF-8, exact bytes otherwise, the same as the host-program mirror does.</summary>
    static void WriteFile(VirtualFileSystem fs, string path, byte[] data)
    {
        try { fs.WriteAllText(path, StrictUtf8.GetString(data)); }
        catch (DecoderFallbackException) { fs.WriteAllBytes(path, data); }
    }

    /// <summary>
    /// Copies a virtual file or directory out to the host. Existing host files at the same paths are overwritten, nothing else
    /// is deleted, and nothing is written through a host symlink or outside <paramref name="host"/>.
    /// </summary>
    public static void Export(Shell shell, string vfs, string host, TextWriter log)
    {
        var fs = shell.Fs;
        vfs = shell.ResolvePath(vfs);
        if (!fs.Exists(vfs)) throw new CliException($"--export: {vfs}: no such file or directory in the machine");
        var root = Path.GetFullPath(host);
        bool IsLink(string p) => (File.Exists(p) || Directory.Exists(p)) && new FileInfo(p).LinkTarget != null;
        void Copy(string v, string h)
        {
            if (h != root && (!Path.GetFullPath(h).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || IsLink(h))) { log.WriteLine($"topwater: export: skipped {v} (would write outside {root} or through a link)"); return; }
            if (fs.IsDirectory(v))
            {
                Directory.CreateDirectory(h);
                foreach (var name in fs.List(v)) Copy(v == "/" ? "/" + name : v + "/" + name, Path.Combine(h, name));
            }
            else File.WriteAllBytes(h, fs.ReadAllBytes(v));
        }
        Copy(vfs, root);
    }
}

/// <summary>A user error: printed without a stack trace, exit code 2.</summary>
sealed class CliException(string message) : Exception(message);
