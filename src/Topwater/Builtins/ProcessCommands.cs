// Processes and time: ps, wait, jobs, kill, ulimit, umask, sleep, timeout, time. See docs/architecture.md.
using System.Globalization;

namespace Topwater;

internal static partial class Builtins
{
    static TimeSpan ParseDuration(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+(?:\.\d+)?)([smhd]?)$");
        if (!m.Success) throw new ShellException(CommandSpec.Message("sleep", "invalidTimeInterval", ("value", s)));
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch { "m" => TimeSpan.FromMinutes(n), "h" => TimeSpan.FromHours(n), "d" => TimeSpan.FromDays(n), _ => TimeSpan.FromSeconds(n) };
    }

    /// <summary>`umask 027`: octal digits up to 07777, of which the permission bits count; null when out of range.</summary>
    static int? OctalMask(string mode)
    {
        int v = 0;
        foreach (var c in mode)
            if (c is < '0' or > '7' || (v = v * 8 + (c - '0')) > 0xFFF) return null;
        return v & 0x1FF;
    }

    /// <summary>`umask u=rwx,g+w,o-r`: clauses change the permissions the mask leaves; null (and the offending character) when malformed.</summary>
    static int? SymbolicMask(string mode, int mask, out char bad, out bool badOperator)
    {
        int allowed = ~mask & 0x1FF, i = 0;
        bad = ' ';
        badOperator = false;
        while (true)
        {
            int who = 0;
            for (; i < mode.Length && "ugoa".Contains(mode[i]); i++) who |= mode[i] switch { 'u' => 0x1C0, 'g' => 0x38, 'o' => 7, _ => 0x1FF };
            if (who == 0) who = 0x1FF;
            if (i >= mode.Length || !"+-=".Contains(mode[i])) { bad = i < mode.Length ? mode[i] : ' '; badOperator = true; return null; }
            while (i < mode.Length && "+-=".Contains(mode[i]))
            {
                char op = mode[i++];
                int bits = 0;
                for (; i < mode.Length && "rwx".Contains(mode[i]); i++) bits |= (mode[i] switch { 'r' => 4, 'w' => 2, _ => 1 }) * 0x49;
                bits &= who;
                allowed = op == '+' ? allowed | bits : op == '-' ? allowed & ~bits : allowed & ~who | bits;
            }
            if (i >= mode.Length) return ~allowed & 0x1FF;
            if (mode[i] != ',') { bad = mode[i]; return null; }
            i++;
        }
    }

    static void RegisterProcessCommands(Shell shell)
    {
        shell.Register("ps", ctx =>
        {
            ctx.Stdout.Write("    PID TTY          TIME CMD\n      1 pts/0    00:00:00 bash\n   4242 pts/0    00:00:00 ps\n");
            return 0;
        });
        shell.Register("wait", ctx =>
        {
            // background jobs ran to completion when they were started: wait just reports their status
            int code = 0;
            var ids = ctx.Args.Where(a => !a.StartsWith('-')).ToList();
            if (ctx.Args.Contains("-n") && ids.Count == 0) return ctx.Shell.WaitNext();
            if (ids.Count == 0) ctx.Shell.ForgetJobs();
            foreach (var id in ids)
            {
                if (id.StartsWith('%')) { code = 0; continue; }
                if (!int.TryParse(id, out var pid)) { ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}wait: " + CommandSpec.Message("wait", "notPid", ("id", id))); code = 2; continue; }
                code = ctx.Shell.WaitFor(pid);
                if (code == 127) ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}wait: " + CommandSpec.Message("wait", "notChild", ("pid", pid.ToString())));
            }
            return code;
        });
        shell.Register("jobs", _ => 0);
        shell.Register("disown", _ => 0);

        shell.Register("kill", ctx =>
        {
            if (ctx.Args is ["-l", ..]) { ctx.Stdout.WriteLine(" 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 9) SIGKILL\t15) SIGTERM"); return 0; }
            int signal = 15, code = 0;
            var targets = new List<string>();
            for (int i = 0; i < ctx.Args.Count; i++)
            {
                var a = ctx.Args[i];
                if (a is "-s" or "-n" && i + 1 < ctx.Args.Count) { a = "-" + ctx.Args[++i]; }
                if (a.StartsWith('-') && targets.Count == 0)
                {
                    var sig = a[1..].ToUpperInvariant().Replace("SIG", "");
                    signal = int.TryParse(sig, out var num) ? num : sig switch { "HUP" => 1, "INT" => 2, "QUIT" => 3, "KILL" => 9, "TERM" => 15, _ => 15 };
                    continue;
                }
                targets.Add(a);
            }
            if (targets.Count == 0) throw new ShellException(CommandSpec.Message("kill", "usage"), 2);
            foreach (var t in targets)
            {
                if (t == "4242") { if (signal != 0) throw new ExitSignal(128 + signal); continue; } // the shell itself
                ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}kill: " + (t.StartsWith('%') ? CommandSpec.Message("kill", "noSuchJob", ("job", t)) : CommandSpec.Message("kill", "noSuchProcess", ("pid", t))));
                code = 1;
            }
            return code;
        });

        shell.Register("ulimit", ctx =>
        {
            if (ctx.Args.Any(a => !a.StartsWith('-'))) return 0; // setting a limit: accepted
            ctx.Stdout.WriteLine(ctx.Args.Contains("-n") ? "1024" : ctx.Args.Contains("-s") ? "8192" : ctx.Args.Contains("-c") ? "0" : "unlimited");
            return 0;
        });
        shell.Register("times", ctx => { ctx.Stdout.Write("0m0.010s 0m0.005s\n0m0.000s 0m0.000s\n"); return 0; });
        shell.Register("umask", ctx =>
        {
            bool symbolic = false, reusable = false;
            int i = 0;
            for (; i < ctx.Args.Count && ctx.Args[i].Length > 1 && ctx.Args[i][0] == '-'; i++)
            {
                if (ctx.Args[i] == "--") { i++; break; }
                foreach (var c in ctx.Args[i][1..])
                    if (c == 'S') symbolic = true;
                    else if (c == 'p') reusable = true;
                    else
                    {
                        ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}umask: " + CommandSpec.Message("umask", "builtinInvalidOption", ("option", "-" + c)));
                        ctx.Stderr.WriteLine("umask: " + CommandSpec.Message("umask", "usage"));
                        return 2;
                    }
            }
            if (i < ctx.Args.Count)
            {
                var mode = ctx.Args[i];
                char bad = ' ';
                bool badOperator = false, octal = mode.Length > 0 && char.IsAsciiDigit(mode[0]);
                int? mask = octal ? OctalMask(mode) : SymbolicMask(mode, ctx.Fs.Umask, out bad, out badOperator);
                if (mask == null)
                {
                    ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}umask: " + (octal
                        ? CommandSpec.Message("umask", "octalOutOfRange", ("value", mode))
                        : CommandSpec.Message("umask", badOperator ? "invalidOperator" : "invalidSymbolic", ("char", bad.ToString()))));
                    return 1;
                }
                ctx.Fs.Umask = mask.Value;
                if (!symbolic) return 0;
            }
            int allowed = ~ctx.Fs.Umask & 0x1FF;
            string Perms(int shift) => string.Concat("rwx".Where((_, k) => (allowed >> (shift + 2 - k) & 1) != 0));
            var shown = symbolic ? $"u={Perms(6)},g={Perms(3)},o={Perms(0)}" : Convert.ToString(ctx.Fs.Umask, 8).PadLeft(4, '0');
            ctx.Stdout.WriteLine(reusable ? $"umask {(symbolic ? "-S " : "")}{shown}" : shown);
            return 0;
        });

        shell.Register("sleep", async ctx =>
        {
            if (ctx.Args.Count == 0) throw new UsageException(CommandSpec.Message("sleep", "missingOperand"));
            var total = ctx.Args.Aggregate(TimeSpan.Zero, (t, a) => t + ParseDuration(a));
            await ctx.Shell.SleepAsync(total, ctx.CancellationToken);
            return 0;
        });

        shell.Register("timeout", async ctx =>
        {
            var args = ctx.Args.SkipWhile(a => a.StartsWith('-')).ToList();
            if (args.Count < 2) throw new ShellException(CommandSpec.Message("timeout", "missingOperand"), 125);
            if (ExecFailure(ctx, args[1], out _) is { } error)
            {
                ctx.Stderr.WriteLine("timeout: " + CommandSpec.Message("timeout", "failedToRun", ("command", args[1]), ("errno", error)));
                return error == CommandSpec.Errno.EACCES ? 126 : 127;
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            cts.CancelAfter(ParseDuration(args[0]));
            try { return await ctx.Shell.WithVirtualTimeoutAsync(ParseDuration(args[0]), () => ctx.Shell.InvokeAsync(args.Skip(1).ToList(), ctx.Io, cts.Token)) ?? 124; }
            catch (OperationCanceledException) when (!ctx.CancellationToken.IsCancellationRequested) { return 124; }
        });

        shell.Register("time", async ctx =>
        {
            var elapsed = ctx.Shell.StartTimer();
            int code = ctx.Args.Count == 0 ? 0 : await ctx.Shell.InvokeAsync(ctx.Args.ToList(), ctx.Io, ctx.CancellationToken);
            var t = elapsed();
            ctx.Stderr.Write($"\nreal\t{(int)t.TotalMinutes}m{t.TotalSeconds % 60:0.000}s\nuser\t0m0.000s\nsys\t0m0.000s\n");
            return code;
        });
    }
}
