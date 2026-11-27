"""Checks that the three ports follow tools/layout.json (docs/architecture.md §8).

python3 tools/check_layout.py              check the repository
python3 tools/check_layout.py --strict cs  treat a port as finished even while it has a legacy entry
python3 tools/check_layout.py --self-test  run the checker's own tests

Fails when (1) a listed file is missing, or a null has no note; (2) a source file is in no row; (3) a file's
`---------- name ----------` markers differ from its row's sections; (4) a file's registration calls differ from
its row's commands, or a spec command is in no row or in two; (5) a file named in `legacy` doesn't exist; (6) the
registry (RegisterAll / register_all / registerAll) calls the registrars in another order than the rows with
`commands`. While a port has a `legacy` entry, (1), (3) and (4) are reported for it as pending and don't fail.
"""
import json
import re
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXT = {"cs": ".cs", "py": ".py", "ts": ".ts"}
MARKER = re.compile(r"^\s*(?://|#) -{10} (.+?) -{10}\s*$")
# a command registration: Register("x", ...), r("x", ...), reg('x', ...), register('x', ...); jq's reg('f', 0, ...) is not one
CALL = re.compile(r"""\b(?:Register|register_command|register|reg|r)\(\s*(["'])([^"'\s]+)\1\s*,(?!\s*\d)""")
LOOP_CALL = re.compile(r"""\b(?:Register|register_command|register|reg|r)\(\s*[A-Za-z_]\w*\s*,""")
LOOP = re.compile(r"^\s*(?:foreach|for)\b")
REGISTRY = re.compile(r"\b(?:void RegisterAll|def register_all|function registerAll)\(")
REGISTRY_CALL = re.compile(r"^\s*([\w.]+)\(shell\);?\s*$")


def markers(text):
    return [m.group(1) for line in text.split("\n") if (m := MARKER.match(line))]


def registrations(text, names):
    """The spec commands a file registers, in order. A loop over literal names that registers its variable counts too."""
    out, lines = [], text.split("\n")
    for i, line in enumerate(lines):
        out += [m.group(2) for m in CALL.finditer(line) if m.group(2) in names]
        # the loop line or one of the two after it (past a lone "{") registers the loop variable
        if LOOP.match(line) and any(LOOP_CALL.search(b) for b in lines[i:i + 3]):
            out += [q for q in re.findall(r"""["']([^"'\s]+)["']""", line) if q in names]
    return out


def registry_calls(text):
    """The calls in the registry function's body, in order: `RegisterTextCommands`, `text.register`, `registerText`."""
    lines = text.split("\n")
    start = next((i for i, line in enumerate(lines) if REGISTRY.search(line)), None)
    if start is None:
        return None
    out = []
    for line in lines[start + 1:]:
        if not line.strip() or line.strip() == "{" or line.lstrip().startswith("from "):
            continue
        if not (m := REGISTRY_CALL.match(line)):
            break
        out.append(m.group(1))
    return out


def registrar_row(call, keys):
    """The row a registry call belongs to: its name without `register`/`commands` is a prefix of the row's or the reverse."""
    n = re.sub(r"register|commands|\.", "", call.lower())
    hits = [k for k in keys if n and (k.startswith(n) or n.startswith(k))]
    return hits[0] if len(hits) == 1 else None


def check(root, layout, names, strict=()):
    errors, pending = [], []
    legacy = {p: v for p, v in layout.get("legacy", {}).items() if p not in strict}

    def fail(port, msg, soft=True):
        (pending if soft and port in legacy else errors).append(f"{port}: {msg}")

    listed = {p: set() for p in EXT}
    owner = {}
    registry = {}
    for row in layout["modules"]:
        for port in EXT:
            rel = row.get(port)
            if rel is None:
                if not row.get("note"):
                    fail(port, f"{row['name']}: null without a note", soft=False)
                continue
            listed[port].add(rel)
            path = root / layout["roots"][port] / rel
            if not path.is_file():
                fail(port, f"{row['name']}: {rel} does not exist")
                continue
            text = path.read_text(encoding="utf-8")
            if (calls := registry_calls(text)) is not None:
                registry[port] = (rel, calls)
            if "sections" in row:
                found, want = markers(text), row["sections"]
                if port != "cs" and found[-1:] == ["helpers"] and want[-1:] != ["helpers"]:
                    found = found[:-1]
                if found != want:
                    fail(port, f"{rel}: sections {found} should be {want}")
            got = registrations(text, names)
            if got != row.get("commands", []):
                fail(port, f"{rel}: registers {got} but its row lists {row.get('commands', [])}")
        for c in row.get("commands", []):
            if c in owner:
                errors.append(f"command {c} is in rows {owner[c]} and {row['name']}")
            owner[c] = row["name"]
    for c in sorted(names - owner.keys()):
        errors.append(f"command {c} (spec/commands) is in no row's commands")
    for c in sorted(owner.keys() - names):
        errors.append(f"command {c} in row {owner[c]} is not in spec/commands")
    keys = {r["name"].removeprefix("commands."): r for r in layout["modules"] if "commands" in r}
    for port in EXT:
        if port not in registry:
            if any(r.get(port) for r in keys.values()):
                fail(port, "no file defines the registry (RegisterAll / register_all / registerAll)")
            continue
        rel, calls = registry[port]
        got = [registrar_row(c, keys) or f"? {c}" for c in calls]
        want = [k for k, r in keys.items() if r.get(port)]
        if got != want:
            fail(port, f"{rel}: the registry calls the registrars as {got} but the rows with commands are {want}")
    for port, files in layout.get("legacy", {}).items():
        for rel in files:
            if not (root / layout["roots"][port] / rel).is_file():
                errors.append(f"{port}: legacy file {rel} does not exist (remove it from tools/layout.json)")
    for port, ext in EXT.items():
        base = root / layout["roots"][port]
        skip = layout.get("ignore", {}).get(port, [])
        allowed = listed[port] | set(layout.get("legacy", {}).get(port, []))
        for path in sorted(base.rglob("*" + ext)):
            rel = path.relative_to(base).as_posix()
            if any(rel.startswith(s) for s in skip) or "/__pycache__/" in "/" + rel or "/node_modules/" in "/" + rel:
                continue
            if rel not in allowed:
                errors.append(f"{port}: {rel} is in no row of tools/layout.json (add it with its counterparts, or a note)")
    return errors, pending


def spec_names(root):
    return {json.loads(p.read_text(encoding="utf-8"))["name"] for p in (root / "spec/commands").glob("*.json")}


def main(argv):
    if "--self-test" in argv:
        return self_test()
    strict = {argv[i + 1] for i, a in enumerate(argv) if a == "--strict" and i + 1 < len(argv)}
    layout = json.loads((ROOT / "tools/layout.json").read_text(encoding="utf-8"))
    errors, pending = check(ROOT, layout, spec_names(ROOT), strict)
    for p in pending:
        print("pending:", p)
    for e in errors:
        print("error:", e)
    print(f"check_layout: {len(errors)} errors, {len(pending)} pending")
    return 1 if errors else 0


def self_test():
    with tempfile.TemporaryDirectory() as d:
        root = Path(d)
        files = {
            "src/A.cs": '// ---------- one ----------\n// ---------- two ----------\nshell.Register("ls", X);\n'
                        'foreach (var n in new[] { "gzip", "zcat" })\n    shell.Register(n, Y);\n',
            "py/a.py": '# ---------- one ----------\n# ---------- two ----------\n# ---------- helpers ----------\n'
                       'r("ls", _ls)\nr("gzip", _g)\nr("zcat", _g)\nreg("ls", 0, f)\n',
            "ts/a.ts": "// ---------- two ----------\nreg('gzip', g);\n",
            "ts/extra.ts": "",
            "src/R.cs": "public static void RegisterAll(Shell shell)\n{\n    RegisterACommands(shell);\n    B.Register(shell);\n}\n",
            "py/r.py": "def register_all(shell) -> None:\n    from . import a, b\n\n    b.register(shell)\n    a.register(shell)\n",
        }
        for rel, text in files.items():
            (root / rel).parent.mkdir(parents=True, exist_ok=True)
            (root / rel).write_text(text)
        layout = {"roots": {"cs": "src", "py": "py", "ts": "ts"},
                  "modules": [{"name": "a", "cs": "A.cs", "py": "a.py", "ts": "a.ts", "sections": ["one", "two"],
                               "commands": ["ls", "gzip", "zcat"]},
                              {"name": "b", "cs": "B.cs", "py": "b.py", "ts": None, "note": "x", "commands": ["cat"]},
                              {"name": "r", "cs": "R.cs", "py": "r.py", "ts": None, "note": "x"}]}
        (root / "src/B.cs").write_text('shell.Register("cat", Z);\n')
        (root / "py/b.py").write_text('r("cat", _c)\n')
        errors, pending = check(root, layout, {"ls", "gzip", "zcat", "cat", "cut"})
        assert not any(e.startswith("cs:") for e in errors), errors
        assert [e for e in errors if e.startswith("py:")] == [                          # check 6
            "py: r.py: the registry calls the registrars as ['b', 'a'] but the rows with commands are ['a', 'b']"], errors
        assert any("ts: no file defines the registry" in e for e in errors), errors
        (root / "py/r.py").write_text("def register_all(shell) -> None:\n    a.register(shell)\n    b.register(shell)\n")
        errors, pending = check(root, layout, {"ls", "gzip", "zcat", "cat", "cut"})
        assert not any(e.startswith(("cs:", "py:")) for e in errors), errors
        assert any("ts: a.ts: sections ['two']" in e for e in errors), errors          # check 3
        assert any("ts: a.ts: registers ['gzip']" in e for e in errors), errors        # check 4, order and set
        assert any("extra.ts is in no row" in e for e in errors), errors                # check 2
        assert any("command cut (spec/commands) is in no row" in e for e in errors), errors
        layout["legacy"] = {"ts": ["extra.ts"]}
        errors, pending = check(root, layout, {"ls", "gzip", "zcat", "cat"})
        assert errors == [], errors
        assert len(pending) == 3, pending
        errors, _ = check(root, layout, {"ls", "gzip", "zcat", "cat"}, strict={"ts"})
        assert len(errors) == 3, errors
        layout["legacy"] = {"ts": ["extra.ts", "gone.ts"]}                              # check 5
        errors, _ = check(root, layout, {"ls", "gzip", "zcat", "cat"})
        assert errors == ["ts: legacy file gone.ts does not exist (remove it from tools/layout.json)"], errors
    print("check_layout self-test: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
