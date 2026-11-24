using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Topwater;

internal static partial class Builtins
{
    /// <summary>Converts a POSIX basic regex (grep/sed default) to .NET syntax: \( \) \{ \} \| \+ \? are operators, bare ones are literals.</summary>
    internal static string BreToNet(string bre)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < bre.Length; i++)
        {
            char c = bre[i];
            if (c == '\\' && i + 1 < bre.Length)
            {
                char n = bre[++i];
                sb.Append("(){}|+?".Contains(n) ? n.ToString() : "\\" + n);
            }
            else sb.Append("(){}|+?".Contains(c) ? "\\" + c : c.ToString());
        }
        return sb.ToString();
    }

    static Regex BuildRegex(string pattern, bool extended, bool fixedString, bool ignoreCase, bool word = false)
    {
        var re = fixedString ? Regex.Escape(pattern) : extended ? pattern : BreToNet(pattern);
        if (word) re = $@"\b(?:{re})\b";
        try { return new Regex(re, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None); }
        catch (ArgumentException e) { throw new ShellException($"invalid regular expression: {e.Message}", 2); }
    }

    static List<int> ParseList(string spec)
    {
        // "1,3-5,7-" -> sorted 1-based indexes (open ranges capped at 1000 fields)
        var result = new SortedSet<int>();
        foreach (var part in spec.Split(','))
        {
            var range = part.Split('-');
            if (range.Length == 1) result.Add(int.Parse(range[0]));
            else
            {
                int from = range[0].Length > 0 ? int.Parse(range[0]) : 1, to = range[1].Length > 0 ? int.Parse(range[1]) : 1000;
                for (int k = from; k <= to; k++) result.Add(k);
            }
        }
        return result.ToList();
    }

    static void RegisterTextCommands(Shell shell)
    {
        shell.Register("grep", ctx =>
        {
            // --include/--exclude/--exclude-dir may repeat, so pull them out before getopt-style parsing
            var include = new List<Regex>(); var exclude = new List<Regex>(); var excludeDir = new List<Regex>();
            var rest = new List<string>();
            foreach (var arg in ctx.Args)
            {
                if (arg.StartsWith("--include=")) include.Add(Glob.ToRegex(arg[10..]));
                else if (arg.StartsWith("--exclude=")) exclude.Add(Glob.ToRegex(arg[10..]));
                else if (arg.StartsWith("--exclude-dir=")) excludeDir.Add(Glob.ToRegex(arg[14..]));
                else rest.Add(arg);
            }
            var a = Args.Parse(rest, "invcrRlLEFwohHqsIaZ", "eABCm",
                "binary-files=I,text=a,ignore-case=i,line-number=n,invert-match=v,count=c,recursive=r,files-with-matches=l,files-without-match=L,extended-regexp=E,fixed-strings=F,word-regexp=w,only-matching=o,quiet=q,regexp=e,max-count=m");
            var positional = a.Positional.ToList();
            var patterns = new List<string>();
            if (a.Value('e') is { } e) patterns.Add(e);
            else if (positional.Count > 0) { patterns.Add(positional[0]); positional.RemoveAt(0); }
            else throw new ShellException("usage: grep [OPTION]... PATTERNS [FILE]...", 2);

            bool recursive = a.Has('r') || a.Has('R');
            if (recursive && positional.Count == 0) positional.Add(".");
            var re = BuildRegex(string.Join("|", patterns), a.Has('E'), a.Has('F'), a.Has('i'), a.Has('w'));
            int after = int.Parse(a.Value('A') ?? a.Value('C') ?? "0"), before = int.Parse(a.Value('B') ?? a.Value('C') ?? "0");
            int maxCount = a.Value('m') is { } m ? int.Parse(m) : int.MaxValue;

            // expand directories for -r
            var files = new List<string>();
            int code = 1;
            bool error = false;
            foreach (var f in positional)
            {
                var abs = ctx.ResolvePath(f);
                if (!ctx.Fs.IsDirectory(abs)) { files.Add(f); continue; }
                if (!recursive) { if (!a.Has('s')) ctx.Stderr.WriteLine($"grep: {f}: Is a directory"); error = true; continue; }
                void Walk(string dirAbs, string display)
                {
                    IReadOnlyList<string> names;
                    try { names = ctx.Fs.List(dirAbs); }
                    catch (ShellException) { if (!a.Has('s')) ctx.Stderr.WriteLine($"grep: {display}: Permission denied"); error = true; return; }
                    foreach (var name in names)
                    {
                        var child = VirtualFileSystem.Normalize(dirAbs, name);
                        var childDisplay = display == "." && f == "." ? name : display.TrimEnd('/') + "/" + name;
                        if (ctx.Fs.IsDirectory(child)) { if (!excludeDir.Any(r => r.IsMatch(name))) Walk(child, childDisplay); }
                        else if ((include.Count == 0 || include.Any(r => r.IsMatch(name))) && !exclude.Any(r => r.IsMatch(name))) files.Add(childDisplay);
                    }
                }
                Walk(abs, f);
            }
            if (positional.Count > 0 && files.Count == 0) return error ? 2 : 1;

            bool showName = !a.Has('h') && (a.Has('H') || files.Count > 1 || recursive);
            foreach (var (name, text) in Inputs(ctx, files, () => error = true))
            {
                if (text.StartsWith("\u007fELF") && !a.Has('a'))
                {
                    if (!a.Has('I') && re.IsMatch(text)) { ctx.Stdout.WriteLine($"grep: {name}: binary file matches"); code = 0; }
                    continue;
                }
                var lines = SplitLines(text);
                var hits = new List<int>();
                for (int i = 0; i < lines.Count && hits.Count < maxCount; i++)
                    if (re.IsMatch(lines[i]) != a.Has('v')) hits.Add(i);
                if (hits.Count > 0) code = 0;
                if (a.Has('q')) { if (code == 0) return 0; continue; }
                var prefix = showName ? name : null;
                if (a.Has('l')) { if (hits.Count > 0) ctx.Stdout.WriteLine(name); continue; }
                if (a.Has('L')) { if (hits.Count == 0) ctx.Stdout.WriteLine(name); continue; }
                if (a.Has('c')) { ctx.Stdout.WriteLine(prefix != null ? $"{prefix}:{hits.Count}" : hits.Count.ToString()); continue; }

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
                        if (a.Has('o') && hitSet.Contains(i)) foreach (Match mt in re.Matches(lines[i])) ctx.Stdout.WriteLine(head + mt.Value);
                        else if (!a.Has('o')) ctx.Stdout.WriteLine(head + lines[i]);
                        lastPrinted = i;
                    }
                }
            }
            return error && code != 0 ? 2 : code;
        }, "Search text with regular expressions");

        CommandHandler headTail = ctx =>
        {
            bool head = ctx.Name == "head";
            var a = Args.Parse(Args.NumericShorthand(ctx.Args), "qv", "nc", "lines=n,bytes=c,quiet=q");
            int code = 0;
            var inputs = Inputs(ctx, a.Positional, () => code = 1).ToList();
            for (int k = 0; k < inputs.Count; k++)
            {
                var (name, text) = inputs[k];
                if (inputs.Count > 1 && !a.Has('q')) ctx.Stdout.WriteLine((k > 0 ? "\n" : "") + $"==> {name} <==");
                if (a.Value('c') is { } bytes)
                {
                    int n = int.Parse(bytes);
                    ctx.Stdout.Write(head ? text[..Math.Min(n, text.Length)] : text[Math.Max(0, text.Length - n)..]);
                    continue;
                }
                var spec = a.Value('n') ?? "10";
                var lines = SplitLines(text);
                IEnumerable<string> pick;
                if (head) pick = spec.StartsWith('-') ? lines.Take(Math.Max(0, lines.Count - int.Parse(spec[1..]))) : lines.Take(int.Parse(spec));
                else pick = spec.StartsWith('+') ? lines.Skip(int.Parse(spec[1..]) - 1) : lines.Skip(Math.Max(0, lines.Count - int.Parse(spec.TrimStart('-'))));
                WriteLines(ctx.Stdout, pick);
            }
            return Task.FromResult(code);
        };
        shell.Register("head", headTail, "Print the first lines of files");
        shell.Register("tail", headTail, "Print the last lines of files");

        shell.Register("wc", ctx =>
        {
            var a = Args.Parse(ctx.Args, "lwcm", longs: "lines=l,words=w,bytes=c,chars=m");
            bool all = !a.Has('l') && !a.Has('w') && !a.Has('c') && !a.Has('m');
            int code = 0;
            long tl = 0, tw = 0, tc = 0;
            var inputs = Inputs(ctx, a.Positional, () => code = 1).ToList();
            string Row(long l, long w, long c, string? name)
            {
                var parts = new List<string>();
                if (all || a.Has('l')) parts.Add(l.ToString());
                if (all || a.Has('w')) parts.Add(w.ToString());
                if (all || a.Has('c') || a.Has('m')) parts.Add(c.ToString());
                if (name != null) parts.Add(name);
                return string.Join(' ', parts);
            }
            foreach (var (name, text) in inputs)
            {
                long l = text.Count(ch => ch == '\n'), w = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                long c = a.Has('m') ? text.Length : Encoding.UTF8.GetByteCount(text);
                tl += l; tw += w; tc += c;
                ctx.Stdout.WriteLine(Row(l, w, c, a.Positional.Count > 0 ? name : null));
            }
            if (inputs.Count > 1) ctx.Stdout.WriteLine(Row(tl, tw, tc, "total"));
            return code;
        }, "Count lines, words and bytes");

        shell.Register("sort", ctx =>
        {
            var a = Args.Parse(ctx.Args, "rnufhVgsbZ", "kto",
                "reverse=r,numeric-sort=n,unique=u,ignore-case=f,key=k,field-separator=t,output=o,general-numeric-sort=g,human-numeric-sort=h,version-sort=V,stable=s,ignore-leading-blanks=b,debug=Z");
            int code = 0;
            var lines = Inputs(ctx, a.Positional, () => code = 1).SelectMany(i => SplitLines(i.Text)).ToList();
            var sep = a.Value('t');
            // -k START[,END] with per-key modifiers, e.g. -k2,2nr
            int kStart = 0, kEnd = int.MaxValue;
            var keyOpts = "";
            if (a.Value('k') is { } k)
            {
                var km = Regex.Match(k, @"^(\d+)(?:\.\d+)?([a-zA-Z]*)(?:,(\d+)(?:\.\d+)?([a-zA-Z]*))?$");
                if (!km.Success) throw new ShellException($"invalid key specification '{k}'", 2);
                kStart = int.Parse(km.Groups[1].Value);
                if (km.Groups[3].Success) kEnd = int.Parse(km.Groups[3].Value);
                keyOpts = km.Groups[2].Value + km.Groups[4].Value;
            }
            bool Opt(char c) => keyOpts.Length > 0 ? keyOpts.Contains(c) : a.Has(c);
            string Key(string line)
            {
                if (kStart == 0) return line;
                var fields = sep != null ? line.Split(sep[0]) : line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (kStart > fields.Length) return "";
                return string.Join(sep ?? " ", fields[(kStart - 1)..Math.Min(kEnd, fields.Length)]);
            }
            static double Num(string s)
            {
                var m = Regex.Match(s.TrimStart(), @"^-?\d+(\.\d+)?");
                return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : 0;
            }
            var cmp = Opt('f') ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            IEnumerable<string> sorted = Opt('n') || Opt('h') || Opt('g') || Opt('V')
                ? lines.OrderBy(l => Num(Key(l))).ThenBy(l => l, StringComparer.Ordinal)
                : lines.OrderBy(Key, cmp).ThenBy(l => l, StringComparer.Ordinal);
            if (Opt('r') || (keyOpts.Length > 0 && a.Has('r'))) sorted = sorted.Reverse();
            if (a.Has('u')) sorted = sorted.Distinct(a.Has('f') ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var output = string.Concat(sorted.Select(l => l + "\n"));
            if (a.Value('o') is { } outFile) ctx.Fs.WriteAllText(ctx.ResolvePath(outFile), output);
            else ctx.Stdout.Write(output);
            return code;
        }, "Sort lines");

        shell.Register("uniq", ctx =>
        {
            var a = Args.Parse(ctx.Args, "cdui", longs: "count=c,repeated=d,unique=u,ignore-case=i");
            var text = a.Positional.Count > 0 ? ctx.Fs.ReadAllText(ctx.ResolvePath(a.Positional[0])) : ctx.StdinText;
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
        }, "Collapse adjacent duplicate lines");

        shell.Register("cut", ctx =>
        {
            var a = Args.Parse(ctx.Args, "s", "dfc", "delimiter=d,fields=f,characters=c,only-delimited=s");
            var delim = a.Value('d') ?? "\t";
            if (delim.Length != 1) throw new ShellException("the delimiter must be a single character");
            int code = 0;
            foreach (var (_, text) in Inputs(ctx, a.Positional, () => code = 1))
                foreach (var line in SplitLines(text))
                {
                    if (a.Value('c') is { } chars) { ctx.Stdout.WriteLine(string.Concat(ParseList(chars).Where(i => i <= line.Length).Select(i => line[i - 1]))); continue; }
                    var spec = a.Value('f') ?? throw new ShellException("you must specify a list of bytes, characters, or fields");
                    if (!line.Contains(delim[0])) { if (!a.Has('s')) ctx.Stdout.WriteLine(line); continue; }
                    var fields = line.Split(delim[0]);
                    ctx.Stdout.WriteLine(string.Join(delim, ParseList(spec).Where(i => i <= fields.Length).Select(i => fields[i - 1])));
                }
            return code;
        }, "Select fields or characters from lines");

        shell.Register("tr", ctx =>
        {
            var a = Args.Parse(ctx.Args, "dsc", longs: "delete=d,squeeze-repeats=s");
            if (a.Positional.Count == 0) throw new ShellException("missing operand");
            var set1 = ExpandSet(a.Positional[0]);
            var set2 = a.Positional.Count > 1 ? ExpandSet(a.Positional[1]) : "";
            var sb = new StringBuilder();
            foreach (var ch in ctx.StdinText)
            {
                int idx = set1.IndexOf(ch);
                if (a.Has('d')) { if (idx < 0) sb.Append(ch); continue; }
                var outCh = idx >= 0 && set2.Length > 0 ? set2[Math.Min(idx, set2.Length - 1)] : ch;
                var squeezeSet = set2.Length > 0 ? set2 : set1;
                if (a.Has('s') && sb.Length > 0 && sb[^1] == outCh && squeezeSet.Contains(outCh)) continue;
                sb.Append(outCh);
            }
            ctx.Stdout.Write(sb.ToString());
            return 0;
        }, "Translate or delete characters");

        shell.Register("tee", ctx =>
        {
            var a = Args.Parse(ctx.Args, "a", longs: "append=a");
            foreach (var f in a.Positional)
                if (a.Has('a')) ctx.Fs.AppendAllText(ctx.ResolvePath(f), ctx.StdinText);
                else ctx.Fs.WriteAllText(ctx.ResolvePath(f), ctx.StdinText);
            ctx.Stdout.Write(ctx.StdinText);
            return 0;
        }, "Copy stdin to stdout and files");

        shell.Register("xargs", async ctx =>
        {
            // -I and -n take a value that may be a separate argument
            var opts = new List<string>();
            int i = 0;
            for (; i < ctx.Args.Count && ctx.Args[i].StartsWith('-'); i++)
            {
                opts.Add(ctx.Args[i]);
                if (ctx.Args[i] is "-I" or "-n" or "-d" && i + 1 < ctx.Args.Count) opts.Add(ctx.Args[++i]);
            }
            var a = Args.Parse(opts, "0rt", "Ind");
            var command = ctx.Args.Skip(i).ToList();
            if (command.Count == 0) command.Add("echo");
            var input = ctx.StdinText;
            if (a.Has('r') && input.Trim().Length == 0) return 0;

            int code = 0;
            if (a.Value('I') is { } replace)
            {
                foreach (var line in SplitLines(input).Where(l => l.Length > 0))
                {
                    var rc = await ctx.InvokeAsync(command.Select(c => c.Replace(replace, line)).ToList());
                    if (rc != 0) code = 123;
                }
                return code;
            }
            var items = a.Has('0') ? input.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                : a.Value('d') is { } d ? input.Split(Unescape(d)[0], StringSplitOptions.RemoveEmptyEntries)
                : input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int batch = a.Value('n') is { } n ? int.Parse(n) : Math.Max(1, items.Length);
            for (int k = 0; k < Math.Max(1, items.Length); k += batch)
            {
                var rc = await ctx.InvokeAsync([.. command, .. items.Skip(k).Take(batch)]);
                if (rc != 0) code = 123;
            }
            return code;
        }, "Build and run commands from stdin");

        shell.Register("sed", ctx => Sed.Run(ctx), "Stream editor (s///, p, d, q with line/regex addresses)");
    }

    static string ExpandSet(string spec)
    {
        spec = spec.Replace("[:upper:]", "A-Z").Replace("[:lower:]", "a-z").Replace("[:digit:]", "0-9")
            .Replace("[:alpha:]", "a-zA-Z").Replace("[:alnum:]", "a-zA-Z0-9").Replace("[:space:]", " \t\n\r\f\v");
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

internal static class Sed
{
    abstract record Addr;
    sealed record LineAddr(int Line) : Addr;
    sealed record LastAddr : Addr;
    sealed record RegexAddr(Regex Re) : Addr;

    sealed class Cmd
    {
        public Addr? A1, A2;
        public bool Negate, InRange;
        public char Op;
        public Regex? Find;
        public string Replace = "";
        public bool Global, Print;
        public string Text = "";
    }

    public static int Run(CommandContext ctx)
    {
        var a = Args.Parse(ctx.Args, "niEr", "e", "quiet=n,silent=n,in-place=i,regexp-extended=E,expression=e");
        var positional = a.Positional.ToList();
        var scripts = new List<string>();
        // Args keeps only the last -e; collect them all
        for (int i = 0; i < ctx.Args.Count - 1; i++) if (ctx.Args[i] is "-e" or "--expression") scripts.Add(ctx.Args[i + 1]);
        if (scripts.Count == 0)
        {
            if (positional.Count == 0) throw new ShellException("usage: sed [-n] [-i] [-E] SCRIPT [FILE]...", 1);
            scripts.Add(positional[0]);
            positional.RemoveAt(0);
        }
        bool extended = a.Has('E') || a.Has('r');
        var cmds = scripts.SelectMany(s => Parse(s, extended)).ToList();

        if (a.Has('i'))
        {
            if (positional.Count == 0) throw new ShellException("no input files");
            foreach (var f in positional)
            {
                var path = ctx.ResolvePath(f);
                var output = new StringWriter();
                Execute(cmds, ctx.Fs.ReadAllText(path), output, a.Has('n'));
                ctx.Fs.WriteAllText(path, output.ToString());
            }
            return 0;
        }
        int code = 0;
        var text = string.Concat(Builtins.Inputs(ctx, positional, () => code = 2).Select(i => i.Text));
        Execute(cmds, text, ctx.Stdout, a.Has('n'));
        return code;
    }

    static void Execute(List<Cmd> cmds, string text, TextWriter output, bool quiet)
    {
        var lines = Builtins.SplitLines(text);
        bool trailingNewline = text.EndsWith('\n');
        foreach (var c in cmds) c.InRange = false;
        for (int n = 0; n < lines.Count; n++)
        {
            var line = lines[n];
            bool deleted = false, quit = false;
            bool last = n == lines.Count - 1;
            foreach (var c in cmds)
            {
                if (!Matches(c, line, n + 1, last)) continue;
                switch (c.Op)
                {
                    case 's':
                        var replaced = c.Global ? c.Find!.Replace(line, c.Replace) : c.Find!.Replace(line, c.Replace, 1);
                        if (c.Print && c.Find.IsMatch(line)) output.Write(replaced + "\n");
                        line = replaced;
                        break;
                    case 'p': output.Write(line + "\n"); break;
                    case 'd': deleted = true; break;
                    case 'q': quit = true; break;
                    case 'a': line += "\n" + c.Text; break;
                    case 'i': line = c.Text + "\n" + line; break;
                    case 'c': line = c.Text; break;
                }
                if (deleted || quit) break;
            }
            if (!deleted && !quiet) output.Write(line + (last && !trailingNewline ? "" : "\n"));
            if (quit) break;
        }
    }

    static bool Matches(Cmd c, string line, int n, bool last)
    {
        bool Hit(Addr a) => a switch { LineAddr l => l.Line == n, LastAddr => last, RegexAddr r => r.Re.IsMatch(line), _ => false };
        bool result;
        if (c.A1 == null) result = true;
        else if (c.A2 == null) result = Hit(c.A1);
        else if (c.InRange)
        {
            if (c.A2 is LineAddr end ? n >= end.Line : Hit(c.A2)) c.InRange = false;
            result = true;
        }
        else if (Hit(c.A1))
        {
            c.InRange = !(c.A2 is LineAddr end && end.Line <= n);
            result = true;
        }
        else result = false;
        return result != c.Negate;
    }

    static List<Cmd> Parse(string script, bool extended)
    {
        var cmds = new List<Cmd>();
        int i = 0;
        string ReadDelimited(char delim)
        {
            var sb = new StringBuilder();
            for (; i < script.Length && script[i] != delim; i++)
            {
                if (script[i] == '\\' && i + 1 < script.Length)
                {
                    if (script[i + 1] == delim) { sb.Append(delim); i++; continue; }
                    sb.Append('\\');
                    i++;
                }
                sb.Append(script[i]);
            }
            if (i >= script.Length) throw new ShellException($"unterminated `s' command");
            i++;
            return sb.ToString();
        }
        Regex MakeRegex(string re, bool ignoreCase) =>
            new(extended ? re : Builtins.BreToNet(re), ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        Addr? ReadAddr()
        {
            if (i >= script.Length) return null;
            if (char.IsDigit(script[i]))
            {
                int s = i;
                while (i < script.Length && char.IsDigit(script[i])) i++;
                return new LineAddr(int.Parse(script[s..i]));
            }
            if (script[i] == '$') { i++; return new LastAddr(); }
            if (script[i] == '/') { i++; return new RegexAddr(MakeRegex(ReadDelimited('/'), false)); }
            return null;
        }

        while (i < script.Length)
        {
            while (i < script.Length && (char.IsWhiteSpace(script[i]) || script[i] == ';')) i++;
            if (i >= script.Length) break;
            var c = new Cmd { A1 = ReadAddr() };
            if (c.A1 != null && i < script.Length && script[i] == ',') { i++; c.A2 = ReadAddr() ?? throw new ShellException("unexpected `,'"); }
            while (i < script.Length && script[i] == ' ') i++;
            if (i < script.Length && script[i] == '!') { c.Negate = true; i++; }
            if (i >= script.Length) throw new ShellException("missing command");
            c.Op = script[i++];
            switch (c.Op)
            {
                case 's':
                    char delim = script[i++];
                    var find = ReadDelimited(delim);
                    var repl = ReadDelimited(delim);
                    bool ignoreCase = false;
                    while (i < script.Length && script[i] is 'g' or 'i' or 'I' or 'p')
                    {
                        switch (script[i++]) { case 'g': c.Global = true; break; case 'p': c.Print = true; break; default: ignoreCase = true; break; }
                    }
                    c.Find = MakeRegex(find, ignoreCase);
                    c.Replace = ConvertReplacement(repl);
                    break;
                case 'p' or 'd' or 'q': break;
                case 'a' or 'i' or 'c':
                    int end = script.IndexOf('\n', i);
                    c.Text = script[i..(end < 0 ? script.Length : end)].TrimStart().TrimStart('\\').TrimStart();
                    i = end < 0 ? script.Length : end;
                    break;
                default: throw new ShellException($"unknown command: `{c.Op}' (supported: s p d q a i c)");
            }
            cmds.Add(c);
        }
        return cmds;
    }

    static string ConvertReplacement(string r)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < r.Length; i++)
        {
            char c = r[i];
            if (c == '\\' && i + 1 < r.Length)
            {
                char n = r[++i];
                sb.Append(char.IsDigit(n) ? "${" + n + "}" : n == 'n' ? "\n" : n == 't' ? "\t" : n == '&' ? "&" : n == '$' ? "$$" : n.ToString());
            }
            else if (c == '&') sb.Append("$0");
            else if (c == '$') sb.Append("$$");
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
