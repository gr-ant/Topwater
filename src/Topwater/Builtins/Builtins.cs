using System.Text;

namespace Topwater;

/// <summary>Small getopt-style parser shared by the built-ins.</summary>
internal sealed class Args
{
    public readonly HashSet<char> Flags = [];
    public readonly Dictionary<char, string> Values = [];
    public readonly List<string> Positional = [];

    public bool Has(char c) => Flags.Contains(c);
    public string? Value(char c) => Values.GetValueOrDefault(c);

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
                if (!map.TryGetValue(name, out var c)) throw new ShellException($"unrecognized option '{a}'", 2);
                if (valued.Contains(c)) r.Values[c] = inline ?? (i + 1 < args.Count ? args[++i] : throw new ShellException($"option '--{name}' requires an argument", 2));
                else r.Flags.Add(c);
                continue;
            }
            if (a.Length < 2 || a[0] != '-') { r.Positional.Add(a); continue; }
            for (int k = 1; k < a.Length; k++)
            {
                char c = a[k];
                if (valued.Contains(c))
                {
                    r.Values[c] = k + 1 < a.Length ? a[(k + 1)..] : i + 1 < args.Count ? args[++i] : throw new ShellException($"option requires an argument -- '{c}'", 2);
                    break;
                }
                if (!flags.Contains(c)) throw new ShellException($"invalid option -- '{c}'", 2);
                r.Flags.Add(c);
            }
        }
        return r;
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
        RegisterFileCommands(shell);
        RegisterTextCommands(shell);
        RegisterShellCommands(shell);
        RegisterSystemCommands(shell);
        RegisterScriptingCommands(shell);
        RegisterUtilityCommands(shell);
        Awk.Register(shell);
        Jq.Register(shell);
        RegisterDocumentCommands(shell);
        RegisterBinaryCommands(shell);
    }

    static readonly Dictionary<string, string> Versions = new()
    {
        ["grep"] = "grep (GNU grep) 3.11", ["sed"] = "sed (GNU sed) 4.9", ["find"] = "find (GNU findutils) 4.9.0",
        ["xargs"] = "xargs (GNU findutils) 4.9.0", ["diff"] = "diff (GNU diffutils) 3.10", ["jq"] = "jq-1.7.1", ["tree"] = "tree v2.1.1",
        ["bash"] = "GNU bash, version 5.2.21(1)-release (x86_64-pc-linux-gnu)", ["sh"] = "GNU bash, version 5.2.21(1)-release (x86_64-pc-linux-gnu)",
        ["file"] = "file-5.45", ["sudo"] = "Sudo version 1.9.15p5", ["less"] = "less 590 (GNU regular expressions)", ["ps"] = "ps from procps-ng 4.0.4",
        ["free"] = "free from procps-ng 4.0.4", ["uptime"] = "uptime from procps-ng 4.0.4", ["column"] = "column from util-linux 2.39.3",
        ["rev"] = "rev from util-linux 2.39.3", ["more"] = "more from util-linux 2.39.3",
    };

    static readonly HashSet<string> NoStandardOptions = ["sudo", "su", "echo", "printf", "test", "[", "true", "false", ":", "awk", "help", "exit", "return", "break", "continue", "set", "eval", "command", "type", "alias", "export", "local", "declare", "read", "cd"];

    /// <summary>`tool --version` / `tool --help` for built-ins, so probing them looks like a real system.</summary>
    internal static bool StandardOption(CommandContext ctx, RegisteredCommand cmd, string arg)
    {
        if (arg is not ("--version" or "--help") || NoStandardOptions.Contains(cmd.Name)) return false;
        if (arg == "--version") ctx.Stdout.WriteLine(Versions.GetValueOrDefault(cmd.Name, $"{cmd.Name} (GNU coreutils) 9.4"));
        else ctx.Stdout.WriteLine($"Usage: {cmd.Name} [OPTION]... [ARG]...\n{cmd.Description}.");
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
        Inputs(ctx, files, onError, ctx.Fs.ReadAllText, () => ctx.StdinText, quiet);

    /// <summary>Like <see cref="Inputs(CommandContext, IReadOnlyList{string}, Action, bool)"/>, but the exact bytes (stdin as UTF-8).</summary>
    internal static IEnumerable<(string Name, byte[] Text)> InputBytes(CommandContext ctx, IReadOnlyList<string> files, Action onError) =>
        Inputs(ctx, files, onError, ctx.Fs.ReadAllBytes, () => Encoding.UTF8.GetBytes(ctx.StdinText), false);

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
                if (!quiet) ctx.Stderr.WriteLine($"{ctx.Name}: {f}: {e.Message[(e.Message.LastIndexOf(':') + 2)..]}");
                onError();
                continue;
            }
            yield return (f, text);
        }
    }

    /// <summary>Backslash escapes: \n \t ..., \xHH, and octal (\NNN, or \0NNN for echo -e).</summary>
    internal static string Unescape(string s, bool keepUnknown = false, bool echo = false)
    {
        var sb = new StringBuilder();
        static bool IsOct(char c) => c is >= '0' and <= '7';
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char n = s[i + 1];
            if (IsOct(n) && (!echo || n == '0'))
            {
                int j = echo ? i + 2 : i + 1, k = j;
                while (k < s.Length && k < j + 3 && IsOct(s[k])) k++;
                sb.Append((char)((k > j ? Convert.ToInt32(s[j..k], 8) : 0) & 0xFF));
                i = k - 1;
            }
            else if (n == 'x' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 2]))
            {
                int k = i + 2;
                while (k < s.Length && k < i + 4 && Uri.IsHexDigit(s[k])) k++;
                sb.Append((char)Convert.ToInt32(s[(i + 2)..k], 16));
                i = k - 1;
            }
            else
            {
                i++;
                var known = n switch
                {
                    'n' => "\n", 't' => "\t", 'r' => "\r", '\\' => "\\", 'a' => "\a", 'b' => "\b", 'f' => "\f", 'v' => "\v",
                    'e' => "\u001b", '"' => "\"", _ => null,
                };
                sb.Append(known ?? (keepUnknown ? "\\" + n : n.ToString()));
            }
        }
        return sb.ToString();
    }

    /// <summary>Commands that are part of bash itself rather than programs in /usr/bin (for `type` and `command -v`).</summary>
    static readonly HashSet<string> ShellBuiltins =
    [
        "cd", "echo", "printf", "read", "export", "unset", "alias", "unalias", "set", "shift", "local", "declare", "typeset",
        "eval", "exit", "return", "break", "continue", "source", ".", "test", "[", "true", "false", "pwd", "type", "command",
        "let", "history", "help", "exec", "wait", "trap", "umask", "readonly", "getopts", "hash", "jobs", "kill", "shopt", ":",
    ];

    internal static bool IsShellBuiltin(Shell shell, string name) => ShellBuiltins.Contains(name) && shell.IsBuiltin(name);

    static void RegisterShellCommands(Shell shell)
    {
        shell.Register("true", _ => 0, "Do nothing, successfully");
        shell.Register(":", _ => 0, "Do nothing, successfully (the null command)");
        shell.Register("false", _ => 1, "Do nothing, unsuccessfully");

        shell.Register("echo", ctx =>
        {
            var args = ctx.Args.ToList();
            bool newline = true, escapes = false;
            while (args.Count > 0 && args[0].Length > 1 && args[0][0] == '-' && args[0].Skip(1).All(c => c is 'n' or 'e' or 'E'))
            {
                if (args[0].Contains('n')) newline = false;
                if (args[0].Contains('e')) escapes = true;
                if (args[0].Contains('E')) escapes = false;
                args.RemoveAt(0);
            }
            var text = string.Join(' ', args);
            if (escapes && text.IndexOf("\\c", StringComparison.Ordinal) is var stop and >= 0) { text = text[..stop]; newline = false; } // \c: print nothing more
            ctx.Stdout.Write(escapes ? Unescape(text, keepUnknown: true, echo: true) : text);
            if (newline) ctx.Stdout.Write('\n');
            return 0;
        }, "Print arguments");

        shell.Register("printf", ctx =>
        {
            var args = ctx.Args.ToList();
            string? variable = null;
            if (args.Count >= 2 && args[0] == "-v") { variable = args[1]; args.RemoveRange(0, 2); }
            if (args.Count > 0 && args[0] == "--") args.RemoveAt(0);
            if (args.Count == 0) throw new ShellException("usage: printf [-v var] format [arguments]", 2);
            var errors = new List<string>();
            var text = Printf.Format(args[0], args.Skip(1).ToList(), errors);
            if (variable == null) ctx.Stdout.Write(text);
            else ctx.Shell.SetVar(variable, text);
            foreach (var e in errors) ctx.Stderr.WriteLine(e);
            return errors.Count > 0 ? 1 : 0;
        }, "Format and print data");

        shell.Register("cd", ctx =>
        {
            var args = ctx.Args.SkipWhile(x => x is "-L" or "-P" or "-e" or "-@").ToList();
            if (args.Count > 0 && args[0] == "--") args.RemoveAt(0);
            if (args.Count > 1) throw new ShellException("too many arguments");
            var target = args.Count == 0 ? ctx.Shell.Environment.GetValueOrDefault("HOME", "/")
                : args[0] == "-" ? ctx.Shell.Environment.GetValueOrDefault("OLDPWD", ctx.Shell.Cwd) : args[0];
            var abs = ctx.ResolvePath(target);
            if (!ctx.Fs.Exists(abs)) throw new ShellException($"{target}: {ctx.Fs.WhyMissing(abs)}");
            if (!ctx.Fs.IsDirectory(abs)) throw new ShellException($"{target}: Not a directory");
            if (!ctx.Fs.CanExecute(abs)) throw new ShellException($"{target}: Permission denied");
            ctx.Shell.Environment["OLDPWD"] = ctx.Shell.Cwd;
            ctx.Shell.Cwd = abs;
            if (args is ["-"]) ctx.Stdout.WriteLine(abs);
            return 0;
        }, "Change the working directory");

        shell.Register("pwd", ctx => { ctx.Stdout.WriteLine(ctx.Shell.Cwd); return 0; }, "Print the working directory");

        shell.Register("export", ctx =>
        {
            var sh = ctx.Shell;
            var flags = string.Concat(ctx.Args.TakeWhile(a => a.StartsWith('-')));
            bool remove = flags.Contains('n');
            var args = ctx.Args.SkipWhile(a => a.StartsWith('-')).ToList();
            if (flags.Contains('f'))
            {
                // export -f: functions a child bash also gets
                foreach (var f in args) if (remove) sh.ExportedFunctions.Remove(f); else if (sh.Functions.ContainsKey(f)) sh.ExportedFunctions.Add(f);
                return 0;
            }
            if (args.Count == 0)
            {
                foreach (var k in sh.Environment.Keys.Concat(sh.Arrays.Keys).Distinct().Order(StringComparer.Ordinal))
                    if (sh.IsExported(k) && DeclareLine(sh, k) is { } line) ctx.Stdout.WriteLine(line);
                return 0;
            }
            int code = 0;
            foreach (var a in args)
            {
                int eq = a.IndexOf('=');
                bool append = eq > 0 && a[eq - 1] == '+';
                var name = eq > 0 ? a[..(append ? eq - 1 : eq)] : a;
                try
                {
                    if (!Topwater.Parsing.Parser.IsName(name)) throw new ShellException($"`{a}': not a valid identifier");
                    if (eq > 0) sh.SetVar(name, a[(eq + 1)..], append);
                    else if (!sh.Arrays.ContainsKey(sh.Resolve(name)) && !sh.Environment.ContainsKey(sh.Resolve(name))) { sh.MarkShellVariable(sh.Resolve(name)); sh.Environment[sh.Resolve(name)] = ""; }
                    if (remove) sh.Unexport(sh.Resolve(name)); else sh.SetAttrs(sh.Resolve(name), "x");
                }
                catch (ShellException e) { ctx.Stderr.WriteLine($"bash: export: {e.Message}"); code = 1; }
            }
            return code;
        }, "Set environment variables");

        shell.Register("unset", async ctx =>
        {
            var flags = string.Concat(ctx.Args.TakeWhile(a => a.StartsWith('-')));
            int code = 0;
            foreach (var arg in ctx.Args.SkipWhile(a => a.StartsWith('-')))
            {
                try
                {
                    var a = await ctx.Shell.ExpandSubscriptAsync(arg, ctx.Io, ctx.CancellationToken);
                    // with neither -v nor -f, a name that is no variable but a function unsets the function
                    if (flags.Contains('f') || !flags.Contains('v') && !ctx.Shell.IsVariableSet(a) && ctx.Shell.Functions.ContainsKey(a)) ctx.Shell.Functions.Remove(a);
                    else ctx.Shell.Unset(a, nameref: flags.Contains('n'));
                }
                catch (ShellException e) { ctx.Stderr.WriteLine($"bash: unset: {e.Message}"); code = 1; }
            }
            return code;
        }, "Remove variables or functions (-f)");

        shell.Register("env", ctx =>
        {
            foreach (var (k, v) in ctx.Shell.Environment.OrderBy(e => e.Key, StringComparer.Ordinal)) ctx.Stdout.WriteLine($"{k}={v}");
            return 0;
        }, "Print the environment");

        shell.Register("alias", ctx =>
        {
            int code = 0;
            if (ctx.Args.Count == 0)
                foreach (var (k, v) in ctx.Shell.Aliases.OrderBy(e => e.Key, StringComparer.Ordinal)) ctx.Stdout.WriteLine($"alias {k}='{v}'");
            foreach (var a in ctx.Args)
            {
                int eq = a.IndexOf('=');
                if (eq > 0) ctx.Shell.Aliases[a[..eq]] = a[(eq + 1)..];
                else if (ctx.Shell.Aliases.TryGetValue(a, out var v)) ctx.Stdout.WriteLine($"alias {a}='{v}'");
                else { ctx.Stderr.WriteLine($"alias: {a}: not found"); code = 1; }
            }
            return code;
        }, "Define or list aliases");

        shell.Register("unalias", ctx => { foreach (var a in ctx.Args) ctx.Shell.Aliases.Remove(a); return 0; }, "Remove aliases");

        shell.Register("history", ctx =>
        {
            var h = ctx.Shell.History;
            for (int i = 0; i < h.Count; i++) ctx.Stdout.WriteLine($"{i + 1,5}  {h[i]}");
            return 0;
        }, "Show command history");

        shell.Register("which", ctx =>
        {
            int code = 0;
            foreach (var a in ctx.Args.Where(a => !a.StartsWith('-')))
                if (ctx.Shell.Commands.ContainsKey(a)) ctx.Stdout.WriteLine($"/usr/bin/{a}");
                else code = 1;
            return code;
        }, "Locate a command");

        shell.Register("type", ctx =>
        {
            int code = 0;
            var opts = Args.Parse(ctx.Args, "atpPf");
            foreach (var a in opts.Positional)
            {
                if (opts.Has('t') || opts.Has('p') || opts.Has('P'))
                {
                    var kind = ctx.Shell.Aliases.ContainsKey(a) ? "alias" : ShellKeywords.Contains(a) ? "keyword" : ctx.Shell.Functions.ContainsKey(a) ? "function"
                        : IsShellBuiltin(ctx.Shell, a) ? "builtin" : ctx.Shell.Commands.ContainsKey(a) ? "file" : null;
                    if (kind == null) { code = 1; continue; }
                    if (opts.Has('t')) ctx.Stdout.WriteLine(kind);
                    else if (kind == "file" || (opts.Has('P') && ctx.Shell.Commands.ContainsKey(a))) ctx.Stdout.WriteLine($"/usr/bin/{a}");
                    continue;
                }
                if (!Describe(ctx, a, opts.Has('a'))) code = 1;
            }
            return code;
        }, "Describe a command");

        shell.Register("test", ctx => Test.Evaluate(ctx, ctx.Args.ToList()) ? 0 : 1, "Evaluate a conditional expression");
        shell.Register("[", ctx =>
        {
            if (ctx.Args.Count == 0 || ctx.Args[^1] != "]") throw new ShellException("missing `]'", 2);
            return Test.Evaluate(ctx, ctx.Args.Take(ctx.Args.Count - 1).ToList()) ? 0 : 1;
        }, "Evaluate a conditional expression");

        CommandHandler runScript = async ctx =>
        {
            var io = ctx.Io;
            var args = ctx.Args.ToList();
            var sh = ctx.Shell;
            bool isBash = ctx.Name is "bash" or "sh", command = false;
            // bash's own options: -c, and -e -u -x -o pipefail ... which the new shell starts with
            var options = new List<(string Name, bool On)>();
            int i = 0;
            for (; isBash && i < args.Count && args[i].Length > 1 && args[i][0] is '-' or '+'; i++)
            {
                var f = args[i];
                if (f == "--") { i++; break; }
                if (f.StartsWith("--")) continue; // --norc, --noprofile, --login
                foreach (var c in f[1..])
                {
                    if (c == 'c') command = true;
                    else if (c == 'o' && i + 1 < args.Count) options.Add((args[++i], f[0] == '-'));
                    else if ("eufxC".Contains(c)) options.Add((c switch { 'e' => "errexit", 'u' => "nounset", 'f' => "noglob", 'x' => "xtrace", _ => "noclobber" }, f[0] == '-'));
                }
            }
            args = args[i..];
            void Setup() { foreach (var (name, on) in options) sh.SetOption(name, on); }
            if (command)
            {
                if (args.Count == 0) throw new ShellException("-c: option requires an argument", 2);
                return await sh.RunScriptTextAsync(args[0], io, subshell: true, ctx.CancellationToken, args.Skip(2).ToList(), new(args.ElementAtOrDefault(1) ?? ctx.Name, null, Setup));
            }
            if (args.Count == 0)
            {
                if (!isBash) throw new ShellException("filename argument required", 2);
                return await sh.RunScriptTextAsync(ctx.StdinText, io with { Stdin = new StdinBuffer("") }, subshell: true, ctx.CancellationToken, [], new(ctx.Name, null, Setup));
            }
            var text = ctx.Fs.ReadAllText(ctx.ResolvePath(args[0]));
            if (isBash) return await sh.RunScriptTextAsync(text, io, subshell: true, ctx.CancellationToken, args.Skip(1).ToList(), new(args[0], args[0], Setup));
            sh.SourceDepth++;
            try { return await sh.RunScriptTextAsync(text, io, subshell: false, ctx.CancellationToken, args.Count > 1 ? args.Skip(1).ToList() : null, sourced: args[0]); }
            catch (ReturnSignal r) { return r.Code; }   // `return` in a sourced file ends that file only
            finally { sh.SourceDepth--; }
        };
        shell.Register("bash", runScript, "Run a command string (-c) or script file");
        shell.Register("sh", runScript, "Run a command string (-c) or script file");
        shell.Register("source", runScript, "Run a script in the current shell");
        shell.Register(".", runScript, "Run a script in the current shell");

        shell.Register("help", ctx =>
        {
            if (ctx.Args.Count > 0)
            {
                if (!ctx.Shell.Commands.TryGetValue(ctx.Args[0], out var c)) throw new ShellException($"no help topics match '{ctx.Args[0]}'");
                ctx.Stdout.WriteLine(c.Usage ?? $"{c.Name}: {c.Description}");
                return 0;
            }
            var cmds = ctx.Shell.Commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            int width = cmds.Max(c => c.Name.Length) + 2;
            ctx.Stdout.WriteLine("Available commands (run '<command> --help' for details on non-standard ones):");
            foreach (var c in cmds) ctx.Stdout.WriteLine($"  {c.Name.PadRight(width)}{c.Description}");
            return 0;
        }, "List available commands");
    }

    static void RegisterSystemCommands(Shell shell)
    {
        shell.Register("whoami", ctx => { ctx.Stdout.WriteLine(ctx.Shell.Options.User); return 0; }, "Print the current user");
        shell.Register("hostname", ctx => { ctx.Stdout.WriteLine(ctx.Shell.Options.Hostname); return 0; }, "Print the host name");

        shell.Register("uname", ctx =>
        {
            var a = Args.Parse(ctx.Args, "asnrvmo", longs: "all=a");
            var host = ctx.Shell.Options.Hostname;
            var parts = new List<string>();
            bool all = a.Has('a');
            if (all || a.Has('s') || a.Flags.Count == 0) parts.Add("Linux");
            if (all || a.Has('n')) parts.Add(host);
            if (all || a.Has('r')) parts.Add("6.8.0-generic");
            if (all || a.Has('v')) parts.Add("#1 SMP PREEMPT_DYNAMIC");
            if (all || a.Has('m')) parts.Add("x86_64");
            if (all || a.Has('o')) parts.Add("GNU/Linux");
            ctx.Stdout.WriteLine(string.Join(' ', parts));
            return 0;
        }, "Print system information");

        shell.Register("date", ctx =>
        {
            // the simulated machine's clock is UTC (TZ is unset), so output never depends on the host's time zone
            var a = Args.Parse(ctx.Args, "uIR", "dr", "utc=u,universal=u,iso-8601=I,date=d,reference=r,rfc-email=R");
            var now = DateTime.UtcNow;
            if (a.Value('d') is { } d) now = ParseDate(d, now);
            else if (a.Value('r') is { } r) now = ctx.Fs.Stat(ctx.ResolvePath(r)).Modified.ToUniversalTime();
            var fmt = a.Positional.FirstOrDefault(p => p.StartsWith('+'));
            ctx.Stdout.WriteLine(Strftime(now, a.Has('I') ? "%Y-%m-%d" : a.Has('R') ? "%a, %d %b %Y %H:%M:%S +0000" : fmt != null ? fmt[1..] : "%a %b %e %H:%M:%S %Z %Y"));
            return 0;
        }, "Print the date and time");

        shell.Register("ps", ctx =>
        {
            ctx.Stdout.Write("    PID TTY          TIME CMD\n      1 pts/0    00:00:00 bash\n   4242 pts/0    00:00:00 ps\n");
            return 0;
        }, "List processes");
    }

    static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    static readonly string[] MonthNames = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>GNU date's format directives (the common ones, with the - flag for no padding). t is UTC.</summary>
    internal static string Strftime(DateTime t, string fmt)
    {
        var sb = new StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < fmt.Length;)
        {
            if (fmt[i] != '%' || i + 1 >= fmt.Length) { sb.Append(fmt[i++]); continue; }
            bool noPad = fmt[i + 1] == '-' && i + 2 < fmt.Length;
            char d = noPad ? fmt[i + 2] : fmt[i + 1];
            i += noPad ? 3 : 2;
            int hour12 = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
            (int Value, int Width)? num = d switch
            {
                'Y' => (t.Year, 4), 'm' => (t.Month, 2), 'd' => (t.Day, 2), 'e' => (t.Day, -2), 'H' => (t.Hour, 2), 'k' => (t.Hour, -2),
                'I' => (hour12, 2), 'l' => (hour12, -2), 'M' => (t.Minute, 2), 'S' => (t.Second, 2), 'y' => (t.Year % 100, 2),
                'j' => (t.DayOfYear, 3), 'u' => (t.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)t.DayOfWeek, 1), 'w' => ((int)t.DayOfWeek, 1),
                _ => null,
            };
            if (num is var (value, width))
            {
                var text = value.ToString(inv);
                sb.Append(noPad ? text : text.PadLeft(Math.Abs(width), width > 0 ? '0' : ' '));
                continue;
            }
            sb.Append(d switch
            {
                'F' => t.ToString("yyyy-MM-dd", inv), 'T' => t.ToString("HH:mm:ss", inv), 'D' => t.ToString("MM/dd/yy", inv), 'R' => t.ToString("HH:mm", inv),
                'a' => DayNames[(int)t.DayOfWeek][..3], 'A' => DayNames[(int)t.DayOfWeek], 'b' or 'h' => MonthNames[t.Month - 1][..3],
                'B' => MonthNames[t.Month - 1], 'p' => t.Hour < 12 ? "AM" : "PM",
                's' => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(inv),
                'N' => ((t.Ticks % TimeSpan.TicksPerSecond) * 100).ToString("000000000", inv), 'Z' => "UTC", 'z' => "+0000",
                'n' => "\n", 't' => "\t", '%' => "%", 'c' => Strftime(t, "%a %b %e %H:%M:%S %Y"),
                _ => "%" + (noPad ? "-" : "") + d,
            });
        }
        return sb.ToString();
    }

    static readonly Dictionary<string, int> DateUnits = new()
    {
        ["second"] = 1, ["sec"] = 1, ["minute"] = 60, ["min"] = 60, ["hour"] = 3600, ["day"] = 86400, ["week"] = 604800, ["fortnight"] = 1209600,
    };

    /// <summary>date -d: @EPOCH, YYYY-MM-DD[ HH:MM[:SS]], now/today/yesterday/tomorrow, and "[+-]N unit[s] [ago]" offsets (UTC).</summary>
    internal static DateTime ParseDate(string text, DateTime now)
    {
        var s = text.Trim().ToLowerInvariant();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^@(-?\d+(?:\.\d+)?)$");
        if (m.Success) return DateTime.UnixEpoch.AddSeconds(double.Parse(m.Groups[1].Value, inv));
        var bad = new ShellException($"invalid date '{text}'");
        var t = now;
        m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d{4})-(\d{1,2})-(\d{1,2})(?:[t ](\d{1,2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?)?(z|utc|[+-]00:?00)?");
        if (m.Success)
        {
            int G(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, inv) : 0;
            try { t = new DateTime(G(1), G(2), G(3), G(4), G(5), G(6), DateTimeKind.Utc); }
            catch (ArgumentOutOfRangeException) { throw bad; }
            s = s[m.Length..].Trim();
        }
        else
            foreach (var (word, days) in new[] { ("yesterday", -1), ("tomorrow", 1), ("today", 0), ("now", 0) })
                if (s.StartsWith(word, StringComparison.Ordinal)) { t = now.AddDays(days); s = s[word.Length..].Trim(); break; }
        foreach (System.Text.RegularExpressions.Match r in System.Text.RegularExpressions.Regex.Matches(s,
                     @"(next|last|[+-]?\s*\d+)?\s*(second|sec|minute|min|hour|day|week|fortnight|month|year)s?(\s+ago)?|(\S+)"))
        {
            if (r.Groups[4].Success || !r.Groups[2].Success) throw bad;
            var word = (r.Groups[1].Success ? r.Groups[1].Value : "1").Replace(" ", "");
            int n = word == "next" ? 1 : word == "last" ? -1 : int.Parse(word, inv);
            if (r.Groups[3].Success) n = -n;
            var unit = r.Groups[2].Value;
            if (unit is "month" or "year")
            {
                // like GNU date, a day past the end of the month rolls over (Jan 31 + 1 month = Mar 2 or 3)
                int months = t.Month - 1 + n * (unit == "year" ? 12 : 1);
                int year = t.Year + (int)Math.Floor(months / 12.0), month = ((months % 12) + 12) % 12 + 1;
                t = new DateTime(year, month, 1, t.Hour, t.Minute, t.Second, DateTimeKind.Utc).AddTicks(t.Ticks % TimeSpan.TicksPerSecond).AddDays(t.Day - 1);
            }
            else t = t.AddSeconds((double)n * DateUnits[unit]);
        }
        return t;
    }
}

internal static class Test
{
    public static bool Unary(Shell shell, string op, string arg)
    {
        var path = shell.ResolvePath(arg);
        var fs = shell.Fs;
        return op switch
        {
            "-z" => arg.Length == 0,
            "-n" => arg.Length > 0,
            "-e" => fs.Exists(path),
            "-r" => fs.CanRead(path),
            "-w" => fs.CanWrite(path),
            "-x" => fs.CanExecute(path),
            "-f" => fs.IsFile(path),
            "-d" => fs.IsDirectory(path),
            "-s" => fs.IsFile(path) && fs.Stat(path).Size > 0,
            "-L" or "-h" or "-b" or "-c" or "-p" or "-S" or "-t" or "-g" or "-u" or "-k" => false, // no links, devices, pipes, sockets, terminals or special bits
            "-O" or "-G" or "-N" => fs.Exists(path),
            "-v" => shell.IsVariableSet(arg),
            "-o" => shell.OptionList.Any(o => o.Name == arg && o.On),
            _ => throw new ShellException($"{op}: unary operator expected", 2),
        };
    }

    static readonly HashSet<string> BinaryOps = ["=", "==", "!=", "<", ">", "-eq", "-ne", "-lt", "-le", "-gt", "-ge", "-nt", "-ot"];

    /// <summary>-nt / -ot: a missing file is older than any existing one, as in bash.</summary>
    internal static bool Newer(Shell sh, string left, string right)
    {
        DateTime? Time(string p) => sh.Fs.Exists(sh.ResolvePath(p)) ? sh.Fs.Stat(sh.ResolvePath(p)).Modified : null;
        return Time(left) is { } l && (Time(right) is not { } r || l > r);
    }

    static bool Binary(Shell sh, string left, string op, string right)
    {
        switch (op)
        {
            case "=" or "==": return left == right;
            case "!=": return left != right;
            case "<": return string.CompareOrdinal(left, right) < 0;
            case ">": return string.CompareOrdinal(left, right) > 0;
            case "-nt": return Newer(sh, left, right);
            case "-ot": return Newer(sh, right, left);
        }
        bool okL = long.TryParse(left.Trim(), out var l), okR = long.TryParse(right.Trim(), out var r);
        if (!okL || !okR) throw new ShellException($"{(okL ? right : left)}: integer expression expected", 2);
        return op switch { "-eq" => l == r, "-ne" => l != r, "-lt" => l < r, "-le" => l <= r, "-gt" => l > r, _ => l >= r };
    }

    /// <summary>test / [ ]: POSIX rules for up to 3 arguments, then ! ( ) -a -o with the usual precedence.</summary>
    public static bool Evaluate(CommandContext ctx, List<string> a)
    {
        int n = a.Count;
        switch (n)
        {
            case 0: return false;
            case 1: return a[0].Length > 0;
            case 2: return a[0] == "!" ? a[1].Length == 0 : Unary(ctx.Shell, a[0], a[1]);
            case 3:
                if (BinaryOps.Contains(a[1])) return Binary(ctx.Shell, a[0], a[1], a[2]);
                if (a[1] is "-a" or "-o") return a[1] == "-a" ? a[0].Length > 0 && a[2].Length > 0 : a[0].Length > 0 || a[2].Length > 0;
                if (a[0] == "!") return !Evaluate(ctx, a.Skip(1).ToList());
                if (a[0] == "(" && a[2] == ")") return a[1].Length > 0;
                throw new ShellException($"{a[1]}: binary operator expected", 2);
            case 4 when a[0] == "!": return !Evaluate(ctx, a.Skip(1).ToList());
        }
        int pos = 0;
        string? Peek() => pos < n ? a[pos] : null;
        bool Or()
        {
            var v = And();
            while (Peek() == "-o") { pos++; var r = And(); v = v || r; }
            return v;
        }
        bool And()
        {
            var v = Not();
            while (Peek() == "-a") { pos++; var r = Not(); v = v && r; }
            return v;
        }
        bool Not()
        {
            if (Peek() == "!") { pos++; return !Not(); }
            return Primary();
        }
        bool Primary()
        {
            var t = Peek() ?? throw new ShellException("argument expected", 2);
            if (t == "(")
            {
                pos++;
                var v = Or();
                if (Peek() != ")") throw new ShellException("`)' expected", 2);
                pos++;
                return v;
            }
            if (pos + 2 < n && BinaryOps.Contains(a[pos + 1])) { pos += 3; return Binary(ctx.Shell, t, a[pos - 2], a[pos - 1]); }
            if (t.Length == 2 && t[0] == '-' && pos + 1 < n) { pos += 2; return Unary(ctx.Shell, t, a[pos - 1]); }
            pos++;
            return t.Length > 0;
        }
        var result = Or();
        if (pos < n) throw new ShellException("too many arguments", 2);
        return result;
    }
}

internal static class Printf
{
    static readonly System.Text.RegularExpressions.Regex Spec = new(@"%([-0 +#]*)(\*|\d*)(?:\.(\*|\d*))?(?:\(([^)]*)\))?([sdiufxXoceEgGbqT%])");
    static readonly System.Text.RegularExpressions.Regex ShellSafe = new(@"^[A-Za-z0-9_./+:,=@%^-]+$");
    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Parses a printf integer argument like bash: 0x1f, 017 (octal), 'c (character code).</summary>
    internal static (long Value, bool Valid) CInteger(string s)
    {
        var t = s.Trim();
        if (t.Length > 0 && t[0] is '\'' or '"') return (t.Length > 1 ? t[1] : 0, true);
        var m = System.Text.RegularExpressions.Regex.Match(t, "^[+-]?(0[xX][0-9a-fA-F]+|0[0-7]*|[1-9][0-9]*)");
        if (!m.Success) return (0, t.Length == 0);
        var body = m.Groups[1].Value;
        long v;
        try
        {
            v = body.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(body[2..], 16)
                : body.StartsWith('0') ? (body.Length > 1 ? Convert.ToInt64(body, 8) : 0) : long.Parse(body, Inv);
        }
        catch (OverflowException) { return (long.MaxValue, false); }
        return (t.StartsWith('-') ? -v : v, m.Length == t.Length);
    }

    static (double Value, bool Valid) CFloat(string s)
    {
        var t = s.Trim();
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^[+-]?(\d+\.?\d*([eE][+-]?\d+)?|\.\d+([eE][+-]?\d+)?|inf|nan)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return (0, t.Length == 0);
        var text = m.Value.ToLowerInvariant();
        double v = text.EndsWith("inf") ? (text.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity)
            : text.EndsWith("nan") ? double.NaN : double.Parse(m.Value, Inv);
        return (v, m.Length == t.Length);
    }

    /// <summary>printf %q: quotes a string so bash would read it back as one word.</summary>
    internal static string ShellQuote(string s)
    {
        if (s.Length == 0) return "''";
        if (ShellSafe.IsMatch(s)) return s;
        var sb = new StringBuilder();
        if (s.Any(c => c < 32 || c == 127))
        {
            sb.Append("$'");
            foreach (var c in s)
                sb.Append(c switch
                {
                    '\\' => "\\\\", '\'' => "\\'", '\n' => "\\n", '\t' => "\\t", '\r' => "\\r", '\u001b' => "\\E",
                    _ when c < 32 || c == 127 => "\\" + Convert.ToString(c, 8).PadLeft(3, '0'),
                    _ => c.ToString(),
                });
            return sb.Append('\'').ToString();
        }
        foreach (var c in s)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c > 127 || "_./+:,=@%^-".Contains(c))) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>C printf formatting of one number (flags "-0 +#", width, precision; conversions d o x X e E f g G).</summary>
    internal static string FormatNumber(string flags, int width, int? prec, char conv, double fval, long ival)
    {
        bool left = flags.Contains('-'), zero = flags.Contains('0') && !left, alt = flags.Contains('#');
        bool negative;
        string body;
        if (conv is 'd' or 'o' or 'x' or 'X')
        {
            negative = ival < 0;
            ulong mag = negative ? (ulong)(-(ival + 1)) + 1 : (ulong)ival;
            body = conv switch
            {
                'o' => Convert.ToString((long)mag, 8), 'x' => mag.ToString("x"), 'X' => mag.ToString("X"),
                _ => mag.ToString(Inv),
            };
            if (prec is { } p) { body = p == 0 && mag == 0 ? "" : body.PadLeft(p, '0'); zero = false; }
            if (alt && conv == 'o' && !body.StartsWith('0')) body = "0" + body;
            if (alt && conv is 'x' or 'X' && mag != 0) body = (conv == 'x' ? "0x" : "0X") + body;
        }
        else
        {
            negative = fval < 0 || (fval == 0 && double.IsNegative(fval));
            double mag = Math.Abs(fval);
            int pr = prec ?? 6;
            if (double.IsNaN(fval)) body = char.IsUpper(conv) ? "NAN" : "nan";
            else if (double.IsInfinity(fval)) body = char.IsUpper(conv) ? "INF" : "inf";
            else body = char.ToLowerInvariant(conv) switch
            {
                'f' => mag.ToString("F" + pr, Inv),
                'e' => Exponent(mag, pr, conv == 'E'),
                _ => General(mag, pr, conv == 'G', alt),
            };
            if (double.IsNaN(fval) || double.IsInfinity(fval)) zero = false;
        }
        var sign = negative ? "-" : flags.Contains('+') ? "+" : flags.Contains(' ') ? " " : "";
        int pad = width - sign.Length - body.Length;
        if (pad <= 0) return sign + body;
        if (left) return sign + body + new string(' ', pad);
        if (zero)
        {
            var prefix = body.StartsWith("0x") || body.StartsWith("0X") ? body[..2] : "";
            return sign + prefix + new string('0', pad) + body[prefix.Length..];
        }
        return new string(' ', pad) + sign + body;
    }

    static string Exponent(double mag, int prec, bool upper)
    {
        var s = mag.ToString((upper ? "E" : "e") + prec, Inv); // e.g. 1.234500e+003
        int e = s.IndexOfAny(['e', 'E']);
        var exp = s[(e + 2)..].TrimStart('0');
        return s[..(e + 2)] + exp.PadLeft(2, '0');
    }

    static string General(double mag, int prec, bool upper, bool alt)
    {
        int p = prec == 0 ? 1 : prec;
        if (mag == 0) return alt ? "0." + new string('0', p - 1) : "0";
        var e = Exponent(mag, p - 1, upper);
        int x = int.Parse(e[(e.IndexOfAny(['e', 'E']) + 1)..], Inv);
        string body = p > x && x >= -4 ? mag.ToString("F" + (p - 1 - x), Inv) : e;
        if (alt) return body;
        int ePos = body.IndexOfAny(['e', 'E']);
        string mant = ePos < 0 ? body : body[..ePos], rest = ePos < 0 ? "" : body[ePos..];
        if (mant.Contains('.')) mant = mant.TrimEnd('0').TrimEnd('.');
        return mant + rest;
    }

    /// <summary>bash printf: C conversions, the format is reused while arguments remain; bad numbers are reported in errors.</summary>
    const char StopMark = '\uE001';

    public static string Format(string format, List<string> args, List<string>? errors = null)
    {
        var fmt = Builtins.Unescape(format, keepUnknown: true);
        var sb = new StringBuilder();
        int next = 0;
        bool stop = false; // a \c in a %b argument ends all output, the rest of the format included
        string Take() { var v = next < args.Count ? args[next] : ""; next++; return v; }
        do
        {
            int before = next;
            sb.Append(Spec.Replace(fmt, m =>
            {
                char conv = m.Groups[5].Value[0];
                if (conv == '%') return "%";
                if (conv == 'u') conv = 'd';
                if (m.Groups[4].Success != (conv == 'T')) return m.Value;
                var flags = m.Groups[1].Value;
                int width = 0;
                if (m.Groups[2].Value == "*")
                {
                    var w = CInteger(Take()).Value;
                    if (w < 0) { flags += "-"; w = -w; }
                    width = (int)Math.Min(w, 10_000);
                }
                else if (m.Groups[2].Value.Length > 0) width = (int)Math.Min(long.Parse(m.Groups[2].Value, Inv), 10_000);
                int? prec = null;
                if (m.Groups[3].Success)
                    prec = m.Groups[3].Value == "*" ? (int)Math.Clamp(CInteger(Take()).Value, 0, 10_000)
                        : m.Groups[3].Value.Length > 0 ? (int)Math.Min(long.Parse(m.Groups[3].Value, Inv), 10_000) : 0;
                var arg = Take();
                if (conv is 's' or 'b' or 'q' or 'c' or 'T')
                {
                    if (stop) return "";
                    if (conv == 'b' && arg.IndexOf("\\c", StringComparison.Ordinal) is var cut and >= 0) { arg = arg[..cut]; stop = true; return Builtins.Unescape(arg, keepUnknown: true, echo: true) + StopMark; }
                    var text = conv switch
                    {
                        'b' => Builtins.Unescape(arg, keepUnknown: true, echo: true), 'q' => ShellQuote(arg), 'c' => arg.Length > 0 ? arg[..1] : "",
                        // %(fmt)T: a time in seconds since the epoch; -1 or nothing is now (UTC, like the rest of this machine)
                        'T' => Builtins.Strftime(arg is "" or "-1" or "-2" ? DateTime.UtcNow : DateTime.UnixEpoch.AddSeconds(CInteger(arg).Value), m.Groups[4].Value),
                        _ => arg,
                    };
                    if (prec is { } p && p < text.Length && conv != 'c') text = text[..p];
                    return flags.Contains('-') ? text.PadRight(width) : text.PadLeft(width);
                }
                if (conv is 'd' or 'i' or 'o' or 'x' or 'X')
                {
                    var (v, ok) = CInteger(arg);
                    if (!ok) errors?.Add($"printf: {arg}: invalid number");
                    return FormatNumber(flags, width, prec, conv == 'i' ? 'd' : conv, 0, v);
                }
                var (f, fok) = CFloat(arg);
                if (!fok) errors?.Add($"printf: {arg}: invalid number");
                return FormatNumber(flags, width, prec, conv, f, 0);
            }));
            if (stop) { sb.Length = sb.ToString().IndexOf(StopMark); break; }
            if (next == before) break; // format consumed no arguments
        } while (next < args.Count);
        return sb.ToString();
    }
}
