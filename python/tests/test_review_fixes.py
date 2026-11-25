"""Regression tests for behaviour that differed from real bash / GNU tools. Mirrored in ReviewFixTests.cs."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402


class ReviewFixTests(unittest.TestCase):
    def test_loop_limit_counts_body_runs(self):
        sh = Shell(max_loop_iterations=3)
        self.assertEqual("1\n2\n3\n", sh.run("printf '1\\n2\\n3\\n' | while read -r l; do echo $l; done").to_tool_output())
        self.assertIn("loop aborted after 3 iterations", sh.run("while true; do :; done").stderr)


if __name__ == "__main__":
    unittest.main()
