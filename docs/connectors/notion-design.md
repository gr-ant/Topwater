# Notion connector: design for every port

The Python port (`python/src/topwater/connectors/notion.py`) is the reference. This note is what the C# and TypeScript
ports implement so the three behave the same: the same names, the same requests, the same messages. The user guide is
[notion.md](notion.md); the mount contract is [mounts.md](../mounts.md#writing-a-source).

- [Placement and API](#placement-and-api)
- [What the Notion API does (checked against the real API)](#what-the-notion-api-does)
- [The file view](#the-file-view)
- [Listing](#listing)
- [Versions and conflicts](#versions-and-conflicts)
- [Operations](#operations)
- [Text: taking the children out and putting them back](#text-taking-the-children-out-and-putting-them-back)
- [Rows: front matter](#rows-front-matter)
- [Requests](#requests)
- [Messages](#messages)
- [The fake and the shared cases](#the-fake-and-the-shared-cases)
- [Live tests](#live-tests)

## Placement and API

The connector needs only HTTP and JSON, so it is in the core package, in a `connectors` folder, never a dependency:

| | Python | C# | TypeScript |
|---|---|---|---|
| File | `topwater/connectors/notion.py` | `src/Topwater/Connectors/NotionSource.cs` | `typescript/src/connectors/notion.ts` |
| Import | `from topwater.connectors.notion import NotionSource` | `using Topwater.Connectors;` | `import { NotionSource } from 'topwater/connectors/notion'` (package `exports` subpath) |
| Constructor | `NotionSource(token, root, id=None, *, transport=None, rate=3.0, timeout=30.0, api_url=...)` | `new NotionSource(Func<CancellationToken, ValueTask<string>> token, string root, string? id = null, NotionOptions? options = null)`; options: `Transport` (an `HttpMessageHandler`), `Rate`, `Timeout`, `ApiUrl` | `new NotionSource({ token: string \| (() => string \| Promise<string>), root, id?, fetch?, rate?, timeout?, apiUrl? })` |
| Transport | webaccess `Transport` callable run in a thread | `HttpClient` over the handler | a `fetch`-shaped function |
| Fake | `python/tests/notion_fake.py` | `tests/.../NotionFake.cs` (an `HttpMessageHandler`) | `typescript/test/notion-fake.ts` (a `fetch`) |

`type` is `notion`; `id` defaults to the root's id (dashed). `capabilities`: write, delete, rename, mkdir. `root` is an
id or a URL: the id is the last run of 32 hex digits in it after removing `-` (a URL like `Cafe-<id>` runs the title's
hex letters into the id, so take the last 32 of the run). Every id read from a response goes through the same parse,
and anything that isn't one is skipped: only parsed ids ever reach a request path. `api_url` must be `https://` unless
a transport is given. Cancellation: C# passes the call's token to every request and to the rate limiter's delay; TS the
`signal`; Python relies on the framework's `wait_for`.

## What the Notion API does

Checked on 2026-11-27 with `Notion-Version: 2026-03-11`, `markdown_version: "v2"` everywhere:

- `GET /v1/pages/{id}/markdown` returns the page's own text with each child page and database as a tag on its own
  lines: `<page url="https://app.notion.com/p/<32 hex>">\nTitle\n</page>`, `<database url="..." data-source-url="collection://<id>" inline="false">\nTitle\n</database>`,
  separated from other blocks by a blank line. A child inside a toggle or column is indented inside it.
- `PATCH .../markdown` with `replace_content`:
  - leaving a child's tag out is refused, `400 validation_error`, "This operation would delete 2 child page(s),
    database(s), or meeting note(s): ... To proceed, either: 1. Include these items ... 2. Set allow_deleting_content:
    true". **The connector never sends `allow_deleting_content`.**
  - keeping a child's tag keeps the child (same id); **the tag's text renames the child**;
  - a tag for a page that is not a child does not move it: it becomes `<mention url="...">Title</mention>`;
  - moving tags around in the text reorders unpredictably (Notion diffs the old and new text), so the connector always
    puts them back where they were;
  - unchanged and edited blocks keep their ids; `<unknown url=... alt="breadcrumb"/>` written back keeps the block.
- `POST /v1/pages` with `markdown` whose first line is a `# ` heading drops that heading (it is taken as the title even
  when `properties.title` is given). The connector creates such a page without `markdown` and then replaces its text.
- `last_edited_time` is to the minute (`2026-11-27T14:09:00.000Z`) for pages and blocks; a database object has
  milliseconds and `+00:00`. A child page's block in its parent's `blocks/{id}/children` carries the page's own
  `last_edited_time`. Moving a page (`POST /v1/pages/{id}/move`) changes the `last_edited_time` of neither the page nor
  either parent. A title change or a text change does change it.
- `move` can't put a page inside a block (a toggle): 404. Databases can't be moved at all.
- `PATCH /v1/pages/{id}` with `{"properties": {"title": {"title": [...]}}}` renames both a page and a database row
  (`title` is the title property's id in every database).
- Page objects have `in_trash`, not `archived`. Row parents are `{"type": "data_source_id", "data_source_id", "database_id"}`.
- The data source query result's `object` is `page`; `next_cursor` is an opaque string.

## The file view

A folder is a page, a database, or one data source of a database with several. Its entries:

- In a page folder: `index.md` (the page's own text), then for each top-level child block, in the order Notion lists
  them:
  - `child_page` whose page has a child page or database among its own top-level blocks: a folder named for its title;
    otherwise a file `Title.md`;
  - `child_database`: a folder named for its title.
- In a database folder: with one data source, a file `Title.md` per row (rows are never folders; their subpages stay in
  their text as tags); with several, a folder per data source, named for its `name`, holding the rows.

Names, from a title (steps 1 to 3 are the shared `common.safe_name` with `Untitled` as the fallback):

1. Each control character (below U+0020, and U+007F) becomes a space; `/` becomes `∕` (U+2215); trim spaces at both
   ends.
2. Empty: `Untitled`. Exactly `.` or `..`: `(.)`, `(..)`.
3. Longer than 200 bytes of UTF-8: cut at 200 bytes, at a character boundary, and trim the end.
4. A file adds `.md`.
5. Unique in the folder: `index.md` is taken first in a page folder. Names given earlier in this session (see 6) are
   taken next. Then the rest in order of `created_time`, then id (string order): the first gets the name, a later one
   with the same name gets `Name (2)`, `Name (3)`, ... (`Name (2).md` for a file), the lowest number not taken.
6. Sticky: a folder remembers (id → title, name) from its last listing; a relisting gives an id its old name again when
   the title is the same, the name is free, and file-or-folder hasn't changed (a name ending `.md` is a file).
7. The entries are returned sorted by name (ordinal).

A name the agent gives becomes a title by turning `∕` back into `/` (and dropping `.md` for a file). On a rename the
title is changed only when the new name isn't one the current title already gives, with or without a ` (N)` suffix: so
moving `Notes (2).md` keeps the title `Notes`. The framework's temporary names (ending `.topwater-swap-N`) are taken
without that suffix and never change a title.

`MountEntry`: `size` null (unknown); `modified` = `last_edited_time` as UTC; `version` = `last_edited_time` exactly as
Notion sent it for pages, rows and `index.md`, empty for database and data source folders.

## Listing

`list(path, max)` lists the folder fresh (except as below) and returns its entries, at most `max`.

- Page folder: `GET /v1/blocks/{id}/children?page_size=100` (then `&start_cursor=`), at most 50 requests; keep blocks
  of type `child_page` / `child_database` that aren't `in_trash`. For the root folder also `GET /v1/pages/{root}` for
  `index.md`'s version. For each `child_page` block with `has_children` true, fetch its children the same way (all at
  once, through the rate limiter) to decide file or folder, and keep the result for 60 seconds: listing that child's
  folder uses it instead of asking again. This look-ahead always fetches fresh, and every write, delete, rename or
  mkdir drops everything kept.
- Database folder: `GET /v1/databases/{id}`; take `data_sources` with valid ids. One: `POST
  /v1/data_sources/{ds}/query` with `{"page_size": 100}` (then `start_cursor`), stopping at 50 requests or `max` rows;
  skip results whose `object` isn't `page` or that are `in_trash`. Several: one folder per data source.
- Paging stops when `has_more` isn't `true`, or `next_cursor` isn't a string of 1–1000 characters, or repeats.
- The root: `GET /v1/pages/{root}`; 404 or in trash: `GET /v1/databases/{root}`; neither: the mount fails ("the root
  page is not in Notion, or not shared with the integration").
- When a folder is listed, a folder listed before under one of its names keeps standing for that item if the id is the
  same and it is still a folder; otherwise it is forgotten (with everything below).

The source keeps a map from paths to the folders it listed; any operation on a path whose folder isn't known lists the
folders on the way first (`stat` of a missing one returns null).

## Versions and conflicts

`version` = `last_edited_time`. The framework stats each change's path before applying it and compares versions;
the source checks again itself, and:

- **Read hash.** `read` remembers a SHA-256 of the bytes it returned, per page id. `write` refetches the page's text,
  renders the file the agent would see now, and if a hash is remembered and differs: conflict "changed in Notion since
  it was read". This catches edits in the same minute, which `last_edited_time` can't. After a write the hash is of
  the file rendered from the Markdown Notion returned.
- **Our own renames.** A rename that changes a title changes `last_edited_time`; a change set that renames a page and
  then writes it carries the old version. So after a title change the source remembers (id: new time → time before)
  and maps it back when comparing and in `stat`. A write forgets it; a listing that includes the id forgets it.
- `stat` of a page, row or folder page returns null when the page is in the trash, or its title is no longer the
  listed one, or its parent is no longer the folder's (page folder: `parent.page_id`; data source folder:
  `parent.data_source_id`; database folder: `parent.database_id`). `index.md` is not checked for title or parent.
  `stat` of a database: null when 404 or in trash. Of a data source folder: from the map, no request.
- Folders are checked for existence only (the framework does the same); deletes and renames of folders don't compare
  versions.

## Operations

Paths are the framework's canonical source paths. Each operation wraps API errors with the messages below.

**read(path, max)**: the item must be a file. `GET .../markdown?markdown_version=v2` (404 → "not found in Notion").
Page and `index.md`: the text without its children's tags + `\n` (empty text: empty file). Row: `GET /v1/pages/{id}`,
then front matter + text + `\n` (text empty: front matter only). More than `max` bytes: "too large". Remember the hash.

**write(path, data, expected)**

1. If the path names a folder: "Is a directory". Decode: more than 1,000,000 bytes, not UTF-8, or a NUL: refused.
2. `index.md` in a page folder that has none yet (a folder made by `mkdir`): update with no version, below.
3. `expected` null: the name must be free (else conflict "already exists"), then **create**. Otherwise the name must
   exist (else conflict "no longer in Notion"), then **update**.

*Create*: the name must end in `.md`, not be `.md`, not be a temporary name. Parent: page folder → `{"type": "page_id",
"page_id"}`; database folder with one data source or data source folder → `{"type": "data_source_id",
"data_source_id"}`; a database with several → refused. Row: split the front matter, get the schema (`GET
/v1/data_sources/{ds}`, kept per data source), turn every value into a property (title skipped), and the title goes
under the schema's title property name; page: under `title`. The text (after front matter) may not contain any
`<page>`/`<database>` tag. Strip leading and trailing newlines. If it is not empty and its first non-blank line does
not start with `# `: `POST /v1/pages` with `markdown` and `markdown_version: "v2"`; otherwise `POST` without it and,
if not empty, `PATCH .../markdown` `replace_content`, then `GET` the page for its version. Returns `last_edited_time`.

*Update*: `GET /v1/pages/{id}` (null or in trash: conflict "no longer in Notion"); if `expected` is given and the
page's version (after the own-rename map) differs: conflict "edited in Notion since it was listed". Fetch the text and
render the current file; remembered hash differs, or `expected` null and the current file isn't empty: conflict
"changed in Notion since it was read". `truncated` true: refused "too long to edit as Markdown". Row: split the front
matter, compute the changed properties (below), the text may keep tags that are in the current Markdown exactly, new
Markdown = text with newlines stripped at both ends; if properties changed, `PATCH /v1/pages/{id}` `{"properties"}`.
Page: take the children's tags out of the current Markdown, the agent's text may keep only tags present in what is
left (nested ones), new Markdown = put the tags back into the agent's text (stripped of newlines at both ends). If the
new Markdown differs from the current: `PATCH .../markdown` `{"type": "replace_content", "replace_content": {"new_str"},
"markdown_version": "v2"}`. Then `GET` the page: its time is the result; remember the hash; forget the own-rename entry.

**delete(path, expected)**: missing: conflict. `index.md`: nothing (no request). Data source folder: refused. Database:
`PATCH /v1/databases/{id}` `{"in_trash": true}`. Page or row: `GET` (gone: conflict); a file compares the version
(conflict "edited in Notion since it was listed"); `PATCH /v1/pages/{id}` `{"in_trash": true}`.

**rename(path, to, expected)**: the source must exist (conflict); list the target folder; the target name taken:
conflict "already exists". Then in this order:

1. `to` is `X/index.md`, the target folder is the same page as the item (made by `mkdir X` beside `X.md`): no request;
   the folder now has `index.md` for it. Returns null.
2. `index.md`: refused. Data source folder: refused.
3. Database: another folder: refused; same folder: if the name isn't the current title's, `PATCH /v1/databases/{id}`
   `{"title": [rich text]}`.
4. Page or row: a file's new name must end in `.md`; a folder page into a database: refused. `GET` the page (gone:
   conflict); a file compares the version. If the folder differs: `POST /v1/pages/{id}/move` `{"parent": ...}` (as for
   create). If the title must change: `PATCH /v1/pages/{id}` `{"properties": {"title": {"title": [...]}}}`, `GET` it,
   and remember the own-rename entry when its time changed. Its kind becomes row or page by the target folder.

Returns null (the version the framework has stays right through the own-rename map). Move the item in the map, and
every listed folder under the old path to the new one.

**mkdir(path)**: taken: conflict. Not in a page folder: refused. If the folder has `Name.md` for a page: no request,
`Name` becomes a folder for that same page (pages moved into it go under that page). Otherwise `POST /v1/pages` with
the title only; the new folder is known and empty (no `index.md` until written or listed). Returns null.

## Text: taking the children out and putting them back

A child tag is a match of `^<(page|database)\s[^>\n]*?url="[^"]*"[^>\n]*>.*?</\1>` (multiline, dot matches newline): at
the start of a line, so tags indented inside a toggle or column are not children here and stay in the text. Any tag
anywhere: `<(page|database)\b[^>]*>.*?</\1>` (dot matches newline).

**Split** the Markdown into the text between child tags; strip `\n` from both ends of each piece; the view is the
non-empty pieces joined by `\n\n`. For each tag, its anchor:

- the text before it (the non-empty pieces before it, joined) is not empty: (its last line, how many times that line
  occurs in it);
- else, the view is not empty: *start*;
- else (a page with no text): *end*.

**Join** the agent's text (newlines stripped at both ends) with the tags. Lines = text split on `\n` (none if empty).
For each tag in order: *start* goes before everything; *end* after everything; an anchor (line, n) goes after the
n-th line equal to `line`, moved forward to the last line of its block (while the next line exists and isn't empty);
when there are fewer than n such lines, after everything. Output: for each insertion point, for each tag: drop empty
lines at the end of the output so far, add an empty line if the output isn't empty, the tag, an empty line. Copy each
text line in turn, except an empty line that comes right after an inserted tag's empty line. Join with `\n` and strip
`\n` from both ends.

Joining the view with its own tags gives back the Markdown Notion sent (for Markdown in Notion's own blank-line form).
The tags put back are always the ones in the page's current Markdown, fetched just before the write, so children moved
in or out earlier in the same apply are kept exactly as they are now.

**Tags in the agent's text**: every tag found by the any-tag pattern must be, character for character, one of the tags
in the allowed text (a page: the current view, so only nested tags; a row: the current Markdown; a new page: none).

## Rows: front matter

The format and its parser are the shared ones in `connectors/common.py` (`front_matter`, `split_front_matter`), the
same as for SharePoint list items; this connector never writes the optional `# comment` line.

Rendering: `---\n`, then for each property except the title, sorted by name (ordinal): the name, bare if it matches
`[A-Za-z0-9_][A-Za-z0-9_ .()/-]*` and doesn't end in a space, else as a JSON string; `: `; the value as JSON (Python's
`json.dumps(..., ensure_ascii=False)`: `", "` between list items, `": "` in objects); `\n`. Then `---\n`. Rows always
have it, even with no properties.

| Type | Value shown | Sent when changed |
|---|---|---|
| rich_text | plain text joined | `{"rich_text": [{"type": "text", "text": {"content"}}]}`, 2,000 characters a piece |
| number | number or null | `{"number": v}` |
| select, status | option name or null | `{"select": {"name"}}` (cut at 100), null when empty |
| multi_select | list of names | `[{"name"}]`; a single string is a list of one |
| checkbox | true / false | must be a boolean |
| date | `"start"` when no end and no time zone, else `{"start", "end", "time_zone"}` without nulls | a string: `{"start"}`; an object with `start` |
| url, email, phone_number | string or null | string or null (empty: null) |
| formula, rollup | their result (number, string, boolean, or a date as above; other rollups null) | refused |
| people | list of names (else ids) | refused |
| created_by, last_edited_by | name (else id) | refused |
| relation | list of ids | refused |
| files | list of names | refused |
| unique_id | `PREFIX-N` or N | refused |
| created_time, last_edited_time, others | the value when it is a string, number, boolean, else null | refused |

Parsing: the file starts with `---\n`; the front matter ends at the first line that is exactly `---` (followed by `\n`
or the end); none: "front matter: no closing ---". Lines that are blank or start (after spaces) with `#` are skipped.
A line is `name: value`: the name is a JSON string when the line starts with `"`, else everything before the first `:`;
a line starting with a space, or without `:`, is refused "front matter: one `name: value` per line". The value: JSON if
it parses; else `~`, `Null`, `NULL` → null; `True`, `TRUE`, `yes`, `Yes` → true; `False`, `FALSE`, `no`, `No` → false;
`[a, b]` → a list of the comma-separated items, trimmed and stripped of `'` and `"`; `'x'` → `x` (with `''` → `'`); else
the text. An empty value followed by lines starting (after spaces) with `- ` is a list of those values; with none, null.
The text after the closing line, with leading newlines removed, is the row's text.

Changed properties: for each name in the front matter, a name not in the schema is refused; the title property is
skipped; a value equal to the current one (as shown) is skipped; otherwise a type not in the editable list is refused,
and the value is converted as in the table, or refused with "expected ...". Names left out are left alone.

## Requests

Requests go through the shared client in `connectors/common.py` (`Api("Notion", ...)`, see [README.md](README.md));
what follows is what it does for Notion. Its failure messages are `common.MESSAGES` with `{service}` = `Notion`.

- Headers: `Authorization: Bearer <token>` (the token asked again for every attempt), `Notion-Version: 2026-03-11`,
  `Accept: application/json`, and `Content-Type: application/json` with a body. GET query strings are URL-encoded.
- Rate: one request every `1/rate` seconds (3 a second by default), shared by every request of the source, waited
  under a lock (the lock belongs to the running event loop in Python).
- Retries, at most 4 after the first:
  - 429, any method; 503, any method; other 5xx, GET only: wait `Retry-After` seconds (a number) when given, else
    2^attempt seconds; every later request waits too. A `Retry-After` over 60: fail "Notion asked to wait N seconds
    (HTTP 429)". Out of tries: "Notion is busy (HTTP 429); tried 5 times".
  - No answer (connection, timeout): GET waits 2^attempt seconds and tries again; anything else fails at once with
    "Notion did not answer; the change may or may not have been made". GET out of tries: "Notion did not answer".
- A response over 32,000,000 bytes is refused. Status 400 and up: an API error with `code` and `message` (cut at 100
  and 1,000 characters; both empty when the body isn't JSON). Below 400, an empty body is `{}`; one that isn't a JSON
  object: "Notion sent a response that isn't a JSON object (HTTP n)".
- A token function that isn't async is called in a worker thread, so a blocking refresh doesn't hold the event loop.
- 404 on a GET is "not there" (null) where the operation expects that; elsewhere API errors become messages.

## Messages

Every refusal is raised by the source method doing the change (`write`, `rename`, `mkdir`, `delete`) as the
framework's source error (`MountSourceError` / `MountSourceException`), or its conflict error; never checked in the
shell, a command or anything bash-specific. The framework turns the file tree into changes whatever program made them
(a redirection, `cp`, `tee`, `sed -i`, a `python3` command when there is one), so all of them get the same message: in
live mode on the command's error output, in staged mode in the apply result. A port must not add an early check in a
command instead of the source. `test_refusals_come_through_the_mount_whatever_command_writes` checks this.

The Python port keeps the texts in one table, `MESSAGES` (key → template with `{path}`, `{name}`, ...), and the limits
as constants at the top of the file, so that a later `spec/connectors/notion.json` can hold both for every port; C# and
TS keep the same keys. The request failures and the front matter messages are the shared `common.MESSAGES`.

What the agent sees (the framework prefixes the machine path, as for every source):

| When | Message |
|---|---|
| 401 | `Notion refused the integration's token (HTTP 401)` |
| 403, 404 | `{path}: the integration can't reach this page (share it with the integration)` |
| other API error | `{path}: Notion refused the change: {message}` |
| a missing path | `{path}: not found in Notion` |
| not `.md` | `{path}: only .md files can be Notion pages` |
| not text | `{path}: Notion pages hold UTF-8 text` |
| too big | `{path}: too large for one Notion page (over 1000000 bytes)` |
| read over the limit | `{path}: too large` |
| tags | `{path}: subpages and databases are folders here; a page's text can't add or change <page> or <database> tags` |
| database moved | `{path}: Notion can't move databases; only rename one where it is` |
| index.md moved | `{path}: index.md is the page's own text; move or rename its folder instead` |
| mkdir in a database | `{path}: a database holds rows: write NAME.md files in it` |
| folder page into a database | `{path}: a page with subpages can't become a database row` |
| several data sources | `{path}: this database has several data sources; use their folders` |
| data source folder changed | `{path}: the folder of a data source can't be changed here` |
| cut short | `{path}: the page is too long to edit as Markdown` |
| a folder written | `{path}: Is a directory` |
| front matter | `{path}: front matter: no closing ---`, `{path}: front matter: one \`name: value\` per line` |
| property | `{path}: no property {name} in this database`, `{path}: {name}: can't be changed here ({type} is set by Notion)`, `{path}: {name}: expected {text \| a number \| an option's name \| a list of option names \| true or false \| a date ("2026-10-01") or {"start": ..., "end": ...}}` |
| conflicts | `{path}: already exists`, `{path}: no longer in Notion`, `{path}: edited in Notion since it was listed`, `{path}: changed in Notion since it was read` |
| requests | see [Requests](#requests); also `Notion sent a page without its Markdown`, `Notion sent a page without an id`, `Notion sent a response too large to use` |

## The fake and the shared cases

Each port has a fake Notion that answers the requests above from memory; the tests never touch the network. Its state:
pages (id, title, parent: page / data source / workspace, in trash, created and edited stamps, content: a list of text
pieces and child references, properties for rows), databases (one data source each), data sources (schema). Ids are
`00000000-0000-4000-8000-` + a 12-digit hex counter, given in creation order; the clock starts at 2026-01-01T00:00 and
moves only when a test says; stamps are to the minute (`%Y-%m-%dT%H:%M:00.000Z`).

It behaves as the real API was seen to: Markdown with child tags as above; `replace_content` keeps a referenced child
(renaming it when the tag's text differs), turns other tags into `<mention>`, refuses a dropped child without
`allow_deleting_content`, and stamps the page; `POST /v1/pages` drops a leading `# ` heading from `markdown`; `move`
refuses a page's own subtree, appends to the new parent's content and stamps nothing; a row moved out of its database
loses its properties; paging by `page_size` (capped by a settable size, for tests) and numeric cursors; 401 for a wrong
token; and injected faults (method, path pattern, status, headers, body, count). `outline()` prints the tree: two
spaces a level, the title, a row's properties as JSON (sorted keys, simple values), `: ` and the text as a JSON string
when there is text, and `Title [database]` lines with the rows under them.

`tests/connectors/notion/cases.json` holds the shared cases (its `about` says the format). A port runs each case
against its own fake and compares every `expect` exactly. Python's live tests (`TOPWATER_LIVE_NOTION=1`) also check
that the fields the connector reads are in the real answers where the fake puts them.

## Live tests

Opt-in only: `TOPWATER_LIVE_NOTION=1` and a token (`NOTION_TOKEN`, or `NOTION_TOKEN=` in
`~/.config/topwater/notion.env`); `NOTION_ROOT` names the sandbox page. They make a scratch page under it, work only
there and move it to the trash at the end. Never print or record the token.
