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
/// <summary>Unquoted text that came out of an expansion (the word of ${x:-a b}): it is split and globbed like one.</summary>
internal sealed record ExpandedTextPart(string Text) : WordPart(false);
/// <summary>The (a b c) of name=(a b c): its source text, expanded when the assignment runs.</summary>
internal sealed record ArrayLiteralPart(string Source) : WordPart(false);

/// <summary>name=value, name[Index]=value or name=(Elements); Append is += on an array or an element.</summary>
internal sealed record Assignment(string Name, Word Value)
{
    public Word? Index { get; init; }
    public string? Elements { get; init; }
    public bool Append { get; init; }
}

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
        ArrayLiteralPart al => "(" + al.Source + ")",
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

internal sealed record Redirect(int Fd, string Op, Word? Target, HereDoc? Here)
{
    /// <summary>{name}>file: the shell picks the fd and stores it in this variable.</summary>
    public string? FdVariable { get; init; }
}

internal abstract record Command
{
    public List<Redirect> Redirects { get; init; } = [];
    /// <summary>The line bash names in this command's own errors (a failed redirection, (( )), [[ ]]); 0: none.</summary>
    public int Line { get; init; }
}
internal sealed record SimpleCommand(List<Assignment> Assignments, List<Word> Words) : Command;
internal sealed record GroupCommand(Script Body, bool Subshell) : Command;
internal sealed record IfCommand(List<(Script Cond, Script Body)> Branches, Script? Else) : Command;
internal sealed record LoopCommand(Script Cond, Script Body, bool Until) : Command;
internal sealed record ForCommand(string Var, List<Word>? Items, Script Body) : Command
{
    /// <summary>A select menu rather than a for loop.</summary>
    public bool Select { get; init; }
}
internal sealed record ArithForCommand(string Init, string Cond, string Step, Script Body) : Command;
/// <summary>Terminator is ";;", ";&amp;" (fall through to the next body) or ";;&amp;" (keep testing patterns).</summary>
internal sealed record CaseCommand(Word Subject, List<(List<Word> Patterns, Script Body, string Terminator)> Clauses) : Command;
internal sealed record FunctionDef(string Name, Command Body) : Command
{
    /// <summary>The body as written, for `declare -f`.</summary>
    public string Source { get; init; } = "";
}
internal sealed record ArithCommand(string Expr) : Command;
/// <summary>[[ ... ]]: operands are words; operators (&amp;&amp; || ! ( ) == ...) are unquoted literal words.</summary>
internal sealed record CondCommand(List<Word> Items) : Command;

internal sealed record Pipeline(List<Command> Commands, bool Negate)
{
    /// <summary>`time [-p] pipeline`: report how long it took on the shell's stderr.</summary>
    public bool Timed { get; init; }
    public bool PosixTime { get; init; }
}
internal sealed record AndOr(Pipeline First, List<(string Op, Pipeline Next)> Rest)
{
    /// <summary>Ended with `&amp;`: runs (to completion, in order) as a background job in a subshell.</summary>
    public bool Background { get; init; }
}
internal sealed record Script(List<AndOr> Items);

internal enum TokenKind { Word, Op, Redirect, Arith }

internal sealed record Token(TokenKind Kind, string Op = "", Word? Word = null, Redirect? Redirect = null)
{
    public int Line { get; init; }
    public int EndLine { get; init; }
    public int Start { get; init; }
    public int End { get; init; }
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

    int _linePos, _line = 1;

    /// <summary>The 1-based line of position pos; positions only move forward, so this counts incrementally.</summary>
    int LineAt(int pos)
    {
        for (; _linePos < pos && _linePos < s.Length; _linePos++) if (s[_linePos] == '\n') _line++;
        return _line;
    }

    public List<Token> Lex()
    {
        var tokens = new List<Token>();
        while (true)
        {
            while (Peek() is ' ' or '\t' or '\r') _i++;
            if (Peek() == '\\' && Peek(1) == '\n') { _i += 2; continue; }
            if (_i >= s.Length) break;
            int count = tokens.Count, start = _i;
            LexOne(tokens);
            if (tokens.Count > count) tokens[count] = tokens[count] with { Line = LineAt(start), EndLine = LineAt(_i), Start = start, End = _i };
        }
        ReadHereDocBodies(); // heredoc on the last line with no trailing newline: body is empty
        return tokens;
    }

    /// <summary>True where a word can be an assignment: at the start of a command or after another assignment.</summary>
    static bool CommandPosition(List<Token> tokens) =>
        tokens.Count == 0 || tokens[^1].Kind is TokenKind.Op or TokenKind.Arith
        || tokens[^1].Word is { } w && (Parser.TryAssignment(w) != null || w.UnquotedLiteral is "then" or "do" or "else" or "elif" or "if" or "while" or "until" or "{" or "!" or "time");

    void LexOne(List<Token> tokens)
    {
        {
            char c = s[_i];
            if (c == '#') { while (_i < s.Length && s[_i] != '\n') _i++; return; }
            if (c == '\n') { _i++; tokens.Add(new(TokenKind.Op, "\n")); ReadHereDocBodies(); return; }
            if (char.IsDigit(c))
            {
                int j = _i;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                if (j < s.Length && s[j] is '>' or '<') { int fd = int.Parse(s[_i..j]); _i = j; tokens.Add(LexRedirect(fd)); return; }
            }
            if (c == '{' && s.IndexOf('}', _i) is var brace and > 0 && brace + 1 < s.Length && s[brace + 1] is '>' or '<'
                && Parser.IsName(s[(_i + 1)..brace]))
            {
                var name = s[(_i + 1)..brace];
                _i = brace + 1;
                var t = LexRedirect(10);
                tokens.Add(t with { Redirect = t.Redirect! with { FdVariable = name } });
                return;
            }
            if (c == '&' && Peek(1) == '>')
            {
                _i += 2;
                bool append = Peek() == '>';
                if (append) _i++;
                tokens.Add(new(TokenKind.Redirect, Redirect: new(1, append ? "&>>" : "&>", null, null)));
                return;
            }
            if (tokens.Count > 0 && tokens[^1] is { Kind: TokenKind.Word } prev && prev.Word!.Is("=~"))
            {
                tokens.Add(new(TokenKind.Word, Word: LexWord(regex: true)));
                return;
            }
            if (c == '<' && Peek(1) == '(') { tokens.Add(new(TokenKind.Word, Word: LexWord())); return; }
            if (c == '>' && Peek(1) == '(') throw new ParseException("process substitution >(...) is not supported by this shell (write to a file, then read it)");
            if (c is '>' or '<') { tokens.Add(LexRedirect(c == '>' ? 1 : 0)); return; }
            if (c == '(' && Peek(1) == '(' && FindArithClose(_i + 2) is var close and >= 0)
            {
                tokens.Add(new(TokenKind.Arith, s[(_i + 2)..close]));
                _i = close + 2;
                return;
            }
            if (c == ';' && Peek(1) == '&' || c == ';' && Peek(1) == ';' && Peek(2) == '&') // case fall-through terminators
            {
                var op = Peek(1) == '&' ? ";&" : ";;&";
                _i += op.Length;
                tokens.Add(new(TokenKind.Op, op));
                return;
            }
            if ((c == '&' && Peek(1) == '&') || (c == '|' && Peek(1) == '|') || (c == ';' && Peek(1) == ';'))
            {
                _i += 2;
                tokens.Add(new(TokenKind.Op, new string(c, 2)));
                return;
            }
            if (c is '|' or '&' or ';' or '(' or ')') { _i++; tokens.Add(new(TokenKind.Op, c.ToString())); return; }
            tokens.Add(new(TokenKind.Word, Word: LexWord(subscript: CommandPosition(tokens))));
        }
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
            else if (Peek(1) == '&') op = "<&";
            else op = "<";
        }
        else if (Peek(1) == '>') op = ">>";
        else if (Peek(1) == '&') op = ">&";
        else if (Peek(1) == '|') op = ">|";
        else op = ">";
        _i += op.Length;

        if (op is "<<" or "<<-")
        {
            while (Peek() is ' ' or '\t') _i++;
            if (_i >= s.Length || "\n;|&<>()".Contains(s[_i])) throw new ParseException("syntax error near unexpected token `newline'");
            var w = LexWord();
            var doc = new HereDoc(string.Concat(w.Parts.OfType<LiteralPart>().Select(p => p.Text)), !w.Parts.Any(p => p.Quoted), op == "<<-");
            _pending.Add(doc);
            return new(TokenKind.Redirect, Redirect: new(fd, "<<", null, doc));
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

    /// <summary>
    /// At the start of an assignment word name[sub]=, the index of the subscript's closing bracket (bash lets the
    /// subscript hold spaces there: a[i + 1]=x); otherwise -1.
    /// </summary>
    int SubscriptEnd()
    {
        int k = _i;
        if (k >= s.Length || !(char.IsLetter(s[k]) || s[k] == '_')) return -1;
        while (k < s.Length && (char.IsLetterOrDigit(s[k]) || s[k] == '_')) k++;
        if (k >= s.Length || s[k] != '[') return -1;
        for (int depth = 0; k < s.Length; k++)
        {
            switch (s[k])
            {
                case '\\': k++; break;
                case '\'': k = s.IndexOf('\'', k + 1); if (k < 0) return -1; break;
                case '"': for (k++; k < s.Length && s[k] != '"'; k++) if (s[k] == '\\') k++; break;
                case '\n': return -1;
                case '[': depth++; break;
                case ']':
                    if (--depth > 0) break;
                    return k + 1 < s.Length && (s[k + 1] == '=' || s[k + 1] == '+' && k + 2 < s.Length && s[k + 2] == '=') ? k : -1;
            }
        }
        return -1;
    }

    /// <param name="regex">Lexes the right side of [[ x =~ re ]], where ( ) | &lt; &gt; are part of the word.</param>
    /// <param name="subscript">The word is in command position, where name[a b]=v is one assignment word.</param>
    Word LexWord(bool regex = false, bool subscript = false)
    {
        var parts = new List<WordPart>();
        var lit = new StringBuilder();
        int depth = 0, subscriptEnd = subscript ? SubscriptEnd() : -1;
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
            // name=( ... ) and name+=( ... ): an array literal, kept as text until the assignment runs
            if (c == '(' && !regex && parts.Count == 0 && lit.Length > 1 && lit[^1] == '='
                && Parser.IsName(lit.ToString(0, lit.Length - 1).TrimEnd('+')))
            {
                Flush();
                int end = FindClose(_i + 1);
                parts.Add(new ArrayLiteralPart(s[(_i + 1)..end]));
                _i = end + 1;
                continue;
            }
            // extglob: @(a|b) !(x) *(x) +(x) ?(x) are one word; only `!(` at the start of a command is not
            if (c == '(' && !regex && lit.Length > 0 && lit[^1] is '@' or '!' or '*' or '+' or '?' && !(subscript && parts.Count == 0 && lit.ToString() == "!"))
            {
                int end = FindClose(_i + 1);
                lit.Append(s, _i, end + 1 - _i);
                _i = end + 1;
                continue;
            }
            if (regex && c is '(' or ')')
            {
                depth += c == '(' ? 1 : -1;
                if (depth < 0) break;
            }
            if (c is ' ' or '\t' or '\r' or '\n' or '|' or '&' or ';' or '(' or ')' or '<' or '>'
                && !(regex && (c is '(' or ')' or '|' or '<' or '>' || (depth > 0 && c is ' ' or '\t')))
                && !(_i < subscriptEnd && c is ' ' or '\t')) break;
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
                    if (sb[^1] == '\0') SkipToQuote(ref i, sb);
                    break;
                case 'x' or 'u' or 'U':
                    int limit = n == 'x' ? 2 : n == 'u' ? 4 : 8, k = i;
                    while (k < s.Length && k < i + limit && Uri.IsHexDigit(s[k])) k++;
                    if (k == i) { sb.Append('\\').Append(n); break; }
                    long code = Convert.ToInt64(s[i..k], 16);
                    sb.Append(code <= 0x10FFFF && code is < 0xD800 or > 0xDFFF ? char.ConvertFromUtf32((int)code) : "\uFFFD");
                    i = k;
                    if (code == 0) SkipToQuote(ref i, sb);
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

        // bash strings cannot hold a NUL: $'a\0b' is just "a"
        void SkipToQuote(ref int at, StringBuilder text)
        {
            text.Length--;
            while (at < s.Length && s[at] != '\'') at += s[at] == '\\' ? 2 : 1;
        }
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

    /// <summary>
    /// The word inside ${x op word}. Quotes and backslashes in it quote (so "$v" in a pattern is literal); inside an
    /// outer "...", a ' is an ordinary character. Parts are Quoted only where the word itself quotes them.
    /// </summary>
    public List<WordPart> LexParamWord(bool inDouble)
    {
        var parts = new List<WordPart>();
        var sb = new StringBuilder();
        void Flush() { if (sb.Length > 0) { parts.Add(new LiteralPart(sb.ToString(), false)); sb.Clear(); } }
        while (_i < s.Length)
        {
            char c = s[_i];
            if (c == '\\' && _i + 1 < s.Length)
            {
                char n = s[_i + 1];
                if (n == '\n') { _i += 2; continue; }
                // in double quotes only \$ \` \" \\ \} escape (and \& in a replacement); elsewhere any character does
                if (inDouble && n is not ('$' or '`' or '"' or '\\' or '}' or '&')) { sb.Append(c); _i++; continue; }
                Flush(); parts.Add(new LiteralPart(n.ToString(), true)); _i += 2;
            }
            else if (c == '\'' && !inDouble)
            {
                Flush();
                int j = s.IndexOf('\'', _i + 1);
                if (j < 0) throw new ParseException("unexpected EOF while looking for matching `''");
                parts.Add(new LiteralPart(s[(_i + 1)..j], true));
                _i = j + 1;
            }
            else if (c == '"') { Flush(); _i++; parts.AddRange(LexQuoted('"')); }
            else if (c == '$' && Peek(1) == '\'') { Flush(); parts.Add(new LiteralPart(LexAnsiC(), true)); }
            else if (c == '$') { Flush(); parts.Add(LexDollar(false)); }
            else if (c == '`') { Flush(); parts.Add(LexBacktick(false)); }
            else { sb.Append(c); _i++; }
        }
        Flush();
        if (parts.Count == 0) parts.Add(new LiteralPart("", false));
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
        if (n == '[') // $[expr]: the old spelling of $((expr))
        {
            int depth = 0, k = _i + 1;
            for (; k < s.Length; k++)
                if (s[k] == '[') depth++;
                else if (s[k] == ']' && --depth == 0) break;
            if (k >= s.Length) throw new ParseException("unexpected EOF while looking for matching `]'");
            var expr = s[(_i + 2)..k];
            _i = k + 1;
            return new ArithPart(expr, quoted);
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
        if (n is '?' or '$' or '#' or '!' or '@' or '*' or '-' || char.IsDigit(n))
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
        int depth = 1, from = k;
        // a case statement's patterns end in an unmatched ')': 0 = awaiting `in`, 1 = in patterns, 2 = in a body
        var cases = new Stack<int>();
        bool Keyword(int at, string w) => string.CompareOrdinal(s, at, w, 0, w.Length) == 0 && (at == from || " \t\n;|&()".Contains(s[at - 1]))
            && (at + w.Length >= s.Length || " \t\n;|&()".Contains(s[at + w.Length]));
        for (; k < s.Length; k++)
        {
            if (char.IsLetter(s[k]))
            {
                if (Keyword(k, "case")) cases.Push(0);
                else if (cases.Count > 0 && cases.Peek() == 0 && Keyword(k, "in")) { cases.Pop(); cases.Push(1); }
                else if (cases.Count > 0 && Keyword(k, "esac")) cases.Pop();
                else { while (k + 1 < s.Length && char.IsLetterOrDigit(s[k + 1])) k++; continue; }
                while (k + 1 < s.Length && char.IsLetter(s[k + 1])) k++;
                continue;
            }
            if (cases.Count > 0 && cases.Peek() == 1 && s[k] is '(' or ')')
            {
                if (s[k] == ')') { cases.Pop(); cases.Push(2); }
                continue;
            }
            if (cases.Count > 0 && cases.Peek() == 2 && s[k] == ';' && k + 1 < s.Length && s[k + 1] is ';' or '&') { cases.Pop(); cases.Push(1); k++; continue; }
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
    static readonly HashSet<string> CondBinary = ["==", "=", "!=", "=~", "<", ">", "-eq", "-ne", "-lt", "-le", "-gt", "-ge", "-nt", "-ot", "-ef"];

    readonly List<Token> _t;
    readonly string _src;
    int _p;

    Parser(List<Token> tokens, string source) { _t = tokens; _src = source; }

    public static Script Parse(string source)
    {
        var p = new Parser(new Lexer(source).Lex(), source);
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
            var item = ParseAndOr();
            items.Add(IsOp("&") ? item with { Background = true } : item);
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
        bool timed = false, posix = false;
        if (IsWord("time"))
        {
            timed = true;
            _p++;
            if (IsWord("-p")) { posix = true; _p++; }
            if (Cur == null || Cur.Kind == TokenKind.Op && Cur.Op is "\n" or ";" or "&" or "&&" or "||" or ")")
                return new Pipeline([], false) { Timed = true, PosixTime = posix }; // `time` alone just reports
        }
        bool negate = false;
        if (IsWord("!")) { negate = true; _p++; }
        var cmds = new List<Command> { ParseCommand() };
        while (IsOp("|"))
        {
            _p++;
            SkipNewlines();
            cmds.Add(ParseCommand());
        }
        return new Pipeline(cmds, negate) { Timed = timed, PosixTime = posix };
    }

    // a compound command's line is where it starts, or where its redirections are
    Command WithRedirects(Command c, int line)
    {
        var r = ParseRedirects([]);
        return c with { Redirects = r.Count == 0 ? c.Redirects : r, Line = r.Count == 0 ? line : _t[_p - 1].EndLine };
    }

    Command ParseCommand()
    {
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new ParseException("command is nested too deeply");
        int start = Cur?.Line ?? 0;
        if (Cur is { Kind: TokenKind.Arith } arith) { _p++; return WithRedirects(new ArithCommand(arith.Op), start); }
        if (IsOp("(") || IsWord("{"))
        {
            bool sub = IsOp("(");
            _p++;
            var body = ParseScript(sub ? ")" : "}");
            if (sub ? !IsOp(")") : !IsWord("}")) throw Cur == null ? new ParseException("syntax error: unexpected end of file") : Unexpected();
            _p++;
            return WithRedirects(new GroupCommand(body, sub), start);
        }
        if (Cur is { Kind: TokenKind.Word } kwTok && kwTok.Word!.UnquotedLiteral is { } kw)
        {
            switch (kw)
            {
                case "if": return WithRedirects(ParseIf(), start);
                case "while" or "until": return WithRedirects(ParseWhile(kw == "until"), start);
                case "for": return WithRedirects(ParseFor(), start);
                case "case": return WithRedirects(ParseCase(), start);
                case "function": return ParseFunction(true);
                case "[[": return WithRedirects(ParseCond(), start);
                case "select": return WithRedirects(ParseFor(select: true), start);
                case "coproc": throw new ParseException("'coproc' is not supported by this shell");
            }
            if (Reserved.Contains(kw) || kw == "}") throw Unexpected();
            if (At(1) is { Kind: TokenKind.Op, Op: "(" } && At(2) is { Kind: TokenKind.Op, Op: ")" }) return ParseFunction(false);
        }

        int line = Cur?.Line ?? 0;
        var assignments = new List<Assignment>();
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
        // bash reports a command that spans lines (a quoted newline) at its last line
        return new SimpleCommand(assignments, words) { Redirects = redirects, Line = _p > 0 ? Math.Max(line, _t[_p - 1].EndLine) : line };
    }

    /// <summary>A compound command's list, which bash requires to hold at least one command (`if; then` is an error).</summary>
    Script ParseList(params string[] terms)
    {
        var list = ParseScript(terms);
        if (list.Items.Count == 0 && Cur != null) throw Unexpected();
        return list;
    }

    IfCommand ParseIf()
    {
        _p++; // if
        var branches = new List<(Script, Script)>();
        var cond = ParseList("then");
        Expect("then");
        branches.Add((cond, ParseList("elif", "else", "fi")));
        Script? elseBody = null;
        while (true)
        {
            if (IsWord("elif"))
            {
                _p++;
                cond = ParseList("then");
                Expect("then");
                branches.Add((cond, ParseList("elif", "else", "fi")));
                continue;
            }
            if (IsWord("else")) { _p++; elseBody = ParseList("fi"); }
            break;
        }
        Expect("fi");
        return new IfCommand(branches, elseBody);
    }

    LoopCommand ParseWhile(bool until)
    {
        _p++;
        var cond = ParseList("do");
        Expect("do");
        var body = ParseList("done");
        Expect("done");
        return new LoopCommand(cond, body, until);
    }

    Command ParseFor(bool select = false)
    {
        _p++; // for / select
        if (!select && Cur is { Kind: TokenKind.Arith } arith)
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
        return new ForCommand(name, items, body) { Select = select };
    }

    CaseCommand ParseCase()
    {
        _p++; // case
        var subject = Cur is { Kind: TokenKind.Word } w ? w.Word! : throw Unexpected();
        _p++;
        SkipNewlines();
        Expect("in");
        SkipNewlines();
        var clauses = new List<(List<Word>, Script, string)>();
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
            var body = ParseScript(";;", ";&", ";;&", "esac");
            var terminator = Cur is { Kind: TokenKind.Op, Op: ";;" or ";&" or ";;&" } t ? t.Op : ";;";
            if (Cur is { Kind: TokenKind.Op, Op: ";;" or ";&" or ";;&" }) _p++;
            SkipNewlines();
            clauses.Add((patterns, body, terminator));
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
        int start = Cur.Start;
        var body = ParseCommand();
        return new FunctionDef(name, body) { Source = _src[start..Math.Max(start, _t[_p - 1].End)].TrimEnd() };
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

    internal static bool IsName(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>
    /// Splits the "sub]=value" that follows an opening bracket. The subscript may span several parts
    /// (arr[$i]=x, m["a b"]=x), so this looks for the first unquoted "]=" or "]+=".
    /// </summary>
    internal static (Word Index, Word Value, bool Append)? SplitSubscript(Word w, int offset)
    {
        var index = new List<WordPart>();
        for (int i = 0; i < w.Parts.Count; i++)
        {
            if (w.Parts[i] is not LiteralPart { Quoted: false } l) { index.Add(w.Parts[i]); continue; }
            var text = i == 0 ? l.Text[offset..] : l.Text;
            int plain = text.IndexOf("]=", StringComparison.Ordinal), plus = text.IndexOf("]+=", StringComparison.Ordinal);
            bool append = plus >= 0 && (plain < 0 || plus < plain);
            int at = append ? plus : plain;
            if (at < 0) { if (text.Length > 0) index.Add(new LiteralPart(text, false)); continue; }
            if (at > 0) index.Add(new LiteralPart(text[..at], false));
            var value = new List<WordPart>();
            var tail = text[(at + (append ? 3 : 2))..];
            if (tail.Length > 0) value.Add(new LiteralPart(tail, false));
            value.AddRange(w.Parts.Skip(i + 1));
            return (new Word(index), new Word(value), append);
        }
        return null;
    }

    internal static Assignment? TryAssignment(Word w)
    {
        if (w.Parts is not [LiteralPart { Quoted: false } first, ..]) return null;
        int eq = first.Text.IndexOf('=');
        int open = first.Text.IndexOf('[');
        if (open > 0 && (eq < 0 || open < eq) && IsName(first.Text[..open]))
            return SplitSubscript(w, open + 1) is var (index, element, plus) ? new Assignment(first.Text[..open], element) { Index = index, Append = plus } : null;
        if (eq <= 0) return null;
        var name = first.Text[..eq];
        bool append = name.EndsWith('+');
        if (append) name = name[..^1];
        if (!IsName(name)) return null;
        if (w.Parts is [_, ArrayLiteralPart literal] && eq == first.Text.Length - 1)
            return new Assignment(name, new Word([])) { Elements = literal.Source, Append = append };
        var rest = new List<WordPart>();
        if (eq + 1 < first.Text.Length) rest.Add(new LiteralPart(first.Text[(eq + 1)..], false));
        rest.AddRange(w.Parts.Skip(1));
        return new Assignment(name, new Word(rest)) { Append = append };
    }
}
