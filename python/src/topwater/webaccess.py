"""The web access a shell's curl and wget get through Shell.allow_web(WebPolicy(...)). Without one the machine has no
network: every host fails to resolve. See "Web access" in docs/reference.md.

    shell.allow_web(WebPolicy(domains=["api.github.com", "*.wikipedia.org"], credentials={"api.github.com": lambda: "Bearer " + token}))

Every URL and redirect is checked against the policy; names are resolved once, private, loopback, link-local and other
internal addresses are refused, and the connection goes to the address that was checked. A fork does not inherit it.
"""
from __future__ import annotations

import asyncio
import hashlib
import http.client
import ipaddress
import socket
import ssl
import time
import unicodedata
from dataclasses import dataclass, field
from typing import Callable, Mapping, Sequence

# A transport sends one request: (method, url, [(name, value)], body, timeout, limit) -> (status, reason, [(name, value)], body).
# It must not follow redirects. It may return more than limit + 1 bytes of body (they are cut), but need not. It raises
# TransportError(kind) for a request that got no answer.
Transport = Callable[[str, str, "list[tuple[str, str]]", "bytes | None", float, int], "tuple[int, str, list[tuple[str, str]], bytes]"]

ERROR_KINDS = ("resolve", "blocked", "connect", "timeout", "tls", "recv")


@dataclass
class WebPolicy:
    """What the agent's curl and wget may reach. domains: "api.github.com" exactly, or "*.wikipedia.org" for its
    subdomains (not the name itself). methods: GET also allows HEAD. max_response_bytes: a body is cut there and the
    command fails. max_requests_per_call / max_requests: requests (each redirect counts) in one tool call / over the
    shell's life. timeout: seconds per request (the call's own limit still applies). credentials: the Authorization header
    for a host (exact name, one of domains), called in host code for every request to it; the agent never sees it, cannot
    replace it, and it is scrubbed from what comes back. explain_blocked: say "blocked by the web policy" instead of failing
    like an unknown host. transport: sends the requests instead of the network (tests, proxies); it must not follow
    redirects, and the private-address check happens where the name is resolved, so one that connects itself must do it."""
    domains: Sequence[str] = ()
    methods: Sequence[str] = ("GET",)
    allow_http: bool = False
    max_response_bytes: int = 10_000_000
    max_requests_per_call: int = 20
    max_requests: int = 500
    timeout: float = 30.0
    max_redirects: int = 10
    credentials: Mapping[str, Callable[[], str]] = field(default_factory=dict)
    explain_blocked: bool = False
    transport: Transport | None = None


@dataclass
class WebExchange:
    """One request a recorded call made and what came back, or how it failed (error: resolve, blocked, connect, timeout,
    tls, recv), stored so a replay never touches the network."""
    method: str
    url: str
    status: int = 0
    reason: str = ""
    headers: list = field(default_factory=list)  # [(name, value)]
    body: bytes = b""
    error: str | None = None


class TransportError(Exception):
    """A transport's request got no answer; kind is one of ERROR_KINDS."""

    def __init__(self, kind: str):
        super().__init__(kind)
        self.kind = kind


class WebFailure(Exception):
    """Why a request failed (kind: bad_url, bad_hostname, protocol, resolve, connect, timeout, tls, recv, too_large,
    redirects, policy, replay, bad_header); each command turns it into its own message and exit status. blocked is set
    when the policy refused it: what an explaining message says."""

    def __init__(self, kind: str, host: str = "", port: int = 0, blocked: str | None = None, detail: str = ""):
        super().__init__(kind)
        self.kind, self.host, self.port, self.blocked, self.detail = kind, host, port, blocked, detail


def _alnum(c: str) -> bool:
    return c.isascii() and c.isalnum()


class WebUrl:
    """A URL as Topwater reads it, the same in every port: http or https, a host (IPv4 in any inet_aton form is canonical
    dotted quad), a port and the path with its query. User info is kept for basic auth, never as the host."""

    def __init__(self, scheme: str, host: str, port: int, path_and_query: str, user_info: str | None, ip: bytes | None):
        self.scheme, self.host, self.port, self.path_and_query, self.user_info, self.ip = scheme, host, port, path_and_query, user_info, ip

    @property
    def default_port(self) -> bool:
        return self.port == (443 if self.scheme == "https" else 80)

    @property
    def host_for_url(self) -> str:
        return f"[{self.host}]" if ":" in self.host else self.host

    @property
    def origin(self) -> str:
        return f"{self.scheme}://{self.host_for_url}{'' if self.default_port else ':' + str(self.port)}"

    def __str__(self) -> str:
        return self.origin + self.path_and_query

    @staticmethod
    def parse(text: str) -> "WebUrl":
        """A missing scheme means http, as in curl. Raises WebFailure."""
        sep = text.find("://")
        scheme, rest = ("http", text) if sep < 0 else (text[:sep].lower(), text[sep + 3:])
        if sep >= 0 and (not scheme or not all(_alnum(c) or c in "+-." for c in scheme)):
            raise WebFailure("bad_url")
        if scheme not in ("http", "https"):
            raise WebFailure("protocol", scheme)
        if any(c <= " " or ord(c) >= 127 for c in rest):
            raise WebFailure("bad_url")
        end = min((i for i in (rest.find("/"), rest.find("?"), rest.find("#")) if i >= 0), default=-1)
        authority, tail = (rest, "") if end < 0 else (rest[:end], rest[end:])
        tail = tail.split("#", 1)[0]
        if not tail.startswith("/"):
            tail = "/" + tail
        user = None
        at = authority.find("@")  # the first @, as curl reads it: "a@b@host" has the host "b@host", which is refused
        if at >= 0:
            user, authority = authority[:at], authority[at + 1:]
        port_text = ""
        if authority.startswith("["):
            close = authority.find("]")
            if close < 0:
                raise WebFailure("bad_url")
            host = authority[1:close].lower()
            after = authority[close + 1:]
            if after:
                if not after.startswith(":"):
                    raise WebFailure("bad_url")
                port_text = after[1:]
            v6 = parse_ipv6(host)
            if v6 is None:
                raise WebFailure("bad_url")
            return WebUrl(scheme, host, _parse_port(port_text, scheme), tail, user, v6)
        host, colon, port_text = authority.partition(":")
        host = host.lower()
        if host.endswith("."):
            host = host[:-1]
        if not host or len(host) > 253 or not all(_alnum(c) or c in "-._" for c in host) or ".." in host:
            raise WebFailure("bad_hostname")
        v4 = parse_ipv4(host)
        return WebUrl(scheme, host if v4 is None else ".".join(map(str, v4)), _parse_port(port_text, scheme), tail, user, v4)

    def resolve(self, location: str) -> "WebUrl":
        """A redirect's Location against this URL: absolute, scheme-relative, absolute-path or relative."""
        if "://" in location:
            return WebUrl.parse(location)
        if location.startswith("//"):
            return WebUrl.parse(self.scheme + ":" + location)
        if location.startswith("/"):
            return WebUrl.parse(self.origin + location)
        path = self.path_and_query.split("?")[0]
        return WebUrl.parse(self.origin + path[:path.rfind("/") + 1] + location)


def _parse_port(text: str, scheme: str) -> int:
    if not text:
        return 443 if scheme == "https" else 80
    if len(text) > 5 or not all("0" <= c <= "9" for c in text) or not 1 <= int(text) <= 65535:
        raise WebFailure("bad_url")
    return int(text)


def parse_ipv4(s: str) -> bytes | None:
    """IPv4 the way inet_aton reads it: 1 to 4 parts, each decimal, 0x hex or 0 octal (127.1, 2130706433, 0x7f.0.0.1)."""
    parts = s.split(".")
    if len(parts) > 4:
        return None
    nums = []
    for p in parts:
        radix = 10
        if p[:2] in ("0x", "0X"):
            radix, p = 16, p[2:]
            if not p:
                nums.append(0)
                continue
        elif len(p) > 1 and p[0] == "0":
            radix, p = 8, p[1:]
        if not p or len(p) > 12:
            return None
        v = 0
        for c in p:
            d = ord(c) - 48 if "0" <= c <= "9" else ord(c) - 87 if "a" <= c <= "f" else ord(c) - 55 if "A" <= c <= "F" else 99
            if d >= radix:
                return None
            v = v * radix + d
        nums.append(v)
    if any(n > 255 for n in nums[:-1]) or nums[-1] >= 1 << (8 * (5 - len(nums))):
        return None
    value = nums[-1]
    for i, n in enumerate(nums[:-1]):
        value |= n << (8 * (3 - i))
    return value.to_bytes(4, "big")


def parse_ipv6(s: str) -> bytes | None:
    if ":" not in s or "%" in s:
        return None
    try:
        return ipaddress.IPv6Address(s).packed
    except ValueError:
        return None


def is_blocked(a: bytes) -> bool:
    """Loopback, private, link-local (169.254.169.254, the cloud metadata service), CGNAT, multicast, reserved, unspecified,
    documentation and benchmarking ranges; for IPv6 also ULA, site-local, Teredo, and any IPv4 it embeds."""
    if len(a) == 4:
        x, y = a[0], a[1]
        return (x in (0, 10, 127) or x >= 224 or x == 100 and (y & 0xC0) == 64 or x == 169 and y == 254 or x == 172 and (y & 0xF0) == 16
                or x == 192 and y == 168 or x == 192 and y == 0 and a[2] in (0, 2) or x == 198 and (y & 0xFE) == 18
                or x == 198 and y == 51 and a[2] == 100 or x == 203 and y == 0 and a[2] == 113)
    if len(a) != 16:
        return True
    if not any(a[:10]) and a[10] == 0xFF and a[11] == 0xFF:
        return is_blocked(a[12:])  # ::ffff:a.b.c.d
    if not any(a[:12]):
        return True  # ::, ::1, ::a.b.c.d
    if a[:4] == b"\x00\x64\xff\x9b":
        return is_blocked(a[12:])  # 64:ff9b::/96 NAT64
    if a[:2] == b"\x20\x02":
        return is_blocked(a[2:6])  # 2002::/16 6to4
    if a[:2] == b"\x20\x01" and (a[2:4] == b"\x00\x00" or a[2:4] == b"\x0d\xb8"):
        return True  # Teredo, documentation
    return (a[0] & 0xFE) == 0xFC or a[0] == 0xFE and (a[1] & 0xC0) in (0x80, 0xC0) or a[0] == 0xFF  # ULA, link/site-local, multicast


# test-only: let a test reach a loopback server through the real resolver and connection path. Not on WebPolicy, so no
# application setting can switch the address check off. _resolve_for_tests(name) returns IP address strings.
_resolve_for_tests: Callable[[str], list] | None = None
_allow_loopback_for_tests = False

MAX_HEADER_BYTES = 64 * 1024
MAX_REQUEST_BYTES = 1_000_000  # shortcut: fixed upload cap like register_api's; make it a policy option when a host needs more
_DROPPED = {h.lower() for h in ("Host", "Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive", "Upgrade", "TE", "Trailer",
                                "Proxy-Authorization", "Proxy-Connection")}


def _is_control(c: str) -> bool:
    return unicodedata.category(c) == "Cc"


def secrets_of(credential: str) -> list[str]:
    """What to scrub from responses: the credential and the token after its scheme, longest first."""
    sp = credential.find(" ")
    # a very short "secret" would shred ordinary output
    return sorted({s for s in (credential, credential[sp + 1:].strip() if sp > 0 else "") if len(s) >= 8}, key=len, reverse=True)


class WebResponse:
    def __init__(self, url: WebUrl, method: str, status: int, reason: str, headers: list, body: bytes, truncated: bool):
        self.url, self.method, self.status, self.reason, self.headers, self.body, self.truncated = url, method, status, reason, headers, body, truncated

    def header(self, name: str) -> str | None:
        return next((v for k, v in self.headers if k.lower() == name.lower()), None)


class WebSession:
    """One shell's web access: the policy, its counters, and the exchanges of the current call (for recordings), or the
    recorded exchanges a replay serves instead of the network."""

    def __init__(self, policy: WebPolicy, replay: list | None = None):
        self.policy = policy
        self._exact: set[str] = set()
        self._suffixes: list[str] = []
        for d in policy.domains:
            name = d.strip().rstrip(".").lower()
            wild = name.startswith("*.")
            bare = name[2:] if wild else name
            if not bare or not all(_alnum(c) or c in "-._" for c in bare):
                raise ValueError(f"{d}: a domain is a host name, or *.name for its subdomains")
            if wild:
                self._suffixes.append("." + bare)
            else:
                self._exact.add(bare)
        self._methods: set[str] = set()
        for m in policy.methods:
            if not m or not (m.isascii() and m.isalpha()):
                raise ValueError(f"{m}: not an HTTP method")
            self._methods.add(m.upper())
        if "GET" in self._methods:
            self._methods.add("HEAD")
        self._credentials: dict[str, Callable[[], str]] = {}
        for host, auth in policy.credentials.items():
            h = host.strip().rstrip(".").lower()
            if h not in self._exact:
                raise ValueError(f"{host}: credentials need the exact host name in domains")
            self._credentials[h] = auth
        if min(policy.max_redirects, policy.max_requests, policy.max_requests_per_call, policy.max_response_bytes) < 0:
            raise ValueError("limits cannot be negative")
        self._send = policy.transport or pinned_transport
        self._replay = replay
        self._replay_at = self._total = self._this_call = 0
        self._log: list[WebExchange] = []

    def begin_call(self) -> None:
        """A new tool call: its request count starts again, and its exchanges are collected afresh."""
        self._this_call = 0
        self._log = []

    def take_log(self) -> list[WebExchange]:
        log, self._log = self._log, []
        return log

    def host_allowed(self, host: str) -> bool:
        return host in self._exact or any(host.endswith(s) for s in self._suffixes)

    async def send(self, url: WebUrl, method: str, headers: list, body: bytes | None, timeout: float, deadline: float) -> WebResponse:
        """Checks the URL against the policy, then sends one request (no redirects). Raises WebFailure."""
        p = self.policy
        explain = (lambda why: why) if p.explain_blocked else (lambda why: None)
        if url.ip is not None:
            if is_blocked(url.ip):
                raise WebFailure("connect", url.host, url.port, explain(f"{url.host} is a private or reserved address"))
            if url.host not in self._exact:
                raise WebFailure("connect", url.host, url.port, explain(f"{url.host} is not an allowed host"))
        elif not self.host_allowed(url.host):
            raise WebFailure("resolve", url.host, url.port, explain(f"{url.host} is not an allowed host"))
        if url.scheme == "http" and not p.allow_http:
            raise WebFailure("protocol", "http", blocked=explain("only https:// URLs are allowed"))
        if method not in self._methods:
            raise WebFailure("policy", blocked=f"{method} requests are not allowed")
        if body is not None and len(body) > MAX_REQUEST_BYTES:
            raise WebFailure("policy", blocked=f"the request body is larger than {MAX_REQUEST_BYTES} bytes")
        if (any(not k or not all(" " < c < "\x7f" and c != ":" for c in k) or any(c in "\r\n\0" for c in v) for k, v in headers)
                or sum(len(k) + len(v) + 4 for k, v in headers) > 16 * 1024):
            raise WebFailure("bad_header")
        if self._this_call >= p.max_requests_per_call:
            raise WebFailure("policy", blocked=f"the limit of {p.max_requests_per_call} requests in one call was reached")
        if self._total >= p.max_requests:
            raise WebFailure("policy", blocked=f"the limit of {p.max_requests} requests for this session was reached")
        self._this_call += 1
        self._total += 1

        sent = [(k, v) for k, v in headers if k.lower() not in _DROPPED and not k.lower().startswith("proxy-")]
        secrets: list[str] = []
        auth = self._credentials.get(url.host)
        if auth is not None:
            sent = [(k, v) for k, v in sent if k.lower() != "authorization"]  # the agent cannot replace it
            try:
                credential = auth() or ""
            except Exception:  # the message may describe the secret
                raise WebFailure("policy", blocked=f"the credentials for {url.host} are not available") from None
            if any(_is_control(c) for c in credential):
                raise WebFailure("policy", blocked=f"the credentials for {url.host} are not a valid header value")
            if credential:
                sent.append(("Authorization", credential))
            secrets = secrets_of(credential)

        if self._replay is not None:
            exchange = self._next_recorded(method, str(url))
        else:
            exchange = await self._exchange(url, method, sent, body, timeout, secrets, deadline)
        self._log.append(exchange)
        if exchange.error is not None:
            blocked = exchange.error == "blocked"
            raise WebFailure("resolve" if blocked else exchange.error, url.host, url.port,
                             explain(f"{url.host} resolves to a private or reserved address") if blocked else None,
                             detail=str(int(timeout * 1000)) if exchange.error == "timeout" else "")
        truncated = len(exchange.body) > p.max_response_bytes
        return WebResponse(url, method, exchange.status, exchange.reason, list(exchange.headers),
                           exchange.body[:p.max_response_bytes] if truncated else exchange.body, truncated)

    def _next_recorded(self, method: str, url: str) -> WebExchange:
        if self._replay_at < len(self._replay):
            nxt = self._replay[self._replay_at]
            if nxt.method == method and nxt.url == url:
                self._replay_at += 1
                return nxt
        raise WebFailure("replay", detail=f"{method} {url}")

    async def _exchange(self, url: WebUrl, method: str, headers: list, body: bytes | None, timeout: float, secrets: list[str],
                        deadline: float) -> WebExchange:
        from .shell import CallTimeout

        def failed(kind: str) -> WebExchange:
            return WebExchange(method, str(url), error=kind)

        # past the cap by the longest secret, so a credential cut in half at the cap is still scrubbed
        cap = self.policy.max_response_bytes + (len(secrets[0]) if secrets else 0)
        wait = min(timeout, deadline - time.monotonic())
        if wait <= 0:
            raise CallTimeout()
        try:
            status, reason, raw_headers, data = await asyncio.wait_for(
                asyncio.to_thread(self._send, method, str(url), list(headers), body, timeout, cap), wait)
        except (asyncio.TimeoutError, TimeoutError):
            if time.monotonic() >= deadline:
                raise CallTimeout() from None
            return failed("timeout")
        except TransportError as e:
            return failed(e.kind if e.kind in ERROR_KINDS else "recv")
        except ssl.SSLError:
            return failed("tls")
        except socket.gaierror:
            return failed("resolve")
        except ConnectionRefusedError:
            return failed("connect")
        except Exception:
            return failed("recv")
        resp_headers = [(str(k), str(v)) for k, v in raw_headers]
        if sum(len(k) + len(v) + 4 for k, v in resp_headers) > MAX_HEADER_BYTES:
            return failed("recv")
        data = b"" if method == "HEAD" else bytes(data[:cap + 1])
        return WebExchange(method, str(url), int(status), reason or "", [(k, _scrub(v, secrets)) for k, v in resp_headers],
                           _scrub_bytes(data, secrets))

    async def fetch(self, url: WebUrl, method: str, forced: str | None, headers: list, body: bytes | None, follow: bool,
                    max_redirects: int, timeout: float, on_hop, deadline: float) -> tuple[WebResponse, int]:
        """Sends method to url, following redirects when follow (up to max_redirects, never more than the policy's), each
        hop checked again. on_hop sees every response, the final one included. Raises WebFailure."""
        limit = self.policy.max_redirects if max_redirects < 0 else min(max_redirects, self.policy.max_redirects)
        first_host = url.host
        redirects = 0
        while True:
            hop_headers = headers if url.host == first_host else [(k, v) for k, v in headers if k.lower() not in ("authorization", "cookie")]
            response = await self.send(url, forced or method, hop_headers, body, timeout, deadline)
            if on_hop:
                on_hop(response)
            location = response.header("Location")
            if not follow or response.status not in (301, 302, 303, 307, 308) or not location:
                return response, redirects
            if redirects >= limit:
                raise WebFailure("redirects", url.host, url.port, detail=str(limit))
            url = url.resolve(location)
            if response.status == 303 and method != "HEAD" or response.status in (301, 302) and method == "POST":
                method, body = "GET", None
                headers = [(k, v) for k, v in headers if k.lower() != "content-type"]
            redirects += 1


def _scrub(s: str, secrets: list[str]) -> str:
    for secret in secrets:
        s = s.replace(secret, "[REDACTED]")
    return s


def _scrub_bytes(data: bytes, secrets: list[str]) -> bytes:
    for secret in secrets:
        data = data.replace(secret.encode("utf-8"), b"[REDACTED]")
    return data


def blob_name(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def pinned_transport(method, url, headers, body, timeout, limit):
    """The network: resolves the name once, refuses it if any address is internal, and connects to an address it checked.
    The TLS handshake and Host header still use the name, and nothing resolves it a second time (DNS rebinding). No
    proxies (the environment's proxy settings are never read), no cookies, no redirects."""
    u = WebUrl.parse(url)
    try:
        found = _resolve_for_tests(u.host) if _resolve_for_tests else [ai[4][0] for ai in socket.getaddrinfo(u.host, u.port, type=socket.SOCK_STREAM)]
        addresses = [ipaddress.ip_address(str(a).split("%")[0]) for a in found]
    except (OSError, UnicodeError, ValueError):
        raise TransportError("resolve") from None
    if not addresses:
        raise TransportError("resolve")
    for a in addresses:
        plain = a.ipv4_mapped or a if a.version == 6 else a
        if is_blocked(plain.packed) and not (_allow_loopback_for_tests and plain.is_loopback):
            raise TransportError("blocked")
    sock = None
    for a in addresses:
        s = socket.socket(socket.AF_INET6 if a.version == 6 else socket.AF_INET, socket.SOCK_STREAM)
        s.settimeout(timeout)
        try:
            s.connect((str(a), u.port))
            sock = s
            break
        except TimeoutError:
            s.close()
            raise
        except OSError:
            s.close()
    if sock is None:
        raise TransportError("connect")
    try:
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        if u.scheme == "https":
            try:
                sock = ssl.create_default_context().wrap_socket(sock, server_hostname=u.host)
            except (ssl.SSLError, ssl.CertificateError):
                raise TransportError("tls") from None
        conn = http.client.HTTPConnection(u.host, u.port, timeout=timeout)
        conn.sock = sock
        conn.putrequest(method, u.path_and_query, skip_host=True, skip_accept_encoding=True)
        conn.putheader("Host", u.host_for_url + ("" if u.default_port else f":{u.port}"))
        for k, v in headers:
            conn.putheader(k, v)
        if body is not None:
            conn.putheader("Content-Length", str(len(body)))
        conn.endheaders(body)
        resp = conn.getresponse()  # http.client caps a header line at 64 KB and the count at 100
        data = bytearray()
        if method != "HEAD":
            while len(data) <= limit and (chunk := resp.read(min(65536, limit + 1 - len(data)))):
                data += chunk
        return resp.status, resp.reason or "", resp.getheaders(), bytes(data)
    except (TransportError, TimeoutError):
        raise
    except (http.client.HTTPException, OSError):
        raise TransportError("recv") from None
    finally:
        sock.close()
