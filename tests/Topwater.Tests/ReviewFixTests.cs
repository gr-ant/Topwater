namespace Topwater.Tests;

/// <summary>Regression tests for behaviour that differed from real bash / GNU tools. The tabular cases are in tests/corpus/review-fixes.jsonl.</summary>
public class ReviewFixTests
{
    [Fact]
    public async Task LoopLimitCountsBodyRuns()
    {
        var shell = new Shell(new ShellOptions { MaxLoopIterations = 3 });
        Assert.Equal("1\n2\n3\n", (await shell.RunAsync("printf '1\\n2\\n3\\n' | while read -r l; do echo $l; done")).ToToolOutput());
        Assert.Contains("loop aborted after 3 iterations", (await shell.RunAsync("while true; do :; done")).Stderr);
    }

    [Fact]
    public async Task DateBeyondWhatThePortHoldsIsAnInvalidDate()
    {
        // GNU accepts @99999999999999 (year 3170843); here it is refused with touch's own message, never an exception's
        var r = await new Shell().RunAsync("cd /tmp && touch -d @99999999999999 f; echo $?; ls f");
        Assert.Equal("touch: invalid date format ‘@99999999999999’\n", r.Stderr[..r.Stderr.IndexOf('\n')] + "\n");
        Assert.StartsWith("1\n", r.Stdout);
    }

    [Fact]
    public async Task UmaskLastsAcrossCallsAndSparesTheHostsOwnFiles()
    {
        var shell = new Shell();
        await shell.RunAsync("umask 077");
        Assert.Equal("-rw-------\n", (await shell.RunAsync("cd /tmp && touch u && ls -l u | cut -c1-10")).Stdout);
        shell.Fs.WriteAllText("/tmp/host", "x");
        Assert.Equal(0b110_100_100, shell.Fs.Stat("/tmp/host").Mode);
    }

    [Fact]
    public async Task ForkKeepsTheUmask()
    {
        var shell = new Shell();
        await shell.RunAsync("umask 027");
        Assert.Equal("0027\n", (await shell.Fork().RunAsync("umask")).Stdout);
    }
}
