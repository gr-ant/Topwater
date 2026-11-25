"""LlamaIndex adapter: `bash_tool(shell)` returns a FunctionTool. Needs `pip install "topwater[llamaindex]"`."""
import asyncio
import threading

try:
    from llama_index.core.tools import FunctionTool
except ImportError as e:
    raise ImportError('LlamaIndex support needs llama-index-core: pip install "topwater[llamaindex]"') from e

from .. import TOOL_DESCRIPTION, TOOL_NAME, Shell, execute_tool


def bash_tool(shell: Shell | None = None, description: str | None = None) -> FunctionTool:
    shell = shell if shell is not None else Shell()
    # the shell runs one call at a time; one thread lock serializes sync and async callers alike, from any event loop
    lock = threading.Lock()

    def run(command: str) -> str:
        """Run a bash command."""
        with lock:
            return asyncio.run(execute_tool(shell, command))

    async def arun(command: str) -> str:
        """Run a bash command."""
        return await asyncio.to_thread(run, command)

    return FunctionTool.from_defaults(fn=run, async_fn=arun, name=TOOL_NAME, description=description or TOOL_DESCRIPTION)
