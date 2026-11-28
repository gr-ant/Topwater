"""Mounted sources (docs/mounts.md): tests/mounts/cases.json, which the C# and TypeScript ports run too, then what only
this port checks: hostile sources, limits, snapshots, recordings and the local folder source."""
from __future__ import annotations

import asyncio
import contextlib
import io
import json
import os
from pathlib import Path

import pytest

from topwater import Shell
from topwater.mounts import LocalFolderSource, MemorySource, MountChange, MountConflictError, MountEntry, MountSource
from topwater.recording import Recording

CASES = json.loads((Path(__file__).resolve().parents[2] / "tests" / "mounts" / "cases.json").read_text())


def change_json(c: MountChange) -> dict:
    d = {"kind": c.kind, "path": c.path}
    if c.to is not None:
        d["to"] = c.to
    if c.version is not None:
        d["version"] = c.version
    if c.content is not None:
        d["content"] = c.content.decode()
    return d


def run_case(case: dict) -> list:
    """Runs a case's steps and returns what each produced, in the shape the file's `expect` holds."""
    src = MemorySource(CASES["files"])
    sh = Shell()
    sh.mount("/mnt/src", src, case["mode"])
    out = []
    for step in case["steps"]:
        if "run" in step:
            r = sh.run(step["run"])
            out.append({"stdout": r.stdout, "stderr": r.stderr, "exitCode": r.exit_code})
        elif "changes" in step:
            out.append([change_json(c) for c in sh.pending_changes("/mnt/src")])
        elif "apply" in step or "applySubset" in step:
            pending = sh.pending_changes("/mnt/src")
            chosen = None if "apply" in step else [pending[i] for i in step["applySubset"]]
            r = sh.apply_changes("/mnt/src", chosen)
            out.append({"applied": [change_json(c)["path"] for c in r.applied], "failed": r.failed and r.failed.path, "conflict": r.conflict})
        elif "reads" in step:
            out.append([p for op, p in src.calls if op == "read"])
            src.calls.clear()
        elif "source" in step:
            out.append({p: None if v is None else v.decode() for p, v in src.tree().items()})
        elif "sourceSet" in step:
            src.set(*step["sourceSet"])
            out.append(None)
        elif "sourceRemove" in step:
            src.remove(step["sourceRemove"])
            out.append(None)
        elif "discard" in step:
            sh.discard_changes("/mnt/src")
            out.append(None)
    return out


@pytest.mark.parametrize("case", CASES["cases"], ids=[c["name"] for c in CASES["cases"]])
def test_shared_case(case):
    got = run_case(case)
    for step, actual in zip(case["steps"], got):
        assert actual == step.get("expect"), (step, actual)


# ---------- what only this port checks ----------

class Hostile(MountSource):
    """A source that lists names no file can have, a duplicate, and an endless supply of entries."""
    type, id = "hostile", "h"

    def __init__(self, count=5):
        self.count = count

    def list(self, path, max_entries):
        self.asked = max_entries
        bad = [MountEntry("", False), MountEntry(".", True), MountEntry("..", True), MountEntry("a/b", False),
               MountEntry("nul\0", False), MountEntry("x" * 256, False), MountEntry("ok", False, 3, version="1"),
               MountEntry("ok", False, 9, version="2"), MountEntry("neg", False, -1), "not an entry"]
        return bad + [MountEntry(f"z{i}", False, 1, version="v") for i in range(self.count)]

    def stat(self, path):
        return None

    def read(self, path, max_bytes):
        return b"x" * (max_bytes + 1) if path == "/z0" else b"abc"


def test_hostile_names_are_skipped_and_listing_is_bounded():
    sh = Shell(max_extract_entries=4)
    h = Hostile()
    sh.mount("/mnt/h", h)
    assert sh.run("ls /mnt/h").stdout == "ok\nz0\nz1\nz2\n"
    assert [w.split(": ", 1)[1] for w in sh.mounts[0].warnings] == ["skipped 9 entries with names that can't be files",
                                                                     "listing stopped at 4 entries"]
    assert h.asked == 4  # the source is told how many entries are wanted
    r = sh.run("cat /mnt/h/ok; cat /mnt/h/z0")
    assert r.stdout == "abc" and r.stderr == "cat: /mnt/h/z0: File too large\n"
    assert sh.run("mount -r /mnt/h").stderr.count("mount: /mnt/h:") == 2


def test_a_file_bigger_than_the_disk_is_not_fetched():
    src = MemorySource({"big.bin": b"x" * 5000, "small.txt": "s\n"})
    sh = Shell(max_file_system_bytes=sh_bytes() + 4000)
    sh.mount("/mnt/s", src)
    r = sh.run("cat /mnt/s/big.bin > /dev/null; cat /mnt/s/small.txt")
    assert r.stdout == "s\n" and "File too large" in r.stderr


def sh_bytes() -> int:
    return Shell().fs.used_bytes


def test_fetched_files_are_evicted_to_make_room():
    src = MemorySource({"a": b"a" * 3000, "b": b"b" * 3000})
    sh = Shell(max_file_system_bytes=sh_bytes() + 4000)
    sh.mount("/mnt/s", src)
    assert sh.run("wc -c /mnt/s/a").stdout == "3000 /mnt/s/a\n"
    assert sh.run("wc -c /mnt/s/b").stdout == "3000 /mnt/s/b\n"
    assert sh.run("wc -c /mnt/s/a").stdout == "3000 /mnt/s/a\n"
    assert [c for c in src.calls if c[0] == "read"] == [("read", "/a"), ("read", "/b"), ("read", "/a")]


def test_a_slow_source_fails_the_command_not_the_session():
    src = MemorySource({"slow.txt": "s\n", "fast.txt": "f\n"})

    async def hook(op, path):
        if path == "/slow.txt":
            await asyncio.sleep(5)
    sh = Shell(max_call_duration=0.5)
    sh.mount("/mnt/s", src)
    src.hook = hook
    r = sh.run("cat /mnt/s/slow.txt")
    assert "the source did not answer in time" in r.stderr or r.exit_code == 124
    assert sh.run("cat /mnt/s/fast.txt").stdout == "f\n"


def test_source_failures_are_reported_and_retried():
    src = MemorySource({"a.txt": "a\n"})
    sh = Shell()
    sh.mount("/mnt/s", src, "staged")
    fail = {"on": True}

    def hook(op, path):
        if fail["on"] and op in ("read", "write"):
            raise OSError("GET https://api/x?sig=SECRET 503")
    src.hook = hook
    assert sh.run("cat /mnt/s/a.txt").stderr == "cat: /mnt/s/a.txt: the source failed (run the command again to retry)\n"
    fail["on"] = False
    assert sh.run("cat /mnt/s/a.txt").stdout == "a\n"
    sh.run("echo b > /mnt/s/a.txt")
    fail["on"] = True
    r = sh.apply_changes("/mnt/s")
    assert r.failed.path == "/a.txt" and r.error == "GET https://api/x?sig=SECRET 503" and not r.conflict  # the host sees it all
    fail["on"] = False
    assert sh.apply_changes("/mnt/s").applied[0].path == "/a.txt" and src.tree()["/a.txt"] == b"b\n"


def test_the_source_never_sees_paths_outside_it():
    src = MemorySource({"a.txt": "a\n"})
    sh = Shell()
    sh.mount("/mnt/s", src, "staged")
    sh.run("cd /mnt/s; cat ../../etc/hostname; ln -s /etc /mnt/s/etc; cat /mnt/s/etc/hostname; echo x > /mnt/s/etc/../b")
    assert all(p.startswith("/") and ".." not in p for _, p in src.calls)
    assert [c.path for c in sh.pending_changes("/mnt/s")] == []  # the link is not a change; ../b left the mount


def test_mount_point_rules():
    sh = Shell()
    src = MemorySource()
    with pytest.raises(ValueError):
        sh.mount("/", src)
    with pytest.raises(ValueError):
        sh.mount("/etc", src)  # not empty
    with pytest.raises(ValueError):
        sh.mount("/mnt/x", src, "sideways")
    with pytest.raises(ValueError):
        sh.mount("/mnt/x", Hostile(), "staged")  # can't write
    sh.mount("/mnt/x", src)
    with pytest.raises(ValueError):
        sh.mount("/mnt/x/y", src)
    with pytest.raises(ValueError):
        sh.mount("/mnt/x", src)
    sh.unmount("/mnt/x")
    assert sh.mounts == [] and sh.run("ls -A /mnt/x").stdout == ""


def test_snapshot_keeps_the_definition_and_the_changes_not_the_files():
    src = MemorySource({"a.txt": "secret\n", "b.txt": "b\n"})
    sh = Shell()
    sh.mount("/mnt/s", src, "staged")
    sh.run("cat /mnt/s/a.txt; echo new > /mnt/s/b.txt; rm /mnt/s/a.txt; cd /mnt/s")
    buf = io.BytesIO()
    sh.save(buf)
    raw = buf.getvalue()
    assert b"secret" not in raw
    assert json.loads(__import__("zipfile").ZipFile(io.BytesIO(raw)).read("manifest.json"))["version"] == 3
    loaded = Shell.load(raw)
    info = loaded.mounts[0]
    assert (info.path, info.mode, info.type, info.id, info.held) == ("/mnt/s", "staged", "memory", "memory", 2)
    assert info.error == "unavailable: no source for memory:memory"
    assert loaded.pending_changes("/mnt/s") == []
    assert [c.kind for c in loaded.snapshot_changes("/mnt/s")] == ["write", "delete"]
    assert "Permission denied" in loaded.run("ls /mnt/s").stderr
    loaded.mount("/mnt/s", src, "staged")
    # a file's changes wait for the host: not in the files, not pending, until it accepts them
    assert loaded.run("ls /mnt/s; cat /mnt/s/b.txt").stdout == "a.txt\nb.txt\nb\n"
    assert loaded.pending_changes("/mnt/s") == [] and "1 changes" not in loaded.run("mount").stdout
    assert "2 changes from a saved session wait for the host" in loaded.run("mount").stdout
    loaded.accept_snapshot_changes("/mnt/s")
    assert loaded.run("ls /mnt/s; cat /mnt/s/b.txt").stdout == "b.txt\nnew\n"
    assert loaded.pending_changes("/mnt/s") == sh.pending_changes("/mnt/s")
    # a snapshot taken in this process comes back as it was, with no source call
    other = Shell()
    other.mount("/mnt/other", src)
    other.mount("/mnt/s", src, "staged")
    other.unmount("/mnt/other")
    before = len(src.calls)
    other.restore(sh.snapshot())
    assert other.run("ls /mnt/s").stdout == "b.txt\n" and other.pending_changes("/mnt/s") == sh.pending_changes("/mnt/s")
    assert [c for c in src.calls[before:] if c[0] == "list"] == []
    # the source changed meanwhile: applying conflicts, against the version the change was made on
    src.set("b.txt", "theirs\n")
    r = loaded.apply_changes("/mnt/s")
    assert r.conflict and r.failed.path == "/b.txt"


def test_a_command_in_a_mount_directory_fetches_it():
    src = MemorySource({"d/a.txt": "hello\n"})
    sh = Shell()
    sh.mount("/mnt/s", src)
    assert sh.run("cd /mnt/s/d && grep -r hello").stdout == "a.txt:hello\n"


def test_a_file_deleted_in_a_renamed_directory():
    src = MemorySource({"d/a.txt": "a\n", "d/b.txt": "b\n"})
    sh = Shell()
    sh.mount("/mnt/s", src, "staged")
    sh.run("rm /mnt/s/d/b.txt; mv /mnt/s/d /mnt/s/e")
    assert [(c.kind, c.path, c.to) for c in sh.pending_changes("/mnt/s")] == [("rename", "/d", "/e"), ("delete", "/e/b.txt", None)]
    assert sh.apply_changes("/mnt/s").failed is None
    assert src.tree() == {"/e": None, "/e/a.txt": b"a\n"}


def test_a_relative_mount_point_is_refused():
    with pytest.raises(ValueError):
        Shell().mount("mnt/s", MemorySource())


def test_the_time_limit_still_ends_the_call_during_a_refresh():
    src = MemorySource({"a": "a"})
    sh = Shell(max_call_duration=0.3)
    sh.mount("/mnt/s", src)

    async def hook(op, path):
        await asyncio.sleep(1)
    src.hook = hook
    r = sh.run("mount -r /mnt/s; echo after")
    assert "after" not in r.stdout


def test_a_working_directory_inside_a_mount_survives_a_restore():
    src = MemorySource({"d/a.txt": "a\n"})
    sh = Shell()
    sh.mount("/mnt/s", src)
    sh.run("cd /mnt/s/d")
    snap = sh.snapshot()
    sh.run("cd /")
    sh.restore(snap)
    assert sh.run("pwd; cat a.txt").stdout == "/mnt/s/d\na\n"


def test_fork_uses_the_same_source():
    src = MemorySource({"a.txt": "a\n"})
    sh = Shell()
    sh.mount("/mnt/s", src, "staged")
    sh.run("echo b >> /mnt/s/a.txt")
    fork = sh.fork()
    assert fork.run("cat /mnt/s/a.txt").stdout == "a\nb\n"


def test_old_snapshots_still_load():
    buf = io.BytesIO()
    Shell().save(buf)
    assert Shell.load(buf.getvalue()).mounts == []


def test_bad_mounts_in_a_snapshot_are_refused():
    import zipfile
    from topwater.snapshot import ShellSnapshot, SnapshotError
    buf = io.BytesIO()
    sh = Shell()
    sh.mount("/mnt/s", MemorySource({"a": "a"}), "staged")
    sh.run("echo b > /mnt/s/a")
    sh.save(buf)
    for bad in ({"path": "/mnt/../etc"}, {"mode": "wild"}, {"path": "/"},
                {"changes": [{"kind": "write", "path": "/a/../b", "size": 0}]},
                {"changes": [{"kind": "write", "path": "/a", "size": 3, "blob": "0" * 64}]},
                {"changes": [{"kind": "explode", "path": "/a"}]}):
        z = zipfile.ZipFile(io.BytesIO(buf.getvalue()))
        m = json.loads(z.read("manifest.json"))
        m["mounts"][0].update(bad)
        out = io.BytesIO()
        with zipfile.ZipFile(out, "w") as w:
            for n in z.namelist():
                w.writestr(n, json.dumps(m) if n == "manifest.json" else z.read(n))
        with pytest.raises(SnapshotError):
            ShellSnapshot.load(out.getvalue())


def test_a_snapshot_holds_at_most_1000_mounts():
    # the overlap check is quadratic: 20,000 mounts in a small file took minutes to load
    import zipfile
    from topwater.snapshot import ShellSnapshot, SnapshotError
    buf = io.BytesIO()
    Shell().save(buf)
    z = zipfile.ZipFile(io.BytesIO(buf.getvalue()))
    m = json.loads(z.read("manifest.json"))
    m["version"] = 3
    m["mounts"] = [{"path": f"/mnt/m{i}", "mode": "read-only", "type": "memory", "id": "memory"} for i in range(1001)]
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as w:
        for n in z.namelist():
            w.writestr(n, json.dumps(m) if n == "manifest.json" else z.read(n))
    with pytest.raises(SnapshotError, match="more than 1000 mounts"):
        ShellSnapshot.load(out.getvalue())


def test_a_recording_replays_without_the_source():
    src = MemorySource({"a.txt": "a\n", "d/b.txt": "b\n"})
    sh = Shell()
    sh.mount("/mnt/s", src, "live")
    sh.run("cat /mnt/s/a.txt")  # fetched before the recording starts
    rec = sh.start_recording()
    sh.run("cat /mnt/s/a.txt /mnt/s/d/b.txt; echo c > /mnt/s/c.txt; mount -r /mnt/s; ls /mnt/s")
    src.set("a.txt", "theirs\n")
    sh.run("echo mine > /mnt/s/a.txt")
    rec.stop()
    buf = io.BytesIO()
    rec.save(buf)
    loaded = Recording.load(buf.getvalue())
    before = list(src.calls)
    report = loaded.replay()
    assert report.matches, report.differences
    assert src.calls == before
    replayed = loaded.create_shell()
    r = replayed.run("mount -r /mnt/s; cat /mnt/s/d/b.txt /mnt/s/c.txt")
    assert r.stdout == "b\n" and r.stderr == "cat: /mnt/s/c.txt: read /c.txt is not in the Topwater recording (run the command again to retry)\n"


def test_local_folder_source(tmp_path):
    root = tmp_path / "vault"
    (root / "notes").mkdir(parents=True)
    (root / "notes" / "a.md").write_text("# A\n")
    (root / "top.md").write_text("top\n")
    (tmp_path / "outside.txt").write_text("private\n")
    os.symlink(tmp_path / "outside.txt", root / "link.md")
    os.symlink(tmp_path, root / "up")
    sh = Shell()
    sh.mount("/mnt/v", LocalFolderSource(root))
    assert sh.run("find /mnt/v | sort").stdout == "/mnt/v\n/mnt/v/notes\n/mnt/v/notes/a.md\n/mnt/v/top.md\n"
    assert sh.run("cat /mnt/v/notes/a.md").stdout == "# A\n"
    assert "Permission denied" in sh.run("echo x > /mnt/v/top.md").stderr
    with pytest.raises(ValueError):
        sh.mount("/mnt/w", LocalFolderSource(root), "staged")  # read-only unless writable

    sh.mount("/mnt/w", LocalFolderSource(root, writable=True, id="vault"), "staged")
    sh.run("echo more >> /mnt/w/top.md; mv /mnt/w/notes/a.md /mnt/w/notes/b.md; mkdir /mnt/w/new; echo n > /mnt/w/new/n.md")
    r = sh.apply_changes("/mnt/w")
    assert r.failed is None, r
    assert (root / "top.md").read_text() == "top\nmore\n"
    assert (root / "notes" / "b.md").read_text() == "# A\n" and not (root / "notes" / "a.md").exists()
    assert (root / "new" / "n.md").read_text() == "n\n"
    assert (tmp_path / "outside.txt").read_text() == "private\n"
    sh.run("echo again >> /mnt/w/top.md")
    (root / "top.md").write_text("edited on the host, longer\n")
    r = sh.apply_changes("/mnt/w")
    assert r.conflict and r.failed.path == "/top.md"
    assert (root / "top.md").read_text() == "edited on the host, longer\n"


# ---------- review 046: regression tests ----------

HOSTILE = Path(__file__).resolve().parents[2] / "tests" / "mounts" / "hostile.snap"


def _host_folder(tmp_path):
    root = tmp_path / "notes"
    (root / "docs").mkdir(parents=True)
    (root / "docs" / "keep.md").write_text("keep\n")
    (root / "a.md").write_text("hello\n")
    return root


def _tree(d) -> list:
    return sorted(os.path.relpath(os.path.join(b, f), d) for b, ds, fs in os.walk(d) for f in fs + ds)


@pytest.mark.parametrize("mode,writable,unmount", [("staged", True, False), ("read-only", True, False), ("read-only", False, False),
                                                   ("staged", True, True)])
def test_a_hostile_snapshot_never_writes_to_a_real_source(tmp_path, mode, writable, unmount):
    """hostile.snap (security review 046): mode live, a rename, a mkdir and a write into the host's folder."""
    from topwater.snapshot import ShellSnapshot
    root = _host_folder(tmp_path)
    sh = Shell()
    sh.mount("/mnt/notes", LocalFolderSource(root, writable=writable, id="notes"), mode)
    if unmount:
        sh.unmount("/mnt/notes")
    sh.restore(ShellSnapshot.load(HOSTILE.read_bytes()))
    for c in ("true", "echo hi > /mnt/notes/x.md; ls /mnt/notes", "mount"):
        sh.run(c)
    assert _tree(root) == ["a.md", "docs", "docs/keep.md"]
    m = sh.mounts[0]
    assert m.mode == (mode if not unmount else "live") and (m.error is not None) == unmount
    if not unmount:
        assert m.held == 3 and [c.path for c in sh.pending_changes("/mnt/notes")] in ([], ["/x.md"])  # only the agent's own
        assert "keep.md" in sh.run("ls /mnt/notes/docs").stdout


def test_a_fork_of_a_replay_shell_never_reaches_the_real_source():
    src = MemorySource({"a.md": "one\n"}, id="m")
    sh = Shell()
    sh.mount("/mnt/m", src, "live")
    rec = sh.start_recording()
    sh.run("cat /mnt/m/a.md")
    rec.stop()
    buf = io.BytesIO()
    rec.save(buf)
    real = MemorySource({"a.md": "one\n"}, id="m")
    replay_shell = Recording.load(buf.getvalue()).create_shell(lambda s: s.mount("/mnt/m", real, "live"))
    n = len(real.calls)
    replay_shell.run("echo replayed > /mnt/m/r.md")
    replay_shell.fork().run("echo forked > /mnt/m/fork.md")
    assert real.calls[n:] == [] and "/fork.md" not in real.tree()


def test_unmount_forgets_the_source():
    src = MemorySource({"a.md": "x\n"})
    sh = Shell()
    sh.mount("/mnt/s", src, "staged")
    snap = sh.snapshot()
    sh.unmount("/mnt/s")
    sh.restore(snap)
    assert sh.mounts[0].error == "unavailable: no source for memory:memory"


def test_a_fifo_never_blocks_the_call(tmp_path):
    root = tmp_path / "v"
    root.mkdir()
    (root / "a.md").write_text("a\n")
    os.mkfifo(root / "fifo")
    sh = Shell(max_call_duration=5)
    sh.mount("/mnt/v", LocalFolderSource(root))
    assert sh.run("ls /mnt/v").stdout == "a.md\n"  # not listed
    os.remove(root / "a.md")
    os.mkfifo(root / "a.md")  # a listed file replaced by a FIFO
    import time as _time
    t = _time.monotonic()
    r = sh.run("cat /mnt/v/a.md")
    assert _time.monotonic() - t < 2 and "not a regular file" in r.stderr


def test_backslash_names_never_reach_a_folder(tmp_path):
    root = _host_folder(tmp_path)
    sh = Shell()
    sh.mount("/mnt/n", LocalFolderSource(root, writable=True, id="n"), "live")
    r = sh.run(r"echo evil > '/mnt/n/..\..\outside\win.txt'")
    assert "not a name a file can have here" in r.stderr and str(root) not in r.stderr
    assert _tree(root) == ["a.md", "docs", "docs/keep.md"]
    r = sh.run("echo ok > /mnt/n/good.md")  # a change that failed doesn't hold up the next ones, and is reported once
    assert r.stderr == "" and (root / "good.md").read_text() == "ok\n"


def test_source_errors_reach_the_agent_only_when_meant_for_it():
    from topwater.mounts import MountSourceError
    src = MemorySource({"a.md": "a\n", "b.md": "b\n"})

    def hook(op, path):
        if path == "/a.md":
            raise MountSourceError("a.md: the page is locked")
        if path == "/b.md":
            raise RuntimeError("GET https://api/x?sig=SECRET 503")
    sh = Shell()
    sh.mount("/mnt/s", src)
    src.hook = hook
    r = sh.run("cat /mnt/s/a.md /mnt/s/b.md")
    assert "the page is locked" in r.stderr and "SECRET" not in r.stderr and "the source failed" in r.stderr


def test_local_folder_errors_show_no_host_path(tmp_path):
    root = _host_folder(tmp_path)
    src = LocalFolderSource(root, writable=True, id="n")
    sh = Shell()
    sh.mount("/mnt/n", src, "live")
    os.chmod(root / "docs", 0o500)
    try:
        r = sh.run("echo x > /mnt/n/docs/new.md")
    finally:
        os.chmod(root / "docs", 0o755)
    assert str(tmp_path) not in r.stdout + r.stderr and "mount: /mnt/n/docs/new.md" in r.stderr


def test_long_names_write_and_leave_no_temporary_file(tmp_path):
    root = _host_folder(tmp_path)
    sh = Shell()
    sh.mount("/mnt/n", LocalFolderSource(root, writable=True, id="n"), "live")
    name = "y" * 250
    assert sh.run(f"echo x > /mnt/n/{name}").stderr == ""
    assert (root / name).read_text() == "x\n" and not [f for f in os.listdir(root) if f.endswith(".tmp")]


def test_a_large_source_is_listed_in_parts(monkeypatch):
    import topwater.mounts as mounts
    monkeypatch.setattr(mounts, "LIST_BUDGET", 5)
    src = MemorySource({f"d{i}/f{j}.txt": f"{i}{j}\n" for i in range(4) for j in range(3)})
    sh = Shell()
    sh.mount("/mnt/s", src)
    assert sh.run("ls /mnt/s/d0").stdout == "f0.txt\nf1.txt\nf2.txt\n"  # named: listed before the command
    assert sh.run("echo /mnt/s/*/f1.txt").stdout == "/mnt/s/d0/f1.txt\n"  # the glob looked into unlisted ones: listed after
    assert sh.run("echo /mnt/s/*/f1.txt").stdout == "/mnt/s/d0/f1.txt /mnt/s/d1/f1.txt /mnt/s/d2/f1.txt /mnt/s/d3/f1.txt\n"
    fresh = Shell()
    fresh.mount("/mnt/s", src)
    awk = "awk 'BEGIN { while ((getline l < \"/mnt/s/d3/f0.txt\") > 0) print l }'"  # a path inside a program: nothing named it
    assert fresh.run(awk).stdout == ""  # d3 was not listed: listed after the command
    assert fresh.run(awk).stdout == ""  # f0.txt was not fetched: fetched after the command
    assert fresh.run(awk).stdout == "30\n"
    assert sh.run("cat /mnt/s/d3/f2.txt; find /mnt/s -type f | wc -l").stdout == "32\n12\n"
    deep = Shell()
    deep.mount("/mnt/s", MemorySource({"a/b/c/f.txt": "deep\n", **{f"z{i}.txt": "" for i in range(5)}}))
    assert deep.run("cat /mnt/s/a/b/c/f.txt").stdout == "deep\n"  # every unlisted directory on the way is listed first


def test_a_restore_from_a_file_does_not_list_everything_in_one_call(monkeypatch):
    import topwater.mounts as mounts
    monkeypatch.setattr(mounts, "LIST_BUDGET", 3)
    src = MemorySource({f"d{i}/f.txt": "x" for i in range(10)})
    sh = Shell()
    sh.mount("/mnt/s", src)
    buf = io.BytesIO()
    sh.save(buf)
    loaded = Shell.load(buf.getvalue())
    loaded.mount("/mnt/s", src)  # adopts the unavailable mount
    before = len(src.calls)
    loaded.run("true")
    assert len([c for c in src.calls[before:] if c[0] == "list"]) == 0


def test_unknown_sizes_show_after_fetching():
    class Pages(MemorySource):
        async def list(self, path, max_entries):
            return [MountEntry(e.name, e.is_dir, None if not e.is_dir else 0, e.modified, e.version)
                    for e in await super().list(path, max_entries)]
    sh = Shell()
    sh.mount("/mnt/p", Pages({"page.md": "twelve bytes"}))
    assert sh.run("stat -c %s /mnt/p/page.md").stdout == "0\n"
    assert sh.run("cat /mnt/p/page.md > /dev/null; stat -c %s /mnt/p/page.md").stdout == "12\n"


def test_a_rename_takes_the_version_the_source_returns():
    class Moves(MemorySource):
        async def rename(self, path, to, expected_version):
            await super().rename(path, to, expected_version)
            data = self._items[to][0]
            self._items[to] = (data, self._version())  # like Drive: a move changes the ETag
            return self._items[to][1]
    src = Moves({"a.md": "a\n"})
    sh = Shell()
    sh.mount("/mnt/s", src, "live")
    sh.run("mv /mnt/s/a.md /mnt/s/b.md")
    r = sh.run("echo more >> /mnt/s/b.md")
    assert r.stderr == "" and src.tree()["/b.md"] == b"a\nmore\n"


def test_mount_names_what_the_source_cannot_do():
    src = MemorySource({"a.md": "a\n"})
    src.capabilities = frozenset({"write"})
    sh = Shell()
    sh.mount("/mnt/s", src, "staged")
    assert sh.run("mount").stdout == "memory:memory on /mnt/s type memory (rw,staged,no-delete,no-rename,no-mkdir)\n"


def test_the_tool_description_names_the_mounts():
    from topwater import tool_description
    sh = Shell()
    sh.mount("/mnt/vault", MemorySource(id="v"))
    sh.mount("/mnt/notes", MemorySource(id="n"), "staged")
    d = tool_description(sh)
    assert "`/mnt/vault` is a folder from outside, read-only." in d and "`/mnt/notes` is a folder from outside; your changes" in d


def test_a_replaced_folder_whose_files_moved_out():
    src = MemorySource({"a/x.md": "x\n", "a/y.md": "y\n", "b/z.md": "z\n", "c.md": "c\n", "d/e/f.md": "f\n"})
    sh = Shell()
    sh.mount("/mnt/s", src, "staged")
    sh.run("echo n > /mnt/s/b/w4.md; mv /mnt/s/a /mnt/s/tmpx && mv /mnt/s/d /mnt/s/a && mv /mnt/s/tmpx /mnt/s/d; mkdir /mnt/s/d/n7; "
           "mv /mnt/s/d/y.md /mnt/s/m0; rm -r /mnt/s/d; echo n > /mnt/s/a/w3.md")
    assert sh.apply_changes("/mnt/s").failed is None
    assert src.tree() == {"/a": None, "/a/e": None, "/a/e/f.md": b"f\n", "/a/w3.md": b"n\n", "/b": None, "/b/w4.md": b"n\n",
                          "/b/z.md": b"z\n", "/c.md": b"c\n", "/m0": b"y\n"}


def test_random_change_sets_apply_to_what_the_mount_shows():
    """Random mkdir, mv, rm and edits on a staged mount: applying the change set makes the source match the mount."""
    import random
    for seed in range(120):
        rnd = random.Random(seed)
        src = MemorySource({"a/x.md": "x\n", "a/y.md": "y\n", "b/z.md": "z\n", "c.md": "c\n", "d/e/f.md": "f\n"})
        sh = Shell()
        sh.mount("/mnt/s", src, "staged")
        for _ in range(rnd.randint(1, 8)):
            files = [p for p in sh.run("cd /mnt/s && find . -mindepth 1").stdout.split() if p]
            dirs = [p for p in files if sh.run(f"test -d /mnt/s/{p} && echo y").stdout] + ["."]
            pick = rnd.choice(files) if files else "."
            op = rnd.choice(["mkdir", "mv", "mv", "rm", "edit", "new", "swap"])
            if op == "mkdir":
                sh.run(f"mkdir /mnt/s/{rnd.choice(dirs)}/n{rnd.randint(0, 9)}")
            elif op == "mv" and files:
                sh.run(f"mv /mnt/s/{pick} /mnt/s/{rnd.choice(dirs)}/m{rnd.randint(0, 9)} 2>/dev/null")
            elif op == "rm" and files:
                sh.run(f"rm -r /mnt/s/{pick}")
            elif op == "edit" and files:
                sh.run(f"test -f /mnt/s/{pick} && echo e >> /mnt/s/{pick}")
            elif op == "new":
                sh.run(f"echo n > /mnt/s/{rnd.choice(dirs)}/w{rnd.randint(0, 9)}.md 2>/dev/null")
            elif op == "swap" and len(files) > 1:
                a, b = rnd.sample(files, 2)
                sh.run(f"mv /mnt/s/{a} /mnt/s/tmpx 2>/dev/null && mv /mnt/s/{b} /mnt/s/{a} 2>/dev/null && mv /mnt/s/tmpx /mnt/s/{b} 2>/dev/null")
        r = sh.apply_changes("/mnt/s")
        assert r.failed is None, (seed, r)
        want = {}
        for p in sh.run("cd /mnt/s && find . -mindepth 1 | sort").stdout.split():
            full = "/mnt/s/" + p[2:]
            want["/" + p[2:]] = sh.run(f"cat {full}").stdout.encode() if not sh.run(f"test -d {full} && echo d").stdout else None
        assert src.tree() == want, seed


# ---------- files from every port ----------

MOUNTS_DIR = Path(__file__).resolve().parents[2] / "tests" / "mounts"
FIXTURE_COMMANDS = ["cat /mnt/s/a.txt; echo more >> /mnt/s/a.txt; mv /mnt/s/d/b.txt /mnt/s/d/c.txt; rm /mnt/s/old.txt; mkdir /mnt/s/e",
                    "echo y > /mnt/l/x.txt", "echo z > /mnt/l/x.txt"]


def make_fixture() -> bytes:
    """The scenario each port records into tests/mounts/mounts-from-<port>.topwater (TOPWATER_WRITE_FIXTURES=1 rewrites it)."""
    src = MemorySource({"a.txt": "a\n", "d/b.txt": "b\n", "old.txt": "o\n"})
    live = MemorySource({"x.txt": "x\n"}, id="live")
    sh = Shell()
    sh.mount("/mnt/s", src, "staged")
    sh.mount("/mnt/l", live, "live")
    rec = sh.start_recording(metadata={"source": "python port"})
    sh.run(FIXTURE_COMMANDS[0])
    sh.run(FIXTURE_COMMANDS[1])
    live.set("x.txt", "theirs\n")
    sh.run(FIXTURE_COMMANDS[2])
    rec.stop()
    buf = io.BytesIO()
    rec.save(buf)
    return buf.getvalue()


def test_this_ports_mount_fixture_is_current():
    path = MOUNTS_DIR / "mounts-from-python.topwater"
    if os.environ.get("TOPWATER_WRITE_FIXTURES") == "1" or not path.exists():
        path.write_bytes(make_fixture())
    check_fixture(path.read_bytes())


@pytest.mark.parametrize("port", ["csharp", "typescript"])
def test_mount_files_from_other_ports(port):
    path = MOUNTS_DIR / f"mounts-from-{port}.topwater"
    if not path.exists():
        pytest.skip(f"written by the {port} tests")
    check_fixture(path.read_bytes())


def check_fixture(raw: bytes) -> None:
    sh = Shell.load(raw)
    assert [(m.path, m.mode, m.type, m.id, m.held) for m in sorted(sh.mounts, key=lambda m: m.path)] == \
        [("/mnt/l", "live", "memory", "live", 1), ("/mnt/s", "staged", "memory", "memory", 4)]
    assert [(c.kind, c.path, c.to) for c in sh.snapshot_changes("/mnt/s")] == \
        [("rename", "/d/b.txt", "/d/c.txt"), ("mkdir", "/e", None), ("write", "/a.txt", None), ("delete", "/old.txt", None)]
    assert sh.snapshot_changes("/mnt/l")[0].content == b"z\n"
    rec = Recording.load(raw)
    assert [c.command for c in rec.calls] == FIXTURE_COMMANDS
    assert any(x.op == "list" for x in rec.mount_start) and any(x.op == "read" for x in rec.calls[0].mounts)
    assert any(x.op == "write" for x in rec.calls[1].mounts) and "changed in the source" in rec.calls[2].stderr
    report = rec.replay()
    assert report.matches, report.differences


def test_the_documented_source_runs(capsys):
    """docs/mounts.md's complete Python source, as written there."""
    import re
    doc = (Path(__file__).resolve().parents[2] / "docs" / "mounts.md").read_text()
    code = re.search(r"```python\n(.*?)```", doc, re.S).group(1)
    scope: dict = {}
    exec(compile(code, "docs/mounts.md", "exec"), scope)
    out = capsys.readouterr().out
    assert "failed=None" in out and scope["wiki"]._pages["plan.md"] == b"# Plan\nFinal\n" and scope["wiki"]._pages["notes.md"] == b"# Notes\n"


@pytest.mark.skipif(not LocalFolderSource._AT, reason="needs descriptor-relative opens (Linux, macOS)")
def test_a_link_swapped_in_while_it_works_is_never_followed(tmp_path):
    import threading
    import time as _time
    root, outside = tmp_path / "root", tmp_path / "outside"
    (root / "d").mkdir(parents=True)
    (outside / "d").mkdir(parents=True)
    (root / "d" / "f.txt").write_text("inside\n")
    (outside / "d" / "f.txt").write_text("OUTSIDE\n")
    src = LocalFolderSource(root, writable=True, id="r")
    stop = False

    def swap():
        while not stop:
            try:
                os.rename(root / "d", root / ".real")
                os.symlink(outside / "d", root / "d")
                os.remove(root / "d")
                os.rename(root / ".real", root / "d")
            except OSError:
                pass
    t = threading.Thread(target=swap)
    t.start()
    leaks, end, n = 0, _time.time() + 1, 0
    try:
        while _time.time() < end:
            n += 1
            with contextlib.suppress(Exception):
                leaks += b"OUTSIDE" in src.read("/d/f.txt", 100)
            with contextlib.suppress(Exception):
                src.write(f"/d/w{n}.txt", b"x", None)
    finally:
        stop = True
        t.join()
    assert leaks == 0 and os.listdir(outside / "d") == ["f.txt"]
