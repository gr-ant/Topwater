# Topwater for Python

One `bash` tool for your LLM agent, backed by a simulated Linux machine that runs inside your Python process. Your
functions are commands on it. Python 3.10 or later, standard library only.

This page covers the Python API. The [main README](https://github.com/gr-ant/Topwater/blob/main/README.md) explains what Topwater is, and
[docs/](https://github.com/gr-ant/Topwater/blob/main/docs/README.md) is the reference; its samples are C#, and the names below are their Python equivalents.

- [Install](#install)
- [Quickstart](#quickstart)
- [Your programs](#your-programs)
- [Files and documents](#files-and-documents)
- [Snapshots and recordings](#snapshots-and-recordings)
- [Containers](#containers)
- [Agent frameworks](#agent-frameworks)
- [Where Python differs](#where-python-differs)
- [Development](#development)

## Install

```bash
pip install --pre topwater
```

Previews are pre-releases on PyPI, hence `--pre`. From a clone: `pip install ./python`.

## Quickstart

`db` stands for your own data; the [main README](https://github.com/gr-ant/Topwater/blob/main/README.md#python) has a version that runs as it is.

```python
import topwater
from topwater import Shell, command

class Tools:
    @command("orders", description="List orders")
    def orders(self, status: str | None = None, limit: int = 20):
        return db.orders(status, limit)

shell = Shell().register(Tools())
output = shell.run("orders --status open | jq length").to_tool_output()
# inside async code: output = await topwater.execute_tool(shell, command)
```

- **Tool definition for your model:** `topwater.TOOL_NAME`, `TOOL_DESCRIPTION`, `TOOL_INPUT_SCHEMA`. On a shell with
  [web access](https://github.com/gr-ant/Topwater/blob/main/docs/web.md), use `topwater.tool_description(shell)`, which names the sites; the adapters do.
- **Limits**, all on by default:
  `Shell(max_call_duration=120, max_file_system_bytes=128 << 20, max_string_bytes=32 << 20, ...)`. Option names are
  the C# ones in snake_case, and `max_call_duration` is in seconds. `ShellResult.trailer` holds the note a limit adds.
- **`shell.run()`** wraps `asyncio.run`. Inside an event loop use `await shell.run_async()`.
- **Web terminal:** `python -m topwater.web --demo`, then open http://127.0.0.1:8000.

## Your programs

```python
from topwater import Shell, command, CommandContext

class Tools:
    @command("count-lines")
    def count_lines(self, ctx: CommandContext) -> int:   # ctx gives stdin, stdout, the filesystem
        ctx.print(len(ctx.stdin.splitlines()))
        return 0                                         # an int is the exit code; lists, dicts and dataclasses print as JSON
```

```python
from topwater.tools import ToolResult, ToolSpec, register_tools   # tools described by a JSON Schema
register_tools(shell, "crm", specs, lambda tool, arguments: ToolResult(call(tool, arguments)))   # sync or async

from topwater.mcp import StdioMcpClient, mount_mcp     # MCP servers
mount_mcp(shell, "files", StdioMcpClient(["npx", "-y", "@modelcontextprotocol/server-filesystem", "/data"]),
          include=lambda name: not name.startswith("admin"))

from topwater.api import ApiEndpoint, register_api     # authenticated HTTP APIs
register_api(shell, "api", {"jira": ApiEndpoint("https://acme.atlassian.net/rest/api/3",
                                                auth=lambda: f"Bearer {secrets.get('jira')}", paths=["/issue/", "/search"])})

from topwater import WebPolicy                         # web access: curl and wget to the sites you list
shell.allow_web(WebPolicy(domains=["api.github.com", "*.wikipedia.org"]))

from topwater.host import bubblewrap, register_host_command   # real host programs
duckdb = register_host_command(shell, "duckdb", launcher=bubblewrap())   # duckdb.close() removes its folder and the command
```

All of these do real work when the agent calls them. [Your programs](https://github.com/gr-ant/Topwater/blob/main/docs/programs.md) says what each guarantees.

## Files and documents

```python
shell.fs.mkdir("/home/agent/data", parents=True)
shell.fs.write_text("/home/agent/data/config.json", text)
shell.fs.add_document("/home/agent/docs/policy.docx", "/real/path/policy.docx")   # or bytes; also_markdown=True
text = topwater.convert.to_markdown(data, "report.xlsx")
```

To give the built-in `git` an identity, write `/home/agent/.gitconfig` with `shell.fs.write_text`, or set
`GIT_AUTHOR_*` and `GIT_COMMITTER_*` in `shell.env` ([git](https://github.com/gr-ant/Topwater/blob/main/docs/machine.md#git)).

## Snapshots and recordings

Machines and recordings are [`.topwater` files](https://github.com/gr-ant/Topwater/blob/main/docs/snapshots-and-recordings.md#the-file-format), the same in
every implementation.

```python
from topwater import Shell, ShellSnapshot, Recording

before = shell.snapshot(); shell.run("rm -rf src"); shell.restore(before)
attempt = shell.fork()                                 # shares nothing mutable; registered handlers are shared
shell.save("machine.topwater")                         # a path or a binary file
later = Shell.load("machine.topwater")                 # built-ins only (also bytes or a file); Shell() kwargs set the limits
shell.restore(ShellSnapshot.load("machine.topwater", shell))   # or load into a shell you configured, with its limits

with shell.start_recording(snapshot_every=10, metadata={"task": "..."}) as rec:   # every run()/run_async() call
    ...                                                # rec.snapshot_now() keeps a checkpoint here
rec.save("run.topwater")                               # Shell.load() of it gives the machine after the last call
run = Recording.load("run.topwater", limits=shell)     # also old .jsonl recordings and snapshot zips
run.calls[3].stdout, run.calls[3].files                # what call 3 printed, the paths it added, changed or removed
probe = run.shell_at(3)                                # the machine after call 3: nearest checkpoint, then calls re-run
report = run.replay(run.create_shell(register_mine))   # report.matches, report.differences; normalize= to ignore times
```

## Containers

```python
from topwater import containers                        # host code only: no shell command reaches it

ctx = containers.build_image(shell.snapshot(), "my-agent")   # docker build; print(ctx.report) for what didn't carry over
containers.to_build_context(shell.snapshot()).write_to("build")   # or write_tar(f); pure, no process
result = containers.from_container("web-1", containers.ImportOptions(paths=("/var/log", "/etc")))
shell = Shell(result.snapshot.user); shell.restore(result.snapshot)
```

An exported image holds what the agent wrote: its files, scripts and `~/.bashrc`, with the session script it
sources. A shell in the image runs that content, so treat the image as untrusted and run it with the same care,
limits and isolation as the agent itself. See [Containers](https://github.com/gr-ant/Topwater/blob/main/docs/containers.md).

## Agent frameworks

Each adapter is an extra. Until the package is on PyPI, name the repository:

```bash
pip install --pre "topwater[langchain]"
```

The extras are `langchain`, `llamaindex`, `openai-agents`, `claude-agent-sdk`, `pydantic-ai`, `crewai`,
`smolagents` and `google-adk`.

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
Use `bash_options`, not `bash_tool` alone: it builds the whole safe setup. `tools=[]` turns off Claude Code's own
tools, its real `Bash` among them, and `setting_sources=[]` with `strict_mcp_config=True` keeps the developer's own
Claude Code settings, allow rules and MCP servers out of the agent. Other `ClaudeAgentOptions` arguments pass through:

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

## Where Python differs

- **Not ported yet:** `bc`, the virtual clock and random seed ([deterministic
  runs](https://github.com/gr-ant/Topwater/blob/main/docs/snapshots-and-recordings.md#deterministic-runs)), and replaying a recording as a test or on real bash
  in Docker (`Topwater.Replay`).
- **Errors.** Snapshot load errors are `topwater.snapshot.SnapshotError` (a `ValueError`; C#'s `InvalidDataException`)
  and `SnapshotVersionError` (C#'s `NotSupportedException`); a bad recording raises `RecordingError`, a
  `SnapshotError`.
- **Clock.** Without a virtual clock or random seed, Python writes no `clock` or `random` and ignores them when
  loading (after checking them). A recording's `start_time` and `random_seed` are kept and written back, never used,
  so a replay here compares wall-clock output (pass `normalize=`). A call's `time` is the wall clock when it started.
- **Containers.** `topwater.containers` is synchronous (it shells out to the docker CLI, and an import runs
  `session.sh` with `Shell.run`), so call it outside a running event loop. Names: `to_build_context`, `build_image`,
  `from_container`, `from_image`, `from_tar(rootfs, ContainerConfig(user, working_dir, env), changes,
  options)`, `docker_available()`; `ImportOptions(paths, include_binaries, restore_session,
  limits)` where `limits` is a dict of `Shell()` arguments. Bad container data (a malformed archive, one over a limit,
  a time or owner no snapshot can hold) raises `InvalidContainerData`, docker failures `ContainerError`. The export
  quotes every name in `setup.sh`, leaves out account names starting with `-`, and refuses a base image name with a
  line break.
- **MCP.** `topwater.mcp` is a small standard-library client for servers spoken to over stdio; `mount_mcp` accepts any
  object with `list_tools()` and `call_tool(name, arguments)` returning MCP's dict shapes, so the official `mcp` SDK
  adapts in a few lines.
- **APIs.** `register_api` takes a `transport` function `(method, url, headers, body, timeout, limit) -> (status,
  reason, headers, body)` in place of an `HttpMessageHandler`. The default uses `urllib` with redirects disabled; a
  transport you pass must not follow redirects either (it can't be checked).
- **Regular expressions.** The standard `re` module has no timeout, but it checks for signals while it backtracks. On
  the main thread, a grep, sed, awk or `[[ =~ ]]` match runs under a one-shot `SIGALRM` for the time the call has
  left, so one catastrophically backtracking match (`grep -E '(a+)+$'` on a long run of `a`s) stops at the call's time
  limit. The alarm is set only around the match, and only when the host has no `SIGALRM` handler or interval timer of
  its own; both are left as they were. In a worker thread, on Windows, or when the host uses `SIGALRM`, such a match
  runs to completion and the time limit is checked between lines, records and steps.
- **Nesting and memory.** Nesting is bounded by Python's recursion limit instead of the .NET stack. Memory use has not
  been measured; text is held as Python strings.

## Development

```bash
cd python && python -m pytest -q
```

The shared cases live in [`../tests/corpus`](https://github.com/gr-ant/Topwater/blob/main/tests/corpus/README.md), recorded from real bash and GNU tools, and
run through `tests/test_corpus.py`. Known differences are in `../tests/corpus/skip/python.json`. Examples with a
real model are in `examples/`.
