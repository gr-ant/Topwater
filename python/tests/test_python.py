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


def test_open_files_are_one_file():
    # what the program wrote and flushed is the file's, by any name, before it is closed (as one inode's contents are)
    sh = shell()
    prog = """
import os, shutil, tempfile
f = open('a', 'w'); f.write('hello'); f.flush()
print(repr(open('a').read()), os.stat('a').st_size, os.path.getsize('a'))
shutil.copy('a', 'b'); print(repr(open('b').read()))
t = open('t.tmp', 'w'); t.write('new'); t.flush(); os.replace('t.tmp', 'c'); t.write('er'); t.close()
print(repr(open('c').read()), os.path.exists('t.tmp'))
g = open('d', 'w'); g.write('AAAA'); g.flush(); h = open('d', 'a'); h.write('bb'); h.close(); g.close()
print(repr(open('d').read()))
with tempfile.TemporaryFile(dir='.') as tf:
    tf.write(b'x' * 10); tf.flush()
u = open('u', 'w'); u.write('gone'); os.unlink('u'); u.write('!'); u.close()
os.mkdir('dir'); v = open('dir/v', 'w'); v.write('moved'); os.rename('dir', 'dir2'); v.close()
print(repr(open('dir2/v').read()), os.path.exists('dir'))
"""
    sh.run("mkdir -p /tmp/o && cd /tmp/o")
    r = sh.run(f"python3 -c {sh_quote(prog)}; ls")
    assert r.stdout == ("'hello' 5 5\n'hello'\n'newer' False\n'AAAAbb'\n'moved' False\n"
                        "a\nb\nc\nd\ndir2\n"), r.stderr


def test_output_keeps_its_order():
    prog = "import sys\nfor i in (1, 2):\n    print('e%d' % i, file=sys.stderr, flush=True)\n    print('o%d' % i, flush=True)"
    assert run(f"python3 -c {sh_quote(prog)} 2>&1").stdout == "e1\no1\ne2\no2\n"
    assert run(f"x=$(python3 -c {sh_quote(prog)} 2>&1); echo \"$x\"").stdout == "e1\no1\ne2\no2\n"


def test_renames_and_links_answer_as_linux():
    prog = """
import errno, os
def t(f, *a):
    try:
        f(*a); print('ok')
    except OSError as e:
        print(type(e).__name__, errno.errorcode[e.errno])
open('f1', 'w').write('1')
t(os.rename, 'f1', 'f1'); print(open('f1').read())
os.makedirs('full/e')
t(os.rename, 'full', 'full/e'); print(os.path.isdir('full/e'))
t(os.rename, 'full', 'full/e/x')
t(os.link, 'full', 'l')
"""
    r = run(f"cd /tmp && python3 -c {sh_quote(prog)}")
    assert r.stdout == "ok\n1\nOSError EINVAL\nTrue\nOSError EINVAL\nPermissionError EPERM\n" and r.stderr == ""


def test_abort():
    r = run("python3 -c 'import os; os.abort()'")
    assert r.exit_code == 134 and r.stderr == "python3: fatal error: the program called abort()\n"


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
    import os
    cache = tmp_path / "cache"
    wasi.runtime(ARTIFACTS, cache)
    [cwasm] = cache.glob("*.cwasm")
    assert [p.name for p in cache.iterdir()] == [cwasm.name]  # no temporary file left
    if hasattr(os, "getuid"):
        assert cache.stat().st_mode & 0o777 == 0o700 and cwasm.stat().st_mode & 0o777 == 0o600
    key = (str(Path(ARTIFACTS).resolve()), str(cache))

    def reload():
        wasi._RUNTIMES.pop(key)
        return wasi.runtime(ARTIFACTS, cache)

    sh = Shell().allow_python(PythonOptions(artifacts=ARTIFACTS, cache_dir=str(cache)))  # deserialized, not compiled
    assert sh.run("python3 -c 'print(3)'").stdout == "3\n"
    # a file that is not the one this module and wasmtime wrote is never loaded: it is compiled again and replaced
    good = cwasm.read_bytes()
    cwasm.write_bytes(good[:32] + b"\0" + good[33:])
    assert reload().module is not None and cwasm.read_bytes() == good
    if hasattr(os, "getuid"):  # nor one others may write
        cwasm.chmod(0o666)
        calls = []
        original = wasi._load_cached
        try:
            wasi._load_cached = lambda *a: calls.append(original(*a)) or calls[-1]
            reload()
        finally:
            wasi._load_cached = original
        assert calls == [None]


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


def test_a_slow_mount_source_ends_at_the_time_limit():
    import asyncio
    sh = shell()
    src = MemorySource({"a.txt": "source\n"})
    sh.mount("/mnt/m", src)

    async def hook(op, path):
        if op == "read":
            await asyncio.sleep(3)
    src.hook = hook
    r = sh.run("timeout 1 python3 -c 'print(open(\"/mnt/\" + \"m/a.txt\").read())'; echo $?")
    assert r.stdout.endswith("124\n")


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


def test_time_limits_hold_however_calls_follow_each_other():
    # one ticker however close the calls are: three timeouts in a row all reach their limit
    r = run("for i in 1 2 3; do timeout 1 python3 -c 'while True: pass'; echo $?; done")
    assert r.stdout == "124\n124\n124\n"
    # many quick calls first do not make a later program's limit come early
    r = run("for i in $(seq 40); do python3 -c pass; done; "
            "timeout 5 python3 -c 'import time\nt = time.monotonic()\nwhile time.monotonic() - t < 2: pass'; echo $?")
    assert r.stdout == "0\n"


def test_a_cancelled_call_stops_its_guest():
    import asyncio
    import threading
    import time

    sh = shell()
    writer = "import time\nfor i in range(1000):\n    open(f'/tmp/w{i}', 'w').write('x')\n    time.sleep(0.01)"

    async def cancel(script):
        task = asyncio.ensure_future(sh.run_async(script))
        await asyncio.sleep(1)
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass

    asyncio.run(cancel(f"python3 -c {sh_quote(writer)}"))
    files = set(sh.fs.listdir("/tmp"))
    time.sleep(0.5)
    assert files and set(sh.fs.listdir("/tmp")) == files  # nothing was written after the cancel
    asyncio.run(cancel("python3 -c 'while True: pass'"))
    time.sleep(0.5)
    assert not [t for t in threading.enumerate() if t.name == "topwater-python3"]


def sh_quote(s: str) -> str:
    return "'" + s.replace("'", "'\\''") + "'"


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


def test_a_write_that_repeats_one_buffer_is_refused_before_it_is_copied():
    # Python has no os.writev here, so this is what a hand-made module could send: 1000 vectors over the same 64 MB
    table = b"".join(struct.pack("<II", 0, 64 << 20) for _ in range(1000))

    class Memory:  # wasmtime's Memory, reduced to what wasi.py calls; any copy of the data is a failure
        def data_len(self, caller):
            return 64 << 20

        def read(self, caller, start, stop):
            assert (start, stop) == (0, len(table)), "copied the data before checking the limit"
            return table

        def write(self, caller, data, ptr):
            pass

    g = wasi.Guest(Shell().fs, [], [], b"", 1 << 20, 256 << 20, b"", float("inf"))
    g.memory = Memory()
    g.fds.append(wasi._File(wasi._Node("/tmp/f", bytearray(), 0, 0), False, True, False))
    wasi._local.guest = g
    try:
        with pytest.raises(wasi.OutputFull):
            wasi.fd_write(None, 1, 0, 1000, 8000)
        assert wasi.fd_write(None, len(g.fds) - 1, 0, 1000, 8000) == wasi.ENOSPC  # past max_memory's buffers
        assert wasi.fd_pwrite(None, len(g.fds) - 1, 0, 1000, 0, 8000) == wasi.ENOSPC
    finally:
        del wasi._local.guest


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
