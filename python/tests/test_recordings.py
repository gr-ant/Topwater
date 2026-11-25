"""Recordings and the .topwater file: what the recorder keeps, the file every port writes and reads (spec/topwater-file),
the old JSON Lines recordings, and loading treated as hostile."""
import io
import json
import sys
import unittest
import zipfile
from pathlib import Path
from types import SimpleNamespace as L

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Recording, RecordingError, Shell, ShellSnapshot  # noqa: E402
from topwater.snapshot import SnapshotError, SnapshotVersionError  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
RECORDINGS = ROOT / "tests" / "Topwater.Tests" / "recordings"
SPEC = ROOT / "spec" / "topwater-file"

# from-python.topwater was written with:
#
#   sh = Shell()
#   rec = sh.start_recording(snapshot_every=2, metadata={"source": "python port", "task": "cross-port fixture"})
#   sh.run(r"mkdir -p work && cd work && printf 'buy milk\nfix bug\n' > todo.txt && wc -l todo.txt")
#   sh.run('greet() { echo "hi $1"; }; export TEAM=core; nums=(3 1 2); ln -s todo.txt latest; greet you')
#   sh.run("rm todo.txt; echo done > out.txt; ls; cat latest; exit 3")
#   rec.save(RECORDINGS / "from-python.topwater")
#
# The other ports write from-csharp.topwater and from-typescript.topwater with the same calls on a deterministic shell.
CROSS_PORT = ("from-python.topwater", "from-csharp.topwater", "from-typescript.topwater")


def saved(rec) -> bytes:
    out = io.BytesIO()
    rec.save(out)
    return out.getvalue()


def rezip(data: bytes, edit) -> bytes:
    """The zip's entries as a dict, changed by edit(entries), zipped again."""
    src = zipfile.ZipFile(io.BytesIO(data))
    entries = {n: src.read(n) for n in src.namelist()}
    edit(entries)
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as z:
        for n, b in entries.items():
            z.writestr(n, b)
    return out.getvalue()


def recorded(*commands, every=0) -> Recording:
    sh = Shell()
    with sh.start_recording(snapshot_every=every) as r:
        for c in commands:
            sh.run(c)
    return r.recording


class CrossPortFiles(unittest.TestCase):
    def test_every_ports_file_loads(self):
        names = [n for n in CROSS_PORT if (RECORDINGS / n).exists()]
        self.assertIn("from-python.topwater", names)
        for name in names:
            with self.subTest(name):
                path = RECORDINGS / name
                rec = Recording.load(path)
                self.assertEqual(3, len(rec.calls))
                self.assertEqual(r"mkdir -p work && cd work && printf 'buy milk\nfix bug\n' > todo.txt && wc -l todo.txt", rec.calls[0].command)
                self.assertEqual("cross-port fixture", rec.metadata["task"])
                self.assertEqual(3, rec.calls[2].exit_code)
                self.assertLessEqual({"/home/agent/work", "/home/agent/work/todo.txt"}, set(rec.calls[0].files))
                self.assertLessEqual({"/home/agent/work/todo.txt", "/home/agent/work/out.txt"}, set(rec.calls[2].files))
                probe = rec.shell_at(1).run("cd /home/agent/work; greet x; echo $TEAM ${nums[@]}; readlink latest")
                self.assertEqual("hi x\ncore 3 1 2\ntodo.txt\n", probe.stdout)
                self.assertEqual("done\n", Shell.load(path).run("cat /home/agent/work/out.txt").stdout)
                if name == "from-python.topwater":  # replays need not match across ports
                    self.assertTrue(rec.replay().matches, rec.replay().differences)

    def test_the_old_json_lines_recordings_load(self):
        basics = Recording.load(RECORDINGS / "basics.jsonl")
        self.assertEqual(6, len(basics.calls))
        self.assertEqual(1, basics.random_seed)
        self.assertEqual("date,region,amount\n", basics.create_shell().run("head -1 ~/data/sales.csv").stdout)
        ts = Recording.load(RECORDINGS / "from-typescript.jsonl")
        self.assertEqual(3, len(ts.calls))
        self.assertEqual("typescript port", ts.metadata["source"])
        self.assertEqual("2\n", ts.create_shell().run("wc -l < ~/todo.txt").stdout)
        self.assertEqual("3 1 2 31 core\nhello py\n", ts.shell_at(1).run('echo "${nums[@]} ${ages[ann]} $TEAM"; greet py').stdout)
        # and they convert: saved again they are .topwater files with the same calls and checkpoints
        again = Recording.load(saved(ts))
        self.assertEqual([c.command for c in ts.calls], [c.command for c in again.calls])
        self.assertEqual(ts.start_time, again.start_time)
        self.assertEqual([c.snapshot is not None for c in ts.calls], [c.snapshot is not None for c in again.calls])

    def test_written_files_match_the_schemas(self):
        try:
            import jsonschema
            from referencing import Registry, Resource
        except ImportError:
            self.skipTest("jsonschema is not installed")
        schemas = {p.name: json.loads(p.read_text()) for p in SPEC.glob("*.schema.json")}
        registry = Registry().with_resources((s["$id"], Resource.from_contents(s)) for s in schemas.values())

        def check(name, doc):
            jsonschema.Draft202012Validator(schemas[name], registry=registry).validate(doc)

        z = zipfile.ZipFile(io.BytesIO(saved(recorded("echo hi > f; ln -s f l; ln f h", "exit 2 # x", every=1))))
        check("manifest.schema.json", json.loads(z.read("manifest.json")))
        for n in z.namelist():
            if n.startswith("checkpoints/"):
                check("checkpoint.schema.json", json.loads(z.read(n)))
        for line in z.read("history.jsonl").decode().splitlines():
            check("history.schema.json", json.loads(line))


class Recorder(unittest.TestCase):
    def test_calls_keep_what_they_returned_and_the_files_they_touched(self):
        sh = Shell()
        sh.run("echo old > keep; echo x > gone; echo same > same")
        rec = sh.start_recording()
        sh.run("echo new > keep; rm gone; touch made; chmod 600 same; cat same")
        sh.run("echo only output")
        sh.run("ln -s keep link")
        rec.stop()
        sh.run("echo after stop")
        calls = rec.recording.calls
        self.assertEqual(3, len(calls))
        self.assertEqual(("same\n", 0), (calls[0].stdout, calls[0].exit_code))
        self.assertEqual([f"/home/agent/{n}" for n in ("gone", "keep", "made", "same")],
                         [p for p in calls[0].files if p != "/home/agent"])
        self.assertEqual([], calls[1].files)
        self.assertEqual(["/home/agent", "/home/agent/link"], calls[2].files)
        self.assertTrue(all(c.time is not None and c.elapsed_ms >= 0 for c in calls))

    def test_the_end_state_is_always_kept_and_other_checkpoints_only_when_asked(self):
        sh = Shell()
        r = sh.start_recording(snapshot_every=3)
        for i in range(7):
            sh.run(f"echo {i} > n{i}")
            if i == 1:
                r.snapshot_now()
        self.assertEqual([1, 2, 5, 6], [c.index for c in r.recording.calls if c.snapshot is not None])
        self.assertEqual("6\n", r.recording.shell_at(6).run("cat n6").stdout)
        self.assertEqual("4\n", r.recording.shell_at(4).run("cat n4; cat n5 2>/dev/null").stdout)
        self.assertEqual("", r.recording.shell_at(-1).run("ls").stdout)
        with self.assertRaises(IndexError):
            r.recording.shell_at(7)
        with self.assertRaises(RuntimeError):
            sh.start_recording()

    def test_snapshot_now_before_any_call_moves_the_start(self):
        sh = Shell()
        r = sh.start_recording()
        sh.fs.write_text("/home/agent/seeded", "by the host\n")
        r.snapshot_now()
        sh.run("cat seeded")
        self.assertEqual([], r.recording.calls[0].files)
        self.assertTrue(r.recording.replay().matches)

    def test_replay_reports_differences_by_field(self):
        rec = recorded("echo one; echo err >&2; exit 4")
        rec.calls[0].stdout = "two\n"
        rec.calls[0].exit_code = 5
        report = rec.replay()
        self.assertFalse(report.matches)
        self.assertEqual([("exitCode", "5", "4"), ("stdout", "two\n", "one\n")],
                         [(d.field, d.expected, d.actual) for d in report.differences])
        self.assertTrue(rec.replay(normalize=lambda s: "x").matches)

    def test_the_file_holds_the_end_machine_history_and_checkpoints(self):
        rec = recorded("echo a > a", "echo b > b", "rm a", every=2)
        data = saved(rec)
        z = zipfile.ZipFile(io.BytesIO(data))
        self.assertEqual({"manifest.json", "history.jsonl", "checkpoints/0.json", "checkpoints/2.json", "checkpoints/3.json"},
                         {n for n in z.namelist() if not n.startswith("blobs/")})
        self.assertEqual(z.read("checkpoints/3.json"), z.read("manifest.json"))
        lines = [json.loads(x) for x in z.read("history.jsonl").decode().splitlines()]
        self.assertEqual(("topwater-history", 1, "python"), (lines[0]["format"], lines[0]["version"], lines[0]["topwater"]))
        self.assertEqual(["index", "command", "stdout", "stderr", "exitCode", "elapsedMs", "time", "files"], list(lines[3]))
        # as a snapshot it is the end machine; ShellSnapshot.load reads it too
        self.assertEqual("b\n", Shell.load(data).run("cat a b 2>/dev/null").stdout)
        self.assertEqual("agent", ShellSnapshot.load(data).user)
        loaded = Recording.load(data)
        self.assertEqual(rec.calls[1].time, loaded.calls[1].time)
        self.assertEqual(rec.calls[2].files, loaded.calls[2].files)

    def test_a_snapshot_is_a_recording_without_calls(self):
        sh = Shell()
        sh.run("echo hi > f")
        out = io.BytesIO()
        sh.save(out)
        rec = Recording.load(out.getvalue())
        self.assertEqual([], rec.calls)
        self.assertEqual("hi\n", rec.create_shell().run("cat f").stdout)
        self.assertEqual("hi\n", Recording.load(saved(rec)).shell_at(-1).run("cat f").stdout)

    def test_a_hand_built_recording_writes_its_newest_checkpoint_as_the_machine(self):
        rec = recorded("echo 1 > f", "echo 2 > f", every=1)
        rec.calls[1].snapshot = None
        self.assertEqual("1\n", Shell.load(saved(rec)).run("cat f").stdout)


class HostileFiles(unittest.TestCase):
    def rejected(self, data, limits=None, error=RecordingError) -> str:
        with self.assertRaises(error) as e:
            Recording.load(data, limits=limits)
        return str(e.exception)

    def test_the_entry_count_is_read_before_the_entries(self):
        many = io.BytesIO()
        with zipfile.ZipFile(many, "w") as z:
            for i in range(1100):
                z.writestr(f"blobs/{i}", b"")
        tiny = L(max_extract_entries=10)  # 4 x 10 + 1002
        for load in (lambda d: Recording.load(d, limits=tiny), lambda d: ShellSnapshot.load(d, tiny)):
            with self.assertRaises(SnapshotError) as e:
                load(many.getvalue())
            self.assertIn("more than 1042 zip entries", str(e.exception))
        # an end record that understates the count doesn't help: zipfile reads the central directory by its size
        data = bytearray(many.getvalue())
        end = data.rfind(b"PK\x05\x06")
        data[end + 8:end + 12] = (1).to_bytes(2, "little") * 2
        self.assertIn("more than 1042 zip entries", self.rejected(bytes(data), tiny, SnapshotError))

    def test_checkpoints_are_checked(self):
        data = saved(recorded("echo a > a", "echo b > b", every=1))

        def with_entries(edit):
            return rezip(data, edit)

        for name in ("checkpoints/01.json", "checkpoints/3.json", "checkpoints/٣.json", "checkpoints/x.json", "checkpoints/1.json/"):
            with self.subTest(name):
                self.assertIn("bad checkpoint", self.rejected(with_entries(lambda e: e.update({name: e["checkpoints/1.json"]}))))
        self.assertIn("checkpoints/0.json is missing", self.rejected(with_entries(lambda e: e.pop("checkpoints/0.json"))))
        self.assertIn("not a valid Topwater snapshot: bad path",
                      self.rejected(with_entries(lambda e: e.update({"checkpoints/1.json": e["checkpoints/1.json"].replace(b'"/home/agent/a"', b'"/../a"')})), error=SnapshotError))

    def test_at_most_1000_checkpoints_holding_4_file_systems(self):
        def calls(n):
            return [json.dumps({"index": i, "command": "true", "stdout": "", "stderr": "", "exitCode": 0}).encode() for i in range(n)]

        def thousand(e):  # counted from the names, before any is read
            e["history.jsonl"] = b"\n".join([e["history.jsonl"].split(b"\n")[0]] + calls(1000))
            e.update({f"checkpoints/{n}.json": b"not read" for n in range(1, 1001)})
        self.assertIn("more than 1000 checkpoints", self.rejected(rezip(saved(recorded()), thousand)))
        big = recorded(*["printf '%010000d%010000d%010000d' 1 2 3 > f"] * 6, every=1)
        self.assertIn("4 x MaxFileSystemBytes", self.rejected(saved(big), L(max_file_system_bytes=40_000)))
        Recording.load(saved(big), limits=L(max_file_system_bytes=70_000))

    def test_history_is_read_capped(self):
        rec = recorded("printf '%05000d' 0")
        limits = L(max_string_bytes=1000, max_extract_entries=1)  # 4 x 1000 + 1024 x 1
        self.assertIn("history.jsonl is larger than 5024 bytes", self.rejected(saved(rec), limits, SnapshotError))

    def test_the_files_options_lower_limits_but_never_raise_them(self):
        rec = recorded("true")
        rec.options.update(max_file_system_bytes=1 << 40, max_string_bytes=1000, max_loop_iterations=-5, max_call_duration=float("inf"))
        loaded = Recording.load(saved(rec), limits=L(max_file_system_bytes=1 << 30, max_call_duration=30.0))
        self.assertEqual((1 << 30, 1000, 0, 30.0), tuple(loaded.options[k] for k in
                                                         ("max_file_system_bytes", "max_string_bytes", "max_loop_iterations", "max_call_duration")))
        self.assertEqual(1 << 30, loaded.create_shell().max_file_system_bytes)

        def header(edit):
            def change(e):
                lines = e["history.jsonl"].split(b"\n")
                h = json.loads(lines[0])
                edit(h)
                e["history.jsonl"] = b"\n".join([json.dumps(h).encode()] + lines[1:])
            return rezip(saved(rec), change)

        self.assertIn("bad user", self.rejected(header(lambda h: h["options"].update(user="../../etc"))))
        self.assertIn("bad maxStringBytes", self.rejected(header(lambda h: h["options"].update(maxStringBytes=True))))
        self.assertIn("unknown format version", self.rejected(header(lambda h: h.update(version=0))))
        with self.assertRaises(SnapshotVersionError) as e:
            Recording.load(header(lambda h: h.update(version=2, options="changed")))
        self.assertEqual("history format version 2 is newer than this version of Topwater supports (1); upgrade Topwater to load it",
                         str(e.exception))

    def test_call_lines_are_checked(self):
        rec = recorded("true", "true")

        def line(edit):
            def change(e):
                lines = e["history.jsonl"].split(b"\n")
                c = json.loads(lines[2])
                edit(c)
                lines[2] = json.dumps(c).encode()
                e["history.jsonl"] = b"\n".join(lines)
            return rezip(saved(rec), change)

        self.assertIn("bad index", self.rejected(line(lambda c: c.update(index=True))))
        self.assertIn("must be strings", self.rejected(line(lambda c: c.update(command=None))))
        self.assertIn("bad exitCode", self.rejected(line(lambda c: c.update(exitCode=False))))
        self.assertIn("bad elapsedMs", self.rejected(line(lambda c: c.update(elapsedMs=-1))))
        self.assertIn("bad line 3: time", self.rejected(line(lambda c: c.update(time="2026-١١-25T09:00:00Z")), error=SnapshotError))

    def test_old_recordings_side_car_names_are_an_allow_list(self):
        start = io.BytesIO()
        Shell().save(start)
        header = {"format": "topwater-recording", "version": 1, "options": {}, "start": {"file": "x/start.zip"}}

        def jsonl(name):
            return (json.dumps({**header, "start": {"file": name}}) + "\n").encode()

        opened = []

        def opener(rel):
            opened.append(rel)
            return start.getvalue()

        for name in ("%2e%2e/x", ".%2e/x", " ../x", "\t../x", "x?y=1", "x#f", "/etc/x", "../x", "C:x", "a\\b", "https://x",
                     "", "a//b", "a/./b", "...", "x\n"):
            with self.subTest(name):
                with self.assertRaises(RecordingError) as e:
                    Recording.load(jsonl(name), open_snapshot=opener)
                self.assertIn("not a plain relative path", str(e.exception))
        self.assertEqual([], opened)
        self.assertEqual([], Recording.load(jsonl("rec.snapshots/start-1_A.zip"), open_snapshot=opener).calls)
        self.assertEqual(["rec.snapshots/start-1_A.zip"], opened)
        self.assertIn("load the recording from its path", self.rejected(jsonl("start.zip")))

    def test_old_recordings_are_capped_too(self):
        import base64
        start = io.BytesIO()
        Shell().save(start)
        ref = {"embedded": base64.b64encode(start.getvalue()).decode()}
        head = json.dumps({"format": "topwater-recording", "version": 1, "options": {}, "start": ref})
        calls = [json.dumps({"index": i, "command": "true", "stdout": "", "stderr": "", "exitCode": 0, "snapshot": ref}) for i in range(1000)]
        self.assertIn("more than 1000 snapshots", self.rejected("\n".join([head] + calls).encode()))
        self.assertIn("not base64", self.rejected(json.dumps({**json.loads(head), "start": {"embedded": "!!"}}).encode()))
        with self.assertRaises(SnapshotVersionError):
            Recording.load(json.dumps({"format": "topwater-recording", "version": 2}).encode())
        self.assertIn("the header is not JSON", self.rejected(b"hello\n"))
        self.assertIn("not a topwater-recording header", self.rejected(b'{"format":"x"}\n'))


if __name__ == "__main__":
    unittest.main()
