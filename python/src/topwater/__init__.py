"""Topwater: give your agent one `bash` tool backed by a simulated Linux shell and your own Python tools."""
from .api import ApiEndpoint, register_api
from .binder import command
from .host import HostCommand, bubblewrap, register_host_command
from .parser import ParseError
from .recording import RecordedCall, Recording, RecordingError, ShellRecorder
from .shell import CommandContext, RegisteredCommand, Shell, ShellResult
from .snapshot import ShellSnapshot
from .tools import ToolResult, ToolSpec, register_tools
from .vfs import FileStat, ShellError, VirtualFileSystem
from .webaccess import WebExchange, WebPolicy

TOOL_NAME = "bash"

TOOL_DESCRIPTION = (
    "Run a command in a sandboxed Linux shell (bash). The working directory, environment variables and files persist between calls. "
    "Standard command-line tools are available (ls, cat, grep, sed, awk, jq, find, sort, diff, git, ...); git works on local repositories. "
    "Python, network access and package installation are not. "
    "Read Word, Excel, PowerPoint and HTML files with `to-markdown FILE`. "
    "Other programs on this machine are live interfaces to this application's systems, so their actions are real: "
    "run `help` to list them and `<program> --help` for usage."
)

TOOL_INPUT_SCHEMA = {
    "type": "object",
    "properties": {"command": {"type": "string", "description": "The bash command to run."}},
    "required": ["command"],
}


async def execute_tool(shell: Shell, command: str) -> str:
    """Runs the model's command and returns the text to send back as the tool result."""
    return (await shell.run_async(command)).to_tool_output()


__all__ = [
    "Shell", "ShellResult", "ShellSnapshot", "Recording", "RecordedCall", "RecordingError", "ShellRecorder", "CommandContext", "RegisteredCommand", "command", "ShellError", "ParseError",
    "VirtualFileSystem", "FileStat",
    "ApiEndpoint", "register_api", "WebPolicy", "WebExchange", "HostCommand", "bubblewrap", "register_host_command", "ToolSpec", "ToolResult", "register_tools", "TOOL_NAME", "TOOL_DESCRIPTION", "TOOL_INPUT_SCHEMA", "execute_tool",
]
