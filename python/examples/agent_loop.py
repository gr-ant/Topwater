"""A minimal agent loop for the use-case examples: Gemini + Topwater's single `bash` tool.

    result = run_agent(shell, "Do the thing", key=os.environ["GEMINI_API_KEY"])
    result.answer, result.commands

Swap `call_gemini` for your own provider; the loop is the same everywhere: send the tool definition, run each
`bash` call through the shell, send the output back.
"""
from __future__ import annotations

import json
import os
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from gemini_harness import call_gemini  # noqa: E402  (also puts ../src on the path)

import topwater  # noqa: E402

SYSTEM = ("You are an autonomous agent working through a bash tool. Use the tool to inspect and act; do not ask the "
          "user questions. When finished, reply with a short final answer.")


@dataclass
class AgentResult:
    answer: str
    commands: list[dict] = field(default_factory=list)  # {"command", "exit", "stderr"}

    @property
    def failures(self) -> list[dict]:
        return [c for c in self.commands if c["exit"] != 0]


def run_agent(shell: topwater.Shell, prompt: str, key: str | None = None, max_turns: int = 30, verbose: bool = True,
              system: str = SYSTEM) -> AgentResult:
    key = key or os.environ.get("GEMINI_API_KEY") or sys.exit("set GEMINI_API_KEY")
    tools = [{"functionDeclarations": [{"name": topwater.TOOL_NAME, "description": topwater.TOOL_DESCRIPTION,
                                        "parameters": topwater.TOOL_INPUT_SCHEMA}]}]
    contents = [{"role": "user", "parts": [{"text": prompt}]}]
    result = AgentResult("(turn limit reached)")
    for _ in range(max_turns):
        resp = call_gemini(key, {"systemInstruction": {"parts": [{"text": system}]}, "contents": contents, "tools": tools})
        content = (resp.get("candidates") or [{}])[0].get("content") or {"role": "model", "parts": []}
        contents.append(content)  # echo back verbatim (keeps thought signatures)
        calls = [p["functionCall"] for p in content.get("parts", []) if "functionCall" in p]
        if not calls:
            result.answer = "".join(p.get("text", "") for p in content.get("parts", []) if not p.get("thought")).strip() or "(empty answer)"
            break
        responses = []
        for call in calls:
            cmd = (call.get("args") or {}).get("command", "")
            r = shell.run(cmd)
            result.commands.append({"command": cmd, "exit": r.exit_code, "stderr": r.stderr[:300], "stdout": r.stdout[:1500]})
            if verbose:
                first = cmd.strip().split("\n")[0][:150]
                print(f"  $ {first}{' …' if chr(10) in cmd.strip() else ''}\n    -> exit {r.exit_code}"
                      + (f" | {r.stderr.strip()[:160]}" if r.stderr.strip() else ""), flush=True)
            responses.append({"functionResponse": {"name": call["name"], "response": {"output": r.to_tool_output()}}})
        contents.append({"role": "user", "parts": responses})
    if os.environ.get("AGENT_LOG"):  # full transcript for debugging
        Path(os.environ["AGENT_LOG"]).write_text(json.dumps({"answer": result.answer, "commands": result.commands}, indent=2))
    return result


def report(name: str, checks: list[tuple[str, bool]], result: AgentResult) -> bool:
    """Prints a pass/fail summary for a use case and returns True if every check passed."""
    print(f"\n=== {name}: {len(result.commands)} commands, {len(result.failures)} non-zero exits")
    print(result.answer[:900])
    print()
    for label, ok in checks:
        print(f"  {'PASS' if ok else 'FAIL'}  {label}")
    ok = all(c for _, c in checks)
    print(f"\n{name}: {'PASSED' if ok else 'FAILED'} ({sum(c for _, c in checks)}/{len(checks)})")
    return ok
