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
                sb.Append(ByteChar((k > j ? Convert.ToInt32(s[j..k], 8) : 0) & 0xFF));
                i = k - 1;
            }
            else if (n == 'x' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 2]))
            {
                int k = i + 2;
                while (k < s.Length && k < i + 4 && Uri.IsHexDigit(s[k])) k++;
                sb.Append(ByteChar(Convert.ToInt32(s[(i + 2)..k], 16)));
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
        return ByteText.Rejoin(sb.ToString());
    }

    /// <summary>A byte value as a character: ASCII as is, 0x80-0xff as the escaped raw byte (<see cref="ByteText"/>), so <c>printf '\xd5'</c> writes one byte.</summary>
    static char ByteChar(int b) => b < 0x80 ? (char)b : (char)(0xDC00 + b);

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
            // %:z +hh:mm, %::z +hh:mm:ss, %:::z +hh (the clock is UTC)
            int colons = 0;
            while (i + 1 + colons < fmt.Length && fmt[i + 1 + colons] == ':') colons++;
            if (colons is > 0 and <= 3 && i + 1 + colons < fmt.Length && fmt[i + 1 + colons] == 'z')
            {
                sb.Append(colons switch { 1 => "+00:00", 2 => "+00:00:00", _ => "+00" });
                i += colons + 2;
                continue;
            }
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

    /// <summary>
    /// date -d, from GNU date's documented input formats: items in any order, each optional. Calendar dates
    /// (2026-11-20, 11/20/2026, 20 Nov 2026, Nov 20, 2026, 20261120), times (13:30[:00[.5]], 1pm), zones
    /// (UTC, Z, EST..., +0100, +01:00), a day of the week, today/now/yesterday/tomorrow, @EPOCH, and relative items
    /// ("3 days ago", "next week", "+2 hours") applied last. Results are UTC, the simulated machine's zone.
    /// </summary>
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

    public static string Format(string format, List<string> args, List<string>? errors = null, DateTime? now = null)
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
                        'T' => Builtins.Strftime(arg is "" or "-1" or "-2" ? now ?? DateTime.UtcNow : DateTime.UnixEpoch.AddSeconds(CInteger(arg).Value), m.Groups[4].Value),
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
