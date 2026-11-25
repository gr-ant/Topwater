<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/topwater_lockup_dark.png">
    <img alt="Topwater" src="assets/brand/topwater_lockup.png" width="400">
  </picture>
</p>

Topwater gives an LLM agent one `bash` tool backed by a simulated Linux shell that runs inside your application.
Your own functions, MCP servers and HTTP APIs show up in it as command-line programs. It is available for
**.NET**, **Python** and **TypeScript**.

You register a function:

```csharp
public class Tools
{
    [Command("orders", Description = "List orders")]
    public IEnumerable<Order> Orders(string? status = null) => Db.Orders(status);
}

var shell = new Shell();
shell.Register(new Tools());
```

The model, with its one `bash` tool, finds it and uses it like any other program:

```console
agent@topwater:~$ help | grep orders
  orders       List orders
agent@topwater:~$ orders --help
Usage: orders [--status <text>]

List orders

Options:
  --status <text>
agent@topwater:~$ orders --status open | jq -r '.[].Customer' | sort | uniq -c
      2 Acme
```

You send the model one tool definition instead of one per function, and it can pipe your tools into `jq`,
`grep` and each other.

## Install

| | |
|---|---|
| **.NET 8+** | `dotnet add package Topwater --prerelease` (add `Topwater.Mcp` for MCP servers, `Topwater.AI` for Microsoft.Extensions.AI) |
| **Python 3.10+** | `pip install "git+https://github.com/gr-ant/Topwater@python#subdirectory=python"` (preview, not on PyPI yet) |
| **TypeScript, Node 20+** | `git clone -b typescript https://github.com/gr-ant/Topwater && cd Topwater/typescript && npm install && npm run build` (preview, not on npm yet) |

None of them has runtime dependencies in its core.

## Use

Give the model one tool (named `bash`, with the description and input schema Topwater provides). When it calls the
tool, run the command and send the text back.

**C#**

```csharp
using Topwater;

var shell = new Shell();
shell.Fs.WriteAllText("/home/agent/notes.txt", "hello");
string output = await BashTool.ExecuteAsync(shell, "grep -c hello notes.txt");   // BashTool.Name, .Description, .InputSchemaJson
```

With Microsoft.Extensions.AI, skip the wiring: `options.Tools = [shell.AsAIFunction()]`.

**Python**

```python
import topwater
from topwater import Shell

shell = Shell()
output = await topwater.execute_tool(shell, "echo hello | tr a-z A-Z")   # TOOL_NAME, TOOL_DESCRIPTION, TOOL_INPUT_SCHEMA
# or, outside async code: shell.run(command).to_tool_output()
```

**TypeScript**

```ts
import { Shell, BashTool } from 'topwater';

const shell = new Shell();
const output = await BashTool.execute(shell, 'echo hello | tr a-z A-Z');   // BashTool.name, .description, .inputSchema
```

Files, variables and the working directory persist between calls. Use one shell per conversation.

## Add your own programs

| | C# | Python | TypeScript |
|---|---|---|---|
| Your functions | `[Command]` methods, `shell.Register(...)` | `@command` methods, `shell.register(...)` | `shell.register(name, handler)` |
| Tools described by JSON Schema | `shell.RegisterTools(...)` | `register_tools(shell, ...)` | `registerTools(shell, ...)` |
| An MCP server | `shell.MountMcpAsync(...)` (Topwater.Mcp) | `mount_mcp(shell, ...)` | `mountMcp(shell, ...)` |
| An HTTP API, credentials hidden from the model | `shell.RegisterApi(...)` | `register_api(shell, ...)` | `registerApi(shell, ...)` |
| A real program from your machine | `shell.RegisterHostCommand("git")` | `register_host_command(shell, "git")` | not yet |
| Snapshots: undo, fork, save and load the machine | `shell.Snapshot()`, `Fork()`, `Save()`, `Load()` | not yet | not yet |

An MCP server becomes one program with a subcommand per tool (`github list-issues --repo acme/api`). An API is
called by alias, never by host, and the model never sees the credential
(`api get jira /issue/PROJ-123 | jq .fields.status`). The [reference](docs/reference.md) covers each of these in
detail using the C# API, and each port's README maps it onto its own
([Python](https://github.com/gr-ant/Topwater/tree/python/python), [TypeScript](https://github.com/gr-ant/Topwater/tree/typescript/typescript)).

## What is in the machine

The same in all three:

- A shell language: pipes, redirects, loops, functions, arrays, heredocs, scripts.
- About 110 standard commands, including `grep`, `sed`, `awk`, `jq`, `find`, `tar` and `bc`.
- An in-memory filesystem laid out like Ubuntu, with a regular user who cannot `sudo`.
- `to-markdown` for reading Word, Excel, PowerPoint and HTML files.

There is no Python, git or open network access unless you add them. The [reference](docs/reference.md) has the full list.

Each command is written from POSIX, the man pages and comparison against the real tool's output — never by copying
or porting its source — and is checked by differential tests that run the real binary. All three implementations
check themselves against the same recorded outputs. The command set was audited for provenance in November 2026.

## Safety

The shell is simulated: commands are interpreted in your process and only touch the in-memory filesystem.
Runaway loops, slow regular expressions, oversized output, calls that run too long and runaway memory or disk use are
all cut off.

MCP calls, registered APIs and host programs are real. A host program runs with your process's permissions, and
Topwater does not sandbox it unless you ask: `Launcher = HostSandbox.Bubblewrap()` in C# (`bubblewrap()` in Python)
runs it under bubblewrap, where it can see only its private copy of the agent's files and has no network. See
[Real host programs](docs/reference.md#real-host-programs) before registering one.

## Try it

```bash
dotnet run --project examples/McpShell    # a terminal on the simulated machine
dotnet run --project examples/WebShell    # the same, as a web page on port 8765
dotnet test
```

For the ports, run `pytest` in `python/` on the `python` branch, and `npm test` in `typescript/` on the `typescript` branch.

`examples/` also has three agents run against Gemini Flash: one triages issues through an MCP server (6 / 6
checks), one writes a Word document with the real OfficeCLI (5 / 5), and one answers questions over CSV files with
the real DuckDB (8 / 8). A separate harness runs the same tool against Claude, GPT and others through OpenRouter; on
its six everyday tasks with the default tool description, Claude Sonnet 5.5 and GPT-5.6 each solved 6 / 6. These are
small samples, not benchmarks.

## License

MIT. See [LICENSE](LICENSE).
