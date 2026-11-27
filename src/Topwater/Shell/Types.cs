// The types the shell and its commands share. See docs/architecture.md.

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
/// <summary>A bad substitution (other than a bad @ operator): bash drops the rest of the line it is on, with status 1.</summary>
internal sealed class LineAbort : ControlFlow;
/// <summary>An expansion error that ends a non-interactive bash (an arithmetic error in a subscript, a bad substitution).</summary>
internal class FatalError(string message, int code) : ShellException(message, code);
/// <summary>A safety limit was hit; ends the whole tool call so an outer loop or caller can't carry on.</summary>
internal sealed class LimitExceeded(string message) : ControlFlow { public override string Message { get; } = message; }

public sealed partial class Shell
{
    /// <param name="Fds">fds 3 and up redirected for one command (3>&amp;1); null when none are.</param>
    internal readonly record struct Io(StdinBuffer Stdin, TextWriter Out, TextWriter Err, Dictionary<int, object>? Fds = null);

    internal const string ExecutableStub = "\u007fELF\u0002\u0001\u0001\0\0\0\0\0\0\0\0\0";

    /// <summary>A new bash process (bash -c, bash file, ./script.sh): $0, the file it runs (if any), and its own options (bash -e).</summary>
    /// <param name="Login">bash -l: read /etc/profile and the first of ~/.bash_profile, ~/.bash_login and ~/.profile first.</param>
    internal sealed record NewProcess(string Arg0, string? File = null, Action? Setup = null, bool Login = false);
}
