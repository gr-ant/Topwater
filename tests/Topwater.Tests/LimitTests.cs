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

    [Fact]
    public async Task TrailerSurvivesTheOutputCaps()
    {
        var r = await Quick().RunAsync("seq 1 20000; seq 1 3000 >&2; sleep 99");
        var text = r.ToToolOutput();
        Assert.Contains("[output truncated", text);
        Assert.EndsWith("Files and variables changed before this point are kept.]", text);
    }
}
