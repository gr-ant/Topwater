"""smolagents adapter: `bash_tool(shell)` returns a Tool. Needs `pip install "topwater[smolagents]"`."""
try:
    from smolagents import Tool
except ImportError as e:
    raise ImportError('smolagents support needs smolagents: pip install "topwater[smolagents]"') from e

from .. import TOOL_DESCRIPTION, TOOL_INPUT_SCHEMA, TOOL_NAME, Shell
from . import _runners


class BashTool(Tool):
    name = TOOL_NAME
    description = TOOL_DESCRIPTION
    inputs = {"command": TOOL_INPUT_SCHEMA["properties"]["command"]}
    output_type = "string"

    def __init__(self, shell: Shell | None = None, description: str | None = None):
        super().__init__()
        self._run, _ = _runners(shell)
        if description:
            self.description = description

    def forward(self, command: str) -> str:
        return self._run(command)


def bash_tool(shell: Shell | None = None, description: str | None = None) -> BashTool:
    return BashTool(shell, description)
