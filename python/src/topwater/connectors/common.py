"""What the connectors share (docs/connectors/README.md): one HTTP client with backoff and bounds for a web API, record
fields shown as front matter (a SharePoint list item, a Notion database row), and names made safe for a file system.
Every port keeps the same pieces with the same rules."""
from __future__ import annotations

import asyncio
import inspect
import json
import re
import time
from typing import Callable

from ..mounts import MountSourceError
from ..webaccess import TransportError, pinned_transport

MAX_RETRIES = 4  # tries after the first, for a rate limit or a server that asks to retry
MAX_RETRY_AFTER = 60.0  # seconds; a longer Retry-After fails the request instead of holding the call
MAX_RESPONSE = 32_000_000  # bytes of one JSON response
SLASH = "∕"  # DIVISION SLASH stands for "/" in a name made from a title
NAME_BYTES = 200  # a name made from a title is cut to this, leaving room for " (2)" and an extension

# What the shared pieces say; {service} is the connector's name for its service ("Notion", "Microsoft Graph"). Each
# connector's own messages stay in its MESSAGES table; this one becomes spec/connectors/common.json later.
MESSAGES = {
    "noAnswer": "{service} did not answer",
    "noAnswerWrite": "{service} did not answer; the change may or may not have been made",
    "busy": "{service} is busy (HTTP {status}); tried {tries} times",
    "waitTooLong": "{service} asked to wait {seconds} seconds (HTTP {status})",
    "responseTooLarge": "{service} sent a response too large to use",
    "notJson": "{service} sent a response that isn't a JSON object (HTTP {status})",
    "frontMatterOpen": "{path}: front matter: no closing ---",
    "frontMatterLine": "{path}: front matter: one `name: value` per line",
}


def msg(key: str, **values) -> str:
    return MESSAGES[key].format(**{k: str(v) for k, v in values.items()})


# ---------- requests ----------

class ApiError(Exception):
    """An answer with an error status: status, the service's error code and message (both cut short)."""

    def __init__(self, status: int, code: str, message: str):
        super().__init__(f"HTTP {status} {code}: {message}")
        self.status, self.code, self.message = status, code, message


def _error_fields(answer) -> tuple[str, str]:
    """(code, message) from {"error": {"code", "message"}} (Microsoft Graph) or {"code", "message"} (Notion)."""
    e = answer.get("error") if isinstance(answer, dict) else None
    e = e if isinstance(e, dict) else answer if isinstance(answer, dict) else {}
    code, message = e.get("code"), e.get("message")
    return (code[:100] if isinstance(code, str) else ""), (message[:1000] if isinstance(message, str) else "")


class Api:
    """Sends requests to one service. token: a string, or a function (may be async) asked before every request, so a
    host that refreshes tokens returns the current one. transport: webaccess's Transport (default: the network through
    pinned_transport, which never follows redirects and refuses internal addresses). rate: requests a second (None: no
    spacing). A 429 or 503 is retried for any method (the service did nothing), another 5xx and no answer only for GET;
    Retry-After is obeyed by every request, up to MAX_RETRY_AFTER."""

    def __init__(self, service: str, token: str | Callable, transport=None, *, timeout: float = 30.0, rate: float | None = None):
        self.service, self._token = service, token
        self._transport = transport or pinned_transport
        self._timeout, self._interval = timeout, (1.0 / rate if rate else 0.0)
        self._lock: tuple | None = None  # (event loop, lock): a lock belongs to one loop
        self._next = 0.0

    async def token(self) -> str:
        t = self._token
        if not callable(t):
            return str(t)
        v = t() if inspect.iscoroutinefunction(t) else await asyncio.to_thread(t)
        if inspect.isawaitable(v):
            v = await v
        return str(v)

    async def send(self, method: str, url: str, body: bytes | None = None, headers: list | None = None, *, auth: bool = True,
                   limit: int = MAX_RESPONSE) -> tuple[int, list, bytes]:
        """One request: (status, headers, body). auth False sends no Authorization header (a pre-signed URL)."""
        loop = asyncio.get_running_loop()
        if self._lock is None or self._lock[0] is not loop:
            self._lock = (loop, asyncio.Lock())
        for attempt in range(MAX_RETRIES + 1):
            async with self._lock[1]:
                wait = self._next - time.monotonic()
                if wait > 0:
                    await asyncio.sleep(wait)
                self._next = max(self._next, time.monotonic()) + self._interval
            hs = list(headers or [])
            if auth:
                hs.append(("Authorization", "Bearer " + await self.token()))
            last = attempt == MAX_RETRIES
            try:
                status, _, rh, raw = await asyncio.to_thread(self._transport, method, url, hs, body, self._timeout, limit)
            except (TransportError, TimeoutError, OSError):
                if method == "GET" and not last:
                    await asyncio.sleep(2 ** attempt)
                    continue
                raise MountSourceError(msg("noAnswer" if method == "GET" else "noAnswerWrite", service=self.service)) from None
            if status == 429 or status == 503 or (status >= 500 and method == "GET"):
                if last:
                    raise MountSourceError(msg("busy", service=self.service, status=status, tries=MAX_RETRIES + 1))
                after = retry_after(rh)
                if after is not None and after > MAX_RETRY_AFTER:
                    raise MountSourceError(msg("waitTooLong", service=self.service, seconds=int(after), status=status))
                pause = after if after is not None else 2.0 ** attempt
                self._next = max(self._next, time.monotonic() + pause)  # every request waits, not only this one
                await asyncio.sleep(pause)
                continue
            return status, rh, raw[:limit + 1]
        raise AssertionError("not reached")

    async def json(self, method: str, url: str, body: dict | None = None, headers: list | None = None) -> dict:
        """A JSON request whose answer must be a JSON object; an error status raises ApiError."""
        hs = [("Accept", "application/json"), *(headers or [])]
        if body is not None:
            hs.append(("Content-Type", "application/json"))
        data = json.dumps(body).encode() if body is not None else b"" if method in ("POST", "PUT", "PATCH") else None  # 411 without a length
        status, _, raw = await self.send(method, url, data, hs)
        if len(raw) > MAX_RESPONSE:
            raise MountSourceError(msg("responseTooLarge", service=self.service))
        if status >= 400:
            try:
                answer = json.loads(raw) if raw else {}
            except ValueError:
                answer = {}
            raise ApiError(status, *_error_fields(answer))
        if status == 204 or not raw:
            return {}
        try:
            answer = json.loads(raw)
        except ValueError:
            answer = None
        if not isinstance(answer, dict):
            raise MountSourceError(msg("notJson", service=self.service, status=status))
        return answer


def retry_after(headers) -> float | None:
    for k, v in headers or ():
        if str(k).lower() == "retry-after":
            try:
                return max(float(v), 0.0)
            except ValueError:
                return None
    return None


def header(headers, name: str) -> str | None:
    return next((str(v) for k, v in headers or () if str(k).lower() == name.lower()), None)


# ---------- front matter ----------

_PLAIN_KEY = re.compile(r"[A-Za-z0-9_][A-Za-z0-9_ .()/-]*")


def front_matter(values: dict, comment: str | None = None) -> str:
    """Fields as front matter: `---`, one `name: JSON value` a line in name order, an optional `# comment` line, `---`.
    A name that isn't plain is written as a JSON string."""
    lines = []
    for name in sorted(values):
        key = name if _PLAIN_KEY.fullmatch(name) and not name.endswith(" ") else json.dumps(name, ensure_ascii=False)
        lines.append(f"{key}: {json.dumps(values[name], ensure_ascii=False)}\n")
    if comment:
        lines.append(f"# {comment}\n")
    return "---\n" + "".join(lines) + "---\n"


def split_front_matter(path: str, text: str) -> tuple[dict | None, str]:
    """(the front matter's values, or None without one; the text after it). Values are JSON, or the YAML an agent may
    write instead (plain text, [a, b], true, ~); `#` lines are comments; `name:` followed by `- item` lines is a list."""
    if not text.startswith("---\n"):
        return None, text
    end = text.find("\n---", 3)
    while end != -1 and text[end + 4:end + 5] not in ("\n", ""):
        end = text.find("\n---", end + 4)
    if end == -1:
        raise MountSourceError(msg("frontMatterOpen", path=path))
    out: dict = {}
    lines = text[4:end].split("\n")
    i = 0
    while i < len(lines):
        ln = lines[i]
        i += 1
        if not ln.strip() or ln.lstrip().startswith("#"):
            continue
        key, rest = None, ""
        if ln.startswith('"'):
            try:
                key, k = json.JSONDecoder().raw_decode(ln)
                rest = ln[k:]
            except ValueError:
                pass
        elif not ln[:1].isspace():
            key, sep, rest = ln.partition(":")
            rest = sep + rest
        if not isinstance(key, str) or not rest.startswith(":"):
            raise MountSourceError(msg("frontMatterLine", path=path))
        raw = rest[1:].strip()
        if raw:
            out[key.strip()] = scalar(raw)
            continue
        items = []
        while i < len(lines) and lines[i].lstrip().startswith("- "):
            items.append(scalar(lines[i].lstrip()[2:].strip()))
            i += 1
        out[key.strip()] = items or None
    return out, text[end + 4:].lstrip("\n")


def scalar(raw: str):
    try:
        return json.loads(raw)
    except ValueError:
        pass
    if raw in ("~", "Null", "NULL"):
        return None
    if raw in ("True", "TRUE", "yes", "Yes"):
        return True
    if raw in ("False", "FALSE", "no", "No"):
        return False
    if raw.startswith("[") and raw.endswith("]"):
        return [x.strip().strip("'\"") for x in raw[1:-1].split(",") if x.strip()]
    if len(raw) >= 2 and raw[0] == raw[-1] == "'":
        return raw[1:-1].replace("''", "'")
    return raw


# ---------- names ----------

def safe_name(title: str, fallback: str) -> str:
    """A title as one file name (the Notion connector's rule): control characters become spaces, "/" becomes SLASH, spaces
    at both ends go, "." and ".." become "(.)" and "(..)", cut to NAME_BYTES of UTF-8; fallback when nothing is left."""
    s = "".join(" " if c < " " or c == "\x7f" else c for c in (title or "")).replace("/", SLASH).strip() or fallback
    if s in (".", ".."):
        s = f"({s})"
    b = s.encode("utf-8", "replace")
    return b[:NAME_BYTES].decode("utf-8", "ignore").rstrip() if len(b) > NAME_BYTES else s


def unique(names: list[str]) -> list[str]:
    """The names, made unique in order: a later duplicate becomes "name (2)" (before an extension: "a (2).md")."""
    seen: set[str] = set()
    out = []
    for n in names:
        stem, dot, ext = n.rpartition(".") if n.endswith(".md") else (n, "", "")
        cand, k = n, 2
        while cand in seen:
            cand = f"{stem} ({k}){dot}{ext}"
            k += 1
        seen.add(cand)
        out.append(cand)
    return out
