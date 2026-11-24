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

    static int LoopJump(CommandContext ctx, bool isBreak)
    {
        if (ctx.Shell.LoopDepth > 0) throw new LoopControl(isBreak, Math.Max(1, ParseCount(ctx, 1)));
        ctx.Stderr.WriteLine($"bash: {ctx.Name}: only meaningful in a `for', `while', or `until' loop");
        return 0;
    }

    static void RegisterScriptingCommands(Shell shell)
    {
        shell.Register("exit", (Func<CommandContext, int>)(ctx => throw new ExitSignal(ParseCount(ctx, ctx.Shell.LastExitCode) & 255)), "Exit the shell");
        shell.Register("return", (Func<CommandContext, int>)(ctx => throw new ReturnSignal(ParseCount(ctx, ctx.Shell.LastExitCode) & 255)), "Return from a function");
        shell.Register("break", (Func<CommandContext, int>)(ctx => LoopJump(ctx, true)), "Exit a loop");
        shell.Register("continue", (Func<CommandContext, int>)(ctx => LoopJump(ctx, false)), "Next loop iteration");

        shell.Register("shift", ctx =>
        {
            int n = ParseCount(ctx, 1);
            var pos = ctx.Shell.Positional;
            if (n > pos.Count) return 1;
            ctx.Shell.Positional = pos.Skip(n).ToList();
            return 0;
        }, "Shift positional parameters");

        shell.Register("trap", ctx =>
        {
            // EXIT actions run when the tool call or script ends; other signals never arrive here
            var args = ctx.Args.Where(a => a != "--").ToList();
            var traps = ctx.Shell.Traps;
            if (args.Count == 0 || args is ["-p"])
            {
                foreach (var (sig, action) in traps) ctx.Stdout.WriteLine($"trap -- '{action}' {sig}");
                return 0;
            }
            if (args[0] == "-l") { ctx.Stdout.WriteLine(" 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 9) SIGKILL\t15) SIGTERM"); return 0; }
            var (act, signals) = args.Count == 1 ? (null, args) : (args[0], args.Skip(1).ToList());
            foreach (var sig in signals)
            {
                var name = sig.ToUpperInvariant();
                if (name.StartsWith("SIG")) name = name[3..];
                if (name == "0") name = "EXIT";
                if (act is null or "-") traps.Remove(name);
                else traps[name] = act;
            }
            return 0;
        }, "Run a command when the shell exits (EXIT)");

        shell.Register("shopt", ctx =>
        {
            string[] known = ["dotglob", "expand_aliases", "extglob", "failglob", "globstar", "inherit_errexit", "lastpipe", "nocaseglob", "nullglob"];
            var a = Args.Parse(ctx.Args, "supqo");
            var names = a.Positional.Count > 0 ? a.Positional.ToList() : known.ToList();
            if (a.Has('s') || a.Has('u'))
            {
                foreach (var n in names)
                {
                    if (!known.Contains(n)) throw new ShellException($"{n}: invalid shell option name");
                    if (a.Has('s')) ctx.Shell.Shopts.Add(n); else ctx.Shell.Shopts.Remove(n);
                }
                return 0;
            }
            int code = 0;
            foreach (var n in names)
            {
                bool on = ctx.Shell.Shopts.Contains(n);
                if (code == 0 && !on) code = 1;
                if (!a.Has('q')) ctx.Stdout.WriteLine($"{n,-15}\t{(on ? "on" : "off")}");
            }
            return code;
        }, "Set shell options (nullglob, dotglob, globstar)");

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
                    else if (c == 'u') ctx.Shell.Nounset = on;
                    else if (c == 'o')
                    {
                        var opt = i + 1 < args.Count ? args[++i] : throw new ShellException("-o: option name required", 2);
                        if (opt == "pipefail") ctx.Shell.Pipefail = on;
                        else if (opt == "errexit") ctx.Shell.Errexit = on;
                        else if (opt == "nounset") ctx.Shell.Nounset = on;
                        // xtrace, noclobber...: accepted, not enforced (bash-like, simplified)
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
            if (a.Has('a'))
            {
                // read -a NAME: every field becomes an element
                ctx.Shell.Unset(names[0]);
                var array = ctx.Shell.Writable(names[0]);
                foreach (var field in Shell.SplitFields(line, env.GetValueOrDefault("IFS", " \t\n"))) array.Set(array.Next.ToString(), field);
            }
            else if (a.Positional.Count == 0) env["REPLY"] = line;
            else
            {
                var fields = Shell.SplitFields(line, env.GetValueOrDefault("IFS", " \t\n"), names.Count);
                for (int i = 0; i < names.Count; i++) env[names[i]] = i < fields.Count ? fields[i] : "";
            }
            // like bash, a final line with no trailing newline is still assigned, but read reports end of input
            return ctx.LastLineTerminated ? 0 : 1;
        }, "Read a line from stdin into variables");

        CommandHandler mapfile = ctx =>
        {
            var a = Args.Parse(ctx.Args, "t", "dnsuCc");
            var name = a.Positional.Count > 0 ? a.Positional[0] : "MAPFILE";
            ctx.Shell.Unset(name);
            var array = ctx.Shell.Writable(name);
            while (ctx.ReadLine() is { } line)
                array.Set(array.Next.ToString(), a.Has('t') || !ctx.LastLineTerminated ? line : line + "\n");
            return Task.FromResult(0);
        };
        shell.Register("mapfile", mapfile, "Read lines from stdin into an array (-t drops the newlines)");
        shell.Register("readarray", mapfile, "Read lines from stdin into an array (-t drops the newlines)");

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
                else if (ctx.Shell.Functions.ContainsKey(n) || IsShellBuiltin(ctx.Shell, n)) ctx.Stdout.WriteLine(n);
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
            foreach (var p in a.Positional)
            {
                var abs = ctx.ResolvePath(p);
                if (!ctx.Fs.Exists(abs)) { ctx.Stderr.WriteLine($"stat: cannot statx '{p}': No such file or directory"); code = 1; continue; }
                var st = ctx.Fs.Stat(abs);
                var type = st.IsDirectory ? "directory" : st.Size == 0 ? "regular empty file" : "regular file";
                var mode = Convert.ToString(st.Mode, 8).PadLeft(3, '0');
                var rwx = VirtualFileSystem.FormatMode(st.IsDirectory, st.Mode);
                var owner = st.Owner;
                var uid = owner == "root" ? "0" : "1000";
                var mtime = st.Modified.ToString("yyyy-MM-dd HH:mm:ss.fffffff00 +0000", CultureInfo.InvariantCulture);
                if (a.Value('c') is { } fmt)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < fmt.Length; i++)
                    {
                        if (fmt[i] != '%' || i + 1 >= fmt.Length) { sb.Append(fmt[i]); continue; }
                        sb.Append(fmt[++i] switch
                        {
                            'n' => p, 's' => st.Size.ToString(), 'F' => type, 'a' => mode, 'A' => rwx, 'U' => owner, 'G' => owner,
                            'u' => uid, 'g' => uid, 'y' => mtime, 'Y' => new DateTimeOffset(st.Modified).ToUnixTimeSeconds().ToString(),
                            'b' => ((st.Size + 511) / 512).ToString(), 'i' => (Math.Abs(abs.GetHashCode()) % 1000000).ToString(), '%' => "%",
                            var c => "%" + c,
                        });
                    }
                    ctx.Stdout.WriteLine(sb.ToString());
                    continue;
                }
                ctx.Stdout.Write(
                    $"  File: {p}\n  Size: {st.Size,-10}\tBlocks: {(st.Size + 511) / 512 * 8,-10} IO Block: 4096   {type}\n" +
                    $"Access: ({mode.PadLeft(4, '0')}/{rwx})  Uid: ({uid,5}/{owner,8})   Gid: ({uid,5}/{owner,8})\n" +
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

        shell.Register("chmod", Chmod.Run, "Change file mode bits");

        CommandHandler chown = ctx =>
        {
            var files = ctx.Args.Where(x => !x.StartsWith('-')).Skip(1).ToList(); // first operand is the owner
            if (files.Count == 0) throw new ShellException("missing operand");
            var owner = ctx.Args.First(x => !x.StartsWith('-')).Split(':')[0];
            int code = 0;
            foreach (var f in files)
            {
                var path = ctx.ResolvePath(f);
                if (!ctx.Fs.Exists(path)) { ctx.Stderr.WriteLine($"{ctx.Name}: cannot access '{f}': No such file or directory"); code = 1; continue; }
                try { ctx.Fs.SetOwner(path, owner.Length > 0 ? owner : ctx.Fs.Stat(path).Owner, ctx.Args.Contains("-R")); }
                catch (ShellException) { ctx.Stderr.WriteLine($"{ctx.Name}: changing ownership of '{f}': Operation not permitted"); code = 1; }
            }
            return Task.FromResult(code);
        };
        shell.Register("chown", chown, "Change file owner");
        shell.Register("chgrp", chown, "Change file group");

        // the agent is a regular user: privilege escalation fails the way it does on a real server
        shell.Register("sudo", ctx =>
        {
            var user = ctx.Shell.Options.User;
            if (ctx.Args is ["-V"] or ["--version"]) { ctx.Stdout.WriteLine("Sudo version 1.9.15p5"); return 0; }
            if (ctx.Args.Count == 0) { ctx.Stderr.WriteLine("usage: sudo -h | -K | -k | -V\nusage: sudo [-AbEHknPS] [-u user] [command [arg ...]]"); return 1; }
            if (ctx.Args[0] is "-l" or "--list") { ctx.Stderr.WriteLine($"Sorry, user {user} may not run sudo on {ctx.Shell.Options.Hostname}."); return 1; }
            ctx.Stderr.WriteLine($"{user} is not in the sudoers file.");
            return 1;
        }, "Execute a command as another user");
        shell.Register("su", ctx =>
        {
            ctx.Stderr.WriteLine("Password: \nsu: Authentication failure");
            return 1;
        }, "Run a command with a substitute user ID");

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
                    var data = ctx.Fs.ReadAllBytes(abs);
                    var t = Encoding.UTF8.GetString(data);
                    desc = t.Length == 0 ? "empty"
                        : MarkdownConverter.FileType(data) is { } magic ? magic
                        : t.StartsWith("\u007fELF") ? "ELF 64-bit LSB pie executable, x86-64, version 1 (SYSV), dynamically linked, stripped"
                        : t.StartsWith("#!") ? (t.Contains("python") ? "Python script" : "Bourne-Again shell script") + ", ASCII text executable"
                        : LooksJson(t) ? "JSON text data"
                        : t.Contains('\0') || !Encoding.UTF8.GetBytes(t).AsSpan().SequenceEqual(data) ? "data" // NUL bytes or invalid UTF-8
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
            List<List<string>> columns;
            int dashes = a.Positional.Count(f => f == "-");
            if (dashes > 1 && !a.Has('s'))
            {
                // several "-" operands take turns reading lines from stdin (seq 6 | paste - - -)
                var stdinLines = SplitLines(ctx.StdinText);
                columns = [];
                int k = 0;
                foreach (var f in a.Positional)
                {
                    if (f == "-") { int start = k++; columns.Add(stdinLines.Where((_, idx) => idx % dashes == start).ToList()); }
                    else columns.AddRange(Inputs(ctx, [f], () => code = 1).Select(i => SplitLines(i.Text)));
                }
            }
            else columns = Inputs(ctx, a.Positional, () => code = 1).Select(i => SplitLines(i.Text)).ToList();
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
            var data = InputBytes(ctx, a.Positional, () => code = 1).SelectMany(i => i.Text).ToArray();
            if (a.Has('d'))
            {
                try { ctx.Stdout.Write(Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(Encoding.UTF8.GetString(data).Where(c => !char.IsWhiteSpace(c)))))); }
                catch (FormatException) { throw new ShellException("invalid input"); }
                return code;
            }
            var enc = Convert.ToBase64String(data);
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
                foreach (var (n, data) in InputBytes(ctx, files, () => code = 1))
                    ctx.Stdout.WriteLine($"{Hex(hash(data))}  {(files.Count == 0 ? "-" : n)}");
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
            string? fmt = null;
            for (int i = 0; i < args.Count; i++)
            {
                var x = args[i];
                if (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) nums.Add(x);
                else if (x is "-w" or "--equal-width") equalWidth = true;
                else if (x == "-s") sep = i + 1 < args.Count ? args[++i] : throw new ShellException("option requires an argument -- 's'");
                else if (x.StartsWith("-s")) sep = x[2..];
                else if (x.StartsWith("--separator=")) sep = x[12..];
                else if (x is "-f" or "--format") fmt = i + 1 < args.Count ? args[++i] : throw new ShellException("option requires an argument -- 'f'");
                else if (x.StartsWith("-f")) fmt = x[2..];
                else if (x.StartsWith("--format=")) fmt = x[9..];
                else throw new ShellException($"invalid option -- '{x.TrimStart('-')}'");
            }
            if (nums.Count is 0 or > 3) throw new ShellException(nums.Count == 0 ? "missing operand" : $"extra operand '{nums[3]}'");
            var v = nums.Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();
            double first = v.Count > 1 ? v[0] : 1, step = v.Count == 3 ? v[1] : 1, last = v[^1];
            if (step == 0) throw new ShellException("invalid Zero increment value: '0'");
            int decimals = nums.Max(x => x.Contains('.') ? x.Length - x.IndexOf('.') - 1 : 0);
            var items = new List<string>();
            for (long k = 0; ; k++)
            {
                double x = first + k * step; // not a running sum, so 0.1 steps don't drift
                if (!(step > 0 ? x <= last + 1e-9 : x >= last - 1e-9)) break;
                items.Add(fmt != null ? Printf.Format(fmt, [x.ToString("R", CultureInfo.InvariantCulture)]) : x.ToString("F" + decimals, CultureInfo.InvariantCulture));
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

/// <summary>chmod with octal (755) and symbolic (u+x, go-w, a=r, +x) modes, -R, -v and -c.</summary>
internal static class Chmod
{
    public static Task<int> Run(CommandContext ctx)
    {
        bool recursive = false, verbose = false, changes = false, quiet = false;
        string? spec = null;
        var files = new List<string>();
        foreach (var arg in ctx.Args)
        {
            if (arg is "-R" or "--recursive") recursive = true;
            else if (arg is "-v" or "--verbose") verbose = true;
            else if (arg is "-c" or "--changes") changes = true;
            else if (arg is "-f" or "--silent" or "--quiet") quiet = true;
            else if (spec == null) spec = arg; // may look like an option, e.g. -x or -w
            else files.Add(arg);
        }
        if (spec == null || files.Count == 0) throw new ShellException(spec == null ? "missing operand" : $"missing operand after '{spec}'");
        int code = 0;
        void Apply(string display, string path)
        {
            var st = ctx.Fs.Stat(path);
            var mode = Parse(spec, st.Mode, st.IsDirectory);
            try { ctx.Fs.Chmod(path, mode); }
            catch (ShellException) { if (!quiet) ctx.Stderr.WriteLine($"chmod: changing permissions of '{display}': Operation not permitted"); code = 1; return; }
            string Fmt(int m) => $"{Convert.ToString(m, 8).PadLeft(4, '0')} ({VirtualFileSystem.FormatMode(st.IsDirectory, m)[1..]})";
            if (mode != st.Mode && (verbose || changes)) ctx.Stdout.WriteLine($"mode of '{display}' changed from {Fmt(st.Mode)} to {Fmt(mode)}");
            else if (mode == st.Mode && verbose) ctx.Stdout.WriteLine($"mode of '{display}' retained as {Fmt(mode)}");
            if (recursive && st.IsDirectory && ctx.Fs.CanRead(path))
                foreach (var n in ctx.Fs.List(path)) Apply(display.TrimEnd('/') + "/" + n, VirtualFileSystem.Normalize(path, n));
        }
        foreach (var f in files)
        {
            var path = ctx.ResolvePath(f);
            if (!ctx.Fs.Exists(path)) { ctx.Stderr.WriteLine($"chmod: cannot access '{f}': No such file or directory"); code = 1; continue; }
            Apply(f, path);
        }
        return Task.FromResult(code);
    }

    public static int Parse(string spec, int mode, bool isDir)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(spec, "^[0-7]{1,4}$")) return Convert.ToInt32(spec, 8);
        foreach (var clause in spec.Split(','))
        {
            var m = System.Text.RegularExpressions.Regex.Match(clause, "^([ugoa]*)([-+=])([rwxXt]*)$");
            if (!m.Success) throw new ShellException($"invalid mode: '{spec}'");
            var who = m.Groups[1].Value is "" or "a" ? "ugo" : m.Groups[1].Value.Replace("a", "ugo");
            int bits = 0;
            bool sticky = false;
            foreach (var c in m.Groups[3].Value)
                switch (c)
                {
                    case 'r': bits |= 4; break;
                    case 'w': bits |= 2; break;
                    case 'x': bits |= 1; break;
                    case 'X': if (isDir || (mode & 0b001_001_001) != 0) bits |= 1; break;
                    case 't': sticky = true; break;
                }
            int mask = 0, clear = 0;
            foreach (var w in who)
            {
                int shift = w == 'u' ? 6 : w == 'g' ? 3 : 0;
                mask |= bits << shift;
                clear |= 7 << shift;
            }
            int stickyBit = sticky ? 0x200 : 0;
            mode = m.Groups[2].Value switch
            {
                "+" => mode | mask | stickyBit,
                "-" => mode & ~(mask | stickyBit),
                _ => (mode & ~clear) | mask | stickyBit,
            };
        }
        return mode;
    }
}
