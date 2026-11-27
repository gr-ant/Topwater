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
    public async Task StringRecipesStopAtTheConfiguredLimit(string cmd, string message) => await HitsStringLimit(SmallStrings(), cmd, message);

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
}
