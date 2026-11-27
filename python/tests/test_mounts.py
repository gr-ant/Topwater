"""Mounted sources (docs/mounts.md): tests/mounts/cases.json, which the C# and TypeScript ports run too, then what only
this port checks: hostile sources, limits, snapshots, recordings and the local folder source."""
from __future__ import annotations

import asyncio
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

    def list(self, path):
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
    sh.mount("/mnt/h", Hostile())
    assert sh.run("ls /mnt/h").stdout == "ok\nz0\nz1\nz2\n"
    assert [w.split(": ", 1)[1] for w in sh.mounts[0].warnings] == ["listing stopped at 4 entries",
                                                                     "skipped 9 entries with names that can't be files"]
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
            raise OSError("the service is down")
    src.hook = hook
    assert sh.run("cat /mnt/s/a.txt").stderr == "cat: /mnt/s/a.txt: the service is down\n"
    fail["on"] = False
    assert sh.run("cat /mnt/s/a.txt").stdout == "a\n"
    sh.run("echo b > /mnt/s/a.txt")
    fail["on"] = True
    r = sh.apply_changes("/mnt/s")
    assert r.failed.path == "/a.txt" and r.error == "the service is down" and not r.conflict
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
    loaded = Shell.load(raw)
    info = loaded.mounts[0]
    assert (info.path, info.mode, info.type, info.id) == ("/mnt/s", "staged", "memory", "memory")
    assert info.error == "unavailable: no source for memory:memory"
    assert [c.kind for c in loaded.pending_changes("/mnt/s")] == ["write", "delete"]
    assert "Permission denied" in loaded.run("ls /mnt/s").stderr
    loaded.mount("/mnt/s", src, "staged")
    assert loaded.run("ls /mnt/s; cat /mnt/s/b.txt").stdout == "b.txt\nnew\n"
    assert loaded.pending_changes("/mnt/s") == sh.pending_changes("/mnt/s")
    # restoring into a shell that has the source lists it again at the next call
    other = Shell()
    other.mount("/mnt/other", src)
    other.restore(sh.snapshot())
    assert other.run("cat /mnt/s/b.txt").stdout == "new\n"
    # the source changed meanwhile: applying conflicts, against the version the change was made on
    src.set("b.txt", "theirs\n")
    r = loaded.apply_changes("/mnt/s")
    assert r.conflict and r.failed.path == "/b.txt"


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
    assert r.stdout == "b\n" and r.stderr == "cat: /mnt/s/c.txt: read /c.txt is not in the Topwater recording\n"


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
