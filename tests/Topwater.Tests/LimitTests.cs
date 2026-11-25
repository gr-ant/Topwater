using System.Diagnostics;

namespace Topwater.Tests;

/// <summary>Resource limits (decision 007): every exhaustion recipe ends in bounded time with its message, and the shell stays usable.</summary>
public class LimitTests
{
    static Shell Quick(double seconds = 1) => new(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(seconds) });

    /// <summary>Runs a call that must hit the time limit; checks exit 124, the trailer, the bound, and that the next call works.</summary>
    static async Task<ShellResult> HitsTimeLimit(Shell sh, string cmd, double seconds = 1)
    {
        var sw = Stopwatch.StartNew();
        var r = await sh.RunAsync(cmd);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(seconds + 8), $"took {sw.Elapsed}");
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
        await HitsTimeLimit(Quick(), "x=$(printf 'a%.0s' {1..28})b; for i in {1..200}; do echo $x; done > s.txt; grep -cE '(a|aa)+$' s.txt 2>/dev/null");

    [Theory]
    [InlineData("awk 'BEGIN { while (1) x++ }'")]
    [InlineData("sed ':a; s/^/x/; ba' <<< x")] // the pattern space grows, so each step is slower than the last
    [InlineData("jq -n 'last(range(1e12))'")]
    public async Task TimeoutInterruptsABuiltInLoop(string loop)
    {
        var sh = new Shell();
        var sw = Stopwatch.StartNew();
        var r = await sh.RunAsync($"timeout 1 {loop}; echo $?");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"took {sw.Elapsed}");
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
        Assert.Contains("bash: /home/agent/f.txt: No space left on device", r.Stderr);
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

    [Fact]
    public async Task TrailerSurvivesTheOutputCaps()
    {
        var r = await Quick().RunAsync("seq 1 20000; seq 1 3000 >&2; sleep 99");
        var text = r.ToToolOutput();
        Assert.Contains("[output truncated", text);
        Assert.EndsWith("Files and variables changed before this point are kept.]", text);
    }
}
