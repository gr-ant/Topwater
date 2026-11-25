"""Permission holes from the filesystem review (ported from PermissionTests.cs); each expectation is what real
Linux and coreutils do for a non-root user."""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402


def setup() -> Shell:
    """The app puts a readable file in root's private home and a root-owned tree with one secret in /srv."""
    sh = Shell()
    sh.fs.write_text("/root/notes.txt", "private\n")
    sh.fs.mkdir("/srv/app/sub", parents=True)
    sh.fs.write_text("/srv/app/readme", "hello\n")
    sh.fs.write_text("/srv/app/sub/data", "data\n")
    sh.fs.write_text("/srv/app/secret", "s3cret\n")
    sh.fs.chmod("/srv/app/secret", 0o600)
    return sh


def err(r) -> str:
    return r.stderr.replace("bash: line 1: ", "").replace("bash: ", "")


UNSEARCHABLE = [
    ("cat /root/notes.txt", "cat: /root/notes.txt: Permission denied\n"),
    ("cat /root/missing", "cat: /root/missing: Permission denied\n"),
    ("cat < /root/notes.txt", "/root/notes.txt: Permission denied\n"),
    ("source /root/notes.txt", "/root/notes.txt: Permission denied\n"),
    ("cat /root/*", "cat: '/root/*': Permission denied\n"),
    ("grep -r private /root", "grep: /root: Permission denied\n"),
    ("ls /root", "ls: cannot open directory '/root': Permission denied\n"),
    ("ls -l /root/notes.txt", "ls: cannot access '/root/notes.txt': Permission denied\n"),
    ("stat /root/notes.txt", "stat: cannot statx '/root/notes.txt': Permission denied\n"),
    ("cd /root", "cd: /root: Permission denied\n"),
    ("cp /root/notes.txt ~/n", "cp: cannot stat '/root/notes.txt': Permission denied\n"),
    ("chmod 777 /root/notes.txt", "chmod: cannot access '/root/notes.txt': Permission denied\n"),
    ("to-markdown /root/notes.txt", "to-markdown: /root/notes.txt: Permission denied\n"),
    ("echo x > /root/notes.txt", "/root/notes.txt: Permission denied\n"),
    ("rm /root/notes.txt", "rm: cannot remove '/root/notes.txt': Permission denied\n"),
]


@pytest.mark.parametrize("cmd,expected", UNSEARCHABLE)
def test_unsearchable_directory_hides_its_entries(cmd, expected):
    sh = setup()
    r = sh.run(cmd)
    assert r.exit_code != 0 and r.stdout == "" and err(r) == expected, f"{cmd}: [{r.exit_code}] {r.stdout}|{r.stderr}"
    assert sh.fs.read_text("/root/notes.txt") == "private\n"  # the app still reads it


def test_tests_inside_unsearchable_directory_are_false():
    sh = setup()
    assert sh.run("[ -e /root/notes.txt ]; a=$?; [ -r /root/notes.txt ]; b=$?; [[ -f /root/notes.txt ]]; c=$?; "
                  "test -w /root/notes.txt; d=$?; [ -s /root/notes.txt ]; echo $a $b $c $d $?").stdout == "1 1 1 1 1\n"
    assert sh.run("[ -d /root ] && [ ! -r /root ] && echo ok").stdout == "ok\n"


def test_read_only_directory_lists_but_does_not_open():
    sh = setup()
    sh.fs.chmod("/srv/app", 0o744)
    assert sh.run("ls /srv/app").stdout == "readme\nsecret\nsub\n"
    assert sh.run("cat /srv/app/readme").stderr == "cat: /srv/app/readme: Permission denied\n"


def test_copy_recursive_skips_unreadable_children():
    sh = setup()
    r = sh.run("cp -r /srv/app ~/x")
    assert (r.exit_code, r.stderr) == (1, "cp: cannot open '/srv/app/secret' for reading: Permission denied\n")
    assert sh.run("ls ~/x; cat ~/x/readme ~/x/sub/data").stdout == "readme\nsub\nhello\ndata\n"
    r = sh.run("cd /srv && cp -r app/ ~/y; cat ~/y/secret")
    assert r.stderr == "cp: cannot open 'app/secret' for reading: Permission denied\ncat: /home/agent/y/secret: No such file or directory\n"
    r = sh.run("cp /srv/app/secret ~/s")
    assert (r.exit_code, r.stderr) == (1, "cp: cannot open '/srv/app/secret' for reading: Permission denied\n")
    assert not sh.fs.exists("/home/agent/s")


def test_copy_onto_existing_file_keeps_owner():
    sh = setup()
    sh.fs.write_text("/tmp/shared", "old")
    sh.fs.chmod("/tmp/shared", 0o666)
    sh.run("echo new > ~/n; cp ~/n /tmp/shared")
    assert sh.run("cat /tmp/shared; stat -c '%U %a' /tmp/shared").stdout == "new\nroot 666\n"


def test_sticky_tmp():
    sh = setup()
    sh.fs.write_text("/tmp/rootfile", "x")
    sh.fs.chmod("/tmp/rootfile", 0o666)
    sh.fs.mkdir("/tmp/rootdir")
    sh.fs.chmod("/tmp/rootdir", 0o777)
    for cmd, expected in [
        ("rm /tmp/rootfile", "rm: cannot remove '/tmp/rootfile': Operation not permitted\n"),
        ("rm -rf /tmp/rootdir", "rm: cannot remove '/tmp/rootdir': Operation not permitted\n"),
        ("mv /tmp/rootfile ~/f", "mv: cannot move '/tmp/rootfile' to '/home/agent/f': Operation not permitted\n"),
        ("echo y > ~/f; mv ~/f /tmp/rootfile", "mv: cannot move '/home/agent/f' to '/tmp/rootfile': Operation not permitted\n"),
    ]:
        r = sh.run(cmd)
        assert r.exit_code == 1 and r.stderr == expected, f"{cmd}: [{r.exit_code}] {r.stderr}"
    assert sh.fs.read_text("/tmp/rootfile") == "x"
    assert sh.fs.is_dir("/tmp/rootdir")
    assert sh.run("echo a > /tmp/mine && mv /tmp/mine /tmp/mine2 && rm /tmp/mine2 && echo b > /tmp/rootdir/f "
                  "&& rm /tmp/rootdir/f && echo ok").stdout == "ok\n"


def test_directory_write_permission():
    sh = setup()
    sh.run("mkdir -p ro/sub; echo b > ro/b; echo c > ro/sub/c; chmod 555 ro/sub; chmod 555 ro")
    for cmd, expected in [
        ("touch ro/new", "touch: cannot touch 'ro/new': Permission denied\n"),
        ("echo x > ~/ro/new", "/home/agent/ro/new: Permission denied\n"),
        ("mkdir ro/d", "mkdir: cannot create directory ‘ro/d’: Permission denied\n"),
        ("rm ro/b", "rm: cannot remove 'ro/b': Permission denied\n"),
        ("rm -r ro/sub", "rm: cannot remove 'ro/sub/c': Permission denied\n"),
        ("mv ro/b x", "mv: cannot move 'ro/b' to 'x': Permission denied\n"),
        ("mv ro/sub x", "mv: cannot move 'ro/sub' to 'x': Permission denied\n"),
        ("echo y > y; mv y ro/", "mv: cannot move 'y' to 'ro/y': Permission denied\n"),
    ]:
        r = sh.run(cmd)
        assert r.exit_code != 0 and err(r) == expected, f"{cmd}: [{r.exit_code}] {r.stderr}"
    assert sh.run("cat ro/b ro/sub/c").stdout == "b\nc\n"
    # a directory moved to a new parent must itself be writable (its ".." changes)
    assert sh.run("chmod 755 ro; mv ro/sub other").stderr == "mv: cannot move 'ro/sub' to 'other': Permission denied\n"
    assert sh.run("chmod 755 ro/sub && mv ro/sub other && echo ok").stdout == "ok\n"


def test_replace_unwritable_file_in_own_directory():
    sh = setup()
    sh.fs.write_text("/home/agent/cfg", "app")
    sh.fs.set_owner("/home/agent/cfg", "root")
    assert err(sh.run("echo agent > ~/cfg")) == "/home/agent/cfg: Permission denied\n"
    assert sh.run("stat -c '%U' ~ | tr '\\n' ' '; stat -c %U ~/cfg; rm ~/cfg && echo agent > ~/cfg && cat ~/cfg | tr '\\n' ' '; "
                  "stat -c %U ~/cfg").stdout == "agent root\nagent agent\n"


def test_chmod_chown_need_ownership():
    sh = setup()
    r = sh.run("chmod 666 /srv/app/secret; chown agent /srv/app/secret")
    assert r.stderr == ("chmod: changing permissions of '/srv/app/secret': Operation not permitted\n"
                        "chown: changing ownership of '/srv/app/secret': Operation not permitted\n")
    assert sh.fs.stat("/srv/app/secret").mode == 0o600


def test_app_is_unrestricted_while_agent_command_awaits():
    import asyncio

    async def main():
        sh = setup()
        run = asyncio.ensure_future(sh.run_async("sleep 0.3"))
        await asyncio.sleep(0.05)
        sh.fs.write_text("/etc/app.conf", "x")  # was: Permission denied, because run_as was "agent"
        assert sh.fs.read_text("/root/notes.txt") == "private\n"
        with sh.fs.as_root():
            pass
        await run
        assert sh.fs.run_as is None
        # a command the app registered still runs as the agent
        sh.register_command("peek", lambda ctx: ctx.stdout.write(f"{sh.fs.can_read('/root/notes.txt')}\n") and 0, "peek")
        assert (await sh.run_async("peek")).stdout == "False\n"

    asyncio.run(main())
