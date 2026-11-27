// Shell: word expansion, parameters, arithmetic, substitution and field splitting. See docs/architecture.md.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Topwater.Parsing;

namespace Topwater;

public sealed partial class Shell
{
    // ---------- parts and arithmetic ----------

    internal async Task<string> ExpandPartAsync(WordPart part, Io io, CancellationToken ct) => part switch
    {
        LiteralPart l => l.Text,
        ExpandedTextPart e => e.Text,
        VariablePart v => await ExpandParameterAsync(v.Name, io, ct, v.Quoted),
        CommandSubPart c => await CaptureAsync(c.Source, io, ct),
        ArithPart a => (await ArithExpansionAsync(a.Expr, io, ct)).ToString(CultureInfo.InvariantCulture),
        ProcSubPart p => await ProcessSubstitutionAsync(p.Source, io, ct),
        _ => "",
    };

    /// <summary>Expands $(cmd), `cmd` and ${...} inside an arithmetic expression, then evaluates it.</summary>
    async Task<long> EvalArithAsync(string expr, Io io, CancellationToken ct)
    {
        // each level of $(( $(( ... )) )) re-reads what is inside it, so unbounded nesting is quadratic work
        if (++_arithDepth > 100) { _arithDepth = 0; throw new LimitExceeded("arithmetic is nested too deeply"); }
        try { return Arith.Eval(this, expr.IndexOfAny(['$', '`']) < 0 ? expr : await ExpandSingleAsync(new Word(new Lexer(expr).LexQuoted(null)), io, ct)); }
        finally { if (_arithDepth > 0) _arithDepth--; }
    }
    /// <summary>$(( )): unlike (( )) and let, an error here ends the script, as in bash.</summary>
    async Task<long> ArithExpansionAsync(string expr, Io io, CancellationToken ct)
    {
        try { return await EvalArithAsync(expr, io, ct); }
        catch (ShellException e) { throw Fatal(io, e.Message, 1); }
    }

    // ---------- parameters ----------

    /// <summary>${x@}: the text bash names is the whole word, or the whole double-quoted string the expansion is in.</summary>
    /// <param name="ends">A bad @ operator on a set variable ends the script (127); any other drops the rest of the line.</param>
    internal sealed class BadSubstitution(string text, bool ends = false) : FatalError(CommandSpec.Format(CommandSpec.Diagnostics.BadSubstitution, ("text", text)), 127)
    {
        public bool Ends { get; } = ends;
    }

    /// <summary>Reports a bad substitution and unwinds as bash does: the script ends, or the rest of the line is dropped.</summary>
    Exception BadSubstitutionStop(Io io, string message, bool ends)
    {
        if (ends) return Fatal(io, message, 127);
        io.Err.WriteLine($"{ErrPrefix}{message}");
        LastExitCode = 1;
        return new LineAbort();
    }

    /// <summary>Expands parts[i]; a bad substitution there ends the script, naming the word or quoted string around it.</summary>
    async Task<string> ExpandPartInAsync(IReadOnlyList<WordPart> parts, int i, Io io, CancellationToken ct)
    {
        try { return await ExpandPartAsync(parts[i], io, ct); }
        catch (BadSubstitution e)
        {
            static bool InString(WordPart p) => p.Quoted && p is not LiteralPart { Verbatim: true };
            int a = 0, b = parts.Count - 1;
            if (InString(parts[i]))
            {
                for (a = i; a > 0 && InString(parts[a - 1]); a--) { }
                for (b = i; b + 1 < parts.Count && InString(parts[b + 1]); b++) { }
            }
            // shortcut: rebuilt from the parsed parts, so $x reads ${x} and quotes inside an unquoted word are gone; keep source spans if that matters
            throw BadSubstitutionStop(io, CommandSpec.Format(CommandSpec.Diagnostics.BadSubstitution, ("text", new Word(parts.Skip(a).Take(b - a + 1).ToList()).ToString())), e.Ends);
        }
    }

    /// <summary>An expansion error that ends a non-interactive bash: the shell exits (a subshell with status 1).</summary>
    ExitSignal Fatal(Io io, string message, int code)
    {
        io.Err.WriteLine($"{ErrPrefix}{message}");
        return new ExitSignal(_subshell > 0 ? 1 : code);
    }

    static readonly Regex ParamRe = new(@"^([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!-])(.*)$", RegexOptions.Singleline);
    static readonly Regex IndirectRe = new(@"^!([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!-])(.*)$", RegexOptions.Singleline);
    static readonly Regex PrefixRe = new(@"^!([A-Za-z_][A-Za-z0-9_]*)[*@]$");

    /// <summary>${p-w} ${p:-w} ${p=w} ${p:=w} ${p+w} ${p:+w} ${p?w} ${p:?w}: the operator and the word; null for any other operator.</summary>
    static (string Op, string Word)? DefaultOp(string rest)
    {
        int n = rest.Length > 0 && rest[0] == ':' ? 2 : 1;
        return rest.Length >= n && rest[n - 1] is '-' or '=' or '+' or '?' ? (rest[..n], rest[n..]) : null;
    }

    /// <summary>${!name...}: the expression with name's value in its place; null if expr is not an indirection.</summary>
    string? IndirectTarget(string expr)
    {
        if (expr.Length < 2 || expr[0] != '!' || PrefixRe.IsMatch(expr) || ArrayRef(expr) is { Keys: true } || IndirectRe.Match(expr) is not { Success: true } m) return null;
        var name = m.Groups[1].Value;
        if (HasAttr(name, 'n')) return null; // ${!ref} of a nameref is the name it refers to
        var target = GetVariable(name);
        if (target.Length == 0) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.InvalidIndirect, ("name", name)));
        return target + m.Groups[2].Value;
    }

    /// <summary>The word of ${x op word}, lexed once per distinct text: quotes in it apply, and inside "..." a ' is literal.</summary>
    Word ParamWord(string text, bool quoted)
    {
        if (_paramWords.TryGetValue((text, quoted), out var w)) return w;
        if (_paramWords.Count >= 512) _paramWords.Clear();
        try { w = new Word(new Lexer(text).LexParamWord(quoted)); }
        catch (ParseException e) { throw new ShellException(e.Message); }
        return _paramWords[(text, quoted)] = w;
    }

    Task<string> ParamWordAsync(string text, bool quoted, Io io, CancellationToken ct) => ExpandSingleAsync(ParamWord(text, quoted), io, ct);

    /// <summary>The text of ${x/pat/rep}'s replacement as a .NET replacement: an unquoted &amp; stands for the match (bash 5.2).</summary>
    async Task<string> ReplacementAsync(string text, bool quoted, Io io, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var part in ParamWord(text, quoted).Parts)
        {
            var t = await ExpandPartAsync(part, io, ct);
            if (sb.Length == 0 && part is LiteralPart { Quoted: false } && TildePrefix(t) is var (home, length)) t = home + t[length..];
            t = t.Replace("$", "$$");
            sb.Append(part.Quoted ? t : t.Replace("&", "$0"));
        }
        return sb.ToString();
    }

    /// <summary>${name}, ${#name}, ${name:-x}, ${name%pat}, ${name/a/b}, ${name:1:2}, ${name^^}, ${name@Q}, ${!name} ...</summary>
    async Task<string> ExpandParameterAsync(string expr, Io io, CancellationToken ct, bool quoted = false)
    {
        if (expr.Length > 1 && expr[0] == '!')
        {
            if (PrefixRe.Match(expr) is { Success: true } pm)
                return string.Join(' ', Environment.Keys.Concat(Arrays.Keys).Where(k => k.StartsWith(pm.Groups[1].Value, StringComparison.Ordinal) && k != ScratchVariable)
                    .Distinct().Order(StringComparer.Ordinal));
            if (Parser.IsName(expr[1..]) && HasAttr(expr[1..], 'n')) return Environment.GetValueOrDefault(expr[1..], "");
            if (IndirectTarget(expr) is { } target) return await ExpandParameterAsync(target, io, ct, quoted);
        }
        if (expr.Length > 1 && expr[0] == '#' && ArrayRef(expr[1..]) is { Keys: false, Tail: "" } counted)
        {
            if (counted.Sub is "@" or "*")
                return (GetArray(counted.Name) is { } a ? a.Items.Count : LookUp(counted.Name) != null ? 1 : 0).ToString(CultureInfo.InvariantCulture);
            return (GetElement(counted.Name, await SubscriptAsync(counted.Sub, io, ct)) ?? "").Length.ToString(CultureInfo.InvariantCulture);
        }
        // as one string, ${a[*]} joins with the first character of IFS and ${a[@]} with a space
        if (await ArrayElementsAsync(expr, io, ct, quoted) is { } all)
            return string.Join(ArrayRef(expr)!.Value.Sub == "*" ? IfsJoiner : " ", all);
        if (ArrayRef(expr) is { Keys: false } one)
        {
            string? element;
            try { element = GetElement(one.Name, await SubscriptAsync(one.Sub, io, ct)); }
            catch (ShellException e) when (e is not FatalError) { io.Err.WriteLine($"{ErrPrefix}{e.Message}"); element = null; } // like bash: reported, and the expansion is empty
            return await ElementOpAsync(element, one.Tail, io, ct, quoted);
        }
        if (expr.Length > 1 && expr[0] == '#')
        {
            if (expr[1..] is "@" or "*") return Positional.Count.ToString(CultureInfo.InvariantCulture);
            CheckSet(expr[1..], io);
            return GetVariable(expr[1..]).Length.ToString(CultureInfo.InvariantCulture);
        }
        var m = ParamRe.Match(expr);
        if (!m.Success) throw new BadSubstitution("${" + expr + "}");
        var name = m.Groups[1].Value;
        var rest = m.Groups[2].Value;
        var value = GetVariable(name);
        var defaultOp = DefaultOp(rest);
        if (defaultOp == null) CheckSet(name, io); // defaults handle unset variables
        if (rest.Length == 0) return value;

        if (defaultOp is var (op, word))
        {
            bool missing = op[0] == ':' ? value.Length == 0 : !IsSet(name);
            switch (op[^1])
            {
                case '-': return missing ? await ParamWordAsync(word, quoted, io, ct) : value;
                case '=':
                    if (!missing) return value;
                    if (!Parser.IsName(name)) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.CannotAssign, ("name", name)));
                    SetVar(name, await ParamWordAsync(word, quoted, io, ct));
                    return GetVariable(name);
                case '+': return missing ? "" : await ParamWordAsync(word, quoted, io, ct);
                default:
                    if (missing) throw Fatal(io, $"{name}: {(word.Length > 0 ? await ParamWordAsync(word, quoted, io, ct) : op[0] == ':' ? "parameter null or not set" : "parameter not set")}", 127);
                    return value;
            }
        }
        if (rest[0] is '#' or '%')
        {
            bool longest = rest.Length > 1 && rest[1] == rest[0];
            var re = Glob.ToRegex(await PatternAsync(ParamWord(rest[(longest ? 2 : 1)..], quoted), io, ct), slashes: true);
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
        if (rest[0] == '/')
        {
            var body = rest[1..];
            char mode = body.Length > 0 && body[0] is '/' or '#' or '%' ? body[0] : ' ';
            if (mode != ' ') body = body[1..];
            // the pattern ends at the first unquoted '/'; after `//` a leading '/' is part of the pattern
            int slash = -1;
            for (int i = mode == '/' && body.StartsWith('/') ? 1 : 0; i < body.Length; i++)
            {
                if (body[i] == '\\') { i++; continue; }
                if (body[i] == '\'' && !quoted) { i = body.IndexOf('\'', i + 1) is var q and >= 0 ? q : body.Length; continue; }
                if (body[i] == '"') { for (i++; i < body.Length && body[i] != '"'; i++) if (body[i] == '\\') i++; continue; }
                if (body[i] == '/') { slash = i; break; }
            }
            var pat = Glob.ToRegexBody(await PatternAsync(ParamWord(slash < 0 ? body : body[..slash], quoted), io, ct), slashes: true);
            var rep = slash < 0 ? "" : await ReplacementAsync(body[(slash + 1)..], quoted, io, ct);
            // an empty pattern matches nothing, except at an anchor: ${x/#/pre-} and ${x/%/-post} add to the ends
            if (pat.Length == 0) return mode == '#' ? rep.Replace("$$", "$") + value : mode == '%' ? value + rep.Replace("$$", "$") : value;
            var re = Glob.Cached(mode == '#' ? "^(?:" + pat + ")" : mode == '%' ? "(?:" + pat + ")$" : pat, RegexOptions.Singleline);
            // ${x//?/$x} grows with the square of x: refuse before building it
            if (mode == '/' && value.Length + (long)rep.Length * (value.Length + 1) > Options.MaxStringBytes
                && value.Length + (long)rep.Length * re.Count(value) > Options.MaxStringBytes)
                throw TooLarge("expansion", "write the data to a file instead");
            return re.Replace(value, rep, mode == '/' ? -1 : 1);
        }
        if (rest[0] is '^' or ',' or '~')
        {
            // ${x^} ${x^^} ${x,} ${x,,} ${x~} ${x~~}, optionally only for characters matching a pattern: ${x^^[aeiou]}
            bool every = rest.Length > 1 && rest[1] == rest[0];
            var only = rest.Length > (every ? 2 : 1) ? Glob.ToRegex(await PatternAsync(ParamWord(rest[(every ? 2 : 1)..], quoted), io, ct), slashes: true) : null;
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length && (every || i == 0); i++)
                if (only == null || only.IsMatch(chars[i].ToString()))
                    chars[i] = rest[0] == '^' || rest[0] == '~' && char.IsLower(chars[i]) ? char.ToUpperInvariant(chars[i]) : char.ToLowerInvariant(chars[i]);
            return new string(chars);
        }
        if (rest.Length == 2 && rest[0] == '@')
        {
            if (rest[1] == 'a') return Builtins.AttributeFlags(this, Resolve(name));
            if (!IsSet(name)) return "";
            return rest[1] switch
            {
                'Q' or 'K' => QuoteValue(value),
                'E' => Builtins.Unescape(value, keepUnknown: true),
                'U' => value.ToUpperInvariant(),
                'L' => value.ToLowerInvariant(),
                'u' => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..],
                'A' => Builtins.AttributeFlags(this, Resolve(name)) is { Length: > 0 } flags ? $"declare -{flags} {name}={QuoteValue(value)}" : $"{name}={QuoteValue(value)}",
                'P' => value,
                _ => throw new BadSubstitution("${" + expr + "}", ends: true),
            };
        }
        if (rest[0] == ':')
        {
            var spec = rest[1..].Split(':', 2);
            long offset = await EvalArithAsync(spec[0], io, ct);
            if (offset < 0) offset += value.Length;
            if (offset < 0 || offset > value.Length) return "";
            var tail = value[(int)offset..];
            if (spec.Length == 1) return tail;
            long len = await EvalArithAsync(spec[1], io, ct);
            if (len < 0 && (len += tail.Length) < 0) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.SubstringNegative, ("expression", spec[1].Trim())));
            return tail[..(int)Math.Min(len, tail.Length)];
        }
        if (rest == "@" && !IsSet(name)) return ""; // bash looks at the operator only when there is a value
        throw new BadSubstitution("${" + expr + "}", ends: rest.StartsWith('@'));
    }

    /// <summary>set -u: expanding an unset variable is an error that ends the (sub)shell, like bash.</summary>
    void CheckSet(string name, Io io)
    {
        if (!Nounset || name is "@" or "*" || IsSet(name)) return;
        throw Fatal(io, CommandSpec.Format(CommandSpec.Diagnostics.UnboundVariable, ("name", name)), 127);
    }

    // ---------- substitution ----------

    /// <summary>&lt;(cmd): writes cmd's output to /dev/fd/N (removed after the tool call) and returns that path.</summary>
    /// <summary>$(...) and &lt;(...) run in a subshell that, as in bash, does not inherit set -e (unless shopt inherit_errexit).</summary>
    async Task<int> RunSubstitutionAsync(string source, Io io, CancellationToken ct)
    {
        var errexit = Errexit;
        if (!Shopts.Contains("inherit_errexit")) Errexit = false;
        try { return await RunScriptTextAsync(source, io, subshell: true, ct, substitution: true); }
        finally { Errexit = errexit; }
    }

    async Task<string> ProcessSubstitutionAsync(string source, Io io, CancellationToken ct)
    {
        var output = new CappedWriter(this);
        await RunSubstitutionAsync(source, io with { Stdin = new StdinBuffer(""), Out = output }, ct);
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
                io.Err.WriteLine($"{ErrPrefix}{target}: {e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]}");
                LastExitCode = 1;
                return "";
            }
        }
        var output = new CappedWriter(this);
        LastExitCode = await RunSubstitutionAsync(source, io with { Stdin = new StdinBuffer(""), Out = output }, ct);
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

    // ---------- words ----------

    /// <summary>Expands a word without field splitting or globbing (redirect targets, heredocs, case subjects).</summary>
    internal async Task<string> ExpandSingleAsync(Word word, Io io, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < word.Parts.Count; i++)
        {
            var text = await ExpandPartInAsync(word.Parts, i, io, ct);
            if (i == 0 && word.Parts[i] is LiteralPart { Quoted: false } && TildePrefix(text) is var (home, length)) text = home + text[length..];
            if (sb.Length + (long)text.Length > Options.MaxStringBytes) throw TooLarge("expansion", "write the data to a file instead");
            sb.Append(text);
        }
        return sb.ToString();
    }

    static readonly Regex AssignmentTilde = new(@"(?<=^|:)~([A-Za-z0-9_.-]*)(?=/|:|$)");

    /// <summary>An assignment's value: one string, with ~ expanded at the start and after each ':' (PATH=~/bin:~/x).</summary>
    internal async Task<string> ExpandAssignmentAsync(Word word, Io io, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < word.Parts.Count; i++)
        {
            var text = await ExpandPartInAsync(word.Parts, i, io, ct);
            if (word.Parts[i] is LiteralPart { Quoted: false } && text.Contains('~'))
            {
                bool atStart = i == 0 || sb.Length > 0 && sb[^1] == ':';
                text = AssignmentTilde.Replace(text, m => (m.Index == 0 && !atStart ? null : TildeValue(m.Groups[1].Value)) ?? m.Value);
            }
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

    /// <summary>~, ~user, ~+ and ~-: the directory they name, or null for an unknown user.</summary>
    string? TildeValue(string user) => user switch
    {
        "" => Environment.GetValueOrDefault("HOME", "/"),
        "+" => _cwd,
        "-" => Environment.GetValueOrDefault("OLDPWD"),
        "root" => "/root",
        _ when user == Options.User => "/home/" + user,
        _ => null,
    };

    /// <summary>A leading ~ or ~user (up to the first '/'): its directory and how many characters it spans.</summary>
    (string Home, int Length)? TildePrefix(string text)
    {
        if (text.Length == 0 || text[0] != '~') return null;
        int end = text.IndexOf('/');
        if (end < 0) end = text.Length;
        return TildeValue(text[1..end]) is { } home ? (home, end) : null;
    }

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
        var fields = Glob.Cached(sep, RegexOptions.None).Split(text, Math.Max(0, maxFields)).ToList();
        if (maxFields <= 0 && other.Length > 0 && fields.Count > 0 && fields[^1].Length == 0) fields.RemoveAt(fields.Count - 1); // "a,b," is two fields
        return fields;
    }

    sealed class Field
    {
        public readonly StringBuilder Text = new();
        public readonly StringBuilder Pattern = new();
        public bool HasGlob, HasContent;
    }

    /// <summary>
    /// An unquoted ${p:-word} or ${p:+word} whose word applies: the word's parts, so quotes inside the word still keep
    /// it from being split (${v:-"a b"} is one field). Null when p's own value applies.
    /// </summary>
    async Task<IReadOnlyList<WordPart>?> AlternativeWordAsync(string expr, Io io, CancellationToken ct)
    {
        var element = ArrayRef(expr);
        var match = element == null ? ParamRe.Match(expr) : null;
        var rest = element is { Keys: false } r && r.Sub is not ("@" or "*") ? r.Tail : match is { Success: true } ? match.Groups[2].Value : "";
        if (DefaultOp(rest) is not var (op, word) || op[^1] is not ('-' or '+')) return null;
        var value = element != null ? GetElement(element.Value.Name, await SubscriptAsync(element.Value.Sub, io, ct))
            : IsSet(match!.Groups[1].Value) ? GetVariable(match.Groups[1].Value) : null;
        bool missing = op[0] == ':' ? string.IsNullOrEmpty(value) : value == null;
        if (op[^1] == '-' ? !missing : missing) return op[^1] == '-' ? null : [];
        // the word's unquoted text is split like any expansion's
        return ParamWord(word, false).Parts.Select(p => p is LiteralPart { Quoted: false } l ? new ExpandedTextPart(l.Text) : p).ToList();
    }

    internal async Task<List<string>> ExpandWordsAsync(IEnumerable<Word> words, Io io, CancellationToken ct)
    {
        var result = new List<string>();
        long total = 0; // all the words together, so {1..10000}$big can't multiply a big value
        foreach (var word in words.SelectMany(Brace.Expand))
        {
            var fields = new List<Field>();
            var cur = new Field();
            void End() { if (cur.HasContent) fields.Add(cur); cur = new Field(); }
            void AddText(string s, bool quoted)
            {
                if ((total += s.Length) > Options.MaxStringBytes) throw TooLarge("expansion", "write the data to a file instead");
                cur.Text.Append(s);
                cur.Pattern.Append(quoted ? Glob.Escape(s) : s);
                if (!quoted && Glob.HasWildcard(s)) cur.HasGlob = true;
                cur.HasContent = true;
            }
            // unquoted $VAR / $(cmd) / $((...)): split into fields on $IFS
            void AddSplit(string text)
            {
                var ifs = Ifs;
                var pieces = SplitFields(text, ifs);
                if (pieces.Count == 0) { if (text.Length > 0 && ifs.Length > 0) End(); return; }
                if (ifs.Contains(text[0])) End();
                for (int k = 0; k < pieces.Count; k++)
                {
                    if (k > 0) End();
                    AddText(pieces[k], false);
                }
                if (ifs.Contains(text[^1])) End();
            }
            // "$@" / "${a[@]}": one field per item, and none at all when there are none; unquoted, each item is split
            void AddEach(IReadOnlyList<string> items, bool quoted)
            {
                for (int k = 0; k < items.Count; k++)
                {
                    if (k > 0) End();
                    if (quoted) AddText(items[k], true); else AddSplit(items[k]);
                }
            }

            IReadOnlyList<WordPart> parts = word.Parts;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part is VariablePart vp)
                {
                    var name = IndirectTarget(vp.Name) ?? vp.Name;
                    if (name == "@" || name == "*" && !vp.Quoted) { AddEach(Positional, vp.Quoted); continue; }
                    if (name is ['@' or '*', ':', .. var slice] && DefaultOp(name[1..]) == null)
                    {
                        // ${@:2} and ${@:2:3} slice the parameters, not the characters; ${@:0} starts at $0
                        var spec = slice.Split(':', 2);
                        List<string> all = ["bash", .. Positional];
                        long from = await EvalArithAsync(spec[0], io, ct);
                        if (from < 0) from += all.Count;
                        long count = spec.Length == 1 ? all.Count : await EvalArithAsync(spec[1], io, ct);
                        var items = from < 0 || from > all.Count ? [] : all.Skip((int)from).Take((int)Math.Clamp(count, 0, all.Count)).ToList();
                        if (vp.Quoted && name[0] == '*') AddText(string.Join(IfsJoiner, items), true); else AddEach(items, vp.Quoted);
                        continue;
                    }
                    if (ArrayRef(name) is { Sub: "@" or "*" } ar && (ar.Sub == "@" || !vp.Quoted) && await ArrayElementsAsync(name, io, ct, vp.Quoted) is { } elements)
                    {
                        AddEach(elements, vp.Quoted);
                        continue;
                    }
                    if (!vp.Quoted && name.AsSpan().IndexOfAny('-', '+') >= 0 && await AlternativeWordAsync(name, io, ct) is { } alternative)
                    {
                        parts = [.. parts.Take(i), .. alternative, .. parts.Skip(i + 1)];
                        i--;
                        continue;
                    }
                    if (name != vp.Name) part = vp with { Name = name };
                }
                var text = part == parts[i] ? await ExpandPartInAsync(parts, i, io, ct) : await ExpandPartAsync(part, io, ct);
                if (part is LiteralPart)
                {
                    if (i == 0 && !part.Quoted && TildePrefix(text) is var (home, length))
                    {
                        AddText(home, true); // the home path is never globbed
                        text = text[length..];
                        if (text.Length == 0) continue;
                    }
                    AddText(text, part.Quoted);
                    continue;
                }
                if (part.Quoted) AddText(text, true);
                else AddSplit(text);
            }
            End();

            foreach (var f in fields)
            {
                var matches = f.HasGlob && !Noglob ? Glob.Expand(this, f.Pattern.ToString()) : [];
                if (matches.Count > 0) result.AddRange(matches);
                else if (!(f.HasGlob && !Noglob && Shopts.Contains("nullglob"))) result.Add(f.Text.ToString());
            }
        }
        return result;
    }
}
