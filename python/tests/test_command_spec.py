"""Drift checks against spec/commands/*.json (read here directly, so a stale _spec.py fails too): the built-ins are
exactly the spec's, and their help listing, --help, --version, options and usage errors come from it.
The C# and TypeScript ports run the same checks (CommandSpecTests.cs, command-spec.test.ts)."""
import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402

SPEC_DIR = Path(__file__).resolve().parents[2] / "spec"
DEFAULTS = json.loads((SPEC_DIR / "shell.json").read_text(encoding="utf-8"))["defaults"]
ALL = {c["name"]: c for c in (json.loads(f.read_text(encoding="utf-8")) for f in sorted((SPEC_DIR / "commands").glob("*.json")))
       if "py" in c.get("ports", ["py"])}
SPEC = {n: c for n, c in ALL.items() if "enabledBy" not in c}  # what every shell has
OPT_IN = sorted(n for n, c in ALL.items() if "enabledBy" in c)  # what the host turns on (python3: allow_python)
NAMES = sorted(SPEC)


def _lines(lines, name, summary=""):
    return "".join(x.replace("{name}", name).replace("{summary}", summary) + "\n" for x in lines)


def _q(s):
    return "'" + s.replace("'", "'\\''") + "'"


def _options(c):
    for o in c.get("options", []):
        if "py" not in o.get("ports", ["py"]):
            continue
        valued = "arg" in o
        if "short" in o:
            yield "-" + o["short"], valued
        for name in o.get("long", []):
            yield "--" + name, valued


def test_builtins_are_exactly_the_spec_commands():
    registered = set(Shell().commands)
    assert sorted(set(SPEC) - registered) == []  # in the spec, not registered
    assert sorted(registered - set(SPEC)) == []  # registered, not in the spec (add it, or mark the port missing)


def test_opt_in_commands_exist_only_once_enabled():
    assert [n for n in OPT_IN if n in Shell().commands] == []
    assert OPT_IN == ["python3"]  # a new one needs its enabling call here
    pytest.importorskip("wasmtime")
    from topwater.commands.python import PythonOptions
    sh = Shell().allow_python(PythonOptions(artifacts=str(SPEC_DIR.parent / "assets" / "python-wasm")))
    c = ALL["python3"]
    assert sh.commands["python3"].description == c["summary"]
    assert sh.run("python3 --help").stdout == sh.run("python3 -h").stdout == _lines(c["help"], "python3")
    assert sh.run("python3 --version").stdout == sh.run("python3 -V").stdout == c["version"] + "\n"


@pytest.mark.parametrize("name", NAMES)
def test_help_and_version_come_from_the_spec(name):
    c = SPEC[name]
    cmd = Shell().commands[name]
    help_text = _lines(c["help"], name) if "help" in c else None
    assert cmd.description == c["summary"]
    assert cmd.usage == help_text
    if not c.get("standardOptions", True):
        return
    sh = Shell()
    assert sh.run(f"{_q(name)} --help").stdout == (help_text or _lines(DEFAULTS["help"], name, c["summary"]))
    assert sh.run(f"{_q(name)} --version").stdout == c.get("version", DEFAULTS["version"].replace("{name}", name)) + "\n"


@pytest.mark.parametrize("name", NAMES)
def test_options_and_usage_errors_come_from_the_spec(name):
    c = SPEC[name]
    if c["parser"] != "args":
        return
    for arg, valued in _options(c):
        r = Shell().run(f"cd /tmp && {_q(name)} {arg}{' 1' if valued else ''} </dev/null")
        assert "invalid option" not in r.stderr and "unrecognized option" not in r.stderr, (arg, r.stderr)
    bad = Shell().run(f"{_q(name)} --no-such-option </dev/null")
    if c.get("shellBuiltin"):
        assert "unrecognized option '--no-such-option'" in bad.stderr
        return
    shown = c.get("reportsAs", name)
    assert bad.stderr == f"{shown}: unrecognized option '--no-such-option'\n" + _lines(c.get("tryHelp", DEFAULTS["tryHelp"]), shown)
    assert bad.exit_code == c.get("usageExitCode", 1)


@pytest.mark.parametrize("name", NAMES)
def test_options_outside_the_spec_are_rejected(name):
    c = SPEC[name]
    if c["parser"] != "args":
        return
    accepted = {arg for arg, _ in _options(c)}
    others = {f"--{n}" for o in c.get("options", []) for n in o.get("long", [])}  # listed for other ports only
    others |= {f"-{o['short']}" for o in c.get("options", []) if "short" in o}
    letters = {f"-{ch}" for ch in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"}
    sh = Shell()
    for arg in sorted((letters | others) - accepted):
        r = sh.run(f"cd /tmp && {_q(name)} {arg} </dev/null")
        assert "invalid option" in r.stderr or "unrecognized option" in r.stderr, (arg, r.stderr)


SUBCOMMANDS = sorted((name, sub) for name, c in SPEC.items() for sub, s in c.get("subcommands", {}).items() if "py" in s.get("ports", ["py"]))


@pytest.mark.parametrize("name,sub", SUBCOMMANDS)
def test_subcommands_come_from_the_spec(name, sub):
    """Summary and help come from the table; every option listed for this port is accepted, others are refused."""
    from topwater._spec import COMMANDS

    s = SPEC[name]["subcommands"][sub]
    table = COMMANDS[name].subcommands[sub]
    assert table.summary == s["summary"]
    sh = Shell()
    assert sh.run(f"{name} help {sub}").stdout == table.help
    assert sh.run(f"{name} {sub} --help").stdout == table.help
    if s.get("unsupported"):
        r = sh.run(f"{name} {sub}")
        assert r.exit_code == 128 and r.stderr.startswith("fatal: ")
        return
    for arg, valued in _options(s):
        r = Shell().run(f"cd /tmp && {name} init -q r && cd r && {name} {sub} {arg}{' 1' if valued else ''} </dev/null")
        assert "unknown option" not in r.stderr and "unknown switch" not in r.stderr, (sub, arg, r.stderr)
    if sub in ("help", "version", "rev-parse", "stash"):
        return  # these read their own arguments
    r = Shell().run(f"cd /tmp && {name} init -q r && cd r && {name} {sub} --no-such-option </dev/null")
    assert r.stderr == f"error: unknown option `no-such-option'\n{table.help}" and r.exit_code == 129
