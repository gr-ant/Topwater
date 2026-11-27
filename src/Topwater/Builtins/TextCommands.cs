// Text: grep, head, tail, wc, sort, uniq, cut, tr, tee, xargs, sed, nl, rev, tac, paste, comm, join, column, seq, diff. See docs/architecture.md.
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
                        // the end may be a collating element: a-[.z.]
                        bool collating = j + 3 < n && p[j + 1] == '[' && p[j + 2] == '.';
                        int endClose = collating ? p.IndexOf(".]", j + 3, StringComparison.Ordinal) : -1;
                        if (collating && endClose != j + 4) return endClose < 0 ? Unmatched : "Invalid collation character";
                        if ((collating ? p[j + 3] : p[j + 1]) < low) return "Invalid range end";
                        low = null;
                        j += collating ? 6 : 2;
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

    /// <summary>comm and join: the lines of exactly two files (- is stdin), or null after reporting an unreadable one.</summary>
    static List<List<string>>? TwoInputs(CommandContext ctx, List<string> files, char sep = '\n')
    {
        if (files.Count < 2)
            throw new UsageException(files.Count > 0 ? CommandSpec.Message(ctx.Name, "missingOperandAfter", ("operand", files[0])) : CommandSpec.Message(ctx.Name, "missingOperand"));
        if (files.Count > 2) throw new UsageException(CommandSpec.Message(ctx.Name, "extraOperand", ("operand", files[2])));
        var output = new List<List<string>>();
        foreach (var f in files)
        {
            var got = Inputs(ctx, [f], () => { }).ToList();
            if (got.Count == 0) return null;
            var lines = got[0].Text.Split(sep).ToList();
            if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
            output.Add(lines);
        }
        return output;
    }

    static int CommCmd(CommandContext ctx)
    {
        var a = Args.Parse(ctx.Args, CommandSpec.Of("comm"));
        char sep = a.Has('z') ? '\0' : '\n';
        if (TwoInputs(ctx, a.Positional, sep) is not [var left, var right]) return 1;
        var delim = a.Value('\u0003') ?? "\t";
        bool[] show = [!a.Has('1'), !a.Has('2'), !a.Has('3')];
        string[] indent = ["", show[0] ? delim : "", string.Concat(Enumerable.Repeat(delim, (show[0] ? 1 : 0) + (show[1] ? 1 : 0)))];
        bool check = !a.Has('\u0002'), strict = a.Has('\u0001');
        var output = new List<string>();
        int[] counts = [0, 0, 0];
        string?[] last = [null, null];
        bool[] unsorted = [false, false];
        void Flush()
        {
            ctx.Stdout.Write(string.Concat(output.Select(x => x + sep)));
            output.Clear();
        }
        // notes line as file k's latest; true the first time file k is found out of order
        bool Disorder(int k, string line)
        {
            bool bad = check && last[k] != null && GitCore.Ordinal.Compare(line, last[k]) < 0 && !unsorted[k];
            unsorted[k] |= bad;
            last[k] = line;
            return bad;
        }
        static bool Less(string x, string y) => GitCore.Ordinal.Compare(x, y) < 0;
        int i = 0, j = 0;
        while (i < left.Count || j < right.Count)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            int col;
            string line;
            var bad = new List<int>();
            if (j >= right.Count || (i < left.Count && Less(left[i], right[j])))
            {
                (col, line) = (0, left[i]);
                if (Disorder(0, left[i])) bad.Add(0);
                i++;
            }
            else if (i >= left.Count || Less(right[j], left[i]))
            {
                (col, line) = (1, right[j]);
                if (Disorder(1, right[j])) bad.Add(1);
                j++;
            }
            else
            {
                (col, line) = (2, left[i]);
                if (Disorder(0, left[i])) bad.Add(0);
                if (Disorder(1, right[j])) bad.Add(1);
                i++;
                j++;
            }
            if (bad.Count > 0 && strict)  // --check-order stops before the line out of order
            {
                Flush();
                ctx.Stderr.WriteLine("comm: " + CommandSpec.Message("comm", "notSorted", ("n", (bad[0] + 1).ToString())));
                return 1;
            }
            counts[col]++;
            if (show[col]) output.Add(indent[col] + line);
            foreach (var k in bad)  // otherwise the line goes out first, then the warning
            {
                Flush();
                ctx.Stderr.WriteLine("comm: " + CommandSpec.Message("comm", "notSorted", ("n", (k + 1).ToString())));
            }
        }
        if (a.Has('\u0004')) output.Add(string.Join(delim, counts) + delim + CommandSpec.Message("comm", "total"));
        Flush();
        if (unsorted.Any(u => u))
        {
            ctx.Stderr.WriteLine("comm: " + CommandSpec.Message("comm", "inputNotSorted"));
            return 1;
        }
        return 0;
    }

    static int JoinCmd(CommandContext ctx)
    {
        var a = Args.Parse(ctx.Args, CommandSpec.Of("join"));
        var tab = a.Value('t') is { Length: > 0 } t0 ? t0 : null; // -t "" splits on blanks, as GNU join does
        if (tab is { Length: > 1 }) throw new ShellException(CommandSpec.Message("join", "multiCharTab", ("value", tab)));
        static int FieldNo(string v) =>
            v.Length > 0 && v.All(char.IsAsciiDigit) && int.TryParse(v, out var n) && n >= 1 ? n - 1
            : v.Length > 0 && v.All(char.IsAsciiDigit) && v.TrimStart('0').Length > 0 ? int.MaxValue  // past int: no line has that field
            : throw new ShellException(CommandSpec.Message("join", "badField", ("value", v)));
        int k1 = 0, k2 = 0;
        if (a.Value('j') is { } jv) k1 = k2 = FieldNo(jv);
        if (a.Value('1') is { } v1) k1 = FieldNo(v1);
        if (a.Value('2') is { } v2) k2 = FieldNo(v2);
        var unpaired = new HashSet<int>();
        foreach (var v in a.All('a').Concat(a.All('v')))
        {
            if (v is not ("1" or "2")) throw new ShellException(CommandSpec.Message("join", "badFileNumberArg", ("value", v)));
            unpaired.Add(v[0] - '0');
        }
        bool only = a.All('v').Count > 0;
        List<(int Side, int N)>? fmt = null;
        if (a.Value('o') is { } ov && ov != "auto")
        {
            fmt = [];
            foreach (var spec in Regex.Split(ov.Trim(), "[ ,]+"))
            {
                var m = Regex.Match(spec, @"\A([0-9]+)\.([0-9]+)\z");
                if (spec == "0") fmt.Add((0, 0));
                else if (!m.Success || m.Groups[1].Value is not ("1" or "2")) throw new ShellException(CommandSpec.Message("join", "badFileNumber", ("spec", spec)));
                else fmt.Add((m.Groups[1].Value[0] - '0', FieldNo(m.Groups[2].Value)));
            }
        }
        if (TwoInputs(ctx, a.Positional) is not [var lefts, var rights]) return 1;
        List<string> Fields(string line)
        {
            if (tab != null) return line.Split(tab[0]).ToList();
            line = line.Trim(' ', '\t');  // GNU join's blanks: spaces and tabs only
            return line.Length > 0 ? Regex.Split(line, "[ \t]+").ToList() : [];
        }
        var left = lefts.Select(Fields).ToList();
        var right = rights.Select(Fields).ToList();
        int[] keys = [k1, k2];
        bool fold = a.Has('i');
        string Key(List<string> f, int side)
        {
            var k = keys[side] < f.Count ? f[keys[side]] : "";
            return fold ? k.ToLowerInvariant() : k;
        }
        if (a.Value('o') == "auto")
            fmt = [(0, 0), .. Enumerable.Range(0, left.Count > 0 ? left[0].Count : 0).Where(n => n != k1).Select(n => (1, n)),
                .. Enumerable.Range(0, right.Count > 0 ? right[0].Count : 0).Where(n => n != k2).Select(n => (2, n))];
        var outSep = tab ?? " ";
        var empty = a.Value('e') ?? "";
        var lines = new List<string>();
        void Emit(List<string>? f1, List<string>? f2)
        {
            var row = new List<string>();
            var (src, k) = f1 != null ? (f1, k1) : (f2!, k2);
            if (fmt != null)
                foreach (var (side, n) in fmt)
                {
                    var f = side == 1 ? f1 : f2;
                    if (side == 0) row.Add(k < src.Count ? src[k] : empty);
                    else row.Add(f != null && n < f.Count ? f[n] : empty);
                }
            else
            {
                row.Add(k < src.Count ? src[k] : "");
                row.AddRange((f1 ?? []).Where((_, n) => n != k1));
                row.AddRange((f2 ?? []).Where((_, n) => n != k2));
            }
            lines.Add(string.Join(outSep, row));
            if (lines.Count >= 1024)  // written as it goes: one repeated key can pair up into far more rows than fit in memory
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                ctx.Stdout.Write(string.Concat(lines.Select(x => x + "\n")));
                lines.Clear();
            }
        }
        int i = 0, j = 0;
        if (a.Has('\u0001') && (left.Count > 0 || right.Count > 0))
        {
            Emit(left.Count > 0 ? left[0] : null, right.Count > 0 ? right[0] : null);
            i = j = 1;
        }
        while (i < left.Count || j < right.Count)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var aKey = i < left.Count ? Key(left[i], 0) : null;
            var bKey = j < right.Count ? Key(right[j], 1) : null;
            if (bKey == null || (aKey != null && GitCore.Ordinal.Compare(aKey, bKey) < 0))
            {
                if (unpaired.Contains(1)) Emit(left[i], null);
                i++;
            }
            else if (aKey == null || GitCore.Ordinal.Compare(bKey, aKey) < 0)
            {
                if (unpaired.Contains(2)) Emit(null, right[j]);
                j++;
            }
            else
            {
                int i2 = i, j2 = j;
                while (i2 < left.Count && Key(left[i2], 0) == aKey) i2++;
                while (j2 < right.Count && Key(right[j2], 1) == aKey) j2++;
                if (!only)
                    for (int x = i; x < i2; x++)
                        for (int y = j; y < j2; y++) Emit(left[x], right[y]);
                (i, j) = (i2, j2);
            }
        }
        ctx.Stdout.Write(string.Concat(lines.Select(x => x + "\n")));
        return 0;
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
            foreach (var (input, text) in Inputs(ctx, files, () => error = true, quiet: a.Has('s')))
            {
                var name = input == "(standard input)" && a.Value('\u0007') is { } label ? label : input; // --label
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
                if (ExecFailure(ctx, argv[0], out _) is { } error)
                {
                    ctx.Stderr.WriteLine("xargs: " + CommandSpec.Message("xargs", "cannotRun", ("command", argv[0]), ("errno", error)));
                    return error == CommandSpec.Errno.EACCES ? 126 : 127;
                }
                var rc = await ctx.InvokeAsync(argv);
                if (rc == 127) return 127;
                if (rc == 255) { ctx.Stderr.WriteLine("xargs: " + CommandSpec.Message("xargs", "exitedWith255", ("command", argv[0]))); return 124; }
                if (rc != 0) code = 123;
            }
            return code;
        });

        shell.Register("sed", ctx => Sed.Run(ctx));

        shell.Register("nl", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("nl"));
            bool all = a.Value('b') == "a";
            int width = int.Parse(a.Value('w') ?? "6"), n = 0, code = 0;
            var sep = a.Value('s') ?? "\t";
            foreach (var (_, text) in Inputs(ctx, a.Positional, () => code = 1))
                foreach (var line in SplitLines(text))
                    ctx.Stdout.WriteLine(line.Length == 0 && !all ? "" : $"{(++n).ToString().PadLeft(width)}{sep}{line}");
            return code;
        });

        shell.Register("rev", ctx =>
        {
            int code = 0;
            foreach (var (_, text) in Inputs(ctx, ctx.Args, () => code = 1))
                foreach (var line in SplitLines(text)) ctx.Stdout.WriteLine(new string(line.Reverse().ToArray()));
            return code;
        });

        shell.Register("tac", ctx =>
        {
            int code = 0;
            foreach (var (_, text) in Inputs(ctx, ctx.Args, () => code = 1)) WriteLines(ctx.Stdout, Enumerable.Reverse(SplitLines(text)));
            return code;
        });

        shell.Register("paste", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("paste"));
            var delims = Unescape(a.Value('d') ?? "\t");
            if (delims.Length == 0) delims = "\0";
            char D(int i) => delims[i % delims.Length];
            string Join(IList<string> cells) { var sb = new StringBuilder(); for (int i = 0; i < cells.Count; i++) { if (i > 0 && D(i - 1) != '\0') sb.Append(D(i - 1)); sb.Append(cells[i]); } return sb.ToString(); }
            int code = 0;
            List<List<string>> columns;
            int dashes = a.Positional.Count(f => f == "-");
            if (dashes > 1 && !a.Has('s'))
            {
                // several "-" operands take turns reading lines from stdin (seq 6 | paste - - -)
                var stdinLines = SplitLines(ctx.StdinText);
                columns = [];
                int k = 0;
                foreach (var f in a.Positional)
                {
                    if (f == "-") { int start = k++; columns.Add(stdinLines.Where((_, idx) => idx % dashes == start).ToList()); }
                    else columns.AddRange(Inputs(ctx, [f], () => code = 1).Select(i => SplitLines(i.Text)));
                }
            }
            else columns = Inputs(ctx, a.Positional, () => code = 1).Select(i => SplitLines(i.Text)).ToList();
            if (a.Has('s')) foreach (var col in columns) ctx.Stdout.WriteLine(Join(col));
            else
            {
                int rows = columns.Count == 0 ? 0 : columns.Max(c => c.Count);
                for (int r = 0; r < rows; r++) ctx.Stdout.WriteLine(Join(columns.Select(c => r < c.Count ? c[r] : "").ToList()));
            }
            return code;
        });

        shell.Register("comm", CommCmd);
        shell.Register("join", JoinCmd);

        shell.Register("column", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("column"));
            int code = 0;
            var lines = Inputs(ctx, a.Positional, () => code = 1).SelectMany(i => SplitLines(i.Text)).ToList();
            if (!a.Has('t')) { WriteLines(ctx.Stdout, lines); return code; }
            var sep = a.Value('s');
            var rows = lines.Where(l => l.Trim().Length > 0)
                .Select(l => sep != null ? l.Split(Unescape(sep).ToCharArray(), StringSplitOptions.RemoveEmptyEntries) : l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToList();
            var outSep = a.Value('o') ?? "  ";
            int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
            var widths = Enumerable.Range(0, cols).Select(c => rows.Max(r => c < r.Length ? r[c].Length : 0)).ToArray();
            foreach (var r in rows)
                ctx.Stdout.WriteLine(string.Join(outSep, r.Select((cell, i) => i == r.Length - 1 ? cell : cell.PadRight(widths[i]))));
            return code;
        });

        shell.Register("seq", ctx =>
        {
            var args = ctx.Args;
            var nums = new List<string>();
            string sep = "\n";
            bool equalWidth = false;
            string? fmt = null;
            for (int i = 0; i < args.Count; i++)
            {
                var x = args[i];
                if (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) nums.Add(x);
                else if (x is "-w" or "--equal-width") equalWidth = true;
                else if (x == "-s") sep = i + 1 < args.Count ? args[++i] : throw new ShellException(CommandSpec.Message("seq", "optionRequiresArgument", ("option", "s")));
                else if (x.StartsWith("-s")) sep = x[2..];
                else if (x.StartsWith("--separator=")) sep = x[12..];
                else if (x is "-f" or "--format") fmt = i + 1 < args.Count ? args[++i] : throw new ShellException(CommandSpec.Message("seq", "optionRequiresArgument", ("option", "f")));
                else if (x.StartsWith("-f")) fmt = x[2..];
                else if (x.StartsWith("--format=")) fmt = x[9..];
                else throw new ShellException(CommandSpec.Message("seq", "invalidOption", ("option", x.TrimStart('-'))));
            }
            if (nums.Count is 0 or > 3) throw new UsageException(nums.Count == 0 ? CommandSpec.Message("seq", "missingOperand") : CommandSpec.Message("seq", "extraOperand", ("operand", nums[3])));
            var v = nums.Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();
            double first = v.Count > 1 ? v[0] : 1, step = v.Count == 3 ? v[1] : 1, last = v[^1];
            if (step == 0) throw new ShellException(CommandSpec.Message("seq", "zeroIncrement"));
            int decimals = nums.Max(x => x.Contains('.') ? x.Length - x.IndexOf('.') - 1 : 0);
            var items = new List<string>();
            for (long k = 0; ; k++)
            {
                double x = first + k * step; // not a running sum, so 0.1 steps don't drift
                if (!(step > 0 ? x <= last + 1e-9 : x >= last - 1e-9)) break;
                items.Add(fmt != null ? Printf.Format(fmt, [x.ToString("R", CultureInfo.InvariantCulture)]) : x.ToString("F" + decimals, CultureInfo.InvariantCulture));
                if (items.Count > 1_000_000) throw new ShellException(CommandSpec.Message("seq", "tooManyNumbers"));
            }
            if (equalWidth && items.Count > 0) { int w = items.Max(s => s.Length); items = items.Select(s => s.PadLeft(w, '0')).ToList(); }
            if (items.Count > 0) ctx.Stdout.Write(string.Join(Unescape(sep), items) + "\n");
            return 0;
        });

        shell.Register("diff", ctx => Diff.Run(ctx));
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
