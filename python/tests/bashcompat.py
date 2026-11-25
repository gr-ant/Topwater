"""The check behind the test_bashcompat_* tables: a script run in a fresh shell (in an empty directory, like the
differential runs against real bash) gives bash's stdout and exit status, and writes to stderr only when bash did."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402


def check(script: str, stdout: str, exit_code: int, quiet: bool) -> None:
    sh = Shell()
    sh.run("mkdir -p /tmp/w && cd /tmp/w")
    r = sh.run(script)
    assert r.stdout == stdout
    assert r.exit_code == exit_code, r.stderr
    assert quiet == (r.stderr == ""), f"bash {'wrote nothing to' if quiet else 'wrote to'} stderr; this shell wrote: {r.stderr}"
