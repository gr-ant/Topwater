"""Pydantic AI adapter: `bash_tool(shell)` returns a Tool. Needs `pip install "topwater[pydantic-ai]"`."""
try:
    from pydantic_ai import Tool
except ImportError as e:
    raise ImportError('Pydantic AI support needs pydantic-ai-slim: pip install "topwater[pydantic-ai]"') from e

from .. import TOOL_DESCRIPTION, TOOL_NAME, Shell
from . import _runners


def bash_tool(shell: Shell | None = None, description: str | None = None) -> Tool:
    _, arun = _runners(shell)

    async def bash(command: str) -> str:
        """Run a bash command.

        Args:
            command: The bash command to run.
        """
        return await arun(command)

    # sequential: the model's parallel calls run one after another, in order, on the one shell
    return Tool(bash, takes_ctx=False, name=TOOL_NAME, description=description or TOOL_DESCRIPTION, sequential=True)
