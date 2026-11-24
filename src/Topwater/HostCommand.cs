using System.Diagnostics;
using System.Text;

namespace Topwater;

/// <summary>Options for <see cref="HostCommandExtensions.RegisterHostCommand"/>.</summary>
public sealed class HostCommandOptions
{
    /// <summary>Virtual directory mirrored to disk for the program. Defaults to the agent's home.</summary>
    public string? Workspace { get; init; }
    /// <summary>Extra environment variables for the program.</summary>
    public IDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>File or directory names kept out of the sync in both directions.</summary>
    public ICollection<string> Exclude { get; init; } = [];
    /// <summary>Other virtual directories the program can see besides the workspace. Agents put scratch files in /tmp.</summary>
    public ICollection<string> SharedDirectories { get; init; } = ["/tmp"];
    /// <summary>Rewrites the command line, e.g. to prefix a real sandbox: (argv, mirrorRoot) => argv.</summary>
    public Func<IReadOnlyList<string>, string, IReadOnlyList<string>>? Launcher { get; init; }
    /// <summary>Redirect path-like arguments into the mirror (on by default).</summary>
    public bool RewritePaths { get; init; } = true;
    public int MaxOutputBytes { get; init; } = 1_000_000;
    public string? Description { get; init; }
}

/// <summary>
/// A real program from the host, exposed to the agent as a command.
///
/// The agent's files live in memory, and real programs need real files. So each registered program gets a private
/// mirror directory on disk. On every call the workspace is copied out to the mirror, the program runs there with
/// the agent's arguments, and whatever it created, changed or deleted is copied back.
///
/// THIS OPENS A DOOR IN THE SANDBOX. The program is real and runs with your process's permissions. Topwater keeps
/// it pointed at the mirror (working directory, HOME, and path-like arguments are redirected there), but that is
/// best effort, not a security boundary: a program that can run other programs, read arbitrary paths or reach the
/// network can still do so. Wrap only tools you would let the agent use directly, and set
/// <see cref="HostCommandOptions.Launcher"/> to run them inside a real sandbox when the stakes call for it.
/// </summary>
public sealed class HostCommand : IDisposable
{
    readonly Shell _shell;
    readonly string _name, _program, _workspace;
    readonly HostCommandOptions _o;
    readonly List<string> _shared; // every directory mirrored to disk: the workspace first, then the extras

    readonly string _privateTmp;

    /// <summary>The mirror of "/" on disk for this program.</summary>
    public string Root { get; }

    internal HostCommand(Shell shell, string name, string program, HostCommandOptions options)
    {
        _shell = shell;
        _name = name;
        _o = options;
        _program = FindOnPath(program) ?? throw new FileNotFoundException($"host program not found: {program}");
        _workspace = VirtualFileSystem.Normalize("/", options.Workspace ?? shell.Environment.GetValueOrDefault("HOME", "/home/agent"));
        _shared = [_workspace];
        foreach (var dir in options.SharedDirectories.Select(d => VirtualFileSystem.Normalize("/", d)))
            if (!_shared.Any(s => dir == s || dir.StartsWith(s.TrimEnd('/') + "/"))) _shared.Add(dir);
        Root = Directory.CreateTempSubdirectory($"topwater-{name}-").FullName;
        _privateTmp = Directory.CreateTempSubdirectory($"topwater-{name}-tmp-").FullName;
    }

    public void Dispose()
    {
        foreach (var dir in new[] { Root, _privateTmp })
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static string? FindOnPath(string program)
    {
        if (program.Contains(Path.DirectorySeparatorChar)) return File.Exists(program) ? Path.GetFullPath(program) : null;
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : [""];
        foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
                if (File.Exists(Path.Combine(dir, program + ext))) return Path.Combine(dir, program + ext);
        return null;
    }

    string Real(string vfsPath) => Path.Combine(Root, vfsPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    bool Skip(string name) => _o.Exclude.Contains(name);
    static bool IsLink(string path) => new FileInfo(path).LinkTarget != null;
    static string Rel(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    // ---------- mirror ----------

    // what each file looked like on both sides when it was last synced, so unchanged files are neither read nor written
    readonly record struct Synced(DateTime Modified, long Size, DateTime MirrorWrite, long MirrorSize);
    readonly Dictionary<string, Synced> _synced = new(StringComparer.Ordinal);

    bool Unchanged(string vpath, FileInfo real, FileStat? virt = null) =>
        _synced.TryGetValue(vpath, out var s) && real.Exists && real.Length == s.MirrorSize && real.LastWriteTimeUtc == s.MirrorWrite
        && (virt is not { } st || st.Modified == s.Modified && st.Size == s.Size);

    // shortcut: a file counts as unchanged while its size and modification time match on both sides, so a same-size
    // rewrite that keeps the timestamp is missed. On disk the program's writes get the kernel's (coarse) clock, while
    // we stamp each synced file with the virtual file's 100ns-precise time, so a program's write can't land on it. In
    // memory every write sets a fresh DateTime.Now; the gap is `mv` (it keeps the moved file's time) onto a path whose
    // old file had the same size and time, which only the back-dated files of a new Shell share. Compare content if that matters.
    void Remember(string vpath, string real, FileStat st)
    {
        try { File.SetLastWriteTimeUtc(real, st.Modified.ToUniversalTime()); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var fi = new FileInfo(real);
        _synced[vpath] = new(st.Modified, st.Size, fi.LastWriteTimeUtc, fi.Length);
    }

    /// <summary>Copies the workspace out to the mirror; returns the relative paths of the files exported.</summary>
    HashSet<string> Export(string ws)
    {
        var fs = _shell.Fs;
        var files = new HashSet<string>(StringComparer.Ordinal);
        var dirs = new HashSet<string>(StringComparer.Ordinal) { "" };
        void Walk(string vpath, string rel)
        {
            if (!fs.CanRead(vpath)) return;
            foreach (var entry in fs.List(vpath))
            {
                if (Skip(entry)) continue;
                var child = VirtualFileSystem.Normalize(vpath, entry);
                var crel = Rel(rel, entry);
                if (fs.IsDirectory(child)) { dirs.Add(crel); Walk(child, crel); }
                else if (fs.CanRead(child)) files.Add(crel);
            }
        }
        if (fs.IsDirectory(ws)) Walk(ws, "");

        var root = Real(ws);
        Directory.CreateDirectory(root);
        // remove what no longer exists in the virtual filesystem, then write what changed
        void Prune(string real, string rel)
        {
            foreach (var d in Directory.GetDirectories(real))
            {
                var name = Path.GetFileName(d);
                if (Skip(name)) continue;
                var crel = Rel(rel, name);
                // a link the program made (git checkout, npm) stays, unless the agent now has a real entry of that
                // name. It is never followed: writing or pruning through it would touch files outside the mirror.
                if (IsLink(d)) { if (dirs.Contains(crel) || files.Contains(crel)) Directory.Delete(d); }
                else if (!dirs.Contains(crel)) Directory.Delete(d, recursive: true);
                else Prune(d, crel);
            }
            foreach (var f in Directory.GetFiles(real))
            {
                var name = Path.GetFileName(f);
                if (Skip(name)) continue;
                var crel = Rel(rel, name);
                if (IsLink(f) ? files.Contains(crel) || dirs.Contains(crel) : !files.Contains(crel) || dirs.Contains(crel)) File.Delete(f);
            }
        }
        Prune(root, "");
        foreach (var d in dirs) Directory.CreateDirectory(Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar)));
        foreach (var rel in files)
        {
            var target = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            var vpath = VirtualFileSystem.Normalize(ws, rel);
            var st = fs.Stat(vpath);
            if (!Unchanged(vpath, new FileInfo(target), st))
            {
                File.WriteAllBytes(target, fs.ReadAllBytes(vpath));
                Remember(vpath, target, st);
            }
            // scripts the agent made executable must be executable for the program too
            if (!OperatingSystem.IsWindows() && (st.Mode & 0b001_000_000) != 0)
                File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        return files;
    }

    /// <summary>Copies changes made by the program back into the virtual filesystem.</summary>
    void Import(string ws, HashSet<string> before, TextWriter err)
    {
        var fs = _shell.Fs;
        var root = Real(ws);
        var after = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string real, string rel)
        {
            var vdir = rel.Length == 0 ? ws : VirtualFileSystem.Normalize(ws, rel);
            try { if (!fs.Exists(vdir)) fs.CreateDirectory(vdir, parents: true); }
            catch (ShellException e) { err.WriteLine($"{_name}: {e.Message}"); return; }
            foreach (var f in Directory.GetFiles(real))
            {
                var name = Path.GetFileName(f);
                var info = new FileInfo(f);
                if (Skip(name) || info.LinkTarget != null) continue;
                var crel = Rel(rel, name);
                after.Add(crel);
                var vfile = VirtualFileSystem.Normalize(vdir, name);
                // untouched by the program: whatever the virtual file holds now stays
                if (before.Contains(crel) && Unchanged(vfile, info)) continue;
                byte[] data;
                // sockets, pipes and files a still-running helper holds open can't be copied; leave them out
                try { data = File.ReadAllBytes(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                try
                {
                    if (!before.Contains(crel) || !fs.ReadAllBytes(vfile).AsSpan().SequenceEqual(data))
                    {
                        WriteFile(fs, vfile, data);
                        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(f) & UnixFileMode.UserExecute) != 0)
                            fs.Chmod(vfile, fs.Stat(vfile).Mode | 0b001_001_001);
                    }
                    Remember(vfile, f, fs.Stat(vfile));
                }
                catch (ShellException e) { err.WriteLine($"{_name}: {e.Message}"); }
            }
            foreach (var d in Directory.GetDirectories(real))
            {
                var name = Path.GetFileName(d);
                if (!Skip(name) && new DirectoryInfo(d).LinkTarget == null) Walk(d, Rel(rel, name));
            }
        }
        Walk(root, "");
        foreach (var rel in before.Where(k => !after.Contains(k)))
        {
            try { fs.Delete(VirtualFileSystem.Normalize(ws, rel)); } catch (ShellException) { }
        }
        // directories the program removed
        void PruneVfs(string vpath, string rel)
        {
            if (!fs.CanRead(vpath)) return;
            foreach (var entry in fs.List(vpath))
            {
                var child = VirtualFileSystem.Normalize(vpath, entry);
                if (Skip(entry) || !fs.IsDirectory(child)) continue;
                var crel = Rel(rel, entry);
                if (!Directory.Exists(Path.Combine(root, crel.Replace('/', Path.DirectorySeparatorChar))))
                {
                    try { fs.Delete(child, recursive: true); } catch (ShellException) { }
                }
                else PruneVfs(child, crel);
            }
        }
        if (fs.IsDirectory(ws)) PruneVfs(ws, "");
    }

    static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    static void WriteFile(VirtualFileSystem fs, string path, byte[] data)
    {
        if (Array.IndexOf(data, (byte)0) < 0)
        {
            try { fs.WriteAllText(path, StrictUtf8.GetString(data)); return; }
            catch (DecoderFallbackException) { }
        }
        fs.WriteAllBytes(path, data);
    }

    // ---------- arguments ----------

    /// <summary>Redirects path-like arguments into the mirror so they can't point at the host's own files.</summary>
    string MapArg(string arg)
    {
        if (!_o.RewritePaths) return arg;
        string prefix = "", value = arg;
        // --out=/path and key=/path (e.g. `--prop src=/tmp/a.md`) carry a path after the first '='
        if (arg.IndexOf('=') is var eq and > 0 && !arg[..eq].Contains('/')) { prefix = arg[..(eq + 1)]; value = arg[(eq + 1)..]; }
        // -o/path and -C/path: a short option with the path attached
        if (prefix.Length == 0 && arg.Length > 3 && arg[0] == '-' && arg[1] != '-' && arg[2] == '/') { prefix = arg[..2]; value = arg[2..]; }
        if (value == "/") return prefix + Root;
        if (value.StartsWith('/'))
        {
            var top = "/" + value.Trim('/').Split('/')[0];
            // a real location (in the simulation or on the host) is redirected; other things that merely start
            // with "/" (XPath-like selectors such as /slide[1], regexes) are left alone.
            // shortcut: a path whose top folder exists nowhere (-o /report.pdf) is therefore passed through as typed;
            // use Launcher with a real sandbox if the program must not be able to write outside the mirror.
            if (_shell.Fs.Exists(top) || Directory.Exists(top) || File.Exists(top))
            {
                var path = VirtualFileSystem.Normalize("/", value);
                // only the shared folders are copied to disk; say so instead of running on a file that isn't there
                if (_shell.Fs.Exists(top) && !_shared.Any(s => path == s || path.StartsWith(s.TrimEnd('/') + "/") || s.StartsWith(path.TrimEnd('/') + "/")))
                    throw new ShellException($"{value}: {_name} can only use files under {string.Join(" and ", _shared)}; copy it there first");
                return prefix + Real(path);
            }
        }
        else if (value.Split('/').Contains(".."))
            return prefix + Real(_shell.ResolvePath(value));
        return arg;
    }

    // ---------- run ----------

    internal async Task<int> RunAsync(CommandContext ctx)
    {
        var before = _shared.ToDictionary(ws => ws, Export);
        var cwd = _shell.Cwd;
        bool inside = _shared.Any(s => cwd == s || cwd.StartsWith(s.TrimEnd('/') + "/"));
        var realCwd = Real(inside ? cwd : _workspace);
        Directory.CreateDirectory(realCwd);
        IReadOnlyList<string> argv = [_program, .. ctx.Args.Select(MapArg)];
        if (_o.Launcher != null) argv = _o.Launcher(argv, Root);

        var home = Real(_shell.Environment.GetValueOrDefault("HOME", _workspace));
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Real("/tmp"));
        var psi = new ProcessStartInfo(argv[0])
        {
            WorkingDirectory = realCwd, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        // the agent's own variables (GIT_AUTHOR_NAME=x git commit) come first; the fixed ones below win
        foreach (var (k, v) in _shell.Environment) psi.Environment[k] = v;
        foreach (var k in new[] { "PWD", "OLDPWD", "SHELL" }) psi.Environment.Remove(k);
        psi.Environment["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        psi.Environment["HOME"] = home;
        psi.Environment["LANG"] = "C.UTF-8";
        psi.Environment["TERM"] = "dumb";
        psi.Environment["USER"] = psi.Environment["LOGNAME"] = _shell.Options.User;
        // the program's own temp files (runtime sockets, scratch files) stay out of the agent's /tmp
        psi.Environment["TMPDIR"] = _privateTmp;
        foreach (var (k, v) in _o.Environment) psi.Environment[k] = v;

        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("process did not start"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ctx.Stderr.WriteLine($"{_name}: cannot execute: {e.Message}");
            return 126;
        }
        using (proc)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            cts.CancelAfter(_o.Timeout);
            using var readCts = new CancellationTokenSource();
            MemoryStream outBuf = new(), errBuf = new();
            var readers = Task.WhenAll(PumpAsync(proc.StandardOutput.BaseStream, outBuf, readCts.Token), PumpAsync(proc.StandardError.BaseStream, errBuf, readCts.Token));
            try
            {
                var input = Encoding.UTF8.GetBytes(ctx.StdinText);
                await proc.StandardInput.BaseStream.WriteAsync(input, cts.Token);
                proc.StandardInput.Close();
            }
            catch (IOException) { } // the program closed stdin early
            catch (OperationCanceledException) { }
            bool timedOut = false;
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                timedOut = true;
            }
            // a child the program left running may hold the pipes open for good; take what has arrived and move on
            if (await Task.WhenAny(readers, Task.Delay(2000)) != readers)
            {
                readCts.Cancel();
                await Task.WhenAny(readers, Task.Delay(500));
            }
            byte[] Snapshot(MemoryStream ms) { lock (ms) return ms.ToArray(); }
            var (outText, errText) = (Clean(Snapshot(outBuf)), Clean(Snapshot(errBuf)));
            foreach (var ws in _shared) Import(ws, before[ws], ctx.Stderr);
            ctx.CancellationToken.ThrowIfCancellationRequested();
            ctx.Stdout.Write(outText);
            ctx.Stderr.Write(errText);
            if (timedOut)
            {
                ctx.Stderr.WriteLine($"{_name}: timed out after {_o.Timeout.TotalSeconds:0.#}s");
                return 124;
            }
            return proc.ExitCode;
        }
    }

    async Task PumpAsync(Stream stream, MemoryStream into, CancellationToken ct)
    {
        var buffer = new byte[16384];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                lock (into)
                    if (into.Length < _o.MaxOutputBytes) into.Write(buffer, 0, (int)Math.Min(n, _o.MaxOutputBytes - into.Length));
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    // show virtual paths, not mirror paths
    string Clean(byte[] data) => Encoding.UTF8.GetString(data).Replace(Root + Path.DirectorySeparatorChar, "/").Replace(Root, "/");
}

/// <summary>Ready-made <see cref="HostCommandOptions.Launcher"/>s that run the host program inside a real sandbox.</summary>
public static class HostSandbox
{
    static readonly string[] Etc =
    [
        "/etc/passwd", "/etc/group", "/etc/nsswitch.conf", "/etc/hosts", "/etc/host.conf", "/etc/resolv.conf", "/etc/gai.conf",
        "/etc/ssl", "/etc/ca-certificates", "/etc/pki", "/etc/alternatives", "/etc/ld.so.cache", "/etc/ld.so.conf", "/etc/ld.so.conf.d",
        "/etc/localtime", "/etc/mime.types", "/etc/ssh/ssh_config", "/etc/ssh/ssh_config.d",
    ];

    /// <summary>
    /// Runs the program under bubblewrap (Linux). It sees only the mirror (read-write, at its real path, so HOME, the
    /// working directory and redirected arguments still resolve), a fresh private /tmp, /usr and the /bin and /lib
    /// directories, a few files from /etc needed to run and resolve names, the program itself and
    /// <paramref name="readOnlyPaths"/>. Your home directory and the rest of the disk are not there; the network is
    /// off unless <paramref name="network"/> (git clone/push need it). Throws if bwrap is missing or can't make a sandbox here.
    /// </summary>
    public static Func<IReadOnlyList<string>, string, IReadOnlyList<string>> Bubblewrap(bool network = false, params string[] readOnlyPaths)
    {
        var bwrap = HostCommand.FindOnPath("bwrap") ?? throw new FileNotFoundException("bwrap is not installed (apt install bubblewrap); it is needed for HostSandbox.Bubblewrap");
        // fail now, not on the agent's first call: unprivileged user namespaces may be disabled (e.g. by AppArmor)
        var probe = Process.Start(new ProcessStartInfo(bwrap) { ArgumentList = { "--unshare-all", "--ro-bind", "/", "/", "true" }, RedirectStandardError = true, UseShellExecute = false })!;
        var why = probe.StandardError.ReadToEnd();
        probe.WaitForExit();
        if (probe.ExitCode != 0) throw new InvalidOperationException($"bwrap cannot create a sandbox on this machine: {why.Trim()}");
        return (argv, root) =>
        {
            List<string> a = [bwrap, "--unshare-all", "--die-with-parent", "--new-session", "--proc", "/proc", "--dev", "/dev",
                "--tmpfs", "/tmp", "--setenv", "TMPDIR", "/tmp", "--ro-bind", "/usr", "/usr"];
            if (network) a.Add("--share-net");
            // merged-/usr systems have /bin -> usr/bin; recreate the link rather than binding through it
            foreach (var dir in new[] { "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32" })
                if (new DirectoryInfo(dir) is { Exists: true } d) a.AddRange(d.LinkTarget is { } target ? ["--symlink", target, dir] : ["--ro-bind", dir, dir]);
            foreach (var p in Etc.Concat(readOnlyPaths).Append(argv[0]).Where(p => !p.StartsWith("/usr/")))
                a.AddRange(["--ro-bind-try", p, p]);
            a.AddRange(["--bind", root, root, "--", .. argv]);
            return a;
        };
    }
}

public static class HostCommandExtensions
{
    /// <summary>
    /// Exposes a real host program to the agent as <paramref name="name"/>. Read the warning on <see cref="HostCommand"/>
    /// first: this runs a real program with the agent's arguments. Dispose the result to remove its mirror directory.
    /// </summary>
    public static HostCommand RegisterHostCommand(this Shell shell, string name, string? program = null, HostCommandOptions? options = null)
    {
        options ??= new HostCommandOptions();
        var cmd = new HostCommand(shell, name, program ?? name, options);
        shell.Register(name, cmd.RunAsync, options.Description ?? $"{name} (host program)");
        return cmd;
    }
}
