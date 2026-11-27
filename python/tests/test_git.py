"""git: identity, and hostile repositories (corrupt or oversized objects, bad indexes, links inside .git, tree entries
that point outside the work tree). The everyday behaviour is pinned by tests/corpus/git.jsonl."""
import base64
import gzip
import hashlib
import json
import sys
import time
import tracemalloc
import zlib
from pathlib import Path

import pytest

from bashcompat import Shell

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests" / "git"))
import make_packs  # noqa: E402
from topwater.commands.gitpack import CACHE_BYTES, ObjectCache  # noqa: E402

IDENT = ("export GIT_AUTHOR_NAME=A GIT_AUTHOR_EMAIL=a@example.com GIT_COMMITTER_NAME=A GIT_COMMITTER_EMAIL=a@example.com "
         "GIT_AUTHOR_DATE='2026-01-02T03:04:05Z' GIT_COMMITTER_DATE='2026-01-02T03:04:05Z'")


def repo(**options) -> Shell:
    sh = Shell(**options)
    r = sh.run(f"{IDENT}; mkdir -p /tmp/w && cd /tmp/w && git init -q && echo a > f && git add f && git commit -qm one")
    assert r.exit_code == 0, r.stderr
    return sh


def put(sh: Shell, kind: str, body: bytes, raw: bytes | None = None) -> str:
    """Writes an object the way a hostile repository could (raw: the stored bytes, whatever they hash to)."""
    data = f"{kind} {len(body)}\0".encode() + body
    sha = hashlib.sha1(data).hexdigest()
    sh.fs.mkdir(f"/tmp/w/.git/objects/{sha[:2]}", parents=True)
    sh.fs.write_bytes(f"/tmp/w/.git/objects/{sha[:2]}/{sha[2:]}", zlib.compress(data) if raw is None else raw)
    return sha


def commit_with_tree(sh: Shell, entries: bytes) -> str:
    tree = put(sh, "tree", entries)
    return put(sh, "commit", f"tree {tree}\nauthor A <a@b> 1 +0000\ncommitter A <a@b> 1 +0000\n\nx\n".encode())


def test_a_commit_without_an_identity_fails_like_real_git():
    sh = Shell()
    r = sh.run("cd /tmp && git init -q r && cd r && git commit -q --allow-empty -m x")
    assert r.exit_code == 128
    assert "*** Please tell me who you are." in r.stderr
    assert r.stderr.endswith("fatal: unable to auto-detect email address (got 'agent@topwater.(none)')\n")


def test_the_host_can_preset_an_identity_through_the_environment_or_gitconfig():
    sh = Shell()
    sh.env["GIT_AUTHOR_NAME"] = sh.env["GIT_COMMITTER_NAME"] = "Host Set"
    sh.env["GIT_AUTHOR_EMAIL"] = sh.env["GIT_COMMITTER_EMAIL"] = "host@example.com"
    r = sh.run("cd /tmp && git init -q r && cd r && git commit -q --allow-empty -m x && git log --format='%an <%ae>'")
    assert r.stdout == "Host Set <host@example.com>\n"
    sh = Shell()
    sh.fs.write_text("/home/agent/.gitconfig", "[user]\n\tname = File Set\n\temail = file@example.com\n")
    r = sh.run("cd /tmp && git init -q r && cd r && git commit -q --allow-empty -m x && git log --format='%an <%ae>'")
    assert r.stdout == "File Set <file@example.com>\n"


def test_an_object_whose_contents_do_not_match_its_name_is_corrupt():
    sh = repo()
    head = sh.run("git rev-parse HEAD").stdout.strip()
    path = f"/tmp/w/.git/objects/{head[:2]}/{head[2:]}"
    sh.fs.write_bytes(path, zlib.compress(b"commit 5\0hello"))
    r = sh.run("git log")
    assert r.exit_code == 128 and r.stderr == f"fatal: object {head} is corrupt\n"
    sh.fs.write_bytes(path, b"not zlib at all")
    assert sh.run("git status").exit_code == 128


def test_an_oversized_object_stops_at_the_string_limit():
    sh = repo(max_string_bytes=1 << 20)
    body = b"\0" * (4 << 20)  # inflates past the limit: never held whole
    sha = put(sh, "blob", body)
    r = sh.run(f"git cat-file -s {sha}")
    assert r.exit_code == 128 and "too large" in r.stderr


def test_a_garbage_index_is_reported_not_trusted():
    sh = repo()
    sh.fs.write_bytes("/tmp/w/.git/index", b"DIRC\0\0\0\2\0\0\0\5" + b"\xff" * 40)
    r = sh.run("git status")
    assert r.exit_code == 128 and r.stderr.startswith("fatal: index file corrupt")


def test_git_never_follows_a_symbolic_link_inside_dot_git():
    sh = repo()
    sh.run("mkdir -p /tmp/elsewhere/heads && echo 0123 > /tmp/elsewhere/heads/x && rm -rf .git/refs && ln -s /tmp/elsewhere .git/refs")
    r = sh.run("git branch evil")
    assert r.exit_code == 128 and "symbolic link" in r.stderr
    assert sh.run("cat /tmp/elsewhere/heads/x").stdout == "0123\n"


def test_tree_entries_that_leave_the_work_tree_are_refused():
    sh = repo()
    blob = put(sh, "blob", b"owned\n")
    for name in (b"..", b".git", b"a/b"):
        c = commit_with_tree(sh, b"100644 " + name + b"\0" + bytes.fromhex(blob))
        r = sh.run(f"git checkout -q {c}")
        assert r.exit_code == 128 and "invalid path" in r.stderr, (name, r.stderr)
    assert not sh.fs.exists("/tmp/owned") and sh.run("git status -s").stdout == ""


def test_checkout_replaces_a_symbolic_link_directory_instead_of_writing_through_it():
    sh = repo()
    sh.run("mkdir d && echo x > d/f && git add d && git commit -qm d && rm -rf d && mkdir /tmp/outside && ln -s /tmp/outside d")
    assert sh.run("git status -s").stdout == " D d/f\n?? d\n"
    sh.run("rm d && ln -s /tmp/outside d && git checkout -- d/f")
    assert sh.fs.listdir("/tmp/outside") == []
    assert not sh.fs.is_symlink("/tmp/w/d") and sh.run("cat d/f").stdout == "x\n"


def test_deeply_nested_trees_end_with_an_error():
    sh = repo()
    blob = put(sh, "blob", b"x\n")
    tree = put(sh, "tree", b"100644 f\0" + bytes.fromhex(blob))
    for _ in range(300):
        tree = put(sh, "tree", b"40000 d\0" + bytes.fromhex(tree))
    c = put(sh, "commit", f"tree {tree}\nauthor A <a@b> 1 +0000\ncommitter A <a@b> 1 +0000\n\nx\n".encode())
    r = sh.run(f"git show --stat {c}")
    assert r.exit_code == 128 and "nested too deeply" in r.stderr


def test_a_long_history_walk_respects_the_call_time_limit():
    sh = repo(max_call_duration=1)
    r = sh.run("for i in $(seq 400); do git commit -q --allow-empty -m $i; done")
    assert r.exit_code in (0, 124)
    r = sh.run("git log --oneline | wc -l")
    assert r.exit_code in (0, 124)


PACKS = json.loads((Path(__file__).resolve().parents[2] / "tests" / "git" / "packs.json").read_text(encoding="utf-8"))


@pytest.mark.parametrize("case", PACKS, ids=[c["name"] for c in PACKS])
def test_packfiles_good_and_hostile(case):
    """tests/git/packs.json (made by tests/git/make_packs.py), shared with the other ports."""
    sh = repo(max_call_duration=case.get("timeLimit", 60 if case.get("cacheBound") else 10))
    sh.fs.mkdir("/tmp/w/.git/objects/pack", parents=True)
    for name, data in case["files"].items():
        sh.fs.write_bytes(f"/tmp/w/.git/objects/pack/{name}", gzip.decompress(base64.b64decode(data)))
    ObjectCache.peak = 0
    tracemalloc.start()
    start = time.monotonic()
    try:
        r = sh.run(case["script"])
        peak = tracemalloc.get_traced_memory()[1]
    finally:
        tracemalloc.stop()
    assert time.monotonic() - start < (30 if case.get("cacheBound") else case.get("timeLimit", 2) + 3)
    if case.get("cacheBound"):
        assert 0 < ObjectCache.peak <= CACHE_BYTES and peak < 400 << 20, (ObjectCache.peak, peak)
    assert r.exit_code == case["exitCode"], r.stderr
    if "stdout" in case:
        assert r.stdout == case["stdout"]
    if "stderr" in case:
        assert case["stderr"] in r.stderr and r.stderr.startswith("fatal: ")


def test_packed_refs_names_that_leave_refs_are_ignored():
    sh = repo()
    sh.run("git remote 2>/dev/null; s=$(git rev-parse HEAD); printf '%s refs/heads/../../outside\\n%s refs/remotes/../x\\n%s refs/remotes/o/cr\\r\\n"
           "%s refs/tags/.hidden\\n%s refs/heads/ok\\n' $s $s $s $s $s > .git/packed-refs")
    r = sh.run("git branch -a; git log --oneline --decorate -1; git tag")
    assert r.exit_code == 0 and ".." not in r.stdout and "hidden" not in r.stdout and "\r" not in r.stdout and "ok" in r.stdout, r.stdout
    r = sh.run("git checkout x; echo $?; cat .git/config")
    assert r.stdout.startswith("1\n") and "remote = .." not in r.stdout


def test_upstream_errors_and_ambiguous_checkout():
    sh = repo()
    r = sh.run("git rev-parse @{u}; git rev-parse nosuch@{u}; git checkout -q --detach; git rev-parse HEAD@{upstream}")
    assert r.stderr == ("fatal: no upstream configured for branch 'master'\nfatal: no such branch: 'nosuch'\n"
                        "fatal: HEAD does not point to a branch\n")
    sh.run("git checkout -q master; for r in origin up; do git config remote.$r.fetch '+refs/heads/*:refs/remotes/'$r'/*'; mkdir -p .git/refs/remotes/$r;"
           " git rev-parse HEAD > .git/refs/remotes/$r/b; done")
    r = sh.run("git checkout b")
    assert r.exit_code == 128 and r.stderr == "fatal: 'b' matched multiple (2) remote tracking branches\n"
    sh.run("rm -r .git/refs/remotes/up; echo f > b")
    r = sh.run("git checkout b")
    assert r.exit_code == 128 and "could be both a local file and a tracking branch" in r.stderr


def test_packed_repositories_take_new_loose_objects():
    sh = repo()
    sh.run("echo b > g && git add g && git commit -qm two")
    # pack everything the way git gc would leave it: objects only in a pack, refs in packed-refs
    names = sh.run("find .git/objects -type f | cut -d/ -f3,4 | tr -d /").stdout.split()
    objs = []
    for sha in names:
        raw = zlib.decompress(sh.fs.read_bytes(f"/tmp/w/.git/objects/{sha[:2]}/{sha[2:]}"))
        kind, _, rest = raw.partition(b" ")
        objs.append({"kind": kind.decode(), "data": rest.partition(b"\0")[2], "name": sha})
    pack, idx = make_packs.build(objs)
    sh.run("rm -rf .git/objects/?? && mkdir -p .git/objects/pack")
    sh.fs.write_bytes("/tmp/w/.git/objects/pack/pack-1.pack", pack)
    sh.fs.write_bytes("/tmp/w/.git/objects/pack/pack-1.idx", idx)
    r = sh.run("git log --oneline && git status -s && echo c > h && git add h && git commit -qm three && git log --oneline | wc -l"
               " && find .git/objects -type f | grep -vc pack && git rev-parse --short HEAD~2 | wc -c")
    assert r.exit_code == 0, r.stderr
    assert r.stdout.split("\n")[-4:] == ["3", "3", "8", ""]
    r = sh.run("git gc; git repack")
    assert r.exit_code == 128 and "not available" in r.stderr


def test_a_5000_object_pack_reads_quickly():
    sh = repo()
    objs, text = [], b""
    for n in range(5000):
        text += b"line %d\n" % n
        objs.append({"kind": "blob", "data": text[-2000:], "name": make_packs.oid("blob", text[-2000:])})
    pack, idx = make_packs.build(objs)
    sh.fs.mkdir("/tmp/w/.git/objects/pack", parents=True)
    sh.fs.write_bytes("/tmp/w/.git/objects/pack/pack-1.pack", pack)
    sh.fs.write_bytes("/tmp/w/.git/objects/pack/pack-1.idx", idx)
    start = time.monotonic()
    r = sh.run(f"git cat-file -s {objs[4999]['name']}; git cat-file -t {objs[10]['name'][:8]}")
    assert r.stdout == "2000\nblob\n" and time.monotonic() - start < 2


def test_odd_revisions_and_patterns_fail_cleanly():
    sh = repo()
    r = sh.run("git rev-parse $'HEAD\\n'")
    assert r.exit_code == 128 and r.stderr.startswith("fatal: ambiguous argument")
    r = sh.run("git log --oneline -- '[z-a]'; git status -s -- '[z-a]'")
    assert r.exit_code == 0 and r.stdout == "" and r.stderr == ""


def test_graph_refuses_history_it_cannot_draw():
    sh = repo()
    r = sh.run("git checkout -qb side && git commit -q --allow-empty -m s && git checkout -q master && "
               "git commit -q --allow-empty -m m && git merge -q side -m merged && git log --graph --oneline")
    assert r.exit_code == 128 and "git log --format='%h %p %s'" in r.stderr and r.stdout == ""
    assert sh.run("git log --graph --oneline --all side").exit_code == 128
    assert sh.run("git log --graph --oneline side").stdout.count("* ") == 2


def test_a_path_too_deep_in_the_index_is_refused_quickly():
    sh = repo(max_call_duration=2)
    path = "/".join(["d"] * 5000) + "/f"
    import struct
    blob = sh.run("git rev-parse HEAD:f").stdout.strip()
    name = path.encode()
    entry = struct.pack(">IIIIIIIIII", 0, 0, 0, 0, 0, 0, 0o100644, 0, 0, 2) + bytes.fromhex(blob) + struct.pack(">H", 0xFFF) + name
    entry += b"\0" * (8 - len(entry) % 8)
    body = b"DIRC" + struct.pack(">II", 2, 1) + entry
    sh.fs.write_bytes("/tmp/w/.git/index", body + hashlib.sha1(body).digest())
    for cmd in ("git status", "git commit -qm x", "git checkout -- ."):
        r = sh.run(cmd)
        assert r.exit_code == 128 and "directories deep" in r.stderr, (cmd, r.stderr)


def test_a_symbolic_head_cannot_lead_outside_dot_git():
    sh = repo()
    sh.fs.write_text("/tmp/w/.git/HEAD", "ref: refs/heads/../../../../victim/newref\n")
    r = sh.run("git commit -q --allow-empty -m x")
    assert r.exit_code == 128 and not sh.fs.exists("/tmp/victim")
    sh.fs.write_text("/tmp/w/.git/HEAD", "ref: ../../victim\n")
    assert "not a git repository" in sh.run("git status").stderr


def test_agent_patterns_stop_at_the_time_limit():
    sh = repo(max_call_duration=1)
    star = "*a" * 30 + "b"
    sh.fs.write_text("/tmp/w/.gitignore", star + "\n")
    sh.fs.write_text("/tmp/w/" + "a" * 200, "x")
    import time
    for cmd in ("git status", f"git add -- '{star}'", "git log --grep='(a+)+b'"):
        start = time.monotonic()
        sh.run(cmd)
        assert time.monotonic() - start < 10, cmd


def test_editor_settings_and_identity_cleanup():
    sh = repo()
    r = sh.run("EDITOR=vim git commit --allow-empty")
    assert r.exit_code == 1 and "no editor can run" in r.stderr
    r = sh.run("GIT_EDITOR=true git commit -q --amend && git log --format=%s")
    assert r.stdout == "one\n"
    r = sh.run("GIT_AUTHOR_NAME=$'Evil\\nName <x>' git commit -q --allow-empty -m n && git log -1 --format='%an' && git status -s")
    assert r.exit_code == 0 and r.stdout == "EvilName x\n"


def test_apply_refuses_paths_outside_the_work_tree():
    sh = repo()
    for name in ("../escape", ".git/config", "/etc/passwd"):
        sh.fs.write_bytes("/tmp/p.diff", f"--- a/{name}\n+++ b/{name}\n@@ -0,0 +1 @@\n+x\n".replace("a//", "a/").replace("b//", "b/").encode())
        r = sh.run("cd /tmp/w && git apply /tmp/p.diff; echo $?")
        assert r.stdout.strip().splitlines()[-1] != "0", name
    assert not sh.fs.exists("/tmp/escape")
    assert sh.run("cat /tmp/w/.git/config").stdout.count("x\n") == 0


def test_bisect_run_with_skips_still_finds_the_commit():
    sh = repo()
    r = sh.run("cd /tmp/w && for i in 2 3 4 5 6; do echo $i > f; git commit -qam c$i; done; git bisect start HEAD HEAD~5 >/dev/null; "
               "git bisect run sh -c 'n=$(cat f); [ $n = 3 ] && exit 125; [ $n -lt 5 ]' | tail -1; git log -1 --format=%s")
    assert r.stdout == "bisect found first bad commit\nc5\n", r.stderr


def test_tar_transform_cannot_leave_the_target_directory():
    import io as _io
    import tarfile
    buf = _io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.GNU_FORMAT) as t:
        for name, data in (("pkg-1.0/ok.txt", b"fine\n"), ("pkg-../pkg-../pkg-../esc1", b"pwned\n")):
            info = tarfile.TarInfo(name)
            info.size = len(data)
            t.addfile(info, _io.BytesIO(data))
        link = tarfile.TarInfo("pkg-1.0/hl")
        link.type, link.linkname = tarfile.LNKTYPE, "pkg-../pkg-../pkg-../victim"
        t.addfile(link)
    sh = Shell()
    sh.run("mkdir -p /tmp/w/a/b/c")
    sh.fs.write_bytes("/tmp/w/h.tar", buf.getvalue())
    sh.fs.write_bytes("/tmp/w/victim", b"secret\n")
    r = sh.run("cd /tmp/w/a/b/c && tar -xf /tmp/w/h.tar --transform='s,pkg-,,g'; echo $?; "
               "tar -xf /tmp/w/h.tar --transform='s,^pkg-1.0,../../../../esc,'; echo $?")
    assert r.stdout == "2\n2\n"
    assert sh.fs.read_bytes("/tmp/w/a/b/c/esc1") == b"pwned\n"
    assert not sh.fs.exists("/tmp/w/esc1") and not sh.fs.exists("/esc") and not sh.fs.exists("/tmp/esc")
    assert sh.fs.read_bytes("/tmp/w/a/b/c/esc/ok.txt") == b"fine\n"
    assert sh.fs.read_bytes("/tmp/w/victim") == b"secret\n"
