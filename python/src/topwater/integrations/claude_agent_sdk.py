"""Claude Agent SDK adapter: `bash_options(shell)` returns ClaudeAgentOptions whose only tool is the simulated `bash`;
`bash_tool(shell)` returns the SdkMcpTool for an MCP server of your own. Needs `pip install "topwater[claude-agent-sdk]"`."""
try:
    from claude_agent_sdk import ClaudeAgentOptions, SdkMcpTool, create_sdk_mcp_server, tool
except ImportError as e:
    raise ImportError('Claude Agent SDK support needs claude-agent-sdk: pip install "topwater[claude-agent-sdk]"') from e

from .. import TOOL_INPUT_SCHEMA, TOOL_NAME, Shell, tool_description
from . import _runners


def bash_tool(shell: Shell | None = None, description: str | None = None) -> SdkMcpTool:
    _, arun = _runners(shell)

    async def handler(args: dict) -> dict:
        return {"content": [{"type": "text", "text": await arun(args["command"])}]}

    return tool(TOOL_NAME, description or tool_description(shell), TOOL_INPUT_SCHEMA)(handler)


def bash_options(shell: Shell | None = None, description: str | None = None, **options) -> ClaudeAgentOptions:
    """Options for an agent whose only tool is `bash` on `shell` (Claude sees it as `mcp__topwater__bash`).

    `tools=[]` turns off Claude Code's built-in tools, the host's real Bash among them. `setting_sources=[]` and
    `strict_mcp_config=True` stop the SDK loading the developer's own Claude Code settings, allow rules and MCP
    servers. Other keyword arguments go to ClaudeAgentOptions; passing one of these again is a TypeError.
    """
    server = create_sdk_mcp_server("topwater", tools=[bash_tool(shell, description)])
    return ClaudeAgentOptions(mcp_servers={"topwater": server}, tools=[], allowed_tools=[f"mcp__topwater__{TOOL_NAME}"],
                              setting_sources=[], strict_mcp_config=True, **options)
