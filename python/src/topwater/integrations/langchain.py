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
    shell = shell or Shell()
    # shortcut: the shell keeps per-call state, so calls are serialized; sync and async callers use separate locks
    sync_lock, async_lock = threading.Lock(), asyncio.Lock()

    def run(command: str) -> str:
        with sync_lock:
            return asyncio.run(execute_tool(shell, command))

    async def arun(command: str) -> str:
        async with async_lock:
            return await execute_tool(shell, command)

    return StructuredTool.from_function(func=run, coroutine=arun, name=TOOL_NAME,
                                        description=description or TOOL_DESCRIPTION, args_schema=BashInput)
