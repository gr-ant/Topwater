// The Shell class: state, constructor, clock, registration, Run and limits. See docs/architecture.md.
using System.Globalization;
using System.Text.RegularExpressions;
using Topwater.Parsing;

namespace Topwater;

/// <summary>
/// A simulated bash session. Give the model a single "bash" tool and pass its command to <see cref="RunAsync"/>.
/// Shell state (cwd, environment, aliases, functions, files) persists between calls.
/// </summary>
public sealed partial class Shell
{
    // ---------- state and constructor ----------

    readonly Dictionary<string, RegisteredCommand> _commands = new(StringComparer.Ordinal);
    readonly List<string> _history = [];
    readonly Stack<Dictionary<string, (string? Value, string? Attrs)>> _locals = new();
    readonly Stack<Dictionary<string, ShellArray?>> _localArrays = new();
    internal Dictionary<string, ShellArray> Arrays = new(StringComparer.Ordinal);
    // epochs are unique across all shells, so an array a snapshot or fork shares is never written in place (see Writable)
    static int s_epochs;
    static int NextEpoch() => Interlocked.Increment(ref s_epochs);
    int _epoch = NextEpoch();
    string _cwd = "/";
    int _condDepth;

    public Shell(ShellOptions? options = null) : this(options ?? new ShellOptions(), null) { }

    /// <param name="forked">The fork's filesystem: when set, the machine isn't seeded and no built-ins are registered (see <see cref="Fork"/>).</param>
    Shell(ShellOptions options, VirtualFileSystem? forked)
    {
        Options = options;
        Fs = forked ?? Options.FileSystem ?? new VirtualFileSystem();
        _mounts = new MountManager(this);
        Fs.Capacity = Options.MaxFileSystemBytes;
        _virtualNow = Options.StartTime?.UtcDateTime;
        if (Options.RandomSeed is { } seed) _random = (ulong)seed;
        Fs.Clock = () => Now;
        _secondsBase = Now;
        if (forked != null) return;
        var user = Options.User;
        var home = "/home/" + user;
        foreach (var dir in new[] { home, "/tmp", "/var/tmp", "/usr/bin", "/bin", "/usr/local/bin", "/opt", "/etc", "/dev/fd", "/var/log" }) Fs.CreateDirectory(dir, parents: true);
        if (!Fs.Exists("/etc/os-release"))
        {
            Fs.CreateDirectory("/usr/lib", parents: true);
            Fs.WriteAllText("/usr/lib/os-release", OsRelease);
            Fs.CreateSymlink("/etc/os-release", "../usr/lib/os-release");
        }
        Fs.WriteAllText("/etc/hostname", Options.Hostname + "\n");
        if (!Fs.Exists("/etc/issue")) Fs.WriteAllText("/etc/issue", "Ubuntu 24.04.1 LTS \\n \\l\n\n");
        if (!Fs.Exists("/etc/issue.net")) Fs.WriteAllText("/etc/issue.net", "Ubuntu 24.04.1 LTS\n");
        if (!Fs.Exists("/etc/passwd"))
            Fs.WriteAllText("/etc/passwd", $"root:x:0:0:root:/root:/bin/bash\n{user}:x:1000:1000:{user}:{home}:/bin/bash\n");
        Fs.WriteAllText("/dev/null", "");
        SeedSystemFiles(user, home);
        // a regular (non-root) account: the agent owns its home; system areas belong to root
        Fs.CreateDirectory("/root", parents: true);
        Fs.Chmod("/root", Oct("700"));
        Fs.SetOwner(home, user, recursive: true);
        Fs.Chmod(home, Oct("750"));
        Fs.Chmod("/tmp", Oct("1777"));
        Fs.Chmod("/var/tmp", Oct("1777"));
        Fs.Chmod("/dev/null", Oct("666"));
        Environment["HOME"] = home;
        Environment["USER"] = user;
        Environment["LOGNAME"] = user;
        Environment["HOSTNAME"] = Options.Hostname;
        Environment["SHELL"] = "/bin/bash";
        Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        Environment["LANG"] = "C.UTF-8";
        Environment["TERM"] = "xterm-256color";
        Cwd = home;
        Builtins.RegisterAll(this);
        _builtinNames = [.. _commands.Keys];
        // the system was "installed" months ago; only the agent's home and the temp directories are fresh
        foreach (var dir in new[] { "/bin", "/usr", "/etc", "/dev", "/opt", "/root", "/var/log", "/var/lib", "/var/cache", "/var/backups", "/var/local", "/var/mail", "/var/opt", "/var/spool", "/run", "/boot", "/lib", "/lib64", "/media", "/mnt", "/sbin", "/srv" })
            Fs.SetModified(dir, InstalledAt, recursive: true);
        foreach (var link in new[] { "/var/run", "/var/lock" }) if (Fs.IsSymlink(link)) Fs.SetModifiedTime(link, InstalledAt, follow: false);
        foreach (var path in new[] { "/", "/home", "/var", home + "/.bashrc", home + "/.profile", home + "/.bash_logout" }) Fs.SetModified(path, InstalledAt);
    }

    /// <summary>
    /// The ordinary clutter of a Linux install. A model that looks around and finds four files in /etc, no dotfiles
    /// and no /proc starts wondering what kind of machine it is on, and spends its turns investigating.
    /// </summary>
    void SeedSystemFiles(string user, string home)
    {
        foreach (var dir in SystemDirs) Fs.CreateDirectory(dir, parents: true);
        // what an ordinary user can't read, so `find /` reports "Permission denied" and exits 1 as on Ubuntu
        foreach (var dir in new[] { "/var/cache/ldconfig", "/var/cache/apt/archives/partial", "/proc/tty/driver" }) Fs.Chmod(dir, Oct("700"));
        Fs.Chmod("/run/lock", Oct("1777"));
        Link("/var/run", "/run");
        Link("/var/lock", "/run/lock");
        Link("/usr/local/man", "share/man");

        void Seed(string path, string content) { if (!Fs.Exists(path)) Fs.WriteAllText(path, content); }
        void Link(string path, string target) { if (!Fs.Exists(path) && !Fs.IsSymlink(path)) Fs.CreateSymlink(path, target); }
        Seed("/etc/hosts", $"127.0.0.1\tlocalhost\n127.0.1.1\t{Options.Hostname}\n\n::1\tip6-localhost ip6-loopback\nfe00::0\tip6-localnet\nff02::1\tip6-allnodes\nff02::2\tip6-allrouters\n");
        Seed("/etc/group", $"root:x:0:\nadm:x:4:\nsudo:x:27:\nusers:x:100:\n{user}:x:1000:\n");
        Seed("/etc/shells", "# Shells a user may have as a login shell\n/bin/sh\n/usr/bin/sh\n/bin/bash\n/usr/bin/bash\n/bin/rbash\n/usr/bin/rbash\n/usr/bin/dash\n");
        Seed("/etc/debian_version", "trixie/sid\n");
        Seed("/var/lib/shells.state", "");
        Seed("/etc/profile", EtcProfile);
        Seed("/etc/bash.bashrc", EtcBashrc);
        Seed("/etc/profile.d/01-locale-fix.sh", "# Use the C.UTF-8 locale when none is set.\nexport LANG=\"${LANG:-C.UTF-8}\"\n");
        Seed("/etc/timezone", "Etc/UTC\n");
        Seed("/etc/resolv.conf", "nameserver 127.0.0.53\noptions edns0 trust-ad\n");
        Seed("/etc/lsb-release", "DISTRIB_ID=Ubuntu\nDISTRIB_RELEASE=24.04\nDISTRIB_CODENAME=noble\nDISTRIB_DESCRIPTION=\"Ubuntu 24.04.1 LTS\"\n");
        Seed("/etc/fstab", "# <file system> <mount point> <type> <options> <dump> <pass>\n/dev/sda1\t/\text4\tdefaults\t0 1\n");

        // these agree with what uname, nproc, free, df and uptime print
        Seed("/proc/version", "Linux version 6.8.0-generic (buildd@lcy02-amd64-047) (x86_64-linux-gnu-gcc-13 (Ubuntu 13.2.0-23ubuntu4) 13.2.0, GNU ld (GNU Binutils for Ubuntu) 2.42) #1 SMP PREEMPT_DYNAMIC\n");
        Seed("/proc/cpuinfo", string.Concat(Enumerable.Range(0, 4).Select(i =>
            $"processor\t: {i}\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t\t: 85\nmodel name\t: Intel(R) Xeon(R) Platinum 8259CL CPU @ 2.50GHz\ncpu MHz\t\t: 2499.998\ncache size\t: 36608 KB\nphysical id\t: 0\nsiblings\t: 4\ncore id\t\t: {i / 2}\ncpu cores\t: 2\nbogomips\t: 4999.99\n\n")));
        Seed("/proc/meminfo", "MemTotal:        8014880 kB\nMemFree:         3984512 kB\nMemAvailable:    5748660 kB\nBuffers:          182272 kB\nCached:          2018752 kB\nSwapTotal:       2097148 kB\nSwapFree:        2097148 kB\nShmem:             12288 kB\n");
        Seed("/proc/mounts", "/dev/sda1 / ext4 rw,relatime 0 0\nproc /proc proc rw,nosuid,nodev,noexec,relatime 0 0\nsysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0\ntmpfs /dev/shm tmpfs rw,nosuid,nodev 0 0\n");
        Seed("/proc/uptime", "274320.42 1082431.77\n");
        Seed("/proc/loadavg", "0.08 0.03 0.01 1/142 4242\n");
        // the shell's own process: /proc/self points at it ($$ is 4242)
        var uid = user == "root" ? 0 : 1000;
        Seed("/proc/4242/comm", "bash\n");
        Seed("/proc/4242/cmdline", "bash\0");
        Seed("/proc/4242/status", $"Name:\tbash\nState:\tS (sleeping)\nPid:\t4242\nPPid:\t1\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nGid:\t{uid}\t{uid}\t{uid}\t{uid}\n");
        Link("/proc/self", "4242");

        foreach (var dir in new[] { "/etc/skel", home })
        {
            Seed(dir + "/.profile", DotProfile);
            Seed(dir + "/.bash_logout", DotBashLogout);
            Seed(dir + "/.bashrc", DotBashrc);
        }
    }

    static readonly string[] SystemDirs =
    [
        "/boot", "/lib", "/lib64", "/media", "/mnt", "/proc/4242", "/proc/tty/driver", "/run/lock", "/sbin", "/srv", "/sys",
        "/etc/profile.d", "/etc/skel", "/usr/games", "/usr/include", "/usr/lib64", "/usr/libexec", "/usr/sbin", "/usr/src",
        "/usr/local/etc", "/usr/local/games", "/usr/local/include", "/usr/local/lib", "/usr/local/sbin", "/usr/local/share/man", "/usr/local/src",
        "/usr/share/base-files", "/usr/share/bash-completion", "/usr/share/common-licenses", "/usr/share/doc", "/usr/share/info", "/usr/share/locale",
        "/usr/share/man", "/usr/share/misc", "/usr/share/terminfo",
        "/var/backups", "/var/cache/apt/archives/partial", "/var/cache/debconf", "/var/cache/ldconfig", "/var/lib/apt", "/var/lib/dpkg", "/var/lib/misc", "/var/lib/pam", "/var/lib/systemd",
        "/var/local", "/var/mail", "/var/opt", "/var/spool",
    ];

    // Ubuntu's files, rewritten in our own words with the same effect: a login shell reads /etc/profile, which runs
    // /etc/profile.d/*.sh; an interactive one reads ~/.bashrc; a non-interactive one (every tool call) reads neither
    const string OsRelease = "PRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nNAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\nVERSION=\"24.04.1 LTS (Noble Numbat)\"\nVERSION_CODENAME=noble\nID=ubuntu\nID_LIKE=debian\nHOME_URL=\"https://www.ubuntu.com/\"\nSUPPORT_URL=\"https://help.ubuntu.com/\"\nBUG_REPORT_URL=\"https://bugs.launchpad.net/ubuntu/\"\nPRIVACY_POLICY_URL=\"https://www.ubuntu.com/legal/terms-and-policies/privacy-policy\"\nUBUNTU_CODENAME=noble\nLOGO=ubuntu-logo\n";
    const string EtcProfile = "# Read by every login shell (bash -l, sh -l).\n\n# interactive bash: the shared settings in /etc/bash.bashrc\nif [ \"${PS1-}\" ] && [ \"${BASH-}\" ] && [ -f /etc/bash.bashrc ]; then\n  . /etc/bash.bashrc\nfi\n\n# then each script in /etc/profile.d\nif [ -d /etc/profile.d ]; then\n  for i in /etc/profile.d/*.sh; do\n    [ -r \"$i\" ] && . \"$i\"\n  done\n  unset i\nfi\n";
    const string EtcBashrc = "# Shared settings for interactive bash shells.\n\n# nothing to do for scripts and bash -c\n[ -z \"${PS1-}\" ] && return\n\nshopt -s checkwinsize\nPS1='\\u@\\h:\\w\\$ '\n";
    const string DotProfile = "# Read by login shells, unless ~/.bash_profile or ~/.bash_login exists.\n\n# bash: also read ~/.bashrc\nif [ -n \"$BASH_VERSION\" ] && [ -f \"$HOME/.bashrc\" ]; then\n    . \"$HOME/.bashrc\"\nfi\n\n# your own programs, when you have them\nfor d in \"$HOME/bin\" \"$HOME/.local/bin\"; do\n    [ -d \"$d\" ] && PATH=\"$d:$PATH\"\ndone\nunset d\n";
    const string DotBashLogout = "# Run when a login shell exits.\n";
    const string DotBashrc = "# Settings for interactive bash shells.\n\n# scripts and bash -c stop here\ncase $- in\n    *i*) ;;\n      *) return;;\nesac\n\n# history: skip duplicates and lines that start with a space\nHISTCONTROL=ignoreboth\nHISTSIZE=1000\nHISTFILESIZE=2000\nshopt -s histappend checkwinsize\n\nPS1='\\u@\\h:\\w\\$ '\n\n# colour and shorthands\nalias ls='ls --color=auto'\nalias grep='grep --color=auto'\nalias ll='ls -alF'\nalias la='ls -A'\nalias l='ls -CF'\n\n[ -f ~/.bash_aliases ] && . ~/.bash_aliases\n";

    DateTime InstalledAt => (_virtualNow is null ? DateTime.Today : Options.StartTime!.Value.UtcDateTime.Date).AddDays(-142).AddHours(9).AddMinutes(13);

    // bash builtins that have no program file on a real system: listing them in /bin makes a model suspicious
    HashSet<string>? _builtinNames;

    internal static int Oct(string s) => Convert.ToInt32(s, 8);
    int _procSubCount;

    public ShellOptions Options { get; }
    public VirtualFileSystem Fs { get; }
    public Dictionary<string, string> Environment { get; internal set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Aliases { get; private set; } = new(StringComparer.Ordinal);
    public IReadOnlyList<string> History => _history;
    public IReadOnlyDictionary<string, RegisteredCommand> Commands => _commands;
    public int LastExitCode { get; private set; }

    internal Dictionary<string, FunctionDef> Functions { get; private set; } = new(StringComparer.Ordinal);
    /// <summary>bash's hash table: where a command name was found on PATH, and how often it was looked up since.</summary>
    internal Dictionary<string, (string Path, int Hits)> Hashed { get; private set; } = new(StringComparer.Ordinal);
    string? _hashedFor; // the PATH the table was filled from: any change to PATH empties it
    internal List<string> Positional { get; set; } = [];
    internal bool Errexit { get; set; }
    internal bool Pipefail { get; set; }
    internal bool Nounset { get; set; }
    internal bool Noglob { get; set; }
    internal bool Xtrace { get; set; }
    internal bool Noclobber { get; set; }
    /// <summary>declare attributes by variable name: i integer, r readonly, l/u lower/upper case, n nameref, x exported.</summary>
    internal Dictionary<string, string> VarAttrs { get; set; } = new(StringComparer.Ordinal);
    /// <summary>How many `source`/`.` files are running, so `return` knows whether it may end one.</summary>
    internal int SourceDepth { get; set; }
    /// <summary>fds 3 and up opened by `exec 3&gt;file` / `exec 3&lt;file`: a TextWriter or a StdinBuffer.</summary>
    internal Dictionary<int, object> Fds { get; } = [];
    internal List<string> DirStack { get; } = [];
    internal (int Optind, int Pos) GetoptsState;
    bool _ifsUnset;
    int _subshell, _lineno, _lastBg, _nextPid = 4300;
    string _lastArg = "";
    int[] _pipeStatus = [];
    readonly List<string> _funcNames = [];
    readonly List<string> _sources = [];
    readonly List<(string Name, bool File)> _errWhere = []; // whose name starts bash's own errors: a script file, bash -c, a function's origin
    readonly Dictionary<string, string> _funcWhere = [];

    /// <summary>bash's prefix for its own errors: "bash: line 3: ", "./s.sh: line 2: ", or "environment: line 1: " in a
    /// function defined in a bash -c string.</summary>
    internal string ErrPrefix => $"{(_errWhere.Count > 0 ? _errWhere[^1].Name : "bash")}: line {Math.Max(_lineno, 1)}: ";
    string _arg0 = "bash";
    /// <summary>`export -f name`: functions a child bash (bash -c, a script) also gets.</summary>
    internal HashSet<string> ExportedFunctions { get; } = [];
    readonly Dictionary<int, int> _jobs = [];
    DateTime _secondsBase;
    DateTime? _virtualNow, _virtualDeadline;
    ulong? _random;

    /// <summary>shopt -s names: nullglob, dotglob, globstar.</summary>
    internal HashSet<string> Shopts { get; private set; } =
        ["cmdhist", "complete_fullquote", "extquote", "force_fignore", "globasciiranges", "globskipdots", "hostcomplete", "interactive_comments", "patsub_replacement", "progcomp", "promptvars", "sourcepath"];
    /// <summary>Only EXIT runs; it fires when the tool call (or script) ends.</summary>
    internal Dictionary<string, string> Traps { get; private set; } = [];

    /// <summary>The recording in progress (<see cref="StartRecording"/>), which every <see cref="RunAsync"/> call is added to.</summary>
    public ShellRecorder? Recorder { get; internal set; }

    /// <summary>The web access in force (<see cref="AllowWeb(WebPolicy)"/>), or null: no network.</summary>
    internal WebSession? Web { get; private set; }

    /// <summary>Outside sources shown as directories (<see cref="MountAsync"/>).</summary>
    readonly MountManager _mounts;
    internal MountManager MountTable => _mounts;

    // an upper bound on the characters variables hold, recounted exactly whenever it passes the limit
    long _variableChars;

    bool _inErrTrap, _inDebugTrap;

    /// <summary>How many loops are running; `break` outside any loop is a warning, not an error.</summary>
    internal int LoopDepth { get; private set; }

    long _totalIterations;
    int _scriptDepth;

    DateTime _callDeadline = DateTime.MaxValue;

    // variables the shell itself created; anything else in Environment came from the host and is exported
    readonly HashSet<string> _unexported = new(StringComparer.Ordinal);

    int _arithDepth;

    readonly Dictionary<(string, bool), Word> _paramWords = [];

    public string Cwd
    {
        get => _cwd;
        set
        {
            var abs = ResolvePath(value);
            if (!Fs.IsDirectory(abs)) throw new ShellException($"{value}: No such file or directory");
            _cwd = abs;
            Environment["PWD"] = abs;
        }
    }

    /// <summary>An absolute path for <paramref name="path"/> from the cwd; ".." goes up from where the path really is, past symbolic links, as the kernel resolves it.</summary>
    public string ResolvePath(string path) => Fs.NormalizePhysical(_cwd, path);

    // ---------- clock and random ----------

    /// <summary>The machine's time (UTC): the virtual clock when <see cref="ShellOptions.StartTime"/> is set, else the host's.</summary>
    internal DateTime Now => _virtualNow ?? DateTime.UtcNow;
    internal DateTimeOffset NowUtc => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));

    /// <summary>`sleep`: waits, or on the virtual clock just moves time on (to the deadline of an enclosing `timeout`, which then fires).</summary>
    internal Task SleepAsync(TimeSpan duration, CancellationToken ct)
    {
        if (_virtualNow is not { } now) return Task.Delay(duration, ct);
        if (_virtualDeadline is { } deadline && now + duration >= deadline) { _virtualNow = deadline; throw new VirtualTimeout(); }
        _virtualNow = now + duration;
        return Task.CompletedTask;
    }

    /// <summary>`timeout`: runs <paramref name="body"/>; on the virtual clock its limit is measured in virtual time. Returns null if it timed out.</summary>
    internal async Task<int?> WithVirtualTimeoutAsync(TimeSpan limit, Func<Task<int>> body)
    {
        if (_virtualNow is not { } now) return await body();
        var saved = _virtualDeadline;
        var mine = now + limit;
        if (saved is null || mine < saved) _virtualDeadline = mine;
        try { return await body(); }
        catch (VirtualTimeout) when (_virtualNow >= mine) { return null; }
        finally { _virtualDeadline = saved; }
    }

    sealed class VirtualTimeout : OperationCanceledException;

    /// <summary>How long something took: on the virtual clock, the virtual time that passed (only `sleep` moves it).</summary>
    internal Func<TimeSpan> StartTimer()
    {
        if (_virtualNow is null) { var sw = System.Diagnostics.Stopwatch.StartNew(); return () => sw.Elapsed; }
        var start = Now;
        return () => Now - start;
    }

    /// <summary>A random number in [0, max): from the seeded generator (splitmix64, so its state fits in a snapshot) or the shared one.</summary>
    internal int NextRandom(int max)
    {
        if (_random is not { } state) return Random.Shared.Next(max);
        state += 0x9E3779B97F4A7C15;
        _random = state;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return (int)((z ^ (z >> 31)) % (ulong)max);
    }

    // ---------- registration ----------

    public Shell Register(string name, CommandHandler handler, string description = "", string? usage = null)
    {
        // built-ins take their summary and help from spec/ (Generated/CommandSpec.g.cs)
        if (_builtinNames is null && description == "" && usage is null && CommandSpec.Commands.TryGetValue(name, out var spec)) (description, usage) = (spec.Summary, spec.Help);
        _commands[name] = new RegisteredCommand(name, description, usage, handler);
        _builtinNames?.Remove(name); // an app command replacing a built-in owns its own --help/--version
        WriteProgramFile(name);
        return this;
    }

    void WriteProgramFile(string name)
    {
        bool shellOnly = CommandSpec.Commands.TryGetValue(name, out var spec) && !spec.ProgramFile && (_builtinNames is null || _builtinNames.Contains(name));
        if (shellOnly || name.Contains('/') || name is "." or ".." or ":" || !Fs.IsDirectory("/usr/bin")) return;
        using var _ = Fs.AsRoot();
        foreach (var dir in new[] { "/usr/bin/", "/bin/" })
        {
            if (!Fs.IsDirectory(dir)) continue;
            Fs.WriteAllText(dir + name, ExecutableStub);
            Fs.Chmod(dir + name, Oct("755"));
            Fs.SetModified(dir + name, InstalledAt);
            // shortcut: a stub is 16 bytes but reports a plausible program size; `wc -c` on it would disagree
            Fs.SetReportedSize(dir + name, 14_000 + (uint)name.Aggregate(17, (h, c) => h * 31 + c) % 180_000 / 8 * 8);
            Fs.SetModified(dir, InstalledAt);
        }
    }

    /// <summary>After a restore, program files for exactly this shell's commands: the snapshot may come from a shell with others.</summary>
    void SyncProgramFiles()
    {
        using var _ = Fs.AsRoot();
        foreach (var dir in new[] { "/usr/bin/", "/bin/" })
            if (Fs.IsDirectory(dir))
                foreach (var name in Fs.List(dir))
                    if (!_commands.ContainsKey(name) && Fs.IsFile(dir + name) && Fs.ReadAllText(dir + name) == ExecutableStub) Fs.Delete(dir + name);
        foreach (var name in _commands.Keys)
            if (!Fs.IsFile("/usr/bin/" + name)) WriteProgramFile(name);
    }

    public Shell Register(string name, Func<CommandContext, int> handler, string description = "", string? usage = null) =>
        Register(name, ctx => Task.FromResult(handler(ctx)), description, usage);

    /// <summary>Registers every [Command] method on <paramref name="target"/>. Pass a Type to register its static methods.</summary>
    public Shell Register(object target)
    {
        AttributeBinder.Register(this, target);
        return this;
    }

    /// <summary>Registers <paramref name="handler"/> and returns what puts back the command it replaced (none: unregisters),
    /// unless something else has been registered under the name since. HostCommand.Dispose uses it.</summary>
    internal Action RegisterReplacing(string name, CommandHandler handler, string description)
    {
        _commands.TryGetValue(name, out var previous);
        bool builtin = _builtinNames?.Contains(name) == true;
        Register(name, handler, description);
        return () =>
        {
            if (!_commands.TryGetValue(name, out var now) || now.Handler != handler) return;
            if (previous is null) { Unregister(name); return; }
            _commands[name] = previous;
            if (builtin) _builtinNames!.Add(name);
        };
    }

    /// <summary>
    /// Lets the agent's <c>curl</c> and <c>wget</c> reach the hosts in <paramref name="policy"/>, over https unless it allows http.
    /// Every URL and redirect is checked against it; names are resolved once, private, loopback, link-local and other
    /// internal addresses are refused, and the connection goes to the address that was checked. A fork does not inherit it.
    /// </summary>
    public Shell AllowWeb(WebPolicy policy)
    {
        Web = new WebSession(policy);
        return this;
    }

    internal void AllowWeb(WebSession session) => Web = session;

    /// <summary>
    /// Shows <paramref name="source"/> as the directory <paramref name="path"/> (docs/mounts.md): read-only, staged (a change set
    /// you apply) or live (changes applied after every command). The mount point must be missing or an empty directory, outside
    /// any other mount. Lists the source before it returns, so a bad credential fails here, in your code.
    /// </summary>
    public Task MountAsync(string path, IMountSource source, MountMode mode = MountMode.ReadOnly) => OffContext.Run(() => _mounts.MountAsync(path, source, mode));

    /// <summary>Removes a mount and its pending changes; the mount point stays as an empty directory.</summary>
    public void Unmount(string path) => _mounts.Unmount(path);

    /// <summary>Every mount: path, mode, source type and id, and why it is unavailable.</summary>
    public IReadOnlyList<MountInfo> Mounts => [.. _mounts.Items.Select(m => m.Info())];

    /// <summary>The changes that would make a staged or live mount's source match its files, in an order that applies one after another.</summary>
    public IReadOnlyList<MountChange> PendingChanges(string path) => _mounts.Get(path).Changes();

    /// <summary>Applies the pending changes, or the ones given (from <see cref="PendingChanges"/>), in order, stopping at the first
    /// conflict or failure. Not bounded by the call time limit.</summary>
    public Task<MountApplyResult> ApplyChangesAsync(string path, IEnumerable<MountChange>? changes = null) => OffContext.Run(() => _mounts.Get(path).ApplyAsync(changes));

    /// <summary>Drops a mount's pending changes, and the ones a loaded snapshot brought: its files are the source's again.</summary>
    public void DiscardChanges(string path) => _mounts.Discard(path);

    /// <summary>The changes a snapshot loaded from a file brought for this mount (as untrusted as the file): not in the files,
    /// never applied, until <see cref="AcceptSnapshotChangesAsync"/>.</summary>
    public IReadOnlyList<MountChange> SnapshotChanges(string path) => [.. _mounts.Get(path).Held];

    /// <summary>Puts a loaded snapshot's changes into the mount's files, where they become pending changes like the agent's.</summary>
    public Task AcceptSnapshotChangesAsync(string path) => OffContext.Run(() => _mounts.AcceptAsync(path));

    /// <summary>Lists the source again, keeping the pending changes.</summary>
    public Task RefreshMountAsync(string path) => OffContext.Run(() => _mounts.RefreshAsync(path));

    public bool Unregister(string name)
    {
        using (Fs.AsRoot())
            foreach (var dir in new[] { "/usr/bin/", "/bin/" })
                if (Fs.IsFile(dir + name)) Fs.Delete(dir + name);
        return _commands.Remove(name);
    }

    // ---------- run ----------

    public Task<ShellResult> RunAsync(string commandLine, CancellationToken cancellationToken = default) =>
        OffContext.Run(() => RunRecordedAsync(commandLine, cancellationToken));

    async Task<ShellResult> RunRecordedAsync(string commandLine, CancellationToken cancellationToken)
    {
        if (Recorder is not { } recorder) return await RunCallAsync(commandLine, cancellationToken);
        var started = NowUtc;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await RunCallAsync(commandLine, cancellationToken);
        recorder.Add(commandLine, result, watch.Elapsed, started, Web?.TakeLog(), _mounts.TakeLog());
        return result;
    }

    async Task<ShellResult> RunCallAsync(string commandLine, CancellationToken cancellationToken)
    {
        _history.Add(commandLine);
        var stdout = new CappedWriter(this);
        var stderr = new CappedWriter(this);
        int code;
        string? trailer = null;
        var previousUser = Fs.RunAs;
        _totalIterations = _scriptDepth = _lineno = 0; // each call is a new bash -c: its lines count from 1
        Web?.BeginCall();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Options.MaxCallDuration);
        _callDeadline = Options.MaxCallDuration < TimeSpan.FromDays(365) ? DateTime.UtcNow + Options.MaxCallDuration : DateTime.MaxValue;
        var callToken = budget.Token; // everything below, built-ins included, sees the call's time limit
        Fs.RunAs = Options.User; // the agent's commands run as a regular user
        var io = new Io(new StdinBuffer(""), new SwitchWriter(stdout), new SwitchWriter(stderr));
        try
        {
            // shortcut: EXIT traps fire when the tool call ends (like `set -e`, they last one call), not at logout
            try
            {
                if (_mounts.Items.Count > 0) await _mounts.PrepareAsync();
                code = await ExecTopLevelAsync(Parser.Parse(commandLine), io, callToken);
            }
            finally
            {
                await RunExitTrapAsync(io, callToken);
                if (_mounts.Items.Count > 0 && DateTime.UtcNow < _callDeadline) await _mounts.SettleAsync(io.Err);
            }
        }
        catch (ParseException e) { stderr.GetStringBuilder().AppendLine($"bash: {e.Message}"); code = 2; }
        catch (ExitSignal e) { code = e.Code; }
        catch (ReturnSignal e) { code = e.Code; }
        catch (LoopControl) { code = 0; }
        catch (LimitExceeded e) { stderr.GetStringBuilder().AppendLine($"bash: {e.Message}"); code = 1; trailer = Stopped(e.Message); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            code = 124; // what `timeout` exits with
            trailer = Stopped($"this call exceeded the {Seconds(Options.MaxCallDuration)} time limit; split the work into shorter calls");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) // an interpreter bug must not take down the host's agent loop
        {
            stderr.GetStringBuilder().AppendLine($"bash: internal error: {e.GetType().Name}: {e.Message}");
            code = 1;
        }
        finally
        {
            // shortcut: `set -e` / `set -o pipefail` last for one tool call, so a stray `set -e` can't poison the session.
            Errexit = Pipefail = Nounset = Noglob = Xtrace = Noclobber = false;
            _callDeadline = DateTime.MaxValue;
            Fds.Clear();
            Fs.RunAs = previousUser;
            if (_procSubCount > 0 && Fs.IsDirectory("/dev/fd"))
                using (Fs.AsRoot())
                    foreach (var f in Fs.List("/dev/fd")) Fs.Delete("/dev/fd/" + f, recursive: true);
            _procSubCount = 0;
        }
        LastExitCode = code;
        return new ShellResult(ByteText.Printable(stdout.ToString()), ByteText.Printable(stderr.ToString()), code) { Trailer = trailer };
    }

    // ---------- limits ----------

    /// <summary>"32 MB", "512 KB" or "1000 bytes": a limit as messages print it.</summary>
    internal static string Size(long bytes) => bytes % (1 << 20) == 0 ? $"{bytes >> 20} MB" : bytes % 1024 == 0 ? $"{bytes >> 10} KB" : $"{bytes} bytes";

    internal static LimitExceeded TooLarge(string what, long limit, string instead) => new($"{what} too large (limit {Size(limit)}); {instead}");
    internal LimitExceeded TooLarge(string what, string instead) => TooLarge(what, Options.MaxStringBytes, instead);

    /// <summary>Called before a variable or element takes a value of <paramref name="adding"/> characters in place of <paramref name="replacing"/>.</summary>
    internal void ChargeVariable(string name, long adding, long replacing)
    {
        long max = Options.MaxStringBytes;
        if (adding > max) throw TooLarge($"{name}: value", "write the data to a file instead");
        if ((_variableChars += adding) <= max) return;
        _variableChars = Environment.Sum(e => (long)e.Key.Length + e.Value.Length)
            + Arrays.Values.Sum(a => a.Items.Sum(e => (long)e.Key.Length + e.Value.Length)) - replacing + adding;
        if (_variableChars > max) throw TooLarge("variables in total", "keep big data in files and unset variables you no longer need");
    }

    /// <summary>When the running call must end (DateTime.MaxValue between calls): what bounds a mounted source's answers.</summary>
    internal DateTime CallDeadline => _callDeadline;

    internal void CheckDeadline()
    {
        if (DateTime.UtcNow >= _callDeadline) throw new OperationCanceledException();
    }

    static string Stopped(string why) =>
        $"[stopped: {why}. Running it again will hit the same limit. Files and variables changed before this point are kept.]";

    internal static string Seconds(TimeSpan t) => $"{t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s";

    /// <summary>User-supplied patterns can backtrack exponentially, and a regex match ignores cancellation.</summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    /// <summary>re, or a copy whose match timeout is what the call has left when that is shorter, so one slow match cannot
    /// outlast the call's time limit.</summary>
    internal Regex Timed(Regex re)
    {
        var left = _callDeadline - DateTime.UtcNow;
        if (left >= re.MatchTimeout) return re;
        return new Regex(re.ToString(), re.Options, left > TimeSpan.FromMilliseconds(1) ? left : TimeSpan.FromMilliseconds(1));
    }

    // ---------- re-exports ----------

    // nothing: C# has no module paths to keep working
}
