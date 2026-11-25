"""Symbolic and hard links where they could hurt (ported from SymlinkTests.cs and BashCompatArchives.cs): loops never hang or
overflow anything, a link grants no access its target doesn't, moving or copying through links can't cut a directory off the
tree, and an archive can't use a link to write outside its target. The cases recorded on real Linux are in the corpus."""
import io
import sys
import tarfile
import threading
import time
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402


def looped() -> Shell:
    sh = Shell(max_call_duration=20)
    # a link back to its own directory, one to the root, one to itself and a two-link cycle
    sh.run("mkdir -p /tmp/w/d/s && cd /tmp/w && echo hi > d/f && ln -s .. d/s/up && ln -s / d/root && ln -s self d/self && ln -s b d/a && ln -s a d/b")
    return sh


@pytest.mark.parametrize("cmd", [
    "find -L d > /dev/null", "find d -follow > /dev/null", "cp -rL d c", "cp -r d c && find c -type l | sort", "du -a d",
    "ls -RL d > /dev/null", "ls -R d", "tar cf t.tar d && tar tf t.tar", "zip -qr z.zip d", "tree d", "shopt -s globstar; echo d/**",
    "rm -r d", "cat d/a d/self", "realpath d/s/up/s/up/s/up/a", "cd d/self",
])
def test_loops_end_quickly(cmd):
    sh = looped()
    start = time.monotonic()
    r = sh.run("cd /tmp/w; " + cmd)
    assert time.monotonic() - start < 10, cmd
    assert "timed out" not in r.stderr
    Shell.load(_saved(sh))  # and the machine still saves and loads


def _saved(sh: Shell) -> bytes:
    b = io.BytesIO()
    sh.save(b)
    return b.getvalue()


def run_with_deadline(sh: Shell, script: str, seconds: float = 30):
    """On its own thread with a deadline: a link followed without end must fail the test, not hang the run."""
    result = []
    worker = threading.Thread(target=lambda: result.append(sh.run(script)), daemon=True)
    worker.start()
    worker.join(seconds)
    assert not worker.is_alive(), f"{script!r} did not finish within {seconds} s"
    return result[0]


@pytest.mark.parametrize("cmd", ["find -L out | wc -l", "cp -rL out o2", "grep -R x out | wc -l", "zip -qr z.zip out", "chmod -R u+w out",
                                 "ls -RL out | wc -l"])
def test_link_fan_out_stops_at_the_time_limit(cmd):
    # two links per level to the level below: no loop, but 2^30 paths
    sh = Shell(max_call_duration=2)
    sh.run("mkdir -p /tmp/w/out/d0 && cd /tmp/w && echo x > out/d0/f && for i in $(seq 1 30); do mkdir out/d$i; "
           "ln -s ../d$((i-1)) out/d$i/a; ln -s ../d$((i-1)) out/d$i/b; done")
    start = time.monotonic()
    run_with_deadline(sh, "cd /tmp/w; " + cmd)
    assert time.monotonic() - start < 10, cmd


@pytest.mark.parametrize("setup", ["mkdir -p w/a; cd w; ln -s /tmp/w/w a/abs; mkdir a/b", "mkdir -p w/a/b; cd w; ln -s /tmp/w/w a/abs"])
def test_copying_through_links_to_a_parent_ends(setup):
    # cp -rL meets a directory it is already copying (through a link higher up, then down a plain child): a cyclic link, in either order
    sh = Shell(max_call_duration=5)
    r = run_with_deadline(sh, f"mkdir -p /tmp/w && cd /tmp/w && {setup}; echo x > a/b/f; ln -s .. a/b/up; ln -s . a/self; ln -s a l; ln -s l l2; "
                              "cp -rL a zz; echo rc=$?; find zz | wc -l")
    assert r.stdout == "rc=1\n4\n"  # zz, zz/abs, zz/b, zz/b/f
    assert sum("cannot copy cyclic symbolic link" in line for line in r.stderr.split("\n")) == 5


def test_a_link_grants_nothing_its_target_does_not():
    sh = Shell()
    sh.fs.write_text("/root/secret", "s\n")
    sh.fs.write_text("/etc/app.conf", "conf\n")
    r = sh.run("cd /tmp; ln -s /root/secret s; cat s; ln -s /etc/app.conf c; echo x >> c; ln /etc/app.conf h; ln -s /etc e; touch e/new; cat c")
    assert r.stdout == "conf\n"
    for message in ("cat: s: Permission denied", "c: Permission denied", "ln: failed to create hard link 'h' => '/etc/app.conf': Operation not permitted",
                    "touch: cannot touch 'e/new': Permission denied"):
        assert message in r.stderr
    assert sh.fs.read_text("/etc/app.conf") == "conf\n"
    assert not sh.fs.exists("/etc/new")


def test_no_directory_moves_into_itself_through_a_link():
    sh = Shell()
    r = sh.run("cd /tmp && mkdir -p d/sub && ln -s d/sub in && mv d in/; mv d in/x; cp -r d in/; ls -R d | head -5")
    assert r.stdout == "d:\nsub\n\nd/sub:\n"
    assert "mv: cannot move 'd' to a subdirectory of itself, 'in/d'" in r.stderr
    assert "cp: cannot copy a directory, 'd', into itself, 'in/d'" in r.stderr


def test_hard_links_share_one_file_and_its_bytes():
    sh = Shell(max_file_system_bytes=64 << 20)
    sh.run("cd /tmp && printf '%10000s' '' > big")
    used = sh.fs.used_bytes
    r = sh.run("cd /tmp && for i in 1 2 3; do ln big h$i; done; stat -c %h big; rm big h1 h2; stat -c '%h %s' h3")
    assert r.stdout == "4\n1 10000\n"
    assert sh.fs.used_bytes == used
    sh.run("rm /tmp/h3")
    assert sh.fs.used_bytes == used - 10000


# ---------- tar ----------

def make_tar(*members) -> bytes:
    """members: (name, type, linkname, data); tarfile writes names and link names exactly as given."""
    out = io.BytesIO()
    with tarfile.open(fileobj=out, mode="w", format=tarfile.GNU_FORMAT) as t:
        for name, kind, link, data in members:
            ti = tarfile.TarInfo(name)
            ti.type, ti.linkname, ti.size, ti.mode = kind, link, len(data), 0o644
            t.addfile(ti, io.BytesIO(data))
    return out.getvalue()


def file(name, data=b"x"):
    return (name, tarfile.REGTYPE, "", data)


def link(kind, name, target):
    return (name, kind, target, b"")


def test_tar_never_writes_outside_the_target():
    sh = Shell()
    sh.run("mkdir -p /tmp/w && cd /tmp/w")
    sh.fs.write_bytes("/tmp/w/evil.tar", make_tar(
        file("../up.txt"), file("/abs.txt"), file("ok/../../up2.txt"), file("../../../../../../etc/evil"), file("good.txt", b"good\n"),
        link(tarfile.SYMTYPE, "lnk", "/etc"), file("lnk/passwd"),
        link(tarfile.LNKTYPE, "hl", "../outside"), link(tarfile.LNKTYPE, "hl2", "/etc/passwd"), link(tarfile.LNKTYPE, "hl3", "good.txt"),
        link(tarfile.SYMTYPE, "up", ".."), file("up/escaped"), link(tarfile.LNKTYPE, "hl4", "lnk/passwd")))
    sh.fs.write_text("/tmp/w/outside", "secret\n")
    r = sh.run("mkdir x && tar xf evil.tar -C x; echo rc=$?; cd x && find . | sort; cat hl3")
    assert r.stdout == "rc=2\n.\n./abs.txt\n./good.txt\n./hl3\n./lnk\n./up\ngood\n"
    for message in ("tar: Removing leading `../' from member names", "tar: ../up.txt: Member name contains '..'",
                    "tar: ok/../../up2.txt: Member name contains '..'", "tar: Removing leading `/' from member names",
                    # a link the archive made is kept, but nothing is written or hard-linked through it (as GNU tar does)
                    "tar: lnk/passwd: Cannot open: Not a directory", "tar: up/escaped: Cannot open: Not a directory",
                    "tar: hl4: Cannot hard link to ‘lnk/passwd’: No such file or directory",
                    "tar: hl: Cannot hard link to ‘outside’: No such file or directory",
                    "tar: hl2: Cannot hard link to ‘etc/passwd’: No such file or directory"):
        assert message in r.stderr
    for p in ("/tmp/up.txt", "/tmp/w/up.txt", "/tmp/up2.txt", "/abs.txt", "/etc/evil", "/tmp/w/x/hl", "/tmp/w/escaped", "/tmp/w/x/hl4"):
        assert not sh.fs.exists(p), p
    assert sh.fs.read_text("/tmp/w/outside") == "secret\n"
    assert sh.fs.read_text("/etc/passwd").startswith("root:")
