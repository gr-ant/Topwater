using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Topwater.Cli;

namespace Topwater.Tests;

/// <summary>The topwater command and its MCP server.</summary>
public sealed class CliTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("topwater-cli-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);
    string P(string name) => Path.Combine(_dir, name);

    static async Task<(int Code, string Out, string Err)> Piped(string stdin, params string[] args)
    {
        StringWriter o = new(), e = new();
        var code = await Topwater.Cli.Cli.RunAsync(args, new Io(new StringReader(stdin), o, e, Terminal: false), CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    static Task<(int Code, string Out, string Err)> Cli(params string[] args) => Piped("", args);

    /// <summary>The server as `topwater mcp` builds it, talking to a real MCP client over in-memory pipes.</summary>
    static async Task<(McpClient Client, Func<Task> Stop)> Connect(Machine machine)
    {
        Pipe toServer = new(), toClient = new();
        var server = McpServerCommand.Create(machine, new ModelContextProtocol.Server.StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()));
        var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
        return (client, async () =>
        {
            await client.DisposeAsync();
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) { }
            await server.DisposeAsync();
        });
    }

    static async Task<string> Bash(McpClient client, string command)
    {
        var result = await client.CallToolAsync("bash", new Dictionary<string, object?> { ["command"] = command });
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    [Fact]
    public async Task McpServerOffersTheBashTool()
    {
        var (client, stop) = await Connect(Machine.Open(new MachineOptions(), TextWriter.Null));
        try
        {
            Assert.Equal("topwater", client.ServerInfo.Name);
            var tool = Assert.Single(await client.ListToolsAsync());
            Assert.Equal("bash", tool.Name);
            Assert.Equal(McpServerCommand.Description, tool.Description);
            Assert.StartsWith("Run a command in a sandboxed Linux shell", tool.Description);
            Assert.DoesNotContain("live interfaces", tool.Description); // no app programs behind the standalone server
            Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(BashTool.InputSchemaJson).RootElement, tool.JsonSchema));

            Assert.Equal("(no output)", await Bash(client, "cd /tmp && x=5 && echo hi > a.txt"));
            Assert.Equal("/tmp\n5 hi\n", await Bash(client, "pwd; echo $x $(cat a.txt)"));
            Assert.Equal("cat: nope: No such file or directory\n[exit code 1]", await Bash(client, "cat nope"));
            // no host access by default: the host's files are not there
            Assert.Contains("No such file or directory", await Bash(client, $"ls {_dir}"));
        }
        finally { await stop(); }
    }

    [Fact]
    public async Task McpServerSavesStateAfterEveryCall()
    {
        var options = new MachineOptions { State = P("machine.zip") };
        var (client, stop) = await Connect(Machine.Open(options, TextWriter.Null));
        try
        {
            await Bash(client, "echo kept > notes.txt; cd /tmp");
            Assert.True(File.Exists(P("machine.zip")));   // saved before the server stopped
        }
        finally { await stop(); }

        var (again, stop2) = await Connect(Machine.Open(options, TextWriter.Null));
        try { Assert.Equal("/tmp\nkept\n", await Bash(again, "pwd; cat ~/notes.txt")); }
        finally { await stop2(); }
    }

    [Fact]
    public async Task RunPassesOutputAndExitCodeThrough()
    {
        var (code, stdout, stderr) = await Cli("run", "echo out; echo err >&2; exit 3");
        Assert.Equal((3, "out\n", "err\n"), (code, stdout, stderr));
        // words after the first belong to the command, options included
        var echo = await Cli("run", "echo", "-n", "x", "|", "wc", "-c");   // joined with spaces and parsed by the shell, like ssh
        Assert.Equal((0, "1\n"), (echo.Code, echo.Out));
        Assert.Equal(124, (await Cli("run", "--timeout", "0.5", "sleep 10")).Code);
    }

    [Fact]
    public async Task StatePersistsBetweenRuns()
    {
        Assert.Equal(0, (await Cli("run", "--state", P("m.zip"), "cd /tmp && echo 1 > f && export V=2")).Code);
        Assert.Equal("/tmp 1 2\n", (await Cli("run", "--state", P("m.zip"), "echo $PWD $(cat f) $V")).Out);
        // --from starts from a snapshot without changing it
        Assert.Equal(0, (await Cli("run", "--from", P("m.zip"), "rm /tmp/f")).Code);
        Assert.Equal("1\n", (await Cli("run", "--from", P("m.zip"), "cat /tmp/f")).Out);
    }

    [Fact]
    public async Task ImportCopiesInAndExportCopiesOut()
    {
        Directory.CreateDirectory(P("proj/sub"));
        File.WriteAllText(P("proj/sub/a.txt"), "code\n");
        File.WriteAllBytes(P("proj/blob.bin"), [0xff, 0x00, 0xfe]);
        File.WriteAllText(P("secret.txt"), "host secret");
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(P("proj/link.txt"), P("secret.txt"));

        var (code, stdout, _) = await Cli("run", "--import", P("proj") + ":/home/agent/proj", "--export", "/home/agent/proj:" + P("out"),
            "cd proj && cat sub/a.txt && wc -c < blob.bin && ls && stat -c %U sub/a.txt && echo new > sub/b.txt && rm sub/a.txt");
        Assert.Equal(0, code);
        Assert.Equal("code\n3\nblob.bin\nsub\nagent\n", stdout);   // the symlink is not followed or copied
        Assert.Equal("new\n", File.ReadAllText(P("out/sub/b.txt")));
        Assert.Equal([0xff, 0x00, 0xfe], File.ReadAllBytes(P("out/blob.bin")));
        Assert.False(File.Exists(P("out/sub/a.txt")));
        Assert.Equal("code\n", File.ReadAllText(P("proj/sub/a.txt")));   // a copy: the host's files are untouched

        Assert.Equal(2, (await Cli("run", "--import", P("missing") + ":/x", "true")).Code);
        Directory.CreateDirectory(P("big"));
        File.WriteAllBytes(P("big/f"), new byte[2 << 20]);
        var full = await Cli("run", "--disk", "1", "--import", P("big") + ":/x", "true");
        Assert.Equal(2, full.Code);
        Assert.Contains("more than the", full.Err);
    }

    [Fact]
    public async Task ExportNeverWritesThroughAHostLink()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(P("out"));
        Directory.CreateDirectory(P("elsewhere"));
        Directory.CreateSymbolicLink(P("out/d"), P("elsewhere"));
        var (_, _, stderr) = await Cli("run", "--export", "/tmp/x:" + P("out"), "mkdir -p /tmp/x/d && echo hi > /tmp/x/d/f && echo ok > /tmp/x/g");
        Assert.Contains("skipped /tmp/x/d", stderr);
        Assert.Empty(Directory.GetFiles(P("elsewhere")));
        Assert.Equal("ok\n", File.ReadAllText(P("out/g")));
    }

    [Fact]
    public async Task SnapshotSaveInfoAndPipedScripts()
    {
        Directory.CreateDirectory(P("data"));
        File.WriteAllText(P("data/x.csv"), "a,b\n1,2\n");
        Assert.Equal(0, (await Cli("snapshot", "save", P("base.zip"), "--import", P("data") + ":data")).Code);
        var info = (await Cli("snapshot", "info", P("base.zip"))).Out;
        Assert.Contains("topwater-snapshot version 1", info);
        Assert.Contains("agent@topwater, cwd /home/agent", info);

        // piped stdin is one script, so multi-line constructs work; the exit code comes back
        var (code, stdout, _) = await Piped("for f in data/*.csv; do\n  wc -l < \"$f\"\ndone\nexit 5\n", "--from", P("base.zip"));
        Assert.Equal((5, "2\n"), (code, stdout));
        var load = await Piped("wc -l < data/x.csv", "snapshot", "load", P("base.zip"));
        Assert.Equal((0, "2\n"), (load.Code, load.Out));

        File.WriteAllText(P("bad.zip"), "not a zip");
        var bad = await Cli("snapshot", "info", P("bad.zip"));
        Assert.Equal(2, bad.Code);
        Assert.Contains("not a valid Topwater snapshot", bad.Err);
    }

    [Fact]
    public async Task UsageErrorsAndCommandsFromOtherBuilds()
    {
        Assert.Contains("topwater mcp", (await Cli("--help")).Out);
        Assert.Equal(2, (await Cli("run")).Code);
        Assert.Equal(2, (await Cli("--bogus")).Code);
        Assert.Equal(2, (await Cli("run", "--import", "nocolon", "true")).Code);
        foreach (var name in new[] { "export", "import", "replay" })
        {
            var (code, _, stderr) = await Cli(name);
            Assert.Equal(1, code);
            Assert.Contains($"topwater {name}: not available in this build", stderr);
        }
    }
}
