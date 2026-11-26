"""CrewAI adapter: `bash_tool(shell)` returns a Tool. Needs `pip install "topwater[crewai]"`."""
try:
    from crewai.tools.base_tool import Tool
    from pydantic import BaseModel, Field
except ImportError as e:
    raise ImportError('CrewAI support needs crewai: pip install "topwater[crewai]"') from e

from .. import TOOL_DESCRIPTION, TOOL_NAME, Shell
from . import _runners


class BashInput(BaseModel):
    command: str = Field(description="The bash command to run.")


def bash_tool(shell: Shell | None = None, description: str | None = None) -> Tool:
    _, arun = _runners(shell)
    # an async func serves both: CrewAI's run() drives it with asyncio.run and arun() awaits it
    return Tool(name=TOOL_NAME, description=description or TOOL_DESCRIPTION, func=arun, args_schema=BashInput)
