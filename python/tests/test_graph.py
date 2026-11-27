"""The Microsoft Graph connector (docs/connectors/graph.md): tests/connectors/graph/cases.json against the fake Graph, which
the other ports run too, then hostile answers and limits, then a few checks against the real service that run only with
TOPWATER_LIVE_GRAPH=1 and the settings in ~/.config/topwater/graph.env (python/examples/graph_auth.py).

    python tests/test_graph.py --record    rewrites the cases' `expect` from what this port does (review the diff)
"""
from __future__ import annotations

import asyncio
import json
import os
import sys
import time
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from graph_fake import FakeGraph  # noqa: E402

from topwater import Shell  # noqa: E402
from topwater.connectors import common  # noqa: E402
from topwater.connectors.graph import GraphSource  # noqa: E402
from topwater.mounts import MountConflictError, MountSourceError  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
CASES_FILE = ROOT / "tests" / "connectors" / "graph" / "cases.json"
CASES = json.loads(CASES_FILE.read_text())


def change_line(src: GraphSource, c) -> str:
    line = f"{c.kind} {c.path}" + (f" -> {c.to}" if c.to else "")
    note = src.note(c)
    return line + (f" # {note}" if note else "")


def run_case(case: dict) -> list:
    fake = FakeGraph.from_tree(case.get("tree", CASES["tree"]))
    src = GraphSource("secret-token", site=fake.site_url, transport=fake)
    sh = Shell()
    sh.mount("/mnt/sp", src, case["mode"])
    out = []
    for step in case["steps"]:
        if "run" in step:
            r = sh.run(step["run"])
            out.append({"stdout": r.stdout, "stderr": r.stderr, "exitCode": r.exit_code})
        elif "changes" in step:
            out.append([change_line(src, c) for c in sh.pending_changes("/mnt/sp")])
        elif "apply" in step:
            r = sh.apply_changes("/mnt/sp")
            out.append({"applied": [c.path for c in r.applied], "failed": r.failed and r.failed.path, "conflict": r.conflict,
                        "error": r.error})
        elif "graph" in step:
            out.append(fake.outline().split("\n"))
        elif "edit" in step:
            e = step["edit"]
            fake.edit(fake.drive_named(e["library"], fake.root_site), e["path"], e["content"])
            out.append(None)
        elif "fault" in step:
            f = step["fault"]
            fake.fault(f["method"], f["path"], f["status"], [tuple(h) for h in f.get("headers", [])], f.get("body"), f.get("times", 1))
            out.append(None)
    assert not fake.leaks, fake.leaks
    return out


@pytest.mark.parametrize("case", CASES["cases"], ids=[c["name"] for c in CASES["cases"]])
def test_shared_case(case):
    got = run_case(case)
    for step, actual in zip(case["steps"], got):
        assert actual == step.get("expect"), (step, actual)


# ---------- what only this port checks ----------

def tree(**libs) -> FakeGraph:
    return FakeGraph.from_tree({"site": "S", "libraries": libs or {"Documents": {"a.txt": "a\n"}}})


def mounted(fake: FakeGraph, mode: str = "staged", **kw) -> tuple[Shell, GraphSource]:
    src = GraphSource(kw.pop("token", "secret-token"), site=fake.site_url, transport=fake, **kw)
    sh = Shell()
    sh.mount("/mnt/sp", src, mode)
    return sh, src


def test_children_are_paged_and_a_next_page_elsewhere_is_not_followed():
    fake = tree(Documents={f"f{i:02}.txt": str(i) for i in range(23)})
    fake.page_size = 5
    sh, _ = mounted(fake, "read-only")
    assert sh.run("ls /mnt/sp/Documents | wc -l").stdout == "23\n"
    assert sum(1 for m, p in fake.requests if p.endswith("/children")) == 5

    class Evil(FakeGraph):
        def _paged(self, rows, q, url):
            status, out, extra = super()._paged(rows, q, url)
            if "@odata.nextLink" in out:
                out["@odata.nextLink"] = "https://evil.example.com/steal?x=1"
            return status, out, extra
    evil = Evil.from_tree({"site": "S", "libraries": {"Documents": {f"f{i}.txt": "x" for i in range(9)}}})
    evil.page_size = 4
    with pytest.raises(ValueError, match="next page on another host; not followed"):
        mounted(evil, "read-only")
    assert evil.leaks == [] and not any("evil" in p for _, p in evil.requests)


def test_a_download_redirect_never_gets_the_token():
    fake = tree()
    fake.download_host = "https://evil.example.com"
    sh, _ = mounted(fake, "read-only")
    assert sh.run("cat /mnt/sp/Documents/a.txt").stdout == "a\n"
    assert fake.leaks == [] and ("GET", "/_layouts/15/download.aspx") in fake.requests

    fake.download_host = "http://plain.example.com"
    sh, _ = mounted(fake, "read-only")
    r = sh.run("cat /mnt/sp/Documents/a.txt")
    assert "isn't https" in r.stderr and r.exit_code == 1


def test_throttling_storm_waits_then_gives_up(monkeypatch):
    slept = []

    async def nap(s):
        slept.append(s)
    monkeypatch.setattr(common.asyncio, "sleep", nap)
    fake = tree()
    src = GraphSource("secret-token", site=fake.site_url, transport=fake)
    fake.fault("GET", "/drives$", 429, [("Retry-After", "3")], times=3)
    assert [e.name for e in asyncio.run(src.list("/", 10))] == ["Documents"]
    assert sum(s > 2.9 for s in slept) >= 3 and max(slept) <= 3.0  # Retry-After holds every request, not only the refused one
    fake.fault("GET", "/children$", 503, times=50)
    with pytest.raises(MountSourceError, match=r"busy \(HTTP 503\); tried 5 times"):
        asyncio.run(src.list("/Documents", 10))
    fake.faults.clear()
    fake.fault("GET", "/children$", 429, [("Retry-After", "3600")])
    with pytest.raises(MountSourceError, match="asked to wait 3600 seconds"):
        asyncio.run(src.list("/Documents", 10))


def test_a_post_is_not_retried_after_a_gateway_timeout(monkeypatch):
    async def nap(s):
        pass
    monkeypatch.setattr(common.asyncio, "sleep", nap)
    fake = tree()
    src = GraphSource("secret-token", site=fake.site_url, transport=fake)
    asyncio.run(src.list("/", 10))
    fake.fault("POST", "/children$", 504)
    with pytest.raises(MountSourceError, match="SharePoint refused"):
        asyncio.run(src.mkdir("/Documents/new"))
    assert sum(1 for m, p in fake.requests if m == "POST") == 1


def test_malformed_answers_are_errors_not_crashes():
    fake = tree()
    src = GraphSource("secret-token", site=fake.site_url, transport=fake)
    asyncio.run(src.list("/", 10))
    for body in ("not json", "[1, 2]", '{"value": "nope"}', '{"value": [{"name": 5}, {"name": "x", "file": {}, "size": -3}]}'):
        fake.fault("GET", "/children$", 200, body=body)
        try:
            got = asyncio.run(src.list("/Documents", 10))
        except MountSourceError as e:
            assert "isn't a JSON object" in str(e)
        else:
            assert all(isinstance(e.name, str) and (e.size is None or e.size >= 0) for e in got)
    fake.fault("GET", "/drives$", 200, body='{"value": [{"id": "../../me", "name": "Evil"}, {"id": "b!x", "name": "a/b"}]}')
    names = asyncio.run(src.list("/", 10))
    assert [e.name for e in names] == ["a∕b"]


def test_illegal_names_and_long_paths_are_refused_before_a_request():
    fake = tree()
    src = GraphSource("secret-token", site=fake.site_url, transport=fake)
    asyncio.run(src.list("/Documents", 10))
    for bad in ('a"b', "a*b", "a:b", "a<b", "a>b", "a?b", "a\\b", "a|b", " lead", "trail ", "trail.", "CON", "nul", ".lock",
                "desktop.ini", "x_vti_y", "~$lock.docx"):
        n = len(fake.requests)
        with pytest.raises(MountSourceError, match="doesn't allow this name"):
            asyncio.run(src.write(f"/Documents/{bad}", b"x", None))
        assert all(m == "GET" for m, _ in fake.requests[n:]), bad
    with pytest.raises(MountSourceError, match="doesn't allow"):
        asyncio.run(src.mkdir("/Documents/forms"))
    asyncio.run(src.mkdir("/Documents/deep"))
    with pytest.raises(MountSourceError, match="too long"):
        asyncio.run(src.write("/Documents/deep/" + "x" * 390, b"x", None))
    assert asyncio.run(src.write("/Documents/a#b%c.txt", b"ok", None))


def test_an_item_deleted_between_list_and_read():
    fake = tree()
    sh, _ = mounted(fake, "read-only")
    fake.remove(fake.drive_named("Documents"), "a.txt")
    r = sh.run("cat /mnt/sp/Documents/a.txt")
    assert r.exit_code == 1 and "deleted in SharePoint since it was listed" in r.stderr, r.stderr


def test_partial_apply_reports_where_it_stopped(monkeypatch):
    async def nap(s):
        pass
    monkeypatch.setattr(common.asyncio, "sleep", nap)
    fake = tree()
    sh, _ = mounted(fake)
    sh.run("cd /mnt/sp/Documents && echo 1 > one.txt && echo 2 > two.txt && echo 3 > three.txt")
    fake.fault("PUT", "three.txt:/content$", 500)
    fake.fault("PUT", "two.txt:/content$", 423, times=1)
    r = sh.apply_changes("/mnt/sp")
    assert [c.path for c in r.applied] == ["/Documents/one.txt"]
    assert r.failed.path == "/Documents/three.txt" and [c.path for c in r.not_tried] == ["/Documents/two.txt"]
    r = sh.apply_changes("/mnt/sp")  # the 500 was answered once: three.txt goes now, and two.txt is locked
    assert [c.path for c in r.applied] == ["/Documents/three.txt"] and r.failed.path == "/Documents/two.txt"
    assert not r.conflict and "locked in SharePoint" in r.error
    r = sh.apply_changes("/mnt/sp")
    assert [c.path for c in r.applied] == ["/Documents/two.txt"] and r.failed is None and sh.pending_changes("/mnt/sp") == []


def test_a_conflict_on_rename_and_delete():
    fake = tree(Documents={"a.txt": "a\n", "b.txt": "b\n", "keep/": None})
    sh, _ = mounted(fake)
    sh.run("mv /mnt/sp/Documents/a.txt /mnt/sp/Documents/keep/; rm /mnt/sp/Documents/b.txt")
    d = fake.drive_named("Documents")
    fake.edit(d, "a.txt", "theirs")
    r = sh.apply_changes("/mnt/sp")
    assert r.conflict and r.failed.path == "/Documents/a.txt" and r.applied == []
    sh.discard_changes("/mnt/sp")
    sh.run("rm /mnt/sp/Documents/b.txt")
    fake.edit(d, "b.txt", "theirs")
    r = sh.apply_changes("/mnt/sp")
    assert r.conflict and fake.find(d, "b.txt") is not None


def test_a_folder_someone_added_to_is_not_deleted():
    fake = tree(Documents={"old/x.txt": "x"})
    sh, _ = mounted(fake)
    sh.run("rm -r /mnt/sp/Documents/old")
    fake.add(fake.drive_named("Documents"), "old/new.txt", "new")
    r = sh.apply_changes("/mnt/sp")
    assert r.failed.path == "/Documents/old" and "isn't empty" in r.error
    assert fake.find(fake.drive_named("Documents"), "old/new.txt") is not None


def test_a_large_file_goes_through_an_upload_session():
    fake = tree()
    src = GraphSource("secret-token", site=fake.site_url, transport=fake)
    big = bytes(range(256)) * (5 * 4096 + 7)  # a little over 5 MB, not a multiple of the chunk
    v = asyncio.run(src.write("/Documents/big.bin", big, None))
    d = fake.drive_named("Documents")
    assert fake.items[fake.find(d, "big.bin")]["content"] == big and v.startswith('"c:')
    assert sum(1 for m, p in fake.requests if p.startswith("/up/")) == 2 and fake.leaks == []
    with pytest.raises(MountConflictError):
        asyncio.run(src.write("/Documents/big.bin", big + b"!", '"c:{stale},1"'))
    v2 = asyncio.run(src.write("/Documents/big.bin", big + b"!", v))
    assert fake.items[fake.find(d, "big.bin")]["content"] == big + b"!" and v2 != v
    assert asyncio.run(src.read("/Documents/big.bin", 10_000_000)) == big + b"!"
    with pytest.raises(MountSourceError, match="too large"):
        asyncio.run(src.read("/Documents/big.bin", 1000))


def test_a_rename_keeps_the_item_and_a_parent_rename_keeps_the_versions():
    fake = tree(Documents={"p/a.txt": "a"})
    src = GraphSource("secret-token", site=fake.site_url, transport=fake)
    d = fake.drive_named("Documents")
    iid = fake.find(d, "p/a.txt")
    before = asyncio.run(src.stat("/Documents/p/a.txt")).version
    asyncio.run(src.rename("/Documents/p", "/Documents/q", None))
    assert asyncio.run(src.stat("/Documents/q/a.txt")).version == before
    asyncio.run(src.mkdir("/Documents/r"))
    asyncio.run(src.rename("/Documents/q/a.txt", "/Documents/r/A.txt", before))
    assert fake.find(d, "r/A.txt") == iid
    assert asyncio.run(src.stat("/Documents/r/a.txt")) is None  # another case is another name here


def test_subsites_nest_and_are_bounded(monkeypatch):
    from topwater.connectors import graph
    monkeypatch.setattr(graph, "MAX_SITE_DEPTH", 3)
    node: dict = {"libraries": {"Documents": {"leaf.txt": "leaf\n"}}}
    for i in range(6, 0, -1):
        node = {"libraries": {"Documents": {}}, "subsites": {f"L{i}": node}}
    fake = FakeGraph.from_tree({"site": "Top", **node})
    sh, _ = mounted(fake, "read-only")
    r = sh.run("find /mnt/sp -maxdepth 8 | sort")
    assert "/mnt/sp/L1/L2/L3/Documents" in r.stdout and "/mnt/sp/L1/L2/L3/L4" not in r.stdout, r.stdout
    sh2, src = mounted(fake, "staged")
    for op in (lambda: src.mkdir("/L1/New"), lambda: src.rename("/L1", "/L9", None), lambda: src.delete("/L1", ""),
               lambda: src.write("/L1/x.txt", b"x", None), lambda: src.mkdir("/Library2")):
        with pytest.raises(MountSourceError, match="(aren't|a site holds)"):
            asyncio.run(op())


def test_a_folder_moves_to_another_site_by_copy_and_delete():
    fake = FakeGraph.from_tree({"site": "S", "libraries": {"Documents": {"Designs/a.txt": "a", "Designs/sub/b.txt": "b"}},
                                "subsites": {"T": {"libraries": {"Shared": {}}}}})
    sh, src = mounted(fake)
    sh.run("mv /mnt/sp/Documents/Designs /mnt/sp/T/Shared/")
    [c] = sh.pending_changes("/mnt/sp")
    assert src.note(c).startswith("copy + delete")
    r = sh.apply_changes("/mnt/sp")
    assert r.failed is None, r.error
    assert fake.outline() == "/T/Shared/Designs/\n/T/Shared/Designs/a.txt ('a')\n/T/Shared/Designs/sub/\n/T/Shared/Designs/sub/b.txt ('b')\n"


def test_list_items_move_between_lists_and_new_items_skip_what_sharepoint_sets():
    fake = FakeGraph.from_tree({"site": "S", "lists": {"A": {"columns": {"Status": ["x", "y"]}, "items": [{"Title": "one", "Status": "y"}]},
                                                       "B": {"columns": {"Status": ["x", "y"]}, "items": []}}})
    sh, src = mounted(fake)
    sh.run("cp /mnt/sp/A/one.md /mnt/sp/A/two.md && mv /mnt/sp/A/one.md /mnt/sp/B/")
    notes = [src.note(c) for c in sh.pending_changes("/mnt/sp")]
    r = sh.apply_changes("/mnt/sp")
    assert r.failed is None, r.error
    assert any(n and n.startswith("copy + delete") for n in notes)
    assert fake.outline() == '/A#2 \'two\' {"Status": "y"}\n/B#1 \'one\' {"Status": "y"}\n'


def test_the_token_function_is_asked_every_time_and_a_refusal_is_clear():
    fake = tree()
    calls = []

    def token():
        calls.append(1)
        return "secret-token"
    src = GraphSource(token, site=fake.site_url, transport=fake)
    asyncio.run(src.list("/Documents", 10))
    assert len(calls) == len(fake.requests)
    with pytest.raises(MountSourceError, match="HTTP 401"):
        asyncio.run(GraphSource("wrong", site=fake.site_url, transport=fake).list("/", 10))


def test_a_drive_alone():
    fake = FakeGraph()
    d = fake.add_drive(None, "OneDrive")
    fake.add(d, "Notes/plan.md", "# plan\n")
    src = GraphSource("secret-token", drive="me", transport=fake)
    sh = Shell()
    sh.mount("/mnt/od", src, "staged")
    assert sh.run("cat /mnt/od/Notes/plan.md").stdout == "# plan\n"
    sh.run("echo more >> /mnt/od/Notes/plan.md")
    assert sh.apply_changes("/mnt/od").failed is None
    assert fake.items[fake.find(d, "Notes/plan.md")]["content"] == b"# plan\nmore\n"


def test_deep_trees_stop_at_the_mount_depth():
    fake = tree(Documents={"/".join(f"d{i}" for i in range(40)) + "/leaf.txt": "x"})
    sh, _ = mounted(fake, "read-only")
    r = sh.run("find /mnt/sp/Documents | wc -l")
    assert int(r.stdout) <= 40


def test_refusals_reach_any_writer_through_the_mount():
    """The connector refuses in its own operations (write, mkdir, rename, delete), so a refusal reaches whatever wrote the
    file: a shell command, a host program registered as a command (here a Python function), or the host's apply call."""
    fake = FakeGraph.from_tree(CASES["tree"])
    sh, _ = mounted(fake, "live")

    def writer(ctx):  # stands in for a python3 program: it writes through the file system, not through bash
        sh.fs.write_text("/mnt/sp/Documents/a:b.txt", "x\n")
        sh.fs.write_text("/mnt/sp/Project Tracker/Pricing page.md", sh.fs.read_text("/mnt/sp/Project Tracker/Pricing page.md")
                         .replace('Status: "In progress"', 'Status: "Someday"'))
        return 0
    sh.register_command("writer", writer)
    sh.run("cat '/mnt/sp/Project Tracker/Pricing page.md' >/dev/null")
    r = sh.run("writer")
    assert "mount: /mnt/sp/Documents/a:b.txt: /Documents/a:b.txt: SharePoint doesn't allow this name" in r.stderr, r.stderr
    assert "Status: expected one of Not started, In progress, Done, Blocked; the change stays pending" in r.stderr, r.stderr
    assert fake.find(fake.drive_named("Documents"), "a:b.txt") is None and '"Someday"' not in fake.outline()

    staged, _ = mounted(FakeGraph.from_tree(CASES["tree"]))
    staged.run("echo x > '/mnt/sp/Documents/CON'")
    res = staged.apply_changes("/mnt/sp")
    assert res.failed.path == "/Documents/CON" and "doesn't allow this name" in res.error


def test_office_documents_are_their_bytes():
    from test_documents import make_docx  # noqa: E402
    fake = tree(Documents={"brief.docx": make_docx()})
    sh, _ = mounted(fake, "read-only")
    r = sh.run("to-markdown /mnt/sp/Documents/brief.docx")
    assert "Annual Report" in r.stdout, r.stderr


# ----- the real service (opt-in) -----

def _live():
    if os.environ.get("TOPWATER_LIVE_GRAPH") != "1":
        return None
    sys.path.insert(0, str(ROOT / "python" / "examples"))
    import graph_auth
    s, p = graph_auth.settings(), graph_auth.provider()
    return (s, p) if s and p else None


LIVE = _live()
live = pytest.mark.skipif(LIVE is None, reason="set TOPWATER_LIVE_GRAPH=1 and sign in (python/examples/graph_auth.py) to run against the real service")


@pytest.fixture
def scratch():
    """A scratch folder in the site's Documents library, deleted for good afterwards."""
    settings, token = LIVE
    src = GraphSource(token, site=settings["GRAPH_SITE_URL"])
    name = f"zz-topwater-test-{int(time.time())}"
    asyncio.run(src.mkdir(f"/Documents/{name}"))
    yield src, name

    async def purge():
        loc = await src._where(f"/Documents/{name}")
        await src._call("POST", src._dp(loc, tail="/permanentDelete"))
    asyncio.run(purge())


@live
def test_live_files_round_trip(scratch):
    src, name = scratch
    sh = Shell()
    sh.max_call_duration = 300
    sh.mount("/mnt/sp", src, "staged")
    r = sh.run(f"cd '/mnt/sp/Documents/{name}' && echo hello > a.txt && mkdir sub && head -c 5000000 /dev/zero | tr '\\0' x > sub/big.txt && mount -c")
    assert r.exit_code == 0, r.stderr
    res = sh.apply_changes("/mnt/sp")
    assert res.failed is None, res.error
    r = sh.run(f"cd '/mnt/sp/Documents/{name}' && mv a.txt sub/b.txt && echo again >> sub/b.txt && rm sub/big.txt")
    res = sh.apply_changes("/mnt/sp")
    assert res.failed is None, res.error
    sh.refresh_mount("/mnt/sp")
    r = sh.run(f"cd '/mnt/sp/Documents/{name}' && find . | sort && cat sub/b.txt")
    assert r.stdout == ".\n./sub\n./sub/b.txt\nhello\nagain\n", r.stdout + r.stderr
    st = asyncio.run(src.stat(f"/Documents/{name}/sub/b.txt"))
    with pytest.raises(MountConflictError):
        asyncio.run(src.write(f"/Documents/{name}/sub/b.txt", b"x", '"c:{00000000-0000-0000-0000-000000000000},1"'))
    with pytest.raises(MountSourceError, match="doesn't allow"):
        asyncio.run(src.write(f"/Documents/{name}/CON", b"x", None))
    assert st.version.startswith('"c:')


@pytest.fixture
def scratch_items():
    """Scratch items in Project Tracker (their titles start with "zz topwater"), deleted afterwards."""
    settings, token = LIVE
    src = GraphSource(token, site=settings["GRAPH_SITE_URL"])
    stamp = int(time.time())

    async def make():
        loc = await src._where("/Project Tracker")
        for t in (f"zz topwater {stamp} a", f"zz topwater {stamp} b"):
            await src._call("POST", f"/sites/{loc.site}/lists/{loc.list}/items", {"fields": {"Title": t, "Status": "Not started", "Budget": 10}})
        return loc
    loc = asyncio.run(make())
    yield src, stamp

    async def purge():
        for it in await src._pages(f"/sites/{loc.site}/lists/{loc.list}/items?expand=fields", 5000):
            if str((it.get("fields") or {}).get("Title", "")).startswith("zz topwater"):
                await src._call("DELETE", f"/sites/{loc.site}/lists/{loc.list}/items/{it['id']}")
    asyncio.run(purge())


@live
def test_live_list_items(scratch_items):
    src, stamp = scratch_items
    sh = Shell()
    sh.max_call_duration = 120
    sh.mount("/mnt/sp", src, "staged")
    a, b = f"zz topwater {stamp} a.md", f"zz topwater {stamp} b.md"
    r = sh.run(f"cd '/mnt/sp/Project Tracker' && ls && cat '{a}'")
    assert "Status page.md" in r.stdout and 'Status: "Not started"' in r.stdout and "# read-only: " in r.stdout, r.stdout + r.stderr
    sh.run(f"cd '/mnt/sp/Project Tracker' && sed -i 's/^Status: .*/Status: \"In progress\"/; s/^Budget: .*/Budget: 25.5/' '{a}' "
           f"&& rm '{b}' && printf -- '---\\nStatus: \"Done\"\\nDueDate: \"2027-02-01\"\\n---\\n' > 'zz topwater {stamp} c.md'")
    res = sh.apply_changes("/mnt/sp")
    assert res.failed is None, res.error
    sh.refresh_mount("/mnt/sp")
    r = sh.run(f"cd '/mnt/sp/Project Tracker' && ls | grep 'zz topwater' && cat '{a}' 'zz topwater {stamp} c.md'")
    assert f"zz topwater {stamp} b.md" not in r.stdout and 'Status: "In progress"' in r.stdout and "Budget: 25.5" in r.stdout
    assert 'Status: "Done"' in r.stdout and "DueDate: \"2027-02-01" in r.stdout, r.stdout
    st = asyncio.run(src.stat(f"/Project Tracker/{a}"))
    text = asyncio.run(src.read(f"/Project Tracker/{a}", 100_000)).replace(b'"In progress"', b'"Blocked"')
    stale = st.version.rsplit(",", 1)[0] + ',1"'
    assert stale != st.version
    with pytest.raises(MountConflictError):  # SharePoint checks If-Match on an item's fields
        asyncio.run(src.write(f"/Project Tracker/{a}", text, stale))
    sh.run(f"cd '/mnt/sp/Project Tracker' && sed -i 's/^ID: .*/ID: 999/' '{a}'")
    res = sh.apply_changes("/mnt/sp")
    assert "ID can't be changed (set by SharePoint)" in res.error


@live
def test_live_subsites_show_their_libraries_and_lists():
    settings, token = LIVE
    sh = Shell()
    sh.max_call_duration = 120
    sh.mount("/mnt/sp", GraphSource(token, site=settings["GRAPH_SITE_URL"]), "read-only")
    r = sh.run("ls /mnt/sp; ls /mnt/sp/*/ | head -20")
    subs = [n for n in r.stdout.split("\n") if n == "Marketing"]
    if subs:
        r = sh.run("find /mnt/sp/Marketing | sort; cat '/mnt/sp/Marketing/Campaign Calendar/'*.md | head -20")
        assert "/mnt/sp/Marketing/Documents/Campaigns" in r.stdout and "Channel:" in r.stdout, r.stdout + r.stderr


@live
def test_live_office_documents_convert():
    settings, token = LIVE
    sh = Shell()
    sh.max_call_duration = 120
    sh.mount("/mnt/sp", GraphSource(token, site=settings["GRAPH_SITE_URL"]), "read-only")
    for f in ("Projects/Website Redesign - brief.docx", "Archive/old-budget-2024.xlsx"):
        r = sh.run(f"cd /mnt/sp/Documents && to-markdown '{f}' | head -5")
        assert r.exit_code == 0 and r.stdout.strip(), (f, r.stderr)
    r = sh.run("head -c 5 '/mnt/sp/Documents/Archive/2023 Annual Report.pdf'")  # to-markdown has no PDF: the bytes are real
    assert r.stdout == "%PDF-", r.stderr


@live
def test_live_answers_have_the_shape_the_fake_gives(scratch):
    """The fields the connector reads from an item, a listing and a site are where the fake puts them."""
    src, name = scratch
    fake = tree()
    fsrc = GraphSource("secret-token", site=fake.site_url, transport=fake)

    async def shapes(s, folder):
        loc = await s._where(f"/Documents/{folder}")
        await s.write(f"/Documents/{folder}/shape.txt", b"x", None)
        item = await s._call("GET", s._dp(loc, loc.rel + "/shape.txt"))
        kids = await s._call("GET", s._dp(loc, tail="/children"))
        site = await s._call("GET", f"/sites/{loc.site}")
        drives = await s._call("GET", f"/sites/{loc.site}/drives")
        return item, kids, site, drives
    asyncio.run(fsrc.mkdir("/Documents/f"))
    real, faked = asyncio.run(shapes(src, name)), asyncio.run(shapes(fsrc, "f"))
    for r, f in zip(real, faked):
        r = r["value"][0] if "value" in r else r
        f = f["value"][0] if "value" in f else f
        for key in ("id", "name", "eTag", "cTag", "size", "lastModifiedDateTime", "file", "folder", "webUrl"):
            assert (key in r) == (key in f) or key not in ("id", "name", "eTag", "cTag", "size", "file", "folder"), (key, r.keys(), f.keys())


if __name__ == "__main__" and sys.argv[1:] == ["--record"]:
    for case in CASES["cases"]:
        for step, actual in zip(case["steps"], run_case(case)):
            if actual is not None:
                step["expect"] = actual
    CASES_FILE.write_text(json.dumps(CASES, indent=1, ensure_ascii=False) + "\n")
