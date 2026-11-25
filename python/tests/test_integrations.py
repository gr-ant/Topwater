import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

import topwater  # noqa: E402


def test_core_import_does_not_load_frameworks():
    code = "import sys, topwater, topwater.integrations; assert not any(m.split('.')[0] in ('langchain_core', 'llama_index') for m in sys.modules)"
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
