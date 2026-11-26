"""LlamaIndex adapter: `bash_tool(shell)` returns a FunctionTool. Needs `pip install "topwater[llamaindex]"`."""
try:
    from llama_index.core.tools import FunctionTool
except ImportError as e:
    raise ImportError('LlamaIndex support needs llama-index-core: pip install "topwater[llamaindex]"') from e

from .. import TOOL_DESCRIPTION, TOOL_NAME, Shell
from . import _runners


def bash_tool(shell: Shell | None = None, description: str | None = None) -> FunctionTool:
    run, arun = _runners(shell)
    return FunctionTool.from_defaults(fn=run, async_fn=arun, name=TOOL_NAME, description=description or TOOL_DESCRIPTION)
