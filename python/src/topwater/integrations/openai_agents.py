"""OpenAI Agents SDK adapter: `bash_tool(shell)` returns a FunctionTool. Needs `pip install "topwater[openai-agents]"`."""
import json

try:
    from agents import FunctionTool
    from agents.tool_context import ToolContext
except ImportError as e:
    raise ImportError('OpenAI Agents support needs openai-agents: pip install "topwater[openai-agents]"') from e

from .. import TOOL_DESCRIPTION, TOOL_INPUT_SCHEMA, TOOL_NAME, Shell
from . import _runners


def bash_tool(shell: Shell | None = None, description: str | None = None) -> FunctionTool:
    _, arun = _runners(shell)

    async def invoke(ctx: ToolContext, args: str) -> str:
        # bad arguments go back to the model as tool text, in the core tool's format; raising here would end the run
        try:
            command = json.loads(args)["command"]
        except (ValueError, TypeError, KeyError):
            command = None
        if not isinstance(command, str):
            return "bash: invalid arguments: expected a JSON object with a string `command`\n[exit code 2]"
        return await arun(command)

    # strict mode (the SDK's default) needs additionalProperties: false
    return FunctionTool(name=TOOL_NAME, description=description or TOOL_DESCRIPTION,
                        params_json_schema={**TOOL_INPUT_SCHEMA, "additionalProperties": False}, on_invoke_tool=invoke)
