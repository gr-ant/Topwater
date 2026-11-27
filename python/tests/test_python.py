"""python3 (commands/python.py over wasi.py): the command in the shell, its limits, and hostile programs. Needs the wasmtime
package; the interpreter comes from the repository's assets/python-wasm."""
import struct
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

pytest.importorskip("wasmtime")

from topwater import Shell  # noqa: E402
from topwater import wasi  # noqa: E402
from topwater.commands.python import PythonOptions  # noqa: E402
from topwater.mounts import MemorySource  # noqa: E402
from topwater.webaccess import WebPolicy  # noqa: E402

ARTIFACTS = str(Path(__file__).resolve().parents[2] / "assets" / "python-wasm")


def shell(**options) -> Shell:
    return Shell(**options).allow_python(PythonOptions(artifacts=ARTIFACTS))


def run(script: str, **options):
    return shell(**options).run(script)


# ---------- the command ----------

def test_opt_in_only():
    assert "python3" not in Shell().commands
    r = Shell().run("python3 -c 'print(1)'")
    assert r.exit_code == 127 and "command not found" in r.stderr


def test_program_argv_stdin_and_exit_codes():
    r = run("python3 -c 'import sys; print(sys.argv[1:], sys.stdin.read().upper())' a 'b c' <<< hi")
    assert r.stdout == "['a', 'b c'] HI\n\n" and r.exit_code == 0
    assert run("python3 -c 'raise SystemExit(7)'").exit_code == 7
    r = run("python3 -c 'import sys; sys.exit(\"bye\")'")
    assert (r.exit_code, r.stderr) == (1, "bye\n")
    r = run("python3 -c '1/0'")
    assert r.exit_code == 1 and r.stderr.endswith("ZeroDivisionError: division by zero\n")


def test_files_are_the_machines_files():
    sh = shell()
    sh.run("mkdir -p /tmp/w && cd /tmp/w && printf 'a\\nb\\n' > in.txt")
    r = sh.run("python3 -c 'open(\"out.txt\", \"w\").write(open(\"in.txt\").read().upper())' && cat out.txt")
    assert r.stdout == "A\nB\n"
    # bytes survive both ways
    sh.run("printf '\\377\\000\\001' > bin")
    assert sh.run("python3 -c 'print(list(open(\"bin\", \"rb\").read()))'").stdout == "[255, 0, 1]\n"
    sh.run("python3 -c 'open(\"bin2\", \"wb\").write(bytes([0, 200, 255]))'")
    assert sh.fs.read_bytes("/tmp/w/bin2") == bytes([0, 200, 255])
    # a file left open is saved at exit, as a real process's is
    assert sh.run("python3 -c 'f = open(\"u\", \"w\"); f.write(\"kept\")'; cat u").stdout == "kept"
    # owner and mode come from the agent's user and umask, as for any command
    assert sh.run("ls -l out.txt | cut -d' ' -f1,3").stdout == "-rw-r--r-- agent\n"


def test_scripts_modules_shebang_and_substitution():
    sh = shell()
    sh.run("mkdir -p /tmp/w && cd /tmp/w")
    sh.run("printf 'import sys\\nprint(\"script\", sys.argv)\\n' > s.py")
    assert sh.run("python3 s.py x").stdout == "script ['s.py', 'x']\n"
    sh.run("printf '#!/usr/bin/env python3\\nprint(\"shebang\")\\n' > t.py && chmod +x t.py")
    assert sh.run("./t.py").stdout == "shebang\n"
    sh.run("printf 'print(\"module\", __name__)\\n' > m.py")
    assert sh.run("python3 -m m").stdout == "module __main__\n"
    assert sh.run("echo 'print(2**10)' | python3").stdout == "1024\n"
    assert sh.run("x=$(python3 -c 'print(\"in\")'); echo \"[$x]\"").stdout == "[in]\n"
    assert sh.run("python3 -c 'print(\"a\\nb\")' | wc -l").stdout == "2\n"
    r = sh.run("python3 nope.py")
    assert r.exit_code == 2 and "can't open file" in r.stderr


def test_environment_is_the_exported_variables():
    r = run("export A=1; B=2; C=3 python3 -c 'import os; print(os.environ.get(\"A\"), os.environ.get(\"B\"), os.environ.get(\"C\"), os.getcwd())'")
    assert r.stdout == "1 None 3 /home/agent\n"


def test_no_state_between_calls():
    sh = shell()
    sh.run("python3 -c 'import builtins, sys; builtins.LEFT = 1; sys.path.append(\"/x\")'")
    assert sh.run("python3 -c 'import builtins, sys; print(hasattr(builtins, \"LEFT\"), \"/x\" in sys.path)'").stdout == "False False\n"


def test_fork_and_snapshot_keep_working():
    sh = shell()
    sh.run("echo 'print(5)' > /tmp/p.py")
    fork = sh.fork()
    assert fork.run("python3 /tmp/p.py").stdout == "5\n"
    snap = sh.snapshot()
    sh.run("rm /tmp/p.py")
    sh.restore(snap)
    assert sh.run("python3 /tmp/p.py").stdout == "5\n"


def test_recording_replays_with_python_enabled():
    sh = shell()
    rec = sh.start_recording()
    sh.run("python3 -c 'open(\"/tmp/r\", \"w\").write(\"rec\")'")
    rec.stop()
    again = rec.recording.shell_at(0, configure=lambda s: s.allow_python(PythonOptions(artifacts=ARTIFACTS)))  # runs call 0 again
    assert again.run("python3 -c 'print(open(\"/tmp/r\").read())'").stdout == "rec\n"


def test_unavailable_add_on_fails_in_host_code(tmp_path):
    with pytest.raises(wasi.PythonUnavailable):
        Shell().allow_python(PythonOptions(artifacts=str(tmp_path)))
    for name in wasi.ARTIFACT_SHA256:
        (tmp_path / name).write_bytes(b"not the interpreter")
    with pytest.raises(wasi.PythonUnavailable, match="sha256"):
        Shell().allow_python(PythonOptions(artifacts=str(tmp_path)))


def test_compile_cache_dir(tmp_path):
    wasi.runtime(ARTIFACTS, tmp_path)
    assert len(list(tmp_path.glob("*.cwasm"))) == 1
    wasi._RUNTIMES.pop((str(Path(ARTIFACTS).resolve()), str(tmp_path)))
    sh = Shell().allow_python(PythonOptions(artifacts=ARTIFACTS, cache_dir=str(tmp_path)))  # deserialized, not compiled
    assert sh.run("python3 -c 'print(3)'").stdout == "3\n"


# ---------- mounts ----------

def test_read_only_mount_refuses_writes():
    sh = shell()
    src = MemorySource({"a.txt": "source\n"})
    sh.mount("/mnt/ro", src)
    assert sh.run("python3 -c 'print(open(\"/mnt/ro/a.txt\").read(), end=\"\")'").stdout == "source\n"
    r = sh.run("python3 -c 'open(\"/mnt/ro/a.txt\", \"w\").write(\"x\")'")
    assert r.exit_code == 1 and "PermissionError" in r.stderr
    r = sh.run("python3 -c 'open(\"/mnt/ro/new.txt\", \"w\")'")
    assert "PermissionError" in r.stderr
    assert src.calls == [c for c in src.calls if c[0] in ("list", "stat", "read")]


def test_staged_mount_writes_become_changes():
    sh = shell()
    src = MemorySource({"a.txt": "old\n"})
    sh.mount("/mnt/st", src, "staged")
    sh.run("python3 -c 'open(\"/mnt/st/a.txt\", \"a\").write(\"more\\n\"); open(\"/mnt/st/b.txt\", \"w\").write(\"b\\n\")'")
    changes = {(c.kind, c.path, c.content) for c in sh.pending_changes("/mnt/st")}
    assert changes == {("write", "/a.txt", b"old\nmore\n"), ("write", "/b.txt", b"b\n")}
    assert all(op != "write" for op, _ in src.calls)  # nothing reached the source


# ---------- the web ----------

def _transport(method, url, headers, body, timeout, limit):
    if url.endswith("/redirect"):
        return 302, "Found", [("Location", "https://api.test/data")], b""
    return 200, "OK", [("Content-Type", "application/json")], b'{"url": "%s", "body": "%s"}' % (url.encode(), (body or b"").decode().encode())


def test_http_goes_through_the_web_policy():
    r = run("python3 -c 'import urllib.request; urllib.request.urlopen(\"https://example.com\")'")
    assert r.exit_code == 1 and "Could not resolve host: example.com" in r.stderr
    sh = shell().allow_web(WebPolicy(domains=["api.test"], methods=["GET", "POST"], transport=_transport))
    r = sh.run("python3 -c 'import urllib.request, json; print(json.load(urllib.request.urlopen(\"https://api.test/redirect\"))[\"url\"])'")
    assert r.stdout == "https://api.test/data\n", r.stderr
    r = sh.run("python3 -c 'import requests; print(requests.post(\"https://api.test/p\", data=b\"hi\").json()[\"body\"])'")
    assert r.stdout == "hi\n", r.stderr
    r = sh.run("python3 -c 'import urllib.request; urllib.request.urlopen(\"https://other.test/\")'")
    assert "other.test is not an allowed host" in r.stderr
    r = sh.run("python3 -c 'import urllib.request; urllib.request.urlopen(\"http://api.test/\")'")
    assert "only https:// URLs are allowed" in r.stderr


def test_http_requests_count_against_the_policy():
    sh = shell().allow_web(WebPolicy(domains=["api.test"], max_requests_per_call=2, transport=_transport))
    r = sh.run("python3 -c '\nimport urllib.request\nfor _ in range(3): urllib.request.urlopen(\"https://api.test/x\")'")
    assert "the limit of 2 requests in one call was reached" in r.stderr


def test_http_bridge_rejects_bad_records():
    def record(*fields, count=None):
        out = b""
        for f in fields[:2]:
            out += struct.pack("<I", len(f)) + f
        out += struct.pack("<I", count if count is not None else 0)
        for f in fields[2:]:
            out += struct.pack("<I", len(f)) + f
        return out
    assert wasi.decode_request(record(b"get", b"https://a/", b"")) == ("GET", "https://a/", [], b"")
    for bad, why in [(record(b"TRACE", b"https://a/", b""), "unsupported"), (record(b"GET", b"https://a/ x", b""), "spaces"),
                     (record(b"GET", b"https://a/", b"", count=1000), "too many"), (record(b"GET", b"https://a/", b"") + b"x", "trailing"),
                     (record(b"GET", b"https://a/", b"Bad Name", b"v", b"", count=1), "header name"),
                     (record(b"GET", b"https://a/", b"X", b"a\r\nb", b"", count=1), "header value"), (b"\xff\xff\xff\xff", "too long")]:
        with pytest.raises(ValueError, match=why):
            wasi.decode_request(bad)


# ---------- hostile programs ----------

def test_busy_loop_and_sleep_end_at_the_time_limit():
    r = run("timeout 1 python3 -c 'while True: pass'; echo $?")
    assert r.stdout == "124\n"
    r = run("timeout 1 python3 -c 'import time; time.sleep(60)'; echo $?")
    assert r.stdout == "124\n"
    r = run("python3 -c 'while True: pass'", max_call_duration=1)
    assert r.exit_code == 124 and r.trailer is not None


def test_memory_is_capped():
    sh = Shell().allow_python(PythonOptions(artifacts=ARTIFACTS, max_memory=96 << 20))
    r = sh.run("python3 -c 'x = bytearray(200 << 20)'")
    assert r.exit_code == 1 and r.stderr.endswith("MemoryError\n")
    r = sh.run("python3 -c '\nx = []\nwhile True: x.append(bytearray(1 << 20))'")
    assert r.exit_code == 1 and "MemoryError" in r.stderr
    assert sh.run("python3 -c 'print(1)'").stdout == "1\n"  # the next call starts fresh


def test_recursion_and_stack():
    r = run("python3 -c 'def f(): f()\nf()'")
    assert r.exit_code == 1 and r.stderr.endswith("RecursionError: maximum recursion depth exceeded\n")
    r = run("python3 -c 'import json; json.loads(\"[\" * 200000)'; echo next")
    assert r.stdout == "next\n" and ("RecursionError" in r.stderr or "ran out of stack" in r.stderr)


def test_huge_files_hit_the_disk_cap():
    sh = shell(max_file_system_bytes=8 << 20)
    r = sh.run("python3 -c '\nf = open(\"/tmp/big\", \"wb\")\nwhile True: f.write(b\"x\" * 65536)'")
    assert r.exit_code == 1 and "No space left on device" in r.stderr
    assert sh.fs.used_bytes <= 8 << 20
    r = sh.run("python3 -c 'import os; os.truncate(\"/tmp/big\", 1 << 40)'")
    assert r.exit_code == 1


def test_output_flood_ends_the_call():
    r = run("python3 -c 'while True: print(\"x\" * 1000)'", max_string_bytes=1 << 20)
    assert r.exit_code == 1 and "output too large" in r.stderr


def test_paths_stay_in_the_machine():
    sh = shell()
    host_file = str(Path(__file__).resolve())  # exists on the host, not in the machine
    r = sh.run(f"python3 -c 'open(\"{host_file}\")'")
    assert "FileNotFoundError" in r.stderr
    assert sh.run("python3 -c 'print(open(\"/../../../etc/hostname\").read(), end=\"\")'").stdout == "topwater\n"
    sh.run("ln -s /etc/hostname /tmp/link && ln -s /tmp/loop /tmp/loop")
    assert sh.run("python3 -c 'print(open(\"/tmp/link\").read(), end=\"\")'").stdout == "topwater\n"
    assert "[Errno 32]" in sh.run("python3 -c 'open(\"/tmp/loop\")'").stderr  # ELOOP, in WASI's numbering


def test_the_stdlib_zip_is_read_only_and_invisible_to_commands():
    sh = shell()
    r = sh.run(f"python3 -c 'open(\"{wasi.STDLIB_PATH}\", \"ab\")'")
    assert r.exit_code == 1 and "OSError" in r.stderr
    r = sh.run(f"python3 -c 'import os; os.remove(\"{wasi.STDLIB_PATH}\")'")
    assert r.exit_code == 1
    assert sh.run("python3 -c 'import json; print(json.dumps(1))'").stdout == "1\n"
    assert sh.run("ls /usr/local/lib").stdout == ""


def test_descriptor_abuse_fails_cleanly():
    r = run("python3 -c 'import os; os.read(999, 1)'")
    assert "Bad file descriptor" in r.stderr
    r = run("python3 -c '\nfs = [open(\"/etc/hostname\") for _ in range(5000)]'")
    assert r.exit_code == 1 and "OSError" in r.stderr
    r = run("python3 -c 'import os; os.close(3); os.close(0); os.close(1); print(\"lost\")'; echo after")
    assert r.stdout == "after\n"


def test_no_processes_threads_or_sockets():
    for code, error in [("import subprocess; subprocess.run([\"ls\"])", "OSError"),
                        ("import socket; socket.socket().connect((\"1.1.1.1\", 80))", "OSError"),
                        ("import threading; threading.Thread(target=print).start()", "RuntimeError")]:
        r = run(f"python3 -c '{code}'")
        assert r.exit_code == 1 and error in r.stderr, (code, r.stderr)


def test_permissions_hold():
    r = run("python3 -c 'open(\"/etc/passwd\", \"a\").write(\"x\")'")
    assert "PermissionError" in r.stderr
    r = run("python3 -c 'import os; os.listdir(\"/root\")'")
    assert "PermissionError" in r.stderr
