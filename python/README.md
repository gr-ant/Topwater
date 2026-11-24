# topwater (Python)

Give your agent one `bash` tool backed by a simulated Linux shell: an in-memory filesystem, a clean-room bash parser, the usual utilities, and your own Python functions as programs. No dependencies; Python 3.10+.

```python
from typing import Annotated
import topwater
from topwater import Shell, command

class OrderTools:
    @command("orders", description="List orders")
    async def list_orders(self, status: Annotated[str | None, "Filter by status"] = None,
                          limit: int = 20, include_archived: bool = False):
        return [...]  # lists/dicts/dataclasses are printed as JSON

shell = Shell()                              # user "agent", cwd /home/agent
shell.register(OrderTools())                 # @command methods become programs
shell.fs.write_text("/data/config.json", s)  # mount data for the agent to explore

# Tool definition for your LLM SDK:
#   name: topwater.TOOL_NAME ("bash"), description: topwater.TOOL_DESCRIPTION,
#   input schema: topwater.TOOL_INPUT_SCHEMA ({"command": str})
result = await topwater.execute_tool(shell, command)   # or shell.run(command).to_tool_output()
```

`orders --status open --limit 5 --include-archived` and `orders --help` (generated) both work. See the repository README for the full list of supported shell syntax and built-ins; behaviour matches the C# library.

Tests: `python -m unittest discover -s tests`
