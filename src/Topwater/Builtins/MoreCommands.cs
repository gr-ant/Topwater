using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Topwater;

internal static partial class Builtins
{
    static int ParseCount(CommandContext ctx, int fallback) =>
        ctx.Args.Count == 0 ? fallback : int.TryParse(ctx.Args[0], out var n) ? n : throw new ShellException($"{ctx.Args[0]}: numeric argument required", 2);

    static TimeSpan ParseDuration(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+(?:\.\d+)?)([smhd]?)$");
        if (!m.Success) throw new ShellException($"invalid time interval '{s}'");
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch { "m" => TimeSpan.FromMinutes(n), "h" => TimeSpan.FromHours(n), "d" => TimeSpan.FromDays(n), _ => TimeSpan.FromSeconds(n) };
    }

    static void RegisterScriptingCommands(Shell shell)
    {
        shell.Register("exit", (Func<CommandContext, int>)(ctx => throw new ExitSignal(ParseCount(ctx, ctx.Shell.LastExitCode) & 255)), "Exit the shell");
        shell.Register("return", (Func<CommandContext, int>)(ctx => throw new ReturnSignal(ParseCount(ctx, ctx.Shell.LastExitCode) & 255)), "Return from a function");
        shell.Register("break", (Func<CommandContext, int>)(ctx => throw new LoopControl(true, Math.Max(1, ParseCount(ctx, 1)))), "Exit a loop");
        shell.Register("continue", (Func<CommandContext, int>)(ctx => throw new LoopControl(false, Math.Max(1, ParseCount(ctx, 1)))), "Next loop iteration");

        shell.Register("shift", ctx =>
        {
            int n = ParseCount(ctx, 1);
            var pos = ctx.Shell.Positional;
            if (n > pos.Count) return 1;
            ctx.Shell.Positional = pos.Skip(n).ToList();
            return 0;
        }, "Shift positional parameters");

        shell.Register("set", ctx =>
        {
            var args = ctx.Args.ToList();
            if (args.Count == 0)
            {
                foreach (var (k, v) in ctx.Shell.Environment.OrderBy(e => e.Key, StringComparer.Ordinal)) ctx.Stdout.WriteLine($"{k}={v}");
                return 0;
            }
            int i = 0;
            for (; i < args.Count; i++)
            {
                var a = args[i];
                if (a == "--") { i++; ctx.Shell.Positional = args.Skip(i).ToList(); return 0; }
                if (a.Length < 2 || a[0] is not ('-' or '+')) break;
                bool on = a[0] == '-';
                foreach (var c in a[1..])
                {
                    if (c == 'e') ctx.Shell.Errexit = on;
                    else if (c == 'o')
                    {
                        var opt = i + 1 < args.Count ? args[++i] : throw new ShellException("-o: option name required", 2);
                        if (opt == "pipefail") ctx.Shell.Pipefail = on;
                        else if (opt == "errexit") ctx.Shell.Errexit = on;
                        // nounset, xtrace, noclobber...: accepted, not enforced (bash-like, simplified)
                    }
                }
            }
            if (i < args.Count) ctx.Shell.Positional = args.Skip(i).ToList();
            return 0;
        }, "Set shell options (-e, -o pipefail) or positional parameters");

        CommandHandler declare = ctx =>
        {
            bool local = ctx.Name == "local";
            foreach (var a in ctx.Args.Where(a => !a.StartsWith('-') && !a.StartsWith('+')))
            {
                int eq = a.IndexOf('=');
                var name = eq > 0 ? a[..eq] : a;
                var value = eq > 0 ? a[(eq + 1)..] : null;
                if (local || ctx.Name is "declare" or "typeset" && ctx.Shell.InFunction) ctx.Shell.DeclareLocal(name, value);
                else ctx.Shell.Environment[name] = value ?? ctx.Shell.Environment.GetValueOrDefault(name, "");
            }
            return Task.FromResult(0);
        };
        shell.Register("local", declare, "Declare function-local variables");
        shell.Register("declare", declare, "Declare variables");
        shell.Register("typeset", declare, "Declare variables");
        shell.Register("readonly", declare, "Declare variables (not enforced)");

        shell.Register("read", ctx =>
        {
            var a = Args.Parse(ctx.Args, "rsea", "ptdnu");
            var names = a.Positional.Count > 0 ? a.Positional : ["REPLY"];
            var delim = a.Value('d') is { } dv ? (dv.Length == 0 ? '\0' : dv[0]) : '\n';
            var line = ctx.ReadLine(delim);
            var env = ctx.Shell.Environment;
            if (line == null) { foreach (var n in names) env[n] = ""; return 1; }
            if (!a.Has('r')) line = System.Text.RegularExpressions.Regex.Replace(line, @"\\(.)", "$1");
            if (a.Positional.Count == 0) { env["REPLY"] = line; return 0; }
            var ifs = env.GetValueOrDefault("IFS", " \t\n");
            var seps = ifs.Where(c => !char.IsWhiteSpace(c)).ToArray();
            string[] fields = seps.Length > 0
                ? line.Split(seps, names.Count).Select(f => f.Trim()).ToArray()
                : line.Trim().Split((char[]?)null, names.Count, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).ToArray();
            for (int i = 0; i < names.Count; i++) env[names[i]] = i < fields.Length ? fields[i] : "";
            return 0;
        }, "Read a line from stdin into variables");

        shell.Register("eval", ctx => ctx.Shell.RunScriptTextAsync(string.Join(' ', ctx.Args), ctx.Io, subshell: false, ctx.CancellationToken), "Run arguments as a shell command");

        shell.Register("let", ctx =>
        {
            if (ctx.Args.Count == 0) throw new ShellException("expression expected");
            long v = 0;
            foreach (var expr in ctx.Args) v = Arith.Eval(ctx.Shell, expr);
            return v != 0 ? 0 : 1;
        }, "Evaluate arithmetic expressions");

        shell.Register("command", async ctx =>
        {
            var args = ctx.Args.ToList();
            bool describe = args.Count > 0 && args[0] is "-v" or "-V";
            if (!describe) return args.Count == 0 ? 0 : await ctx.Shell.InvokeAsync(args, ctx.Io, ctx.CancellationToken);
            int code = 0;
            foreach (var n in args.Skip(1))
            {
                if (ctx.Shell.Aliases.TryGetValue(n, out var al)) ctx.Stdout.WriteLine($"alias {n}='{al}'");
                else if (ctx.Shell.Functions.ContainsKey(n)) ctx.Stdout.WriteLine(n);
                else if (ctx.Shell.Commands.ContainsKey(n)) ctx.Stdout.WriteLine($"/usr/bin/{n}");
                else code = 1;
            }
            return code;
        }, "Run a command, or describe it with -v");

        shell.Register("printenv", ctx =>
        {
            if (ctx.Args.Count == 0)
            {
                foreach (var (k, v) in ctx.Shell.Environment.OrderBy(e => e.Key, StringComparer.Ordinal)) ctx.Stdout.WriteLine($"{k}={v}");
                return 0;
            }
            int code = 0;
            foreach (var n in ctx.Args)
                if (ctx.Shell.Environment.TryGetValue(n, out var v)) ctx.Stdout.WriteLine(v); else code = 1;
            return code;
        }, "Print environment variables");

        shell.Register("sleep", async ctx =>
        {
            if (ctx.Args.Count == 0) throw new ShellException("missing operand");
            var total = ctx.Args.Aggregate(TimeSpan.Zero, (t, a) => t + ParseDuration(a));
            await Task.Delay(total, ctx.CancellationToken);
            return 0;
        }, "Pause for a number of seconds");

        shell.Register("timeout", async ctx =>
        {
            var args = ctx.Args.SkipWhile(a => a.StartsWith('-')).ToList();
            if (args.Count < 2) throw new ShellException("missing operand", 125);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            cts.CancelAfter(ParseDuration(args[0]));
            try { return await ctx.Shell.InvokeAsync(args.Skip(1).ToList(), ctx.Io, cts.Token); }
            catch (OperationCanceledException) when (!ctx.CancellationToken.IsCancellationRequested) { return 124; }
        }, "Run a command with a time limit");

        shell.Register("time", async ctx =>
        {
            var sw = Stopwatch.StartNew();
            int code = ctx.Args.Count == 0 ? 0 : await ctx.Shell.InvokeAsync(ctx.Args.ToList(), ctx.Io, ctx.CancellationToken);
            var t = sw.Elapsed;
            ctx.Stderr.Write($"\nreal\t{(int)t.TotalMinutes}m{t.TotalSeconds % 60:0.000}s\nuser\t0m0.000s\nsys\t0m0.000s\n");
            return code;
        }, "Time a command");

        shell.Register("clear", _ => 0, "Clear the terminal");
    }

    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    static void RegisterUtilityCommands(Shell shell)
    {
        shell.Register("basename", ctx =>
        {
            var a = Args.Parse(ctx.Args, "az", "s", "multiple=a,suffix=s,zero=z");
            if (a.Positional.Count == 0) throw new ShellException("missing operand");
            var suffix = a.Value('s') ?? (!a.Has('a') && a.Positional.Count == 2 ? a.Positional[1] : null);
            var names = a.Has('a') || a.Value('s') != null ? a.Positional : a.Positional.Take(1).ToList();
            foreach (var n in names)
            {
                var b = n.TrimEnd('/') is var t && t.Length == 0 ? "/" : t[(t.LastIndexOf('/') + 1)..];
                if (suffix != null && b.EndsWith(suffix) && b != suffix) b = b[..^suffix.Length];
                ctx.Stdout.WriteLine(b);
            }
            return 0;
        }, "Strip directory and suffix from a path");

        shell.Register("dirname", ctx =>
        {
            if (ctx.Args.Count == 0) throw new ShellException("missing operand");
            foreach (var n in ctx.Args)
            {
                var t = n.TrimEnd('/');
                int i = t.LastIndexOf('/');
                ctx.Stdout.WriteLine(t.Length == 0 ? "/" : i < 0 ? "." : i == 0 ? "/" : t[..i].TrimEnd('/') is var d && d.Length > 0 ? d : "/");
            }
            return 0;
        }, "Strip the last component from a path");

        CommandHandler realpath = ctx =>
        {
            var a = Args.Parse(ctx.Args, "fesmqnz", longs: "canonicalize=f,canonicalize-existing=e,canonicalize-missing=m");
            if (ctx.Name == "readlink" && !a.Has('f') && !a.Has('e') && !a.Has('m')) return Task.FromResult(1); // nothing is a symlink
            int code = 0;
            foreach (var p in a.Positional.DefaultIfEmpty("."))
            {
                var abs = ctx.ResolvePath(p);
                if (!a.Has('m') && !ctx.Fs.Exists(abs) && (a.Has('e') || ctx.Name == "realpath" || !ctx.Fs.IsDirectory(VirtualFileSystem.GetParent(abs))))
                { ctx.Stderr.WriteLine($"{ctx.Name}: {p}: No such file or directory"); code = 1; continue; }
                ctx.Stdout.WriteLine(abs);
            }
            return Task.FromResult(code);
        };
        shell.Register("realpath", realpath, "Print the absolute path");
        shell.Register("readlink", realpath, "Print resolved path (-f)");

        shell.Register("stat", ctx =>
        {
            var a = Args.Parse(ctx.Args, "Lt", "c", "format=c,dereference=L,terse=t");
            int code = 0;
            var user = ctx.Shell.Options.User;
            foreach (var p in a.Positional)
            {
                var abs = ctx.ResolvePath(p);
                if (!ctx.Fs.Exists(abs)) { ctx.Stderr.WriteLine($"stat: cannot statx '{p}': No such file or directory"); code = 1; continue; }
                var st = ctx.Fs.Stat(abs);
                var type = st.IsDirectory ? "directory" : st.Size == 0 ? "regular empty file" : "regular file";
                var mode = st.IsDirectory ? "755" : "644";
                var rwx = st.IsDirectory ? "drwxr-xr-x" : "-rw-r--r--";
                var mtime = st.Modified.ToString("yyyy-MM-dd HH:mm:ss.fffffff00 +0000", CultureInfo.InvariantCulture);
                if (a.Value('c') is { } fmt)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < fmt.Length; i++)
                    {
                        if (fmt[i] != '%' || i + 1 >= fmt.Length) { sb.Append(fmt[i]); continue; }
                        sb.Append(fmt[++i] switch
                        {
                            'n' => p, 's' => st.Size.ToString(), 'F' => type, 'a' => mode, 'A' => rwx, 'U' => user, 'G' => user,
                            'u' => "1000", 'g' => "1000", 'y' => mtime, 'Y' => new DateTimeOffset(st.Modified).ToUnixTimeSeconds().ToString(),
                            'b' => ((st.Size + 511) / 512).ToString(), 'i' => (Math.Abs(abs.GetHashCode()) % 1000000).ToString(), '%' => "%",
                            var c => "%" + c,
                        });
                    }
                    ctx.Stdout.WriteLine(sb.ToString());
                    continue;
                }
                ctx.Stdout.Write(
                    $"  File: {p}\n  Size: {st.Size,-10}\tBlocks: {(st.Size + 511) / 512 * 8,-10} IO Block: 4096   {type}\n" +
                    $"Access: (0{mode}/{rwx})  Uid: ( 1000/{user,8})   Gid: ( 1000/{user,8})\n" +
                    $"Modify: {mtime}\nChange: {mtime}\n");
            }
            return code;
        }, "Show file status");

        shell.Register("du", ctx =>
        {
            var a = Args.Parse(ctx.Args, "shacSk", "d", "summarize=s,human-readable=h,all=a,total=c,max-depth=d");
            int maxDepth = a.Has('s') ? 0 : a.Value('d') is { } d ? int.Parse(d) : int.MaxValue;
            string Fmt(long kb) => !a.Has('h') ? kb.ToString()
                : kb < 10 ? $"{kb:0.0}K" : kb < 1024 ? $"{kb}K" : kb < 1024 * 1024 ? $"{kb / 1024.0:0.#}M" : $"{kb / 1048576.0:0.#}G";
            long total = 0;
            int code = 0;
            long Walk(string abs, string display, int depth)
            {
                if (!ctx.Fs.IsDirectory(abs))
                {
                    long kb = (ctx.Fs.Stat(abs).Size + 4095) / 4096 * 4;
                    if (a.Has('a') && depth <= maxDepth || depth == 0) ctx.Stdout.WriteLine($"{Fmt(kb)}\t{display}");
                    return kb;
                }
                long sum = 4;
                foreach (var n in ctx.Fs.List(abs)) sum += Walk(VirtualFileSystem.Normalize(abs, n), display.TrimEnd('/') + "/" + n, depth + 1);
                if (depth <= maxDepth) ctx.Stdout.WriteLine($"{Fmt(sum)}\t{display}");
                return sum;
            }
            foreach (var p in a.Positional.DefaultIfEmpty("."))
            {
                var abs = ctx.ResolvePath(p);
                if (!ctx.Fs.Exists(abs)) { ctx.Stderr.WriteLine($"du: cannot access '{p}': No such file or directory"); code = 1; continue; }
                total += Walk(abs, p, 0);
            }
            if (a.Has('c')) ctx.Stdout.WriteLine($"{Fmt(total)}\ttotal");
            return code;
        }, "Estimate disk usage");

        shell.Register("df", ctx =>
        {
            bool h = ctx.Args.Any(x => x.StartsWith('-') && x.Contains('h'));
            ctx.Stdout.Write(h
                ? "Filesystem      Size  Used Avail Use% Mounted on\n/dev/sda1        40G  7.8G   30G  21% /\ntmpfs           3.9G     0  3.9G   0% /dev/shm\n"
                : "Filesystem     1K-blocks    Used Available Use% Mounted on\n/dev/sda1       41152736 8123456  31016272  21% /\ntmpfs            4007440       0   4007440   0% /dev/shm\n");
            return 0;
        }, "Report filesystem disk space");

        CommandHandler noopOnFiles = ctx =>
        {
            var files = ctx.Args.Where(x => !x.StartsWith('-')).Skip(1).ToList(); // first operand is the mode/owner
            if (files.Count == 0) throw new ShellException("missing operand");
            int code = 0;
            foreach (var f in files)
                if (!ctx.Fs.Exists(ctx.ResolvePath(f))) { ctx.Stderr.WriteLine($"{ctx.Name}: cannot access '{f}': No such file or directory"); code = 1; }
            return Task.FromResult(code);
        };
        shell.Register("chmod", noopOnFiles, "Change file mode bits");
        shell.Register("chown", noopOnFiles, "Change file owner");
        shell.Register("chgrp", noopOnFiles, "Change file group");

        shell.Register("nl", ctx =>
        {
            var a = Args.Parse(ctx.Args, "", "bsw");
            bool all = a.Value('b') == "a";
            int width = int.Parse(a.Value('w') ?? "6"), n = 0, code = 0;
            var sep = a.Value('s') ?? "\t";
            foreach (var (_, text) in Inputs(ctx, a.Positional, () => code = 1))
                foreach (var line in SplitLines(text))
                    ctx.Stdout.WriteLine(line.Length == 0 && !all ? "" : $"{(++n).ToString().PadLeft(width)}{sep}{line}");
            return code;
        }, "Number lines");

        CommandHandler pager = ctx =>
        {
            int code = 0;
            foreach (var (_, text) in Inputs(ctx, ctx.Args.Where(x => !x.StartsWith('-') && !x.StartsWith('+')).ToList(), () => code = 1)) ctx.Stdout.Write(text);
            return Task.FromResult(code);
        };
        shell.Register("less", pager, "View files (non-interactive: prints them)");
        shell.Register("more", pager, "View files (non-interactive: prints them)");

        shell.Register("file", ctx =>
        {
            int code = 0;
            foreach (var p in ctx.Args.Where(x => !x.StartsWith('-')))
            {
                var abs = ctx.ResolvePath(p);
                string desc;
                if (!ctx.Fs.Exists(abs)) { desc = $"cannot open `{p}' (No such file or directory)"; code = 1; }
                else if (ctx.Fs.IsDirectory(abs)) desc = "directory";
                else
                {
                    var t = ctx.Fs.ReadAllText(abs);
                    desc = t.Length == 0 ? "empty"
                        : t.StartsWith("\u007fELF") ? "ELF 64-bit LSB pie executable, x86-64, version 1 (SYSV), dynamically linked, stripped"
                        : t.StartsWith("#!") ? (t.Contains("python") ? "Python script" : "Bourne-Again shell script") + ", ASCII text executable"
                        : LooksJson(t) ? "JSON text data"
                        : t.Any(c => c > 127) ? "Unicode text, UTF-8 text" : "ASCII text";
                }
                ctx.Stdout.WriteLine($"{p}: {desc}");
            }
            return code;
        }, "Determine file type");

        shell.Register("rev", ctx =>
        {
            int code = 0;
            foreach (var (_, text) in Inputs(ctx, ctx.Args, () => code = 1))
                foreach (var line in SplitLines(text)) ctx.Stdout.WriteLine(new string(line.Reverse().ToArray()));
            return code;
        }, "Reverse each line");

        shell.Register("tac", ctx =>
        {
            int code = 0;
            foreach (var (_, text) in Inputs(ctx, ctx.Args, () => code = 1)) WriteLines(ctx.Stdout, Enumerable.Reverse(SplitLines(text)));
            return code;
        }, "Print lines in reverse order");

        shell.Register("paste", ctx =>
        {
            var a = Args.Parse(ctx.Args, "s", "d", "serial=s,delimiters=d");
            var delims = Unescape(a.Value('d') ?? "\t");
            if (delims.Length == 0) delims = "\0";
            char D(int i) => delims[i % delims.Length];
            string Join(IList<string> cells) { var sb = new StringBuilder(); for (int i = 0; i < cells.Count; i++) { if (i > 0 && D(i - 1) != '\0') sb.Append(D(i - 1)); sb.Append(cells[i]); } return sb.ToString(); }
            int code = 0;
            var columns = Inputs(ctx, a.Positional, () => code = 1).Select(i => SplitLines(i.Text)).ToList();
            if (a.Has('s')) foreach (var col in columns) ctx.Stdout.WriteLine(Join(col));
            else
            {
                int rows = columns.Count == 0 ? 0 : columns.Max(c => c.Count);
                for (int r = 0; r < rows; r++) ctx.Stdout.WriteLine(Join(columns.Select(c => r < c.Count ? c[r] : "").ToList()));
            }
            return code;
        }, "Merge lines of files");

        shell.Register("column", ctx =>
        {
            var a = Args.Parse(ctx.Args, "tn", "so", "table=t,separator=s,output-separator=o");
            int code = 0;
            var lines = Inputs(ctx, a.Positional, () => code = 1).SelectMany(i => SplitLines(i.Text)).ToList();
            if (!a.Has('t')) { WriteLines(ctx.Stdout, lines); return code; }
            var sep = a.Value('s');
            var rows = lines.Where(l => l.Trim().Length > 0)
                .Select(l => sep != null ? l.Split(Unescape(sep).ToCharArray(), StringSplitOptions.RemoveEmptyEntries) : l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToList();
            var outSep = a.Value('o') ?? "  ";
            int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
            var widths = Enumerable.Range(0, cols).Select(c => rows.Max(r => c < r.Length ? r[c].Length : 0)).ToArray();
            foreach (var r in rows)
                ctx.Stdout.WriteLine(string.Join(outSep, r.Select((cell, i) => i == r.Length - 1 ? cell : cell.PadRight(widths[i]))));
            return code;
        }, "Format input into aligned columns (-t)");

        shell.Register("base64", ctx =>
        {
            var a = Args.Parse(ctx.Args, "di", "w", "decode=d,ignore-garbage=i,wrap=w");
            int code = 0;
            var text = string.Concat(Inputs(ctx, a.Positional, () => code = 1).Select(i => i.Text));
            if (a.Has('d'))
            {
                try { ctx.Stdout.Write(Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c)))))); }
                catch (FormatException) { throw new ShellException("invalid input"); }
                return code;
            }
            var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            int wrap = int.Parse(a.Value('w') ?? "76");
            if (wrap > 0) enc = string.Join('\n', enc.Chunk(wrap).Select(c => new string(c)));
            ctx.Stdout.WriteLine(enc);
            return code;
        }, "Base64 encode/decode");

        foreach (var (name, hash) in new (string, Func<byte[], byte[]>)[] { ("md5sum", MD5.HashData), ("sha1sum", SHA1.HashData), ("sha256sum", SHA256.HashData), ("sha512sum", SHA512.HashData) })
        {
            shell.Register(name, ctx =>
            {
                int code = 0;
                var files = ctx.Args.Where(x => !x.StartsWith('-') || x == "-").ToList();
                foreach (var (n, text) in Inputs(ctx, files, () => code = 1))
                    ctx.Stdout.WriteLine($"{Hex(hash(Encoding.UTF8.GetBytes(text)))}  {(files.Count == 0 ? "-" : n)}");
                return code;
            }, $"Compute {name[..^3].ToUpperInvariant()} checksums");
        }

        shell.Register("mktemp", ctx =>
        {
            var a = Args.Parse(ctx.Args, "dqu", "pt", "directory=d,quiet=q,tmpdir=p");
            var template = a.Positional.FirstOrDefault() ?? "tmp.XXXXXXXXXX";
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var name = System.Text.RegularExpressions.Regex.Replace(template, "X{3,}$", m => new string(Enumerable.Range(0, m.Length).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray()));
            var path = template.Contains('/') && a.Value('p') == null ? ctx.ResolvePath(name) : VirtualFileSystem.Normalize(a.Value('p') ?? "/tmp", name);
            if (a.Has('d')) ctx.Fs.CreateDirectory(path); else ctx.Fs.WriteAllText(path, "");
            ctx.Stdout.WriteLine(path);
            return 0;
        }, "Create a temporary file or directory");

        shell.Register("seq", ctx =>
        {
            var args = ctx.Args;
            var nums = new List<string>();
            string sep = "\n";
            bool equalWidth = false;
            for (int i = 0; i < args.Count; i++)
            {
                var x = args[i];
                if (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) nums.Add(x);
                else if (x is "-w" or "--equal-width") equalWidth = true;
                else if (x == "-s") sep = i + 1 < args.Count ? args[++i] : throw new ShellException("option requires an argument -- 's'");
                else if (x.StartsWith("-s")) sep = x[2..];
                else if (x.StartsWith("--separator=")) sep = x[12..];
                else throw new ShellException($"invalid option -- '{x.TrimStart('-')}'");
            }
            if (nums.Count is 0 or > 3) throw new ShellException(nums.Count == 0 ? "missing operand" : $"extra operand '{nums[3]}'");
            var v = nums.Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();
            double first = v.Count > 1 ? v[0] : 1, step = v.Count == 3 ? v[1] : 1, last = v[^1];
            if (step == 0) throw new ShellException("invalid Zero increment value: '0'");
            int decimals = nums.Max(x => x.Contains('.') ? x.Length - x.IndexOf('.') - 1 : 0);
            var items = new List<string>();
            for (double x = first; step > 0 ? x <= last + 1e-9 : x >= last - 1e-9; x += step)
            {
                items.Add(x.ToString("F" + decimals, CultureInfo.InvariantCulture));
                if (items.Count > 1_000_000) throw new ShellException("too many numbers");
            }
            if (equalWidth && items.Count > 0) { int w = items.Max(s => s.Length); items = items.Select(s => s.PadLeft(w, '0')).ToList(); }
            if (items.Count > 0) ctx.Stdout.Write(string.Join(Unescape(sep), items) + "\n");
            return 0;
        }, "Print a sequence of numbers");

        shell.Register("diff", ctx => Diff.Run(ctx), "Compare files line by line (-u for unified)");

        // ---------- fake system info ----------
        shell.Register("id", ctx =>
        {
            var u = ctx.Shell.Options.User;
            var a = Args.Parse(ctx.Args, "ugnGr");
            ctx.Stdout.WriteLine(a.Has('u') || a.Has('g') ? (a.Has('n') ? u : "1000") : $"uid=1000({u}) gid=1000({u}) groups=1000({u})");
            return 0;
        }, "Print user and group IDs");
        shell.Register("uptime", ctx =>
        {
            ctx.Stdout.WriteLine(ctx.Args.Contains("-p") ? "up 3 days, 4 hours, 12 minutes" : $" {DateTime.Now:HH:mm:ss} up 3 days,  4:12,  1 user,  load average: 0.08, 0.03, 0.01");
            return 0;
        }, "Show how long the system has been running");
        shell.Register("free", ctx =>
        {
            bool h = ctx.Args.Any(x => x.Contains('h'));
            ctx.Stdout.Write(h
                ? "               total        used        free      shared  buff/cache   available\nMem:           7.6Gi       1.9Gi       3.8Gi        12Mi       2.1Gi       5.5Gi\nSwap:          2.0Gi          0B       2.0Gi\n"
                : "               total        used        free      shared  buff/cache   available\nMem:         8014880     1992344     3984512       12288     2201024     5748660\nSwap:        2097148           0     2097148\n");
            return 0;
        }, "Display memory usage");
        shell.Register("nproc", ctx => { ctx.Stdout.WriteLine("4"); return 0; }, "Print the number of processors");
    }

    static bool LooksJson(string t)
    {
        var s = t.TrimStart();
        if (s.Length == 0 || s[0] is not ('{' or '[')) return false;
        try { JsonDocument.Parse(t); return true; } catch (JsonException) { return false; }
    }
}

internal static class Diff
{
    enum Op { Equal, Delete, Insert }

    public static int Run(CommandContext ctx)
    {
        var a = Args.Parse(ctx.Args, "uqsiwbBNr", "U", "unified=u,brief=q,report-identical-files=s,ignore-case=i,ignore-all-space=w,ignore-space-change=b,recursive=r");
        if (a.Positional.Count != 2) throw new ShellException(a.Positional.Count < 2 ? $"missing operand after '{a.Positional.LastOrDefault() ?? "diff"}'" : $"extra operand '{a.Positional[2]}'", 2);
        string Read(string p)
        {
            if (p == "-") return ctx.StdinText;
            var abs = ctx.ResolvePath(p);
            if (!ctx.Fs.Exists(abs)) throw new ShellException($"{p}: No such file or directory", 2);
            if (ctx.Fs.IsDirectory(abs)) throw new ShellException($"{p}: Is a directory (directory diff is not supported)", 2);
            return ctx.Fs.ReadAllText(abs);
        }
        var (an, bn) = (a.Positional[0], a.Positional[1]);
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
        if (a.Has('u') || a.Value('U') != null) Unified(ctx.Stdout, ops, x, y, an, bn, int.Parse(a.Value('U') ?? "3"));
        else Normal(ctx.Stdout, ops, x, y);
        return 1;
    }

    /// <summary>LCS diff. shortcut: O(n*m) memory; switch to Myers if agents diff large files.</summary>
    static List<(Op Op, int A, int B)> Compute(List<string> x, List<string> y)
    {
        if ((long)x.Count * y.Count > 25_000_000) throw new ShellException("files too large to compare", 2);
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
            w.WriteLine($"@@ -{aStart},{aLen} +{bStart},{bLen} @@");
            foreach (var o in hunk)
                w.WriteLine(o.Op switch { Op.Equal => " " + x[o.A], Op.Delete => "-" + x[o.A], _ => "+" + y[o.B] });
        }
    }
}
