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
- Web terminal: `PYTHONPATH=src python -m topwater.web --demo`, then open http://127.0.0.1:8000
- Examples with a real model: `examples/`
- Tests: `python -m unittest discover -s tests`

See the [main README](../README.md) and the [reference](../docs/reference.md) for everything else.
