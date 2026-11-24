using System.Globalization;
using System.Text.RegularExpressions;

namespace Topwater;

/// <summary>find with real expressions: ( ) ! -not -a -o, tests, and actions (-print -print0 -exec -delete).</summary>
internal sealed class Find
{
    sealed record Entry(string Abs, string Name, string Display, int Depth, string Start);
    sealed class QuitSignal : Exception;
    delegate Task<bool> Pred(Entry e);

    readonly CommandContext _ctx;
    List<string> _args = [];
    int _p, _maxDepth = int.MaxValue, _minDepth;
    bool _hasAction, _prune;
    readonly List<string> _deletes = [];
    readonly List<(List<string> Cmd, List<string> Paths)> _batches = [];

    Find(CommandContext ctx) => _ctx = ctx;

    public static Task<int> RunAsync(CommandContext ctx) => new Find(ctx).Run();

    async Task<int> Run()
    {
        var starts = _ctx.Args.TakeWhile(x => !x.StartsWith('-') && x != "!" && x != "(").ToList();
        _args = _ctx.Args.Skip(starts.Count).ToList();
        if (starts.Count == 0) starts.Add(".");
        Pred expr = _args.Count == 0 ? _ => Task.FromResult(true) : ParseOr();
        if (_p < _args.Count) throw new ShellException($"unexpected `{_args[_p]}'");

        int code = 0;
        async Task Walk(string abs, string display, int depth, string start)
        {
            _prune = false;
            if (depth >= _minDepth && await expr(new Entry(abs, VirtualFileSystem.GetName(abs), display, depth, start)) && !_hasAction)
                _ctx.Stdout.WriteLine(display);
            if (_prune || depth >= _maxDepth || !_ctx.Fs.IsDirectory(abs)) return;
            IReadOnlyList<string> names;
            try { names = _ctx.Fs.List(abs); }
            catch (ShellException) { _ctx.Stderr.WriteLine($"find: '{display}': Permission denied"); code = 1; return; }
            foreach (var name in names)
                await Walk(VirtualFileSystem.Normalize(abs, name), display.EndsWith('/') ? display + name : display + "/" + name, depth + 1, start);
        }
        try
        {
            foreach (var s in starts)
            {
                var abs = _ctx.ResolvePath(s);
                if (!_ctx.Fs.Exists(abs)) { _ctx.Stderr.WriteLine($"find: '{s}': No such file or directory"); code = 1; continue; }
                await Walk(abs, s, 0, s);
            }
        }
        catch (QuitSignal) { }
        foreach (var (cmd, paths) in _batches)
            if (paths.Count > 0 && await _ctx.InvokeAsync(cmd.SelectMany(x => x == "{}" ? paths : [x]).ToList()) != 0) code = 1;
        foreach (var path in _deletes.OrderByDescending(p => p.Length))
            if (_ctx.Fs.Exists(path)) _ctx.Fs.Delete(path, recursive: true);
        return code;
    }

    bool Is(string s) => _p < _args.Count && _args[_p] == s;

    Pred ParseOr()
    {
        var l = ParseAnd();
        while (Is("-o") || Is("-or"))
        {
            _p++;
            var left = l;
            var right = ParseAnd();
            l = async e => await left(e) || await right(e);
        }
        return l;
    }

    Pred ParseAnd()
    {
        var l = ParseNot();
        while (_p < _args.Count && !Is("-o") && !Is("-or") && !Is(")"))
        {
            if (Is("-a") || Is("-and")) _p++;
            var left = l;
            var right = ParseNot();
            l = async e => await left(e) && await right(e);
        }
        return l;
    }

    Pred ParseNot()
    {
        if (Is("!") || Is("-not"))
        {
            _p++;
            var inner = ParseNot();
            return async e => !await inner(e);
        }
        if (Is("("))
        {
            _p++;
            var inner = ParseOr();
            if (!Is(")")) throw new ShellException("invalid expression; expected `)'");
            _p++;
            return inner;
        }
        return Primary();
    }

    static Task<bool> T(bool b) => Task.FromResult(b);

    static readonly Dictionary<char, string> TimeFormats = new()
    {
        ['Y'] = "yyyy", ['m'] = "MM", ['d'] = "dd", ['H'] = "HH", ['M'] = "mm", ['S'] = "ss'.0000000000'", ['T'] = "HH:mm:ss'.0000000000'",
        ['F'] = "yyyy-MM-dd", ['+'] = "yyyy-MM-dd'+'HH:mm:ss'.0000000000'", ['a'] = "ddd", ['b'] = "MMM", ['y'] = "yy", ['D'] = "MM/dd/yy", ['R'] = "HH:mm",
    };

    static readonly Regex PrintfSpec = new(@"\G%([-+ #0]*)(\d*)(?:\.(\d+))?");

    /// <summary>find -printf: %p %f %h %P %s %d %y %m %M %u %g %k %t %T@ %Tk ... with optional width, plus \n \t \0 escapes.</summary>
    string Printf(string fmt, Entry e)
    {
        var st = _ctx.Fs.Stat(e.Abs);
        var inv = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        int i = 0, n = fmt.Length;
        while (i < n)
        {
            char c = fmt[i];
            if (c == '\\' && i + 1 < n)
            {
                sb.Append(fmt[i + 1] switch { 'n' => "\n", 't' => "\t", '0' => "\0", '\\' => "\\", 'r' => "\r", var o => "\\" + o });
                i += 2;
                continue;
            }
            if (c != '%' || i + 1 >= n) { sb.Append(c); i++; continue; }
            var m = PrintfSpec.Match(fmt, i);
            int j = i + m.Length;
            if (j >= n) { sb.Append(fmt[i..]); break; }
            char d = fmt[j++];
            if (d == '%') { sb.Append('%'); i = j; continue; }
            string value;
            if (d is 'T' or 'A' or 'C')
            {
                char k = j < n ? fmt[j] : '\0';
                j++;
                value = k == '@' ? (new DateTimeOffset(st.Modified).ToUnixTimeMilliseconds() / 1000.0).ToString("F10", inv)
                    : TimeFormats.TryGetValue(k, out var tf) ? st.Modified.ToString(tf, inv) : "%" + d + k;
            }
            else if (d == 't') value = st.Modified.ToString("ddd MMM dd HH:mm:ss'.0000000000' yyyy", inv);
            else
            {
                var trimmed = e.Display.TrimEnd('/');
                var baseName = trimmed.Length == 0 ? "/" : trimmed[(trimmed.LastIndexOf('/') + 1)..];
                value = d switch
                {
                    'p' => e.Display,
                    'f' => e.Depth == 0 ? baseName : e.Name,
                    'h' => trimmed.Contains('/') ? (e.Display[..e.Display.LastIndexOf('/')] is var h && h.Length > 0 ? h : "/") : ".",
                    'P' => e.Depth > 0 ? e.Display[e.Start.Length..].TrimStart('/') : "",
                    'H' => e.Start,
                    's' => st.Size.ToString(inv),
                    'd' => e.Depth.ToString(inv),
                    'y' or 'Y' => st.IsDirectory ? "d" : "f",
                    'm' => Convert.ToString(st.Mode & 0xFFF, 8),
                    'M' => VirtualFileSystem.FormatMode(st.IsDirectory, st.Mode),
                    'u' or 'g' => st.Owner,
                    'n' => "1",
                    'k' => ((st.Size + 4095) / 4096 * 4).ToString(inv),
                    'b' => ((st.Size + 4095) / 4096 * 8).ToString(inv),
                    _ => "%" + d,
                };
            }
            int width = m.Groups[2].Length > 0 ? int.Parse(m.Groups[2].Value, inv) : 0;
            sb.Append(m.Groups[1].Value.Contains('-') ? value.PadRight(width) : value.PadLeft(width));
            i = j;
        }
        return sb.ToString();
    }

    Pred Primary()
    {
        var tok = _args[_p++];
        string Arg() => _p < _args.Count ? _args[_p++] : throw new ShellException($"missing argument to `{tok}'");
        var fs = _ctx.Fs;
        switch (tok)
        {
            case "-name" or "-iname":
            {
                var re = Glob.ToRegex(Arg(), ignoreCase: tok == "-iname");
                return e => T(re.IsMatch(e.Name));
            }
            case "-path" or "-ipath" or "-wholename":
            {
                var re = Glob.ToRegex(Arg(), ignoreCase: tok == "-ipath", slashes: true);
                return e => T(re.IsMatch(e.Display));
            }
            case "-regex":
            {
                var re = new Regex("^(?:" + Arg() + ")$", RegexOptions.None, Shell.RegexTimeout);
                return e => T(re.IsMatch(e.Display));
            }
            case "-type":
            {
                var t = Arg();
                if (t is not ("f" or "d" or "l")) throw new ShellException($"Unknown argument to -type: {t}");
                return e => T(t != "l" && fs.IsDirectory(e.Abs) == (t == "d"));
            }
            case "-maxdepth": _maxDepth = int.Parse(Arg()); return _ => T(true);
            case "-mindepth": _minDepth = int.Parse(Arg()); return _ => T(true);
            case "-mmin" or "-mtime":
            {
                var spec = Arg();
                double unit = tok == "-mmin" ? 1 : 1440, n = double.Parse(spec.TrimStart('+', '-'), CultureInfo.InvariantCulture);
                return e =>
                {
                    var age = (DateTime.Now - fs.Stat(e.Abs).Modified).TotalMinutes / unit;
                    return T(spec[0] == '-' ? age < n : spec[0] == '+' ? age > n : Math.Floor(age) == n);
                };
            }
            case "-newer":
            {
                var refTime = fs.Stat(_ctx.ResolvePath(Arg())).Modified;
                return e => T(fs.Stat(e.Abs).Modified > refTime);
            }
            case "-size":
            {
                var m = Regex.Match(Arg(), @"^([+-]?)(\d+)([cwbkMG]?)$");
                if (!m.Success) throw new ShellException("invalid -size argument");
                long unit = m.Groups[3].Value switch { "c" => 1, "w" => 2, "k" => 1024, "M" => 1 << 20, "G" => 1 << 30, _ => 512 };
                long n = long.Parse(m.Groups[2].Value);
                return e =>
                {
                    if (fs.IsDirectory(e.Abs)) return T(false);
                    long size = (fs.Stat(e.Abs).Size + unit - 1) / unit;
                    return T(m.Groups[1].Value == "+" ? size > n : m.Groups[1].Value == "-" ? size < n : size == n);
                };
            }
            case "-empty": return e => T(fs.IsDirectory(e.Abs) ? fs.List(e.Abs).Count == 0 : fs.Stat(e.Abs).Size == 0);
            case "-true": return _ => T(true);
            case "-false": return _ => T(false);
            case "-print":
                _hasAction = true;
                return e => { _ctx.Stdout.WriteLine(e.Display); return T(true); };
            case "-print0":
                _hasAction = true;
                return e => { _ctx.Stdout.Write(e.Display + "\0"); return T(true); };
            case "-printf":
            {
                _hasAction = true;
                var fmt = Arg();
                return e => { _ctx.Stdout.Write(Printf(fmt, e)); return T(true); };
            }
            case "-quit": return _ => throw new QuitSignal();
            case "-prune": return _ => { _prune = true; return T(true); };
            case "-delete":
                _hasAction = true;
                return e => { _deletes.Add(e.Abs); return T(true); };
            case "-exec" or "-execdir" or "-ok":
            {
                _hasAction = true;
                var cmd = new List<string>();
                while (_p < _args.Count && _args[_p] != ";" && !(_args[_p] == "+" && cmd.Count > 0 && cmd[^1] == "{}")) cmd.Add(_args[_p++]);
                if (_p >= _args.Count) throw new ShellException($"missing argument to `{tok}'");
                if (cmd.Count == 0) throw new ShellException($"invalid argument `{_args[_p]}' to `{tok}'");
                if (_args[_p++] == "+")
                {
                    var batch = (cmd, new List<string>());
                    _batches.Add(batch);
                    return e => { batch.Item2.Add(e.Display); return T(true); };
                }
                return async e => await _ctx.InvokeAsync(cmd.Select(x => x.Replace("{}", e.Display)).ToList()) == 0;
            }
            default:
                throw new ShellException($"unknown predicate `{tok}' (supported: -name -iname -path -regex -type -size -mmin -mtime -newer -empty " +
                                         "-maxdepth -mindepth -prune -print -print0 -printf -exec -delete -quit ! -not -a -o ( ))");
        }
    }
}
