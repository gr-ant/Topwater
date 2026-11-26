using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Topwater;

internal static partial class Builtins
{
    internal static readonly Dictionary<string, string> PosixClasses = new()
    {
        ["alpha"] = "a-zA-Z", ["digit"] = "0-9", ["alnum"] = "a-zA-Z0-9", ["upper"] = "A-Z", ["lower"] = "a-z", ["space"] = @" \t\n\r\f\v",
        ["blank"] = @" \t", ["punct"] = @"!-/:-@\[-`{-~", ["xdigit"] = "0-9A-Fa-f", ["cntrl"] = @"\x00-\x1f\x7f", ["print"] = @"\x20-\x7e",
        ["graph"] = @"\x21-\x7e", ["word"] = "a-zA-Z0-9_",
    };

    /// <summary>
    /// POSIX regex (grep/sed; basic unless extended) to .NET: [[:class:]] brackets, \&lt; \&gt; word edges, and in basic
    /// mode \( \) \{ \} \| \+ \? are the operators while bare ( ) { } | + ? are literals.
    /// </summary>
    internal static string PosixToNet(string pattern, bool extended, bool bracketEscapes = false)
    {
        var sb = new StringBuilder();
        int n = pattern.Length;
        char? last = null; // last character emitted outside brackets, to spot a leading *
        var groups = new Stack<int>(); // where each open group starts in sb
        int atom = 0;                  // where the last atom starts in sb
        string? quantifier = null;     // the quantifier on the last atom, as emitted
        int wraps = 0;                 // groups wrapped around the last atom
        void Atom(string text) { atom = sb.Length; quantifier = null; wraps = 0; sb.Append(text); }
        void Quantifier(string text)
        {
            if (quantifier is [var q] && text is [var t])
            {
                // a run of * + ? is one quantifier: ++ is +, ?? is ?, any other mix is *; linear however long the run
                sb[^1] = q == t ? q : '*';
                quantifier = sb[^1].ToString();
                return;
            }
            if (quantifier != null)
            {
                // .NET rejects a{2}{3}; POSIX applies them in turn. Each wrap copies the pattern, so keep it to a few.
                if (++wraps > 32) throw new ShellException("Regular expression too big", 2);
                sb.Insert(atom, "(?:").Append(')');
            }
            quantifier = text;
            sb.Append(text);
        }
        for (int i = 0; i < n; i++)
        {
            char c = pattern[i];
            if (c == '[')
            {
                int j = i + 1;
                var body = new StringBuilder();
                if (j < n && pattern[j] == '^') { body.Append('^'); j++; }
                if (j < n && pattern[j] == ']') { body.Append(@"\]"); j++; }
                while (j < n && pattern[j] != ']')
                {
                    if (pattern[j] == '[' && j + 1 < n && pattern[j + 1] is ':' or '.' or '=')
                    {
                        int close = pattern.IndexOf(pattern[j + 1] + "]", j + 2, StringComparison.Ordinal);
                        if (close >= 0)
                        {
                            var name = pattern[(j + 2)..close];
                            if (pattern[j + 1] == ':')
                                body.Append(PosixClasses.TryGetValue(name, out var cls) ? cls : throw new ShellException($"invalid character class '{name}'", 2));
                            else body.Append(Regex.Escape(name));
                            j = close + 2;
                            continue;
                        }
                    }
                    if (bracketEscapes && pattern[j] == '\\' && j + 1 < n) { body.Append(pattern, j, 2); j += 2; continue; } // awk: \/ \] \. are escapes too
                    if (pattern[j] is '\\' or '[') body.Append('\\'); // backslash is literal in POSIX brackets
                    body.Append(pattern[j]);
                    j++;
                }
                if (j >= n) { Atom(@"\["); last = '['; continue; }
                Atom("[" + body + "]");
                last = ']';
                i = j;
                continue;
            }
            bool escaped = c == '\\' && i + 1 < n;
            if (escaped) c = pattern[++i];
            bool op = escaped != extended && "(){}|+?".Contains(c); // operators: bare in ERE, escaped in BRE
            bool leading = last == null || last is '^' or '(' or '|'; // a quantifier here is a literal
            if (escaped && c is '<' or '>') Atom(@"\b");
            else if (op && c == '(') { groups.Push(sb.Length); sb.Append('('); }
            else if (op && c == ')' && groups.Count > 0) { atom = groups.Pop(); quantifier = null; wraps = 0; sb.Append(')'); }
            else if (op && c == '|') sb.Append('|');
            else if ((c == '*' && !escaped || op && c is '+' or '?') && !leading) Quantifier(c.ToString());
            else if (op && c == '{' && !leading && Interval(pattern, i + 1, extended) is { } end)
            {
                Quantifier("{" + (pattern[i + 1] == ',' ? "0" : "") + pattern[(i + 1)..end] + "}");
                i = end + (extended ? 0 : 1);
            }
            else if (escaped && !"(){}|+?*".Contains(c)) Atom("\\" + c); // \. \w \1 ...
            else Atom("(){}|+?*".Contains(c) ? "\\" + c : c.ToString()); // a literal, or . ^ $ as they are
            last = op && c is '(' or '|' ? c : escaped || c is '(' or '|' ? 'x' : c;
        }
        return sb.ToString();
    }

    /// <summary>The end of an interval whose text starts at <paramref name="i"/> (after the "{"): the index of its "}" (of
    /// "\}" in basic mode), or null when the text is not m, m, m,n or ,n.</summary>
    static int? Interval(string p, int i, bool extended)
    {
        int j = i;
        while (j < p.Length && (char.IsAsciiDigit(p[j]) || p[j] == ',')) j++;
        var body = p[i..j];
        if (body.Length == 0 || body == "," || body.Count(ch => ch == ',') > 1) return null;
        if (extended) return j < p.Length && p[j] == '}' ? j : null;
        return j + 1 < p.Length && p[j] == '\\' && p[j + 1] == '}' ? j : null;
    }

    internal const string ClassSyntax = "character class syntax is [[:space:]], not [:space:]";

    /// <summary>
    /// The error GNU grep/sed report for a malformed POSIX regex (basic unless extended), or null when it is valid.
    /// grep is lenient where sed is not: in grep -E a stray ")" and a "{" that does not start a valid interval are literals.
    /// </summary>
    internal static string? PosixRegexError(string p, bool extended, bool grep)
    {
        const string Content = @"Invalid content of \{\}", Unmatched = "Unmatched [, [^, [:, [., or [=";
        int n = p.Length, open = 0, closed = 0;
        bool atStart = true; // an interval here has nothing to repeat
        for (int i = 0; i < n;)
        {
            char c = p[i];
            if (c == '[')
            {
                int j = i + 1;
                if (j < n && p[j] == '^') j++;
                if (j >= n) return "Invalid regular expression";
                int start = j;
                if (p[j] == ']') j++;
                char? low = null; // the character a following '-' starts a range from
                while (j < n && p[j] != ']')
                {
                    if (p[j] == '[' && j + 1 < n && p[j + 1] is ':' or '.' or '=')
                    {
                        char kind = p[j + 1];
                        int close = p.IndexOf(kind + "]", j + 2, StringComparison.Ordinal);
                        if (close < 0) return Unmatched;
                        var name = p[(j + 2)..close];
                        if (kind == ':' && !PosixClasses.ContainsKey(name)) return "Invalid character class name";
                        if (kind == '.' && name.Length != 1) return "Invalid collation character";
                        low = kind == '.' ? name[0] : null;
                        j = close + 2;
                        continue;
                    }
                    if (p[j] == '-' && low != null && j + 1 < n && p[j + 1] != ']')
                    {
                        if (p[j + 1] < low) return "Invalid range end";
                        low = null;
                        j += 2;
                        continue;
                    }
                    low = p[j++];
                }
                if (j >= n) return Unmatched;
                if (j - start >= 2 && p[start] == ':' && p[j - 1] == ':') return ClassSyntax;
                i = j + 1;
                atStart = false;
                continue;
            }
            bool escaped = c == '\\';
            if (escaped)
            {
                if (i + 1 >= n) return "Trailing backslash";
                c = p[++i];
            }
            i++;
            if (escaped && c is >= '1' and <= '9')
            {
                if (c - '0' > closed) return "Invalid back reference";
            }
            else if (escaped != extended && c == '(') { open++; atStart = true; continue; }
            else if (escaped != extended && c == ')')
            {
                if (open == 0) { if (extended && grep) { atStart = false; continue; } return @"Unmatched ) or \)"; }
                open--;
                closed++;
            }
            else if (escaped != extended && c == '|') { atStart = true; continue; }
            else if (escaped != extended && c == '{' && !atStart)
            {
                // {m} {m,} {m,n} {,n}; in grep -E anything else that is not obviously an interval is a literal "{"
                bool lenient = extended && grep;
                string end = extended ? "}" : @"\}";
                int Num(ref int k)
                {
                    int value = -1;
                    for (; k < n && p[k] != ',' && string.CompareOrdinal(p, k, end, 0, end.Length) != 0; k++)
                        value = value == -2 || !char.IsAsciiDigit(p[k]) ? -2 : Math.Min(100000, Math.Max(value, 0) * 10 + (p[k] - '0'));
                    return value;
                }
                int k = i, lo = Num(ref k), hi;
                if (k >= n) { if (lenient) continue; return @"Unmatched \{"; }
                if (lo == -2) { if (lenient) continue; return Content; }
                if (p[k] == ',')
                {
                    k++;
                    hi = Num(ref k);
                    if (k >= n) { if (lenient) continue; return @"Unmatched \{"; }
                    if (hi == -2) { if (lenient) continue; return Content; }
                    if (p[k] == ',') return Content;
                    if (lo < 0) lo = 0;
                }
                else if (lo < 0) return Content;
                else hi = lo;
                if (hi >= 0 && lo > hi) return Content;
                if (Math.Max(lo, hi) > 32767) return "Regular expression too big";
                i = k + end.Length;
            }
            atStart = false;
        }
        return open > 0 ? @"Unmatched ( or \(" : null;
    }

    /// <summary>Splits xargs input like GNU xargs: blanks separate items; quotes and backslashes protect them.</summary>
    static List<string> XargsWords(string text)
    {
        var items = new List<string>();
        var cur = new StringBuilder();
        bool inWord = false;
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (c is ' ' or '\t' or '\n')
            {
                if (inWord) { items.Add(cur.ToString()); cur.Clear(); inWord = false; }
                i++;
            }
            else if (c is '\'' or '"')
            {
                int j = text.IndexOf(c, i + 1);
                if (j < 0 || text.IndexOf('\n', i + 1, j - i - 1) >= 0)
                    throw new ShellException(CommandSpec.Message("xargs", "unmatchedQuote", ("kind", c == '\'' ? "single" : "double")));
                cur.Append(text, i + 1, j - i - 1);
                inWord = true;
                i = j + 1;
            }
            else if (c == '\\' && i + 1 < text.Length) { cur.Append(text[i + 1]); inWord = true; i += 2; }
            else { cur.Append(c); inWord = true; i++; }
        }
        if (inWord) items.Add(cur.ToString());
        return items;
    }

    static int CountArg(string cmd, string spec, string what)
    {
        var digits = spec.TrimStart('+', '-');
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) && int.TryParse(digits, out var n) ? n
            : throw new ShellException(CommandSpec.Message(cmd, "invalidCount", ("what", what), ("value", spec)));
    }

    static Regex BuildRegex(List<string> patterns, bool extended, bool fixedString, bool ignoreCase, bool word = false, bool line = false, bool perl = false)
    {
        // .NET has no \K ("keep"): a leading X\KY means the same as (?<=X)Y
        static string Keep(string p) => p.IndexOf(@"\K", StringComparison.Ordinal) is var k and > 0 ? $"(?<={p[..k]}){p[(k + 2)..]}" : p;
        // GNU reports every bad pattern, one line each; the shell adds the first "grep: "
        var errors = fixedString || perl ? [] : patterns.Select(p => PosixRegexError(p, extended, grep: true)).OfType<string>().ToList();
        if (errors.Count > 0) throw new ShellException(string.Join("\ngrep: ", errors), 2);
        var parts = patterns.Select(p => fixedString ? Regex.Escape(p) : perl ? Keep(p) : PosixToNet(p, extended)).ToList();
        var re = parts.Count == 1 ? parts[0] : string.Join("|", parts.Select(p => $"(?:{p})"));
        if (word) re = $@"\b(?:{re})\b";
        if (line) re = $"^(?:{re})$";
        try { return new Regex(re, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, Shell.RegexTimeout); }
        catch (ArgumentException e) { throw new ShellException(CommandSpec.Message("grep", "invalidRegex", ("detail", e.Message)), 2); }
    }

    static (HashSet<int> Wanted, int OpenFrom) ParseList(string spec)
    {
        // "1,3-5,7-" -> 1-based indexes {1,3,4,5} plus "everything from 7"
        var result = new HashSet<int>();
        int openFrom = int.MaxValue;
        foreach (var part in spec.Split(','))
        {
            int dash = part.IndexOf('-');
            string a = dash < 0 ? part : part[..dash], b = dash < 0 ? "" : part[(dash + 1)..];
            if (!a.All(char.IsAsciiDigit) || !b.All(char.IsAsciiDigit) || (a.Length == 0 && b.Length == 0) || (a.Length > 0 && int.Parse(a) == 0))
                throw new ShellException(a == "0" ? CommandSpec.Message("cut", "fieldsFromOne") : CommandSpec.Message("cut", "invalidFieldValue", ("part", part)));
            if (dash < 0) result.Add(int.Parse(a));
            else if (b.Length == 0) openFrom = Math.Min(openFrom, int.Parse(a));
            else for (int k = a.Length > 0 ? int.Parse(a) : 1, to = int.Parse(b); k <= to; k++) result.Add(k);
        }
        return (result, openFrom);
    }

    static void RegisterTextCommands(Shell shell)
    {
        shell.Register("grep", ctx =>
        {
            // --include/--exclude/--exclude-dir and -e/-f may repeat, so pull them out before getopt-style parsing
            var include = new List<Regex>(); var exclude = new List<Regex>(); var excludeDir = new List<Regex>();
            var rest = new List<string>();
            var patterns = new List<string>();
            var args = ctx.Args;
            for (int i = 0; i < args.Count;)
            {
                var arg = args[i++];
                if (arg is "--include" or "--exclude" or "--exclude-dir" && i < args.Count) arg += "=" + args[i++];
                if (arg.StartsWith("--include=")) { include.Add(Glob.ToRegex(arg[10..])); continue; }
                if (arg.StartsWith("--exclude=")) { exclude.Add(Glob.ToRegex(arg[10..])); continue; }
                if (arg.StartsWith("--exclude-dir=")) { excludeDir.Add(Glob.ToRegex(arg[14..])); continue; }
                char kind;
                string value;
                var m = arg.StartsWith("--") ? Match.Empty : Regex.Match(arg, "^-([a-zA-Z]*?)([ef])(.*)$", RegexOptions.Singleline);
                var lng = Regex.Match(arg, "^--(regexp|file)(?:=(.*))?$", RegexOptions.Singleline);
                if (m.Success && m.Groups[1].Value.All(ch => "invcrRlLEFwxohHqsIaZP".Contains(ch)))
                {
                    kind = m.Groups[2].Value[0];
                    value = m.Groups[3].Value;
                    if (m.Groups[1].Length > 0) rest.Add("-" + m.Groups[1].Value);
                    if (value.Length == 0) value = i < args.Count ? args[i++] : throw new UsageException(CommandSpec.Format(CommandSpec.Getopt.MissingShortArgument, ("option", kind.ToString())));
                }
                else if (lng.Success)
                {
                    kind = lng.Groups[1].Value[0];
                    value = lng.Groups[2].Success ? lng.Groups[2].Value : i < args.Count ? args[i++] : "";
                }
                else { rest.Add(arg); continue; }
                if (kind == 'e') patterns.Add(value);
                else patterns.AddRange(SplitLines(value == "-" ? ctx.StdinText : ctx.Fs.ReadAllText(ctx.ResolvePath(value))));
            }
            var a = Args.Parse(rest, CommandSpec.Of("grep"));
            var positional = a.Positional.ToList();
            if (patterns.Count == 0)
            {
                if (positional.Count == 0) throw new UsageException("");
                patterns.Add(positional[0]);
                positional.RemoveAt(0);
            }
            patterns = patterns.SelectMany(p => p.Length > 0 ? p.Split('\n') : [p]).ToList(); // a pattern with newlines is several patterns

            bool recursive = a.Has('r') || a.Has('R');
            bool implicitDot = recursive && positional.Count == 0;
            if (implicitDot) positional.Add(".");
            if (a.Has('E') && !a.Has('F') && !a.Has('P'))
                foreach (var p in patterns)
                    if (Regex.Match(p, @"^[*+?]|(?<!\\)[(|]([*+?])") is { Success: true } lead)
                        ctx.Stderr.WriteLine("grep: " + CommandSpec.Message("grep", "leadingRepetition", ("char", lead.Value[^1].ToString())));
            var re = BuildRegex(patterns, a.Has('E'), a.Has('F'), a.Has('i'), a.Has('w'), a.Has('x'), a.Has('P'));
            int after = int.Parse(a.Value('A') ?? a.Value('C') ?? "0"), before = int.Parse(a.Value('B') ?? a.Value('C') ?? "0");
            int maxCount = a.Value('m') is { } mc ? int.Parse(mc) : int.MaxValue;

            // expand directories for -r
            var files = new List<string>();
            int code = 1;
            bool error = false, walked = false;
            foreach (var f in positional)
            {
                var abs = ctx.ResolvePath(f);
                if (!ctx.Fs.IsDirectory(abs)) { files.Add(f); continue; }
                walked = true;
                var above = new HashSet<string>(StringComparer.Ordinal) { ctx.Fs.RealPath(abs, out _) ?? abs }; // the directories being walked
                if (!recursive) { if (!a.Has('s')) ctx.Stderr.WriteLine("grep: " + CommandSpec.Message("grep", "openFailed", ("file", f), ("errno", "Is a directory"))); error = true; continue; }
                void Walk(string dirAbs, string display)
                {
                    IReadOnlyList<string> names;
                    try { names = ctx.Fs.List(dirAbs); }
                    catch (ShellException) { if (!a.Has('s')) ctx.Stderr.WriteLine("grep: " + CommandSpec.Message("grep", "openFailed", ("file", display), ("errno", "Permission denied"))); error = true; return; }
                    foreach (var name in names)
                    {
                        ctx.CancellationToken.ThrowIfCancellationRequested(); // -R can make the walk far bigger than the tree: the call's time limit
                        var child = VirtualFileSystem.Normalize(dirAbs, name);
                        var childDisplay = display == "." && implicitDot ? name : display.TrimEnd('/') + "/" + name;
                        if (!a.Has('R') && ctx.Fs.IsSymlink(child)) continue; // -r skips links it meets; -R follows them
                        if (ctx.Fs.IsDirectory(child))
                        {
                            if (excludeDir.Any(r => r.IsMatch(name))) continue;
                            if (ctx.Fs.RealPath(child, out _) is not { } real || !above.Add(real)) { if (!a.Has('s')) ctx.Stderr.WriteLine($"grep: {childDisplay}: warning: recursive directory loop"); continue; }
                            Walk(child, childDisplay);
                            above.Remove(real);
                        }
                        else if ((include.Count == 0 || include.Any(r => r.IsMatch(name))) && !exclude.Any(r => r.IsMatch(name))) files.Add(childDisplay);
                    }
                }
                Walk(abs, f);
            }
            if (positional.Count > 0 && files.Count == 0) return error ? 2 : 1;

            bool showName = !a.Has('h') && (a.Has('H') || files.Count > 1 || walked);
            foreach (var (name, text) in Inputs(ctx, files, () => error = true, quiet: a.Has('s')))
            {
                bool binary = text.Contains('\0') && !a.Has('a'); // GNU grep's test: a NUL byte
                if (binary && a.Has('I')) continue;
                var lines = SplitLines(text);
                var hits = new List<int>();
                for (int i = 0; i < lines.Count && hits.Count < maxCount; i++)
                {
                    ctx.CancellationToken.ThrowIfCancellationRequested(); // one slow match per line can't add up past the time limit
                    if (re.IsMatch(lines[i]) != a.Has('v')) hits.Add(i);
                }
                if (hits.Count > 0) code = 0;
                if (a.Has('q')) { if (code == 0) return 0; continue; }
                var prefix = showName ? name : null;
                if (a.Has('l')) { if (hits.Count > 0) ctx.Stdout.WriteLine(name); continue; }
                if (a.Has('L')) { if (hits.Count == 0) ctx.Stdout.WriteLine(name); continue; }
                if (a.Has('c')) { ctx.Stdout.WriteLine(prefix != null ? $"{prefix}:{hits.Count}" : hits.Count.ToString()); continue; }
                if (binary) { if (hits.Count > 0) ctx.Stdout.WriteLine("grep: " + CommandSpec.Message("grep", "binaryFileMatches", ("file", name))); continue; }

                int lastPrinted = -1;
                var hitSet = hits.ToHashSet();
                foreach (var h in hits)
                {
                    int from = Math.Max(Math.Max(0, h - before), lastPrinted + 1), to = Math.Min(lines.Count - 1, h + after);
                    if ((before > 0 || after > 0) && lastPrinted >= 0 && from > lastPrinted + 1) ctx.Stdout.WriteLine("--");
                    for (int i = from; i <= to; i++)
                    {
                        char sep = hitSet.Contains(i) ? ':' : '-';
                        var head = (prefix != null ? prefix + sep : "") + (a.Has('n') ? $"{i + 1}{sep}" : "");
                        if (a.Has('o') && hitSet.Contains(i)) foreach (Match mt in re.Matches(lines[i])) { if (mt.Length > 0) ctx.Stdout.WriteLine(head + mt.Value); }
                        else if (!a.Has('o')) ctx.Stdout.WriteLine(head + lines[i]);
                        lastPrinted = i;
                    }
                }
            }
            return error && !(a.Has('q') && code == 0) ? 2 : code;
        });

        // Ubuntu's egrep and fgrep are scripts that run `grep -E` / `grep -F`, so messages say "grep:"
        foreach (var (alias, flag) in new[] { ("egrep", "-E"), ("fgrep", "-F") })
            shell.Register(alias, ctx => shell.Commands["grep"].Handler(
                new CommandContext(ctx.Shell, "grep", [flag, .. ctx.Args], ctx.Io.Stdin, ctx.Stdout, ctx.Stderr, ctx.CancellationToken) { Fds = ctx.Fds }));

        CommandHandler headTail = ctx =>
        {
            bool head = ctx.Name == "head";
            // shortcut: tail -f/-F print what is there and return instead of following (nothing else writes the file)
            var a = Args.Parse(Args.NumericShorthand(ctx.Args), CommandSpec.Of(ctx.Name));
            if (a.Value('n') is { } lines0) CountArg(ctx.Name, lines0, "lines"); // a bad count is reported before any file is opened
            if (a.Value('c') is { } bytes0) CountArg(ctx.Name, bytes0, "bytes");
            int code = 0;
            var inputs = InputBytes(ctx, a.Positional, () => code = 1).ToList();
            for (int k = 0; k < inputs.Count; k++)
            {
                var (name, data) = inputs[k];
                if ((inputs.Count > 1 && !a.Has('q')) || a.Has('v')) ctx.Stdout.WriteLine((k > 0 ? "\n" : "") + $"==> {name} <==");
                if (a.Value('c') is { } bytes) // byte counts
                {
                    int n = CountArg(ctx.Name, bytes, "bytes");
                    byte[] chunk = head
                        ? bytes.StartsWith('-') ? data[..Math.Max(0, data.Length - n)] : data[..Math.Min(n, data.Length)]
                        : bytes.StartsWith('+') ? data[Math.Min(data.Length, Math.Max(0, n - 1))..] : n > 0 ? data[Math.Max(0, data.Length - n)..] : [];
                    ctx.Stdout.Write(ByteText.Decode(chunk));
                    continue;
                }
                var text = ByteText.Decode(data);
                var spec = a.Value('n') ?? "10";
                int count = CountArg(ctx.Name, spec, "lines");
                var lines = SplitLines(text);
                List<string> pick;
                if (head) pick = spec.StartsWith('-') ? lines.Take(Math.Max(0, lines.Count - count)).ToList() : lines.Take(count).ToList();
                else pick = spec.StartsWith('+') ? lines.Skip(Math.Max(0, count - 1)).ToList() : lines.Skip(Math.Max(0, lines.Count - count)).ToList();
                var output = string.Concat(pick.Select(l => l + "\n"));
                // the file's last line had no newline, and neither does the output
                if (pick.Count > 0 && !text.EndsWith('\n') && (!head || pick.Count == lines.Count)) output = output[..^1];
                ctx.Stdout.Write(output);
            }
            return Task.FromResult(code);
        };
        shell.Register("head", headTail);
        shell.Register("tail", headTail);

        shell.Register("wc", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("wc"));
            bool all = !a.Has('l') && !a.Has('w') && !a.Has('c') && !a.Has('m');
            bool[] selected = [all || a.Has('l'), all || a.Has('w'), all || a.Has('c') || a.Has('m')];
            int code = 0;
            long tl = 0, tw = 0, tc = 0;
            var inputs = InputBytes(ctx, a.Positional, () => code = 1).ToList();
            // GNU pads every column to one width: none for a single count of a single file, 7 for stdin, else the total size
            int width = selected.Count(x => x) == 1 && a.Positional.Count <= 1 ? 1
                : inputs.Any(i => i.Name == "(standard input)") ? 7
                : inputs.Sum(i => (long)i.Text.Length).ToString(CultureInfo.InvariantCulture).Length;
            string Row(long l, long w, long c, string? name)
            {
                var parts = new List<string>();
                long[] values = [l, w, c];
                for (int k = 0; k < 3; k++) if (selected[k]) parts.Add(values[k].ToString(CultureInfo.InvariantCulture).PadLeft(width));
                if (name != null) parts.Add(name);
                return string.Join(' ', parts);
            }
            foreach (var (name, data) in inputs)
            {
                var text = Encoding.UTF8.GetString(data);
                long l = text.Count(ch => ch == '\n'), w = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                long c = a.Has('m') ? text.Length : data.Length;
                tl += l; tw += w; tc += c;
                ctx.Stdout.WriteLine(Row(l, w, c, a.Positional.Count > 0 ? name : null));
            }
            if (a.Positional.Count > 1) ctx.Stdout.WriteLine(Row(tl, tw, tc, "total"));
            return code;
        });

        shell.Register("sort", ctx => Sort.Run(ctx));

        shell.Register("uniq", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("uniq"));
            int code = 0;
            var text = string.Concat(Inputs(ctx, a.Positional.Take(1).ToList(), () => code = 1).Select(i => i.Text));
            if (code != 0) return code;
            var cmp = a.Has('i') ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var groups = new List<(string Line, int Count)>();
            foreach (var line in SplitLines(text))
                if (groups.Count > 0 && cmp.Equals(groups[^1].Line, line)) groups[^1] = (groups[^1].Line, groups[^1].Count + 1);
                else groups.Add((line, 1));
            foreach (var (line, count) in groups)
            {
                if (a.Has('d') && count < 2 || a.Has('u') && count > 1) continue;
                ctx.Stdout.WriteLine(a.Has('c') ? $"{count,7} {line}" : line);
            }
            return 0;
        });

        shell.Register("cut", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("cut"));
            var delim = a.Value('d') ?? "\t";
            if (delim.Length != 1) throw new ShellException(CommandSpec.Message("cut", "delimiterNotSingle"));
            var chars = a.Value('c') ?? a.Value('b');
            var spec = chars ?? a.Value('f') ?? throw new UsageException(CommandSpec.Message("cut", "listRequired"));
            var (wantedSet, openFrom) = ParseList(spec);
            bool complement = a.Has('z');
            var outDelim = a.Value('\u0002'); // --output-delimiter (long only)
            IEnumerable<string> Pick(IReadOnlyList<string> items) =>
                items.Where((_, i) => (i + 1 >= openFrom || wantedSet.Contains(i + 1)) != complement);
            int code = 0;
            var sb = new StringBuilder();
            foreach (var (_, text) in Inputs(ctx, a.Positional, () => code = 1))
                foreach (var line in SplitLines(text))
                {
                    if (chars != null) { sb.Append(string.Join(outDelim ?? "", Pick(line.Select(ch => ch.ToString()).ToList()))).Append('\n'); continue; }
                    if (!line.Contains(delim[0])) { if (!a.Has('s')) sb.Append(line).Append('\n'); continue; }
                    sb.Append(string.Join(outDelim ?? delim, Pick(line.Split(delim[0])))).Append('\n');
                }
            ctx.Stdout.Write(sb.ToString());
            return code;
        });

        shell.Register("tr", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("tr"));
            if (a.Positional.Count == 0) throw new UsageException(CommandSpec.Message("tr", "missingOperand"));
            if (a.Positional.Count > 2) throw new UsageException(CommandSpec.Message("tr", "extraOperand", ("operand", a.Positional[2])));
            var set1 = ExpandSet(a.Positional[0]);
            var set2 = a.Positional.Count > 1 ? ExpandSet(a.Positional[1]) : "";
            bool complement = a.Has('c') || a.Has('C');
            bool InSet1(char ch) => set1.Contains(ch) != complement;
            bool translate = set2.Length > 0 && !a.Has('d');
            Func<char, bool> squeeze = set2.Length > 0 ? set2.Contains : InSet1; // -s applies to the last set given
            var sb = new StringBuilder();
            foreach (var ch in ctx.StdinText)
            {
                bool hit = InSet1(ch);
                if (a.Has('d') && hit) continue;
                var outCh = translate && hit ? (complement ? set2[^1] : set2[Math.Min(set1.IndexOf(ch), set2.Length - 1)]) : ch;
                if (a.Has('s') && sb.Length > 0 && sb[^1] == outCh && squeeze(outCh)) continue;
                sb.Append(outCh);
            }
            ctx.Stdout.Write(sb.ToString());
            return 0;
        });

        shell.Register("tee", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("tee"));
            int code = 0;
            foreach (var f in a.Positional)
            {
                var path = ctx.ResolvePath(f);
                if (path is "/dev/stdout" or "/dev/stderr") { (path == "/dev/stderr" ? ctx.Stderr : ctx.Stdout).Write(ctx.StdinText); continue; }
                try
                {
                    if (a.Has('a')) ctx.Fs.AppendAllText(path, ctx.StdinText);
                    else ctx.Fs.WriteAllText(path, ctx.StdinText);
                }
                catch (ShellException e)
                {
                    ctx.Stderr.WriteLine("tee: " + CommandSpec.Message("tee", "writeFailed", ("file", f), ("reason", e.Message[(e.Message.LastIndexOf(':') + 2)..])));
                    code = 1;
                }
            }
            ctx.Stdout.Write(ctx.StdinText);
            return code;
        });

        shell.Register("xargs", async ctx =>
        {
            // -I, -n, ... take a value that may be a separate argument
            var opts = new List<string>();
            int i = 0;
            for (; i < ctx.Args.Count && ctx.Args[i].StartsWith('-') && ctx.Args[i] != "-"; i++)
            {
                opts.Add(ctx.Args[i]);
                if (ctx.Args[i] is "-I" or "-n" or "-d" or "-L" or "-P" or "-s" or "-E" or "-a" && i + 1 < ctx.Args.Count) opts.Add(ctx.Args[++i]);
            }
            var a = Args.Parse(opts, CommandSpec.Of("xargs"));
            var command = ctx.Args.Skip(i).ToList();
            if (command.Count == 0) command.Add("echo");
            var input = a.Value('a') is { } file ? ctx.Fs.ReadAllText(ctx.ResolvePath(file)) : ctx.StdinText;
            // shortcut: -P is accepted but commands run one after another (output order stays deterministic).
            var replace = a.Value('I');
            List<List<string>> groups;
            if (replace != null || a.Value('L') != null)
            {
                var lines = SplitLines(input).Select(l => replace != null ? l.TrimStart(' ', '\t') : l).ToList();
                if (replace != null) groups = lines.Where(l => l.Length > 0).Select(l => new List<string> { l }).ToList();
                else
                {
                    int per = int.Parse(a.Value('L')!);
                    if (per < 1) throw new ShellException(CommandSpec.Message("xargs", "invalidValue", ("value", a.Value('L')!), ("option", "L")));
                    var words = lines.Select(XargsWords).Where(w => w.Count > 0).ToList();
                    groups = words.Chunk(per).Select(c => c.SelectMany(w => w).ToList()).ToList();
                }
            }
            else
            {
                List<string> items;
                if (a.Has('0')) items = input.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
                else if (a.Value('d') is { } d)
                {
                    var delim = Unescape(d);
                    items = input.Split(delim.Length > 0 ? delim[0] : '\n').ToList();
                    if (items.Count > 0 && items[^1] is "" or "\n") items.RemoveAt(items.Count - 1);
                }
                else items = XargsWords(input);
                int per = a.Value('n') is { } n ? int.Parse(n) : Math.Max(1, items.Count);
                if (per < 1) throw new ShellException(CommandSpec.Message("xargs", "invalidValue", ("value", a.Value('n')!), ("option", "n")));
                groups = items.Chunk(per).Select(c => c.ToList()).ToList();
            }
            if (groups.Count == 0)
            {
                if (a.Has('r') || replace != null) return 0;
                groups.Add([]); // like GNU xargs, run the command once even with no input
            }
            int code = 0;
            foreach (var group in groups)
            {
                var argv = replace != null ? command.Select(c => c.Replace(replace, group[0])).ToList() : [.. command, .. group];
                if (a.Has('t')) ctx.Stderr.WriteLine(string.Join(' ', argv));
                var rc = await ctx.InvokeAsync(argv);
                if (rc == 127) return 127;
                if (rc == 255) { ctx.Stderr.WriteLine("xargs: " + CommandSpec.Message("xargs", "exitedWith255", ("command", argv[0]))); return 124; }
                if (rc != 0) code = 123;
            }
            return code;
        });

        shell.Register("sed", ctx => Sed.Run(ctx));
    }

    static readonly Dictionary<string, string> TrClasses = new()
    {
        ["upper"] = "A-Z", ["lower"] = "a-z", ["digit"] = "0-9", ["alpha"] = "a-zA-Z", ["alnum"] = "a-zA-Z0-9", ["space"] = " \t\n\r\f\v",
        ["blank"] = " \t", ["xdigit"] = "0-9A-Fa-f", ["punct"] = "!-/:-@[-`{-~", ["cntrl"] = "\u0000-\u001f\u007f", ["print"] = " -~", ["graph"] = "!-~",
    };

    static string ExpandSet(string spec)
    {
        spec = Regex.Replace(spec, @"\[:(\w+):\]", m => TrClasses.GetValueOrDefault(m.Groups[1].Value, m.Value));
        spec = Unescape(spec);
        var sb = new StringBuilder();
        for (int i = 0; i < spec.Length; i++)
        {
            if (i + 2 < spec.Length && spec[i + 1] == '-' && spec[i] <= spec[i + 2])
            {
                for (char c = spec[i]; c <= spec[i + 2]; c++) sb.Append(c);
                i += 2;
            }
            else sb.Append(spec[i]);
        }
        return sb.ToString();
    }
}

internal static class Sort
{
    const string Ordering = "bdfghnrMV";
    const string HumanSuffix = "KMGTPEZY";
    static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    sealed record Key(int StartField, int StartChar, int? EndField, int EndChar, string Opts);

    static double Number(string s, char mode)
    {
        s = s.Trim();
        var m = Regex.Match(s, mode == 'g' ? @"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?" : @"^-?(\d+\.?\d*|\.\d+)");
        if (!m.Success) return mode == 'g' ? double.NegativeInfinity : 0;
        double v = double.Parse(m.Value, CultureInfo.InvariantCulture);
        if (mode == 'h' && m.Length < s.Length && HumanSuffix.IndexOf(char.ToUpperInvariant(s[m.Length])) is var k and >= 0) v *= Math.Pow(1024, k + 1);
        return v;
    }

    /// <summary>A comparable key value: a string, a double, or a version (list of number/text chunks).</summary>
    static IComparable Value(string text, string opts)
    {
        if (opts.Contains('b')) text = text.TrimStart();
        if (opts.Contains('d')) text = string.Concat(text.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)));
        if (opts.Contains('f')) text = text.ToUpperInvariant();
        foreach (var mode in "nghV")
            if (opts.Contains(mode)) return mode == 'V' ? new Version(text) : Number(text, mode);
        if (opts.Contains('M'))
        {
            var t = text.Trim();
            return (double)(Array.IndexOf(Months, (t.Length >= 3 ? t[..3] : t).ToUpperInvariant()) + 1);
        }
        return new Ordinal(text);
    }

    sealed record Ordinal(string S) : IComparable
    {
        public int CompareTo(object? o) => string.CompareOrdinal(S, ((Ordinal)o!).S);
    }

    sealed class Version(string s) : IComparable
    {
        readonly List<(int Kind, System.Numerics.BigInteger Num, string Text)> _chunks = Regex.Matches(s, @"\d+|\D+")
            .Select(m => char.IsAsciiDigit(m.Value[0]) ? (0, System.Numerics.BigInteger.Parse(m.Value, CultureInfo.InvariantCulture), "") : (1, System.Numerics.BigInteger.Zero, m.Value)).ToList();

        public int CompareTo(object? o)
        {
            var other = ((Version)o!)._chunks;
            for (int i = 0; i < Math.Min(_chunks.Count, other.Count); i++)
            {
                var (a, b) = (_chunks[i], other[i]);
                int c = a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : a.Kind == 0 ? a.Num.CompareTo(b.Num) : string.CompareOrdinal(a.Text, b.Text);
                if (c != 0) return c;
            }
            return _chunks.Count.CompareTo(other.Count);
        }
    }

    public static int Run(CommandContext ctx)
    {
        var a = Args.Parse(ctx.Args, CommandSpec.Of("sort"));
        var args = ctx.Args;
        var specs = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is "-k" or "--key" && i + 1 < args.Count) specs.Add(args[++i]);
            else if (args[i].StartsWith("-k") && args[i].Length > 2) specs.Add(args[i][2..]);
            else if (args[i].StartsWith("--key=")) specs.Add(args[i][6..]);
        }
        string? sep = a.Value('t') is { } t ? Builtins.Unescape(t) : null;
        if (sep != null && sep.Length != 1) throw new ShellException(CommandSpec.Message("sort", "multiCharacterTab", ("value", a.Value('t')!)), 2);
        var globalOpts = string.Concat(Ordering.Where(a.Has));
        var keys = new List<Key>();
        foreach (var spec in specs)
        {
            var km = Regex.Match(spec, @"^(\d+)(?:\.(\d+))?([a-zA-Z]*)(?:,(\d+)(?:\.(\d+))?([a-zA-Z]*))?$");
            if (!km.Success || int.Parse(km.Groups[1].Value) == 0) throw new ShellException(CommandSpec.Message("sort", "invalidKey", ("value", spec)), 2);
            var opts = km.Groups[3].Value + km.Groups[6].Value;
            keys.Add(new Key(int.Parse(km.Groups[1].Value), km.Groups[2].Success ? int.Parse(km.Groups[2].Value) : 1,
                km.Groups[4].Success ? int.Parse(km.Groups[4].Value) : null, km.Groups[5].Success ? int.Parse(km.Groups[5].Value) : 0,
                opts.Any(Ordering.Contains) ? opts : globalOpts + opts));
        }
        if (keys.Count == 0) keys.Add(new Key(0, 1, null, 0, globalOpts));

        string Extract(string line, Key k)
        {
            if (k.StartField == 0) return line;
            var fields = sep != null ? line.Split(sep[0]) : line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (k.StartField > fields.Length) return "";
            int end = k.EndField is { } ef ? Math.Min(ef, fields.Length) : fields.Length;
            if (end < k.StartField) return "";
            var chosen = fields[(k.StartField - 1)..end];
            chosen[0] = k.StartChar - 1 < chosen[0].Length ? chosen[0][(k.StartChar - 1)..] : "";
            if (k.EndField is { } ef2 && k.EndChar > 0 && ef2 <= fields.Length)
            {
                int cut = k.EndChar - (chosen.Length == 1 ? k.StartChar - 1 : 0);
                chosen[^1] = chosen[^1][..Math.Clamp(cut, 0, chosen[^1].Length)];
            }
            return string.Join(sep ?? " ", chosen);
        }

        int Compare((string Line, IComparable[] Values) x, (string Line, IComparable[] Values) y)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                int c = x.Values[i].CompareTo(y.Values[i]);
                if (c != 0) return keys[i].Opts.Contains('r') ? -c : c;
            }
            if (a.Has('s') || a.Has('u')) return 0;
            int last = Math.Sign(string.CompareOrdinal(x.Line, y.Line));
            return a.Has('r') ? -last : last;
        }

        int code = 0;
        var named = new List<(string Name, string Text)>();
        foreach (var f in a.Positional.DefaultIfEmpty("-"))
        {
            named.AddRange(Builtins.Inputs(ctx, [f], () => code = 2));
            if (code != 0) return code; // sort stops at the first file it can't read
        }
        var items = named.SelectMany(i => Builtins.SplitLines(i.Text)).Select(l => (Line: l, Values: keys.Select(k => Value(Extract(l, k), k.Opts)).ToArray())).ToList();
        if (a.Has('c') || a.Has('C'))
        {
            for (int i = 1; i < items.Count; i++)
            {
                int c = Compare(items[i - 1], items[i]);
                if (c > 0 || (a.Has('u') && c == 0))
                {
                    if (a.Has('c')) ctx.Stderr.WriteLine("sort: " + CommandSpec.Message("sort", "disorder", ("file", a.Positional.Count > 0 ? named[0].Name : "-"), ("line", (i + 1).ToString()), ("text", items[i].Line)));
                    return 1;
                }
            }
            return 0;
        }
        var comparer = Comparer<(string Line, IComparable[] Values)>.Create(Compare);
        var sorted = items.OrderBy(x => x, comparer).ToList(); // OrderBy is stable, like Python's sort
        if (a.Has('u')) sorted = sorted.Where((x, i) => i == 0 || Compare(sorted[i - 1], x) != 0).ToList();
        var output = string.Concat(sorted.Select(x => x.Line + "\n"));
        if (a.Value('o') is { } outFile) ctx.Fs.WriteAllText(ctx.ResolvePath(outFile), output);
        else ctx.Stdout.Write(output);
        return code;
    }
}

internal static class Sed
{
    abstract record Addr;
    sealed record LineAddr(int Line) : Addr;
    sealed record LastAddr : Addr;
    sealed record RegexAddr(Regex Re) : Addr;
    sealed record StepAddr(int First, int Step) : Addr;
    sealed record ZeroAddr : Addr;          // 0,/re/: the range may end on the first line
    sealed record PlusAddr(int Count) : Addr; // addr,+N
    sealed record MultAddr(int Count) : Addr; // addr,~N

    abstract record ReplPart;
    sealed record LitPart(string Text) : ReplPart;
    sealed record GroupPart(int Group) : ReplPart;
    sealed record CasePart(char Op) : ReplPart;

    sealed class Cmd
    {
        public Addr? A1, A2;
        public bool Negate, InRange;
        public int RangeEnd;
        public char Op;
        public Regex? Re;
        public List<ReplPart> Repl = [];
        public bool Global, Print;
        public int Nth = 1;
        public string Text = "", Label = "";
        public int Target; // jump target for b/t/T and for skipping a { block
        public int Code;
        public Dictionary<char, char> Table = [];
    }

    // the help text (spec/commands/sed.json) is also printed after a usage error
    static string Usage => CommandSpec.Of("sed").Help!;

    public static int Run(CommandContext ctx)
    {
        // a bad command line: the complaint, then the whole usage text, on stderr
        int Bad(string message) { ctx.Stderr.Write((message.Length > 0 ? $"sed: {message}\n" : "") + Usage); return 1; }
        var scripts = new List<(string Text, string Where)>();
        int expressions = 0;
        void AddScript(char kind, string value)
        {
            if (kind == 'e') { scripts.Add((value, $"-e expression #{++expressions}")); return; }
            string text;
            try { text = value == "-" ? ctx.StdinText : ctx.Fs.ReadAllText(ctx.ResolvePath(value)); }
            catch (ShellException e) { throw new ShellException(CommandSpec.Message("sed", "cannotOpenScript", ("file", value), ("errno", e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..])), 4); }
            scripts.Add((text, $"file {value}"));
        }
        var files = new List<string>();
        bool quiet = false, extended = false, separate = false, inPlace = false;
        var suffix = "";
        var args = ctx.Args;
        for (int i = 0; i < args.Count;)
        {
            var arg = args[i++];
            if (arg == "--") { files.AddRange(args.Skip(i)); break; }
            if (arg.StartsWith("--"))
            {
                var name = arg[2..];
                string? value = null;
                if (name.IndexOf('=') is var eq and >= 0) { value = name[(eq + 1)..]; name = name[..eq]; }
                switch (name)
                {
                    case "quiet" or "silent": quiet = true; break;
                    case "regexp-extended": extended = true; break;
                    case "separate": separate = true; break;
                    case "in-place": inPlace = true; suffix = value ?? ""; break;
                    case "expression" or "file":
                        if (value == null && i >= args.Count) return Bad(CommandSpec.Format(CommandSpec.Getopt.MissingLongArgument, ("option", name)));
                        AddScript(name[0], value ?? args[i++]);
                        break;
                    case "posix" or "debug" or "sandbox" or "follow-symlinks" or "unbuffered": break;
                    case "help": ctx.Stdout.Write(Usage); return 0;
                    default: return Bad(CommandSpec.Format(CommandSpec.Getopt.UnrecognizedOption, ("option", arg)));
                }
                continue;
            }
            if (arg.Length < 2 || arg[0] != '-') { files.Add(arg); continue; }
            for (int k = 1; k < arg.Length; k++)
            {
                char f = arg[k];
                if (f == 'n') quiet = true;
                else if (f is 'E' or 'r') extended = true;
                else if (f == 's') separate = true;
                else if (f == 'u') { }
                else if (f == 'i') { inPlace = true; suffix = arg[(k + 1)..]; break; } // like GNU, -i takes the rest of the word as a backup suffix
                else if (f is 'e' or 'f')
                {
                    var value = arg[(k + 1)..];
                    if (value.Length == 0 && i >= args.Count) return Bad(CommandSpec.Format(CommandSpec.Getopt.MissingShortArgument, ("option", f.ToString())));
                    AddScript(f, value.Length > 0 ? value : args[i++]);
                    break;
                }
                else return Bad(CommandSpec.Format(CommandSpec.Getopt.InvalidOption, ("option", f.ToString())));
            }
        }
        if (scripts.Count == 0)
        {
            if (files.Count == 0) return Bad("");
            AddScript('e', files[0]);
            files.RemoveAt(0);
        }
        var cmds = Parse(scripts, extended);

        if (inPlace)
        {
            if (files.Count == 0) throw new ShellException(CommandSpec.Message("sed", "noInputFiles"), 1);
            int code = 0;
            foreach (var f in files)
            {
                var path = ctx.ResolvePath(f);
                string original;
                try { original = ctx.Fs.ReadAllText(path); }
                catch (ShellException) when (ctx.Fs.IsDirectory(path)) { ctx.Stderr.WriteLine("sed: " + CommandSpec.Message("sed", "notRegularFile", ("file", f))); code = 4; continue; }
                catch (ShellException e) { ctx.Stderr.WriteLine("sed: " + CommandSpec.Message("sed", "openFailed", ("file", f), ("errno", e.Message[(e.Message.LastIndexOf(':') + 2)..]))); code = 2; continue; }
                var output = new StringWriter();
                var quitCode = Execute(cmds, original, output, quiet, ctx.CancellationToken, ctx.Shell.Options.MaxStringBytes);
                if (suffix.Length > 0)
                    ctx.Fs.WriteAllText(ctx.ResolvePath(suffix.Contains('*') ? suffix.Replace("*", f[(f.LastIndexOf('/') + 1)..]) : f + suffix), original);
                if (ctx.Fs.IsSymlink(path)) ctx.Fs.Delete(path); // like GNU sed -i, the edited text replaces the link; its target stays as it was
                ctx.Fs.WriteAllText(path, output.ToString());
                if (quitCode is { } q) return q;
            }
            return code;
        }
        bool error = false;
        var texts = Builtins.Inputs(ctx, files, () => error = true).Select(i => i.Text).ToList();
        if (separate)
        {
            foreach (var t in texts)
                if (Execute(cmds, t, ctx.Stdout, quiet, ctx.CancellationToken, ctx.Shell.Options.MaxStringBytes) is { } q) return q;
        }
        else
        {
            var joined = string.Concat(texts.Select((t, k) => t.EndsWith('\n') || k == texts.Count - 1 ? t : t + "\n"));
            if (Execute(cmds, joined, ctx.Stdout, quiet, ctx.CancellationToken, ctx.Shell.Options.MaxStringBytes) is { } q) return q;
        }
        return files.Any(f => f != "-" && ctx.Fs.IsDirectory(ctx.ResolvePath(f))) ? 4 : error ? 2 : 0; // 4: a read error (a directory)
    }

    /// <summary>Parses a sed script into a flat command list; { } blocks and b/t/T labels become jump targets.</summary>
    /// <param name="parts">Each -e expression and -f file, with where GNU says an error is ("-e expression #2", "file x.sed").</param>
    static List<Cmd> Parse(List<(string Text, string Where)> parts, bool extended)
    {
        var script = string.Join("\n", parts.Select(p => p.Text));
        var starts = new int[parts.Count];
        for (int k = 1; k < parts.Count; k++) starts[k] = starts[k - 1] + parts[k - 1].Text.Length + 1;
        var cmds = new List<Cmd>();
        var labels = new Dictionary<string, int>();
        var blocks = new Stack<(int Cmd, int At)>();
        Regex? lastRe = null;
        int i = 0, n = script.Length;

        // GNU's place for an error: how many characters of the expression were read (or the line of the file) at pos
        ShellException Fail(string message, int pos, bool charZero = false)
        {
            int k = 0;
            for (int s = 1; s < starts.Length; s++) if (starts[s] < pos) k = s;
            pos = Math.Min(pos, starts[k] + parts[k].Text.Length);
            var where = parts[k].Where;
            var at = where.StartsWith("file ", StringComparison.Ordinal)
                ? $" line {1 + script.AsSpan(starts[k], pos - starts[k]).Count('\n')}"
                : $", char {(charZero ? 0 : pos - starts[k])}";
            return new ShellException($"{where}{at}: {message}");
        }

        void SkipBlanks() { while (i < n && script[i] is ' ' or '\t') i++; }

        // what: "regex" (of s), "addr" (an address regex), "replacement" or "y"; a regex's [brackets] may hold the delimiter
        string ReadDelimited(char delim, string what)
        {
            bool regex = what is "regex" or "addr";
            bool inBracket = false;
            var sb = new StringBuilder();
            while (i < n && (script[i] != delim || inBracket))
            {
                char ch = script[i];
                if (ch == '\\' && i + 1 < n)
                {
                    if (script[i + 1] == delim) sb.Append(delim);
                    else if (script[i + 1] == 'n' && regex) sb.Append(@"\n");
                    else if (inBracket) { sb.Append(ch); i++; continue; } // a backslash is literal in brackets
                    else sb.Append(script, i, 2);
                    i += 2;
                    continue;
                }
                if (ch == '\n') break; // a newline in the replacement needs a backslash before it
                if (regex && !inBracket && ch == '[')
                {
                    sb.Append(ch);
                    i++;
                    if (i < n && script[i] == '^') sb.Append(script[i++]);
                    if (i < n && script[i] == ']') sb.Append(script[i++]);
                    inBracket = true;
                    continue;
                }
                if (inBracket && ch == '[' && i + 1 < n && script[i + 1] is ':' or '.' or '=' && script.IndexOf(script[i + 1] + "]", i + 2, StringComparison.Ordinal) is var close and >= 0
                    && script.IndexOf('\n', i, close - i) < 0)
                {
                    sb.Append(script, i, close + 2 - i);
                    i = close + 2;
                    continue;
                }
                if (ch == ']') inBracket = false;
                sb.Append(ch);
                i++;
            }
            if (i >= n || script[i] != delim)
                throw Fail(what switch { "addr" => CommandSpec.Message("sed", "unterminatedAddress"), "y" => CommandSpec.Message("sed", "unterminatedY"), _ => CommandSpec.Message("sed", "unterminatedS") }, i);
            i++;
            return sb.ToString();
        }

        Regex CompileRegex(string text, RegexOptions options, int errorAt)
        {
            if (text.Length == 0) return lastRe ?? throw Fail(CommandSpec.Message("sed", "noPreviousRegex"), errorAt);
            if (Builtins.PosixRegexError(text, extended, grep: false) is { } error)
                throw error == Builtins.ClassSyntax ? new ShellException(error, 4) : Fail(error, errorAt);
            try { return lastRe = new Regex(Builtins.PosixToNet(text, extended), options, Shell.RegexTimeout); }
            catch (ArgumentException e) { throw Fail(CommandSpec.Message("sed", "invalidRegex", ("detail", e.Message)), errorAt); }
        }

        int ReadNumber()
        {
            int start = i;
            while (i < n && char.IsAsciiDigit(script[i])) i++;
            return int.Parse(script[start..i], CultureInfo.InvariantCulture);
        }

        Addr? ReadAddr(bool second)
        {
            if (i >= n) return null;
            char ch = script[i];
            if (second && ch is '+' or '~' && i + 1 < n && char.IsAsciiDigit(script[i + 1]))
            {
                i++;
                int count = ReadNumber();
                return ch == '+' ? new PlusAddr(count) : new MultAddr(count);
            }
            if (char.IsAsciiDigit(ch))
            {
                int num = ReadNumber();
                if (!second && i < n && script[i] == '~')
                {
                    i++;
                    return new StepAddr(num, i < n && char.IsAsciiDigit(script[i]) ? ReadNumber() : 0);
                }
                return num == 0 && !second ? new ZeroAddr() : new LineAddr(num);
            }
            if (ch == '$') { i++; return new LastAddr(); }
            if (ch == '/' || (ch == '\\' && i + 1 < n))
            {
                char delim = ch == '/' ? '/' : script[i + 1];
                i += ch == '/' ? 1 : 2;
                var text = ReadDelimited(delim, "addr");
                var options = RegexOptions.None;
                while (i < n && script[i] is 'I' or 'M') options |= script[i++] == 'I' ? RegexOptions.IgnoreCase : RegexOptions.Multiline;
                SkipBlanks();
                return new RegexAddr(CompileRegex(text, options, i));
            }
            return null;
        }

        string ReadLabel()
        {
            SkipBlanks();
            int start = i;
            while (i < n && script[i] is not (';' or '\n' or '}')) i++;
            return script[start..i].Trim();
        }

        // text for a/i/c: `a text` or `a\` NEWLINE text, where a trailing backslash continues the text
        string ReadText()
        {
            SkipBlanks();
            if (i >= n) throw Fail(CommandSpec.Message("sed", "expectedBackslash"), i);
            if (script[i] == '\\')
            {
                i++;
                if (i < n && script[i] == '\n') i++;
                else SkipBlanks();
            }
            var sb = new StringBuilder();
            while (i < n && script[i] != '\n')
            {
                if (script[i] == '\\' && i + 1 < n) { sb.Append(script[i + 1]); i += 2; continue; }
                sb.Append(script[i++]);
            }
            return sb.ToString();
        }

        while (true)
        {
            while (i < n && (char.IsWhiteSpace(script[i]) || script[i] == ';')) i++;
            if (i >= n) break;
            if (script[i] == '#') { while (i < n && script[i] != '\n') i++; continue; }
            var c = new Cmd { A1 = ReadAddr(false) };
            if (c.A1 != null && i < n && script[i] == ',')
            {
                i++;
                SkipBlanks();
                c.A2 = ReadAddr(true) ?? throw Fail(CommandSpec.Message("sed", "unexpectedComma"), Math.Min(i + 1, n));
            }
            if (c.A1 is ZeroAddr && c.A2 is not RegexAddr) throw Fail(CommandSpec.Message("sed", "lineZero"), Math.Min(i + 1, n));
            SkipBlanks();
            if (i < n && script[i] == '!')
            {
                c.Negate = true;
                i++;
                SkipBlanks();
                if (i < n && script[i] == '!') throw Fail(CommandSpec.Message("sed", "multipleBang"), i + 1);
            }
            if (i >= n) throw Fail(CommandSpec.Message("sed", "missingCommand"), i);
            char op = c.Op = script[i++];
            switch (op)
            {
                case '{': blocks.Push((cmds.Count, i)); break;
                case '}':
                    if (blocks.Count == 0) throw Fail(CommandSpec.Message("sed", "unexpectedBrace"), i);
                    if (c.A1 != null) throw Fail(CommandSpec.Message("sed", "braceAddresses"), i);
                    cmds[blocks.Pop().Cmd].Target = cmds.Count + 1;
                    break;
                case 's':
                    if (i >= n) throw Fail(CommandSpec.Message("sed", "unterminatedS"), i);
                    char delim = script[i++];
                    var find = ReadDelimited(delim, "regex");
                    c.Repl = ParseReplacement(ReadDelimited(delim, "replacement"));
                    var options = RegexOptions.None;
                    bool numbered = false;
                    while (i < n)
                    {
                        char f = script[i];
                        if (char.IsAsciiDigit(f))
                        {
                            c.Nth = ReadNumber();
                            if (numbered) throw Fail(CommandSpec.Message("sed", "multipleNumbers"), i);
                            if (c.Nth == 0) throw Fail(CommandSpec.Message("sed", "zeroNumber"), i);
                            numbered = true;
                            continue;
                        }
                        if (!"gpiImMew".Contains(f)) break;
                        i++;
                        switch (f)
                        {
                            case 'g' when c.Global: throw Fail(CommandSpec.Message("sed", "multipleG"), i);
                            case 'p' when c.Print: throw Fail(CommandSpec.Message("sed", "multipleP"), i);
                            case 'g': c.Global = true; break;
                            case 'p': c.Print = true; break;
                            case 'i' or 'I': options |= RegexOptions.IgnoreCase; break;
                            case 'm' or 'M': options |= RegexOptions.Multiline; break;
                            case 'w' when script.IndexOf('\n', i) is var eol && script[i..(eol < 0 ? n : eol)].Trim().Length == 0:
                                throw Fail(CommandSpec.Message("sed", "missingFilename"), i);
                            default: throw Fail(CommandSpec.Message("sed", "flagNotSupported", ("flag", f.ToString())), i);
                        }
                    }
                    SkipBlanks();
                    if (i < n && script[i] is not (';' or '\n' or '}' or '#')) throw Fail(CommandSpec.Message("sed", "unknownSOption"), i + 1);
                    // GNU compiles the regex once it has read the character that ends the command
                    int compiledAt = i < n && script[i] is ';' or '\n' ? i + 1 : i;
                    c.Re = CompileRegex(find, options, compiledAt);
                    var maxRef = c.Repl.OfType<GroupPart>().Select(g => g.Group).DefaultIfEmpty(0).Max();
                    if (maxRef > c.Re.GetGroupNumbers().Length - 1) throw Fail(CommandSpec.Message("sed", "invalidReference", ("number", maxRef.ToString())), compiledAt);
                    break;
                case 'y':
                    char yd = i < n ? script[i] : '\0';
                    i++;
                    var src = Builtins.Unescape(ReadDelimited(yd, "y"));
                    var dst = Builtins.Unescape(ReadDelimited(yd, "y"));
                    if (src.Length != dst.Length) throw Fail(CommandSpec.Message("sed", "yLengths"), i);
                    for (int k = 0; k < src.Length; k++) c.Table[src[k]] = dst[k];
                    break;
                case 'a' or 'i' or 'c': c.Text = ReadText(); break;
                case ':':
                    if (c.A1 != null) throw Fail(CommandSpec.Message("sed", "colonAddresses"), i);
                    var label = ReadLabel();
                    if (label.Length == 0) throw Fail(CommandSpec.Message("sed", "colonLabel"), i);
                    labels[label] = cmds.Count;
                    break;
                case 'b' or 't' or 'T': c.Label = ReadLabel(); break;
                case 'v': ReadLabel(); break; // "requires GNU sed VERSION": always met
                case 'q' or 'Q':
                    SkipBlanks();
                    c.Code = i < n && char.IsAsciiDigit(script[i]) ? ReadNumber() : 0;
                    break;
                default:
                    int at = i;
                    if (op is 'r' or 'R' or 'w' or 'W' && ReadLabel().Length == 0) throw Fail(CommandSpec.Message("sed", "missingFilename"), i);
                    if (!"=dDgGhHnNpPxz".Contains(op)) throw Fail(CommandSpec.Message("sed", "unknownCommand", ("op", op.ToString())), at);
                    break;
            }
            cmds.Add(c);
            SkipBlanks();
            if (op != '{' && i < n && script[i] is not (';' or '\n' or '}' or '#')) throw Fail(CommandSpec.Message("sed", "extraCharacters"), i + 1);
        }
        if (blocks.Count > 0) throw Fail(CommandSpec.Message("sed", "unmatchedBrace"), blocks.Peek().At, charZero: true);
        foreach (var c in cmds.Where(c => c.Op is 'b' or 't' or 'T'))
        {
            if (c.Label.Length > 0 && !labels.ContainsKey(c.Label)) throw new ShellException(CommandSpec.Message("sed", "missingLabel", ("label", c.Label)), 4);
            c.Target = c.Label.Length > 0 ? labels[c.Label] : cmds.Count;
        }
        return cmds;
    }

    /// <summary>A sed replacement: literals, groups (&amp; is 0), and \U \L \u \l \E case markers.</summary>
    static List<ReplPart> ParseReplacement(string r)
    {
        var parts = new List<ReplPart>();
        for (int i = 0; i < r.Length; i++)
        {
            char c = r[i];
            if (c == '\\' && i + 1 < r.Length)
            {
                char nx = r[++i];
                if (char.IsAsciiDigit(nx)) parts.Add(new GroupPart(nx - '0'));
                else if ("ULulE".Contains(nx)) parts.Add(new CasePart(nx));
                else parts.Add(new LitPart(nx switch { 'n' => "\n", 't' => "\t", 'r' => "\r", _ => nx.ToString() }));
            }
            else parts.Add(c == '&' ? new GroupPart(0) : new LitPart(c.ToString()));
        }
        return parts;
    }

    /// <summary>Appends the replacement for m to sb, stopping at the string limit before it grows further.</summary>
    static void Expand(List<ReplPart> parts, Match m, StringBuilder sb, long maxString)
    {
        char? mode = null, one = null;
        foreach (var p in parts)
        {
            if (p is CasePart cp)
            {
                if (cp.Op is 'U' or 'L' or 'E') { mode = cp.Op == 'E' ? null : cp.Op; one = null; }
                else one = cp.Op;
                continue;
            }
            var text = p is GroupPart g ? (g.Group < m.Groups.Count ? m.Groups[g.Group].Value : "") : ((LitPart)p).Text;
            if (mode != null) text = mode == 'U' ? text.ToUpperInvariant() : text.ToLowerInvariant();
            if (one != null && text.Length > 0)
            {
                text = (one == 'u' ? char.ToUpperInvariant(text[0]) : char.ToLowerInvariant(text[0])) + text[1..];
                one = null;
            }
            if (sb.Length + (long)text.Length > maxString) throw Shell.TooLarge("sed: pattern space", maxString, "process the input in smaller pieces");
            sb.Append(text);
        }
    }

    static (string Text, bool Changed) Substitute(Cmd c, string ps, long maxString)
    {
        var sb = new StringBuilder();
        int pos = 0, count = 0, prevEnd = -1;
        bool done = false;
        for (var m = c.Re!.Match(ps); m.Success; m = m.NextMatch())
        {
            if (m.Length == 0 && m.Index == prevEnd) continue; // no empty match right after the previous match (GNU: "abc" s/b*/X/g -> XaXcX)
            prevEnd = m.Index + m.Length;
            if (++count < c.Nth) continue;
            sb.Append(ps, pos, m.Index - pos);
            Expand(c.Repl, m, sb, maxString);
            pos = m.Index + m.Length;
            done = true;
            if (!c.Global) break;
        }
        if (!done) return (ps, false);
        sb.Append(ps, pos, ps.Length - pos);
        return (sb.ToString(), true);
    }

    static bool Matches(Cmd c, string ps, int n, bool last)
    {
        bool Hit(Addr a) => a switch
        {
            LineAddr l => l.Line == n,
            LastAddr => last,
            RegexAddr r => r.Re.IsMatch(ps),
            StepAddr s => s.Step <= 0 ? n == s.First : n >= s.First && (n - s.First) % s.Step == 0,
            ZeroAddr => n == 1,
            _ => false,
        };
        bool result;
        if (c.A1 == null) result = true;
        else if (c.A2 == null) result = Hit(c.A1);
        else if (c.InRange)
        {
            bool ended = c.A2 switch
            {
                LineAddr l => n >= l.Line,
                PlusAddr => n >= c.RangeEnd,
                MultAddr mu => mu.Count <= 0 || n % mu.Count == 0,
                var a2 => Hit(a2),
            };
            c.InRange = !ended;
            result = true;
        }
        else if (Hit(c.A1))
        {
            if (c.A1 is ZeroAddr) c.InRange = !Hit(c.A2);
            else
                switch (c.A2)
                {
                    case LineAddr l: c.InRange = l.Line > n; break;
                    case PlusAddr p: c.RangeEnd = n + p.Count; c.InRange = p.Count > 0; break;
                    case MultAddr mu: c.InRange = mu.Count > 0 && n % mu.Count != 0; break;
                    case LastAddr: c.InRange = !last; break;
                    default: c.InRange = true; break;
                }
            result = true;
        }
        else result = false;
        return result != c.Negate;
    }

    /// <summary>Runs a parsed script over text. Returns the q/Q exit code if the script quit, else null.</summary>
    static int? Execute(List<Cmd> cmds, string text, TextWriter output, bool quiet, CancellationToken ct, long maxString)
    {
        var lines = Builtins.SplitLines(text);
        bool trailing = text.EndsWith('\n');
        foreach (var c in cmds) c.InRange = false;
        var hold = "";
        int idx = 0, n = 0;
        bool pendingNewline = false;

        void Write(string s, bool fromInput = false)
        {
            if (pendingNewline) { output.Write('\n'); pendingNewline = false; }
            output.Write(s);
            // the last input line had no newline: don't invent one (unless more output follows)
            if (fromInput && idx >= lines.Count && !trailing) pendingNewline = true;
            else output.Write('\n');
        }

        int? exitCode = null;
        long steps = 0;
        var ps = "";
        bool reread = true;
        while (exitCode == null)
        {
            if (reread)
            {
                if (idx >= lines.Count) break;
                ps = lines[idx++];
                n++;
            }
            reread = true;
            var appends = new List<string>();
            bool tflag = false, autoprint = !quiet;
            for (int pc = 0; pc < cmds.Count;)
            {
                if (++steps > 10_000_000) throw new ShellException(CommandSpec.Message("sed", "tooManySteps"));
                if ((steps & 1023) == 0) ct.ThrowIfCancellationRequested(); // the call's time limit, or `timeout`
                if (ps.Length > maxString || hold.Length > maxString)
                    throw Shell.TooLarge("sed: pattern space", maxString, "process the input in smaller pieces");
                var c = cmds[pc++];
                if (!Matches(c, ps, n, idx >= lines.Count))
                {
                    if (c.Op == '{') pc = c.Target;
                    continue;
                }
                bool stop = false;
                switch (c.Op)
                {
                    case 's':
                        (ps, var changed) = Substitute(c, ps, maxString);
                        if (changed) { tflag = true; if (c.Print) Write(ps, true); }
                        break;
                    case 'p': Write(ps, true); break;
                    case 'P': Write(ps.Split('\n', 2)[0], true); break;
                    case 'd': autoprint = false; stop = true; break;
                    case 'D':
                        autoprint = false;
                        stop = true;
                        if (ps.Contains('\n')) { ps = ps[(ps.IndexOf('\n') + 1)..]; reread = false; }
                        break;
                    case 'n':
                        if (idx >= lines.Count) { stop = true; break; } // no next line: GNU sed ends here, printing the pattern space
                        if (!quiet) Write(ps, true);
                        foreach (var t in appends) Write(t);
                        appends.Clear();
                        ps = lines[idx++];
                        n++;
                        break;
                    case 'N':
                        if (idx >= lines.Count) { stop = true; break; }
                        ps += "\n" + lines[idx++];
                        n++;
                        break;
                    case 'g': ps = hold; break;
                    case 'G': ps += "\n" + hold; break;
                    case 'h': hold = ps; break;
                    case 'H': hold += "\n" + ps; break;
                    case 'x': (ps, hold) = (hold, ps); break;
                    case 'z': ps = ""; break;
                    case 'y': ps = string.Concat(ps.Select(ch => c.Table.TryGetValue(ch, out var r) ? r : ch)); break;
                    case '=': Write(n.ToString(CultureInfo.InvariantCulture)); break;
                    case 'a': appends.Add(c.Text); break;
                    case 'i': Write(c.Text); break;
                    case 'c':
                        if (c.A2 == null || !c.InRange || c.Negate) Write(c.Text);
                        autoprint = false;
                        stop = true;
                        break;
                    case 'b': pc = c.Target; break;
                    case 't': if (tflag) { tflag = false; pc = c.Target; } break;
                    case 'T': if (!tflag) pc = c.Target; else tflag = false; break;
                    case 'q': exitCode = c.Code; stop = true; break;
                    case 'Q': exitCode = c.Code; autoprint = false; stop = true; break;
                }
                if (stop) break;
            }
            if (autoprint) Write(ps, true);
            foreach (var t in appends) Write(t);
        }
        return exitCode;
    }
}
