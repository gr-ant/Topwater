using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Topwater.Parsing;

namespace Topwater;

public sealed class ShellOptions
{
    public string User { get; init; } = "agent";
    public string Hostname { get; init; } = "topwater";
    public VirtualFileSystem? FileSystem { get; init; }
    /// <summary>Upper bound on iterations of any single loop, so a runaway `while true` can't hang the agent.</summary>
    public int MaxLoopIterations { get; init; } = 100_000;
    /// <summary>Most bytes one tar/unzip extraction or gzip decompression may produce, so an archive bomb ends with an error instead of filling memory.</summary>
    public long MaxExtractBytes { get; init; } = 64L << 20;
    /// <summary>Most entries one tar or unzip run may extract or list.</summary>
    public int MaxExtractEntries { get; init; } = 100_000;
    /// <summary>Longest one tool call may run. Past it the call stops with exit code 124 and a note in the tool output; files and variables changed so far are kept.</summary>
    public TimeSpan MaxCallDuration { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>Most bytes all files may hold together. The agent's writes past it fail with "No space left on device" (the call goes on);
    /// your app's own writes are counted but never refused. `df` shows it as the disk size.</summary>
    public long MaxFileSystemBytes { get; init; } = 128L << 20;
    /// <summary>Longest one string may be (a command's buffered output, a variable, an expansion, an awk/sed/jq value), and the most
    /// all variables may hold together, counted in characters. Past it the call stops with exit code 1 and a note in the tool output.</summary>
    public long MaxStringBytes { get; init; } = 32L << 20;
    /// <summary>A virtual clock for repeatable runs: when set, the machine's time starts here and stands still except that
    /// `sleep` (and a `timeout` that expires) moves it forward instantly instead of waiting. `date`, file times, SECONDS,
    /// EPOCHSECONDS, `uptime`, `time`, `find -mmin`, jq's `now` and printf's %(fmt)T all read it. Null (the default): the host's clock.</summary>
    public DateTimeOffset? StartTime { get; init; }
    /// <summary>Seeds every random choice the machine makes ($RANDOM, mktemp names), so a run repeats exactly.
    /// Null (the default): unpredictable. Assigning RANDOM=n in the shell reseeds it either way, as in bash.</summary>
    public int? RandomSeed { get; init; }
}

public sealed record ShellResult(string Stdout, string Stderr, int ExitCode)
{
    /// <summary>Set when a safety limit ended the call: a bracketed note that <see cref="ToToolOutput"/> always shows last, never cut.</summary>
    public string? Trailer { get; init; }

    /// <summary>Formats the result as the text returned to the model from the bash tool.</summary>
    public string ToToolOutput(int maxChars = 30_000)
    {
        // each stream is cut on its own, so a long stdout can't push the error message and exit code out of view
        static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + $"\n... [output truncated, {s.Length - max} more characters]\n";
        var text = Clip(Stdout, maxChars) + Clip(Stderr, Math.Max(1, maxChars / 6));
        if (ExitCode != 0) text += (text.Length == 0 || text.EndsWith('\n') ? "" : "\n") + $"[exit code {ExitCode}]";
        if (Trailer != null) text += (text.Length == 0 || text.EndsWith('\n') ? "" : "\n") + Trailer;
        return text.Length == 0 ? "(no output)" : text;
    }
}

public delegate Task<int> CommandHandler(CommandContext context);

public sealed record RegisteredCommand(string Name, string Description, string? Usage, CommandHandler Handler);

/// <summary>A command's stdin. Shared by every command in a loop body so `while read` consumes it line by line.</summary>
internal sealed class StdinBuffer(string text)
{
    int _pos;
    /// <summary>`exec &lt;file`: the shell's stdin becomes the file's contents.</summary>
    public void Reset(string newText) { text = newText; _pos = 0; }
    /// <summary>False when the last ReadLine hit end of input before the delimiter.</summary>
    public bool LastTerminated { get; private set; } = true;
    public string ReadAll() { var r = text[_pos..]; _pos = text.Length; return r; }

    public string? ReadLine(char delimiter = '\n')
    {
        if (_pos >= text.Length) return null;
        int nl = text.IndexOf(delimiter, _pos);
        var line = nl < 0 ? text[_pos..] : text[_pos..nl];
        _pos = nl < 0 ? text.Length : nl + 1;
        LastTerminated = nl >= 0;
        return line;
    }

    /// <summary>read -n / -N: up to count characters, stopping after the delimiter unless <paramref name="delimiter"/> is null.</summary>
    public string? ReadChars(int count, char? delimiter)
    {
        if (_pos >= text.Length) return null;
        int end = Math.Min(text.Length, _pos + count);
        int stop = delimiter is { } d ? text.IndexOf(d, _pos, end - _pos) : -1;
        var chunk = text[_pos..(stop >= 0 ? stop : end)];
        _pos = stop >= 0 ? stop + 1 : end;
        LastTerminated = stop >= 0 || chunk.Length == count;
        return chunk;
    }
}

/// <summary>Everything a command needs: its arguments, stdio, and the shell it runs in.</summary>
public sealed class CommandContext
{
    readonly StdinBuffer _stdin;
    string? _stdinText;

    internal CommandContext(Shell shell, string name, IReadOnlyList<string> args, StdinBuffer stdin, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        Shell = shell; Name = name; Args = args; _stdin = stdin; Stdout = stdout; Stderr = stderr; CancellationToken = ct;
    }

    public Shell Shell { get; }
    public string Name { get; }
    public IReadOnlyList<string> Args { get; }
    /// <summary>Everything piped or redirected into the command ("" when nothing was). Reading it consumes stdin.</summary>
    public string StdinText => _stdinText ??= _stdin.ReadAll();
    public TextReader Stdin => new StringReader(StdinText);
    public TextWriter Stdout { get; }
    public TextWriter Stderr { get; }
    public CancellationToken CancellationToken { get; }
    public VirtualFileSystem Fs => Shell.Fs;
    public string ResolvePath(string path) => Shell.ResolvePath(path);

    /// <summary>Reads one line of stdin (without the newline), or null at end of input.</summary>
    public string? ReadLine(char delimiter = '\n') => _stdin.ReadLine(delimiter);

    internal Shell.Io Io => new(_stdin, Stdout, Stderr, Fds);
    /// <summary>fds 3 and up this command was given (`read -u 3 x 3&lt;file`).</summary>
    internal Dictionary<int, object>? Fds { get; init; }
    internal bool LastLineTerminated => _stdin.LastTerminated;
    internal StdinBuffer StdinBuffer => _stdin;

    /// <summary>Runs another command with this command's stdout/stderr (used by xargs).</summary>
    public Task<int> InvokeAsync(IReadOnlyList<string> argv, string stdin = "") =>
        Shell.InvokeAsync(argv, new Shell.Io(new StdinBuffer(stdin), Stdout, Stderr), CancellationToken);
}

/// <summary>A bash array. Indexed arrays keep their elements in index order; associative ones in insertion order.</summary>
internal sealed class ShellArray(bool assoc)
{
    sealed class ByIndex : IComparer<string> { public int Compare(string? a, string? b) => long.Parse(a!).CompareTo(long.Parse(b!)); }

    public bool Assoc { get; } = assoc;
    public IDictionary<string, string> Items { get; private init; } = assoc ? new Dictionary<string, string>(StringComparer.Ordinal) : new SortedDictionary<string, string>(new ByIndex());
    /// <summary>The index `+=(x)` appends at: one past the highest index ever set.</summary>
    public long Next { get; set; }
    /// <summary>Which shell state owns this object; a (sub)shell copies an array before changing one it doesn't own.</summary>
    public int Epoch;

    public string? Get(string key) => Items.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string value)
    {
        Items[key] = value;
        if (!Assoc) Next = Math.Max(Next, long.Parse(key) + 1);
    }

    public ShellArray Clone(int epoch) => new(Assoc)
    {
        Items = Assoc ? new Dictionary<string, string>(Items, StringComparer.Ordinal) : new SortedDictionary<string, string>(Items, new ByIndex()),
        Next = Next, Epoch = epoch,
    };
}

internal abstract class ControlFlow : Exception;
internal sealed class LoopControl(bool isBreak, int levels) : ControlFlow { public bool IsBreak { get; } = isBreak; public int Levels { get; } = levels; }
internal sealed class ReturnSignal(int code) : ControlFlow { public int Code { get; } = code; }
internal sealed class ExitSignal(int code) : ControlFlow { public int Code { get; } = code; }
/// <summary>A safety limit was hit; ends the whole tool call so an outer loop or caller can't carry on.</summary>
internal sealed class LimitExceeded(string message) : ControlFlow { public override string Message { get; } = message; }

/// <summary>
/// A simulated bash session. Give the model a single "bash" tool and pass its command to <see cref="RunAsync"/>.
/// Shell state (cwd, environment, aliases, functions, files) persists between calls.
/// </summary>
public sealed partial class Shell
{
    /// <param name="Fds">fds 3 and up redirected for one command (3>&amp;1); null when none are.</param>
    internal readonly record struct Io(StdinBuffer Stdin, TextWriter Out, TextWriter Err, Dictionary<int, object>? Fds = null);

    internal const string ExecutableStub = "\u007fELF\u0002\u0001\u0001\0\0\0\0\0\0\0\0\0";

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
            Fs.WriteAllText("/etc/os-release", "PRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nNAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\nVERSION=\"24.04.1 LTS (Noble Numbat)\"\nVERSION_CODENAME=noble\nID=ubuntu\nID_LIKE=debian\nHOME_URL=\"https://www.ubuntu.com/\"\n");
        Fs.WriteAllText("/etc/hostname", Options.Hostname + "\n");
        if (!Fs.Exists("/etc/issue")) Fs.WriteAllText("/etc/issue", "Ubuntu 24.04.1 LTS \\n \\l\n\n");
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
        foreach (var dir in new[] { "/bin", "/usr", "/etc", "/dev", "/opt", "/root", "/var/log", "/var/lib", "/var/cache", "/boot", "/lib", "/lib64", "/media", "/mnt", "/sbin", "/srv" })
            Fs.SetModified(dir, InstalledAt, recursive: true);
        foreach (var path in new[] { "/", "/home", "/var", home + "/.bashrc", home + "/.profile", home + "/.bash_logout" }) Fs.SetModified(path, InstalledAt);
    }

    /// <summary>
    /// The ordinary clutter of a Linux install. A model that looks around and finds four files in /etc, no dotfiles
    /// and no /proc starts wondering what kind of machine it is on, and spends its turns investigating.
    /// </summary>
    void SeedSystemFiles(string user, string home)
    {
        foreach (var dir in new[] { "/boot", "/lib", "/lib64", "/media", "/mnt", "/proc", "/run", "/sbin", "/srv", "/sys", "/usr/lib", "/usr/share", "/usr/sbin", "/var/lib", "/var/cache" })
            Fs.CreateDirectory(dir, parents: true);

        void Seed(string path, string content) { if (!Fs.Exists(path)) Fs.WriteAllText(path, content); }
        Seed("/etc/hosts", $"127.0.0.1\tlocalhost\n127.0.1.1\t{Options.Hostname}\n\n::1\tip6-localhost ip6-loopback\nfe00::0\tip6-localnet\nff02::1\tip6-allnodes\nff02::2\tip6-allrouters\n");
        Seed("/etc/group", $"root:x:0:\nadm:x:4:\nsudo:x:27:\nusers:x:100:\n{user}:x:1000:\n");
        Seed("/etc/shells", "# /etc/shells: valid login shells\n/bin/sh\n/bin/bash\n/usr/bin/bash\n/usr/bin/sh\n");
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

        Seed(home + "/.profile", "# ~/.profile: executed by the command interpreter for login shells.\n\nif [ -n \"$BASH_VERSION\" ]; then\n    if [ -f \"$HOME/.bashrc\" ]; then\n\t. \"$HOME/.bashrc\"\n    fi\nfi\n\nif [ -d \"$HOME/bin\" ] ; then\n    PATH=\"$HOME/bin:$PATH\"\nfi\n");
        Seed(home + "/.bash_logout", "# ~/.bash_logout: executed by bash(1) when login shell exits.\n\nif [ \"$SHLVL\" = 1 ]; then\n    [ -x /usr/bin/clear_console ] && /usr/bin/clear_console -q\nfi\n");
        Seed(home + "/.bashrc", "# ~/.bashrc: executed by bash(1) for non-login shells.\n\n# If not running interactively, don't do anything\ncase $- in\n    *i*) ;;\n      *) return;;\nesac\n\nHISTCONTROL=ignoreboth\nHISTSIZE=1000\nHISTFILESIZE=2000\n\nshopt -s checkwinsize\n\nPS1='\\u@\\h:\\w\\$ '\n\nalias ls='ls --color=auto'\nalias ll='ls -alF'\nalias la='ls -A'\nalias l='ls -CF'\n");
    }

    DateTime InstalledAt => (_virtualNow is null ? DateTime.Today : Options.StartTime!.Value.UtcDateTime.Date).AddDays(-142).AddHours(9).AddMinutes(13);

    // bash builtins that have no program file on a real system: listing them in /bin makes a model suspicious
    static readonly HashSet<string> NoProgramFile = ["alias", "break", "cd", "command", "continue", "declare", "eval", "exit", "export", "help",
        "history", "let", "local", "mapfile", "read", "readarray", "readonly", "return", "set", "shift", "shopt", "source", "trap", "type", "typeset", "unalias", "unset"];

    HashSet<string>? _builtinNames;

    internal static int Oct(string s) => Convert.ToInt32(s, 8);
    int _procSubCount;

    public ShellOptions Options { get; }
    public VirtualFileSystem Fs { get; }
    public Dictionary<string, string> Environment { get; private set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Aliases { get; private set; } = new(StringComparer.Ordinal);
    public IReadOnlyList<string> History => _history;
    public IReadOnlyDictionary<string, RegisteredCommand> Commands => _commands;
    public int LastExitCode { get; private set; }

    internal Dictionary<string, FunctionDef> Functions { get; private set; } = new(StringComparer.Ordinal);
    internal List<string> Positional { get; set; } = [];
    internal bool Errexit { get; set; }
    internal bool Pipefail { get; set; }
    internal bool Nounset { get; set; }
    internal bool Noglob { get; set; }
    internal bool Xtrace { get; set; }
    internal bool Noclobber { get; set; }
    /// <summary>declare attributes by variable name: i integer, r readonly, l/u lower/upper case, n nameref, x exported.</summary>
    internal Dictionary<string, string> VarAttrs { get; private set; } = new(StringComparer.Ordinal);
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

    /// <summary>The machine's time (UTC): the virtual clock when <see cref="ShellOptions.StartTime"/> is set, else the host's.</summary>
    internal DateTime Now => _virtualNow ?? DateTime.UtcNow;

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
    /// <summary>shopt -s names: nullglob, dotglob, globstar.</summary>
    internal HashSet<string> Shopts { get; private set; } =
        ["cmdhist", "complete_fullquote", "extquote", "force_fignore", "globasciiranges", "globskipdots", "hostcomplete", "interactive_comments", "patsub_replacement", "progcomp", "promptvars", "sourcepath"];
    /// <summary>Only EXIT runs; it fires when the tool call (or script) ends.</summary>
    internal Dictionary<string, string> Traps { get; private set; } = [];

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

    // ---------- registration ----------

    public Shell Register(string name, CommandHandler handler, string description = "", string? usage = null)
    {
        _commands[name] = new RegisteredCommand(name, description, usage, handler);
        _builtinNames?.Remove(name); // an app command replacing a built-in owns its own --help/--version
        WriteProgramFile(name);
        return this;
    }

    void WriteProgramFile(string name)
    {
        bool shellOnly = NoProgramFile.Contains(name) && (_builtinNames is null || _builtinNames.Contains(name));
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

    public bool Unregister(string name)
    {
        using (Fs.AsRoot())
            foreach (var dir in new[] { "/usr/bin/", "/bin/" })
                if (Fs.IsFile(dir + name)) Fs.Delete(dir + name);
        return _commands.Remove(name);
    }

    // ---------- execution ----------

    public async Task<ShellResult> RunAsync(string commandLine, CancellationToken cancellationToken = default)
    {
        if (Recorder is not { } recorder) return await RunCallAsync(commandLine, cancellationToken);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await RunCallAsync(commandLine, cancellationToken);
        recorder.Add(commandLine, result, watch.Elapsed);
        return result;
    }

    /// <summary>The recording in progress (<see cref="StartRecording"/>), which every <see cref="RunAsync"/> call is added to.</summary>
    public ShellRecorder? Recorder { get; internal set; }

    async Task<ShellResult> RunCallAsync(string commandLine, CancellationToken cancellationToken)
    {
        _history.Add(commandLine);
        var stdout = new CappedWriter(this);
        var stderr = new CappedWriter(this);
        int code;
        string? trailer = null;
        var previousUser = Fs.RunAs;
        _totalIterations = _scriptDepth = _lineno = 0; // each call is a new bash -c: its lines count from 1
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Options.MaxCallDuration);
        var callToken = budget.Token; // everything below, built-ins included, sees the call's time limit
        Fs.RunAs = Options.User; // the agent's commands run as a regular user
        var io = new Io(new StdinBuffer(""), new SwitchWriter(stdout), new SwitchWriter(stderr));
        try
        {
            // shortcut: EXIT traps fire when the tool call ends (like `set -e`, they last one call), not at logout
            try { code = await ExecScriptAsync(Parser.Parse(commandLine), io, callToken); }
            finally { await RunExitTrapAsync(io, callToken); }
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

    /// <summary>"32 MB", "512 KB" or "1000 bytes": a limit as messages print it.</summary>
    internal static string Size(long bytes) => bytes % (1 << 20) == 0 ? $"{bytes >> 20} MB" : bytes % 1024 == 0 ? $"{bytes >> 10} KB" : $"{bytes} bytes";

    internal static LimitExceeded TooLarge(string what, long limit, string instead) => new($"{what} too large (limit {Size(limit)}); {instead}");
    internal LimitExceeded TooLarge(string what, string instead) => TooLarge(what, Options.MaxStringBytes, instead);

    // an upper bound on the characters variables hold, recounted exactly whenever it passes the limit
    long _variableChars;

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

    static string Stopped(string why) =>
        $"[stopped: {why}. Running it again will hit the same limit. Files and variables changed before this point are kept.]";

    internal static string Seconds(TimeSpan t) => $"{t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s";

    /// <summary>A new bash process (bash -c, bash file, ./script.sh): $0, the file it runs (if any), and its own options (bash -e).</summary>
    internal sealed record NewProcess(string Arg0, string? File = null, Action? Setup = null);

    /// <param name="process">Set for a new bash process: it sees only exported variables and functions.</param>
    /// <param name="sourced">The file `source` is running, for BASH_SOURCE.</param>
    internal async Task<int> RunScriptTextAsync(string text, Io io, bool subshell, CancellationToken ct, List<string>? positional = null,
        NewProcess? process = null, string? sourced = null)
    {
        Script script;
        try { script = Parser.Parse(text); }
        catch (ParseException e) { io.Err.WriteLine($"bash: {e.Message}"); return 2; }
        if (++_scriptDepth > 100) throw new LimitExceeded("maximum script nesting level exceeded (100)");
        var savedArg0 = _arg0;
        int sources = _sources.Count, where = _errWhere.Count, line = _lineno;
        if ((process?.File ?? sourced) is { } file) _sources.Add(file);
        if (process != null) _errWhere.Add((process.File ?? process.Arg0, process.File != null));
        else if (sourced != null) _errWhere.Add((sourced, true));
        try
        {
            if (!subshell)
            {
                var savedPos = Positional;
                if (positional != null) Positional = positional;
                try { return await ExecScriptAsync(script, io, ct); }
                finally { if (positional != null) Positional = savedPos; }
            }
            var saved = SaveState();
            Traps = [];
            io = io with { Out = new SwitchWriter(io.Out), Err = new SwitchWriter(io.Err) }; // `exec >file` in a script ends with it
            if (process != null) BecomeNewProcess(process);
            if (positional != null) Positional = positional;
            try
            {
                try { return await ExecScriptAsync(script, io, ct); }
                finally { await RunExitTrapAsync(io, ct); }
            }
            catch (ExitSignal e) { return e.Code; }
            finally { RestoreState(saved); }
        }
        finally
        {
            _scriptDepth--;
            _arg0 = savedArg0;
            _sources.RemoveRange(sources, _sources.Count - sources);
            _errWhere.RemoveRange(where, _errWhere.Count - where);
            _lineno = line;
        }
    }

    /// <summary>What a child bash inherits: exported variables (not their other attributes) and exported functions, no arrays, aliases or options.</summary>
    void BecomeNewProcess(NewProcess process)
    {
        Environment = Environment.Where(e => IsExported(e.Key)).ToDictionary(StringComparer.Ordinal);
        VarAttrs = Environment.Keys.ToDictionary(k => k, _ => "x", StringComparer.Ordinal);
        Arrays = new(StringComparer.Ordinal);
        Aliases = new(StringComparer.Ordinal);
        Functions = Functions.Where(f => ExportedFunctions.Contains(f.Key)).ToDictionary(StringComparer.Ordinal);
        Errexit = Pipefail = Nounset = Xtrace = Noglob = Noclobber = false;
        _arg0 = process.Arg0;
        process.Setup?.Invoke();
    }

    async Task RunExitTrapAsync(Io io, CancellationToken ct)
    {
        if (!Traps.Remove("EXIT", out var action) || action.Length == 0) return;
        var savedCode = LastExitCode;
        try { await RunScriptTextAsync(action, io, subshell: false, ct); }
        catch (ControlFlow) { }
        LastExitCode = savedCode;
    }

    sealed record State(string Cwd, Dictionary<string, string> Env, Dictionary<string, string> Aliases,
        Dictionary<string, FunctionDef> Functions, List<string> Positional, bool Errexit, bool Pipefail, bool Nounset,
        HashSet<string> Shopts, Dictionary<string, string> Traps, Dictionary<string, ShellArray> Arrays)
    {
        public required (bool Noglob, bool Xtrace, bool Noclobber, bool IfsUnset) Flags { get; init; }
        public required Dictionary<string, string> Attrs { get; init; }
        public long VariableChars { get; init; }
    }

    // arrays are shared with the saved state and copied on first write (see Writable), so a subshell or
    // pipeline stage doesn't copy every element just to start
    State SaveState()
    {
        _epoch = NextEpoch();
        _subshell++;
        return new(_cwd, new(Environment), new(Aliases), new(Functions), Positional, Errexit, Pipefail, Nounset, [.. Shopts], new(Traps), new(Arrays))
            { Flags = (Noglob, Xtrace, Noclobber, _ifsUnset), Attrs = new(VarAttrs), VariableChars = _variableChars };
    }

    void RestoreState(State s)
    {
        _epoch = NextEpoch();
        _subshell--;
        (Noglob, Xtrace, Noclobber, _ifsUnset) = s.Flags;
        _variableChars = s.VariableChars; // was a bound for the state coming back
        VarAttrs = s.Attrs;
        Arrays = s.Arrays;
        Environment = s.Env;
        Aliases = s.Aliases;
        Functions = s.Functions;
        Positional = s.Positional;
        Errexit = s.Errexit;
        Pipefail = s.Pipefail;
        Nounset = s.Nounset;
        Shopts = s.Shopts;
        Traps = s.Traps;
        _cwd = Fs.IsDirectory(s.Cwd) ? s.Cwd : "/";
    }

    async Task<int> ExecScriptAsync(Script script, Io io, CancellationToken ct)
    {
        int code = 0;
        foreach (var item in script.Items)
        {
            if (item.Background) { code = await ExecBackgroundAsync(item with { Background = false }, io, ct); continue; }
            // every command but the last of an && / || list is a condition: set -e and the ERR trap ignore it
            code = await ExecListPipelineAsync(item.First, item.Rest.Count > 0, io, ct);
            int lastRun = 0;
            bool negated = item.First.Negate;
            for (int k = 0; k < item.Rest.Count; k++)
            {
                var (op, next) = item.Rest[k];
                if ((op == "&&") != (code == 0)) continue;
                code = await ExecListPipelineAsync(next, k < item.Rest.Count - 1, io, ct);
                lastRun = k + 1;
                negated = next.Negate;
            }
            if (code != 0 && _condDepth == 0 && lastRun == item.Rest.Count && !negated)
            {
                if (Traps.TryGetValue("ERR", out var onError) && onError.Length > 0 && !_inErrTrap)
                {
                    _inErrTrap = true;
                    try { await RunScriptTextAsync(onError, io, subshell: false, ct); }
                    finally { _inErrTrap = false; LastExitCode = code; }
                }
                if (Errexit) throw new ExitSignal(code); // set -e: never inside if/while conditions
            }
        }
        return code;
    }

    bool _inErrTrap, _inDebugTrap;

    async Task<int> ExecListPipelineAsync(Pipeline p, bool condition, Io io, CancellationToken ct)
    {
        if (!condition) return await ExecPipelineAsync(p, io, ct);
        _condDepth++;
        try { return await ExecPipelineAsync(p, io, ct); }
        finally { _condDepth--; }
    }

    /// <summary>
    /// cmd &amp;: runs now, to completion, in a subshell; $! gets a job id that `wait` reports its status for.
    /// shortcut: jobs run in order, not concurrently; run them truly in parallel if agents start relying on overlap.
    /// </summary>
    async Task<int> ExecBackgroundAsync(AndOr item, Io io, CancellationToken ct)
    {
        var saved = SaveState();
        int code;
        try { code = await ExecScriptAsync(new Script([item]), io with { Stdin = new StdinBuffer("") }, ct); }
        catch (ExitSignal e) { code = e.Code; }
        finally { RestoreState(saved); }
        _lastBg = ++_nextPid;
        _jobs[_lastBg] = code;
        LastExitCode = 0;
        return 0;
    }

    /// <summary>`wait [id]`: the job's exit status (it has already finished), 127 for an unknown id.</summary>
    internal int WaitFor(int pid) => _jobs.Remove(pid, out var code) ? code : 127;

    /// <summary>`wait -n`: the status of the next finished job (the oldest), or 127 when there are none.</summary>
    internal int WaitNext() => _jobs.Count == 0 ? 127 : WaitFor(_jobs.Keys.Min());
    internal bool IsJob(int pid) => _jobs.ContainsKey(pid);

    async Task<int> ExecPipelineAsync(Pipeline p, Io io, CancellationToken ct)
    {
        if (p.Timed)
        {
            var elapsed = StartTimer();
            int status = p.Commands.Count == 0 ? 0 : await ExecPipelineAsync(p with { Timed = false }, io, ct);
            var t = elapsed();
            io.Err.Write(p.PosixTime ? FormattableString.Invariant($"real {t.TotalSeconds:0.00}\nuser 0.00\nsys 0.00\n")
                : FormattableString.Invariant($"\nreal\t{(int)t.TotalMinutes}m{t.TotalSeconds % 60:0.000}s\nuser\t0m0.000s\nsys\t0m0.000s\n"));
            return LastExitCode = status;
        }
        if (!p.Negate) return await RunPipelineAsync(p, io, ct);
        _condDepth++; // like a condition, a negated pipeline is exempt from set -e
        int code;
        try { code = await RunPipelineAsync(p, io, ct); }
        finally { _condDepth--; }
        return LastExitCode = code == 0 ? 1 : 0;
    }

    async Task<int> RunPipelineAsync(Pipeline p, Io io, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var input = io.Stdin;
        int code = 0, failed = 0;
        var statuses = new int[p.Commands.Count];
        for (int k = 0; k < p.Commands.Count; k++)
        {
            bool last = k == p.Commands.Count - 1;
            // shortcut: pipeline stages run one after another with buffered text; stream if huge outputs or infinite producers matter.
            var output = last ? io.Out : new CappedWriter(this);
            var stage = io with { Stdin = input, Out = output };
            if (p.Commands.Count == 1) code = await ExecCommandAsync(p.Commands[k], stage, ct);
            else if (last)
            {
                // shortcut: the last stage keeps its variables (bash's lastpipe), so `cmd | while read` loops can count; only `exit` is contained
                try { code = await ExecCommandAsync(p.Commands[k], stage, ct); }
                catch (ExitSignal e) { code = e.Code; }
            }
            else
            {
                // every stage but the last is a subshell: `exit`, `cd` and assignments stay inside it
                var saved = SaveState();
                try { code = await ExecCommandAsync(p.Commands[k], stage, ct); }
                catch (ExitSignal e) { code = e.Code; }
                finally { RestoreState(saved); }
            }
            if (code != 0) failed = code;
            statuses[k] = code;
            if (!last) input = new StdinBuffer(output.ToString()!);
        }
        _pipeStatus = statuses;
        if (Pipefail) code = failed;
        LastExitCode = code;
        return code;
    }

    /// <summary>Buffers a command's output (a pipeline stage, $(...), the call's stdout); past the string limit the call stops.</summary>
    internal class CappedWriter(Shell shell, long? limit = null) : StringWriter
    {
        readonly long _limit = limit ?? shell.Options.MaxStringBytes;
        protected virtual void Full() => throw shell.TooLarge("output", "write to a file in smaller pieces or filter earlier in the pipeline");
        void Check(long adding) { if (GetStringBuilder().Length + adding > _limit) Full(); }
        public override void Write(char value) { Check(1); base.Write(value); }
        public override void Write(string? value) { Check(value?.Length ?? 0); base.Write(value); }
        public override void Write(char[] buffer, int index, int count) { Check(count); base.Write(buffer, index, count); }
        public override void Write(ReadOnlySpan<char> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void Write(StringBuilder? value) { Check(value?.Length ?? 0); base.Write(value); }
        public override void WriteLine(ReadOnlySpan<char> buffer) { Check(buffer.Length + CoreNewLine.Length); base.WriteLine(buffer); }
        public override void WriteLine(StringBuilder? value) { Check((value?.Length ?? 0) + CoreNewLine.Length); base.WriteLine(value); }
    }

    /// <summary>A redirect's file: buffered, but written through every megabyte, so the filesystem's cap (not the string limit) bounds it.</summary>
    sealed class FileSink(Shell shell, string path) : CappedWriter(shell, 1 << 20)
    {
        public string Path { get; } = path;

        protected override void Full()
        {
            var text = ToString();
            GetStringBuilder().Clear(); // dropped even if the disk is full, so a loop writing to a full disk doesn't grow the buffer
            shell.Fs.AppendAllText(Path, text);
        }

        public override void Write(string? value)
        {
            if (value is not { Length: >= 1 << 20 }) { base.Write(value); return; }
            Full(); // a big write (cat of a big file) goes straight to the file instead of through the buffer
            shell.Fs.AppendAllText(Path, value);
        }
    }

    /// <summary>A file opened by `exec >file`: written through, so later commands in the same call can read it.</summary>
    sealed class FileAppender(VirtualFileSystem fs, string path) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => fs.AppendAllText(path, value.ToString());
        public override void Write(string? value) { if (!string.IsNullOrEmpty(value)) fs.AppendAllText(path, value); }
        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
    }

    /// <summary>A shell's stdout or stderr as `exec` sees it: what it points at can be changed for the rest of the shell.</summary>
    internal sealed class SwitchWriter(TextWriter target) : TextWriter
    {
        public TextWriter Target { get; set; } = target;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Target.Write(value);
        public override void Write(string? value) => Target.Write(value);
        public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);
    }

    async Task<int> ExecCommandAsync(Command cmd, Io io, CancellationToken ct)
    {
        // a stack overflow can't be caught in .NET and would take the host process down
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new LimitExceeded("maximum nesting level exceeded");
        if (cmd is SimpleCommand { Assignments.Count: 0, Words: [var only] } && only.Is("exec")) return await ExecRedirectsAsync(cmd.Redirects, io, ct);
        if (cmd.Line > 0) _lineno = cmd.Line; // a failed redirection reports this line too
        var sinks = new List<FileSink>();
        var redirected = await ApplyRedirectsAsync(cmd.Redirects, io, sinks, ct);
        if (redirected is not { } rio) return 1;

        int code;
        try
        {
            code = cmd switch
            {
                SimpleCommand s => await ExecSimpleAsync(s, rio, ct),
                GroupCommand { Subshell: false } g => await ExecScriptAsync(g.Body, rio, ct),
                GroupCommand g => await ExecSubshellAsync(g.Body, rio, ct),
                IfCommand i => await ExecIfAsync(i, rio, ct),
                LoopCommand l => await ExecLoopAsync(l, rio, ct),
                ForCommand { Select: true } f => await ExecSelectAsync(f, rio, ct),
                ForCommand f => await ExecForAsync(f, rio, ct),
                ArithForCommand f => await ExecArithForAsync(f, rio, ct),
                CaseCommand c => await ExecCaseAsync(c, rio, ct),
                FunctionDef d => DefineFunction(d),
                ArithCommand a => await EvalArithAsync(a.Expr, rio, ct) != 0 ? 0 : 1,
                CondCommand c => await new CondEvaluator(this, c.Items, rio, ct).EvaluateAsync() ? 0 : 1,
                _ => throw new InvalidOperationException(cmd.GetType().Name),
            };
        }
        catch (ShellException e)
        {
            rio.Err.WriteLine($"{ErrPrefix}{e.Message}");
            code = cmd is CondCommand ? 2 : e.ExitCode;
        }
        catch (RegexMatchTimeoutException) { throw new LimitExceeded("pattern matching took too long"); }
        finally
        {
            foreach (var sink in sinks)
            {
                try { Fs.AppendAllText(sink.Path, sink.ToString()); }
                catch (ShellException e) { io.Err.WriteLine($"{ErrPrefix}{e.Message}"); code = 1; }
            }
        }
        return code;
    }

    int DefineFunction(FunctionDef d)
    {
        Functions[d.Name] = d;
        _funcWhere[d.Name] = _errWhere.Count > 0 && _errWhere[^1].File ? _errWhere[^1].Name : "environment";
        return 0;
    }

    async Task<int> ExecSubshellAsync(Script body, Io io, CancellationToken ct)
    {
        var saved = SaveState();
        try { return await ExecScriptAsync(body, io with { Out = new SwitchWriter(io.Out), Err = new SwitchWriter(io.Err) }, ct); }
        catch (ExitSignal e) { return e.Code; }
        finally { RestoreState(saved); }
    }

    async Task<int> ConditionAsync(Script cond, Io io, CancellationToken ct)
    {
        _condDepth++;
        try { return await ExecScriptAsync(cond, io, ct); }
        finally { _condDepth--; }
    }

    async Task<int> ExecIfAsync(IfCommand c, Io io, CancellationToken ct)
    {
        foreach (var (cond, body) in c.Branches)
            if (await ConditionAsync(cond, io, ct) == 0) return await ExecScriptAsync(body, io, ct);
        return c.Else != null ? await ExecScriptAsync(c.Else, io, ct) : 0;
    }

    /// <summary>Runs a loop body; returns (exit code, true if `break`).</summary>
    async Task<(int Code, bool Break)> LoopBodyAsync(Script body, Io io, CancellationToken ct)
    {
        LoopDepth++;
        try { return (await ExecScriptAsync(body, io, ct), false); }
        catch (LoopControl lc)
        {
            if (lc.Levels > 1 && LoopDepth > 1) throw new LoopControl(lc.IsBreak, lc.Levels - 1);
            return (0, lc.IsBreak);
        }
        finally { LoopDepth--; }
    }

    /// <summary>How many loops are running; `break` outside any loop is a warning, not an error.</summary>
    internal int LoopDepth { get; private set; }

    long _totalIterations;
    int _scriptDepth;

    /// <summary>User-supplied patterns can backtrack exponentially, and a regex match ignores cancellation.</summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    void CheckIterations(ref int n, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // nested loops multiply, so one tool call also has a total budget (10x the single-loop cap)
        const string instead = "process the whole input with one command (awk, sed, xargs) instead of a shell loop";
        if (++n > Options.MaxLoopIterations)
            throw new LimitExceeded($"loop aborted after {Options.MaxLoopIterations} iterations (the limit for one loop); {instead}");
        if (++_totalIterations > Options.MaxLoopIterations * 10L)
            throw new LimitExceeded($"loop aborted after {Options.MaxLoopIterations * 10L} iterations in this call (the limit for all loops in one call); {instead}");
    }

    async Task<int> ExecLoopAsync(LoopCommand l, Io io, CancellationToken ct)
    {
        int code = 0, n = 0;
        while (true)
        {
            if ((await ConditionAsync(l.Cond, io, ct) == 0) == l.Until) break;
            CheckIterations(ref n, ct); // counts body runs, so a loop of exactly the limit still finishes
            var (c, brk) = await LoopBodyAsync(l.Body, io, ct);
            code = c;
            if (brk) break;
        }
        return code;
    }

    async Task<int> ExecForAsync(ForCommand f, Io io, CancellationToken ct)
    {
        var items = f.Items == null ? Positional.ToList() : await ExpandWordsAsync(f.Items, io, ct);
        int code = 0, n = 0;
        foreach (var item in items)
        {
            CheckIterations(ref n, ct);
            SetVar(f.Var, item);
            var (c, brk) = await LoopBodyAsync(f.Body, io, ct);
            code = c;
            if (brk) break;
        }
        return code;
    }

    /// <summary>select: the menu and the PS3 prompt go to stderr, choices come from stdin, until `break` or end of input.</summary>
    async Task<int> ExecSelectAsync(ForCommand f, Io io, CancellationToken ct)
    {
        var items = f.Items == null ? Positional.ToList() : await ExpandWordsAsync(f.Items, io, ct);
        int code = 0, n = 0;
        bool menu = true;
        while (true)
        {
            if (menu) for (int k = 0; k < items.Count; k++) io.Err.WriteLine($"{k + 1}) {items[k]}");
            io.Err.Write(GetVariable("PS3") is { Length: > 0 } ps3 ? ps3 : "#? ");
            if (io.Stdin.ReadLine() is not { } line) { io.Err.WriteLine(); break; }
            CheckIterations(ref n, ct);
            if (menu = line.Trim().Length == 0) continue; // an empty answer shows the menu again
            SetVar("REPLY", line);
            SetVar(f.Var, int.TryParse(line.Trim(), out var choice) && choice >= 1 && choice <= items.Count ? items[choice - 1] : "");
            var (c, brk) = await LoopBodyAsync(f.Body, io, ct);
            code = c;
            if (brk) break;
        }
        return code;
    }

    async Task<int> ExecArithForAsync(ArithForCommand f, Io io, CancellationToken ct)
    {
        int code = 0, n = 0;
        if (f.Init.Length > 0) await EvalArithAsync(f.Init, io, ct);
        while (f.Cond.Length == 0 || await EvalArithAsync(f.Cond, io, ct) != 0)
        {
            CheckIterations(ref n, ct);
            var (c, brk) = await LoopBodyAsync(f.Body, io, ct);
            code = c;
            if (brk) break;
            if (f.Step.Length > 0) await EvalArithAsync(f.Step, io, ct);
        }
        return code;
    }

    async Task<int> ExecCaseAsync(CaseCommand c, Io io, CancellationToken ct)
    {
        var subject = await ExpandSingleAsync(c.Subject, io, ct);
        int code = 0;
        bool fallThrough = false;
        foreach (var (patterns, body, terminator) in c.Clauses)
        {
            if (!fallThrough)
            {
                bool matched = false;
                foreach (var pattern in patterns)
                    if (matched = Glob.ToRegex(await PatternAsync(pattern, io, ct), Shopts.Contains("nocasematch"), slashes: true).IsMatch(subject)) break;
                if (!matched) continue;
            }
            code = await ExecScriptAsync(body, io, ct);
            if (terminator == ";;") return code;
            fallThrough = terminator == ";&"; // ;;& goes on testing the next patterns
        }
        return code;
    }

    async Task<Io?> ApplyRedirectsAsync(List<Redirect> redirects, Io io, List<FileSink> sinks, CancellationToken ct)
    {
        foreach (var r in redirects)
        {
            try
            {
                switch (r.Op)
                {
                    case "<<" or "<<<" or "<":
                        var input = await OpenInputAsync(r, io, ct);
                        io = r.Fd >= 3 ? io with { Fds = new(io.Fds ?? []) { [r.Fd] = input } } : io with { Stdin = input };
                        break;
                    case ">&" or "<&":
                        var target = await ExpandSingleAsync(r.Target!, io, ct);
                        object dup = target == "-" ? (r.Op == "<&" ? new StdinBuffer("") : TextWriter.Null)
                            : int.TryParse(target, out var n) && Descriptor(n, io) is { } d ? d
                            : throw new ShellException($"{target}: {(int.TryParse(target, out _) ? "Bad file descriptor" : "ambiguous redirect")}");
                        io = WithFd(io, r.Fd, dup);
                        break;
                    default: // > >> >| &> &>>
                        var w = await OpenOutputAsync(r, io, ct, path => { var sink = new FileSink(this, path); sinks.Add(sink); return sink; });
                        io = r.Op.StartsWith('&') ? io with { Out = w, Err = w } : WithFd(io, r.Fd, w);
                        break;
                }
            }
            catch (ShellException e)
            {
                io.Err.WriteLine($"{ErrPrefix}{e.Message}");
                return null;
            }
        }
        return io;
    }

    async Task<StdinBuffer> OpenInputAsync(Redirect r, Io io, CancellationToken ct) => r.Op switch
    {
        "<<" => new StdinBuffer(await ExpandSingleAsync(r.Here!.Body, io, ct)),
        "<<<" => new StdinBuffer(await ExpandSingleAsync(r.Target!, io, ct) + "\n"),
        _ => await ReadRedirectAsync(r, io, ct),
    };

    async Task<StdinBuffer> ReadRedirectAsync(Redirect r, Io io, CancellationToken ct)
    {
        var target = await ExpandSingleAsync(r.Target!, io, ct);
        try { return new StdinBuffer(Fs.ReadPipeText(ResolvePath(target))); }
        catch (ShellException e) { throw new ShellException($"{target}: {e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]}"); }
    }

    /// <summary>The writer for > >> >| &amp;> &amp;>>: /dev/null, /dev/stdout, /dev/stderr, or a file made by <paramref name="open"/>.</summary>
    async Task<TextWriter> OpenOutputAsync(Redirect r, Io io, CancellationToken ct, Func<string, TextWriter> open)
    {
        var target = await ExpandSingleAsync(r.Target!, io, ct);
        var path = ResolvePath(target);
        if (path == "/dev/null") return TextWriter.Null;
        if (path is "/dev/stderr" or "/dev/stdout") return path == "/dev/stderr" ? io.Err : io.Out;
        if (path.StartsWith("/dev/fd/") && int.TryParse(path[8..], out var fd) && Descriptor(fd, io) is TextWriter fw) return fw;
        if (Fs.IsDirectory(path)) throw new ShellException($"{target}: Is a directory");
        if (Noclobber && r.Op is ">" or "&>" && Fs.IsFile(path)) throw new ShellException($"{target}: cannot overwrite existing file");
        if (!r.Op.EndsWith(">>") || !Fs.Exists(path)) Fs.WriteAllText(path, ""); // truncate on open, like bash
        return open(path);
    }

    /// <summary>What fd n refers to: 0 a StdinBuffer, 1 and 2 writers, 3+ whatever `exec` or this command opened; null if closed.</summary>
    object? Descriptor(int n, Io io) => n switch
    {
        0 => io.Stdin, 1 => io.Out, 2 => io.Err,
        _ => io.Fds?.GetValueOrDefault(n) ?? Fds.GetValueOrDefault(n),
    };

    static Io WithFd(Io io, int fd, object target) => fd switch
    {
        0 => target is StdinBuffer sb ? io with { Stdin = sb } : io,
        1 => target is TextWriter w ? io with { Out = w } : io,
        2 => target is TextWriter w ? io with { Err = w } : io,
        _ => io with { Fds = new(io.Fds ?? []) { [fd] = target } },
    };

    /// <summary>
    /// `exec` with only redirections: they last for the rest of the shell. fds 1 and 2 are retargeted through the
    /// SwitchWriter each (sub)shell starts with; files opened this way are written through as text arrives.
    /// shortcut: inside a command that has its own redirections (`{ exec >f; } 2>x`) the change is ignored.
    /// </summary>
    async Task<int> ExecRedirectsAsync(List<Redirect> redirects, Io io, CancellationToken ct)
    {
        foreach (var r in redirects)
        {
            try
            {
                object? target;
                if (r.Op is "<<" or "<<<" or "<") target = await OpenInputAsync(r, io, ct);
                else if (r.Op is ">&" or "<&")
                {
                    var t = await ExpandSingleAsync(r.Target!, io, ct);
                    target = t == "-" ? null : int.TryParse(t, out var n) && Descriptor(n, io) is { } d ? (d is SwitchWriter sw ? sw.Target : d)
                        : throw new ShellException($"{t}: Bad file descriptor");
                }
                else target = await OpenOutputAsync(r, io, ct, path => new FileAppender(Fs, path));
                int named = r.FdVariable == null ? r.Fd
                    : target == null ? (int.TryParse(GetVariable(r.FdVariable), out var open) ? open : -1) // {fd}>&- closes the fd it names
                    : Enumerable.Range(10, 100).First(n => !Fds.ContainsKey(n));
                if (r.FdVariable != null && target != null) SetVar(r.FdVariable, named.ToString(CultureInfo.InvariantCulture));
                foreach (var fd in r.Op.StartsWith('&') ? [1, 2] : new[] { named })
                {
                    if (fd < 0) continue;
                    if (fd == 0) io.Stdin.Reset(target is StdinBuffer sb ? sb.ReadAll() : "");
                    else if (fd <= 2) { if ((fd == 1 ? io.Out : io.Err) is SwitchWriter sw) sw.Target = target as TextWriter ?? TextWriter.Null; }
                    else if (target == null) Fds.Remove(fd);
                    else Fds[fd] = target;
                }
            }
            catch (ShellException e)
            {
                io.Err.WriteLine($"{ErrPrefix}{e.Message}");
                return 1;
            }
        }
        return 0;
    }

    static bool IsDeclaration(Word w) => w.UnquotedLiteral is "local" or "declare" or "typeset" or "readonly" or "export";

    async Task<int> ExecSimpleAsync(SimpleCommand c, Io io, CancellationToken ct)
    {
        _lineno = c.Line;
        if (Traps.Count > 0 && !_inDebugTrap && Traps.TryGetValue("DEBUG", out var debug) && debug.Length > 0)
        {
            // shortcut: the DEBUG trap runs before simple commands only, not before (( )), [[ ]], for and case
            _inDebugTrap = true;
            var status = LastExitCode;
            try { await RunScriptTextAsync(debug, io, subshell: false, ct); }
            finally { _inDebugTrap = false; LastExitCode = status; }
        }
        var words = c.Words;
        bool declaration = words.Count > 1 && IsDeclaration(words[0]);
        if (declaration && !words[0].Is("export") && await DeclareArraysAsync(words, io, ct) is { } rest)
        {
            if (rest.Count == 1) return 0;
            words = rest;
        }
        var argv = declaration ? await ExpandDeclarationAsync(words, io, ct) : await ExpandWordsAsync(words, io, ct);
        var assigned = new List<(string Name, string Value, bool Append)>();
        foreach (var a in c.Assignments)
        {
            try
            {
                if (a.Elements != null || a.Index != null) { await AssignArrayAsync(a, io, ct); continue; }
                var v = await ExpandAssignmentAsync(a.Value, io, ct);
                if (Xtrace) io.Err.WriteLine($"+ {a.Name}{(a.Append ? "+=" : "=")}{TraceQuote(v)}");
                if (words.Count > 0) { assigned.Add((a.Name, v, a.Append)); continue; }
                // with no command, assignments apply left to right: IFS=, parts=($csv) splits on the comma
                SetVar(a.Name, v, a.Append);
            }
            catch (ShellException e) when (words.Count == 0 && e.Message.EndsWith("readonly variable"))
            {
                io.Err.WriteLine($"{ErrPrefix}{e.Message}");
                throw new ExitSignal(1); // like bash, assigning a read-only variable ends the script
            }
        }

        if (argv.Count == 0)
        {
            foreach (var (k, v, append) in assigned) SetVar(k, v, append);
            // only a command substitution leaves its exit code behind; `$empty` alone is a successful no-op
            return c.Words.Concat(c.Assignments.Select(a => a.Value)).Any(w => w.Parts.Any(p => p is CommandSubPart)) ? LastExitCode : 0;
        }

        if (Aliases.TryGetValue(argv[0], out var alias))
        {
            // an alias holding a pipeline or list is shell text, not just words
            if (assigned.Count == 0 && alias.IndexOfAny(['|', ';', '&', '<', '>']) >= 0 && Aliases.Remove(argv[0]))
                try { return await RunScriptTextAsync(alias + " " + string.Join(' ', argv.Skip(1).Select(Printf.ShellQuote)), io, subshell: false, ct); }
                finally { Aliases[argv[0]] = alias; }   // removed while it runs so it can't expand itself
            argv = [.. await ExpandWordsAsync(Parser.ParseWords(alias), io, ct), .. argv.Skip(1)];
        }
        if (Xtrace) io.Err.WriteLine("+ " + string.Join(' ', argv.Select(TraceQuote)));

        try
        {
            if (assigned.Count == 0) return await InvokeAsync(argv, io, ct);

            // VAR=x cmd: the variable only exists for that command
            var saved = assigned.ToDictionary(a => a.Name, a => (Value: Environment.TryGetValue(a.Name, out var v) ? v : null, Attrs: VarAttrs.GetValueOrDefault(a.Name)));
            foreach (var (k, v, append) in assigned)
            {
                Environment[k] = append ? Environment.GetValueOrDefault(k) + v : v;
                SetAttrs(k, "x"); // exported to the command
            }
            try { return await InvokeAsync(argv, io, ct); }
            finally
            {
                foreach (var (k, (v, attrs)) in saved)
                {
                    if (v == null) Environment.Remove(k); else Environment[k] = v;
                    if (attrs == null) VarAttrs.Remove(k); else VarAttrs[k] = attrs;
                }
            }
        }
        finally { _lastArg = argv[^1]; }
    }

    /// <summary>set -x: an argument as bash traces it, quoted only when it needs to be.</summary>
    static string TraceQuote(string s) => s.Length > 0 && s.All(ch => char.IsAsciiLetterOrDigit(ch) || "_./=:,+@%^-".Contains(ch)) ? s : QuoteValue(s);

    /// <summary>${x@Q}: single quotes, or $'...' when the text holds control characters.</summary>
    internal static string QuoteValue(string s) =>
        s.Any(ch => ch < 32 || ch == 127) ? Printf.ShellQuote(s) : "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>`local x="a b" y=$(cmd)`: arguments that look like assignments are expanded as assignments (no splitting).</summary>
    async Task<List<string>> ExpandDeclarationAsync(List<Word> words, Io io, CancellationToken ct)
    {
        var argv = new List<string>();
        foreach (var w in words)
        {
            if (argv.Count > 0 && Parser.TryAssignment(w) is { Elements: null, Index: null } a)
                argv.Add(a.Name + (a.Append ? "+=" : "=") + await ExpandAssignmentAsync(a.Value, io, ct));
            else argv.AddRange(await ExpandWordsAsync([w], io, ct));
        }
        return argv;
    }

    /// <param name="skipFunctions">`command ls` / `builtin cd`: run the command itself even when a function has its name.</param>
    internal async Task<int> InvokeAsync(IReadOnlyList<string> argv, Io io, CancellationToken ct, bool skipFunctions = false)
    {
        var name = argv[0];
        if (!skipFunctions && Functions.TryGetValue(name, out var fn)) return await CallFunctionAsync(fn.Body, argv, io, ct);
        if (name.Contains('/'))
        {
            var path = ResolvePath(name);
            if (VirtualFileSystem.GetParent(path) is "/usr/bin" or "/bin" or "/usr/local/bin" && _commands.ContainsKey(VirtualFileSystem.GetName(path)))
                name = VirtualFileSystem.GetName(path);
            else return await RunScriptFileAsync(name, path, argv, io, ct);
        }
        if (!_commands.TryGetValue(name, out var cmd))
        {
            io.Err.WriteLine($"{argv[0]}: command not found (run 'help' to list available commands)");
            return 127;
        }
        var ctx = new CommandContext(this, name, argv.Skip(1).ToList(), io.Stdin, io.Out, io.Err, ct) { Fds = io.Fds };
        if (argv.Count == 2 && _builtinNames?.Contains(name) == true && Builtins.StandardOption(ctx, cmd, argv[1])) return 0;
        try
        {
            return await cmd.Handler(ctx);
        }
        catch (OperationCanceledException) { throw; }
        catch (ControlFlow) { throw; }
        catch (UsageException e) when (!Builtins.IsShellBuiltin(this, name))
        {
            io.Err.Write(Builtins.UsageError(name is "egrep" or "fgrep" ? "grep" : name, e.Message));
            return name is "ls" or "grep" or "egrep" or "fgrep" or "sort" or "diff" ? 2 : 1;
        }
        catch (ShellException e)
        {
            io.Err.WriteLine($"{(e is UsageException || Builtins.IsShellBuiltin(this, name) ? ErrPrefix : "")}{name}: {e.Message}");
            return e is UsageException ? 2 : e.ExitCode;
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            io.Err.WriteLine($"{name}: invalid number{(BadNumber.Match(e.Message) is { Success: true } m ? $": '{m.Groups[1].Value}'" : "")}");
            return 1;
        }
        catch (Exception e)
        {
            io.Err.WriteLine($"{name}: {e.Message}");
            return 1;
        }
    }

    static readonly Regex BadNumber = new("input string '(.*)' was not", RegexOptions.Singleline);

    async Task<int> RunScriptFileAsync(string name, string path, IReadOnlyList<string> argv, Io io, CancellationToken ct)
    {
        if (Fs.IsDirectory(path)) { io.Err.WriteLine($"{ErrPrefix}{name}: Is a directory"); return 126; }
        if (!Fs.IsFile(path)) { io.Err.WriteLine($"{ErrPrefix}{name}: No such file or directory"); return 127; }
        if (!Fs.CanExecute(path) || !Fs.CanRead(path)) { io.Err.WriteLine($"{ErrPrefix}{name}: Permission denied"); return 126; }
        var text = Fs.ReadAllText(path);
        if (text.StartsWith(ExecutableStub[..4])) { io.Err.WriteLine($"{ErrPrefix}{name}: cannot execute binary file"); return 126; }
        if (text.StartsWith("#!"))
        {
            var interp = text[2..(text.IndexOf('\n') is var nl and >= 0 ? nl : text.Length)].Trim();
            if (!Regex.IsMatch(interp, @"(^|/|\s)(ba)?sh(\s|$)"))
            {
                io.Err.WriteLine($"{ErrPrefix}{name}: {interp.Split(' ')[0]}: bad interpreter: No such file or directory");
                return 126;
            }
        }
        return await RunScriptTextAsync(text, io, subshell: true, ct, argv.Skip(1).ToList(), new NewProcess(name, name));
    }

    async Task<int> CallFunctionAsync(Command body, IReadOnlyList<string> argv, Io io, CancellationToken ct)
    {
        if (_locals.Count >= 200) throw new LimitExceeded($"{argv[0]}: maximum function nesting level exceeded (200)");
        var savedPos = Positional;
        var onReturn = Traps.GetValueOrDefault("RETURN");
        Positional = argv.Skip(1).ToList();
        _locals.Push([]);
        _localArrays.Push(new Dictionary<string, ShellArray?>());
        _funcNames.Add(argv[0]);
        var where = _funcWhere.GetValueOrDefault(argv[0], "environment");
        _errWhere.Add((where, where != "environment"));
        int code, line = _lineno;
        try { code = await ExecCommandAsync(body, io, ct); }
        catch (ReturnSignal r) { code = r.Code; }
        finally
        {
            _errWhere.RemoveAt(_errWhere.Count - 1);
            _lineno = line;
            // shadowed values come back without passing ChargeVariable, so they count again
            foreach (var (k, v) in _localArrays.Pop())
                if (v == null) Arrays.Remove(k); else { Arrays[k] = v; _variableChars += v.Items.Sum(e => (long)e.Key.Length + e.Value.Length); }
            foreach (var (k, (v, attrs)) in _locals.Pop())
            {
                if (v == null) Environment.Remove(k); else { Environment[k] = v; _variableChars += v.Length; }
                if (attrs == null) VarAttrs.Remove(k); else VarAttrs[k] = attrs;
            }
            Positional = savedPos;
            _funcNames.RemoveAt(_funcNames.Count - 1);
        }
        // a RETURN trap set inside the function runs as it returns
        if (Traps.TryGetValue("RETURN", out var trap) && trap != onReturn && trap.Length > 0)
        {
            Traps.Remove("RETURN");
            await RunScriptTextAsync(trap, io, subshell: false, ct);
        }
        return LastExitCode = code;
    }

    internal bool InFunction => _locals.Count > 0;

    /// <summary>[[ -v name ]], [[ -v a[k] ]]: whether the variable or element is set.</summary>
    internal bool IsVariableSet(string expr)
    {
        if (ArrayRef(expr) is { Keys: false, Tail: "" } r)
            return r.Sub is "@" or "*" ? GetArray(r.Name) is { Items.Count: > 0 } : GetElement(r.Name, r.Sub) != null;
        return Parser.IsName(expr) || expr.All(char.IsDigit) ? IsSet(expr) : false;
    }

    /// <summary>The set -o options this shell has, and whether each is on.</summary>
    internal IEnumerable<(string Name, bool On)> OptionList => [
        ("allexport", false), ("braceexpand", true), ("emacs", false), ("errexit", Errexit), ("errtrace", false), ("functrace", false),
        ("hashall", true), ("histexpand", false), ("history", false), ("ignoreeof", false), ("interactive-comments", true), ("keyword", false),
        ("monitor", false), ("noclobber", Noclobber), ("noexec", false), ("noglob", Noglob), ("nolog", false), ("notify", false),
        ("nounset", Nounset), ("onecmd", false), ("physical", false), ("pipefail", Pipefail), ("posix", false), ("privileged", false),
        ("verbose", false), ("vi", false), ("xtrace", Xtrace)];

    /// <summary>set -o NAME / set +o NAME; false for an unknown name.</summary>
    internal bool SetOption(string name, bool on)
    {
        switch (name)
        {
            case "errexit": Errexit = on; return true;
            case "nounset": Nounset = on; return true;
            case "pipefail": Pipefail = on; return true;
            case "xtrace": Xtrace = on; return true;
            case "noglob": Noglob = on; return true;
            case "noclobber": Noclobber = on; return true;
            default: return OptionList.Any(o => o.Name == name); // accepted, no effect here
        }
    }

    /// <summary>`read -u N` / `mapfile -u N`: the input opened on fd N by `exec N&lt;file`, or null.</summary>
    internal StdinBuffer? InputFd(int fd) => Fds.GetValueOrDefault(fd) as StdinBuffer;

    /// <summary>`wait` with no arguments: every job has finished; forget them.</summary>
    internal void ForgetJobs() => _jobs.Clear();

    /// <summary>True for a built-in command (not one the app registered or replaced).</summary>
    internal bool IsBuiltin(string name) => _builtinNames?.Contains(name) == true;

    /// <summary>`local NAME[=VALUE]`: remembers the outer value and attributes so they are restored when the function returns.</summary>
    internal void DeclareLocal(string name, string? value)
    {
        if (_locals.Count == 0) throw new ShellException("can only be used in a function");
        if (_locals.Peek().TryAdd(name, (Environment.TryGetValue(name, out var old) ? old : null, VarAttrs.GetValueOrDefault(name)))) VarAttrs.Remove(name);
        MarkShellVariable(name);
        Environment[name] = value ?? "";
    }

    // ---------- variables ----------

    internal bool HasAttr(string name, char attr) => VarAttrs.Count > 0 && VarAttrs.TryGetValue(name, out var f) && f.Contains(attr);

    // variables the shell itself created; anything else in Environment came from the host and is exported
    readonly HashSet<string> _unexported = new(StringComparer.Ordinal);

    /// <summary>Whether a child bash (bash -c, a script) sees the variable: declared -x, or put in Environment by the host.</summary>
    internal bool IsExported(string name) => HasAttr(name, 'x') || Environment.ContainsKey(name) && !_unexported.Contains(name);

    /// <summary>Records that the shell (not the host) is about to create name, so it is not exported until `export`.</summary>
    internal void MarkShellVariable(string name)
    {
        if (!Environment.ContainsKey(name)) _unexported.Add(name);
    }

    internal void Unexport(string name)
    {
        SetAttrs(name, "", "x");
        _unexported.Add(name);
    }

    internal void SetAttrs(string name, string on, string off = "")
    {
        var f = VarAttrs.GetValueOrDefault(name, "");
        if (on.Contains('l')) f = f.Replace("u", "");
        if (on.Contains('u')) f = f.Replace("l", "");
        foreach (var c in on) if (!f.Contains(c)) f += c;
        foreach (var c in off) f = f.Replace(c.ToString(), "");
        if (f.Length == 0) VarAttrs.Remove(name); else VarAttrs[name] = f;
    }

    /// <summary>Follows namerefs (declare -n ref=name) to the variable they stand for.</summary>
    internal string Resolve(string name)
    {
        for (int i = 0; i < 10 && HasAttr(name, 'n') && Environment.TryGetValue(name, out var target) && target.Length > 0; i++) name = target;
        return name;
    }

    /// <summary>
    /// Assigns a variable the way bash does: through namerefs, refusing readonly ones, and applying -i (arithmetic),
    /// -l and -u. name[sub] sets an element; a plain assignment to an array sets element 0.
    /// </summary>
    internal void SetVar(string name, string value, bool append = false)
    {
        name = Resolve(name);
        if (ArrayRef(name) is { Keys: false, Tail: "" } r && r.Sub is not ("@" or "*"))
        {
            CheckWritable(r.Name);
            SetElement(r.Name, r.Sub, Converted(Resolve(r.Name), value, append ? GetElement(r.Name, r.Sub) : null));
            return;
        }
        if (!Parser.IsName(name)) throw new ShellException($"`{name}': not a valid identifier");
        CheckWritable(name);
        if (name == "SECONDS" && long.TryParse(value, out var seconds)) { _secondsBase = Now.AddSeconds(-seconds); return; }
        if (name == "RANDOM") { _random = (ulong)(long.TryParse(value, out var seed) ? seed : 0); return; }
        var array = Arrays.ContainsKey(name);
        value = Converted(name, value, !append ? null : array ? GetElement(name, "0") : Environment.GetValueOrDefault(name));
        if (array) SetElement(name, "0", value);
        else
        {
            ChargeVariable(name, value.Length, Environment.GetValueOrDefault(name)?.Length ?? 0);
            MarkShellVariable(name);
            Environment[name] = value;
        }
    }

    string Converted(string name, string value, string? old)
    {
        if (VarAttrs.Count == 0 || !VarAttrs.TryGetValue(name, out var f)) return old + value;
        if (f.Contains('i')) return (Arith.Eval(this, value) + (old is { Length: > 0 } ? Arith.Eval(this, old) : 0)).ToString(CultureInfo.InvariantCulture);
        value = old + value;
        return f.Contains('l') ? value.ToLowerInvariant() : f.Contains('u') ? value.ToUpperInvariant() : value;
    }

    void CheckWritable(string name)
    {
        if (HasAttr(name, 'r')) throw new ShellException($"{name}: readonly variable");
    }

    static readonly string[] BashVersInfo = ["5", "2", "21", "1", "release", "x86_64-pc-linux-gnu"];

    /// <summary>The array called name (through namerefs), including the shell's own FUNCNAME, PIPESTATUS and BASH_VERSINFO.</summary>
    internal ShellArray? GetArray(string name)
    {
        name = Resolve(name);
        if (Arrays.TryGetValue(name, out var a)) return a;
        IEnumerable<string>? items = name switch
        {
            "FUNCNAME" when _funcNames.Count > 0 => Enumerable.Reverse(_funcNames),
            "BASH_SOURCE" when _sources.Count > 0 => Enumerable.Reverse(_sources),
            "PIPESTATUS" => _pipeStatus.Select(c => c.ToString(CultureInfo.InvariantCulture)),
            "BASH_VERSINFO" => BashVersInfo,
            _ => null,
        };
        if (items == null) return null;
        var built = new ShellArray(false);
        foreach (var item in items) built.Set(built.Next.ToString(CultureInfo.InvariantCulture), item);
        return built;
    }

    /// <summary>Shell variables bash sets itself; they are not in the environment `env` prints.</summary>
    string? ShellVariable(string name) => name switch
    {
        "IFS" => _ifsUnset ? null : " \t\n",
        "OPTIND" => "1",
        "BASH" => "/usr/bin/bash",
        "BASH_VERSION" => "5.2.21(1)-release",
        "UID" or "EUID" => Options.User == "root" ? "0" : "1000",
        "PPID" => "4241",
        "BASHPID" => "4242",
        "SHLVL" => "1",
        "OSTYPE" => "linux-gnu",
        "MACHTYPE" => "x86_64-pc-linux-gnu",
        "HOSTTYPE" => "x86_64",
        "LINENO" => _lineno.ToString(CultureInfo.InvariantCulture),
        "SECONDS" => ((long)(Now - _secondsBase).TotalSeconds).ToString(CultureInfo.InvariantCulture),
        "EPOCHSECONDS" => new DateTimeOffset(Now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        "EPOCHREALTIME" => (new DateTimeOffset(Now).ToUnixTimeMilliseconds() / 1000.0).ToString("0.000000", CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>$-: the single-letter options that are on.</summary>
    string OptionFlags => (Errexit ? "e" : "") + (Noglob ? "f" : "") + "h" + (Nounset ? "u" : "") + (Xtrace ? "x" : "") + "B" + (Noclobber ? "C" : "") + "c";

    // ---------- arrays ----------

    /// <summary>The array to change: created if missing, copied first if a saved state still shares it.</summary>
    internal ShellArray Writable(string name, bool assoc = false)
    {
        name = Resolve(name);
        CheckWritable(name);
        if (!Arrays.TryGetValue(name, out var a))
        {
            a = new ShellArray(assoc) { Epoch = _epoch };
            if (Environment.Remove(name, out var scalar)) a.Set("0", scalar); // a plain variable becomes element 0
            return Arrays[name] = a;
        }
        return a.Epoch == _epoch ? a : Arrays[name] = a.Clone(_epoch);
    }

    /// <summary>`local arr` / `declare -A m`: a fresh array, restored to the caller's when the function returns.</summary>
    internal void DeclareArray(string name, bool assoc, bool local)
    {
        CheckWritable(name);
        if (local && _localArrays.Count > 0) _localArrays.Peek().TryAdd(name, Arrays.GetValueOrDefault(name));
        else if (Arrays.TryGetValue(name, out var old) && old.Assoc == assoc) return;
        if (local && _locals.Count > 0) DeclareLocal(name, null);
        Environment.Remove(name);
        Arrays[name] = new ShellArray(assoc) { Epoch = _epoch };
    }

    /// <summary>The storage key for a subscript: the text itself for an associative array, else its arithmetic value.</summary>
    internal string ArrayKey(ShellArray? array, string subscript, string name = "")
    {
        if (array is { Assoc: true }) return subscript;
        long i = Arith.Eval(this, subscript);
        if (i < 0) i += array?.Next ?? 1;
        return i >= 0 ? i.ToString(CultureInfo.InvariantCulture) : throw new ShellException($"{name}: bad array subscript");
    }

    /// <summary>One element, or null if unset. A plain variable answers as element 0.</summary>
    internal string? GetElement(string name, string subscript)
    {
        name = Resolve(name);
        if (GetArray(name) is { } a) return a.Get(ArrayKey(a, subscript, name));
        return Environment.TryGetValue(name, out var scalar) && ArrayKey(null, subscript, name) == "0" ? scalar : null;
    }

    internal void SetElement(string name, string subscript, string value)
    {
        var a = Writable(name);
        var key = ArrayKey(a, subscript, name);
        ChargeVariable(name, key.Length + value.Length, a.Get(key) is { } old ? key.Length + old.Length : 0);
        a.Set(key, value);
    }

    /// <summary>unset NAME or unset 'NAME[sub]'; -n (<paramref name="nameref"/>) unsets a nameref itself, not what it names.</summary>
    internal void Unset(string target, bool nameref = false)
    {
        if (!nameref) target = Resolve(target);
        if (ArrayRef(target) is { Keys: false, Tail: "" } r && Arrays.ContainsKey(r.Name) && r.Sub is not ("@" or "*"))
        {
            var a = Writable(r.Name);
            a.Items.Remove(ArrayKey(a, Unquote(r.Sub), r.Name));
            return;
        }
        var name = ArrayRef(target)?.Name ?? target;
        if (HasAttr(name, 'r')) throw new ShellException($"{name}: cannot unset: readonly variable");
        Environment.Remove(name);
        Arrays.Remove(name);
        VarAttrs.Remove(name);
        _unexported.Remove(name);
        if (name == "IFS") _ifsUnset = true;
    }

    static string Unquote(string s) => s.Length >= 2 && s[0] is '"' or '\'' && s[^1] == s[0] ? s[1..^1] : s;

    /// <summary>Splits name[sub]rest (or !name[sub] for the keys); null when the text is not that shape.</summary>
    internal static (bool Keys, string Name, string Sub, string Tail)? ArrayRef(string expr)
    {
        int start = expr.StartsWith('!') ? 1 : 0, open = expr.IndexOf('[');
        if (open <= start || !Parser.IsName(expr[start..open])) return null;
        int depth = 0, k = open;
        for (; k < expr.Length; k++)
            if (expr[k] == '[') depth++;
            else if (expr[k] == ']' && --depth == 0) break;
        return k < expr.Length ? (start == 1, expr[start..open], expr[(open + 1)..k], expr[(k + 1)..]) : null;
    }

    /// <summary>The elements ${name[@]...} stands for (keys for ${!name[@]}); null if expr is not a whole-array expansion.</summary>
    async Task<List<string>?> ArrayElementsAsync(string expr, Io io, CancellationToken ct, bool quoted = false)
    {
        if (ArrayRef(expr) is not { Sub: "@" or "*" } r) return null;
        var a = GetArray(r.Name);
        var name = Resolve(r.Name);
        List<string> items = a == null ? (Environment.TryGetValue(name, out var scalar) ? [r.Keys ? "0" : scalar] : [])
            : r.Keys ? [.. a.Items.Keys] : [.. a.Items.Values];
        if (r.Keys || r.Tail.Length == 0) return items;
        if (DefaultOp(r.Tail) is var (op, word))
        {
            // ${a[@]:-word}: the array as a whole is tested; empty means no elements (or, with ':', nothing but empty text)
            bool missing = items.Count == 0 || op[0] == ':' && string.Join(r.Sub == "*" ? IfsJoiner : " ", items).Length == 0;
            if (op.EndsWith('?') && missing) throw Fatal(io, $"{r.Name}[{r.Sub}]: {(word.Length > 0 ? await ParamWordAsync(word, quoted, io, ct) : "parameter null or not set")}", 127);
            if (op.EndsWith('=') && missing) throw new ShellException($"{r.Name}[{r.Sub}]: bad array subscript");
            if (op.EndsWith('-')) return missing ? [await ParamWordAsync(word, quoted, io, ct)] : items;
            if (op.EndsWith('+')) return missing ? [] : [await ParamWordAsync(word, quoted, io, ct)];
            return items;
        }
        if (r.Tail[0] == ':')
        {
            var spec = r.Tail[1..].Split(':', 2);
            long from = await EvalArithAsync(spec[0], io, ct);
            long count = spec.Length == 1 ? long.MaxValue : await EvalArithAsync(spec[1], io, ct);
            if (count < 0) throw new ShellException($"{spec[1].Trim()}: substring expression < 0");
            // an indexed array is sliced by index, so a sparse array skips its gaps: offsets count from the first index >= from
            if (a is { Assoc: false } && !r.Keys)
            {
                var keys = a.Items.Keys.Select(long.Parse).ToList();
                if (from < 0) from += keys.Count == 0 ? 0 : keys[^1] + 1;
                if (from < 0) return [];
                int first = keys.FindIndex(k => k >= from);
                return first < 0 ? [] : items.Skip(first).Take((int)Math.Min(count, int.MaxValue)).ToList();
            }
            if (from < 0) from += items.Count;
            if (from < 0 || from > items.Count) return [];
            return items.Skip((int)from).Take((int)Math.Min(count, int.MaxValue)).ToList();
        }
        // any other operator applies to each element: ${files[@]%.txt}, ${names[@]^^}
        var result = new List<string>();
        foreach (var item in items) result.Add(await ElementOpAsync(item, r.Tail, io, ct, quoted));
        return result;
    }

    const string ScratchVariable = "__topwater_element";

    /// <summary>Applies a ${...} operator to one value by expanding it through a scratch variable.</summary>
    async Task<string> ElementOpAsync(string? value, string rest, Io io, CancellationToken ct, bool quoted = false)
    {
        if (rest.Length == 0) return value ?? "";
        if (value == null) Environment.Remove(ScratchVariable); else Environment[ScratchVariable] = value;
        try { return await ExpandParameterAsync(ScratchVariable + rest, io, ct, quoted); }
        finally { Environment.Remove(ScratchVariable); }
    }

    /// <summary>`unset 'a[${#a[@]}-1]'`: name[sub] with the subscript's expansions done (the subscript stays text otherwise).</summary>
    internal async Task<string> ExpandSubscriptAsync(string target, Io io, CancellationToken ct) =>
        ArrayRef(target) is { Keys: false, Tail: "" } r && r.Sub.IndexOfAny(['$', '`']) >= 0 ? $"{r.Name}[{await SubscriptAsync(r.Sub, io, ct)}]" : target;

    async Task<string> SubscriptAsync(string sub, Io io, CancellationToken ct) =>
        sub.IndexOfAny(['$', '`', '"', '\'']) < 0 ? sub : await ExpandSingleAsync(new Word(new Lexer(Unquote(sub)).LexQuoted(null)), io, ct);

    async Task AssignArrayAsync(Assignment a, Io io, CancellationToken ct)
    {
        var name = Resolve(a.Name);
        if (a.Elements == null)
        {
            var key = await ExpandSingleAsync(a.Index!, io, ct);
            var value = await ExpandAssignmentAsync(a.Value, io, ct);
            if (Xtrace) io.Err.WriteLine($"+ {name}[{key}]{(a.Append ? "+=" : "=")}{TraceQuote(value)}");
            SetVar($"{name}[{key}]", value, a.Append);
            return;
        }
        ShellArray target;
        if (a.Append) target = Writable(name);
        else
        {
            CheckWritable(name);
            target = new ShellArray(Arrays.TryGetValue(name, out var old) && old.Assoc) { Epoch = _epoch };
            Environment.Remove(name);
        }
        foreach (var w in Parser.ParseWords(a.Elements))
        {
            // [key]=value sets that element; anything else is appended, after the usual splitting and globbing
            if (w.Parts is [LiteralPart { Quoted: false, Text: ['[', ..] }, ..] && Parser.SplitSubscript(w, 1) is var (index, element, _))
                target.Set(ArrayKey(target, await ExpandSingleAsync(index, io, ct)), await ExpandSingleAsync(element, io, ct));
            else
                foreach (var field in await ExpandWordsAsync([w], io, ct)) target.Set(target.Next.ToString(CultureInfo.InvariantCulture), field);
        }
        Arrays[name] = target;
    }

    /// <summary>
    /// `local a=(x y)`, `declare -A m`, `declare -a list=()`: the array arguments are handled here, because a built-in
    /// only receives expanded text. Returns the words left for the built-in, or null if none were array declarations.
    /// </summary>
    async Task<List<Word>?> DeclareArraysAsync(List<Word> words, Io io, CancellationToken ct)
    {
        var flags = string.Concat(words.Skip(1).Select(w => w.UnquotedLiteral).Where(t => t is ['-', ..]));
        if (flags.Contains('p') || flags.Contains('f') || flags.Contains('F')) return null;
        bool local = words[0].Is("local") || InFunction && !flags.Contains('g');
        bool assoc = flags.Contains('A'), any = false;
        var attrs = string.Concat(flags.Where(c => c is 'r' or 'x')) + (words[0].Is("readonly") ? "r" : "");
        var keep = new List<Word> { words[0] };
        foreach (var w in words.Skip(1))
        {
            if (w.UnquotedLiteral is ['-', ..]) continue;
            var assignment = Parser.TryAssignment(w);
            if (assignment is { Elements: not null })
            {
                DeclareArray(assignment.Name, assoc, local);
                await AssignArrayAsync(assignment, io, ct);
            }
            else if (assignment == null && (assoc || flags.Contains('a')) && w.UnquotedLiteral is { } name && Parser.IsName(name))
                DeclareArray(name, assoc, local);
            else { keep.Add(w); continue; }
            if (attrs.Length > 0) SetAttrs(assignment?.Name ?? w.UnquotedLiteral!, attrs);
            any = true;
        }
        return any ? keep : null;
    }

    // ---------- expansion ----------

    internal string GetVariable(string name)
    {
        if (name.Length > 0 && char.IsDigit(name[0]) && int.TryParse(name, out var n))
            return n == 0 ? _arg0 : n <= Positional.Count ? Positional[n - 1] : "";
        return name switch
        {
            "?" => LastExitCode.ToString(CultureInfo.InvariantCulture),
            "$" => "4242",
            "!" => _lastBg == 0 ? "" : _lastBg.ToString(CultureInfo.InvariantCulture),
            "#" => Positional.Count.ToString(CultureInfo.InvariantCulture),
            "@" => string.Join(' ', Positional),
            "*" => string.Join(IfsJoiner, Positional),
            "-" => OptionFlags,
            "_" => _lastArg,
            "PWD" => _cwd,
            "RANDOM" => NextRandom(32768).ToString(CultureInfo.InvariantCulture),
            _ => LookUp(name) ?? "",
        };
    }

    /// <summary>A named variable's value (element 0 of an array), or null when it is unset.</summary>
    string? LookUp(string name)
    {
        name = Resolve(name);
        if (GetArray(name) is { } array) return array.Get("0");
        return Environment.TryGetValue(name, out var v) ? v : ShellVariable(name);
    }

    /// <summary>The IFS that splitting uses: the default when it is unset.</summary>
    internal string Ifs => Environment.GetValueOrDefault("IFS", " \t\n");

    /// <summary>"$*" and "${a[*]}" join with the first character of IFS (a space when IFS is unset).</summary>
    string IfsJoiner => Environment.TryGetValue("IFS", out var ifs) ? (ifs.Length > 0 ? ifs[..1] : "") : " ";

    bool IsSet(string name) =>
        name is "?" or "$" or "#" or "@" or "*" or "-" or "_" or "PWD" or "RANDOM" or "0"
        || (char.IsDigit(name[0]) && int.TryParse(name, out var n) && n <= Positional.Count)
        || name == "!" && _lastBg != 0
        || LookUp(name) != null;

    internal async Task<string> ExpandPartAsync(WordPart part, Io io, CancellationToken ct) => part switch
    {
        LiteralPart l => l.Text,
        ExpandedTextPart e => e.Text,
        VariablePart v => await ExpandParameterAsync(v.Name, io, ct, v.Quoted),
        CommandSubPart c => await CaptureAsync(c.Source, io, ct),
        ArithPart a => (await ArithExpansionAsync(a.Expr, io, ct)).ToString(CultureInfo.InvariantCulture),
        ProcSubPart p => await ProcessSubstitutionAsync(p.Source, io, ct),
        _ => "",
    };

    /// <summary>Expands $(cmd), `cmd` and ${...} inside an arithmetic expression, then evaluates it.</summary>
    async Task<long> EvalArithAsync(string expr, Io io, CancellationToken ct)
    {
        // each level of $(( $(( ... )) )) re-reads what is inside it, so unbounded nesting is quadratic work
        if (++_arithDepth > 100) { _arithDepth = 0; throw new LimitExceeded("arithmetic is nested too deeply"); }
        try { return Arith.Eval(this, expr.IndexOfAny(['$', '`']) < 0 ? expr : await ExpandSingleAsync(new Word(new Lexer(expr).LexQuoted(null)), io, ct)); }
        finally { if (_arithDepth > 0) _arithDepth--; }
    }
    int _arithDepth;

    /// <summary>$(( )): unlike (( )) and let, an error here ends the script, as in bash.</summary>
    async Task<long> ArithExpansionAsync(string expr, Io io, CancellationToken ct)
    {
        try { return await EvalArithAsync(expr, io, ct); }
        catch (ShellException e) { throw Fatal(io, e.Message, 1); }
    }

    /// <summary>An expansion error that ends a non-interactive bash: the shell exits (a subshell with status 1).</summary>
    ExitSignal Fatal(Io io, string message, int code)
    {
        io.Err.WriteLine($"{ErrPrefix}{message}");
        return new ExitSignal(_subshell > 0 ? 1 : code);
    }

    static readonly Regex ParamRe = new(@"^([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!-])(.*)$", RegexOptions.Singleline);
    static readonly Regex IndirectRe = new(@"^!([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!-])(.*)$", RegexOptions.Singleline);
    static readonly Regex PrefixRe = new(@"^!([A-Za-z_][A-Za-z0-9_]*)[*@]$");

    /// <summary>${p-w} ${p:-w} ${p=w} ${p:=w} ${p+w} ${p:+w} ${p?w} ${p:?w}: the operator and the word; null for any other operator.</summary>
    static (string Op, string Word)? DefaultOp(string rest)
    {
        int n = rest.Length > 0 && rest[0] == ':' ? 2 : 1;
        return rest.Length >= n && rest[n - 1] is '-' or '=' or '+' or '?' ? (rest[..n], rest[n..]) : null;
    }

    /// <summary>${!name...}: the expression with name's value in its place; null if expr is not an indirection.</summary>
    string? IndirectTarget(string expr)
    {
        if (expr.Length < 2 || expr[0] != '!' || PrefixRe.IsMatch(expr) || ArrayRef(expr) is { Keys: true } || IndirectRe.Match(expr) is not { Success: true } m) return null;
        var name = m.Groups[1].Value;
        if (HasAttr(name, 'n')) return null; // ${!ref} of a nameref is the name it refers to
        var target = GetVariable(name);
        if (target.Length == 0) throw new ShellException($"{name}: invalid indirect expansion");
        return target + m.Groups[2].Value;
    }

    readonly Dictionary<(string, bool), Word> _paramWords = [];

    /// <summary>The word of ${x op word}, lexed once per distinct text: quotes in it apply, and inside "..." a ' is literal.</summary>
    Word ParamWord(string text, bool quoted)
    {
        if (_paramWords.TryGetValue((text, quoted), out var w)) return w;
        if (_paramWords.Count >= 512) _paramWords.Clear();
        try { w = new Word(new Lexer(text).LexParamWord(quoted)); }
        catch (ParseException e) { throw new ShellException(e.Message); }
        return _paramWords[(text, quoted)] = w;
    }

    Task<string> ParamWordAsync(string text, bool quoted, Io io, CancellationToken ct) => ExpandSingleAsync(ParamWord(text, quoted), io, ct);

    /// <summary>The text of ${x/pat/rep}'s replacement as a .NET replacement: an unquoted &amp; stands for the match (bash 5.2).</summary>
    async Task<string> ReplacementAsync(string text, bool quoted, Io io, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var part in ParamWord(text, quoted).Parts)
        {
            var t = await ExpandPartAsync(part, io, ct);
            if (sb.Length == 0 && part is LiteralPart { Quoted: false } && TildePrefix(t) is var (home, length)) t = home + t[length..];
            t = t.Replace("$", "$$");
            sb.Append(part.Quoted ? t : t.Replace("&", "$0"));
        }
        return sb.ToString();
    }

    /// <summary>${name}, ${#name}, ${name:-x}, ${name%pat}, ${name/a/b}, ${name:1:2}, ${name^^}, ${name@Q}, ${!name} ...</summary>
    async Task<string> ExpandParameterAsync(string expr, Io io, CancellationToken ct, bool quoted = false)
    {
        if (expr.Length > 1 && expr[0] == '!')
        {
            if (PrefixRe.Match(expr) is { Success: true } pm)
                return string.Join(' ', Environment.Keys.Concat(Arrays.Keys).Where(k => k.StartsWith(pm.Groups[1].Value, StringComparison.Ordinal) && k != ScratchVariable)
                    .Distinct().Order(StringComparer.Ordinal));
            if (Parser.IsName(expr[1..]) && HasAttr(expr[1..], 'n')) return Environment.GetValueOrDefault(expr[1..], "");
            if (IndirectTarget(expr) is { } target) return await ExpandParameterAsync(target, io, ct, quoted);
        }
        if (expr.Length > 1 && expr[0] == '#' && ArrayRef(expr[1..]) is { Keys: false, Tail: "" } counted)
        {
            if (counted.Sub is "@" or "*")
                return (GetArray(counted.Name) is { } a ? a.Items.Count : LookUp(counted.Name) != null ? 1 : 0).ToString(CultureInfo.InvariantCulture);
            return (GetElement(counted.Name, await SubscriptAsync(counted.Sub, io, ct)) ?? "").Length.ToString(CultureInfo.InvariantCulture);
        }
        // as one string, ${a[*]} joins with the first character of IFS and ${a[@]} with a space
        if (await ArrayElementsAsync(expr, io, ct, quoted) is { } all)
            return string.Join(ArrayRef(expr)!.Value.Sub == "*" ? IfsJoiner : " ", all);
        if (ArrayRef(expr) is { Keys: false } one)
        {
            string? element;
            try { element = GetElement(one.Name, await SubscriptAsync(one.Sub, io, ct)); }
            catch (ShellException e) { io.Err.WriteLine($"{ErrPrefix}{e.Message}"); element = null; } // like bash: reported, and the expansion is empty
            return await ElementOpAsync(element, one.Tail, io, ct, quoted);
        }
        if (expr.Length > 1 && expr[0] == '#')
        {
            if (expr[1..] is "@" or "*") return Positional.Count.ToString(CultureInfo.InvariantCulture);
            CheckSet(expr[1..], io);
            return GetVariable(expr[1..]).Length.ToString(CultureInfo.InvariantCulture);
        }
        var m = ParamRe.Match(expr);
        if (!m.Success) throw new ShellException($"${{{expr}}}: bad substitution");
        var name = m.Groups[1].Value;
        var rest = m.Groups[2].Value;
        var value = GetVariable(name);
        var defaultOp = DefaultOp(rest);
        if (defaultOp == null) CheckSet(name, io); // defaults handle unset variables
        if (rest.Length == 0) return value;

        if (defaultOp is var (op, word))
        {
            bool missing = op[0] == ':' ? value.Length == 0 : !IsSet(name);
            switch (op[^1])
            {
                case '-': return missing ? await ParamWordAsync(word, quoted, io, ct) : value;
                case '=':
                    if (!missing) return value;
                    if (!Parser.IsName(name)) throw new ShellException($"${name}: cannot assign in this way");
                    SetVar(name, await ParamWordAsync(word, quoted, io, ct));
                    return GetVariable(name);
                case '+': return missing ? "" : await ParamWordAsync(word, quoted, io, ct);
                default:
                    if (missing) throw Fatal(io, $"{name}: {(word.Length > 0 ? await ParamWordAsync(word, quoted, io, ct) : op[0] == ':' ? "parameter null or not set" : "parameter not set")}", 127);
                    return value;
            }
        }
        if (rest[0] is '#' or '%')
        {
            bool longest = rest.Length > 1 && rest[1] == rest[0];
            var re = Glob.ToRegex(await PatternAsync(ParamWord(rest[(longest ? 2 : 1)..], quoted), io, ct), slashes: true);
            if (rest[0] == '#')
            {
                for (int i = longest ? value.Length : 0; longest ? i >= 0 : i <= value.Length; i += longest ? -1 : 1)
                    if (re.IsMatch(value[..i])) return value[i..];
            }
            else
            {
                for (int i = longest ? 0 : value.Length; longest ? i <= value.Length : i >= 0; i += longest ? 1 : -1)
                    if (re.IsMatch(value[i..])) return value[..i];
            }
            return value;
        }
        if (rest[0] == '/')
        {
            var body = rest[1..];
            char mode = body.Length > 0 && body[0] is '/' or '#' or '%' ? body[0] : ' ';
            if (mode != ' ') body = body[1..];
            // the pattern ends at the first unquoted '/'; after `//` a leading '/' is part of the pattern
            int slash = -1;
            for (int i = mode == '/' && body.StartsWith('/') ? 1 : 0; i < body.Length; i++)
            {
                if (body[i] == '\\') { i++; continue; }
                if (body[i] == '\'' && !quoted) { i = body.IndexOf('\'', i + 1) is var q and >= 0 ? q : body.Length; continue; }
                if (body[i] == '"') { for (i++; i < body.Length && body[i] != '"'; i++) if (body[i] == '\\') i++; continue; }
                if (body[i] == '/') { slash = i; break; }
            }
            var pat = Glob.ToRegexBody(await PatternAsync(ParamWord(slash < 0 ? body : body[..slash], quoted), io, ct), slashes: true);
            var rep = slash < 0 ? "" : await ReplacementAsync(body[(slash + 1)..], quoted, io, ct);
            // an empty pattern matches nothing, except at an anchor: ${x/#/pre-} and ${x/%/-post} add to the ends
            if (pat.Length == 0) return mode == '#' ? rep.Replace("$$", "$") + value : mode == '%' ? value + rep.Replace("$$", "$") : value;
            var re = Glob.Cached(mode == '#' ? "^(?:" + pat + ")" : mode == '%' ? "(?:" + pat + ")$" : pat, RegexOptions.Singleline);
            // ${x//?/$x} grows with the square of x: refuse before building it
            if (mode == '/' && value.Length + (long)rep.Length * (value.Length + 1) > Options.MaxStringBytes
                && value.Length + (long)rep.Length * re.Count(value) > Options.MaxStringBytes)
                throw TooLarge("expansion", "write the data to a file instead");
            return re.Replace(value, rep, mode == '/' ? -1 : 1);
        }
        if (rest[0] is '^' or ',' or '~')
        {
            // ${x^} ${x^^} ${x,} ${x,,} ${x~} ${x~~}, optionally only for characters matching a pattern: ${x^^[aeiou]}
            bool every = rest.Length > 1 && rest[1] == rest[0];
            var only = rest.Length > (every ? 2 : 1) ? Glob.ToRegex(await PatternAsync(ParamWord(rest[(every ? 2 : 1)..], quoted), io, ct), slashes: true) : null;
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length && (every || i == 0); i++)
                if (only == null || only.IsMatch(chars[i].ToString()))
                    chars[i] = rest[0] == '^' || rest[0] == '~' && char.IsLower(chars[i]) ? char.ToUpperInvariant(chars[i]) : char.ToLowerInvariant(chars[i]);
            return new string(chars);
        }
        if (rest.Length == 2 && rest[0] == '@')
        {
            if (rest[1] == 'a') return Builtins.AttributeFlags(this, Resolve(name));
            if (!IsSet(name)) return "";
            return rest[1] switch
            {
                'Q' or 'K' => QuoteValue(value),
                'E' => Builtins.Unescape(value, keepUnknown: true),
                'U' => value.ToUpperInvariant(),
                'L' => value.ToLowerInvariant(),
                'u' => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..],
                'A' => Builtins.AttributeFlags(this, Resolve(name)) is { Length: > 0 } flags ? $"declare -{flags} {name}={QuoteValue(value)}" : $"{name}={QuoteValue(value)}",
                'P' => value,
                _ => throw new ShellException($"${{{expr}}}: bad substitution"),
            };
        }
        if (rest[0] == ':')
        {
            var spec = rest[1..].Split(':', 2);
            long offset = await EvalArithAsync(spec[0], io, ct);
            if (offset < 0) offset += value.Length;
            if (offset < 0 || offset > value.Length) return "";
            var tail = value[(int)offset..];
            if (spec.Length == 1) return tail;
            long len = await EvalArithAsync(spec[1], io, ct);
            if (len < 0 && (len += tail.Length) < 0) throw new ShellException($"{spec[1].Trim()}: substring expression < 0");
            return tail[..(int)Math.Min(len, tail.Length)];
        }
        throw new ShellException($"${{{expr}}}: bad substitution");
    }

    /// <summary>set -u: expanding an unset variable is an error that ends the (sub)shell, like bash.</summary>
    void CheckSet(string name, Io io)
    {
        if (!Nounset || name is "@" or "*" || IsSet(name)) return;
        throw Fatal(io, $"{name}: unbound variable", 127);
    }

    /// <summary>&lt;(cmd): writes cmd's output to /dev/fd/N (removed after the tool call) and returns that path.</summary>
    /// <summary>$(...) and &lt;(...) run in a subshell that, as in bash, does not inherit set -e (unless shopt inherit_errexit).</summary>
    async Task<int> RunSubstitutionAsync(string source, Io io, CancellationToken ct)
    {
        var errexit = Errexit;
        if (!Shopts.Contains("inherit_errexit")) Errexit = false;
        try { return await RunScriptTextAsync(source, io, subshell: true, ct); }
        finally { Errexit = errexit; }
    }

    async Task<string> ProcessSubstitutionAsync(string source, Io io, CancellationToken ct)
    {
        var output = new CappedWriter(this);
        await RunSubstitutionAsync(source, io with { Stdin = new StdinBuffer(""), Out = output }, ct);
        var path = $"/dev/fd/{63 + _procSubCount++}";
        using (Fs.AsRoot()) Fs.WriteAllText(path, output.ToString());
        return path;
    }

    async Task<string> CaptureAsync(string source, Io io, CancellationToken ct)
    {
        if (ReadFileWord(source) is { } fileWord) // $(< file) is the file's contents
        {
            var target = await ExpandSingleAsync(fileWord, io, ct);
            try
            {
                var text = Fs.ReadAllText(ResolvePath(target));
                LastExitCode = 0;
                return text.TrimEnd('\n');
            }
            catch (ShellException e)
            {
                io.Err.WriteLine($"{ErrPrefix}{target}: {e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]}");
                LastExitCode = 1;
                return "";
            }
        }
        var output = new CappedWriter(this);
        LastExitCode = await RunSubstitutionAsync(source, io with { Stdin = new StdinBuffer(""), Out = output }, ct);
        return output.ToString().TrimEnd('\n');
    }

    /// <summary>The file word of `$(&lt; file)`, or null when the substitution is anything else.</summary>
    static Word? ReadFileWord(string source)
    {
        if (!source.Contains('<')) return null;
        Script script;
        try { script = Parser.Parse(source); }
        catch (ParseException) { return null; }
        return script.Items is [{ Rest.Count: 0, First: { Negate: false, Commands: [SimpleCommand { Words.Count: 0, Assignments.Count: 0 } cmd] } }]
               && cmd.Redirects is [{ Op: "<" } r] ? r.Target : null;
    }

    /// <summary>Expands a word without field splitting or globbing (redirect targets, heredocs, case subjects).</summary>
    internal async Task<string> ExpandSingleAsync(Word word, Io io, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < word.Parts.Count; i++)
        {
            var text = await ExpandPartAsync(word.Parts[i], io, ct);
            if (i == 0 && word.Parts[i] is LiteralPart { Quoted: false } && TildePrefix(text) is var (home, length)) text = home + text[length..];
            if (sb.Length + (long)text.Length > Options.MaxStringBytes) throw TooLarge("expansion", "write the data to a file instead");
            sb.Append(text);
        }
        return sb.ToString();
    }

    static readonly Regex AssignmentTilde = new(@"(?<=^|:)~([A-Za-z0-9_.-]*)(?=/|:|$)");

    /// <summary>An assignment's value: one string, with ~ expanded at the start and after each ':' (PATH=~/bin:~/x).</summary>
    internal async Task<string> ExpandAssignmentAsync(Word word, Io io, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < word.Parts.Count; i++)
        {
            var text = await ExpandPartAsync(word.Parts[i], io, ct);
            if (word.Parts[i] is LiteralPart { Quoted: false } && text.Contains('~'))
            {
                bool atStart = i == 0 || sb.Length > 0 && sb[^1] == ':';
                text = AssignmentTilde.Replace(text, m => (m.Index == 0 && !atStart ? null : TildeValue(m.Groups[1].Value)) ?? m.Value);
            }
            sb.Append(text);
        }
        return sb.ToString();
    }

    /// <summary>Expands a word into a glob pattern: quoted text is escaped, unquoted text keeps its wildcards.</summary>
    internal async Task<string> PatternAsync(Word word, Io io, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var part in word.Parts)
        {
            var text = await ExpandPartAsync(part, io, ct);
            sb.Append(part.Quoted ? Glob.Escape(text) : text);
        }
        return sb.ToString();
    }

    /// <summary>~, ~user, ~+ and ~-: the directory they name, or null for an unknown user.</summary>
    string? TildeValue(string user) => user switch
    {
        "" => Environment.GetValueOrDefault("HOME", "/"),
        "+" => _cwd,
        "-" => Environment.GetValueOrDefault("OLDPWD"),
        "root" => "/root",
        _ when user == Options.User => "/home/" + user,
        _ => null,
    };

    /// <summary>A leading ~ or ~user (up to the first '/'): its directory and how many characters it spans.</summary>
    (string Home, int Length)? TildePrefix(string text)
    {
        if (text.Length == 0 || text[0] != '~') return null;
        int end = text.IndexOf('/');
        if (end < 0) end = text.Length;
        return TildeValue(text[1..end]) is { } home ? (home, end) : null;
    }

    /// <summary>
    /// Splits text on IFS like bash: IFS whitespace runs collapse and are trimmed at the ends, every other IFS character
    /// delimits one field. maxFields &gt; 0 leaves the rest of the line in the last field (`read a b`).
    /// </summary>
    internal static List<string> SplitFields(string text, string ifs, int maxFields = 0)
    {
        if (ifs.Length == 0) return text.Length > 0 ? [text] : [];
        var ws = string.Concat(" \t\n".Where(ifs.Contains));
        var other = string.Concat(ifs.Where(c => !ws.Contains(c)));
        if (ws.Length > 0) text = text.Trim(ws.ToCharArray());
        if (text.Length == 0) return [];
        static string Set(string chars) => "[" + string.Concat(chars.Select(c => c is '\\' or ']' or '[' or '^' or '-' ? "\\" + c : c.ToString())) + "]";
        string wsRx = Set(ws), otherRx = Set(other);
        var sep = other.Length == 0 ? wsRx + "+" : ws.Length == 0 ? otherRx : $"{wsRx}*{otherRx}{wsRx}*|{wsRx}+";
        var fields = Glob.Cached(sep, RegexOptions.None).Split(text, Math.Max(0, maxFields)).ToList();
        if (maxFields <= 0 && other.Length > 0 && fields.Count > 0 && fields[^1].Length == 0) fields.RemoveAt(fields.Count - 1); // "a,b," is two fields
        return fields;
    }

    sealed class Field
    {
        public readonly StringBuilder Text = new();
        public readonly StringBuilder Pattern = new();
        public bool HasGlob, HasContent;
    }

    /// <summary>
    /// An unquoted ${p:-word} or ${p:+word} whose word applies: the word's parts, so quotes inside the word still keep
    /// it from being split (${v:-"a b"} is one field). Null when p's own value applies.
    /// </summary>
    async Task<IReadOnlyList<WordPart>?> AlternativeWordAsync(string expr, Io io, CancellationToken ct)
    {
        var element = ArrayRef(expr);
        var match = element == null ? ParamRe.Match(expr) : null;
        var rest = element is { Keys: false } r && r.Sub is not ("@" or "*") ? r.Tail : match is { Success: true } ? match.Groups[2].Value : "";
        if (DefaultOp(rest) is not var (op, word) || op[^1] is not ('-' or '+')) return null;
        var value = element != null ? GetElement(element.Value.Name, await SubscriptAsync(element.Value.Sub, io, ct))
            : IsSet(match!.Groups[1].Value) ? GetVariable(match.Groups[1].Value) : null;
        bool missing = op[0] == ':' ? string.IsNullOrEmpty(value) : value == null;
        if (op[^1] == '-' ? !missing : missing) return op[^1] == '-' ? null : [];
        // the word's unquoted text is split like any expansion's
        return ParamWord(word, false).Parts.Select(p => p is LiteralPart { Quoted: false } l ? new ExpandedTextPart(l.Text) : p).ToList();
    }

    internal async Task<List<string>> ExpandWordsAsync(IEnumerable<Word> words, Io io, CancellationToken ct)
    {
        var result = new List<string>();
        long total = 0; // all the words together, so {1..10000}$big can't multiply a big value
        foreach (var word in words.SelectMany(Brace.Expand))
        {
            var fields = new List<Field>();
            var cur = new Field();
            void End() { if (cur.HasContent) fields.Add(cur); cur = new Field(); }
            void AddText(string s, bool quoted)
            {
                if ((total += s.Length) > Options.MaxStringBytes) throw TooLarge("expansion", "write the data to a file instead");
                cur.Text.Append(s);
                cur.Pattern.Append(quoted ? Glob.Escape(s) : s);
                if (!quoted && Glob.HasWildcard(s)) cur.HasGlob = true;
                cur.HasContent = true;
            }
            // unquoted $VAR / $(cmd) / $((...)): split into fields on $IFS
            void AddSplit(string text)
            {
                var ifs = Ifs;
                var pieces = SplitFields(text, ifs);
                if (pieces.Count == 0) { if (text.Length > 0 && ifs.Length > 0) End(); return; }
                if (ifs.Contains(text[0])) End();
                for (int k = 0; k < pieces.Count; k++)
                {
                    if (k > 0) End();
                    AddText(pieces[k], false);
                }
                if (ifs.Contains(text[^1])) End();
            }
            // "$@" / "${a[@]}": one field per item, and none at all when there are none; unquoted, each item is split
            void AddEach(IReadOnlyList<string> items, bool quoted)
            {
                for (int k = 0; k < items.Count; k++)
                {
                    if (k > 0) End();
                    if (quoted) AddText(items[k], true); else AddSplit(items[k]);
                }
            }

            IReadOnlyList<WordPart> parts = word.Parts;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part is VariablePart vp)
                {
                    var name = IndirectTarget(vp.Name) ?? vp.Name;
                    if (name == "@" || name == "*" && !vp.Quoted) { AddEach(Positional, vp.Quoted); continue; }
                    if (name is ['@' or '*', ':', .. var slice] && DefaultOp(name[1..]) == null)
                    {
                        // ${@:2} and ${@:2:3} slice the parameters, not the characters; ${@:0} starts at $0
                        var spec = slice.Split(':', 2);
                        List<string> all = ["bash", .. Positional];
                        long from = await EvalArithAsync(spec[0], io, ct);
                        if (from < 0) from += all.Count;
                        long count = spec.Length == 1 ? all.Count : await EvalArithAsync(spec[1], io, ct);
                        var items = from < 0 || from > all.Count ? [] : all.Skip((int)from).Take((int)Math.Clamp(count, 0, all.Count)).ToList();
                        if (vp.Quoted && name[0] == '*') AddText(string.Join(IfsJoiner, items), true); else AddEach(items, vp.Quoted);
                        continue;
                    }
                    if (ArrayRef(name) is { Sub: "@" or "*" } ar && (ar.Sub == "@" || !vp.Quoted) && await ArrayElementsAsync(name, io, ct, vp.Quoted) is { } elements)
                    {
                        AddEach(elements, vp.Quoted);
                        continue;
                    }
                    if (!vp.Quoted && name.AsSpan().IndexOfAny('-', '+') >= 0 && await AlternativeWordAsync(name, io, ct) is { } alternative)
                    {
                        parts = [.. parts.Take(i), .. alternative, .. parts.Skip(i + 1)];
                        i--;
                        continue;
                    }
                    if (name != vp.Name) part = vp with { Name = name };
                }
                var text = await ExpandPartAsync(part, io, ct);
                if (part is LiteralPart)
                {
                    if (i == 0 && !part.Quoted && TildePrefix(text) is var (home, length))
                    {
                        AddText(home, true); // the home path is never globbed
                        text = text[length..];
                        if (text.Length == 0) continue;
                    }
                    AddText(text, part.Quoted);
                    continue;
                }
                if (part.Quoted) AddText(text, true);
                else AddSplit(text);
            }
            End();

            foreach (var f in fields)
            {
                var matches = f.HasGlob && !Noglob ? Glob.Expand(this, f.Pattern.ToString()) : [];
                if (matches.Count > 0) result.AddRange(matches);
                else if (!(f.HasGlob && !Noglob && Shopts.Contains("nullglob"))) result.Add(f.Text.ToString());
            }
        }
        return result;
    }
}

/// <summary>Evaluates [[ ... ]].</summary>
internal sealed class CondEvaluator(Shell shell, List<Word> items, Shell.Io io, CancellationToken ct)
{
    static readonly HashSet<string> Unary = ["-z", "-n", "-e", "-f", "-d", "-s", "-r", "-w", "-x", "-L", "-h", "-a", "-v", "-o", "-t", "-b", "-c", "-p", "-S", "-g", "-u", "-k", "-O", "-G", "-N"];
    int _p;

    bool IsOp(string s) => _p < items.Count && items[_p].Is(s);

    public async Task<bool> EvaluateAsync()
    {
        if (items.Count == 0) throw new ShellException("syntax error in conditional expression", 2);
        var v = await Or();
        if (_p < items.Count) throw new ShellException($"syntax error in conditional expression near `{items[_p]}'", 2);
        return v;
    }

    async Task<bool> Or()
    {
        var l = await And();
        while (IsOp("||")) { _p++; var r = await And(); l = l || r; }
        return l;
    }

    async Task<bool> And()
    {
        var l = await Not();
        while (IsOp("&&")) { _p++; var r = await Not(); l = l && r; }
        return l;
    }

    async Task<bool> Not()
    {
        if (IsOp("!")) { _p++; return !await Not(); }
        return await Primary();
    }

    async Task<bool> Primary()
    {
        if (IsOp("("))
        {
            _p++;
            var v = await Or();
            if (!IsOp(")")) throw new ShellException("syntax error in conditional expression: expected `)'", 2);
            _p++;
            return v;
        }
        if (_p >= items.Count) throw new ShellException("syntax error in conditional expression: unexpected end", 2);
        if (items[_p].UnquotedLiteral is { } u && Unary.Contains(u) && _p + 1 < items.Count && !(_p + 2 < items.Count && Parser.IsCondBinary(items[_p + 1])))
        {
            var arg = await shell.ExpandSingleAsync(items[_p + 1], io, ct);
            _p += 2;
            return Test.Unary(shell, u == "-a" ? "-e" : u == "-h" ? "-L" : u, arg);
        }
        var left = items[_p++];
        if (_p < items.Count && Parser.IsCondBinary(items[_p]))
        {
            var op = items[_p++].UnquotedLiteral!;
            if (_p >= items.Count) throw new ShellException($"unexpected argument to conditional binary operator `{op}'", 2);
            var right = items[_p++];
            var l = await shell.ExpandSingleAsync(left, io, ct);
            switch (op)
            {
                case "==" or "=" or "!=":
                    bool match = Glob.ToRegex(await shell.PatternAsync(right, io, ct), shell.Shopts.Contains("nocasematch"), slashes: true).IsMatch(l);
                    return op == "!=" ? !match : match;
                case "=~":
                    // quoted parts of the regex match literally
                    var reText = new StringBuilder();
                    foreach (var part in right.Parts)
                    {
                        var t = await shell.ExpandPartAsync(part, io, ct);
                        reText.Append(part.Quoted ? Regex.Escape(t) : t);
                    }
                    var re = reText.ToString();
                    try
                    {
                        var m = Glob.Cached(Builtins.PosixToNet(re, extended: true), shell.Shopts.Contains("nocasematch") ? RegexOptions.IgnoreCase : RegexOptions.None, Shell.RegexTimeout).Match(l);
                        // BASH_REMATCH: the whole match, then each group; emptied when nothing matches
                        var groups = new ShellArray(false);
                        if (m.Success) for (int g = 0; g < m.Groups.Count; g++) groups.Set(g.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Groups[g].Value);
                        shell.Environment.Remove("BASH_REMATCH");
                        shell.Arrays["BASH_REMATCH"] = groups;
                        return m.Success;
                    }
                    catch (ArgumentException) { throw new ShellException($"invalid regular expression `{re}'", 2); }
                case "<": return string.CompareOrdinal(l, await shell.ExpandSingleAsync(right, io, ct)) < 0;
                case ">": return string.CompareOrdinal(l, await shell.ExpandSingleAsync(right, io, ct)) > 0;
                case "-nt": return Test.Newer(shell, l, await shell.ExpandSingleAsync(right, io, ct));
                case "-ot": return Test.Newer(shell, await shell.ExpandSingleAsync(right, io, ct), l);
                case "-ef": return shell.Fs.SameFile(shell.ResolvePath(l), shell.ResolvePath(await shell.ExpandSingleAsync(right, io, ct)));
                default:
                    var a = Arith.Eval(shell, l);
                    var b = Arith.Eval(shell, await shell.ExpandSingleAsync(right, io, ct));
                    return op switch { "-eq" => a == b, "-ne" => a != b, "-lt" => a < b, "-le" => a <= b, "-gt" => a > b, "-ge" => a >= b, _ => false };
            }
        }
        return (await shell.ExpandSingleAsync(left, io, ct)).Length > 0;
    }
}

/// <summary>Brace expansion: a{b,c}d, {1..10}, {01..05}, {a..e}, {0..10..2}; a brace may span an expansion: {$x,y}.</summary>
internal static class Brace
{
    const int MaxItems = 10_000;
    const char Placeholder = '';

    public static IEnumerable<Word> Expand(Word w)
    {
        if (!w.Parts.Any(p => p is LiteralPart { Quoted: false } l && l.Text.Contains('{'))) return [w];
        // everything but unquoted text becomes one placeholder character, so braces work on plain text
        var text = new StringBuilder();
        var opaque = new List<WordPart>();
        foreach (var p in w.Parts)
            if (p is LiteralPart { Quoted: false } l) text.Append(l.Text);
            else { text.Append((char)(Placeholder + opaque.Count)); opaque.Add(p); }
        if (opaque.Count > 0x1000) return [w];
        var results = ExpandText(text.ToString());
        return results.Count == 1 && results[0] == text.ToString() ? [w] : results.Select(t => Rebuild(t, opaque));
    }

    static Word Rebuild(string text, List<WordPart> opaque)
    {
        var parts = new List<WordPart>();
        var lit = new StringBuilder();
        foreach (var c in text)
        {
            if (c < Placeholder || c >= Placeholder + opaque.Count) { lit.Append(c); continue; }
            if (lit.Length > 0) { parts.Add(new LiteralPart(lit.ToString(), false)); lit.Clear(); }
            parts.Add(opaque[c - Placeholder]);
        }
        if (lit.Length > 0 || parts.Count == 0) parts.Add(new LiteralPart(lit.ToString(), false));
        return new Word(parts);
    }

    static List<string> ExpandText(string text)
    {
        for (int open = text.IndexOf('{'); open >= 0; open = text.IndexOf('{', open + 1))
        {
            int close = FindClose(text, open);
            if (close < 0) continue;
            var alts = Alternatives(text[(open + 1)..close]);
            if (alts == null) continue;
            var results = new List<string>();
            foreach (var alt in alts)
            {
                results.AddRange(ExpandText(text[..open] + alt + text[(close + 1)..]));
                if (results.Count > MaxItems) throw new ShellException("brace expansion: too many items");
            }
            return results;
        }
        return [text];
    }

    static int FindClose(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    static readonly Regex NumberRange = new(@"^(-?\d+)\.\.(-?\d+)(?:\.\.(-?\d+))?$");
    static readonly Regex LetterRange = new(@"^([a-zA-Z])\.\.([a-zA-Z])(?:\.\.(-?\d+))?$");

    static List<string>? Alternatives(string inner)
    {
        var m = NumberRange.Match(inner);
        if (m.Success)
        {
            long a = long.Parse(m.Groups[1].Value), b = long.Parse(m.Groups[2].Value);
            long step = m.Groups[3].Success ? Math.Abs(long.Parse(m.Groups[3].Value)) : 1;
            if (step == 0) step = 1;
            bool pad = m.Groups[1].Value.TrimStart('-').StartsWith('0') && m.Groups[1].Value.Length > 1
                       || m.Groups[2].Value.TrimStart('-').StartsWith('0') && m.Groups[2].Value.Length > 1;
            int width = Math.Max(m.Groups[1].Value.Length, m.Groups[2].Value.Length);
            if (Math.Abs(b - a) / step > MaxItems) throw new ShellException("brace expansion: too many items");
            var list = new List<string>();
            for (long v = a; a <= b ? v <= b : v >= b; v += a <= b ? step : -step)
                list.Add(pad ? v.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') : v.ToString(CultureInfo.InvariantCulture));
            return list;
        }
        m = LetterRange.Match(inner);
        if (m.Success)
        {
            char a = m.Groups[1].Value[0], b = m.Groups[2].Value[0];
            int step = m.Groups[3].Success ? Math.Max(1, Math.Abs(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))) : 1;
            var list = new List<string>();
            for (int c = a; a <= b ? c <= b : c >= b; c += a <= b ? step : -step) list.Add(((char)c).ToString());
            return list;
        }
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '{') depth++;
            else if (inner[i] == '}') depth--;
            else if (inner[i] == ',' && depth == 0) { parts.Add(inner[start..i]); start = i + 1; }
        }
        parts.Add(inner[start..]);
        return parts.Count < 2 ? null : parts;
    }
}

internal static class Glob
{
    public static bool HasWildcard(string s) => s.IndexOfAny(['*', '?', '[']) >= 0 || s.Contains("@(") || s.Contains("+(") || s.Contains("!(");

    public static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) { if (c is '*' or '?' or '[' or ']' or '\\' or '(' or ')' or '|' or '@' or '!' or '+') sb.Append('\\'); sb.Append(c); }
        return sb.ToString();
    }

    static bool HasUnescapedWildcard(string seg)
    {
        for (int i = 0; i < seg.Length; i++)
        {
            if (seg[i] == '\\') { i++; continue; }
            if (seg[i] is '*' or '?' or '[' || seg[i] is '@' or '+' or '!' && i + 1 < seg.Length && seg[i + 1] == '(') return true;
        }
        return false;
    }

    static string Unescape(string seg) => Regex.Replace(seg, @"\\(.)", "$1");

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, RegexOptions), Regex> Cache = new();

    /// <summary>A compiled-once regex: patterns repeat (a case in a loop), so building one per match would dominate.</summary>
    public static Regex Cached(string pattern, RegexOptions options, TimeSpan? timeout = null)
    {
        if (Cache.TryGetValue((pattern, options), out var re)) return re;
        if (Cache.Count >= 1024) Cache.Clear();
        return Cache[(pattern, options)] = new Regex(pattern, options, timeout ?? Shell.RegexTimeout);
    }

    /// <param name="slashes">True for case/[[ ]]/${x%pat} matching, where * also matches '/'.</param>
    public static Regex ToRegex(string pattern, bool ignoreCase = false, bool slashes = false) =>
        Cached("^" + ToRegexBody(pattern, slashes) + @"\z", (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Singleline);

    public static string ToRegexBody(string pattern, bool slashes = false)
    {
        var sb = new StringBuilder();
        int i = 0;
        Body(pattern, ref i, sb, slashes, inGroup: false);
        return sb.ToString();
    }

    /// <summary>Translates pattern from i, stopping at an unescaped | or ) when inside an extglob group.</summary>
    static void Body(string p, ref int i, StringBuilder sb, bool slashes, bool inGroup)
    {
        string any = slashes ? "." : "[^/]";
        for (; i < p.Length; i++)
        {
            char c = p[i];
            if (inGroup && c is '|' or ')') return;
            if (c is '@' or '!' or '*' or '+' or '?' && i + 1 < p.Length && p[i + 1] == '(' && GroupEnd(p, i + 1) >= 0)
            {
                // extglob: @(a|b) one of, *(..) any number, +(..) at least one, ?(..) optional, !(..) anything else
                i += 2;
                var alternatives = new List<string>();
                while (true)
                {
                    var alt = new StringBuilder();
                    Body(p, ref i, alt, slashes, inGroup: true);
                    alternatives.Add(alt.ToString());
                    if (i >= p.Length || p[i] == ')') break;
                    i++; // past '|'
                }
                var group = "(?:" + string.Join('|', alternatives) + ")";
                // shortcut: !(..) is exact only at the end of a pattern (rm !(*.txt)); make it a real complement if agents nest it
                sb.Append(c switch { '@' => group, '*' => group + "*", '+' => group + "+", '?' => group + "?", _ => $"(?:(?!{group}\\z){any}*)" });
                continue;
            }
            switch (c)
            {
                case '\\' when i + 1 < p.Length: sb.Append(Regex.Escape(p[++i].ToString())); break;
                case '*': sb.Append(any).Append('*'); break;
                case '?': sb.Append(any); break;
                case '[' when BracketEnd(p, i) is var close and > 0:
                    sb.Append(Bracket(p[(i + 1)..close]));
                    i = close;
                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
    }

    /// <summary>The ')' closing the extglob group that opens at i, or -1.</summary>
    static int GroupEnd(string p, int i)
    {
        int depth = 0;
        for (; i < p.Length; i++)
        {
            if (p[i] == '\\') { i++; continue; }
            if (p[i] == '(') depth++;
            else if (p[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>The ']' closing the bracket expression that opens at i (allowing []...] and [[:class:]]), or -1.</summary>
    static int BracketEnd(string p, int i)
    {
        int k = i + 1;
        if (k < p.Length && p[k] is '!' or '^') k++;
        if (k < p.Length && p[k] == ']') k++;
        for (; k < p.Length; k++)
        {
            if (p[k] == '[' && k + 1 < p.Length && p[k + 1] is ':' or '.' or '=')
            {
                int e = p.IndexOf(p[k + 1] + "]", k + 2, StringComparison.Ordinal);
                if (e < 0) return -1;
                k = e + 1;
                continue;
            }
            if (p[k] == '\\') { k++; continue; }
            if (p[k] == ']') return k;
        }
        return -1;
    }

    static string Bracket(string inner)
    {
        var sb = new StringBuilder("[");
        int k = 0;
        if (inner.Length > 0 && inner[0] is '!' or '^') { sb.Append('^'); k = 1; }
        for (; k < inner.Length; k++)
        {
            char c = inner[k];
            if (c == '[' && k + 1 < inner.Length && inner[k + 1] == ':' && inner.IndexOf(":]", k + 2, StringComparison.Ordinal) is var e and >= 0)
            {
                sb.Append(Builtins.PosixClasses.GetValueOrDefault(inner[(k + 2)..e], ""));
                k = e + 1;
                continue;
            }
            if (c == '\\' && k + 1 < inner.Length) c = inner[++k];
            if (c is '\\' or '[' or ']' or '^') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append(']').ToString();
    }

    /// <summary>Matches a glob pattern against the virtual filesystem; returns paths in the same form (relative/absolute) as the pattern.</summary>
    public static List<string> Expand(Shell shell, string pattern)
    {
        bool absolute = pattern.StartsWith('/');
        var current = new List<(string Abs, string Display)> { (absolute ? "/" : shell.Cwd, absolute ? "/" : "") };
        bool dotglob = shell.Shopts.Contains("dotglob"), globstar = shell.Shopts.Contains("globstar");
        var segs = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int k = 0; k < segs.Length; k++)
        {
            var seg = segs[k];
            var next = new List<(string, string)>();
            if (seg == "**" && globstar) // zero or more directories; as the last segment, every file and directory below
            {
                bool last = !pattern.EndsWith('/') && k == segs.Length - 1;
                var queue = new Queue<(string Abs, string Display)>(current);
                while (queue.Count > 0)
                {
                    var (abs, display) = queue.Dequeue();
                    bool isBase = current.Contains((abs, display));
                    if (!last) next.Add((abs, display));
                    else if (display.Length > 0 && isBase) next.Add((abs, Join(display, ""))); // bash lists the base itself as "dir/"
                    if (shell.Fs.IsDirectory(abs) && shell.Fs.CanRead(abs))
                    {
                        foreach (var name in shell.Fs.List(abs))
                        {
                            var child = VirtualFileSystem.Normalize(abs, name);
                            if (!dotglob && name.StartsWith('.')) continue;
                            if (shell.Fs.IsDirectory(child, follow: false)) queue.Enqueue((child, Join(display, name))); // like bash, ** does not follow links
                            else if (last) next.Add((child, Join(display, name)));
                        }
                        if (last && !isBase) next.Add((abs, display));
                    }
                }
                current = next;
                continue;
            }
            foreach (var (abs, display) in current)
            {
                if (!shell.Fs.IsDirectory(abs) || !shell.Fs.CanRead(abs)) continue;
                if (seg is "." or "..") { next.Add((VirtualFileSystem.Normalize(abs, seg), Join(display, seg))); continue; }
                if (HasUnescapedWildcard(seg))
                {
                    var re = ToRegex(seg, shell.Shopts.Contains("nocaseglob"));
                    foreach (var name in shell.Fs.List(abs))
                        if (re.IsMatch(name) && (!name.StartsWith('.') || seg.StartsWith('.') || dotglob))
                            next.Add((VirtualFileSystem.Normalize(abs, name), Join(display, name)));
                }
                else
                {
                    var name = Unescape(seg);
                    var child = VirtualFileSystem.Normalize(abs, name);
                    if (shell.Fs.Exists(child, follow: false)) next.Add((child, Join(display, name)));
                }
            }
            current = next;
        }
        if (pattern.EndsWith('/')) // `echo */` lists directories only
            return current.Where(c => c.Display.Length > 0 && shell.Fs.IsDirectory(c.Abs))
                .Select(c => c.Display == "/" ? "/" : c.Display + "/").Order(StringComparer.Ordinal).ToList();
        return current.Select(c => c.Display).Where(d => d.Length > 0).Order(StringComparer.Ordinal).ToList();
    }

    static string Join(string display, string name) => display switch { "" => name, "/" => "/" + name, _ => display + "/" + name };
}
