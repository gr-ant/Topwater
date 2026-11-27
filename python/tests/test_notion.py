"""The Notion connector (docs/connectors/notion.md): tests/connectors/notion/cases.json, which the C# and TypeScript ports
will run against their own fakes, then what only this port checks (hostile answers, limits), then tests against the real
API that run only with TOPWATER_LIVE_NOTION=1 and a token (NOTION_TOKEN, or NOTION_TOKEN= in
~/.config/topwater/notion.env) for a sandbox page (NOTION_ROOT; default the project's sandbox). They work in a scratch
page they make under it and move to the trash afterwards."""
import asyncio
import json
import os
import time
from pathlib import Path

import pytest

from notion_fake import FakeNotion
from topwater import Shell
from topwater.connectors import notion as nt
from topwater.connectors import common
from topwater.connectors.common import split_front_matter
from topwater.connectors.notion import NotionSource
from topwater.mounts import MountConflictError, MountSourceError

CASES = json.loads((Path(__file__).resolve().parents[2] / "tests" / "connectors" / "notion" / "cases.json").read_text(encoding="utf-8"))
TOKEN = "secret-token"


def _source(fake: FakeNotion, **kw) -> NotionSource:
    return NotionSource(TOKEN, fake.root, transport=fake, rate=1000, **kw)


def run_case(case: dict, tree: dict) -> list:
    """Runs a case's steps; returns what each step produced, to compare with its `expect`."""
    fake = FakeNotion.from_tree(case.get("tree", tree), TOKEN)
    sh = Shell()
    sh.mount("/mnt/n", _source(fake), case["mode"])
    out = []
    for step in case["steps"]:
        if "run" in step:
            r = sh.run(step["run"])
            out.append({"stdout": r.stdout, "stderr": r.stderr, "exitCode": r.exit_code})
        elif "changes" in step:
            out.append([{k: v for k, v in (("kind", c.kind), ("path", c.path), ("to", c.to), ("version", c.version),
                                            ("content", None if c.content is None else c.content.decode())) if v is not None}
                        for c in sh.pending_changes("/mnt/n")])
        elif "apply" in step:
            r = sh.apply_changes("/mnt/n")
            out.append({"applied": [c.path for c in r.applied], "failed": r.failed.path if r.failed else None,
                        "conflict": r.conflict, "error": r.error})
        elif "notion" in step:
            out.append(fake.outline())
        elif "edit" in step:
            fake.edit(fake.find(step["edit"]["title"]), step["edit"]["text"])
            out.append(None)
        elif "tick" in step:
            fake.tick(step["tick"])
            out.append(None)
        elif "trash" in step:
            fake.pages[fake.find(step["trash"])]["trash"] = True
            out.append(None)
        elif "refresh" in step:
            sh.refresh_mount("/mnt/n")
            out.append(None)
        elif "fault" in step:
            f = step["fault"]
            fake.fault(f["method"], f["path"], f["status"], [tuple(h) for h in f.get("headers", [])], f.get("body"), f.get("times", 1))
            out.append(None)
        else:
            raise AssertionError(f"unknown step {step}")
    return out


@pytest.mark.parametrize("case", CASES["cases"], ids=[c["name"] for c in CASES["cases"]])
def test_shared_case(case):
    got = run_case(case, CASES["tree"])
    for i, (step, actual) in enumerate(zip(case["steps"], got)):
        if "expect" in step:
            assert actual == step["expect"], f"step {i}: {json.dumps(step)[:200]}"


# ----- the pieces -----

ROOT_MD = ('<p />\n\nTEST\n\n<page url="https://app.notion.com/p/425832d4414a81398d0bcd47bef2f7f5">\nProjects\n</page>\n\n'
           '<page url="https://app.notion.com/p/425832d4414a8101ad70d81f39276711">\nWiki\n</page>\n\n'
           '<database url="https://app.notion.com/p/425832d4414a81e3871dff6de2277fde" data-source-url="collection://425832d4-414a-818d-be8b-000bbfa94cea" inline="false">\nMeeting Notes\n</database>')


def test_child_tags_come_out_of_the_text_and_go_back_where_they_were():
    view, tags = nt._split(ROOT_MD)
    assert view == "<p />\n\nTEST" and len(tags) == 3
    assert nt._join(view, tags) == ROOT_MD
    assert nt._join("<p />\n\nTEST\nmore", tags).startswith("<p />\n\nTEST\nmore\n\n<page")  # text added to the block stays in it
    assert nt._join("new start\n\n<p />\n\nTEST\n\nend", tags).endswith("Meeting Notes\n</database>\n\nend")
    assert nt._join("all new", tags).startswith("all new\n\n<page")  # the line is gone: the tags go at the end
    view, tags = nt._split('<page url="https://x/p/' + "a" * 32 + '">\nA\n</page>')
    assert view == "" and nt._join("# Title\n\ntext", tags).startswith("# Title\n\ntext\n\n<page")  # a page without text: after it
    nested = 'Text\n\n<toggle>\nT\n\t<page url="https://x/p/' + "b" * 32 + '">\nB\n</page>\n</toggle>'
    assert nt._split(nested) == (nested, [])  # a page inside a toggle stays in the text


def test_the_text_may_keep_but_not_add_or_change_page_tags():
    tag = '<page url="https://x/p/' + "b" * 32 + '">\nB\n</page>'
    nt._check_tags("/a.md", f"x\n\n\t{tag}\n", f"y\n\n\t{tag}")
    for bad in (tag.replace("B", "Renamed"), '<page url="https://x/p/' + "c" * 32 + '">C</page>', "<database url=\"u\">D</database>"):
        with pytest.raises(MountSourceError, match="folders here"):
            nt._check_tags("/a.md", bad, f"y\n\n\t{tag}")


def test_names_are_safe_unique_and_stable():
    assert nt._name("A/B test") == "A∕B test" and nt._unname("A∕B test") == "A/B test"
    assert nt._name("") == nt._name("  \t") == "Untitled"
    assert nt._name(".") == "(.)" and nt._name("..") == "(..)"
    assert nt._name("a\0b\nc") == "a b c"
    long = nt._name("é" * 300)
    assert len(long.encode()) <= common.NAME_BYTES and long == "é" * 100
    items = [nt._Item("page", f"id{i}", t, d, created=str(i)) for i, (t, d) in enumerate([("Notes", False), ("Notes", False), ("Notes (2)", False), ("Notes", True)])]
    names = nt._names(items, {}, reserve="index.md")
    assert {n: i.id for n, i in names.items()} == {"Notes": "id3", "Notes (2).md": "id2", "Notes (3).md": "id1", "Notes.md": "id0"}
    again = nt._names([items[1], items[2], items[3]], {i.id: (i.title, n) for n, i in names.items()}, None)
    assert {n: i.id for n, i in again.items()} == {"Notes": "id3", "Notes (2).md": "id2", "Notes (3).md": "id1"}  # id0 gone: no renames
    assert nt._names([nt._Item("page", "x", "index", False)], {}, "index.md").keys() == {"index (2).md"}


def test_every_message_is_used_and_filled():
    code = Path(nt.__file__).read_text(encoding="utf-8").split("def _msg", 1)[1]
    assert [k for k in nt.MESSAGES if f'"{k}"' not in code] == []
    assert nt._msg("noRoot") == "the root page is not in Notion, or not shared with the integration"
    shared = Path(common.__file__).read_text(encoding="utf-8").split("def msg", 1)[1]
    assert [k for k in common.MESSAGES if f'"{k}"' not in shared] == []
    assert common.msg("busy", service="Notion", status=429, tries=5) == "Notion is busy (HTTP 429); tried 5 times"


def test_ids_come_from_ids_and_urls_only():
    i = "425832d4-414a-8087-b9a1-f1cb08ff38b1"
    for s in (i, i.replace("-", ""), "https://www.notion.so/Cafe-425832d4414a8087b9a1f1cb08ff38b1?pvs=4", f"https://app.notion.com/p/{i.replace('-', '')}"):
        assert nt._parse_id(s) == i
    for s in ("../../v1/users", "", None, 42, "abc", "x" * 3000):
        assert nt._parse_id(s) is None


def test_front_matter_reads_json_and_the_yaml_an_agent_writes():
    fm, body = split_front_matter("/r.md", '---\nDone: true\nType: Retro\nTags: [a, "b"]\nList:\n  - x\n  - y\n"Odd: name": 3\n# note\nEmpty:\n---\n\nbody\n---\nmore')
    assert fm == {"Done": True, "Type": "Retro", "Tags": ["a", "b"], "List": ["x", "y"], "Odd: name": 3, "Empty": None}
    assert body == "body\n---\nmore"
    assert split_front_matter("/r.md", "no front\n") == (None, "no front\n")
    for bad in ("---\nDone: true\n", "---\n  indented: 1\n---\n", "---\nno colon\n---\n"):
        with pytest.raises(MountSourceError, match="front matter"):
            split_front_matter("/r.md", bad)
    props = {"Name": {"type": "title", "title": []}, "Odd: name": {"type": "number", "number": 3}, "F": {"type": "formula", "formula": {"type": "string", "string": "x"}}}
    assert nt._front(props) == '---\nF: "x"\n"Odd: name": 3\n---\n'


def test_property_values_are_checked():
    schema = {"N": {"type": "number"}, "C": {"type": "checkbox"}, "F": {"type": "formula"}, "S": {"type": "select"}}
    current = {"F": {"type": "formula", "formula": {"type": "number", "number": 2}}}
    assert nt._props_payload("/r.md", {"N": 2, "S": None, "F": 2}, schema, current) == {"N": {"number": 2}, "S": {"select": None}}
    for fm, msg in (({"N": "two"}, "expected a number"), ({"C": "yes please"}, "true or false"), ({"F": 3}, "set by Notion"), ({"X": 1}, "no property X")):
        with pytest.raises(MountSourceError, match=msg):
            nt._props_payload("/r.md", fm, schema, current)


# ----- the source against the fake -----

TREE = CASES["tree"]


def _mounted(mode="staged", tree=TREE, **kw):
    fake = FakeNotion.from_tree(tree, TOKEN)
    sh = Shell()
    src = _source(fake, **kw)
    sh.mount("/mnt/n", src, mode)
    return fake, sh, src


def test_the_token_comes_from_the_provider_for_every_request_and_never_reaches_the_agent():
    fake = FakeNotion.from_tree(TREE, "rotating-1")
    calls, current = [], ["rotating-1"]

    async def provider():
        calls.append(1)
        return current[0]
    sh = Shell()
    sh.mount("/mnt/n", NotionSource(provider, fake.root, transport=fake, rate=1000), "staged")
    n = len(calls)
    assert n == len(fake.requests)
    fake.token = current[0] = "rotating-2"  # the host refreshed it
    r = sh.run("cat /mnt/n/Wiki/Onboarding.md; grep -r rotating /mnt/n; env; mount")
    assert "Welcome" in r.stdout and "rotating" not in r.stdout + r.stderr and len(calls) > n
    fake.token = "something else"
    r = sh.run("mount -r /mnt/n; ls /mnt/n")
    assert "Notion refused the integration's token (HTTP 401)" in r.stderr and "rotating" not in r.stderr


def test_a_bad_root_fails_when_mounting():
    fake = FakeNotion.from_tree(TREE, TOKEN)
    with pytest.raises(ValueError, match="not in Notion, or not shared"):
        Shell().mount("/mnt/n", NotionSource(TOKEN, "f" * 32, transport=fake, rate=1000))
    with pytest.raises(ValueError, match="root"):
        NotionSource(TOKEN, "../etc")
    with pytest.raises(ValueError, match="https"):
        NotionSource(TOKEN, "a" * 32, api_url="http://api.notion.com")


def test_pagination_lists_everything():
    tree = {"title": "R", "children": [{"title": f"p{i:03}", "text": "x"} for i in range(25)]
            + [{"database": "D", "schema": {}, "rows": [{"title": f"r{i:03}"} for i in range(23)]}]}
    fake = FakeNotion.from_tree(tree, TOKEN)
    fake.page_size = 4
    sh = Shell()
    sh.mount("/mnt/n", _source(fake), "read-only")
    assert sh.run("ls /mnt/n | wc -l; ls /mnt/n/D | wc -l").stdout == "27\n23\n"


def test_deep_nesting_is_listed_to_the_framework_limit():
    tree = node = {"title": "L0", "text": "t"}
    for i in range(1, 45):
        child = {"title": f"L{i}", "text": "t"}
        node["children"] = [child]
        node = child
    fake, sh, _ = _mounted("read-only", tree)
    r = sh.run("find /mnt/n -name index.md | wc -l; find /mnt/n -name 'L44*'")
    assert int(r.stdout.split()[0]) <= 33 and r.exit_code == 0


def test_rate_limits_and_busy_answers_are_retried_then_reported():
    fake, sh, src = _mounted("read-only")
    fake.fault("GET", "/markdown", 429, [("Retry-After", "0")], times=3)
    fake.fault("GET", "/v1/pages/", 503, [("Retry-After", "0")], times=1)
    assert "Welcome" in sh.run("cat /mnt/n/Wiki/Onboarding.md").stdout
    fake.fault("GET", "/markdown", 429, [("Retry-After", "0")], times=50)
    r = sh.run("cat /mnt/n/Wiki/Style\\ Guide.md")
    assert "Notion is busy (HTTP 429); tried 5 times" in r.stderr
    fake.faults.clear()
    fake.fault("GET", "/markdown", 429, [("Retry-After", "3600")])
    assert "asked to wait 3600 seconds" in sh.run("cat '/mnt/n/Inbox (unsorted)/random idea.md'").stderr


def test_requests_keep_to_the_rate():
    fake = FakeNotion.from_tree(TREE, TOKEN)
    t = time.monotonic()
    Shell().mount("/mnt/n", NotionSource(TOKEN, fake.root, transport=fake, rate=50), "read-only")
    assert time.monotonic() - t >= (len(fake.requests) - 1) / 50 * 0.9


def test_a_write_that_got_no_answer_is_not_repeated():
    fake, sh, src = _mounted("live")
    from topwater.webaccess import TransportError
    sent = []

    def flaky(method, url, *a):
        if method == "PATCH":
            sent.append(url)
            raise TransportError("reset")
        return fake(method, url, *a)
    src._http._transport = flaky
    r = sh.run("echo hi > /mnt/n/Wiki/Onboarding.md")
    assert "the change may or may not have been made" in r.stderr and len(sent) == 1


@pytest.mark.parametrize("cmd, refusal", [
    ("echo hi > /mnt/n/Wiki/notes.txt", "only .md files can be Notion pages"),
    ("cp /mnt/n/Wiki/Onboarding.md /mnt/n/Wiki/copy.txt", "only .md files can be Notion pages"),
    ("printf '<page url=\"https://x/p/%s\">X</page>\\n' " + "c" * 32 + " | tee -a /mnt/n/Wiki/Onboarding.md", "can't add or change <page>"),
    ("sed -i '1i <database url=\"u\">D</database>' /mnt/n/Wiki/Onboarding.md", "can't add or change <page>"),
    ("mkdir '/mnt/n/Meeting Notes/sub'", "a database holds rows"),
    ("mv '/mnt/n/Meeting Notes' /mnt/n/Wiki/", "Notion can't move databases"),
])
def test_refusals_come_through_the_mount_whatever_command_writes(cmd, refusal):
    """The source refuses in its own methods, so any program that changes the files (a redirection, cp, tee, sed, a
    future python3) gets the same message from the framework, and nothing reaches Notion."""
    fake, sh, _ = _mounted("live")
    before = fake.outline()
    r = sh.run(cmd)
    assert refusal in r.stderr and "the change stays pending" in r.stderr, r.stderr
    assert fake.outline() == before


@pytest.mark.parametrize("answer", [b"not json", b"[1,2]", b'{"results": "nope", "has_more": true, "next_cursor": 5}',
                                    b'{"results": [{"id": "../../v1/users", "type": "child_page", "child_page": {"title": "x"}}, 7, null], "has_more": false}'])
def test_malformed_answers_fail_cleanly(answer):
    fake = FakeNotion.from_tree(TREE, TOKEN)
    fake.fault("GET", "/children", 200, body=answer, times=100)
    sh = Shell()
    try:
        sh.mount("/mnt/n", _source(fake), "read-only")
    except ValueError as e:
        assert "isn't a JSON object" in str(e)
        return
    assert sh.run("ls /mnt/n").exit_code == 0
    assert not any("users" in p for _, p in fake.requests)


def test_hostile_titles_become_safe_names():
    titles = ["a/b", "..", ".", "", "x\0y", "\x1b[31mred", "é" * 400, "index", "Notes", "Notes"]
    tree = {"title": "R", "children": [{"title": t, "text": "t"} for t in titles]}
    fake, sh, _ = _mounted("read-only", tree)
    names = sh.run("ls -1 /mnt/n").stdout.split("\n")[:-1]
    assert len(names) == len(titles) + 1 and len(set(names)) == len(names)
    assert {"a∕b.md", "(..).md", "(.).md", "Untitled.md", "x y.md", " [31mred.md".lstrip(), "index (2).md", "Notes.md", "Notes (2).md", "index.md"} <= set(names)
    assert all(len(n.encode()) <= 255 and "/" not in n and "\0" not in n for n in names)


def test_a_huge_page_is_refused_past_the_limit():
    fake = FakeNotion.from_tree(TREE, TOKEN)
    fake.edit(fake.find("Onboarding"), "x" * 3_000_000)
    sh = Shell(max_file_system_bytes=Shell().fs.used_bytes + 2_500_000)
    sh.mount("/mnt/n", _source(fake), "staged")
    r = sh.run("cat /mnt/n/Wiki/Onboarding.md | wc -c")
    assert r.stdout.strip() == "0" and "too large" in r.stderr.lower()
    r = sh.run("for i in $(seq 101); do printf '%10000s' x; done > /mnt/n/Wiki/Big.md; mount -c")
    assert "write /mnt/n/Wiki/Big.md" in r.stdout
    res = sh.apply_changes("/mnt/n")
    assert res.failed.path == "/Wiki/Big.md" and "too large for one Notion page" in res.error


def test_a_page_trashed_between_listing_and_reading():
    fake, sh, _ = _mounted("staged")
    fake.pages[fake.find("Onboarding")]["trash"] = True
    r = sh.run("cat /mnt/n/Wiki/Onboarding.md")
    assert r.exit_code == 1 and "not found in Notion" in r.stderr


def test_a_failed_change_stops_the_apply_and_says_which():
    fake, sh, _ = _mounted("staged")
    sh.run("echo a > /mnt/n/Wiki/Onboarding.md; echo b > '/mnt/n/Wiki/Style Guide.md'; echo c > /mnt/n/Wiki/New.md")
    fake.fault("PATCH", "/markdown", 400, body=json.dumps({"object": "error", "status": 400, "code": "validation_error", "message": "Content too complex."}), times=1)
    r = sh.apply_changes("/mnt/n")
    assert [c.path for c in r.applied] == ["/Wiki/New.md"] and r.failed.path == "/Wiki/Onboarding.md"
    assert "Notion refused the change: Content too complex." in r.error and not r.conflict and [c.path for c in r.not_tried] == ["/Wiki/Style Guide.md"]
    r = sh.apply_changes("/mnt/n")
    assert r.failed is None and "Onboarding\": \"a\"" in fake.outline().replace("Onboarding: ", "Onboarding\": ")


def test_children_survive_any_edit_of_the_parent():
    fake, sh, _ = _mounted("live")
    for text in ("", "only new text", "<p />", "TEST\nmore"):
        r = sh.run(f"printf '%s' '{text}' > /mnt/n/Projects/index.md")
        assert r.stderr == "", r.stderr
        assert [c.split(":")[0].strip() for c in fake.outline().split("\n") if c.startswith("    ")][:3] == ["Website Redesign", "Kickoff notes", "Q4 Launch Plan"]
    projects = fake.pages[fake.find("Projects")]
    assert fake._children(projects) == [fake.find("Website Redesign"), fake.find("Q4 Launch Plan")]


def test_the_source_never_sends_allow_deleting_content():
    fake, sh, src = _mounted("live")
    seen = []
    real = src._http._transport

    def spy(method, url, headers, body, *a):
        seen.append(body or b"")
        return real(method, url, headers, body, *a)
    src._http._transport = spy
    sh.run("echo x > /mnt/n/Projects/index.md; mkdir /mnt/n/Wiki/X; echo y > /mnt/n/Wiki/X/index.md")
    assert seen and not any(b"allow_deleting_content" in b for b in seen)


def test_conflict_errors_reach_the_source_contract():
    fake, sh, src = _mounted("staged")

    async def go():
        st = await src.stat("/Wiki/Onboarding.md")
        fake.tick()
        fake.edit(fake.find("Onboarding"), "changed")
        with pytest.raises(MountConflictError):
            await src.write("/Wiki/Onboarding.md", b"mine", st.version)
        with pytest.raises(MountConflictError):
            await src.write("/Wiki/Onboarding.md", b"mine", None)
        with pytest.raises(MountConflictError):
            await src.rename("/Wiki/Onboarding.md", "/Wiki/Style Guide.md", st.version)
    asyncio.run(go())


# ----- the real API (opt-in) -----

def _live_token() -> str | None:
    if os.environ.get("TOPWATER_LIVE_NOTION") != "1":
        return None
    if os.environ.get("NOTION_TOKEN"):
        return os.environ["NOTION_TOKEN"]
    env = Path.home() / ".config" / "topwater" / "notion.env"
    if env.exists():
        for line in env.read_text().splitlines():
            if line.startswith("NOTION_TOKEN="):
                return line.split("=", 1)[1].strip().strip('"')
    return None


LIVE = _live_token()
live = pytest.mark.skipif(LIVE is None, reason="set TOPWATER_LIVE_NOTION=1 and a Notion token to run against the real API")
SANDBOX = os.environ.get("NOTION_ROOT", "425832d4-414a-8087-b9a1-f1cb08ff38b1")


@pytest.fixture
def scratch():
    """A scratch page under the sandbox root with a small tree, moved to the trash afterwards."""
    api = NotionSource(LIVE, SANDBOX)

    async def make():
        page = await api._request("POST", "/v1/pages", {"parent": {"page_id": SANDBOX}, "properties": {"title": {"title": nt._rich(f"zz topwater test {int(time.time())}")}}, "markdown": "Scratch page for a Topwater test.", "markdown_version": "v2"})
        pid = page["id"]
        for title, text in (("Inbox", "Unsorted."), ("Wiki", "Knowledge.")):
            sub = await api._request("POST", "/v1/pages", {"parent": {"page_id": pid}, "properties": {"title": {"title": nt._rich(title)}}, "markdown": text, "markdown_version": "v2"})
            if title == "Inbox":
                for t, x in (("Notes", "one"), ("Notes", "two"), ("Idea", "an idea")):
                    await api._request("POST", "/v1/pages", {"parent": {"page_id": sub["id"]}, "properties": {"title": {"title": nt._rich(t)}}, "markdown": x, "markdown_version": "v2"})
        db = await api._request("POST", "/v1/databases", {"parent": {"type": "page_id", "page_id": pid}, "title": nt._rich("Tasks"),
                                                          "initial_data_source": {"properties": {"Name": {"title": {}}, "Done": {"checkbox": {}}, "Kind": {"select": {"options": [{"name": "Bug"}]}}}}})
        await api._request("POST", "/v1/pages", {"parent": {"type": "data_source_id", "data_source_id": db["data_sources"][0]["id"]},
                                                 "properties": {"Name": {"title": nt._rich("Fix login")}, "Done": {"checkbox": False}, "Kind": {"select": {"name": "Bug"}}}})
        return pid
    pid = asyncio.run(make())
    yield pid
    asyncio.run(api._request("PATCH", f"/v1/pages/{pid}", {"in_trash": True}))


@live
def test_live_restructure_applies_and_keeps_children(scratch):
    sh = Shell()
    sh.max_call_duration = 120
    sh.mount("/mnt/n", NotionSource(lambda: LIVE, scratch), "staged")
    r = sh.run("cd /mnt/n && find . | sort && cat Inbox/index.md 'Tasks/Fix login.md'")
    assert "./Inbox/Notes (2).md" in r.stdout and "./Wiki.md" in r.stdout and "Unsorted." in r.stdout and 'Kind: "Bug"' in r.stdout, r.stdout + r.stderr
    second = sh.run("cat '/mnt/n/Inbox/Notes (2).md'").stdout.strip()  # created in the same minute: the id decides which is (2)
    r = sh.run("cd /mnt/n && mkdir Wiki && mv Wiki.md Wiki/index.md && mv 'Inbox/Notes (2).md' Wiki/Notes.md && echo 'Unsorted, now sorted.' > Inbox/index.md && sed -i 's/Done: false/Done: true/' 'Tasks/Fix login.md' && mkdir Wiki/Ideas && mv Inbox/Idea.md Wiki/Ideas/ && rm Inbox/Notes.md && mount -c")
    assert r.exit_code == 0, r.stderr
    res = sh.apply_changes("/mnt/n")
    assert res.failed is None, res.error
    sh.refresh_mount("/mnt/n")
    r = sh.run("cd /mnt/n && find . | sort && cat Inbox.md Wiki/index.md Wiki/Notes.md 'Tasks/Fix login.md'")
    assert r.stdout.split("\n")[:15] == [".", "./Inbox.md", "./Tasks", "./Tasks/Fix login.md", "./Wiki", "./Wiki/Ideas", "./Wiki/Ideas/Idea.md",
                                        "./Wiki/Ideas/index.md", "./Wiki/Notes.md", "./Wiki/index.md", "./index.md", "Unsorted, now sorted.",
                                        "Knowledge.", second, "---"], r.stdout
    assert "Done: true" in r.stdout


@live
def test_live_answers_have_the_shape_the_fake_gives(scratch):
    """The fields the connector reads are where the fake puts them."""
    api = NotionSource(LIVE, scratch)
    fake = FakeNotion.from_tree({"title": "R", "text": "t", "children": [{"title": "C", "text": "c"}, {"database": "D", "schema": {"Done": "checkbox"}, "rows": [{"title": "r"}]}]})
    fsrc = _source(fake)

    async def shapes(src, root):
        page = await src._request("GET", f"/v1/pages/{root}")
        kids = await src._request("GET", f"/v1/blocks/{root}/children", query={"page_size": 100})
        md = await src._request("GET", f"/v1/pages/{root}/markdown", query={"markdown_version": "v2"})
        db = next(b for b in kids["results"] if b["type"] == "child_database")
        dbo = await src._request("GET", f"/v1/databases/{db['id']}")
        rows = await src._request("POST", f"/v1/data_sources/{dbo['data_sources'][0]['id']}/query", {"page_size": 100})
        return page, kids, md, dbo, rows

    real, faked = asyncio.run(shapes(api, scratch)), asyncio.run(shapes(fsrc, fake.root))
    for r, f in zip(real, faked):
        assert not set(f) - set(r), set(f) - set(r)
        for b in f.get("results", []):
            a = next(a for a in r["results"] if a.get("type") == b.get("type") or a.get("object") == b.get("object") == "page")
            assert not set(b) - set(a), set(b) - set(a)
    assert real[1]["results"][0]["type"] in ("child_page", "child_database", "paragraph")
    assert real[0]["last_edited_time"].endswith(":00.000Z")  # minute granularity, as the fake has it
