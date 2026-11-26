"""LangChain adapter: `bash_tool(shell)` returns a StructuredTool. Needs `pip install "topwater[langchain]"`."""
try:
    from langchain_core.tools import StructuredTool
    from pydantic import BaseModel, Field
except ImportError as e:
    raise ImportError('LangChain support needs langchain-core: pip install "topwater[langchain]"') from e

from .. import TOOL_NAME, Shell, tool_description
from . import _runners


class BashInput(BaseModel):
    command: str = Field(description="The bash command to run.")


def bash_tool(shell: Shell | None = None, description: str | None = None) -> StructuredTool:
    run, arun = _runners(shell)
    return StructuredTool.from_function(func=run, coroutine=arun, name=TOOL_NAME,
                                        description=description or tool_description(shell), args_schema=BashInput)
