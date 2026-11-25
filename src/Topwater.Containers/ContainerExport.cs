using System.Formats.Tar;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Topwater.Containers;

/// <summary>
/// A Docker build context for a snapshot: a Dockerfile, a setup script and a tar layer with the files. Nothing here starts
/// a process; <see cref="ContainerExport.BuildImageAsync"/> hands it to docker. See "Containers" in docs/reference.md.
/// </summary>
public sealed class BuildContext
{
    internal BuildContext(string dockerfile, string setup, string session, byte[] layer, ContainerReport report)
    {
        Dockerfile = dockerfile; SetupScript = setup; SessionScript = session; Layer = layer; Report = report;
    }

    public string Dockerfile { get; }
    /// <summary>setup.sh, run as root while the image builds: creates the accounts, removes deleted files, hooks the session into ~/.bashrc.</summary>
    public string SetupScript { get; }
    /// <summary>The shell state that isn't files (variables, functions, aliases, options, traps) as bash. It lands at /etc/topwater/session.sh.</summary>
    public string SessionScript { get; }
    /// <summary>layer.tar: the files, with numeric owners, modes and modification times.</summary>
    public byte[] Layer { get; }
    /// <summary>What was exported and what couldn't be.</summary>
    public ContainerReport Report { get; }

    /// <summary>Writes Dockerfile, setup.sh and layer.tar into <paramref name="directory"/> (created if missing).</summary>
    public void WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Dockerfile"), Dockerfile);
        File.WriteAllText(Path.Combine(directory, "setup.sh"), SetupScript);
        File.WriteAllBytes(Path.Combine(directory, "layer.tar"), Layer);
    }

    /// <summary>Writes the same three files as one tar, the form <c>docker build -</c> reads from stdin.</summary>
    public void WriteTar(Stream stream)
    {
        using var tar = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: true); // docker detects a context tar by its first header: no PAX extended header
        foreach (var (name, data) in new[] { ("Dockerfile", Encoding.UTF8.GetBytes(Dockerfile)), ("setup.sh", Encoding.UTF8.GetBytes(SetupScript)), ("layer.tar", Layer) })
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data), Mode = (UnixFileMode)420 });
    }
}

/// <summary>Snapshot to container. Host APIs only: no command inside the shell can reach them.</summary>
public static class ContainerExport
{
    public const string DefaultBaseImage = "ubuntu:24.04";

    /// <summary>Generated files in the image: the session script, and the list of paths the export wrote or deleted (read back by an import).</summary>
    internal const string SessionPath = "/etc/topwater/session.sh", PathsPath = "/etc/topwater/paths";

    /// <summary>Appended to the user's ~/.bashrc; an import strips exactly this.</summary>
    internal const string BashrcHook = "\n# Topwater session: variables, functions, aliases and options from the snapshot\n[ -r /etc/topwater/session.sh ] && . /etc/topwater/session.sh\n";

    /// <summary>
    /// The Docker build context for <paramref name="snapshot"/>. Only what differs from a fresh Topwater machine is exported;
    /// <paramref name="baseImage"/> (Ubuntu 24.04, which Topwater imitates) provides the real system. Pure: no process, no disk.
    /// </summary>
    public static BuildContext ToBuildContext(this ShellSnapshot snapshot, string baseImage = DefaultBaseImage) =>
        new Exporter(snapshot, baseImage).Build();

    /// <summary>
    /// Builds an image tagged <paramref name="tag"/> with <c>docker build</c> from <see cref="ToBuildContext"/>, and returns the context,
    /// whose <see cref="BuildContext.Report"/> says what didn't carry over. <c>docker run -it tag</c> then lands in the machine.
    /// </summary>
    public static async Task<BuildContext> BuildImageAsync(this ShellSnapshot snapshot, string tag, string baseImage = DefaultBaseImage, CancellationToken cancellationToken = default)
    {
        var context = snapshot.ToBuildContext(baseImage);
        await Docker.RunAsync(["build", "-q", "-t", tag, "-"], stdin: s => { context.WriteTar(s); return Task.CompletedTask; }, cancellationToken: cancellationToken);
        return context;
    }

    // ---------- never exported, whatever the snapshot holds ----------

    /// <summary>Simulated by Topwater; a container has the real ones.</summary>
    internal static readonly string[] Simulated = ["/proc", "/sys", "/dev", "/run"];

    /// <summary>Accounts are created with useradd/groupadd; copying these files would replace the image's whole account database.</summary>
    internal static readonly HashSet<string> AccountFiles = ["/etc/passwd", "/etc/group", "/etc/shadow", "/etc/gshadow"];

    /// <summary>Docker writes these into every container when it starts.</summary>
    internal static readonly HashSet<string> DockerManaged = ["/etc/hostname", "/etc/hosts", "/etc/resolv.conf"];

    /// <summary>The program stub Topwater writes to /bin and /usr/bin for each command (Shell.ExecutableStub).</summary>
    internal static readonly byte[] Stub = Encoding.UTF8.GetBytes("\u007fELF\u0002\u0001\u0001\0\0\0\0\0\0\0\0\0");

    internal static bool IsStub(FileItem f) => !f.IsDirectory && f.Data.AsSpan().SequenceEqual(Stub);

    internal static bool Under(string path, string dir) => path == dir || path.StartsWith(dir + "/", StringComparison.Ordinal);

    /// <summary>Ubuntu merged /bin, /sbin and /lib into /usr: they are symlinks there, which a layer must not replace with directories.</summary>
    internal static string UsrMerge(string path)
    {
        foreach (var d in new[] { "/bin", "/sbin", "/lib", "/lib64", "/lib32", "/libx32" })
            if (Under(path, d)) return "/usr" + path;
        return path;
    }

    /// <summary>bash sets these itself, or they describe the running process: never carried.</summary>
    internal static readonly HashSet<string> ManagedVariables =
    [
        "PWD", "OLDPWD", "SHLVL", "_", "BASHPID", "PPID", "UID", "EUID", "GROUPS", "SHELLOPTS", "BASHOPTS", "RANDOM", "SRANDOM", "SECONDS",
        "LINENO", "FUNCNAME", "PIPESTATUS", "HISTCMD", "EPOCHSECONDS", "EPOCHREALTIME", "DIRSTACK", "COLUMNS", "LINES", "HOSTNAME",
        "BASH", "BASH_VERSION", "BASH_VERSINFO", "BASH_SOURCE", "BASH_LINENO", "BASH_ARGC", "BASH_ARGV", "BASH_COMMAND", "BASH_SUBSHELL", "OPTIND", "OPTARG",
    ];

    /// <summary>Quotes <paramref name="s"/> as one bash word.</summary>
    internal static string Quote(string s)
    {
        if (s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || "_./:@%+=,-".Contains(c))) return s;
        if (!s.Any(char.IsControl)) return "'" + s.Replace("'", "'\\''") + "'";
        var sb = new StringBuilder("$'");
        foreach (var c in s)
            sb.Append(c switch
            {
                '\\' => "\\\\", '\'' => "\\'", '\n' => "\\n", '\t' => "\\t", '\r' => "\\r",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        return sb.Append('\'').ToString();
    }

    sealed class Exporter(ShellSnapshot snapshot, string baseImage)
    {
        readonly ContainerReport _report = new();
        readonly SnapshotData _snap = SnapshotData.Read(snapshot);
        readonly SnapshotData _default = SnapshotData.Default(snapshot.User, snapshot.Hostname);

        bool Changed(string path) =>
            _snap.Files.TryGetValue(path, out var f) && !(_default.Files.TryGetValue(path, out var d) && f.SameContent(d));

        public BuildContext Build()
        {
            var user = snapshot.User;
            var accounts = Account.ParsePasswd(_snap.Text("/etc/passwd"));
            var groups = Account.ParseGroup(_snap.Text("/etc/group"));
            // useradd and groupadd would read a name starting with "-" as an option, and refuse it as a name
            foreach (var n in accounts.Select(a => a.Name).Concat(groups.Select(g => g.Name)).Where(n => n.StartsWith('-')).Distinct())
                _report.Skip("account " + n, "its name starts with '-', which no real account name can; not created");
            accounts.RemoveAll(a => a.Name.StartsWith('-'));
            groups.RemoveAll(g => g.Name.StartsWith('-'));
            var home = accounts.FirstOrDefault(a => a.Name == user)?.Home ?? "/home/" + user;

            // ---- files: what differs from a fresh machine, by path in the image ----
            var layer = new SortedDictionary<string, FileItem>(StringComparer.Ordinal);
            foreach (var f in _snap.Files.Values)
            {
                if (f.Path == "/" || !Changed(f.Path)) continue;
                if (Simulated.Any(s => Under(f.Path, s))) _report.Skip(f.Path, "simulated by Topwater; the container has its own");
                else if (IsStub(f)) { if (!_default.Files.ContainsKey(f.Path)) _report.Skip(f.Path, "a Topwater program stub (16 bytes standing in for a program), not a real program"); }
                else if (AccountFiles.Contains(f.Path)) _report.Skip(f.Path, "not copied: its accounts are created with useradd/groupadd instead");
                else if (DockerManaged.Contains(f.Path)) _report.Skip(f.Path, "docker writes this file when the container starts (see docker run --hostname, --add-host, --dns)");
                else if (UsrMerge(f.Path) is var target && target != f.Path && Changed(target)) _report.Skip(f.Path, $"is {target} in the image, which the snapshot also changed; kept {target}");
                else layer[target] = f with { Path = target };
            }
            // parents too, so each lands with the snapshot's owner and mode rather than root 0755
            foreach (var path in layer.Keys.ToList())
                for (var p = VirtualFileSystem.GetParent(path); p != "/" && !layer.ContainsKey(p); p = VirtualFileSystem.GetParent(p))
                    if (_snap.Files.TryGetValue(p, out var dir) && dir.IsDirectory) layer[p] = dir;
            _report._carried.AddRange(layer.Keys);

            // ---- deleted: in a fresh machine, gone from the snapshot ----
            var deleted = new List<string>();
            foreach (var d in _default.Files.Values)
            {
                if (_snap.Files.ContainsKey(d.Path) || deleted.Any(x => Under(d.Path, x))) continue;
                if (Simulated.Any(s => Under(d.Path, s)) || IsStub(d) || AccountFiles.Contains(d.Path) || DockerManaged.Contains(d.Path))
                    _report.Skip(d.Path, "deleted in the snapshot, but kept: the real system needs it");
                else deleted.Add(UsrMerge(d.Path));
            }
            _report._deleted.AddRange(deleted);

            // ---- shell state: ENV, the session script, history, the ~/.bashrc hook ----
            var session = Session(out var env);
            var now = DateTime.UtcNow;
            var bashrc = home + "/.bashrc";
            bool hookInLayer = layer.TryGetValue(bashrc, out var rc) && !rc.IsDirectory;
            if (hookInLayer) layer[bashrc] = rc! with { Data = [.. rc.Data!, .. Encoding.UTF8.GetBytes(BashrcHook)] };
            var paths = string.Concat(layer.Keys.Select(p => "A " + p + "\n").Concat(deleted.Select(p => "D " + p + "\n")));
            layer.TryAdd("/etc/topwater", new FileItem("/etc/topwater", true, 0x1ED, "root", now));
            layer[SessionPath] = new FileItem(SessionPath, false, 0x1A4, "root", now, Encoding.UTF8.GetBytes(session));
            layer[PathsPath] = new FileItem(PathsPath, false, 0x1A4, "root", now, Encoding.UTF8.GetBytes(paths));
            var history = Strings("history");
            if (history.Count > 0)
            {
                // timestamped, so a multi-line command stays one entry
                var stamp = ((DateTimeOffset)DateTime.SpecifyKind(snapshot.Created, DateTimeKind.Utc)).ToUnixTimeSeconds();
                layer[home + "/.bash_history"] = new FileItem(home + "/.bash_history", false, 0x180, user, snapshot.Created,
                    Encoding.UTF8.GetBytes(string.Concat(history.Select(h => $"#{stamp}\n{h}\n"))));
            }

            // ---- accounts: every owner in the layer needs a real uid, known before the build ----
            var owners = new SortedSet<string>(layer.Values.Select(f => f.Owner).Append(user), StringComparer.Ordinal);
            int next = accounts.Select(a => a.Uid).Concat(groups.Select(g => g.Gid)).Where(id => id is >= 1000 and < 60000).Select(id => id + 1).DefaultIfEmpty(1000).Max();
            foreach (var o in owners.Where(o => !accounts.Any(a => a.Name == o)))
            {
                if (o.StartsWith('-')) { _report.Skip("owner " + o, "its name starts with '-', which no real account name can; its files are root's"); continue; }
                accounts.Add(new(o, next, next, "", "/home/" + o, "/bin/bash"));
                _report.Skip("owner " + o, $"not in the snapshot's /etc/passwd; created as uid {next}");
                next++;
            }
            var byName = accounts.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First());
            var needed = byName.Values.ToList(); // every account in the snapshot, plus the owners added above

            NotCarriedState();
            return new BuildContext(Dockerfile(env, user), Setup(accounts, groups, needed, deleted, hookInLayer ? null : bashrc, user), session,
                Layer(layer, byName), _report);
        }

        /// <summary>The session script; exported scalars a Dockerfile ENV can hold go to <paramref name="env"/> instead.</summary>
        string Session(out List<(string Name, string Value)> env)
        {
            env = [];
            var sb = new StringBuilder($"# Generated by Topwater from a snapshot taken {snapshot.Created.ToString("u", CultureInfo.InvariantCulture)}.\n");
            sb.Append("# Sourced by ~/.bashrc, so an interactive shell starts where the Topwater session was.\n\n");

            // variables that differ from a fresh machine's
            var defaults = _default.Variables.ToDictionary(v => (string)v["name"]!, v => v.ToJsonString());
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in _snap.Variables)
            {
                var name = (string)v["name"]!;
                names.Add(name);
                if (ManagedVariables.Contains(name) || defaults.TryGetValue(name, out var d) && d == v.ToJsonString()) continue;
                var value = (string?)v["value"];
                var attrs = (string?)v["attrs"] ?? "";
                bool exported = (bool?)v["exported"] ?? false;
                if (exported && v["array"] == null && value != null && !value.Any(char.IsControl) && attrs.Trim('x').Length == 0)
                {
                    env.Add((name, value));
                    continue;
                }
                var flags = new string(attrs.Where("irlunx".Contains).Distinct().ToArray()) + (exported && !attrs.Contains('x') ? "x" : "");
                if (v["array"] is JsonObject a)
                {
                    bool assoc = (bool?)a["assoc"] ?? false;
                    var items = (a["items"]?.AsArray() ?? []).Select(i => $"[{(assoc ? Quote((string)i![0]!) : (string)i![0]!)}]={Quote((string)i[1]!)}");
                    sb.Append($"declare -{flags}{(assoc ? 'A' : 'a')} {name}=({string.Join(' ', items)})\n");
                }
                else sb.Append(flags.Length > 0 ? $"declare -{flags} " : "").Append(value == null ? name : $"{name}={Quote(value)}").Append('\n');
            }
            foreach (var d in defaults.Keys.Where(d => !names.Contains(d) && !ManagedVariables.Contains(d))) sb.Append($"unset {d}\n");

            foreach (var f in _snap.Manifest["functions"]?.AsArray() ?? [])
            {
                var name = (string)f!["name"]!;
                sb.Append($"{name}() {(string)f["body"]!}\n");
                if ((bool?)f["exported"] == true) sb.Append($"export -f {name}\n");
            }

            sb.Append("unalias -a\n"); // ~/.bashrc's own aliases give way to the snapshot's
            foreach (var (k, v) in _snap.Manifest["aliases"]?.AsObject() ?? []) sb.Append($"alias {Quote(k + "=" + (string)v!)}\n");

            foreach (var o in Strings("set"))
                if (o == "errexit") _report.Skip("set -o errexit", "left off: in an interactive shell it closes the terminal at the first failing command");
                else sb.Append($"set -o {o}\n");
            var shopt = Strings("shopt");
            var defaultShopt = Strings("shopt", _default);
            foreach (var o in shopt.Except(defaultShopt)) sb.Append($"shopt -s {o} 2>/dev/null\n");
            foreach (var o in defaultShopt.Except(shopt)) sb.Append($"shopt -u {o} 2>/dev/null\n");
            foreach (var (sig, action) in _snap.Manifest["traps"]?.AsObject() ?? []) sb.Append($"trap -- {Quote((string)action!)} {sig}\n");
            var positional = Strings("positional");
            if (positional.Count > 0) sb.Append($"set -- {string.Join(' ', positional.Select(Quote))}\n");
            return sb.ToString();
        }

        List<string> Strings(string key, SnapshotData? from = null) =>
            ((from ?? _snap).Manifest[key]?.AsArray() ?? []).Select(n => (string)n!).ToList();

        void NotCarriedState()
        {
            var commands = _snap.Files.Values.Where(f => f.Path.StartsWith("/usr/bin/") && IsStub(f) && !_default.Files.ContainsKey(f.Path)).Select(f => f.Path[9..]).ToList();
            if (commands.Count > 0) _report.Skip("registered commands", string.Join(", ", commands) + ": host-side handlers, not programs");
            if (Strings("dirStack").Count > 0) _report.Skip("directory stack (pushd)", "not recreated; the shell starts in the working directory");
            if ((_snap.Manifest["jobs"]?.AsArray().Count ?? 0) > 0) _report.Skip("finished background jobs", "process state; a new shell has none");
            _report.Skip("$?, PIPESTATUS, $_, $!, SECONDS, getopts position", "per-process; a new shell starts them fresh");
            _report.Skip($"hostname {snapshot.Hostname}", "an image can't set it; pass --hostname to docker run");
            _report.Skip("Topwater's limits (MaxFileSystemBytes, MaxCallDuration, ...)", "a real container isn't bound by them; use docker run's limits");
        }

        static bool IsPlainName(string s) => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

        static string Setup(List<Account> accounts, List<(string Name, int Gid)> groups, List<Account> needed, List<string> deleted, string? hookBashrc, string user)
        {
            var sb = new StringBuilder("#!/bin/bash\n# Generated by Topwater: accounts, deletions and the session hook. Runs once, as root, while the image builds.\nset -e\n");
            // an account of the base image holding an id the snapshot needs gives way (ubuntu:24.04 ships "ubuntu", uid and gid 1000)
            sb.Append($"keep={Quote("|" + string.Join('|', accounts.Select(a => a.Name).Concat(groups.Select(g => g.Name)).Distinct()) + "|")}\n");
            sb.Append("free_uid() { o=$(getent passwd \"$1\" | cut -d: -f1); case \"$keep\" in *\"|$o|\"*) ;; *) [ -z \"$o\" ] || userdel -r \"$o\" 2>/dev/null || userdel \"$o\";; esac; }\n");
            sb.Append("free_gid() { o=$(getent group \"$1\" | cut -d: -f1); case \"$keep\" in *\"|$o|\"*) ;; *) [ -z \"$o\" ] || groupdel \"$o\";; esac; }\n");
            foreach (var a in needed.Where(a => a.Uid >= 1000)) sb.Append($"free_uid {a.Uid}\n");
            foreach (var gid in needed.Select(a => a.Gid).Distinct())
            {
                var name = groups.FirstOrDefault(g => g.Gid == gid).Name ?? needed.First(a => a.Gid == gid).Name;
                if (gid >= 1000) sb.Append($"free_gid {gid}\n");
                sb.Append($"getent group {Quote(name)} >/dev/null || groupadd -o -g {gid} {Quote(name)}\n");
            }
            foreach (var a in needed)
                sb.Append($"id -u {Quote(a.Name)} >/dev/null 2>&1 || useradd -o {(a.Home.StartsWith("/home/") ? "-m" : "-M")} -u {a.Uid} -g {a.Gid} -d {Quote(a.Home)} -s {Quote(a.Shell.Length > 0 ? a.Shell : "/bin/bash")} -c {Quote(a.Gecos)} {Quote(a.Name)}\n");
            if (deleted.Count > 0) sb.Append($"rm -rf -- {string.Join(' ', deleted.Select(Quote))}\n");
            if (hookBashrc != null)
                sb.Append($"if [ -d {Quote(VirtualFileSystem.GetParent(hookBashrc))} ]; then printf '%s' {Quote(BashrcHook)} >> {Quote(hookBashrc)}; chown {Quote(user)}: {Quote(hookBashrc)}; fi\n");
            return sb.ToString();
        }

        static byte[] Layer(SortedDictionary<string, FileItem> layer, Dictionary<string, Account> accounts)
        {
            var ms = new MemoryStream();
            using (var tar = new TarWriter(ms, TarEntryFormat.Gnu, leaveOpen: true))
                foreach (var (path, f) in layer)
                {
                    var a = accounts.GetValueOrDefault(f.Owner) ?? new Account("root", 0, 0, "", "/root", "");
                    // another name of a file goes in as a hard link when its first name is earlier in the layer, else as a copy
                    var hard = f.HardLink is { } h && layer.ContainsKey(UsrMerge(h)) && string.CompareOrdinal(UsrMerge(h), path) < 0 ? UsrMerge(h) : null;
                    var type = f.IsDirectory ? TarEntryType.Directory : hard != null ? TarEntryType.HardLink : f.LinkTarget != null ? TarEntryType.SymbolicLink : TarEntryType.RegularFile;
                    var e = new GnuTarEntry(type, path[1..] + (f.IsDirectory ? "/" : ""))
                    {
                        Mode = (UnixFileMode)(f.Mode & 0xFFF), Uid = a.Uid, Gid = a.Gid, UserName = a.Name, ModificationTime = f.Mtime,
                    };
                    if (type == TarEntryType.HardLink) e.LinkName = hard![1..];
                    else if (type == TarEntryType.SymbolicLink) e.LinkName = f.LinkTarget!;
                    else if (type == TarEntryType.RegularFile) e.DataStream = new MemoryStream(f.Data!);
                    tar.WriteEntry(e);
                }
            return ms.ToArray();
        }

        string Dockerfile(List<(string Name, string Value)> env, string user)
        {
            static string Env(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$") + "\"";
            // a line break ends a Dockerfile instruction and quoting can't carry one, so such values never reach the file
            static bool OneLine(string s) => s.IndexOfAny(['\n', '\r']) < 0;
            static string Flat(string s) => s.Replace('\n', ' ').Replace('\r', ' ');
            if (!IsPlainName(user) || user.StartsWith('-')) throw new InvalidOperationException($"snapshot user {Flat(user)} is not a plain account name");
            var sb = new StringBuilder($"# Generated by Topwater from a snapshot of {Flat(snapshot.User)}@{Flat(snapshot.Hostname)} taken {snapshot.Created.ToString("u", CultureInfo.InvariantCulture)}.\n");
            sb.Append($"FROM {baseImage}\n");
            sb.Append("COPY setup.sh /tmp/topwater-setup.sh\n");
            sb.Append("RUN bash /tmp/topwater-setup.sh && rm /tmp/topwater-setup.sh\n");
            sb.Append("ADD layer.tar /\n");
            foreach (var (n, v) in env)
            {
                if (!IsPlainName(n) || n.Contains('-') || !OneLine(v)) { _report.Skip($"environment variable {Flat(n)}", "its name or value can't be written safely into a Dockerfile (e.g. a line break)"); continue; }
                sb.Append($"ENV {n}={Env(v)}\n");
            }
            if (OneLine(_snap.Cwd)) sb.Append($"WORKDIR {Env(_snap.Cwd)}\n");
            else _report.Skip("working directory", "its path contains a line break, which a Dockerfile can't hold; the container starts in /");
            sb.Append($"USER {user}\n");
            return sb.ToString();
        }
    }
}
