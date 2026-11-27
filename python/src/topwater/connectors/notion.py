"""A Notion page tree as a mount source (docs/connectors/notion.md). Every port follows the same design, written down in
docs/connectors/notion-design.md: the file view, the requests, the checks and the messages.

    shell.mount("/mnt/notion", NotionSource(token=lambda: my_token(), root="425832d4-414a-8087-b9a1-f1cb08ff38b1"), "staged")

A page is `Title.md`; a page with subpages or databases is a folder `Title/` whose own text is `Title/index.md`. A
database is a folder of rows, each `Row.md` with its properties as front matter. Reading uses Notion-flavored Markdown
(markdown_version v2). The token never reaches the agent: it is asked from `token` for every request, so a host that
refreshes OAuth tokens returns the current one there.
"""
from __future__ import annotations

import asyncio
import hashlib
import inspect
import json
import re
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Callable
from urllib.parse import urlencode

from ..commands import spec_format
from ..mounts import CAPABILITIES, MountConflictError, MountEntry, MountSource, MountSourceError
from ..webaccess import TransportError, pinned_transport

NOTION_VERSION = "2026-03-11"
API_URL = "https://api.notion.com"
RATE = 3.0  # requests a second, Notion's documented average
MAX_RETRIES = 4  # tries after the first, for a rate limit or an answer Notion asks to retry
MAX_RETRY_AFTER = 60.0  # seconds; a longer Retry-After fails the request
MAX_RESPONSE = 32_000_000  # bytes of one response
MAX_PAGE_BYTES = 1_000_000  # bytes of text one write may send
MAX_LIST_REQUESTS = 50  # requests one listing of a page's children or a database's rows may take (5,000 blocks or rows)
NAME_BYTES = 200  # a title longer than this is cut, leaving room for " (2)" and ".md"
PREFETCH_SECONDS = 60.0  # a page's children, fetched to see whether it is a folder, serve its own listing this long
INDEX = "index.md"
END = ("", 0)  # where a tag goes back when its page had no text: after the new text
SLASH = "∕"  # DIVISION SLASH stands for "/" in a name
SWAP = re.compile(r"\.topwater-swap-\d+$")  # the framework's temporary names (docs/mounts.md, change sets)
# a page's own child pages and databases: tags at the start of a line; nested ones (in a toggle, a column) are indented
_CHILD_TAG = re.compile(r'^<(page|database)\s[^>\n]*?url="[^"]*"[^>\n]*>.*?</\1>', re.S | re.M)
_ANY_TAG = re.compile(r'<(page|database)\b[^>]*>.*?</\1>', re.S)
_HEX = re.compile(r"[0-9a-f]+")
EDITABLE_TYPES = frozenset({"rich_text", "number", "select", "status", "multi_select", "checkbox", "date", "url", "email", "phone_number"})

# What the agent (or the host) is told, by key; a later spec/connectors/notion.json holds this table for every port.
MESSAGES = {
    # requests
    "noAnswer": "Notion did not answer",
    "noAnswerWrite": "Notion did not answer; the change may or may not have been made",
    "responseTooLarge": "Notion sent a response too large to use",
    "busy": "Notion is busy (HTTP {status}); tried {tries} times",
    "waitTooLong": "Notion asked to wait {seconds} seconds (HTTP {status})",
    "notJson": "Notion sent a response that isn't a JSON object (HTTP {status})",
    "noMarkdown": "Notion sent a page without its Markdown",
    "noId": "Notion sent a page without an id",
    "noDataSource": "a database's data source is no longer in Notion",
    "noRoot": "the root page is not in Notion, or not shared with the integration",
    "rootGone": "the root page is no longer in Notion",
    "unauthorized": "Notion refused the integration's token (HTTP 401)",
    "unreachable": "{path}: the integration can't reach this page (share it with the integration)",
    "refused": "{path}: Notion refused the change: {message}",
    # refusals
    "isDirectory": "{path}: Is a directory",
    "notMarkdown": "{path}: only .md files can be Notion pages",
    "notText": "{path}: Notion pages hold UTF-8 text",
    "writeTooLarge": "{path}: too large for one Notion page (over {limit} bytes)",
    "readTooLarge": "{path}: too large",
    "tags": "{path}: subpages and databases are folders here; a page's text can't add or change <page> or <database> tags",
    "databaseMoved": "{path}: Notion can't move databases; only rename one where it is",
    "indexMoved": "{path}: index.md is the page's own text; move or rename its folder instead",
    "mkdirInDatabase": "{path}: a database holds rows: write NAME.md files in it",
    "folderToRow": "{path}: a page with subpages can't become a database row",
    "severalSources": "{path}: this database has several data sources; use their folders",
    "sourceFolder": "{path}: the folder of a data source can't be changed here",
    "truncated": "{path}: the page is too long to edit as Markdown",
    "goneAfterChange": "{path}: no longer in Notion after the change",
    "frontMatterOpen": "{path}: front matter: no closing ---",
    "frontMatterLine": "{path}: front matter: one `name: value` per line",
    "noProperty": "{path}: no property {name} in this database",
    "computed": "{path}: {name}: can't be changed here ({type} is set by Notion)",
    "expected": "{path}: {name}: expected {what}",
    # conflicts
    "exists": "{path}: already exists",
    "gone": "{path}: no longer in Notion",
    "edited": "{path}: edited in Notion since it was listed",
    "changedSinceRead": "{path}: changed in Notion since it was read",
}


def _msg(key: str, **values) -> str:
    return spec_format(MESSAGES[key], **{k: str(v) for k, v in values.items()})


class _Missing(MountSourceError):
    """A path that names nothing in Notion."""


class _ApiError(Exception):
    def __init__(self, status: int, code: str, message: str):
        super().__init__(f"HTTP {status} {code}: {message}")
        self.status, self.code, self.message = status, code, message


@dataclass
class _Item:
    """What a name in a listed folder stands for. kind: page, row, database, source (one data source of a database with
    several) or index (a folder page's own text)."""
    kind: str
    id: str
    title: str
    is_dir: bool
    version: str = ""
    modified: datetime | None = None
    created: str = ""
    source: str = ""  # a database with one data source: its id


@dataclass
class _Folder:
    item: _Item  # the page, database or data source the folder is
    names: dict = field(default_factory=dict)  # name -> _Item
    sticky: dict = field(default_factory=dict)  # id -> (title, name): a name given before stays while the title does


class NotionSource(MountSource):
    """A Notion page (or database) and everything below it, read-write. token: the integration's token, or a function
    (may be async) called for every request that returns it. root: the id or URL of the top page or database, shared with
    the integration. id: your name for it in snapshots (default: the root's id). transport: sends one request
    (webaccess's Transport; default: the network through webaccess.pinned_transport); tests pass a fake. rate: requests
    a second."""
    type = "notion"

    def __init__(self, token: str | Callable, root: str, id: str | None = None, *, transport=None, rate: float = RATE,
                 timeout: float = 30.0, api_url: str = API_URL):
        self._root = _parse_id(root)
        if self._root is None:
            raise ValueError("root: a Notion page or database id or URL")
        if not api_url.startswith("https://") and transport is None:
            raise ValueError("api_url: an https URL")
        self.id = id if id is not None else self._root
        self.capabilities = CAPABILITIES
        self._token, self._transport = token, transport or pinned_transport
        self._interval, self._timeout, self._api = 1.0 / rate, timeout, api_url.rstrip("/")
        self._lock: tuple | None = None  # (event loop, lock): a lock belongs to one loop
        self._next = 0.0
        self._folders: dict[str, _Folder] = {}  # source path -> folder, as listed
        self._prefetched: dict[str, tuple[float, list]] = {}  # page id -> (when, its child page and database blocks)
        self._seen: dict[str, bytes] = {}  # page id -> hash of the file the agent read
        self._ours: dict[str, dict[str, str]] = {}  # page id -> {version our rename made: the version before it}
        self._schemas: dict[str, dict] = {}  # data source id -> its properties

    # ----- requests -----

    async def _request(self, method: str, path: str, body: dict | None = None, query: dict | None = None) -> dict:
        """One API call, rate limited, retried on 429 (any method) and on 5xx or no answer (GET only, which repeats safely;
        503 for any method, which Notion sends before doing anything)."""
        loop = asyncio.get_running_loop()
        if self._lock is None or self._lock[0] is not loop:
            self._lock = (loop, asyncio.Lock())
        url = self._api + path + ("?" + urlencode(query) if query else "")
        data = None if body is None else json.dumps(body).encode()
        for attempt in range(MAX_RETRIES + 1):
            async with self._lock[1]:
                wait = self._next - time.monotonic()
                if wait > 0:
                    await asyncio.sleep(wait)
                self._next = max(self._next, time.monotonic()) + self._interval
            token = self._token() if callable(self._token) else self._token
            if inspect.isawaitable(token):
                token = await token
            headers = [("Authorization", f"Bearer {token}"), ("Notion-Version", NOTION_VERSION), ("Accept", "application/json")]
            if data is not None:
                headers.append(("Content-Type", "application/json"))
            last = attempt == MAX_RETRIES
            try:
                status, _, rh, raw = await asyncio.to_thread(self._transport, method, url, headers, data, self._timeout, MAX_RESPONSE)
            except (TransportError, TimeoutError, OSError):
                if method == "GET" and not last:
                    await asyncio.sleep(2 ** attempt)
                    continue
                raise MountSourceError(_msg("noAnswer" if method == "GET" else "noAnswerWrite")) from None
            if len(raw) > MAX_RESPONSE:
                raise MountSourceError(_msg("responseTooLarge"))
            if status == 429 or (status >= 500 and (method == "GET" or status == 503)):
                if last:
                    raise MountSourceError(_msg("busy", status=status, tries=MAX_RETRIES + 1))
                after = _retry_after(rh)
                if after is not None and after > MAX_RETRY_AFTER:
                    raise MountSourceError(_msg("waitTooLong", seconds=int(after), status=status))
                pause = after if after is not None else 2.0 ** attempt
                self._next = max(self._next, time.monotonic() + pause)  # every request waits, not only this one
                await asyncio.sleep(pause)
                continue
            try:
                answer = json.loads(raw)
            except ValueError:
                answer = None
            if not isinstance(answer, dict):
                raise MountSourceError(_msg("notJson", status=status))
            if status >= 400:
                raise _ApiError(status, _text(answer.get("code"), 100), _text(answer.get("message"), 1000))
            return answer
        raise AssertionError("not reached")

    async def _get(self, path: str, **query) -> dict | None:
        """A GET whose 404 is None."""
        try:
            return await self._request("GET", path, query=query or None)
        except _ApiError as e:
            if e.status == 404:
                return None
            raise

    async def _page(self, pid: str) -> dict | None:
        page = await self._get(f"/v1/pages/{pid}")
        return None if page is None or page.get("in_trash") else page

    async def _markdown(self, pid: str, path: str) -> tuple[str, bool]:
        got = await self._get(f"/v1/pages/{pid}/markdown", markdown_version="v2")
        if got is None:
            raise _Missing(f"{path}: not found in Notion")
        md = got.get("markdown")
        if not isinstance(md, str):
            raise MountSourceError(_msg("noMarkdown"))
        return md, bool(got.get("truncated"))

    async def _children(self, pid: str, fresh: bool = False) -> list:
        """A page's child page and database blocks, in order; unless fresh, those its parent's listing just fetched."""
        hit = self._prefetched.pop(pid, None)
        if hit is not None and not fresh and time.monotonic() - hit[0] < PREFETCH_SECONDS:
            return hit[1]
        out, cursor = [], None
        for _ in range(MAX_LIST_REQUESTS):  # shortcut: a page with more than 5,000 top-level blocks shows the subpages among the first 5,000
            got = await self._get(f"/v1/blocks/{pid}/children", page_size=100, **({"start_cursor": cursor} if cursor else {}))
            if got is None:
                raise _Missing("a page is no longer in Notion")
            out += [b for b in _results(got) if b.get("type") in ("child_page", "child_database") and not b.get("in_trash")]
            cursor = _cursor(got, cursor)
            if cursor is None:
                break
        return out

    async def _rows(self, ds: str, max_entries: int) -> list:
        out, cursor = [], None
        for _ in range(MAX_LIST_REQUESTS):
            got = await self._request("POST", f"/v1/data_sources/{ds}/query", {"page_size": 100} | ({"start_cursor": cursor} if cursor else {}))
            out += [r for r in _results(got) if r.get("object") == "page" and not r.get("in_trash")]
            cursor = _cursor(got, cursor)
            if cursor is None or len(out) >= max_entries:
                break
        return out

    async def _schema(self, ds: str) -> dict:
        if ds not in self._schemas:
            got = await self._get(f"/v1/data_sources/{ds}")
            props = got.get("properties") if got else None
            if not isinstance(props, dict):
                raise MountSourceError(_msg("noDataSource"))
            self._schemas[ds] = props
        return self._schemas[ds]

    # ----- the folders -----

    async def _folder(self, path: str) -> _Folder:
        """The folder at a source path, listing it (and the folders on the way) when it hasn't been."""
        f = self._folders.get(path)
        if f is not None:
            return f
        if path == "/":
            f = _Folder(await self._root_item())
        else:
            parent, name = path.rsplit("/", 1)
            item = (await self._folder(parent or "/")).names.get(name)
            if item is None or not item.is_dir:
                raise _Missing(f"{path}: not found in Notion")
            f = _Folder(item)
        await self._fill(f, path, 100_000)
        self._folders[path] = f
        return f

    async def _root_item(self) -> _Item:
        try:
            page = await self._page(self._root)
            if page is not None:
                return _Item("page", self._root, _title(page), True, _stamp(page), _when(page), _text(page.get("created_time"), 64))
            db = await self._get(f"/v1/databases/{self._root}")
        except _ApiError as e:
            raise MountSourceError(_refusal("/", e)) from None
        if db is None or db.get("in_trash"):
            raise MountSourceError(_msg("noRoot"))
        return _Item("database", self._root, _plain(db.get("title")), True, "", _when(db))

    async def _fill(self, f: _Folder, path: str, max_entries: int) -> None:
        """Lists the folder again: its names, sticky for the ids that keep their title."""
        it = f.item
        items: list[_Item] = []
        if it.kind == "page":
            if path == "/":
                page = await self._page(it.id)
                if page is None:
                    raise MountSourceError(_msg("rootGone"))
                it.version, it.modified = _stamp(page), _when(page)
            blocks = await self._children(it.id)
            await asyncio.gather(*(self._lookahead(b) for b in blocks if b.get("type") == "child_page" and b.get("has_children")))
            for b in blocks:
                bid = _parse_id(b.get("id"))
                if bid is None:
                    continue
                kind = "page" if b["type"] == "child_page" else "database"
                title = _text((b.get(b["type"]) or {}).get("title"), 2000)
                is_dir = kind == "database" or bool(self._prefetched.get(bid, (0, []))[1])
                items.append(_Item(kind, bid, title, is_dir, _stamp(b) if kind == "page" else "", _when(b), _text(b.get("created_time"), 64)))
        elif it.kind == "database":
            db = await self._get(f"/v1/databases/{it.id}")
            if db is None or db.get("in_trash"):
                raise _Missing(f"{path}: not found in Notion")
            sources = [s for s in (db.get("data_sources") or []) if isinstance(s, dict) and _parse_id(s.get("id"))]
            if len(sources) == 1:
                it.source = _parse_id(sources[0]["id"])
                items = await self._row_items(it.source, max_entries)
            else:
                it.source = ""
                items = [_Item("source", _parse_id(s["id"]), _text(s.get("name"), 2000), True) for s in sources]
        elif it.kind == "source":
            items = await self._row_items(it.id, max_entries)
        for i in items:
            self._ours.pop(i.id, None)  # listed again: the version Notion gives is the one the mount now has
        f.names = _names(items, f.sticky, reserve=INDEX if it.kind == "page" else None)
        if it.kind == "page":
            f.names = {INDEX: _Item("index", it.id, it.title, False, it.version, it.modified)} | f.names
        f.sticky = {i.id: (i.title, n) for n, i in f.names.items() if i.kind != "index"}
        for n, i in f.names.items():  # a folder listed before now stands for the item under its name, or goes
            sub = self._folders.get(_join_path(path, n))
            if sub is not None and sub.item.id == i.id and i.is_dir:
                i.source = i.source or sub.item.source
                sub.item = i
            elif sub is not None:
                self._forget(_join_path(path, n))

    async def _lookahead(self, block: dict) -> None:
        """Fetches a child page's own children, to show it as a folder or a file; its listing then uses them."""
        bid = _parse_id(block.get("id"))
        if bid is not None:
            self._prefetched[bid] = (time.monotonic(), await self._children(bid, fresh=True))

    async def _row_items(self, ds: str, max_entries: int) -> list:
        return [_Item("row", rid, _title(r), False, _stamp(r), _when(r), _text(r.get("created_time"), 64))
                for r in await self._rows(ds, max_entries) if (rid := _parse_id(r.get("id")))]

    async def _find(self, path: str) -> tuple[_Folder, str, _Item | None]:
        parent, name = path.rsplit("/", 1)
        f = await self._folder(parent or "/")
        return f, name, f.names.get(name)

    def _parent_ref(self, f: _Folder, path: str) -> tuple[dict, str | None]:
        """The `parent` a page in folder f gets, and the data source when that is a database's."""
        it = f.item
        if it.kind == "page":
            return {"type": "page_id", "page_id": it.id}, None
        if it.kind == "database" and not it.source:
            raise MountSourceError(_msg("severalSources", path=path))
        ds = it.id if it.kind == "source" else it.source
        return {"type": "data_source_id", "data_source_id": ds}, ds

    def _version(self, pid: str, page: dict) -> str:
        v = _stamp(page)
        return self._ours.get(pid, {}).get(v, v)

    def _moved(self, old: str, new: str) -> None:
        for p in [p for p in self._folders if p == old or p.startswith(old + "/")]:
            self._folders[new + p[len(old):]] = self._folders.pop(p)

    def _forget(self, path: str) -> None:
        for p in [p for p in self._folders if p == path or p.startswith(path + "/")]:
            del self._folders[p]

    # ----- the source -----

    async def list(self, path: str, max_entries: int) -> list[MountEntry]:
        try:
            f = self._folders.get(path)
            if f is None:
                f = await self._folder(path)
            else:
                await self._fill(f, path, max_entries)
        except _ApiError as e:
            raise MountSourceError(_refusal(path, e)) from None
        return [MountEntry(n, i.is_dir, None, i.modified, i.version) for n, i in list(f.names.items())[:max_entries]]

    async def stat(self, path: str) -> MountEntry | None:
        if path == "/":
            return MountEntry("", True)
        try:
            f, name, it = await self._find(path)
            if it is None:
                return None
            if it.kind == "source":
                return MountEntry(name, True)
            if it.kind == "database":
                db = await self._get(f"/v1/databases/{it.id}")
                return None if db is None or db.get("in_trash") else MountEntry(name, True, None, _when(db))
            page = await self._page(it.id)
        except _Missing:
            return None
        except _ApiError as e:
            raise MountSourceError(_refusal(path, e)) from None
        if page is None:
            return None
        if it.kind != "index" and (_title(page) != it.title or not _under(page, f.item)):
            return None  # renamed or moved in Notion: the name no longer stands for it
        it.version, it.modified = self._version(it.id, page), _when(page)
        return MountEntry(name, it.is_dir, None, it.modified, it.version)

    async def read(self, path: str, max_bytes: int) -> bytes:
        try:
            _, _, it = await self._find(path)
            if it is None or it.is_dir:
                raise _Missing(f"{path}: not found in Notion")
            data = (await self._view(it, path))[0]
        except _ApiError as e:
            raise MountSourceError(_refusal(path, e)) from None
        if len(data) > max_bytes:
            raise MountSourceError(_msg("readTooLarge", path=path))
        self._seen[it.id] = hashlib.sha256(data).digest()
        return data

    async def _view(self, it: _Item, path: str, page: dict | None = None) -> tuple[bytes, str, bool]:
        """The file the agent sees, the page's Markdown and whether Notion cut it short."""
        md, cut = await self._markdown(it.id, path)
        if it.kind == "row" and page is None:
            page = await self._page(it.id)
            if page is None:
                raise _Missing(f"{path}: not found in Notion")
        return _render(it, md, page), md, cut

    async def write(self, path: str, data: bytes, expected_version: str | None) -> str:
        self._prefetched.clear()  # what a listing fetched ahead may be out of date after a change
        try:
            f, name, it = await self._find(path)
            if it is not None and it.is_dir:
                raise MountSourceError(_msg("isDirectory", path=path))
            text = _decode(path, data)
            if it is None and name == INDEX and f.item.kind == "page":  # the text of a folder made with mkdir
                it = _Item("index", f.item.id, f.item.title, False, f.item.version, f.item.modified)
                version = await self._update(it, path, text, None)
                f.names[INDEX] = it
                return version
            if expected_version is None:
                if it is not None:
                    raise MountConflictError(_msg("exists", path=path))
                return await self._create(f, path, name, text)
            if it is None:
                raise MountConflictError(_msg("gone", path=path))
            return await self._update(it, path, text, expected_version)
        except _ApiError as e:
            raise MountSourceError(_refusal(path, e)) from None

    async def _create(self, f: _Folder, path: str, name: str, text: str) -> str:
        if not name.endswith(".md") or name == ".md" or SWAP.search(name):
            raise MountSourceError(_msg("notMarkdown", path=path))
        parent, ds = self._parent_ref(f, path)
        title = _unname(name[:-3])
        props: dict = {}
        body, key = text, "title"
        if ds is not None:
            fm, body = _split_front(path, text)
            schema = await self._schema(ds)
            props = _props_payload(path, fm, schema, {})
            key = next((k for k, v in schema.items() if isinstance(v, dict) and v.get("type") == "title"), "title")
        _check_tags(path, body, "")
        props[key] = {"title": _rich(title)}
        body = body.strip("\n")
        first = next((ln for ln in body.split("\n") if ln.strip()), "")
        inline = bool(body) and not first.startswith("# ")  # Notion takes a first "# " heading as the title and drops it
        page = await self._request("POST", "/v1/pages", {"parent": parent, "properties": props}
                                   | ({"markdown": body, "markdown_version": "v2"} if inline else {}))
        pid = _parse_id(page.get("id"))
        if pid is None:
            raise MountSourceError(_msg("noId"))
        if body and not inline:
            await self._replace(pid, body)
            page = await self._page(pid) or page
        it = _Item("row" if ds else "page", pid, title, False, _stamp(page), _when(page), _text(page.get("created_time"), 64))
        f.names[name] = it
        f.sticky[pid] = (title, name)
        return it.version

    async def _update(self, it: _Item, path: str, text: str, expected: str | None) -> str:
        """Writes a page's text (and a row's changed properties). expected None: the text of a page made with mkdir, which
        must still be empty."""
        page = await self._page(it.id)
        if page is None:
            raise MountConflictError(_msg("gone", path=path))
        if expected is not None and self._version(it.id, page) != expected:
            raise MountConflictError(_msg("edited", path=path))
        view, md, cut = await self._view(it, path, page)
        seen = self._seen.get(it.id)
        if (seen is not None and seen != hashlib.sha256(view).digest()) or (expected is None and view):
            raise MountConflictError(_msg("changedSinceRead", path=path))
        if cut:
            raise MountSourceError(_msg("truncated", path=path))
        if it.kind == "row":
            fm, body = _split_front(path, text)
            ds = _parse_id((page.get("parent") or {}).get("data_source_id"))
            changes = _props_payload(path, fm, await self._schema(ds) if ds else {}, page.get("properties") or {})
            _check_tags(path, body, md)
            new_md = body.strip("\n")
            if changes:
                await self._request("PATCH", f"/v1/pages/{it.id}", {"properties": changes})
        else:
            own, tags = _split(md)
            _check_tags(path, text, own)
            new_md = _join(text.strip("\n"), tags)
        if new_md != md:
            got = await self._replace(it.id, new_md)
            md = got if isinstance(got, str) else new_md
        page = await self._page(it.id)
        if page is None:
            raise MountSourceError(_msg("goneAfterChange", path=path))
        self._ours.pop(it.id, None)
        it.version, it.modified = _stamp(page), _when(page)
        self._seen[it.id] = hashlib.sha256(_render(it, md, page)).digest()
        return it.version

    async def _replace(self, pid: str, md: str):
        """Replaces a page's text; returns the Markdown Notion made of it. Notion refuses to drop a child page or database
        unless asked (allow_deleting_content, never sent), so a mistake here fails instead of deleting a subpage."""
        got = await self._request("PATCH", f"/v1/pages/{pid}/markdown",
                                  {"type": "replace_content", "replace_content": {"new_str": md}, "markdown_version": "v2"})
        return got.get("markdown")

    async def delete(self, path: str, expected_version: str | None) -> None:
        self._prefetched.clear()
        try:
            f, name, it = await self._find(path)
            if it is None:
                raise MountConflictError(_msg("gone", path=path))
            if it.kind == "index":
                return  # the page's own text goes with its folder; deleting it alone changes nothing (docs/connectors/notion.md)
            if it.kind == "source":
                raise MountSourceError(_msg("sourceFolder", path=path))
            if it.kind == "database":
                await self._request("PATCH", f"/v1/databases/{it.id}", {"in_trash": True})
            else:
                page = await self._page(it.id)
                if page is None:
                    raise MountConflictError(_msg("gone", path=path))
                if not it.is_dir and self._version(it.id, page) != expected_version:
                    raise MountConflictError(_msg("edited", path=path))
                await self._request("PATCH", f"/v1/pages/{it.id}", {"in_trash": True})
        except _ApiError as e:
            raise MountSourceError(_refusal(path, e)) from None
        del f.names[name]
        f.sticky.pop(it.id, None)
        self._forget(path)

    async def rename(self, path: str, to: str, expected_version: str | None) -> str | None:
        self._prefetched.clear()
        try:
            f, name, it = await self._find(path)
            if it is None:
                raise MountConflictError(_msg("gone", path=path))
            dest, new = to.rsplit("/", 1)
            g = await self._folder(dest or "/")
            if new in g.names:
                raise MountConflictError(_msg("exists", path=to))
            final = SWAP.sub("", new)
            if new == INDEX and g.item.id == it.id and it.kind == "page":  # X.md into the folder `mkdir X` made for it
                del f.names[name]
                g.names[INDEX] = _Item("index", it.id, it.title, False, it.version, it.modified)
                return None
            if it.kind == "index":
                raise MountSourceError(_msg("indexMoved", path=path))
            if it.kind == "source":
                raise MountSourceError(_msg("sourceFolder", path=path))
            if it.kind == "database":
                if g is not f:
                    raise MountSourceError(_msg("databaseMoved", path=path))
                if not _keeps(it.title, final):
                    await self._request("PATCH", f"/v1/databases/{it.id}", {"title": _rich(_unname(final))})
                    it.title = _unname(final)
            else:
                if not it.is_dir and (not final.endswith(".md") or final == ".md"):
                    raise MountSourceError(_msg("notMarkdown", path=path))
                parent, ds = self._parent_ref(g, to)
                if ds is not None and it.is_dir:
                    raise MountSourceError(_msg("folderToRow", path=path))
                page = await self._page(it.id)
                if page is None:
                    raise MountConflictError(_msg("gone", path=path))
                before = self._version(it.id, page)
                if not it.is_dir and before != expected_version:
                    raise MountConflictError(_msg("edited", path=path))
                if g is not f:
                    await self._request("POST", f"/v1/pages/{it.id}/move", {"parent": parent})
                base = final if it.is_dir else final[:-3]
                if not _keeps(it.title, base):
                    await self._request("PATCH", f"/v1/pages/{it.id}", {"properties": {"title": {"title": _rich(_unname(base))}}})
                    it.title = _unname(base)
                    page = await self._page(it.id)
                    if page is not None and _stamp(page) != before:
                        self._ours.setdefault(it.id, {})[_stamp(page)] = before  # our own rename is not an edit by someone else
                it.kind = "row" if ds is not None else "page"
        except _ApiError as e:
            raise MountSourceError(_refusal(path, e)) from None
        del f.names[name]
        f.sticky.pop(it.id, None)
        g.names[new] = it
        g.sticky[it.id] = (it.title, new)
        self._moved(path, to)
        return None

    async def mkdir(self, path: str) -> str | None:
        self._prefetched.clear()
        try:
            f, name, it = await self._find(path)
            if it is not None:
                raise MountConflictError(_msg("exists", path=path))
            if f.item.kind != "page":
                raise MountSourceError(_msg("mkdirInDatabase", path=path))
            leaf = f.names.get(name + ".md")
            if leaf is not None and leaf.kind == "page":  # `mkdir X` beside the page X.md: the folder for X's subpages
                it = f.names[name] = _Item("page", leaf.id, leaf.title, True, leaf.version, leaf.modified, leaf.created)
                self._folders[path] = _Folder(it)
                return None
            title = _unname(SWAP.sub("", name))
            page = await self._request("POST", "/v1/pages", {"parent": self._parent_ref(f, path)[0], "properties": {"title": {"title": _rich(title)}}})
        except _ApiError as e:
            raise MountSourceError(_refusal(path, e)) from None
        pid = _parse_id(page.get("id"))
        if pid is None:
            raise MountSourceError(_msg("noId"))
        it = _Item("page", pid, title, True, _stamp(page), _when(page), _text(page.get("created_time"), 64))
        f.names[name] = it
        f.sticky[pid] = (title, name)
        self._folders[path] = _Folder(it)  # its index.md appears when written, or at the next listing
        return None


# ----- names -----

def _name(title: str) -> str:
    """A title as a name: control characters become spaces, "/" a division slash; empty is "Untitled"; long ones are cut."""
    t = "".join(" " if ord(c) < 32 or ord(c) == 127 else c for c in title).replace("/", SLASH).strip()
    t = t or "Untitled"
    if t in (".", ".."):
        t = f"({t})"
    b = t.encode("utf-8", "replace")
    return b[:NAME_BYTES].decode("utf-8", "ignore").rstrip() if len(b) > NAME_BYTES else t


def _unname(base: str) -> str:
    return base.replace(SLASH, "/")


def _keeps(title: str, base: str) -> bool:
    """Whether a name (without .md) is the one the title already gives, with or without a " (2)" for a duplicate."""
    n = _name(title)
    return base == n or re.fullmatch(re.escape(n) + r" \(\d+\)", base) is not None


def _names(items: list, sticky: dict, reserve: str | None) -> dict:
    """Unique names for a folder's items: `Title.md` for a page file, `Title` for a folder; a duplicate gets " (2)", " (3)"
    in the order Notion created them. A name given before stays while its title does, so a listing never renames."""
    out: dict[str, _Item] = {}
    taken = {reserve} if reserve else set()
    for i in items:  # names this session already gave
        old = sticky.get(i.id)
        if old and old[0] == i.title and old[1] not in taken and old[1].endswith(".md") != i.is_dir:
            out[old[1]] = i
            taken.add(old[1])
    given = {id(i) for i in out.values()}
    late = []
    for i in sorted((i for i in items if id(i) not in given), key=lambda i: (i.created, i.id)):
        n = _name(i.title) + ("" if i.is_dir else ".md")
        if n in taken:
            late.append(i)
        else:
            out[n] = i
            taken.add(n)
    for i in late:
        k = 2
        while (n := f"{_name(i.title)} ({k}){'' if i.is_dir else '.md'}") in taken:
            k += 1
        out[n] = i
        taken.add(n)
    return dict(sorted(out.items()))


# ----- Markdown -----

def _render(it: _Item, md: str, page: dict | None) -> bytes:
    """The file the agent sees: a page's text without its children's tags, or a row's front matter and text."""
    if it.kind == "row":
        return (_front((page or {}).get("properties") or {}) + (md + "\n" if md else "")).encode()
    view = _split(md)[0]
    return (view + "\n" if view else "").encode()


def _split(md: str) -> tuple[str, list]:
    """A page's Markdown without its own child page and database tags (they are folder entries here), and each tag with
    where it was: (the line before it, which occurrence of that line), None at the start, END on a page without text."""
    parts, at, pos = [], [], 0
    for m in _CHILD_TAG.finditer(md):
        parts.append(md[pos:m.start()])
        at.append((len(parts), m.group(0)))
        pos = m.end()
    parts.append(md[pos:])
    chunks = [p.strip("\n") for p in parts]
    view = "\n\n".join(c for c in chunks if c)
    tags = []
    for i, tag in at:
        before = "\n\n".join(c for c in chunks[:i] if c)
        lines = before.split("\n")
        tags.append(((lines[-1], lines.count(lines[-1])) if before else None if view else END, tag))
    return view, tags


def _join(text: str, tags: list) -> str:
    """Puts the tags _split took out back into edited text: after the block ending with the same line when it is still
    there, else at the end."""
    lines = text.split("\n") if text else []
    after: dict[int, list] = {}
    for anchor, tag in tags:
        if anchor is None or anchor == END:
            after.setdefault(-1 if anchor is None else len(lines), []).append(tag)
            continue
        hits = [i for i, ln in enumerate(lines) if ln == anchor[0]]
        at = hits[anchor[1] - 1] if len(hits) >= anchor[1] else len(lines)
        while at < len(lines) - 1 and lines[at + 1] != "":  # after the block that line ends now (text added to it stays in it)
            at += 1
        after.setdefault(at, []).append(tag)
    out: list[str] = []
    gap = False  # a blank line was just added after a tag

    def put(ts: list) -> None:
        nonlocal gap
        for t in ts:
            while out and out[-1] == "":
                out.pop()
            out.extend(([""] if out else []) + [t, ""])
            gap = True
    put(after.get(-1, []))
    for i, ln in enumerate(lines):
        if not (gap and ln == ""):
            out.append(ln)
            gap = False
        put(after.get(i, []))
    put(after.get(len(lines), []))
    return "\n".join(out).strip("\n")


def _check_tags(path: str, text: str, allowed_from: str) -> None:
    """The agent's text may keep the <page> and <database> tags Notion showed it (nested ones, a row's), never add or
    change one: a tag's title renames that page, and a page's children are folders here."""
    allowed = {m.group(0) for m in _ANY_TAG.finditer(allowed_from)}
    if any(m.group(0) not in allowed for m in _ANY_TAG.finditer(text)):
        raise MountSourceError(_msg("tags", path=path))


def _decode(path: str, data: bytes) -> str:
    if len(data) > MAX_PAGE_BYTES:
        raise MountSourceError(_msg("writeTooLarge", path=path, limit=MAX_PAGE_BYTES))
    try:
        text = bytes(data).decode("utf-8")
    except UnicodeDecodeError:
        raise MountSourceError(_msg("notText", path=path)) from None
    if "\0" in text:
        raise MountSourceError(_msg("notText", path=path))
    return text


# ----- properties as front matter -----

def _front(props: dict) -> str:
    """A row's properties (all but the title, which is the file name) as YAML front matter, one `name: JSON value` a line."""
    lines = []
    for name in sorted(props):
        p = props[name]
        if not isinstance(p, dict) or p.get("type") == "title":
            continue
        key = name if re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_ .()/-]*", name) and not name.endswith(" ") else json.dumps(name, ensure_ascii=False)
        lines.append(f"{key}: {json.dumps(_value(p), ensure_ascii=False)}\n")
    return "---\n" + "".join(lines) + "---\n"


def _value(p: dict):
    """A property's value as the front matter shows it."""
    t = p.get("type")
    v = p.get(t)
    if t == "rich_text":
        return _plain(v)
    if t in ("select", "status"):
        return v.get("name") if isinstance(v, dict) else None
    if t in ("multi_select", "files"):
        return [o.get("name") for o in v if isinstance(o, dict)] if isinstance(v, list) else []
    if t == "relation":
        return [o.get("id") for o in v if isinstance(o, dict)] if isinstance(v, list) else []
    if t == "people":
        return [o.get("name") or o.get("id") for o in v if isinstance(o, dict)] if isinstance(v, list) else []
    if t in ("created_by", "last_edited_by"):
        return (v.get("name") or v.get("id")) if isinstance(v, dict) else None
    if t in ("formula", "rollup") and isinstance(v, dict):
        t, v = v.get("type"), v.get(v.get("type"))
    if t == "date":
        if not isinstance(v, dict):
            return None
        return v.get("start") if not v.get("end") and not v.get("time_zone") else {k: v[k] for k in ("start", "end", "time_zone") if v.get(k)}
    if t == "unique_id" and isinstance(v, dict):
        return f"{v['prefix']}-{v.get('number')}" if v.get("prefix") else v.get("number")
    return v if v is None or isinstance(v, (bool, int, float, str)) else None


def _split_front(path: str, text: str) -> tuple[dict | None, str]:
    """(the front matter's values, or None without one; the text after it)."""
    if not text.startswith("---\n"):
        return None, text
    end = text.find("\n---", 3)
    while end != -1 and text[end + 4:end + 5] not in ("\n", ""):
        end = text.find("\n---", end + 4)
    if end == -1:
        raise MountSourceError(_msg("frontMatterOpen", path=path))
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
            raise MountSourceError(_msg("frontMatterLine", path=path))
        raw = rest[1:].strip()
        if raw:
            out[key.strip()] = _scalar(raw)
            continue
        items = []
        while i < len(lines) and lines[i].lstrip().startswith("- "):
            items.append(_scalar(lines[i].lstrip()[2:].strip()))
            i += 1
        out[key.strip()] = items or None
    return out, text[end + 4:].lstrip("\n")


def _scalar(raw: str):
    """A front matter value: JSON, or the YAML an agent may write instead (plain text, [a, b], true, ~)."""
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


def _props_payload(path: str, fm: dict | None, schema: dict, current: dict) -> dict:
    """The properties to send for front matter: those whose value differs from `current` (a new row: all of them). A
    property left out is left as it is."""
    out = {}
    for name, value in (fm or {}).items():
        spec = schema.get(name)
        if not isinstance(spec, dict):
            raise MountSourceError(_msg("noProperty", path=path, name=name))
        t = spec.get("type")
        now = current.get(name)
        if t == "title" or (isinstance(now, dict) and _value(now) == value):
            continue
        if t not in EDITABLE_TYPES:
            raise MountSourceError(_msg("computed", path=path, name=name, type=t))
        out[name] = {t: _prop_value(path, name, t, value)}
    return out


def _prop_value(path: str, name: str, t: str, v):
    def bad(what: str) -> MountSourceError:
        return MountSourceError(_msg("expected", path=path, name=name, what=what))
    if t == "rich_text":
        if v is not None and not isinstance(v, (str, int, float)):
            raise bad("text")
        return _rich("" if v is None else str(v))
    if t == "number":
        if v is not None and (isinstance(v, bool) or not isinstance(v, (int, float))):
            raise bad("a number")
        return v
    if t in ("select", "status"):
        if v is not None and not isinstance(v, str):
            raise bad("an option's name")
        return {"name": v[:100]} if v else None
    if t == "multi_select":
        v = [v] if isinstance(v, str) else [] if v is None else v
        if not isinstance(v, list) or not all(isinstance(x, str) for x in v):
            raise bad("a list of option names")
        return [{"name": x[:100]} for x in v]
    if t == "checkbox":
        if not isinstance(v, bool):
            raise bad("true or false")
        return v
    if t == "date":
        if v is None or isinstance(v, str):
            return None if not v else {"start": v}
        if isinstance(v, dict) and isinstance(v.get("start"), str):
            return {k: v[k] for k in ("start", "end", "time_zone") if v.get(k) is not None}
        raise bad('a date ("2026-10-01") or {"start": ..., "end": ...}')
    if v is not None and not isinstance(v, str):
        raise bad("text")
    return v or None


# ----- small helpers -----

def _parse_id(s) -> str | None:
    """A Notion id from an id or URL (its last 32 hex digits), dashed; None when there is none. Ids in responses go
    through it too, so nothing else reaches a request's path."""
    if not isinstance(s, str) or len(s) > 2000:
        return None
    hits = [r for r in _HEX.findall(s.lower().replace("-", "")) if len(r) >= 32]  # "Cafe-<id>" in a URL runs into the id
    if not hits:
        return None
    h = hits[-1][-32:]
    return f"{h[:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:]}"


def _join_path(d: str, name: str) -> str:
    return "/" + name if d == "/" else d + "/" + name


def _text(v, limit: int) -> str:
    return v[:limit] if isinstance(v, str) else ""


def _plain(rich) -> str:
    return "".join(_text(r.get("plain_text"), 2000) for r in rich if isinstance(r, dict))[:2000] if isinstance(rich, list) else ""


def _rich(s: str) -> list:
    return [{"type": "text", "text": {"content": s[i:i + 2000]}} for i in range(0, len(s), 2000)] or [{"type": "text", "text": {"content": ""}}]


def _title(page: dict) -> str:
    for p in (page.get("properties") or {}).values():
        if isinstance(p, dict) and p.get("type") == "title":
            return _plain(p.get("title"))
    return ""


def _under(page: dict, folder: _Item) -> bool:
    """Whether a page's parent is still the folder it was listed in."""
    p = page.get("parent") or {}
    if folder.kind == "page":
        return _parse_id(p.get("page_id")) == folder.id
    if folder.kind == "source":
        return _parse_id(p.get("data_source_id")) == folder.id
    return _parse_id(p.get("database_id")) == folder.id


def _stamp(obj: dict) -> str:
    return _text(obj.get("last_edited_time"), 64)


def _when(obj: dict) -> datetime | None:
    try:
        return datetime.fromisoformat(_stamp(obj).replace("Z", "+00:00")).astimezone(timezone.utc).replace(tzinfo=None)
    except ValueError:
        return None


def _results(got: dict) -> list:
    r = got.get("results")
    return [x for x in r if isinstance(x, dict)] if isinstance(r, list) else []


def _cursor(got: dict, previous: str | None) -> str | None:
    c = got.get("next_cursor")
    return c if got.get("has_more") is True and isinstance(c, str) and 0 < len(c) <= 1000 and c != previous else None


def _retry_after(headers) -> float | None:
    for k, v in headers or ():
        if str(k).lower() == "retry-after":
            try:
                return max(float(v), 0.0)
            except ValueError:
                return None
    return None


def _refusal(path: str, e: _ApiError) -> str:
    if e.status == 401:
        return _msg("unauthorized")
    if e.status in (403, 404):
        return _msg("unreachable", path=path)
    return _msg("refused", path=path, message=e.message or e.code or str(e.status))
