"""Snapshot, restore, fork, save and load: every kind of machine state survives, copies share nothing mutable, the saved
format is the C# port's (a zip saved by either loads in the other), and loading treats the file as hostile."""
import io
import json
import sys
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402
from topwater.snapshot import FORMAT_VERSION, ShellSnapshot, SnapshotError, SnapshotVersionError  # noqa: E402

FIXTURES = Path(__file__).resolve().parents[2] / "tests" / "Topwater.Tests" / "snapshots"
BINARY = bytes([0, 1, 2, 0x7F, 0x80, 0xFE, 0xFF, 10, 0xC3])


def setup() -> Shell:
    """A shell holding one of everything a snapshot captures."""
    sh = Shell()
    sh.fs.write_bytes("/home/agent/blob.bin", BINARY)
    sh.fs.mkdir("/data", parents=True)
    sh.fs.write_text("/data/report.txt", "héllo wörld\nline 2\n")
    sh.fs.set_owner("/data", "bob", recursive=True)
    r = sh.run("""
mkdir -p proj/src && cd proj
printf 'hello\\n' > a.txt; chmod 600 a.txt; printf 'x' > src/run.sh; chmod 755 src/run.sh
echo one >> log.txt; echo two >> log.txt
touch $'odd\\nname' 'sp ace'
declare -i n=5; readonly R=fixed; export EXP=exported; declare -l low=ABC; declare -u up=abc
target=val; declare -n ref=target; plain='multi
line'
arr=(one 'two words' three); unset 'arr[1]'; arr+=(four)
declare -A map=([k1]=v1 ['key two']=v2)
greet() { echo "hi $1 from $(pwd)"; }; export -f greet
count() { local i; for ((i=0; i<$1; i++)); do printf '%s,' $i; done; echo; }
alias ll='ls -l' gs='git status'
set -- p1 'p 2' p3
shopt -s nullglob globstar
trap ':' ERR; trap 'echo int' INT
pushd /tmp >/dev/null; pushd /home/agent/proj >/dev/null
false
""")
    assert r.exit_code == 1, r.to_tool_output()
    return sh


PROBE = """
echo "status=$? args=$# [$1] [$2] [$3] pwd=$PWD"
declare -p n R EXP low up ref target plain arr map
n+=2; echo "n=$n ref=$ref"; up=xyz; echo "up=$up"
greet bob; count 3; bash -c 'echo "child sees EXP=$EXP"; greet child'
declare -f count
alias; dirs; shopt nullglob globstar dotglob; trap -p ERR INT
echo "${!map[@]}" "${map[key two]}" "${!arr[@]}"; arr+=(five); echo "${!arr[@]}"
ls -l a.txt src/run.sh | cut -c1-10; cat log.txt; ls /home/agent/proj
R=changed
"""


def files(sh: Shell) -> list:
    """Every path's metadata and exact bytes, as the host sees them."""
    out = []

    def walk(path):
        st = sh.fs.stat(path)
        out.append((path, st.is_dir, st.size, st.mode, st.owner, st.modified, None if st.is_dir else sh.fs.read_bytes(path)))
        if st.is_dir:
            for name in sh.fs.listdir(path):
                walk(("" if path == "/" else path) + "/" + name)

    walk("/")
    return out


def mess(sh: Shell) -> None:
    """Changes every kind of state, so a restore has something to undo."""
    sh.fs.write_bytes("/home/agent/blob.bin", b"\x09\x09")
    sh.fs.chmod("/data/report.txt", 0)
    sh.run("""cd /; rm -rf /home/agent/proj; echo new > /tmp/new.txt; echo three >> /tmp/log.txt
n=99; unset low target; unalias ll; unset -f greet; arr[0]=CHANGED; map[k1]=CHANGED; map[k3]=new
set --; shopt -u nullglob; trap - ERR INT; popd; popd; alias extra=x""")


def save(sh) -> bytes:
    b = io.BytesIO()
    sh.save(b)
    return b.getvalue()


class ForwardOnly(io.RawIOBase):
    """A stream that can only be read forward, like a pipe."""

    def __init__(self, data: bytes):
        self._b = io.BytesIO(data)

    def readable(self):
        return True

    def readinto(self, buf):
        n = self._b.readinto(buf)
        return n


class SnapshotTests(unittest.TestCase):
    def test_a_file_older_than_year_1000_survives_save_and_load(self):
        from datetime import datetime
        sh = Shell()
        sh.fs.write_text("/home/agent/old.txt", "x")
        sh.fs.set_modified_time("/home/agent/old.txt", datetime(512, 4, 26))
        buf = io.BytesIO()
        sh.save(buf)
        back = Shell.load(buf.getvalue())
        self.assertEqual(back.fs.stat("/home/agent/old.txt").modified.year, 512)

    def test_every_kind_of_state_survives(self):
        expected = setup().run(PROBE).to_tool_output()  # a twin, so probing doesn't touch the shell under test
        for how in ("restore", "restore twice", "fork", "save and load", "forward-only stream", "configured shell", "path"):
            with self.subTest(how):
                sh = setup()
                before, history, env = files(sh), list(sh.history), dict(sh.env)
                if how == "restore":
                    snap = sh.snapshot()
                    mess(sh)
                    sh.restore(snap)
                    copy = sh
                elif how == "restore twice":
                    snap = sh.snapshot()
                    sh.restore(snap)
                    mess(sh)
                    sh.restore(snap)
                    copy = sh
                elif how == "fork":
                    copy = sh.fork()
                    mess(sh)
                elif how == "save and load":
                    copy = Shell.load(save(sh))
                elif how == "forward-only stream":
                    copy = Shell.load(ForwardOnly(save(sh)))
                elif how == "configured shell":
                    copy = Shell()
                    copy.restore(ShellSnapshot.load(io.BytesIO(save(sh)), copy))
                else:
                    import tempfile
                    with tempfile.TemporaryDirectory() as d:
                        sh.save(d + "/m.zip")
                        copy = Shell.load(d + "/m.zip")
                self.assertEqual(before, files(copy))
                self.assertEqual(history, copy.history)
                self.assertEqual(env, copy.env)
                self.assertEqual(1, copy.last_exit_code)
                self.assertEqual("/home/agent/proj", copy.cwd)
                probe = copy.run(PROBE).to_tool_output()
                self.assertEqual(expected, probe)
                for part in ("status=1 args=3 [p1] [p 2] [p3] pwd=/home/agent/proj", 'declare -i n="5"', 'declare -r R="fixed"',
                             'declare -x EXP="exported"', 'declare -l low="abc"', 'declare -n ref="target"',
                             'declare -a arr=([0]="one" [2]="three" [3]="four")', "n=7 ref=val", "up=XYZ", "R: readonly variable",
                             "hi bob from /home/agent/proj", "0,1,2,", "child sees EXP=exported", "hi child from",
                             "alias gs='git status'", "alias ll='ls -l'", "~/proj /tmp", "trap -- ':' ERR", "trap -- 'echo int' INT",
                             "key two", "v2", "0 2 3 4", "-rw-------", "-rwxr-xr-x", "one\ntwo\n"):
                    self.assertIn(part, probe)
                self.assertEqual(BINARY, copy.fs.read_bytes("/home/agent/blob.bin"))
                self.assertEqual("bob", copy.fs.stat("/data/report.txt").owner)
                self.assertIn("odd\nname", copy.fs.listdir("/home/agent/proj"))

    def test_a_snapshot_never_sees_later_changes(self):
        sh = Shell()
        sh.run("a=(1 2 3); declare -A m=([k]=v); s=before; echo one >> f.txt; printf 'bin\\x00\\x01' > b.bin")
        snap = sh.snapshot()
        sh.run("a[0]=X; a+=(4); m[k]=X; s=after; echo two >> f.txt; printf 'zz' > b.bin; chmod 000 f.txt; mkdir d; cd d")
        sh.fs.write_bytes("/home/agent/b.bin", b"\x07")
        later = sh.fork()
        sh.restore(snap)
        self.assertEqual("1 2 3|v|before|one|/home/agent\n", sh.run('echo "${a[*]}|${m[k]}|$s|$(cat f.txt)|$PWD"').stdout)
        self.assertEqual(b"bin\x00\x01", sh.fs.read_bytes("/home/agent/b.bin"))
        self.assertEqual("-rw-r--r--", sh.run("ls -l f.txt | cut -c1-10").stdout.strip())
        sh.run("a[1]=Y; echo three >> f.txt; rm b.bin")
        sh.restore(snap)
        self.assertEqual("1 2 3|one\n", sh.run('echo "${a[*]}|$(cat f.txt)"').stdout)
        self.assertTrue(sh.fs.exists("/home/agent/b.bin"))
        self.assertEqual("X 2 3 4|after\n", later.run('echo "${a[*]}|$s"').stdout)

    def test_a_fork_and_its_parent_share_nothing_mutable(self):
        parent = Shell()
        parent.run("a=(1 2); declare -A m=([k]=v); x=1; f() { echo f1; }; alias q=echo; echo base > file; set -- p; pushd /tmp >/dev/null")
        fork = parent.fork()
        parent.run("a[0]=P; m[k]=P; x=P; f() { echo fP; }; unalias q; echo P >> ~/file; set -- P; popd; mkdir ~/pd; chmod 700 ~/file")
        self.assertEqual("1 2|v|1|f1|base|p|/tmp ~|no pd|-rw-r--r--\n", fork.run(
            'echo "${a[*]}|${m[k]}|$x|$(f)|$(cat ~/file)|$1|$(dirs)|$( [ -e ~/pd ] && echo pd || echo no pd)|$(ls -l ~/file | cut -c1-10)"').stdout)
        fork.run("a[1]=F; m[n]=F; y=F; echo F > ~/file; cd /; f() { echo fF; }")
        self.assertEqual("P 2|P||fP\n", parent.run('echo "${a[*]}|${m[k]}${m[n]}|$y|$(f)"').stdout)
        self.assertEqual("base\nP\n", parent.fs.read_text("/home/agent/file"))
        self.assertEqual("F\n", fork.fs.read_text("/home/agent/file"))
        self.assertEqual(("/home/agent", "/"), (parent.cwd, fork.cwd))
        self.assertIsNot(parent.fs, fork.fs)

    def test_fork_shares_registered_handlers(self):
        calls = []
        parent = Shell().register_command("tick", lambda ctx: calls.append(1) or 0)
        fork = parent.fork()
        parent.run("tick")
        fork.run("tick")
        self.assertEqual(2, len(calls))
        self.assertTrue(fork.fs.is_file("/usr/bin/tick"))

    def test_restore_keeps_this_shells_registrations(self):
        def say(text):
            return lambda ctx: ctx.stdout.write(text + "\n") and 0

        other = Shell().register_command("theirs", say("theirs"))
        other.run("echo data > d.txt")
        mine = Shell().register_command("mine", say("mine ran"))
        mine.restore(other.snapshot())
        self.assertEqual("data\nmine ran\n", mine.run("cat d.txt; mine").stdout)
        self.assertEqual(127, mine.run("theirs").exit_code)
        self.assertTrue(mine.fs.is_file("/usr/bin/mine"))
        self.assertFalse(mine.fs.exists("/usr/bin/theirs"))
        self.assertTrue(mine.fs.is_file("/usr/bin/ls"))

    def test_load_gives_a_shell_without_host_registrations(self):
        sh = Shell().register_command("orders", lambda ctx: ctx.stdout.write("3 orders\n") and 0)
        loaded = Shell.load(save(sh))
        self.assertEqual(127, loaded.run("orders").exit_code)
        self.assertFalse(loaded.fs.exists("/usr/bin/orders"))

    def test_load_uses_the_snapshots_user_and_hostname(self):
        sh = Shell("dev", "box")
        sh.run("echo hi > note")
        snap = ShellSnapshot.load(save(sh))
        self.assertEqual(("dev", "box"), (snap.user, snap.hostname))
        self.assertEqual("dev@box /home/dev hi\n", Shell.load(save(sh)).run('echo "$(whoami)@$(hostname) $PWD $(cat note)"').stdout)

    def test_the_saved_format_is_a_zip_with_a_versioned_manifest_and_deduplicated_blobs(self):
        sh = Shell()
        sh.fs.write_text("/home/agent/a", "same")
        sh.fs.write_text("/home/agent/b", "same")
        for link in ("/etc/os-release", "/var/run", "/var/lock", "/proc/self", "/usr/local/man"):
            sh.fs.delete(link)  # the default machine's links
        zf = zipfile.ZipFile(io.BytesIO(save(sh)))
        m = json.loads(zf.read("manifest.json"))
        self.assertEqual(("topwater-snapshot", 1), (m["format"], m["version"]))  # no links: written as version 1, which older Topwater reads
        self.assertEqual(3, FORMAT_VERSION)
        fs = {f["path"]: f for f in m["files"]}
        a = fs["/home/agent/a"]
        self.assertEqual(("0644", "agent"), (a["mode"], a["owner"]))
        self.assertRegex(a["mtime"], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}Z$")
        self.assertEqual(a["blob"], fs["/home/agent/b"]["blob"])
        self.assertEqual("/home/agent", m["cwd"])
        self.assertTrue(any(v["name"] == "HOME" and v.get("exported") for v in m["variables"]))
        self.assertLess(sum(n.startswith("blobs/") for n in zf.namelist()), len(m["files"]) / 2)

    # ---------- across ports ----------

    SETUP = """mkdir -p proj/src && cd proj
printf 'hello\\n' > a.txt; chmod 600 a.txt; echo AAH+/wo= | base64 -d > bin.dat; echo 'héllo wörld' > u.txt
declare -i n=5; export EXP='exp "q" $x'; plain=$'multi\\nline'
arr=(one 'two words' three); unset 'arr[1]'; declare -A map=([k1]=v1 ['key two']=v2)
greet() { echo "hi $1"; }; export -f greet; alias ll='ls -l'; set -- p1 'p 2'; shopt -s nullglob; trap 'echo int' INT
echo setup done
"""
    CROSS_PROBE = ("echo \"$PWD $n $EXP [$plain] ${!arr[@]} ${arr[2]} ${map[key two]} $1|$2\"; greet you; alias ll; shopt nullglob; "
                   "trap -p INT; ls -l a.txt | cut -c1-10; od -An -tx1 bin.dat; cat u.txt; bash -c 'greet child'; declare -p n arr; whoami")
    CROSS_EXPECTED = ("/home/agent/proj 5 exp \"q\" $x [multi\nline] 0 2 three v2 p1|p 2\nhi you\nalias ll='ls -l'\nnullglob       \ton\n"
                      "trap -- 'echo int' INT\n-rw-------\n 00 01 fe ff 0a\nhéllo wörld\nhi child\ndeclare -i n=\"5\"\n"
                      "declare -a arr=([0]=\"one\" [2]=\"three\")\nagent\n")

    def test_a_snapshot_saved_by_csharp_loads(self):
        # tests/Topwater.Tests/snapshots/from-csharp.zip: Topwater.Cli run --state from-csharp.zip "<SETUP>"
        sh = Shell.load(FIXTURES / "from-csharp.zip")
        self.assertEqual(self.CROSS_EXPECTED, sh.run(self.CROSS_PROBE).stdout)

    def test_a_snapshot_with_links_saved_by_csharp_loads(self):
        # tests/Topwater.Tests/snapshots/links-from-csharp.zip (format version 2); see ASnapshotWithLinksLoads in SnapshotTests.cs
        sh = Shell.load(FIXTURES / "links-from-csharp.zip")
        r = sh.run("pwd; readlink l etc dang s/self ../loop1 sl2; cat l s/self/self/../f; stat -c '%h %F' f h sl2; echo more >> h; cat f; cat ../loop1 2>&1")
        self.assertEqual("/home/agent/d\nf\n/etc\nnowhere\n.\nloop2\nf\nhi\nhi\n2 regular file\n2 regular file\n2 symbolic link\nhi\nmore\n"
                         "cat: ../loop1: Too many levels of symbolic links\n", r.stdout)

    def test_python_saves_what_the_csharp_fixture_holds(self):
        # from-python.zip, which the C# tests load, is this shell saved; the same probe gives the same answer here
        sh = Shell()
        self.assertEqual("setup done\n", sh.run(self.SETUP).stdout)
        loaded = Shell.load(save(sh))
        self.assertEqual(self.CROSS_EXPECTED, loaded.run(self.CROSS_PROBE).stdout)
        self.assertEqual(self.CROSS_EXPECTED, Shell.load(FIXTURES / "from-python.zip").run(self.CROSS_PROBE).stdout)

    # escaped bytes (no UTF-8) in a variable, an alias, a file name and $_, and valid UTF-8 made from escapes; snapshots/bytes-from-*.zip
    # are this setup saved by each port (TOPWATER_WRITE_FIXTURES=1 writes this port's again)
    BYTES_SETUP = r"""x=$(printf 'a\xffb'); OK=$'\xe2\x9c\x93'; alias raw=$'echo \xfe'; touch $'f\xfd.txt' $'caf\xc3\xa9.txt'; echo $'last\xfc'"""
    BYTES_CHECK = r"""printf %s "$_" | od -An -tx1; printf %s "$x" | od -An -tx1; echo "$OK"; ls *.txt | od -An -tx1; alias raw | od -An -tx1"""

    def test_raw_bytes_in_the_session_survive_every_ports_snapshot(self):
        import os
        fresh = Shell()
        fresh.run(self.BYTES_SETUP)
        if os.environ.get("TOPWATER_WRITE_FIXTURES"):
            (FIXTURES / "bytes-from-python.zip").write_bytes(save(fresh))
        mine = save(fresh)
        want = fresh.run(self.BYTES_CHECK).stdout
        for part in (" 6c 61 73 74 fc\n", " 61 ff 62\n", "✓\n", " fd ", " c3 a9 ", " fe "):
            self.assertIn(part, want)
        self.assertEqual(want, Shell.load(mine).run(self.BYTES_CHECK).stdout)
        for port in ("python", "csharp", "typescript"):
            with self.subTest(port):
                self.assertEqual(want, Shell.load(FIXTURES / f"bytes-from-{port}.zip").run(self.BYTES_CHECK).stdout)

    # ---------- untrusted input ----------

    @staticmethod
    def tamper(sh: Shell, edit, zip_edit=None) -> bytes:
        src = zipfile.ZipFile(io.BytesIO(save(sh)))
        m = json.loads(src.read("manifest.json"))
        edit(m)
        entries = {n: src.read(n) for n in src.namelist() if n != "manifest.json"}
        if zip_edit:
            zip_edit(m, entries)
        out = io.BytesIO()
        with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
            for n, b in entries.items():
                z.writestr(n, b)
            z.writestr("manifest.json", json.dumps(m))
        return out.getvalue()

    def rejected(self, data: bytes, limits=None) -> str:
        with self.assertRaises(SnapshotError) as e:
            ShellSnapshot.load(data, limits)
        self.assertTrue(str(e.exception).startswith("not a valid Topwater snapshot: "), str(e.exception))
        return str(e.exception)

    @staticmethod
    def entry(m, path):
        return next(f for f in m["files"] if f["path"] == path)

    def test_a_newer_format_version_is_refused_clearly(self):
        def edit(m):
            m["version"], m["files"] = 4, "changed shape"
        with self.assertRaises(SnapshotVersionError) as e:
            ShellSnapshot.load(self.tamper(Shell(), edit))
        self.assertEqual("snapshot format version 4 is newer than this version of Topwater supports (3); upgrade Topwater to load it",
                         str(e.exception))

    def test_not_a_snapshot_is_refused(self):
        self.rejected(b"hello, not a zip")
        self.assertIn("not a Topwater snapshot manifest", self.rejected(self.tamper(Shell(), lambda m: m.update(format="something-else"))))
        empty = io.BytesIO()
        zipfile.ZipFile(empty, "w").close()
        self.assertIn("manifest.json is missing", self.rejected(empty.getvalue()))

    def test_path_tricks_are_refused(self):
        sh = Shell()
        sh.fs.write_text("/home/agent/x", "x")
        for path in ("/home/agent/../../etc/evil", "home/agent/relative", "/home//agent/x", "/home/agent/./x", "/home/agent/x/",
                     "/home/agent/nul\0byte", "", None, 7):
            with self.subTest(path):
                self.assertIn("bad path", self.rejected(self.tamper(sh, lambda m: self.entry(m, "/home/agent/x").update(path=path))))

    def test_entries_must_be_well_formed(self):
        sh = Shell()
        sh.fs.write_text("/home/agent/x", "content")

        def file(**kv):
            return self.tamper(sh, lambda m: self.entry(m, "/home/agent/x").update(**kv))

        self.assertIn('unknown type "socket"', self.rejected(file(type="socket")))
        self.assertIn("bad symlink target", self.rejected(file(type="symlink")))
        self.assertIn("bad mode", self.rejected(file(mode="9999")))
        self.assertIn("owner", self.rejected(file(owner="../root")))
        self.assertIn("mtime", self.rejected(file(mtime="yesterday")))
        self.assertIn("size", self.rejected(file(size="7")))
        self.assertIn("File exists", self.rejected(self.tamper(sh, lambda m: m["files"].append(dict(self.entry(m, "/home/agent/x"))))))
        self.assertIn("parent is not a directory", self.rejected(file(path="/nowhere/x")))
        self.assertIn("bad user", self.rejected(self.tamper(sh, lambda m: m.update(user="../../etc"))))
        self.assertIn("bad hostname", self.rejected(self.tamper(sh, lambda m: m.update(hostname="a\nb"))))
        self.assertIn("bad cwd", self.rejected(self.tamper(sh, lambda m: m.update(cwd="../up"))))
        self.assertIn("bad variable name", self.rejected(self.tamper(sh, lambda m: m["variables"].append({"name": "a;b", "value": "x"}))))
        self.assertIn("bad index", self.rejected(self.tamper(sh, lambda m: m["variables"].append({"name": "a", "array": {"items": [["-1", "x"]]}}))))
        self.assertIn("unknown set option", self.rejected(self.tamper(sh, lambda m: m.update(set=["restricted"]))))
        self.assertIn("exit codes", self.rejected(self.tamper(sh, lambda m: m.update(lastExitCode=300))))
        self.assertIn("bad alias", self.rejected(self.tamper(sh, lambda m: m.update(aliases={"a b": "x"}))))

    def test_null_booleans_and_other_digits_are_refused(self):
        sh = Shell()
        for getopts in ([None, 1], [True, 1], [0, False], [0, -1]):
            with self.subTest(getopts=getopts):
                self.assertIn("bad getopts", self.rejected(self.tamper(sh, lambda m: m.update(getopts=getopts))))
        for job in ([True, 0], [None, 0], [1, True]):
            with self.subTest(job=job):
                self.assertIn("bad job", self.rejected(self.tamper(sh, lambda m: m.update(jobs=[job]))))
        # \d would take Arabic-Indic and other digits; a time is ASCII digits only
        self.assertIn("bad created", self.rejected(self.tamper(sh, lambda m: m.update(created="2026-11-25T09:00:0٣Z"))))
        self.assertIn("bad secondsBase", self.rejected(self.tamper(sh, lambda m: m.update(secondsBase="٢026-11-25T09:00:00Z"))))

    def test_links_survive_save_load_fork_and_restore(self):
        sh = Shell()
        sh.run("mkdir -p d/s; echo hi > d/f; ln -s f d/l; ln -s /etc d/etc; ln -s nowhere d/dang; ln -s . d/s/self; ln -s loop2 loop1; "
               "ln -s loop1 loop2; ln d/f d/h; ln -s f d/sl; ln d/sl d/sl2")
        data = save(sh)
        m = json.loads(zipfile.ZipFile(io.BytesIO(data)).read("manifest.json"))
        self.assertEqual(2, m["version"])
        self.assertEqual(("symlink", "f"), (self.entry(m, "/home/agent/d/l")["type"], self.entry(m, "/home/agent/d/l")["target"]))
        self.assertEqual("/home/agent/d/f", self.entry(m, "/home/agent/d/h")["link"])
        probe = ("readlink d/l d/etc d/dang loop1; cat d/l d/s/self/self/../f; stat -c '%h %F' d/f d/h d/sl2; echo more >> d/h; cat d/f; "
                 "cat loop1 2>&1")
        want = ("f\n/etc\nnowhere\nloop2\nhi\nhi\n2 regular file\n2 regular file\n2 symbolic link\nhi\nmore\n"
                "cat: loop1: Too many levels of symbolic links\n")
        for copy in (Shell.load(data), sh.fork()):
            self.assertEqual(want, copy.run(probe).stdout)
        snap = sh.snapshot()
        sh.run("rm -r d loop1")
        sh.restore(snap)
        self.assertEqual(want, sh.run(probe).stdout)

    def test_links_in_a_snapshot_are_checked(self):
        sh = Shell()
        sh.fs.mkdir("/home/agent/d")
        sh.fs.create_symlink("/home/agent/l", "/etc")  # before x: entries come in creation order, so x is the later one
        sh.fs.write_text("/home/agent/x", "x")

        def edit(which, **kv):
            return self.tamper(sh, lambda m: self.entry(m, which).update(**kv))

        # an entry can't be put through a link (it would land in /etc), nor be a hard link to a directory, a later or a missing entry
        self.assertIn("parent is not a directory", self.rejected(edit("/home/agent/x", path="/home/agent/l/x")))
        self.assertIn("not an earlier file", self.rejected(edit("/home/agent/x", link="/home/agent/d")))
        self.assertIn("not an earlier file", self.rejected(edit("/home/agent/x", link="/home/agent/missing")))
        self.assertIn("not an earlier file", self.rejected(edit("/home/agent/l", link="/home/agent/x")))
        self.assertIn("bad link", self.rejected(edit("/home/agent/x", link="../etc/passwd")))
        self.assertIn("bad symlink target", self.rejected(edit("/home/agent/l", target="a\0b")))
        self.assertIn("bad symlink target", self.rejected(edit("/home/agent/l", target="a" * 4097)))
        # a link to anywhere, even a loop, loads: it is only a path, resolved later with the agent's permissions
        self.assertEqual("l", Shell.load(edit("/home/agent/l", target="l")).fs.read_link("/home/agent/l"))

    def test_blobs_must_match_their_size_and_hash(self):
        sh = Shell()
        sh.fs.write_text("/home/agent/x", "content")

        def replace(data):
            def z(m, entries):
                entries["blobs/" + self.entry(m, "/home/agent/x")["blob"]] = data
            return self.tamper(sh, lambda m: None, z)

        self.assertIn("doesn't match its hash", self.rejected(replace(b"CONTENT")))
        # a blob far bigger than its entry claims (a zip bomb) stops at the claimed size
        self.assertIn("is larger than 7 bytes", self.rejected(replace(bytes(10 << 20))))
        self.assertIn("is missing", self.rejected(self.tamper(sh, lambda m: self.entry(m, "/home/agent/x").update(blob="0" * 64))))
        self.assertIn("bad /home/agent/x: blob", self.rejected(self.tamper(sh, lambda m: self.entry(m, "/home/agent/x").update(blob="../../etc/passwd"))))

    def test_functions_must_be_one_definition(self):
        def add(name, body):
            return self.tamper(Shell(), lambda m: m.setdefault("functions", []).append({"name": name, "body": body}))

        self.assertIn("not one function definition", self.rejected(add("f", "{ :; }; rm -rf /")))
        self.assertIn("not one function definition", self.rejected(add("f; rm -rf /; g", "{ :; }")))
        self.assertIn("function f:", self.rejected(add("f", "{ unterminated")))

    def test_snapshots_over_the_limits_are_refused(self):
        from types import SimpleNamespace as L
        sh = Shell()
        sh.fs.write_text("/home/agent/big", "x" * 100_000)
        sh.run("v=$(printf '%0500d' 0)")
        data = save(sh)
        self.assertIn("MaxFileSystemBytes", self.rejected(data, L(max_file_system_bytes=50_000)))
        self.assertIn("MaxExtractEntries", self.rejected(data, L(max_extract_entries=100)))
        self.assertIn("MaxStringBytes", self.rejected(data, L(max_string_bytes=400)))
        # a snapshot larger than any it could hold is refused before it is read, from any kind of source
        tiny = L(max_file_system_bytes=100, max_string_bytes=100, max_extract_entries=1)
        self.assertIn("larger than", self.rejected(data, tiny))
        with self.assertRaises(SnapshotError):
            ShellSnapshot.load(ForwardOnly(data), tiny)
        # within the limits it loads, and the loaded shell enforces them as usual
        loaded = Shell.load(data, max_file_system_bytes=200_000)
        self.assertEqual(500, len(loaded.env["v"]))
        self.assertIn("No space left on device", loaded.run("cat big big > more").to_tool_output())

    def test_the_agent_cannot_reach_snapshots_or_containers(self):
        sh = Shell()
        for name in ("snapshot", "restore", "fork", "save", "load", "docker", "topwater"):
            self.assertNotIn(name, sh.commands)
        self.assertEqual(127, sh.run("docker build -t x .").exit_code)


if __name__ == "__main__":
    unittest.main()
