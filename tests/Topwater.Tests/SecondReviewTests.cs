using Topwater;

namespace Topwater.Tests;

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

/// <summary>Schema shapes that Pydantic/FastMCP servers produce, and option edge cases.</summary>
public class SchemaMapperReviewTests
{
    const string Schema = """
        {"type":"object","$defs":{"Level":{"type":"string","enum":["1","2"]}},"properties":{
          "tags":{"anyOf":[{"type":"array","items":{"type":"string"}},{"type":"null"}],"description":"Tags"},
          "id":{"anyOf":[{"type":"integer"},{"type":"string"}]},
          "level":{"$ref":"#/$defs/Level"},
          "ratio":{"type":"number"},
          "limit":{"type":"integer"},
          "cache":{"type":"boolean"},
          "title":{"type":"string"},
          "odd":true}}
        """;

    static async Task<(ShellResult result, string? sent)> Call(string command)
    {
        string? sent = null;
        var tools = new[] { new System.Collections.Generic.List<ToolSpec> { new("run", "Run it", System.Text.Json.JsonDocument.Parse(Schema).RootElement) } }[0];
        var shell = new Shell().RegisterTools("t", tools, (_, args, _) => { sent = args.ToJsonString(); return Task.FromResult(new ToolResult("ok")); });
        return (await shell.RunAsync(command), sent);
    }

    [Theory]
    [InlineData("t run --tags 123", """{"tags":["123"]}""")]                          // Optional[list[str]] keeps its item type
    [InlineData("t run --id abc", """{"id":"abc"}""")]                                 // Union[int, str] accepts text
    [InlineData("t run --id 7", """{"id":7}""")]
    [InlineData("t run --level 1", """{"level":"1"}""")]                               // $ref to a string enum stays a string
    [InlineData("t run --no-cache=true", """{"cache":false}""")]
    [InlineData("t run --title -h", """{"title":"-h"}""")]                             // -h as a value is not a help request
    [InlineData("t run --limit 9 --json '{\"limit\":2,\"title\":\"x\"}'", """{"limit":9,"title":"x"}""")]   // options win whatever the order
    [InlineData("t run --json '{\"tags\":[\"a\"]}' --tags b", """{"tags":["b"]}""")]
    public async Task SendsWhatWasMeant(string command, string expected)
    {
        var (result, sent) = await Call(command);
        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(expected, sent);
    }

    [Fact]
    public async Task RejectsNonFiniteNumbersAndStillPrintsHelp()
    {
        var (bad, sent) = await Call("t run --ratio NaN");
        Assert.Equal(2, bad.ExitCode);
        Assert.Null(sent);

        var (help, _) = await Call("t run --help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("--tags TEXT", help.Stdout);
        Assert.Contains("Tags (repeatable)", help.Stdout);
        Assert.Contains("(one of: 1, 2)", help.Stdout);
        Assert.Contains("--odd", help.Stdout);   // a boolean schema used to crash help
    }
}

/// <summary>Host bridge findings, exercised with the real /bin/sh (skipped where there isn't one).</summary>
public class HostBridgeReviewTests
{
    static bool Unix => File.Exists("/bin/sh") && File.Exists("/bin/ln");

    static async Task<string> Out(Shell shell, string cmd)
    {
        var r = await shell.RunAsync(cmd);
        Assert.True(r.ExitCode == 0, $"`{cmd}` exited {r.ExitCode}: {r.Stderr}");
        return r.Stdout;
    }

    [Fact]
    public async Task ALinkLeftInTheMirrorIsNeverWrittenThrough()
    {
        if (!Unix) return;
        var sh = new Shell();
        using var ln = sh.RegisterHostCommand("realln", "/bin/ln");
        await Out(sh, "echo original > target.txt && realln -s target.txt link.txt");
        await Out(sh, "echo replaced > link.txt && realln --version");   // any call exports the workspace again
        var mirror = Path.Combine(ln.Root, "home", "agent");
        Assert.Null(new FileInfo(Path.Combine(mirror, "link.txt")).LinkTarget);
        Assert.Equal("replaced\n", File.ReadAllText(Path.Combine(mirror, "link.txt")));
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(mirror, "target.txt")));
    }

    [Fact]
    public async Task ABackgroundChildDoesNotHangTheCommand()
    {
        if (!Unix) return;
        var sh = new Shell();
        using var real = sh.RegisterHostCommand("realsh", "/bin/sh");
        var run = sh.RunAsync("realsh -c 'sleep 15 & echo started'");
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal("started\n", (await run).Stdout);
    }

    [Fact]
    public async Task ModesVariablesAndRootReachTheProgram()
    {
        if (!Unix) return;
        var sh = new Shell();
        using var real = sh.RegisterHostCommand("realsh", "/bin/sh");
        Assert.Equal("ran\n", await Out(sh, "printf '#!/bin/sh\\necho ran\\n' > run.sh && chmod +x run.sh && realsh -c ./run.sh"));
        Assert.Equal("bar\n", await Out(sh, "export FOO=bar; realsh -c 'echo $FOO'"));
        Assert.Equal("home\ntmp\n", await Out(sh, "realsh -c 'ls \"$1\"' x /"));          // "/" is the mirror, not the host's root
        Assert.Equal("made\n", await Out(sh, "realsh -c 'echo made > new.sh; chmod +x new.sh' && test -x new.sh && cat new.sh"));

        var r = await sh.RunAsync("realsh /etc/hostname");
        Assert.NotEqual(0, r.ExitCode);
        Assert.Contains("can only use files under /home/agent and /tmp", r.Stderr);
    }
}

/// <summary>Input that used to overflow the stack, which .NET cannot catch: it killed the host process.</summary>
public class DeepNestingTests
{
    [Theory]
    [InlineData("((", "))", 200_000)]          // arithmetic
    [InlineData("( ", " )", 50_000)]           // subshells
    [InlineData("{ ", "; }", 50_000)]          // groups
    [InlineData("if ", "; then :; fi", 20_000)]
    [InlineData("echo $((", "))", 100_000)]
    [InlineData("echo $((-", "))", 1)]         // control: shallow input still parses
    public async Task DeepNestingIsAnErrorNotACrash(string open, string close, int depth)
    {
        var body = depth == 1 ? "1" : "true";
        var cmd = string.Concat(Enumerable.Repeat(open, depth)) + body + string.Concat(Enumerable.Repeat(close, depth));
        var r = await new Shell().RunAsync(cmd);
        if (depth == 1) Assert.Equal("-1\n", r.Stdout);
        else Assert.NotEqual("", r.Stderr);
    }

    [Fact]
    public async Task LoaderVariablesDoNotReachHostPrograms()
    {
        if (!File.Exists("/bin/sh")) return;
        var sh = new Shell();
        using var real = sh.RegisterHostCommand("realsh", "/bin/sh");
        var r = await sh.RunAsync("export LD_PRELOAD=/home/agent/evil.so LD_LIBRARY_PATH=/home/agent KEEP=yes; realsh -c 'echo \"[$LD_PRELOAD][$LD_LIBRARY_PATH][$KEEP]\"'");
        Assert.Equal("[][][yes]\n", r.Stdout);
    }
}
