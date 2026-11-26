"""Snapshot to Docker build context and rootfs tar to snapshot. Most tests need no docker; the round trip at the end
runs against a real daemon and is skipped where there is none."""
import io
import re
import subprocess
import sys
import tarfile
import time
import unittest
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402
from topwater import containers  # noqa: E402
from topwater.containers import (ContainerConfig, ImportOptions, InvalidContainerData, build_image, docker_available,  # noqa: E402
                                 from_container, from_image, from_tar, to_build_context)


def entry(name, data=None, kind=tarfile.REGTYPE, uid=1000, mode=0o644, link=None):
    return (name, data, kind, uid, mode, link)


def F(name, text, uid=1000):
    return entry(name, text.encode(), uid=uid)


def D(name, uid=1000):
    return entry(name.rstrip("/") + "/", None, tarfile.DIRTYPE, uid, 0o755)


def L(name, target):
    return entry(name, None, tarfile.SYMTYPE, link=target)


def make_tar(*entries) -> io.BytesIO:
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.PAX_FORMAT) as t:
        for name, data, kind, uid, mode, link in entries:
            info = tarfile.TarInfo(name)
            info.type, info.uid, info.mode, info.mtime = kind, uid, mode, 1700000000
            if link:
                info.linkname = link
            info.size = len(data) if data is not None else 0
            t.addfile(info, io.BytesIO(data) if data is not None else None)
    buf.seek(0)
    return buf


def read_layer(layer: bytes) -> dict:
    """name -> (TarInfo, data)"""
    out = {}
    with tarfile.open(fileobj=io.BytesIO(layer)) as t:
        for m in t.getmembers():
            out[m.name.rstrip("/")] = (m, t.extractfile(m).read() if m.isreg() else None)
    return out


def imported(tar, config=None, changes=(), options=None):
    return from_tar(tar, config or ContainerConfig("agent", "/home/agent"), changes, options)


def restored(r) -> Shell:
    sh = Shell(r.snapshot.user)
    sh.restore(r.snapshot)
    return sh


def whats(report):
    return [i.what for i in report.not_carried]


class ExportTests(unittest.TestCase):
    def test_fresh_machine_layer_holds_only_bookkeeping(self):
        ctx = to_build_context(Shell().snapshot())
        self.assertEqual(["etc/topwater", "etc/topwater/paths", "etc/topwater/session.sh"], sorted(read_layer(ctx.layer)))

    def test_links_are_exported_as_links(self):
        # symlinks as symlinks, other names of a file as hard links to its first
        sh = Shell()
        sh.run("cd; echo hi > a; ln -s a l; ln -s /etc/shadow s; ln a h")
        layer = read_layer(to_build_context(sh.snapshot()).layer)
        self.assertEqual((tarfile.SYMTYPE, "a"), (layer["home/agent/l"][0].type, layer["home/agent/l"][0].linkname))
        self.assertEqual((tarfile.SYMTYPE, "/etc/shadow"), (layer["home/agent/s"][0].type, layer["home/agent/s"][0].linkname))
        self.assertEqual((tarfile.LNKTYPE, "home/agent/a"), (layer["home/agent/h"][0].type, layer["home/agent/h"][0].linkname))

    def test_line_breaks_cannot_inject_dockerfile_instructions(self):
        # the agent chooses its working directory and variables; a line break in either must not add a Dockerfile instruction
        sh = Shell()
        sh.run("mkdir -p $'/tmp/x\\nRUN touch /pwned' && cd $'/tmp/x\\nRUN touch /pwned'; export EVIL=$'a\\nRUN touch /pwned2'; "
               "export $'CR=b\\rRUN touch /pwned3'; export OK=fine")
        ctx = to_build_context(sh.snapshot())
        lines = re.split(r"[\r\n]", ctx.dockerfile)
        self.assertEqual(1, sum(line.startswith("RUN") for line in lines))  # only the generator's own setup step
        self.assertFalse(any("pwned" in line for line in lines))
        self.assertIn('ENV OK="fine"', lines)  # ordinary variables still carry over
        self.assertIn("working directory", whats(ctx.report))
        self.assertFalse(any("EVIL" in line for line in lines))  # a multi-line value goes to the quoted session script
        self.assertIn("EVIL=$'a\\nRUN touch /pwned2'", ctx.session_script)

    def test_hostile_names_never_reach_the_dockerfile_or_setup_script(self):
        sh = Shell()
        sh.fs.write_text("/etc/passwd", sh.fs.read_text("/etc/passwd") + "ev'il;touch /x:x:1500:1500::/home/evil:/bin/bash\n")
        ctx = to_build_context(sh.snapshot())
        self.assertNotIn("ev'il;touch", ctx.setup_script.replace("'\\''", ""))  # quoted, not raw
        self.assertIn("keep='|root|agent|ev'\\''il;touch /x|", ctx.setup_script)
        with self.assertRaises(ValueError):
            to_build_context(sh.snapshot(), "ubuntu:24.04\nRUN touch /pwned")

    def test_names_starting_with_a_dash_never_reach_useradd_or_groupadd(self):
        # useradd and groupadd would read them as options; they are reported and left out
        sh = Shell()
        sh.fs.write_text("/etc/passwd", sh.fs.read_text("/etc/passwd") + "-ou:x:1500:1500::/home/x:/bin/bash\n")
        sh.fs.write_text("/etc/group", sh.fs.read_text("/etc/group") + "-og:x:1501:\n")
        sh.fs.write_text("/srv/f", "x")
        sh.fs.set_owner("/srv/f", "-oo")
        ctx = to_build_context(sh.snapshot())
        self.assertFalse(re.search(r"(?m)^.*(-ou|-og|-oo).*$", ctx.setup_script))
        self.assertTrue({"account -ou", "account -og", "owner -oo"} <= set(whats(ctx.report)))
        self.assertEqual(0, read_layer(ctx.layer)["srv/f"][0].uid)
        for user in ("-x", "0"):
            with self.subTest(user), self.assertRaises(ValueError):
                to_build_context(Shell(user).snapshot())

    def test_passwd_fields_and_working_directory_stay_data(self):
        # home, shell and comment go to useradd as quoted words; the working directory to WORKDIR escaped
        sh = Shell()
        sh.fs.write_text("/etc/passwd", sh.fs.read_text("/etc/passwd") + "eve:x:1500:1500:$(touch /p1):/home/e;touch /p2:/bin/sh `touch /p3`\n")
        sh.run("""mkdir -p '/tmp/$(x) "y"' && cd '/tmp/$(x) "y"'""")
        ctx = to_build_context(sh.snapshot())
        self.assertIn("-d '/home/e;touch /p2' -s '/bin/sh `touch /p3`' -c '$(touch /p1)' eve\n", ctx.setup_script)
        self.assertIn('WORKDIR "/tmp/\\$(x) \\"y\\""\n', ctx.dockerfile)

    def test_simulated_paths_and_stubs_are_not_exported(self):
        sh = Shell()
        sh.run("echo x > /tmp/t; echo hi > ~/a.txt")
        names = list(read_layer(to_build_context(sh.snapshot()).layer))
        self.assertFalse([n for n in names if n.startswith(("proc", "dev", "usr/bin", "bin"))])
        self.assertIn("tmp/t", names)

    def test_new_file_keeps_mode_owner_and_mtime(self):
        sh = Shell()
        sh.run("echo hi > ~/a.txt; chmod 600 ~/a.txt")
        secs = int(sh.run("stat -c %Y ~/a.txt").stdout)
        layer = read_layer(to_build_context(sh.snapshot()).layer)
        m, data = layer["home/agent/a.txt"]
        self.assertEqual((0o600, 1000, 1000, "agent", secs, b"hi\n"), (m.mode, m.uid, m.gid, m.uname, m.mtime, data))
        self.assertTrue(layer["home/agent"][0].isdir())

    def test_registered_command_is_reported(self):
        sh = Shell().register_command("mytool", lambda ctx: 0)
        ctx = to_build_context(sh.snapshot())
        self.assertNotIn("usr/bin/mytool", read_layer(ctx.layer))
        self.assertTrue(any(i.what == "registered commands" and "mytool" in i.why for i in ctx.report.not_carried))

    def test_deleted_default_file_is_removed_in_setup(self):
        sh = Shell()
        sh.run("rm ~/.profile")
        ctx = to_build_context(sh.snapshot())
        self.assertIn("/home/agent/.profile", ctx.report.deleted)
        self.assertRegex(ctx.setup_script, r"rm -rf -- .*/home/agent/\.profile")

    def test_account_and_docker_managed_files_are_not_copied(self):
        sh = Shell()
        sh.fs.write_text("/etc/passwd", "root:x:0:0::/root:/bin/bash\n")
        sh.fs.write_text("/etc/hostname", "other\n")
        ctx = to_build_context(sh.snapshot())
        names = read_layer(ctx.layer)
        self.assertNotIn("etc/passwd", names)
        self.assertNotIn("etc/hostname", names)
        self.assertIn("/etc/passwd", whats(ctx.report))
        self.assertIn("/etc/hostname", whats(ctx.report))

    def test_bin_file_is_merged_into_usr_bin(self):
        sh = Shell()
        sh.fs.write_text("/bin/x", "#!/bin/sh\n")
        layer = read_layer(to_build_context(sh.snapshot()).layer)
        self.assertIn("usr/bin/x", layer)
        self.assertNotIn("bin/x", layer)

    def test_unknown_owner_gets_an_account(self):
        sh = Shell()
        sh.fs.write_text("/srv/f", "x")
        sh.fs.set_owner("/srv/f", "bob")
        ctx = to_build_context(sh.snapshot())
        self.assertEqual(1001, read_layer(ctx.layer)["srv/f"][0].uid)
        self.assertRegex(ctx.setup_script, r"useradd .*-u 1001 .*bob")
        self.assertIn("owner bob", whats(ctx.report))

    def test_dockerfile_has_base_env_workdir_user(self):
        sh = Shell("dev")
        sh.run("""export EXP='a $b "c"'; mkdir -p /tmp/w; cd /tmp/w""")
        df = to_build_context(sh.snapshot()).dockerfile
        self.assertIn("FROM ubuntu:24.04\n", df)
        self.assertIn('ENV EXP="a \\$b \\"c\\""\n', df)
        self.assertNotIn("ENV PATH", df)
        self.assertIn('WORKDIR "/tmp/w"\n', df)
        self.assertTrue(df.endswith("USER dev\n"))
        self.assertIn("FROM debian:12\n", to_build_context(sh.snapshot(), "debian:12").dockerfile)

    def test_session_script_recreates_state(self):
        sh = Shell()
        r = sh.run("""plain=val; declare -i n=5; arr=(a b c d); unset 'arr[1]'
declare -A map=(['key two']=v2); nl=$'one\\ntwo'
greet() { echo "hi $1"; }; export -f greet
alias ll='ls -l'; shopt -s nullglob""")
        self.assertEqual(0, r.exit_code)
        ctx = to_build_context(sh.snapshot())
        self.assertIn("unalias -a\n", ctx.session_script)
        self.assertIn("$'one\\ntwo'", ctx.session_script)
        fresh = Shell()
        self.assertEqual(0, fresh.run(ctx.session_script).exit_code)
        probe = fresh.run("declare -p plain n arr map nl; type greet; alias ll; shopt nullglob").to_tool_output()
        for part in ('plain="val"', 'declare -i n="5"', '[0]="a"', '[3]="d"', "key two", "v2", "greet is a function", "alias ll='ls -l'", "nullglob"):
            self.assertIn(part, probe)
        self.assertNotIn("[1]=", probe)
        self.assertEqual("one\ntwo", fresh.run('printf %s "$nl"').stdout)

    def test_write_tar_is_a_plain_ustar_context(self):
        ctx = to_build_context(Shell().snapshot())
        buf = io.BytesIO()
        ctx.write_tar(buf)
        with tarfile.open(fileobj=io.BytesIO(buf.getvalue())) as t:
            self.assertEqual(["Dockerfile", "setup.sh", "layer.tar"], t.getnames())
            self.assertEqual(ctx.layer, t.extractfile("layer.tar").read())
        self.assertEqual(b"Dockerfile", buf.getvalue()[:10])  # the first header is the file itself, no PAX header


class ImportTests(unittest.TestCase):
    def test_imports_only_selected_paths(self):
        tar = make_tar(D("home/agent"), F("home/agent/x.txt", "one two\nthree\n"), F("home/agent/skip.txt", "no"), D("srv"), F("srv/s.txt", "s"), F("opt/o.txt", "o"))
        r = imported(tar, changes=["A /home/agent", "A /home/agent/x.txt"], options=ImportOptions(paths=("/srv",)))
        sh = restored(r)
        self.assertEqual("one two\nthree\n", sh.run("cat ~/x.txt").stdout)
        self.assertEqual(0, sh.run("test -f /srv/s.txt").exit_code)
        self.assertNotEqual(0, sh.run("test -e ~/skip.txt || test -e /opt/o.txt").exit_code)
        self.assertIn("ls", sh.run("ls /usr/bin").stdout)
        self.assertIn("/home/agent/x.txt", r.report.carried)

    def test_owners_map_through_passwd(self):
        tar = make_tar(F("etc/passwd", "carol:x:1234:1234::/home/carol:/bin/bash\n"), F("srv/a", "a", uid=1234), F("srv/b", "b", uid=4321))
        sh = restored(imported(tar, changes=["A /srv/a", "A /srv/b"]))
        self.assertEqual("carol\n4321\n", sh.run("stat -c '%U' /srv/a /srv/b").stdout)

    def test_config_sets_user_workdir_env(self):
        tar = make_tar(F("etc/passwd", "carol:x:1234:1234::/home/carol:/bin/bash\n"))
        r = imported(tar, ContainerConfig("1234", "/srv/w", ("FOO=bar", "1BAD=x")))
        self.assertEqual("carol", r.snapshot.user)
        self.assertEqual("/srv/w\nbar\n", restored(r).run("pwd; printenv FOO").stdout)
        self.assertTrue(any("1BAD" in w for w in whats(r.report)))

    def test_symlinks_come_back_as_links(self):
        # wherever they point (even nowhere); hard links as copies; an entry below a link (only a crafted archive has one) is reported
        tar = make_tar(D("srv"), F("srv/x", "data"), D("srv/d"), F("srv/d/f", "in dir"), L("srv/abs", "/srv/x"), L("srv/rel", "../srv/x"),
                       L("srv/dl", "/srv/d"), L("srv/gone", "/nowhere"), L("srv/etc", "/etc"), F("srv/etc/evil", "x"),
                       entry("srv/hard", None, tarfile.LNKTYPE, link="srv/x"))
        r = imported(tar, changes=[f"A /srv{p}" for p in ("", "/x", "/d", "/d/f", "/abs", "/rel", "/dl", "/gone", "/etc", "/etc/evil", "/hard")])
        sh = restored(r)
        self.assertEqual("datadatain dirdata", sh.run("cat /srv/abs /srv/rel /srv/dl/f /srv/hard").stdout)
        self.assertEqual("/srv/x ../srv/x /srv/d /nowhere /etc\n", sh.run("echo $(readlink /srv/abs /srv/rel /srv/dl /srv/gone /srv/etc)").stdout)
        self.assertFalse(sh.fs.exists("/etc/evil"))
        self.assertIn("/srv/etc/evil", whats(r.report))

    def test_elf_skipped_unless_included(self):
        elf = b"\x7fELF\x01\x02\x03"
        r = imported(make_tar(entry("srv/prog", elf)), changes=["A /srv/prog"])
        self.assertNotIn("/srv/prog", r.report.carried)
        self.assertIn("/srv/prog", whats(r.report))
        kept = imported(make_tar(entry("srv/prog", elf)), changes=["A /srv/prog"], options=ImportOptions(include_binaries=True))
        self.assertIn("/srv/prog", kept.report.carried)
        self.assertEqual(elf, restored(kept).fs.read_bytes("/srv/prog"))

    def test_deletes_and_keeps_invalid_utf8(self):
        raw = bytes([0xFF, 0xFE, ord("a"), 0xC3, 0x28, 0])
        r = imported(make_tar(entry("srv/raw", raw)), changes=["A /srv/raw", "D /home/agent/.profile"])
        self.assertIn("/home/agent/.profile", r.report.deleted)
        sh = restored(r)
        self.assertNotEqual(0, sh.run("test -e ~/.profile").exit_code)
        self.assertEqual(raw, sh.fs.read_bytes("/srv/raw"))

    def test_exported_layer_read_back_gives_the_machine_back(self):
        sh = Shell()
        sh.run("echo hi > ~/a.txt; echo '# mine' >> ~/.bashrc; greet() { echo \"hi $1\"; }; alias ll='ls -l'; x=1; mkdir -p /tmp/w")
        # a line break in a name can't add an entry to /etc/topwater/paths, and the file comes back
        sh.run("mkdir -p $'/tmp/x\\nD /home/agent'; touch $'/tmp/x\\nD /home/agent/.profile'; echo nl > $'/tmp/n\\nl'; cd /tmp/w")
        ctx = to_build_context(sh.snapshot())
        layer = read_layer(ctx.layer)
        changes = layer["etc/topwater/paths"][1].decode().split("\0")  # docker diff's format, NUL-terminated, which an import also reads from the image
        rootfs = io.BytesIO()
        with tarfile.open(fileobj=rootfs, mode="w", format=tarfile.PAX_FORMAT) as t:
            pw = b"root:x:0:0::/root:/bin/bash\nagent:x:1000:1000::/home/agent:/bin/bash\n"
            info = tarfile.TarInfo("etc/passwd")
            info.size = len(pw)
            t.addfile(info, io.BytesIO(pw))
            for m, data in layer.values():
                t.addfile(m, io.BytesIO(data) if data is not None else None)
        rootfs.seek(0)
        r = from_tar(rootfs, ContainerConfig("agent", "/tmp/w"), changes)
        back = restored(r)
        self.assertEqual(sh.fs.read_text("/home/agent/.bashrc"), back.fs.read_text("/home/agent/.bashrc"))
        self.assertFalse(back.fs.exists("/etc/topwater"))
        self.assertFalse(back.fs.exists("/home/agent/.bash_history"))
        self.assertEqual(sh.history, back.history)
        self.assertEqual("agent", back.fs.stat("/home/agent/a.txt").owner)
        self.assertTrue(back.fs.exists("/home/agent/.profile"))
        self.assertEqual("nl\n", back.fs.read_text("/tmp/n\nl"))
        self.assertTrue(back.fs.exists("/tmp/x\nD /home/agent/.profile"))
        probe = "pwd; cat ~/a.txt; greet you; alias ll; declare -p x"
        self.assertEqual(sh.run(probe).stdout, back.run(probe).stdout)

    # ---------- hostile input ----------

    def test_non_canonical_names_raise(self):
        for name in ("../etc/x", "home/../../x", "a//b", "/abs", "a/./b"):
            with self.subTest(name), self.assertRaises(InvalidContainerData):
                imported(make_tar(F(name, "x")), changes=["A /a/b"])

    def test_leading_dot_slash_is_accepted(self):
        self.assertIn("/srv/x", imported(make_tar(F("./srv/x", "ok")), changes=["A /srv/x"]).report.carried)

    def test_too_many_bytes_raises(self):
        with self.assertRaises(InvalidContainerData):
            imported(make_tar(entry("srv/big", bytes(2000))), changes=["A /srv/big"], options=ImportOptions(limits={"max_file_system_bytes": 1000}))

    def test_too_many_entries_raises(self):
        files = [F(f"srv/f{i}", "x") for i in range(10)]
        with self.assertRaises(InvalidContainerData):
            imported(make_tar(*files), options=ImportOptions(paths=("/srv",), limits={"max_extract_entries": 3}))

    def test_malformed_archives_raise(self):
        with self.assertRaises(InvalidContainerData):
            imported(io.BytesIO(bytes((i * 7 + 13) & 0xFF for i in range(2048))), changes=["A /x"])
        good = make_tar(entry("srv/f", bytes(2000))).getvalue()
        with self.assertRaises(InvalidContainerData):
            imported(io.BytesIO(good[:512 + 100]), changes=["A /srv/f"])

    def test_histories_count_against_the_byte_limit(self):
        # every /home/*/.bash_history is read for the history, imported or not, so each counts
        homes = [entry(f"home/u{i}/.bash_history", bytes(30_000)) for i in range(5)]
        with self.assertRaises(InvalidContainerData):
            imported(make_tar(*homes), options=ImportOptions(limits={"max_file_system_bytes": 100_000}))
        imported(make_tar(*homes[:2]), options=ImportOptions(limits={"max_file_system_bytes": 100_000}))

    def test_bad_times_and_owners_raise_invalid_container_data(self):
        for kw in ({"mtime": 10 ** 20}, {"mtime": -10 ** 14}, {"uid": -5}):
            buf = io.BytesIO()
            with tarfile.open(fileobj=buf, mode="w", format=tarfile.PAX_FORMAT) as t:
                info = tarfile.TarInfo("srv/x")
                info.size, info.mtime, info.uid = 1, kw.get("mtime", 1700000000), kw.get("uid", 0)
                t.addfile(info, io.BytesIO(b"x"))
            buf.seek(0)
            with self.subTest(kw), self.assertRaises(InvalidContainerData):
                imported(buf, changes=["A /srv/x"])
        # 2,040 levels deep (under the 4,096-character name limit); a working directory past 4,096 characters
        deep = "a/" * 2040 + "f"
        self.assertIn("/" + deep, imported(make_tar(F(deep, "x")), changes=["A /" + deep]).report.carried)
        with self.assertRaises(InvalidContainerData):
            imported(make_tar(), ContainerConfig("agent", "/a" * 3000))
        self.assertEqual("/a" * 1500, restored(imported(make_tar(), ContainerConfig("agent", "/a" * 1500))).run("pwd").stdout.strip())
        # a uid of 5,000 digits: the passwd line is ignored, the config user becomes root
        huge = "9" * 5000
        r = imported(make_tar(F("etc/passwd", f"big:x:{huge}:1::/home/big:/bin/bash\n"), F("srv/f", "x", uid=0)), ContainerConfig(huge), ["A /srv/f"])
        self.assertEqual("root", r.snapshot.user)

    def test_many_directory_links_import_in_linear_time(self):
        # each link to a directory once copied the directory's entries: 20,000 links took minutes
        n = 20_000
        tar = make_tar(D("srv"), *[e for i in range(n) for e in (D(f"srv/d{i}"), F(f"srv/d{i}/f", "x"), L(f"srv/l{i}", f"/srv/d{i}"))])
        start = time.monotonic()
        r = imported(tar, options=ImportOptions(paths=("/srv",)))
        self.assertLess(time.monotonic() - start, 120)  # was over 190 s; now a few seconds
        self.assertEqual("x", restored(r).run(f"cat /srv/l{n - 1}/f").stdout)

    def test_symlink_loop_comes_back(self):
        # as the loop it is: following it fails, nothing hangs
        r = imported(make_tar(L("srv/a", "/srv/b"), L("srv/b", "/srv/a")), changes=["A /srv/a", "A /srv/b"])
        run = restored(r).run("readlink /srv/a; cat /srv/a")
        self.assertEqual(("/srv/b\n", "cat: /srv/a: Too many levels of symbolic links\n"), (run.stdout, run.stderr))

    def test_the_agent_cannot_reach_containers(self):
        # host APIs only: the module registers no command, and the default shell has no docker program
        sh = Shell()
        self.assertFalse([n for n in sh.commands if "docker" in n or "container" in n])
        self.assertEqual(127, sh.run("docker build -t x - < /dev/null").exit_code)
        self.assertFalse(hasattr(containers, "register"))


BINARY = bytes([0, 1, 2, 0x7F, 0x80, 0xFE, 0xFF, 10, 0xC3])


def docker(*args, stdin=None) -> str:
    p = subprocess.run(["docker", *args], input=stdin, capture_output=True, text=True)
    assert p.returncode == 0, f"docker {' '.join(args)}: exit {p.returncode}: {p.stderr}"
    return p.stdout


def machine() -> Shell:
    sh = Shell()
    sh.fs.write_bytes("/home/agent/blob.bin", BINARY)
    sh.fs.mkdir("/data", parents=True)
    sh.fs.write_text("/data/report.txt", "héllo wörld\n")
    sh.fs.set_owner("/data", "bob", recursive=True)
    r = sh.run("""mkdir -p proj/src && cd proj
printf 'hello\\n' > a.txt; chmod 600 a.txt; printf '#!/bin/sh\\necho ran\\n' > src/run.sh; chmod 755 src/run.sh
touch 'sp ace'; ln -s a.txt alink; ln a.txt ahard
export EXP='exported $x "q"'; plain='multi
line'; declare -i n=5; arr=(one 'two words' three); unset 'arr[1]'; declare -A map=([k1]=v1 ['key two']=v2)
greet() { echo "hi $1 from $(pwd)"; }; export -f greet
alias ll='ls -l'
shopt -s nullglob
rm ~/.profile
echo done""")
    assert r.exit_code == 0, r.to_tool_output()
    return sh


@unittest.skipUnless(docker_available(), "docker isn't available")
class DockerRoundTripTests(unittest.TestCase):
    """Topwater to a real container and back. Every image and container made here is removed."""

    def test_a_snapshot_runs_in_a_real_container_and_comes_back(self):
        tag = "topwater-pytest-" + uuid.uuid4().hex[:12]
        name = tag + "-c"
        try:
            context = build_image(machine().snapshot(), tag)
            self.assertIn("/home/agent/proj/a.txt", context.report.carried)
            files = docker("run", "--rm", tag, "bash", "-c", """id -un; pwd; echo "EXP=$EXP"
stat -c '%n %a %U %G' /home/agent/proj/a.txt /home/agent/proj/src/run.sh /data/report.txt
cat a.txt; src/run.sh; od -An -tx1 /home/agent/blob.bin; cat /data/report.txt; ls 'sp ace'
test -e ~/.profile && echo profile-present || echo profile-gone
readlink alink; cat alink; stat -c %h a.txt""")
            self.assertEqual("agent\n/home/agent/proj\nEXP=exported $x \"q\"\n/home/agent/proj/a.txt 600 agent agent\n"
                             "/home/agent/proj/src/run.sh 755 agent agent\n/data/report.txt 644 bob bob\nhello\nran\n"
                             " 00 01 02 7f 80 fe ff 0a c3\nhéllo wörld\nsp ace\nprofile-gone\na.txt\nhello\n2\n", files)
            # the session comes back in an interactive bash: functions, aliases, variables, options
            session = docker("run", "-i", "--rm", tag, "bash", "-i",
                             stdin="greet you; alias ll; declare -p plain n arr map; shopt nullglob; bash -c 'greet child'\n")
            self.assertEqual("hi you from /home/agent/proj\nalias ll='ls -l'\ndeclare -- plain=$'multi\\nline'\ndeclare -i n=\"5\"\n"
                             "declare -a arr=([0]=\"one\" [2]=\"three\")\ndeclare -A map=([k1]=\"v1\" [\"key two\"]=\"v2\" )\n"
                             "nullglob       \ton\nhi child from /home/agent/proj\n", session)

            # real programs change the container, and the import brings the changes back
            docker("run", "--name", name, tag, "bash", "-c", """echo changed >> a.txt; echo new > new.txt
perl -e 'open F, ">calc.txt"; print F 6*7, "\\n"'
ln -s a.txt link.txt; ln -s /etc/shadow secret; rm src/run.sh; mkdir -p /tmp/work && seq 1 5 > /tmp/work/nums""")
            result = from_container(name, ImportOptions(paths=("/var/log",)))
            self.assertNotIn("/home/agent/proj/secret", whats(result.report))  # links come back as links
            sh = restored(result)
            out = sh.run("""whoami; pwd; echo "EXP=$EXP"; cat a.txt new.txt calc.txt link.txt; ls src | wc -l; readlink secret
sort -rn /tmp/work/nums | head -1; test -s /var/log/dpkg.log && echo dpkg.log came along; greet back; declare -p n; alias ll""").stdout
            self.assertEqual("agent\n/home/agent/proj\nEXP=exported $x \"q\"\nhello\nchanged\nnew\n42\nhello\nchanged\n0\n/etc/shadow\n5\n"
                             "dpkg.log came along\nhi back from /home/agent/proj\ndeclare -i n=\"5\"\nalias ll='ls -l'\n", out)
            self.assertEqual(BINARY, sh.fs.read_bytes("/home/agent/blob.bin"))
            self.assertEqual("bob", sh.fs.stat("/data/report.txt").owner)

            # and an image imports without being run
            back = restored(from_image(tag))
            self.assertEqual("hello\n", back.run("cat a.txt").stdout)
            self.assertEqual("/home/agent/proj", back.cwd)
        finally:
            subprocess.run(["docker", "rm", "-f", name], capture_output=True)
            subprocess.run(["docker", "rmi", "-f", tag], capture_output=True)


if __name__ == "__main__":
    unittest.main()
