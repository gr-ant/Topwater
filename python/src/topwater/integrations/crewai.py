"""CrewAI adapter: `bash_tool(shell)` returns a Tool. Needs `pip install "topwater[crewai]"`."""
import asyncio

try:
    from crewai.tools.base_tool import Tool
    from pydantic import BaseModel, Field
except ImportError as e:
    raise ImportError('CrewAI support needs crewai: pip install "topwater[crewai]"') from e

from .. import TOOL_DESCRIPTION, TOOL_NAME, Shell
from . import _runners


class BashInput(BaseModel):
    command: str = Field(description="The bash command to run.")


class BashTool(Tool):
    # CrewAI's Tool only awaits async funcs; the sync one runs on a worker thread instead
    async def _arun(self, *args, **kwargs):
        return await asyncio.to_thread(self.func, *args, **kwargs)


def bash_tool(shell: Shell | None = None, description: str | None = None) -> BashTool:
    run, _ = _runners(shell)
    return BashTool(name=TOOL_NAME, description=description or TOOL_DESCRIPTION, func=run, args_schema=BashInput)
