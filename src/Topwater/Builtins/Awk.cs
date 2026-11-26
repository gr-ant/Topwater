using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Topwater;

/// <summary>
/// awk: patterns, ranges, BEGIN/END, fields, associative arrays, if/while/do/for/for-in, print/printf (with > / >> files),
/// user-defined functions, getline (from the input or a file), RS (one character, a regex, or "" for paragraph mode)
/// and the common built-ins. Mirrors python/src/topwater/commands/awk.py.
/// shortcut: no pipes to/from commands (print | "cmd", "cmd" | getline); add them if agents need them.
/// </summary>
internal static class Awk
{
    public static void Register(Shell shell) => shell.Register("awk", ctx => new AwkRunner(ctx).Run());
}

internal readonly struct AV
{
    // Kind: 0 number, 1 string, 2 strnum (input data: numeric if it looks numeric), 3 uninitialized
    public readonly string? S;
    public readonly double N;
    public readonly byte K;
    AV(string? s, double n, byte k) { S = s; N = n; K = k; }
    public static AV Num(double n) => new(null, n, 0);
    public static AV Str(string s) => new(s, 0, 1);
    public static AV StrNum(string s) => new(s, 0, 2);
    public static readonly AV Uninit = new("", 0, 3);
    public static AV Bool(bool b) => Num(b ? 1 : 0);

    static readonly Regex NumRe = new(@"^\s*[-+]?(\d+\.?\d*([eE][-+]?\d+)?|\.\d+([eE][-+]?\d+)?)\s*$");
    static readonly Regex LeadRe = new(@"^\s*[-+]?(\d+\.?\d*([eE][-+]?\d+)?|\.\d+([eE][-+]?\d+)?)");

    public bool LooksNumeric => K == 2 && NumRe.IsMatch(S!);
    public bool IsNumeric => K is 0 or 3 || LooksNumeric;

    public double ToNum()
    {
        if (K == 0) return N;
        if (K == 3) return 0;
        var m = LeadRe.Match(S!);
        return m.Success ? double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture) : 0;
    }

    public string ToStr() => K == 0 ? AwkFormat.Number(N) : S!;
    public bool Truthy => K switch { 0 => N != 0, 1 => S!.Length > 0, 2 => LooksNumeric ? ToNum() != 0 : S!.Length > 0, _ => false };
}

internal static class AwkFormat
{
    /// <summary>Like gawk, integral values print as integers however large; others use the given %g-style format.</summary>
    public static string Number(double v, string? fmt = null) =>
        double.IsFinite(v) && v == Math.Floor(v)
            ? (Math.Abs(v) < 9e18 ? ((long)v).ToString(CultureInfo.InvariantCulture) : new System.Numerics.BigInteger(v).ToString(CultureInfo.InvariantCulture))
            : fmt == null ? G(v, 6, false) : Sprintf(fmt, [AV.Num(v)]);

    public static string E(double v, int prec, bool upper)
    {
        var s = v.ToString((upper ? "E" : "e") + prec, CultureInfo.InvariantCulture); // 1.234560e+005
        int i = s.IndexOfAny(['e', 'E']);
        int exp = int.Parse(s[(i + 1)..], CultureInfo.InvariantCulture);
        return s[..(i + 1)] + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
    }

    public static string G(double v, int prec, bool upper, bool alt = false)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        if (v == 0) return "0";
        int p = prec == 0 ? 1 : prec;
        var e = E(v, p - 1, upper);
        int x = int.Parse(e[(e.IndexOfAny(['e', 'E']) + 1)..], CultureInfo.InvariantCulture);
        string r;
        if (x < -4 || x >= p)
        {
            int i = e.IndexOfAny(['e', 'E']);
            var mant = e[..i];
            if (!alt && mant.Contains('.')) mant = mant.TrimEnd('0').TrimEnd('.');
            r = mant + e[i..];
        }
        else
        {
            r = v.ToString("F" + (p - 1 - x), CultureInfo.InvariantCulture);
            if (!alt && r.Contains('.')) r = r.TrimEnd('0').TrimEnd('.');
        }
        return r;
    }

    static readonly Regex Spec = new(@"%([-+ #0]*)(\*|\d+)?(?:\.(\*|\d*))?([diouxXeEfFgGcs%])");

    public static string Sprintf(string fmt, IReadOnlyList<AV> args)
    {
        int next = 0;
        AV Next() => next < args.Count ? args[next++] : AV.Uninit;
        return Spec.Replace(fmt, m =>
        {
            char conv = m.Groups[4].Value[0];
            if (conv == '%') return "%";
            var flags = m.Groups[1].Value;
            int width = m.Groups[2].Value == "*" ? (int)Next().ToNum() : m.Groups[2].Value.Length > 0 ? int.Parse(m.Groups[2].Value) : 0;
            int? prec = m.Groups[3].Success ? m.Groups[3].Value == "*" ? (int)Next().ToNum() : m.Groups[3].Value.Length > 0 ? int.Parse(m.Groups[3].Value) : 0 : null;
            if (width < 0) { flags += "-"; width = -width; }
            var arg = Next();
            string body;
            string sign = "";
            bool numeric = true;
            switch (conv)
            {
                case 'd' or 'i':
                    var n = arg.ToNum();
                    long l = double.IsNaN(n) ? 0 : (long)Math.Truncate(n);
                    sign = l < 0 ? "-" : flags.Contains('+') ? "+" : flags.Contains(' ') ? " " : "";
                    body = Math.Abs(l).ToString(CultureInfo.InvariantCulture);
                    if (prec is { } dp) body = body.PadLeft(dp, '0');
                    break;
                case 'o' or 'x' or 'X' or 'u':
                    var u = (long)Math.Truncate(arg.ToNum());
                    body = conv == 'u' ? u.ToString(CultureInfo.InvariantCulture) : Convert.ToString(u, conv == 'o' ? 8 : 16);
                    if (conv == 'X') body = body.ToUpperInvariant();
                    if (flags.Contains('#') && u != 0) body = (conv == 'o' ? "0" : conv == 'x' ? "0x" : conv == 'X' ? "0X" : "") + body;
                    break;
                case 'e' or 'E' or 'f' or 'F' or 'g' or 'G':
                    var d = arg.ToNum();
                    sign = d < 0 || (d == 0 && double.IsNegative(d)) ? "-" : flags.Contains('+') ? "+" : flags.Contains(' ') ? " " : "";
                    d = Math.Abs(d);
                    body = conv switch
                    {
                        'e' or 'E' => E(d, prec ?? 6, conv == 'E'),
                        'f' or 'F' => d.ToString("F" + (prec ?? 6), CultureInfo.InvariantCulture),
                        _ => G(d, prec ?? 6, conv == 'G', flags.Contains('#')),
                    };
                    break;
                case 'c':
                    numeric = false;
                    body = arg.K == 0 ? ((char)(int)arg.N).ToString() : arg.ToStr() is { Length: > 0 } cs ? cs[..1] : "";
                    break;
                default: // s
                    numeric = false;
                    body = arg.ToStr();
                    if (prec is { } sp && sp < body.Length) body = body[..sp];
                    break;
            }
            int len = sign.Length + body.Length;
            if (len >= width) return sign + body;
            if (flags.Contains('-')) return (sign + body).PadRight(width);
            if (flags.Contains('0') && numeric && !(prec != null && conv is 'd' or 'i')) return sign + body.PadLeft(width - sign.Length, '0');
            return (sign + body).PadLeft(width);
        });
    }
}

internal sealed class AwkRunner
{
    // ---------- AST ----------
    abstract record Node;
    sealed record NumN(double V) : Node;
    sealed record StrN(string V) : Node;
    sealed record RegexN(string Pattern) : Node;
    sealed record VarN(string Name) : Node;
    sealed record FieldN(Node Index) : Node;
    sealed record IndexN(string Name, List<Node> Keys) : Node;
    sealed record AssignN(Node Target, string Op, Node Value) : Node;
    sealed record CondN(Node C, Node A, Node B) : Node;
    sealed record BinN(string Op, Node L, Node R) : Node;
    sealed record UnN(string Op, Node E) : Node;
    sealed record IncrN(Node Target, bool Inc, bool Prefix) : Node;
    sealed record MatchN(Node S, Node Re, bool Negate) : Node;
    sealed record InN(List<Node> Keys, string Array) : Node;
    sealed record ConcatN(Node L, Node R) : Node;
    sealed record CallN(string Name, List<Node> Args) : Node;
    sealed record UserCallN(string Name, List<Node> Args) : Node;
    /// <summary>getline [var] [&lt; file]; Cmd is set for `cmd | getline`.</summary>
    sealed record GetlineN(Node? Target, Node? File, Node? Cmd) : Node;

    abstract record Stmt;
    sealed record BlockS(List<Stmt> Body) : Stmt;
    sealed record ExprS(Node E) : Stmt;
    sealed record PrintS(bool Printf, List<Node> Args, string? RedirOp, Node? Dest) : Stmt;
    sealed record IfS(Node C, Stmt Then, Stmt? Else) : Stmt;
    sealed record WhileS(Node C, Stmt Body, bool DoWhile) : Stmt;
    sealed record ForS(Stmt? Init, Node? C, Stmt? Step, Stmt Body) : Stmt;
    sealed record ForInS(string Var, string Array, Stmt Body) : Stmt;
    sealed record SimpleS(string Kind, Node? Arg) : Stmt; // next, exit, break, continue
    sealed record DeleteS(string Array, List<Node>? Keys) : Stmt;
    sealed record ReturnS(Node? Value) : Stmt;

    sealed class Rule
    {
        public string Kind = "main"; // BEGIN | END | main
        public Node? Pattern, Pattern2;
        public BlockS? Action;
        public bool InRange;
    }

    sealed class NextEx : Exception;
    sealed class ExitEx : Exception;
    sealed class BreakEx : Exception;
    sealed class ContinueEx : Exception;
    sealed class ReturnEx(AV value) : Exception { public AV Value { get; } = value; }

    /// <summary>A user function call: parameters are locals; array arguments are passed by reference.</summary>
    sealed class Frame(IEnumerable<string> parameters)
    {
        public readonly HashSet<string> Params = [.. parameters];
        public readonly Dictionary<string, AV> Scalars = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, AV>> Arrays = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Pending = new(StringComparer.Ordinal); // parameter -> unset global passed for it
    }

    readonly CommandContext _ctx;
    readonly Dictionary<string, AV> _vars = new(StringComparer.Ordinal);
    readonly Dictionary<string, Dictionary<string, AV>> _arrays = new(StringComparer.Ordinal);
    readonly Dictionary<string, Regex> _regexes = new(StringComparer.Ordinal);
    readonly List<(string Path, bool Append, StringBuilder Text)> _outputs = [];
    readonly Random _random = new(0);
    List<string> _fields = [""];
    int _exitCode;
    long _iterations;
    Dictionary<string, (List<string> Params, BlockS Body)> _functions = [];
    readonly List<Frame> _frames = [];
    IEnumerator<string> _input = Enumerable.Empty<string>().GetEnumerator(); // the main input's records, shared by the rules and getline
    readonly Dictionary<string, IEnumerator<string>> _getlineFiles = new(StringComparer.Ordinal);

    public AwkRunner(CommandContext ctx) => _ctx = ctx;

    AwkError Err(string msg) => new(msg);
    sealed class AwkError(string message) : ShellException(message, 2);

    // ---------- driver ----------

    public int Run()
    {
        var args = _ctx.Args;
        string? program = null;
        var assigns = new List<string>();
        var operands = new List<string>();
        string? fs = null;
        int i = 0;
        for (; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--") { i++; break; }
            if (a == "--version" || (a == "-W" && i + 1 < args.Count && args[i + 1] == "version"))
            {
                _ctx.Stdout.WriteLine("GNU Awk 5.2.1, API 3.2, PMA Avon 8-g1, (GNU MPFR 4.2.1, GNU MP 6.3.0)");
                return 0;
            }
            if (a.Length < 2 || a[0] != '-') break;
            string Val(string flag) => a.Length > 2 ? a[2..] : ++i < args.Count ? args[i] : throw Err($"option requires an argument -- {flag}");
            if (a.StartsWith("-F")) fs = Val("F");
            else if (a.StartsWith("-v")) assigns.Add(Val("v"));
            else if (a.StartsWith("-f")) { var source = Val("f"); program = source is "-" or "/dev/stdin" ? _ctx.StdinText : _ctx.Fs.ReadAllText(_ctx.ResolvePath(source)); }
            else throw Err($"unknown option {a}");
        }
        if (program == null)
        {
            if (i >= args.Count) throw Err("usage: awk [-F fs][-v var=value][prog | -f progfile][file ...]");
            program = args[i++];
        }
        operands.AddRange(args.Skip(i));

        var parser = new AwkParser(program);
        var rules = parser.Parse();
        _functions = parser.Functions;
        SetVar("FS", AV.Str(fs == null ? " " : fs == "t" ? "\t" : Builtins.Unescape(fs)));
        SetVar("OFS", AV.Str(" "));
        SetVar("ORS", AV.Str("\n"));
        SetVar("RS", AV.Str("\n"));
        SetVar("SUBSEP", AV.Str("\u001c"));
        SetVar("CONVFMT", AV.Str("%.6g"));
        SetVar("OFMT", AV.Str("%.6g"));
        SetVar("NR", AV.Num(0));
        SetVar("FNR", AV.Num(0));
        SetVar("RSTART", AV.Num(0));
        SetVar("RLENGTH", AV.Num(-1));
        SetVar("FILENAME", AV.Str(""));
        var environ = _arrays["ENVIRON"] = new(StringComparer.Ordinal);
        foreach (var (k, v) in _ctx.Shell.Environment) environ[k] = AV.StrNum(v);
        foreach (var a in assigns) Assign(a);

        _input = InputRecords(operands).GetEnumerator();
        try
        {
            foreach (var r in rules.Where(r => r.Kind == "BEGIN")) Exec(r.Action!);
            if (rules.Any(r => r.Kind != "BEGIN")) ProcessInput(rules);
        }
        catch (ExitEx) { }
        try
        {
            foreach (var r in rules.Where(r => r.Kind == "END")) Exec(r.Action!);
        }
        catch (ExitEx) { }

        foreach (var (path, append, text) in _outputs)
            if (append) _ctx.Fs.AppendAllText(path, text.ToString()); else _ctx.Fs.WriteAllText(path, text.ToString());
        return _exitCode;
    }

    bool Assign(string assignment)
    {
        var m = Regex.Match(assignment, @"^([A-Za-z_][A-Za-z0-9_]*)=(.*)$", RegexOptions.Singleline);
        if (!m.Success) return false;
        SetVar(m.Groups[1].Value, AV.StrNum(Builtins.Unescape(m.Groups[2].Value)));
        return true;
    }

    /// <summary>The main input: records of each file operand in turn (var=value operands are applied when reached), or stdin.</summary>
    IEnumerable<string> InputRecords(List<string> operands)
    {
        bool readAny = false;
        foreach (var op in operands)
        {
            if (Assign(op)) continue;
            readAny = true;
            string name, text;
            if (op == "-") (name, text) = ("", _ctx.StdinText);
            else
            {
                var path = _ctx.ResolvePath(op);
                if (!_ctx.Fs.IsFile(path))
                {
                    _ctx.Stderr.WriteLine($"awk: cannot open \"{op}\" ({(_ctx.Fs.IsDirectory(path) ? "Is a directory" : "No such file or directory")})");
                    _exitCode = 2;
                    continue;
                }
                (name, text) = (op, _ctx.Fs.ReadAllText(path));
            }
            _vars["FILENAME"] = AV.Str(name);
            _vars["FNR"] = AV.Num(0);
            foreach (var r in Records(text)) yield return r;
        }
        if (!readAny)
        {
            _vars["FILENAME"] = AV.Str("");
            foreach (var r in Records(_ctx.StdinText)) yield return r;
        }
    }

    void ProcessInput(List<Rule> rules)
    {
        var main = rules.Where(r => r.Kind == "main").ToList();
        while (_input.MoveNext())
        {
            _ctx.CancellationToken.ThrowIfCancellationRequested(); // slow patterns over many records: the call's time limit
            _vars["NR"] = AV.Num(_vars["NR"].ToNum() + 1);
            _vars["FNR"] = AV.Num(_vars["FNR"].ToNum() + 1);
            SetRecord(_input.Current);
            try
            {
                foreach (var r in main)
                    if (Matches(r)) { if (r.Action != null) Exec(r.Action); else Print(new PrintS(false, [], null, null)); }
            }
            catch (NextEx) { }
        }
    }

    /// <summary>Splits input into records on RS: newline, one character, a regex, or "" (blank-line separated paragraphs).</summary>
    List<string> Records(string text)
    {
        var rs = _vars["RS"].ToStr();
        if (rs == "\n") return Builtins.SplitLines(text);
        if (rs.Length == 0) return Regex.Split(text.Trim('\n'), @"\n\n+").Where(p => p.Length > 0).ToList();
        var parts = rs.Length == 1 ? text.Split(rs[0]).ToList() : GetRegex(rs).Split(text).ToList();
        if (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        return parts;
    }

    bool Matches(Rule r)
    {
        if (r.Pattern == null) return true;
        if (r.Pattern2 == null) return Eval(r.Pattern).Truthy;
        if (r.InRange)
        {
            if (Eval(r.Pattern2).Truthy) r.InRange = false;
            return true;
        }
        if (!Eval(r.Pattern).Truthy) return false;
        r.InRange = !Eval(r.Pattern2).Truthy;
        return true;
    }

    // ---------- variables & fields ----------

    /// <summary>The current function frame when name is one of its parameters, else null.</summary>
    Frame? Local(string name) => _frames.Count > 0 && _frames[^1].Params.Contains(name) ? _frames[^1] : null;

    bool IsArray(string name) => Local(name) is { } f ? f.Arrays.ContainsKey(name) : _arrays.ContainsKey(name);

    AV GetVar(string name)
    {
        if (Local(name) is { } f)
        {
            if (f.Arrays.ContainsKey(name)) throw Err($"attempt to use array {name} in a scalar context");
            return f.Scalars.GetValueOrDefault(name, AV.Uninit);
        }
        return name == "NF" ? AV.Num(_fields.Count - 1) : _vars.GetValueOrDefault(name, AV.Uninit);
    }

    void SetVar(string name, AV v)
    {
        if (Local(name) is { } f)
        {
            if (f.Arrays.ContainsKey(name)) throw Err($"can't assign to {name}; it's an array name.");
            f.Scalars[name] = v;
            return;
        }
        if (name == "NF")
        {
            int n = Math.Max(0, (int)v.ToNum());
            while (_fields.Count - 1 < n) _fields.Add("");
            if (_fields.Count - 1 > n) _fields.RemoveRange(n + 1, _fields.Count - 1 - n);
            Rebuild();
            return;
        }
        if (_arrays.ContainsKey(name)) throw Err($"can't assign to {name}; it's an array name.");
        _vars[name] = v;
    }

    void SetRecord(string line)
    {
        var fs = _vars["FS"].ToStr();
        if (_vars["RS"].ToStr().Length == 0 && fs != " ") // paragraph mode: newline always separates fields too
            fs = fs.Length == 0 ? "\n" : $"(?:{(fs.Length > 1 ? fs : Regex.Escape(fs))})|\n";
        _fields = [line];
        _fields.AddRange(SplitFields(line, fs));
    }

    /// <summary>Number to string with CONVFMT (integers stay integers).</summary>
    string Str(AV v) => v.K == 0 ? AwkFormat.Number(v.N, _vars["CONVFMT"].ToStr() is { Length: > 0 } f ? f : null) : v.ToStr();

    List<string> SplitFields(string s, string fs)
    {
        if (s.Length == 0) return [];
        if (fs == " ") return s.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (fs.Length == 1 && fs != "\\") return s.Split(fs[0]).ToList();
        if (fs.Length == 0) return s.Select(c => c.ToString()).ToList();
        return GetRegex(fs).Split(s).ToList();
    }

    void Rebuild() => _fields[0] = string.Join(GetVar("OFS").ToStr(), _fields.Skip(1));

    string GetField(int i)
    {
        if (i < 0) throw Err($"trying to access out of range field {i}");
        return i < _fields.Count ? _fields[i] : "";
    }

    void SetField(int i, string v)
    {
        if (i < 0) throw Err($"trying to access out of range field {i}");
        if (i == 0) { SetRecord(v); return; }
        while (_fields.Count <= i) _fields.Add("");
        _fields[i] = v;
        Rebuild();
    }

    Dictionary<string, AV> Array(string name)
    {
        if (Local(name) is { } f)
        {
            if (f.Scalars.TryGetValue(name, out var sv) && sv.K != 3) throw Err($"can't use scalar {name} as array");
            if (f.Pending.Remove(name, out var global))
            {
                if (!_arrays.TryGetValue(global, out var shared)) _arrays[global] = shared = new(StringComparer.Ordinal);
                f.Arrays[name] = shared;
            }
            if (!f.Arrays.TryGetValue(name, out var local)) f.Arrays[name] = local = new(StringComparer.Ordinal);
            return local;
        }
        if (_vars.ContainsKey(name) && _vars[name].K != 3) throw Err($"can't use scalar {name} as array");
        if (!_arrays.TryGetValue(name, out var arr)) _arrays[name] = arr = new(StringComparer.Ordinal);
        return arr;
    }

    string Key(List<Node> keys) => string.Join(GetVar("SUBSEP").ToStr(), keys.Select(k => Str(Eval(k))));

    Regex GetRegex(string pattern)
    {
        if (_regexes.TryGetValue(pattern, out var re)) return re;
        try { re = new Regex(Builtins.PosixToNet(pattern, extended: true, bracketEscapes: true), RegexOptions.CultureInvariant, Shell.RegexTimeout); }
        catch (ArgumentException e) { throw Err($"invalid regular expression /{pattern}/: {e.Message}"); }
        return _regexes[pattern] = re;
    }

    Regex RegexOf(Node n) => GetRegex(n is RegexN r ? r.Pattern : Eval(n).ToStr());

    AV Get(Node lv) => lv switch
    {
        VarN v => GetVar(v.Name),
        FieldN f => AV.StrNum(GetField((int)Eval(f.Index).ToNum())),
        IndexN ix => Array(ix.Name) is var arr && Key(ix.Keys) is var k && arr.TryGetValue(k, out var val) ? val : arr[k] = AV.Uninit,
        _ => throw Err("invalid assignment target"),
    };

    void Set(Node lv, AV v)
    {
        switch (lv)
        {
            case VarN var: SetVar(var.Name, v); break;
            case FieldN f: SetField((int)Eval(f.Index).ToNum(), v.ToStr()); break;
            case IndexN ix: Array(ix.Name)[Key(ix.Keys)] = v; break;
            default: throw Err("invalid assignment target");
        }
    }

    // ---------- statements ----------

    void Exec(Stmt s)
    {
        switch (s)
        {
            case BlockS b: foreach (var x in b.Body) Exec(x); break;
            case ExprS e: Eval(e.E); break;
            case PrintS p: Print(p); break;
            case IfS i:
                if (Eval(i.C).Truthy) Exec(i.Then);
                else if (i.Else != null) Exec(i.Else);
                break;
            case WhileS w:
                if (w.DoWhile) { do { if (!Loop(w.Body)) break; } while (Eval(w.C).Truthy); }
                else while (Eval(w.C).Truthy) { if (!Loop(w.Body)) break; }
                break;
            case ForS f:
                if (f.Init != null) Exec(f.Init);
                while (f.C == null || Eval(f.C).Truthy)
                {
                    if (!Loop(f.Body)) break;
                    if (f.Step != null) Exec(f.Step);
                }
                break;
            case ForInS fi:
                foreach (var k in Array(fi.Array).Keys.ToList())
                {
                    SetVar(fi.Var, AV.StrNum(k));
                    if (!Loop(fi.Body)) break;
                }
                break;
            case SimpleS { Kind: "next" }: throw new NextEx();
            case SimpleS { Kind: "exit" } ex:
                if (ex.Arg != null) _exitCode = (int)Eval(ex.Arg).ToNum();
                throw new ExitEx();
            case SimpleS { Kind: "break" }: throw new BreakEx();
            case SimpleS { Kind: "continue" }: throw new ContinueEx();
            case ReturnS r:
                if (_frames.Count == 0) throw Err("return outside function");
                throw new ReturnEx(r.Value == null ? AV.Uninit : Eval(r.Value));
            case DeleteS d:
                if (d.Keys == null) Array(d.Array).Clear();
                else Array(d.Array).Remove(Key(d.Keys));
                break;
        }
    }

    LimitExceeded TooLarge() => Shell.TooLarge("awk: string", _ctx.Shell.Options.MaxStringBytes, "build smaller strings or print as you go");

    /// <summary>Runs a loop body; false means `break`.</summary>
    bool Loop(Stmt body)
    {
        if (++_iterations > 10_000_000) throw Err("loop iteration limit exceeded");
        _ctx.CancellationToken.ThrowIfCancellationRequested();
        try { Exec(body); return true; }
        catch (BreakEx) { return false; }
        catch (ContinueEx) { return true; }
    }

    string OutputString(AV v) => v.K == 0 ? AwkFormat.Number(v.N, GetVar("OFMT").ToStr()) : v.ToStr();

    void Print(PrintS p)
    {
        string text;
        if (p.Printf)
        {
            if (p.Args.Count == 0) throw Err("printf: no format");
            text = AwkFormat.Sprintf(Eval(p.Args[0]).ToStr(), p.Args.Skip(1).Select(Eval).ToList());
        }
        else
        {
            text = (p.Args.Count == 0 ? _fields[0] : string.Join(GetVar("OFS").ToStr(), p.Args.Select(a => OutputString(Eval(a)))))
                   + GetVar("ORS").ToStr();
        }
        if (p.Dest == null) { _ctx.Stdout.Write(text); return; }
        var dest = Eval(p.Dest).ToStr();
        if (dest is "/dev/stdout" or "-") { _ctx.Stdout.Write(text); return; }
        if (dest == "/dev/stderr") { _ctx.Stderr.Write(text); return; }
        var path = _ctx.ResolvePath(dest);
        var existing = _outputs.FindIndex(o => o.Path == path);
        if (existing < 0) { _outputs.Add((path, p.RedirOp == ">>", new StringBuilder())); existing = _outputs.Count - 1; }
        _outputs[existing].Text.Append(text);
    }

    // ---------- expressions ----------

    static int Compare(AV a, AV b) =>
        a.IsNumeric && b.IsNumeric ? a.ToNum().CompareTo(b.ToNum()) : string.CompareOrdinal(a.ToStr(), b.ToStr());

    AV Eval(Node n)
    {
        switch (n)
        {
            case NumN x: return AV.Num(x.V);
            case StrN s: return AV.Str(s.V);
            case RegexN r: return AV.Bool(GetRegex(r.Pattern).IsMatch(_fields[0]));
            case VarN or FieldN or IndexN: return Get(n);
            case AssignN a:
            {
                var v = Eval(a.Value);
                if (a.Op != "=")
                {
                    double l = Get(a.Target).ToNum(), r = v.ToNum();
                    v = AV.Num(Arith(a.Op[..^1], l, r));
                }
                Set(a.Target, v);
                return v;
            }
            case CondN c: return Eval(c.C).Truthy ? Eval(c.A) : Eval(c.B);
            case BinN { Op: "&&" } b: return AV.Bool(Eval(b.L).Truthy && Eval(b.R).Truthy);
            case BinN { Op: "||" } b: return AV.Bool(Eval(b.L).Truthy || Eval(b.R).Truthy);
            case BinN b:
            {
                var l = Eval(b.L);
                var r = Eval(b.R);
                return b.Op switch
                {
                    "<" => AV.Bool(Compare(l, r) < 0), "<=" => AV.Bool(Compare(l, r) <= 0),
                    ">" => AV.Bool(Compare(l, r) > 0), ">=" => AV.Bool(Compare(l, r) >= 0),
                    "==" => AV.Bool(Compare(l, r) == 0), "!=" => AV.Bool(Compare(l, r) != 0),
                    _ => AV.Num(Arith(b.Op, l.ToNum(), r.ToNum())),
                };
            }
            case UnN u:
                var e = Eval(u.E);
                return u.Op switch { "!" => AV.Bool(!e.Truthy), "-" => AV.Num(-e.ToNum()), _ => AV.Num(e.ToNum()) };
            case IncrN inc:
            {
                var old = Get(inc.Target).ToNum();
                var nv = old + (inc.Inc ? 1 : -1);
                Set(inc.Target, AV.Num(nv));
                return AV.Num(inc.Prefix ? nv : old);
            }
            case MatchN m: return AV.Bool(RegexOf(m.Re).IsMatch(Eval(m.S).ToStr()) != m.Negate);
            case InN i: return AV.Bool(Array(i.Array).ContainsKey(Key(i.Keys)));
            case ConcatN c:
            {
                var (l, r) = (Str(Eval(c.L)), Str(Eval(c.R)));
                if (l.Length + (long)r.Length > _ctx.Shell.Options.MaxStringBytes) throw TooLarge();
                return AV.Str(l + r);
            }
            case CallN call: return Call(call);
            case UserCallN u: return UserCall(u);
            case GetlineN g: return Getline(g);
        }
        throw Err("unsupported expression");
    }

    double Arith(string op, double l, double r) => op switch
    {
        "+" => l + r, "-" => l - r, "*" => l * r,
        "/" => r == 0 ? throw Err("division by zero") : l / r,
        "%" => r == 0 ? throw Err("division by zero in %") : l % r,
        "^" or "**" => Math.Pow(l, r),
        _ => throw Err($"unknown operator {op}"),
    };

    AV Call(CallN c)
    {
        var a = c.Args;
        string S(int i) => i < a.Count ? Str(Eval(a[i])) : "";
        double N(int i) => i < a.Count ? Eval(a[i]).ToNum() : 0;
        switch (c.Name)
        {
            case "length":
                if (a.Count == 0) return AV.Num(_fields[0].Length);
                if (a[0] is VarN v && IsArray(v.Name)) return AV.Num(Array(v.Name).Count);
                return AV.Num(S(0).Length);
            case "substr":
            {
                var s = S(0);
                double m = Math.Round(N(1), MidpointRounding.ToEven);
                double end = a.Count > 2 ? m + Math.Round(N(2), MidpointRounding.ToEven) : double.PositiveInfinity;
                double start = Math.Max(m, 1);
                end = Math.Min(end, s.Length + 1);
                return AV.Str(end <= start ? "" : s[((int)start - 1)..((int)end - 1)]);
            }
            case "index": return AV.Num(S(0).IndexOf(S(1), StringComparison.Ordinal) + 1);
            case "split":
            {
                if (a.Count < 2 || a[1] is not VarN target) throw Err("split: second argument must be an array name");
                var parts = a.Count > 2 ? (a[2] is RegexN re ? GetRegex(re.Pattern).Split(S(0)).ToList() : SplitFields(S(0), S(2))) : SplitFields(S(0), GetVar("FS").ToStr());
                if (S(0).Length == 0) parts = [];
                var dest = Array(target.Name);
                dest.Clear();
                for (int i = 0; i < parts.Count; i++) dest[(i + 1).ToString(CultureInfo.InvariantCulture)] = AV.StrNum(parts[i]);
                return AV.Num(parts.Count);
            }
            case "sub" or "gsub":
            {
                if (a.Count < 2) throw Err($"{c.Name}: requires at least 2 arguments");
                var re = RegexOf(a[0]);
                var repl = S(1);
                var target = a.Count > 2 ? a[2] : new FieldN(new NumN(0));
                var src = Get(target).ToStr();
                int count = 0, pos = 0;
                // one builder rather than Regex.Replace, which holds a string per match until the end
                var sb = new StringBuilder();
                for (var m = re.Match(src); m.Success; m = m.NextMatch())
                {
                    count++;
                    sb.Append(src, pos, m.Index - pos);
                    for (int i = 0; i < repl.Length; i++)
                    {
                        if (repl[i] == '\\' && i + 1 < repl.Length && repl[i + 1] is '&' or '\\') sb.Append(repl[++i]);
                        else if (repl[i] == '&') sb.Append(src, m.Index, m.Length);
                        else sb.Append(repl[i]);
                    }
                    pos = m.Index + m.Length;
                    if (sb.Length > _ctx.Shell.Options.MaxStringBytes) throw TooLarge();
                    if (c.Name == "sub") break;
                }
                if (count > 0) Set(target, AV.Str(sb.Append(src, pos, src.Length - pos).ToString()));
                return AV.Num(count);
            }
            case "match":
            {
                var m = RegexOf(a[1]).Match(S(0));
                SetVar("RSTART", AV.Num(m.Success ? m.Index + 1 : 0));
                SetVar("RLENGTH", AV.Num(m.Success ? m.Length : -1));
                return AV.Num(m.Success ? m.Index + 1 : 0);
            }
            case "sprintf": return AV.Str(AwkFormat.Sprintf(S(0), a.Skip(1).Select(Eval).ToList()));
            case "tolower": return AV.Str(S(0).ToLowerInvariant());
            case "toupper": return AV.Str(S(0).ToUpperInvariant());
            case "int": return AV.Num(Math.Truncate(N(0)));
            case "sqrt": return AV.Num(Math.Sqrt(N(0)));
            case "exp": return AV.Num(Math.Exp(N(0)));
            case "log": return AV.Num(Math.Log(N(0)));
            case "sin": return AV.Num(Math.Sin(N(0)));
            case "cos": return AV.Num(Math.Cos(N(0)));
            case "atan2": return AV.Num(Math.Atan2(N(0), N(1)));
            case "rand": return AV.Num(_random.NextDouble());
            case "srand": return AV.Num(0);
            case "fflush": return AV.Num(0);
            case "close":
            {
                var name = S(0);
                return AV.Num(_getlineFiles.Remove(_ctx.ResolvePath(name)) || _outputs.Any(o => o.Path == _ctx.ResolvePath(name)) ? 0 : -1);
            }
            case "system": throw Err("system() is not supported");
        }
        throw Err($"calling undefined function {c.Name}");
    }

    AV UserCall(UserCallN c)
    {
        if (!_functions.TryGetValue(c.Name, out var fn)) throw Err($"function `{c.Name}' not defined");
        if (c.Args.Count > fn.Params.Count) throw Err($"function `{c.Name}' called with more arguments than declared");
        if (_frames.Count >= 200) throw Err($"function `{c.Name}': call nesting too deep");
        var frame = new Frame(fn.Params);
        for (int i = 0; i < c.Args.Count; i++)
        {
            var (param, arg) = (fn.Params[i], c.Args[i]);
            if (arg is VarN v && IsArray(v.Name)) frame.Arrays[param] = Array(v.Name); // arrays are passed by reference
            else if (arg is VarN g && Local(g.Name) == null && !_vars.ContainsKey(g.Name) && g.Name != "NF") frame.Pending[param] = g.Name; // may become a global array
            else frame.Scalars[param] = Eval(arg);
        }
        _frames.Add(frame);
        try
        {
            Exec(fn.Body);
            return AV.Uninit;
        }
        catch (ReturnEx r) { return r.Value; }
        finally { _frames.RemoveAt(_frames.Count - 1); }
    }

    AV Getline(GetlineN g)
    {
        if (g.Cmd != null) throw Err("\"command\" | getline is not supported");
        string record;
        if (g.File != null)
        {
            var name = Eval(g.File).ToStr();
            var key = name is "-" or "/dev/stdin" ? "-" : _ctx.ResolvePath(name);
            if (!_getlineFiles.TryGetValue(key, out var it))
            {
                if (key != "-" && !_ctx.Fs.IsFile(key)) return AV.Num(-1);
                _getlineFiles[key] = it = Records(key == "-" ? _ctx.StdinText : _ctx.Fs.ReadAllText(key)).GetEnumerator();
            }
            if (!it.MoveNext()) return AV.Num(0);
            record = it.Current;
        }
        else
        {
            if (!_input.MoveNext()) return AV.Num(0);
            record = _input.Current;
            _vars["NR"] = AV.Num(_vars["NR"].ToNum() + 1);
            _vars["FNR"] = AV.Num(_vars["FNR"].ToNum() + 1);
        }
        if (g.Target == null) SetRecord(record);
        else Set(g.Target, AV.StrNum(record));
        return AV.Num(1);
    }

    // ---------- parser ----------

    enum TK { Num, Str, Regex, Name, Func, Call, Keyword, Op, Newline, EOF }
    readonly record struct Tok(TK Kind, string Text, double Num = 0);

    sealed class AwkParser
    {
        static readonly HashSet<string> Keywords = ["BEGIN", "END", "if", "else", "while", "for", "do", "break", "continue", "next", "nextfile", "exit", "delete", "in", "print", "printf", "getline", "function", "func", "return"];
        static readonly HashSet<string> Funcs = ["length", "substr", "index", "split", "sub", "gsub", "match", "sprintf", "tolower", "toupper", "int", "sqrt", "exp", "log", "sin", "cos", "atan2", "rand", "srand", "system", "close", "fflush"];
        static readonly string[] Ops = ["**=", "+=", "-=", "*=", "/=", "%=", "^=", "**", "==", "<=", ">=", "!=", "++", "--", "&&", "||", ">>", "!~", "{", "}", "(", ")", "[", "]", ";", ",", "+", "-", "*", "/", "%", "^", "!", ">", "<", "|", "?", ":", "~", "$", "="];
        static readonly HashSet<string> AssignOps = ["=", "+=", "-=", "*=", "/=", "%=", "^=", "**="];

        readonly List<Tok> _t;
        int _p;
        public readonly Dictionary<string, (List<string> Params, BlockS Body)> Functions = new(StringComparer.Ordinal);

        public AwkParser(string src) => _t = Lex(src);

        static AwkError Err(string m) => new($"syntax error: {m}");

        static List<Tok> Lex(string s)
        {
            var toks = new List<Tok>();
            bool RegexAllowed() => toks.Count == 0 || toks[^1] switch
            {
                { Kind: TK.Num or TK.Str or TK.Name or TK.Regex } => false,
                { Kind: TK.Func, Text: "length" } => false,
                { Kind: TK.Op, Text: ")" or "]" or "$" or "++" or "--" } => false,
                _ => true,
            };
            for (int i = 0; i < s.Length;)
            {
                char c = s[i];
                if (c is ' ' or '\t' or '\r') { i++; continue; }
                if (c == '\\' && i + 1 < s.Length && s[i + 1] == '\n') { i += 2; continue; }
                if (c == '#') { while (i < s.Length && s[i] != '\n') i++; continue; }
                if (c == '\n') { toks.Add(new(TK.Newline, "\n")); i++; continue; }
                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    var m = Regex.Match(s[i..], @"^(0[xX][0-9a-fA-F]+|\d*\.?\d*([eE][-+]?\d+)?)");
                    var txt = m.Value;
                    double v = txt.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(txt[2..], 16) : double.Parse(txt, NumberStyles.Float, CultureInfo.InvariantCulture);
                    toks.Add(new(TK.Num, txt, v));
                    i += txt.Length;
                    continue;
                }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    for (i++; i < s.Length && s[i] != '"'; i++)
                    {
                        if (s[i] == '\\' && i + 1 < s.Length)
                        {
                            char n = s[++i];
                            sb.Append(n switch { 'n' => "\n", 't' => "\t", 'r' => "\r", '\\' => "\\", '"' => "\"", '/' => "/", 'a' => "\a", 'b' => "\b", 'f' => "\f", 'v' => "\v", _ => "\\" + n });
                        }
                        else sb.Append(s[i]);
                    }
                    if (i >= s.Length) throw Err("non-terminated string");
                    i++;
                    toks.Add(new(TK.Str, sb.ToString()));
                    continue;
                }
                if (c == '/' && RegexAllowed())
                {
                    var sb = new StringBuilder();
                    bool inClass = false;
                    for (i++; i < s.Length && (s[i] != '/' || inClass); i++)
                    {
                        if (s[i] == '\n') throw Err("newline in regex");
                        if (s[i] == '\\' && i + 1 < s.Length)
                        {
                            if (s[i + 1] == '/') sb.Append('/'); else sb.Append(s[i]).Append(s[i + 1]);
                            i++;
                            continue;
                        }
                        if (s[i] == '[') inClass = true;
                        else if (s[i] == ']') inClass = false;
                        sb.Append(s[i]);
                    }
                    if (i >= s.Length) throw Err("non-terminated regular expression");
                    i++;
                    toks.Add(new(TK.Regex, sb.ToString()));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i;
                    while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                    var w = s[i..j];
                    var kind = Keywords.Contains(w) ? TK.Keyword : Funcs.Contains(w) ? TK.Func : TK.Name;
                    if (kind == TK.Name && j < s.Length && s[j] == '(') kind = TK.Call; // a user-defined function call: no space allowed before the (
                    toks.Add(new(kind, w));
                    i = j;
                    continue;
                }
                var op = Ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0) ?? throw Err($"unexpected character '{c}'");
                toks.Add(new(TK.Op, op));
                i += op.Length;
            }
            toks.Add(new(TK.EOF, ""));
            return toks;
        }

        Tok Peek => _t[_p];
        Tok PeekAt(int o) => _t[Math.Min(_p + o, _t.Count - 1)];
        bool IsOp(string o) => Peek.Kind == TK.Op && Peek.Text == o;
        bool IsKw(string k) => Peek.Kind == TK.Keyword && Peek.Text == k;
        bool Accept(string o) { if (!IsOp(o)) return false; _p++; return true; }
        void Expect(string o) { if (!Accept(o)) throw Err($"expected '{o}' near '{Peek.Text}'"); }
        void SkipNewlines() { while (Peek.Kind == TK.Newline) _p++; }
        void SkipTerminators() { while (Peek.Kind == TK.Newline || IsOp(";")) _p++; }

        public List<Rule> Parse()
        {
            var rules = new List<Rule>();
            SkipTerminators();
            while (Peek.Kind != TK.EOF)
            {
                var r = new Rule();
                if (IsKw("BEGIN") || IsKw("END")) { r.Kind = Peek.Text; _p++; SkipNewlines(); r.Action = Block(); }
                else if (IsKw("function") || IsKw("func"))
                {
                    _p++;
                    var name = Peek.Kind is TK.Name or TK.Call ? Peek.Text : throw Err("function name expected");
                    _p++;
                    Expect("(");
                    var parameters = new List<string>();
                    while (!IsOp(")"))
                    {
                        parameters.Add(Peek.Kind == TK.Name ? Peek.Text : throw Err("function parameter name expected"));
                        _p++;
                        if (!Accept(",")) break;
                        SkipNewlines();
                    }
                    Expect(")");
                    SkipNewlines();
                    Functions[name] = (parameters, Block());
                    SkipTerminators();
                    continue;
                }
                else
                {
                    if (!IsOp("{"))
                    {
                        r.Pattern = Expr(false);
                        if (Accept(",")) { SkipNewlines(); r.Pattern2 = Expr(false); }
                    }
                    if (IsOp("{")) r.Action = Block();
                }
                rules.Add(r);
                SkipTerminators();
            }
            return rules;
        }

        BlockS Block()
        {
            Expect("{");
            var body = new List<Stmt>();
            while (true)
            {
                SkipTerminators();
                if (Accept("}")) break;
                if (Peek.Kind == TK.EOF) throw Err("unexpected end of program, missing '}'");
                body.Add(Statement());
            }
            return new BlockS(body);
        }

        void EndSimple()
        {
            if (Peek.Kind == TK.Newline || IsOp(";")) { _p++; return; }
            if (IsOp("}") || Peek.Kind == TK.EOF) return;
            throw Err($"unexpected '{Peek.Text}'");
        }

        Stmt Statement()
        {
            if (IsOp("{")) return Block();
            if (IsOp(";")) { _p++; return new BlockS([]); }
            if (Peek.Kind == TK.Keyword)
            {
                switch (Peek.Text)
                {
                    case "if":
                    {
                        _p++;
                        Expect("(");
                        var c = Expr(false);
                        Expect(")");
                        SkipNewlines();
                        var then = Statement();
                        int save = _p;
                        SkipTerminators();
                        if (IsKw("else")) { _p++; SkipNewlines(); return new IfS(c, then, Statement()); }
                        _p = save;
                        return new IfS(c, then, null);
                    }
                    case "while":
                    {
                        _p++;
                        Expect("(");
                        var c = Expr(false);
                        Expect(")");
                        if (IsOp(";")) { _p++; return new WhileS(c, new BlockS([]), false); }
                        SkipNewlines();
                        return new WhileS(c, Statement(), false);
                    }
                    case "do":
                    {
                        _p++;
                        SkipNewlines();
                        var body = Statement();
                        SkipTerminators();
                        if (!IsKw("while")) throw Err("expected 'while' after 'do' body");
                        _p++;
                        Expect("(");
                        var c = Expr(false);
                        Expect(")");
                        EndSimple();
                        return new WhileS(c, body, true);
                    }
                    case "for":
                    {
                        _p++;
                        Expect("(");
                        if (Peek.Kind == TK.Name && PeekAt(1) is { Kind: TK.Keyword, Text: "in" } && PeekAt(2).Kind == TK.Name && PeekAt(3) is { Kind: TK.Op, Text: ")" })
                        {
                            var v = Peek.Text;
                            var arr = PeekAt(2).Text;
                            _p += 4;
                            SkipNewlines();
                            return new ForInS(v, arr, Statement());
                        }
                        Stmt? init = IsOp(";") ? null : new ExprS(Expr(false));
                        Expect(";");
                        SkipNewlines();
                        Node? cond = IsOp(";") ? null : Expr(false);
                        Expect(";");
                        SkipNewlines();
                        Stmt? step = IsOp(")") ? null : new ExprS(Expr(false));
                        Expect(")");
                        if (IsOp(";")) { _p++; return new ForS(init, cond, step, new BlockS([])); }
                        SkipNewlines();
                        return new ForS(init, cond, step, Statement());
                    }
                    case "next" or "break" or "continue" or "nextfile":
                    {
                        var k = Peek.Text == "nextfile" ? "next" : Peek.Text;
                        _p++;
                        EndSimple();
                        return new SimpleS(k, null);
                    }
                    case "exit":
                    {
                        _p++;
                        Node? arg = Peek.Kind is TK.Newline or TK.EOF || IsOp(";") || IsOp("}") ? null : Expr(false);
                        EndSimple();
                        return new SimpleS("exit", arg);
                    }
                    case "delete":
                    {
                        _p++;
                        var name = Peek.Kind == TK.Name ? Peek.Text : throw Err("delete: expected array name");
                        _p++;
                        List<Node>? keys = null;
                        if (Accept("[")) { keys = ExprList(false); Expect("]"); }
                        EndSimple();
                        return new DeleteS(name, keys);
                    }
                    case "print" or "printf":
                    {
                        bool printf = Peek.Text == "printf";
                        _p++;
                        List<Node> args = [];
                        if (IsOp("("))
                        {
                            int save = _p;
                            _p++;
                            var list = ExprList(false);
                            if (Accept(")") && (Peek.Kind is TK.Newline or TK.EOF || IsOp(";") || IsOp("}") || IsOp(">") || IsOp(">>") || IsOp("|"))) args = list;
                            else { _p = save; args = ExprList(true); }
                        }
                        else if (!(Peek.Kind is TK.Newline or TK.EOF || IsOp(";") || IsOp("}") || IsOp(">") || IsOp(">>") || IsOp("|"))) args = ExprList(true);
                        string? redir = null;
                        Node? dest = null;
                        if (IsOp(">") || IsOp(">>")) { redir = Peek.Text; _p++; dest = Concat(true); }
                        else if (IsOp("|")) throw new AwkError("output pipes (print | \"cmd\") are not supported");
                        EndSimple();
                        return new PrintS(printf, args, redir, dest);
                    }
                    case "return":
                    {
                        _p++;
                        Node? value = Peek.Kind is TK.Newline or TK.EOF || IsOp(";") || IsOp("}") ? null : Expr(false);
                        EndSimple();
                        return new ReturnS(value);
                    }
                }
            }
            var e = Expr(false);
            EndSimple();
            return new ExprS(e);
        }

        List<Node> ExprList(bool noGt)
        {
            var list = new List<Node> { Expr(noGt) };
            while (Accept(",")) { SkipNewlines(); list.Add(Expr(noGt)); }
            return list;
        }

        static bool IsLValue(Node n) => n is VarN or FieldN or IndexN;

        Node Expr(bool noGt)
        {
            var lhs = Ternary(noGt);
            if (Peek.Kind == TK.Op && AssignOps.Contains(Peek.Text) && IsLValue(lhs))
            {
                var op = Peek.Text;
                _p++;
                SkipNewlines();
                return new AssignN(lhs, op, Expr(noGt));
            }
            return lhs;
        }

        Node Ternary(bool noGt)
        {
            var c = Or(noGt);
            if (!Accept("?")) return c;
            SkipNewlines();
            var a = Expr(noGt);
            SkipNewlines();
            Expect(":");
            SkipNewlines();
            return new CondN(c, a, Expr(noGt));
        }

        Node Or(bool noGt)
        {
            var l = And(noGt);
            while (Accept("||")) { SkipNewlines(); l = new BinN("||", l, And(noGt)); }
            return l;
        }

        Node And(bool noGt)
        {
            var l = In(noGt);
            while (Accept("&&")) { SkipNewlines(); l = new BinN("&&", l, In(noGt)); }
            return l;
        }

        Node In(bool noGt)
        {
            var l = Match(noGt);
            while (IsKw("in"))
            {
                _p++;
                var name = Peek.Kind == TK.Name ? Peek.Text : throw Err("expected array name after 'in'");
                _p++;
                l = new InN([l], name);
            }
            return l;
        }

        Node Match(bool noGt)
        {
            var l = Comparison(noGt);
            while (IsOp("~") || IsOp("!~"))
            {
                bool neg = Peek.Text == "!~";
                _p++;
                l = new MatchN(l, Comparison(noGt), neg);
            }
            return l;
        }

        Node Comparison(bool noGt)
        {
            var l = Concat(noGt);
            while (IsOp("|") && PeekAt(1) is { Kind: TK.Keyword, Text: "getline" })
            {
                _p += 2;
                l = new GetlineN(GetlineTarget(), null, l);
            }
            if (Peek.Kind == TK.Op && Peek.Text is "<" or "<=" or "==" or "!=" or ">=" || (IsOp(">") && !noGt))
            {
                var op = Peek.Text;
                _p++;
                return new BinN(op, l, Concat(noGt));
            }
            return l;
        }

        bool StartsConcat() => Peek.Kind is TK.Num or TK.Str or TK.Name or TK.Func or TK.Call or TK.Regex || IsOp("$") || IsOp("(");

        /// <summary>The optional variable after getline.</summary>
        Node? GetlineTarget()
        {
            if (Peek.Kind != TK.Name && !IsOp("$")) return null;
            var target = Primary();
            return IsLValue(target) ? target : throw Err("getline: expected a variable");
        }

        Node Concat(bool noGt)
        {
            var l = Additive();
            while (StartsConcat()) l = new ConcatN(l, Additive());
            return l;
        }

        Node Additive()
        {
            var l = Multiplicative();
            while (IsOp("+") || IsOp("-")) { var op = Peek.Text; _p++; l = new BinN(op, l, Multiplicative()); }
            return l;
        }

        Node Multiplicative()
        {
            var l = Unary();
            while (IsOp("*") || IsOp("/") || IsOp("%")) { var op = Peek.Text; _p++; l = new BinN(op, l, Unary()); }
            return l;
        }

        Node Unary()
        {
            if (IsOp("!") || IsOp("-") || IsOp("+")) { var op = Peek.Text; _p++; return new UnN(op, Unary()); }
            return Power();
        }

        Node Power()
        {
            var b = Postfix();
            if (IsOp("^") || IsOp("**")) { _p++; return new BinN("^", b, Unary()); }
            return b;
        }

        Node Postfix()
        {
            var e = Prefix();
            if (IsLValue(e) && (IsOp("++") || IsOp("--"))) { var inc = Peek.Text == "++"; _p++; return new IncrN(e, inc, false); }
            return e;
        }

        Node Prefix()
        {
            if (IsOp("++") || IsOp("--"))
            {
                var inc = Peek.Text == "++";
                _p++;
                var t = Prefix();
                if (!IsLValue(t)) throw Err("++ or -- needs a variable");
                return new IncrN(t, inc, true);
            }
            return Primary();
        }

        Node Primary()
        {
            var t = Peek;
            switch (t.Kind)
            {
                case TK.Num: _p++; return new NumN(t.Num);
                case TK.Str: _p++; return new StrN(t.Text);
                case TK.Regex: _p++; return new RegexN(t.Text);
                case TK.Op when t.Text == "$":
                    _p++;
                    return new FieldN(IsOp("++") || IsOp("--") ? Prefix() : Primary());
                case TK.Op when t.Text == "(":
                {
                    _p++;
                    var list = ExprList(false);
                    Expect(")");
                    if (list.Count == 1) return list[0];
                    if (!IsKw("in")) throw Err("expected 'in' after (a, b)");
                    _p++;
                    var name = Peek.Kind == TK.Name ? Peek.Text : throw Err("expected array name");
                    _p++;
                    return new InN(list, name);
                }
                case TK.Op when t.Text is "-" or "+" or "!":
                    return Unary();
                case TK.Func:
                {
                    _p++;
                    if (!Accept("("))
                    {
                        if (t.Text == "length") return new CallN("length", []);
                        throw Err($"expected '(' after {t.Text}");
                    }
                    var args = IsOp(")") ? [] : ExprList(false);
                    Expect(")");
                    return new CallN(t.Text, args);
                }
                case TK.Name:
                {
                    _p++;
                    if (Accept("["))
                    {
                        var keys = ExprList(false);
                        Expect("]");
                        return new IndexN(t.Text, keys);
                    }
                    return new VarN(t.Text);
                }
                case TK.Keyword when t.Text == "getline":
                {
                    _p++;
                    var target = GetlineTarget();
                    Node? file = Accept("<") ? Primary() : null;
                    return new GetlineN(target, file, null);
                }
                case TK.Call:
                {
                    _p++;
                    Expect("(");
                    var args = IsOp(")") ? [] : ExprList(false);
                    Expect(")");
                    return new UserCallN(t.Text, args);
                }
            }
            throw Err(t.Kind == TK.EOF ? "unexpected end of program" : $"unexpected '{t.Text}'");
        }
    }
}
