"""OneDrive and SharePoint through Microsoft Graph as a mount source (docs/connectors/graph.md). Every port follows the
design in docs/connectors/graph-design.md: the file view, the requests, the checks and the messages.

    shell.mount("/mnt/sp", GraphSource(token=my_token, site="https://contoso.sharepoint.com/sites/Team"), "staged")
    shell.mount("/mnt/od", GraphSource(token=my_token, drive="me"), "read-only")

A drive (a OneDrive, or one document library) is its folders and files, Office documents as their real bytes. A site is
a folder of its document libraries, its lists and its subsites, by name; when two of them share a name, the list gets
" (list)" and the subsite " (site)". A list is a folder of items, each `Title.md` holding the item's columns as front
matter. The token never reaches the agent, and is never sent anywhere but the Graph API.
"""
from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Callable
from urllib.parse import quote, unquote, urlsplit

from ..mounts import CAPABILITIES, MountConflictError, MountEntry, MountSource, MountSourceError
from .common import Api, ApiError, front_matter, header, safe_name, split_front_matter, unique

GRAPH = "https://graph.microsoft.com/v1.0"
SIMPLE_UPLOAD = 4 * 1024 * 1024  # bytes a single PUT may send; more goes through an upload session
CHUNK = 10 * 320 * 1024  # an upload session's chunk: a multiple of 320 KiB
PAGE = 200  # entries asked for per page
MAX_LIST_REQUESTS = 50  # pages one listing may take
MAX_SITE_DEPTH = 8  # subsites below the mounted site that are shown
MAX_PATH = 400  # characters in a decoded SharePoint path, the library's own path included
MAX_COPY_BYTES = 250_000_000  # what a move between libraries may copy
MAX_COPY_ENTRIES = 1000
MAX_ITEM_BYTES = 1_000_000  # a list item file
SELECT = "id,name,size,eTag,cTag,lastModifiedDateTime,folder,file"
_ID = re.compile(r"[A-Za-z0-9!_.,:-]{1,400}")
_ILLEGAL = set('"*:<>?/\\|')
_RESERVED = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(10)), *(f"LPT{i}" for i in range(10)), ".LOCK", "DESKTOP.INI"}
# columns every list has that say nothing about the item, or that the file name already shows (Title)
_SKIP = {"Title", "ContentType", "Attachments", "Edit", "LinkTitle", "LinkTitleNoMenu", "DocIcon", "ItemChildCount",
         "FolderChildCount", "AppAuthor", "AppEditor", "ComplianceAssetId"}
_SET_BY_SHAREPOINT = {"ID", "Created", "Modified", "Author", "Editor"}
_TYPES = ("text", "choice", "number", "currency", "boolean", "dateTime", "personOrGroup", "lookup", "calculated",
          "hyperlinkOrPicture", "term", "geolocation", "thumbnail", "contentApprovalStatus")
EDITABLE_TYPES = frozenset({"text", "choice", "number", "currency", "boolean", "dateTime"})

# What the agent sees (the framework puts "mount: <machine path>: " before an apply failure). Every port uses this table
# word for word; it becomes spec/connectors/graph.json later.
MESSAGES = {
    "token": "Microsoft Graph refused the token (HTTP 401)",
    "denied": "{path}: access denied (the app or the signed-in user lacks permission)",
    "missing": "{path}: No such file or directory",
    "goneSinceListed": "{path}: No such file or directory (deleted in SharePoint since it was listed)",
    "changed": "{path}: changed in SharePoint since it was read",
    "changedCode": "{path}: changed in SharePoint since it was read ({code})",
    "deleted": "{path}: deleted in SharePoint since it was read",
    "exists": "{path}: already exists",
    "locked": "{path}: locked in SharePoint (checked out or open for editing)",
    "full": "{path}: the library is out of storage",
    "refused": "{path}: SharePoint refused the change: {reason}",
    "cantOpen": "the {what} can't be opened: {reason}",
    "nextPage": "Microsoft Graph sent a next page on another host; not followed",
    "notJson": "Microsoft Graph sent a response that isn't a JSON object (HTTP {status})",
    "badId": "Microsoft Graph sent an id that can't be used",
    "badName": "{path}: SharePoint doesn't allow this name (no \" * : < > ? \\ |, leading or trailing spaces, trailing dot, "
               "reserved names such as CON or .lock, _vti_, or ~$ at the start)",
    "tooLong": "{path}: the path is too long for SharePoint (over {max} characters with the library's own path)",
    "notDir": "{path}: Not a directory",
    "isDir": "{path}: Is a directory",
    "tooLarge": "{path}: File too large",
    "downloadNotHttps": "{path}: the download link isn't https",
    "downloadFailed": "{path}: the download failed (HTTP {status})",
    "noUploadLink": "{path}: Microsoft Graph sent no usable upload link",
    "uploadEnded": "{path}: the upload ended without the file being saved",
    "filesInLibrary": "{path}: a site holds libraries, lists and subsites; put files in a library",
    "noCreateContainer": "{path}: new libraries, lists and sites aren't created from here; create them in SharePoint",
    "noRenameContainer": "{path}: libraries, lists and sites aren't renamed or moved from here; do it in SharePoint",
    "noDeleteContainer": "{path}: libraries, lists and sites aren't deleted from here; do it in SharePoint",
    "notEmpty": "{path}: the folder isn't empty in SharePoint (someone added to it); not deleted",
    "noTargetFolder": "{path}: the folder it moves to doesn't exist",
    "fileToLibrary": "{path}: files and folders can only move to a library",
    "itemToList": "{path}: a list item can only move to a list",
    "itemsAreMd": "{path}: a list holds items, one .md file each",
    "noFolderInList": "{path}: a list holds items, not folders",
    "itemTooLarge": "{path}: too large for a list item (over {max} bytes)",
    "itemUtf8": "{path}: a list item is UTF-8 text",
    "itemText": "{path}: a list item holds only its columns, as front matter between --- lines",
    "noColumn": "{path}: no column {name} in this list",
    "readOnlyColumn": "{path}: {name} can't be changed ({why})",
    "notEditable": "{path}: {name} can't be changed here",
    "expected": "{path}: {name}: expected {what}",
    "copyTooMany": "{path}: too many entries to move between libraries (over {max}); move it in SharePoint",
    "copyTooLarge": "{path}: too large to move between libraries (over {max} bytes); move it in SharePoint",
    "copiedNotDeleted": "{path}: copied to {to}, but the original could not be deleted: {reason}",
    # not an error: what note(change) tells the host about a move between libraries, lists or sites
    "crossMove": "copy + delete: {path} moves to another library, list or site; the copy gets a new id and loses version "
                 "history and sharing",
}
# the {why} of "readOnlyColumn"
READ_ONLY = {"sharepoint": "set by SharePoint", "type": "a {type} column, not editable here"}
# the {what} of "expected" for each column type
EXPECTED = {
    "text": "text",
    "maxLength": "at most {max} characters",
    "choiceName": "a choice's name",
    "choice": "one of {choices}",
    "number": "a number",
    "boolean": "true or false",
    "dateTime": 'a date ("2026-12-10") or a date and time ("2026-12-10T09:00:00Z")',
}


def _m(key: str, **values: str) -> str:
    return MESSAGES[key].format(**values)


class _Missing(MountSourceError):
    """A path that names nothing."""


@dataclass
class _Container:
    """A name at a site's level: a library (drive), a list or a subsite."""
    kind: str
    id: str
    prefix: str = ""  # a library's server-relative path, for MAX_PATH


@dataclass
class _Loc:
    """What a path names. kind: site, drive (a path in a library), list (the list's folder) or item (a list item file).
    top: the mount path of the library, list or site it is in, which tells whether a move crosses one."""
    kind: str
    top: str
    site: str = ""
    drive: str = ""
    list: str = ""
    rel: str = "/"
    prefix: str = ""


@dataclass
class _Column:
    key: str  # the front matter's name: the column's display name (its internal name when two share one)
    name: str  # the internal name, the key in an item's fields
    type: str
    read_only: str = ""  # why it can't be changed here; "" when it can
    choices: tuple = ()
    free_choice: bool = False
    max_length: int | None = None


@dataclass
class _ListData:
    columns: list = field(default_factory=list)
    names: dict = field(default_factory=dict)  # file name -> item id, as last listed


class GraphSource(MountSource):
    """A OneDrive or SharePoint document library (drive: a drive id, or "me"), or a SharePoint site with its libraries,
    lists and subsites (site: its URL, "host:/sites/path", or its id). token: an access token for Microsoft Graph, or a
    function (may be async) called before every request that returns the current one; signing in is the host's job.
    id: your name for it in snapshots (default: the site's URL or the drive id). transport: sends one request
    (webaccess's Transport; default: the network through webaccess.pinned_transport); tests pass a fake."""
    type = "graph"

    def __init__(self, token: str | Callable, *, site: str | None = None, drive: str | None = None, id: str | None = None,
                 transport=None, timeout: float = 60.0, api_url: str = GRAPH, rate: float | None = None):
        if (site is None) == (drive is None):
            raise ValueError("give one of site and drive")
        if not api_url.startswith("https://"):
            raise ValueError("api_url: an https URL")
        self._site_ref, self._drive_ref = site, drive
        self.id = id if id is not None else (site or drive or "")
        self.capabilities = CAPABILITIES
        self._api_url = api_url.rstrip("/")
        self._api = Api("Microsoft Graph", token, transport, timeout=timeout, rate=rate)
        self._root: _Loc | None = None
        self._containers: dict[str, dict[str, _Container]] = {}  # a site's mount path -> its names
        self._lists: dict[str, _ListData] = {}  # list id -> columns and item names

    # ----- requests -----

    async def _call(self, method: str, url: str, body: dict | None = None, headers: list | None = None) -> dict:
        return await self._api.json(method, url if url.startswith("https://") else self._api_url + url, body, headers)

    async def _get(self, url: str) -> dict | None:
        """A GET whose 404 is None."""
        try:
            return await self._call("GET", url)
        except ApiError as e:
            if e.status == 404:
                return None
            raise

    async def _pages(self, url: str, max_entries: int) -> list[dict]:
        """The `value` of every page, following @odata.nextLink only back to the same API, up to max_entries."""
        out: list[dict] = []
        sep = "&" if "?" in url else "?"
        url = f"{self._api_url}{url}{sep}$top={max(1, min(PAGE, max_entries))}"
        for _ in range(MAX_LIST_REQUESTS):
            got = await self._call("GET", url)
            out += [v for v in got.get("value") or () if isinstance(v, dict)] if isinstance(got.get("value"), list) else []
            nxt = got.get("@odata.nextLink")
            if len(out) >= max_entries or not isinstance(nxt, str) or nxt == url:
                break
            if not nxt.startswith(self._api_url + "/"):
                raise MountSourceError(_m("nextPage"))
            url = nxt
        return out[:max_entries]

    def _refusal(self, path: str, e: ApiError) -> MountSourceError | MountConflictError:
        if e.status == 401:
            return MountSourceError(_m("token"))
        if e.status == 403:
            return MountSourceError(_m("denied", path=path))
        if e.status == 404:
            return _Missing(_m("missing", path=path))
        if e.status == 409 or e.status == 412:
            return MountConflictError(_m("changedCode", path=path, code=e.code or str(e.status)))
        if e.status == 423:
            return MountSourceError(_m("locked", path=path))
        if e.status == 507:
            return MountSourceError(_m("full", path=path))
        return MountSourceError(_m("refused", path=path, reason=e.message or e.code or str(e.status)))

    # ----- where a path leads -----

    async def _start(self) -> _Loc:
        if self._root is not None:
            return self._root
        try:
            if self._drive_ref is not None:
                d = await self._call("GET", ("/me/drive" if self._drive_ref == "me" else f"/drives/{_safe_id(self._drive_ref)}") + "?$select=id,webUrl")
                self._root = _Loc("drive", "/", drive=_safe_id(d.get("id")), prefix=_web_path(d.get("webUrl")))
            else:
                s = await self._call("GET", _site_url(self._site_ref) + "?$select=id,webUrl")
                self._root = _Loc("site", "/", site=_safe_id(s.get("id")))
        except ApiError as e:
            raise MountSourceError(_m("cantOpen", what="drive" if self._drive_ref else "site", reason=str(self._refusal("/", e)))) from None
        return self._root

    async def _site_names(self, site_path: str, site_id: str, depth: int, fresh: bool = False) -> dict[str, _Container]:
        """A site's libraries, lists and subsites by name (libraries keep their name; on a clash a list gets " (list)" and
        a subsite " (site)"; what is still taken gets " (2)")."""
        if not fresh and site_path in self._containers:
            return self._containers[site_path]
        drives = await self._pages(f"/sites/{site_id}/drives?$select=id,name,webUrl", 2000)
        lists = await self._pages(f"/sites/{site_id}/lists?$select=id,displayName,list", 2000)
        subs = await self._pages(f"/sites/{site_id}/sites?$select=id,displayName,name", 2000) if depth < MAX_SITE_DEPTH else []
        found: list[tuple[str, _Container]] = []
        for d in sorted(drives, key=lambda d: str(d.get("name"))):
            if _ok_id(d.get("id")):
                found.append((safe_name(str(d.get("name") or ""), "Library"), _Container("drive", d["id"], _web_path(d.get("webUrl")))))
        taken = {n for n, _ in found}
        for kind, rows, title, tag in (("list", lists, "displayName", " (list)"), ("site", subs, "displayName", " (site)")):
            for r in sorted(rows, key=lambda r: str(r.get(title))):
                info = r.get("list") if kind == "list" else {}
                if not _ok_id(r.get("id")) or not isinstance(info, dict):
                    continue
                template = str(info.get("template", "")).lower()
                if kind == "list" and (info.get("hidden") or "library" in template or "catalog" in template):
                    continue  # document libraries are listed as drives; hidden and system lists are left out
                n = safe_name(str(r.get(title) or r.get("name") or ""), kind.title())
                found.append((n + tag if n in taken else n, _Container(kind, r["id"])))
                taken.add(n)
        names = unique([n for n, _ in found])
        self._containers[site_path] = out = {n: c for n, (_, c) in zip(names, found)}
        return out

    async def _where(self, path: str) -> _Loc:
        root = await self._start()
        parts = [] if path == "/" else path.split("/")[1:]
        if root.kind == "drive":
            return _Loc("drive", "/", drive=root.drive, rel=path, prefix=root.prefix)
        site_path, site, depth = "/", root.site, 0
        for i, part in enumerate(parts):
            names = await self._site_names(site_path, site, depth)
            c = names.get(part)
            if c is None:
                names = await self._site_names(site_path, site, depth, fresh=True)
                c = names.get(part)
            if c is None:
                raise _Missing(_m("missing", path=path))
            here = "/" + "/".join(parts[:i + 1])
            if c.kind == "site":
                site_path, site, depth = here, c.id, depth + 1
                continue
            rest = parts[i + 1:]
            if c.kind == "drive":
                return _Loc("drive", here, site=site, drive=c.id, rel="/" + "/".join(rest), prefix=c.prefix)
            if len(rest) > 1:
                raise _Missing(_m("missing", path=path))
            return _Loc("item" if rest else "list", here, site=site, list=c.id, rel=rest[0] if rest else "/")
        return _Loc("site", site_path, site=site)

    def _top_of(self, path: str) -> str | None:
        """The library, list or site a path is in, from what was listed (no request); None when not known."""
        if self._root is None or self._root.kind == "drive":
            return "/"
        parts, site_path = path.split("/")[1:], "/"
        for i, part in enumerate(parts):
            c = self._containers.get(site_path, {}).get(part)
            if c is None:
                return None
            here = "/" + "/".join(parts[:i + 1])
            if c.kind != "site":
                return here
            site_path = here
        return site_path

    def note(self, change) -> str | None:
        """What the host should know about a change before applying it: a rename between two libraries, lists or sites is
        a copy and a delete (the copy gets a new id, without the original's version history, sharing or metadata)."""
        if getattr(change, "kind", None) != "rename" or change.to is None:
            return None
        a, b = self._top_of(change.path), self._top_of(change.to)
        if a is None or b is None or a == b:
            return None
        return _m("crossMove", path=change.path)

    # ----- drive requests -----

    @staticmethod
    def _dp(loc: _Loc, rel: str | None = None, tail: str = "") -> str:
        """The URL of a path in a drive, addressed by path: /drives/D/root, /drives/D/root:/a/b:, plus /children etc."""
        rel = loc.rel if rel is None else rel
        if rel == "/":
            return f"/drives/{loc.drive}/root{tail}"
        return f"/drives/{loc.drive}/root:{quote(rel)}:{tail}"

    async def _item(self, loc: _Loc, path: str, rel: str | None = None) -> dict | None:
        try:
            got = await self._get(self._dp(loc, rel) + f"?$select={SELECT}")
        except ApiError as e:
            raise self._refusal(path, e) from None
        rel = loc.rel if rel is None else rel
        if got is None or (rel != "/" and got.get("name") != rel.rsplit("/", 1)[1]):
            return None  # SharePoint matches names without case; another case is another name here
        return got

    def _check_name(self, path: str, loc: _Loc, rel: str) -> None:
        name = rel.rsplit("/", 1)[1]
        if any(c in _ILLEGAL for c in name) or name != name.strip(" ") or name.endswith(".") or name.upper() in _RESERVED \
                or "_vti_" in name.lower() or name.startswith("~$") \
                or (rel.count("/") == 1 and name.lower() == "forms"):
            raise MountSourceError(_m("badName", path=path))
        if len(loc.prefix) + len(rel) > MAX_PATH:
            raise MountSourceError(_m("tooLong", path=path, max=str(MAX_PATH)))

    # ----- list requests -----

    async def _columns(self, loc: _Loc, fresh: bool = False) -> _ListData:
        data = self._lists.get(loc.list)
        if data is not None and not fresh:
            return data
        rows = await self._pages(f"/sites/{loc.site}/lists/{loc.list}/columns", 2000)
        cols, keys = [], set()
        for c in rows:
            name = c.get("name")
            if not isinstance(name, str) or c.get("hidden") or name in _SKIP or name.startswith("_"):
                continue
            t = next((t for t in _TYPES if isinstance(c.get(t), dict)), "other")
            key = safe_name(str(c.get("displayName") or name), name)
            key = name if key in keys else key
            keys.add(key)
            why = (READ_ONLY["sharepoint"] if name in _SET_BY_SHAREPOINT or c.get("readOnly") else
                   "" if t in EDITABLE_TYPES else READ_ONLY["type"].format(type=t))
            ch = c.get("choice") or {}
            cols.append(_Column(key, name, t, why, tuple(x for x in ch.get("choices") or () if isinstance(x, str)),
                                bool(ch.get("allowTextEntry")), (c.get("text") or {}).get("maxLength") if t == "text" else None))
        data = self._lists[loc.list] = _ListData(cols, data.names if data else {})
        return data

    async def _list_items(self, loc: _Loc, path: str, max_entries: int) -> list[tuple[str, dict]]:
        data = await self._columns(loc, fresh=True)
        items = await self._pages(f"/sites/{loc.site}/lists/{loc.list}/items?expand=fields", max_entries)
        items = [it for it in items if _ok_id(it.get("id")) and isinstance(it.get("fields"), dict)
                 and str((it.get("contentType") or {}).get("name", "")) != "Folder"]
        items.sort(key=lambda it: int(it["id"]) if str(it["id"]).isdigit() else 0)
        names = unique([safe_name(str(it["fields"].get("Title") or ""), f"Item {it['id']}") + ".md" for it in items])
        data.names = dict(zip(names, (it["id"] for it in items)))
        return list(zip(names, items))

    async def _item_of(self, loc: _Loc, path: str) -> dict | None:
        data = self._lists.get(loc.list)
        if data is None or loc.rel not in data.names:
            await self._list_items(loc, path, 5000)
            data = self._lists[loc.list]
        iid = data.names.get(loc.rel)
        if iid is None:
            return None
        try:
            return await self._get(f"/sites/{loc.site}/lists/{loc.list}/items/{iid}?expand=fields")
        except ApiError as e:
            raise self._refusal(path, e) from None

    def _render(self, data: _ListData, item: dict) -> bytes:
        f = item.get("fields") or {}
        values = {}
        for c in data.columns:
            if c.name in ("Author", "Editor"):
                who = (item.get("createdBy" if c.name == "Author" else "lastModifiedBy") or {}).get("user") or {}
                values[c.key] = who.get("displayName") if isinstance(who, dict) else None
            elif c.name == "ID":  # not among an item's fields: its id
                values[c.key] = int(item["id"]) if str(item.get("id")).isdigit() else None
            elif c.type in ("personOrGroup", "lookup"):
                values[c.key] = f.get(c.name + "LookupId", f.get(c.name))
            else:
                values[c.key] = f.get(c.name)
            if values[c.key] is not None and not isinstance(values[c.key], (str, int, float, bool, list)):
                values[c.key] = None
        ro = sorted(c.key for c in data.columns if c.read_only)
        return front_matter(values, ("read-only: " + ", ".join(ro)) if ro else None).encode()

    def _fields(self, path: str, data: _ListData, text: str, current: bytes | None) -> dict:
        """The fields to send for an item file: those whose value differs from the current file's."""
        fm, body = split_front_matter(path, text)
        if body.strip():
            raise MountSourceError(_m("itemText", path=path))
        now, _ = split_front_matter(path, current.decode()) if current else ({}, "")
        by_key = {c.key: c for c in data.columns}
        out = {}
        for key, v in (fm or {}).items():
            c = by_key.get(key)
            if c is None:
                raise MountSourceError(_m("noColumn", path=path, name=key))
            if (now or {}).get(key, object()) == v or (current is None and (c.read_only or v is None)):
                continue  # a new item: what SharePoint sets is left to it
            if c.read_only:
                raise MountSourceError(_m("readOnlyColumn", path=path, name=key, why=c.read_only))
            out[c.name] = _field_value(path, c, v)
            if c.type == "choice" and isinstance(v, list):
                out[c.name + "@odata.type"] = "Collection(Edm.String)"
        return out

    # ----- the source -----

    async def list(self, path: str, max_entries: int) -> list[MountEntry]:
        loc = await self._where(path)
        if loc.kind == "site":
            depth = 0 if loc.top == "/" else loc.top.count("/")
            names = await self._site_names(loc.top, loc.site, depth, fresh=True)
            return [MountEntry(n, True, 0, None, f"{c.kind}:{c.id}") for n, c in list(names.items())[:max_entries]]
        if loc.kind == "list":
            data = await self._columns(loc, fresh=True)
            return [MountEntry(n, False, len(self._render(data, it)), _when(it), _text(it.get("eTag")))
                    for n, it in await self._list_items(loc, path, max_entries)]
        if loc.kind == "item":
            raise MountSourceError(_m("notDir", path=path))
        try:
            rows = await self._pages(self._dp(loc, tail="/children") + f"?$select={SELECT}", max_entries)
        except ApiError as e:
            raise self._refusal(path, e) from None
        return [e for e in (_entry(r) for r in rows) if e is not None]

    async def stat(self, path: str) -> MountEntry | None:
        try:
            loc = await self._where(path)
        except _Missing:
            return None
        name = path.rsplit("/", 1)[1]
        if loc.kind in ("site", "list") or (loc.kind == "drive" and loc.rel == "/"):
            return MountEntry(name, True, 0, None, "")
        if loc.kind == "item":
            it = await self._item_of(loc, path)
            if it is None:
                return None
            return MountEntry(name, False, len(self._render(self._lists[loc.list], it)), _when(it), _text(it.get("eTag")))
        it = await self._item(loc, path)
        e = None if it is None else _entry(it)
        return None if e is None else MountEntry(name, e.is_dir, e.size, e.modified, e.version)

    async def read(self, path: str, max_bytes: int) -> bytes:
        loc = await self._where(path)
        if loc.kind == "item":
            it = await self._item_of(loc, path)
            if it is None:
                raise _Missing(_m("missing", path=path))
            return self._render(self._lists[loc.list], it)
        if loc.kind != "drive" or loc.rel == "/":
            raise MountSourceError(_m("isDir", path=path))
        status, rh, raw = await self._api.send("GET", self._api_url + self._dp(loc, tail="/content"), limit=max_bytes)
        if status in (301, 302, 303, 307, 308):
            where = header(rh, "Location") or ""
            if urlsplit(where).scheme != "https":
                raise MountSourceError(_m("downloadNotHttps", path=path))
            status, rh, raw = await self._api.send("GET", where, auth=False, limit=max_bytes)  # pre-signed: no token
        if status == 404:
            raise _Missing(_m("goneSinceListed", path=path))
        if status >= 300:
            raise MountSourceError(_m("downloadFailed", path=path, status=str(status)))
        if len(raw) > max_bytes:
            raise MountSourceError(_m("tooLarge", path=path))
        return raw

    async def write(self, path: str, data: bytes, expected_version: str | None) -> str:
        loc = await self._where_new(path)
        if loc.kind == "item":
            return await self._write_item(loc, path, bytes(data), expected_version)
        if loc.kind != "drive" or loc.rel == "/":
            raise MountSourceError(_m("filesInLibrary", path=path))
        self._check_name(path, loc, loc.rel)
        ifm = [("If-Match", expected_version)] if expected_version else []
        try:
            if len(data) <= SIMPLE_UPLOAD:
                url = self._dp(loc, tail="/content") + ("" if expected_version else "?@microsoft.graph.conflictBehavior=fail")
                status, rh, raw = await self._api.send("PUT", self._api_url + url, bytes(data), [("Content-Type", "application/octet-stream"), *ifm])
                got = _answer(status, raw)
            else:
                got = await self._upload(loc, path, bytes(data), expected_version)
        except ApiError as e:
            raise self._refusal(path, e) from None
        return _text(got.get("cTag") or got.get("eTag"))

    async def _upload(self, loc: _Loc, path: str, data: bytes, expected: str | None) -> dict:
        s = await self._call("POST", self._dp(loc, tail="/createUploadSession"),
                             {"item": {"@microsoft.graph.conflictBehavior": "replace" if expected else "fail"}},
                             [("If-Match", expected)] if expected else None)
        url = s.get("uploadUrl")
        if not isinstance(url, str) or urlsplit(url).scheme != "https":
            raise MountSourceError(_m("noUploadLink", path=path))
        try:
            for start in range(0, len(data), CHUNK):
                part = data[start:start + CHUNK]
                rng = f"bytes {start}-{start + len(part) - 1}/{len(data)}"
                status, _, raw = await self._api.send("PUT", url, part, [("Content-Range", rng)], auth=False)  # pre-signed
                if status in (200, 201):
                    return _answer(status, raw)
                if status != 202:
                    _answer(status, raw)
            raise MountSourceError(_m("uploadEnded", path=path))
        except BaseException:
            try:
                await self._api.send("DELETE", url, auth=False)
            except Exception:
                pass
            raise

    async def _where_new(self, path: str) -> _Loc:
        """Where a path that may not exist yet would go: its parent must exist."""
        try:
            return await self._where(path)
        except _Missing:
            parent, name = path.rsplit("/", 1)
            loc = await self._where(parent or "/")
            if loc.kind == "drive":
                return _Loc("drive", loc.top, site=loc.site, drive=loc.drive, rel=(loc.rel.rstrip("/") + "/" + name), prefix=loc.prefix)
            if loc.kind == "list":
                return _Loc("item", loc.top, site=loc.site, list=loc.list, rel=name)
            if loc.kind == "site":
                return _Loc("new-in-site", loc.top, site=loc.site, rel=name)
            raise MountSourceError(_m("notDir", path=path)) from None

    async def _write_item(self, loc: _Loc, path: str, data: bytes, expected: str | None) -> str:
        if not loc.rel.endswith(".md"):
            raise MountSourceError(_m("itemsAreMd", path=path))
        if len(data) > MAX_ITEM_BYTES:
            raise MountSourceError(_m("itemTooLarge", path=path, max=str(MAX_ITEM_BYTES)))
        try:
            text = data.decode()
        except UnicodeDecodeError:
            raise MountSourceError(_m("itemUtf8", path=path)) from None
        cols = await self._columns(loc)
        base = f"/sites/{loc.site}/lists/{loc.list}/items"
        try:
            if expected is None:
                fields = self._fields(path, cols, text, None)
                fields["Title"] = loc.rel[:-3]
                got = await self._call("POST", base, {"fields": fields})
                if _ok_id(got.get("id")):
                    cols.names[loc.rel] = got["id"]
                return _text(got.get("eTag"))
            it = await self._item_of(loc, path)
            if it is None:
                raise MountConflictError(_m("deleted", path=path))
            fields = self._fields(path, cols, text, self._render(cols, it))
            if not fields:
                return _text(it.get("eTag"))
            got = await self._call("PATCH", f"{base}/{it['id']}/fields", fields, [("If-Match", expected)])
        except ApiError as e:
            raise self._refusal(path, e) from None
        return _text(got.get("@odata.etag")) or _text((await self._item_of(loc, path) or {}).get("eTag"))

    async def delete(self, path: str, expected_version: str | None) -> None:
        loc = await self._where(path)
        if loc.kind in ("site", "list") or (loc.kind == "drive" and loc.rel == "/"):
            raise MountSourceError(_m("noDeleteContainer", path=path))
        try:
            if loc.kind == "item":
                it = await self._item_of(loc, path)
                if it is None:
                    raise MountConflictError(_m("deleted", path=path))
                await self._call("DELETE", f"/sites/{loc.site}/lists/{loc.list}/items/{it['id']}", None,
                                 [("If-Match", expected_version)] if expected_version else None)
                self._lists[loc.list].names.pop(loc.rel, None)
                return
            it = await self._item(loc, path)
            if it is None:
                raise MountConflictError(_m("deleted", path=path))
            if "folder" in it:
                left = await self._pages(self._dp(loc, tail="/children") + "?$select=id,name", 1)
                if left:
                    raise MountSourceError(_m("notEmpty", path=path))
                await self._call("DELETE", self._dp(loc))
            else:
                await self._call("DELETE", self._dp(loc), None, [("If-Match", expected_version)] if expected_version else None)
        except ApiError as e:
            raise self._refusal(path, e) from None

    async def mkdir(self, path: str) -> str | None:
        loc = await self._where_new(path)
        if loc.kind == "new-in-site":
            raise MountSourceError(_m("noCreateContainer", path=path))
        if loc.kind in ("item", "list"):
            raise MountSourceError(_m("noFolderInList", path=path))
        if loc.kind != "drive" or loc.rel == "/":
            raise MountConflictError(_m("exists", path=path))
        self._check_name(path, loc, loc.rel)
        parent, name = loc.rel.rsplit("/", 1)
        try:
            got = await self._call("POST", self._dp(loc, parent or "/", "/children"),
                                   {"name": name, "folder": {}, "@microsoft.graph.conflictBehavior": "fail"})
        except ApiError as e:
            raise self._refusal(path, e) from None
        return _text(got.get("eTag")) or None

    async def rename(self, path: str, to: str, expected_version: str | None) -> str | None:
        src = await self._where(path)
        dst = await self._where_new(to)
        if src.kind in ("site", "list") or (src.kind == "drive" and src.rel == "/"):
            raise MountSourceError(_m("noRenameContainer", path=path))
        if src.kind == "item":
            if dst.kind != "item":
                raise MountSourceError(_m("itemToList", path=to))
            if dst.list != src.list:
                return await self._copy_item(src, dst, path, to, expected_version)
            if not dst.rel.endswith(".md"):
                raise MountSourceError(_m("itemsAreMd", path=to))
            it = await self._item_of(src, path)
            if it is None:
                raise MountConflictError(_m("deleted", path=path))
            try:
                got = await self._call("PATCH", f"/sites/{src.site}/lists/{src.list}/items/{it['id']}/fields", {"Title": dst.rel[:-3]},
                                       [("If-Match", expected_version)] if expected_version else None)
            except ApiError as e:
                raise self._refusal(path, e) from None
            names = self._lists[src.list].names
            names[dst.rel] = names.pop(src.rel, it["id"])
            return _text(got.get("@odata.etag")) or None
        if dst.kind != "drive" or dst.rel == "/":
            raise MountSourceError(_m("fileToLibrary", path=to))
        self._check_name(to, dst, dst.rel)
        if dst.drive != src.drive:
            return await self._copy_drive(src, dst, path, to, expected_version)
        it = await self._item(src, path)
        if it is None:
            raise MountConflictError(_m("deleted", path=path))
        body: dict = {"name": dst.rel.rsplit("/", 1)[1]}
        new_parent, old_parent = dst.rel.rsplit("/", 1)[0] or "/", src.rel.rsplit("/", 1)[0] or "/"
        try:
            if new_parent != old_parent:
                p = await self._item(dst, to, new_parent)
                if p is None or "folder" not in p or not _ok_id(p.get("id")):
                    raise MountSourceError(_m("noTargetFolder", path=to))
                body["parentReference"] = {"id": p["id"]}
            ifm = [("If-Match", expected_version)] if expected_version and "folder" not in it else None
            got = await self._call("PATCH", self._dp(src), body, ifm)
        except ApiError as e:
            raise self._refusal(path, e) from None
        return _text(got.get("cTag") or got.get("eTag")) or None

    # ----- moves between libraries, lists and sites: a copy and a delete -----

    async def _copy_drive(self, src: _Loc, dst: _Loc, path: str, to: str, expected: str | None) -> str | None:
        it = await self._item(src, path)
        if it is None:
            raise MountConflictError(_m("deleted", path=path))
        if "file" in it and expected and _text(it.get("cTag") or it.get("eTag")) != expected:
            raise MountConflictError(_m("changed", path=path))
        budget = [MAX_COPY_ENTRIES, MAX_COPY_BYTES]
        version = await self._copy_tree(src, dst, path, to, it, budget)
        try:
            await self._call("DELETE", self._dp(src), None, [("If-Match", expected)] if expected and "file" in it else None)
        except ApiError as e:
            raise MountSourceError(_m("copiedNotDeleted", path=path, to=to, reason=str(self._refusal(path, e)))) from None
        return version

    async def _copy_tree(self, src: _Loc, dst: _Loc, path: str, to: str, it: dict, budget: list) -> str | None:
        budget[0] -= 1
        if budget[0] < 0:
            raise MountSourceError(_m("copyTooMany", path=path, max=str(MAX_COPY_ENTRIES)))
        if "folder" in it:
            version = await self.mkdir(to)
            for child in await self._pages(self._dp(src, tail="/children") + f"?$select={SELECT}", MAX_COPY_ENTRIES):
                name = child.get("name")
                if isinstance(name, str) and _entry(child) is not None:
                    s = _Loc("drive", src.top, src.site, src.drive, rel=src.rel.rstrip("/") + "/" + name, prefix=src.prefix)
                    d = _Loc("drive", dst.top, dst.site, dst.drive, rel=dst.rel.rstrip("/") + "/" + name, prefix=dst.prefix)
                    self._check_name(to + "/" + name, d, d.rel)
                    await self._copy_tree(s, d, path + "/" + name, to + "/" + name, child, budget)
            return version
        size = it.get("size") if isinstance(it.get("size"), int) else MAX_COPY_BYTES
        budget[1] -= size
        if budget[1] < 0:
            raise MountSourceError(_m("copyTooLarge", path=path, max=str(MAX_COPY_BYTES)))
        data = await self.read(path, max(size, 0) + 1)
        return await self.write(to, data, None)

    async def _copy_item(self, src: _Loc, dst: _Loc, path: str, to: str, expected: str | None) -> str | None:
        it = await self._item_of(src, path)
        if it is None:
            raise MountConflictError(_m("deleted", path=path))
        if expected and _text(it.get("eTag")) != expected:
            raise MountConflictError(_m("changed", path=path))
        fm, _ = split_front_matter(path, self._render(self._lists[src.list], it).decode())
        target = await self._columns(dst)
        keep = {c.key for c in target.columns if not c.read_only}
        text = front_matter({k: v for k, v in (fm or {}).items() if k in keep and v is not None})
        version = await self._write_item(dst, to, text.encode(), None)
        try:
            await self._call("DELETE", f"/sites/{src.site}/lists/{src.list}/items/{it['id']}", None, [("If-Match", expected)] if expected else None)
        except ApiError as e:
            raise MountSourceError(_m("copiedNotDeleted", path=path, to=to, reason=str(self._refusal(path, e)))) from None
        self._lists[src.list].names.pop(src.rel, None)
        return version


# ----- helpers -----

def _field_value(path: str, c: _Column, v):
    def bad(what: str) -> MountSourceError:
        return MountSourceError(_m("expected", path=path, name=c.key, what=what))
    if c.type == "text":
        if v is not None and (isinstance(v, (bool, list, dict))):
            raise bad(EXPECTED["text"])
        s = "" if v is None else str(v)
        if c.max_length and len(s) > c.max_length:
            raise bad(EXPECTED["maxLength"].format(max=c.max_length))
        return s
    if c.type == "choice":
        vs = v if isinstance(v, list) else [] if v is None else [v]
        for x in vs:
            if not isinstance(x, str):
                raise bad(EXPECTED["choiceName"])
            if not c.free_choice and x not in c.choices:
                raise bad(EXPECTED["choice"].format(choices=", ".join(c.choices)))
        return v
    if c.type in ("number", "currency"):
        if v is not None and (isinstance(v, bool) or not isinstance(v, (int, float))):
            raise bad(EXPECTED["number"])
        return v
    if c.type == "boolean":
        if not isinstance(v, bool):
            raise bad(EXPECTED["boolean"])
        return v
    if c.type == "dateTime":
        if v is None or v == "":
            return None
        if not isinstance(v, str) or not re.fullmatch(r"\d{4}-\d\d-\d\d(T\d\d:\d\d(:\d\d(\.\d+)?)?(Z|[+-]\d\d:\d\d)?)?", v):
            raise bad(EXPECTED["dateTime"])
        return v if "T" in v else v + "T00:00:00Z"
    raise MountSourceError(_m("notEditable", path=path, name=c.key))


def _answer(status: int, raw: bytes) -> dict:
    try:
        got = json.loads(raw) if raw else {}
    except ValueError:
        got = None
    if status >= 400:
        e = (got or {}).get("error") if isinstance(got, dict) else None
        e = e if isinstance(e, dict) else {}
        raise ApiError(status, _text(e.get("code"))[:100], _text(e.get("message"))[:1000])
    if not isinstance(got, dict):
        raise MountSourceError(_m("notJson", status=str(status)))
    return got


def _entry(it: dict) -> MountEntry | None:
    name = it.get("name")
    if not isinstance(name, str):
        return None
    if isinstance(it.get("folder"), dict):
        return MountEntry(name, True, 0, _when(it), _text(it.get("eTag")))
    if isinstance(it.get("file"), dict):
        size = it.get("size")
        return MountEntry(name, False, size if isinstance(size, int) and size >= 0 else None, _when(it), _text(it.get("cTag") or it.get("eTag")))
    return None  # a OneNote notebook, a link or anything else that is neither a file nor a folder


def _when(it: dict) -> datetime | None:
    try:
        return datetime.fromisoformat(_text(it.get("lastModifiedDateTime")).replace("Z", "+00:00")).astimezone(timezone.utc).replace(tzinfo=None)
    except ValueError:
        return None


def _text(v) -> str:
    return v[:1024] if isinstance(v, str) else ""


def _ok_id(v) -> bool:
    return isinstance(v, str) and _ID.fullmatch(v) is not None


def _safe_id(v) -> str:
    """An id from a response or the host, checked before it goes into a URL's path."""
    if not _ok_id(v):
        raise MountSourceError(_m("badId"))
    return v


def _web_path(url) -> str:
    return unquote(urlsplit(url).path) if isinstance(url, str) else ""


def _site_url(ref: str) -> str:
    """The Graph URL of a site from its web URL (https://host/sites/x), host:/sites/x, or its id."""
    if ref.startswith("https://"):
        u = urlsplit(ref)
        return f"/sites/{quote(u.hostname or '')}:{quote(u.path.rstrip('/') or '/')}"
    if ":" in ref and "/" in ref:
        host, _, p = ref.partition(":")
        return f"/sites/{quote(host)}:{quote(p)}"
    return f"/sites/{_safe_id(ref)}"
