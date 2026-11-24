<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/topwater_lockup_dark.png">
    <img alt="Topwater" src="assets/brand/topwater_lockup.png" width="400">
  </picture>
</p>

<h3 align="center">A simulated Linux shell for LLM agents, in C#</h3>

<p align="center">
  Your methods and your MCP servers become command-line programs in an in-memory Linux machine.<br>
  The model gets one tool, <code>bash</code>, and finds the rest with <code>--help</code>.
</p>

<p align="center">
  <b>.NET 8</b> · runs inside your process · no containers
</p>

---

```console
agent@topwater:~$ github --help
Usage: github TOOL [--option value]...

Tools:
  create-issue   Create a new issue in a repository
  list-issues    List issues in a repository
  ...
agent@topwater:~$ github list-issues --repo acme/api --state open | jq -r '.[].title' | head -3
Timeout on large uploads
Add retry to webhook sender
Docs: fix auth example
agent@topwater:~$ sudo rm -rf /
agent is not in the sudoers file.
```

## Why

Every tool definition you hand a model costs context, and a few MCP servers can add up to hundreds of them.
The shell is the one interface models already know well: they are good at `grep`, pipes and `--help`.

- **One tool instead of hundreds.** The model sees `bash`. It lists programs with `help` and reads a tool's
  options only when it needs that tool.
- **Tools compose.** Output from one tool pipes into `jq`, a file, or the next tool, without a round trip
  through the model.
- **No real shell.** Commands are interpreted inside your process, and the agent's files live in memory.

## Quick start

Add a project reference to `src/Topwater/Topwater.csproj` (and `src/Topwater.Mcp/Topwater.Mcp.csproj` for MCP).

```csharp
using Topwater;

var shell = new Shell();
shell.Fs.WriteAllText("/home/agent/notes.txt", "hi");     // give the agent some files

// Hand the model one tool: BashTool.Name, BashTool.Description, BashTool.InputSchemaJson
// ...and run whatever it asks for.
string output = await BashTool.ExecuteAsync(shell, "grep -c hi notes.txt");
```

### Mount an MCP server

```csharp
using Topwater.Mcp;

// a local server over stdio
await using var fs = await shell.MountMcpAsync("files", "npx", ["-y", "@modelcontextprotocol/server-filesystem", "/data"]);

// or any McpClient you already have (HTTP, auth, your own transport)
await using var gh = await shell.MountMcpAsync("github", client, new McpMountOptions { Include = name => !name.StartsWith("delete") });
```

Each server becomes one program. `github` lists its tools, `github list-issues --help` shows the options
(built from the tool's JSON Schema), and `github list-issues --repo acme/api` calls it. Nothing about the
server's tools is sent to the model up front.

### Turn your own methods into commands

```csharp
public class Tools
{
    [Command("orders", Description = "List orders")]
    public List<Order> Orders(string? status = null, int limit = 20) => Db.Orders(status, limit);   // prints as JSON
}

shell.Register(new Tools());      // orders --status open | jq length
```

## Try it

```bash
dotnet run --project examples/McpShell                                   # a terminal on the simulated machine
dotnet run --project examples/McpShell -- everything npx -y @modelcontextprotocol/server-everything
```

The second form mounts an MCP server as a command, so you can type what an agent would.

## Features

- **MCP servers as programs.** Tools become subcommands with generated `--help`, typed options and exit codes.
- **A real shell language.** Pipes, redirects, loops, functions, heredocs, `$(...)`, `set -e`, scripts.
- **About 100 standard commands.** `grep`, `sed`, `awk`, `jq`, `find`, `sort`, `diff`, `xargs` and friends.
- **Document reading.** Word, Excel, PowerPoint, PDF and HTML become searchable Markdown with `to-markdown`.
- **A regular user account.** The agent owns its home folder. System files are read-only and `sudo` says no.
- **Guard rails.** Runaway loops stop, long output is trimmed, and a failing tool returns an error instead of
  taking down your agent.

The full list is in the [reference](docs/reference.md).

## Test results

All of these were run by a real model, Gemini Flash, with nothing but the `bash` tool. The samples are small,
so read them as "it works", not as benchmarks.

**MCP server as a command.** The model triaged an issue tracker that it could reach only through an MCP server
mounted as `tracker`. It found the tools with `tracker --help` and passed 6 / 6 checks in 12 commands, none
failing. One run, five tools: `examples/McpAgent`.

**A real program through the shell.** With the real [OfficeCLI](https://github.com/iOfficeAI/OfficeCLI)
registered as a host command, the model turned a text file of notes into a Word document with a heading, a
summary, a table and two sections: 5 / 5 checks in 30 commands. One run: `examples/OfficeAgent`.

**Six everyday tasks, three ways of describing the tool** (two runs each, `examples/Harness`):

| How the tool was described | Tasks solved |
|---|---|
| "An ordinary Linux machine" | 9 / 12 |
| **"A sandboxed shell. No Python, network or git." (the default)** | **11 / 12** |
| "A simulated environment" | 9 / 12 |

Every failure was the model running out of its 25 turns, never a wrong answer. Four of the six tasks passed
every time. On the other two (sum a CSV, write a backup script) the model often explored the machine for 20
turns before doing a one-line job: reading unrelated files, looking for Python, searching for hidden test
files. The default description did best, but a two-task gap on twelve runs is not strong evidence.

**A system prompt line helps a little.** Adding "Go straight to the task: run only the commands it needs,
and do not explore the machine first" took those two tasks from 6 / 8 to 8 / 8 in one batch, but a later run
with the same line still explored for 21 turns and failed. Removing every Python file from the sample project
did not help either: 7 of 8 runs still checked for Python, and 5 of 8 passed. Expect this model to wander; give
it turns to spare.

Every example saves its full transcript under `runs/`, so you can see exactly what the model did.

**Earlier runs.** The Python version of the same shell passed three longer tasks: answering questions from
Word, Excel and HTML documents (6 / 6 checks), analysing a SQLite database (7 / 7), and cutting a release with
the real `git` (10 / 10). Those scripts are on the [`python` branch](../../tree/python).

## Limitations

- **Not on NuGet yet.** Reference the projects from this repository.
- **MCP calls are real.** The shell is simulated; the servers you mount are not. Use `Include` to expose only
  the tools you want the agent to have.
- **MCP tools only.** Resources and prompts are not exposed, and the tool list is read once when mounting.
- **No Python or network inside the machine.** Agents reach for both. The default tool description tells them
  up front.
- **PDFs need `pdftotext`** on your machine.
- **A real host program you register is real.** It runs with your process's permissions; path redirection is a
  convenience, not a sandbox. See the [reference](docs/reference.md#real-host-programs).

## Development

```bash
dotnet test
```

The earlier Python port is on the [`python` branch](../../tree/python) and is not kept in step with `main`.

## License

Not chosen yet. The shell was written from scratch, so any license is possible.
