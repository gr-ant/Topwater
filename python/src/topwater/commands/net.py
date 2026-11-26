"""curl and wget over the shell's web access (Shell.allow_web); without one every host fails to resolve."""
from __future__ import annotations

import asyncio
import base64
import re
import time
import zlib
from urllib.parse import unquote

from .. import bytetext
from .._spec import COMMANDS
from ..vfs import ShellError, utc_now
from ..webaccess import WebFailure, WebPolicy, WebSession, WebUrl
from . import spec_message

_NO_NETWORK = WebSession(WebPolicy(explain_blocked=False))
_FLOAT = re.compile(r"\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*")


def register(shell) -> None:
    shell.register_command("curl", _curl)
    shell.register_command("wget", _wget)


def _seconds(s: float) -> float:
    return 86400.0 if s <= 0 or s > 86400 else s


def _float(text: str) -> float | None:
    return float(text) if _FLOAT.fullmatch(text) else None


def _int32(text: str, sign: bool) -> int | None:
    if not re.fullmatch(r"[+-]?\d+" if sign else r"\d+", text):
        return None
    v = int(text)
    return v if -2**31 <= v < 2**31 else None


def _set_header(headers: list, name: str, value: str | None) -> list:
    i = next((n for n, (k, _) in enumerate(headers) if k.lower() == name.lower()), -1)
    if value is None:
        if i >= 0:
            del headers[i]
    elif i >= 0:
        headers[i] = (headers[i][0], value)
    else:
        headers.append((name, value))
    return headers


def _remote_name(url: WebUrl) -> str:
    """The last path segment of the URL, as -O and wget name the file (empty for a directory)."""
    path = url.path_and_query.split("?")[0]
    return path[path.rfind("/") + 1:]


def _errno(e: ShellError) -> str:
    return str(e).rsplit(": ", 1)[-1]


# ---------- curl ----------

_CURL_LONG = {
    "data": "d", "data-ascii": "d", "data-binary": "\x01", "data-raw": "\x02", "data-urlencode": "\x03", "json": "\x04",
    "get": "G", "request": "X", "header": "H", "user-agent": "A", "user": "u", "location": "L", "max-redirs": "\x05",
    "include": "i", "head": "I", "output": "o", "remote-name": "O", "fail": "f", "fail-with-body": "\x06", "silent": "s",
    "show-error": "S", "write-out": "w", "max-time": "m", "connect-timeout": "\x07", "compressed": "\x08", "verbose": "v",
    "insecure": "k", "help": "h", "version": "V", "retry": "\x10", "retry-delay": "\x11", "retry-max-time": "\x12",
    "retry-all-errors": "\x13", "retry-connrefused": "\x14", "proto": "\x15", "proto-redir": "\x16", "fail-early": "\x17",
    "no-progress-meter": "\x18", "tlsv1": "\x19", "tlsv1.0": "\x19", "tlsv1.1": "\x19", "tlsv1.2": "\x19", "tlsv1.3": "\x19",
    "ssl-reqd": "\x19", "http1.1": "\x19", "http2": "\x19", "tls-max": "\x1a", "progress-bar": "\x1b",
}
_CURL_FLAGS = "GLiIOfsSvkhV\x06\x08\x13\x14\x17\x18\x19\x1b"
_CURL_VALUED = "dXHAuowm\x01\x02\x03\x04\x05\x07\x10\x11\x12\x15\x16\x1a"

# what --proto names: the protocols curl 8.5 on Ubuntu knows (Topwater itself only speaks http and https)
_CURL_PROTOCOLS = ("dict", "file", "ftp", "ftps", "gopher", "gophers", "http", "https", "imap", "imaps", "ldap", "ldaps", "mqtt",
                   "pop3", "pop3s", "rtmp", "rtmpe", "rtmps", "rtmpt", "rtmpte", "rtmpts", "rtsp", "scp", "sftp", "smb", "smbs", "smtp",
                   "smtps", "telnet", "tftp", "ws", "wss")
_REDIR_PROTOCOLS = ("http", "https", "ftp", "ftps")


def _proto_set(spec: str, start, warn) -> set | None:
    """--proto and --proto-redir: from start, each comma-separated name added (+ or none), removed (-) or made the only one
    (=); "all" is every protocol. An unknown name gets a warning and is skipped; None when a sign other than these, or none
    of a name, makes it a usage error."""
    result = set(start)
    for token in filter(None, spec.split(",")):
        i, action = 0, "+"
        while i < len(token) and not (token[i].isascii() and token[i].isalnum()):
            if token[i] not in "+-=":
                return None
            action = token[i]
            i += 1
        if i == len(token):
            return None
        name = token[i:].lower()
        if action == "=":
            result.clear()
        names = _CURL_PROTOCOLS if name == "all" else (name,) if name in _CURL_PROTOCOLS else ()
        if not names:
            warn(spec_message("curl", "unknownProtocol", name=token[i:]))
            continue
        if action == "-":
            result -= set(names)
        else:
            result |= set(names)
    return result


def _curl_warning(text: str) -> str:
    """curl's "Warning: " lines, wrapped at 79 columns (the width curl assumes when stderr is not a terminal)."""
    width = 79 - 9
    out = []
    rest = text
    while rest:
        if len(rest) <= width:
            out.append(spec_message("curl", "warning", text=rest) + "\n")
            break
        cut = width - 1
        while cut > 0 and rest[cut] not in " \t":
            cut -= 1
        if cut == 0:
            cut = width - 1
        out.append(spec_message("curl", "warning", text=rest[:cut + 1]) + "\n")
        rest = rest[cut + 1:]
    return "".join(out)


async def _curl(ctx):
    web = ctx.shell._web or _NO_NETWORK
    spec = COMMANDS["curl"]

    def usage(message: str) -> int:
        ctx.stderr.write(message + "\n" + spec.try_help)
        return 2

    opts: list[tuple[str, str | None, str]] = []
    urls: list[str] = []
    args = ctx.args
    i = 0
    while i < len(args):
        arg = args[i]
        if arg.startswith("--") and len(arg) > 2:
            key = _CURL_LONG.get(arg[2:])
            if key is None:
                return usage(spec_message("curl", "unknownOption", option=arg))
            if key in _CURL_VALUED:
                i += 1
                if i >= len(args):
                    return usage(spec_message("curl", "needsParameter", option=arg))
                opts.append((key, args[i], arg))
            else:
                opts.append((key, None, arg))
        elif arg.startswith("-") and len(arg) > 1:
            for j in range(1, len(arg)):
                c = arg[j]
                if c == "#":
                    opts.append(("\x1b", None, "-#"))
                    continue
                letter = c.isascii() and c.isalpha()
                if c in _CURL_VALUED and letter:
                    if j + 1 < len(arg):
                        value = arg[j + 1:]
                    else:
                        i += 1
                        value = args[i] if i < len(args) else None
                    if value is None:
                        return usage(spec_message("curl", "needsParameter", option="-" + c))
                    opts.append((c, value, "-" + c))
                    break
                if c not in _CURL_FLAGS or not letter:
                    return usage(spec_message("curl", "unknownOption", option="-" + c))
                opts.append((c, None, "-" + c))
        else:
            urls.append(arg)
        i += 1

    def has(k: str) -> bool:
        return any(o[0] == k for o in opts)

    def last(k: str) -> str | None:
        return next((o[1] for o in reversed(opts) if o[0] == k), None)

    if has("h"):
        ctx.stdout.write(spec.help)
        return 0
    if has("V"):
        ctx.stdout.write(spec.version + "\n")
        return 0
    if not urls:
        ctx.stderr.write(spec.try_help)
        return 2

    silent, head, verbose = has("s") and not has("S"), has("I"), has("v")
    include = has("i") or head
    fail, fail_with_body, follow, compressed, get = has("f"), has("\x06"), has("L"), has("\x08"), has("G")

    max_time, max_redirs = 0.0, -1
    for key, value, name in opts:
        if key == "\x05":
            n = _int32(value, True)
            if n is None:
                return usage(spec_message("curl", "badNumber", option=name))
            max_redirs = n
        elif key in ("m", "\x07"):
            s = _float(value)
            if s is None or s < 0:
                return usage(spec_message("curl", "badNumber", option=name))
            max_time = s if max_time == 0 else min(max_time, s)
    timeout = _seconds(max_time) if max_time > 0 and _seconds(max_time) < web.policy.timeout else web.policy.timeout

    # --retry, --retry-delay, --retry-max-time: whole seconds, not negative
    retry = [0, 0, 0]
    for key, value, name in opts:
        if key in ("\x10", "\x11", "\x12"):
            if not re.fullmatch(r"[+-]?\d+", value):
                return usage(spec_message("curl", "badNumber", option=name))
            if int(value) < 0:
                return usage(spec_message("curl", "negativeNumber", option=name))
            retry[ord(key) - 0x10] = int(value)
    retries, retry_delay, retry_max_time = retry
    retry_all, retry_refused, fail_early = has("\x13"), has("\x14"), has("\x17")
    protocols, redir_protocols = set(_CURL_PROTOCOLS), set(_REDIR_PROTOCOLS)
    for key, value, name in opts:
        if key in ("\x15", "\x16"):
            got = _proto_set(value, _CURL_PROTOCOLS if key == "\x15" else _REDIR_PROTOCOLS, lambda w: ctx.stderr.write(_curl_warning(w)))
            if got is None:
                return usage(spec_message("curl", "badlyUsed", option=name))
            if key == "\x15":
                protocols = got
            else:
                redir_protocols = got

    # the body: -d values joined with &, as curl does
    parts: list[bytes] = []
    json_data = any_data = False
    for key, value, name in opts:
        if key not in ("d", "\x01", "\x02", "\x03", "\x04"):
            continue
        any_data = True
        json_data |= key == "\x04"
        try:
            if key == "\x03":
                data = _urlencode_data(value).encode()
            elif key != "\x02" and value.startswith("@"):
                f = value[1:]
                data = bytetext.encode(ctx.stdin) if f == "-" else ctx.fs.read_bytes(ctx.resolve(f))
                if key in ("d", "\x04"):
                    data = data.replace(b"\r", b"").replace(b"\n", b"")
            else:
                data = bytetext.encode(value)
        except ShellError:
            ctx.stderr.write(spec_message("curl", "readFile", option=name) + "\n" + spec.try_help)
            return 26
        parts.append(data)
    body = (b"" if json_data else b"&").join(parts) if any_data else None
    query = body.decode("utf-8", "replace") if get and body is not None else None
    if get:
        body = None

    headers = [("User-Agent", "curl/8.5.0"), ("Accept", "*/*")]
    if body is not None:
        _set_header(headers, "Content-Type", "application/json" if json_data else "application/x-www-form-urlencoded")
    if json_data:
        _set_header(headers, "Accept", "application/json")
    if compressed:
        _set_header(headers, "Accept-Encoding", "deflate, gzip")
    agent = last("A")
    if agent is not None:
        _set_header(headers, "User-Agent", agent or None)
    for key, h, _ in opts:
        if key != "H":
            continue
        colon, semi = h.find(":"), h.find(";")
        if colon > 0 and (semi < 0 or colon < semi):
            v = h[colon + 1:].strip()
            _set_header(headers, h[:colon].strip(), v or None)
        elif semi > 0 and not h[semi + 1:].strip():
            _set_header(headers, h[:semi].strip(), "")
    user = last("u")

    method = "HEAD" if head else "POST" if body is not None else "GET"
    forced = last("X")
    outputs = [(k, v) for k, v, _ in opts if k in ("o", "O")]
    exit_code = 0

    for raw in urls:
        output = outputs.pop(0) if outputs else ("", None)
        started = time.monotonic()
        tries = 0
        while True:
            buffer = bytearray()
            code = redirects = 0
            hops: list = []  # (response, seconds since the start, request bytes)
            last_r = None
            message, url_text, target, retriable = "", raw, None, True
            clock = time.monotonic()
            try:
                if query is not None:
                    url_text += ("&" if "?" in raw else "?") + query
                url = WebUrl.parse(url_text)
                if url.scheme not in protocols:
                    raise WebFailure("protocol", url.scheme)
                sent = list(headers)
                user_info = user if user is not None else unquote(url.user_info) if url.user_info is not None else None
                if user_info is not None and not any(k.lower() == "authorization" for k, _ in sent):
                    sent.append(("Authorization", "Basic " + base64.b64encode((user_info if ":" in user_info else user_info + ":").encode()).decode()))
                target = _remote_name(url) if output[0] == "O" else output[1]
                if target == "":
                    raise WebFailure("recv", detail="noRemoteName")

                def on_hop(r, sent=sent, hops=hops, buffer=buffer, clock=clock):
                    first = not hops
                    host = f"{r.url.host_for_url}{'' if r.url.default_port else ':' + str(r.url.port)}"
                    # shortcut: the body counts toward the first request only; a 303 drops it, other redirects resend it
                    request = (f"{r.method} {r.url.path_and_query} HTTP/1.1\r\nHost: {host}\r\n" + "".join(f"{k}: {v}\r\n" for k, v in sent)
                               + (f"Content-Length: {len(body)}\r\n" if body is not None and first else "") + "\r\n")
                    hops.append((r, time.monotonic() - clock, len(request.encode()) + (len(body) if body is not None and first else 0)))
                    if verbose:
                        out = [f"> {r.method} {r.url.path_and_query} HTTP/1.1\r\n> Host: {host}\r\n"]
                        out += [f"> {k}: {v}\r\n" for k, v in sent]
                        out.append(f"> \r\n< HTTP/1.1 {r.status} {r.reason}\r\n")
                        out += [f"< {k}: {v}\r\n" for k, v in r.headers]
                        ctx.stderr.write("".join(out) + "< \r\n")
                    if include:
                        buffer.extend(_status_block(r).encode())
                    # --proto-redir (and --proto) decide where a redirect may lead
                    location = r.header("Location")
                    if follow and r.status in (301, 302, 303, 307, 308) and location:
                        nxt = r.url.resolve(location).scheme
                        if nxt not in protocols or nxt not in redir_protocols:
                            raise WebFailure("protocol", nxt)

                last_r, redirects = await web.fetch(url, method, forced, sent, body, follow, max_redirs, timeout, on_hop, ctx.shell._deadline)
                data = last_r.body
                encoding = (last_r.header("Content-Encoding") or "").strip().lower()
                if compressed and not last_r.truncated and not last_r.error and encoding in ("gzip", "x-gzip", "deflate") and data:
                    data = _decompress(data, encoding != "deflate", web.policy.max_response_bytes)
                    if data is None:
                        raise WebFailure("recv")
                if (fail or fail_with_body) and last_r.status >= 400:
                    if fail_with_body:
                        buffer.extend(data)
                    code = 22
                    message = spec_message("curl", "httpError", status=str(last_r.status))
                else:
                    if not head:
                        buffer.extend(data)
                    if last_r.truncated:
                        code, message = 63, spec_message("curl", "tooLarge")
                    elif last_r.error:
                        code, message = _curl_cut_short(last_r)
                if target is not None and target != "-" and (code in (0, 18, 63) or fail_with_body):
                    try:
                        ctx.fs.write_bytes(ctx.resolve(target), bytes(buffer))
                    except ShellError:
                        code, message = 23, spec_message("curl", "writeFailed")
                    buffer.clear()
            except WebFailure as f:
                code, message = _curl_error(f, web.policy.explain_blocked)
                retriable = f.blocked is None and f.kind != "replay"  # the web policy's answer would not change
            if last_r is None and hops:
                last_r = hops[-1][0]
            if buffer:
                ctx.stdout.write(bytetext.decode(bytes(buffer)))
            if code and not silent:
                ctx.stderr.write(spec_message("curl", "error", code=str(code), message=message) + "\n")

            total = time.monotonic() - clock
            size = len(last_r.body) if last_r else 0
            upload = len(body) if body is not None else 0
            connects = sum(1 for k, h in enumerate(hops) if k == 0 or (h[0].url.host, h[0].url.port) != (hops[k - 1][0].url.host, hops[k - 1][0].url.port))
            secs = lambda x: f"{x:.6f}"  # noqa: E731
            variables = {
                "errormsg": "" if code == 0 else message, "filename_effective": "" if target in (None, "-") else target, "url": url_text,
                "scheme": last_r.url.scheme.upper() if last_r else "", "http_version": "1.1" if last_r else "0",
                "remote_port": str(last_r.url.port) if last_r else "", "num_connects": str(connects),
                "size_upload": str(upload), "size_header": str(sum(len(_status_block(h[0]).encode()) for h in hops)),
                "size_request": str(sum(h[2] for h in hops)),
                "speed_download": str(int(size / total) if total > 0 else 0), "speed_upload": str(int(upload / total) if total > 0 and hops else 0),
                # the phases before the first response byte are not timed apart
                "time_total": secs(total), "time_namelookup": secs(0), "time_connect": secs(0), "time_appconnect": secs(0),
                "time_pretransfer": secs(0), "time_starttransfer": secs(hops[-1][1] if hops else 0),
                "time_redirect": secs(hops[-2][1] if len(hops) > 1 else 0),
            }

            # --retry: a timeout or unresolved name, a refused connection (--retry-connrefused), a transient HTTP status, or anything (--retry-all-errors)
            why = None
            if retriable and tries < retries:
                if code in (6, 28):
                    why = "retryTimeout"
                elif code == 7 and retry_refused:
                    why = "retryRefused"
                elif code in (0, 22) and last_r is not None and last_r.status in (408, 429, 500, 502, 503, 504):
                    why = "retryHttp"
                elif retry_all and code:
                    why = "retryAll"
            if why is None or retry_max_time > 0 and time.monotonic() - started >= retry_max_time:
                break
            wait = retry_delay if retry_delay > 0 else min(600, 1 << min(tries, 10))
            # an HTTP retry waits at least the Retry-After seconds (shortcut: the HTTP-date form is not read)
            after = (last_r.header("Retry-After") or "").strip() if why == "retryHttp" else ""
            if after.isascii() and after.isdigit() and int(after) > wait:
                wait = int(after)
            if not has("s"):  # -S brings back errors, not warnings
                ctx.stderr.write(_curl_warning(spec_message("curl", "retrying", why=spec_message("curl", why), seconds=str(wait), left=str(retries - tries))))
            sh = ctx.shell
            # the call's deadline cuts the wait short
            await asyncio.sleep(max(0.0, min(wait, sh._deadline - time.monotonic())))
            sh.check_deadline()
            tries += 1
        exit_code = code  # without --fail-early, the last URL's status
        fmt = last("w")
        if fmt is not None:
            ctx.stdout.write(_write_out(ctx, fmt, last_r, redirects, code, method, forced, variables))
        if fail_early and code:
            break
    return exit_code


def _curl_cut_short(r) -> tuple[int, str]:
    """A response whose body ended early (written as far as it came)."""
    length = (r.header("Content-Length") or "").strip()
    if length.isascii() and length.isdigit() and int(length) > len(r.body):
        return 18, spec_message("curl", "partial", bytes=str(int(length) - len(r.body)))
    return 18, spec_message("curl", "partialChunked")


def _curl_error(f: WebFailure, explain: bool) -> tuple[int, str]:
    if f.blocked is not None and (explain or f.kind == "policy"):
        return {"resolve": 6, "connect": 7}.get(f.kind, 1), spec_message("curl", "blocked", reason=f.blocked)
    k = f.kind
    if k == "bad_url":
        return 3, spec_message("curl", "badUrl")
    if k == "bad_hostname":
        return 3, spec_message("curl", "badHostname")
    if k == "protocol":
        return 1, spec_message("curl", "protocol", scheme=f.host)
    if k == "resolve":
        return 6, spec_message("curl", "resolve", host=f.host)
    if k == "connect":
        return 7, spec_message("curl", "connect", host=f.host, port=str(f.port))
    if k == "timeout":
        return 28, spec_message("curl", "timeout", ms=f.detail or "0", bytes="0")
    if k == "tls":
        return 60, spec_message("curl", "tls", host=f.host)
    if k == "redirects":
        return 47, spec_message("curl", "redirects", max=f.detail)
    if k == "bad_header":
        return 43, spec_message("curl", "badHeader")
    if k == "too_large":
        return 63, spec_message("curl", "tooLarge")
    if k == "replay":
        return 7, spec_message("curl", "replay", request=f.detail)
    if k == "partial":
        return 18, spec_message("curl", "partialChunked")
    if k == "weird":
        return 8, spec_message("curl", "weird")
    if k == "empty":
        return 52, spec_message("curl", "empty")
    if k == "recv" and f.detail == "noRemoteName":
        return 23, spec_message("curl", "noRemoteName")
    return 56, spec_message("curl", "recv")


def _status_block(r) -> str:
    return f"HTTP/1.1 {r.status} {r.reason}\r\n" + "".join(f"{k}: {v}\r\n" for k, v in r.headers) + "\r\n"


def _urlencode_data(s: str) -> str:
    """--data-urlencode: "name=value" encodes the value, "=value" and "value" encode it all; space is +."""
    def enc(v: str) -> str:
        return "".join(chr(b) if chr(b).isascii() and chr(b).isalnum() or chr(b) in "-._~" else "+" if b == 32 else f"%{b:02X}"
                       for b in bytetext.encode(v))
    eq = s.find("=")
    return enc(s) if eq < 0 else enc(s[1:]) if eq == 0 else s[:eq] + "=" + enc(s[eq + 1:])


def _decompress(data: bytes, gzip: bool, max_bytes: int) -> bytes | None:
    """gzip or zlib-wrapped deflate, at most max_bytes out; None when the data is corrupt."""
    try:
        z = zlib.decompressobj(31 if gzip else 15)
        out = z.decompress(data, max_bytes + 1)
        if len(out) > max_bytes:
            raise WebFailure("too_large")
        return out
    except zlib.error:
        return None


def _write_out(ctx, fmt: str, r, redirects: int, code: int, method: str, forced: str | None, variables: dict) -> str:
    out: list[str] = []
    i = 0
    while i < len(fmt):
        c = fmt[i]
        if c == "\\" and i + 1 < len(fmt) and fmt[i + 1] in "ntr\\":
            i += 1
            out.append({"n": "\n", "t": "\t", "r": "\r"}.get(fmt[i], "\\"))
        elif c != "%" or i + 1 >= len(fmt):
            out.append(c)
        elif fmt[i + 1] == "%":
            out.append("%")
            i += 1
        elif fmt[i + 1] != "{" or fmt.find("}", i) < 0:
            out.append(c)
        else:
            close = fmt.find("}", i)
            name = fmt[i + 2:close]
            i = close
            location = r.header("Location") if r else None
            if name in ("http_code", "response_code"):
                out.append(f"{r.status if r else 0:03d}")
            elif name == "content_type":
                out.append((r.header("Content-Type") if r else None) or "")
            elif name == "size_download":
                out.append(str(len(r.body) if r else 0))
            elif name == "url_effective":
                out.append(str(r.url) if r else "")
            elif name == "redirect_url":
                if r is not None and 300 <= r.status < 400 and location:
                    try:
                        out.append(str(r.url.resolve(location)))
                    except WebFailure:
                        out.append(location)
            elif name == "num_redirects":
                out.append(str(redirects))
            elif name == "method":
                out.append(forced or (r.method if r else method))
            elif name == "exitcode":
                out.append(str(code))
            elif name in variables:
                out.append(variables[name])
            else:
                ctx.stderr.write(spec_message("curl", "unknownWriteOut", name=name) + "\n")
        i += 1
    return "".join(out)


# ---------- wget ----------

_WGET_LONG = {
    "output-document": "O", "quiet": "q", "server-response": "S", "header": "\x01", "post-data": "\x02", "post-file": "\x03",
    "user-agent": "U", "timeout": "T", "max-redirect": "\x04", "no-check-certificate": "\x05", "help": "h", "version": "V",
    "no-verbose": "\x06", "directory-prefix": "P", "no-clobber": "\x07", "spider": "\x08", "tries": "t", "show-progress": "\x09",
}
_WGET_VALUED = "OUTPt\x01\x02\x03\x04"


async def _wget(ctx):
    web = ctx.shell._web or _NO_NETWORK
    spec = COMMANDS["wget"]

    def usage(message: str, code: int = 2) -> int:
        ctx.stderr.write(message + "\n" + spec.try_help)
        return code

    opts: list[tuple[str, str | None]] = []
    urls: list[str] = []
    args = ctx.args
    i = 0
    while i < len(args):
        arg = args[i]
        if arg == "--":
            urls += args[i + 1:]
            break
        if arg.startswith("--"):
            eq = arg.find("=")
            name = arg[2:] if eq < 0 else arg[2:eq]
            key = _WGET_LONG.get(name)
            if key is None:
                return usage(spec_message("wget", "unrecognizedOption", option=arg if eq < 0 else arg[:eq]))
            if key in _WGET_VALUED:
                if eq >= 0:
                    value = arg[eq + 1:]
                else:
                    i += 1
                    value = args[i] if i < len(args) else None
                if value is None:
                    return usage(spec_message("wget", "longRequiresArgument", option=arg))
                opts.append((key, value))
            else:
                opts.append((key, None))
        elif arg.startswith("-") and len(arg) > 1:
            j = 0
            while j + 1 < len(arg):
                j += 1
                c = arg[j]
                # -nv and -nc: wget's two-letter short options
                if c == "n" and j + 1 < len(arg) and arg[j + 1] in "vc":
                    j += 1
                    opts.append(("\x06" if arg[j] == "v" else "\x07", None))
                    continue
                if c not in "OqSUThVPt":
                    return usage(spec_message("wget", "invalidOption", option=c))
                if c in _WGET_VALUED:
                    if j + 1 < len(arg):
                        value = arg[j + 1:]
                    else:
                        i += 1
                        value = args[i] if i < len(args) else None
                    if value is None:
                        return usage(spec_message("wget", "requiresArgument", option=c))
                    opts.append((c, value))
                    break
                opts.append((c, None))
        else:
            urls.append(arg)
        i += 1

    def has(k: str) -> bool:
        return any(o[0] == k for o in opts)

    def last(k: str) -> str | None:
        return next((o[1] for o in reversed(opts) if o[0] == k), None)

    if has("h"):
        ctx.stdout.write(spec.help)
        return 0
    if has("V"):
        ctx.stdout.write(spec.version + "\n")
        return 0
    if not urls:
        return usage(spec_message("wget", "missingUrl"), 1)

    quiet, server, brief, spider, no_clobber = has("q"), has("S"), has("\x06"), has("\x08"), has("\x07")
    prefix = last("P")
    prefix = prefix.rstrip("/") + "/" if prefix else None
    timeout = web.policy.timeout
    t = last("T")
    if t is not None:
        s = _float(t)
        if s is None or s < 0:
            return usage(spec_message("wget", "badNumber", option="--timeout", value=t))
        if s > 0 and _seconds(s) < timeout:
            timeout = _seconds(s)
    max_redirect = 20
    mr = last("\x04")
    if mr is not None:
        n = _int32(mr, False)
        if n is None:
            return usage(spec_message("wget", "badNumber", option="--max-redirect", value=mr))
        max_redirect = n

    headers = [("User-Agent", "Wget/1.21.4"), ("Accept", "*/*"), ("Accept-Encoding", "identity")]
    body = None
    post = last("\x02")
    if post is not None:
        body = bytetext.encode(post)
    post_file = last("\x03")
    if post_file is not None:
        try:
            body = ctx.fs.read_bytes(ctx.resolve(post_file))
        except ShellError as e:
            ctx.stderr.write(spec_message("wget", "readFile", file=post_file, errno=_errno(e)) + "\n")
            return 3
    if body is not None:
        _set_header(headers, "Content-Type", "application/x-www-form-urlencoded")
    ua = last("U")
    if ua is not None:
        _set_header(headers, "User-Agent", ua)
    for key, h in opts:
        if key != "\x01":
            continue
        colon = h.find(":")
        if colon <= 0:
            return usage(spec_message("wget", "invalidHeader", header=h))
        _set_header(headers, h[:colon].strip(), h[colon + 1:].strip())

    # -O FILE is created before anything is fetched, and every body is appended to it
    document = last("O")
    if document is not None and document != "-":
        try:
            ctx.fs.write_bytes(ctx.resolve(document), b"")
        except ShellError as e:
            ctx.stderr.write(spec_message("wget", "writeFailed", file=document, errno=_errno(e)) + "\n")
            return 1

    def now() -> str:
        return utc_now().strftime("%Y-%m-%d %H:%M:%S")

    def say(line: str) -> None:  # the full report
        if not quiet and not brief:
            ctx.stderr.write(line + "\n")

    def note(line: str) -> None:  # also under -nv
        if not quiet:
            ctx.stderr.write(line + "\n")

    def file_for(u) -> str:
        """The file a download is saved to: the URL's last segment (index.html), under -P."""
        name = _remote_name(u)
        return (prefix or "") + (unquote(name) if name else "index.html")

    exit_code = 0
    for raw in urls:
        code = 0
        try:
            url = WebUrl.parse(raw)
        except WebFailure as f:
            ctx.stderr.write((spec_message("wget", "scheme", url=raw, scheme=f.host) if f.kind == "protocol"
                              else spec_message("wget", "badUrl", url=raw)) + "\n")
            exit_code = 1
            continue
        if no_clobber and document is None and not spider and ctx.fs.exists(ctx.resolve(file_for(url))):
            say(spec_message("wget", "clobber", file=file_for(url)))
            say("")
            continue
        if spider:
            say(spec_message("wget", "spiderMode"))
        say(spec_message("wget", "start", time=now(), url=str(url)))
        hop = [0]

        def on_hop(r):
            if hop[0] > 0:
                if spider:
                    say(spec_message("wget", "spiderMode"))
                say(spec_message("wget", "start", time=now(), url=str(r.url)))
            hop[0] += 1
            say(spec_message("wget", "sent", status="" if server else f"{r.status} {r.reason}"))
            if server:
                ctx.stderr.write(f"  HTTP/1.1 {r.status} {r.reason}\n" + "".join(f"  {k}: {v}\n" for k, v in r.headers))
            loc = r.header("Location")
            if r.status in (301, 302, 303, 307, 308) and loc:
                say(spec_message("wget", "following", location=loc))

        try:
            r, _ = await web.fetch(url, "HEAD" if spider else "POST" if body is not None else "GET", None, list(headers), body, True,
                                   max_redirect, timeout, on_hop, ctx.shell._deadline)
            if r.status >= 400:
                if brief:
                    note(spec_message("wget", "nvErrorUrl", url=str(r.url)))
                note(spec_message("wget", "spiderMissing") if spider else spec_message("wget", "httpError", time=now(), status=f"{r.status}: {r.reason}"))
                say("")
                code = 8
            elif spider:
                ctype = r.header("Content-Type")
                say(spec_message("wget", "length", length=(r.header("Content-Length") or spec_message("wget", "lengthUnspecified")) + (f" [{ctype}]" if ctype is not None else "")))
                say(spec_message("wget", "spiderExists"))
                say("")
                if brief:
                    note(spec_message("wget", "nvSpider", time=now(), url=str(r.url), status=f"{r.status} {r.reason}"))
            else:
                length, ctype = r.header("Content-Length"), r.header("Content-Type")
                say(spec_message("wget", "length", length=(length or spec_message("wget", "lengthUnspecified")) + (f" [{ctype}]" if ctype is not None else "")))
                file = document
                if file is None:
                    file = file_for(url)
                    if prefix is not None:
                        try:
                            ctx.fs.mkdir(ctx.resolve(prefix), parents=True)
                        except ShellError as e:
                            ctx.stderr.write(spec_message("wget", "writeFailed", file=file, errno=_errno(e)) + "\n")
                            exit_code = 3
                            continue
                    if ctx.fs.exists(ctx.resolve(file)):
                        n = 1
                        while ctx.fs.exists(ctx.resolve(f"{file}.{n}")):
                            n += 1
                        file = f"{file}.{n}"
                say(spec_message("wget", "saving", file="STDOUT" if file == "-" else file))
                if file == "-":
                    ctx.stdout.write(bytetext.decode(r.body))
                else:
                    try:
                        path = ctx.resolve(file)
                        ctx.fs.write_bytes(path, ctx.fs.read_bytes(path) + r.body if document is not None else r.body)
                    except ShellError as e:
                        ctx.stderr.write(spec_message("wget", "writeFailed", file=file, errno=_errno(e)) + "\n")
                        exit_code = 3
                        continue
                say("")
                size = str(len(r.body)) + (f"/{length}" if length is not None else "")
                if r.error:  # the body ended early: kept as far as it came
                    note(spec_message("wget", "closed", time=now(), size=str(len(r.body))))
                    say("")
                    code = 4
                else:
                    say(spec_message("wget", "written", time=now(), size=size) if file == "-"
                        else spec_message("wget", "saved", time=now(), file=file, size=size))
                    say("")
                if brief and not r.error:
                    note(spec_message("wget", "nvSaved", time=now(), url=str(r.url), size=size, file=file))
                if r.truncated:
                    ctx.stderr.write(spec_message("wget", "tooLarge", limit=str(web.policy.max_response_bytes)) + "\n")
                    code = 1
        except WebFailure as f:
            code, message = _wget_error(f, web.policy.explain_blocked)
            if message and (not quiet or f.blocked is not None and web.policy.explain_blocked or f.kind in ("policy", "replay", "bad_header")):
                ctx.stderr.write(message + "\n")
        if code:
            exit_code = code
    return exit_code


def _wget_error(f: WebFailure, explain: bool) -> tuple[int, str]:
    if f.blocked is not None and (explain or f.kind == "policy"):
        return 4 if f.kind in ("resolve", "connect") else 1, spec_message("wget", "blocked", reason=f.blocked)
    k = f.kind
    if k in ("bad_url", "bad_hostname"):
        return 1, spec_message("wget", "badUrl", url=f.host)
    if k == "protocol":
        return 1, spec_message("wget", "scheme", url=f.host + "://", scheme=f.host)
    if k == "resolve":
        return 4, spec_message("wget", "resolve", host=f.host)
    if k == "connect":
        return 4, spec_message("wget", "connect", host=f.host, port=str(f.port))
    if k == "timeout":
        return 4, spec_message("wget", "timeout")
    if k == "tls":
        return 5, spec_message("wget", "tls", host=f.host)
    if k == "redirects":
        return 8, spec_message("wget", "redirects", max=f.detail)
    if k == "bad_header":
        return 2, spec_message("wget", "invalidHeader", header="")
    if k == "replay":
        return 4, spec_message("wget", "replay", request=f.detail)
    return 4, spec_message("wget", "recv")
