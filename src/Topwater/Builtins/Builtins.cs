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
    public static List<string> NumericShorthand(IReadOnlyList<string> args) =>
        args.SelectMany(a => a.Length > 1 && a[0] == '-' && a.Skip(1).All(char.IsDigit) ? new[] { "-n", a[1..] } : [a]).ToList();
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

    static readonly HashSet<string> NoStandardOptions = ["sudo", "su", "echo", "printf", "test", "[", "true", "false", "awk", "help", "exit", "return", "break", "continue", "set", "eval", "command", "type", "alias", "export", "local", "declare", "read", "cd"];

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
    internal static IEnumerable<(string Name, string Text)> Inputs(CommandContext ctx, IReadOnlyList<string> files, Action onError) =>
        Inputs(ctx, files, onError, ctx.Fs.ReadAllText, () => ctx.StdinText);

    /// <summary>Like <see cref="Inputs(CommandContext, IReadOnlyList{string}, Action)"/>, but the exact bytes (stdin as UTF-8).</summary>
    internal static IEnumerable<(string Name, byte[] Text)> InputBytes(CommandContext ctx, IReadOnlyList<string> files, Action onError) =>
        Inputs(ctx, files, onError, ctx.Fs.ReadAllBytes, () => Encoding.UTF8.GetBytes(ctx.StdinText));

    static IEnumerable<(string Name, T Text)> Inputs<T>(CommandContext ctx, IReadOnlyList<string> files, Action onError, Func<string, T> read, Func<T> stdin)
    {
        if (files.Count == 0) { yield return ("(standard input)", stdin()); yield break; }
        foreach (var f in files)
        {
            if (f == "-") { yield return ("(standard input)", stdin()); continue; }
            T text;
            try { text = read(ctx.ResolvePath(f)); }
            catch (ShellException e)
            {
                ctx.Stderr.WriteLine($"{ctx.Name}: {f}: {e.Message[(e.Message.LastIndexOf(':') + 2)..]}");
                onError();
                continue;
            }
            yield return (f, text);
        }
    }

    internal static string Unescape(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char n = s[++i];
            sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', '0' => '\0', 'a' => '\a', 'e' => '\u001b', '"' => '"', _ => n });
        }
        return sb.ToString();
    }

    static string SafeUnescape(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && "ntr\\0ae\"".Contains(s[i + 1])) { sb.Append(Unescape(s.Substring(i, 2))); i++; }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    static void RegisterShellCommands(Shell shell)
    {
        shell.Register("true", _ => 0, "Do nothing, successfully");
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
            ctx.Stdout.Write(escapes ? SafeUnescape(text) : text);
            if (newline) ctx.Stdout.Write('\n');
            return 0;
        }, "Print arguments");

        shell.Register("printf", ctx =>
        {
            if (ctx.Args.Count == 0) throw new ShellException("usage: printf format [arguments]", 2);
            ctx.Stdout.Write(Printf.Format(ctx.Args[0], ctx.Args.Skip(1).ToList()));
            return 0;
        }, "Format and print data");

        shell.Register("cd", ctx =>
        {
            var target = ctx.Args.Count == 0 ? ctx.Shell.Environment.GetValueOrDefault("HOME", "/")
                : ctx.Args[0] == "-" ? ctx.Shell.Environment.GetValueOrDefault("OLDPWD", ctx.Shell.Cwd) : ctx.Args[0];
            var abs = ctx.ResolvePath(target);
            if (!ctx.Fs.Exists(abs)) throw new ShellException($"{target}: No such file or directory");
            if (!ctx.Fs.IsDirectory(abs)) throw new ShellException($"{target}: Not a directory");
            ctx.Shell.Environment["OLDPWD"] = ctx.Shell.Cwd;
            ctx.Shell.Cwd = abs;
            if (ctx.Args is ["-"]) ctx.Stdout.WriteLine(abs);
            return 0;
        }, "Change the working directory");

        shell.Register("pwd", ctx => { ctx.Stdout.WriteLine(ctx.Shell.Cwd); return 0; }, "Print the working directory");

        shell.Register("export", ctx =>
        {
            var args = ctx.Args.Where(a => a != "-p").ToList();
            if (args.Count == 0)
            {
                foreach (var (k, v) in ctx.Shell.Environment.OrderBy(e => e.Key, StringComparer.Ordinal)) ctx.Stdout.WriteLine($"declare -x {k}=\"{v}\"");
                return 0;
            }
            foreach (var a in args)
            {
                int eq = a.IndexOf('=');
                if (eq > 0) ctx.Shell.Environment[a[..eq]] = a[(eq + 1)..];
                else ctx.Shell.Environment.TryAdd(a, "");
            }
            return 0;
        }, "Set environment variables");

        shell.Register("unset", ctx =>
        {
            bool functions = ctx.Args.Contains("-f");
            foreach (var a in ctx.Args.Where(a => !a.StartsWith('-')))
                if (functions) ctx.Shell.Functions.Remove(a); else ctx.Shell.Environment.Remove(a);
            return 0;
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
                    var kind = ctx.Shell.Aliases.ContainsKey(a) ? "alias" : ctx.Shell.Functions.ContainsKey(a) ? "function" : ctx.Shell.Commands.ContainsKey(a) ? "file" : null;
                    if (kind == null) { code = 1; continue; }
                    ctx.Stdout.WriteLine(opts.Has('t') ? kind : kind == "file" ? $"/usr/bin/{a}" : "");
                    continue;
                }
                if (ctx.Shell.Aliases.TryGetValue(a, out var v)) ctx.Stdout.WriteLine($"{a} is aliased to `{v}'");
                else if (ctx.Shell.Functions.ContainsKey(a)) ctx.Stdout.WriteLine($"{a} is a function");
                else if (ctx.Shell.Commands.ContainsKey(a)) ctx.Stdout.WriteLine($"{a} is /usr/bin/{a}");
                else { ctx.Stderr.WriteLine($"type: {a}: not found"); code = 1; }
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
            bool isBash = ctx.Name is "bash" or "sh";
            while (isBash && args.Count > 0 && args[0].StartsWith('-') && args[0] != "-c") args.RemoveAt(0); // -e, -x, -l ...
            if (isBash && args is ["-c", var script, .. var rest])
                return await ctx.Shell.RunScriptTextAsync(script, io, subshell: true, ctx.CancellationToken, rest.Skip(1).ToList());
            if (args.Count == 0)
            {
                if (!isBash) throw new ShellException("filename argument required", 2);
                return await ctx.Shell.RunScriptTextAsync(ctx.StdinText, io with { Stdin = new StdinBuffer("") }, subshell: true, ctx.CancellationToken);
            }
            var text = ctx.Fs.ReadAllText(ctx.ResolvePath(args[0]));
            return await ctx.Shell.RunScriptTextAsync(text, io, subshell: isBash, ctx.CancellationToken, args.Count > 1 || isBash ? args.Skip(1).ToList() : null);
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
            var a = Args.Parse(ctx.Args, "uI", longs: "utc=u,iso-8601=I");
            var now = a.Has('u') ? DateTime.UtcNow : DateTime.Now;
            var fmt = a.Positional.FirstOrDefault(p => p.StartsWith('+'));
            ctx.Stdout.WriteLine(a.Has('I') ? now.ToString("yyyy-MM-dd") : fmt != null ? Strftime(now, fmt[1..]) : now.ToString("ddd MMM d HH:mm:ss yyyy"));
            return 0;
        }, "Print the date and time");

        shell.Register("ps", ctx =>
        {
            ctx.Stdout.Write("    PID TTY          TIME CMD\n      1 pts/0    00:00:00 bash\n   4242 pts/0    00:00:00 ps\n");
            return 0;
        }, "List processes");
    }

    static string Strftime(DateTime t, string fmt)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%' || i + 1 >= fmt.Length) { sb.Append(fmt[i]); continue; }
            sb.Append(fmt[++i] switch
            {
                'Y' => t.ToString("yyyy"), 'm' => t.ToString("MM"), 'd' => t.ToString("dd"), 'H' => t.ToString("HH"),
                'M' => t.ToString("mm"), 'S' => t.ToString("ss"), 'F' => t.ToString("yyyy-MM-dd"), 'T' => t.ToString("HH:mm:ss"),
                'a' => t.ToString("ddd"), 'A' => t.ToString("dddd"), 'b' => t.ToString("MMM"), 'B' => t.ToString("MMMM"),
                'y' => t.ToString("yy"), 'j' => t.DayOfYear.ToString("000"), 'Z' => "UTC", 'z' => "+0000",
                's' => new DateTimeOffset(t).ToUnixTimeSeconds().ToString(), 'n' => "\n", '%' => "%",
                var c => "%" + c,
            });
        }
        return sb.ToString();
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
            "-L" => false,
            _ => throw new ShellException($"{op}: unary operator expected", 2),
        };
    }

    public static bool Evaluate(CommandContext ctx, List<string> a)
    {
        if (a.Count > 0 && a[0] == "!") return !Evaluate(ctx, a.Skip(1).ToList());
        switch (a.Count)
        {
            case 0: return false;
            case 1: return a[0].Length > 0;
            case 2:
                return Unary(ctx.Shell, a[0], a[1]);
            case 3:
                if (a[1] is "=" or "==") return a[0] == a[2];
                if (a[1] == "!=") return a[0] != a[2];
                if (!long.TryParse(a[0], out var l) || !long.TryParse(a[2], out var r))
                    throw new ShellException($"integer expression expected", 2);
                return a[1] switch
                {
                    "-eq" => l == r, "-ne" => l != r, "-lt" => l < r, "-le" => l <= r, "-gt" => l > r, "-ge" => l >= r,
                    _ => throw new ShellException($"{a[1]}: binary operator expected", 2),
                };
            default: throw new ShellException("too many arguments (-a/-o are not supported; use && or ||)", 2);
        }
    }
}

internal static class Printf
{
    static readonly System.Text.RegularExpressions.Regex Spec = new(@"%([-0 +#]*)(\d*)(?:\.(\d+))?([sdifxXoc%])");

    public static string Format(string format, List<string> args)
    {
        var fmt = Unescape(format);
        var sb = new StringBuilder();
        int next = 0;
        do
        {
            int before = next;
            sb.Append(Spec.Replace(fmt, m =>
            {
                char conv = m.Groups[4].Value[0];
                if (conv == '%') return "%";
                var arg = next < args.Count ? args[next++] : "";
                var flags = m.Groups[1].Value;
                int width = m.Groups[2].Value.Length > 0 ? int.Parse(m.Groups[2].Value) : 0;
                int? prec = m.Groups[3].Value.Length > 0 ? int.Parse(m.Groups[3].Value) : null;
                string s = conv switch
                {
                    's' => prec is { } p && p < arg.Length ? arg[..p] : arg,
                    'c' => arg.Length > 0 ? arg[..1] : "",
                    'd' or 'i' => ParseLong(arg).ToString(),
                    'x' => ParseLong(arg).ToString("x"),
                    'X' => ParseLong(arg).ToString("X"),
                    'o' => System.Convert.ToString(ParseLong(arg), 8),
                    _ => (double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0)
                        .ToString("F" + (prec ?? 6), System.Globalization.CultureInfo.InvariantCulture),
                };
                if (s.Length >= width) return s;
                if (flags.Contains('-')) return s.PadRight(width);
                return flags.Contains('0') && conv != 's' ? s.PadLeft(width, '0') : s.PadLeft(width);
            }));
            if (next == before) break; // format consumed no arguments
        } while (next < args.Count);
        return sb.ToString();
    }

    static long ParseLong(string s) => long.TryParse(s, out var v) ? v : 0;

    static string Unescape(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                sb.Append(n switch { 'n' => "\n", 't' => "\t", 'r' => "\r", '\\' => "\\", '"' => "\"", 'e' => "\u001b", _ => "\\" + n });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
