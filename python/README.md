# topwater (Python)

Your agent already knows how to use a computer. Topwater gives it one: a simulated Linux machine where your
functions are commands, so the agent needs a single `bash` tool. Python 3.10+, standard library only.

```bash
pip install .
```

```python
import topwater
from topwater import Shell, command

class Tools:
    @command("orders", description="List orders")
    def orders(self, status: str | None = None, limit: int = 20):
        return db.orders(status, limit)

shell = Shell().register(Tools())
output = await topwater.execute_tool(shell, "orders --status open | jq length")
# or, outside async code: shell.run(command).to_tool_output()
```

- Tool definition for your model: `topwater.TOOL_NAME`, `TOOL_DESCRIPTION`, `TOOL_INPUT_SCHEMA`
- Limits (all on by default): `Shell(max_call_duration=120, max_file_system_bytes=128 << 20, max_string_bytes=32 << 20, ...)`
- Real programs, HTTP APIs and MCP servers: `topwater.host.register_host_command` (optionally under `bubblewrap()`),
  `topwater.api.register_api`, `topwater.mcp.mount_mcp`, `topwater.tools.register_tools`
- `git` for local repositories (`commands/git.py`, formats in `commands/gitcore.py`); preset an identity with
  `shell.fs.write_text("/home/agent/.gitconfig", ...)` or `GIT_AUTHOR_*`/`GIT_COMMITTER_*` in `shell.env`
- Web terminal: `PYTHONPATH=src python -m topwater.web --demo`, then open http://127.0.0.1:8000
- Examples with a real model: `examples/`
- Tests: `python -m pytest`. The shared cases live in `../tests/corpus` (recorded from real bash and GNU tools) and run via `tests/test_corpus.py`; known differences are in `../tests/corpus/skip/python.json`.

See the [main README](../README.md) and the [reference](../docs/reference.md) for everything else. The reference shows
the C# API; the Python names are below.

## The reference in Python

Option names are the C# ones in snake_case, as keyword arguments to `Shell(...)` (`MaxCallDuration` is
`max_call_duration` in seconds); `ShellResult.trailer` holds the limit note.

```python
from topwater import Shell, command, CommandContext

class Tools:
    @command("count-lines")
    def count_lines(self, ctx: CommandContext) -> int:   # ctx gives stdin, stdout, the filesystem
        ctx.print(len(ctx.stdin.splitlines()))
        return 0                                         # an int is the exit code; lists, dicts and dataclasses print as JSON

from topwater.mcp import StdioMcpClient, mount_mcp     # MCP servers
mount_mcp(shell, "files", StdioMcpClient(["npx", "-y", "@modelcontextprotocol/server-filesystem", "/data"]),
          include=lambda name: not name.startswith("admin"))

from topwater.tools import ToolResult, ToolSpec, register_tools   # tools described by a JSON Schema
register_tools(shell, "crm", specs, lambda tool, arguments: ToolResult(call(tool, arguments)))   # sync or async

from topwater.api import ApiEndpoint, register_api     # authenticated HTTP APIs
register_api(shell, "api", {"jira": ApiEndpoint("https://acme.atlassian.net/rest/api/3",
                                                auth=lambda: f"Bearer {secrets.get('jira')}", paths=["/issue/", "/search"])})

shell.fs.mkdir("/home/agent/data", parents=True)       # files
shell.fs.write_text("/home/agent/data/config.json", text)
shell.fs.add_document("/home/agent/docs/policy.docx", "/real/path/policy.docx")   # or bytes; also_markdown=True
text = topwater.convert.to_markdown(data, "report.xlsx")

from topwater.host import bubblewrap, register_host_command   # real host programs
git = register_host_command(shell, "git", launcher=bubblewrap(network=True))   # git.close() removes its folder

from topwater import ShellSnapshot                    # snapshots: every port loads the others' .topwater files
before = shell.snapshot(); shell.run("rm -rf src"); shell.restore(before)
attempt = shell.fork()                                 # shares nothing mutable; registered handlers are shared
shell.save("machine.topwater")                         # a path or a binary file
later = Shell.load("machine.topwater")                 # built-ins only (also bytes or a file); Shell() kwargs set the limits
shell.restore(ShellSnapshot.load("machine.topwater", shell))   # or load into a shell you configured, with its limits

from topwater import Recording                         # recordings: the calls, in the same .topwater file
with shell.start_recording(snapshot_every=10, metadata={"task": "..."}) as rec:   # every run()/run_async() call
    ...                                                # rec.snapshot_now() keeps a checkpoint here
rec.save("run.topwater")                               # Shell.load() of it gives the machine after the last call
run = Recording.load("run.topwater", limits=shell)     # also old .jsonl recordings and snapshot zips
run.calls[3].stdout, run.calls[3].files                # what call 3 printed, the paths it added, changed or removed
probe = run.shell_at(3)                                # the machine after call 3: nearest checkpoint, then calls re-run
report = run.replay(run.create_shell(register_mine))  # report.matches, report.differences; normalize= to ignore times

from topwater import containers                        # host code only: no shell command reaches it
ctx = containers.build_image(shell.snapshot(), "my-agent")   # docker build; print(ctx.report) for what didn't carry over
containers.to_build_context(shell.snapshot()).write_to("build")   # or write_tar(f); pure, no process
result = containers.from_container("web-1", containers.ImportOptions(paths=("/var/log", "/etc")))
shell = Shell(result.snapshot.user); shell.restore(result.snapshot)
```

Where Python differs from the reference:

- Not ported yet: `bc`. There is no Microsoft.Extensions.AI equivalent; the plain tool definition works with any
  Python SDK's tool-calling loop.
- Snapshot load errors are `topwater.snapshot.SnapshotError` (a `ValueError`; C#'s `InvalidDataException`) and
  `SnapshotVersionError` (C#'s `NotSupportedException`); a bad recording raises `RecordingError`, a `SnapshotError`.
  This port has no virtual clock or random seed, so it writes no `clock` or `random` and ignores them when loading
  (after checking them); a recording's `start_time` and `random_seed` are kept and written back, never used, so a
  replay here compares wall-clock output (pass `normalize=`). A call's `time` is the wall clock when it started.
- `topwater.containers` is synchronous (it shells out to the docker CLI, and an import runs `session.sh` with
  `Shell.run`), so call it outside a running event loop. Names: `to_build_context`, `build_image`, `from_container`,
  `from_image`, `from_tar(rootfs, ContainerConfig(user, working_dir, env), changes, options)`, `docker_available()`;
  `ImportOptions(paths, include_binaries, restore_session, limits)` where `limits` is a dict of `Shell()` arguments.
  Bad container data (a malformed archive, one over a limit, a time or owner no snapshot can hold) raises
  `InvalidContainerData`, docker failures `ContainerError`. The export quotes every name in `setup.sh`, leaves out
  account names starting with `-`, and refuses a base image name with a line break.
- An exported image holds what the agent wrote: its files, scripts and `~/.bashrc` (with the session script it
  sources), modes and owners. A shell in the image runs that content, so treat the image as untrusted and run it with
  the same care, limits and isolation as the agent itself.
- `topwater.mcp` is a small standard-library client for servers spoken to over stdio; `mount_mcp` accepts any object
  with `list_tools()` and `call_tool(name, arguments)` returning MCP's dict shapes, so the official `mcp` SDK adapts in
  a few lines.
- `register_api` takes a `transport` function `(method, url, headers, body, timeout, limit) -> (status, reason,
  headers, body)` in place of an `HttpMessageHandler`. The default uses `urllib` with redirects disabled; a transport
  you pass must not follow redirects either (it can't be checked).
- The standard `re` module has no timeout, but it checks for signals while it backtracks. On the main thread, a grep,
  sed, awk or `[[ =~ ]]` match runs under a one-shot `SIGALRM` for the time the call has left, so one catastrophically
  backtracking match (`grep -E '(a+)+$'` on a long run of `a`s) stops at the call's time limit. The alarm is set only
  around the match, and only when the host has no `SIGALRM` handler or interval timer of its own; both are left as
  they were. In a worker thread, on Windows, or when the host uses `SIGALRM`, such a match runs to completion and the
  time limit is checked between lines, records and steps.
- Nesting is bounded by Python's recursion limit instead of the .NET stack. Memory use has not been measured; text is
  held as Python strings.
- `shell.run()` wraps `asyncio.run`; inside an event loop use `await shell.run_async()`.

## Agent frameworks

```bash
pip install "topwater[langchain]"   # or llamaindex, openai-agents, claude-agent-sdk, pydantic-ai, crewai, smolagents, google-adk
```

Every adapter exposes `bash_tool(shell=None, description=None)`: a tool named `bash` with the standard description
and one `command` argument, as the framework's own tool type. Calls on one shell run one at a time, even from two
tools, and the shell keeps its state between calls. Sync tools work inside a running event loop (a notebook, an
async handler) too, though the loop waits for the call. Each module imports only its own framework;
`import topwater` imports none.

```python
from langchain.agents import create_agent                  # LangChain, and LangGraph underneath
from topwater import Shell
from topwater.integrations.langchain import bash_tool

shell = Shell()
agent = create_agent(llm, tools=[bash_tool(shell)])         # or llm.bind_tools([bash_tool(shell)])
```

```python
from llama_index.core.agent.workflow import FunctionAgent
from topwater.integrations.llamaindex import bash_tool

agent = FunctionAgent(tools=[bash_tool(shell)], llm=llm)
```

```python
from agents import Agent                                   # OpenAI Agents SDK
from topwater.integrations.openai_agents import bash_tool

agent = Agent(name="assistant", tools=[bash_tool(shell)])
```

The Claude Agent SDK takes the tool through an in-process MCP server, so Claude sees it as `mcp__topwater__bash`.
`bash_options` builds the whole safe setup: `tools=[]` turns off Claude Code's own tools, its real `Bash` among them,
and `setting_sources=[]` with `strict_mcp_config=True` keeps the developer's own Claude Code settings, allow rules and
MCP servers out of the agent. Other `ClaudeAgentOptions` arguments pass through:

```python
from claude_agent_sdk import query
from topwater.integrations.claude_agent_sdk import bash_options

async for message in query(prompt="...", options=bash_options(shell, system_prompt="...")):
    print(message)
```

```python
from pydantic_ai import Agent
from topwater.integrations.pydantic_ai import bash_tool

agent = Agent(model, tools=[bash_tool(shell)])
```

```python
from crewai import Agent
from topwater.integrations.crewai import bash_tool

agent = Agent(role="Analyst", goal="...", backstory="...", tools=[bash_tool(shell)])
```

```python
from smolagents import ToolCallingAgent
from topwater.integrations.smolagents import bash_tool

agent = ToolCallingAgent(tools=[bash_tool(shell)], model=model)
```

```python
from google.adk.agents import Agent
from topwater.integrations.google_adk import bash_tool

agent = Agent(name="assistant", model=model, tools=[bash_tool(shell)])
```

Pydantic AI and Google ADK need Python 3.11 or later. CrewAI pins an older `openai` than the OpenAI Agents SDK
wants, so pip reports a conflict when both extras share an environment. AutoGen has no adapter: it is in maintenance
mode, and Microsoft points new projects to Microsoft Agent Framework.

See the [main README](../README.md) and the [reference](../docs/reference.md) for everything else.
