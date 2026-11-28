using System.Diagnostics;

namespace Topwater.Tests;

/// <summary>Resource limits (decision 007): every exhaustion recipe ends in bounded time with its message, and the shell stays usable.</summary>
public class LimitTests
{
    static Shell Quick(double seconds = 1) => new(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(seconds) });

    /// <summary>Runs a call that must hit the time limit; checks exit 124, the trailer, the bound, and that the next call works.</summary>
    static async Task<ShellResult> HitsTimeLimit(Shell sh, string cmd, double seconds = 1)
    {
        var (r, took) = await TestHost.Timed(() => sh.RunAsync(cmd));
        Assert.True(took < TimeSpan.FromSeconds(seconds + 8), $"took {took}");
        Assert.Equal(124, r.ExitCode);
        Assert.EndsWith($"[stopped: this call exceeded the {seconds} s time limit; split the work into shorter calls. Running it again will hit the same limit. Files and variables changed before this point are kept.]", r.ToToolOutput());
        Assert.Equal("ok\n", (await sh.RunAsync("echo ok")).Stdout);
        return r;
    }

    [Fact]
    public async Task SleepForeverStopsAtTheCallLimitAndKeepsEarlierOutput()
    {
        var sh = Quick();
        var r = await HitsTimeLimit(sh, "x=kept; echo before; sleep 99999 2>/dev/null");
        Assert.StartsWith("before\n", r.ToToolOutput());
        Assert.Equal("kept\n", (await sh.RunAsync("echo $x")).Stdout);
    }

    [Fact]
    public async Task ConfiguredDurationIsPrinted() => await HitsTimeLimit(Quick(1.5), "sleep 60", 1.5);

    [Fact]
    public async Task ShellLoopOfAwkRunsStopsAtTheCallLimit() =>
        await HitsTimeLimit(Quick(), "while :; do awk 'BEGIN { while (1) x++ }' 2>/dev/null; done");

    [Fact]
    public async Task SlowRegexOverManyLinesStopsAtTheCallLimit() =>
        await HitsTimeLimit(Quick(), "x=$(printf 'a%.0s' {1..22})b; for i in {1..5000}; do echo $x; done > s.txt; grep -cE '(a|aa)+$' s.txt 2>/dev/null");

    [Theory]
    [InlineData("awk 'BEGIN { while (1) x++ }'")]
    [InlineData("sed ':a; s/^/x/; ba' <<< x")] // the pattern space grows, so each step is slower than the last
    [InlineData("jq -n 'last(range(1e12))'")]
    public async Task TimeoutInterruptsABuiltInLoop(string loop)
    {
        var sh = new Shell();
        var (r, took) = await TestHost.Timed(() => sh.RunAsync($"timeout 0.1 {loop}; echo $?"));
        Assert.True(took < TimeSpan.FromSeconds(6), $"took {took}");
        Assert.Equal("124\n", r.Stdout);
    }

    [Fact]
    public async Task LoopLimitNamesWhichLimitTripped()
    {
        var sh = new Shell(new ShellOptions { MaxLoopIterations = 10 });
        var one = await sh.RunAsync("while :; do :; done 2>/dev/null");
        Assert.Contains("loop aborted after 10 iterations (the limit for one loop)", one.Stderr);
        Assert.StartsWith("[stopped: loop aborted after 10 iterations", one.ToToolOutput().Split('\n')[^1]);
        var all = await sh.RunAsync("for i in {1..10}; do for j in {1..10}; do :; done; done");
        Assert.Contains("loop aborted after 100 iterations in this call (the limit for all loops in one call)", all.Stderr);
    }

    // ---------- filesystem ----------

    static Shell SmallDisk() => new(new ShellOptions { MaxFileSystemBytes = 1L << 20, MaxLoopIterations = 5_000 });
    const string Kilobyte = "x=$(printf '%01023d' 0); ";

    [Fact]
    public async Task AppendLoopStopsAtTheCapAndRmFreesSpace()
    {
        var sh = SmallDisk();
        var r = await sh.RunAsync(Kilobyte + "for i in {1..2000}; do echo $x >> f.txt; done; echo done");
        Assert.Contains("bash: line 1: /home/agent/f.txt: No space left on device", r.Stderr);
        Assert.EndsWith("done\n", r.Stdout); // the call goes on
        Assert.True(sh.Fs.UsedBytes <= 1L << 20);
        Assert.Equal("1\n", (await sh.RunAsync(Kilobyte + "echo $x >> f.txt; echo $?")).Stdout);
        var df = (await sh.RunAsync("df -h .")).Stdout;
        Assert.Matches(@"/dev/sda1\s+1\.0M\s+1\.0M\s+\d+\s+100% /", df);
        Assert.Equal("0\n", (await sh.RunAsync("rm f.txt; echo hi > g.txt; echo $?")).Stdout);
        Assert.Matches(@"/dev/sda1\s+1\.0M\s+\S+K\s+", (await sh.RunAsync("df -h .")).Stdout);
    }

    [Fact]
    public async Task UnboundedAppendLoopEndsAtTheLoopLimit()
    {
        var r = await SmallDisk().RunAsync(Kilobyte + "while :; do echo $x >> f.txt; done");
        Assert.Contains("No space left on device", r.Stderr);
        Assert.Contains("loop aborted after 5000 iterations", r.ToToolOutput());
    }

    [Fact]
    public async Task CpRecursiveStopsAtTheCap()
    {
        var sh = SmallDisk();
        var r = await sh.RunAsync(Kilobyte + "mkdir d; for i in {1..400}; do echo $x > d/$i; done; cp -r d d2; echo $?; cp -r d d3");
        Assert.Equal("0\n", r.Stdout);
        Assert.Contains("cp: error writing 'd3': No space left on device", r.Stderr);
        Assert.Equal(1, r.ExitCode);
        Assert.False(sh.Fs.Exists("/home/agent/d3"));
    }

    [Fact]
    public async Task TarExtractStopsAtTheCap()
    {
        var sh = SmallDisk();
        var r = await sh.RunAsync(Kilobyte + "mkdir d; for i in {1..600}; do echo $x > d/$i; done; tar czf d.tgz d; rm -r d; mkdir a b; tar xzf d.tgz -C a; echo $?; tar xzf d.tgz -C b");
        Assert.StartsWith("0\n", r.Stdout);
        Assert.Contains("Cannot open: No space left on device", r.Stderr);
        Assert.NotEqual(0, r.ExitCode);
        Assert.Equal("ok\n", (await sh.RunAsync("rm -r a b; tar xzf d.tgz -C . && echo ok")).Stdout);
    }

    [Fact]
    public async Task HostWritesAreCountedButNeverRefused()
    {
        var sh = SmallDisk();
        sh.Fs.WriteAllText("/data.txt", new string('x', 2 << 20));
        sh.Fs.AddDocument("/docs/a.bin", new byte[100]);
        Assert.True(sh.Fs.UsedBytes > 2 << 20);
        var r = await sh.RunAsync("df .; echo y > y.txt");
        Assert.Matches(@"/dev/sda1\s+1024\s+20\d\d\s+0\s+", r.Stdout);
        Assert.Contains("No space left on device", r.Stderr);
    }

    // ---------- strings ----------

    static Shell SmallStrings() => new(new ShellOptions { MaxStringBytes = 1L << 20 });

    /// <summary>Runs a call that must hit the string limit: exit 1, the message on stderr and in the trailer, then a working shell.</summary>
    static async Task<ShellResult> HitsStringLimit(Shell sh, string cmd, string message)
    {
        var (r, took) = await TestHost.Timed(() => sh.RunAsync(cmd));
        Assert.True(took < TimeSpan.FromSeconds(20), $"took {took}");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("bash: " + message, r.Stderr);
        var last = r.ToToolOutput().Split('\n')[^1];
        Assert.StartsWith("[stopped: " + message, last);
        Assert.EndsWith("Running it again will hit the same limit. Files and variables changed before this point are kept.]", last);
        Assert.Equal("ok\n", (await sh.RunAsync("echo ok")).Stdout);
        return r;
    }

    [Fact]
    public async Task BashStringDoublingStopsAtTheDefaultLimit() =>
        await HitsStringLimit(new Shell(), "x=a; while :; do x=$x$x; done 2>/dev/null", "variables in total too large (limit 32 MB)");

    [Theory]
    [InlineData("awk 'BEGIN { x = \"a\"; while (1) x = x x }' 2>/dev/null", "awk: string too large (limit 1 MB)")]
    [InlineData("awk 'BEGIN { x = \"ab\"; while (1) gsub(/./, \"&&\", x) }'", "awk: string too large (limit 1 MB)")]
    [InlineData("echo a | sed ':a; s/.*/&&/; ba' 2>/dev/null", "sed: pattern space too large (limit 1 MB)")]
    [InlineData("jq -n 'reduce range(100) as $i (\"a\"; . + .)' 2>/dev/null", "jq: value too large (limit 1 MB)")]
    [InlineData("jq -n 'reduce range(100) as $i ([1]; . + .)'", "jq: value too large (limit 1 MB)")]
    [InlineData("jq -n '\"ab\" * 1e12'", "jq: value too large (limit 1 MB)")]
    [InlineData("x=$(printf '%02000d' 0); y=${x//?/$x}", "expansion too large (limit 1 MB)")]
    [InlineData("m=$(printf '%01000d' 0); echo {1..10000}$m >/dev/null", "expansion too large (limit 1 MB)")]
    [InlineData("big=$(printf '%0100000d' 0); for ((i = 0; i < 1000000; i++)); do a[i]=$big; done", "variables in total too large (limit 1 MB)")]
    [InlineData("awk 'BEGIN { for (i = 0; i < 2100; i++) printf \"%01000d\", 0 }' > f; read -r x < f", "x: value too large (limit 1 MB)")]
    [InlineData("awk 'BEGIN { for (i = 0; i < 2100; i++) printf \"%01000d\", 0 }' > f; cat f 2>/dev/null", "output too large (limit 1 MB)")]
    [InlineData("awk 'BEGIN { for (i = 0; i < 2100; i++) printf \"%01000d\", 0 }' > f; cat f | wc -c", "output too large (limit 1 MB)")]
    [InlineData("awk 'BEGIN { for (i = 0; i < 2100; i++) printf \"%01000d\", 0 }' > f; x=$(cat f)", "output too large (limit 1 MB)")]
    // aliases and traps live as long as the session, as variables do, and were not counted at all
    [InlineData("v=$(printf '%010000d' 0); v=$v$v$v$v$v$v$v$v$v$v; v=$v$v$v; for i in 1 2 3 4; do alias a$i=$v; done", "variables in total too large (limit 1 MB)")]
    [InlineData("v=$(printf '%010000d' 0); v=$v$v$v$v$v$v$v$v$v$v; v=$v$v$v; trap \"$v\" USR1 USR2 HUP INT", "variables in total too large (limit 1 MB)")]
    public async Task StringRecipesStopAtTheConfiguredLimit(string cmd, string message) => await HitsStringLimit(SmallStrings(), cmd, message);

    [Fact]
    public async Task HistoryKeepsTheNewestLinesWithinTheStringLimit()
    {
        // every call's line was kept for good: calls of 300 KB each grew it without bound
        var sh = SmallStrings();
        var line = "#" + new string('x', 300_000);
        for (int i = 0; i < 10; i++) await sh.RunAsync(line + i);
        Assert.InRange(sh.History.Sum(h => (long)h.Length), 1, 1L << 20);
        Assert.Equal(line + 9, sh.History[^1]);
        Assert.Equal("ok\n", (await sh.RunAsync("echo ok")).Stdout);
    }

    [Fact]
    public async Task HugePrintfWidthIsBounded() =>
        Assert.Equal("10000\n", (await SmallStrings().RunAsync("printf '%0999999999d' 1 | wc -c")).Stdout);

    [Fact]
    public async Task RedirectsAreBoundedByTheDiskNotTheStringLimit()
    {
        var sh = SmallStrings();
        var r = await sh.RunAsync("awk 'BEGIN { for (i = 0; i < 3000; i++) printf \"%01000d\\n\", 0 }' > f; cat f f > g; wc -c < g");
        Assert.Equal("6006000\n", r.Stdout);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public async Task VariablesCanBeRewrittenNearTheLimit()
    {
        // x is 600 KB: x=${x}y holds 1.2 MB for a moment but only 600 KB after, so it must not count both
        var sh = SmallStrings();
        await sh.RunAsync("kb() { awk -v n=$1 'BEGIN { for (i = 0; i < n; i++) printf \"%01000d\", 0 }'; }");
        var r = await sh.RunAsync("x=$(kb 600); for i in 1 2 3; do x=${x}y; done; echo ${#x}; unset x; y=$(kb 900); echo ${#y}");
        Assert.Equal("600003\n900000\n", r.Stdout);
    }

    [Fact]
    public async Task TrailerSurvivesTheOutputCaps()
    {
        var r = await Quick().RunAsync("seq 1 20000; seq 1 3000 >&2; sleep 99");
        var text = r.ToToolOutput();
        Assert.Contains("[output truncated", text);
        Assert.EndsWith("Files and variables changed before this point are kept.]", text);
    }

    [Fact]
    public async Task JqDefRecursionIsBounded()
    {
        // a def that never stops recursing ends in an error, not a hang or a crash; 100 levels deep still works
        var r = await new Shell().RunAsync("jq -n 'def f: f; f'; echo $?; jq -n 'def r: if . < 99 then .+1 | r else . end; 0 | r'");
        Assert.Equal("5\n99\n", r.Stdout);
        Assert.Contains("recursion too deep", r.Stderr);
    }

    // Board 041: inputs that used to run for minutes or grow without bound now stop inside a short call limit.

    [Fact]
    public async Task GitApplyHunkSearchIsLinear()
    {
        var sh = Quick(2);
        sh.Fs.CreateDirectory("/tmp/w", true);
        sh.Fs.WriteAllBytes("/tmp/w/big", System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("x\n", 30000))));
        const int n = 30000;
        var patch = $"--- a/big\n+++ b/big\n@@ -1,{n + 1} +1,{n + 1} @@\n" + string.Concat(Enumerable.Repeat(" x\n", n)) + "-absent\n+y\n";
        sh.Fs.WriteAllBytes("/tmp/w/p", System.Text.Encoding.ASCII.GetBytes(patch));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = await sh.RunAsync("cd /tmp/w && git apply p; echo $?");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
        Assert.Equal("1\n", r.Stdout);
        Assert.Contains("patch does not apply", r.Stderr);
    }

    [Fact]
    public async Task JoinOnOneRepeatedKeyStreamsItsOutput()
    {
        var sh = Quick(1);
        sh.Fs.CreateDirectory("/tmp/w", true);
        sh.Fs.WriteAllBytes("/tmp/w/a", System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("k v\n", 10000))));
        // the managed heap is sampled while join runs (other tests share the process, so the bound is loose): holding
        // the product grew it by gigabytes
        long peak = 0;
        using var done = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                await Task.Delay(10);
            }
        });
        var r = await sh.RunAsync("cd /tmp/w && join a a > /dev/null");
        done.Cancel();
        await sampler;
        Assert.NotEqual(0, r.ExitCode);  // the time limit ends 100 million rows
        Assert.True(peak < 1L << 30, $"peak heap {peak >> 20} MB");
    }

    // name lengths picked so the unbounded match takes about 10 s in Python
    [Theory]
    [InlineData("tar -cf t.tar --transform='s/\\(a*\\)*b/x/' $n", 26)]
    [InlineData("tar -cf t.tar --transform='s/(a|aa)+b/x/x' $n", 36)]
    [InlineData("tar -cf t.tar --exclude='*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*b' $n", 32)]
    [InlineData("find . -name '*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*b'", 32)]
    public async Task TarAndFindPatternsHonourTheTimeLimit(string cmd, int n)
    {
        var sh = Quick(1);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await sh.RunAsync($"mkdir -p /tmp/w && cd /tmp/w && n={new string('a', n)}c && echo x > $n && {cmd}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task JqDepthLimitIsTheSameForEveryKindOfFunction()
    {
        var sh = new Shell();
        var kinds = new Dictionary<string, string>
        {
            ["filter"] = "def f(n): if n == 0 then 0 else 1 + f(n-1) end; f(N)",
            ["value"] = "def f($n): if $n == 0 then 0 else 1 + f($n-1) end; f(N)",
            ["none"] = "def r: if . < N then .+1 | r else . end; 0 | r",
        };
        foreach (var (name, src) in kinds)
        {
            Assert.True((await sh.RunAsync($"jq -n '{src.Replace("N", "99")}'")).Stdout == "99\n", name);
            var r = await sh.RunAsync($"jq -n '{src.Replace("N", "100")}'");
            Assert.True(r.ExitCode == 5 && r.Stderr.Contains("recursion too deep"), name);
        }
    }

    [Fact]
    public async Task DirectoriesStopAtTheLongestPathLinuxAllows()
    {
        // rm -r, cp -r, Clone and the change tracker walk a tree by recursion: a tree nested by mv beyond any path the agent
        // could type crashed the host with a stack overflow
        var sh = new Shell();
        var r = await sh.RunAsync("""
            n=$(printf 'x%.0s' {1..250}); d=a; for i in {1..16}; do d=$d/$n; done
            mkdir -p $d && echo deep
            mkdir $d/$n; echo "mkdir $?"
            mkdir -p b/$n/$n; mv b $d; echo "mv $?"
            mkdir -p c/$n/$n; cp -r c $d; echo "cp $?"
            ln -s $PWD/$d l; mkdir -p l/$n/$n; echo "link $?"
            mkdir -p $d/$n/$n/$n; echo "mkdir -p $?"
            ls b c >/dev/null && echo kept
            """);
        Assert.Equal("deep\nmkdir 1\nmv 1\ncp 1\nlink 1\nmkdir -p 1\nkept\n", r.Stdout);
        Assert.Equal(5, r.Stderr.Split('\n').Count(l => l.EndsWith("File name too long")));
    }

    [Theory]
    // what the agent writes, nested deeply: each recursed once per level and crashed the host with a stack overflow
    [InlineData("( ", 50_000, "", "eval \"[[ $(cat p) a ]]\"")]
    [InlineData("( ", 50_000, "", "test $(cat p) a")]
    [InlineData("\\( ", 50_000, "", "eval find /tmp $(cat p) -true")]
    [InlineData("(", 50_000, "1", "awk \"BEGIN { print $(cat p) }\"")]
    [InlineData("1+", 50_000, "1", "awk \"BEGIN { print $(cat p) }\"")]
    [InlineData("if(1)", 50_000, "print 1", "awk \"BEGIN { $(cat p) }\"")]
    [InlineData("(", 50_000, "1", "bc < p")]
    [InlineData("1+", 50_000, "1", "bc < p")]
    [InlineData("[", 50_000, "", "jq . p")]
    [InlineData("try ", 50_000, "1", "jq -n \"$(cat p)\"")]
    [InlineData("{a:", 50_000, "1", "jq -n \"$(cat p)\"")]
    [InlineData("", 0, "", "jq -n 'reduce range(50000) as $i (null; [.]) | tojson'")]
    [InlineData("", 0, "", "jq -n 'reduce range(50000) as $i (null; [.]) | . == .'")]
    [InlineData("", 0, "", "jq -n -c 'reduce range(50000) as $i (null; [.])'")]
    public async Task DeepNestingEndsTheCallInsteadOfTheHost(string token, int n, string end, string cmd)
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/home/agent/p", string.Concat(Enumerable.Repeat(token, n)) + end);
        var r = await sh.RunAsync(cmd);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("maximum nesting level exceeded", r.Stderr);
        Assert.Equal("ok\n", (await sh.RunAsync("echo ok")).Stdout);
    }

    [Theory]
    // a chain, or a run of !, is not nesting: it runs, and quickly
    [InlineData("eval \"[[ $(printf '! %.0s' $(seq 50000)) a ]]\"; echo $?", "0\n")]
    [InlineData("find /tmp $(printf -- '-false -o %.0s' $(seq 50000)) -true -prune", "/tmp\n")]
    [InlineData("find /tmp -maxdepth 0 $(printf -- '! %.0s' $(seq 50001)) -false", "/tmp\n")]
    public async Task LongChainsAreNotNesting(string cmd, string expected)
    {
        var (r, took) = await TestHost.Timed(() => new Shell().RunAsync(cmd));
        Assert.Equal(expected, r.Stdout);
        Assert.True(took < TimeSpan.FromSeconds(10), $"took {took}");
    }

    [Theory]
    // jq helpers on a deep value, run from a host thread with a small stack
    [InlineData("[$v, $v] | unique | length")]
    [InlineData("[$v, $v] | sort | length")]
    [InlineData("[$v, $v] | group_by(.) | length")]
    [InlineData("[$v, $v] | unique_by(.) | length")]
    [InlineData("[$v, [$v]] | min | length")]
    [InlineData("{a: $v, b: $v} | to_entries | sort_by(.value) | length")]
    [InlineData("$v | getpath([range(30000) | 0]) | length")]
    [InlineData("[$v | tostream] | length")]
    [InlineData("$v | [paths] | length")]
    [InlineData("$v | [leaf_paths] | length")]
    [InlineData("$v | [..] | length")]
    [InlineData("$v | flatten | length")]
    [InlineData("$v | walk(.) | length")]
    [InlineData("$v | tojson | length")]
    [InlineData("$v | tostring | length")]
    [InlineData("$v | @json | length")]
    [InlineData("$v | @text | length")]
    [InlineData("$v | @base64 | length")]
    [InlineData("$v | contains($v)")]
    [InlineData("$v | inside($v)")]
    [InlineData("$v | index([$v])")]
    [InlineData("[$v] - [$v] | length")]
    [InlineData("[$v] | indices([$v])")]
    [InlineData("$v | . < $v")]
    [InlineData("$v | . == $v")]
    [InlineData("$v | del(.[0][0]) | length")]
    [InlineData("$v | delpaths([[range(30000) | 0]]) | length")]
    [InlineData("$v | to_entries | length")]
    [InlineData("[$v] | add | length")]
    [InlineData("$v + $v | length")]
    [InlineData("{a: $v} * {a: $v} | length")]
    [InlineData("{a: (reduce range(30000) as $i (null; {a: .}))} * {a: (reduce range(30000) as $i (null; {a: .}))} | length")]
    [InlineData("$v | any")]
    [InlineData("[$v] | in([$v])? // 1")]
    [InlineData("$v | [path(..)] | length")]
    [InlineData("$v | [recurse(.[]?)] | length")]
    [InlineData("$v | tojson | fromjson | length")]
    [InlineData("$v | ltrimstr($v)? | length")]
    public void JqHelpersOnDeepValuesEndTheCallOnASmallStack(string filter)
    {
        var sh = new Shell();
        var cmd = $"jq -n 'reduce range(30000) as $i (null; [.]) as $v | {filter}'";
        ShellResult? r = null, after = null;
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            try { r = sh.RunAsync(cmd).GetAwaiter().GetResult(); after = sh.RunAsync("echo ok").GetAwaiter().GetResult(); }
            catch (Exception e) { failed = e; }
        }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(failed);
        if (r!.ExitCode != 0) Assert.Matches("maximum nesting level exceeded|recursion too deep|not supported|too many steps", r.Stderr);
        Assert.Equal("ok\n", after!.Stdout);
    }

    [Theory]
    // each copied the rest of the path, or every path's prefix, per level: 26 GB for setpath with a 100,000-long path
    [InlineData("jq -n 'null | setpath([range(30000) | 0]; 1) | length'")]
    [InlineData("jq -n 'reduce range(10000) as $i (null; [.]) | [paths] | length'")]
    [InlineData("jq -n 'reduce range(10000) as $i (null; [.]) | [..] | length'")]
    public async Task JqHelpersOnLongPathsStayBounded(string cmd)
    {
        var (r, took) = await TestHost.Timed(() => new Shell().RunAsync(cmd));
        Assert.True(took < TimeSpan.FromSeconds(60), $"took {took}");
        Assert.NotEqual(0, r.ExitCode);
        Assert.Matches("too many steps|maximum nesting level exceeded", r.Stderr);
    }

    [Fact]
    public async Task EmptyFilesAndDirectoriesCountAgainstTheDisk()
    {
        // they hold no bytes, but each costs the host memory: 600,000 took 350 MB in one call, and calls add up
        var sh = new Shell(new ShellOptions { MaxFileSystemBytes = 16L << 20 }); // 16,384 names
        var r = await sh.RunAsync("mkdir d && cd d && seq 20000 | xargs touch; echo $?; ls | wc -l; df / | tail -1 | awk '{print $3}'");
        var lines = r.Stdout.Split('\n');
        Assert.Equal("123", lines[0]); // xargs: a run of touch failed
        Assert.InRange(int.Parse(lines[1]), 15_000, 16_384);
        Assert.Contains("No space left on device", r.Stderr);
        r = await sh.RunAsync("mkdir x; ln -s a y; cp -r /home/agent/d /tmp/e; echo $?; cd; rm -rf d; mkdir z && echo made");
        Assert.Contains("1\nmade\n", r.Stdout);
    }
}
