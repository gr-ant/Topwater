// sed: the stream editor (the command is registered with the text commands). See docs/architecture.md.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Topwater;

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
            // a directory reads as an empty script, as GNU sed does
            try { text = value == "-" ? ctx.StdinText : ctx.Fs.IsDirectory(ctx.ResolvePath(value)) ? "" : ctx.Fs.ReadAllText(ctx.ResolvePath(value)); }
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
        if (scripts[0].Text.StartsWith("#n")) quiet = true; // "#n" opening the script means -n
        var cmds = Parse(scripts, extended);
        bool stdinRead = false;
        string? ReadFile(string f)
        {
            if (f == "/dev/stdin") { if (stdinRead) return ""; stdinRead = true; return ctx.StdinText; } // stdin is read once
            try { var path = ctx.ResolvePath(f); return ctx.Fs.IsFile(path) ? ctx.Fs.ReadAllText(path) : null; }
            catch (ShellException) { return null; }
        }

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
                var quitCode = Execute(cmds, original, output, quiet, ctx.CancellationToken, ctx.Shell.Options.MaxStringBytes, ReadFile);
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
                if (Execute(cmds, t, ctx.Stdout, quiet, ctx.CancellationToken, ctx.Shell.Options.MaxStringBytes, ReadFile) is { } q) return q;
        }
        else
        {
            var joined = string.Concat(texts.Select((t, k) => t.EndsWith('\n') || k == texts.Count - 1 ? t : t + "\n"));
            if (Execute(cmds, joined, ctx.Stdout, quiet, ctx.CancellationToken, ctx.Shell.Options.MaxStringBytes, ReadFile) is { } q) return q;
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

        // a label ends at a blank too, and the next command may follow it directly (`bx;:x p`)
        string ReadLabel(bool blanks = true)
        {
            SkipBlanks();
            int start = i;
            while (i < n && script[i] is not (';' or '\n' or '}') && !(blanks && script[i] is ' ' or '\t')) i++;
            return blanks ? script[start..i] : script[start..i].Trim();
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
                case 'r' or 'R':
                    // the file name is the rest of the line, as in GNU sed
                    SkipBlanks();
                    int nameStart = i;
                    while (i < n && script[i] != '\n') i++;
                    c.Text = script[nameStart..i];
                    if (c.Text.Length == 0) throw Fail(CommandSpec.Message("sed", "missingFilename"), i);
                    break;
                case 'v': ReadLabel(); break; // "requires GNU sed VERSION": always met
                case 'q' or 'Q':
                    SkipBlanks();
                    c.Code = i < n && char.IsAsciiDigit(script[i]) ? ReadNumber() : 0;
                    break;
                default:
                    int at = i;
                    if (op is 'w' or 'W' && ReadLabel(blanks: false).Length == 0) throw Fail(CommandSpec.Message("sed", "missingFilename"), i);
                    if (!"=dDgGhHnNpPxz".Contains(op)) throw Fail(CommandSpec.Message("sed", "unknownCommand", ("op", op.ToString())), at);
                    break;
            }
            cmds.Add(c);
            SkipBlanks();
            if (op is not ('{' or ':' or 'b' or 't' or 'T' or 'r' or 'R') && i < n && script[i] is not (';' or '\n' or '}' or '#')) throw Fail(CommandSpec.Message("sed", "extraCharacters"), i + 1);
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
    /// <param name="readFile">a file's text for r and R, or null when it can't be read (sed then adds nothing)</param>
    static int? Execute(List<Cmd> cmds, string text, TextWriter output, bool quiet, CancellationToken ct, long maxString, Func<string, string?> readFile)
    {
        var rLines = new Dictionary<string, Queue<string>>(); // R: each file's lines not yet used
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
            var appends = new List<(string Text, bool Raw)>(); // a and R add a line; r adds a file as it is
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
                        WriteAppends(appends);
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
                    case 'a': appends.Add((c.Text, false)); break;
                    case 'r': if (readFile(c.Text) is { Length: > 0 } content) appends.Add((content, true)); break;
                    case 'R':
                        if (!rLines.TryGetValue(c.Text, out var queue)) rLines[c.Text] = queue = new(readFile(c.Text) is { } all ? Builtins.SplitLines(all) : []);
                        if (queue.TryDequeue(out var line)) appends.Add((line, false));
                        break;
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
            WriteAppends(appends);
        }
        return exitCode;

        void WriteAppends(List<(string Text, bool Raw)> appends)
        {
            foreach (var (t, raw) in appends)
            {
                if (!raw) { Write(t); continue; }
                if (pendingNewline) { output.Write('\n'); pendingNewline = false; }
                output.Write(t);
            }
            appends.Clear();
        }
    }
}
