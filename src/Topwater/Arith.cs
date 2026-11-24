using System.Globalization;
using System.Text.RegularExpressions;

namespace Topwater;

/// <summary>Bash integer arithmetic: $(( )), (( )), let, for (( ; ; )).</summary>
internal sealed class Arith
{
    static readonly string[] Ops =
    [
        "**=", "<<=", ">>=", "&&", "||", "==", "!=", "<=", ">=", "<<", ">>", "**", "++", "--", "+=", "-=", "*=", "/=", "%=",
        "&=", "|=", "^=", "+", "-", "*", "/", "%", "<", ">", "=", "!", "~", "&", "|", "^", "?", ":", "(", ")", ",",
    ];
    static readonly HashSet<string> AssignOps = ["=", "+=", "-=", "*=", "/=", "%=", "<<=", ">>=", "&=", "|=", "^=", "**="];
    static readonly string[][] Levels =
    [
        ["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<", "<=", ">", ">="], ["<<", ">>"], ["+", "-"], ["*", "/", "%"],
    ];

    readonly Shell _sh;
    readonly string _src;
    readonly List<string> _t;
    int _p;

    Arith(Shell sh, string src, List<string> tokens) { _sh = sh; _src = src; _t = tokens; }

    public static long Eval(Shell sh, string expr, int depth = 0)
    {
        if (depth > 20) throw new ShellException($"{expr}: expression recursion level exceeded");
        expr = Regex.Replace(expr, @"\$\{?([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[#?$])\}?", m => sh.GetVariable(m.Groups[1].Value));
        var tokens = Tokenize(expr);
        if (tokens.Count == 0) return 0;
        var a = new Arith(sh, expr, tokens) { _depth = depth };
        var v = a.Comma(true);
        if (a._p < tokens.Count) throw a.Syntax(tokens[a._p]);
        return v;
    }

    int _depth;

    static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        for (int i = 0; i < s.Length;)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (char.IsLetterOrDigit(s[i]) || s[i] == '_')
            {
                int j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '#')) j++;
                tokens.Add(s[i..j]);
                i = j;
                continue;
            }
            var op = Ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0)
                     ?? throw new ShellException($"{s}: syntax error: invalid arithmetic operator (error token is \"{s[i..]}\")");
            tokens.Add(op);
            i += op.Length;
        }
        return tokens;
    }

    ShellException Syntax(string token) => new($"{_src.Trim()}: syntax error in expression (error token is \"{token}\")");
    string? Peek => _p < _t.Count ? _t[_p] : null;
    bool Accept(string s) { if (Peek != s) return false; _p++; return true; }
    static bool IsIdent(string s) => char.IsLetter(s[0]) || s[0] == '_';

    long Var(string name)
    {
        var v = _sh.GetVariable(name).Trim();
        if (v.Length == 0) return 0;
        return long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : Eval(_sh, v, _depth + 1);
    }

    void Set(string name, long v) => _sh.Environment[name] = v.ToString(CultureInfo.InvariantCulture);

    long Comma(bool ev)
    {
        var v = Assign(ev);
        while (Accept(",")) v = Assign(ev);
        return v;
    }

    long Assign(bool ev)
    {
        if (_p + 1 < _t.Count && IsIdent(_t[_p]) && AssignOps.Contains(_t[_p + 1]))
        {
            var name = _t[_p];
            var op = _t[_p + 1];
            _p += 2;
            var r = Assign(ev);
            if (!ev) return r;
            var v = op == "=" ? r : Apply(op[..^1], Var(name), r, true);
            Set(name, v);
            return v;
        }
        return Ternary(ev);
    }

    long Ternary(bool ev)
    {
        var c = Binary(0, ev);
        if (!Accept("?")) return c;
        var a = Assign(ev && c != 0);
        if (!Accept(":")) throw Syntax(Peek ?? "end of expression");
        var b = Assign(ev && c == 0);
        return c != 0 ? a : b;
    }

    long Binary(int level, bool ev)
    {
        if (level == Levels.Length) return Unary(ev);
        var l = Binary(level + 1, ev);
        while (Peek is { } op && Levels[level].Contains(op))
        {
            _p++;
            if (op == "&&") { var r = Binary(level + 1, ev && l != 0); l = l != 0 && r != 0 ? 1 : 0; }
            else if (op == "||") { var r = Binary(level + 1, ev && l == 0); l = l != 0 || r != 0 ? 1 : 0; }
            else l = Apply(op, l, Binary(level + 1, ev), ev);
        }
        return l;
    }

    long Apply(string op, long l, long r, bool ev)
    {
        if (op is "/" or "%" && r == 0) return ev ? throw new ShellException($"{_src.Trim()}: division by 0") : 0;
        return op switch
        {
            "+" => l + r, "-" => l - r, "*" => l * r, "/" => l / r, "%" => l % r, "**" => Pow(l, r),
            "<<" => l << (int)r, ">>" => l >> (int)r, "&" => l & r, "|" => l | r, "^" => l ^ r,
            "<" => l < r ? 1 : 0, "<=" => l <= r ? 1 : 0, ">" => l > r ? 1 : 0, ">=" => l >= r ? 1 : 0,
            "==" => l == r ? 1 : 0, "!=" => l != r ? 1 : 0,
            _ => throw Syntax(op),
        };
    }

    static long Pow(long b, long e)
    {
        long r = 1;
        for (; e > 0; e--) r *= b;
        return r;
    }

    long Unary(bool ev)
    {
        if (Accept("+")) return Unary(ev);
        if (Accept("-")) return -Unary(ev);
        if (Accept("!")) return Unary(ev) == 0 ? 1 : 0;
        if (Accept("~")) return ~Unary(ev);
        if (Peek is "++" or "--")
        {
            var op = _t[_p++];
            var name = Peek is { } n && IsIdent(n) ? n : throw Syntax(Peek ?? op);
            _p++;
            if (!ev) return 0;
            var v = Var(name) + (op == "++" ? 1 : -1);
            Set(name, v);
            return v;
        }
        var b = Postfix(ev);
        if (Accept("**")) return Pow(b, Unary(ev)); // right-associative
        return b;
    }

    long Postfix(bool ev)
    {
        if (Accept("("))
        {
            var v = Comma(ev);
            if (!Accept(")")) throw Syntax(Peek ?? "end of expression");
            return v;
        }
        var tok = Peek ?? throw Syntax("end of expression (operand expected)");
        _p++;
        if (char.IsDigit(tok[0]))
        {
            try
            {
                if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToInt64(tok[2..], 16);
                if (tok.Contains('#')) { var parts = tok.Split('#'); return Convert.ToInt64(parts[1], int.Parse(parts[0])); }
                if (tok.Length > 1 && tok[0] == '0') return Convert.ToInt64(tok, 8);
                return long.Parse(tok, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
            {
                throw new ShellException($"{tok}: value too great for base (error token is \"{tok}\")");
            }
        }
        if (!IsIdent(tok)) throw Syntax(tok);
        if (Peek is "++" or "--")
        {
            var op = _t[_p++];
            var old = Var(tok);
            if (ev) Set(tok, old + (op == "++" ? 1 : -1));
            return old;
        }
        return Var(tok);
    }
}
