using System.Globalization;
using System.Text;

namespace Topwater;

internal static partial class Builtins
{
    /// <summary>A filesystem error "/abs/dir/x: reason" as ("arg/x", reason): the path shown the way the user typed it.</summary>
    static (string Path, string Reason) Typed(ShellException e, string arg, string abs)
    {
        int i = e.Message.LastIndexOf(": ", StringComparison.Ordinal);
        var path = i < 0 ? abs : e.Message[..i];
        return (path == abs ? arg : path.StartsWith(abs.TrimEnd('/') + "/") ? arg.TrimEnd('/') + path[abs.TrimEnd('/').Length..] : path, e.Message[(i + 2)..]);
    }

    /// <summary>touch -t [[CC]YY]MMDDhhmm[.ss]; a two-digit year 69-99 is 19YY, else 20YY.</summary>
    static DateTime? TouchStamp(string t, DateTime now)
    {
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^(\d{2}|\d{4})?(\d{2})(\d{2})(\d{2})(\d{2})(?:\.(\d{2}))?$");
        if (!m.Success) return null;
        var y = m.Groups[1].Value;
        int year = y.Length == 0 ? now.Year : y.Length == 4 ? int.Parse(y) : int.Parse(y) + (int.Parse(y) >= 69 ? 1900 : 2000);
        try
        {
            return new DateTime(year, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value),
                m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : 0, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>ls -h: powers of 1024, rounded up; one decimal below 10 (4.0K, 1.1M), whole numbers above (121K).</summary>
    static string HumanSize(long n)
    {
        if (n < 1024) return n.ToString(CultureInfo.InvariantCulture);
        double v = n;
        int u = -1;
        do { v /= 1024; u++; } while (v >= 1024 && u < 5);
        if (Math.Ceiling(v * 10) / 10 is var tenths and < 10) return tenths.ToString("0.0", CultureInfo.InvariantCulture) + "KMGTPE"[u];
        v = Math.Ceiling(v);
        return v >= 1024 && u < 5 ? "1.0" + "KMGTPE"[u + 1] : v.ToString("0", CultureInfo.InvariantCulture) + "KMGTPE"[u];
    }

    /// <summary>ls -l's date: "Nov 25 16:52" within the last six months, else "Jan  2  2020" (also for future times);
    /// or in a --time-style: full-iso, long-iso, iso, or +FORMAT (FORMAT for old times, NEWLINE FORMAT for recent ones).</summary>
    static string LsDate(DateTime t, DateTime now, string? style = null)
    {
        bool recent = t <= now && (now - t).TotalSeconds < 15778476;
        return style switch
        {
            "full-iso" => Strftime(t, "%Y-%m-%d %H:%M:%S.%N %z"),
            "long-iso" => Strftime(t, "%Y-%m-%d %H:%M"),
            "iso" => Strftime(t, recent ? "%m-%d %H:%M" : "%Y-%m-%d "),
            ['+', ..] => style[1..].Split('\n', 2) is var f && recent && f.Length > 1 ? Strftime(t, f[1]) : Strftime(t, f[0]),
            _ => string.Format(CultureInfo.InvariantCulture, recent ? "{0:MMM} {1,2} {0:HH:mm}" : "{0:MMM} {1,2}  {0:yyyy}", t, t.Day),
        };
    }

    static readonly string[] TimeStyles = ["full-iso", "long-iso", "iso", "locale", "posix-full-iso", "posix-long-iso", "posix-iso", "posix-locale"];

    /// <summary>ls --time-style: a style name, a unique abbreviation of one, or +FORMAT; posix- styles act as the plain
    /// ones, since the locale is not POSIX.</summary>
    static string TimeStyle(string arg)
    {
        if (arg.StartsWith('+')) return arg;
        var hits = TimeStyles.Contains(arg) ? [arg] : TimeStyles.Where(s => s.StartsWith(arg, StringComparison.Ordinal)).ToArray();
        if (hits.Length != 1)
            throw new ShellException($"{(hits.Length == 0 ? "invalid" : "ambiguous")} argument {QuoteL(arg)} for ‘time style’\nValid arguments are:\n"
                + "  - [posix-]full-iso\n  - [posix-]long-iso\n  - [posix-]iso\n  - [posix-]locale\n"
                + "  - +FORMAT (e.g., +%H:%M) for a 'date'-style format\nTry 'ls --help' for more information.", 2);
        return hits[0].StartsWith("posix-", StringComparison.Ordinal) ? hits[0][6..] : hits[0];
    }

    /// <summary>ls -s: the space a file takes, in KiB (or -h units).</summary>
    static string BlockSize(FileStat st, bool human) => human ? HumanSize(st.Blocks * 512) : (st.Blocks / 2).ToString(CultureInfo.InvariantCulture);

    /// <summary>ls -l rows, each column as wide as its widest entry (owners left-aligned, numbers right-aligned).</summary>
    /// <param name="widthFrom">More entries that count toward the widths: ls sizes the columns for its file operands
    /// over all operands, directories included.</param>
    static IEnumerable<string> LongEntries(CommandContext ctx, IEnumerable<(string Abs, string Name)> entries, bool human, IEnumerable<string>? widthFrom = null,
        string? style = null, bool blocks = false, bool follow = false, Func<FileStat, string>? classify = null)
    {
        var now = ctx.Fs.Clock();
        string Size(FileStat st) => human ? HumanSize(st.Size) : st.Size.ToString(CultureInfo.InvariantCulture);
        // Broken: -L meets a link leading nowhere, shown as a row of question marks
        var rows = entries.Select(e => (Broken: follow && !ctx.Fs.Exists(e.Abs), e.Name, e.Abs))
            .Select(e => (St: ctx.Fs.Stat(e.Abs, follow && !e.Broken), e.Broken, e.Name, e.Abs)).Select(r => (r.St, r.Broken, r.Name, r.Abs,
            Links: r.St.Links.ToString(CultureInfo.InvariantCulture), Size: Size(r.St))).ToList();
        if (rows.Count == 0) yield break;
        var all = rows.Where(r => !r.Broken).Select(r => r.St).Concat((widthFrom ?? []).Select(p => ctx.Fs.Stat(p, follow))).ToList();
        int Width(Func<FileStat, string> f) => all.Select(s => f(s).Length).DefaultIfEmpty(1).Max();
        int lw = Width(s => s.Links.ToString(CultureInfo.InvariantCulture)), ow = Width(s => s.Owner), sw = Width(Size), bw = Width(s => BlockSize(s, human));
        int dw = Width(s => LsDate(s.Modified, now, style));
        foreach (var r in rows)
        {
            if (r.Broken)
            {
                yield return (blocks ? "?".PadLeft(bw) + " " : "") + $"l????????? {"?".PadLeft(lw)} {"?".PadRight(ow)} {"?".PadRight(ow)} {"?".PadLeft(sw)} {"?".PadLeft(dw)} {r.Name}";
                continue;
            }
            // a link shows where it points; -F marks that target's type
            var name = r.St.LinkTarget is { } t ? r.Name + " -> " + t + (classify != null && ctx.Fs.Exists(r.Abs) ? classify(ctx.Fs.Stat(r.Abs)) : "")
                : r.Name + (classify?.Invoke(r.St) ?? "");
            yield return (blocks ? BlockSize(r.St, human).PadLeft(bw) + " " : "")
                + $"{VirtualFileSystem.FormatMode(r.St)} {r.Links.PadLeft(lw)} {r.St.Owner.PadRight(ow)} {r.St.Owner.PadRight(ow)} {r.Size.PadLeft(sw)} {LsDate(r.St.Modified, now, style)} {name}";
        }
    }

    static string ShowNonprinting(string line) => string.Concat(line.Select(c =>
        c == '\t' || (c >= 32 && c < 127) || c > 159 ? c.ToString() : c == 127 ? "^?" : c < 32 ? "^" + (char)(c + 64) : "M-^" + (char)(c - 64)));

    static void RegisterFileCommands(Shell shell)
    {
        shell.Register("ls", ctx =>
        {
            // \u0001: --time-style=STYLE, \u0002: --full-time
            var a = Args.Parse(ctx.Args, "alhA1dFrtRSZskLH\u0002", "\u0001",
                "all=a,almost-all=A,human-readable=h,directory=d,reverse=r,recursive=R,size=s,kibibytes=k,time-style=\u0001,full-time=\u0002,dereference=L,dereference-command-line=H");
            var paths = a.Positional.Count > 0 ? a.Positional : ["."];
            bool lng = a.Has('l') || a.Has('\u0002'), human = a.Has('h'), blocks = a.Has('s'), deref = a.Has('L');
            var style = a.Has('\u0002') ? "full-iso" : lng && a.Value('\u0001') is { } ts ? TimeStyle(ts) : null;
            var fs = ctx.Fs;
            int code = 0;
            // -F: what each kind of entry gets; a link itself gets @ (unless -L follows it)
            string Classify(FileStat st) => st.IsDirectory ? "/" : st.IsSymlink ? "@" : (st.Mode & 0x49) != 0 ? "*" : "";
            void Show(IEnumerable<(string Abs, string Name)> entries, bool follow, IEnumerable<string>? widthFrom = null)
            {
                var named = entries.Select(e => (e.Abs, Name: e.Name + (!lng && a.Has('F') && fs.Exists(e.Abs, follow) ? Classify(fs.Stat(e.Abs, follow)) : ""))).ToList();
                if (lng) { WriteLines(ctx.Stdout, LongEntries(ctx, named, human, widthFrom, style, blocks, follow, a.Has('F') ? Classify : null)); return; }
                if (!blocks) { WriteLines(ctx.Stdout, named.Select(e => e.Name)); return; }
                var sizes = named.Select(e => follow && !fs.Exists(e.Abs) ? "?" : BlockSize(fs.Stat(e.Abs, follow), human)).ToList();
                int w = sizes.Select(x => x.Length).DefaultIfEmpty(0).Max();
                WriteLines(ctx.Stdout, named.Select((e, k) => sizes[k].PadLeft(w) + " " + e.Name));
            }

            // a link named on the command line is followed to a directory, unless -l, -d or -F shows the link itself
            bool FollowOperand(string p) => deref || a.Has('H') || p.EndsWith('/') || !(lng || a.Has('d') || a.Has('F'));
            bool Exists(string p) => fs.Exists(ctx.ResolvePath(p), deref || p.EndsWith('/'));
            FileStat StatOf(string p) => fs.Stat(ctx.ResolvePath(p), deref || a.Has('H') || p.EndsWith('/'));

            // operands are listed like directory entries: byte order (the C locale), or by -t / -S, reversed by -r
            List<string> Ordered(IEnumerable<string> ps)
            {
                var list = ps.OrderBy(p => p, StringComparer.Ordinal).ToList();
                if (a.Has('t')) list = list.OrderByDescending(p => StatOf(p).Modified).ToList();
                if (a.Has('S')) list = list.OrderByDescending(p => StatOf(p).Size).ToList();
                if (a.Has('r')) list.Reverse();
                return list;
            }
            var files = Ordered(paths.Where(p => Exists(p) && (a.Has('d') || !fs.IsDirectory(ctx.ResolvePath(p), FollowOperand(p)))));
            foreach (var p in paths.Where(p => !Exists(p))) { ctx.Stderr.WriteLine($"ls: cannot access '{p}': {fs.WhyMissing(ctx.ResolvePath(p), deref || p.EndsWith('/'))}"); code = 2; }
            foreach (var group in files.GroupBy(p => deref || a.Has('H') || p.EndsWith('/')))
                Show(group.Select(f => (ctx.ResolvePath(f), f)), group.Key, paths.Except(files).Where(Exists).Select(ctx.ResolvePath));

            var dirs = Ordered(paths.Except(files).Where(p => fs.IsDirectory(ctx.ResolvePath(p))));
            bool first = files.Count == 0;
            var listed = new HashSet<string>(StringComparer.Ordinal); // real paths, so -RL stops at a link back up the tree
            void ListDir(string abs, string label)
            {
                abs = fs.RealPath(abs, out _) ?? abs; // where it really is, so "." and ".." are the directories themselves
                if (a.Has('R') && deref && !listed.Add(abs))
                {
                    ctx.Stderr.WriteLine($"ls: {label}: not listing already-listed directory");
                    code = 2;
                    return;
                }
                if (paths.Count > 1 || a.Has('R')) ctx.Stdout.WriteLine((first ? "" : "\n") + label + ":");
                first = false;
                if (!fs.CanRead(abs)) { ctx.Stderr.WriteLine($"ls: cannot open directory '{label}': Permission denied"); code = 2; return; }
                var entries = fs.List(abs).Where(n => a.Has('a') || a.Has('A') || !n.StartsWith('.')).ToList();
                if (deref && (lng || blocks)) // a link leading nowhere: an error, then a row of question marks
                    foreach (var n in entries.Where(n => !fs.Exists(VirtualFileSystem.Normalize(abs, n))))
                    {
                        ctx.Stderr.WriteLine($"ls: cannot access '{(label == "." ? "" : label.TrimEnd('/') + "/")}{n}': {fs.WhyMissing(VirtualFileSystem.Normalize(abs, n))}");
                        code = 1;
                    }
                FileStat St(string n) => fs.Stat(VirtualFileSystem.Normalize(abs, n), deref && fs.Exists(VirtualFileSystem.Normalize(abs, n)));
                if (a.Has('t')) entries = entries.OrderByDescending(n => St(n).Modified).ToList();
                if (a.Has('S')) entries = entries.OrderByDescending(n => St(n).Size).ToList();
                if (a.Has('a')) entries.InsertRange(0, [".", ".."]);
                if (a.Has('r')) entries.Reverse();
                if (lng || blocks)
                {
                    long kib = entries.Sum(n => St(n).Blocks / 2);
                    ctx.Stdout.WriteLine($"total {(human ? HumanSize(kib * 1024) : kib)}");
                }
                Show(entries.Select(n => (VirtualFileSystem.Normalize(abs, n), n)), deref);
                if (!a.Has('R')) return;
                foreach (var n in entries.Where(n => n is not "." and not ".."))
                {
                    var child = VirtualFileSystem.Normalize(abs, n);
                    if (fs.IsDirectory(child, deref)) ListDir(child, label.TrimEnd('/') + "/" + n);
                }
            }
            foreach (var d in dirs) ListDir(ctx.ResolvePath(d), d);
            return code;
        }, "List directory contents");

        shell.Register("cat", ctx =>
        {
            var a = Args.Parse(ctx.Args, "nbsAETveu", longs: "number=n,number-nonblank=b,squeeze-blank=s,show-all=A,show-ends=E,show-tabs=T,show-nonprinting=v");
            bool ends = a.Has('A') || a.Has('E') || a.Has('e'), tabs = a.Has('A') || a.Has('T'), nonprint = a.Has('A') || a.Has('v') || a.Has('e');
            bool plain = !(a.Has('n') || a.Has('b') || a.Has('s') || ends || tabs || nonprint);
            int code = 0, lineNo = 0;
            bool prevBlank = false;
            foreach (var (_, text) in Inputs(ctx, a.Positional, () => code = 1))
            {
                if (plain) { ctx.Stdout.Write(text); continue; }
                var sb = new StringBuilder();
                foreach (var raw in SplitLines(text))
                {
                    var line = raw;
                    if (a.Has('s') && line.Length == 0 && prevBlank) continue;
                    prevBlank = line.Length == 0;
                    if (nonprint) line = ShowNonprinting(line);
                    if (tabs) line = line.Replace("\t", "^I");
                    if (!(a.Has('b') && line.Length == 0) && (a.Has('n') || a.Has('b'))) sb.Append($"{++lineNo,6}\t");
                    sb.Append(line).Append(ends ? "$\n" : "\n");
                }
                if (sb.Length > 0 && !text.EndsWith('\n')) sb.Length -= ends ? 2 : 1; // an unterminated last line gets neither a newline nor a $
                ctx.Stdout.Write(sb.ToString());
            }
            return code;
        }, "Print files");

        shell.Register("mkdir", ctx =>
        {
            var a = Args.Parse(ctx.Args, "pvZ", longs: "parents=p,verbose=v");
            if (a.Positional.Count == 0) throw new UsageException("missing operand");
            int code = 0;
            foreach (var p in a.Positional)
            {
                try { ctx.Fs.CreateDirectory(ctx.ResolvePath(p), a.Has('p')); }
                catch (ShellException e) { ctx.Stderr.WriteLine($"mkdir: cannot create directory {QuoteL(p)}: {e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]}"); code = 1; }
            }
            return code;
        }, "Create directories");

        shell.Register("touch", ctx =>
        {
            // shortcut: one modification time per file; -a and -m both set it
            var a = Args.Parse(ctx.Args, "acmhf", "dtr", "no-create=c,date=d,reference=r,time=f,no-dereference=h");
            if (a.Positional.Count == 0) throw new UsageException("missing file operand");
            DateTime? when = null;
            if (a.Value('r') is { } reference)
            {
                var abs = ctx.ResolvePath(reference);
                if (!ctx.Fs.Exists(abs)) throw new ShellException($"failed to get attributes of {QuoteAlways(reference)}: {ctx.Fs.WhyMissing(abs)}");
                when = ctx.Fs.Stat(abs).Modified;
            }
            if (a.Value('d') is { } d)
                try { when = ParseDate(d, when ?? ctx.Fs.Clock()); }
                catch (ShellException) { throw new ShellException($"invalid date format {QuoteL(d)}"); }
            if (a.Value('t') is { } t) when = TouchStamp(t, ctx.Fs.Clock()) ?? throw new ShellException($"invalid date format {QuoteL(t)}");
            int code = 0;
            foreach (var p in a.Positional)
            {
                var path = ctx.ResolvePath(p);
                if (a.Has('c') && !ctx.Fs.Exists(path)) continue;
                // an explicit time needs ownership, not write permission
                try { if (a.Has('h') && ctx.Fs.IsSymlink(path)) ctx.Fs.SetModifiedTime(path, when ?? ctx.Fs.Clock(), follow: false); else if (when is { } w) { if (!ctx.Fs.Exists(path)) ctx.Fs.Touch(path); ctx.Fs.SetModifiedTime(path, w); } else ctx.Fs.Touch(path); }
                catch (ShellException e) { ctx.Stderr.WriteLine($"touch: cannot touch {QuoteAlways(p)}: {Typed(e, p, path).Reason}"); code = 1; }
            }
            return code;
        }, "Create empty files / update timestamps");

        shell.Register("rm", ctx =>
        {
            var a = Args.Parse(ctx.Args, "rRfvd", longs: "recursive=r,force=f");
            bool recursive = a.Has('r') || a.Has('R');
            if (a.Positional.Count == 0 && !a.Has('f')) throw new UsageException("missing operand");
            int code = 0;
            foreach (var p in a.Positional)
            {
                var abs = ctx.ResolvePath(p);
                if (!ctx.Fs.Exists(abs, follow: false))
                {
                    if (!a.Has('f') || ctx.Fs.WhyMissing(abs, follow: false) != "No such file or directory") { ctx.Stderr.WriteLine($"rm: cannot remove '{p}': {ctx.Fs.WhyMissing(abs, follow: false)}"); code = 1; }
                    continue;
                }
                if (ctx.Fs.IsDirectory(abs, follow: p.EndsWith('/')) && !recursive && !a.Has('d')) { ctx.Stderr.WriteLine($"rm: cannot remove '{p}': Is a directory"); code = 1; continue; }
                if (recursive && p.EndsWith('/') && ctx.Fs.IsSymlink(abs) && ctx.Fs.IsDirectory(abs))
                {
                    // like GNU rm: "link/" is the directory it leads to, which is emptied; the link itself is no directory to remove
                    foreach (var n in ctx.Fs.List(abs))
                        try { ctx.Fs.Delete(abs + "/" + n, recursive: true); }
                        catch (ShellException e) { var (path, why) = Typed(e, p.TrimEnd('/') + "/" + n, abs + "/" + n); ctx.Stderr.WriteLine($"rm: cannot remove '{path}': {why}"); }
                    if (!a.Has('f')) { ctx.Stderr.WriteLine($"rm: cannot remove '{p}': Not a directory"); code = 1; }
                    continue;
                }
                try { ctx.Fs.Delete(abs, recursive); }
                catch (ShellException e) { var (path, why) = Typed(e, p, abs); ctx.Stderr.WriteLine($"rm: cannot remove '{path}': {why}"); code = 1; }
            }
            return code;
        }, "Remove files or directories");

        shell.Register("rmdir", ctx =>
        {
            var a = Args.Parse(ctx.Args, "pv", longs: "parents=p,verbose=v");
            if (a.Positional.Count == 0) throw new UsageException("missing operand");
            int code = 0;
            foreach (var arg in a.Positional)
            {
                // -p: a/b/c, then a/b, then a
                for (var p = arg.TrimEnd('/'); p.Length > 0; p = a.Has('p') && p.LastIndexOf('/') is var i and > 0 ? p[..i].TrimEnd('/') : "")
                {
                    var abs = ctx.ResolvePath(p);
                    string? why = !ctx.Fs.IsDirectory(abs, follow: false) ? (ctx.Fs.Exists(abs, follow: false) ? "Not a directory" : ctx.Fs.WhyMissing(abs)) : null;
                    if (why == null)
                        try { ctx.Fs.Delete(abs); if (a.Has('v')) ctx.Stdout.WriteLine($"rmdir: removing directory, {QuoteAlways(p)}"); }
                        catch (ShellException e) { why = Typed(e, p, abs).Reason; }
                    if (why != null) { ctx.Stderr.WriteLine($"rmdir: failed to remove {QuoteAlways(p)}: {why}"); code = 1; break; }
                }
            }
            return code;
        }, "Remove empty directories");

        CommandHandler copyOrMove = ctx =>
        {
            bool move = ctx.Name == "mv";
            var a = Args.Parse(ctx.Args, move ? "fvniZ" : "rRfvanpZLPHd", longs: "recursive=r,force=f,dereference=L,no-dereference=P");
            // cp follows a symbolic link it is given unless copying recursively (-r, -a) or told not to; -L follows them all
            bool followTop = !move && !(a.Has('P') || a.Has('d') || a.Has('a')) && (a.Has('L') || a.Has('H') || !(a.Has('r') || a.Has('R')));
            if (a.Positional.Count < 2) throw new UsageException(a.Positional.Count == 0 ? "missing file operand" : $"missing destination file operand after {QuoteAlways(a.Positional[0])}");
            var sources = a.Positional.Take(a.Positional.Count - 1).ToList();
            var destArg = a.Positional[^1];
            var dest = ctx.ResolvePath(destArg);
            bool destIsDir = ctx.Fs.IsDirectory(dest);
            if ((sources.Count > 1 || destArg.EndsWith('/') && !ctx.Fs.IsDirectory(ctx.ResolvePath(sources[0]))) && !destIsDir)
            {
                if (sources.Count == 1 && !ctx.Fs.Exists(ctx.ResolvePath(sources[0]))) throw new ShellException($"cannot stat '{sources[0]}': No such file or directory");
                if (sources.Count > 1)
                    throw new ShellException($"target '{destArg}': {(ctx.Fs.Exists(dest) ? "Not a directory" : "No such file or directory")}");
                throw new ShellException(move ? $"cannot move '{sources[0]}' to '{destArg}': Not a directory" : $"cannot create regular file '{destArg}': Not a directory");
            }
            int code = 0;
            foreach (var s in sources)
            {
                var src = ctx.ResolvePath(s);
                if (!ctx.Fs.Exists(src, followTop)) { ctx.Stderr.WriteLine($"{ctx.Name}: cannot stat '{s}': {ctx.Fs.WhyMissing(src, followTop)}"); code = 1; continue; }
                if (!move && ctx.Fs.IsDirectory(src, followTop) && !(a.Has('r') || a.Has('R') || a.Has('a')))
                { ctx.Stderr.WriteLine($"cp: -r not specified; omitting directory '{s}'"); code = 1; continue; }
                var target = destIsDir ? VirtualFileSystem.Normalize(dest, VirtualFileSystem.GetName(src)) : dest;
                if (target != src && !move && followTop && ctx.Fs.SameFile(src, target)) // through a link or another hard link; the plain case is a clash below
                { ctx.Stderr.WriteLine($"cp: {QuoteAlways(s)} and {QuoteAlways(destIsDir ? destArg.TrimEnd('/') + "/" + VirtualFileSystem.GetName(src) : destArg)} are the same file"); code = 1; continue; }
                if (!move && ctx.Fs.IsFile(src, followTop) && ctx.Fs.IsSymlink(target) && !ctx.Fs.Exists(target))
                { ctx.Stderr.WriteLine($"cp: not writing through dangling symlink '{(destIsDir ? destArg.TrimEnd('/') + "/" + VirtualFileSystem.GetName(src) : destArg)}'"); code = 1; continue; }
                if (target == "/dev/null" && !move) continue; // copying to /dev/null discards the data
                var shown = destIsDir ? destArg.TrimEnd('/') + "/" + VirtualFileSystem.GetName(src) : destArg;
                bool srcDir = ctx.Fs.IsDirectory(src, followTop);
                string? clash = target == src ? $"{QuoteAlways(s)} and {QuoteAlways(shown)} are the same file"
                    : srcDir && target.StartsWith(src + "/", StringComparison.Ordinal)
                        ? move ? $"cannot move {QuoteAlways(s)} to a subdirectory of itself, {QuoteAlways(shown)}"
                            : $"cannot copy a directory, {QuoteAlways(s)}, into itself, {QuoteAlways(shown)}"
                    : !ctx.Fs.Exists(target) ? null
                    : srcDir && !ctx.Fs.IsDirectory(target) ? $"cannot overwrite non-directory {QuoteAlways(shown)} with directory {QuoteAlways(s)}"
                    : !srcDir && ctx.Fs.IsDirectory(target) ? $"cannot overwrite directory {QuoteAlways(shown)} with non-directory"
                    : move && srcDir && ctx.Fs.List(target).Count > 0 ? $"cannot overwrite {QuoteAlways(shown)}: Directory not empty"
                    : null;
                if (clash != null)
                {
                    ctx.Stderr.WriteLine($"{ctx.Name}: {clash}");
                    code = 1;
                    // cp creates the copy and fills it until it reaches the copy itself
                    if (!move && clash.StartsWith("cannot copy", StringComparison.Ordinal)) CopyIntoItself(ctx, src, target, []);
                    continue;
                }
                if (move && srcDir && ctx.Fs.IsDirectory(target)) ctx.Fs.Delete(target); // an empty directory is replaced
                try
                {
                    if (move) ctx.Fs.Move(src, target);
                    else ctx.Fs.Copy(src, target, (abs, dir) =>
                    {
                        var p = Typed(new ShellException(abs + ": "), s, src).Path;
                        ctx.Stderr.WriteLine(dir ? $"cp: cannot access '{p}': Permission denied" : $"cp: cannot open '{p}' for reading: Permission denied");
                        code = 1;
                    }, followTop, a.Has('L'), (abs, why) =>
                    {
                        var p = Typed(new ShellException(abs + ": "), s, src).Path;
                        ctx.Stderr.WriteLine(why == "cyclic" ? $"cp: cannot copy cyclic symbolic link '{p}'" : $"cp: cannot stat '{p}': {why}");
                        code = 1;
                    });
                }
                catch (ShellException e) when (e.Message.Contains(" into itself, ") || e.Message.Contains(" to a subdirectory of itself, "))
                {
                    ctx.Stderr.WriteLine(move ? $"mv: cannot move '{s}' to a subdirectory of itself, '{shown}'" : $"cp: cannot copy a directory, '{s}', into itself, '{shown}'");
                    code = 1;
                    continue;
                }
                catch (ShellException e) when (e.Message.EndsWith(VirtualFileSystem.NoSpace))
                {
                    ctx.Stderr.WriteLine($"{ctx.Name}: error writing '{shown}': {VirtualFileSystem.NoSpace}");
                    code = 1;
                    continue;
                }
                catch (ShellException e) when (e.Message.EndsWith("Permission denied") || e.Message.EndsWith("Operation not permitted"))
                {
                    var why = Typed(e, s, src).Reason;
                    ctx.Stderr.WriteLine(move ? $"mv: cannot move '{s}' to '{shown}': {why}"
                        : $"cp: cannot create {(ctx.Fs.IsDirectory(src) ? "directory" : "regular file")} '{shown}': {why}");
                    code = 1;
                    continue;
                }
                if (a.Has('v')) ctx.Stdout.WriteLine($"{(move ? "renamed " : "")}'{s}' -> '{(destIsDir ? destArg.TrimEnd('/') + "/" + VirtualFileSystem.GetName(src) : destArg)}'");
            }
            return Task.FromResult(code);
        };
        shell.Register("cp", copyOrMove, "Copy files and directories");

        // cp -r d d/x copies d into d/x, entry by entry, until it meets a directory this copy created; there it stops.
        // Returns false once stopped. shortcut: entries go in name order, where GNU goes in the directory's own order.
        static bool CopyIntoItself(CommandContext ctx, string from, string to, HashSet<string> created)
        {
            if (!ctx.Fs.Exists(to)) { ctx.Fs.CreateDirectory(to); created.Add(to); }
            foreach (var name in ctx.Fs.List(from).ToList())
            {
                var child = VirtualFileSystem.Normalize(from, name);
                if (created.Contains(child)) return false;
                if (!ctx.Fs.IsDirectory(child)) ctx.Fs.Copy(child, VirtualFileSystem.Normalize(to, name));
                else if (!CopyIntoItself(ctx, child, VirtualFileSystem.Normalize(to, name), created)) return false;
            }
            return true;
        }
        shell.Register("mv", copyOrMove, "Move or rename files");

        shell.Register("ln", ctx =>
        {
            var a = Args.Parse(ctx.Args, "sfnvTrLPb", "t", "symbolic=s,force=f,no-dereference=n,no-target-directory=T,target-directory=t,verbose=v,relative=r,logical=L,physical=P");
            var fs = ctx.Fs;
            bool sym = a.Has('s');
            var ops = a.Positional;
            var into = a.Value('t');
            if (into == null && !a.Has('T') && (ops.Count > 2 || ops.Count == 2 && fs.IsDirectory(ctx.ResolvePath(ops[^1]), !a.Has('n')))) into = ops[^1];
            if (ops.Count == 0 || (ops.Count == 1 && a.Has('T') && into == null))
                throw new UsageException(ops.Count == 0 ? "missing file operand" : $"missing destination file operand after {QuoteAlways(ops[0])}");
            if (into != null && a.Value('t') == null) ops = ops.Take(ops.Count - 1).ToList();
            if (into != null && !fs.IsDirectory(ctx.ResolvePath(into)))
                throw new ShellException(a.Value('t') != null && !fs.Exists(ctx.ResolvePath(into)) ? $"failed to access {QuoteAlways(into)}: {fs.WhyMissing(ctx.ResolvePath(into))}"
                    : fs.Exists(ctx.ResolvePath(into)) ? $"target {QuoteAlways(into)} is not a directory" : $"target {QuoteAlways(into)}: No such file or directory");
            // (what the link points to, the link's name): `ln f` links into ".", `ln f d` into d when it is a directory
            var pairs = into != null || ops.Count == 1
                ? ops.Select(t => (t, (into == null ? "" : into.TrimEnd('/') + "/") + VirtualFileSystem.GetName(t))).ToList()
                : [(ops[0], ops[1])];
            int code = 0;
            foreach (var (target, name) in pairs)
            {
                var shownName = into == null && ops.Count == 1 ? "./" + name : name;
                var abs = ctx.ResolvePath(name);
                var src = ctx.ResolvePath(target);
                string Fail(string why, bool arrow = true) =>
                    $"ln: failed to create {(sym ? "symbolic" : "hard")} link {QuoteAlways(shownName)}{(!arrow ? "" : sym ? (target.Length == 0 ? " -> ''" : "") : " => " + QuoteAlways(target))}: {why}";
                try
                {
                    if (!sym && !fs.Exists(src, a.Has('L'))) { ctx.Stderr.WriteLine($"ln: failed to access {QuoteAlways(target)}: {fs.WhyMissing(src, a.Has('L'))}"); code = 1; continue; }
                    if (!sym && fs.IsDirectory(src)) { ctx.Stderr.WriteLine($"ln: {target}: hard link not allowed for directory"); code = 1; continue; }
                    if (fs.Exists(abs, follow: false))
                    {
                        if (!a.Has('f')) { ctx.Stderr.WriteLine(Fail("File exists", arrow: false)); code = 1; continue; }
                        if (fs.IsDirectory(abs, follow: false)) { ctx.Stderr.WriteLine($"ln: {name}: cannot overwrite directory"); code = 1; continue; }
                        if (fs.SameFile(abs, sym ? VirtualFileSystem.Normalize(VirtualFileSystem.GetParent(abs), target) : src))
                        { ctx.Stderr.WriteLine($"ln: {QuoteAlways(target)} and {QuoteAlways(name)} are the same file"); code = 1; continue; }
                        fs.Delete(abs);
                    }
                    if (!sym) fs.CreateHardLink(src, abs, a.Has('L'));
                    else
                    {
                        var stored = target;
                        if (a.Has('r') && target.Length > 0)
                        {
                            // -r: from the link's real directory to the target's real path
                            var to = (fs.RealPath(src, out _, existing: false) ?? src).Split('/', StringSplitOptions.RemoveEmptyEntries);
                            var dir = VirtualFileSystem.GetParent(abs);
                            var from = (fs.RealPath(dir, out _) ?? dir).Split('/', StringSplitOptions.RemoveEmptyEntries);
                            int common = 0;
                            while (common < to.Length && common < from.Length && to[common] == from[common]) common++;
                            stored = string.Join('/', Enumerable.Repeat("..", from.Length - common).Concat(to.Skip(common)));
                            if (stored.Length == 0) stored = ".";
                        }
                        fs.CreateSymlink(abs, stored);
                    }
                    if (a.Has('v')) ctx.Stdout.WriteLine($"{QuoteAlways(shownName)} {(sym ? "->" : "=>")} {QuoteAlways(target)}");
                }
                catch (ShellException e) { ctx.Stderr.WriteLine(Fail(Reason(e), arrow: Reason(e) != "File exists")); code = 1; }
            }
            return code;
        }, "Make links between files (-s symbolic, -f replace)");

        shell.Register("find", Find.RunAsync, "Search for files");

        shell.Register("tree", ctx =>
        {
            var a = Args.Parse(ctx.Args, "ad", valued: "L");
            int maxDepth = a.Value('L') is { } l ? int.Parse(l) : int.MaxValue;
            var root = a.Positional.FirstOrDefault() ?? ".";
            var abs = ctx.ResolvePath(root);
            if (!ctx.Fs.IsDirectory(abs)) throw new ShellException($"{root} [error opening dir]");
            int dirs = 0, files = 0;
            var sb = new StringBuilder(root + "\n");
            void Walk(string dir, string prefix, int depth)
            {
                var entries = ctx.Fs.List(dir).Where(n => a.Has('a') || !n.StartsWith('.'))
                    .Where(n => !a.Has('d') || ctx.Fs.IsDirectory(VirtualFileSystem.Normalize(dir, n), follow: false)).ToList();
                for (int i = 0; i < entries.Count; i++)
                {
                    bool last = i == entries.Count - 1;
                    var child = VirtualFileSystem.Normalize(dir, entries[i]);
                    sb.Append(prefix).Append(last ? "└── " : "├── ").Append(entries[i]).Append(ctx.Fs.IsSymlink(child) ? " -> " + ctx.Fs.ReadLink(child) : "").Append('\n');
                    if (ctx.Fs.IsDirectory(child, follow: false))
                    {
                        dirs++;
                        if (depth < maxDepth) Walk(child, prefix + (last ? "    " : "│   "), depth + 1);
                    }
                    else files++;
                }
            }
            Walk(abs, "", 1);
            sb.Append($"\n{dirs} director{(dirs == 1 ? "y" : "ies")}").Append(a.Has('d') ? "\n" : $", {files} file{(files == 1 ? "" : "s")}\n");
            ctx.Stdout.Write(sb.ToString());
            return 0;
        }, "Show a directory tree");
    }
}
