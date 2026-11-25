"""LangChain adapter: `bash_tool(shell)` returns a StructuredTool. Needs `pip install "topwater[langchain]"`."""
import asyncio
import threading

try:
    from langchain_core.tools import StructuredTool
    from pydantic import BaseModel, Field
except ImportError as e:
    raise ImportError('LangChain support needs langchain-core: pip install "topwater[langchain]"') from e

from .. import TOOL_DESCRIPTION, TOOL_NAME, Shell, execute_tool


class BashInput(BaseModel):
    command: str = Field(description="The bash command to run.")


def bash_tool(shell: Shell | None = None, description: str | None = None) -> StructuredTool:
    shell = shell if shell is not None else Shell()
    # the shell runs one call at a time; one thread lock serializes sync and async callers alike, from any event loop
    lock = threading.Lock()

    def run(command: str) -> str:
        with lock:
            return asyncio.run(execute_tool(shell, command))

    async def arun(command: str) -> str:
        return await asyncio.to_thread(run, command)

    return StructuredTool.from_function(func=run, coroutine=arun, name=TOOL_NAME,
                                        description=description or TOOL_DESCRIPTION, args_schema=BashInput)
