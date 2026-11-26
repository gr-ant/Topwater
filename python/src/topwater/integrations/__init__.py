"""Optional framework adapters, one module per framework: `topwater.integrations.langchain`, `.llamaindex`,
`.openai_agents`, `.claude_agent_sdk`, `.pydantic_ai`, `.crewai`, `.smolagents` and `.google_adk`."""
import asyncio
import threading
import weakref
from concurrent.futures import ThreadPoolExecutor

from .. import Shell, execute_tool

# one lock per shell, so two adapters (or two tools) on one shell never interleave their calls
_locks: "weakref.WeakKeyDictionary[Shell, threading.Lock]" = weakref.WeakKeyDictionary()
_locks_guard = threading.Lock()


def _runners(shell: Shell | None):
    """The sync and async functions every adapter wraps: both run the tool call on one shell, one call at a time."""
    shell = shell if shell is not None else Shell()
    with _locks_guard:
        lock = _locks.setdefault(shell, threading.Lock())

    def call(command: str) -> str:
        with lock:
            return asyncio.run(execute_tool(shell, command))

    def run(command: str) -> str:
        try:
            asyncio.get_running_loop()
        except RuntimeError:
            return call(command)
        # called from inside a running event loop (a notebook, an async handler): asyncio.run can't nest, so the
        # call gets a worker thread and its own loop, and this thread waits for it
        with ThreadPoolExecutor(1) as pool:
            return pool.submit(call, command).result()

    async def arun(command: str) -> str:
        return await asyncio.to_thread(call, command)

    return run, arun
