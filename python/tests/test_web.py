"""curl and wget against tests/web/cases.jsonl (recorded from real curl 8.5.0 and wget 1.21.4; the C# and TypeScript ports run
the same cases), then what the web policy guarantees: SSRF, caps, credentials, recordings, and the real connection path."""
import base64
import functools
import gzip
import io
import json
import os
import socket
import struct
import sys
import threading
import time
import zlib
from pathlib import Path
from urllib.parse import urlsplit

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Recording, Shell, WebPolicy  # noqa: E402
from topwater.webaccess import is_blocked, parse_ipv4, pinned_transport  # noqa: E402

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
    return WebPolicy(**{"domains": ["api.test"], "allow_http": True, "explain_blocked": False, "methods": ["GET", "POST", "PUT", "DELETE", "PATCH"],
                        "transport": fixture, **more})


def make(**more):
    fx = Fixture()
    sh = Shell().allow_web(fixture_policy(fx, **more))
    sh.run("mkdir -p /tmp/w && cd /tmp/w")
    return sh, fx


@pytest.fixture(scope="module")
def raw_server():
    """Serves the "raw" routes on a loopback port, byte for byte as tests/web/server.py does, then closes."""
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen()

    def handle(conn):
        with conn:
            f = conn.makefile("rb")
            target = f.readline().split(b" ")[1].decode()
            while f.readline() not in (b"\r\n", b"\n", b""):
                pass
            conn.sendall(ROUTES[target]["raw"].encode("latin-1"))
            if ROUTES[target].get("reset"):  # a reset (RST) instead of a close
                time.sleep(0.2)
                conn.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
                return
            conn.shutdown(socket.SHUT_WR)

    def serve():
        while True:
            try:
                conn, _ = listener.accept()
            except OSError:
                return
            threading.Thread(target=handle, args=(conn,), daemon=True).start()
    threading.Thread(target=serve, daemon=True).start()
    yield listener.getsockname()[1]
    listener.close()


def wire_shell(port, **more):
    """The real connection path to raw_server: the wire cases are malformed responses a fake transport cannot send."""
    sh = Shell().allow_web(WebPolicy(domains=[f"api.test:{port}"], allow_http=True, explain_blocked=False, **more,
                                     transport=functools.partial(pinned_transport, resolve=lambda name: ["127.0.0.1"], allow_loopback=True)))
    sh.run("mkdir -p /tmp/w && cd /tmp/w")
    return sh


@pytest.mark.parametrize("case", CASES, ids=[c["id"] for c in CASES])
def test_case(case, raw_server):
    if case.get("wire"):
        sh = wire_shell(raw_server)
        r = sh.run(case["script"].replace("http://api.test/", f"http://api.test:{raw_server}/"))
    else:
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
    explain = Shell().allow_web(WebPolicy(domains=["api.test"], transport=Fixture()))  # explaining is the default
    r = explain.run("curl -sS https://evil.test/; curl -sS http://api.test/; curl -sS https://127.0.0.1/; curl -sS -X POST https://api.test/")
    assert r.stderr == ("curl: (6) Blocked by the web policy: evil.test is not an allowed host (allowed: api.test)\n"
                        "curl: (1) Blocked by the web policy: only https:// URLs are allowed\n"
                        "curl: (7) Blocked by the web policy: 127.0.0.1 is a private or reserved address\n"
                        "curl: (1) Blocked by the web policy: POST requests are not allowed\n")
    r = sh.run("curl -sS -X PROPFIND http://api.test/echo; echo $?")
    assert r.stdout == "1\n"
    assert r.stderr == "curl: (1) Blocked by the web policy: PROPFIND requests are not allowed\n"
    assert fx.requests == []

    many = Shell().allow_web(WebPolicy(domains=[f"h{i}.test" for i in range(12)] + ["*.Wild.test:8080"], transport=Fixture()))
    r = many.run("curl -sS https://evil.test:8443/x; wget -q https://[2606:4700::1]/; echo $?")
    assert r.stdout == "4\n"
    assert r.stderr == (
        "curl: (6) Blocked by the web policy: evil.test:8443 is not an allowed host (allowed: h0.test, h1.test, h2.test, h3.test, "
        "h4.test, h5.test, h6.test, h7.test, h8.test, h9.test, and 3 more)\n"
        "wget: blocked by the web policy: [2606:4700::1] is not an allowed host (allowed: h0.test, h1.test, h2.test, h3.test, "
        "h4.test, h5.test, h6.test, h7.test, h8.test, h9.test, and 3 more)\n")
    none = Shell().allow_web(WebPolicy(transport=Fixture()))
    assert none.run("curl -sS https://a.test/").stderr == "curl: (6) Blocked by the web policy: a.test is not an allowed host (allowed: none)\n"


def test_ports_are_part_of_the_policy():
    fx = Fixture()
    sh = Shell().allow_web(WebPolicy(domains=["api.test", "alt.test:8443", "*.wild.test:8080"], allow_http=True, explain_blocked=False,
                                     transport=fx, credentials={"api.test": lambda: "Bearer " + TOKEN, "alt.test:8443": lambda: "Bearer alt-secret-1"}))
    r = sh.run("curl -s https://api.test/hello; curl -s http://api.test/hello; curl -s http://alt.test:8443/hello; "
               "curl -s https://x.wild.test:8080/hello")
    assert r.stdout == "hello\n" * 4
    assert [h.get("authorization") for h in fx.seen] == ["Bearer " + TOKEN, "Bearer " + TOKEN, "Bearer alt-secret-1", None]
    r = sh.run("curl -sS http://api.test:22/; echo $?; curl -sS https://alt.test/; echo $?; curl -sS https://x.wild.test/; echo $?; "
               "wget -q http://api.test:8080/; echo $?; curl -sS http://127.0.0.1:22/; echo $?")
    assert r.stdout == "7\n7\n7\n4\n7\n"
    assert r.stderr == ("curl: (7) Failed to connect to api.test port 22 after 0 ms: Couldn't connect to server\n"
                        "curl: (7) Failed to connect to alt.test port 443 after 0 ms: Couldn't connect to server\n"
                        "curl: (7) Failed to connect to x.wild.test port 443 after 0 ms: Couldn't connect to server\n"
                        "curl: (7) Failed to connect to 127.0.0.1 port 22 after 0 ms: Couldn't connect to server\n")
    assert len(fx.requests) == 4
    assert Shell().allow_web(WebPolicy(domains=["*.2.3.4"], transport=fx)).run("curl -s https://1.2.3.4/; echo $?").stdout == "7\n"
    explain = Shell().allow_web(WebPolicy(domains=["api.test"], allow_http=True, transport=fx))
    r = explain.run("curl -sS http://api.test:22/; echo $?; wget -q http://api.test:22/; echo $?")
    assert r.stdout == "7\n4\n"
    assert r.stderr == ("curl: (7) Blocked by the web policy: api.test:22 is not an allowed host (allowed: api.test)\n"
                        "wget: blocked by the web policy: api.test:22 is not an allowed host (allowed: api.test)\n")
    for domains, credentials in ((["api.test:0"], {}), (["api.test:x"], {}), (["*.wild.test"], {"a.wild.test": lambda: "x"}),
                                 (["api.test"], {"api.test:99999": lambda: "x"})):
        with pytest.raises(ValueError):
            WebSessionFor(domains, credentials)


def WebSessionFor(domains, credentials):
    return Shell().allow_web(WebPolicy(domains=domains, credentials=credentials))


def test_https_only_by_default():
    sh = Shell().allow_web(WebPolicy(domains=["api.test"], explain_blocked=False, transport=Fixture()))
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
    blocked += ["168.63.129.16", "192.88.99.1", "::ffff:0:7f00:1", "::ffff:0:808:808", "100::1", "100::ffff:ffff:ffff:ffff",
                "2001:20::1", "2001:2f:ffff::1", "3fff::1", "3fff:fff::1", "5f00::1", "5f00:ffff::1"]
    open_ = ["8.8.8.8", "1.1.1.1", "172.32.0.1", "100.128.0.1", "2606:4700::1111", "2002:808:808::", "168.63.129.17", "192.88.98.1",
             "100:0:0:1::1", "2001:30::1", "2001:1f::1", "3fff:1000::1", "5f01::1", "::fffe:0:808:808"]
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

    # the agent's own Authorization and Cookie stay with the first URL's origin: scheme, host and port
    sh = Shell().allow_web(WebPolicy(domains=["api.test", "api.test:8080"], allow_http=True, transport=fx))
    fx.override = None
    for target in ("https://api.test/x", "http://api.test:8080/x", "http://api.test/x"):
        fx.seen.clear()
        fx.override = lambda m, url, h, b, target=target: ((302, "Found", [("Location", target)], b"") if url.endswith("/go")
                                                          else (200, "OK", [], b"ok\n"))
        sh.run("curl -sL -H 'Authorization: Bearer mine' -H 'Cookie: k=v' http://api.test/go")
        kept = target == "http://api.test/x"
        assert ("authorization" in fx.seen[-1], "cookie" in fx.seen[-1]) == (kept, kept), target


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


def test_a_body_cut_short_replays_as_it_came(raw_server):
    sh = wire_shell(raw_server)
    recorder = sh.start_recording()
    first = sh.run(f"curl -sS http://api.test:{raw_server}/short-length; curl -sS http://api.test:{raw_server}/empty; "
                   f"wget -q -O- http://api.test:{raw_server}/chunked-short; echo $?")
    recorder.stop()
    assert first.stdout == "shortok\n4\n"
    loaded, _ = save_and_load(recorder.recording)
    assert [(x.error, x.status, x.body) for x in loaded.calls[0].web] == [("partial", 200, b"short"), ("empty", 0, b""), ("partial", 200, b"ok\n")]
    report = loaded.replay()
    assert report.matches, report.differences


SECRET = "Bearer s3cr3t-T0KEN-value"


def leaks(data) -> bool:
    """Whether any 8 characters of the credential's token show."""
    text = data if isinstance(data, str) else data.decode("latin-1")
    return any(TOKEN[i:i + 8] in text for i in range(len(TOKEN) - 7))


def test_a_cut_body_from_a_host_with_a_credential_keeps_nothing(raw_server):
    sh = wire_shell(raw_server, credentials={f"api.test:{raw_server}": lambda: SECRET})
    recorder = sh.start_recording()
    r = sh.run(f"curl -sS --compressed http://api.test:{raw_server}/secret-gzip-cut; echo rc=$?; "
               f"curl -sS http://api.test:{raw_server}/secret-cut; echo rc=$?; curl -sS http://api.test:{raw_server}/reset-body; echo rc=$?")
    recorder.stop()
    assert r.stdout == "rc=18\nrc=18\nrc=56\n"
    _, saved = save_and_load(recorder.recording)
    assert not leaks(r.stdout + r.stderr) and not leaks(saved)


def test_a_compressed_body_cut_at_the_cap_never_shows_the_credential():
    body = b"x" * 1000 + b"Authorization: " + SECRET.encode() + b"\n" + b"y" * 200
    # stored blocks of 40 bytes: the decoded prefix follows the cut closely, and the blocks' own bytes put the cut past the
    # cap's margin for the credential, so without the end-of-stream check the prefix would show up to inside the token
    c = zlib.compressobj(0, zlib.DEFLATED, 31)
    data = b"".join(c.compress(body[i:i + 40]) + c.flush(zlib.Z_FULL_FLUSH) for i in range(0, len(body), 40)) + c.flush()
    for cap in range(1000, len(data), 3):
        sh, fx = make(credentials={"api.test": lambda: SECRET}, max_response_bytes=cap)
        fx.override = lambda *a: (200, "OK", [("Content-Encoding", "gzip")], data)
        r = sh.run("curl -sS -H 'Accept-Encoding: gzip' http://api.test/x | tail -c 60")
        assert not leaks(r.stdout + r.stderr), cap


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
def restore_proxy():
    proxy = os.environ.get("http_proxy")
    yield
    if proxy is None:
        os.environ.pop("http_proxy", None)
    else:
        os.environ["http_proxy"] = proxy


def test_names_resolve_once_and_connect_to_the_checked_address(restore_proxy):
    """The real connection path, against a loopback server a test-only transport lets through."""
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
        domains = [f"pinned.test:{port}", f"pinned.test:{port + 1}", "meta.test"]
        sh = Shell().allow_web(WebPolicy(domains=domains, allow_http=True, explain_blocked=False,
                                         transport=functools.partial(pinned_transport, resolve=resolve, allow_loopback=True)))
        r = sh.run(f"curl -sS http://pinned.test:{port}/")
        assert (r.stdout, r.stderr) == ("ok\n", "")
        assert seen["host"] == f"pinned.test:{port}"
        assert lookups == ["pinned.test"]

        r = sh.run("curl -sS http://meta.test/; echo $?; wget -q http://meta.test/; echo $?")
        assert r.stdout == "6\n4\n"
        assert r.stderr == "curl: (6) Could not resolve host: meta.test\n"

        # without allow_loopback, loopback is refused like any internal address
        sh = Shell().allow_web(WebPolicy(domains=domains, allow_http=True, explain_blocked=False,
                                         transport=functools.partial(pinned_transport, resolve=resolve)))
        assert sh.run(f"curl -sS http://pinned.test:{port + 1}/").stderr == "curl: (6) Could not resolve host: pinned.test\n"
        sh = Shell().allow_web(WebPolicy(domains=["meta.test"], allow_http=True, transport=functools.partial(pinned_transport, resolve=resolve)))
        assert sh.run("curl -sS http://meta.test/").stderr == \
            "curl: (6) Blocked by the web policy: meta.test resolves to a private or reserved address\n"
    finally:
        listener.close()
    server.join(5)


def test_the_credential_does_not_leave_through_an_encoding_or_a_range():
    fx = Fixture()
    sh = Shell().allow_web(WebPolicy(domains=["api.test"], allow_http=True, transport=fx, credentials={"api.test": lambda: "Bearer " + TOKEN}))
    sh.run("mkdir -p /tmp/w && cd /tmp/w")
    import zlib

    def compressing(method, url, headers, body):
        h = {k.lower(): v for k, v in headers}
        echo = "".join(f"{k}: {v}\n" for k, v in headers).encode()
        if "range" in h:
            start, end = map(int, h["range"][6:].split("-"))
            return 206, "Partial Content", [], echo[start:end + 1]
        if "gzip" in h.get("accept-encoding", ""):
            data = gzip.compress(echo)
            return 200, "OK", [("Content-Encoding", "gzip"), ("Content-Length", str(len(data)))], data
        if "deflate" in h.get("accept-encoding", ""):
            return 200, "OK", [("Content-Encoding", "Deflate ")], zlib.compress(echo)
        return 200, "OK", [], echo
    fx.override = compressing
    recorder = sh.start_recording()
    r = sh.run("curl -s --compressed http://api.test/echo; curl -s -H 'Accept-Encoding: gzip' http://api.test/echo | gunzip; "
               "curl -s -H 'Accept-Encoding: gzip' -o f http://api.test/echo; zcat f; cat f; "
               "curl -s -H 'Accept-Encoding: deflate' http://api.test/echo; wget -q -O - --header 'Accept-Encoding: gzip' http://api.test/echo; "
               "curl -si -H 'Accept-Encoding: gzip' http://api.test/echo; "
               "curl -s -H 'Range: bytes=0-40' http://api.test/echo; curl -s -H 'Range: bytes=41-200' -H 'If-Range: x' http://api.test/echo")
    recorder.stop()
    assert TOKEN not in r.stdout + r.stderr
    # what comes back is already decoded (gunzip and zcat find no gzip); a Range is not sent, so each gets the whole body
    assert r.stdout.count("Authorization: [REDACTED]") == 7
    assert r.stderr.count("not in gzip format") == 2
    assert "Content-Encoding" not in r.stdout and "Content-Length" not in r.stdout
    assert all("range" not in h and "if-range" not in h for h in fx.seen)
    _, raw = save_and_load(recorder.recording)
    import zipfile
    z = zipfile.ZipFile(io.BytesIO(raw))
    for n in z.namelist():
        data = z.read(n)
        assert TOKEN.encode() not in data and TOKEN.encode() not in (gzip.decompress(data) if data[:2] == b"\x1f\x8b" else b""), n

    # an encoding that cannot be decoded, or a corrupt one, fails the request; the cap still holds on the decoded body
    for headers, data in (([("Content-Encoding", "br")], b"x"), ([("Content-Encoding", "gzip")], b"not gzip"), ([("Content-Encoding", "br")], b"")):
        fx.override = lambda *_, headers=headers, data=data: (200, "OK", headers, data)
        assert sh.run("curl -sS http://api.test/echo; echo $?").stdout == "56\n"
    sh.allow_web(WebPolicy(domains=["api.test"], allow_http=True, transport=fx, max_response_bytes=1000, credentials={"api.test": lambda: "Bearer " + TOKEN}))
    fx.override = lambda *_: (200, "OK", [("Content-Encoding", "gzip")], gzip.compress(bytes(1_000_000)))
    assert sh.run("curl -s http://api.test/echo | wc -c; echo ${PIPESTATUS[0]}").stdout == "1000\n63\n"


def test_a_replay_never_goes_live():
    sh = Shell()
    recorder = sh.start_recording()
    sh.run("curl -sS https://api.test/hello; echo $?")
    recorder.stop()
    rec, _ = save_and_load(recorder.recording)
    assert rec.web is None
    fx = Fixture()
    replay = rec.create_shell(lambda s: s.allow_web(WebPolicy(domains=["api.test"], transport=fx, credentials={"api.test": lambda: TOKEN})))
    r = replay.run("curl -sS https://api.test/hello; echo $?")
    assert (r.stdout, r.stderr) == ("6\n", "curl: (6) Could not resolve host: api.test\n")
    assert fx.requests == []
    assert rec.replay(replay).matches


def test_the_tool_description_names_the_sites():
    import topwater
    sh = Shell()
    assert topwater.tool_description(sh) == topwater.tool_description(None) == topwater.TOOL_DESCRIPTION
    domains = [f"s{i}.test" for i in range(22)]
    topwater.allow_web(sh, WebPolicy(domains=domains, methods=["get", "Post"]))
    expected = topwater.TOOL_DESCRIPTION.replace(
        "Python, network access and package installation are not. ",
        "Python and package installation are not; `curl` and `wget` reach only these sites (methods: GET, POST): "
        + ", ".join(domains[:20]) + ", and 2 more. ")
    assert expected != topwater.TOOL_DESCRIPTION
    assert topwater.tool_description(sh) == expected
    rec = Recording(sh.snapshot(), {})
    rec.web = sh._web.policy
    assert topwater.tool_description(rec.create_shell()) == topwater.TOOL_DESCRIPTION  # a replay reaches nothing live
    pytest.importorskip("langchain_core")
    from topwater.integrations.langchain import bash_tool
    assert bash_tool(sh).description == expected
    assert bash_tool(sh, description="custom").description == "custom"


def test_a_dripping_server_is_stopped_at_the_timeout(restore_proxy):
    """The real connection path: a server that sends one byte every 0.1 s inside each read's timeout is cut off at the
    request's timeout, and no thread is left reading."""
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    port = listener.getsockname()[1]
    stop = threading.Event()

    def serve():
        while not stop.is_set():
            try:
                conn, _ = listener.accept()
            except OSError:
                return
            with conn:
                f = conn.makefile("rb")
                while f.readline() not in (b"\r\n", b""):
                    pass
                try:
                    conn.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: 1000\r\n\r\n")
                    while not stop.is_set():
                        conn.sendall(b"x")
                        time.sleep(0.1)
                except OSError:
                    pass
    server = threading.Thread(target=serve, daemon=True)
    server.start()
    try:
        sh = Shell().allow_web(WebPolicy(domains=[f"drip.test:{port}"], allow_http=True, timeout=1.0,
                                         transport=functools.partial(pinned_transport, resolve=lambda name: ["127.0.0.1"], allow_loopback=True)))
        threads = threading.active_count()
        started = time.monotonic()
        r = sh.run(f"curl -sS http://drip.test:{port}/; echo $?")
        assert time.monotonic() - started < 2.0
        assert r.stdout == "28\n"
        assert threading.active_count() == threads
    finally:
        stop.set()
        listener.close()
    server.join(5)
