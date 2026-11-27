// System identity and packages: whoami, hostname, uname, date, sudo, apt, id, uptime, free, nproc. See docs/architecture.md.
using System.Globalization;

namespace Topwater;

internal static partial class Builtins
{
    static readonly string[] IsoUnits = ["hours", "minutes", "date", "seconds", "ns"];
    static readonly string[] IsoFormats = ["%Y-%m-%dT%H%:z", "%Y-%m-%dT%H:%M%:z", "%Y-%m-%d", "%Y-%m-%dT%H:%M:%S%:z", "%Y-%m-%dT%H:%M:%S,%N%:z"];

    static readonly Dictionary<string, int> DateUnits = new()
    {
        ["second"] = 1, ["sec"] = 1, ["minute"] = 60, ["min"] = 60, ["hour"] = 3600, ["day"] = 86400, ["week"] = 604800, ["fortnight"] = 1209600,
    };

    static readonly string[] MonthAbbr = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    static readonly string[] DayAbbr = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    static readonly Dictionary<string, int> ZoneHours = new()
    {
        ["utc"] = 0, ["gmt"] = 0, ["ut"] = 0, ["z"] = 0, ["est"] = -5, ["edt"] = -4, ["cst"] = -6, ["cdt"] = -5,
        ["mst"] = -7, ["mdt"] = -6, ["pst"] = -8, ["pdt"] = -7,
    };

    internal static DateTime ParseDate(string text, DateTime now)
    {
        // a date past what DateTime holds (year 9999; GNU goes much further) is refused like any other bad date
        try { return ParseDateOrThrow(text, now); }
        catch (Exception e) when (e is ArgumentOutOfRangeException or OverflowException)
        {
            throw new ShellException(CommandSpec.Message("date", "invalidDate", ("value", text)));
        }
    }

    static DateTime ParseDateOrThrow(string text, DateTime now)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var bad = new ShellException(CommandSpec.Message("date", "invalidDate", ("value", text)));
        var s = text.Trim().ToLowerInvariant();
        var epoch = System.Text.RegularExpressions.Regex.Match(s, @"^@(-?\d+(?:\.\d+)?)$");
        if (epoch.Success) return DateTime.UnixEpoch.AddSeconds(double.Parse(epoch.Groups[1].Value, inv));

        int? year = null, month = null, day = null, zone = null; // zone in minutes east of UTC
        TimeSpan? time = null;
        int? weekday = null, dayShift = null;
        var relative = new List<(int N, string Unit)>();
        const string Months = "(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\\.?";
        System.Text.RegularExpressions.Match m;
        bool Eat(string pattern)
        {
            m = System.Text.RegularExpressions.Regex.Match(s, "^(?:" + pattern + ")");
            if (!m.Success) return false;
            s = s[m.Length..].TrimStart(' ', '\t', ',');
            return true;
        }
        int G(int g) => int.Parse(m.Groups[g].Value, inv);
        int MonthOf(int g) => Array.IndexOf(MonthAbbr, m.Groups[g].Value[..3]) + 1;
        static int FullYear(int y, int digits) => digits > 2 ? y : y < 69 ? 2000 + y : 1900 + y;
        void SetDate(int? y, int mo, int d) { if (day != null) throw bad; year = y ?? year; month = mo; day = d; }
        void SetZone(int minutes) { if (zone != null) throw bad; zone = minutes; }

        bool afterTime = false;
        while (s.Length > 0)
        {
            // a signed number right after a time of day is its zone ("10:00 +2" is UTC+2), anywhere else it's relative
            if (afterTime && Eat(@"([+-])\s*(\d{1,2}:\d{2}|\d{1,4})\b"))
            {
                afterTime = false;
                var v = m.Groups[2].Value.Replace(":", "");
                int minutes = v.Length <= 2 ? int.Parse(v, inv) * 60 : int.Parse(v[..^2], inv) * 60 + int.Parse(v[^2..], inv);
                SetZone(m.Groups[1].Value == "-" ? -minutes : minutes);
                continue;
            }
            bool wasTime = false;
            if (Eat(@"(\d{4})-(\d{1,2})-(\d{1,2})(?:t(?=\d))?")) SetDate(G(1), G(2), G(3));
            else if (Eat(@"(\d{1,2})/(\d{1,2})(?:/(\d{2}|\d{4}))?\b"))
                SetDate(m.Groups[3].Success ? FullYear(G(3), m.Groups[3].Length) : null, G(1), G(2));
            else if (Eat(@"(\d{4})(\d{2})(\d{2})\b")) SetDate(G(1), G(2), G(3));
            else if (Eat(@"(\d{1,2}):(\d{2})(?::(\d{2})(?:[.,](\d+))?)?\s*(?:(a|p)\.?m\.?\b)?"))
            {
                if (time != null) throw bad;
                int h = G(1);
                if (m.Groups[5].Success) { if (h is < 1 or > 12) throw bad; h = h % 12 + (m.Groups[5].Value == "p" ? 12 : 0); }
                double frac = m.Groups[4].Success ? double.Parse("0." + m.Groups[4].Value, inv) : 0;
                if (h > 23 || G(2) > 59 || (m.Groups[3].Success && G(3) > 60)) throw bad;
                time = new TimeSpan(h, G(2), m.Groups[3].Success ? G(3) : 0) + TimeSpan.FromSeconds(frac);
                wasTime = true;
            }
            else if (Eat(@"(\d{1,2})\s*(a|p)\.?m\.?\b"))
            {
                if (time != null || G(1) is < 1 or > 12) throw bad;
                time = TimeSpan.FromHours(G(1) % 12 + (m.Groups[2].Value == "p" ? 12 : 0));
                wasTime = true;
            }
            else if (Eat(@"(\d{1,2})(?:st|nd|rd|th)?[\s-]*" + Months + @"(?:[\s-]+(\d{4})\b)?"))
                SetDate(m.Groups[3].Success ? G(3) : null, MonthOf(2), G(1));
            else if (Eat(Months + @"\s*(\d{1,2})(?:st|nd|rd|th)?\b(?:,?\s*(\d{4})\b)?"))
                SetDate(m.Groups[3].Success ? G(3) : null, MonthOf(1), G(2));
            else if (Eat(@"(next|last|this|[+-]?\s*\d+)?\s*(second|sec|minute|min|hour|day|week|fortnight|month|year)s?\b(\s+ago\b)?"))
            {
                var word = m.Groups[1].Success ? m.Groups[1].Value.Replace(" ", "") : "1";
                int n = word switch { "next" => 1, "last" => -1, "this" => 0, _ => int.Parse(word, inv) };
                relative.Add((m.Groups[3].Success ? -n : n, m.Groups[2].Value));
            }
            else if (Eat(@"(utc|gmt|ut|z|[ecmp][sd]t)\b(?:([+-])(\d{1,2})(?::?(\d{2}))?)?"))
            {
                int offset = ZoneHours[m.Groups[1].Value] * 60;
                if (m.Groups[2].Success) offset += (m.Groups[2].Value == "-" ? -1 : 1) * (G(3) * 60 + (m.Groups[4].Success ? G(4) : 0));
                SetZone(offset);
            }
            else if (Eat(@"([+-])(\d{2}):?(\d{2})\b")) SetZone((m.Groups[1].Value == "-" ? -1 : 1) * (G(2) * 60 + G(3)));
            else if (Eat(@"(sun|mon|tue|wed|thu|fri|sat)[a-z]*\.?\b"))
            {
                if (weekday != null) throw bad;
                weekday = Array.IndexOf(DayAbbr, m.Groups[1].Value);
            }
            else if (Eat(@"(today|now|yesterday|tomorrow)\b"))
            {
                if (dayShift != null) throw bad;
                dayShift = m.Groups[1].Value switch { "yesterday" => -1, "tomorrow" => 1, _ => 0 };
            }
            else if (year == null && day != null && Eat(@"(\d{4})\b")) year = G(1); // "Fri Nov 20 13:30:00 UTC 2026", date's own output
            else throw bad;
            afterTime = wasTime;
        }

        // absolute items set the date and time; anything not given comes from now, and a date alone means midnight
        bool absolute = day != null || time != null || weekday != null;
        DateTime t;
        try { t = new DateTime(year ?? now.Year, month ?? now.Month, day ?? now.Day, 0, 0, 0, DateTimeKind.Utc); }
        catch (ArgumentOutOfRangeException) { throw bad; }
        t += time ?? (absolute ? TimeSpan.Zero : now.TimeOfDay);
        if (zone is { } z) t = t.AddMinutes(-z);
        if (weekday is { } w) t = t.AddDays(((w - (int)t.DayOfWeek) % 7 + 7) % 7);
        if (dayShift is { } shift) t = t.AddDays(shift);
        foreach (var (n, unit) in relative)
        {
            if (unit is "month" or "year")
            {
                // like GNU date, a day past the end of the month rolls over (Jan 31 + 1 month = Mar 2 or 3)
                int months = t.Month - 1 + n * (unit == "year" ? 12 : 1);
                int y = t.Year + (int)Math.Floor(months / 12.0), mo = ((months % 12) + 12) % 12 + 1;
                t = new DateTime(y, mo, 1, t.Hour, t.Minute, t.Second, DateTimeKind.Utc).AddTicks(t.Ticks % TimeSpan.TicksPerSecond).AddDays(t.Day - 1);
            }
            else t = t.AddSeconds((double)n * DateUnits[unit]);
        }
        return t;
    }

    static void RegisterSystemCommands(Shell shell)
    {
        shell.Register("whoami", ctx => { ctx.Stdout.WriteLine(ctx.Shell.Options.User); return 0; });
        shell.Register("hostname", ctx => { ctx.Stdout.WriteLine(ctx.Shell.Options.Hostname); return 0; });

        shell.Register("uname", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("uname"));
            var host = ctx.Shell.Options.Hostname;
            var parts = new List<string>();
            bool all = a.Has('a');
            if (all || a.Has('s') || a.Flags.Count == 0) parts.Add("Linux");
            if (all || a.Has('n')) parts.Add(host);
            if (all || a.Has('r')) parts.Add("6.8.0-generic");
            if (all || a.Has('v')) parts.Add("#1 SMP PREEMPT_DYNAMIC");
            if (all || a.Has('m')) parts.Add("x86_64");
            if (all || a.Has('o')) parts.Add("GNU/Linux");
            ctx.Stdout.WriteLine(string.Join(' ', parts));
            return 0;
        });

        shell.Register("date", ctx =>
        {
            // the simulated machine's clock is UTC (TZ is unset), so output never depends on the host's time zone
            // -I[UNIT] / --iso-8601[=UNIT] take an optional value, so they are read here, before the shared parser
            string? iso = null;
            var args = ctx.Args.Where(x =>
            {
                string? unit = x == "--iso-8601" ? "" : x.StartsWith("--iso-8601=", StringComparison.Ordinal) ? x[11..] : x.StartsWith("-I", StringComparison.Ordinal) ? x[2..] : null;
                if (unit == null) return true;
                iso = unit == "" ? "date" : IsoUnits.FirstOrDefault(u => u.StartsWith(unit, StringComparison.Ordinal))
                    ?? throw new UsageException(CommandSpec.Message("date", "invalidIso", ("value", unit)));
                return false;
            }).ToList();
            var a = Args.Parse(args, CommandSpec.Of("date"));
            var now = ctx.Shell.Now;
            if (a.Value('d') is { } d) now = ParseDate(d, now);
            else if (a.Value('r') is { } r) now = ctx.Fs.Stat(ctx.ResolvePath(r)).Modified.ToUniversalTime();
            var fmt = a.Positional.FirstOrDefault(p => p.StartsWith('+'));
            ctx.Stdout.WriteLine(Strftime(now, iso != null ? IsoFormats[Array.IndexOf(IsoUnits, iso)] : a.Has('R') ? "%a, %d %b %Y %H:%M:%S +0000" : fmt != null ? fmt[1..] : "%a %b %e %H:%M:%S %Z %Y"));
            return 0;
        });

        shell.Register("clear", _ => 0);
        // the agent is a regular user: privilege escalation fails the way it does on a real server
        shell.Register("sudo", ctx =>
        {
            var user = ctx.Shell.Options.User;
            if (ctx.Args is ["-V"] or ["--version"]) { ctx.Stdout.WriteLine("Sudo version 1.9.15p5"); return Task.FromResult(0); }
            if (ctx.Args.Count == 0) { ctx.Stderr.WriteLine(CommandSpec.Message("sudo", "usage")); return Task.FromResult(1); }
            if (ctx.Args[0] is "-l" or "--list") { ctx.Stderr.WriteLine(CommandSpec.Message("sudo", "listDenied", ("user", user), ("host", ctx.Shell.Options.Hostname))); return Task.FromResult(1); }
            // `sudo apt install x`: the package manager's own answer (nothing can be installed) says more than a refusal
            var target = ctx.Args.SkipWhile(a => a.StartsWith('-')).ToList();
            if (target is ["apt" or "apt-get" or "apt-cache" or "dpkg", ..]) return ctx.InvokeAsync(target);
            ctx.Stderr.WriteLine(CommandSpec.Message("sudo", "notSudoer", ("user", user)));
            return Task.FromResult(1);
        });

        // no package sources and no network: package managers say so, with their usual failure status
        foreach (var name in new[] { "apt", "apt-get", "apt-cache" })
            shell.Register(name, ctx => { ctx.Stderr.WriteLine(CommandSpec.Message(name, "unavailable")); return 100; });
        shell.Register("dpkg", ctx => { ctx.Stderr.WriteLine("dpkg: " + CommandSpec.Message("dpkg", "unavailable")); return 2; });
        shell.Register("su", ctx =>
        {
            ctx.Stderr.WriteLine(CommandSpec.Message("su", "authenticationFailure"));
            return 1;
        });

        // ---------- fake system info ----------
        shell.Register("id", ctx =>
        {
            var u = ctx.Shell.Options.User;
            var a = Args.Parse(ctx.Args, CommandSpec.Of("id"));
            ctx.Stdout.WriteLine(a.Has('u') || a.Has('g') ? (a.Has('n') ? u : "1000") : $"uid=1000({u}) gid=1000({u}) groups=1000({u})");
            return 0;
        });
        shell.Register("uptime", ctx =>
        {
            ctx.Stdout.WriteLine(ctx.Args.Contains("-p") ? "up 3 days, 4 hours, 12 minutes" : $" {ctx.Shell.Now:HH:mm:ss} up 3 days,  4:12,  1 user,  load average: 0.08, 0.03, 0.01");
            return 0;
        });
        shell.Register("free", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("free"));
            const string binary = "bkmg\u0001\u0002", decimalUnits = "_\u0003\u0004\u0005\u0006\u0007"; // option keys by power: --tebi is \u0001, --kilo \u0003
            var units = (binary + decimalUnits[1..]).Where(a.Has).ToList();
            // shortcut: a repeated flag (-m -m) is counted once, real free rejects it too
            if (units.Count > 1) throw new ShellException(CommandSpec.Message("free", "multipleUnits"));
            char unit = units.Count > 0 ? units[0] : 'k';
            bool si = a.Has('\u000e') || decimalUnits.Contains(unit), human = a.Has('h'), wide = a.Has('w');
            long b = si ? 1000 : 1024;
            int power = binary.Contains(unit) ? binary.IndexOf(unit) : decimalUnits.IndexOf(unit);
            string Size(long kb)
            {
                long n = kb * 1024;
                if (!human) return (n / (long)Math.Pow(b, power)).ToString(CultureInfo.InvariantCulture);
                if (n < b) return n + "B";
                for (int i = 1; ; i++)
                {
                    double v = n / Math.Pow(b, i);
                    string suffix = "KMGTPE"[i - 1] + (si ? "" : "i");
                    if (v < 10) return v.ToString("0.0", CultureInfo.InvariantCulture) + suffix;
                    if (v < b || i == 6) return (long)v + suffix;
                }
            }
            string Row(string label, IEnumerable<string> cells) => label.PadRight(8) + string.Concat(cells.Select(c => c.PadLeft(12))) + "\n";
            long[] mem = [8014880, 1992344, 3984512, 12288, 180224, 2020800, 5748660], swap = [2097148, 0, 2097148]; // kB: total used free shared buffers cache available
            long[] shown = wide ? mem : [mem[0], mem[1], mem[2], mem[3], mem[4] + mem[5], mem[6]];
            ctx.Stdout.Write(Row("", wide ? ["total", "used", "free", "shared", "buffers", "cache", "available"] : ["total", "used", "free", "shared", "buff/cache", "available"])
                + Row("Mem:", shown.Select(Size)) + Row("Swap:", swap.Select(Size))
                + (a.Has('t') ? Row("Total:", swap.Select((s, i) => Size(mem[i] + s))) : ""));
            return 0;
        });
        shell.Register("nproc", ctx => { ctx.Stdout.WriteLine("4"); return 0; });
    }
}
