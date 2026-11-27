using System.Text;
using System.Text.RegularExpressions;

namespace Topwater;

/// <summary>Small getopt-style parser shared by the built-ins.</summary>
internal sealed class Args
{
    public readonly HashSet<char> Flags = [];
    public readonly Dictionary<char, string> Values = [];
    public readonly List<string> Positional = [];

    public bool Has(char c) => Flags.Contains(c);
    public string? Value(char c) => Values.GetValueOrDefault(c);
    public readonly Dictionary<char, List<string>> Every = [];  // each value of a repeated option, in order
    public IReadOnlyList<string> All(char c) => Every.TryGetValue(c, out var l) ? l : [];

    void Put(char c, string v)
    {
        Values[c] = v;
        if (!Every.TryGetValue(c, out var l)) Every[c] = l = [];
        l.Add(v);
    }

    /// <param name="flags">Allowed boolean short flags, e.g. "rfi".</param>
    /// <param name="valued">Short options that take a value, e.g. "n".</param>
    /// <param name="longs">Long-option aliases, e.g. "recursive=r,ignore-case=i".</param>
    public static Args Parse(IReadOnlyList<string> args, string flags = "", string valued = "", string longs = "")
    {
        var map = longs.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1][0]);
        var r = new Args();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--") { r.Positional.AddRange(args.Skip(i + 1)); break; }
            if (a.StartsWith("--"))
            {
                var name = a[2..];
                string? inline = null;
                if (name.IndexOf('=') is var eq and > 0) { inline = name[(eq + 1)..]; name = name[..eq]; }
                if (!map.TryGetValue(name, out var c)) throw new UsageException(CommandSpec.Format(CommandSpec.Getopt.UnrecognizedOption, ("option", a)));
                if (valued.Contains(c)) r.Put(c, inline ?? (i + 1 < args.Count ? args[++i] : throw new UsageException(CommandSpec.Format(CommandSpec.Getopt.MissingLongArgument, ("option", name)))));
                else r.Flags.Add(c);
                continue;
            }
            if (a.Length < 2 || a[0] != '-') { r.Positional.Add(a); continue; }
            for (int k = 1; k < a.Length; k++)
            {
                char c = a[k];
                if (valued.Contains(c))
                {
                    r.Put(c, k + 1 < a.Length ? a[(k + 1)..] : i + 1 < args.Count ? args[++i] : throw new UsageException(CommandSpec.Format(CommandSpec.Getopt.MissingShortArgument, ("option", c.ToString()))));
                    break;
                }
                if (!flags.Contains(c)) throw new UsageException(CommandSpec.Format(CommandSpec.Getopt.InvalidOption, ("option", c.ToString())));
                r.Flags.Add(c);
            }
        }
        return r;
    }

    /// <summary>Parses with the command's option table from spec/.</summary>
    public static Args Parse(IReadOnlyList<string> args, CommandSpec.Entry spec)
    {
        var (flags, valued, longs) = spec.Options ?? throw new InvalidOperationException($"{spec.Name} parses its own arguments");
        return Parse(args, flags, valued, longs);
    }

    /// <summary>Rewrites "-5" into "-n 5" (head/tail/xargs style).</summary>
    public static List<string> NumericShorthand(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        foreach (var a in args)
        {
            bool isValue = result.Count > 0 && result[^1] is "-n" or "-c" or "--lines" or "--bytes";
            if (a.Length > 1 && a[0] == '-' && a.Skip(1).All(char.IsDigit) && !isValue) result.AddRange(["-n", a[1..]]);
            else result.Add(a);
        }
        return result;
    }
}

internal static partial class Builtins
{
    public static void RegisterAll(Shell shell)
    {
        RegisterScriptingCommands(shell);
        RegisterVariableCommands(shell);
        RegisterProcessCommands(shell);
        RegisterSystemCommands(shell);
        RegisterPathCommands(shell);
        RegisterFileCommands(shell);
        RegisterTextCommands(shell);
        Awk.Register(shell);
        Jq.Register(shell);
        Bc.Register(shell);
        RegisterBinaryCommands(shell);
        RegisterArchiveCommands(shell);
        RegisterDocumentCommands(shell);
        RegisterWebCommands(shell);
        Git.Register(shell);
        RegisterMountCommands(shell);
    }

    /// <summary>`tool --version` / `tool --help` for built-ins, so probing them looks like a real system.</summary>
    internal static bool StandardOption(CommandContext ctx, RegisteredCommand cmd, string arg)
    {
        if (arg is not ("--version" or "--help") || !CommandSpec.Commands.TryGetValue(cmd.Name, out var spec) || !spec.StandardOptions) return false;
        if (arg == "--version") ctx.Stdout.WriteLine(spec.Version);
        else ctx.Stdout.Write(cmd.Usage ?? $"Usage: {cmd.Name} [OPTION]... [ARG]...\n{cmd.Description}.\n");
        return true;
    }

    internal static List<string> SplitLines(string text)
    {
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    internal static void WriteLines(TextWriter w, IEnumerable<string> lines) { foreach (var l in lines) w.Write(l + "\n"); }

    /// <summary>Reads each file argument (or stdin when there are none / "-"), reporting missing files to stderr.</summary>
    internal static IEnumerable<(string Name, string Text)> Inputs(CommandContext ctx, IReadOnlyList<string> files, Action onError, bool quiet = false) =>
        Inputs(ctx, files, onError, ctx.Fs.ReadPipeText, () => ctx.StdinText, quiet);

    /// <summary>Like <see cref="Inputs(CommandContext, IReadOnlyList{string}, Action, bool)"/>, but the exact bytes (stdin's too: see <see cref="ByteText"/>).</summary>
    internal static IEnumerable<(string Name, byte[] Text)> InputBytes(CommandContext ctx, IReadOnlyList<string> files, Action onError) =>
        Inputs(ctx, files, onError, ctx.Fs.ReadAllBytes, () => ByteText.Encode(ctx.StdinText), false);

    static IEnumerable<(string Name, T Text)> Inputs<T>(CommandContext ctx, IReadOnlyList<string> files, Action onError, Func<string, T> read, Func<T> stdin, bool quiet)
    {
        if (files.Count == 0) { yield return ("(standard input)", stdin()); yield break; }
        foreach (var f in files)
        {
            if (f == "-") { yield return ("(standard input)", stdin()); continue; }
            T text;
            try { text = read(ctx.ResolvePath(f)); }
            catch (ShellException e)
            {
                if (!quiet) ctx.Stderr.WriteLine($"{ctx.Name}: {CannotRead(ctx.Name, f, e.Message[(e.Message.LastIndexOf(':') + 2)..])}");
                onError();
                continue;
            }
            yield return (f, text);
        }
    }

    /// <summary>A bad command line the way GNU tools report one: the problem, then a pointer to --help.</summary>
    internal static string UsageError(string cmd, string message)
    {
        var (shown, tryHelp) = CommandSpec.Commands.TryGetValue(cmd, out var spec) ? (spec.ReportsAs, spec.TryHelp) : (cmd, $"Try '{cmd} --help' for more information.\n");
        return (message.Length > 0 ? $"{shown}: {message}\n" : "") + tryHelp;
    }

    /// <summary>The message after "NAME: " when an input file can't be read, worded the way each GNU tool words it.</summary>
    internal static string CannotRead(string cmd, string f, string why) =>
        CommandSpec.Message(cmd, why == "Is a directory" && CommandSpec.Of(cmd).Messages.ContainsKey("readDirectory") ? "readDirectory" : "openFailed", ("file", f), ("errno", why));

    /// <summary>A file name in a GNU message: as is when plain, else shell-quoted like <see cref="QuoteAlways"/>.</summary>
    internal static string Quote(string s) =>
        s.Length > 0 && s[0] is not ('~' or '#') && !s.Any(c => c < 32 || c == 127 || " !\"$&'()*;<>?[\\^`|=:".Contains(c)) ? s : QuoteAlways(s);

    /// <summary>A file name shell-quoted even when plain: 'nope', "it's", 'a'$'\t''b'.</summary>
    internal static string QuoteAlways(string s)
    {
        if (s.Any(c => c < 32 || c == 127))
        {
            // each control character becomes its own $'\t' piece between quoted runs
            var sb = new StringBuilder();
            foreach (var run in Regex.Split(s, @"([\x00-\x1f\x7f])"))
                if (run.Length == 1 && (run[0] < 32 || run[0] == 127))
                    sb.Append("$'\\").Append(run[0] switch { '\t' => "t", '\n' => "n", '\r' => "r", _ => Convert.ToString(run[0], 8).PadLeft(3, '0') }).Append('\'');
                else if (run.Length > 0) sb.Append(QuoteAlways(run));
            return sb.ToString();
        }
        return s.Contains('\'') && !s.Any(c => c is '"' or '$' or '`' or '\\' or '!') ? $"\"{s}\"" : "'" + s.Replace("'", "'\\''") + "'";
    }

    /// <summary>A value quoted the way GNU tools quote one in a UTF-8 locale: ‘x’.</summary>
    internal static string QuoteL(string s) => $"‘{s}’";

    /// <summary>Why a program-running command (env, timeout) can't exec <paramref name="cmd"/>, or null: it runs programs only, so
    /// a name must be a file on PATH (with no PATH, /bin and /usr/bin, as execvp does). <paramref name="path"/> is where it was found.</summary>
    internal static string? ExecFailure(CommandContext ctx, string cmd, out string? path)
    {
        var sh = ctx.Shell;
        path = cmd.Contains('/') ? cmd
            : sh.Environment.ContainsKey("PATH") ? sh.SearchPath(cmd)
            : new[] { "/bin/", "/usr/bin/" }.Select(d => d + cmd).FirstOrDefault(p => ctx.Fs.IsFile(p));
        if (path == null) return sh.IsAppOnly(cmd) ? null : CommandSpec.Errno.ENOENT;
        var abs = ctx.ResolvePath(path);
        return !ctx.Fs.Exists(abs) ? ctx.Fs.WhyMissing(abs) : !ctx.Fs.IsFile(abs) || !ctx.Fs.CanExecute(abs) ? CommandSpec.Errno.EACCES : null;
    }
}
