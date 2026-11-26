"""curl and wget against tests/web/cases.jsonl (recorded from real curl 8.5.0 and wget 1.21.4; the C# and TypeScript ports run
the same cases), then what the web policy guarantees: SSRF, caps, credentials, recordings, and the real connection path."""
import base64
import gzip
import io
import json
import os
import socket
import sys
import threading
import time
from pathlib import Path
from urllib.parse import urlsplit

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Recording, Shell, WebPolicy  # noqa: E402
from topwater import webaccess  # noqa: E402
from topwater.webaccess import is_blocked, parse_ipv4  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
WEB = ROOT / "tests" / "web"
ROUTES = json.loads((WEB / "routes.json").read_text(encoding="utf-8"))["routes"]
CASES = [json.loads(line) for line in (WEB / "cases.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]
RECORDINGS = ROOT / "tests" / "Topwater.Tests" / "recordings"
ECHOED = ("content-type", "accept", "authorization", "user-agent", "x-test")
TOKEN = "s3cr3t-T0KEN-value"


class Fixture:
    """A transport that answers like tests/web/server.py: the routes in routes.json, and /echo with the request it got."""

    def __init__(self):
        self.requests = []  # (method, url)
        self.seen = []  # the request headers, lower-cased names
        self.override = None

    def __call__(self, method, url, headers, body, timeout, limit):
        self.requests.append((method, url))
        seen = {}
        for k, v in headers:
            seen[k.lower()] = seen[k.lower()] + ", " + v if k.lower() in seen else v
        self.seen.append(seen)
        if self.override:
            return self.override(method, url, headers, body)
        u = urlsplit(url)
        target = u.path + ("?" + u.query if u.query else "")
        r = ROUTES.get(u.path)
        if r is None:
            status, reason, hs, data = 404, "Not Found", [("Content-Type", "text/plain")], b"no route\n"
        elif r.get("echo"):
            lines = [f"{method} {target}"] + [f"{h}: {seen[h]}" for h in ECHOED if h in seen]
            status, reason, hs, data = 200, "OK", [("Content-Type", "text/plain")], ("\n".join(lines) + "\n\n").encode() + (body or b"")
        else:
            data = base64.b64decode(r["bodyBase64"]) if "bodyBase64" in r else r["body"].encode()
            status, reason, hs = r["status"], r["reason"], [tuple(h) for h in r["headers"]]
        return status, reason, hs + [("Content-Length", str(len(data)))], b"" if method == "HEAD" else data


def fixture_policy(fixture, **more):
    return WebPolicy(**{"domains": ["api.test"], "allow_http": True, "methods": ["GET", "POST", "PUT", "DELETE", "PATCH"],
                        "transport": fixture, **more})


def make(**more):
    fx = Fixture()
    sh = Shell().allow_web(fixture_policy(fx, **more))
    sh.run("mkdir -p /tmp/w && cd /tmp/w")
    return sh, fx


@pytest.mark.parametrize("case", CASES, ids=[c["id"] for c in CASES])
def test_case(case):
    sh, _ = make()
    r = sh.run(case["script"])
    assert r.stdout == case["stdout"]
    if case["stderr"] is not None:
        assert r.stderr == case["stderr"]
    assert r.exit_code == case["exitCode"]


def test_without_a_policy_there_is_no_network():
    r = Shell().run("curl -sS https://example.com/; echo $?; wget -q https://example.com/; echo $?")
    assert r.stdout == "6\n4\n"
    assert r.stderr == "curl: (6) Could not resolve host: example.com\n"


def test_blocked_hosts_can_be_explained():
    sh, fx = make()
    explain = Shell().allow_web(WebPolicy(domains=["api.test"], explain_blocked=True, transport=Fixture()))
    r = explain.run("curl -sS https://evil.test/; curl -sS http://api.test/; curl -sS https://127.0.0.1/; curl -sS -X POST https://api.test/")
    assert r.stderr == ("curl: (6) Blocked by the web policy: evil.test is not an allowed host\n"
                        "curl: (1) Blocked by the web policy: only https:// URLs are allowed\n"
                        "curl: (7) Blocked by the web policy: 127.0.0.1 is a private or reserved address\n"
                        "curl: (1) Blocked by the web policy: POST requests are not allowed\n")
    r = sh.run("curl -sS -X PROPFIND http://api.test/echo; echo $?")
    assert r.stdout == "1\n"
    assert r.stderr == "curl: (1) Blocked by the web policy: PROPFIND requests are not allowed\n"
    assert fx.requests == []


def test_https_only_by_default():
    sh = Shell().allow_web(WebPolicy(domains=["api.test"], transport=Fixture()))
    r = sh.run("curl -sS http://api.test/hello; echo $?; curl -s https://api.test/hello")
    assert r.stdout == "1\nhello\n"
    assert r.stderr == 'curl: (1) Protocol "http" not supported or disabled in libcurl\n'


@pytest.mark.parametrize("url,code", [
    ("http://[::ffff:127.0.0.1]/", 7), ("http://[::ffff:a9fe:a9fe]/", 7), ("http://[fd00:ec2::254]/", 7), ("http://[fe80::1]/", 7),
    ("http://0/", 7), ("http://017700000001/", 7), ("http://127.1/", 7), ("http://100.100.100.200/", 7),
    ("http://api.test.evil.test/", 6), ("http://evil.test/#@api.test/", 6), ("http://api.test:80@evil.test/", 6)])
def test_internal_addresses_and_lookalikes_are_refused(url, code):
    sh, fx = make()
    assert sh.run(f"curl -s '{url}'; echo $?").stdout == f"{code}\n"
    assert fx.requests == []


def test_address_ranges():
    import ipaddress
    blocked = ["0.0.0.0", "10.1.2.3", "100.64.0.1", "127.0.0.1", "169.254.169.254", "172.16.0.1", "172.31.255.255", "192.168.1.1",
               "224.0.0.1", "255.255.255.255", "::", "::1", "::ffff:10.0.0.1", "::7f00:1", "64:ff9b::a9fe:a9fe", "2002:7f00:1::",
               "fc00::1", "fe80::1", "fec0::1", "ff02::1", "2001::1", "2001:db8::1"]
    open_ = ["8.8.8.8", "1.1.1.1", "172.32.0.1", "100.128.0.1", "2606:4700::1111", "2002:808:808::"]
    for a in blocked:
        assert is_blocked(ipaddress.ip_address(a).packed), a
    for a in open_:
        assert not is_blocked(ipaddress.ip_address(a).packed), a
    assert parse_ipv4("2130706433") == bytes([127, 0, 0, 1])
    assert parse_ipv4("0177.0.0.01") == bytes([127, 0, 0, 1])
    assert parse_ipv4("0x7f.1") == bytes([127, 0, 0, 1])
    assert parse_ipv4("256.0.0.1") is None
    assert parse_ipv4("1.2.3.4.5") is None
    assert parse_ipv4("example.com") is None


def test_the_credential_is_the_hosts_and_never_shown():
    sh, fx = make()
    sh.allow_web(WebPolicy(domains=["api.test", "other.test"], allow_http=True, transport=fx,
                           credentials={"api.test": lambda: "Bearer " + TOKEN}))
    r = sh.run("curl -s -H 'Authorization: Bearer mine' -H 'Host: other.test' -u me:pw http://api.test/echo; "
               "curl -sv http://api.test/echo -o /dev/null")
    assert "authorization: [REDACTED]\n" in r.stdout
    assert TOKEN not in r.stdout + r.stderr
    assert "Authorization" not in r.stderr
    assert all(h["authorization"] == "Bearer " + TOKEN for h in fx.seen)
    assert all(h.get("host", "api.test") == "api.test" for h in fx.seen)

    # a redirect to another allowed host gets neither the credential nor the agent's own Authorization
    fx.seen.clear()
    fx.override = lambda m, url, h, b: ((302, "Found", [("Location", "http://other.test/x")], b"") if urlsplit(url).hostname == "api.test"
                                        else (200, "OK", [], b"ok\n"))
    r = sh.run("curl -sL -H 'Authorization: Bearer mine' http://api.test/go")
    assert r.stdout == "ok\n"
    assert fx.seen[0]["authorization"] == "Bearer " + TOKEN
    assert "authorization" not in fx.seen[1]


def test_responses_are_cut_at_the_cap():
    sh, _ = make()
    sh.allow_web(WebPolicy(domains=["api.test"], allow_http=True, max_response_bytes=4, transport=Fixture()))
    r = sh.run('curl -sS http://api.test/hello; echo " $?"; wget -q -O - http://api.test/hello; echo " $?"')
    assert r.stdout == "hell 63\nhell 1\n"
    assert r.stderr == "curl: (63) Maximum file size exceeded\nwget: the response is larger than the web policy allows (4 bytes)\n"
    # a gzip bomb stops at the cap too
    fx = Fixture()
    bomb = gzip.compress(bytes(1_000_000))
    fx.override = lambda *_: (200, "OK", [("Content-Encoding", "gzip")], bomb)
    sh.allow_web(WebPolicy(domains=["api.test"], allow_http=True, max_response_bytes=10_000, transport=fx))
    assert sh.run("curl -s --compressed http://api.test/gzip | wc -c; echo ${PIPESTATUS[0]}").stdout == "0\n63\n"


def test_requests_are_counted_per_call_and_per_shell():
    fx = Fixture()
    sh = Shell().allow_web(WebPolicy(domains=["api.test"], allow_http=True, max_requests_per_call=3, max_requests=5, max_redirects=50, transport=fx))
    r = sh.run("curl -sSL http://api.test/loop; echo $?")
    assert r.stdout == "1\n"
    assert r.stderr == "curl: (1) Blocked by the web policy: the limit of 3 requests in one call was reached\n"
    r = sh.run("for i in 1 2 3; do curl -s http://api.test/hello; done; echo $?")
    assert r.stdout == "hello\nhello\n1\n"
    assert "5 requests for this session" in sh.run("curl -sS http://api.test/hello").stderr
    assert len(fx.requests) == 5


def test_redirects_stop_at_the_policy_limit():
    sh, _ = make()
    assert sh.run("curl -sSL http://api.test/loop").stderr == "curl: (47) Maximum (10) redirects followed\n"
    r = sh.run("wget -O - http://api.test/loop; echo $?")
    assert r.stderr.endswith("10 redirections exceeded.\n")
    assert r.stdout == "8\n"


def test_slow_answers_time_out():
    sh, fx = make()

    def slow(*_):
        time.sleep(1)
        return 200, "OK", [], b""
    fx.override = slow
    started = time.monotonic()
    r = sh.run("curl -sS -m 0.2 http://api.test/hello; echo $?; wget -q -T 0.2 http://api.test/hello; echo $?")
    assert r.stdout == "28\n4\n"
    assert r.stderr == "curl: (28) Operation timed out after 200 milliseconds with 0 bytes received\n"
    assert time.monotonic() - started < 5


def test_huge_headers_are_refused():
    sh, fx = make()
    fx.override = lambda *_: (200, "OK", [("X-Big", "a" * 70_000)], b"x")
    assert sh.run("curl -sS http://api.test/hello; echo $?").stdout == "56\n"


def test_header_injection_is_refused():
    sh, fx = make()
    assert sh.run("curl -sS -H $'X-Test: a\\r\\nX-Evil: b' http://api.test/echo; echo $?").stdout == "43\n"
    assert fx.requests == []


def test_forks_do_not_inherit_web_access():
    sh, _ = make()
    assert sh.fork().run("curl -s http://api.test/hello; echo $?").stdout == "6\n"


def save_and_load(rec):
    f = io.BytesIO()
    rec.save(f)
    f.seek(0)
    return Recording.load(f), f.getvalue()


def test_recordings_replay_without_the_network():
    sh, fx = make()
    recorder = sh.start_recording()
    sh.run("curl -s http://api.test/json")
    sh.run("curl -sL http://api.test/redirect -o page; wget -q http://api.test/gzip; curl -sS http://nope.test/")
    sh.run("cat page; wc -c < gzip")
    recorder.stop()
    loaded, _ = save_and_load(recorder.recording)
    assert loaded.web.domains == ["api.test"]
    assert len(loaded.calls[1].web) == 3  # the redirect, the page, the gzip file; the refused host never reached the network
    assert loaded.calls[1].web[1].body == b"hello\n"

    before = len(fx.requests)
    report = loaded.replay()
    assert report.matches, report.differences
    assert len(fx.requests) == before
    assert loaded.shell_at(1).fs.read_text("/tmp/w/page") == "hello\n"

    # asking for something that was not recorded is reported, not fetched
    r = loaded.create_shell().run("curl -sS http://api.test/hello")
    assert r.stderr == "curl: (7) Topwater replay: GET http://api.test/hello is not in the recording\n"


# the calls in recordings/web-from-csharp.topwater (WebScenario in WebAccessTests.cs)
WEB_SCENARIO = [
    "curl -s http://api.test/json",
    "curl -sL -o page http://api.test/redirect; wget -q http://api.test/gzip; cat page; wc -c < gzip",
    "curl -sS http://nope.test/; echo $?",
]


def test_a_recording_from_the_csharp_port_replays():
    path = RECORDINGS / "web-from-csharp.topwater"
    if not path.exists():
        pytest.skip("written by the C# tests")
    rec = Recording.load(str(path))
    assert [c.command for c in rec.calls] == WEB_SCENARIO
    assert rec.calls[1].web[1].body == b"hello\n"
    report = rec.replay()
    assert report.matches, report.differences


def test_a_recording_from_this_port_round_trips():
    sh = Shell().allow_web(fixture_policy(Fixture()))
    recorder = sh.start_recording(metadata={"source": "python port", "task": "web fixture"})
    for c in WEB_SCENARIO:
        sh.run(c)
    recorder.stop()
    rec, raw = save_and_load(recorder.recording)
    assert [c.command for c in rec.calls] == WEB_SCENARIO
    import zipfile
    history = zipfile.ZipFile(io.BytesIO(raw)).read("history.jsonl").decode().splitlines()
    header, calls = json.loads(history[0]), [json.loads(x) for x in history[1:]]
    assert header["web"] == {"domains": ["api.test"], "methods": ["GET", "POST", "PUT", "DELETE", "PATCH"], "allowHttp": True,
                             "maxResponseBytes": 10_000_000, "maxRequestsPerCall": 20, "maxRequests": 500, "timeoutMs": 30000,
                             "maxRedirects": 10, "explainBlocked": False, "credentials": []}
    assert calls[1]["web"][0] == {"method": "GET", "url": "http://api.test/redirect", "status": 302, "reason": "Found",
                                  "headers": [["Location", "/hello"], ["Content-Length", "0"]], "size": 0}
    assert "web" not in calls[2]
    report = rec.replay()
    assert report.matches, report.differences
    again, raw2 = save_and_load(rec)
    assert again.replay().matches


def test_a_recording_keeps_no_credential():
    sh = Shell().allow_web(WebPolicy(domains=["api.test"], allow_http=True, transport=Fixture(), credentials={"api.test": lambda: "Bearer " + TOKEN}))
    recorder = sh.start_recording()
    sh.run("curl -s http://api.test/echo")
    recorder.stop()
    rec, raw = save_and_load(recorder.recording)
    import zipfile
    z = zipfile.ZipFile(io.BytesIO(raw))
    assert all(TOKEN.encode() not in z.read(n) for n in z.namelist())
    assert rec.web.credentials.keys() == {"api.test"}
    assert rec.replay().matches


def test_bad_recorded_exchanges_are_refused():
    import zipfile
    sh, _ = make()
    recorder = sh.start_recording()
    sh.run("curl -s http://api.test/hello; curl -s http://nope.test/")
    recorder.stop()
    _, raw = save_and_load(recorder.recording)

    history = zipfile.ZipFile(io.BytesIO(raw)).read("history.jsonl").decode().splitlines()
    body = json.loads(history[1])["web"][0]["body"]

    def edit(change=None, drop=None):
        src = zipfile.ZipFile(io.BytesIO(raw))
        out = io.BytesIO()
        with zipfile.ZipFile(out, "w") as z:
            for n in src.namelist():
                data = src.read(n)
                if n == "history.jsonl" and change:
                    lines = [json.loads(x) for x in data.decode().splitlines()]
                    change(lines)
                    data = "".join(json.dumps(x) + "\n" for x in lines).encode()
                if n != drop:
                    z.writestr(n, data)
        out.seek(0)
        return out

    def set_(where, value):
        def change(lines):
            target = lines[where[0]]
            for k in where[1:-1]:
                target = target[k]
            target[where[-1]] = value
        return change

    cases = {
        "bad response": edit(set_((1, "web", 0, "status"), 42)),
        "doesn't hold": edit(set_((1, "web", 0, "size"), 7)),
        "unknown error": edit(set_((1, "web", 0), {"method": "GET", "url": "http://api.test/hello", "error": "nope"})),
        "bad web policy": edit(set_((0, "web", "domains"), ["exa mple.com"])),
        "is missing": edit(drop="blobs/" + body),
    }
    for why, f in cases.items():
        with pytest.raises(ValueError, match=why):
            Recording.load(f)
    with pytest.raises(ValueError, match="MaxFileSystemBytes"):
        Recording.load(edit(), limits=type("L", (), {"max_file_system_bytes": 3})())
    assert len(Recording.load(edit()).calls[0].web) == 1  # the refused host never reached the network


def test_bad_policies_are_refused():
    with pytest.raises(ValueError):
        Shell().allow_web(WebPolicy(domains=["exa mple.com"]))
    with pytest.raises(ValueError):
        Shell().allow_web(WebPolicy(domains=["a.com"], credentials={"b.com": lambda: "x"}))
    with pytest.raises(ValueError):
        Shell().allow_web(WebPolicy(max_requests=-1))


@pytest.fixture
def hooks():
    proxy = os.environ.get("http_proxy")
    yield
    webaccess._resolve_for_tests = None
    webaccess._allow_loopback_for_tests = False
    if proxy is None:
        os.environ.pop("http_proxy", None)
    else:
        os.environ["http_proxy"] = proxy


def test_names_resolve_once_and_connect_to_the_checked_address(hooks):
    """The real connection path (no transport), against a loopback server the test-only hooks let through."""
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    port = listener.getsockname()[1]
    seen = {}

    def serve():
        conn, _ = listener.accept()
        with conn:
            f = conn.makefile("rb")
            while (line := f.readline().decode()) not in ("\r\n", "\n", ""):
                if line.lower().startswith("host:"):
                    seen["host"] = line[5:].strip()
            conn.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: 3\r\nConnection: close\r\n\r\nok\n")
    server = threading.Thread(target=serve, daemon=True)
    server.start()
    lookups = []

    def resolve(name):
        lookups.append(name)
        return ["127.0.0.1"] if name == "pinned.test" else ["169.254.169.254"]
    try:
        os.environ["http_proxy"] = "http://127.0.0.1:1"  # the host's proxy settings are never used
        webaccess._resolve_for_tests = resolve
        webaccess._allow_loopback_for_tests = True
        sh = Shell().allow_web(WebPolicy(domains=["pinned.test", "meta.test"], allow_http=True))
        r = sh.run(f"curl -sS http://pinned.test:{port}/")
        assert (r.stdout, r.stderr) == ("ok\n", "")
        assert seen["host"] == f"pinned.test:{port}"
        assert lookups == ["pinned.test"]

        r = sh.run("curl -sS http://meta.test/; echo $?; wget -q http://meta.test/; echo $?")
        assert r.stdout == "6\n4\n"
        assert r.stderr == "curl: (6) Could not resolve host: meta.test\n"

        webaccess._allow_loopback_for_tests = False  # without the hook, loopback is refused like any internal address
        assert sh.run(f"curl -sS http://pinned.test:{port + 1}/").stderr == "curl: (6) Could not resolve host: pinned.test\n"
        sh = Shell().allow_web(WebPolicy(domains=["meta.test"], allow_http=True, explain_blocked=True))
        assert sh.run("curl -sS http://meta.test/").stderr == \
            "curl: (6) Blocked by the web policy: meta.test resolves to a private or reserved address\n"
    finally:
        listener.close()
    server.join(5)
