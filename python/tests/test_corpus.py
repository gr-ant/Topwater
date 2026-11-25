"""Runs the shared cases in ../tests/corpus/*.jsonl (see the README there); known differences: skip/python.json."""
import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402

CORPUS = Path(__file__).resolve().parents[2] / "tests" / "corpus"
SKIPS = json.loads((CORPUS / "skip" / "python.json").read_text(encoding="utf-8"))
CASES = {}  # id -> case
for f in sorted(CORPUS.glob("*.jsonl")):
    for line in f.read_text(encoding="utf-8").splitlines():
        if line.strip():
            c = json.loads(line)
            c["area"] = f.stem
            CASES[c["id"]] = c


def _params():
    for id, c in CASES.items():
        reason = SKIPS.get(id) or SKIPS.get(c["area"])
        yield pytest.param(c, id=id, marks=pytest.mark.skip(reason=reason) if reason else ())


@pytest.mark.parametrize("case", list(_params()))
def test_case(case):
    sh = Shell()
    sh.run("mkdir -p /tmp/w && cd /tmp/w && hash -r")
    if case.get("setup"):
        sh.run(case["setup"])
    r = sh.run(case["script"])
    if case.get("toolOutput") is not None:
        assert r.to_tool_output() == case["toolOutput"]
        return
    assert r.stdout == case["stdout"]
    if isinstance(case.get("exitCode"), int):
        assert r.exit_code == case["exitCode"], r.stderr
    if isinstance(case.get("quiet"), bool):
        assert case["quiet"] == (r.stderr == ""), f"stderr: {r.stderr!r}"


def test_skip_list_names_real_cases():
    areas = {c["area"] for c in CASES.values()}
    assert [k for k in SKIPS if k not in CASES and k not in areas] == []
