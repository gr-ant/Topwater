using System.Numerics;
using System.Text;

namespace Topwater;

/// <summary>
/// bc: the POSIX arbitrary-precision calculator language. Written clean-room from the POSIX spec, the bc(1) man
/// page and the observed behaviour of GNU bc 1.07.1 on this machine.
///
/// Design (deliberately structurally independent of GNU bc): a number is a <see cref="BigInteger"/> mantissa with
/// an int scale (the count of fractional digits), so its value is Mantissa / 10^Scale. Programs are tokenised,
/// parsed into an AST and the AST is evaluated directly. There is no digit-array arithmetic and no bytecode VM.
///
/// Matches GNU bc digit-for-digit on + - * / % ^ and sqrt, bc's scale rules, ibase/obase (including obase &gt; 16
/// space-separated digit groups and ibase up to 16), truncation (never rounding) and the 70-column line wrapping
/// with a trailing backslash. The -l math library (s c a l e j, default scale 20) is computed here from Taylor
/// series and argument-reduction identities, NOT ported from GNU's libmath.b; it is accurate to the requested
/// scale and may differ from GNU in the last digit or two.
///
/// Limits (bc input can run for minutes or build enormous numbers): one bc invocation is capped at 5 s (a
/// CancellationTokenSource linked to ctx.CancellationToken, checked in the eval loop) and any single number is
/// capped at 100000 digits; hitting either stops with an error and a non-zero exit.
///
/// shortcut: a parse error aborts the whole program (GNU recovers statement-by-statement and runs the rest);
/// read() is unsupported (runtime error). Upgrade either if agents need it.
/// </summary>
internal static class Bc
{
    const int MaxDigits = 100000;
    static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(5);

    const string Usage = """
        Usage: bc [options] [file ...]
        An arbitrary precision calculator language.

          -l            load the math library and set the default scale to 20
          -q, -s, -w    accepted and ignored (quiet / POSIX / warnings)
              --version  print the version and exit
              --help     print this message

        Reads a bc program from the files, or from standard input when none are given.
        Limits: one invocation is stopped after 5 s, and any single number is capped at 100000 digits.

        """;

    public static void Register(Shell shell) => shell.Register("bc", Run, "An arbitrary precision calculator language", Usage);

    // ---- errors and limits -------------------------------------------------

    /// <summary>A bc runtime error (divide by zero, sqrt of a negative, ...): reported, then bc carries on.</summary>
    sealed class BcError(string message) : Exception(message);
    /// <summary>A safety limit was hit (time or digit cap): the whole invocation stops with a non-zero exit.</summary>
    sealed class BcLimit(string message) : Exception(message);
    sealed class BcParse(string message) : Exception(message);
    sealed class Halt : Exception;
    sealed class BreakEx : Exception;
    sealed class ContinueEx : Exception;
    sealed class ReturnEx(Num value) : Exception { public Num Value { get; } = value; }

    // ---- numbers: value = M / 10^S, S >= 0 ---------------------------------

    readonly struct Num(BigInteger m, int s)
    {
        public readonly BigInteger M = m;
        public readonly int S = s;
        public bool IsZero => M.IsZero;
        public static readonly Num Zero = new(BigInteger.Zero, 0);
        public static readonly Num One = new(BigInteger.One, 0);
        public static Num Int(long v) => new(v, 0);
    }

    static readonly BigInteger Ten = 10;

    static BigInteger Pow10(long n)
    {
        if (n < 0) throw new BcError("internal: negative power of ten");
        if (n > 4L * MaxDigits + 1000) throw new BcLimit("bc: number too large");
        return BigInteger.Pow(Ten, (int)n);
    }

    static int Digits(BigInteger m) => m.IsZero ? 1 : (int)Math.Floor(BigInteger.Log10(BigInteger.Abs(m))) + 1;

    static Num Guard(Num x) // enforce the per-number digit cap
    {
        if (!x.M.IsZero && BigInteger.Log10(BigInteger.Abs(x.M)) >= MaxDigits) throw new BcLimit("bc: number too large");
        return x;
    }

    /// <summary>Re-express x at a new scale, truncating extra fractional digits toward zero.</summary>
    static Num Rescale(Num x, int newScale) =>
        newScale >= x.S ? new Num(x.M * Pow10(newScale - x.S), newScale)
                        : new Num(x.M / Pow10(x.S - newScale), newScale);

    static Num Add(Num a, Num b)
    {
        int s = Math.Max(a.S, b.S);
        return Guard(new Num(a.M * Pow10(s - a.S) + b.M * Pow10(s - b.S), s));
    }

    static Num Sub(Num a, Num b)
    {
        int s = Math.Max(a.S, b.S);
        return Guard(new Num(a.M * Pow10(s - a.S) - b.M * Pow10(s - b.S), s));
    }

    static Num MulExact(Num a, Num b) => new(a.M * b.M, a.S + b.S);

    static Num Mul(Num a, Num b, int scale)
    {
        int nat = a.S + b.S;
        int target = Math.Min(nat, Math.Max(scale, Math.Max(a.S, b.S)));
        return Guard(Rescale(new Num(a.M * b.M, nat), target));
    }

    static Num Div(Num a, Num b, int scale)
    {
        if (b.IsZero) throw new BcError("bc: divide by zero");
        BigInteger num = a.M * Pow10((long)b.S + scale);
        BigInteger den = b.M * Pow10(a.S);
        return Guard(new Num(num / den, scale)); // BigInteger division truncates toward zero
    }

    static Num Mod(Num a, Num b, int scale)
    {
        if (b.IsZero) throw new BcError("bc: modulo by zero");
        return Guard(Sub(a, MulExact(Div(a, b, scale), b)));
    }

    static Num Pow(Num a, Num expo, int scale)
    {
        BigInteger e = expo.M / Pow10(expo.S); // truncate the exponent toward zero
        if (e.IsZero) return Num.One;
        bool neg = e.Sign < 0;
        e = BigInteger.Abs(e);
        if (e > int.MaxValue) throw new BcLimit("bc: number too large");
        int ei = (int)e;
        if (!a.M.IsZero && (double)ei * BigInteger.Log10(BigInteger.Abs(a.M)) > MaxDigits + 1) throw new BcLimit("bc: number too large");
        var p = new Num(BigInteger.Pow(a.M, ei), a.S * ei); // a^|e|, exact
        if (neg) return Div(Num.One, p, scale);
        int target = Math.Min(p.S, Math.Max(scale, a.S));
        return Guard(Rescale(p, target));
    }

    static int Compare(Num a, Num b)
    {
        int s = Math.Max(a.S, b.S);
        return (a.M * Pow10(s - a.S)).CompareTo(b.M * Pow10(s - b.S));
    }

    static BigInteger ISqrt(BigInteger n)
    {
        if (n.Sign <= 0) return BigInteger.Zero;
        BigInteger x = BigInteger.One << (int)((n.GetBitLength() + 1) / 2);
        while (true)
        {
            BigInteger y = (x + n / x) >> 1;
            if (y >= x) break;
            x = y;
        }
        return x;
    }

    static Num Sqrt(Num a, int scale)
    {
        if (a.M.Sign < 0) throw new BcError("bc: square root of a negative number");
        if (a.M.IsZero) return new Num(BigInteger.Zero, Math.Max(scale, a.S));
        int rs = Math.Max(scale, a.S);
        if ((long)rs + Digits(a.M) / 2 + 1 > MaxDigits) throw new BcLimit("bc: number too large");
        return Guard(new Num(ISqrt(a.M * Pow10(2L * rs - a.S)), rs));
    }

    static Num Length(Num x)
    {
        BigInteger intPart = BigInteger.Abs(x.M) / Pow10(x.S);
        int len = (intPart.IsZero ? 0 : Digits(intPart)) + x.S;
        return Num.Int(len == 0 ? 1 : len);
    }

    // ---- number literal conversion (uses the current ibase at eval time) ---

    static int DigitVal(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'A' and <= 'Z' ? c - 'A' + 10 : 36;

    static Num Convert(string raw, int ibase)
    {
        int dot = raw.IndexOf('.');
        string ip = dot < 0 ? raw : raw[..dot];
        string fp = dot < 0 ? "" : raw[(dot + 1)..];
        int totalDigits = ip.Length + fp.Length;
        BigInteger IntOf(string s)
        {
            BigInteger v = 0;
            foreach (char c in s)
            {
                int d = DigitVal(c);
                // A single-digit number keeps its full face value; in a multi-digit number a digit >= ibase clamps
                // to ibase-1 (POSIX). shortcut: GNU is inconsistent for out-of-range letter digits in a fraction of
                // a tiny base (e.g. ibase=2; A.8) and we don't reproduce that corner; normal digits in bases 2..16 match.
                if (totalDigits > 1 && d > ibase - 1) d = ibase - 1;
                v = v * ibase + d;
            }
            return v;
        }
        BigInteger intVal = IntOf(ip);
        int k = fp.Length;
        BigInteger mant = intVal * Pow10(k);
        if (k > 0) mant += IntOf(fp) * Pow10(k) / BigInteger.Pow(ibase, k); // truncate the fraction to k decimals
        return new Num(mant, k);
    }

    // ---- output ------------------------------------------------------------

    sealed class Out(TextWriter w)
    {
        int _col;
        public void Str(string s) { foreach (char c in s) Char(c); }
        void Char(char c)
        {
            if (c == '\n') { w.Write('\n'); _col = 0; return; }
            if (_col >= 68) { w.Write("\\\n"); _col = 0; } // wrap: 68 chars then a trailing backslash
            w.Write(c);
            _col++;
        }
    }

    static string ToBase(BigInteger n, int obase) // n >= 0, obase in [2,16]
    {
        if (n.IsZero) return "0";
        const string Ds = "0123456789ABCDEF";
        var sb = new StringBuilder();
        while (!n.IsZero) { sb.Insert(0, Ds[(int)(n % obase)]); n /= obase; }
        return sb.ToString();
    }

    static string Format(Num x, int obase)
    {
        if (x.IsZero) return "0";
        var sb = new StringBuilder();
        if (x.M.Sign < 0) sb.Append('-');
        BigInteger mag = BigInteger.Abs(x.M);
        BigInteger scalePow = Pow10(x.S);
        BigInteger intPart = mag / scalePow, frac = mag % scalePow;

        if (obase <= 16)
        {
            if (!intPart.IsZero) sb.Append(ToBase(intPart, obase));
        }
        else if (!intPart.IsZero)
        {
            int width = (obase - 1).ToString().Length;
            foreach (var g in Groups(intPart, obase)) sb.Append(' ').Append(g.ToString().PadLeft(width, '0'));
        }

        if (x.S > 0)
        {
            sb.Append('.');
            int n = 0; // digits needed so obase^n >= 10^scale
            BigInteger p = BigInteger.One, limit = scalePow;
            while (p < limit) { p *= obase; n++; }
            BigInteger r = frac;
            int width = obase <= 16 ? 1 : (obase - 1).ToString().Length;
            for (int i = 0; i < n; i++)
            {
                r *= obase;
                BigInteger digit = r / scalePow;
                r %= scalePow;
                if (obase <= 16) sb.Append("0123456789ABCDEF"[(int)digit]);
                else { if (i > 0) sb.Append(' '); sb.Append(digit.ToString().PadLeft(width, '0')); }
            }
        }
        return sb.ToString();
    }

    static List<BigInteger> Groups(BigInteger n, int obase) // most-significant first
    {
        var list = new List<BigInteger>();
        if (n.IsZero) { list.Add(BigInteger.Zero); return list; }
        while (!n.IsZero) { list.Insert(0, n % obase); n /= obase; }
        return list;
    }

    // ---- AST ---------------------------------------------------------------

    abstract record Node;
    sealed record Lit(string Raw) : Node;
    sealed record VarRef(string Name) : Node;
    sealed record ArrRef(string Name, Node Index) : Node;
    sealed record Reg(string Name) : Node;              // ibase / obase / scale / last
    sealed record Paren(Node Inner) : Node;             // keeps (x=5) from being a top-level assignment
    sealed record UnOp(string Op, Node E) : Node;
    sealed record BinOp(string Op, Node L, Node R) : Node;
    sealed record Assign(Node Target, string Op, Node Rhs) : Node;
    sealed record IncDec(Node Target, bool Pre, bool Inc) : Node;
    sealed record Call(string Name, List<Node> Args) : Node;

    abstract record Stmt;
    sealed record ExprStmt(Node E) : Stmt;
    sealed record StrStmt(string S) : Stmt;
    sealed record PrintStmt(List<object> Items) : Stmt; // each item is a Node or a string
    sealed record IfStmt(Node Cond, Stmt Then, Stmt? Else) : Stmt;
    sealed record WhileStmt(Node Cond, Stmt Body) : Stmt;
    sealed record ForStmt(Node? Init, Node? Cond, Node? Update, Stmt Body) : Stmt;
    sealed record Block(List<Stmt> Body) : Stmt;
    sealed record BreakStmt : Stmt;
    sealed record ContinueStmt : Stmt;
    sealed record ReturnStmt(Node? E) : Stmt;
    sealed record HaltStmt : Stmt;
    sealed record Empty : Stmt;
    sealed record FuncDef(string Name, List<(string Name, bool Arr)> Params, List<(string Name, bool Arr)> Autos, List<Stmt> Body) : Stmt;

    // ---- tokenizer ---------------------------------------------------------

    enum T { Num, Name, Str, Op, NL, Eof }
    readonly record struct Tok(T Kind, string Text);

    static readonly HashSet<string> Keywords = ["if", "else", "while", "for", "break", "continue", "return",
        "print", "define", "auto", "halt", "quit", "length", "sqrt", "read", "last", "ibase", "obase", "scale"];

    static List<Tok> Tokenize(string src)
    {
        var toks = new List<Tok>();
        int i = 0, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            if (c == '\n') { toks.Add(new(T.NL, "\n")); i++; continue; }
            if (c is ' ' or '\t' or '\r') { i++; continue; }
            if (c == '\\' && i + 1 < n && src[i + 1] == '\n') { i += 2; continue; } // line continuation
            if (c == '#') { while (i < n && src[i] != '\n') i++; continue; }         // # comment
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i += 2;
                continue;
            }
            if (c == '"')
            {
                int j = ++i;
                while (i < n && src[i] != '"') i++;
                toks.Add(new(T.Str, src[j..i]));
                if (i < n) i++;
                continue;
            }
            if (char.IsDigit(c) || (c >= 'A' && c <= 'Z') || (c == '.' && i + 1 < n && char.IsDigit(src[i + 1])))
            {
                int j = i;
                while (i < n && (char.IsDigit(src[i]) || (src[i] >= 'A' && src[i] <= 'Z'))) i++;
                if (i < n && src[i] == '.') { i++; while (i < n && (char.IsDigit(src[i]) || (src[i] >= 'A' && src[i] <= 'Z'))) i++; }
                toks.Add(new(T.Num, src[j..i]));
                continue;
            }
            if (c >= 'a' && c <= 'z')
            {
                int j = i;
                while (i < n && (src[i] is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) i++;
                toks.Add(new(T.Name, src[j..i]));
                continue;
            }
            // operators (greedy on the two-char forms)
            string two = i + 1 < n ? src.Substring(i, 2) : "";
            if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||" or "++" or "--"
                or "+=" or "-=" or "*=" or "/=" or "%=" or "^=") { toks.Add(new(T.Op, two)); i += 2; continue; }
            if ("+-*/%^=<>!()[]{},;.".IndexOf(c) >= 0) { toks.Add(new(T.Op, c.ToString())); i++; continue; }
            throw new BcParse($"bc: illegal character: {c}");
        }
        toks.Add(new(T.Eof, ""));
        return toks;
    }

    // ---- parser ------------------------------------------------------------

    sealed class Parser(List<Tok> toks)
    {
        int _p;
        Tok Cur => toks[_p];
        Tok Next => toks[Math.Min(_p + 1, toks.Count - 1)];
        bool IsOp(string s) => Cur.Kind == T.Op && Cur.Text == s;
        bool IsKw(string s) => Cur.Kind == T.Name && Cur.Text == s;
        Tok Eat() => toks[_p++];
        void Expect(string op) { if (!IsOp(op)) throw new BcParse($"bc: syntax error, expected '{op}'"); _p++; }
        void SkipNL() { while (Cur.Kind == T.NL) _p++; }
        void SkipSep() { while (Cur.Kind == T.NL || IsOp(";")) _p++; }

        public List<Stmt> Program()
        {
            var list = new List<Stmt>();
            SkipSep();
            while (Cur.Kind != T.Eof)
            {
                list.Add(Statement());
                if (Cur.Kind is T.NL || IsOp(";")) SkipSep();
                else if (Cur.Kind != T.Eof && !IsOp("}")) SkipSep();
            }
            return list;
        }

        Stmt Statement()
        {
            if (Cur.Kind == T.Str) return new StrStmt(Eat().Text);
            if (IsOp("{"))
            {
                _p++;
                var body = new List<Stmt>();
                SkipSep();
                while (!IsOp("}") && Cur.Kind != T.Eof) { body.Add(Statement()); SkipSep(); }
                Expect("}");
                return new Block(body);
            }
            if (IsOp(";")) { return new Empty(); }
            if (Cur.Kind == T.Name)
                switch (Cur.Text)
                {
                    case "if": return IfStatement();
                    case "while": return WhileStatement();
                    case "for": return ForStatement();
                    case "define": return DefineStatement();
                    case "print": return PrintStatement();
                    case "break": _p++; return new BreakStmt();
                    case "continue": _p++; return new ContinueStmt();
                    case "halt" or "quit": _p++; return new HaltStmt();
                    case "return": return ReturnStatement();
                }
            return new ExprStmt(Expr());
        }

        Stmt SingleStatement() { SkipNL(); return Statement(); }

        Stmt IfStatement()
        {
            _p++; Expect("("); var cond = Expr(); Expect(")");
            var then = SingleStatement();
            Stmt? els = null;
            int save = _p; SkipNL();
            if (IsKw("else")) { _p++; els = SingleStatement(); } else _p = save;
            return new IfStmt(cond, then, els);
        }

        Stmt WhileStatement()
        {
            _p++; Expect("("); var cond = Expr(); Expect(")");
            return new WhileStmt(cond, SingleStatement());
        }

        Stmt ForStatement()
        {
            _p++; Expect("(");
            Node? init = IsOp(";") ? null : Expr(); Expect(";");
            Node? cond = IsOp(";") ? null : Expr(); Expect(";");
            Node? upd = IsOp(")") ? null : Expr(); Expect(")");
            return new ForStmt(init, cond, upd, SingleStatement());
        }

        Stmt ReturnStatement()
        {
            _p++;
            if (Cur.Kind is T.NL or T.Eof || IsOp(";") || IsOp("}")) return new ReturnStmt(null);
            return new ReturnStmt(Expr());
        }

        Stmt PrintStatement()
        {
            _p++;
            var items = new List<object>();
            do
            {
                if (Cur.Kind == T.Str) items.Add(Eat().Text);
                else items.Add(Expr());
            } while (IsOp(",") && Eat().Text == ",");
            return new PrintStmt(items);
        }

        Stmt DefineStatement()
        {
            _p++;
            string name = Cur.Kind == T.Name ? Eat().Text : throw new BcParse("bc: syntax error in define");
            Expect("(");
            var ps = new List<(string, bool)>();
            while (!IsOp(")"))
            {
                string pn = Eat().Text;
                bool arr = false;
                if (IsOp("[")) { _p++; Expect("]"); arr = true; }
                ps.Add((pn, arr));
                if (IsOp(",")) _p++;
            }
            Expect(")"); SkipNL(); Expect("{"); SkipSep();
            var autos = new List<(string, bool)>();
            while (IsKw("auto"))
            {
                _p++;
                while (true)
                {
                    string an = Eat().Text;
                    bool arr = false;
                    if (IsOp("[")) { _p++; Expect("]"); arr = true; }
                    autos.Add((an, arr));
                    if (IsOp(",")) { _p++; continue; }
                    break;
                }
                SkipSep();
            }
            var body = new List<Stmt>();
            while (!IsOp("}") && Cur.Kind != T.Eof) { body.Add(Statement()); SkipSep(); }
            Expect("}");
            return new FuncDef(name, ps, autos, body);
        }

        // expression grammar (low to high precedence):
        //   assign  ->  '=' and the op= forms, right-associative, lvalue on the left
        //   ||  ->  &&  ->  !  ->  relational  ->  + -  ->  * / %  ->  unary -  ->  ^  ->  ++/-- / primary
        Node Expr()
        {
            var node = Or();
            if (Cur.Kind == T.Op && Cur.Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "^=")
            {
                if (node is not (VarRef or ArrRef or Reg)) throw new BcParse("bc: syntax error, invalid assignment");
                string op = Eat().Text;
                return new Assign(node, op, Expr());
            }
            return node;
        }

        Node Or() { var l = And(); while (IsOp("||")) { _p++; l = new BinOp("||", l, And()); } return l; }
        Node And() { var l = Not(); while (IsOp("&&")) { _p++; l = new BinOp("&&", l, Not()); } return l; }
        Node Not() { if (IsOp("!")) { _p++; return new UnOp("!", Not()); } return Rel(); }

        Node Rel()
        {
            var l = AddSub();
            while (Cur.Kind == T.Op && Cur.Text is "==" or "!=" or "<=" or ">=" or "<" or ">")
            { string op = Eat().Text; l = new BinOp(op, l, AddSub()); }
            return l;
        }

        Node AddSub()
        {
            var l = MulDiv();
            while (IsOp("+") || IsOp("-")) { string op = Eat().Text; l = new BinOp(op, l, MulDiv()); }
            return l;
        }

        Node MulDiv()
        {
            var l = Power();
            while (IsOp("*") || IsOp("/") || IsOp("%")) { string op = Eat().Text; l = new BinOp(op, l, Power()); }
            return l;
        }

        Node Power() // right-associative; base and exponent are unary, so -2^2 == (-2)^2 and 2^-2 works
        {
            var b = Unary();
            if (IsOp("^")) { _p++; return new BinOp("^", b, Power()); }
            return b;
        }

        Node Unary()
        {
            if (IsOp("-")) { _p++; return new UnOp("-", Unary()); }
            if (IsOp("++") || IsOp("--")) { bool inc = Eat().Text == "++"; var t = Unary(); return new IncDec(t, true, inc); }
            return Postfix();
        }

        Node Postfix()
        {
            var e = Primary();
            if ((IsOp("++") || IsOp("--")) && e is VarRef or ArrRef or Reg)
            { bool inc = Eat().Text == "++"; return new IncDec(e, false, inc); }
            return e;
        }

        Node Primary()
        {
            if (IsOp("(")) { _p++; var e = Expr(); Expect(")"); return new Paren(e); }
            if (Cur.Kind == T.Num) return new Lit(Eat().Text);
            if (IsOp(".")) { _p++; return new Reg("last"); }
            if (Cur.Kind == T.Name)
            {
                string name = Eat().Text;
                if (IsOp("("))
                {
                    _p++;
                    var args = new List<Node>();
                    while (!IsOp(")"))
                    {
                        // an array passed to a function: name[]
                        if (Cur.Kind == T.Name && Next.Kind == T.Op && Next.Text == "[" && toks[Math.Min(_p + 2, toks.Count - 1)].Text == "]")
                        { args.Add(new ArrRef(Eat().Text, null!)); _p += 2; }
                        else args.Add(Expr());
                        if (IsOp(",")) _p++;
                    }
                    Expect(")");
                    return new Call(name, args);
                }
                if (IsOp("[")) { _p++; var idx = Expr(); Expect("]"); return new ArrRef(name, idx); }
                if (name is "ibase" or "obase" or "scale" or "last") return new Reg(name);
                return new VarRef(name);
            }
            throw new BcParse($"bc: syntax error near '{Cur.Text}'");
        }
    }

    // ---- evaluator ---------------------------------------------------------

    sealed class Eval
    {
        readonly Out _out;
        readonly bool _mathlib;
        readonly CancellationToken _token;
        int _ibase = 10, _obase = 10, _scale, _depth, _ticks;
        Num _last = Num.Zero;
        readonly Dictionary<string, Num> _vars = new();
        readonly Dictionary<string, Dictionary<BigInteger, Num>> _arrays = new();
        readonly Dictionary<string, FuncDef> _funcs = new();
        public TextWriter Err = TextWriter.Null;

        public Eval(Out o, bool mathlib, CancellationToken token) { _out = o; _mathlib = mathlib; _token = token; if (mathlib) _scale = 20; }

        void Tick() { if ((++_ticks & 0x3ff) == 0) _token.ThrowIfCancellationRequested(); }

        public void Run(List<Stmt> prog)
        {
            // functions first, so forward calls resolve
            foreach (var s in prog) if (s is FuncDef f) _funcs[f.Name] = f;
            foreach (var s in prog)
            {
                if (s is FuncDef) continue;
                try { Exec(s); }
                catch (BcError e) { Err.WriteLine(e.Message); }
            }
        }

        void Exec(Stmt s)
        {
            Tick();
            switch (s)
            {
                case FuncDef f: _funcs[f.Name] = f; break;
                case Empty: break;
                case HaltStmt: throw new Halt();
                case BreakStmt: throw new BreakEx();
                case ContinueStmt: throw new ContinueEx();
                case ReturnStmt r: throw new ReturnEx(r.E == null ? Num.Zero : EvalN(r.E));
                case Block b: foreach (var x in b.Body) Exec(x); break;
                case StrStmt str: _out.Str(Unescape(str.S)); break;
                case ExprStmt es:
                    var v = EvalN(es.E);
                    if (es.E is not Assign) { _out.Str(Format(v, _obase)); _out.Str("\n"); _last = v; }
                    break;
                case PrintStmt p:
                    foreach (var item in p.Items)
                        if (item is string lit) _out.Str(Unescape(lit));
                        else { var pv = EvalN((Node)item); _out.Str(Format(pv, _obase)); _last = pv; }
                    break;
                case IfStmt i: if (!EvalN(i.Cond).IsZero) Exec(i.Then); else if (i.Else != null) Exec(i.Else); break;
                case WhileStmt w:
                    while (!EvalN(w.Cond).IsZero) { Tick(); try { Exec(w.Body); } catch (BreakEx) { break; } catch (ContinueEx) { } }
                    break;
                case ForStmt fr:
                    if (fr.Init != null) EvalN(fr.Init);
                    while (fr.Cond == null || !EvalN(fr.Cond).IsZero)
                    {
                        Tick();
                        try { Exec(fr.Body); } catch (BreakEx) { break; } catch (ContinueEx) { }
                        if (fr.Update != null) EvalN(fr.Update);
                    }
                    break;
            }
        }

        Num EvalN(Node n)
        {
            switch (n)
            {
                case Lit l: return Convert(l.Raw, _ibase);
                case Paren p: return EvalN(p.Inner);
                case VarRef v: return _vars.GetValueOrDefault(v.Name, Num.Zero);
                case Reg r: return r.Name switch { "ibase" => Num.Int(_ibase), "obase" => Num.Int(_obase), "scale" => Num.Int(_scale), _ => _last };
                case ArrRef a: return Arr(a.Name).GetValueOrDefault(Index(a.Index), Num.Zero);
                case UnOp u when u.Op == "-": var e = EvalN(u.E); return new Num(-e.M, e.S);
                case UnOp u: return EvalN(u.E).IsZero ? Num.One : Num.Zero; // "!"
                case BinOp b: return Bin(b);
                case Assign asn: return DoAssign(asn);
                case IncDec id: return DoIncDec(id);
                case Call c: return DoCall(c);
            }
            throw new BcError("bc: internal: bad node");
        }

        Num Bin(BinOp b)
        {
            if (b.Op == "&&") return !EvalN(b.L).IsZero && !EvalN(b.R).IsZero ? Num.One : Num.Zero;
            if (b.Op == "||") return !EvalN(b.L).IsZero || !EvalN(b.R).IsZero ? Num.One : Num.Zero;
            var l = EvalN(b.L);
            var r = EvalN(b.R);
            switch (b.Op)
            {
                case "+": return Add(l, r);
                case "-": return Sub(l, r);
                case "*": return Mul(l, r, _scale);
                case "/": return Div(l, r, _scale);
                case "%": return Mod(l, r, _scale);
                case "^": return Pow(l, r, _scale);
            }
            int cmp = Compare(l, r);
            bool res = b.Op switch { "==" => cmp == 0, "!=" => cmp != 0, "<" => cmp < 0, "<=" => cmp <= 0, ">" => cmp > 0, _ => cmp >= 0 };
            return res ? Num.One : Num.Zero;
        }

        Num Apply(string op, Num l, Num r) => op switch
        {
            "+=" => Add(l, r), "-=" => Sub(l, r), "*=" => Mul(l, r, _scale),
            "/=" => Div(l, r, _scale), "%=" => Mod(l, r, _scale), "^=" => Pow(l, r, _scale),
            _ => r,
        };

        Num DoAssign(Assign a)
        {
            Num cur = a.Op == "=" ? Num.Zero : EvalN(a.Target); // op= reads the target before the right-hand side
            Num val = Apply(a.Op, cur, EvalN(a.Rhs));
            return Store(a.Target, val);
        }

        Num DoIncDec(IncDec id)
        {
            Num cur = EvalN(id.Target);
            Num next = id.Inc ? Add(cur, Num.One) : Sub(cur, Num.One);
            Store(id.Target, next);
            return id.Pre ? next : cur;
        }

        Num Store(Node target, Num val)
        {
            switch (target)
            {
                case VarRef v: _vars[v.Name] = val; return val;
                case ArrRef a: Arr(a.Name)[Index(a.Index)] = val; return val;
                case Reg r:
                    long iv = (long)(val.M / Pow10(val.S)); // registers take the integer part
                    switch (r.Name)
                    {
                        case "ibase": if (iv < 2) { Err.WriteLine("bc: ibase too small, set to 2"); iv = 2; } _ibase = (int)Math.Min(iv, 16); return Num.Int(_ibase);
                        case "obase": if (iv < 2) { Err.WriteLine("bc: obase too small, set to 2"); iv = 2; } _obase = (int)iv; return Num.Int(_obase);
                        case "scale": if (iv < 0) { Err.WriteLine("bc: negative scale, set to 0"); iv = 0; } _scale = (int)iv; return Num.Int(_scale);
                        default: _last = val; return val;
                    }
            }
            throw new BcError("bc: cannot assign");
        }

        Dictionary<BigInteger, Num> Arr(string name) => _arrays.TryGetValue(name, out var a) ? a : _arrays[name] = new();
        BigInteger Index(Node idx) { var v = EvalN(idx); return v.M / Pow10(v.S); }

        Num DoCall(Call c)
        {
            switch (c.Name)
            {
                case "sqrt": return Sqrt(EvalN(c.Args[0]), _scale);
                case "length": return Length(EvalN(c.Args[0]));
                case "scale": return Num.Int(EvalN(c.Args[0]).S);
                case "read": throw new BcError("bc: read() is not supported");
            }
            if (_mathlib && c.Name is "s" or "c" or "a" or "l" or "e" or "j")
                return MathFn(c.Name, c.Args);
            if (_funcs.TryGetValue(c.Name, out var fn)) return CallUser(fn, c.Args);
            throw new BcError($"bc: function {c.Name} not defined");
        }

        Num CallUser(FuncDef fn, List<Node> args)
        {
            if (++_depth > 2000) { _depth--; throw new BcError("bc: recursion too deep"); }
            // evaluate arguments in the caller's scope first
            var argVals = new List<Num>();
            var argArrays = new List<Dictionary<BigInteger, Num>?>();
            for (int k = 0; k < fn.Params.Count; k++)
            {
                if (fn.Params[k].Arr)
                {
                    argArrays.Add(args[k] is ArrRef ar ? new Dictionary<BigInteger, Num>(Arr(ar.Name)) : new());
                    argVals.Add(Num.Zero);
                }
                else { argVals.Add(EvalN(args[k])); argArrays.Add(null); }
            }
            // save, bind, run, restore (bc's dynamic scoping for params and autos)
            var savedV = new List<(string, bool, Num)>();
            var savedA = new List<(string, bool, Dictionary<BigInteger, Num>?)>();
            void SaveV(string nm) { savedV.Add((nm, _vars.TryGetValue(nm, out var old), old)); }
            void SaveA(string nm) { savedA.Add((nm, _arrays.TryGetValue(nm, out var old), old)); }
            try
            {
                for (int k = 0; k < fn.Params.Count; k++)
                {
                    var (pn, arr) = fn.Params[k];
                    if (arr) { SaveA(pn); _arrays[pn] = argArrays[k]!; }
                    else { SaveV(pn); _vars[pn] = argVals[k]; }
                }
                foreach (var (an, arr) in fn.Autos)
                    if (arr) { SaveA(an); _arrays[an] = new(); } else { SaveV(an); _vars[an] = Num.Zero; }

                try { foreach (var st in fn.Body) Exec(st); }
                catch (ReturnEx rex) { return rex.Value; }
                return Num.Zero;
            }
            finally
            {
                for (int k = savedV.Count - 1; k >= 0; k--) { var (nm, had, old) = savedV[k]; if (had) _vars[nm] = old; else _vars.Remove(nm); }
                for (int k = savedA.Count - 1; k >= 0; k--) { var (nm, had, old) = savedA[k]; if (had) _arrays[nm] = old!; else _arrays.Remove(nm); }
                _depth--;
            }
        }

        // ---- math library (-l): computed from series/identities, truncated to the requested scale ----
        // These are not GNU's libmath.b. They match GNU digit-for-digit at scale 5 and above (verified by
        // differential runs); at very low scales (1-4) they can differ by a last digit, where our value is the
        // correct truncation and GNU's own guard-digit scheme makes it slightly less accurate.

        int Ws => _scale + 20; // internal working scale: 20 guard digits

        Num MathFn(string name, List<Node> args)
        {
            int outScale = _scale;
            Num r = name switch
            {
                "s" => Sin(EvalN(args[0])),
                "c" => Cos(EvalN(args[0])),
                "a" => Atan(EvalN(args[0])),
                "l" => Ln(EvalN(args[0])),
                "e" => Exp(EvalN(args[0])),
                _ => Bessel(EvalN(args[0]), EvalN(args[1])),
            };
            return Rescale(r, outScale);
        }

        Num MulI(Num a, Num b) => Mul(a, b, Ws);
        Num DivI(Num a, Num b) => Div(a, b, Ws);

        Num Pi() => MulI(Num.Int(4), Atan(Num.One)); // pi at working scale

        Num Exp(Num x)
        {
            // range reduction: e^x = (e^(x/2^k))^(2^k) with |x/2^k| < 1, then Taylor
            int k = 0;
            Num t = x;
            while (Compare(new Num(BigInteger.Abs(t.M), t.S), Num.One) > 0) { t = DivI(t, Num.Int(2)); k++; }
            Num term = Num.One, sum = Num.One;
            for (int i = 1; i < 100000; i++)
            {
                Tick();
                term = DivI(MulI(term, t), Num.Int(i));
                if (term.IsZero) break;
                sum = Add(sum, term);
            }
            for (int i = 0; i < k; i++) sum = MulI(sum, sum);
            return sum;
        }

        Num Ln(Num x)
        {
            if (x.M.Sign <= 0) throw new BcError("bc: cannot take the logarithm of a non-positive number");
            // ln(x) = m*ln(2) + 2*(y + y^3/3 + y^5/5 + ...), y = (a-1)/(a+1), a = x/2^m in [2/3, 4/3]
            int m = 0;
            Num a = x, three = Num.Int(3), two = Num.Int(2);
            while (Compare(MulI(a, two), three) > 0) { a = DivI(a, two); m++; }      // a > 3/2
            while (Compare(MulI(a, three), two) < 0) { a = MulI(a, two); m--; }       // a < 2/3
            Num y = DivI(Sub(a, Num.One), Add(a, Num.One));
            Num y2 = MulI(y, y), term = y, sum = y;
            for (int i = 3; i < 200000; i += 2)
            {
                Tick();
                term = MulI(term, y2);
                Num add = DivI(term, Num.Int(i));
                if (add.IsZero) break;
                sum = Add(sum, add);
            }
            Num ln = MulI(two, sum);
            if (m != 0) ln = Add(ln, MulI(Num.Int(m), Ln2()));
            return ln;
        }

        Num _ln2;
        Num Ln2()
        {
            if (!_ln2.IsZero) return _ln2;
            // ln 2 = 2*(y + y^3/3 + ...), y = 1/3
            Num y = DivI(Num.One, Num.Int(3)), y2 = MulI(y, y), term = y, sum = y;
            for (int i = 3; i < 200000; i += 2)
            {
                Tick();
                term = MulI(term, y2);
                Num add = DivI(term, Num.Int(i));
                if (add.IsZero) break;
                sum = Add(sum, add);
            }
            return _ln2 = MulI(Num.Int(2), sum);
        }

        Num Sin(Num x)
        {
            // reduce modulo 2*pi into [-pi, pi], then into [-pi/2, pi/2] using sin(pi - r) = sin(r)
            Num pi = Pi(), twoPi = MulI(Num.Int(2), pi), halfPi = DivI(pi, Num.Int(2));
            Num q = Div(x, twoPi, 0);                 // nearest-ish integer number of turns (truncated)
            Num r = Sub(x, MulI(q, twoPi));
            while (Compare(r, pi) > 0) r = Sub(r, twoPi);
            while (Compare(r, new Num(-pi.M, pi.S)) < 0) r = Add(r, twoPi);
            int sign = 1;
            if (Compare(r, halfPi) > 0) r = Sub(pi, r);
            else if (Compare(r, new Num(-halfPi.M, halfPi.S)) < 0) { r = Sub(new Num(-pi.M, pi.S), r); }
            Num term = r, sum = r, r2 = MulI(r, r);
            for (int i = 3; i < 200000; i += 2)
            {
                Tick();
                term = DivI(MulI(term, r2), Num.Int((long)i * (i - 1)));
                term = new Num(-term.M, term.S);
                if (term.IsZero) break;
                sum = Add(sum, term);
            }
            return new Num(sign * sum.M, sum.S);
        }

        Num Cos(Num x)
        {
            // reduce modulo 2*pi into [-pi, pi], then into [-pi/2, pi/2] using cos(pi - r) = -cos(r)
            Num pi = Pi(), twoPi = MulI(Num.Int(2), pi), halfPi = DivI(pi, Num.Int(2));
            Num q = Div(x, twoPi, 0);
            Num r = Sub(x, MulI(q, twoPi));
            while (Compare(r, pi) > 0) r = Sub(r, twoPi);
            while (Compare(r, new Num(-pi.M, pi.S)) < 0) r = Add(r, twoPi);
            int sign = 1;
            if (Compare(r, halfPi) > 0) { r = Sub(pi, r); sign = -1; }
            else if (Compare(r, new Num(-halfPi.M, halfPi.S)) < 0) { r = Add(pi, r); sign = -1; }
            Num term = Num.One, sum = Num.One, r2 = MulI(r, r);
            for (int i = 2; i < 200000; i += 2)
            {
                Tick();
                term = DivI(MulI(term, r2), Num.Int((long)i * (i - 1)));
                term = new Num(-term.M, term.S);
                if (term.IsZero) break;
                sum = Add(sum, term);
            }
            return new Num(sign * sum.M, sum.S);
        }

        Num Atan(Num x)
        {
            // halving: atan(x) = 2*atan(x/(1+sqrt(1+x^2))), applied until |x| is small, then Taylor
            int k = 0;
            Num t = x, tenth = new Num(1, 1); // 0.1
            while (Compare(new Num(BigInteger.Abs(t.M), t.S), tenth) > 0)
            {
                Num s = Sqrt(Add(Num.One, MulI(t, t)), Ws);
                t = DivI(t, Add(Num.One, s));
                k++;
            }
            Num term = t, sum = t, t2 = MulI(t, t);
            for (int i = 3; i < 200000; i += 2)
            {
                Tick();
                term = MulI(term, t2);
                term = new Num(-term.M, term.S);
                Num add = DivI(term, Num.Int(i));
                if (add.IsZero) break;
                sum = Add(sum, add);
            }
            return MulI(Num.Int(BigInteger.Pow(2, k) > int.MaxValue ? throw new BcError("bc: atan overflow") : (long)BigInteger.Pow(2, k)), sum);
        }

        Num Bessel(Num order, Num x)
        {
            long n = (long)(order.M / Pow10(order.S));
            int sign = 1;
            if (n < 0) { n = -n; if ((n & 1) == 1) sign = -1; }
            // J_n(x) = sum_{m>=0} (-1)^m / (m! (m+n)!) * (x/2)^(2m+n)
            Num half = DivI(x, Num.Int(2));
            Num half2 = MulI(half, half);
            Num powTerm = Num.One; // (x/2)^n
            for (long i = 0; i < n; i++) powTerm = MulI(powTerm, half);
            BigInteger fact = 1;
            for (long i = 2; i <= n; i++) fact *= i; // n!
            Num term = DivI(powTerm, new Num(fact, 0)); // m=0 term
            Num sum = term;
            for (int m = 1; m < 200000; m++)
            {
                Tick();
                // term_m = term_{m-1} * (-(x/2)^2) / (m * (m+n))
                term = DivI(MulI(term, half2), Num.Int((long)m * (m + n)));
                term = new Num(-term.M, term.S);
                if (term.IsZero) break;
                sum = Add(sum, term);
            }
            return new Num(sign * sum.M, sum.S);
        }

        static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                sb.Append(c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', '\\' => '\\', _ => c });
            }
            return sb.ToString();
        }
    }

    // ---- command entry point -----------------------------------------------

    static int Run(CommandContext ctx)
    {
        bool mathlib = false;
        var files = new List<string>();
        foreach (var a in ctx.Args)
        {
            if (a == "-l" || a == "--mathlib") mathlib = true;
            else if (a is "-q" or "--quiet" or "-s" or "--standard" or "-w" or "--warn" or "-i" or "--interactive") { }
            else if (a.StartsWith('-') && a.Length > 1 && a != "-") ctx.Stderr.WriteLine($"bc: unknown option {a}");
            else files.Add(a);
        }

        string source;
        if (files.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                if (f == "-") { sb.Append(ctx.StdinText); continue; }
                try { sb.Append(ctx.Fs.ReadAllText(ctx.ResolvePath(f))).Append('\n'); }
                catch (ShellException e) { ctx.Stderr.WriteLine($"bc: {f}: {e.Message[(e.Message.LastIndexOf(':') + 2)..]}"); return 1; }
            }
            source = sb.ToString();
        }
        else source = ctx.StdinText;

        var o = new Out(ctx.Stdout);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        cts.CancelAfter(TimeLimit);
        var eval = new Eval(o, mathlib, cts.Token) { Err = ctx.Stderr };
        try
        {
            var prog = new Parser(Tokenize(source)).Program();
            eval.Run(prog);
            return 0;
        }
        catch (Halt) { return 0; }
        catch (BcParse e) { ctx.Stderr.WriteLine(e.Message); return 0; } // GNU prints a syntax error and exits 0 on a pipe
        catch (BcLimit e) { ctx.Stderr.WriteLine(e.Message); return 1; }
        catch (OperationCanceledException) when (!ctx.CancellationToken.IsCancellationRequested)
        {
            ctx.Stderr.WriteLine("bc: computation stopped: exceeded the 5 s limit (lower scale or use smaller operands)");
            return 1;
        }
    }
}
