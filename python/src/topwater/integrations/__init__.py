"""Optional framework adapters, one module per framework: `topwater.integrations.langchain`, `.llamaindex`,
`.openai_agents`, `.claude_agent_sdk`, `.pydantic_ai`, `.crewai`, `.smolagents` and `.google_adk`."""
import asyncio
import threading

from .. import Shell, execute_tool


def _runners(shell: Shell | None):
    """The sync and async functions every adapter wraps: both run the tool call on one shell, one call at a time."""
    shell = shell if shell is not None else Shell()
    # one thread lock serializes sync and async callers alike, from any event loop
    lock = threading.Lock()

    def run(command: str) -> str:
        with lock:
            return asyncio.run(execute_tool(shell, command))

    async def arun(command: str) -> str:
        return await asyncio.to_thread(run, command)

    return run, arun
