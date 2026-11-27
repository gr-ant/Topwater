"""A stateful stand-in for the Notion API, as a webaccess transport for NotionSource: pages with Markdown text, child pages
and databases, rows with properties, move, trash, pagination, minute-granular last_edited_time, and faults to inject.
It answers like the real API did when it was checked (docs/connectors/notion-design.md, "The fake"); the C# and
TypeScript ports keep one that behaves the same, and tests/connectors/notion/cases.json runs against each.

    fake = FakeNotion.from_tree({"title": "Root", "text": "hello", "children": [{"title": "Child"}]})
    source = NotionSource("secret", fake.root, transport=fake, rate=1000)
"""
from __future__ import annotations

import json
import re
import threading
from datetime import datetime, timedelta
from urllib.parse import parse_qs, urlsplit

TAG = re.compile(r'^<(page|database)\s[^>\n]*?url="([^"]*)"[^>\n]*>\n?(.*?)\n?</\1>', re.S | re.M)


def _hex(i: str) -> str:
    return i.replace("-", "")


def _rich(s: str) -> list:
    return [{"type": "text", "text": {"content": s, "link": None}, "plain_text": s, "href": None}]


def _plain(rich) -> str:
    return "".join((r.get("text") or {}).get("content", r.get("plain_text", "")) for r in rich or [])


class FakeNotion:
    def __init__(self, token: str = "secret-token"):
        self.token = token
        self.clock = datetime(2026, 1, 1)
        self.pages: dict[str, dict] = {}
        self.dbs: dict[str, dict] = {}
        self.sources: dict[str, dict] = {}
        self.requests: list[tuple[str, str]] = []
        self.faults: list[dict] = []
        self.page_size = 100  # the most a list answer holds
        self.root = ""
        self._n = 0
        self._lock = threading.Lock()

    # ----- building and inspecting -----

    def _id(self) -> str:
        self._n += 1
        return f"00000000-0000-4000-8000-{self._n:012x}"

    def stamp(self) -> str:
        return self.clock.strftime("%Y-%m-%dT%H:%M:00.000Z")  # Notion's last_edited_time is to the minute

    def tick(self, seconds: float = 60) -> None:
        self.clock += timedelta(seconds=seconds)

    def add_page(self, parent: str | None, title: str, text: str = "", props: dict | None = None) -> str:
        """A page under a page (or a row in a data source, parent "source:ID"); None: a top page."""
        pid = self._id()
        kind, ref = ("source", parent[7:]) if parent and parent.startswith("source:") else ("page", parent)
        self.pages[pid] = {"id": pid, "title": title, "parent": (kind, ref) if ref else ("workspace", None), "trash": False,
                           "created": self.stamp(), "edited": self.stamp(), "content": [("text", text)] if text else [], "props": props or {}}
        if kind == "page" and ref:
            self.pages[ref]["content"].append(("child", pid))
        return pid

    def add_database(self, parent: str, title: str, schema: dict, title_property: str = "Name") -> tuple[str, str]:
        """A database under a page with one data source; schema: property name -> type. Returns (database, data source)."""
        did, sid = self._id(), self._id()
        props = {title_property: {"id": "title", "name": title_property, "type": "title", "title": {}}}
        for i, (name, t) in enumerate(schema.items()):
            props[name] = {"id": f"p{i}", "name": name, "type": t, t: {}}
        self.dbs[did] = {"id": did, "title": title, "parent": parent, "trash": False, "created": self.stamp(), "edited": self.stamp(), "sources": [sid]}
        self.sources[sid] = {"id": sid, "name": title, "db": did, "schema": props}
        self.pages[parent]["content"].append(("child", did))
        return did, sid

    @classmethod
    def from_tree(cls, tree: dict, token: str = "secret-token") -> "FakeNotion":
        """The format of tests/connectors/notion/cases.json: {"title", "text", "children": [...]} for a page; {"database",
        "schema": {name: type}, "rows": [{"title", "props": {name: value}, "text"}]} for a database."""
        fake = cls(token)

        def build(node: dict, parent: str | None) -> None:
            if "database" in node:
                _, sid = fake.add_database(parent, node["database"], node.get("schema", {}), node.get("titleProperty", "Name"))
                schema = fake.sources[sid]["schema"]
                for r in node.get("rows", []):
                    props = {k: fake._prop(schema[k]["type"], v) for k, v in r.get("props", {}).items()}
                    fake.add_page("source:" + sid, r["title"], r.get("text", ""), props)
                return
            pid = fake.add_page(parent, node["title"], node.get("text", ""))
            fake.root = fake.root or pid
            for c in node.get("children", []):
                build(c, pid)
        build(tree, None)
        return fake

    @staticmethod
    def _prop(t: str, v) -> dict:
        """A property as the API returns it, from a simple value (or the value a request sent)."""
        if t == "rich_text":
            v = _rich(v) if isinstance(v, str) else [r | {"plain_text": (r.get("text") or {}).get("content", "")} for r in v or []]
        elif t in ("select", "status"):
            v = {"name": v} if isinstance(v, str) else v
        elif t == "multi_select":
            v = [{"name": x} if isinstance(x, str) else x for x in v or []]
        elif t == "date":
            v = {"start": v, "end": None, "time_zone": None} if isinstance(v, str) else v
        return {"type": t, t: v}

    def find(self, title: str) -> str:
        hits = [i for i, p in self.pages.items() if p["title"] == title and not p["trash"]] + \
               [i for i, d in self.dbs.items() if d["title"] == title and not d["trash"]]
        assert len(hits) == 1, (title, hits)
        return hits[0]

    def edit(self, pid: str, text: str) -> None:
        """Someone changes a page's text in Notion."""
        p = self.pages[pid]
        p["content"] = [c for c in p["content"] if c[0] == "child"]
        if text:
            p["content"].insert(0, ("text", text))
        p["edited"] = self.stamp()

    def fault(self, method: str, path: str, status: int, headers: list | None = None, body: bytes | str | None = None, times: int = 1) -> None:
        """The next `times` requests whose method matches and whose path matches the regex `path` get this answer."""
        self.faults.append({"method": method, "path": re.compile(path), "status": status, "headers": headers or [],
                            "body": body.encode() if isinstance(body, str) else body if body is not None else b'{"object":"error"}', "times": times})

    def outline(self) -> str:
        """The tree as text: a line per page or database (two spaces a level), with its text and a row's properties."""
        out: list[str] = []

        def page(pid: str, depth: int) -> None:
            p = self.pages[pid]
            props = {k: _simple(v) for k, v in sorted(p["props"].items())}
            text = self._text(p)
            out.append("  " * depth + p["title"] + (" " + json.dumps(props, ensure_ascii=False) if props else "") + (": " + json.dumps(text, ensure_ascii=False) if text else ""))
            for kind, ref in p["content"]:
                if kind == "child" and ref in self.pages and not self.pages[ref]["trash"]:
                    page(ref, depth + 1)
                elif kind == "child" and ref in self.dbs and not self.dbs[ref]["trash"]:
                    out.append("  " * (depth + 1) + self.dbs[ref]["title"] + " [database]")
                    for rid in self._rows(self.dbs[ref]["sources"][0]):
                        page(rid, depth + 2)
        page(self.root, 0)
        return "\n".join(out) + "\n"

    def _text(self, p: dict) -> str:
        return "\n\n".join(t for k, t in p["content"] if k == "text")

    # ----- the API -----

    def __call__(self, method, url, headers, body, timeout, limit):
        with self._lock:
            u = urlsplit(url)
            self.requests.append((method, u.path))
            for f in self.faults:
                if f["times"] > 0 and f["method"] == method and f["path"].search(u.path):
                    f["times"] -= 1
                    return f["status"], "Fault", f["headers"], f["body"]
            h = {k.lower(): v for k, v in headers}
            if h.get("authorization") != f"Bearer {self.token}":
                return self._error(401, "unauthorized", "API token is invalid.")
            if not h.get("notion-version"):
                return self._error(400, "missing_version", "Notion-Version header failed validation.")
            try:
                data = json.loads(body) if body else {}
                status, answer = self._route(method, u.path, parse_qs(u.query), data)
            except _Refused as e:
                return self._error(e.status, e.code, e.message)
            return status, "OK", [("Content-Type", "application/json")], json.dumps(answer).encode()

    @staticmethod
    def _error(status: int, code: str, message: str):
        return status, "Error", [("Content-Type", "application/json")], json.dumps(
            {"object": "error", "status": status, "code": code, "message": message}).encode()

    def _route(self, method: str, path: str, query: dict, data: dict):
        m = re.fullmatch(r"/v1/(pages|blocks|databases|data_sources)(?:/([0-9a-f-]{32,36}))?(?:/(markdown|move|children|query))?", path)
        if not m:
            raise _Refused(400, "invalid_request_url", "Invalid request URL.")
        what, rid, sub = m.groups()
        rid = self._norm(rid) if rid else None
        if what == "pages" and rid is None and method == "POST":
            return 200, self._create(data)
        if what == "pages" and sub is None:
            p = self._live_page(rid)
            if method == "PATCH":
                self._patch(p, data)
            return 200, self._page_json(p)
        if what == "pages" and sub == "markdown":
            p = self._live_page(rid)
            if method == "PATCH":
                self._replace(p, data)
            return 200, {"object": "page_markdown", "id": p["id"], "markdown": self._markdown(p), "truncated": False, "unknown_block_ids": []}
        if what == "pages" and sub == "move" and method == "POST":
            return 200, self._move(self._live_page(rid), data.get("parent") or {})
        if what == "blocks" and sub == "children" and method == "GET":
            return 200, self._paged(self._blocks(self._live_page(rid)), query)
        if what == "databases" and sub is None:
            d = self.dbs.get(rid)
            if d is None:
                raise _Refused(404, "object_not_found", f"Could not find database with ID: {rid}.")
            if method == "PATCH":
                if "title" in data:
                    d["title"] = _plain(data["title"])
                if "in_trash" in data:
                    d["trash"] = bool(data["in_trash"])
                d["edited"] = self.stamp()
            return 200, self._db_json(d)
        if what == "data_sources" and rid in self.sources:
            s = self.sources[rid]
            if sub == "query" and method == "POST":
                return 200, self._paged([self._page_json(self.pages[r]) for r in self._rows(rid)], {"page_size": [data.get("page_size", 100)], "start_cursor": [data.get("start_cursor", "")]})
            if sub is None and method == "GET":
                return 200, {"object": "data_source", "id": rid, "properties": s["schema"], "parent": {"type": "database_id", "database_id": s["db"]}}
        raise _Refused(404, "object_not_found", f"Could not find {what} with ID: {rid}.")

    @staticmethod
    def _norm(i: str) -> str:
        h = i.replace("-", "")
        return f"{h[:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:]}"

    def _live_page(self, pid: str) -> dict:
        p = self.pages.get(pid)
        if p is None or p["trash"]:
            raise _Refused(404, "object_not_found", f"Could not find page with ID: {pid}. Make sure the relevant pages and databases are shared with your integration.")
        return p

    def _rows(self, sid: str) -> list:
        return sorted((i for i, p in self.pages.items() if p["parent"] == ("source", sid) and not p["trash"]), key=lambda i: (self.pages[i]["created"], i))

    def _parent_json(self, p: dict) -> dict:
        kind, ref = p["parent"]
        if kind == "page":
            return {"type": "page_id", "page_id": ref}
        if kind == "source":
            return {"type": "data_source_id", "data_source_id": ref, "database_id": self.sources[ref]["db"]}
        return {"type": "workspace", "workspace": True}

    def _page_json(self, p: dict) -> dict:
        kind, ref = p["parent"]
        if kind == "source":
            schema = self.sources[ref]["schema"]
            tname = next(k for k, v in schema.items() if v["type"] == "title")
            props = {tname: {"id": "title", "type": "title", "title": _rich(p["title"])}}
            props |= {k: {"id": schema[k]["id"], **v} for k, v in p["props"].items() if k in schema}
        else:
            props = {"title": {"id": "title", "type": "title", "title": _rich(p["title"])}}
        return {"object": "page", "id": p["id"], "created_time": p["created"], "last_edited_time": p["edited"], "in_trash": p["trash"],
                "parent": self._parent_json(p), "properties": props, "url": f"https://app.notion.com/p/{_hex(p['id'])}"}

    def _db_json(self, d: dict) -> dict:
        return {"object": "database", "id": d["id"], "title": _rich(d["title"]), "parent": {"type": "page_id", "page_id": d["parent"]},
                "in_trash": d["trash"], "is_inline": False, "created_time": d["created"], "last_edited_time": d["edited"],
                "data_sources": [{"id": s, "name": self.sources[s]["name"]} for s in d["sources"]]}

    def _children(self, p: dict) -> list:
        return [ref for kind, ref in p["content"] if kind == "child" and not (self.pages.get(ref) or self.dbs.get(ref))["trash"]]

    def _tag(self, ref: str) -> str:
        if ref in self.pages:
            return f'<page url="https://app.notion.com/p/{_hex(ref)}">\n{self.pages[ref]["title"]}\n</page>'
        d = self.dbs[ref]
        return f'<database url="https://app.notion.com/p/{_hex(ref)}" data-source-url="collection://{d["sources"][0]}" inline="false">\n{d["title"]}\n</database>'

    def _markdown(self, p: dict) -> str:
        live = set(self._children(p))
        return "\n\n".join(t if k == "text" else self._tag(t) for k, t in p["content"] if k == "text" or t in live)

    def _blocks(self, p: dict) -> list:
        out = []
        for n, (kind, ref) in enumerate(p["content"]):
            if kind == "text":
                for k, para in enumerate(t for t in ref.split("\n\n") if t):
                    out.append({"object": "block", "id": f"00000000-0000-4000-9{n:03x}-{k:012x}", "type": "paragraph", "has_children": False,
                                "in_trash": False, "created_time": p["created"], "last_edited_time": p["edited"],
                                "paragraph": {"rich_text": _rich(para)}})
            elif ref in self._children(p):
                c = self.pages.get(ref) or self.dbs[ref]
                kind = "child_page" if ref in self.pages else "child_database"
                out.append({"object": "block", "id": ref, "type": kind, kind: {"title": c["title"]}, "in_trash": False,
                            "has_children": bool(c.get("content")), "created_time": c["created"], "last_edited_time": c["edited"],
                            "parent": {"type": "page_id", "page_id": p["id"]}})
        return out

    def _paged(self, items: list, query: dict):
        size = min(int((query.get("page_size") or [100])[0]), self.page_size)
        start = int((query.get("start_cursor") or ["0"])[0] or 0)
        more = start + size < len(items)
        return {"object": "list", "results": items[start:start + size], "has_more": more, "next_cursor": str(start + size) if more else None}

    def _parse(self, p: dict, md: str, allow_deleting: bool) -> list:
        """New content from Markdown as the real API takes it: a child's tag keeps the child where it stands (its text
        renames it), a tag for any other page becomes a mention, and a child left out is refused unless allowed."""
        content, pos, kept = [], 0, set()
        children = self._children(p)
        for m in TAG.finditer(md):
            text = md[pos:m.start()].strip("\n")
            if text:
                content.append(("text", text))
            pos = m.end()
            ref = self._norm(re.findall(r"[0-9a-f]{32}", m.group(2).replace("-", ""))[-1]) if re.search(r"[0-9a-f]{32}", m.group(2).replace("-", "")) else None
            if ref in children:
                c = self.pages.get(ref) or self.dbs[ref]
                if m.group(3).strip() and m.group(3).strip() != c["title"]:
                    c["title"], c["edited"] = m.group(3).strip(), self.stamp()
                content.append(("child", ref))
                kept.add(ref)
            else:
                content.append(("text", f'<mention url="{m.group(2)}">{m.group(3).strip()}</mention>'))
        text = md[pos:].strip("\n")
        if text:
            content.append(("text", text))
        gone = [c for c in children if c not in kept]
        if gone and not allow_deleting:
            raise _Refused(400, "validation_error", f"This operation would delete {len(gone)} child page(s), database(s), or meeting note(s)")
        for c in gone:
            (self.pages.get(c) or self.dbs[c])["trash"] = True
        merged = []  # adjacent text is one chunk
        for kind, v in content:
            if kind == "text" and merged and merged[-1][0] == "text":
                merged[-1] = ("text", merged[-1][1] + "\n\n" + v)
            else:
                merged.append((kind, v))
        return merged

    def _replace(self, p: dict, data: dict) -> None:
        if data.get("type") != "replace_content" or data.get("markdown_version") != "v2":
            raise _Refused(400, "validation_error", "Only replace_content with markdown_version v2 is faked.")
        rc = data.get("replace_content") or {}
        p["content"] = self._parse(p, rc.get("new_str", ""), bool(rc.get("allow_deleting_content")))
        p["edited"] = self.stamp()

    def _parent(self, ref: dict) -> tuple[str, str]:
        if ref.get("type") == "page_id" or "page_id" in ref:
            pid = self._norm(ref.get("page_id", ""))
            self._live_page(pid)
            return "page", pid
        sid = self._norm(ref.get("data_source_id", ""))
        if sid not in self.sources or self.dbs[self.sources[sid]["db"]]["trash"]:
            raise _Refused(404, "object_not_found", f"Could not find data source with ID: {sid}.")
        return "source", sid

    def _set_props(self, p: dict, props: dict) -> None:
        schema = self.sources[p["parent"][1]]["schema"] if p["parent"][0] == "source" else {}
        for name, v in props.items():
            if name == "title" or (schema.get(name) or {}).get("type") == "title":
                p["title"] = _plain(v.get("title"))
                continue
            if name not in schema:
                raise _Refused(400, "validation_error", f"{name} is not a property that exists.")
            t = schema[name]["type"]
            if t in ("select", "status", "multi_select") and v.get(t) is not None:
                options = [o["name"] for o in ([v[t]] if t != "multi_select" else v[t])]
                if any("," in o for o in options if t == "multi_select"):
                    raise _Refused(400, "validation_error", "Option names can't contain commas.")
            p["props"][name] = self._prop(t, v.get(t))

    def _create(self, data: dict) -> dict:
        kind, ref = self._parent(data.get("parent") or {})
        pid = self.add_page(ref if kind == "page" else "source:" + ref, "")
        p = self.pages[pid]
        try:
            self._set_props(p, data.get("properties") or {})
            md = data.get("markdown")
            if md is not None:
                if data.get("markdown_version") != "v2":
                    raise _Refused(400, "validation_error", "markdown_version v2 is the only one faked.")
                first, _, rest = md.lstrip("\n").partition("\n")
                if first.startswith("# "):  # Notion takes a first heading as the title, and drops it from the text
                    p["title"] = p["title"] or first[2:].strip()
                    md = rest
                p["content"] = self._parse(p, md, False)
        except _Refused:
            del self.pages[pid]
            if kind == "page":
                self.pages[ref]["content"].remove(("child", pid))
            raise
        return self._page_json(p)

    def _patch(self, p: dict, data: dict) -> None:
        if "properties" in data:
            self._set_props(p, data["properties"])
        if "in_trash" in data:
            p["trash"] = bool(data["in_trash"])
        p["edited"] = self.stamp()

    def _move(self, p: dict, ref: dict) -> dict:
        kind, to = self._parent(ref)
        up = to
        while kind == "page" and up:
            if up == p["id"]:
                raise _Refused(400, "validation_error", "Can't move a page into itself or its own subpages.")
            parent = self.pages[up]["parent"]
            up = parent[1] if parent[0] == "page" else None
        old_kind, old = p["parent"]
        if old_kind == "page":
            self.pages[old]["content"].remove(("child", p["id"]))
        if kind == "page":
            self.pages[to]["content"].append(("child", p["id"]))
        if (kind, to) != (old_kind, old) and old_kind == "source":
            p["props"] = {}  # a row that leaves its database keeps only its title
        p["parent"] = (kind, to)
        return self._page_json(p)  # a move leaves last_edited_time as it was, as Notion does


def _simple(v: dict):
    t = v.get("type")
    x = v.get(t)
    if t == "rich_text":
        return _plain(x)
    if t in ("select", "status"):
        return (x or {}).get("name")
    if t == "multi_select":
        return [o["name"] for o in x or []]
    if t == "date":
        return (x or {}).get("start")
    return x


class _Refused(Exception):
    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status, self.code, self.message = status, code, message
