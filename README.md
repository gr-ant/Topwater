<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/topwater_lockup_dark.png">
    <img alt="Topwater" src="assets/brand/topwater_lockup.png" width="400">
  </picture>
</p>

Topwater is a .NET 8 library that gives an LLM agent one `bash` tool backed by a simulated Linux shell running
inside your process. Your C# methods and MCP servers show up in it as command-line programs.

```console
agent@topwater:~$ orders --status open | jq length
2
agent@topwater:~$ github list-issues --repo acme/api | jq -r '.[].title'
Timeout on large uploads
Add retry to webhook sender
agent@topwater:~$ sudo rm -rf /
agent is not in the sudoers file.
[exit code 1]
```

`orders` is a C# method and `github` is an MCP server. The model finds both with `help` and `--help`, so you
send it one tool definition instead of one per function.

## Install

Not on NuGet yet. Reference the project from a clone of this repository:

```bash
dotnet add reference path/to/Topwater/src/Topwater/Topwater.csproj
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

Files, variables and the working directory persist between calls. Use one `Shell` per conversation.

## Add your own programs

A C# method:

```csharp
public class Tools
{
    [Command("orders", Description = "List orders")]
    public List<Order> Orders(string? status = null, int limit = 20) => Db.Orders(status, limit);
}

shell.Register(new Tools());      // orders --status open --limit 5
```

An MCP server (add `src/Topwater.Mcp`):

```csharp
await using var github = await shell.MountMcpAsync("github", client);   // github list-issues --repo acme/api
```

A real program from your machine:

```csharp
using var git = shell.RegisterHostCommand("git");
```

## What is in the machine

- A shell language: pipes, redirects, loops, functions, arrays, heredocs, scripts.
- About 100 standard commands, including `grep`, `sed`, `awk`, `jq` and `find`.
- An in-memory filesystem laid out like Ubuntu, with a regular user who cannot `sudo`.
- `to-markdown` for reading Word, Excel, PowerPoint, PDF and HTML files.

There is no Python, network or git unless you add them. The [reference](docs/reference.md) has the full list.

## Safety

The shell is simulated: commands are interpreted in your process and only touch the in-memory filesystem.
Runaway loops, slow regular expressions and oversized output are cut off.

MCP calls and host programs are real. A host program runs with your process's permissions, and Topwater does not
sandbox it; see [Real host programs](docs/reference.md#real-host-programs) before registering one.

## Try it

```bash
dotnet run --project examples/McpShell    # a terminal on the simulated machine
dotnet run --project examples/WebShell    # the same, as a web page on port 8765
dotnet test
```

`examples/` also has three agents run against Gemini Flash: one triages issues through an MCP server (6 / 6
checks), one writes a Word document with the real OfficeCLI (5 / 5), and a harness runs six everyday tasks
(11 / 12 solved with the default tool description). These are small samples, not benchmarks.

## License

No license has been chosen yet.
