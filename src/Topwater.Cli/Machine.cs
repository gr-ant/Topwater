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
        RefuseHistory(options.State, shell.Options);
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

    /// <summary>
    /// A state file is rewritten after every command as the machine alone, so a recording there would lose its history
    /// and checkpoints: refused, pointing at the ways that leave it as it is.
    /// </summary>
    public static void RefuseHistory(string? state, ShellOptions limits)
    {
        if (state == null || !File.Exists(state)) return;
        bool history;
        try
        {
            using var file = File.OpenRead(state);
            history = ShellSnapshot.HasHistory(file, limits);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new CliException($"{state}: {e.Message}");
        }
        if (history)
            throw new CliException($"{state} is a recording; --state would overwrite it with the machine alone and lose its history. " +
                $"Start from it with --from {state} (never written), or copy its machine into a new state file with: topwater import {state} --state NEW");
    }

    /// <summary>Writes the state file, if there is one. Called after every command so a killed process loses at most one.</summary>
    public void Save()
    {
        if (_options.State is not { } state) return;
        try { SaveTo(Shell, state); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.WriteLine($"topwater: could not save {state}: {e.Message}"); }
    }

    /// <summary>Saves the state, then copies each <c>--export</c> path out to the host; false if an export failed.</summary>
    public bool Close()
    {
        Save();
        bool ok = true;
        foreach (var (vfs, host) in _options.Exports)
        {
            try { Export(Shell, vfs, host, _log); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ShellException) { _log.WriteLine($"topwater: export {vfs} -> {host}: {e.Message}"); ok = false; }
        }
        return ok;
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

    // Windows and macOS file systems ignore case by default, so C:\Out and c:\out are the same folder there
    static readonly StringComparison HostPathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
        var top = fs.RealPath(vfs, out _) ?? vfs;
        void Copy(string v, string h)
        {
            if (h != root && (!Path.GetFullPath(h).StartsWith(root + Path.DirectorySeparatorChar, HostPathComparison) || IsLink(h))) { log.WriteLine($"topwater: export: skipped {v} (would write outside {root} or through a link)"); return; }
            // a virtual link is never followed into a directory (it could loop, fan out or reach the whole machine), and
            // goes out as a copy of its file only when that file is inside what is exported; no host link is made
            if (v != vfs && fs.IsSymlink(v) && !(fs.IsFile(v) && fs.RealPath(v, out _) is { } real && real.StartsWith(top + "/", StringComparison.Ordinal)))
            {
                log.WriteLine($"topwater: export: skipped {v} (a symbolic link to {fs.ReadLink(v)}, not a file inside {vfs})");
                return;
            }
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
