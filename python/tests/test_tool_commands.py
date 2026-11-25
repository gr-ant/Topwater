"""Schema-described tools and MCP servers exposed as a program with subcommands (ported from ToolCommandTests.cs and
the schema-mapper part of SecondReviewTests.cs)."""
import json
import sys
import textwrap
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402
from topwater.mcp import StdioMcpClient, mount_mcp  # noqa: E402
from topwater.tools import ToolResult, ToolSpec, register_tools  # noqa: E402

SCHEMA = json.loads("""
{"type":"object","required":["repo"],"properties":{
  "repo":{"type":"string","description":"owner/name"},
  "state":{"type":"string","enum":["open","closed"],"default":"open"},
  "limit":{"type":"integer"},
  "labels":{"type":"array","items":{"type":"string"}},
  "include_drafts":{"type":"boolean"},
  "filter":{"type":"object"},
  "ratio":{"type":["number","null"]}}}
""")


def j(x) -> str:
    return json.dumps(x, separators=(",", ":"))


def make():
    calls = []
    tools = [ToolSpec("list_issues", "List issues in a repository.\nSecond line.", SCHEMA),
             ToolSpec("whoami", "Show the current user", {"type": "object"}),
             ToolSpec("fail", None, {})]

    def invoke(tool, args):
        calls.append((tool, j(args)))
        return ToolResult("boom", is_error=True) if tool == "fail" else ToolResult('[{"n":1},{"n":2}]')

    return register_tools(Shell(), "gh", tools, invoke, "GitHub tools"), calls


def test_lists_tools_and_documents_each():
    sh, _ = make()
    help_ = sh.run("gh --help")
    assert "GitHub tools" in help_.stdout
    assert "list-issues  List issues in a repository." in help_.stdout
    assert "Second line" not in help_.stdout
    assert sh.run("gh").stdout == help_.stdout
    assert "gh" in sh.run("help").stdout
    one = sh.run("gh list-issues --help").stdout
    for s in ["Usage: gh list-issues --repo TEXT [options]", "--repo TEXT", "owner/name (required)",
              "(one of: open, closed) (default: open)", "--labels TEXT", "(repeatable)", "--limit INT", "--include-drafts ", "--json JSON"]:
        assert s in one


def test_parses_options_by_type():
    sh, calls = make()
    r = sh.run("""gh list-issues --repo a/b --limit 5 --labels bug --labels "help wanted" --include-drafts --filter '{"x":1}' --ratio=0.5 | jq length""")
    assert r.stdout == "2\n"
    assert calls[0] == ("list_issues", '{"repo":"a/b","limit":5,"labels":["bug","help wanted"],"include_drafts":true,"filter":{"x":1},"ratio":0.5}')
    sh.run("""gh list_issues --repo a/b --no-include-drafts --labels '["x","y"]' --include_drafts false""")
    assert calls[1][1] == '{"repo":"a/b","include_drafts":false,"labels":["x","y"]}'
    sh.run("""echo '{"repo":"from/stdin","limit":2}' | gh list-issues --json - --limit 9""")
    assert calls[2][1] == '{"repo":"from/stdin","limit":9}'
    assert sh.run("gh whoami").exit_code == 0
    assert calls[3][1] == "{}"


@pytest.mark.parametrize("command,message", [
    ("gh nope", "unknown tool 'nope'"),
    ("gh list-issues", "missing required option --repo"),
    ("gh list-issues --repo a --limit many", "option --limit expects a whole number, got 'many'"),
    ("gh list-issues --repo a --colour red", "unknown option --colour; options are --repo"),
    ("gh list-issues --repo", "option --repo needs a value"),
    ("gh list-issues a/b", "unexpected argument 'a/b'"),
    ("gh list-issues --json '[1]'", "--json must be a JSON object"),
    ("gh whoami --x 1", "this tool takes no options"),
])
def test_bad_usage_explains_itself_without_calling_the_tool(command, message):
    sh, calls = make()
    r = sh.run(command)
    assert r.exit_code == 2
    assert message in r.stderr
    assert not calls


def test_tool_errors_go_to_stderr_with_exit_one():
    sh, _ = make()
    r = sh.run("gh fail || echo handled")
    assert r.stderr == "boom\n"
    assert r.stdout == "handled\n"


# ---------- schema shapes that Pydantic/FastMCP servers produce, and option edge cases ----------

PYDANTIC = json.loads("""
{"type":"object","$defs":{"Level":{"type":"string","enum":["1","2"]}},"properties":{
  "tags":{"anyOf":[{"type":"array","items":{"type":"string"}},{"type":"null"}],"description":"Tags"},
  "id":{"anyOf":[{"type":"integer"},{"type":"string"}]},
  "level":{"$ref":"#/$defs/Level"},
  "ratio":{"type":"number"},
  "limit":{"type":"integer"},
  "cache":{"type":"boolean"},
  "title":{"type":"string"},
  "odd":true}}
""")


def call(command):
    sent = []
    sh = register_tools(Shell(), "t", [ToolSpec("run", "Run it", PYDANTIC)], lambda _t, args: sent.append(j(args)) or "ok")
    return sh.run(command), (sent[0] if sent else None)


@pytest.mark.parametrize("command,expected", [
    ("t run --tags 123", '{"tags":["123"]}'),  # Optional[list[str]] keeps its item type
    ("t run --id abc", '{"id":"abc"}'),  # Union[int, str] accepts text
    ("t run --id 7", '{"id":7}'),
    ("t run --level 1", '{"level":"1"}'),  # $ref to a string enum stays a string
    ("t run --no-cache=true", '{"cache":false}'),
    ("t run --title -h", '{"title":"-h"}'),  # -h as a value is not a help request
    ("t run --limit 9 --json '{\"limit\":2,\"title\":\"x\"}'", '{"limit":9,"title":"x"}'),  # options win whatever the order
    ("t run --json '{\"tags\":[\"a\"]}' --tags b", '{"tags":["b"]}'),
])
def test_sends_what_was_meant(command, expected):
    result, sent = call(command)
    assert result.exit_code == 0, result.stderr
    assert sent == expected


def test_rejects_non_finite_numbers_and_still_prints_help():
    bad, sent = call("t run --ratio NaN")
    assert bad.exit_code == 2 and sent is None
    help_, _ = call("t run --help")
    assert help_.exit_code == 0
    assert "--tags TEXT" in help_.stdout
    assert "Tags (repeatable)" in help_.stdout
    assert "(one of: 1, 2)" in help_.stdout
    assert "--odd" in help_.stdout  # a boolean schema used to crash help


# ---------- a real MCP server over stdio (a tiny stdlib server in a subprocess) ----------

SERVER = textwrap.dedent('''
    import json, sys
    TOOLS = [
        {"name": "get_forecast", "description": "Weather forecast for a city",
         "inputSchema": {"type": "object", "required": ["city"], "properties": {
             "city": {"type": "string", "description": "City name"}, "days": {"type": "integer", "description": "Days ahead"}}}},
        {"name": "admin_reset", "description": "Dangerous", "inputSchema": {"type": "object"}},
    ]
    for line in sys.stdin:
        msg = json.loads(line)
        if "id" not in msg:
            continue
        m, p = msg["method"], msg.get("params", {})
        if m == "initialize":
            result = {"protocolVersion": "2025-03-26", "capabilities": {"tools": {}}, "serverInfo": {"name": "weather", "version": "1.0"}}
        elif m == "tools/list":
            result = {"tools": TOOLS}
        elif m == "tools/call":
            a = p["arguments"]
            if a.get("city") == "Atlantis":
                result = {"isError": True, "content": [{"type": "text", "text": "no such city"}]}
            else:
                result = {"content": [{"type": "text", "text": json.dumps({"city": a["city"], "days": a.get("days", 1), "high": 21})}]}
        else:
            print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": -32601, "message": "nope"}}), flush=True)
            continue
        print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": result}), flush=True)
''')


def test_mounts_a_real_mcp_server(tmp_path):
    script = tmp_path / "server.py"
    script.write_text(SERVER)
    with StdioMcpClient([sys.executable, str(script)]) as client:
        sh = Shell()
        tools = mount_mcp(sh, "weather", client, include=lambda name: not name.startswith("admin"))
        assert tools == ["get_forecast"]
        help_ = sh.run("weather --help").stdout
        assert "get-forecast  Weather forecast for a city" in help_
        assert "admin" not in help_
        tool_help = sh.run("weather get-forecast --help").stdout
        for s in ["--city TEXT", "City name (required)", "--days INT"]:
            assert s in tool_help
        r = sh.run("weather get-forecast --city Lisbon --days 3 | jq -r '\"\\(.city) \\(.days) \\(.high)\"'")
        assert r.stdout == "Lisbon 3 21\n"
        bad = sh.run("weather get-forecast --city Atlantis")
        assert bad.exit_code == 1
        assert bad.stderr == "no such city\n"
        assert sh.run("weather admin-reset").exit_code == 2
