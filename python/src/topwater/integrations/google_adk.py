"""Google ADK adapter: `bash_tool(shell)` returns a FunctionTool. Needs `pip install "topwater[google-adk]"`."""
from typing import Annotated

try:
    from google.adk.tools import FunctionTool
    from pydantic import Field
except ImportError as e:
    raise ImportError('Google ADK support needs google-adk: pip install "topwater[google-adk]"') from e

from .. import TOOL_DESCRIPTION, TOOL_NAME, Shell
from . import _runners


def bash_tool(shell: Shell | None = None, description: str | None = None) -> FunctionTool:
    _, arun = _runners(shell)

    # ADK takes the name from the function and the description from its docstring
    async def bash(command: Annotated[str, Field(description="The bash command to run.")]) -> str:
        return await arun(command)

    bash.__name__, bash.__doc__ = TOOL_NAME, description or TOOL_DESCRIPTION
    return FunctionTool(bash)
