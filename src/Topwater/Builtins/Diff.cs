// diff: the line diff engine and its output formats (the command is registered with the text commands). See docs/architecture.md.

namespace Topwater;

internal static class Diff
{
    enum Op { Equal, Delete, Insert }

    public static int Run(CommandContext ctx)
    {
        var a = Args.Parse(ctx.Args, CommandSpec.Of("diff"));
        if (a.Positional.Count != 2) throw new UsageException(a.Positional.Count < 2 ? CommandSpec.Message("diff", "missingOperandAfter", ("operand", a.Positional.LastOrDefault() ?? "diff")) : CommandSpec.Message("diff", "extraOperand", ("operand", a.Positional[2])));
        var (an, bn) = (a.Positional[0], a.Positional[1]);
        foreach (var p in new[] { an, bn })
            if (p != "-" && !a.Has('N') && !ctx.Fs.Exists(ctx.ResolvePath(p))) throw new ShellException(CommandSpec.Message("diff", "fileNotFound", ("file", p)), 2);
        bool IsDir(string p) => p != "-" && ctx.Fs.IsDirectory(ctx.ResolvePath(p));
        if (IsDir(an) && IsDir(bn)) return Dirs(ctx, a, an, bn, 0);
        // a directory and a file: compare the file with the one of the same name in the directory
        if (IsDir(an)) an = Join(an, VirtualFileSystem.GetName(bn));
        else if (IsDir(bn)) bn = Join(bn, VirtualFileSystem.GetName(an));
        return Files(ctx, a, an, bn, null);
    }

    static string Join(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    /// <summary>Two directories: entries only one side has, then each common one in name order (subdirectories with -r).</summary>
    static int Dirs(CommandContext ctx, Args a, string an, string bn, int depth)
    {
        if (depth > 64) throw new ShellException(CommandSpec.Message("diff", "tooLarge"), 2); // shortcut: a symlink loop ends here, not with GNU's loop message
        var fs = ctx.Fs;
        string pa = ctx.ResolvePath(an), pb = ctx.ResolvePath(bn);
        var left = fs.List(pa).ToHashSet();
        var right = fs.List(pb).ToHashSet();
        int code = 0;
        foreach (var name in left.Union(right).Order(StringComparer.Ordinal))
        {
            string x = Join(an, name), y = Join(bn, name);
            bool inA = left.Contains(name), inB = right.Contains(name);
            bool dirA = inA && fs.IsDirectory(Join(pa, name)), dirB = inB && fs.IsDirectory(Join(pb, name));
            int rc;
            if (!inA || !inB)
            {
                if (a.Has('N') && !(dirA || dirB)) rc = Files(ctx, a, x, y, Header(ctx, a, x, y));
                else { ctx.Stdout.WriteLine(CommandSpec.Message("diff", "onlyIn", ("dir", inA ? an : bn), ("name", name))); rc = 1; }
            }
            else if (dirA && dirB)
            {
                if (a.Has('r')) rc = Dirs(ctx, a, x, y, depth + 1);
                else { ctx.Stdout.WriteLine(CommandSpec.Message("diff", "commonSubdirectories", ("file1", x), ("file2", y))); rc = 0; }
            }
            else if (dirA || dirB)
            {
                ctx.Stdout.WriteLine(CommandSpec.Message("diff", "typeMismatch", ("file1", x), ("kind1", dirA ? "directory" : "regular file"), ("file2", y), ("kind2", dirB ? "directory" : "regular file")));
                rc = 1;
            }
            else rc = Files(ctx, a, x, y, Header(ctx, a, x, y));
            code = Math.Max(code, rc);
        }
        return code;
    }

    /// <summary>The line GNU diff prints before each differing pair in a directory: the options as typed, then both names.</summary>
    static string Header(CommandContext ctx, Args a, string x, string y)
    {
        var options = ctx.Args.ToList();
        foreach (var operand in a.Positional) options.Remove(operand);
        return string.Join(' ', ["diff", .. options, x, y]);
    }

    static int Files(CommandContext ctx, Args a, string an, string bn, string? header)
    {
        string Read(string p)
        {
            if (p == "-") return ctx.StdinText;
            var abs = ctx.ResolvePath(p);
            if (!ctx.Fs.Exists(abs))
            {
                if (a.Has('N')) return "";
                throw new ShellException(CommandSpec.Message("diff", "fileNotFound", ("file", p)), 2);
            }
            return ctx.Fs.ReadAllText(abs);
        }
        var x = Builtins.SplitLines(Read(an));
        var y = Builtins.SplitLines(Read(bn));
        string Key(string l)
        {
            if (a.Has('w')) l = string.Concat(l.Where(c => !char.IsWhiteSpace(c)));
            else if (a.Has('b')) l = System.Text.RegularExpressions.Regex.Replace(l.TrimEnd(), @"\s+", " ");
            return a.Has('i') ? l.ToLowerInvariant() : l;
        }
        var ops = Compute(x.Select(Key).ToList(), y.Select(Key).ToList());
        if (ops.All(o => o.Op == Op.Equal))
        {
            if (a.Has('s')) ctx.Stdout.WriteLine($"Files {an} and {bn} are identical");
            return 0;
        }
        if (a.Has('q')) { ctx.Stdout.WriteLine($"Files {an} and {bn} differ"); return 1; }
        if (header != null) ctx.Stdout.WriteLine(header);
        if (a.Has('u') || a.Value('U') != null) Unified(ctx.Stdout, ops, x, y, an, bn, int.Parse(a.Value('U') ?? "3"));
        else Normal(ctx.Stdout, ops, x, y);
        return 1;
    }

    /// <summary>LCS diff. shortcut: O(n*m) memory; switch to Myers if agents diff large files.</summary>
    static List<(Op Op, int A, int B)> Compute(List<string> x, List<string> y)
    {
        if ((long)x.Count * y.Count > 25_000_000) throw new ShellException(CommandSpec.Message("diff", "tooLarge"), 2);
        var lcs = new int[x.Count + 1, y.Count + 1];
        for (int i = x.Count - 1; i >= 0; i--)
            for (int j = y.Count - 1; j >= 0; j--)
                lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var ops = new List<(Op, int, int)>();
        int a = 0, b = 0;
        while (a < x.Count || b < y.Count)
        {
            if (a < x.Count && b < y.Count && x[a] == y[b]) ops.Add((Op.Equal, a++, b++));
            else if (b < y.Count && (a == x.Count || lcs[a, b + 1] >= lcs[a + 1, b])) ops.Add((Op.Insert, a, b++));
            else ops.Add((Op.Delete, a++, b));
        }
        // emit deletions before insertions inside each change block, like GNU diff
        for (int i = 0; i < ops.Count;)
        {
            if (ops[i].Item1 == Op.Equal) { i++; continue; }
            int j = i;
            while (j < ops.Count && ops[j].Item1 != Op.Equal) j++;
            var block = ops.GetRange(i, j - i).OrderBy(o => o.Item1 == Op.Delete ? 0 : 1).ToList();
            for (int k = 0; k < block.Count; k++) ops[i + k] = block[k];
            i = j;
        }
        return ops;
    }

    static string Range(int start, int count) => count == 1 ? $"{start}" : $"{start},{start + count - 1}";

    static void Normal(TextWriter w, List<(Op Op, int A, int B)> ops, List<string> x, List<string> y)
    {
        for (int i = 0; i < ops.Count;)
        {
            if (ops[i].Op == Op.Equal) { i++; continue; }
            int j = i;
            while (j < ops.Count && ops[j].Op != Op.Equal) j++;
            var dels = ops.GetRange(i, j - i).Where(o => o.Op == Op.Delete).ToList();
            var ins = ops.GetRange(i, j - i).Where(o => o.Op == Op.Insert).ToList();
            int aStart = ops.GetRange(i, j - i).Min(o => o.A), bStart = ops.GetRange(i, j - i).Min(o => o.B);
            if (dels.Count > 0 && ins.Count > 0) w.WriteLine($"{Range(aStart + 1, dels.Count)}c{Range(bStart + 1, ins.Count)}");
            else if (dels.Count > 0) w.WriteLine($"{Range(aStart + 1, dels.Count)}d{bStart}");
            else w.WriteLine($"{aStart}a{Range(bStart + 1, ins.Count)}");
            foreach (var d in dels) w.WriteLine("< " + x[d.A]);
            if (dels.Count > 0 && ins.Count > 0) w.WriteLine("---");
            foreach (var n in ins) w.WriteLine("> " + y[n.B]);
            i = j;
        }
    }

    static void Unified(TextWriter w, List<(Op Op, int A, int B)> ops, List<string> x, List<string> y, string an, string bn, int context)
    {
        w.WriteLine($"--- {an}");
        w.WriteLine($"+++ {bn}");
        var changes = Enumerable.Range(0, ops.Count).Where(i => ops[i].Op != Op.Equal).ToList();
        for (int c = 0; c < changes.Count;)
        {
            int first = changes[c], last = first;
            while (c + 1 < changes.Count && changes[c + 1] - last <= 2 * context + 1) last = changes[++c];
            c++;
            int start = Math.Max(0, first - context), end = Math.Min(ops.Count, last + context + 1);
            var hunk = ops.GetRange(start, end - start);
            int aLen = hunk.Count(o => o.Op != Op.Insert), bLen = hunk.Count(o => o.Op != Op.Delete);
            int aStart = hunk.Min(o => o.A) + (aLen > 0 ? 1 : 0), bStart = hunk.Min(o => o.B) + (bLen > 0 ? 1 : 0);
            static string Range(int start, int len) => len == 1 ? $"{start}" : $"{start},{len}"; // GNU leaves out a count of 1
            w.WriteLine($"@@ -{Range(aStart, aLen)} +{Range(bStart, bLen)} @@");
            foreach (var o in hunk)
                w.WriteLine(o.Op switch { Op.Equal => " " + x[o.A], Op.Delete => "-" + x[o.A], _ => "+" + y[o.B] });
        }
    }
}
