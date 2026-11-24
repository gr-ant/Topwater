<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/topwater_lockup_dark.png">
    <img alt="Topwater" src="assets/brand/topwater_lockup.png" width="400">
  </picture>
</p>

Topwater is a .NET 8 library that gives an LLM agent a single `bash` tool backed by a simulated Linux shell
running inside your process.

The shell has an in-memory filesystem, a real shell language (pipes, loops, functions, heredocs), about 100
standard commands (`grep`, `sed`, `awk`, `jq`, `find`, ...) and a regular non-root user. Your C# methods, MCP
servers and selected real host programs appear in it as command-line programs, so the model discovers them with
`--help` instead of receiving every tool definition up front. No containers are involved.

```console
agent@topwater:~$ github --help
GitHub issues

Usage: github TOOL [--option value]...
       github TOOL --help

Tools:
  create-issue  Create a new issue in a repository
  list-issues   List issues in a repository
agent@topwater:~$ github list-issues --repo acme/api --state open | jq -r '.[].title'
Timeout on large uploads
Add retry to webhook sender
Docs: fix auth example
agent@topwater:~$ orders --status open | jq length
2
agent@topwater:~$ sudo rm -rf /
agent is not in the sudoers file.
[exit code 1]
```

Here `github` is an MCP server and `orders` is a C# method. The model sees neither until it runs them.

## Installation

Topwater is not on NuGet yet. Reference the projects from this repository:

```bash
dotnet add reference path/to/Topwater/src/Topwater/Topwater.csproj
dotnet add reference path/to/Topwater/src/Topwater.Mcp/Topwater.Mcp.csproj   # only for MCP servers
```

The core project has no dependencies. `Topwater.Mcp` uses the official `ModelContextProtocol` SDK.

## Usage

Create a shell, give the model the `bash` tool, and pass each command it sends to `BashTool.ExecuteAsync`:

```csharp
using Topwater;

var shell = new Shell();
shell.Fs.WriteAllText("/home/agent/notes.txt", "hello");

// Tool definition for your model API: BashTool.Name, BashTool.Description, BashTool.InputSchemaJson.
// When the model calls it with {"command": "..."}:
string output = await BashTool.ExecuteAsync(shell, "grep -c hello notes.txt");
// Send `output` back as the tool result.
```

`ExecuteAsync` returns stdout, then stderr, then `[exit code N]` if the command failed. The working directory,
variables, functions and files persist between calls, so use one `Shell` per conversation. A complete agent loop
against the Gemini API is in [`examples/Shared/GeminiAgent.cs`](examples/Shared/GeminiAgent.cs).

To use the shell yourself:

```bash
dotnet run --project examples/McpShell
dotnet run --project examples/McpShell -- everything npx -y @modelcontextprotocol/server-everything
```

The second form mounts an MCP server as the command `everything`.

`dotnet run --project examples/WebShell` serves the same terminal as a web page on port 8765 for other machines
on your network. It registers no host programs or MCP servers, so visitors only reach the in-memory machine.

## Adding programs

### C# methods

```csharp
using System.ComponentModel;
using Topwater;

public class Tools
{
    [Command("orders", Description = "List orders")]
    public List<Order> Orders([Description("Filter by status")] string? status = null, int limit = 20) => Db.Orders(status, limit);
}

shell.Register(new Tools());
```

```console
agent@topwater:~$ orders --help
Usage: orders [--status <text>] [--limit <int>]

List orders

Options:
  --status <text>  Filter by status
  --limit <int>    [default: 20]
```

Parameters become `--kebab-case` options, `bool` becomes a flag and enums become choices. A returned string is
printed, an `int` is the exit code, and any other value is printed as JSON. An exception becomes an error
message and a non-zero exit code. For full control over arguments and streams, register a handler:
`shell.Register("deploy", async ctx => { ...; return 0; })`.

### MCP servers

```csharp
using Topwater.Mcp;

// a server started over stdio
await using var files = await shell.MountMcpAsync("files", "npx", ["-y", "@modelcontextprotocol/server-filesystem", "/data"]);

// or an McpClient you already created (HTTP, auth, custom transport)
await using var github = await shell.MountMcpAsync("github", client, new McpMountOptions
{
    Include = name => !name.StartsWith("delete"),
});
```

Each server becomes one program with a subcommand per tool. Options are generated from each tool's JSON Schema
and checked before the call; a tool error goes to stderr with exit code 1. Only tools are mounted, not resources
or prompts, and the tool list is read once at mount time. `shell.RegisterTools(...)` applies the same mapping to
any tools described by a JSON Schema, without MCP.

### Host programs

```csharp
using var office = shell.RegisterHostCommand("officecli", options: new HostCommandOptions
{
    Environment = { ["OFFICECLI_NO_AUTO_RESIDENT"] = "1" },
});
```

A registered program is a real executable from your `PATH`. Before each call, the agent's home directory and
`/tmp` are copied to a private folder on disk; the program runs there with path arguments redirected into it, and
its changes are copied back. `HostCommandOptions` sets the workspace, shared folders, environment, timeout,
exclusions and an optional `Launcher` that wraps the command line.

## The simulated machine

| Area | What it has |
|---|---|
| Shell language | pipes, redirects, `$(...)`, `if`/`for`/`while`/`case`, functions, heredocs, parameter expansion, `set -e`, scripts |
| Programs | about 100, including `grep` `sed` `awk` `jq` `find` `sort` `diff` `xargs`; `help` lists them all, yours included |
| Documents | `to-markdown` converts Word, Excel, PowerPoint, HTML, CSV and JSON to Markdown; PDF needs `pdftotext` on the host |
| User | `agent`, a regular user who owns its home directory and can write to `/tmp`; system folders are read-only and `sudo` refuses |
| Filesystem | in memory, laid out like Ubuntu 24.04; populate it from your code through `shell.Fs` |

The [reference](docs/reference.md) lists every supported feature, command and option.

## Safety and limits

- The shell is simulated: commands are interpreted in your process and touch only the in-memory filesystem.
  MCP calls and registered host programs are real. Use `McpMountOptions.Include` to expose only the tools you
  want the agent to have.
- A host program runs with your process's permissions. Path redirection keeps it pointed at its private folder
  but is not a sandbox; set `HostCommandOptions.Launcher` to run it inside a real one (bubblewrap, firejail, a
  container). See [Real host programs](docs/reference.md#real-host-programs).
- Loops stop after 100,000 iterations, a regular expression that runs longer than 5 seconds is stopped, and tool
  output is cut at 30,000 characters with a note saying how much was dropped.
- A command that throws becomes an error message and exit code, not an exception in your agent loop.
- A `Shell` is not safe to share between threads.

## Limitations

- No Python, network access or git inside the machine unless you register them. The default tool description
  tells the model so.
- Not supported: arrays, background jobs, symlinks. The full list is under
  [Not supported](docs/reference.md#not-supported).
- MCP resources and prompts are not exposed.

## Test results

These runs used Gemini Flash with only the `bash` tool. The samples are small: they show that it works and are
not benchmarks. Each example saves its full transcript under `runs/`.

| Example | Task | Result |
|---|---|---|
| `examples/McpAgent` | Triage an issue tracker reachable only through an MCP server | 6 / 6 checks in 12 commands |
| `examples/OfficeAgent` | Turn text notes into a Word document with the real OfficeCLI | 5 / 5 checks in 30 commands |

`examples/Harness` runs six everyday tasks, twice each, under three tool descriptions:

| Tool description | Tasks solved |
|---|---|
| "An ordinary Linux machine" | 9 / 12 |
| "A sandboxed shell. No Python, network or git." (the default) | 11 / 12 |
| "A simulated environment" | 9 / 12 |

Every failure was the model running out of its 25 turns, usually after exploring the machine instead of doing
the task; none was a wrong answer. A system prompt line telling it to go straight to the task helped in one
batch and not in another. Removing the Python files from the sample project did not stop it checking for Python.

## Development

```bash
dotnet test
```

An earlier Python port is on the [`python` branch](../../tree/python) and is not kept in step with `main`.

## License

No license has been chosen yet.
