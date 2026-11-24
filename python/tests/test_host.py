"""Real host programs exposed as commands (uses the real git; skipped when it isn't installed)."""
import shutil
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402
from topwater.host import register_host_command  # noqa: E402

GIT_ENV = {"GIT_AUTHOR_NAME": "Agent", "GIT_AUTHOR_EMAIL": "agent@example.com", "GIT_COMMITTER_NAME": "Agent",
           "GIT_COMMITTER_EMAIL": "agent@example.com", "GIT_CONFIG_NOSYSTEM": "1"}


def out(shell: Shell, cmd: str) -> str:
    r = shell.run(cmd)
    assert r.exit_code == 0, f"`{cmd}` exited {r.exit_code}: {r.stderr}"
    return r.stdout


class HostCommandTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which("git"), "git not installed")
    def test_real_git_works_on_virtual_files(self):
        sh = Shell()
        git = register_host_command(sh, "git", env=GIT_ENV)
        self.addCleanup(git.close)
        out(sh, "mkdir proj && cd proj && git init -q -b main && echo hello > a.txt && git add a.txt && git commit -q -m first")
        self.assertEqual(" M a.txt\n", out(sh, "echo world >> a.txt && git status --short"))
        self.assertEqual("2\n", out(sh, "git commit -qam second && git log --oneline | wc -l"))
        # files the program creates, changes and deletes show up in the virtual filesystem (and vice versa)
        self.assertEqual("a.txt\n", out(sh, "git checkout -q -b feature && echo new > b.txt && mkdir d && echo x > d/c.txt && git add . && git commit -qm 'add b' && git checkout -q main && ls"))
        self.assertEqual("a.txt\nb.txt\nd\nnew\n", out(sh, "git merge -q feature && ls && cat b.txt"))
        self.assertEqual(" D d/c.txt\n", out(sh, "rm -r d && git status --short"))
        self.assertTrue(len(sh.fs.read_bytes("/home/agent/proj/.git/index")) > 0)  # binary files round-trip
        # path arguments can't reach the host's own files, and output shows virtual paths
        r = sh.run("git diff --no-index /etc/hostname a.txt")
        self.assertIn("Could not access '/etc/hostname'", r.stderr)
        r = sh.run("git -C ../../../../../etc status")
        self.assertNotEqual(0, r.exit_code)
        self.assertNotIn(str(git.root), r.stdout + r.stderr)
        self.assertEqual("/home/agent/proj\n", out(sh, "git rev-parse --show-toplevel"))

    def test_missing_program_fails_at_registration(self):
        with self.assertRaises(FileNotFoundError):
            register_host_command(Shell(), "definitely-not-a-real-program-xyz")

    @unittest.skipUnless(shutil.which("sleep"), "sleep not installed")
    def test_timeout_kills_the_program(self):
        sh = Shell()
        slow = register_host_command(sh, "realsleep", "sleep", timeout=0.3)
        self.addCleanup(slow.close)
        r = sh.run("realsleep 30")
        self.assertEqual(124, r.exit_code)
        self.assertIn("timed out", r.stderr)


if __name__ == "__main__":
    unittest.main()
