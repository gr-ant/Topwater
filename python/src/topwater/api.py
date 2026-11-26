"""Authenticated HTTP APIs the agent can call but not redirect or read the credential of.

    from topwater.api import ApiEndpoint, register_api
    register_api(shell, "api", {"jira": ApiEndpoint("https://acme.atlassian.net/rest/api/3", auth=lambda: "Bearer " + token,
                                                    paths=["/issue/", "/search"])})
    # the agent: api get jira /issue/PROJ-1 --query fields=summary | jq .

The agent picks an alias, an allowed method and a path under the base URL; the host, scheme, port and credential are
fixed here. The credential is added in host code and scrubbed from everything that comes back. Redirects are never
followed: a 3xx is reported with its Location and exit code 1.
"""
from __future__ import annotations

import asyncio
import unicodedata
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Callable, Mapping, Sequence
from urllib.parse import quote, unquote, urlsplit

from .vfs import ShellError
from .webaccess import decode_body, secrets_of

# headers that cannot carry a credential, change the destination or smuggle a second request
ALLOWED_HEADERS = ("Accept", "Accept-Language", "Content-Type", "If-Match", "If-None-Match")
MAX_REQUEST_BYTES = 1_000_000  # shortcut: fixed upload cap, make it an ApiEndpoint option when a host needs bigger uploads


@dataclass
class ApiEndpoint:
    """One HTTP API. The agent supplies only a path under `base_url`."""
    base_url: str
    description: str | None = None
    auth: Callable[[], str] | None = None  # returns the credential header's value (e.g. "Bearer " + token); never shown to the agent
    auth_header: str = "Authorization"
    methods: Sequence[str] = ("GET",)
    paths: Sequence[str] | None = None  # allowed path prefixes under base_url, checked after resolving . and ..
    allow_http: bool = False  # permit plain http:// (local test servers); the credential then travels unencrypted
    timeout: float = 30.0
    max_response_bytes: int = 1_000_000  # bodies beyond this are dropped, with a note on stderr


# A transport sends one request: (method, url, headers, body, timeout, limit) -> (status, reason, [(name, value)], body).
# It must not follow redirects. It may return more than `limit` bytes of body (they are cut), but need not.
Transport = Callable[[str, str, Mapping[str, str], "bytes | None", float, int], "tuple[int, str, list[tuple[str, str]], bytes]"]


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None  # urllib then raises HTTPError, which carries the 3xx response


_opener = urllib.request.build_opener(_NoRedirect)  # no cookie processor: nothing is remembered between calls


def urllib_transport(method, url, headers, body, timeout, limit):
    req = urllib.request.Request(url, data=body, headers=dict(headers), method=method)
    try:
        resp = _opener.open(req, timeout=timeout)
    except urllib.error.HTTPError as e:  # non-2xx answers, redirects included, are still answers
        resp = e
    with resp:
        return resp.status, resp.reason or "", list(resp.headers.items()), resp.read(limit + 1)


def register_api(shell, program: str, endpoints: Mapping[str, ApiEndpoint], transport: Transport | None = None):
    """Registers `program` (e.g. "api") so the agent can call the given HTTP APIs by alias: `api get jira /issue/PROJ-1`.

    `transport` replaces the default urllib sender (tests, proxies); it must not follow redirects itself."""
    send = transport or urllib_transport
    bases = {}
    for alias, e in endpoints.items():
        u = urlsplit(e.base_url)
        if not u.netloc or u.scheme != "https" and not (e.allow_http and u.scheme == "http"):
            raise ValueError(f"{alias}: the base URL must be an absolute https:// URL (set allow_http for http://)")
        if "@" in u.netloc or u.query or u.fragment or "?" in e.base_url or "#" in e.base_url:
            raise ValueError(f"{alias}: the base URL must not contain user info, a query or a fragment")
        if any(not _is_token(m) for m in e.methods) or _bad_auth_header(e.auth_header):
            raise ValueError(f"{alias}: invalid method or credential header")
        bases[alias] = u

    first = next(iter(endpoints), "NAME")

    def help_text() -> str:
        out = ["Call this application's HTTP APIs. Credentials are added for you.\n\n",
               f"Usage: {program} METHOD ENDPOINT PATH [options]\n  e.g. {program} get {first} /path --query key=value | jq .\n\nEndpoints:\n"]
        for alias, e in endpoints.items():
            out.append(f"  {alias}  {e.description or ''}\n      {e.base_url.rstrip('/')}  methods: {', '.join(m.upper() for m in e.methods)}")
            out.append(f"  paths: {', '.join(e.paths)}\n" if e.paths else "\n")
        out.append("\nOptions:\n")
        out.append("  -d, --data BODY        request body; @FILE reads a file, - reads stdin (Content-Type defaults to application/json)\n")
        out.append("  -q, --query KEY=VALUE  add a query parameter (repeatable)\n")
        out.append(f"  -H, --header 'NAME: VALUE'  one of {', '.join(ALLOWED_HEADERS)}\n")
        out.append("  -i, --include          print the status line and response headers before the body\n")
        out.append("\nThe response body goes to stdout. A status outside 2xx exits 1 with the status on stderr; redirects are not followed.\n")
        return "".join(out)

    async def run(ctx) -> int:
        args = list(ctx.args)
        if not args or any(a in ("--help", "-h") for a in args) or args[0] == "help":
            ctx.stdout.write(help_text())
            return 0

        def usage(message: str) -> int:
            ctx.stderr.write(f"{program}: {message}\nTry `{program} --help`.\n")
            return 2

        positional: list[str] = []
        query: list[str] = []
        headers: list[tuple[str, str]] = []
        data: str | None = None
        include = False
        i = 0
        while i < len(args):
            arg = args[i]
            flag, inline = arg, ""
            eq = arg.find("=")
            has_inline = arg.startswith("--") and eq > 0
            if has_inline:
                flag, inline = arg[:eq], arg[eq + 1:]

            def nxt():
                nonlocal i
                if has_inline:
                    return inline
                i += 1
                return args[i] if i < len(args) else None

            if flag in ("-i", "--include"):
                include = True
            elif flag in ("-d", "--data"):
                data = nxt()
                if data is None:
                    return usage(f"{flag} needs a value")
            elif flag in ("-q", "--query"):
                kv = nxt()
                at = kv.find("=") if kv is not None else -1
                if at <= 0:
                    return usage(f"{flag} expects KEY=VALUE")
                # escaped, so a value cannot add parameters, a fragment or a line break
                query.append(quote(kv[:at], safe="") + "=" + quote(kv[at + 1:], safe=""))
            elif flag in ("-H", "--header"):
                hv = nxt()
                colon = hv.find(":") if hv is not None else -1
                if colon <= 0:
                    return usage(f"{flag} expects 'NAME: VALUE'")
                raw_name, value = hv[:colon].strip(), hv[colon + 1:].strip()
                name = next((h for h in ALLOWED_HEADERS if h.lower() == raw_name.lower()), None)
                if name is None:
                    return usage(f"header '{raw_name}' is not allowed; allowed: {', '.join(ALLOWED_HEADERS)}")
                if any(_is_control(c) for c in value):
                    return usage("header values cannot contain control characters")
                headers.append((name, value))
            elif arg.startswith("-") and len(arg) > 1:
                return usage(f"unknown option {arg}")
            else:
                positional.append(arg)
            i += 1
        if len(positional) != 3:
            return usage(f"expected METHOD ENDPOINT PATH, e.g. `{program} get {first} /path`")
        method, alias, path = positional[0].upper(), positional[1], positional[2]
        ep = endpoints.get(alias)
        if ep is None:
            return usage(f"unknown endpoint '{alias}'; endpoints: {', '.join(endpoints)}")
        if not _is_token(method) or method not in (m.upper() for m in ep.methods):
            return usage(f"{method} is not allowed for {alias}; allowed: {', '.join(m.upper() for m in ep.methods)}")
        url = _build_url(bases[alias], ep.paths, path, query)
        if url is None:
            return usage(f"path '{_printable(path)}' is not allowed for {alias}" + (f"; allowed: {', '.join(ep.paths)}" if ep.paths else ""))

        body: bytes | None = None
        if data is not None:
            try:
                body = ctx.stdin.encode() if data == "-" else ctx.fs.read_bytes(ctx.resolve(data[1:])) if data.startswith("@") else data.encode()
            except (ShellError, OSError) as e:
                return usage(str(e))
            if len(body) > MAX_REQUEST_BYTES:
                return usage(f"the request body is {len(body)} bytes; the limit is {MAX_REQUEST_BYTES}")

        try:
            credential = ep.auth() if ep.auth else ""
        except Exception:  # the message may describe the secret
            ctx.stderr.write(f"{program}: the credentials for {alias} are not available\n")
            return 1
        credential = credential or ""
        secrets = secrets_of(credential)

        def scrub(s: str) -> str:
            for secret in secrets:
                s = s.replace(secret, "[REDACTED]")
            return s

        send_headers: dict[str, str] = {}
        if body is not None:
            send_headers["Content-Type"] = "application/json"
        for name, value in headers:
            send_headers[name] = value
            if name == "Content-Type" and body is None:
                body = b""
        if credential:
            if any(_is_control(c) for c in credential):
                ctx.stderr.write(f"{program}: the credentials for {alias} are not a valid header value\n")
                return 1
            send_headers[ep.auth_header] = credential

        # read past the cap by the longest secret, so a credential cut in half at the cap still gets scrubbed
        limit = ep.max_response_bytes + (len(secrets[0]) if secrets else 0)
        try:
            status, reason, resp_headers, raw = await asyncio.wait_for(
                asyncio.to_thread(send, method, url, send_headers, body, ep.timeout, limit), ep.timeout)
        except (asyncio.TimeoutError, TimeoutError):
            ctx.stderr.write(f"{program}: {alias} did not answer within {ep.timeout:g} s\n")
            return 1
        except Exception as e:
            why = (str(getattr(e, "reason", None) or e) if isinstance(e, OSError) else type(e).__name__)
            ctx.stderr.write(scrub(f"{program}: {method} {alias} {_printable(path)} failed: {' '.join(why.splitlines())}") + "\n")
            return 1

        encoding = next((v for k, v in resp_headers if k.lower() == "content-encoding"), "").strip().lower()
        if encoding not in ("", "identity"):
            # a compressed body would hide the credential from the scrub: decode it first, within the same cap
            decoded = decode_body(raw, encoding, limit + 1)
            if decoded is None:
                ctx.stderr.write(scrub(f"{program}: {method} {alias} {_printable(path)} failed: unsupported content encoding "
                                       f"{_printable(encoding)}") + "\n")
                return 1
            raw = decoded
        truncated = len(raw) > ep.max_response_bytes
        text = scrub(raw[: limit].decode("utf-8", "replace"))
        if truncated and len(text) > ep.max_response_bytes:
            text = text[: ep.max_response_bytes]
        status_line = f"HTTP {status} {reason}".rstrip()
        if include:
            ctx.stdout.write(scrub("".join([status_line, "\n", *(f"{k}: {v}\n" for k, v in resp_headers), "\n"])))
        ctx.stdout.write(text if not text or text.endswith("\n") else text + "\n")
        if truncated:
            ctx.stderr.write(f"{program}: response truncated at {ep.max_response_bytes} bytes\n")
        if 200 <= status < 300:
            return 1 if truncated else 0
        location = next((v for k, v in resp_headers if k.lower() == "location"), "nowhere")
        ctx.stderr.write(scrub(f"{program}: {status_line} (redirect to {location} not followed)" if 300 <= status < 400
                               else f"{program}: {status_line}") + "\n")
        return 1

    shell.register_command(program, run, f"call HTTP APIs ({', '.join(endpoints)}); run `{program} --help`",
                           f"{program} METHOD ENDPOINT PATH [options]")
    return shell


_URL_SAFE = "%&=+,;:/?!$'()*~[]-._"  # what may stay unescaped in the path and query the agent typed


def _is_control(c: str) -> bool:
    return unicodedata.category(c) == "Cc"


def _is_token(s: str) -> bool:
    return bool(s) and s.isascii() and s.isalpha()


def _bad_auth_header(name: str) -> bool:
    # the agent may set the allowed headers, so the credential cannot live in one of them
    return (not name or any(c <= " " or ord(c) >= 127 or c == ":" for c in name)
            or name.lower() in {h.lower() for h in ALLOWED_HEADERS} or name.lower() == "host")


def _printable(s: str) -> str:
    return "".join("?" if _is_control(c) else c for c in s)


def _is_cleanly_escaped(s: str) -> bool:
    """Every % starts a valid escape."""
    i = s.find("%")
    while i >= 0:
        if i + 2 >= len(s) or any(c not in "0123456789abcdefABCDEF" for c in s[i + 1:i + 3]):
            return False
        i = s.find("%", i + 1)
    return True


def _under_prefix(path: str, prefix: str) -> bool:
    """Prefix match on whole segments: "/search" allows "/search" and "/search/x" but not "/searchadmin"."""
    return path.startswith(prefix) and (prefix.endswith("/") or len(path) == len(prefix) or path[len(prefix)] == "/")


def _build_url(base, allowed: Sequence[str] | None, path: str, extra_query: Sequence[str]) -> str | None:
    """The URL for `path` under `base`, or None when the path could reach anything else: another origin, a place
    above the base path, or outside the allowed prefixes."""
    raw_path, _, raw_query = path.partition("?")
    # a leading single slash and nothing that can start an authority, user info, fragment, or a second line
    if not raw_path.startswith("/") or raw_path.startswith("//") or any(c in "\\@#" or _is_control(c) or ord(c) > 127 for c in path):
        return None
    if "%" in raw_query and not _is_cleanly_escaped(raw_query):
        return None
    segments: list[str] = []
    parts = raw_path[1:].split("/")
    for i, seg in enumerate(parts):
        last = i == len(parts) - 1
        if not seg and not last:
            return None  # "//" anywhere could read as an authority
        if "%" in seg and not _is_cleanly_escaped(seg):
            return None
        decoded = unquote(seg)
        # decoded once and checked, so %2e%2e, %2f and double-encoded (%252e) tricks cannot reach a server that decodes again
        if "/" in decoded or "%" in decoded or any(c == "\\" or _is_control(c) for c in decoded):
            return None
        dots = decoded.split(";")[0]
        if dots in (".", ".."):
            if dots != decoded:
                return None  # "..;" is ".." to some Java servers
            if dots == "..":
                if not segments:
                    return None
                segments.pop()
            if last:
                segments.append("")  # "/a/." is the directory "/a/"
            continue
        segments.append(seg)
    relative = "/" + "/".join(segments)
    if allowed and not any(_under_prefix(relative, p) for p in allowed):
        return None
    base_path = base.path.rstrip("/")
    query_text = "&".join([q for q in (quote(raw_query, safe=_URL_SAFE), *extra_query) if q])
    url = f"{base.scheme}://{base.netloc}{base_path}{quote(relative, safe=_URL_SAFE)}" + (f"?{query_text}" if query_text else "")
    # belt and braces: whatever the parsing above missed, the result must still be the same origin under the base path
    u = urlsplit(url)
    same = (u.scheme == base.scheme and u.netloc == base.netloc and "@" not in u.netloc
            and (u.path == base_path or u.path.startswith(base_path + "/")))
    return url if same else None
