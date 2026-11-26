# Topwater.Mcp

Mount any MCP server as one command-line program inside a [Topwater](https://github.com/gr-ant/Topwater) shell. The model discovers tools with
`--help` instead of loading every tool definition up front, and pipes results into `jq` like any program.

```bash
dotnet add package Topwater.Mcp --prerelease
```

```csharp
using Topwater;
using Topwater.Mcp;

var shell = new Shell();
await using var files = await shell.MountMcpAsync("files", "npx", ["-y", "@modelcontextprotocol/server-filesystem", "/data"]);
```

What the agent types:

```console
files --help                          # lists the tools
files read-file --help                # options built from the tool's JSON Schema
files read-file --path /data/a.txt    # calls the tool; the result goes to stdout
```

Numbers, required options and unknown options are checked before the call. Calls are real, so expose only what you
would let the agent do. See [MCP servers](https://github.com/gr-ant/Topwater/blob/main/docs/programs.md#mcp-servers).

To go the other way, giving an MCP client (Claude Desktop, VS Code, Cursor, ...) a Topwater shell as its `bash` tool, run
`topwater mcp` from the [Topwater.Cli](https://www.nuget.org/packages/Topwater.Cli) package.
