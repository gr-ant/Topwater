"""Claude Agent SDK adapter: `bash_tool(shell)` returns an SdkMcpTool for an in-process MCP server.
Needs `pip install "topwater[claude-agent-sdk]"`."""
try:
    from claude_agent_sdk import SdkMcpTool, tool
except ImportError as e:
    raise ImportError('Claude Agent SDK support needs claude-agent-sdk: pip install "topwater[claude-agent-sdk]"') from e

from .. import TOOL_DESCRIPTION, TOOL_INPUT_SCHEMA, TOOL_NAME, Shell
from . import _runners


def bash_tool(shell: Shell | None = None, description: str | None = None) -> SdkMcpTool:
    _, arun = _runners(shell)

    async def handler(args: dict) -> dict:
        return {"content": [{"type": "text", "text": await arun(args["command"])}]}

    return tool(TOOL_NAME, description or TOOL_DESCRIPTION, TOOL_INPUT_SCHEMA)(handler)
