"""The interpreter for Topwater's opt-in python3 command: bashkit's CPython 3.14 build for wasm32-wasip1 and its standard
library (interpreter/, copied from the repository's assets/python-wasm). Topwater finds it here when the host calls
Shell.allow_python() without an artifacts folder. Nothing to import yourself."""
from pathlib import Path

ARTIFACTS = Path(__file__).resolve().parent / "interpreter"
