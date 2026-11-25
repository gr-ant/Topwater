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
```

Where Python differs from the reference:

- Not ported yet: `bc` and snapshots (`Snapshot`/`Restore`/`Fork`/`Save`/`Load`). There is no Microsoft.Extensions.AI
  equivalent; the plain tool definition works with any Python SDK's tool-calling loop.
- `topwater.mcp` is a small standard-library client for servers spoken to over stdio; `mount_mcp` accepts any object
  with `list_tools()` and `call_tool(name, arguments)` returning MCP's dict shapes, so the official `mcp` SDK adapts in
  a few lines.
- `register_api` takes a `transport` function `(method, url, headers, body, timeout, limit) -> (status, reason,
  headers, body)` in place of an `HttpMessageHandler`. The default uses `urllib` with redirects disabled; a transport
  you pass must not follow redirects either (it can't be checked).
- The standard `re` module cannot time out or interrupt a single match, so one catastrophically backtracking match on
  one line (`grep -E '(a|aa)+$'` on a long run of `a`s) runs to completion; the time limit is checked between lines,
  records and steps.
- Nesting is bounded by Python's recursion limit instead of the .NET stack. Memory use has not been measured; text is
  held as Python strings.
- `shell.run()` wraps `asyncio.run`; inside an event loop use `await shell.run_async()`.
