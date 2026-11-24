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
}

public sealed record ShellResult(string Stdout, string Stderr, int ExitCode)
{
    /// <summary>Formats the result as the text returned to the model from the bash tool.</summary>
    public string ToToolOutput(int maxChars = 30_000)
    {
        // each stream is cut on its own, so a long stdout can't push the error message and exit code out of view
        static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + $"\n... [output truncated, {s.Length - max} more characters]\n";
        var text = Clip(Stdout, maxChars) + Clip(Stderr, Math.Max(1, maxChars / 6));
        if (ExitCode != 0) text += (text.Length == 0 || text.EndsWith('\n') ? "" : "\n") + $"[exit code {ExitCode}]";
        return text.Length == 0 ? "(no output)" : text;
    }
}

public delegate Task<int> CommandHandler(CommandContext context);

public sealed record RegisteredCommand(string Name, string Description, string? Usage, CommandHandler Handler);

/// <summary>A command's stdin. Shared by every command in a loop body so `while read` consumes it line by line.</summary>
internal sealed class StdinBuffer(string text)
{
    int _pos;
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

    internal Shell.Io Io => new(_stdin, Stdout, Stderr);
    internal bool LastLineTerminated => _stdin.LastTerminated;

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
    public long Next { get; private set; }
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
    internal readonly record struct Io(StdinBuffer Stdin, TextWriter Out, TextWriter Err);

    internal const string ExecutableStub = "\u007fELF\u0002\u0001\u0001\0\0\0\0\0\0\0\0\0";

    readonly Dictionary<string, RegisteredCommand> _commands = new(StringComparer.Ordinal);
    readonly List<string> _history = [];
    readonly Stack<Dictionary<string, string?>> _locals = new();
    readonly Stack<Dictionary<string, ShellArray?>> _localArrays = new();
    internal Dictionary<string, ShellArray> Arrays = new(StringComparer.Ordinal);
    int _epoch;
    string _cwd = "/";
    int _condDepth;

    public Shell(ShellOptions? options = null)
    {
        Options = options ?? new ShellOptions();
        Fs = Options.FileSystem ?? new VirtualFileSystem();
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

    static readonly DateTime InstalledAt = DateTime.Today.AddDays(-142).AddHours(9).AddMinutes(13);

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

    internal Dictionary<string, Command> Functions { get; private set; } = new(StringComparer.Ordinal);
    internal List<string> Positional { get; set; } = [];
    internal bool Errexit { get; set; }
    internal bool Pipefail { get; set; }
    internal bool Nounset { get; set; }
    /// <summary>shopt -s names: nullglob, dotglob, globstar.</summary>
    internal HashSet<string> Shopts { get; private set; } = [];
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

    public string ResolvePath(string path) => VirtualFileSystem.Normalize(_cwd, path);

    // ---------- registration ----------

    public Shell Register(string name, CommandHandler handler, string description = "", string? usage = null)
    {
        _commands[name] = new RegisteredCommand(name, description, usage, handler);
        _builtinNames?.Remove(name); // an app command replacing a built-in owns its own --help/--version
        bool shellOnly = _builtinNames is null && NoProgramFile.Contains(name);
        if (!shellOnly && !name.Contains('/') && name is not "." and not ".." and not ":" && Fs.IsDirectory("/usr/bin"))
        {
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
        return this;
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
        _history.Add(commandLine);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code;
        var previousUser = Fs.RunAs;
        _totalIterations = _scriptDepth = 0;
        Fs.RunAs = Options.User; // the agent's commands run as a regular user
        var io = new Io(new StdinBuffer(""), stdout, stderr);
        try
        {
            // shortcut: EXIT traps fire when the tool call ends (like `set -e`, they last one call), not at logout
            try { code = await ExecScriptAsync(Parser.Parse(commandLine), io, cancellationToken); }
            finally { await RunExitTrapAsync(io, cancellationToken); }
        }
        catch (ParseException e) { stderr.WriteLine($"bash: {e.Message}"); code = 2; }
        catch (ExitSignal e) { code = e.Code; }
        catch (ReturnSignal e) { code = e.Code; }
        catch (LoopControl) { code = 0; }
        catch (LimitExceeded e) { stderr.WriteLine($"bash: {e.Message}"); code = 1; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) // an interpreter bug must not take down the host's agent loop
        {
            stderr.WriteLine($"bash: internal error: {e.GetType().Name}: {e.Message}");
            code = 1;
        }
        finally
        {
            // shortcut: `set -e` / `set -o pipefail` last for one tool call, so a stray `set -e` can't poison the session.
            Errexit = Pipefail = Nounset = false;
            Fs.RunAs = previousUser;
            if (_procSubCount > 0 && Fs.IsDirectory("/dev/fd"))
                using (Fs.AsRoot())
                    foreach (var f in Fs.List("/dev/fd")) Fs.Delete("/dev/fd/" + f, recursive: true);
            _procSubCount = 0;
        }
        LastExitCode = code;
        return new ShellResult(stdout.ToString(), stderr.ToString(), code);
    }

    internal async Task<int> RunScriptTextAsync(string text, Io io, bool subshell, CancellationToken ct, List<string>? positional = null)
    {
        Script script;
        try { script = Parser.Parse(text); }
        catch (ParseException e) { io.Err.WriteLine($"bash: {e.Message}"); return 2; }
        if (++_scriptDepth > 100) throw new LimitExceeded("maximum script nesting level exceeded (100)");
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
            if (positional != null) Positional = positional;
            try
            {
                try { return await ExecScriptAsync(script, io, ct); }
                finally { await RunExitTrapAsync(io, ct); }
            }
            catch (ExitSignal e) { return e.Code; }
            finally { RestoreState(saved); }
        }
        finally { _scriptDepth--; }
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
        Dictionary<string, Command> Functions, List<string> Positional, bool Errexit, bool Pipefail, bool Nounset,
        HashSet<string> Shopts, Dictionary<string, string> Traps, Dictionary<string, ShellArray> Arrays);

    // arrays are shared with the saved state and copied on first write (see Writable), so a subshell or
    // pipeline stage doesn't copy every element just to start
    State SaveState()
    {
        _epoch++;
        return new(_cwd, new(Environment), new(Aliases), new(Functions), Positional, Errexit, Pipefail, Nounset, [.. Shopts], new(Traps), new(Arrays));
    }

    void RestoreState(State s)
    {
        _epoch++;
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
            code = await ExecPipelineAsync(item.First, io, ct);
            int lastRun = 0;
            bool negated = item.First.Negate;
            for (int k = 0; k < item.Rest.Count; k++)
            {
                var (op, next) = item.Rest[k];
                if ((op == "&&") != (code == 0)) continue;
                code = await ExecPipelineAsync(next, io, ct);
                lastRun = k + 1;
                negated = next.Negate;
            }
            // set -e: only the final command of an && / || list counts, and never inside if/while conditions
            if (Errexit && code != 0 && _condDepth == 0 && lastRun == item.Rest.Count && !negated) throw new ExitSignal(code);
        }
        return code;
    }

    async Task<int> ExecPipelineAsync(Pipeline p, Io io, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var input = io.Stdin;
        int code = 0, failed = 0;
        for (int k = 0; k < p.Commands.Count; k++)
        {
            bool last = k == p.Commands.Count - 1;
            // shortcut: pipeline stages run one after another with buffered text; stream if huge outputs or infinite producers matter.
            var output = last ? io.Out : new StringWriter();
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
            if (!last) input = new StdinBuffer(output.ToString()!);
        }
        if (Pipefail) code = failed;
        if (p.Negate) code = code == 0 ? 1 : 0;
        LastExitCode = code;
        return code;
    }

    sealed class FileSink(string path) : StringWriter
    {
        public string Path { get; } = path;
    }

    async Task<int> ExecCommandAsync(Command cmd, Io io, CancellationToken ct)
    {
        // a stack overflow can't be caught in .NET and would take the host process down
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new LimitExceeded("maximum nesting level exceeded");
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
            rio.Err.WriteLine($"bash: {e.Message}");
            code = cmd is CondCommand ? 2 : e.ExitCode;
        }
        finally
        {
            foreach (var sink in sinks)
            {
                try { Fs.AppendAllText(sink.Path, sink.ToString()); }
                catch (ShellException e) { io.Err.WriteLine($"bash: {e.Message}"); }
            }
        }
        return code;
    }

    int DefineFunction(FunctionDef d)
    {
        Functions[d.Name] = d.Body;
        return 0;
    }

    async Task<int> ExecSubshellAsync(Script body, Io io, CancellationToken ct)
    {
        var saved = SaveState();
        try { return await ExecScriptAsync(body, io, ct); }
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
        if (++n > Options.MaxLoopIterations || ++_totalIterations > Options.MaxLoopIterations * 10L)
            throw new LimitExceeded($"loop aborted after {Options.MaxLoopIterations} iterations");
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
            Environment[f.Var] = item;
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
        foreach (var (patterns, body) in c.Clauses)
            foreach (var pattern in patterns)
                if (Glob.ToRegex(await PatternAsync(pattern, io, ct), slashes: true).IsMatch(subject))
                    return await ExecScriptAsync(body, io, ct);
        return 0;
    }

    async Task<Io?> ApplyRedirectsAsync(List<Redirect> redirects, Io io, List<FileSink> sinks, CancellationToken ct)
    {
        foreach (var r in redirects)
        {
            try
            {
                switch (r.Op)
                {
                    case "<<":
                        io = io with { Stdin = new StdinBuffer(await ExpandSingleAsync(r.Here!.Body, io, ct)) };
                        break;
                    case "<<<":
                        io = io with { Stdin = new StdinBuffer(await ExpandSingleAsync(r.Target!, io, ct) + "\n") };
                        break;
                    case "<":
                        var src = await ExpandSingleAsync(r.Target!, io, ct);
                        io = io with { Stdin = new StdinBuffer(Fs.ReadAllText(ResolvePath(src))) };
                        break;
                    case ">&":
                        var fd = await ExpandSingleAsync(r.Target!, io, ct);
                        if (r.Fd == 2 && fd == "1") io = io with { Err = io.Out };
                        else if (r.Fd == 1 && fd == "2") io = io with { Out = io.Err };
                        break;
                    default: // > >> &> &>>
                        var target = await ExpandSingleAsync(r.Target!, io, ct);
                        TextWriter w;
                        var path = ResolvePath(target);
                        if (path == "/dev/null") w = TextWriter.Null;
                        else if (path is "/dev/stderr" or "/dev/stdout") w = path == "/dev/stderr" ? io.Err : io.Out;
                        else
                        {
                            if (Fs.IsDirectory(path)) throw new ShellException($"{target}: Is a directory");
                            if (!r.Op.EndsWith(">>") || !Fs.Exists(path)) Fs.WriteAllText(path, ""); // truncate on open, like bash
                            var sink = new FileSink(path);
                            sinks.Add(sink);
                            w = sink;
                        }
                        if (r.Op.StartsWith('&')) io = io with { Out = w, Err = w };
                        else if (r.Fd == 2) io = io with { Err = w };
                        else if (r.Fd <= 1) io = io with { Out = w };   // fd 3+ is opened (the file is created) but nothing writes to it
                        break;
                }
            }
            catch (ShellException e)
            {
                io.Err.WriteLine($"bash: {e.Message}");
                return null;
            }
        }
        return io;
    }

    async Task<int> ExecSimpleAsync(SimpleCommand c, Io io, CancellationToken ct)
    {
        var words = c.Words;
        if (words.Count > 1 && words[0].UnquotedLiteral is "local" or "declare" or "typeset" or "readonly" && await DeclareArraysAsync(words, io, ct) is { } rest)
        {
            if (rest.Count == 1) return 0;
            words = rest;
        }
        var argv = await ExpandWordsAsync(words, io, ct);
        var assigned = new List<(string, string)>();
        foreach (var a in c.Assignments)
        {
            if (a.Elements != null || a.Index != null) await AssignArrayAsync(a, io, ct);
            else if (words.Count > 0) assigned.Add((a.Name, await ExpandSingleAsync(a.Value, io, ct)));
            else
            {
                // with no command, assignments apply left to right: IFS=, parts=($csv) splits on the comma
                var v = await ExpandSingleAsync(a.Value, io, ct);
                if (Arrays.ContainsKey(a.Name)) SetElement(a.Name, "0", v); else Environment[a.Name] = v;   // a plain assignment to an array sets element 0
            }
        }

        if (argv.Count == 0)
        {
            foreach (var (k, v) in assigned) Environment[k] = v;
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

        if (assigned.Count == 0) return await InvokeAsync(argv, io, ct);

        // VAR=x cmd: the variable only exists for that command
        var saved = assigned.ToDictionary(a => a.Item1, a => Environment.TryGetValue(a.Item1, out var v) ? v : null);
        foreach (var (k, v) in assigned) Environment[k] = v;
        try { return await InvokeAsync(argv, io, ct); }
        finally
        {
            foreach (var (k, v) in saved)
                if (v == null) Environment.Remove(k); else Environment[k] = v;
        }
    }

    internal async Task<int> InvokeAsync(IReadOnlyList<string> argv, Io io, CancellationToken ct)
    {
        var name = argv[0];
        if (Functions.TryGetValue(name, out var fn)) return await CallFunctionAsync(fn, argv, io, ct);
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
        var ctx = new CommandContext(this, name, argv.Skip(1).ToList(), io.Stdin, io.Out, io.Err, ct);
        if (argv.Count == 2 && _builtinNames?.Contains(name) == true && Builtins.StandardOption(ctx, cmd, argv[1])) return 0;
        try
        {
            return await cmd.Handler(ctx);
        }
        catch (OperationCanceledException) { throw; }
        catch (ControlFlow) { throw; }
        catch (ShellException e)
        {
            io.Err.WriteLine($"{name}: {e.Message}");
            return e.ExitCode;
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
        if (Fs.IsDirectory(path)) { io.Err.WriteLine($"bash: {name}: Is a directory"); return 126; }
        if (!Fs.IsFile(path)) { io.Err.WriteLine($"bash: {name}: No such file or directory"); return 127; }
        if (!Fs.CanExecute(path) || !Fs.CanRead(path)) { io.Err.WriteLine($"bash: {name}: Permission denied"); return 126; }
        var text = Fs.ReadAllText(path);
        if (text.StartsWith(ExecutableStub[..4])) { io.Err.WriteLine($"bash: {name}: cannot execute binary file"); return 126; }
        if (text.StartsWith("#!"))
        {
            var interp = text[2..(text.IndexOf('\n') is var nl and >= 0 ? nl : text.Length)].Trim();
            if (!Regex.IsMatch(interp, @"(^|/|\s)(ba)?sh(\s|$)"))
            {
                io.Err.WriteLine($"bash: {name}: {interp.Split(' ')[0]}: bad interpreter: No such file or directory");
                return 126;
            }
        }
        return await RunScriptTextAsync(text, io, subshell: true, ct, argv.Skip(1).ToList());
    }

    async Task<int> CallFunctionAsync(Command body, IReadOnlyList<string> argv, Io io, CancellationToken ct)
    {
        if (_locals.Count >= 200) throw new LimitExceeded($"{argv[0]}: maximum function nesting level exceeded (200)");
        var savedPos = Positional;
        Positional = argv.Skip(1).ToList();
        _locals.Push(new Dictionary<string, string?>());
        _localArrays.Push(new Dictionary<string, ShellArray?>());
        try { return await ExecCommandAsync(body, io, ct); }
        catch (ReturnSignal r) { return r.Code; }
        finally
        {
            foreach (var (k, v) in _localArrays.Pop())
                if (v == null) Arrays.Remove(k); else Arrays[k] = v;
            foreach (var (k, v) in _locals.Pop())
                if (v == null) Environment.Remove(k); else Environment[k] = v;
            Positional = savedPos;
        }
    }

    internal bool InFunction => _locals.Count > 0;

    /// <summary>True for a built-in command (not one the app registered or replaced).</summary>
    internal bool IsBuiltin(string name) => _builtinNames?.Contains(name) == true;

    /// <summary>`local NAME[=VALUE]`: remembers the outer value so it is restored when the function returns.</summary>
    internal void DeclareLocal(string name, string? value)
    {
        if (_locals.Count == 0) throw new ShellException("can only be used in a function");
        _locals.Peek().TryAdd(name, Environment.TryGetValue(name, out var old) ? old : null);
        Environment[name] = value ?? "";
    }

    // ---------- arrays ----------

    /// <summary>The array to change: created if missing, copied first if a saved state still shares it.</summary>
    internal ShellArray Writable(string name, bool assoc = false)
    {
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
        if (local && _localArrays.Count > 0) _localArrays.Peek().TryAdd(name, Arrays.GetValueOrDefault(name));
        else if (Arrays.TryGetValue(name, out var old) && old.Assoc == assoc) return;
        if (local && _locals.Count > 0) DeclareLocal(name, null);
        Environment.Remove(name);
        Arrays[name] = new ShellArray(assoc) { Epoch = _epoch };
    }

    /// <summary>The storage key for a subscript: the text itself for an associative array, else its arithmetic value.</summary>
    internal string ArrayKey(ShellArray? array, string subscript)
    {
        if (array is { Assoc: true }) return subscript;
        long i = Arith.Eval(this, subscript);
        if (i < 0) i += array?.Next ?? 0;
        return i >= 0 ? i.ToString(CultureInfo.InvariantCulture) : throw new ShellException($"[{subscript}]: bad array subscript");
    }

    /// <summary>One element, or null if unset. A plain variable answers as element 0.</summary>
    internal string? GetElement(string name, string subscript)
    {
        if (Arrays.TryGetValue(name, out var a)) return a.Get(ArrayKey(a, subscript));
        return Environment.TryGetValue(name, out var scalar) && ArrayKey(null, subscript) == "0" ? scalar : null;
    }

    internal void SetElement(string name, string subscript, string value)
    {
        var a = Writable(name);
        a.Set(ArrayKey(a, subscript), value);
    }

    internal void Unset(string target)
    {
        if (ArrayRef(target) is { Keys: false, Tail: "" } r && Arrays.ContainsKey(r.Name) && r.Sub is not ("@" or "*"))
        {
            var a = Writable(r.Name);
            a.Items.Remove(ArrayKey(a, Unquote(r.Sub)));
            return;
        }
        var name = ArrayRef(target)?.Name ?? target;
        Environment.Remove(name);
        Arrays.Remove(name);
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
    async Task<List<string>?> ArrayElementsAsync(string expr, Io io, CancellationToken ct)
    {
        if (ArrayRef(expr) is not { Sub: "@" or "*" } r) return null;
        List<string> items = !Arrays.TryGetValue(r.Name, out var a) ? (Environment.TryGetValue(r.Name, out var scalar) ? [r.Keys ? "0" : scalar] : [])
            : r.Keys ? [.. a.Items.Keys] : [.. a.Items.Values];
        if (r.Keys || r.Tail.Length == 0) return items;
        if (r.Tail[0] == ':' && r.Tail.Length > 1 && r.Tail[1] is not ('-' or '=' or '+' or '?'))
        {
            var spec = r.Tail[1..].Split(':', 2);
            long from = Arith.Eval(this, spec[0]);
            from = Math.Clamp(from < 0 ? items.Count + from : from, 0, items.Count);
            long count = spec.Length == 1 ? items.Count - from : Math.Clamp(Arith.Eval(this, spec[1]), 0, items.Count - from);
            return items.GetRange((int)from, (int)count);
        }
        // any other operator applies to each element: ${files[@]%.txt}, ${names[@]^^}
        var result = new List<string>();
        foreach (var item in items) result.Add(await ElementOpAsync(item, r.Tail, io, ct));
        return result;
    }

    /// <summary>Applies a ${...} operator to one value by expanding it through a scratch variable.</summary>
    async Task<string> ElementOpAsync(string? value, string rest, Io io, CancellationToken ct)
    {
        if (rest.Length == 0) return value ?? "";
        const string scratch = "__topwater_element";
        if (value == null) Environment.Remove(scratch); else Environment[scratch] = value;
        try { return await ExpandParameterAsync(scratch + rest, io, ct); }
        finally { Environment.Remove(scratch); }
    }

    async Task<string> SubscriptAsync(string sub, Io io, CancellationToken ct) =>
        sub.IndexOfAny(['$', '`', '"', '\'']) < 0 ? sub : await ExpandSingleAsync(new Word(new Lexer(Unquote(sub)).LexQuoted(null)), io, ct);

    async Task AssignArrayAsync(Assignment a, Io io, CancellationToken ct)
    {
        if (a.Elements == null)
        {
            var key = await ExpandSingleAsync(a.Index!, io, ct);
            var value = await ExpandSingleAsync(a.Value, io, ct);
            SetElement(a.Name, key, a.Append ? GetElement(a.Name, key) + value : value);
            return;
        }
        ShellArray target;
        if (a.Append) target = Writable(a.Name);
        else
        {
            target = new ShellArray(Arrays.TryGetValue(a.Name, out var old) && old.Assoc) { Epoch = _epoch };
            Environment.Remove(a.Name);
        }
        foreach (var w in Parser.ParseWords(a.Elements))
        {
            // [key]=value sets that element; anything else is appended, after the usual splitting and globbing
            if (w.Parts is [LiteralPart { Quoted: false, Text: ['[', ..] }, ..] && Parser.SplitSubscript(w, 1) is var (index, element, _))
                target.Set(ArrayKey(target, await ExpandSingleAsync(index, io, ct)), await ExpandSingleAsync(element, io, ct));
            else
                foreach (var field in await ExpandWordsAsync([w], io, ct)) target.Set(target.Next.ToString(CultureInfo.InvariantCulture), field);
        }
        Arrays[a.Name] = target;
    }

    /// <summary>
    /// `local a=(x y)`, `declare -A m`, `declare -a list=()`: the array arguments are handled here, because a built-in
    /// only receives expanded text. Returns the words left for the built-in, or null if none were array declarations.
    /// </summary>
    async Task<List<Word>?> DeclareArraysAsync(List<Word> words, Io io, CancellationToken ct)
    {
        bool local = words[0].Is("local") || InFunction;
        var flags = string.Concat(words.Skip(1).Select(w => w.UnquotedLiteral).Where(t => t is ['-', ..]));
        bool assoc = flags.Contains('A'), any = false;
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
            any = true;
        }
        return any ? keep : null;
    }

    // ---------- expansion ----------

    internal string GetVariable(string name)
    {
        if (name.Length > 0 && char.IsDigit(name[0]) && int.TryParse(name, out var n))
            return n == 0 ? "bash" : n <= Positional.Count ? Positional[n - 1] : "";
        return name switch
        {
            "?" => LastExitCode.ToString(CultureInfo.InvariantCulture),
            "$" => "4242",
            "!" => "",
            "#" => Positional.Count.ToString(CultureInfo.InvariantCulture),
            "@" or "*" => string.Join(' ', Positional),
            "PWD" => _cwd,
            "RANDOM" => Random.Shared.Next(32768).ToString(CultureInfo.InvariantCulture),
            _ => Arrays.TryGetValue(name, out var array) ? array.Get("0") ?? "" : Environment.GetValueOrDefault(name, ""),
        };
    }

    bool IsSet(string name) =>
        name is "?" or "$" or "#" or "@" or "*" or "PWD" or "RANDOM" or "0"
        || (char.IsDigit(name[0]) && int.TryParse(name, out var n) && n <= Positional.Count)
        || Environment.ContainsKey(name) || Arrays.ContainsKey(name);

    internal async Task<string> ExpandPartAsync(WordPart part, Io io, CancellationToken ct) => part switch
    {
        LiteralPart l => l.Text,
        VariablePart v => await ExpandParameterAsync(v.Name, io, ct),
        CommandSubPart c => await CaptureAsync(c.Source, io, ct),
        ArithPart a => (await EvalArithAsync(a.Expr, io, ct)).ToString(CultureInfo.InvariantCulture),
        ProcSubPart p => await ProcessSubstitutionAsync(p.Source, io, ct),
        _ => "",
    };

    /// <summary>Expands $(cmd), `cmd` and ${...} inside an arithmetic expression, then evaluates it.</summary>
    async Task<long> EvalArithAsync(string expr, Io io, CancellationToken ct) =>
        Arith.Eval(this, expr.IndexOfAny(['$', '`']) < 0 ? expr : await ExpandSingleAsync(new Word(new Lexer(expr).LexQuoted(null)), io, ct));

    static readonly Regex ParamRe = new(@"^([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!])(.*)$", RegexOptions.Singleline);

    /// <summary>${name}, ${#name}, ${name:-x}, ${name%pat}, ${name/a/b}, ${name:1:2}, ${name^^} ...</summary>
    async Task<string> ExpandParameterAsync(string expr, Io io, CancellationToken ct)
    {
        if (expr.Length > 1 && expr[0] == '#' && ArrayRef(expr[1..]) is { Keys: false, Tail: "" } counted)
        {
            if (counted.Sub is "@" or "*")
                return (Arrays.TryGetValue(counted.Name, out var a) ? a.Items.Count : Environment.ContainsKey(counted.Name) ? 1 : 0).ToString(CultureInfo.InvariantCulture);
            return (GetElement(counted.Name, await SubscriptAsync(counted.Sub, io, ct)) ?? "").Length.ToString(CultureInfo.InvariantCulture);
        }
        // as one string, ${a[*]} joins with the first character of IFS and ${a[@]} with a space
        if (await ArrayElementsAsync(expr, io, ct) is { } all)
            return string.Join(ArrayRef(expr)!.Value.Sub == "*" && Environment.TryGetValue("IFS", out var ifs) ? ifs.Length > 0 ? ifs[..1] : "" : " ", all);
        if (ArrayRef(expr) is { Keys: false } one)
            return await ElementOpAsync(GetElement(one.Name, await SubscriptAsync(one.Sub, io, ct)), one.Tail, io, ct);
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
        if (!(rest.Length > 0 && rest[0] is '-' or '=' or '+' or '?' || rest.StartsWith(":-") || rest.StartsWith(":=") || rest.StartsWith(":+") || rest.StartsWith(":?")))
            CheckSet(name, io); // defaults handle unset variables
        if (rest.Length == 0) return value;

        async Task<string> Arg(string text)
        {
            if (text.Length >= 2 && text[0] is '"' or '\'' && text[^1] == text[0]) text = text[1..^1];
            return await ExpandSingleAsync(new Word(new Lexer(text).LexQuoted(null)), io, ct);
        }
        // pattern text is unquoted, so its wildcards stay active
        async Task<string> Pattern(string text) =>
            await PatternAsync(new Word(new Lexer(text).LexQuoted(null).Select(p => p is LiteralPart l ? l with { Quoted = false } : p).ToList()), io, ct);

        foreach (var op in new[] { ":-", ":=", ":+", ":?", "-", "=", "+", "?" })
        {
            if (!rest.StartsWith(op)) continue;
            bool missing = op[0] == ':' ? value.Length == 0 : !IsSet(name);
            var arg = rest[op.Length..];
            switch (op.TrimStart(':'))
            {
                case "-": return missing ? await Arg(arg) : value;
                case "=":
                    if (!missing) return value;
                    var v = await Arg(arg);
                    Environment[name] = v;
                    return v;
                case "+": return missing ? "" : await Arg(arg);
                default:
                    if (missing) throw new ShellException($"{name}: {(arg.Length > 0 ? await Arg(arg) : "parameter null or not set")}");
                    return value;
            }
        }
        if (rest.StartsWith('#') || rest.StartsWith('%'))
        {
            bool longest = rest.Length > 1 && rest[1] == rest[0];
            var re = Glob.ToRegex(await Pattern(rest[(longest ? 2 : 1)..]), slashes: true);
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
        if (rest.StartsWith('/'))
        {
            var body = rest[1..];
            char mode = body.Length > 0 && body[0] is '/' or '#' or '%' ? body[0] : ' ';
            if (mode != ' ') body = body[1..];
            int slash = -1;
            for (int i = 0; i < body.Length; i++) { if (body[i] == '\\') { i++; continue; } if (body[i] == '/') { slash = i; break; } }
            var pat = Glob.ToRegexBody(await Pattern(slash < 0 ? body : body[..slash]), slashes: true);
            var rep = slash < 0 ? "" : await Arg(body[(slash + 1)..]);
            // an empty pattern matches nothing, except at an anchor: ${x/#/pre-} and ${x/%/-post} add to the ends
            if (pat.Length == 0) return mode == '#' ? rep + value : mode == '%' ? value + rep : value;
            var re = new Regex(mode == '#' ? "^" + pat : mode == '%' ? pat + "$" : pat, RegexOptions.Singleline);
            return re.Replace(value, rep.Replace("$", "$$"), mode == '/' ? -1 : 1);
        }
        if (rest is "^^") return value.ToUpperInvariant();
        if (rest is ",,") return value.ToLowerInvariant();
        if (rest is "^") return value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
        if (rest is ",") return value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
        if (rest.StartsWith(':'))
        {
            var spec = rest[1..].Split(':', 2);
            var offset = Arith.Eval(this, spec[0]);
            if (offset < 0) offset = Math.Max(0, value.Length + offset);
            if (offset > value.Length) return "";
            var tail = value[(int)offset..];
            if (spec.Length == 1) return tail;
            var len = Arith.Eval(this, spec[1]);
            if (len < 0) len = Math.Max(0, tail.Length + len);
            return tail[..(int)Math.Min(len, tail.Length)];
        }
        throw new ShellException($"${{{expr}}}: bad substitution");
    }

    /// <summary>set -u: expanding an unset variable is an error that ends the (sub)shell, like bash.</summary>
    void CheckSet(string name, Io io)
    {
        if (!Nounset || name is "@" or "*" || IsSet(name)) return;
        io.Err.WriteLine($"bash: {name}: unbound variable");
        throw new ExitSignal(127);
    }

    /// <summary>&lt;(cmd): writes cmd's output to /dev/fd/N (removed after the tool call) and returns that path.</summary>
    async Task<string> ProcessSubstitutionAsync(string source, Io io, CancellationToken ct)
    {
        var output = new StringWriter();
        await RunScriptTextAsync(source, io with { Stdin = new StdinBuffer(""), Out = output }, subshell: true, ct);
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
                io.Err.WriteLine($"bash: {target}: {e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]}");
                LastExitCode = 1;
                return "";
            }
        }
        var output = new StringWriter();
        LastExitCode = await RunScriptTextAsync(source, io with { Stdin = new StdinBuffer(""), Out = output }, subshell: true, ct);
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

    /// <summary>Expands a word without field splitting or globbing (redirect targets, assignments, heredocs).</summary>
    internal async Task<string> ExpandSingleAsync(Word word, Io io, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < word.Parts.Count; i++)
        {
            var text = await ExpandPartAsync(word.Parts[i], io, ct);
            if (i == 0 && word.Parts[i] is LiteralPart { Quoted: false }) text = ExpandTilde(text);
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

    string ExpandTilde(string text) =>
        text == "~" || text.StartsWith("~/") ? Environment.GetValueOrDefault("HOME", "/") + text[1..] : text;

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
        var fields = new Regex(sep).Split(text, Math.Max(0, maxFields)).ToList();
        if (maxFields <= 0 && other.Length > 0 && fields.Count > 0 && fields[^1].Length == 0) fields.RemoveAt(fields.Count - 1); // "a,b," is two fields
        return fields;
    }

    sealed class Field
    {
        public readonly StringBuilder Text = new();
        public readonly StringBuilder Pattern = new();
        public bool HasGlob, HasContent;
    }

    internal async Task<List<string>> ExpandWordsAsync(IEnumerable<Word> words, Io io, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var word in words.SelectMany(Brace.Expand))
        {
            var fields = new List<Field>();
            var cur = new Field();
            void End() { if (cur.HasContent) fields.Add(cur); cur = new Field(); }
            void AddText(string s, bool quoted)
            {
                cur.Text.Append(s);
                cur.Pattern.Append(quoted ? Glob.Escape(s) : s);
                if (!quoted && Glob.HasWildcard(s)) cur.HasGlob = true;
                cur.HasContent = true;
            }

            for (int i = 0; i < word.Parts.Count; i++)
            {
                var part = word.Parts[i];
                if (part is VariablePart { Name: "@", Quoted: true })
                {
                    // "$@": one field per positional parameter
                    for (int k = 0; k < Positional.Count; k++)
                    {
                        if (k > 0) End();
                        AddText(Positional[k], true);
                    }
                    continue;
                }
                if (part is VariablePart { Quoted: true } ap && ArrayRef(ap.Name) is { Sub: "@" } && await ArrayElementsAsync(ap.Name, io, ct) is { } elements)
                {
                    // "${arr[@]}": one field per element, and none at all for an empty array
                    for (int k = 0; k < elements.Count; k++)
                    {
                        if (k > 0) End();
                        AddText(elements[k], true);
                    }
                    continue;
                }
                if (part is VariablePart { Name: ['@' or '*', ':', .. var slice] })
                {
                    // ${@:2} and ${@:2:3} slice the parameters, not the characters; ${@:0} starts at $0
                    var spec = slice.Split(':', 2);
                    List<string> all = ["bash", .. Positional];
                    int from = (int)Math.Clamp(Arith.Eval(this, spec[0]), 0, all.Count);
                    int count = spec.Length == 1 ? all.Count - from : (int)Math.Clamp(Arith.Eval(this, spec[1]), 0, all.Count - from);
                    for (int k = 0; k < count; k++)
                    {
                        if (k > 0) End();
                        AddText(all[from + k], part.Quoted);
                    }
                    continue;
                }
                var text = await ExpandPartAsync(part, io, ct);
                if (part is LiteralPart)
                {
                    if (i == 0 && !part.Quoted && (text == "~" || text.StartsWith("~/")))
                    {
                        AddText(Environment.GetValueOrDefault("HOME", "/"), true); // home path is never globbed
                        text = text[1..];
                        if (text.Length == 0) continue;
                    }
                    AddText(text, part.Quoted);
                    continue;
                }
                if (part.Quoted) { AddText(text, true); continue; }

                // unquoted $VAR / $(cmd) / $((...)): split into fields on $IFS
                var ifs = Environment.GetValueOrDefault("IFS", " \t\n");
                var pieces = SplitFields(text, ifs);
                if (pieces.Count == 0) { if (text.Length > 0 && ifs.Length > 0) End(); continue; }
                if (ifs.Contains(text[0])) End();
                for (int k = 0; k < pieces.Count; k++)
                {
                    if (k > 0) End();
                    AddText(pieces[k], false);
                }
                if (ifs.Contains(text[^1])) End();
            }
            End();

            foreach (var f in fields)
            {
                var matches = f.HasGlob ? Glob.Expand(this, f.Pattern.ToString()) : [];
                if (matches.Count > 0) result.AddRange(matches);
                else if (!(f.HasGlob && Shopts.Contains("nullglob"))) result.Add(f.Text.ToString());
            }
        }
        return result;
    }
}

/// <summary>Evaluates [[ ... ]].</summary>
internal sealed class CondEvaluator(Shell shell, List<Word> items, Shell.Io io, CancellationToken ct)
{
    static readonly HashSet<string> Unary = ["-z", "-n", "-e", "-f", "-d", "-s", "-r", "-w", "-x", "-L", "-h", "-a", "-v"];
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
            if (u == "-v") return shell.Environment.ContainsKey(arg);
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
                    bool match = Glob.ToRegex(await shell.PatternAsync(right, io, ct), slashes: true).IsMatch(l);
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
                        var m = Regex.Match(l, re, RegexOptions.None, Shell.RegexTimeout);
                        if (m.Success) shell.Environment["BASH_REMATCH"] = m.Value;
                        return m.Success;
                    }
                    catch (ArgumentException) { throw new ShellException($"invalid regular expression `{re}'", 2); }
                case "<": return string.CompareOrdinal(l, await shell.ExpandSingleAsync(right, io, ct)) < 0;
                case ">": return string.CompareOrdinal(l, await shell.ExpandSingleAsync(right, io, ct)) > 0;
                case "-nt": return Test.Newer(shell, l, await shell.ExpandSingleAsync(right, io, ct));
                case "-ot": return Test.Newer(shell, await shell.ExpandSingleAsync(right, io, ct), l);
                default:
                    var a = Arith.Eval(shell, l);
                    var b = Arith.Eval(shell, await shell.ExpandSingleAsync(right, io, ct));
                    return op switch { "-eq" => a == b, "-ne" => a != b, "-lt" => a < b, "-le" => a <= b, "-gt" => a > b, "-ge" => a >= b, _ => false };
            }
        }
        return (await shell.ExpandSingleAsync(left, io, ct)).Length > 0;
    }
}

/// <summary>Brace expansion: a{b,c}d, {1..10}, {01..05}, {a..e}, {0..10..2}.</summary>
internal static class Brace
{
    const int MaxItems = 10_000;

    public static IEnumerable<Word> Expand(Word w)
    {
        for (int pi = 0; pi < w.Parts.Count; pi++)
        {
            if (w.Parts[pi] is not LiteralPart { Quoted: false } lit) continue;
            var text = lit.Text;
            for (int open = text.IndexOf('{'); open >= 0; open = text.IndexOf('{', open + 1))
            {
                int close = FindClose(text, open);
                if (close < 0) continue;
                var alts = Alternatives(text[(open + 1)..close]);
                if (alts == null) continue;
                var results = new List<Word>();
                foreach (var alt in alts)
                {
                    var parts = w.Parts.ToList();
                    parts[pi] = new LiteralPart(text[..open] + alt + text[(close + 1)..], false);
                    results.AddRange(Expand(new Word(parts)));
                    if (results.Count > MaxItems) throw new ShellException("brace expansion: too many items");
                }
                return results;
            }
        }
        return [w];
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

    static List<string>? Alternatives(string inner)
    {
        var m = Regex.Match(inner, @"^(-?\d+)\.\.(-?\d+)(?:\.\.(-?\d+))?$");
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
        m = Regex.Match(inner, @"^([a-zA-Z])\.\.([a-zA-Z])$");
        if (m.Success)
        {
            char a = m.Groups[1].Value[0], b = m.Groups[2].Value[0];
            var list = new List<string>();
            for (int c = a; a <= b ? c <= b : c >= b; c += a <= b ? 1 : -1) list.Add(((char)c).ToString());
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
    public static bool HasWildcard(string s) => s.IndexOfAny(['*', '?', '[']) >= 0;

    public static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) { if (c is '*' or '?' or '[' or ']' or '\\') sb.Append('\\'); sb.Append(c); }
        return sb.ToString();
    }

    static bool HasUnescapedWildcard(string seg)
    {
        for (int i = 0; i < seg.Length; i++)
        {
            if (seg[i] == '\\') { i++; continue; }
            if (seg[i] is '*' or '?' or '[') return true;
        }
        return false;
    }

    static string Unescape(string seg) => Regex.Replace(seg, @"\\(.)", "$1");

    /// <param name="slashes">True for case/[[ ]]/${x%pat} matching, where * also matches '/'.</param>
    public static Regex ToRegex(string pattern, bool ignoreCase = false, bool slashes = false) =>
        new("^" + ToRegexBody(pattern, slashes) + @"\z", (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Singleline);

    public static string ToRegexBody(string pattern, bool slashes = false)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            switch (c)
            {
                case '\\' when i + 1 < pattern.Length: sb.Append(Regex.Escape(pattern[++i].ToString())); break;
                case '*': sb.Append(slashes ? ".*" : "[^/]*"); break;
                case '?': sb.Append(slashes ? "." : "[^/]"); break;
                case '[':
                    int close = i + 2 <= pattern.Length ? pattern.IndexOf(']', i + 2) : -1;
                    if (close < 0) { sb.Append(@"\["); break; }
                    var set = pattern[(i + 1)..close];
                    if (set.StartsWith('!')) set = "^" + set[1..];
                    sb.Append('[').Append(set.Replace("\\", "\\\\")).Append(']');
                    i = close;
                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.ToString();
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
                            if (shell.Fs.IsDirectory(child)) queue.Enqueue((child, Join(display, name)));
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
                    var re = ToRegex(seg);
                    foreach (var name in shell.Fs.List(abs))
                        if (re.IsMatch(name) && (!name.StartsWith('.') || seg.StartsWith('.') || dotglob))
                            next.Add((VirtualFileSystem.Normalize(abs, name), Join(display, name)));
                }
                else
                {
                    var name = Unescape(seg);
                    var child = VirtualFileSystem.Normalize(abs, name);
                    if (shell.Fs.Exists(child)) next.Add((child, Join(display, name)));
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
