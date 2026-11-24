using System.Text;

namespace Topwater.Parsing;

public sealed class ParseException(string message) : Exception(message);

internal abstract record WordPart(bool Quoted);
internal sealed record LiteralPart(string Text, bool Quoted) : WordPart(Quoted);
/// <summary>$name or ${expr} (expr may contain operators like :- or %%).</summary>
internal sealed record VariablePart(string Name, bool Quoted) : WordPart(Quoted);
internal sealed record CommandSubPart(string Source, bool Quoted) : WordPart(Quoted);
internal sealed record ArithPart(string Expr, bool Quoted) : WordPart(Quoted);
/// <summary>&lt;(cmd): replaced by the path of a file holding cmd's output.</summary>
internal sealed record ProcSubPart(string Source) : WordPart(true);

internal sealed record Word(IReadOnlyList<WordPart> Parts)
{
    public static Word Lit(string text) => new([new LiteralPart(text, false)]);
    public bool Is(string text) => Parts is [LiteralPart { Quoted: false } l] && l.Text == text;
    public string? UnquotedLiteral => Parts is [LiteralPart { Quoted: false } l] ? l.Text : null;
    public override string ToString() => string.Concat(Parts.Select(p => p switch
    {
        LiteralPart l => l.Text,
        VariablePart v => "${" + v.Name + "}",
        CommandSubPart c => "$(" + c.Source + ")",
        ArithPart a => "$((" + a.Expr + "))",
        ProcSubPart ps => "<(" + ps.Source + ")",
        _ => ""
    }));
}

internal sealed class HereDoc(string delimiter, bool expand, bool stripTabs)
{
    public string Delimiter { get; } = delimiter;
    public bool Expand { get; } = expand;
    public bool StripTabs { get; } = stripTabs;
    public Word Body { get; set; } = new([new LiteralPart("", true)]);
}

internal sealed record Redirect(int Fd, string Op, Word? Target, HereDoc? Here);

internal abstract record Command
{
    public List<Redirect> Redirects { get; init; } = [];
}
internal sealed record SimpleCommand(List<(string Name, Word Value)> Assignments, List<Word> Words) : Command;
internal sealed record GroupCommand(Script Body, bool Subshell) : Command;
internal sealed record IfCommand(List<(Script Cond, Script Body)> Branches, Script? Else) : Command;
internal sealed record LoopCommand(Script Cond, Script Body, bool Until) : Command;
internal sealed record ForCommand(string Var, List<Word>? Items, Script Body) : Command;
internal sealed record ArithForCommand(string Init, string Cond, string Step, Script Body) : Command;
internal sealed record CaseCommand(Word Subject, List<(List<Word> Patterns, Script Body)> Clauses) : Command;
internal sealed record FunctionDef(string Name, Command Body) : Command;
internal sealed record ArithCommand(string Expr) : Command;
/// <summary>[[ ... ]]: operands are words; operators (&amp;&amp; || ! ( ) == ...) are unquoted literal words.</summary>
internal sealed record CondCommand(List<Word> Items) : Command;

internal sealed record Pipeline(List<Command> Commands, bool Negate);
internal sealed record AndOr(Pipeline First, List<(string Op, Pipeline Next)> Rest);
internal sealed record Script(List<AndOr> Items);

internal enum TokenKind { Word, Op, Redirect, Arith }

internal sealed record Token(TokenKind Kind, string Op = "", Word? Word = null, Redirect? Redirect = null)
{
    public override string ToString() => Kind switch
    {
        TokenKind.Word => Word!.ToString(),
        TokenKind.Op => Op == "\n" ? "newline" : Op,
        TokenKind.Arith => "((",
        _ => Redirect!.Op,
    };
}

/// <summary>Clean-room lexer for a practical subset of POSIX sh / bash syntax.</summary>
internal sealed class Lexer(string s)
{
    int _i;
    readonly List<HereDoc> _pending = [];

    char Peek(int o = 0) => _i + o < s.Length ? s[_i + o] : '\0';

    public List<Token> Lex()
    {
        var tokens = new List<Token>();
        while (true)
        {
            while (Peek() is ' ' or '\t' or '\r') _i++;
            if (Peek() == '\\' && Peek(1) == '\n') { _i += 2; continue; }
            if (_i >= s.Length) break;
            char c = s[_i];
            if (c == '#') { while (_i < s.Length && s[_i] != '\n') _i++; continue; }
            if (c == '\n') { _i++; tokens.Add(new(TokenKind.Op, "\n")); ReadHereDocBodies(); continue; }
            if (char.IsDigit(c))
            {
                int j = _i;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                if (j < s.Length && s[j] is '>' or '<') { int fd = int.Parse(s[_i..j]); _i = j; tokens.Add(LexRedirect(fd)); continue; }
            }
            if (c == '&' && Peek(1) == '>')
            {
                _i += 2;
                bool append = Peek() == '>';
                if (append) _i++;
                tokens.Add(new(TokenKind.Redirect, Redirect: new(1, append ? "&>>" : "&>", null, null)));
                continue;
            }
            if (tokens.Count > 0 && tokens[^1] is { Kind: TokenKind.Word } prev && prev.Word!.Is("=~"))
            {
                tokens.Add(new(TokenKind.Word, Word: LexWord(regex: true)));
                continue;
            }
            if (c == '<' && Peek(1) == '(') { tokens.Add(new(TokenKind.Word, Word: LexWord())); continue; }
            if (c is '>' or '<') { tokens.Add(LexRedirect(c == '>' ? 1 : 0)); continue; }
            if (c == '(' && Peek(1) == '(' && FindArithClose(_i + 2) is var close and >= 0)
            {
                tokens.Add(new(TokenKind.Arith, s[(_i + 2)..close]));
                _i = close + 2;
                continue;
            }
            if ((c == '&' && Peek(1) == '&') || (c == '|' && Peek(1) == '|') || (c == ';' && Peek(1) == ';'))
            {
                _i += 2;
                tokens.Add(new(TokenKind.Op, new string(c, 2)));
                continue;
            }
            if (c is '|' or '&' or ';' or '(' or ')') { _i++; tokens.Add(new(TokenKind.Op, c.ToString())); continue; }
            tokens.Add(new(TokenKind.Word, Word: LexWord()));
        }
        ReadHereDocBodies(); // heredoc on the last line with no trailing newline: body is empty
        return tokens;
    }

    /// <summary>Index of the "))" closing an arithmetic expression whose body starts at k, or -1.</summary>
    int FindArithClose(int k)
    {
        int depth = 0;
        for (; k < s.Length; k++)
        {
            if (s[k] == '(') depth++;
            else if (s[k] == ')')
            {
                if (depth > 0) { depth--; continue; }
                return k + 1 < s.Length && s[k + 1] == ')' ? k : -1;
            }
        }
        return -1;
    }

    Token LexRedirect(int fd)
    {
        string op;
        if (s[_i] == '<')
        {
            if (string.CompareOrdinal(s, _i, "<<<", 0, 3) == 0) op = "<<<";
            else if (string.CompareOrdinal(s, _i, "<<-", 0, 3) == 0) op = "<<-";
            else if (Peek(1) == '<') op = "<<";
            else op = "<";
        }
        else if (Peek(1) == '>') op = ">>";
        else if (Peek(1) == '&') op = ">&";
        else if (Peek(1) == '|') op = ">|";
        else op = ">";
        _i += op.Length;
        if (op == ">|") op = ">";

        if (op is "<<" or "<<-")
        {
            while (Peek() is ' ' or '\t') _i++;
            if (_i >= s.Length || "\n;|&<>()".Contains(s[_i])) throw new ParseException("syntax error near unexpected token `newline'");
            var w = LexWord();
            var doc = new HereDoc(string.Concat(w.Parts.OfType<LiteralPart>().Select(p => p.Text)), !w.Parts.Any(p => p.Quoted), op == "<<-");
            _pending.Add(doc);
            return new(TokenKind.Redirect, Redirect: new(0, "<<", null, doc));
        }
        return new(TokenKind.Redirect, Redirect: new(fd, op, null, null));
    }

    void ReadHereDocBodies()
    {
        foreach (var doc in _pending)
        {
            var body = new StringBuilder();
            while (_i < s.Length)
            {
                int end = s.IndexOf('\n', _i);
                string line = end < 0 ? s[_i..] : s[_i..end];
                _i = end < 0 ? s.Length : end + 1;
                if (doc.StripTabs) line = line.TrimStart('\t');
                if (line.TrimEnd('\r') == doc.Delimiter) break;
                body.Append(line).Append('\n');
            }
            doc.Body = doc.Expand
                ? new Word(new Lexer(body.ToString()).LexQuoted(terminator: null))
                : new Word([new LiteralPart(body.ToString(), true)]);
        }
        _pending.Clear();
    }

    /// <param name="regex">Lexes the right side of [[ x =~ re ]], where ( ) | &lt; &gt; are part of the word.</param>
    Word LexWord(bool regex = false)
    {
        var parts = new List<WordPart>();
        var lit = new StringBuilder();
        int depth = 0;
        void Flush() { if (lit.Length > 0) { parts.Add(new LiteralPart(lit.ToString(), false)); lit.Clear(); } }

        while (_i < s.Length)
        {
            char c = s[_i];
            if (c == '<' && Peek(1) == '(')
            {
                Flush();
                int close = FindClose(_i + 2);
                parts.Add(new ProcSubPart(s[(_i + 2)..close]));
                _i = close + 1;
                continue;
            }
            if (regex && c is '(' or ')')
            {
                depth += c == '(' ? 1 : -1;
                if (depth < 0) break;
            }
            if (c is ' ' or '\t' or '\r' or '\n' or '|' or '&' or ';' or '(' or ')' or '<' or '>'
                && !(regex && (c is '(' or ')' or '|' or '<' or '>' || (depth > 0 && c is ' ' or '\t')))) break;
            switch (c)
            {
                case '\\':
                    if (_i + 1 >= s.Length) { lit.Append('\\'); _i++; break; }
                    if (s[_i + 1] == '\n') { _i += 2; break; }
                    Flush(); parts.Add(new LiteralPart(s[_i + 1].ToString(), true)); _i += 2;
                    break;
                case '\'':
                    Flush();
                    int j = s.IndexOf('\'', _i + 1);
                    if (j < 0) throw new ParseException("unexpected EOF while looking for matching `''");
                    parts.Add(new LiteralPart(s[(_i + 1)..j], true));
                    _i = j + 1;
                    break;
                case '"':
                    Flush(); _i++;
                    parts.AddRange(LexQuoted('"'));
                    break;
                case '$' when Peek(1) == '\'':
                    Flush(); parts.Add(new LiteralPart(LexAnsiC(), true));
                    break;
                case '$' when Peek(1) == '"': // $"..." (locale translation) is just "..."
                    Flush(); _i += 2;
                    parts.AddRange(LexQuoted('"'));
                    break;
                case '$':
                    Flush(); parts.Add(LexDollar(false));
                    break;
                case '`':
                    Flush(); parts.Add(LexBacktick(false));
                    break;
                default:
                    lit.Append(c); _i++;
                    break;
            }
        }
        Flush();
        return new Word(parts);
    }

    /// <summary>$'...': backslash escapes are decoded (\n, \t, \x41, \u00e9, \0NN, \', ...).</summary>
    string LexAnsiC()
    {
        int i = _i + 2;
        var sb = new StringBuilder();
        static bool IsOct(char ch) => ch is >= '0' and <= '7';
        while (i < s.Length && s[i] != '\'')
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); i++; continue; }
            char n = s[i + 1];
            i += 2;
            switch (n)
            {
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'e' or 'E': sb.Append('\u001b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'v': sb.Append('\v'); break;
                case '\\' or '\'' or '"' or '?': sb.Append(n); break;
                case >= '0' and <= '7':
                    int j = i - 1;
                    while (j < s.Length && j < i + 2 && IsOct(s[j])) j++;
                    sb.Append((char)(Convert.ToInt32(s[(i - 1)..j], 8) & 0xFF));
                    i = j;
                    break;
                case 'x' or 'u' or 'U':
                    int limit = n == 'x' ? 2 : n == 'u' ? 4 : 8, k = i;
                    while (k < s.Length && k < i + limit && Uri.IsHexDigit(s[k])) k++;
                    if (k == i) { sb.Append('\\').Append(n); break; }
                    long code = Convert.ToInt64(s[i..k], 16);
                    sb.Append(code <= 0x10FFFF && code is < 0xD800 or > 0xDFFF ? char.ConvertFromUtf32((int)code) : "\uFFFD");
                    i = k;
                    break;
                case 'c' when i < s.Length:
                    sb.Append((char)(char.ToUpperInvariant(s[i]) ^ 0x40));
                    i++;
                    break;
                default: sb.Append('\\').Append(n); break;
            }
        }
        if (i >= s.Length) throw new ParseException("unexpected EOF while looking for matching `''");
        _i = i + 1;
        return sb.ToString();
    }

    /// <summary>Double-quoted text (terminator '"') or a heredoc body / ${...} argument (terminator null).</summary>
    public List<WordPart> LexQuoted(char? terminator)
    {
        var parts = new List<WordPart>();
        var sb = new StringBuilder();
        void Flush() { if (sb.Length > 0) { parts.Add(new LiteralPart(sb.ToString(), true)); sb.Clear(); } }

        while (_i < s.Length && s[_i] != terminator)
        {
            char c = s[_i];
            if (c == '\\' && _i + 1 < s.Length && (s[_i + 1] is '$' or '`' or '\\' or '\n' || s[_i + 1] == terminator))
            {
                if (s[_i + 1] != '\n') sb.Append(s[_i + 1]);
                _i += 2;
            }
            else if (c == '$') { Flush(); parts.Add(LexDollar(true)); }
            else if (c == '`') { Flush(); parts.Add(LexBacktick(true)); }
            else { sb.Append(c); _i++; }
        }
        if (terminator != null)
        {
            if (_i >= s.Length) throw new ParseException($"unexpected EOF while looking for matching `{terminator}'");
            _i++;
        }
        Flush();
        if (parts.Count == 0) parts.Add(new LiteralPart("", true)); // "" is still an argument
        return parts;
    }

    WordPart LexDollar(bool quoted)
    {
        char n = Peek(1);
        if (n == '(')
        {
            if (Peek(2) == '(' && FindArithClose(_i + 3) is var ac and >= 0)
            {
                var expr = s[(_i + 3)..ac];
                _i = ac + 2;
                return new ArithPart(expr, quoted);
            }
            int close = FindClose(_i + 2);
            var src = s[(_i + 2)..close];
            _i = close + 1;
            return new CommandSubPart(src, quoted);
        }
        if (n == '{')
        {
            int depth = 0, k = _i + 2;
            for (; k < s.Length; k++)
            {
                if (s[k] == '\\') { k++; continue; }
                if (s[k] == '{') depth++;
                else if (s[k] == '}') { if (depth == 0) break; depth--; }
            }
            if (k >= s.Length) throw new ParseException("unexpected EOF while looking for matching `}'");
            var name = s[(_i + 2)..k];
            _i = k + 1;
            return new VariablePart(name, quoted);
        }
        if (char.IsLetter(n) || n == '_')
        {
            int j = _i + 1;
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
            var name = s[(_i + 1)..j];
            _i = j;
            return new VariablePart(name, quoted);
        }
        if (n is '?' or '$' or '#' or '!' or '@' or '*' || char.IsDigit(n))
        {
            _i += 2;
            return new VariablePart(n.ToString(), quoted);
        }
        _i++;
        return new LiteralPart("$", quoted);
    }

    WordPart LexBacktick(bool quoted)
    {
        var sb = new StringBuilder();
        int j = _i + 1;
        for (; j < s.Length && s[j] != '`'; j++)
        {
            if (s[j] == '\\' && j + 1 < s.Length && s[j + 1] is '`' or '\\' or '$') j++;
            sb.Append(s[j]);
        }
        if (j >= s.Length) throw new ParseException("unexpected EOF while looking for matching ``'");
        _i = j + 1;
        return new CommandSubPart(sb.ToString(), quoted);
    }

    /// <summary>Index of the ')' closing a $( whose body starts at <paramref name="k"/>.</summary>
    int FindClose(int k)
    {
        int depth = 1;
        for (; k < s.Length; k++)
        {
            switch (s[k])
            {
                case '\\': k++; break;
                case '\'': k = s.IndexOf('\'', k + 1); if (k < 0) k = s.Length; break;
                case '"':
                    for (k++; k < s.Length && s[k] != '"'; k++) if (s[k] == '\\') k++;
                    break;
                case '(': depth++; break;
                case ')': if (--depth == 0) return k; break;
                case '<' when k + 2 < s.Length && s[k + 1] == '<' && s[k + 2] != '<' && (k == 0 || s[k - 1] != '<'):
                    k = SkipHereDoc(k + 2) - 1; // its body is text: quotes and parentheses in it mean nothing
                    break;
            }
        }
        throw new ParseException("unexpected EOF while looking for matching `)'");
    }

    /// <summary>From just after a `&lt;&lt;`, returns the index after the heredoc's closing delimiter line.</summary>
    int SkipHereDoc(int k)
    {
        bool stripTabs = k < s.Length && s[k] == '-';
        if (stripTabs) k++;
        while (k < s.Length && s[k] is ' ' or '\t') k++;
        int start = k;
        while (k < s.Length && !char.IsWhiteSpace(s[k]) && s[k] is not (')' or ';' or '|' or '&')) k++;
        var delimiter = s[start..k].Replace("'", "").Replace("\"", "").Replace("\\", "");
        int line = s.IndexOf('\n', k);
        if (delimiter.Length == 0 || line < 0) return k;
        for (int from = line + 1; from <= s.Length;)
        {
            int end = s.IndexOf('\n', from);
            if (end < 0) end = s.Length;
            var text = s[from..end];
            if ((stripTabs ? text.TrimStart('\t') : text) == delimiter) return end;
            from = end + 1;
        }
        return s.Length;
    }
}

internal sealed class Parser
{
    static readonly HashSet<string> Reserved = ["then", "elif", "else", "fi", "do", "done", "esac", "in"];
    static readonly HashSet<string> CondBinary = ["==", "=", "!=", "=~", "<", ">", "-eq", "-ne", "-lt", "-le", "-gt", "-ge", "-nt", "-ot"];

    readonly List<Token> _t;
    int _p;

    Parser(List<Token> tokens) => _t = tokens;

    public static Script Parse(string source)
    {
        var p = new Parser(new Lexer(source).Lex());
        var script = p.ParseScript();
        if (p._p < p._t.Count) throw p.Unexpected();
        return script;
    }

    /// <summary>Lexes a string into plain words (used for alias values).</summary>
    public static List<Word> ParseWords(string source) =>
        new Lexer(source).Lex().Where(t => t.Kind == TokenKind.Word).Select(t => t.Word!).ToList();

    Token? Cur => _p < _t.Count ? _t[_p] : null;
    Token? At(int o) => _p + o < _t.Count ? _t[_p + o] : null;
    bool IsOp(string op) => Cur is { Kind: TokenKind.Op } t && t.Op == op;
    bool IsWord(string w) => Cur is { Kind: TokenKind.Word } t && t.Word!.Is(w);
    void SkipNewlines() { while (IsOp("\n")) _p++; }
    void SkipSeparators() { while (IsOp("\n") || IsOp(";")) _p++; }
    ParseException Unexpected() => new($"syntax error near unexpected token `{Cur?.ToString() ?? "newline"}'");

    void Expect(string word)
    {
        if (!IsWord(word)) throw Cur == null ? new ParseException($"syntax error: unexpected end of file (expected `{word}')") : Unexpected();
        _p++;
    }

    bool AtTerminator(string[] terms) =>
        Cur is { Kind: TokenKind.Word } w && w.Word!.UnquotedLiteral is { } lit && terms.Contains(lit)
        || Cur is { Kind: TokenKind.Op } o && terms.Contains(o.Op);

    Script ParseScript(params string[] terms)
    {
        var items = new List<AndOr>();
        while (true)
        {
            while (IsOp("\n") || IsOp(";") || IsOp("&")) _p++;
            if (Cur == null || AtTerminator(terms)) break;
            items.Add(ParseAndOr());
            if (Cur == null || IsOp("\n") || IsOp(";") || IsOp("&")) continue;
            if (AtTerminator(terms)) break;
            throw Unexpected();
        }
        return new Script(items);
    }

    AndOr ParseAndOr()
    {
        var first = ParsePipeline();
        var rest = new List<(string, Pipeline)>();
        while (IsOp("&&") || IsOp("||"))
        {
            var op = Cur!.Op; _p++;
            SkipNewlines();
            rest.Add((op, ParsePipeline()));
        }
        return new AndOr(first, rest);
    }

    Pipeline ParsePipeline()
    {
        bool negate = false;
        if (IsWord("!")) { negate = true; _p++; }
        var cmds = new List<Command> { ParseCommand() };
        while (IsOp("|"))
        {
            _p++;
            SkipNewlines();
            cmds.Add(ParseCommand());
        }
        return new Pipeline(cmds, negate);
    }

    Command WithRedirects(Command c)
    {
        var r = ParseRedirects([]);
        return r.Count == 0 ? c : c with { Redirects = r };
    }

    Command ParseCommand()
    {
        if (Cur is { Kind: TokenKind.Arith } arith) { _p++; return WithRedirects(new ArithCommand(arith.Op)); }
        if (IsOp("(") || IsWord("{"))
        {
            bool sub = IsOp("(");
            _p++;
            var body = ParseScript(sub ? ")" : "}");
            if (sub ? !IsOp(")") : !IsWord("}")) throw Cur == null ? new ParseException("syntax error: unexpected end of file") : Unexpected();
            _p++;
            return WithRedirects(new GroupCommand(body, sub));
        }
        if (Cur is { Kind: TokenKind.Word } kwTok && kwTok.Word!.UnquotedLiteral is { } kw)
        {
            switch (kw)
            {
                case "if": return WithRedirects(ParseIf());
                case "while" or "until": return WithRedirects(ParseWhile(kw == "until"));
                case "for": return WithRedirects(ParseFor());
                case "case": return WithRedirects(ParseCase());
                case "function": return ParseFunction(true);
                case "[[": return WithRedirects(ParseCond());
                case "select": throw new ParseException("'select' is not supported by this shell");
            }
            if (Reserved.Contains(kw) || kw == "}") throw Unexpected();
            if (At(1) is { Kind: TokenKind.Op, Op: "(" } && At(2) is { Kind: TokenKind.Op, Op: ")" }) return ParseFunction(false);
        }

        var assignments = new List<(string, Word)>();
        var words = new List<Word>();
        var redirects = new List<Redirect>();
        while (Cur != null)
        {
            if (Cur.Kind == TokenKind.Redirect) { ParseRedirects(redirects); continue; }
            if (Cur.Kind != TokenKind.Word) break;
            var w = Cur.Word!;
            _p++;
            if (words.Count == 0 && TryAssignment(w) is { } a) { assignments.Add(a); continue; }
            words.Add(w);
        }
        if (words.Count == 0 && assignments.Count == 0 && redirects.Count == 0) throw Unexpected();
        return new SimpleCommand(assignments, words) { Redirects = redirects };
    }

    IfCommand ParseIf()
    {
        _p++; // if
        var branches = new List<(Script, Script)>();
        var cond = ParseScript("then");
        Expect("then");
        branches.Add((cond, ParseScript("elif", "else", "fi")));
        Script? elseBody = null;
        while (true)
        {
            if (IsWord("elif"))
            {
                _p++;
                cond = ParseScript("then");
                Expect("then");
                branches.Add((cond, ParseScript("elif", "else", "fi")));
                continue;
            }
            if (IsWord("else")) { _p++; elseBody = ParseScript("fi"); }
            break;
        }
        Expect("fi");
        return new IfCommand(branches, elseBody);
    }

    LoopCommand ParseWhile(bool until)
    {
        _p++;
        var cond = ParseScript("do");
        Expect("do");
        var body = ParseScript("done");
        Expect("done");
        return new LoopCommand(cond, body, until);
    }

    Command ParseFor()
    {
        _p++; // for
        if (Cur is { Kind: TokenKind.Arith } arith)
        {
            _p++;
            var parts = arith.Op.Split(';');
            if (parts.Length != 3) throw new ParseException("syntax error: expected `for (( init; cond; step ))'");
            SkipSeparators();
            Expect("do");
            var b = ParseScript("done");
            Expect("done");
            return new ArithForCommand(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), b);
        }
        var name = Cur is { Kind: TokenKind.Word } nt && nt.Word!.UnquotedLiteral is { } n && IsName(n) ? n : throw Unexpected();
        _p++;
        SkipNewlines();
        List<Word>? items = null;
        if (IsWord("in"))
        {
            _p++;
            items = [];
            while (Cur is { Kind: TokenKind.Word } w) { items.Add(w.Word!); _p++; }
        }
        SkipSeparators();
        Expect("do");
        var body = ParseScript("done");
        Expect("done");
        return new ForCommand(name, items, body);
    }

    CaseCommand ParseCase()
    {
        _p++; // case
        var subject = Cur is { Kind: TokenKind.Word } w ? w.Word! : throw Unexpected();
        _p++;
        SkipNewlines();
        Expect("in");
        SkipNewlines();
        var clauses = new List<(List<Word>, Script)>();
        while (!IsWord("esac"))
        {
            if (Cur == null) throw new ParseException("syntax error: unexpected end of file (expected `esac')");
            if (IsOp("(")) _p++;
            var patterns = new List<Word>();
            while (true)
            {
                if (Cur is not { Kind: TokenKind.Word } pt) throw Unexpected();
                patterns.Add(pt.Word!);
                _p++;
                if (IsOp("|")) { _p++; continue; }
                break;
            }
            if (!IsOp(")")) throw Unexpected();
            _p++;
            var body = ParseScript(";;", "esac");
            if (IsOp(";;")) _p++;
            SkipNewlines();
            clauses.Add((patterns, body));
        }
        _p++; // esac
        return new CaseCommand(subject, clauses);
    }

    FunctionDef ParseFunction(bool keyword)
    {
        if (keyword) _p++;
        var name = Cur is { Kind: TokenKind.Word } w && w.Word!.UnquotedLiteral is { } n ? n : throw Unexpected();
        _p++;
        if (IsOp("("))
        {
            _p++;
            if (!IsOp(")")) throw Unexpected();
            _p++;
        }
        SkipNewlines();
        if (Cur == null) throw new ParseException("syntax error: unexpected end of file");
        return new FunctionDef(name, ParseCommand());
    }

    CondCommand ParseCond()
    {
        _p++; // [[
        var items = new List<Word>();
        while (!IsWord("]]"))
        {
            switch (Cur)
            {
                case null: throw new ParseException("syntax error: unexpected end of file (expected `]]')");
                case { Kind: TokenKind.Word } w: items.Add(w.Word!); break;
                case { Kind: TokenKind.Op, Op: "&&" or "||" or "(" or ")" } o: items.Add(Word.Lit(o.Op)); break;
                case { Kind: TokenKind.Redirect, Redirect: { Op: "<" or ">", Here: null } r }: items.Add(Word.Lit(r.Op)); break;
                default: throw Unexpected();
            }
            _p++;
        }
        _p++; // ]]
        return new CondCommand(items);
    }

    public static bool IsCondBinary(Word w) => w.UnquotedLiteral is { } s && CondBinary.Contains(s);

    List<Redirect> ParseRedirects(List<Redirect> into)
    {
        while (Cur is { Kind: TokenKind.Redirect } t)
        {
            _p++;
            var r = t.Redirect!;
            if (r.Here == null)
            {
                if (Cur is not { Kind: TokenKind.Word }) throw Unexpected();
                r = r with { Target = Cur.Word };
                _p++;
            }
            into.Add(r);
        }
        return into;
    }

    static bool IsName(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    static (string, Word)? TryAssignment(Word w)
    {
        if (w.Parts is not [LiteralPart { Quoted: false } first, ..]) return null;
        int eq = first.Text.IndexOf('=');
        if (eq <= 0) return null;
        var name = first.Text[..eq];
        bool append = name.EndsWith('+');
        if (append) name = name[..^1];
        if (!IsName(name)) return null;
        var rest = new List<WordPart>();
        if (append) rest.Add(new VariablePart(name, true)); // VAR+=x  ==  VAR="$VAR"x
        if (eq + 1 < first.Text.Length) rest.Add(new LiteralPart(first.Text[(eq + 1)..], false));
        rest.AddRange(w.Parts.Skip(1));
        return (name, new Word(rest));
    }
}
