import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

import topwater  # noqa: E402


def test_core_import_does_not_load_frameworks():
    # google.adk, not google: some google packages install a .pth file that imports the google namespace at startup
    frameworks = "('langchain_core', 'llama_index', 'agents', 'claude_agent_sdk', 'pydantic_ai', 'crewai', 'smolagents', 'google.adk')"
    code = f"import sys, topwater, topwater.integrations; assert not any(m.startswith(f + '.') or m == f for m in sys.modules for f in {frameworks})"
    subprocess.run([sys.executable, "-c", code], check=True, env={"PYTHONPATH": str(Path(topwater.__file__).parents[1])})


def test_langchain():
    pytest.importorskip("langchain_core")
    from topwater.integrations.langchain import bash_tool

    tool = bash_tool(topwater.Shell())
    assert tool.name == topwater.TOOL_NAME and tool.description == topwater.TOOL_DESCRIPTION
    schema = tool.args_schema.model_json_schema()
    assert schema["required"] == ["command"] and schema["properties"]["command"]["type"] == "string"
    tool.invoke({"command": "cd /tmp && echo hi > a.txt"})
    assert tool.invoke({"command": "cat a.txt"}).startswith("hi")
    assert "[exit code 1]" in tool.invoke({"command": "cat /nope"})


def test_langchain_async():
    pytest.importorskip("langchain_core")
    import asyncio

    from topwater.integrations.langchain import bash_tool

    tool = bash_tool()

    async def go():
        await tool.ainvoke({"command": "x=5; echo $x > /tmp/x"})
        return await tool.ainvoke({"command": "cat /tmp/x"})

    assert asyncio.run(go()).startswith("5")
    assert asyncio.run(go()).startswith("5")  # a second event loop works too


def test_llamaindex():
    pytest.importorskip("llama_index.core")
    from topwater.integrations.llamaindex import bash_tool

    tool = bash_tool(topwater.Shell())
    assert tool.metadata.name == topwater.TOOL_NAME and tool.metadata.description == topwater.TOOL_DESCRIPTION
    schema = tool.metadata.get_parameters_dict()
    assert schema["required"] == ["command"] and schema["properties"]["command"]["type"] == "string"
    tool.call(command="cd /tmp && echo hi > a.txt")
    assert tool.call(command="cat a.txt").content.startswith("hi")
    assert "[exit code 1]" in tool.call(command="cat /nope").content


def test_langgraph_agent():
    pytest.importorskip("langgraph")
    pytest.importorskip("langchain.agents")
    from langchain.agents import create_agent
    from langchain_core.language_models.fake_chat_models import GenericFakeChatModel
    from langchain_core.messages import AIMessage

    from topwater.integrations.langchain import bash_tool

    class FakeModel(GenericFakeChatModel):
        def bind_tools(self, tools, **kwargs):
            return self

    model = FakeModel(messages=iter([
        AIMessage("", tool_calls=[{"name": "bash", "args": {"command": "echo hi > /tmp/a; cat /tmp/a"}, "id": "1"}]),
        AIMessage("done"),
    ]))
    result = create_agent(model, tools=[bash_tool()]).invoke({"messages": [("user", "go")]})
    tool_message = result["messages"][2]
    assert tool_message.name == "bash" and tool_message.content.startswith("hi")
    assert result["messages"][-1].content == "done"


def test_openai_agents():
    pytest.importorskip("agents")
    import asyncio
    import json

    from agents.tool_context import ToolContext

    from topwater.integrations.openai_agents import bash_tool

    tool = bash_tool(topwater.Shell())
    assert tool.name == topwater.TOOL_NAME and tool.description == topwater.TOOL_DESCRIPTION
    assert tool.strict_json_schema and tool.params_json_schema["required"] == ["command"]

    def call(command):
        args = json.dumps({"command": command})
        ctx = ToolContext(context=None, tool_name="bash", tool_call_id="1", tool_arguments=args)
        return asyncio.run(tool.on_invoke_tool(ctx, args))

    call("cd /tmp && echo hi > a.txt")
    assert call("cat a.txt").startswith("hi")
    assert "[exit code 1]" in call("cat /nope")


def test_claude_agent_sdk():
    pytest.importorskip("claude_agent_sdk")
    import asyncio

    from claude_agent_sdk import create_sdk_mcp_server
    from mcp.types import CallToolRequest, CallToolRequestParams, ListToolsRequest

    from topwater.integrations.claude_agent_sdk import bash_tool

    tool = bash_tool(topwater.Shell())
    assert tool.name == topwater.TOOL_NAME and tool.description == topwater.TOOL_DESCRIPTION
    server = create_sdk_mcp_server("topwater", tools=[tool])["instance"]

    async def go():
        listed = await server.request_handlers[ListToolsRequest](ListToolsRequest(method="tools/list"))
        [spec] = listed.root.tools
        assert spec.name == "bash" and spec.inputSchema["required"] == ["command"]

        async def call(command):
            request = CallToolRequest(method="tools/call", params=CallToolRequestParams(name="bash", arguments={"command": command}))
            return (await server.request_handlers[CallToolRequest](request)).root.content[0].text

        await call("cd /tmp && echo hi > a.txt")
        assert (await call("cat a.txt")).startswith("hi")
        assert "[exit code 1]" in await call("cat /nope")

    asyncio.run(go())


def test_pydantic_ai():
    pytest.importorskip("pydantic_ai")
    from pydantic_ai import Agent
    from pydantic_ai.messages import ModelResponse, TextPart, ToolCallPart, ToolReturnPart
    from pydantic_ai.models.function import FunctionModel

    from topwater.integrations.pydantic_ai import bash_tool

    tool = bash_tool(topwater.Shell())
    assert tool.name == topwater.TOOL_NAME and tool.description == topwater.TOOL_DESCRIPTION
    schema = tool.tool_def.parameters_json_schema
    assert schema["required"] == ["command"] and schema["properties"]["command"]["type"] == "string"

    def model(messages, info):
        returns = [p for m in messages for p in m.parts if isinstance(p, ToolReturnPart)]
        if len(returns) < 2:
            command = ["cd /tmp && echo hi > a.txt", "cat a.txt"][len(returns)]
            return ModelResponse(parts=[ToolCallPart("bash", {"command": command})])
        return ModelResponse(parts=[TextPart(returns[-1].content)])

    assert Agent(FunctionModel(model), tools=[tool]).run_sync("go").output.startswith("hi")


def test_crewai():
    pytest.importorskip("crewai")
    import asyncio

    from topwater.integrations.crewai import bash_tool

    tool = bash_tool(topwater.Shell())
    assert tool.name == topwater.TOOL_NAME and tool.description == topwater.TOOL_DESCRIPTION
    assert tool.args_schema.model_json_schema()["required"] == ["command"]
    tool.run(command="cd /tmp && echo hi > a.txt")
    assert tool.run(command="cat a.txt").startswith("hi")
    assert "[exit code 1]" in asyncio.run(tool.arun(command="cat /nope"))


def test_smolagents():
    pytest.importorskip("smolagents")
    from topwater.integrations.smolagents import bash_tool

    tool = bash_tool(topwater.Shell())
    assert tool.name == topwater.TOOL_NAME and tool.description == topwater.TOOL_DESCRIPTION
    assert tool.inputs == {"command": topwater.TOOL_INPUT_SCHEMA["properties"]["command"]} and tool.output_type == "string"
    tool(command="cd /tmp && echo hi > a.txt")
    assert tool(command="cat a.txt").startswith("hi")
    assert "[exit code 1]" in tool(command="cat /nope")
    assert bash_tool(description="custom").description == "custom" and tool.description == topwater.TOOL_DESCRIPTION


def test_google_adk():
    pytest.importorskip("google.adk")
    import asyncio

    from topwater.integrations.google_adk import bash_tool

    tool = bash_tool(topwater.Shell())
    declaration = tool._get_declaration()
    assert tool.name == declaration.name == topwater.TOOL_NAME and declaration.description == topwater.TOOL_DESCRIPTION
    schema = declaration.parameters_json_schema
    assert schema["required"] == ["command"] and schema["properties"]["command"]["type"] == "string"

    def call(command):
        return asyncio.run(tool.run_async(args={"command": command}, tool_context=None))

    call("cd /tmp && echo hi > a.txt")
    assert call("cat a.txt").startswith("hi")
    assert "[exit code 1]" in call("cat /nope")
