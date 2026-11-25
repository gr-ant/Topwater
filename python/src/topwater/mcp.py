"""Mount an MCP server as one program: `weather` lists its tools, `weather get-forecast --city Lisbon` calls one.

    from topwater.mcp import StdioMcpClient, mount_mcp
    client = StdioMcpClient(["npx", "-y", "@modelcontextprotocol/server-filesystem", "/data"])
    mount_mcp(shell, "fs", client)

The model sees no tool definitions up front; it discovers them the way it would discover any command-line program.
The tool list is read once, when mounting. The calls are real: they reach the server.

`StdioMcpClient` is a minimal stdlib client (JSON-RPC 2.0 over a subprocess's stdio: initialize, tools/list,
tools/call; no resources, prompts, sampling or auth). `mount_mcp` accepts any object with `list_tools()` and
`call_tool(name, arguments)` (sync or async) that speak MCP's own dict shapes, so a client from the official `mcp`
SDK can be adapted in a few lines.
"""
from __future__ import annotations

import asyncio
import inspect
import json
import subprocess
import threading
from typing import Callable, Sequence

from .tools import ToolResult, ToolSpec, register_tools

_PROTOCOL = "2025-03-26"


class McpError(Exception):
    """The server answered a request with a JSON-RPC error, or stopped answering."""


class StdioMcpClient:
    """Starts a local MCP server and talks to it over stdin/stdout (newline-delimited JSON-RPC)."""

    def __init__(self, command: Sequence[str], env: dict[str, str] | None = None, timeout: float = 60.0):
        import os
        self.timeout = timeout
        self._lock = threading.Lock()
        self._id = 0
        self._proc = subprocess.Popen(list(command), stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                      env={**os.environ, **(env or {})})
        self.server_info = self._request("initialize", {"protocolVersion": _PROTOCOL, "capabilities": {},
                                                        "clientInfo": {"name": "topwater", "version": "1.0"}}).get("serverInfo") or {}
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def _send(self, message: dict) -> None:
        self._proc.stdin.write((json.dumps(message) + "\n").encode())
        self._proc.stdin.flush()

    def _request(self, method: str, params: dict | None = None) -> dict:
        with self._lock:  # one request at a time keeps the reader trivial
            self._id += 1
            rid = self._id
            try:
                self._send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params or {}})
            except OSError as e:
                raise McpError(f"the server is not reachable: {e}") from None
            timer = threading.Timer(self.timeout, self._proc.kill)  # a server that never answers is killed
            timer.start()
            try:
                while True:
                    line = self._proc.stdout.readline()
                    if not line:
                        raise McpError("the server closed the connection" if not timer.finished.is_set() else "the server did not answer in time")
                    try:
                        msg = json.loads(line)
                    except ValueError:
                        continue
                    if msg.get("method") and "id" in msg:  # a request from the server: answer politely
                        self._send({"jsonrpc": "2.0", "id": msg["id"],
                                    **({"result": {}} if msg["method"] == "ping" else {"error": {"code": -32601, "message": "not supported"}})})
                    elif msg.get("id") == rid:
                        if "error" in msg:
                            raise McpError(str(msg["error"].get("message", msg["error"])))
                        return msg.get("result") or {}
            finally:
                timer.cancel()

    def list_tools(self) -> list[dict]:
        tools, cursor = [], None
        while True:
            page = self._request("tools/list", {"cursor": cursor} if cursor else {})
            tools += page.get("tools", [])
            cursor = page.get("nextCursor")
            if not cursor:
                return tools

    def call_tool(self, name: str, arguments: dict) -> dict:
        return self._request("tools/call", {"name": name, "arguments": arguments})

    def close(self) -> None:
        try:
            self._proc.stdin.close()
            self._proc.wait(2)
        except Exception:
            self._proc.kill()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


def render_result(result: dict) -> str:
    """The text of an MCP CallToolResult: text blocks as they are, other content as a one-line note."""
    parts = []
    for block in result.get("content") or []:
        kind = block.get("type")
        if kind == "text":
            parts.append(block.get("text", ""))
        elif kind == "image":
            parts.append(f"[image, {block.get('mimeType')}]")
        elif kind == "resource_link":
            parts.append(f"[resource {block.get('uri')}{', ' + block['mimeType'] if block.get('mimeType') else ''}]")
        elif kind == "resource":
            r = block.get("resource") or {}
            parts.append(r["text"] if "text" in r else f"[resource {r.get('uri')}{', ' + r['mimeType'] if r.get('mimeType') else ''}]")
        else:
            parts.append(f"[{kind} content]")
    if not parts and result.get("structuredContent") is not None:
        parts.append(json.dumps(result["structuredContent"]))
    return "\n".join(parts)


def mount_mcp(shell, program: str, client, *, description: str | None = None, include: Callable[[str], bool] | None = None):
    """Mounts a connected MCP client as one program. `include(name)` returning False hides a tool from the agent
    (by its MCP name), e.g. to expose only read-only tools. Returns the MCP names of the tools exposed."""
    listed = client.list_tools()
    if inspect.isawaitable(listed):
        raise TypeError("mount_mcp needs a synchronous list_tools(); await it yourself and use register_tools")
    tools = [ToolSpec(t["name"], t.get("description"), t.get("inputSchema") or {})
             for t in listed if include is None or include(t["name"])]
    info = getattr(client, "server_info", None) or {}
    description = description or info.get("description") or info.get("title") or f"{info.get('name') or program} tools"

    async def invoke(tool: str, args: dict) -> ToolResult:
        try:
            if inspect.iscoroutinefunction(client.call_tool):
                result = await client.call_tool(tool, args)
            else:  # a blocking stdio call must not stall the event loop
                result = await asyncio.to_thread(client.call_tool, tool, args)
        except (McpError, OSError, ValueError) as e:
            return ToolResult(str(e), is_error=True)
        return ToolResult(render_result(result), bool(result.get("isError")))

    register_tools(shell, program, tools, invoke, description)
    return [t.name for t in tools]
