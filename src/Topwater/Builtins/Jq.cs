using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Topwater;

/// <summary>
/// jq: a parser and evaluator for the jq language as agents use it. Pipes, `,`, `//`, arithmetic, comparisons, and/or,
/// object and array construction, string interpolation and @formats, variables (`as $x` with destructuring, --arg,
/// --argjson, $ENV, $ARGS), if/elif/else, try/catch, `?`, reduce, foreach, path expressions with assignment
/// (`=`, `|=`, `+=` ...) and del(), and the common builtins. Values are null, bool, double, string,
/// List&lt;object?&gt; and Dictionary&lt;string, object?&gt; (insertion-ordered: entries are never removed in place).
/// Mirrors python/src/topwater/commands/jq.py.
/// shortcut: no `def`, `label`/`break`, `input`/`inputs`, or strftime-style date builtins (only now/todate/fromdate);
/// add them if agents need them.
/// </summary>
internal static class Jq
{
    public static void Register(Shell shell) => shell.Register("jq", Run);

    // ---------- errors ----------

    internal sealed class JqError(object? value) : Exception(value as string ?? Dump(value))
    {
        public object? Value { get; } = value;
    }

    sealed class CompileError(string message) : Exception(message);

    // ---------- values ----------

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static string TypeName(object? v) => v switch
    {
        null => "null",
        bool => "boolean",
        double => "number",
        string => "string",
        List<object?> => "array",
        _ => "object",
    };

    static bool Truthy(object? v) => v is not (null or false);

    /// <summary>Shortest round-trip digits, laid out like Python's repr (and jq): 1e+17, 1.5e-07, 0.0001.</summary>
    internal static string FormatNumber(double v)
    {
        if (double.IsNaN(v)) return "null";
        if (double.IsInfinity(v)) return v > 0 ? "1.7976931348623157e+308" : "-1.7976931348623157e+308";
        if (Math.Floor(v) == v && Math.Abs(v) < 1e17) return ((long)v).ToString(Inv);
        var m = Regex.Match(v.ToString("R", Inv), @"^(-?)(\d+)(?:\.(\d+))?(?:E([+-]\d+))?$");
        string sign = m.Groups[1].Value, intPart = m.Groups[2].Value.TrimStart('0'), frac = m.Groups[3].Value;
        int exp = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, Inv) : 0;
        int decExp = intPart.Length > 0 ? intPart.Length - 1 + exp : exp - (frac.Length - frac.TrimStart('0').Length) - 1;
        var digits = (intPart + frac).TrimStart('0').TrimEnd('0');
        if (decExp < -4 || decExp >= 16)
            return $"{sign}{digits[0]}{(digits.Length > 1 ? "." + digits[1..] : "")}e{(decExp < 0 ? "-" : "+")}{Math.Abs(decExp):00}";
        if (decExp < 0) return sign + "0." + new string('0', -decExp - 1) + digits;
        return sign + digits[..(decExp + 1)] + "." + digits[(decExp + 1)..];
    }

    static string Quote(string s, bool asciiOnly = false)
    {
        var sb = new StringBuilder("\"");
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c == 0x7F || (asciiOnly && c > 0x7E)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Serialises like jq. indent is the per-level indentation ("" = compact).</summary>
    internal static string Dump(object? v, string indent = "", bool sortKeys = false, bool asciiOnly = false)
    {
        var sb = new StringBuilder();
        Write(sb, v, indent, sortKeys, asciiOnly, 0);
        return sb.ToString();
    }

    static void Write(StringBuilder sb, object? v, string indent, bool sortKeys, bool asciiOnly, int level)
    {
        Shell.CheckStack(); // a deep value or program must not overflow the host's stack
        switch (v)
        {
            case null: sb.Append("null"); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case double d: sb.Append(FormatNumber(d)); return;
            case string s: sb.Append(Quote(s, asciiOnly)); return;
        }
        bool isList = v is List<object?>;
        var list = v as List<object?>;
        var obj = v as Dictionary<string, object?>;
        int count = isList ? list!.Count : obj!.Count;
        if (count == 0) { sb.Append(isList ? "[]" : "{}"); return; }
        sb.Append(isList ? '[' : '{');
        var keys = obj == null ? null : sortKeys ? obj.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() : obj.Keys.ToList();
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            if (indent.Length > 0) { sb.Append('\n'); for (int k = 0; k <= level; k++) sb.Append(indent); }
            if (isList) Write(sb, list![i], indent, sortKeys, asciiOnly, level + 1);
            else
            {
                sb.Append(Quote(keys![i], asciiOnly)).Append(indent.Length > 0 ? ": " : ":");
                Write(sb, obj![keys[i]], indent, sortKeys, asciiOnly, level + 1);
            }
        }
        if (indent.Length > 0) { sb.Append('\n'); for (int k = 0; k < level; k++) sb.Append(indent); }
        sb.Append(isList ? ']' : '}');
    }

    static int Rank(object? v) => v switch
    {
        null => 0, false => 1, true => 2, double => 3, string => 4, List<object?> => 5, _ => 6,
    };

    /// <summary>jq's total order: null &lt; false &lt; true &lt; numbers &lt; strings &lt; arrays &lt; objects.</summary>
    internal static int Compare(object? a, object? b)
    {
        Shell.CheckStack();
        int ra = Rank(a), rb = Rank(b);
        if (ra != rb) return ra < rb ? -1 : 1;
        switch (a)
        {
            case double x: return x.CompareTo((double)b!);
            case string s: return Math.Sign(string.CompareOrdinal(s, (string)b!));
            case List<object?> la:
                var lb = (List<object?>)b!;
                for (int i = 0; i < Math.Min(la.Count, lb.Count); i++)
                {
                    int c = Compare(la[i], lb[i]);
                    if (c != 0) return c;
                }
                return la.Count.CompareTo(lb.Count);
            case Dictionary<string, object?> oa:
                var ob = (Dictionary<string, object?>)b!;
                var ka = oa.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList<object?>();
                var kb = ob.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList<object?>();
                int kc = Compare(ka, kb);
                if (kc != 0) return kc;
                foreach (var k in oa.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    int c = Compare(oa[k], ob[k]);
                    if (c != 0) return c;
                }
                return 0;
        }
        return 0;
    }

    static readonly IComparer<object?> Order = Comparer<object?>.Create(Compare);

    // ---------- JSON input ----------

    /// <summary>Parses whitespace-separated JSON values (jq accepts several documents in one input).</summary>
    internal static List<object?> ParseStream(string text)
    {
        var p = new JsonParser(text);
        var result = new List<object?>();
        while (true)
        {
            p.SkipSpace();
            if (p.AtEnd) return result;
            result.Add(p.Value());
        }
    }

    sealed class JsonParser(string s)
    {
        int _i;
        public bool AtEnd => _i >= s.Length;

        public void SkipSpace() { while (_i < s.Length && s[_i] is ' ' or '\t' or '\r' or '\n') _i++; }

        ShellException Fail(string what)
        {
            int line = 1;
            for (int k = 0; k < Math.Min(_i, s.Length); k++) if (s[k] == '\n') line++;
            return new ShellException($"error (at <stdin>:{line}): Cannot parse input: {what} at line {line}", 2);
        }

        public object? Value()
        {
            Shell.CheckStack();
            SkipSpace();
            if (AtEnd) throw Fail("Expecting value");
            char c = s[_i];
            switch (c)
            {
                case '{':
                {
                    _i++;
                    var obj = new Dictionary<string, object?>();
                    SkipSpace();
                    if (_i < s.Length && s[_i] == '}') { _i++; return obj; }
                    while (true)
                    {
                        SkipSpace();
                        if (_i >= s.Length || s[_i] != '"') throw Fail("Expecting property name enclosed in double quotes");
                        var key = Str();
                        SkipSpace();
                        if (_i >= s.Length || s[_i] != ':') throw Fail("Expecting ':' delimiter");
                        _i++;
                        obj[key] = Value();
                        SkipSpace();
                        if (_i < s.Length && s[_i] == ',') { _i++; continue; }
                        if (_i < s.Length && s[_i] == '}') { _i++; return obj; }
                        throw Fail("Expecting ',' delimiter");
                    }
                }
                case '[':
                {
                    _i++;
                    var list = new List<object?>();
                    SkipSpace();
                    if (_i < s.Length && s[_i] == ']') { _i++; return list; }
                    while (true)
                    {
                        list.Add(Value());
                        SkipSpace();
                        if (_i < s.Length && s[_i] == ',') { _i++; continue; }
                        if (_i < s.Length && s[_i] == ']') { _i++; return list; }
                        throw Fail("Expecting ',' delimiter");
                    }
                }
                case '"': return Str();
            }
            if (string.CompareOrdinal(s, _i, "true", 0, 4) == 0) { _i += 4; return true; }
            if (string.CompareOrdinal(s, _i, "false", 0, 5) == 0) { _i += 5; return false; }
            if (string.CompareOrdinal(s, _i, "null", 0, 4) == 0) { _i += 4; return null; }
            var m = NumberRe.Match(s, _i);
            if (m.Success && m.Index == _i && m.Length > 0)
            {
                _i += m.Length;
                return double.Parse(m.Value, NumberStyles.Float, Inv);
            }
            throw Fail("Expecting value");
        }

        static readonly Regex NumberRe = new(@"\G-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?");

        string Str()
        {
            _i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (_i >= s.Length) throw Fail("Unterminated string starting");
                char c = s[_i++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw Fail("Invalid control character");
                if (c != '\\') { sb.Append(c); continue; }
                if (_i >= s.Length) throw Fail("Unterminated string starting");
                char e = s[_i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '"' or '\\' or '/': sb.Append(e); break;
                    case 'u':
                        if (_i + 4 > s.Length || !s.Substring(_i, 4).All(Uri.IsHexDigit)) throw Fail("Invalid \\uXXXX escape");
                        sb.Append((char)Convert.ToInt32(s.Substring(_i, 4), 16));
                        _i += 4;
                        break;
                    default: throw Fail("Invalid \\escape");
                }
            }
        }
    }

    // ---------- lexer ----------

    enum Tk { Num, Str, Field, Var, Format, Ident, Op, Eof }

    sealed record Token(Tk Kind, object? Value)
    {
        public string Text => Value as string ?? "";
    }

    static readonly string[] Punct = ["|=", "+=", "-=", "*=", "/=", "%=", "//=", "==", "!=", "<=", ">=", "//", "..",
        "|", ",", "=", "<", ">", "+", "-", "*", "/", "%", "(", ")", "[", "]", "{", "}", ":", ";", "?", "."];
    static readonly HashSet<string> Keywords = ["if", "then", "elif", "else", "end", "as", "reduce", "foreach", "try", "catch", "and", "or", "def", "label"];

    static readonly Regex NumTok = new(@"\G\d*\.?\d*([eE][+-]?\d+)?");
    static readonly Regex IdentTok = new(@"\G[$@]?[A-Za-z_][A-Za-z0-9_]*(::[A-Za-z_][A-Za-z0-9_]*)*");
    static readonly Regex FieldTok = new(@"\G[A-Za-z_][A-Za-z0-9_]*");

    static List<Token> Lex(string src)
    {
        var toks = new List<Token>();
        int i = 0, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#') { while (i < n && src[i] != '\n') i++; continue; }
            if (c == '"')
            {
                var (parts, end) = LexString(src, i + 1);
                toks.Add(new(Tk.Str, parts));
                i = end;
                continue;
            }
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(src[i + 1])))
            {
                var m = NumTok.Match(src, i);
                toks.Add(new(Tk.Num, double.Parse(m.Value, NumberStyles.Float, Inv)));
                i += m.Length;
                continue;
            }
            if (c == '.' && i + 1 < n && (char.IsAsciiLetter(src[i + 1]) || src[i + 1] == '_'))
            {
                var m = FieldTok.Match(src, i + 1);
                toks.Add(new(Tk.Field, m.Value));
                i += 1 + m.Length;
                continue;
            }
            if (c is '$' or '@' or '_' || char.IsAsciiLetter(c))
            {
                var m = IdentTok.Match(src, i);
                if (!m.Success) throw new CompileError($"syntax error, unexpected '{c}'");
                var word = m.Value;
                i += m.Length;
                if (word[0] == '$') toks.Add(new(Tk.Var, word[1..]));
                else if (word[0] == '@') toks.Add(new(Tk.Format, word[1..]));
                else toks.Add(new(Keywords.Contains(word) ? Tk.Op : Tk.Ident, word));
                continue;
            }
            var p = Punct.FirstOrDefault(op => string.CompareOrdinal(src, i, op, 0, op.Length) == 0)
                ?? throw new CompileError($"syntax error, unexpected '{c}'");
            toks.Add(new(Tk.Op, p));
            i += p.Length;
        }
        toks.Add(new(Tk.Eof, null));
        return toks;
    }

    /// <summary>A string literal body after the opening quote: parts are strings and (source of \(...)) interpolations.</summary>
    static (List<object> Parts, int End) LexString(string src, int i)
    {
        var parts = new List<object>();
        var buf = new StringBuilder();
        int n = src.Length;
        while (i < n && src[i] != '"')
        {
            char c = src[i];
            if (c != '\\') { buf.Append(c); i++; continue; }
            char e = i + 1 < n ? src[i + 1] : '\0';
            if (e == '(')
            {
                int depth = 1, j = i + 2;
                while (j < n && depth > 0)
                {
                    if (src[j] == '"') { j = LexString(src, j + 1).End; continue; }
                    if (src[j] == '(') depth++;
                    else if (src[j] == ')') depth--;
                    j++;
                }
                if (depth > 0) throw new CompileError("unterminated string interpolation");
                if (buf.Length > 0) { parts.Add(buf.ToString()); buf.Clear(); }
                parts.Add(new Interp(src[(i + 2)..(j - 1)]));
                i = j;
                continue;
            }
            if (e == 'u' && i + 6 <= n && src.Substring(i + 2, 4).All(Uri.IsHexDigit))
            {
                int code = Convert.ToInt32(src.Substring(i + 2, 4), 16);
                i += 6;
                if (code is >= 0xD800 and < 0xDC00 && i + 6 <= n && src[i] == '\\' && src[i + 1] == 'u' && src.Substring(i + 2, 4).All(Uri.IsHexDigit))
                {
                    code = 0x10000 + ((code - 0xD800) << 10) + (Convert.ToInt32(src.Substring(i + 2, 4), 16) - 0xDC00);
                    i += 6;
                }
                buf.Append(code is >= 0xD800 and <= 0xDFFF ? "\uFFFD" : char.ConvertFromUtf32(code));
                continue;
            }
            buf.Append(e switch
            {
                'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', '"' => '"', '\\' => '\\', '/' => '/',
                _ => throw new CompileError($"invalid escape \\{e} in string literal"),
            });
            i += 2;
        }
        if (i >= n) throw new CompileError("unterminated string literal");
        if (buf.Length > 0 || parts.Count == 0) parts.Add(buf.ToString());
        return (parts, i + 1);
    }

    sealed record Interp(string Source);

    // ---------- AST ----------

    abstract record Node;
    sealed record Identity : Node;
    sealed record Lit(object? Value) : Node;
    sealed record PipeN(Node L, Node R) : Node;
    sealed record CommaN(Node L, Node R) : Node;
    sealed record IndexN(Node Target, Node Key) : Node;
    sealed record SliceN(Node Target, Node? Lo, Node? Hi) : Node;
    sealed record IterateN(Node Target) : Node;
    sealed record ArrayN(Node? Body) : Node;
    sealed record ObjectN(List<(Node Key, Node Value)> Entries) : Node;
    sealed record StringN(List<object> Parts, string? Fmt) : Node; // parts: string or Node
    sealed record FormatN(string Name) : Node;
    sealed record NegN(Node X) : Node;
    sealed record BinOp(string Op, Node L, Node R) : Node;
    sealed record AndN(Node L, Node R) : Node;
    sealed record OrN(Node L, Node R) : Node;
    sealed record AltN(Node L, Node R) : Node;
    sealed record IfN(Node Cond, Node Then, Node? Else) : Node;
    sealed record TryN(Node Body, Node? Handler) : Node;
    sealed record VarN(string Name) : Node;
    sealed record AsN(Node Source, Pat Pattern, Node Body) : Node;
    sealed record ReduceN(Node Source, Pat Pattern, Node Init, Node Update) : Node;
    sealed record ForeachN(Node Source, Pat Pattern, Node Init, Node Update, Node? Extract) : Node;
    sealed record AssignN(string Op, Node L, Node R) : Node;
    sealed record CallN(string Name, List<Node> Args) : Node;
    sealed record DefN(string Key, List<string> Params, Node Body, Node Rest) : Node; // key: name/arity; a $param keeps its $

    abstract record Pat;
    sealed record VarPat(string Name) : Pat;
    sealed record ArrayPat(List<Pat> Items) : Pat;
    sealed record ObjectPat(List<(Node Key, Pat Value)> Entries) : Pat;

    // ---------- parser ----------

    /// <summary>Recursive descent over jq's precedence, lowest first: `|`, `,`, `//`, assignment, or, and, comparison,
    /// + -, * / %, unary minus, postfix (.a [i] [] ?).</summary>
    sealed class Parser(string src)
    {
        readonly List<Token> _t = Lex(src);
        int _p;

        Token Peek(int k = 0) => _t[Math.Min(_p + k, _t.Count - 1)];
        bool IsOp(params string[] ops) => Peek() is { Kind: Tk.Op } t && ops.Contains(t.Text);
        Token Take() => _t[_p++];

        void Expect(string op)
        {
            if (!IsOp(op)) throw Unexpected();
            _p++;
        }

        CompileError Unexpected()
        {
            var t = Peek();
            return new CompileError("syntax error, unexpected " + (t.Kind == Tk.Eof ? "end of file" : t.Kind == Tk.Str ? "'\"'" : $"'{(t.Value is double d ? FormatNumber(d) : t.Text)}'"));
        }

        public Node Parse()
        {
            var node = Pipe();
            if (Peek().Kind != Tk.Eof) throw Unexpected();
            return node;
        }

        Node Pipe()
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (IsOp("def"))
            {
                _p++;
                var t = Peek();
                if (t.Kind != Tk.Ident || t.Text is "true" or "false" or "null") throw new CompileError(Unexpected().Message + ", expecting IDENT");
                _p++;
                var ps = new List<string>();
                if (IsOp("("))
                {
                    _p++;
                    while (true)
                    {
                        var q = Peek();
                        if (q.Kind is not (Tk.Ident or Tk.Var)) throw new CompileError(Unexpected().Message + ", expecting IDENT or $name");
                        _p++;
                        ps.Add((q.Kind == Tk.Var ? "$" : "") + q.Text);
                        if (!IsOp(";")) break;
                        _p++;
                    }
                    Expect(")");
                }
                Expect(":");
                var body = Pipe();
                Expect(";");
                return new DefN($"{t.Text}/{ps.Count}", ps, body, Pipe());
            }
            var left = Comma();
            if (IsOp("as"))
            {
                _p++;
                var pattern = Pattern();
                Expect("|");
                return new AsN(left, pattern, Pipe());
            }
            if (IsOp("|")) { _p++; return new PipeN(left, Pipe()); }
            return left;
        }

        Pat Pattern()
        {
            Shell.CheckStack();
            var t = Peek();
            if (t.Kind == Tk.Var) { _p++; return new VarPat(t.Text); }
            if (IsOp("["))
            {
                _p++;
                var items = new List<Pat> { Pattern() };
                while (IsOp(",")) { _p++; items.Add(Pattern()); }
                Expect("]");
                return new ArrayPat(items);
            }
            if (IsOp("{"))
            {
                _p++;
                var entries = new List<(Node, Pat)>();
                while (true)
                {
                    var k = Peek();
                    if (k.Kind == Tk.Var) { _p++; entries.Add((new Lit(k.Text), new VarPat(k.Text))); }
                    else
                    {
                        var key = ObjectKey();
                        Expect(":");
                        entries.Add((key, Pattern()));
                    }
                    if (!IsOp(",")) break;
                    _p++;
                }
                Expect("}");
                return new ObjectPat(entries);
            }
            throw Unexpected();
        }

        Node Comma()
        {
            var left = Alternative();
            while (IsOp(",")) { _p++; left = new CommaN(left, Alternative()); }
            return left;
        }

        Node Alternative()
        {
            Shell.CheckStack();
            var left = Assignment();
            if (IsOp("//")) { _p++; return new AltN(left, Alternative()); }
            return left;
        }

        Node Assignment()
        {
            var left = OrExpr();
            if (IsOp("=", "|=", "+=", "-=", "*=", "/=", "%=", "//=")) { var op = Take().Text; return new AssignN(op, left, Alternative()); }
            return left;
        }

        Node OrExpr()
        {
            var left = AndExpr();
            while (IsOp("or")) { _p++; left = new OrN(left, AndExpr()); }
            return left;
        }

        Node AndExpr()
        {
            var left = Comparison();
            while (IsOp("and")) { _p++; left = new AndN(left, Comparison()); }
            return left;
        }

        Node Comparison()
        {
            var left = Additive();
            if (IsOp("==", "!=", "<", "<=", ">", ">=")) { var op = Take().Text; return new BinOp(op, left, Additive()); }
            return left;
        }

        Node Additive()
        {
            var left = Multiplicative();
            while (IsOp("+", "-")) { var op = Take().Text; left = new BinOp(op, left, Multiplicative()); }
            return left;
        }

        Node Multiplicative()
        {
            var left = Unary();
            while (IsOp("*", "/", "%")) { var op = Take().Text; left = new BinOp(op, left, Unary()); }
            return left;
        }

        Node Unary()
        {
            Shell.CheckStack();
            if (IsOp("-")) { _p++; return new NegN(Unary()); }
            return Postfix();
        }

        Node Postfix()
        {
            Shell.CheckStack();
            var node = Primary();
            while (true)
            {
                var t = Peek();
                if (t.Kind == Tk.Field) { _p++; node = new IndexN(node, new Lit(t.Text)); }
                else if (IsOp(".") && Peek(1).Kind == Tk.Str) { _p++; node = new IndexN(node, StringLit(null)); }
                else if (IsOp("[") || (IsOp(".") && Peek(1) is { Kind: Tk.Op, Text: "[" }))
                {
                    if (IsOp(".")) _p++;
                    node = Bracket(node);
                }
                else if (IsOp("?")) { _p++; node = new TryN(node, null); }
                else return node;
            }
        }

        Node Bracket(Node target)
        {
            Expect("[");
            if (IsOp("]")) { _p++; return new IterateN(target); }
            if (IsOp(":"))
            {
                _p++;
                var hi = Pipe();
                Expect("]");
                return new SliceN(target, null, hi);
            }
            var idx = Pipe();
            if (IsOp(":"))
            {
                _p++;
                var hi = IsOp("]") ? null : Pipe();
                Expect("]");
                return new SliceN(target, idx, hi);
            }
            Expect("]");
            return new IndexN(target, idx);
        }

        Node StringLit(string? fmt)
        {
            var parts = (List<object>)Take().Value!;
            var outParts = parts.Select(p => p is Interp ip ? (object)new Parser(ip.Source).Parse() : p).ToList();
            if (outParts is [string only] && fmt == null) return new Lit(only);
            return new StringN(outParts, fmt);
        }

        Node ObjectKey()
        {
            var t = Peek();
            if (t.Kind == Tk.Ident || (t.Kind == Tk.Op && Keywords.Contains(t.Text))) { _p++; return new Lit(t.Text); }
            if (t.Kind == Tk.Var) { _p++; return new VarN(t.Text); }
            if (t.Kind == Tk.Str) return StringLit(null);
            if (t.Kind == Tk.Format && Peek(1).Kind == Tk.Str) { _p++; return StringLit(t.Text); }
            if (IsOp("("))
            {
                _p++;
                var key = Pipe();
                Expect(")");
                return key;
            }
            throw Unexpected();
        }

        Node Primary()
        {
            var t = Peek();
            switch (t.Kind)
            {
                case Tk.Num: _p++; return new Lit(t.Value);
                case Tk.Str: return StringLit(null);
                case Tk.Format:
                    _p++;
                    return Peek().Kind == Tk.Str ? StringLit(t.Text) : new FormatN(t.Text);
                case Tk.Field: _p++; return new IndexN(new Identity(), new Lit(t.Text));
                case Tk.Var: _p++; return new VarN(t.Text);
                case Tk.Ident:
                    _p++;
                    if (t.Text == "true") return new Lit(true);
                    if (t.Text == "false") return new Lit(false);
                    if (t.Text == "null") return new Lit(null);
                    var args = new List<Node>();
                    if (IsOp("("))
                    {
                        _p++;
                        args.Add(Pipe());
                        while (IsOp(";")) { _p++; args.Add(Pipe()); }
                        Expect(")");
                    }
                    return new CallN(t.Text, args);
                case Tk.Op: break;
                default: throw Unexpected();
            }
            switch (t.Text)
            {
                case ".":
                    _p++;
                    if (Peek().Kind == Tk.Str) return new IndexN(new Identity(), StringLit(null));
                    if (IsOp("[")) return Bracket(new Identity());
                    return new Identity();
                case "..": _p++; return new CallN("recurse", []);
                case "(":
                {
                    _p++;
                    var node = Pipe();
                    Expect(")");
                    return node;
                }
                case "[":
                {
                    _p++;
                    if (IsOp("]")) { _p++; return new ArrayN(null); }
                    var node = Pipe();
                    Expect("]");
                    return new ArrayN(node);
                }
                case "{": return Object();
                case "if": return If();
                case "try":
                {
                    _p++;
                    var body = Postfix();
                    Node? handler = null;
                    if (IsOp("catch")) { _p++; handler = Postfix(); }
                    return new TryN(body, handler);
                }
                case "reduce" or "foreach":
                {
                    _p++;
                    var source = Postfix();
                    Expect("as");
                    var pattern = Pattern();
                    Expect("(");
                    var init = Pipe();
                    Expect(";");
                    var update = Pipe();
                    Node? extract = null;
                    if (t.Text == "foreach" && IsOp(";")) { _p++; extract = Pipe(); }
                    Expect(")");
                    return t.Text == "reduce" ? new ReduceN(source, pattern, init, update) : new ForeachN(source, pattern, init, update, extract);
                }
            }
            throw Unexpected();
        }

        Node If()
        {
            Shell.CheckStack();
            _p++;
            var cond = Pipe();
            Expect("then");
            var then = Pipe();
            if (IsOp("elif")) return new IfN(cond, then, If());
            Node? other = null;
            if (IsOp("else")) { _p++; other = Pipe(); }
            Expect("end");
            return new IfN(cond, then, other);
        }

        Node Object()
        {
            Expect("{");
            var entries = new List<(Node, Node)>();
            while (!IsOp("}"))
            {
                var key = ObjectKey();
                Node value;
                if (IsOp(":"))
                {
                    _p++;
                    value = Alternative();
                    while (IsOp("|")) { _p++; value = new PipeN(value, Alternative()); }
                }
                else if (key is VarN v) { value = v; key = new Lit(v.Name); }
                else value = new IndexN(new Identity(), key);
                entries.Add((key, value));
                if (!IsOp(",")) break;
                _p++;
            }
            Expect("}");
            return new ObjectN(entries);
        }
    }

    // ---------- evaluation helpers ----------

    static string Brief(object? v)
    {
        var text = Dump(v);
        return text.Length <= 11 ? text : text[..10] + "...";
    }

    static string ErrType(object? v) => $"{TypeName(v)} ({Brief(v)})";

    static object? Index(object? v, object? k)
    {
        switch (k)
        {
            case string key:
                if (v == null) return null;
                if (v is Dictionary<string, object?> o) return o.GetValueOrDefault(key);
                throw new JqError($"Cannot index {TypeName(v)} with \"{key}\"");
            case double d:
                if (v == null) return null;
                if (v is List<object?> l)
                {
                    long i = (long)Math.Floor(d);
                    if (i < 0) i += l.Count;
                    return i >= 0 && i < l.Count ? l[(int)i] : null;
                }
                throw new JqError($"Cannot index {TypeName(v)} with number");
            case Dictionary<string, object?> slice when v is null or List<object?> or string:
                return Slice(v, slice.GetValueOrDefault("start"), slice.GetValueOrDefault("end"));
            case List<object?> sub when v is List<object?>:
                return Indices(v, sub);
        }
        throw new JqError($"Cannot index {TypeName(v)} with {TypeName(k)}");
    }

    static (int, int) SliceBounds(int n, object? lo, object? hi)
    {
        int Clamp(object? x, int dflt)
        {
            if (x == null) return dflt;
            if (x is not double d) throw new JqError("Start and end indices of an array slice must be numbers");
            long i = (long)Math.Floor(d);
            if (i < 0) i += n;
            return (int)Math.Max(0, Math.Min(n, i));
        }
        int a = Clamp(lo, 0), b = Clamp(hi, n);
        return (a, Math.Max(a, b));
    }

    static object? Slice(object? v, object? lo, object? hi)
    {
        switch (v)
        {
            case null: return null;
            case List<object?> l: { var (a, b) = SliceBounds(l.Count, lo, hi); return l.GetRange(a, b - a); }
            case string s: { var (a, b) = SliceBounds(s.Length, lo, hi); return s[a..b]; }
        }
        throw new JqError($"Cannot index {TypeName(v)} with object");
    }

    static object? Indices(object? v, object? sub)
    {
        if (v == null) return null;
        if (v is string s && sub is string t)
        {
            if (t.Length == 0) return null;
            var r = new List<object?>();
            for (int i = 0; i < s.Length; i++) if (string.CompareOrdinal(s, i, t, 0, t.Length) == 0 && i + t.Length <= s.Length) r.Add((double)i);
            return r;
        }
        if (v is List<object?> l)
        {
            var needle = sub as List<object?> ?? [sub];
            if (needle.Count == 0) return null;
            var r = new List<object?>();
            for (int i = 0; i + needle.Count <= l.Count; i++)
                if (needle.Select((x, k) => Compare(l[i + k], x) == 0).All(b => b)) r.Add((double)i);
            return r;
        }
        throw new JqError($"Cannot determine indices of {TypeName(sub)} in {TypeName(v)}");
    }

    static List<object?> Iterate(object? v) => v switch
    {
        List<object?> l => l,
        Dictionary<string, object?> o => o.Values.ToList(),
        null => throw new JqError("Cannot iterate over null"),
        _ => throw new JqError($"Cannot iterate over {ErrType(v)}"),
    };

    // jq runs synchronously on one thread, so the string limit can ride along with it instead of through every call
    [ThreadStatic] static long t_maxString;
    [ThreadStatic] static DateTime t_now;

    static T Sized<T>(T value, long size)
    {
        if (t_maxString > 0 && size > t_maxString) throw Shell.TooLarge("jq: value", t_maxString, "produce smaller values or stream them with .[]");
        return value;
    }

    static object? Arith(string op, object? a, object? b)
    {
        switch (op)
        {
            case "+":
                if (a == null) return b;
                if (b == null) return a;
                if (a is double x1 && b is double y1) return x1 + y1;
                if (a is string s1 && b is string t1) return Sized(s1, s1.Length + (long)t1.Length) + t1;
                if (a is List<object?> l1 && b is List<object?> m1) return Sized(l1, 8L * (l1.Count + m1.Count)).Concat(m1).ToList(); // an element costs at least a reference
                if (a is Dictionary<string, object?> o1 && b is Dictionary<string, object?> p1)
                {
                    var r = new Dictionary<string, object?>(o1);
                    foreach (var (k, v) in p1) r[k] = v;
                    return r;
                }
                break;
            case "-":
                if (a is double x2 && b is double y2) return x2 - y2;
                if (a is List<object?> l2 && b is List<object?> m2) return l2.Where(x => m2.All(y => Compare(x, y) != 0)).ToList();
                break;
            case "*":
                if (a is double x3 && b is double y3) return x3 * y3;
                if ((a is string && b is double) || (b is string && a is double))
                {
                    var (s, k) = a is string sa ? (sa, (double)b!) : ((string)b!, (double)a!);
                    return k > 0 ? string.Concat(Enumerable.Repeat(Sized(s, (long)Math.Min(s.Length * Math.Ceiling(k), long.MaxValue)), (int)Math.Ceiling(k))) : null;
                }
                if (a is Dictionary<string, object?> o3 && b is Dictionary<string, object?> p3) return DeepMerge(o3, p3);
                break;
            case "/" or "%":
                if (a is double x4 && b is double y4)
                {
                    if (op == "/" ? y4 == 0 : (long)y4 == 0)
                        throw new JqError($"{ErrType(a)} and {ErrType(b)} cannot be divided because the divisor is zero");
                    return op == "/" ? x4 / y4 : (double)((long)x4 % (long)y4);
                }
                if (op == "/" && a is string s4 && b is string t4) return SplitString(s4, t4);
                break;
        }
        var verb = op switch { "+" => "added", "-" => "subtracted", "*" => "multiplied", _ => "divided" };
        throw new JqError($"{ErrType(a)} and {ErrType(b)} cannot be {verb}");
    }

    static Dictionary<string, object?> DeepMerge(Dictionary<string, object?> a, Dictionary<string, object?> b)
    {
        Shell.CheckStack();
        var r = new Dictionary<string, object?>(a);
        foreach (var (k, v) in b)
            r[k] = r.GetValueOrDefault(k) is Dictionary<string, object?> x && v is Dictionary<string, object?> y ? DeepMerge(x, y) : v;
        return r;
    }

    static List<object?> SplitString(string s, string sep) =>
        s.Length == 0 ? [] : sep.Length == 0 ? s.Select(c => (object?)c.ToString()).ToList() : s.Split(sep).Select(x => (object?)x).ToList();

    static string ToStr(object? v) => v as string ?? Dump(v);

    static (Regex Re, bool Global) MakeRegex(object? pattern, object? flags)
    {
        if (pattern is not string p) throw new JqError($"{ErrType(pattern)} cannot be matched, as it is not a string");
        if (flags != null && flags is not string) throw new JqError($"{ErrType(flags)} is not a string");
        var f = (string?)flags ?? "";
        var opts = RegexOptions.None;
        foreach (var c in f)
        {
            if (c == 'i') opts |= RegexOptions.IgnoreCase;
            else if (c == 'x') opts |= RegexOptions.IgnorePatternWhitespace;
            else if (c == 's') opts |= RegexOptions.Singleline;
            else if (!"gnlp".Contains(c)) throw new JqError($"{f} is not a valid modifier string");
        }
        try { return (new Regex(p, opts, Shell.RegexTimeout), f.Contains('g')); }
        catch (ArgumentException e) { throw new JqError($"{p} (at offset 0) is not a valid regex: {e.Message}"); }
    }

    static List<Match> Matches(Regex re, string s, bool global) =>
        global ? re.Matches(s).ToList() : re.Match(s) is { Success: true } m ? [m] : [];

    static IEnumerable<string> NamedGroups(Regex re) => re.GetGroupNames().Where(n => !int.TryParse(n, out _));

    static Dictionary<string, object?> MatchObject(Regex re, Match m)
    {
        var caps = new List<object?>();
        foreach (int g in re.GetGroupNumbers().Where(g => g > 0))
        {
            var grp = m.Groups[g];
            var name = re.GroupNameFromNumber(g);
            caps.Add(new Dictionary<string, object?>
            {
                ["offset"] = grp.Success ? (double)grp.Index : -1.0, ["length"] = (double)(grp.Success ? grp.Length : 0),
                ["string"] = grp.Success ? grp.Value : null, ["name"] = int.TryParse(name, out _) ? null : name,
            });
        }
        return new() { ["offset"] = (double)m.Index, ["length"] = (double)m.Length, ["string"] = m.Value, ["captures"] = caps };
    }

    static string Format(string fmt, object? v)
    {
        switch (fmt)
        {
            case "text": return ToStr(v);
            case "json": return Dump(v);
            case "csv" or "tsv":
                if (v is not List<object?> row) throw new JqError($"{ErrType(v)} cannot be {fmt}-formatted, only an array can be");
                return string.Join(fmt == "csv" ? "," : "\t", row.Select(x => x switch
                {
                    List<object?> or Dictionary<string, object?> => throw new JqError($"{ErrType(x)} is not valid in a csv row"),
                    null => "",
                    string s => fmt == "csv" ? "\"" + s.Replace("\"", "\"\"") + "\"" : s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r"),
                    _ => ToStr(x),
                }));
            case "html":
                return ToStr(v).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&#39;").Replace("\"", "&quot;");
            case "uri":
                var sb = new StringBuilder();
                foreach (var b in Encoding.UTF8.GetBytes(ToStr(v)))
                    if (char.IsAsciiLetterOrDigit((char)b) || "-_.~".Contains((char)b)) sb.Append((char)b);
                    else sb.Append('%').Append(b.ToString("X2"));
                return sb.ToString();
            case "sh":
                return string.Join(" ", (v as List<object?> ?? [v]).Select(x => x switch
                {
                    List<object?> or Dictionary<string, object?> => throw new JqError($"{ErrType(x)} can not be escaped for shell"),
                    string s => "'" + s.Replace("'", "'\\''") + "'",
                    _ => ToStr(x),
                }));
            case "base64": return Convert.ToBase64String(Encoding.UTF8.GetBytes(ToStr(v)));
            case "base64d":
                var t = ToStr(v).TrimEnd('=');
                try { return Encoding.UTF8.GetString(Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4))); }
                catch (FormatException) { throw new JqError($"{ErrType(v)} is not valid base64 data"); }
        }
        throw new JqError($"{fmt} is not a valid format");
    }

    static IEnumerable<List<object?>> AllPaths(object? v, bool leafOnly = false, List<object?>? prefix = null)
    {
        Shell.CheckStack();
        prefix ??= [];
        IEnumerable<(object? Key, object? Value)> items = v switch
        {
            Dictionary<string, object?> o => o.Select(p => ((object?)p.Key, p.Value)).ToList(),
            List<object?> l => l.Select((x, i) => ((object?)(double)i, x)).ToList(),
            _ => [],
        };
        foreach (var (k, x) in items)
        {
            var p = new List<object?>(prefix) { k };
            if (!leafOnly || x is not (List<object?> or Dictionary<string, object?>)) yield return p;
            foreach (var q in AllPaths(x, leafOnly, p)) yield return q;
        }
    }

    static IEnumerable<object?> Recurse(object? v)
    {
        Shell.CheckStack();
        yield return v;
        if (v is List<object?> or Dictionary<string, object?>)
            foreach (var x in Iterate(v).ToList())
                foreach (var y in Recurse(x)) yield return y;
    }

    static object? GetPath(object? v, object? path)
    {
        if (path is not List<object?> p) throw new JqError("Path must be specified as an array");
        foreach (var k in p)
        {
            if (v == null) return null;
            v = Index(v, k);
        }
        return v;
    }

    static object? SetPath(object? v, object? path, object? value)
    {
        Shell.CheckStack();
        if (path is not List<object?> p) throw new JqError("Path must be specified as an array");
        if (p.Count == 0) return value;
        var k = p[0];
        var rest = p.GetRange(1, p.Count - 1);
        switch (k)
        {
            case string key:
            {
                v ??= new Dictionary<string, object?>();
                if (v is not Dictionary<string, object?> o) throw new JqError($"Cannot index {TypeName(v)} with \"{key}\"");
                return new Dictionary<string, object?>(o) { [key] = SetPath(o.GetValueOrDefault(key), rest, value) };
            }
            case double d:
            {
                v ??= new List<object?>();
                if (v is not List<object?> l) throw new JqError($"Cannot index {TypeName(v)} with number");
                long i = (long)d;
                if (i < 0)
                {
                    i += l.Count;
                    if (i < 0) throw new JqError("Out of bounds negative array index");
                }
                if (i > 100_000_000) throw new JqError("Array index too large");
                var r = new List<object?>(l);
                while (r.Count <= i) r.Add(null);
                r[(int)i] = SetPath(r[(int)i], rest, value);
                return r;
            }
            case Dictionary<string, object?> slice:
            {
                v ??= new List<object?>();
                if (v is not List<object?> l) throw new JqError($"Cannot update field at object index of {TypeName(v)}");
                var (a, b) = SliceBounds(l.Count, slice.GetValueOrDefault("start"), slice.GetValueOrDefault("end"));
                if (SetPath(l.GetRange(a, b - a), rest, value) is not List<object?> repl) throw new JqError("A slice of an array can only be assigned another array");
                return l.Take(a).Concat(repl).Concat(l.Skip(b)).ToList();
            }
        }
        throw new JqError($"Invalid path component {Dump(k)}");
    }

    static object? DelPaths(object? v, IEnumerable<object?> paths)
    {
        foreach (var p in paths.OrderBy(x => x, Order).Reverse()) v = DelPath(v, (List<object?>)p!);
        return v;
    }

    static object? DelPath(object? v, List<object?> path)
    {
        Shell.CheckStack();
        if (path.Count == 0 || v == null) return null;
        var k = path[0];
        if (path.Count > 1)
        {
            var child = Index(v, k);
            return child == null ? v : SetPath(v, new List<object?> { k }, DelPath(child, path.GetRange(1, path.Count - 1)));
        }
        switch (k)
        {
            case string key:
                if (v is not Dictionary<string, object?> o) throw new JqError($"Cannot delete field at object index of {TypeName(v)}");
                return o.Where(p => p.Key != key).ToDictionary(p => p.Key, p => p.Value);
            case double d:
                if (v is not List<object?> l) throw new JqError($"Cannot delete field at index of {TypeName(v)}");
                long i = (long)d + (d < 0 ? l.Count : 0);
                return i >= 0 && i < l.Count ? l.Where((_, idx) => idx != i).ToList() : l;
            case Dictionary<string, object?> slice when v is List<object?> sl:
                var (a, b) = SliceBounds(sl.Count, slice.GetValueOrDefault("start"), slice.GetValueOrDefault("end"));
                return sl.Take(a).Concat(sl.Skip(b)).ToList();
        }
        throw new JqError($"Invalid path component {Dump(k)}");
    }

    static List<object?> ToEntries(object? v) => v switch
    {
        Dictionary<string, object?> o => o.Select(p => (object?)new Dictionary<string, object?> { ["key"] = p.Key, ["value"] = p.Value }).ToList(),
        List<object?> l => l.Select((x, i) => (object?)new Dictionary<string, object?> { ["key"] = (double)i, ["value"] = x }).ToList(),
        _ => throw new JqError($"{ErrType(v)} has no keys"),
    };

    static Dictionary<string, object?> FromEntries(object? entries)
    {
        var r = new Dictionary<string, object?>();
        foreach (var e in Iterate(entries))
        {
            if (e is not Dictionary<string, object?> o) throw new JqError($"Cannot index {TypeName(e)} with \"key\"");
            var k = new[] { "key", "k", "name", "Name", "Key", "K" }.Select(n => o.GetValueOrDefault(n)).FirstOrDefault(x => x != null);
            var key = k switch
            {
                double d => FormatNumber(d), bool b => b ? "true" : "false", null => "null", string s => s,
                _ => throw new JqError($"Cannot use {ErrType(k)} as object key"),
            };
            var vk = new[] { "value", "v", "Value", "V" }.FirstOrDefault(o.ContainsKey);
            r[key] = vk == null ? null : o[vk];
        }
        return r;
    }

    static bool Contains(object? a, object? b)
    {
        Shell.CheckStack();
        if (a is Dictionary<string, object?> oa && b is Dictionary<string, object?> ob) return ob.All(p => oa.TryGetValue(p.Key, out var x) && Contains(x, p.Value));
        if (a is List<object?> la && b is List<object?> lb) return lb.All(y => la.Any(x => Contains(x, y)));
        if (a is string sa && b is string sb) return sa.Contains(sb, StringComparison.Ordinal);
        if (TypeName(a) == TypeName(b)) return Compare(a, b) == 0;
        throw new JqError($"{ErrType(a)} and {ErrType(b)} cannot have their containment checked");
    }

    static double Length(object? v) => v switch
    {
        null => 0,
        bool => throw new JqError($"{ErrType(v)} has no length"),
        double d => Math.Abs(d),
        string s => s.Length,
        List<object?> l => l.Count,
        Dictionary<string, object?> o => o.Count,
        _ => 0,
    };

    static List<object?> Keys(object? v, bool sort = true) => v switch
    {
        Dictionary<string, object?> o => (sort ? o.Keys.OrderBy(k => k, StringComparer.Ordinal) : o.Keys.AsEnumerable()).Select(k => (object?)k).ToList(),
        List<object?> l => Enumerable.Range(0, l.Count).Select(i => (object?)(double)i).ToList(),
        _ => throw new JqError($"{ErrType(v)} has no keys"),
    };

    static object? Add(object? v)
    {
        object? acc = null;
        foreach (var x in v == null ? [] : Iterate(v)) acc = Arith("+", acc, x);
        return acc;
    }

    static List<object?> Flatten(object? v, double depth = 1e9)
    {
        Shell.CheckStack();
        if (v is not List<object?> l) throw new JqError($"Cannot iterate over {TypeName(v)}");
        if (depth < 0) throw new JqError("flatten depth must not be negative");
        var r = new List<object?>();
        foreach (var x in l)
            if (x is List<object?> && depth > 0) r.AddRange(Flatten(x, depth - 1));
            else r.Add(x);
        return r;
    }

    static readonly Regex NumberText = new(@"^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*$");

    static double ToNumber(object? v) => v switch
    {
        double d => d,
        string s when NumberText.IsMatch(s) => double.Parse(s.Trim(), NumberStyles.Float, Inv),
        string s => throw new JqError($"Cannot parse '{s}' as JSON"),
        _ => throw new JqError($"{ErrType(v)} cannot be parsed as a number"),
    };

    static List<object?> Sorted(object? v) =>
        v is List<object?> l ? l.OrderBy(x => x, Order).ToList() : throw new JqError($"{ErrType(v)} cannot be sorted, as it is not an array");

    static List<object?> Unique(List<object?> sorted) => sorted.Where((x, i) => i == 0 || Compare(sorted[i - 1], x) != 0).ToList();

    static string StringArg(string what, object? v) => v as string ?? throw new JqError($"{ErrType(v)} cannot be {what}");
    static double NumberArg(object? v) => v is double d ? d : throw new JqError($"{ErrType(v)} number required");

    static string AsciiCase(string s, bool upper) =>
        string.Concat(s.Select(c => upper ? (c is >= 'a' and <= 'z' ? (char)(c - 32) : c) : (c is >= 'A' and <= 'Z' ? (char)(c + 32) : c)));

    static string ToDate(object? v)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(NumberArg(v))).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv); }
        catch (ArgumentOutOfRangeException) { throw new JqError($"{ErrType(v)} is out of range for a date"); }
    }

    static double FromDate(object? v)
    {
        var s = StringArg("parsed as a date", v);
        if (!DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss'Z'", Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
            throw new JqError($"date \"{s}\" does not match format \"%Y-%m-%dT%H:%M:%SZ\"");
        return new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds();
    }

    static object? FromJson(object? v)
    {
        var s = StringArg("parsed as JSON", v);
        List<object?> values;
        try { values = ParseStream(s); }
        catch (ShellException) { throw new JqError($"{s} (while parsing '{s}')"); }
        if (values.Count != 1) throw new JqError($"Unexpected extra JSON values (while parsing '{s}')");
        return values[0];
    }

    static double Round(double x) => x >= 0 ? Math.Floor(x + 0.5) : -Math.Floor(-x + 0.5);

    static Func<object?, IEnumerable<object?>> MathFn(Func<double, double> fn) => v => [fn(NumberArg(v))];

    static readonly Dictionary<string, Func<object?, IEnumerable<object?>>> Builtins0 = new()
    {
        ["length"] = v => [Length(v)],
        ["utf8bytelength"] = v => [(double)Encoding.UTF8.GetByteCount(StringArg("measured in UTF-8 bytes", v))],
        ["not"] = v => [!Truthy(v)],
        ["keys"] = v => [Keys(v)],
        ["keys_unsorted"] = v => [Keys(v, sort: false)],
        ["values"] = v => v != null ? [v] : [],
        ["to_entries"] = v => [ToEntries(v)],
        ["from_entries"] = v => [FromEntries(v)],
        ["add"] = v => [Add(v)],
        ["any"] = v => [Iterate(v).Any(Truthy)],
        ["all"] = v => [Iterate(v).All(Truthy)],
        ["flatten"] = v => [Flatten(v)],
        ["floor"] = MathFn(Math.Floor),
        ["ceil"] = MathFn(Math.Ceiling),
        ["round"] = MathFn(Round),
        ["sqrt"] = MathFn(Math.Sqrt),
        ["fabs"] = MathFn(Math.Abs),
        ["abs"] = MathFn(Math.Abs),
        ["tostring"] = v => [ToStr(v)],
        ["tonumber"] = v => [ToNumber(v)],
        ["tojson"] = v => [Dump(v)],
        ["fromjson"] = v => [FromJson(v)],
        ["type"] = v => [TypeName(v)],
        ["infinite"] = _ => [double.PositiveInfinity],
        ["nan"] = _ => [double.NaN],
        ["isinfinite"] = v => [double.IsInfinity(NumberArg(v))],
        ["isnan"] = v => [double.IsNaN(NumberArg(v))],
        ["isnormal"] = v => [double.IsFinite(NumberArg(v)) && (double)v! != 0],
        ["sort"] = v => [Sorted(v)],
        ["unique"] = v => [Unique(Sorted(v))],
        ["reverse"] = v => [v switch
        {
            null => new List<object?>(),
            List<object?> l => Enumerable.Reverse(l).ToList(),
            string s => new string(s.Reverse().ToArray()),
            _ => throw new JqError($"Cannot reverse {ErrType(v)}"),
        }],
        ["min"] = v => [Sorted(v) is { Count: > 0 } l ? l[0] : null],
        ["max"] = v => [Sorted(v) is { Count: > 0 } l ? l[^1] : null],
        ["ascii_downcase"] = v => [AsciiCase(StringArg("lowercased", v), false)],
        ["ascii_upcase"] = v => [AsciiCase(StringArg("uppercased", v), true)],
        ["trim"] = v => [StringArg("trimmed", v).Trim(' ', '\t', '\n', '\r', '\f', '\v')],
        ["ltrim"] = v => [StringArg("trimmed", v).TrimStart(' ', '\t', '\n', '\r', '\f', '\v')],
        ["rtrim"] = v => [StringArg("trimmed", v).TrimEnd(' ', '\t', '\n', '\r', '\f', '\v')],
        ["explode"] = v => [StringArg("exploded", v).Select(c => (object?)(double)c).ToList()],
        ["implode"] = v => [string.Concat(Iterate(v).Select(c => (char)NumberArg(c)))],
        ["first"] = v => [Index(v, 0.0)],
        ["last"] = v => [Index(v, -1.0)],
        ["paths"] = v => AllPaths(v).ToList(),
        ["leaf_paths"] = v => AllPaths(v, leafOnly: true).ToList(),
        ["arrays"] = v => (v is List<object?>) ? [v] : [],
        ["objects"] = v => (v is Dictionary<string, object?>) ? [v] : [],
        ["iterables"] = v => (v is List<object?> or Dictionary<string, object?>) ? [v] : [],
        ["booleans"] = v => (v is bool) ? [v] : [],
        ["numbers"] = v => (v is double) ? [v] : [],
        ["strings"] = v => (v is string) ? [v] : [],
        ["nulls"] = v => v == null ? [v] : [],
        ["scalars"] = v => (v is not (List<object?> or Dictionary<string, object?>)) ? [v] : [],
        ["recurse"] = v => Recurse(v).ToList(),
        ["empty"] = _ => [],
        ["error"] = v => throw new JqError(v),
        ["now"] = _ => [new DateTimeOffset(t_now).ToUnixTimeMilliseconds() / 1000.0],
        ["todate"] = v => [ToDate(v)],
        ["todateiso8601"] = v => [ToDate(v)],
        ["fromdate"] = v => [FromDate(v)],
        ["fromdateiso8601"] = v => [FromDate(v)],
        ["input_filename"] = _ => [null],
    };

    // ---------- interpreter ----------

    // nested calls of functions a filter defines; the same in every port, below any port's own stack limit
    // shortcut: real jq recurses far deeper (and tail calls without limit); raise with an explicit stack per port if needed
    const int MaxDefDepth = 100;

    /// <summary>A def as its scope holds it (beside the $variables, keyed name/arity); the scope holds it too, for recursion.</summary>
    sealed record Fn(List<string> Params, Node Body, Dictionary<string, object?> Scope);

    /// <summary>A filter argument: runs where it was written.</summary>
    sealed record Closure(Node Body, Dictionary<string, object?> Scope);

    sealed class Interpreter(IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        int _depth; // def calls in progress

        static Dictionary<string, object?> Define(DefN d, Dictionary<string, object?> vs)
        {
            var scope = new Dictionary<string, object?>(vs);
            scope[d.Key] = new Fn(d.Params, d.Body, scope);
            return scope;
        }

        /// <summary>Calls a def: filter parameters are closures over the caller's scope, $parameters bind each value in turn.
        /// run is Ev, or Paths for a call used as a path expression.</summary>
        IEnumerable<T> Defined<T>(Fn fn, List<Node> args, object? v, Dictionary<string, object?> vs, Func<Node, object?, Dictionary<string, object?>, IEnumerable<T>> run)
        {
            if (_depth >= MaxDefDepth) throw new JqError($"recursion too deep (more than {MaxDefDepth} nested function calls)");
            var call = new Dictionary<string, object?>(fn.Scope);
            var values = new List<(string Name, Node Arg)>();
            for (int i = 0; i < fn.Params.Count; i++)
                if (fn.Params[i].StartsWith('$')) values.Add((fn.Params[i][1..], args[i]));
                else call[$"{fn.Params[i]}/0"] = new Closure(args[i], vs);
            return BindArgs(0, call);

            IEnumerable<T> BindArgs(int i, Dictionary<string, object?> scope)
            {
                if (i == values.Count)
                {
                    // count only while the body runs: a body paused after a yield is not on the stack
                    using var it = run(fn.Body, v, scope).GetEnumerator();
                    while (true)
                    {
                        _depth++;
                        try { if (!it.MoveNext()) yield break; }
                        finally { _depth--; }
                        yield return it.Current;
                    }
                }
                foreach (var x in Ev(values[i].Arg, v, vs))
                {
                    var inner = new Dictionary<string, object?>(scope) { [values[i].Name] = x, [$"{values[i].Name}/0"] = new Closure(new Lit(x), []) };
                    foreach (var y in BindArgs(i + 1, inner)) yield return y;
                }
            }
        }

        static Dictionary<string, object?> With(Dictionary<string, object?> vs, string name, object? value) => new(vs) { [name] = value };

        Dictionary<string, object?> EnvObject() => env.ToDictionary(p => p.Key, p => (object?)p.Value);

        /// <summary>Yields every output of node applied to input v; vs holds the bound $variables.</summary>
        long _steps;

        /// <summary>Stops endless generators (range(1e10), recurse without a base case) and runaway nesting.</summary>
        void Step()
        {
            if (++_steps > 20_000_000) throw new JqError("too many steps (possible infinite loop)");
            if ((_steps & 1023) == 0) ct.ThrowIfCancellationRequested(); // the call's time limit, or `timeout`
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        }

        public IEnumerable<object?> Ev(Node node, object? v, Dictionary<string, object?> vs)
        {
            Step();
            switch (node)
            {
                case Identity: yield return v; break;
                case Lit l: yield return l.Value; break;
                case PipeN p:
                    foreach (var x in Ev(p.L, v, vs))
                        foreach (var y in Ev(p.R, x, vs)) yield return y;
                    break;
                case CommaN c:
                    foreach (var x in Ev(c.L, v, vs)) yield return x;
                    foreach (var x in Ev(c.R, v, vs)) yield return x;
                    break;
                case IndexN ix:
                    foreach (var t in Ev(ix.Target, v, vs))
                        foreach (var k in Ev(ix.Key, v, vs)) yield return Index(t, k);
                    break;
                case SliceN s:
                    foreach (var t in Ev(s.Target, v, vs))
                        foreach (var lo in s.Lo == null ? new object?[] { null } : Ev(s.Lo, v, vs))
                            foreach (var hi in s.Hi == null ? new object?[] { null } : Ev(s.Hi, v, vs)) yield return Slice(t, lo, hi);
                    break;
                case IterateN it:
                    foreach (var t in Ev(it.Target, v, vs))
                        foreach (var x in Iterate(t)) yield return x;
                    break;
                case ArrayN a: yield return a.Body == null ? new List<object?>() : Ev(a.Body, v, vs).ToList(); break;
                case ObjectN o:
                    foreach (var x in ObjectEv(o.Entries, 0, new Dictionary<string, object?>(), v, vs)) yield return x;
                    break;
                case StringN s:
                    foreach (var x in StringEv(s.Parts, 0, "", v, vs, s.Fmt)) yield return x;
                    break;
                case FormatN f: yield return Format(f.Name, v); break;
                case NegN n:
                    foreach (var x in Ev(n.X, v, vs)) yield return x is double d ? -d : throw new JqError($"{ErrType(x)} cannot be negated");
                    break;
                case BinOp b:
                    foreach (var r in Ev(b.R, v, vs))
                        foreach (var l in Ev(b.L, v, vs))
                        {
                            if (b.Op is "+" or "-" or "*" or "/" or "%") { yield return Arith(b.Op, l, r); continue; }
                            int c = Compare(l, r);
                            yield return b.Op switch { "==" => c == 0, "!=" => c != 0, "<" => c < 0, "<=" => c <= 0, ">" => c > 0, _ => c >= 0 };
                        }
                    break;
                case AndN or OrN:
                {
                    bool isOr = node is OrN;
                    var (left, right) = node is AndN an ? (an.L, an.R) : (((OrN)node).L, ((OrN)node).R);
                    foreach (var a in Ev(left, v, vs))
                    {
                        if (Truthy(a) == isOr) { yield return isOr; continue; }
                        foreach (var b2 in Ev(right, v, vs)) yield return Truthy(b2);
                    }
                    break;
                }
                case AltN alt:
                {
                    bool found = false;
                    using (var e = Ev(alt.L, v, vs).GetEnumerator())
                        while (true)
                        {
                            object? cur;
                            try
                            {
                                if (!e.MoveNext()) break;
                                cur = e.Current;
                            }
                            catch (JqError) { break; }
                            if (Truthy(cur)) { found = true; yield return cur; }
                        }
                    if (!found)
                        foreach (var x in Ev(alt.R, v, vs)) yield return x;
                    break;
                }
                case IfN i:
                    foreach (var c in Ev(i.Cond, v, vs))
                    {
                        if (Truthy(c)) { foreach (var x in Ev(i.Then, v, vs)) yield return x; }
                        else if (i.Else == null) yield return v;
                        else foreach (var x in Ev(i.Else, v, vs)) yield return x;
                    }
                    break;
                case TryN t:
                {
                    JqError? caught = null;
                    using (var e = Ev(t.Body, v, vs).GetEnumerator())
                        while (true)
                        {
                            object? cur;
                            try
                            {
                                if (!e.MoveNext()) break;
                                cur = e.Current;
                            }
                            catch (JqError err) { caught = err; break; }
                            yield return cur;
                        }
                    if (caught != null && t.Handler != null)
                        foreach (var x in Ev(t.Handler, caught.Value, vs)) yield return x;
                    break;
                }
                case VarN vn:
                    if (vs.TryGetValue(vn.Name, out var val)) yield return val;
                    else if (vn.Name == "ENV") yield return EnvObject();
                    else throw new JqError($"${vn.Name} is not defined");
                    break;
                case AsN a:
                    foreach (var x in Ev(a.Source, v, vs))
                        foreach (var bound in Bind(a.Pattern, x, vs))
                            foreach (var y in Ev(a.Body, v, bound)) yield return y;
                    break;
                case ReduceN r:
                    foreach (var init in Ev(r.Init, v, vs))
                    {
                        var acc = init;
                        foreach (var x in Ev(r.Source, v, vs))
                            foreach (var bound in Bind(r.Pattern, x, vs))
                            {
                                var results = Ev(r.Update, acc, bound).ToList();
                                acc = results.Count > 0 ? results[^1] : null;
                            }
                        yield return acc;
                    }
                    break;
                case ForeachN f:
                    foreach (var init in Ev(f.Init, v, vs))
                    {
                        var acc = init;
                        foreach (var x in Ev(f.Source, v, vs))
                            foreach (var bound in Bind(f.Pattern, x, vs))
                                foreach (var next in Ev(f.Update, acc, bound))
                                {
                                    acc = next;
                                    if (f.Extract == null) yield return acc;
                                    else foreach (var y in Ev(f.Extract, acc, bound)) yield return y;
                                }
                    }
                    break;
                case AssignN asg:
                    foreach (var x in Assign(asg.Op, asg.L, asg.R, v, vs)) yield return x;
                    break;
                case DefN d:
                    foreach (var x in Ev(d.Rest, v, Define(d, vs))) yield return x;
                    break;
                case CallN dc when vs.TryGetValue($"{dc.Name}/{dc.Args.Count}", out var f):
                    foreach (var x in f is Closure cl ? Ev(cl.Body, v, cl.Scope) : Defined((Fn)f!, dc.Args, v, vs, Ev)) yield return x;
                    break;
                case CallN call:
                    foreach (var x in Call(call.Name, call.Args, v, vs)) yield return x;
                    break;
            }
        }

        IEnumerable<object?> ObjectEv(List<(Node Key, Node Value)> entries, int i, Dictionary<string, object?> acc, object? v, Dictionary<string, object?> vs)
        {
            if (i == entries.Count) { yield return new Dictionary<string, object?>(acc); yield break; }
            foreach (var k in Ev(entries[i].Key, v, vs))
            {
                if (k is not string key) throw new JqError("Object keys must be strings");
                foreach (var val in Ev(entries[i].Value, v, vs))
                    foreach (var x in ObjectEv(entries, i + 1, new Dictionary<string, object?>(acc) { [key] = val }, v, vs)) yield return x;
            }
        }

        IEnumerable<object?> StringEv(List<object> parts, int i, string acc, object? v, Dictionary<string, object?> vs, string? fmt)
        {
            if (i == parts.Count) { yield return acc; yield break; }
            if (parts[i] is string s)
            {
                foreach (var x in StringEv(parts, i + 1, acc + s, v, vs, fmt)) yield return x;
                yield break;
            }
            foreach (var x in Ev((Node)parts[i], v, vs))
                foreach (var y in StringEv(parts, i + 1, acc + (fmt != null ? Format(fmt, x) : ToStr(x)), v, vs, fmt)) yield return y;
        }

        IEnumerable<Dictionary<string, object?>> Bind(Pat pattern, object? value, Dictionary<string, object?> vs)
        {
            switch (pattern)
            {
                case VarPat p: return [With(vs, p.Name, value)];
                case ArrayPat a:
                {
                    if (value != null && value is not List<object?>) throw new JqError($"Cannot index {TypeName(value)} with number");
                    IEnumerable<Dictionary<string, object?>> results = [vs];
                    for (int i = 0; i < a.Items.Count; i++)
                    {
                        var item = Index(value, (double)i);
                        var sub = a.Items[i];
                        results = results.SelectMany(r => Bind(sub, item, r)).ToList();
                    }
                    return results;
                }
                default:
                {
                    IEnumerable<Dictionary<string, object?>> results = [vs];
                    foreach (var (keyNode, sub) in ((ObjectPat)pattern).Entries)
                        results = results.SelectMany(r => Ev(keyNode, value, r).SelectMany(k => Bind(sub, Index(value, k), r))).ToList();
                    return results;
                }
            }
        }

        // -- paths: (path, value) pairs for the left side of =, |=, del(), path() --

        static List<object?> Append(List<object?> p, object? k) => new(p) { k };

        public IEnumerable<(List<object?> Path, object? Value)> Paths(Node node, object? v, Dictionary<string, object?> vs)
        {
            switch (node)
            {
                case Identity: yield return (new List<object?>(), v); break;
                case PipeN p:
                    foreach (var (pp, x) in Paths(p.L, v, vs))
                        foreach (var (qp, y) in Paths(p.R, x, vs)) yield return (pp.Concat(qp).ToList(), y);
                    break;
                case CommaN c:
                    foreach (var r in Paths(c.L, v, vs)) yield return r;
                    foreach (var r in Paths(c.R, v, vs)) yield return r;
                    break;
                case IndexN ix:
                    foreach (var (pp, t) in Paths(ix.Target, v, vs))
                        foreach (var k in Ev(ix.Key, v, vs)) yield return (Append(pp, k), Index(t, k));
                    break;
                case SliceN s:
                    foreach (var (pp, t) in Paths(s.Target, v, vs))
                        foreach (var lo in s.Lo == null ? new object?[] { null } : Ev(s.Lo, v, vs))
                            foreach (var hi in s.Hi == null ? new object?[] { null } : Ev(s.Hi, v, vs))
                                yield return (Append(pp, new Dictionary<string, object?> { ["start"] = lo, ["end"] = hi }), Slice(t, lo, hi));
                    break;
                case IterateN it:
                    foreach (var (pp, t) in Paths(it.Target, v, vs))
                    {
                        if (t is Dictionary<string, object?> o) { foreach (var (k, x) in o.ToList()) yield return (Append(pp, k), x); }
                        else
                        {
                            var items = Iterate(t);
                            for (int i = 0; i < items.Count; i++) yield return (Append(pp, (double)i), items[i]);
                        }
                    }
                    break;
                case IfN i:
                    foreach (var c in Ev(i.Cond, v, vs))
                    {
                        if (Truthy(c)) { foreach (var r in Paths(i.Then, v, vs)) yield return r; }
                        else if (i.Else == null) yield return (new List<object?>(), v);
                        else foreach (var r in Paths(i.Else, v, vs)) yield return r;
                    }
                    break;
                case AltN alt:
                {
                    bool found = false;
                    using (var e = Paths(alt.L, v, vs).GetEnumerator())
                        while (true)
                        {
                            (List<object?>, object?) cur;
                            try
                            {
                                if (!e.MoveNext()) break;
                                cur = e.Current;
                            }
                            catch (JqError) { break; }
                            if (Truthy(cur.Item2)) { found = true; yield return cur; }
                        }
                    if (!found)
                        foreach (var r in Paths(alt.R, v, vs)) yield return r;
                    break;
                }
                case TryN t:
                    using (var e = Paths(t.Body, v, vs).GetEnumerator())
                        while (true)
                        {
                            (List<object?>, object?) cur;
                            try
                            {
                                if (!e.MoveNext()) break;
                                cur = e.Current;
                            }
                            catch (JqError) { break; }
                            yield return cur;
                        }
                    break;
                case AsN a:
                    foreach (var x in Ev(a.Source, v, vs))
                        foreach (var bound in Bind(a.Pattern, x, vs))
                            foreach (var r in Paths(a.Body, v, bound)) yield return r;
                    break;
                case DefN d:
                    foreach (var r in Paths(d.Rest, v, Define(d, vs))) yield return r;
                    break;
                case CallN dc when vs.TryGetValue($"{dc.Name}/{dc.Args.Count}", out var f):
                    foreach (var r in f is Closure cl ? Paths(cl.Body, v, cl.Scope) : Defined((Fn)f!, dc.Args, v, vs, Paths)) yield return r;
                    break;
                case CallN { Name: "empty", Args.Count: 0 }: break;
                case CallN { Name: "select", Args.Count: 1 } sel:
                    foreach (var c in Ev(sel.Args[0], v, vs))
                        if (Truthy(c)) yield return (new List<object?>(), v);
                    break;
                case CallN { Name: "recurse", Args.Count: 0 }:
                    yield return (new List<object?>(), v);
                    foreach (var p in AllPaths(v).ToList()) yield return (p, GetPath(v, p));
                    break;
                case CallN { Name: "first" or "last", Args.Count: 0 } fl:
                {
                    double k = fl.Name == "first" ? 0 : -1;
                    yield return (new List<object?> { k }, Index(v, k));
                    break;
                }
                case CallN { Name: "getpath", Args.Count: 1 } gp:
                    foreach (var p in Ev(gp.Args[0], v, vs)) yield return (new List<object?>((List<object?>)p!), GetPath(v, p));
                    break;
                case CallN { Name: "first" or "last", Args.Count: 1 } fl1:
                {
                    var found = Paths(fl1.Args[0], v, vs).ToList();
                    if (found.Count > 0) yield return fl1.Name == "first" ? found[0] : found[^1];
                    break;
                }
                case CallN { Name: "paths" or "leaf_paths", Args.Count: 0 } ps:
                    foreach (var p in AllPaths(v, ps.Name == "leaf_paths").ToList()) yield return (p, GetPath(v, p));
                    break;
                case CallN other: throw new JqError($"Invalid path expression with result {other.Name}");
                default: throw new JqError("Invalid path expression");
            }
        }

        IEnumerable<object?> Assign(string op, Node lhs, Node rhs, object? v, Dictionary<string, object?> vs)
        {
            if (op == "|=")
            {
                var output = v;
                foreach (var (p, _) in Paths(lhs, v, vs).ToList())
                {
                    var results = Ev(rhs, GetPath(output, p), vs).Take(1).ToList();
                    output = results.Count > 0 ? SetPath(output, p, results[0]) : DelPath(output, p);
                }
                yield return output;
                yield break;
            }
            foreach (var value in Ev(rhs, v, vs))
            {
                var output = v;
                foreach (var (p, _) in Paths(lhs, v, vs).ToList())
                {
                    object? next;
                    if (op == "=") next = value;
                    else if (op == "//=") { var cur = GetPath(output, p); next = Truthy(cur) ? cur : value; }
                    else next = Arith(op[..1], GetPath(output, p), value);
                    output = SetPath(output, p, next);
                }
                yield return output;
            }
        }

        List<List<object?>> Cartesian(List<Node> args, object? v, Dictionary<string, object?> vs)
        {
            var results = new List<List<object?>> { new() };
            foreach (var node in args) results = results.SelectMany(r => Ev(node, v, vs).Select(x => new List<object?>(r) { x })).ToList();
            return results;
        }

        static readonly HashSet<string> RegexFns = ["test", "match", "capture", "scan", "split", "splits", "sub", "gsub"];

        IEnumerable<object?> Call(string name, List<Node> a, object? v, Dictionary<string, object?> vs)
        {
            int n = a.Count;
            if (n == 0 && Builtins0.TryGetValue(name, out var fn)) { foreach (var x in fn(v)) yield return x; yield break; }
            if (n == 0 && name == "env") { yield return EnvObject(); yield break; }
            var key = $"{name}/{n}";
            switch (key)
            {
                case "select/1":
                    foreach (var c in Ev(a[0], v, vs)) if (Truthy(c)) yield return v;
                    yield break;
                case "map/1":
                    yield return Iterate(v).SelectMany(x => Ev(a[0], x, vs)).ToList();
                    yield break;
                case "map_values/1":
                    if (v is Dictionary<string, object?> mo)
                    {
                        var r = new Dictionary<string, object?>();
                        foreach (var (k, x) in mo)
                            if (Ev(a[0], x, vs).Take(1).ToList() is [var first]) r[k] = first;
                        yield return r;
                    }
                    else yield return Iterate(v).SelectMany(x => Ev(a[0], x, vs).Take(1)).ToList();
                    yield break;
                case "sort_by/1" or "group_by/1" or "unique_by/1" or "min_by/1" or "max_by/1":
                {
                    if (v is not List<object?> list) throw new JqError($"Cannot index {TypeName(v)} with number");
                    var keyed = list.Select(x => (Key: (object?)Ev(a[0], x, vs).ToList(), Value: x)).OrderBy(kx => kx.Key, Order).ToList();
                    if (name == "sort_by") yield return keyed.Select(kx => kx.Value).ToList();
                    else if (name is "min_by" or "max_by") yield return keyed.Count == 0 ? null : name == "min_by" ? keyed[0].Value : keyed[^1].Value;
                    else
                    {
                        var groups = new List<(object? Key, List<object?> Items)>();
                        foreach (var (k, x) in keyed)
                            if (groups.Count > 0 && Compare(groups[^1].Key, k) == 0) groups[^1].Items.Add(x);
                            else groups.Add((k, [x]));
                        yield return name == "group_by" ? groups.Select(g => (object?)g.Items).ToList() : groups.Select(g => g.Items[0]).ToList();
                    }
                    yield break;
                }
                case "with_entries/1":
                    yield return FromEntries(ToEntries(v).SelectMany(e => Ev(a[0], e, vs)).ToList());
                    yield break;
                case "has/1":
                    foreach (var k in Ev(a[0], v, vs))
                    {
                        if (v is Dictionary<string, object?> ho && k is string hk) yield return ho.ContainsKey(hk);
                        else if (v is List<object?> hl && k is double hd) yield return hd >= 0 && hd < hl.Count;
                        else throw new JqError($"Cannot check whether {TypeName(v)} has a {TypeName(k)} key");
                    }
                    yield break;
                case "in/1":
                    foreach (var o in Ev(a[0], v, vs))
                        foreach (var x in Call("has", [new Lit(v)], o, vs)) yield return x;
                    yield break;
                case "contains/1" or "inside/1":
                    foreach (var x in Ev(a[0], v, vs)) yield return name == "contains" ? Contains(v, x) : Contains(x, v);
                    yield break;
                case "startswith/1" or "endswith/1":
                    foreach (var x in Ev(a[0], v, vs))
                    {
                        if (v is not string s || x is not string t) throw new JqError($"{name}() requires string inputs");
                        yield return name == "startswith" ? s.StartsWith(t, StringComparison.Ordinal) : s.EndsWith(t, StringComparison.Ordinal);
                    }
                    yield break;
                case "ltrimstr/1" or "rtrimstr/1":
                    foreach (var x in Ev(a[0], v, vs))
                    {
                        if (v is string s && x is string t && t.Length > 0 && (name == "ltrimstr" ? s.StartsWith(t, StringComparison.Ordinal) : s.EndsWith(t, StringComparison.Ordinal)))
                            yield return name == "ltrimstr" ? s[t.Length..] : s[..^t.Length];
                        else yield return v;
                    }
                    yield break;
                case "join/1":
                    foreach (var sep in Ev(a[0], v, vs))
                    {
                        var parts = Iterate(v).Select(x => x switch
                        {
                            List<object?> or Dictionary<string, object?> => throw new JqError($"Cannot join with {TypeName(x)}"),
                            null => "",
                            _ => ToStr(x),
                        }).ToList();
                        if (parts.Count > 0 && sep is not string) throw new JqError($"{ErrType(sep)} and string cannot be added");
                        yield return string.Join(sep as string ?? "", parts);
                    }
                    yield break;
                case "split/1":
                    foreach (var sep in Ev(a[0], v, vs))
                    {
                        if (v is not string s || sep is not string t) throw new JqError("split input and separator must be strings");
                        yield return SplitString(s, t);
                    }
                    yield break;
                case "index/1" or "rindex/1" or "indices/1":
                    foreach (var x in Ev(a[0], v, vs))
                    {
                        var found = Indices(v, x) as List<object?>;
                        if (name == "indices") yield return found;
                        else yield return found is { Count: > 0 } ? (name == "index" ? found[0] : found[^1]) : null;
                    }
                    yield break;
                case "range/1" or "range/2" or "range/3":
                    foreach (var bounds in Cartesian(a, v, vs))
                    {
                        if (bounds.Any(b => b is not double)) throw new JqError("Range bounds must be numeric");
                        double lo = n == 1 ? 0 : (double)bounds[0]!, hi = n == 1 ? (double)bounds[0]! : (double)bounds[1]!, step = n == 3 ? (double)bounds[2]! : 1;
                        for (long count = 0; (step > 0 && lo + count * step < hi) || (step < 0 && lo + count * step > hi); count++) { Step(); yield return lo + count * step; }
                    }
                    yield break;
                case "first/1":
                    foreach (var x in Ev(a[0], v, vs)) { yield return x; break; }
                    yield break;
                case "last/1":
                {
                    object? last = null;
                    bool any = false;
                    foreach (var x in Ev(a[0], v, vs)) { last = x; any = true; }
                    if (any) yield return last;
                    yield break;
                }
                case "nth/1":
                    foreach (var k in Ev(a[0], v, vs)) yield return Index(v, k);
                    yield break;
                case "nth/2":
                    foreach (var k in Ev(a[0], v, vs))
                    {
                        if (k is not double nd || nd < 0) throw new JqError("Out of bounds negative array index");
                        int idx = 0;
                        foreach (var x in Ev(a[1], v, vs)) { if (idx++ == (int)nd) { yield return x; break; } }
                    }
                    yield break;
                case "limit/2":
                    foreach (var k in Ev(a[0], v, vs))
                    {
                        if (k is not double max) throw new JqError("Invalid limit: must be a number");
                        if (max <= 0) continue;
                        int taken = 0;
                        foreach (var x in Ev(a[1], v, vs)) { yield return x; if (++taken >= max) break; }
                    }
                    yield break;
                case "any/1" or "all/1":
                {
                    var vals = Iterate(v).SelectMany(x => Ev(a[0], x, vs)).Select(Truthy).ToList();
                    yield return name == "any" ? vals.Any(b => b) : vals.All(b => b);
                    yield break;
                }
                case "any/2" or "all/2":
                {
                    var vals = Ev(a[0], v, vs).SelectMany(x => Ev(a[1], x, vs)).Select(Truthy).ToList();
                    yield return name == "any" ? vals.Any(b => b) : vals.All(b => b);
                    yield break;
                }
                case "IN/1" or "IN/2":
                    foreach (var x in n == 1 ? new[] { v } : Ev(a[0], v, vs)) yield return Ev(a[^1], v, vs).Any(y => Compare(x, y) == 0);
                    yield break;
                case "isempty/1":
                    yield return !Ev(a[0], v, vs).Any();
                    yield break;
                case "recurse/1" or "recurse/2":
                {
                    IEnumerable<object?> Walk(object? x, int depth)
                    {
                        Shell.CheckStack();
                        if (depth > 10_000) throw new JqError("recurse: too deep");
                        Step();
                        yield return x;
                        foreach (var y in Ev(a[0], x, vs))
                        {
                            if (n == 2 && !Ev(a[1], y, vs).Any(Truthy)) continue;
                            foreach (var z in Walk(y, depth + 1)) yield return z;
                        }
                    }
                    foreach (var x in Walk(v, 0)) yield return x;
                    yield break;
                }
                case "walk/1":
                {
                    object? Walk(object? x)
                    {
                        Shell.CheckStack();
                        if (x is List<object?> wl) x = wl.Select(Walk).ToList();
                        else if (x is Dictionary<string, object?> wo) x = wo.ToDictionary(p => p.Key, p => Walk(p.Value));
                        var r = Ev(a[0], x, vs).Take(1).ToList();
                        return r.Count > 0 ? r[0] : null;
                    }
                    yield return Walk(v);
                    yield break;
                }
                case "until/2" or "while/2" or "repeat/1":
                {
                    var x = v;
                    for (int count = 1; ; count++)
                    {
                        if (count > 100_000) throw new JqError($"{name}: too many iterations");
                        if (name != "repeat")
                        {
                            bool cond = Ev(a[0], x, vs).Any(Truthy);
                            if (name == "until" && cond) { yield return x; break; }
                            if (name == "while")
                            {
                                if (!cond) break;
                                yield return x;
                            }
                        }
                        else yield return x;
                        var next = Ev(a[^1], x, vs).ToList();
                        if (next.Count == 0) break;
                        x = next[^1];
                    }
                    yield break;
                }
                case "flatten/1":
                    foreach (var d in Ev(a[0], v, vs)) yield return Flatten(v, NumberArg(d));
                    yield break;
                case "path/1":
                    foreach (var (p, _) in Paths(a[0], v, vs)) yield return p;
                    yield break;
                case "paths/1":
                    foreach (var p in AllPaths(v).ToList())
                        if (Ev(a[0], GetPath(v, p), vs).Any(Truthy)) yield return p;
                    yield break;
                case "del/1":
                    yield return DelPaths(v, Paths(a[0], v, vs).Select(p => (object?)p.Path).ToList());
                    yield break;
                case "delpaths/1":
                    foreach (var ps in Ev(a[0], v, vs)) yield return DelPaths(v, Iterate(ps));
                    yield break;
                case "getpath/1":
                    foreach (var p in Ev(a[0], v, vs)) yield return GetPath(v, p);
                    yield break;
                case "setpath/2":
                    foreach (var p in Ev(a[0], v, vs))
                        foreach (var x in Ev(a[1], v, vs)) yield return SetPath(v, p, x);
                    yield break;
                case "error/1":
                    foreach (var msg in Ev(a[0], v, vs)) throw new JqError(msg);
                    yield break;
                case "debug/0" or "stderr/0":
                    yield return v;
                    yield break;
                case "input/0" or "inputs/0" or "input_line_number/0" or "tostream/0" or "fromstream/1" or "min_by/0" or "max_by/0":
                    throw new JqError($"{key} is not supported");
            }
            if (RegexFns.Contains(name) && (name is "sub" or "gsub" ? n is 2 or 3 : n is 1 or 2))
            {
                foreach (var x in RegexCall(name, a, v, vs)) yield return x;
                yield break;
            }
            throw new CompileError($"{key} is not defined");
        }

        IEnumerable<object?> RegexCall(string name, List<Node> args, object? v, Dictionary<string, object?> vs)
        {
            if (v is not string s) throw new JqError($"{ErrType(v)} cannot be matched, as it is not a string");
            if (name is "sub" or "gsub")
            {
                foreach (var pattern in Ev(args[0], v, vs))
                    foreach (var flags in args.Count < 3 ? new object?[] { null } : Ev(args[2], v, vs))
                    {
                        var (re, global) = MakeRegex(pattern, flags);
                        foreach (var x in Substitute(re, args[1], s, vs, global || name == "gsub")) yield return x;
                    }
                yield break;
            }
            foreach (var combo in Cartesian(args, v, vs))
            {
                var (re, global) = MakeRegex(combo[0], combo.Count > 1 ? combo[1] : null);
                switch (name)
                {
                    case "test": yield return re.IsMatch(s); break;
                    case "match":
                        foreach (var m in Matches(re, s, global)) yield return MatchObject(re, m);
                        break;
                    case "capture":
                        foreach (var m in Matches(re, s, global)) yield return NamedGroups(re).ToDictionary(g => g, g => m.Groups[g].Success ? (object?)m.Groups[g].Value : null);
                        break;
                    case "scan":
                        foreach (Match m in re.Matches(s))
                            yield return re.GetGroupNumbers().Length == 1 ? m.Value : re.GetGroupNumbers().Where(g => g > 0).Select(g => m.Groups[g].Success ? (object?)m.Groups[g].Value : null).ToList();
                        break;
                    default:
                        var pieces = new List<object?>();
                        int pos = 0;
                        foreach (Match m in re.Matches(s)) { pieces.Add(s[pos..m.Index]); pos = m.Index + m.Length; }
                        pieces.Add(s[pos..]);
                        if (name == "split") yield return pieces;
                        else foreach (var p in pieces) yield return p;
                        break;
                }
            }
        }

        IEnumerable<object?> Substitute(Regex re, Node replNode, string s, Dictionary<string, object?> vs, bool global)
        {
            var results = new List<string> { "" };
            int pos = 0;
            foreach (var m in Matches(re, s, global))
            {
                var captures = NamedGroups(re).ToDictionary(g => g, g => m.Groups[g].Success ? (object?)m.Groups[g].Value : null);
                var reps = Ev(replNode, captures, vs).ToList();
                foreach (var r in reps) if (r is not string) throw new JqError($"{ErrType(r)} cannot be added to a string");
                var prefix = s[pos..m.Index];
                results = results.SelectMany(acc => reps.Select(r => acc + prefix + (string)r!)).ToList();
                pos = m.Index + m.Length;
            }
            foreach (var acc in results) yield return acc + s[pos..];
        }
    }

    static Node CompileFilter(string src)
    {
        try { return new Parser(src).Parse(); }
        catch (CompileError e) { throw new ShellException($"error: {e.Message}\njq: 1 compile error", 3); }
        catch (InsufficientExecutionStackException) { throw new ShellException("error: filter is nested too deeply\njq: 1 compile error", 3); }
    }

    static readonly Dictionary<string, char> LongFlags = new()
    {
        ["raw-output"] = 'r', ["compact-output"] = 'c', ["null-input"] = 'n', ["slurp"] = 's', ["sort-keys"] = 'S', ["join-output"] = 'j',
        ["raw-input"] = 'R', ["exit-status"] = 'e', ["ascii-output"] = 'a', ["monochrome-output"] = 'M', ["color-output"] = 'C', ["tab"] = 't',
    };

    static Task<int> Run(CommandContext ctx)
    {
        var args = ctx.Args;
        var flags = new HashSet<char>();
        var named = new Dictionary<string, object?>();
        var extra = new List<object?>();
        string? filter = null;
        var files = new List<string>();
        var indent = "  ";
        string? restMode = null; // --args / --jsonargs: remaining positionals are $ARGS.positional
        for (int i = 0; i < args.Count;)
        {
            var arg = args[i++];
            if (arg is "--arg" or "--argjson" or "--slurpfile" or "--rawfile")
            {
                if (i + 1 >= args.Count) throw new ShellException($"{arg} takes two parameters (e.g. {arg} varname value)", 2);
                string name = args[i], value = args[i + 1];
                i += 2;
                if (arg == "--arg") named[name] = value;
                else if (arg == "--argjson")
                {
                    List<object?> values;
                    try { values = ParseStream(value); } catch (ShellException) { values = []; }
                    if (values.Count != 1) throw new ShellException("Invalid JSON text passed to --argjson", 2);
                    named[name] = values[0];
                }
                else
                {
                    var text = ctx.Fs.ReadAllText(ctx.ResolvePath(value));
                    named[name] = arg == "--rawfile" ? text : ParseStream(text);
                }
                continue;
            }
            if (arg == "--indent")
            {
                if (i >= args.Count || !args[i].All(char.IsAsciiDigit) || args[i].Length == 0) throw new ShellException("--indent takes one parameter", 2);
                indent = new string(' ', Math.Min(int.Parse(args[i++], Inv), 7));
                continue;
            }
            if (arg is "--args" or "--jsonargs") { restMode = arg; continue; }
            if (arg.StartsWith("--") && arg.Length > 2)
            {
                if (!LongFlags.TryGetValue(arg[2..], out var f)) throw new ShellException($"Unknown option: {arg}", 2);
                flags.Add(f);
                continue;
            }
            if (arg.StartsWith('-') && arg.Length > 1)
            {
                foreach (var c in arg[1..])
                {
                    if (!"rcnsSjReaMC".Contains(c)) throw new ShellException($"Unknown option: {arg}", 2);
                    flags.Add(c);
                }
                continue;
            }
            if (filter == null) filter = arg;
            else if (restMode == "--args") extra.Add(arg);
            else if (restMode == "--jsonargs")
            {
                var values = ParseStream(arg);
                if (values.Count != 1) throw new ShellException("Invalid JSON text passed to --jsonargs", 2);
                extra.Add(values[0]);
            }
            else files.Add(arg);
        }
        if (filter == null) throw new ShellException(CommandSpec.Message("jq", "missingFilter"), 2);
        var node = CompileFilter(filter);
        int code = 0;

        List<object?> docs;
        if (flags.Contains('n')) docs = [null];
        else
        {
            var texts = Builtins.Inputs(ctx, files, () => code = 2).Select(t => t.Text).ToList();
            if (flags.Contains('R'))
            {
                var raw = string.Concat(texts);
                if (flags.Contains('s')) docs = [raw];
                else
                {
                    var lines = raw.Length == 0 ? [] : raw.Split('\n').ToList();
                    if (raw.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
                    docs = lines.Select(l => (object?)l).ToList();
                }
            }
            else
            {
                docs = texts.SelectMany(ParseStream).ToList();
                if (flags.Contains('s')) docs = [docs];
            }
        }
        var variables = new Dictionary<string, object?>(named)
        {
            ["ARGS"] = new Dictionary<string, object?> { ["positional"] = extra, ["named"] = new Dictionary<string, object?>(named) },
            ["__loc__"] = new Dictionary<string, object?> { ["file"] = "<stdin>", ["line"] = 1.0 },
        };
        var unit = flags.Contains('c') ? "" : flags.Contains('t') ? "\t" : indent;
        var interp = new Interpreter(ctx.Shell.Environment, ctx.CancellationToken);
        t_maxString = ctx.Shell.Options.MaxStringBytes;
        t_now = ctx.Shell.Now;
        object? last = null;
        bool any = false;
        var output = new List<(bool Err, string Text)>();
        foreach (var doc in docs)
        {
            try
            {
                foreach (var result in interp.Ev(node, doc, variables))
                {
                    last = result;
                    any = true;
                    var text = (flags.Contains('r') || flags.Contains('j')) && result is string s ? s : Dump(result, unit, flags.Contains('S'), flags.Contains('a'));
                    output.Add((false, flags.Contains('j') ? text : text + "\n"));
                }
            }
            catch (JqError e)
            {
                var msg = e.Value as string ?? Dump(e.Value) + " (not a string)";
                output.Add((true, $"jq: error (at <unknown>): {msg}\n"));
                code = 5;
            }
            catch (CompileError e) { throw new ShellException($"error: {e.Message}\njq: 1 compile error", 3); }
            catch (InsufficientExecutionStackException)
            {
                output.Add((true, "jq: error: recursion too deep\n"));
                code = 5;
            }
        }
        foreach (var (err, text) in output) (err ? ctx.Stderr : ctx.Stdout).Write(text);
        if (code == 0 && flags.Contains('e')) return Task.FromResult(!any ? 4 : Truthy(last) ? 0 : 1);
        return Task.FromResult(code);
    }
}
