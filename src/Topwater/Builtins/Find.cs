using System.Globalization;
using System.Text.RegularExpressions;

namespace Topwater;

/// <summary>find with real expressions: ( ) ! -not -a -o, tests, and actions (-print -print0 -exec -delete).</summary>
internal sealed class Find
{
    sealed record Entry(string Abs, string Name, string Display);
    delegate Task<bool> Pred(Entry e);

    readonly CommandContext _ctx;
    List<string> _args = [];
    int _p, _maxDepth = int.MaxValue, _minDepth;
    bool _hasAction;
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
        async Task Walk(string abs, string display, int depth)
        {
            if (depth >= _minDepth && await expr(new Entry(abs, VirtualFileSystem.GetName(abs), display)) && !_hasAction)
                _ctx.Stdout.WriteLine(display);
            if (depth >= _maxDepth || !_ctx.Fs.IsDirectory(abs)) return;
            foreach (var name in _ctx.Fs.List(abs))
                await Walk(VirtualFileSystem.Normalize(abs, name), display.EndsWith('/') ? display + name : display + "/" + name, depth + 1);
        }
        foreach (var s in starts)
        {
            var abs = _ctx.ResolvePath(s);
            if (!_ctx.Fs.Exists(abs)) { _ctx.Stderr.WriteLine($"find: '{s}': No such file or directory"); code = 1; continue; }
            await Walk(abs, s, 0);
        }
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
                var re = new Regex("^(?:" + Arg() + ")$");
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
                                         "-maxdepth -mindepth -print -print0 -exec -delete ! -not -a -o ( ))");
        }
    }
}
