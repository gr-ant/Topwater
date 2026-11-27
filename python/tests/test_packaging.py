from pathlib import Path

HERE = Path(__file__).resolve().parents[1]


def test_third_party_notices_is_the_repository_copy():
    # the package carries jq's help text (_spec.py), so it ships the notice: a copy, since setuptools packs only this folder
    assert (HERE / "THIRD-PARTY-NOTICES.md").read_text() == (HERE.parent / "THIRD-PARTY-NOTICES.md").read_text()
    assert (HERE / "LICENSE").read_text() == (HERE.parent / "LICENSE").read_text()
    assert 'license-files = ["LICENSE", "THIRD-PARTY-NOTICES.md"]' in (HERE / "pyproject.toml").read_text()


# each module imports alone, as the first import of a fresh interpreter: this is what finds import cycles
# (docs/architecture.md §3.3 and §9). A missing optional framework (an integration's) is not a failure.
CHILD = """import importlib, sys
try:
    importlib.import_module(sys.argv[1])
except ImportError as e:
    missing = e if isinstance(e, ModuleNotFoundError) else e.__cause__
    if not isinstance(missing, ModuleNotFoundError) or (missing.name or "").split(".")[0] == "topwater":
        raise
"""


def test_each_module_imports_alone():
    import subprocess
    import sys

    src = HERE / "src"
    modules = [".".join(p.relative_to(src).with_suffix("").parts).removesuffix(".__init__")
               for p in sorted((src / "topwater").rglob("*.py")) if p.name != "__main__.py"]
    assert len(modules) > 50
    env = {"PYTHONPATH": str(src), "PATH": ""}
    procs = {m: subprocess.Popen([sys.executable, "-c", CHILD, m], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
             for m in modules}
    errors = {m: p.communicate()[1] for m, p in procs.items()}
    failed = {m: errors[m].strip().splitlines()[-1] for m, p in procs.items() if p.returncode}
    assert failed == {}
