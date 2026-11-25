"""Real host programs exposed as commands (uses the real git; skipped when it isn't installed)."""
import shutil
import sys
import threading
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
        self.assertIn("can only use files under /home/agent and /tmp", r.stderr)
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

    @unittest.skipUnless(shutil.which("sh") and shutil.which("find"), "sh or find not installed")
    def test_virtual_links_never_lead_out_of_the_mirror(self):
        # a loop or a link to / is not followed, a link to a file outside the workspace is left out, one to a file inside goes
        # as a copy; no real link is made, and nothing hangs
        sh = Shell()
        real = register_host_command(sh, "realsh", "sh")
        self.addCleanup(real.close)
        out(sh, "mkdir -p proj/d && cd proj && echo in > d/f && echo secret > /tmp/outside && ln -s .. d/up && ln -s / root && "
                "ln -s /tmp/outside out && ln -s d/f inside && ln -s d dl")
        # on its own thread with a deadline: a link followed without end must fail the test, not hang the run
        result = []
        worker = threading.Thread(target=lambda: result.append(sh.run("cd ~/proj && realsh -c 'find . | sort; cat inside; find . -type l | wc -l'")), daemon=True)
        worker.start()
        worker.join(60)
        self.assertFalse(worker.is_alive(), "the host command did not finish within 60 s")
        r = result[0]
        self.assertEqual("", r.stderr)
        self.assertEqual(".\n./d\n./d/f\n./inside\nin\n0\n", r.stdout)
        self.assertEqual("..\n/\n/tmp/outside\nd/f\nd\n", out(sh, "cd ~/proj && readlink d/up root out inside dl"))


if __name__ == "__main__":
    unittest.main()
