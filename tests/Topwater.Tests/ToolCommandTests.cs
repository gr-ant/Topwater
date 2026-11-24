using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Topwater;
using Topwater.Mcp;

namespace Topwater.Tests;

/// <summary>Schema-described tools (and MCP servers) exposed as a program with subcommands.</summary>
public class ToolCommandTests
{
    const string Schema = """
        {"type":"object","required":["repo"],"properties":{
          "repo":{"type":"string","description":"owner/name"},
          "state":{"type":"string","enum":["open","closed"],"default":"open"},
          "limit":{"type":"integer"},
          "labels":{"type":"array","items":{"type":"string"}},
          "include_drafts":{"type":"boolean"},
          "filter":{"type":"object"},
          "ratio":{"type":["number","null"]}}}
        """;

    static (Shell shell, List<(string tool, string args)> calls) Make()
    {
        var calls = new List<(string, string)>();
        var tools = new[]
        {
            new ToolSpec("list_issues", "List issues in a repository.\nSecond line.", JsonDocument.Parse(Schema).RootElement),
            new ToolSpec("whoami", "Show the current user", JsonDocument.Parse("""{"type":"object"}""").RootElement),
            new ToolSpec("fail", null, JsonDocument.Parse("{}").RootElement),
        };
        var shell = new Shell().RegisterTools("gh", tools, (tool, args, _) =>
        {
            calls.Add((tool, args.ToJsonString()));
            return Task.FromResult(tool == "fail" ? new ToolResult("boom", IsError: true) : new ToolResult("""[{"n":1},{"n":2}]"""));
        }, "GitHub tools");
        return (shell, calls);
    }

    [Fact]
    public async Task ListsToolsAndDocumentsEach()
    {
        var (sh, _) = Make();
        var help = await sh.RunAsync("gh --help");
        Assert.Contains("GitHub tools", help.Stdout);
        Assert.Contains("list-issues  List issues in a repository.", help.Stdout);
        Assert.DoesNotContain("Second line", help.Stdout);
        Assert.Equal(help.Stdout, (await sh.RunAsync("gh")).Stdout);
        Assert.Contains("gh", (await sh.RunAsync("help")).Stdout);

        var one = (await sh.RunAsync("gh list-issues --help")).Stdout;
        Assert.Contains("Usage: gh list-issues --repo TEXT [options]", one);
        Assert.Contains("--repo TEXT", one);
        Assert.Contains("owner/name (required)", one);
        Assert.Contains("(one of: open, closed) (default: open)", one);
        Assert.Contains("--labels TEXT", one);
        Assert.Contains("(repeatable)", one);
        Assert.Contains("--limit INT", one);
        Assert.Contains("--include-drafts ", one);
        Assert.Contains("--json JSON", one);
    }

    [Fact]
    public async Task ParsesOptionsByType()
    {
        var (sh, calls) = Make();
        var r = await sh.RunAsync("""gh list-issues --repo a/b --limit 5 --labels bug --labels "help wanted" --include-drafts --filter '{"x":1}' --ratio=0.5 | jq length""");
        Assert.Equal("2\n", r.Stdout);
        Assert.Equal("list_issues", calls[0].tool);
        Assert.Equal("""{"repo":"a/b","limit":5,"labels":["bug","help wanted"],"include_drafts":true,"filter":{"x":1},"ratio":0.5}""", calls[0].args);

        await sh.RunAsync("""gh list_issues --repo a/b --no-include-drafts --labels '["x","y"]' --include_drafts false""");
        Assert.Equal("""{"repo":"a/b","include_drafts":false,"labels":["x","y"]}""", calls[1].args);

        await sh.RunAsync("""echo '{"repo":"from/stdin","limit":2}' | gh list-issues --json - --limit 9""");
        Assert.Equal("""{"repo":"from/stdin","limit":9}""", calls[2].args);

        Assert.Equal(0, (await sh.RunAsync("gh whoami")).ExitCode);
        Assert.Equal("{}", calls[3].args);
    }

    [Theory]
    [InlineData("gh nope", "unknown tool 'nope'")]
    [InlineData("gh list-issues", "missing required option --repo")]
    [InlineData("gh list-issues --repo a --limit many", "option --limit expects a whole number, got 'many'")]
    [InlineData("gh list-issues --repo a --colour red", "unknown option --colour; options are --repo")]
    [InlineData("gh list-issues --repo", "option --repo needs a value")]
    [InlineData("gh list-issues a/b", "unexpected argument 'a/b'")]
    [InlineData("gh list-issues --json '[1]'", "--json must be a JSON object")]
    [InlineData("gh whoami --x 1", "this tool takes no options")]
    public async Task BadUsageExplainsItselfWithoutCallingTheTool(string command, string message)
    {
        var (sh, calls) = Make();
        var r = await sh.RunAsync(command);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains(message, r.Stderr);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task ToolErrorsGoToStderrWithExitOne()
    {
        var (sh, _) = Make();
        var r = await sh.RunAsync("gh fail || echo handled");
        Assert.Equal("boom\n", r.Stderr);
        Assert.Equal("handled\n", r.Stdout);
    }

    // ---------- a real MCP server, connected in process ----------

    [Fact]
    public async Task MountsARealMcpServer()
    {
        Pipe toServer = new(), toClient = new();
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "weather", Version = "1.0" },
            ToolCollection =
            [
                McpServerTool.Create(([Description("City name")] string city, [Description("Days ahead")] int days = 1) =>
                        city == "Atlantis" ? throw new InvalidOperationException("no such city") : $$"""{"city":"{{city}}","days":{{days}},"high":21}""",
                    new McpServerToolCreateOptions { Name = "get_forecast", Description = "Weather forecast for a city" }),
                McpServerTool.Create(() => "secret", new McpServerToolCreateOptions { Name = "admin_reset", Description = "Dangerous" }),
            ],
        });
        using var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));

        var sh = new Shell();
        await using var mount = await sh.MountMcpAsync("weather", client, new McpMountOptions { Include = name => !name.StartsWith("admin") });
        Assert.Equal(["get_forecast"], mount.Tools);

        var help = (await sh.RunAsync("weather --help")).Stdout;
        Assert.Contains("get-forecast  Weather forecast for a city", help);
        Assert.DoesNotContain("admin", help);
        var toolHelp = (await sh.RunAsync("weather get-forecast --help")).Stdout;
        Assert.Contains("--city TEXT", toolHelp);
        Assert.Contains("City name (required)", toolHelp);
        Assert.Contains("--days INT", toolHelp);

        var r = await sh.RunAsync("weather get-forecast --city Lisbon --days 3 | jq -r '\"\\(.city) \\(.days) \\(.high)\"'");
        Assert.Equal("Lisbon 3 21\n", r.Stdout);

        var bad = await sh.RunAsync("weather get-forecast --city Atlantis");
        Assert.Equal(1, bad.ExitCode);
        Assert.NotEqual("", bad.Stderr);
        Assert.Equal(2, (await sh.RunAsync("weather admin-reset")).ExitCode);

        stop.Cancel();
        try { await running; } catch (OperationCanceledException) { }
    }
}
