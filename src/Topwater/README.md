![Topwater](https://raw.githubusercontent.com/gr-ant/Topwater/main/assets/brand/topwater_lockup.png)

Topwater gives an LLM agent one `bash` tool backed by a simulated Linux machine running inside your .NET process:
a bash interpreter, an in-memory Ubuntu-like filesystem, about 140 commands (`grep`, `sed`, `awk`, `jq`, `find`,
`tar`, `git`, ...), and your own C# methods as command-line programs.

```bash
dotnet add package Topwater --prerelease
```

## Use

```csharp
using Topwater;

var shell = new Shell();
shell.Fs.WriteAllText("/home/agent/notes.txt", "hello");

// Give your model one tool: BashTool.Name, BashTool.Description, BashTool.InputSchemaJson.
// When it calls the tool, run the command and send the text back:
string output = await BashTool.ExecuteAsync(shell, "grep -c hello notes.txt");
```

Register your own methods and the model finds them with `help` and `--help`, and pipes them into `jq` like any program:

```csharp
public class Tools
{
    [Command("orders", Description = "List orders")]
    public IEnumerable<Order> Orders(string? status = null) => Db.Orders(status);
}

shell.Register(new Tools());   // agent: orders --status open | jq -r '.[].Customer'
```

## Safe by default

Commands are interpreted in your process and only touch the in-memory filesystem. Runaway loops, slow regular
expressions, oversized output, calls that run too long and runaway memory or disk use are all cut off. Nothing the
agent types reaches your machine or the network unless you register a real host program, an MCP server or an HTTP
API, or allow a list of web sites with `AllowWeb`.

## More

- [Documentation](https://github.com/gr-ant/Topwater/blob/main/docs/README.md): [the
  machine](https://github.com/gr-ant/Topwater/blob/main/docs/machine.md), [your
  programs](https://github.com/gr-ant/Topwater/blob/main/docs/programs.md),
  [snapshots and recordings](https://github.com/gr-ant/Topwater/blob/main/docs/snapshots-and-recordings.md), [limits and
  safety](https://github.com/gr-ant/Topwater/blob/main/docs/limits.md)
- [Topwater.Mcp](https://www.nuget.org/packages/Topwater.Mcp): mount an MCP server as a command
- [Topwater.AI](https://www.nuget.org/packages/Topwater.AI): use the shell as a Microsoft.Extensions.AI tool
- [Topwater.Containers](https://www.nuget.org/packages/Topwater.Containers): a machine as a Docker image, and back
- [Topwater.Replay](https://www.nuget.org/packages/Topwater.Replay): recordings as regression tests
- [Topwater.Cli](https://www.nuget.org/packages/Topwater.Cli): the `topwater` command and MCP server
- [Source and issues](https://github.com/gr-ant/Topwater) · MIT licensed
