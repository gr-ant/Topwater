"""A stateful stand-in for Microsoft Graph (v1.0), as a webaccess transport for GraphSource: sites with subsites, document
libraries with folders and files (eTag and cTag, children paging, path addressing, content through a pre-signed
download redirect, simple PUT and upload sessions, PATCH rename and move, DELETE, conflict behaviour, If-Match), and
lists with columns and items (eTags, fields PATCH, read-only columns), plus faults to inject. It answers the way the real
service did when it was checked (docs/connectors/graph-design.md, "The fake"); the C# and TypeScript ports keep one that
behaves the same, and tests/connectors/graph/cases.json runs against each.

    fake = FakeGraph.from_tree({"site": "Sandbox", "libraries": {"Documents": {"Inbox/a.txt": "hi"}}})
    source = GraphSource("secret-token", site=fake.site_url, transport=fake)
"""
from __future__ import annotations

import json
import re
import threading
from datetime import datetime, timedelta
from urllib.parse import parse_qs, quote, unquote, urlsplit

API = "https://graph.microsoft.com/v1.0"
HOST = "fake.sharepoint.com"
UPLOAD = "https://upload.fake.sharepoint.com/up/"
ILLEGAL = set('"*:<>?/\\|')
RESERVED = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(10)), *(f"LPT{i}" for i in range(10)), ".LOCK", "DESKTOP.INI"}
SIMPLE_LIMIT = 4 * 1024 * 1024


class _Refused(Exception):
    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status, self.code, self.message = status, code, message


def _guid(n: int) -> str:
    return f"{n:08X}-0000-4000-8000-{n:012X}"


class FakeGraph:
    def __init__(self, token: str = "secret-token"):
        self.token = token
        self.clock = datetime(2026, 1, 1)
        self.sites: dict[str, dict] = {}
        self.drives: dict[str, dict] = {}
        self.items: dict[str, dict] = {}
        self.lists: dict[str, dict] = {}
        self.sessions: dict[str, dict] = {}
        self.requests: list[tuple[str, str]] = []
        self.faults: list[dict] = []
        self.leaks: list[str] = []  # URLs outside the API that were sent the bearer token
        self.download_host = "https://" + HOST  # where content redirects to
        self.page_size = 200  # the most one page holds
        self.me: str | None = None  # the drive /me/drive is
        self.root_site = ""
        self._n = 0
        self._lock = threading.Lock()

    # ----- building and inspecting -----

    def _next(self) -> int:
        self._n += 1
        return self._n

    def stamp(self) -> str:
        return self.clock.strftime("%Y-%m-%dT%H:%M:%SZ")

    def tick(self, seconds: float = 1) -> None:
        self.clock += timedelta(seconds=seconds)

    @property
    def site_url(self) -> str:
        return self.sites[self.root_site]["webUrl"]

    def add_site(self, title: str, parent: str | None = None) -> str:
        n = self._next()
        url = (self.sites[parent]["webUrl"] if parent else f"https://{HOST}/sites") + "/" + re.sub(r"[^A-Za-z0-9]", "", title)
        sid = f"{HOST},{_guid(n).lower()},{_guid(n + 1000).lower()}"
        self.sites[sid] = {"id": sid, "displayName": title, "name": url.rsplit("/", 1)[1], "webUrl": url, "parent": parent,
                           "drives": [], "lists": [], "subsites": []}
        if parent:
            self.sites[parent]["subsites"].append(sid)
        else:
            self.root_site = self.root_site or sid
        return sid

    def add_drive(self, site: str | None, name: str) -> str:
        n = self._next()
        did = f"b!fakeDrive{n:04d}"
        root = self._new_item(did, None, "root", folder=True)
        web = (self.sites[site]["webUrl"] if site else f"https://{HOST}/personal/me") + "/" + ("Shared Documents" if name == "Documents" else name)
        self.drives[did] = {"id": did, "name": name, "root": root, "webUrl": quote(web, safe=":/"), "site": site}
        if site:
            self.sites[site]["drives"].append(did)
        if self.me is None and site is None:
            self.me = did
        return did

    def _new_item(self, drive: str, parent: str | None, name: str, folder: bool = False, content: bytes = b"") -> str:
        n = self._next()
        iid = f"01FAKE{n:06d}"
        self.items[iid] = {"id": iid, "drive": drive, "parent": parent, "name": name, "folder": folder, "content": content,
                           "e": 1, "c": 1, "guid": _guid(n), "modified": self.stamp()}
        return iid

    def add(self, drive: str, path: str, content: str | bytes | None = None) -> str:
        """A file (content) or a folder (None) at path, with the folders above it."""
        cur = self.drives[drive]["root"]
        parts = [p for p in path.strip("/").split("/") if p]
        for i, part in enumerate(parts):
            last = i == len(parts) - 1
            kid = self._child(cur, part)
            if kid is None:
                is_folder = not last or content is None
                data = b"" if is_folder else (content.encode() if isinstance(content, str) else bytes(content))
                kid = self._new_item(drive, cur, part, is_folder, data)
            elif last and content is not None:
                self._set_content(kid, content.encode() if isinstance(content, str) else bytes(content))
            cur = kid
        return cur

    def add_list(self, site: str, title: str, columns: dict | None = None, template: str = "genericList", hidden: bool = False) -> str:
        n = self._next()
        lid = _guid(n).lower()
        cols = [
            {"name": "Title", "displayName": "Title", "text": {"maxLength": 255}, "readOnly": False},
            {"name": "ID", "displayName": "ID", "number": {}, "readOnly": True},
            {"name": "Created", "displayName": "Created", "dateTime": {}, "readOnly": True},
            {"name": "Modified", "displayName": "Modified", "dateTime": {}, "readOnly": True},
            {"name": "Author", "displayName": "Created By", "personOrGroup": {}, "readOnly": True},
            {"name": "Editor", "displayName": "Modified By", "personOrGroup": {}, "readOnly": True},
            {"name": "ContentType", "displayName": "Content Type", "readOnly": True},
            {"name": "Attachments", "displayName": "Attachments", "boolean": {}, "readOnly": False},
            {"name": "_ColorTag", "displayName": "Color Tag", "text": {}, "readOnly": True},
            {"name": "LinkTitle", "displayName": "Title", "computed": {}, "readOnly": True, "hidden": True},
        ]
        for name, spec in (columns or {}).items():
            col = {"name": name, "displayName": name, "readOnly": False}
            if isinstance(spec, list):
                col["choice"] = {"choices": spec, "allowTextEntry": False, "displayAs": "dropDownMenu"}
            elif isinstance(spec, dict):
                col.update(spec)
            elif spec == "text":
                col["text"] = {"allowMultipleLines": False, "maxLength": 255}
            elif spec == "note":
                col["text"] = {"allowMultipleLines": True}
            elif spec == "currency":
                col["currency"] = {"locale": "en-US"}
            elif spec == "calculated":
                col.update({"calculated": {"formula": "=1"}, "readOnly": True})
            else:
                col[spec] = {}
            cols.append(col)
        for i, c in enumerate(cols):
            c.setdefault("hidden", False)
            c["id"] = _guid(n * 100 + i).lower()
        self.lists[lid] = {"id": lid, "site": site, "displayName": title, "template": template, "hidden": hidden,
                           "columns": cols, "items": {}, "next": 1}
        self.sites[site]["lists"].append(lid)
        return lid

    def add_item(self, lid: str, fields: dict, who: str = "Grant Miller") -> str:
        lst = self.lists[lid]
        iid = str(lst["next"])
        lst["next"] += 1
        f = {k: v for k, v in fields.items()}
        f.update({"id": iid, "Created": self.stamp(), "Modified": self.stamp(), "AuthorLookupId": "10",
                  "EditorLookupId": "10", "ContentType": "Item", "Attachments": False})
        lst["items"][iid] = {"fields": f, "e": 1, "guid": _guid(self._next()).lower(), "created": self.stamp(), "modified": self.stamp(),
                             "by": who, "editor": who}
        return iid

    @classmethod
    def from_tree(cls, tree: dict, token: str = "secret-token") -> "FakeGraph":
        """{site, libraries: {name: {path: text, "folder/": null}}, lists: {name: {columns, items: [fields]}}, subsites: {name: tree}}"""
        fake = cls(token)

        def build(node: dict, parent: str | None) -> None:
            sid = fake.add_site(node.get("site", "Site"), parent)
            for lib, files in (node.get("libraries") or {}).items():
                d = fake.add_drive(sid, lib)
                for p, v in (files or {}).items():
                    fake.add(d, p, v)
            for name, spec in (node.get("lists") or {}).items():
                lid = fake.add_list(sid, name, spec.get("columns"), spec.get("template", "genericList"), spec.get("hidden", False))
                for fields in spec.get("items") or ():
                    fake.add_item(lid, fields)
            for name, sub in (node.get("subsites") or {}).items():
                build({**sub, "site": name}, sid)
        build(tree, None)
        return fake

    def drive_named(self, name: str, site: str | None = None) -> str:
        return next(d for d, v in self.drives.items() if v["name"] == name and (site is None or v["site"] == site))

    def list_named(self, title: str) -> str:
        return next(k for k, v in self.lists.items() if v["displayName"] == title)

    def find(self, drive: str, path: str) -> str | None:
        cur = self.drives[drive]["root"]
        for part in [p for p in path.strip("/").split("/") if p]:
            cur = self._child(cur, part)
            if cur is None:
                return None
        return cur

    def edit(self, drive: str, path: str, content: str | bytes) -> None:
        """Someone else changes a file."""
        self._set_content(self.find(drive, path), content.encode() if isinstance(content, str) else content)

    def remove(self, drive: str, path: str) -> None:
        self._delete(self.find(drive, path))

    def fault(self, method: str, path: str, status: int, headers: list | None = None, body: bytes | str | None = None, times: int = 1) -> None:
        """The next `times` requests of `method` whose URL path matches the regex `path` get this answer instead."""
        self.faults.append({"method": method, "path": re.compile(path), "status": status, "headers": headers or [],
                            "body": body.encode() if isinstance(body, str) else body, "times": times})

    def outline(self) -> str:
        """Every site, library path and list item, one a line: what tests compare."""
        out = []

        def site(sid: str, at: str) -> None:
            s = self.sites[sid]
            for d in s["drives"]:
                def walk(iid: str, p: str) -> None:
                    for k in sorted(self._children(iid), key=lambda k: self.items[k]["name"]):
                        it = self.items[k]
                        q = f"{p}/{it['name']}"
                        out.append(q + "/" if it["folder"] else f"{q} ({it['content'].decode('utf-8', 'replace')[:40]!r})")
                        if it["folder"]:
                            walk(k, q)
                walk(self.drives[d]["root"], f"{at}{self.drives[d]['name']}")
            for lid in s["lists"]:
                lst = self.lists[lid]
                for iid, it in sorted(lst["items"].items(), key=lambda kv: int(kv[0])):
                    shown = {k: v for k, v in sorted(it["fields"].items()) if k not in (
                        "id", "ID", "Created", "Modified", "AuthorLookupId", "EditorLookupId", "ContentType", "Attachments", "Title") and v is not None}
                    out.append(f"{at}{lst['displayName']}#{iid} {it['fields'].get('Title')!r} {json.dumps(shown, sort_keys=True)}")
            for sub in s["subsites"]:
                site(sub, f"{at}{self.sites[sub]['displayName']}/")
        site(self.root_site, "/")
        return "\n".join(out) + "\n"

    # ----- the transport -----

    def __call__(self, method, url, headers, body, timeout, limit):
        with self._lock:
            u = urlsplit(url)
            hdr = {k.lower(): v for k, v in headers}
            self.requests.append((method, u.path))
            for f in self.faults:
                if f["times"] > 0 and f["method"] == method and f["path"].search(u.path):
                    f["times"] -= 1
                    return f["status"], "Fault", f["headers"], f["body"] if f["body"] is not None else json.dumps(
                        {"error": {"code": "fault", "message": "injected"}}).encode()
            api = url.startswith(API + "/")
            if not api and "authorization" in hdr:
                self.leaks.append(url)
            try:
                if url.startswith(UPLOAD):
                    status, data, extra = self._upload(method, u.path.rsplit("/", 1)[1], hdr, body or b"")
                elif not api:
                    status, data, extra = self._download(method, u)
                else:
                    if hdr.get("authorization") != "Bearer " + self.token:
                        raise _Refused(401, "InvalidAuthenticationToken", "Access token is empty or invalid.")
                    query = {k: v[0] for k, v in parse_qs(u.query).items()}
                    data_in = None
                    if body and "application/json" in hdr.get("content-type", ""):
                        data_in = json.loads(body)
                    status, data, extra = self._route(method, u.path[len("/v1.0"):], query, hdr, data_in, body, url)
            except _Refused as e:
                return e.status, "Refused", [("Content-Type", "application/json")], json.dumps(
                    {"error": {"code": e.code, "message": e.message, "innerError": {"date": self.stamp()}}}).encode()
            if isinstance(data, (bytes, bytearray)):
                return status, "OK", extra, bytes(data)
            return status, "OK", [("Content-Type", "application/json"), *extra], b"" if data is None else json.dumps(data).encode()

    # ----- routes -----

    def _route(self, method: str, path: str, q: dict, hdr: dict, data, raw: bytes | None, url: str):
        m = re.fullmatch(r"/sites/([^/:]+):(/.*)", path)
        if m and method == "GET":
            want = f"https://{unquote(m.group(1))}{unquote(m.group(2))}".rstrip("/").lower()
            s = next((s for s in self.sites.values() if s["webUrl"].lower() == want), None)
            if s is None:
                raise _Refused(404, "itemNotFound", "Requested site could not be found")
            return 200, self._site_json(s), []
        m = re.fullmatch(r"/sites/([^/]+)(/.*)?", path)
        if m:
            s = self.sites.get(unquote(m.group(1)))
            if s is None:
                raise _Refused(404, "itemNotFound", "Requested site could not be found")
            return self._site_route(method, s, m.group(2) or "", q, hdr, data, url)
        if path == "/me/drive":
            if self.me is None:
                raise _Refused(404, "itemNotFound", "no drive")
            return 200, self._drive_json(self.drives[self.me]), []
        m = re.fullmatch(r"/drives/([^/]+)(/.*)?", path)
        if m:
            d = self.drives.get(unquote(m.group(1)))
            if d is None:
                raise _Refused(404, "itemNotFound", "The drive could not be found")
            if not m.group(2):
                return 200, self._drive_json(d), []
            return self._drive_route(method, d, m.group(2), q, hdr, data, raw, url)
        raise _Refused(400, "BadRequest", f"Resource not found for the segment '{path}'.")

    def _site_json(self, s: dict) -> dict:
        return {"id": s["id"], "name": s["name"], "displayName": s["displayName"], "webUrl": s["webUrl"],
                "createdDateTime": "2026-01-01T00:00:00Z", "siteCollection": {"hostname": HOST}}

    def _drive_json(self, d: dict) -> dict:
        return {"id": d["id"], "name": d["name"], "webUrl": d["webUrl"], "driveType": "documentLibrary" if d["site"] else "business"}

    def _paged(self, rows: list, q: dict, url: str):
        top = min(int(q.get("$top", self.page_size)), self.page_size, 999)
        skip = int(q.get("$skiptoken", "0") or 0)
        out = {"value": rows[skip:skip + top]}
        if skip + top < len(rows):
            base = re.sub(r"&\$skiptoken=\d+", "", url)
            out["@odata.nextLink"] = f"{base}&$skiptoken={skip + top}"
        return 200, out, []

    def _site_route(self, method, s, rest, q, hdr, data, url):
        if rest == "" and method == "GET":
            return 200, self._site_json(s), []
        if rest == "/drives" and method == "GET":
            return self._paged([self._drive_json(self.drives[d]) for d in s["drives"]], q, url)
        if rest == "/sites" and method == "GET":
            return self._paged([self._site_json(self.sites[x]) for x in s["subsites"]], q, url)
        if rest == "/lists":
            if method == "POST":
                raise _Refused(403, "accessDenied", "Access denied")
            rows = [{"id": lid, "name": self.lists[lid]["displayName"].replace(" ", ""), "displayName": self.lists[lid]["displayName"],
                     "list": {"hidden": self.lists[lid]["hidden"], "template": self.lists[lid]["template"], "contentTypesEnabled": False}}
                    for lid in s["lists"]]
            rows += [{"id": _guid(9000 + i).lower(), "name": d, "displayName": self.drives[d]["name"],
                      "list": {"hidden": False, "template": "documentLibrary", "contentTypesEnabled": False}} for i, d in enumerate(s["drives"])]
            return self._paged(rows, q, url)
        m = re.fullmatch(r"/lists/([^/]+)(/.*)?", rest)
        if not m or self.lists.get(m.group(1), {}).get("site") != s["id"]:
            raise _Refused(404, "itemNotFound", "The specified list was not found")
        lst = self.lists[m.group(1)]
        sub = m.group(2) or ""
        if sub == "/columns" and method == "GET":
            return self._paged([dict(c) for c in lst["columns"]], q, url)
        if sub == "/items" and method == "GET":
            return self._paged([self._list_item_json(lst, i, q) for i in sorted(lst["items"], key=int)], q, url)
        if sub == "/items" and method == "POST":
            fields = (data or {}).get("fields") or {}
            self._check_fields(lst, fields)
            iid = self.add_item(lst["id"], {k: v for k, v in fields.items() if "@" not in k})
            self.tick()
            return 201, self._list_item_json(lst, iid, {"expand": "fields"}), []
        m = re.fullmatch(r"/items/(\d+)(/fields)?", sub)
        if not m or m.group(1) not in lst["items"]:
            raise _Refused(404, "itemNotFound", "Item not found")
        iid, it = m.group(1), lst["items"][m.group(1)]
        if method == "GET" and not m.group(2):
            return 200, self._list_item_json(lst, iid, q), []
        self._if_match(hdr, [self._item_etag(it)], "resourceModified")
        if method == "DELETE" and not m.group(2):
            del lst["items"][iid]
            return 204, None, []
        if method == "PATCH" and m.group(2):
            self._check_fields(lst, data or {})
            it["fields"].update({k: v for k, v in (data or {}).items() if "@" not in k})
            self.tick()
            it["e"] += 1
            it["modified"] = it["fields"]["Modified"] = self.stamp()
            it["editor"] = "Topwater Agent"
            return 200, {"@odata.etag": self._item_etag(it), **it["fields"]}, []
        raise _Refused(405, "invalidRequest", "Method not allowed")

    def _check_fields(self, lst: dict, fields: dict) -> None:
        cols = {c["name"]: c for c in lst["columns"]}
        for k in fields:
            if "@" in k:
                continue
            c = cols.get(k)
            if c is None:
                raise _Refused(400, "invalidRequest", f"Field '{k}' is not recognized")
            if c.get("readOnly"):
                raise _Refused(400, "invalidRequest", f"Field '{k}' is read-only")

    def _item_etag(self, it: dict) -> str:
        return f'"{it["guid"]},{it["e"]}"'

    def _list_item_json(self, lst: dict, iid: str, q: dict) -> dict:
        it = lst["items"][iid]
        out = {"@odata.etag": self._item_etag(it), "eTag": self._item_etag(it), "id": iid, "createdDateTime": it["created"],
               "lastModifiedDateTime": it["modified"], "contentType": {"id": "0x0100", "name": "Item"},
               "createdBy": {"user": {"displayName": it["by"]}}, "lastModifiedBy": {"user": {"displayName": it["editor"]}},
               "parentReference": {"id": lst["id"], "siteId": lst["site"]}}
        if "fields" in q.get("expand", ""):
            out["fields"] = {"@odata.etag": self._item_etag(it), **it["fields"]}
        return out

    # ----- drive items -----

    def _children(self, iid: str) -> list[str]:
        return sorted((k for k, v in self.items.items() if v["parent"] == iid), key=lambda k: self.items[k]["name"].lower())

    def _child(self, iid: str, name: str) -> str | None:
        return next((k for k in self._children(iid) if self.items[k]["name"].lower() == name.lower()), None)

    def _path_of(self, iid: str) -> str:
        parts = []
        while self.items[iid]["parent"] is not None:
            parts.append(self.items[iid]["name"])
            iid = self.items[iid]["parent"]
        return "/" + "/".join(reversed(parts))

    def _tags(self, it: dict) -> tuple[str, str | None]:
        return f'"{{{it["guid"]}}},{it["e"]}"', None if it["folder"] else f'"c:{{{it["guid"]}}},{it["c"]}"'

    def _item_json(self, iid: str) -> dict:
        it = self.items[iid]
        e, c = self._tags(it)
        d = self.drives[it["drive"]]
        out = {"id": iid, "name": "root" if it["parent"] is None else it["name"], "eTag": e, "lastModifiedDateTime": it["modified"],
               "createdDateTime": "2026-01-01T00:00:00Z", "size": len(it["content"]) if not it["folder"] else
               sum(len(self.items[k]["content"]) for k in self.items if self._under(k, iid))}
        if it["parent"] is not None:
            parent_path = self._path_of(it["parent"])
            out["parentReference"] = {"driveId": d["id"], "driveType": "documentLibrary", "id": it["parent"],
                                      "path": f"/drives/{d['id']}/root:" + ("" if parent_path == "/" else parent_path)}
        else:
            out["root"] = {}
        if it["folder"]:
            out["folder"] = {"childCount": len(self._children(iid))}
        else:
            out["cTag"] = c
            out["file"] = {"mimeType": "application/octet-stream", "hashes": {"quickXorHash": "AAAAAAAAAAAAAAAAAAAAAAAAAAA="}}
        return out

    def _under(self, k: str, top: str) -> bool:
        while k is not None:
            if k == top:
                return True
            k = self.items[k]["parent"]
        return False

    def _set_content(self, iid: str, data: bytes) -> None:
        it = self.items[iid]
        self.tick()
        it["content"], it["modified"] = bytes(data), self.stamp()
        it["e"] += 1
        it["c"] += 1
        self._touch(it["parent"])

    def _touch(self, iid: str | None) -> None:
        if iid is not None:
            self.items[iid]["e"] += 1  # a folder's eTag changes with what it holds

    def _delete(self, iid: str) -> None:
        for k in [k for k in self.items if self._under(k, iid)]:
            del self.items[k]

    def _if_match(self, hdr: dict, tags: list, code: str) -> None:
        want = hdr.get("if-match")
        if want is not None and want != "*" and want not in tags:
            raise _Refused(412, code, "ETag does not match current item's value")

    @staticmethod
    def _check_name(name: str) -> None:
        if not name or any(c in ILLEGAL for c in name) or name.upper() in RESERVED:
            raise _Refused(400, "invalidRequest", "The provided name cannot contain any illegal characters.")
        if name != name.strip(" "):
            raise _Refused(400, "invalidRequest", "The provided name cannot contain leading, or trailing, spaces.")
        if name.endswith("."):
            raise _Refused(400, "invalidRequest", "The provided name cannot contain any illegal characters.")
        if "_vti_" in name.lower():
            raise _Refused(403, "accessDenied", "Access denied")

    def _resolve(self, d: dict, rest: str) -> tuple[str | None, str, str, str]:
        """(the item or None, its parent's id or None, its name, what follows: "", "/children", "/content", ...)"""
        if rest == "/root" or rest.startswith("/root/"):
            return d["root"], "", "", rest[5:]
        m = re.fullmatch(r"/root:(/[^:]*?):?(/children|/content|/createUploadSession)?", rest)
        if not m:
            raise _Refused(400, "BadRequest", "Bad path")
        parts = [unquote(p) for p in m.group(1).split("/")[1:] if p != ""]
        cur, parent = d["root"], None
        for i, part in enumerate(parts):
            parent = cur
            cur = self._child(cur, part) if cur is not None else None
        return cur, parent or "", parts[-1] if parts else "", m.group(2) or ""

    def _drive_route(self, method, d, rest, q, hdr, data, raw, url):
        iid, parent, name, tail = self._resolve(d, rest)
        if tail == "/children" and method == "GET":
            if iid is None:
                raise _Refused(404, "itemNotFound", "The resource could not be found.")
            return self._paged([self._item_json(k) for k in self._children(iid)], q, url)
        if tail == "/children" and method == "POST":
            if iid is None:
                raise _Refused(404, "itemNotFound", "The resource could not be found.")
            new = (data or {}).get("name", "")
            self._check_name(new)
            if self._child(iid, new) is not None:
                if (data or {}).get("@microsoft.graph.conflictBehavior") == "fail":
                    raise _Refused(409, "nameAlreadyExists", "Name already exists")
            self.tick()
            k = self._new_item(d["id"], iid, new, folder="folder" in (data or {}))
            self._touch(iid)
            return 201, self._item_json(k), []
        if tail == "/content" and method == "GET":
            if iid is None:
                raise _Refused(404, "itemNotFound", "The resource could not be found.")
            return 302, None, [("Location", f"{self.download_host}/_layouts/15/download.aspx?UniqueId={iid}&tempauth=fake.{iid}")]
        if tail == "/content" and method == "PUT":
            if raw is not None and len(raw) > SIMPLE_LIMIT:
                raise _Refused(413, "requestTooLarge", "Request entity too large")
            return self._put(d, iid, parent, name, hdr, q, raw or b"")
        if tail == "/createUploadSession" and method == "POST":
            if iid is not None:
                self._if_match(hdr, list(self._tags(self.items[iid])), "notAllowed")
                if ((data or {}).get("item") or {}).get("@microsoft.graph.conflictBehavior") == "fail":
                    raise _Refused(409, "nameAlreadyExists", "The specified item name already exists.")
            elif not parent:
                raise _Refused(404, "itemNotFound", "The resource could not be found.")
            else:
                self._check_name(name)
            token = f"s{self._next()}"
            self.sessions[token] = {"drive": d["id"], "item": iid, "parent": parent, "name": name, "data": bytearray()}
            return 200, {"uploadUrl": UPLOAD + token, "expirationDateTime": "2026-01-02T00:00:00Z", "nextExpectedRanges": ["0-"]}, []
        if tail:
            raise _Refused(405, "invalidRequest", "Method not allowed")
        if iid is None:
            raise _Refused(404, "itemNotFound", "The resource could not be found.")
        if method == "GET":
            return 200, self._item_json(iid), []
        it = self.items[iid]
        if method == "DELETE":
            if it["parent"] is None:
                raise _Refused(403, "accessDenied", "Cannot delete the root")
            self._if_match(hdr, list(self._tags(it)), "resourceModified")
            self._delete(iid)
            self._touch(it["parent"])
            return 204, None, []
        if method == "PATCH":
            self._if_match(hdr, list(self._tags(it)), "resourceModified")
            new_name = (data or {}).get("name", it["name"])
            new_parent = ((data or {}).get("parentReference") or {}).get("id", it["parent"])
            if new_parent not in self.items or not self.items[new_parent]["folder"] or self.items[new_parent]["drive"] != d["id"]:
                raise _Refused(400, "invalidRequest", "Invalid parent reference")
            if self._under(new_parent, iid):
                raise _Refused(400, "invalidRequest", "Cannot move a folder into itself")
            self._check_name(new_name)
            other = self._child(new_parent, new_name)
            if other is not None and other != iid:
                raise _Refused(409, "nameAlreadyExists", "The specified item name already exists.")
            self.tick()
            self._touch(it["parent"])
            it["name"], it["parent"] = new_name, new_parent
            self._touch(new_parent)
            it["e"] += 1
            return 200, self._item_json(iid), []
        raise _Refused(405, "invalidRequest", "Method not allowed")

    def _put(self, d, iid, parent, name, hdr, q, data):
        if iid is not None:
            if self.items[iid]["folder"]:
                raise _Refused(409, "nameAlreadyExists", "A folder has this name")
            if q.get("@microsoft.graph.conflictBehavior") == "fail":
                raise _Refused(409, "nameAlreadyExists", "The specified item name already exists.")
            self._if_match(hdr, list(self._tags(self.items[iid])), "notAllowed")
            self._set_content(iid, data)
            return 200, self._item_json(iid), []
        if "if-match" in hdr:
            raise _Refused(412, "notAllowed", "ETag does not match current item's value")
        if not parent:
            raise _Refused(404, "itemNotFound", "The resource could not be found.")
        self._check_name(name)
        self.tick()
        k = self._new_item(d["id"], parent, name, content=data)
        self._touch(parent)
        return 201, self._item_json(k), []

    def _upload(self, method: str, token: str, hdr: dict, body: bytes):
        s = self.sessions.get(token)
        if s is None:
            raise _Refused(404, "itemNotFound", "The upload session was not found")
        if method == "DELETE":
            del self.sessions[token]
            return 204, None, []
        m = re.fullmatch(r"bytes (\d+)-(\d+)/(\d+)", hdr.get("content-range", ""))
        if method != "PUT" or not m:
            raise _Refused(400, "invalidRequest", "Bad Content-Range")
        start, end, total = map(int, m.groups())
        if start != len(s["data"]) or end - start + 1 != len(body):
            raise _Refused(416, "invalidRange", "The fragment overlaps or skips")
        if end + 1 < total and len(body) % (320 * 1024):
            raise _Refused(400, "invalidRequest", "A fragment must be a multiple of 320 KiB")
        s["data"] += body
        if end + 1 < total:
            return 202, {"expirationDateTime": "2026-01-02T00:00:00Z", "nextExpectedRanges": [f"{end + 1}-"]}, []
        del self.sessions[token]
        if s["item"] is not None and s["item"] in self.items:
            self._set_content(s["item"], bytes(s["data"]))
            return 200, self._item_json(s["item"]), []
        self.tick()
        k = self._new_item(s["drive"], s["parent"], s["name"], content=bytes(s["data"]))
        return 201, self._item_json(k), []

    def _download(self, method: str, u):
        m = re.search(r"UniqueId=([^&]+)&tempauth=fake\.([^&]+)", u.query)
        if method != "GET" or not m or m.group(1) != m.group(2):
            return 401, b"", []
        it = self.items.get(m.group(1))
        if it is None:
            return 404, b"", []
        return 200, it["content"], [("Content-Type", "application/octet-stream")]
