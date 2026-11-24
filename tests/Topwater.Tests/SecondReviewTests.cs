using Topwater;

namespace Topwater.Tests;

/// <summary>Bugs found by the second code review; each case is what real bash prints.</summary>
public class SecondReviewTests
{
    [Theory]
    [InlineData("source ~/.bashrc && echo ok", "ok\n")]
    [InlineData("echo a; break; echo b", "a\nb\n")]
    [InlineData("x=$(cat <<EOF\nit's fine\nEOF\n); echo \"$x\"", "it's fine\n")]
    [InlineData("x=$(cat <<EOF\n1) item\nEOF\n); echo \"$x\"", "1) item\n")]
    [InlineData("a=5 b=-3; echo $(($a-$b))", "8\n")]
    [InlineData("echo $((-2**2))", "4\n")]
    [InlineData("alias cnt='echo a b | wc -w'; cnt", "2\n")]
    [InlineData("x=outer; f(){ local x; echo \"[$x]\"; }; f", "[]\n")]
    [InlineData("set -- foo bar baz; echo \"${@:2}\"", "bar baz\n")]
    [InlineData("echo x | exit 3; echo after", "after\n")]
    [InlineData("false; $empty; echo $?", "0\n")]
    [InlineData("echo hi 3>/dev/null", "hi\n")]
    [InlineData("touch a; sleep 0.02; touch b; [ b -nt a ] && [ a -ot b ] && echo newer", "newer\n")]
    [InlineData("echo hi > f.txt; grep -r hi f.txt", "hi\n")]
    [InlineData("mkdir d; echo hi > d/a.txt; echo hi > d/a.log; grep -r --include '*.txt' hi d", "d/a.txt:hi\n")]
    [InlineData("echo foobar | grep -oP 'foo\\K\\w+'", "bar\n")]
    [InlineData("awk 'BEGIN{while(i++<25000)printf \"x\";print \"\"}' | cut -c5- | wc -c", "24997\n")]
    public async Task MatchesBash(string cmd, string expected)
    {
        var r = await new Shell().RunAsync(cmd);
        Assert.Equal(expected, r.Stdout);
    }
}

/// <summary>Things that used to hang or could crash the host process must now stop with an error.</summary>
public class RunawayTests
{
    [Theory]
    [InlineData("while :; do while :; do :; done; done; echo survived")]   // inner cap used to be swallowed by the outer loop
    [InlineData("f(){ f; f; }; f; echo survived")]                          // exponential once the depth error was swallowed
    [InlineData("x='eval \"$x\"'; eval \"$x\"; echo survived")]
    [InlineData("printf './a.sh\\n' > a.sh; chmod +x a.sh; ./a.sh; echo survived")]
    [InlineData("echo a | sed ':a;s/x*/x/;ta'")]
    [InlineData("jq -n '[range(1e10)] | length'")]
    [InlineData("jq -n '[1|recurse(.+1)] | length'")]
    public async Task StopsInsteadOfHanging(string cmd)
    {
        var run = new Shell().RunAsync(cmd);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60))));
        var r = await run;
        Assert.NotEqual(0, r.ExitCode);
        Assert.DoesNotContain("survived", r.Stdout);
        Assert.NotEqual("", r.Stderr);
    }

    [Fact]
    public async Task LongOutputKeepsTheErrorAndExitCode()
    {
        var r = await new Shell().RunAsync("seq 1 20000; nosuchcmd");
        var text = r.ToToolOutput();
        Assert.Contains("command not found", text);
        Assert.EndsWith("[exit code 127]", text);
        Assert.True(text.Length < 31_000);
    }
}
