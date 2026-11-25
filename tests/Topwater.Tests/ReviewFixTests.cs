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
}
