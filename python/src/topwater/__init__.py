"""Topwater: give your agent one `bash` tool backed by a simulated Linux shell and your own Python tools."""
from .binder import command
from .parser import ParseError
from .shell import CommandContext, RegisteredCommand, Shell, ShellResult
from .vfs import FileStat, ShellError, VirtualFileSystem

TOOL_NAME = "bash"

TOOL_DESCRIPTION = (
    "Run a command in a sandboxed Linux shell (bash). The working directory, environment variables and files persist between calls. "
    "Standard command-line tools are available (ls, cat, grep, sed, awk, jq, find, sort, diff, ...); "
    "Python, network access, git and package installation are not. "
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
    "Shell", "ShellResult", "CommandContext", "RegisteredCommand", "command", "ShellError", "ParseError",
    "VirtualFileSystem", "FileStat", "TOOL_NAME", "TOOL_DESCRIPTION", "TOOL_INPUT_SCHEMA", "execute_tool",
]
