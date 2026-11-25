"""Host bridge: /tmp sharing, incremental mirror sync, loader variables, the bubblewrap launcher (ported from
HostCommandTests.cs, HostBridgeTests2.cs and the host parts of SecondReviewTests.cs). Each test is skipped where
its programs are missing."""
import os
import shutil
import sys
import time
import uuid
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402
from topwater.host import bubblewrap, register_host_command  # noqa: E402

needs_sh = pytest.mark.skipif(not shutil.which("sh"), reason="sh not installed")


def out(sh: Shell, cmd: str) -> str:
    r = sh.run(cmd)
    assert r.exit_code == 0, f"`{cmd}` exited {r.exit_code}: {r.stderr}"
    return r.stdout


def host(sh, name, program, request, **opts):
    cmd = register_host_command(sh, name, program, **opts)
    request.addfinalizer(cmd.close)
    return cmd


@pytest.mark.skipif(not (shutil.which("cp") and shutil.which("sh")), reason="cp/sh not installed")
def test_tmp_is_shared_and_key_value_paths_are_redirected(request):
    sh = Shell()
    host(sh, "realcp", "cp", request)
    host(sh, "realsh", "sh", request)
    sh.run("echo scratch > /tmp/in.txt")
    assert out(sh, "realcp /tmp/in.txt /tmp/out.txt && cat /tmp/out.txt") == "scratch\n"
    assert out(sh, "cd /tmp && realcp in.txt ~/home-copy.txt && cat ~/home-copy.txt") == "scratch\n"
    # a path after key= is redirected too, so it can't reach the host's real /tmp
    assert out(sh, "echo scratch > /tmp/only-virtual-$$.txt; realsh -c 'cat \"${0#src=}\"' src=/tmp/only-virtual-$$.txt") == "scratch\n"


@needs_sh
def test_unchanged_files_are_not_rewritten_and_changes_still_sync_both_ways(request):
    sh = Shell()
    real = host(sh, "realsh", "sh", request)
    out(sh, "echo same > keep.txt && echo one > edit.txt && echo a > prog.txt && realsh -c true")
    keep = real.root / "home/agent/keep.txt"
    stamp = keep.stat().st_mtime_ns
    # the agent changes one file to the same size and the program another, right after the export
    assert out(sh, "echo two > edit.txt && realsh -c 'cat edit.txt; echo b > prog.txt'") == "two\n"
    assert out(sh, "cat prog.txt") == "b\n"
    assert keep.stat().st_mtime_ns == stamp
    # deletions in both directions still travel
    out(sh, "rm keep.txt && realsh -c 'test ! -e keep.txt && rm prog.txt'")
    assert not sh.fs.exists("/home/agent/prog.txt")
    assert not keep.exists()
    # a binary file the program rewrites with the same size comes back
    sh.fs.write_bytes("/home/agent/bin.dat", bytes([0, 1, 2, 3]))
    out(sh, "realsh -c 'printf \"\\000\\011\\012\\013\" > bin.dat'")
    assert sh.fs.read_bytes("/home/agent/bin.dat") == bytes([0, 9, 10, 11])


@needs_sh
def test_many_quick_same_size_rewrites_are_all_seen(request):
    sh = Shell()
    host(sh, "realsh", "sh", request)
    for i in range(10):
        # the program rewrites the file it was just given, in the same clock tick if the machine is fast
        assert out(sh, f"echo {i} > n.txt && realsh -c 'cat n.txt; echo {(i + 1) % 10} > n.txt'") == f"{i}\n"
        assert out(sh, "cat n.txt") == f"{(i + 1) % 10}\n"


@pytest.mark.skipif(not shutil.which("ln"), reason="ln not installed")
def test_a_link_left_in_the_mirror_is_never_written_through(request):
    sh = Shell()
    ln = host(sh, "realln", "ln", request)
    out(sh, "echo original > target.txt && realln -s target.txt link.txt")
    out(sh, "echo replaced > link.txt && realln --version")  # any call exports the workspace again
    mirror = ln.root / "home/agent"
    assert not (mirror / "link.txt").is_symlink()
    assert (mirror / "link.txt").read_text() == "replaced\n"
    assert (mirror / "target.txt").read_text() == "original\n"


@pytest.mark.skipif(not (shutil.which("sh") and shutil.which("sleep")), reason="sh/sleep not installed")
def test_a_background_child_does_not_hang_the_command(request):
    sh = Shell()
    host(sh, "realsh", "sh", request)
    t = time.time()
    r = sh.run("realsh -c 'sleep 15 & echo started'")
    assert time.time() - t < 10
    assert r.stdout == "started\n"


@needs_sh
def test_modes_variables_and_root_reach_the_program(request):
    sh = Shell()
    host(sh, "realsh", "sh", request)
    assert out(sh, "printf '#!/bin/sh\\necho ran\\n' > run.sh && chmod +x run.sh && realsh -c ./run.sh") == "ran\n"
    assert out(sh, "export FOO=bar; realsh -c 'echo $FOO'") == "bar\n"
    assert out(sh, "realsh -c 'ls \"$1\"' x /") == "home\ntmp\n"  # "/" is the mirror, not the host's root
    assert out(sh, "realsh -c 'echo made > new.sh; chmod +x new.sh' && test -x new.sh && cat new.sh") == "made\n"
    r = sh.run("realsh /etc/hostname")
    assert r.exit_code != 0
    assert "can only use files under /home/agent and /tmp" in r.stderr


@needs_sh
def test_loader_variables_do_not_reach_host_programs(request):
    sh = Shell()
    host(sh, "realsh", "sh", request)
    r = sh.run("export LD_PRELOAD=/home/agent/evil.so LD_LIBRARY_PATH=/home/agent KEEP=yes; realsh -c 'echo \"[$LD_PRELOAD][$LD_LIBRARY_PATH][$KEEP]\"'")
    assert r.stdout == "[][][yes]\n"


# ---------- bubblewrap ----------

def sandboxed(request, network=False, program="sh", name="realsh", **opts):
    if not shutil.which("bwrap") or not shutil.which(program):
        pytest.skip(f"bwrap/{program} not installed")
    try:
        launcher = bubblewrap(network)
    except RuntimeError as e:
        pytest.skip(str(e))
    sh = Shell()
    return sh, host(sh, name, program, request, launcher=launcher, **opts)


def test_bubblewrap_runs_the_program_and_round_trips_files(request):
    # rewrite_paths off, so these tests hit the sandbox and not the argument redirection
    sh, _ = sandboxed(request, rewrite_paths=False)
    assert out(sh, "mkdir -p proj && cd proj && echo hello > in.txt && realsh -c 'cat in.txt; cp in.txt out.txt; echo t > /tmp/scratch'") == "hello\n"
    assert out(sh, "cat ~/proj/out.txt") == "hello\n"
    assert out(sh, "cd ~ && realsh -c 'cd \"$HOME\"/proj && pwd'") == "/home/agent/proj\n"
    assert not sh.fs.exists("/tmp/scratch")  # the program's /tmp is its own


def test_bubblewrap_hides_the_real_home_and_other_files(request):
    sh, _ = sandboxed(request, rewrite_paths=False)
    secret = Path(os.path.expanduser("~")) / f".topwater-secret-{uuid.uuid4().hex}"
    secret.write_text("secret")
    try:
        r = sh.run(f"realsh -c 'cat \"$1\"' x {secret}")
        assert "secret" not in r.stdout and r.exit_code != 0
        # the same command without the sandbox can read it, so the test means something
        host(sh, "plainsh", "sh", request, rewrite_paths=False)
        assert out(sh, f"plainsh -c 'cat \"$1\"' x {secret}") == "secret"
    finally:
        secret.unlink()


@pytest.mark.skipif(not shutil.which("git"), reason="git not installed")
def test_git_in_bubblewrap_works_but_its_aliases_cannot_read_host_files(request):
    env = {"GIT_AUTHOR_NAME": "A", "GIT_AUTHOR_EMAIL": "a@x", "GIT_COMMITTER_NAME": "A", "GIT_COMMITTER_EMAIL": "a@x"}
    sh, _ = sandboxed(request, program="git", name="git", env=env)
    secret = Path(os.path.expanduser("~")) / f".topwater-secret-{uuid.uuid4().hex}"
    secret.write_text("secret")
    try:
        assert out(sh, "git init -q -b main && echo hi > a.txt && git add a.txt && git commit -qm first && git log --oneline | wc -l") == "1\n"
        r = sh.run(f"git -c alias.x='!cat {secret}' x")
        assert r.exit_code != 0 and "secret" not in r.stdout
    finally:
        secret.unlink()


def test_bubblewrap_has_no_network_by_default(request):
    sh, _ = sandboxed(request, rewrite_paths=False)
    # a fresh network namespace has only the loopback interface
    lines = out(sh, "realsh -c 'cat /proc/net/dev'").split("\n")[2:]
    assert [l.split(":")[0].strip() for l in lines if ":" in l] == ["lo"]


def test_bubblewrap_launcher_binds_the_mirror_and_keeps_the_command():
    if not shutil.which("bwrap"):
        pytest.skip("bwrap not installed")
    try:
        net, plain = bubblewrap(network=True), bubblewrap()
    except RuntimeError as e:
        pytest.skip(str(e))
    argv = net(["/usr/bin/git", "status"], "/tmp/mirror")
    assert "--share-net" in argv
    assert argv[-6:] == ["--bind", "/tmp/mirror", "/tmp/mirror", "--", "/usr/bin/git", "status"]
    assert "--share-net" not in plain(["/usr/bin/git"], "/tmp/mirror")
