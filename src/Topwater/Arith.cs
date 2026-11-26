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
        if (depth > 20) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.RecursionExceeded, ("expression", expr)));
        if (expr.Contains('$')) expr = Dollar.Replace(expr, m => sh.GetVariable(m.Groups[1].Value));
        var tokens = Tokenize(expr);
        if (tokens.Count == 0) return 0;
        var a = new Arith(sh, expr, tokens) { _depth = depth };
        var v = a.Comma(true);
        if (a._p < tokens.Count) throw a.Syntax(tokens[a._p]);
        return v;
    }

    int _depth;

    static readonly Regex Dollar = new(@"\$\{?([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[#?$])\}?");

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
                // name[subscript] is one operand, so an element can be read, assigned and incremented anywhere
                if (j < s.Length && s[j] == '[' && char.IsLetter(s[i]) || j < s.Length && s[j] == '[' && s[i] == '_')
                {
                    int depth = 0;
                    for (; j < s.Length; j++)
                        if (s[j] == '[') depth++;
                        else if (s[j] == ']' && --depth == 0) { j++; break; }
                    if (depth > 0) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.ArithmeticBadSubscript, ("expression", s)));
                }
                tokens.Add(s[i..j]);
                i = j;
                continue;
            }
            var op = Ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0)
                     ?? throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.InvalidArithmeticOperator, ("expression", s), ("token", s[i..])));
            // `5--3` (after $a-$b is substituted) is two minus signs; `--` is one operator only next to a name
            bool nextIsName = i + 2 < s.Length && (char.IsLetter(s[i + 2]) || s[i + 2] == '_');
            if (op is "++" or "--" && !nextIsName && !(tokens.Count > 0 && IsIdent(tokens[^1]))) op = op[..1];
            tokens.Add(op);
            i += op.Length;
        }
        return tokens;
    }

    ShellException Syntax(string token) => new(CommandSpec.Format(CommandSpec.Diagnostics.ArithmeticSyntax, ("expression", _src.Trim()), ("token", token)));
    string? Peek => _p < _t.Count ? _t[_p] : null;
    bool Accept(string s) { if (Peek != s) return false; _p++; return true; }
    static bool IsIdent(string s) => char.IsLetter(s[0]) || s[0] == '_';

    /// <summary>A variable or name[sub] element as a number: empty or unset is 0, other text is evaluated as an expression.</summary>
    long Var(string name)
    {
        int open = name.IndexOf('[');
        var v = (open < 0 ? _sh.GetVariable(name) : _sh.GetElement(name[..open], name[(open + 1)..^1]) ?? "").Trim();
        if (v.Length == 0) return 0;
        // a value like 010 or 0x1f is a literal in its base, as if it were written in the expression
        return v.TrimStart('-', '+') is not ['0', _, ..] && long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : Eval(_sh, v, _depth + 1);
    }

    void Set(string name, long v) => _sh.SetVar(name, v.ToString(CultureInfo.InvariantCulture));

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
        if (level == Levels.Length) return Power(ev);
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
        if (op is "/" or "%" && r == 0) return ev ? throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.DivisionByZero, ("expression", _src.Trim()))) : 0;
        return op switch
        {
            "+" => l + r, "-" => l - r, "*" => l * r, "/" => r == -1 ? -l : l / r, "%" => r == -1 ? 0 : l % r, "**" => Pow(l, r),
            "<<" => l << (int)r, ">>" => l >> (int)r, "&" => l & r, "|" => l | r, "^" => l ^ r,
            "<" => l < r ? 1 : 0, "<=" => l <= r ? 1 : 0, ">" => l > r ? 1 : 0, ">=" => l >= r ? 1 : 0,
            "==" => l == r ? 1 : 0, "!=" => l != r ? 1 : 0,
            _ => throw Syntax(op),
        };
    }

    /// <summary>Exponentiation by squaring, wrapping around like bash's 64-bit arithmetic.</summary>
    long Pow(long b, long e)
    {
        if (e < 0) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.NegativeExponent, ("expression", _src.Trim()), ("token", e.ToString())));
        long r = 1;
        for (; e > 0; e >>= 1, b *= b) if ((e & 1) != 0) r *= b;
        return r;
    }

    /// <summary>A literal in the given base; values past 64 bits wrap around like bash.</summary>
    static long ParseLiteral(string digits, int radix, string tok)
    {
        if (digits.Length == 0 || radix < 2 || radix > 64) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.InvalidBase, ("token", tok)));
        ulong v = 0;
        foreach (var c in digits)
        {
            int d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'z' => c - 'a' + 10,
                >= 'A' and <= 'Z' => radix <= 36 ? c - 'A' + 10 : c - 'A' + 36,
                '@' => 62,
                '_' => 63,
                _ => 99,
            };
            if (d >= radix) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.ValueTooGreat, ("token", tok)));
            v = unchecked(v * (ulong)radix + (ulong)d);
        }
        return unchecked((long)v);
    }

    long Unary(bool ev)
    {
        // every nesting level passes through here; a stack overflow would kill the host process
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new ShellException(CommandSpec.Diagnostics.ExpressionNested);
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
        return Postfix(ev);
    }

    /// <summary>`**` binds looser than unary minus in bash: -2**2 is 4.</summary>
    long Power(bool ev)
    {
        var b = Unary(ev);
        return Accept("**") ? Pow(b, Power(ev)) : b; // right-associative
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
                if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return ParseLiteral(tok.Length > 2 ? tok[2..] : "0", 16, tok);
                if (tok.Contains('#')) { var parts = tok.Split('#', 2); return ParseLiteral(parts[1], int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var radix) ? radix : 0, tok); }
                if (tok.Length > 1 && tok[0] == '0') return ParseLiteral(tok, 8, tok);
                return ParseLiteral(tok, 10, tok);
            }
            catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
            {
                throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.ValueTooGreat, ("token", tok)));
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
