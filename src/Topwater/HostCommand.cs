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

    /// <summary>The mirror of "/" on disk for this program.</summary>
    public string Root { get; }

    internal HostCommand(Shell shell, string name, string program, HostCommandOptions options)
    {
        _shell = shell;
        _name = name;
        _o = options;
        _program = FindOnPath(program) ?? throw new FileNotFoundException($"host program not found: {program}");
        _workspace = VirtualFileSystem.Normalize("/", options.Workspace ?? shell.Environment.GetValueOrDefault("HOME", "/home/agent"));
        Root = Directory.CreateTempSubdirectory($"topwater-{name}-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string? FindOnPath(string program)
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
    static string Rel(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    // ---------- mirror ----------

    /// <summary>Copies the workspace out to the mirror; returns {relative path: bytes} as exported.</summary>
    Dictionary<string, byte[]> Export()
    {
        var fs = _shell.Fs;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
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
                else if (fs.CanRead(child)) files[crel] = fs.ReadAllBytes(child);
            }
        }
        if (fs.IsDirectory(_workspace)) Walk(_workspace, "");

        var root = Real(_workspace);
        Directory.CreateDirectory(root);
        // remove what no longer exists in the virtual filesystem, then write what changed
        void Prune(string real, string rel)
        {
            foreach (var d in Directory.GetDirectories(real))
            {
                var name = Path.GetFileName(d);
                if (Skip(name)) continue;
                var crel = Rel(rel, name);
                if (!dirs.Contains(crel)) Directory.Delete(d, recursive: true); else Prune(d, crel);
            }
            foreach (var f in Directory.GetFiles(real))
            {
                var name = Path.GetFileName(f);
                if (!Skip(name) && !files.ContainsKey(Rel(rel, name))) File.Delete(f);
            }
        }
        Prune(root, "");
        foreach (var d in dirs) Directory.CreateDirectory(Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar)));
        foreach (var (rel, data) in files)
        {
            var target = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(data)) File.WriteAllBytes(target, data);
        }
        return files;
    }

    /// <summary>Copies changes made by the program back into the virtual filesystem.</summary>
    void Import(Dictionary<string, byte[]> before, TextWriter err)
    {
        var fs = _shell.Fs;
        var root = Real(_workspace);
        var after = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string real, string rel)
        {
            var vdir = rel.Length == 0 ? _workspace : VirtualFileSystem.Normalize(_workspace, rel);
            try { if (!fs.Exists(vdir)) fs.CreateDirectory(vdir, parents: true); }
            catch (ShellException e) { err.WriteLine($"{_name}: {e.Message}"); return; }
            foreach (var f in Directory.GetFiles(real))
            {
                var name = Path.GetFileName(f);
                if (Skip(name) || new FileInfo(f).LinkTarget != null) continue;
                var crel = Rel(rel, name);
                after.Add(crel);
                var data = File.ReadAllBytes(f);
                if (before.TryGetValue(crel, out var old) && old.AsSpan().SequenceEqual(data)) continue;
                try { WriteFile(fs, VirtualFileSystem.Normalize(vdir, name), data); }
                catch (ShellException e) { err.WriteLine($"{_name}: {e.Message}"); }
            }
            foreach (var d in Directory.GetDirectories(real))
            {
                var name = Path.GetFileName(d);
                if (!Skip(name) && new DirectoryInfo(d).LinkTarget == null) Walk(d, Rel(rel, name));
            }
        }
        Walk(root, "");
        foreach (var rel in before.Keys.Where(k => !after.Contains(k)))
        {
            try { fs.Delete(VirtualFileSystem.Normalize(_workspace, rel)); } catch (ShellException) { }
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
        if (fs.IsDirectory(_workspace)) PruneVfs(_workspace, "");
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
        if (arg.StartsWith('-') && arg.IndexOf('=') is var eq and > 0) { prefix = arg[..(eq + 1)]; value = arg[(eq + 1)..]; }
        if (value.StartsWith('/') && value != "/")
        {
            var top = "/" + value.Trim('/').Split('/')[0];
            // a real location (in the simulation or on the host) is redirected; other things that merely start
            // with "/" (XPath-like selectors such as /slide[1], regexes) are left alone
            if (_shell.Fs.Exists(top) || Directory.Exists(top) || File.Exists(top))
                return prefix + Real(VirtualFileSystem.Normalize("/", value));
        }
        else if (value.Split('/').Contains(".."))
            return prefix + Real(_shell.ResolvePath(value));
        return arg;
    }

    // ---------- run ----------

    internal async Task<int> RunAsync(CommandContext ctx)
    {
        var before = Export();
        var cwd = _shell.Cwd;
        bool inside = cwd == _workspace || cwd.StartsWith(_workspace.TrimEnd('/') + "/");
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
        psi.Environment["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        psi.Environment["HOME"] = home;
        psi.Environment["LANG"] = "C.UTF-8";
        psi.Environment["TERM"] = "dumb";
        psi.Environment["USER"] = psi.Environment["LOGNAME"] = _shell.Options.User;
        psi.Environment["TMPDIR"] = Real("/tmp");
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
            var stdout = ReadCappedAsync(proc.StandardOutput.BaseStream);
            var stderr = ReadCappedAsync(proc.StandardError.BaseStream);
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
            var (outText, errText) = (Clean(await stdout), Clean(await stderr));
            Import(before, ctx.Stderr);
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

    async Task<byte[]> ReadCappedAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
            if (ms.Length < _o.MaxOutputBytes) ms.Write(buffer, 0, (int)Math.Min(n, _o.MaxOutputBytes - ms.Length));
        return ms.ToArray();
    }

    // show virtual paths, not mirror paths
    string Clean(byte[] data) => Encoding.UTF8.GetString(data).Replace(Root + Path.DirectorySeparatorChar, "/").Replace(Root, "/");
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
