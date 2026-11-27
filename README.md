<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/topwater_lockup_dark.png">
    <img alt="Topwater" src="assets/brand/topwater_lockup.png" width="400">
  </picture>
</p>

<p align="center">
  One <code>bash</code> tool for your LLM agent, backed by a simulated Linux machine that runs inside your application.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/Topwater"><img alt="NuGet" src="https://img.shields.io/nuget/vpre/Topwater?label=NuGet"></a>
  <a href="https://pypi.org/project/topwater/"><img alt="PyPI" src="https://img.shields.io/pypi/v/topwater?include_prereleases&label=PyPI"></a>
  <a href="https://www.npmjs.com/package/topwater"><img alt="npm" src="https://img.shields.io/npm/v/topwater?label=npm"></a>
  <a href="https://github.com/gr-ant/Topwater/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/gr-ant/Topwater/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

<p align="center">
  <a href="https://gr-ant.github.io/Topwater/"><strong>Try it out in your browser →</strong></a>
  &nbsp;·&nbsp;
  <a href="https://gr-ant.github.io/Topwater/replay.html">Replay viewer</a>
</p>

Topwater is a library for **.NET**, **Python** and **TypeScript**. You register your functions, MCP servers and
HTTP APIs. The model sees them as command-line programs on a small Linux machine and reaches all of them through a
single tool:

```console
agent@topwater:~$ help | grep orders
  orders       List orders
agent@topwater:~$ orders --help
Usage: orders [--status <text>]

List orders

Options:
  --status <text>
agent@topwater:~$ orders --status open | jq length
2
```

- **One tool definition** instead of one per function. The model discovers programs with `help` and `--help`.
- **Composition.** Your tools pipe into `jq`, `grep`, `awk` and each other, and results can be kept in files.
- **Nothing real by default.** Commands are interpreted in your process against an in-memory filesystem, with
  limits on time, memory, disk and output. See [Safety](#safety).

## Quickstart

Each example registers one function and runs the command a model would send. All three print `2`.

### .NET

Requires .NET 8 or later.

```bash
dotnet add package Topwater --prerelease
```

This installs `0.1.0-preview.4`; [CHANGELOG.md](CHANGELOG.md) says what changed.

```csharp
using Topwater;

var shell = new Shell();
shell.Register(new Tools());

// Every call the model makes to its bash tool goes through this one method.
Console.WriteLine(await BashTool.ExecuteAsync(shell, "orders --status open | jq length"));

public record Order(int Id, string Customer, string Status);

public class Tools
{
    static readonly Order[] All = [new(1, "Acme", "open"), new(2, "Globex", "shipped"), new(3, "Acme", "open")];

    [Command("orders", Description = "List orders")]
    public IEnumerable<Order> Orders(string? status = null) =>
        All.Where(o => status is null || o.Status == status);
}
```

### Python

Requires Python 3.10 or later. Standard library only.

```bash
pip install --pre topwater
```

```python
from topwater import Shell, command

ORDERS = [
    {"id": 1, "customer": "Acme", "status": "open"},
    {"id": 2, "customer": "Globex", "status": "shipped"},
    {"id": 3, "customer": "Acme", "status": "open"},
]

class Tools:
    @command("orders", description="List orders")
    def orders(self, status: str | None = None):
        return [o for o in ORDERS if status in (None, o["status"])]

shell = Shell().register(Tools())

# Every call the model makes to its bash tool goes through this one line.
print(shell.run("orders --status open | jq length").to_tool_output())
```

Inside async code, use `await topwater.execute_tool(shell, command)` instead of `shell.run`.

### TypeScript

Requires Node 22.3 or later. No runtime dependencies; the package is an ES module.

```bash
npm install topwater
```

```ts
import { Shell, BashTool } from 'topwater';

const ORDERS = [
  { id: 1, customer: 'Acme', status: 'open' },
  { id: 2, customer: 'Globex', status: 'shipped' },
  { id: 3, customer: 'Acme', status: 'open' },
];

const shell = new Shell();
shell.registerTools(
  'shop',
  [{ name: 'orders', description: 'List orders', inputSchema: { type: 'object', properties: { status: { type: 'string' } } } }],
  (tool, args) => ({ text: JSON.stringify(ORDERS.filter(o => !args.status || o.status === args.status)) }),
);

// Every call the model makes to its bash tool goes through this one method.
console.log(await BashTool.execute(shell, 'shop orders --status open | jq length'));
```

TypeScript describes tools with a JSON Schema, so the function appears as a subcommand: `shop orders`.

### No code

`topwater mcp` is an MCP server with one sandboxed `bash` tool, for Claude Desktop, Claude Code, Cursor, VS Code or
any other MCP client. `topwater` alone opens a shell on the simulated machine.

```bash
dotnet tool install -g Topwater.Cli --prerelease                      # needs .NET 8
```

From `0.1.0-preview.3` on, each release also carries a standalone binary that needs no .NET:

```bash
curl -fsSL https://gr-ant.github.io/Topwater/install.sh | sh          # Linux, macOS
```

```powershell
irm https://gr-ant.github.io/Topwater/install.ps1 | iex               # Windows
```

From `0.1.0-preview.5` on, also as a container image (linux/amd64 and linux/arm64; Docker Desktop on macOS and Windows
runs it too), with nothing to install but Docker:

```bash
docker run -i --rm ghcr.io/gr-ant/topwater:preview mcp
```

```json
{
  "mcpServers": {
    "topwater": {
      "command": "docker",
      "args": ["run", "-i", "--rm", "-v", "topwater:/data", "ghcr.io/gr-ant/topwater:preview", "mcp", "--state", "machine.topwater"]
    }
  }
}
```

That is Claude Desktop's `claude_desktop_config.json`; the named volume `topwater` keeps the machine between
restarts. VS Code and the other clients are in [Docker](docs/cli.md#docker).

See [Command line and MCP server](docs/cli.md) for the commands, the options and a Claude Desktop config.

## Connect it to a model

Give the model one tool, using the name, description and input schema that Topwater provides. When the model calls
it, run the command and return the text.

| | Tool definition | Run a call |
|---|---|---|
| **.NET** | `BashTool.Name`, `BashTool.Description`, `BashTool.InputSchemaJson` | `await BashTool.ExecuteAsync(shell, command)` |
| **Python** | `topwater.TOOL_NAME`, `TOOL_DESCRIPTION`, `TOOL_INPUT_SCHEMA` | `await topwater.execute_tool(shell, command)` |
| **TypeScript** | `BashTool.name`, `BashTool.description`, `BashTool.inputSchema` | `await BashTool.execute(shell, command)` |

The tool takes one string argument, `command`. Files, variables and the working directory persist between calls.
Use one shell per conversation; a shell is not safe to share between threads.

Adapters do this wiring for common agent frameworks:

| | Frameworks | How |
|---|---|---|
| **.NET** | Microsoft.Extensions.AI, Semantic Kernel, Microsoft Agent Framework, the OpenAI and Anthropic SDKs | `shell.AsAIFunction()` from the `Topwater.AI` package. [Details](docs/integrations.md#microsoftextensionsai) |
| **Python** | LangChain and LangGraph, LlamaIndex, OpenAI Agents SDK, Claude Agent SDK, Pydantic AI, CrewAI, smolagents, Google ADK | `bash_tool(shell)` from `topwater.integrations.<framework>`. [Details](python/README.md#agent-frameworks) |
| **TypeScript** | Vercel AI SDK, OpenAI Agents, LangChain.js and LangGraph.js, Mastra, Claude Agent SDK | `bashTool(shell)` from `topwater/<framework>`. [Details](typescript/README.md#agent-frameworks) |
| **Any MCP client** | Claude Desktop, Claude Code, Cursor, VS Code | [`topwater mcp`](docs/cli.md#mcp-server), no code |

The Claude Agent SDK ships Claude Code's own tools, and its `Bash` runs on your computer. Use `bash_options(shell)`
(Python) or `bashOptions(shell)` (TypeScript): they return options whose only tool is the Topwater shell, with the
built-in tools and the developer's own Claude Code settings left out. See
[Claude Agent SDK](docs/integrations.md#claude-agent-sdk).

## Add your own programs

| | .NET | Python | TypeScript |
|---|---|---|---|
| Your functions | `[Command]` methods, `shell.Register(obj)` | `@command` methods, `shell.register(obj)` | `shell.register(name, handler, description)` |
| Tools described by JSON Schema | `shell.RegisterTools(...)` | `register_tools(shell, ...)` | `shell.registerTools(...)` |
| An MCP server | `shell.MountMcpAsync(...)` (package `Topwater.Mcp`) | `topwater.mcp.mount_mcp(shell, ...)` | `shell.mountMcp(...)` |
| An HTTP API, credentials hidden from the model | `shell.RegisterApi(...)` | `register_api(shell, ...)` | `shell.registerApi(...)` |
| [Web access](docs/web.md) to sites you list, through `curl` and `wget` | `shell.AllowWeb(new WebPolicy { ... })` | `shell.allow_web(WebPolicy(...))` | `shell.allowWeb({ ... })` |
| [Outside files as a folder](docs/mounts.md) (a host folder, an Obsidian vault, your own source), read-only or with changes you review | `await shell.MountAsync(path, source, mode)` | `shell.mount(path, source, mode)` | `await shell.mount(path, source, mode)` |
| [Your Notion pages as a folder](docs/connectors/notion.md): the agent reorganises them, you review the change set | not yet | `shell.mount(path, NotionSource(token, root), "staged")` | not yet |
| [SharePoint and OneDrive as a folder](docs/connectors/graph.md): libraries, lists and subsites; you review the change set | not yet | `shell.mount(path, GraphSource(token, site=url), "staged")` | not yet |
| A real program from your machine | `shell.RegisterHostCommand("duckdb")` | `register_host_command(shell, "duckdb")` | not yet |

An MCP server becomes one program with a subcommand per tool (`github list-issues --repo acme/api`), so its tool
definitions are read on demand with `--help` rather than sent to the model up front. An API is called by alias,
never by host, and the model never sees the credential (`api get jira /issue/PROJ-123 | jq .fields.status`).
[Your programs](docs/programs.md) has the details.

## Save, move and replay

| | .NET | Python | TypeScript |
|---|---|---|---|
| [Snapshots](docs/snapshots-and-recordings.md#snapshots): undo, fork, save and load the machine | `shell.Snapshot()`, `Restore()`, `Fork()`, `Save()`, `Shell.Load()` | `shell.snapshot()`, `restore()`, `fork()`, `save()`, `Shell.load()` | `ShellSnapshot.capture(shell)`, `save()`, `load()`, `restore()`; no fork |
| [Deterministic runs](docs/snapshots-and-recordings.md#deterministic-runs): a virtual clock and a seed | `StartTime`, `RandomSeed` | not yet | `startTime`, `randomSeed` |
| [Recordings](docs/snapshots-and-recordings.md#recordings): record a session and replay it | `shell.StartRecording()`, `Recording.Load()`, `ReplayAsync()` | `shell.start_recording()`, `Recording.load()`, `replay()` | `startRecording(shell)`, `Recording.load()`, `replay()` |
| Recordings as regression tests, or replayed on real bash in Docker | `Replay.AssertMatches()`, `DockerTarget` (package `Topwater.Replay`) | not yet | not yet |
| [Containers](docs/containers.md): the machine as a Docker image, and a container as a machine | `snapshot.BuildImageAsync(...)`, `ContainerImport.FromContainerAsync(...)` (package `Topwater.Containers`) | `build_image(snapshot, ...)`, `from_container(...)` (`topwater.containers`) | not yet |

Snapshots and recordings are one file, the [`.topwater` file](docs/snapshots-and-recordings.md#the-file-format): a zip
with the machine and, for a recording, every call and checkpoints to roll back to. All three implementations read
and write it, so a machine saved in one opens in another.

The [replay viewer](https://gr-ant.github.io/Topwater/replay.html) opens a recording in the browser. Step through
it turn by turn, see the files after each turn and a diff of what the turn changed, compare any two moments, and
continue from any turn in a terminal. What you type there becomes a branch you can compare and download.

An exported container image holds what the agent wrote, including a `~/.bashrc` that a shell in the image runs.
Treat it as untrusted and run it as carefully as the agent itself.

## What is in the machine

- **A shell language:** pipes, redirects, loops, functions, arrays, heredocs, traps and scripts.
- **About 140 commands,** among them `grep`, `sed`, `awk`, `jq`, `find`, `sort`, `diff`, `tar` and `zip`.
- **`git`** for local repositories: commit, branch, diff, merge with conflict markers, stash. The `.git` folder is
  git's own format, so real git reads it, and a repository carries over through snapshots and container exports.
- **`to-markdown`** for reading Word, Excel, PowerPoint and HTML files.
- **An in-memory filesystem** laid out like Ubuntu 24.04, with permissions, symbolic links and a regular user who
  cannot `sudo`.
- **Realistic behaviour.** Output and error messages follow bash 5.2 and the GNU tools on Ubuntu 24.04, so what the
  model already knows about Linux holds.

What is not there:

| | |
|---|---|
| Python, Node, Perl, compilers | `command not found`, as bash prints it |
| Network | None by default. `curl` and `wget` exist and fail as on an offline machine, until you [allow a list of sites](docs/web.md). There is no `ssh`, and `git clone`, `fetch` and `push` say there is no network |
| Packages | `apt` and `dpkg` exist only to say that nothing can be installed |
| Background jobs and job control | `cmd &` runs to completion |

[The machine](docs/machine.md) lists the shell features, every command and the
[differences from bash](docs/machine.md#not-supported).

Each command is written from POSIX, the man pages and comparison against the real tool's output, never by copying
or porting its source. The command set was audited for provenance in November 2026. All three implementations take
their commands, options and messages from one [command spec](spec/README.md) and run one
[shared corpus](tests/corpus/README.md) of cases recorded from real bash and GNU tools, each with a short list of
the cases it does not pass yet.

## Safety

**Simulated.** Shell commands are interpreted in your process and read and write only the in-memory filesystem.
They cannot open host files, start processes or use the network, unless you allow a list of sites with `AllowWeb`.

**Limited.** Every limit is on by default, with the same defaults in all three. When one is reached, the model gets
a message that says what was cut off and what to do instead:

| Limit | Default |
|---|---|
| Time for one tool call | 120 s |
| All files together | 128 MB |
| One string, and all variables together | 32 MB |
| One loop | 100,000 iterations |
| Tool output returned to the model | 30,000 characters |

Deep recursion and oversized archive extractions are also cut off, and in .NET so is a slow regular expression
(one match is capped at 5 s). [Limits and safety](docs/limits.md) lists every limit, its option and the measured
memory use in .NET.

**Real, and your responsibility:**

- **Your functions, MCP servers and registered APIs do real work.** Expose only what you would let the agent do.
- **Web access reaches the sites you list,** and the agent can send what it has read to any of them that accepts
  the methods you allow. Keep it to `GET` and exact hosts. See [Web access](docs/web.md#what-it-guarantees).
- **Host programs run with your process's permissions.** Topwater does not sandbox them unless you ask:
  `Launcher = HostSandbox.Bubblewrap()` in .NET, or `launcher=bubblewrap()` in Python, runs the program under
  bubblewrap with a private copy of the agent's files and no network. Read
  [Real host programs](docs/programs.md#real-host-programs) before registering one.
- **Python and TypeScript cannot always interrupt a single regular-expression match.** Run the shell in a separate
  process or worker thread if you need a hard stop. See [the
  differences](docs/limits.md#differences-in-python-and-typescript).

## Benchmark

We ran 53 tasks of our own with Claude Sonnet 5.5, GPT-5.6 Terra and Gemini 4 Argon. On 36 everyday tasks (shop
API queries and writes, file and log work, Office documents, git), one `bash` tool on Topwater and real bash in a
Docker container each solved 108 of 108 episodes. Function tools with no code execution solved 89 of 108: they
matched bash on the API, document and git tasks and lost where an answer had to be computed over a file or needed
a shell command. Given a Python tool as well, function tools solved all 45 episodes of the file tasks, at about
twice Topwater's tokens.

On five large-document tasks (a 110-page contract, a 40,000-row workbook, a 15.8 MB log), Topwater and Docker bash
with Python each solved 14 of 15 at about 12,000 tokens per episode. Function tools with Python solved 13 of 15 at
22,000. Function tools without code solved 7 of 15 at 244,000; five of those episodes were stopped by our
400,000-token cap. On 12 harder shell tasks, run twice, Topwater solved 69 of 72 and Docker bash with Python 72 of
72, and Topwater used about twice the tokens. That gap is too small to confirm and too large to rule out. Two of
the three misses were the model's own bug and one was a Topwater bug; models also had to work around git and tar
features Topwater lacks. In 55 safety episodes nothing on the host was read, written or reached.

The samples are small and we wrote the tasks ourselves. [benchmarks/RESULTS.md](benchmarks/RESULTS.md) has the
method, the intervals and the limits.

## Status

Topwater is in preview. APIs may change before 1.0. [CHANGELOG.md](CHANGELOG.md) lists what changed.

| | Where | State |
|---|---|---|
| **.NET** | NuGet: [Topwater](https://www.nuget.org/packages/Topwater), [Topwater.Mcp](https://www.nuget.org/packages/Topwater.Mcp), [Topwater.AI](https://www.nuget.org/packages/Topwater.AI), [Topwater.Containers](https://www.nuget.org/packages/Topwater.Containers), [Topwater.Replay](https://www.nuget.org/packages/Topwater.Replay), [Topwater.Cli](https://www.nuget.org/packages/Topwater.Cli) | `0.1.0-preview.4` |
| **Python** | PyPI: [topwater](https://pypi.org/project/topwater/) | `0.1.0a4` (`pip install --pre topwater`) |
| **TypeScript** | npm: [topwater](https://www.npmjs.com/package/topwater) | `0.1.0-preview.4` |
| **Browser** | [demo](https://gr-ant.github.io/Topwater/), [`typescript/`](typescript/README.md#in-the-browser) | the TypeScript build in a Web Worker |

This README describes `main`. `git`, web access, symbolic links and the `.topwater` recording file are newer than
the published .NET packages; to use them now, reference the projects in `src/` from a clone. The `bc` calculator is
in .NET only, and the tables above show what Python and TypeScript do not have yet.

## Documentation

- [docs/](docs/README.md): [the machine](docs/machine.md), [your programs](docs/programs.md),
  [web access](docs/web.md), [connecting to a model](docs/integrations.md), [command line and MCP server](docs/cli.md),
  [snapshots and recordings](docs/snapshots-and-recordings.md), [containers](docs/containers.md) and
  [limits and safety](docs/limits.md). Samples use the .NET API.
- [Python](python/README.md) and [TypeScript](typescript/README.md): each package's API, its framework adapters and
  where it differs.
- [Command spec](spec/README.md) and [shared test corpus](tests/corpus/README.md): the one list of commands,
  options and messages, and the recorded cases, that all three implementations share.

## Examples

From a clone:

```bash
dotnet run --project examples/McpShell                             # a terminal on the simulated machine
URL=http://127.0.0.1:8765 dotnet run --project examples/WebShell   # the same, as a local web page
```

Without `URL`, WebShell listens on all network interfaces.

| Example | What it shows | Needs |
|---|---|---|
| `examples/McpAgent` | an agent that triages issues through an MCP server | a Gemini API key |
| `examples/OfficeAgent` | an agent that writes a Word document with the real OfficeCLI | a Gemini API key |
| `examples/DuckDbAgent` | an agent that answers questions over CSV files with the real DuckDB | a Gemini API key |
| `examples/AgentFrameworkAgent` | the shell in a Microsoft Agent Framework agent | `OPENAI_API_KEY` |
| `python/examples/` | Python agents and use cases | a Gemini API key |

The installed Python package has a web terminal: `python -m topwater.web --demo`.

## Contributing

Bug reports are most useful as the command that was run, what Topwater printed and what real bash prints.

[AGENTS.md](AGENTS.md) has the rules for changing the repository: commands are specified in `spec/` first, the
clean-room rule, the safety rules and how to run the tests.

## License

MIT. See [LICENSE](LICENSE), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the jq help text it reproduces.
