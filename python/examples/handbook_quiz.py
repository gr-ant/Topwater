"""Document-retrieval test: put a large handbook in the virtual filesystem and quiz Gemini on it through the bash tool.

Usage: GEMINI_API_KEY=... python examples/handbook_quiz.py path/to/handbook.txt
The handbook used for the questions below is Northwestern University's 2025-26 Student Handbook (pdftotext -layout).
"""
from __future__ import annotations

import os
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from gemini_harness import SYSTEM, call_gemini  # noqa: E402  (also puts ../src on the path)

import topwater  # noqa: E402
from topwater import Shell  # noqa: E402

DOC_PATH = "/home/agent/docs/student-handbook-2025-26.txt"
MAX_TURNS = 30

# (question, checks that must all appear in the answer, case-insensitive regexes)
QUESTIONS = [
    ("Under the amnesty policy for taking responsible action in an alcohol/drug emergency, what three actions are required?",
     [r"call", r"stay", r"cooperat"]),
    ("What penalties does the Illinois hazing law cited in the handbook carry?",
     [r"three years|3 years", r"25,000|25000"]),
    ("When exactly do the 24-hour quiet hours for finals week begin and end?",
     [r"5(:00)?\s*p\.?m", r"friday before finals", r"(end|last|close) of finals|friday at the end"]),
    ("A student's religion requires burning candles. What should they do?",
     [r"accommodation", r"housing portal"]),
    ("Can I keep a fish tank in my dorm room?",
     [r"\bno\b|not (allowed|permitted)|prohibited", r"aquarium|water"]),
]


def main() -> None:
    key = os.environ.get("GEMINI_API_KEY") or sys.exit("set GEMINI_API_KEY")
    if len(sys.argv) < 2:
        sys.exit("usage: handbook_quiz.py path/to/handbook.txt")
    text = Path(sys.argv[1]).read_text(encoding="utf-8", errors="replace")

    shell = Shell()
    shell.fs.mkdir("/home/agent/docs", parents=True)
    shell.fs.write_text(DOC_PATH, text)

    prompt = ("I'm a new student. The university's 2025-26 student handbook is somewhere in ~/docs. Answer these questions "
              "using only what the handbook says, and quote the relevant line for each:\n"
              + "\n".join(f"{i}. {q}" for i, (q, _) in enumerate(QUESTIONS, 1)))
    tools = [{"functionDeclarations": [{"name": topwater.TOOL_NAME, "description": topwater.TOOL_DESCRIPTION,
                                        "parameters": topwater.TOOL_INPUT_SCHEMA}]}]
    contents = [{"role": "user", "parts": [{"text": prompt}]}]
    answer, commands = "(turn limit reached)", []
    for _ in range(MAX_TURNS):
        resp = call_gemini(key, {"systemInstruction": {"parts": [{"text": SYSTEM}]}, "contents": contents, "tools": tools})
        content = (resp.get("candidates") or [{}])[0].get("content") or {"role": "model", "parts": []}
        contents.append(content)
        calls = [p["functionCall"] for p in content.get("parts", []) if "functionCall" in p]
        if not calls:
            answer = "".join(p.get("text", "") for p in content.get("parts", []) if not p.get("thought")).strip()
            break
        responses = []
        for call in calls:
            cmd = (call.get("args") or {}).get("command", "")
            r = shell.run(cmd)
            out = r.to_tool_output()
            commands.append(cmd)
            truncated = " (TRUNCATED)" if "output truncated" in out else ""
            print(f"  $ {cmd[:160]}\n    -> exit {r.exit_code}, {len(out)} chars{truncated}")
            responses.append({"functionResponse": {"name": call["name"], "response": {"output": out}}})
        contents.append({"role": "user", "parts": responses})

    print(f"\n=== ANSWER ({len(commands)} commands)\n{answer}\n")
    # grade each question against its numbered section of the answer
    sections = re.split(r"\n\s*(?:\*\*)?\s*(?=[1-5][.)])", "\n" + answer)
    sections = [s for s in sections if re.match(r"\s*(\*\*)?\s*[1-5][.)]", s)]
    score = 0
    for i, (q, checks) in enumerate(QUESTIONS, 1):
        part = next((s for s in sections if re.match(rf"\s*(\*\*)?\s*{i}[.)]", s)), answer)
        ok = all(re.search(c, part, re.I) for c in checks)
        score += ok
        print(f"Q{i} {'PASS' if ok else 'FAIL'}: {q}")
    print(f"\nscore: {score}/{len(QUESTIONS)}")


if __name__ == "__main__":
    main()
