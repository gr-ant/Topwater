using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;

namespace Topwater.Replay;

public sealed class DockerTargetOptions
{
    /// <summary>The base image. The default is the release Topwater imitates.</summary>
    public string Image { get; init; } = "ubuntu:24.04";
    /// <summary>
    /// apt packages added on top of <see cref="Image"/> (built once, then cached as a local image), so the real machine has
    /// the programs Topwater provides that the base image lacks. Empty: use the image as it is. Building needs the network.
    /// </summary>
    public IReadOnlyList<string> Packages { get; init; } = ["jq", "tree", "file", "less", "bc", "zip", "unzip", "xxd", "bsdextrautils", "procps"];
    /// <summary>Longest one call may run; past it the call is killed and returns 124, like Topwater's time limit.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Give the container a network. Off by default, as Topwater has none.</summary>
    public bool Network { get; init; }
    public string Docker { get; init; } = "docker";
}

/// <summary>
/// Replays on real bash in a throwaway Docker container that starts from a snapshot's files, user, host name, working
/// directory and exported variables. Each call is a fresh <c>bash -c</c>; the working directory, variables and functions are
/// saved after it and restored before the next, so state carries over as in a Topwater shell (aliases and shell options don't).
/// </summary>
public sealed class DockerTarget : IReplayTarget
{
    const string StateDir = "/run/topwater-replay";
    readonly DockerTargetOptions _o;
    readonly string _container, _user;
    readonly List<string> _env;

    DockerTarget(DockerTargetOptions o, string container, string user, List<string> env, string image)
    {
        _o = o; _container = container; _user = user; _env = env; Name = "docker " + image;
    }

    public string Name { get; }

    /// <summary>Starts a container holding <paramref name="start"/>'s machine. Throws when docker isn't usable.</summary>
    public static async Task<DockerTarget> StartAsync(ShellSnapshot start, DockerTargetOptions? options = null, CancellationToken ct = default)
    {
        var o = options ?? new DockerTargetOptions();
        var image = await EnsureImageAsync(o, ct);
        var shell = new Shell(new ShellOptions { User = start.User, Hostname = start.Hostname, MaxFileSystemBytes = long.MaxValue, MaxExtractEntries = int.MaxValue });
        shell.Restore(start);
        var pristine = new Shell(new ShellOptions { User = start.User, Hostname = start.Hostname });
        var container = "topwater-replay-" + Guid.NewGuid().ToString("N")[..12];
        if (!AccountName.IsMatch(start.User)) throw new ArgumentException($"snapshot user {start.User} is not an account name a container can hold");
        // commands may come from untrusted recordings: bound memory, swap, CPU, processes and file size; no capabilities beyond
        // what the root setup step needs to write the files with their owners, and nothing can gain privileges (setuid)
        var run = new List<string>
        {
            "run", "-d", "--rm", "--name", container, "--hostname", start.Hostname, "--memory", "1g", "--memory-swap", "1g", "--cpus", "1",
            "--pids-limit", "512", "--ulimit", "fsize=1073741824", "--cap-drop", "ALL",
            "--cap-add", "CHOWN", "--cap-add", "DAC_OVERRIDE", "--cap-add", "FOWNER", "--security-opt", "no-new-privileges",
        };
        if (!o.Network) run.AddRange(["--network", "none"]);
        run.AddRange([image, "sleep", "infinity"]);
        await RunDockerAsync(o, run, null, ct, check: true);
        var target = new DockerTarget(o, container, start.User, [], image);
        try
        {
            var (tar, owners) = FilesTar(shell, pristine);
            var setup = new StringBuilder("set -e\n");
            var user = Quote(start.User);
            if (start.User != "root")
                setup.Append($"userdel -r ubuntu 2>/dev/null || true\nid -u {user} >/dev/null 2>&1 || useradd -m -u 1000 -s /bin/bash {user}\nchmod 750 /home/{user}\n");
            // an owner no account can be named keeps its number only
            foreach (var (name, uid) in owners)
                if (name != "root" && name != start.User && AccountName.IsMatch(name)) setup.Append($"id -u {Quote(name)} >/dev/null 2>&1 || useradd -M -u {uid} {Quote(name)}\n");
            setup.Append($"mkdir -p {StateDir}\nchown {user} {StateDir}\n");
            await target.ExecAsync(["-u", "root"], ["bash", "-c", setup.ToString()], null, ct, check: true);
            if (tar.Length > 0) await target.ExecAsync(["-i", "-u", "root"], ["tar", "-xpf", "-", "-C", "/", "--numeric-owner"], tar, ct, check: true);

            // the session: exported variables become the environment, the rest (and functions) the saved state
            var env = new StringBuilder();
            foreach (var (k, v) in shell.Environment)
                if (k is not ("PWD" or "OLDPWD" or "SHLVL" or "_") && VariableName.IsMatch(k)) env.Append($"export {k}={Quote(v)}\n");
            var state = Encoding.UTF8.GetBytes(env.ToString());
            await target.ExecAsync(["-i", "-u", start.User], ["bash", "-c", $"cat > {StateDir}/env && printf %s {Quote(shell.Cwd)} > {StateDir}/cwd"], state, ct, check: true);
            target._env.AddRange(new[] { "HOME", "USER", "LOGNAME", "SHELL", "PATH", "LANG", "TERM" }.Where(shell.Environment.ContainsKey).Select(k => $"{k}={shell.Environment[k]}"));
            return target;
        }
        catch
        {
            await target.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs <c>docker version</c>: true when a daemon answers, so tests can skip without docker.</summary>
    public static async Task<bool> IsAvailableAsync(string docker = "docker")
    {
        try { return (await RunDockerAsync(new DockerTargetOptions { Docker = docker }, ["version", "--format", "{{.Server.Version}}"], null, default)).Code == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    // restore state, run the command, save state on the way out (also after `exit N`)
    const string Wrapper = $$"""
        __tw_s={{StateDir}}
        [ -r $__tw_s/env ] && . $__tw_s/env 2>/dev/null
        cd "$(cat $__tw_s/cwd 2>/dev/null)" 2>/dev/null || cd
        __tw_save() {
          __tw_rc=$?
          {
            for __tw_v in $(compgen -v); do
              case $__tw_v in BASH*|EUID|PPID|UID|SHELLOPTS|BASHOPTS|FUNCNAME|GROUPS|DIRSTACK|PIPESTATUS|RANDOM|SRANDOM|SECONDS|LINENO|EPOCH*|HISTCMD|_|OLDPWD|PWD|SHLVL|COPROC*|__tw_*) continue;; esac
              __tw_d=$(declare -p "$__tw_v" 2>/dev/null) || continue
              __tw_a=${__tw_d#declare }; __tw_a=${__tw_a%% *}
              [[ $__tw_a == *r* ]] && continue
              printf '%s\n' "${__tw_d/#declare /declare -g }"
            done
            for __tw_v in $(compgen -A function); do [[ $__tw_v == __tw_* ]] || declare -f "$__tw_v"; done
          } > $__tw_s/env.new 2>/dev/null && mv -f $__tw_s/env.new $__tw_s/env
          pwd > $__tw_s/cwd 2>/dev/null
          exit $__tw_rc
        }
        trap __tw_save EXIT
        eval "$1"
        """;

    public async Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        var flags = new List<string> { "-u", _user, "-w", "/" };
        foreach (var e in _env) flags.AddRange(["-e", e]);
        var seconds = Math.Max(1, (int)_o.CallTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (code, stdout, stderr) = await ExecAsync(flags, ["timeout", "-k", "5", seconds, "bash", "-c", Wrapper, "bash", command], null, cancellationToken);
        return new ShellResult(stdout, stderr, code);
    }

    public async ValueTask DisposeAsync()
    {
        try { await RunDockerAsync(_o, ["rm", "-f", _container], null, default); } catch (Exception) { /* best effort */ }
    }

    Task<(int Code, string Stdout, string Stderr)> ExecAsync(List<string> flags, IEnumerable<string> argv, byte[]? stdin, CancellationToken ct, bool check = false) =>
        RunDockerAsync(_o, ["exec", .. flags, _container, .. argv], stdin, ct, check);

    static async Task<(int Code, string Stdout, string Stderr)> RunDockerAsync(DockerTargetOptions o, IEnumerable<string> args, byte[]? stdin, CancellationToken ct, bool check = false)
    {
        var psi = new ProcessStartInfo(o.Docker) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outTask = ReadAllAsync(p.StandardOutput.BaseStream, ct);
        var errTask = ReadAllAsync(p.StandardError.BaseStream, ct);
        if (stdin != null)
        {
            await p.StandardInput.BaseStream.WriteAsync(stdin, ct);
            p.StandardInput.Close();
        }
        await p.WaitForExitAsync(ct);
        var result = (p.ExitCode, await outTask, await errTask);
        if (check && result.ExitCode != 0) throw new InvalidOperationException($"docker {string.Join(' ', args.Take(3))} failed ({result.ExitCode}): {result.Item3.Trim()}");
        return result;
    }

    static async Task<string> ReadAllAsync(Stream s, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    static async Task<string> EnsureImageAsync(DockerTargetOptions o, CancellationToken ct)
    {
        if (o.Packages.Count == 0) return o.Image;
        var dockerfile = $"FROM {o.Image}\nRUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends {string.Join(' ', o.Packages)} && rm -rf /var/lib/apt/lists/*\n";
        var tag = "topwater-replay:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dockerfile)))[..12].ToLowerInvariant();
        if ((await RunDockerAsync(o, ["image", "inspect", tag], null, ct)).Code == 0) return tag;
        await RunDockerAsync(o, ["build", "-t", tag, "-"], Encoding.UTF8.GetBytes(dockerfile), ct, check: true);
        return tag;
    }

    /// <summary>
    /// A tar of what the snapshot adds to a fresh machine: files and directories a new shell doesn't have or has differently.
    /// What the simulation invents (/proc, /sys, /dev, the program stubs in /usr/bin and /bin) stays out; the image provides the real ones.
    /// </summary>
    static (byte[] Tar, Dictionary<string, int> Owners) FilesTar(Shell shell, Shell pristine)
    {
        var owners = new Dictionary<string, int>(StringComparer.Ordinal) { ["root"] = 0, [shell.Options.User] = shell.Options.User == "root" ? 0 : 1000 };
        int Uid(string owner) => owners.TryGetValue(owner, out var id) ? id : owners[owner] = 1001 + owners.Count;
        var stub = pristine.Fs.ReadAllBytes("/usr/bin/ls");
        var ms = new MemoryStream();
        using (var tar = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            void Walk(string dir)
            {
                foreach (var name in shell.Fs.List(dir))
                {
                    var path = dir == "/" ? "/" + name : dir + "/" + name;
                    if (path is "/proc" or "/sys" or "/dev") continue;
                    var st = shell.Fs.Stat(path);
                    bool known = pristine.Fs.Exists(path);
                    if (st.IsDirectory)
                    {
                        if (!known || pristine.Fs.Stat(path) is var p && (p.Mode != st.Mode || p.Owner != st.Owner)) Add(path, st, null);
                        Walk(path);
                        continue;
                    }
                    var bytes = shell.Fs.ReadAllBytes(path);
                    if (bytes.AsSpan().SequenceEqual(stub)) continue;
                    if (known && pristine.Fs.Stat(path) is var q && q.Mode == st.Mode && q.Owner == st.Owner && pristine.Fs.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
                    Add(path, st, bytes);
                }
            }
            void Add(string path, FileStat st, byte[]? bytes)
            {
                var entry = new PaxTarEntry(bytes == null ? TarEntryType.Directory : TarEntryType.RegularFile, path.TrimStart('/'))
                {
                    Mode = (UnixFileMode)(st.Mode & 0xFFF), Uid = Uid(st.Owner), Gid = Uid(st.Owner), UserName = st.Owner, GroupName = st.Owner,
                    ModificationTime = new DateTimeOffset(DateTime.SpecifyKind(st.Modified.ToUniversalTime(), DateTimeKind.Utc)),
                };
                if (bytes != null) entry.DataStream = new MemoryStream(bytes);
                tar.WriteEntry(entry);
            }
            Walk("/");
        }
        return (ms.ToArray(), owners);
    }

    static readonly System.Text.RegularExpressions.Regex AccountName = new("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}$"), VariableName = new("^[A-Za-z_][A-Za-z0-9_]*$");

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
