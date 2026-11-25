"""The `api` command (ported from ApiCommandTests.cs): authenticated HTTP calls the agent can make but not redirect
or read the credential of. A fake transport stands in for the network, plus one real-socket round trip."""
import http.server
import sys
import threading
import time
from pathlib import Path
from urllib.parse import urlsplit

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402
from topwater.api import ApiEndpoint, register_api  # noqa: E402

TOKEN = "s3cr3t-T0KEN-value"


class Stub:
    """Records every request and answers with `respond`."""

    def __init__(self):
        self.requests = []
        self.respond = lambda: (200, "OK", [], b'{"ok":true}')

    def __call__(self, method, url, headers, body, timeout, limit):
        self.requests.append((method, url, {k.lower(): v for k, v in headers.items()}, (body or b"").decode()))
        r = self.respond()
        if isinstance(r, Exception):
            raise r
        return r


def make(more=None):
    stub = Stub()
    endpoints = {
        "jira": ApiEndpoint("https://acme.atlassian.net/rest/api/3", description="Issue tracker",
                            auth=lambda: f"Bearer {TOKEN}", paths=["/issue/", "/search"]),
        "crm": ApiEndpoint("https://crm.example.com/v1/", description="CRM", auth=lambda: TOKEN,
                           auth_header="X-Api-Key", methods=["GET", "POST"]),
    }
    if more:
        more(endpoints)
    return register_api(Shell(), "api", endpoints, stub), stub


def test_gets_with_the_credential_and_pipes_the_body():
    sh, stub = make()
    r = sh.run("api get jira /issue/PROJ-1 --query 'fields=summary,status' -q 'x=a b&c=d#e' | jq -r .ok")
    assert (r.exit_code, r.stdout) == (0, "true\n")
    (method, url, headers, _), = stub.requests
    assert method == "GET"
    assert url == "https://acme.atlassian.net/rest/api/3/issue/PROJ-1?fields=summary%2Cstatus&x=a%20b%26c%3Dd%23e"
    assert headers["authorization"] == f"Bearer {TOKEN}"
    sh.run("api get crm /contacts")
    assert stub.requests[1][2]["x-api-key"] == TOKEN
    assert "authorization" not in stub.requests[1][2]
    assert stub.requests[1][1] == "https://crm.example.com/v1/contacts"


def test_help_lists_endpoints_without_the_credential():
    sh, _ = make()
    help_ = sh.run("api --help").stdout
    assert "jira  Issue tracker" in help_
    assert "methods: GET  paths: /issue/, /search" in help_
    assert "crm  CRM" in help_
    assert "methods: GET, POST" in help_
    assert TOKEN not in help_
    assert "api" in sh.run("help").stdout


BAD_PATHS = ["https://evil.com/x", "//evil.com/x", "/issue//evil.com", "issue/1", "/\\evil.com/x", "\\\\evil.com\\x",
             "/issue/1@evil.com", "/issue/../../admin", "/issue/../admin", "/issue/%2e%2e/admin", "/issue/%2E%2E/%2e%2e/admin",
             "/issue/.%2e/admin", "/issue/%252e%252e/admin", "/issue/..;/admin", "/issue%2f..%2fadmin", "/issue/%zz",
             "/searchadmin", "/admin?x=/issue/", "/issue/1#frag", "/issue/１"]


@pytest.mark.parametrize("path", BAD_PATHS)
def test_paths_cannot_leave_the_base_or_the_allowed_prefixes(path):
    sh, stub = make()
    sh.env["P"] = path
    r = sh.run('api get jira "$P"')
    assert r.exit_code == 2
    assert "is not allowed" in r.stderr
    assert not stub.requests


@pytest.mark.parametrize("path", ["/x/../../y", "/%2e%2e/other", "/.."])
def test_without_prefixes_paths_still_cannot_climb_above_the_base(path):
    sh, stub = make()
    sh.env["P"] = path
    assert sh.run('api get crm "$P"').exit_code == 2
    assert not stub.requests


def test_control_characters_are_refused_everywhere():
    sh, stub = make()
    assert sh.run("api get jira $'/issue/1\\r\\nX-Evil: 1'").exit_code == 2
    assert sh.run("api get jira /issue/%0d%0aX").exit_code == 2
    assert sh.run("api get jira /issue/1 -H $'Accept: a\\r\\nAuthorization: x'").exit_code == 2
    assert not stub.requests


def test_dot_segments_inside_the_allowed_area_resolve():
    sh, stub = make()
    assert sh.run("api get jira /issue/A/../B/./C").exit_code == 0
    assert sh.run("api get jira /search").exit_code == 0
    assert sh.run("api get jira '/search/x?jql=project%3DX'").exit_code == 0
    assert [urlsplit(r[1]).path for r in stub.requests] == ["/rest/api/3/issue/B/C", "/rest/api/3/search", "/rest/api/3/search/x"]
    assert urlsplit(stub.requests[2][1]).query == "jql=project%3DX"


def test_methods_are_allow_listed_and_get_is_the_default():
    sh, stub = make()
    for cmd in ["api post jira /issue --data '{}'", "api delete jira /issue/1", "api put crm /contacts/1 -d x", "api 'GET /x HTTP/1.1' crm /a"]:
        r = sh.run(cmd)
        assert r.exit_code == 2
        assert "not allowed" in r.stderr
    assert not stub.requests
    assert sh.run("api POST crm /contacts -d '{\"a\":1}'").exit_code == 0
    assert (stub.requests[0][0], stub.requests[0][3], stub.requests[0][2]["content-type"]) == ("POST", '{"a":1}', "application/json")


def test_body_from_file_and_stdin():
    sh, stub = make()
    sh.fs.write_text("/home/agent/c.json", '{"from":"file"}')
    assert sh.run("api post crm /contacts --data @c.json").exit_code == 0
    assert sh.run("echo '{\"from\":\"stdin\"}' | api post crm /contacts --data - -H 'content-type: text/plain'").exit_code == 0
    assert stub.requests[0][3] == '{"from":"file"}'
    assert (stub.requests[1][3], stub.requests[1][2]["content-type"]) == ('{"from":"stdin"}\n', "text/plain")
    assert sh.run("api post crm /contacts --data @nope.json").exit_code == 2
    assert sh.run("api post crm /contacts --data @/root/secret").exit_code == 2
    sh.fs.write_text("/home/agent/big", "x" * 1_000_001)
    assert sh.run("api post crm /contacts --data @big").exit_code == 2
    assert len(stub.requests) == 2


@pytest.mark.parametrize("header", ["Authorization: Bearer mine", "X-Api-Key: mine", "Cookie: a=b", "Host: evil.com",
                                    "Proxy-Authorization: x", "Transfer-Encoding: chunked", "Content-Length: 5"])
def test_the_agent_cannot_set_sensitive_headers(header):
    sh, stub = make()
    sh.env["H"] = header
    assert sh.run('api get crm /contacts -H "$H"').exit_code == 2
    assert not stub.requests


def test_allowed_headers_pass_through():
    sh, stub = make()
    assert sh.run("api get crm /contacts -H 'accept: text/csv'").exit_code == 0
    assert stub.requests[0][2]["accept"] == "text/csv"


def test_echoed_credentials_are_scrubbed():
    sh, stub = make()
    stub.respond = lambda: (401, f"Bad {TOKEN}", [("X-Echo", TOKEN)],
                            f'{{"you_sent":"Bearer {TOKEN}","raw":"{TOKEN}"}}'.encode())
    r = sh.run("api get jira /issue/1 -i")
    assert r.exit_code == 1
    assert TOKEN not in r.stdout + r.stderr
    assert '"raw":"[REDACTED]"' in r.stdout
    assert "X-Echo: [REDACTED]" in r.stdout
    assert "HTTP 401" in r.stderr


def test_a_credential_cut_by_the_size_cap_is_still_scrubbed():
    sh, stub = make(lambda e: e.update(big=ApiEndpoint("https://big.example", auth=lambda: TOKEN, auth_header="X-Key", max_response_bytes=100)))
    stub.respond = lambda: (200, "OK", [], ("a" * 95 + TOKEN + "b" * 5000).encode())
    r = sh.run("api get big /x")
    assert r.exit_code == 1
    assert TOKEN[:5] not in r.stdout
    assert len(r.stdout) == 101  # the cap plus a newline
    assert "truncated at 100 bytes" in r.stderr


def test_errors_and_redirects_exit_one_with_the_body_kept():
    sh, stub = make()
    stub.respond = lambda: (404, "Not Found", [], b'{"error":"no such issue"}')
    r = sh.run("api get jira /issue/X | jq -r .error")
    assert r.stdout == "no such issue\n"
    assert "api: HTTP 404 Not Found" in r.stderr
    assert sh.run("api get jira /issue/X").exit_code == 1
    stub.respond = lambda: (302, "Found", [("Location", "https://evil.com/steal")], b"")
    r = sh.run("api get jira /issue/X")
    assert r.exit_code == 1
    assert "HTTP 302 Found (redirect to https://evil.com/steal not followed)" in r.stderr
    assert len(stub.requests) == 3


def test_failures_become_one_line_errors():
    sh, stub = make()
    stub.respond = lambda: ConnectionError("boom")
    r = sh.run("api get jira /issue/1")
    assert r.exit_code == 1
    assert r.stderr.startswith("api: GET jira /issue/1 failed:")
    assert len(r.stderr.strip().split("\n")) == 1
    stub.respond = lambda: RuntimeError(f"handler bug, header was Bearer {TOKEN}")
    r = sh.run("api get jira /issue/1")
    assert (r.exit_code, r.stderr) == (1, "api: GET jira /issue/1 failed: RuntimeError\n")


def test_the_timeout_is_enforced_by_the_command():
    def slow(*_):
        time.sleep(1)
        return 200, "OK", [], b""

    sh = register_api(Shell(), "api", {"s": ApiEndpoint("https://s.example", timeout=0.1)}, slow)
    r = sh.run("api get s /x")
    assert (r.exit_code, r.stderr) == (1, "api: s did not answer within 0.1 s\n")


def test_auth_failures_do_not_leak_their_message():
    def vault():
        raise RuntimeError(f"vault said {TOKEN}")

    sh, stub = make(lambda e: e.update(vault=ApiEndpoint("https://v.example", auth=vault)))
    r = sh.run("api get vault /x")
    assert (r.exit_code, r.stderr) == (1, "api: the credentials for vault are not available\n")
    assert not stub.requests


def test_usage_errors_exit_two_without_a_call():
    sh, stub = make()
    for cmd in ["api get", "api get nope /x", "api get jira /issue/1 --bogus", "api get jira /issue/1 -q novalue", "api get jira /issue/1 extra"]:
        assert sh.run(cmd).exit_code == 2
    assert not stub.requests


def test_registration_refuses_unsafe_configuration():
    def reg(e):
        register_api(Shell(), "api", {"x": e}, Stub())

    for bad in [ApiEndpoint("http://plain.example"), ApiEndpoint("https://user:pw@h.example"), ApiEndpoint("https://h.example/?k=v"),
                ApiEndpoint("/relative"), ApiEndpoint("https://h.example", auth_header="Accept")]:
        with pytest.raises(ValueError):
            reg(bad)
    reg(ApiEndpoint("http://localhost", allow_http=True))


def test_real_socket_round_trip_without_following_redirects():
    seen = []

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            seen.append(f"{self.path} {self.headers.get('Authorization')}")
            if self.path.endswith("/moved"):
                self.send_response(301)
                self.send_header("Location", "http://127.0.0.1:1/elsewhere")
                self.end_headers()
            else:
                body = f'["echo {self.headers.get("Authorization")}"]'.encode()
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        def log_message(self, *a):
            pass

    server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        sh = register_api(Shell(), "api", {"local": ApiEndpoint(f"http://127.0.0.1:{server.server_port}/api", allow_http=True,
                                                                auth=lambda: f"Bearer {TOKEN}")})
        ok = sh.run("api get local /items | jq -r '.[0]'")
        assert (ok.exit_code, ok.stdout) == (0, "echo [REDACTED]\n")
        moved = sh.run("api get local /moved")
        assert moved.exit_code == 1
        assert "HTTP 301" in moved.stderr and "not followed" in moved.stderr
        assert seen == [f"/api/items Bearer {TOKEN}", f"/api/moved Bearer {TOKEN}"]
    finally:
        server.shutdown()
