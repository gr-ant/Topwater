# Microsoft Graph connector: design for every port

The Python port (`python/src/topwater/connectors/graph.py`, with `connectors/common.py`) is the reference. This note is
what the C# and TypeScript ports implement so the three behave the same: the same file view, the same requests, the
same checks and messages. The user guide is [graph.md](graph.md); the mount contract is
[mounts.md](../mounts.md#writing-a-source). The Notion connector's note, [notion-design.md](notion-design.md), follows
the same plan; [what the two share](#shared-with-the-notion-connector) is at the end.

- [Placement and API](#placement-and-api)
- [What Graph does (checked against the real service)](#what-graph-does)
- [The file view](#the-file-view)
- [Listing](#listing)
- [Versions and conflicts](#versions-and-conflicts)
- [Operations](#operations)
- [List items: front matter](#list-items-front-matter)
- [Refusals and the error path](#refusals-and-the-error-path)
- [Requests](#requests)
- [Data tables](#data-tables)
- [The fake and the shared cases](#the-fake-and-the-shared-cases)
- [Live tests and the demo](#live-tests-and-the-demo)
- [Shared with the Notion connector](#shared-with-the-notion-connector)

## Placement and API

Graph needs only HTTPS and JSON, so the connector is in the core package, in the `connectors` folder, never a
dependency. Signing in is not: the host passes a token or a function that returns one (MSAL, Azure.Identity, or
`python/examples/graph_auth.py` for development).

| | Python | C# | TypeScript |
|---|---|---|---|
| File | `topwater/connectors/graph.py` | `src/Topwater/Connectors/GraphSource.cs` | `typescript/src/connectors/graph.ts` |
| Shared pieces | `topwater/connectors/common.py` | `Connectors/ConnectorCommon.cs` | `connectors/common.ts` |
| Constructor | `GraphSource(token, *, site=None, drive=None, id=None, transport=None, timeout=60, api_url=..., rate=None)` | `new GraphSource(Func<CancellationToken, ValueTask<string>> token, GraphOptions options)`; options: `Site`, `Drive`, `Id`, `Transport` (an `HttpMessageHandler`), `Timeout`, `ApiUrl`, `Rate` | `new GraphSource({ token: string \| (() => string \| Promise<string>), site?, drive?, id?, fetch?, timeout?, apiUrl?, rate? })` |
| Transport | webaccess `Transport` run in a thread | `HttpClient` over the handler, `AllowAutoRedirect = false` | a `fetch`-shaped function, `redirect: 'manual'` |
| Fake | `python/tests/graph_fake.py` | `tests/.../GraphFake.cs` (an `HttpMessageHandler`) | `typescript/test/graph-fake.ts` (a `fetch`) |

`type` is `graph`; `id` defaults to the `site` or `drive` argument. `capabilities`: write, delete, rename, mkdir. Give
exactly one of `site` (a web URL `https://host/sites/x`, `host:/sites/x`, or a Graph site id) and `drive` (a drive id,
or `me`). `api_url` must be `https://`. The default transport never follows redirects and refuses internal addresses
(webaccess's pinned transport; in C# and TS the same checks as their web layers). Cancellation: C# passes the call's
token to every request and every backoff delay; TS the `signal`; Python relies on the framework's `wait_for`.

The source also has `note(change) -> str | None` (C# `Note(MountChange)`, TS `note(change)`): what the host should know
before applying a change. Today it says when a rename is a [copy and a delete](#moves-between-libraries-lists-and-sites).

## What Graph does

Checked against a real tenant (Microsoft Graph v1.0, November 2026):

- `GET /drives/{d}/root:/{path}:/content` answers `302` with `Location` on the tenant's SharePoint host
  (`/sites/<site>/_layouts/15/download.aspx?...`), a pre-signed URL: it needs no token. Upload session URLs
  (`createUploadSession` → `uploadUrl`) are pre-signed the same way.
- A file's `cTag` changes only with its content; its `eTag` also changes on a rename or move. A folder's `eTag` does
  not change when a child is added.
- Names match without case: writing `B.TXT` where `b.txt` exists fails with `409 nameAlreadyExists` (a conflict here).
- `PATCH .../items/{id}/fields` honours `If-Match` with the item's `eTag` (`412` when stale). List item `eTag`s look
  like `"guid,N"`.
- `PATCH /drives/{d}/items/{id}` with `name` and/or `parentReference.id` renames or moves in the same drive and keeps
  the id, version history and sharing. Graph v1.0 has no way to move between drives without a long-running copy
  monitor, and none to create, rename or delete a subsite.
- `DELETE` puts the item in the site's recycle bin. `POST .../permanentDelete` skips it (the live tests use it on their
  scratch folder only).

## The file view

The root of a **drive** mount is the drive's root folder: its folders and files, by name, as Graph lists them.

The root of a **site** mount, and every subsite folder, holds the site's containers:

1. **Libraries** (`GET /sites/{s}/drives`), sorted by name, each under its own name.
2. **Lists** (`GET /sites/{s}/lists`), sorted by `displayName`, leaving out hidden lists (`list.hidden`) and lists whose
   `list.template` contains `library` or `catalog` (document libraries are already there as drives; galleries are
   system lists).
3. **Subsites** (`GET /sites/{s}/sites`), sorted by `displayName`, while the subsite is at most `MAX_SITE_DEPTH` (8)
   levels below the mounted site; deeper ones are not shown.

Names: the title made safe by `safe_name` ([data tables](#data-tables)); the fallback is `Library`, `List` or `Site`.
**When names collide**, the library keeps its name, a list gets ` (list)` and a subsite ` (site)` (so `Projects/` is
the library, `Projects (list)/` the list, `Projects (site)/` the subsite); a name still taken after that gets ` (2)`,
` (3)`... in the order above. The suffix depends only on what shares the name at that level, never on listing order,
so the names are stable between listings, and the kind is visible at a glance exactly where it is ambiguous. Names
without a clash stay as SharePoint shows them, so the common case reads naturally.

A **library** folder is the drive: its folders and files. A file whose item has neither `file` nor `folder` (a OneNote
notebook, a link) is left out.

A **list** folder holds one file per item, `Title.md` (from `fields.Title`; empty: `Item {id}`), in order of item id;
a repeated title gets ` (2)`, ` (3)`... before `.md`. Items inside list folders are shown flat; list folders themselves
(content type `Folder`) are left out. A list has no subfolders.

A **subsite** folder is a site folder as above. Subsites are navigate-only: nothing creates, renames or deletes one
([operations](#operations)); their libraries and lists are as editable as the top site's.

`MountEntry`: a file: `size` from Graph, `modified` = `lastModifiedDateTime` as UTC, `version` = `cTag` (else `eTag`).
A folder: `size` 0, `version` = `eTag`. A list item: `size` = the rendered file's length, `version` = the item's `eTag`.
A container: a folder whose `version` is `kind:id`.

## Listing

`list(path, max)`:

- A site folder: libraries, lists and subsites (three paged requests, at most 2,000 each), named as above, at most
  `max`. The names are kept per site path; every later operation resolves a path through them, and a name that isn't
  there causes one fresh listing of that site before "No such file or directory".
- A library folder: `GET /drives/{d}/root/children` or `/drives/{d}/root:/{path}:/children` with
  `$select=id,name,size,eTag,cTag,lastModifiedDateTime,folder,file`.
- A list folder: its columns (`GET /sites/{s}/lists/{l}/columns`, kept until the list is listed again), then
  `GET /sites/{s}/lists/{l}/items?expand=fields`. The names are kept: a later operation on `Title.md` uses that item id.

Paging: `$top=200` (or `max` when smaller), then `@odata.nextLink`, at most 50 requests or `max` entries. A next link
that isn't under `api_url` fails the listing ("not followed"); a repeated link stops it.

## Versions and conflicts

The framework stats each change's path before applying it and compares versions; the source checks again itself:

- File writes send `If-Match: <cTag>` (simple PUT and `createUploadSession`). A new file sends
  `@microsoft.graph.conflictBehavior=fail` instead, so one created meanwhile is a conflict.
- Deleting or renaming a file sends `If-Match` with the version read (renames of folders don't: a folder's `eTag` says
  nothing about what the agent saw).
- List items: `PATCH .../fields` and `DELETE .../items/{id}` send `If-Match: <eTag>`.
- `409` and `412` become a conflict (`MountConflictError`) with Graph's code; a path that vanished between the listing
  and the change is a conflict too ("deleted in SharePoint since it was read").
- `stat` treats a name Graph matched in another case as missing (SharePoint matches without case, the mount doesn't).

## Operations

**read(path, max)**: a list item: rendered from a fresh `GET .../items/{id}?expand=fields`. A file: `GET .../content`
with the token; a redirect (`301`, `302`, `303`, `307`, `308`) is followed once **without** the token, and only to an
`https` URL. `404`: missing ("deleted in SharePoint since it was listed"). More than `max` bytes: "File too large".

**write(path, data, expected)**: in a list: [list items](#list-items-front-matter). At a site's level: refused
("put files in a library"). In a library: [name checks](#data-tables) first, before any request; then up to 4 MiB one
`PUT .../content`, above that `createUploadSession` and chunks of 3,276,800 bytes (10 × 320 KiB) with `Content-Range`,
sent without the token; any failure deletes the session. Returns the new `cTag`.

**mkdir(path)**: in a library: `POST .../children` with `{"name", "folder": {}, "@microsoft.graph.conflictBehavior":
"fail"}`. At a site's level: refused ("new libraries, lists and sites aren't created from here"). In a list: refused.

**rename(path, to, expected)**: a library, list or site: refused. Inside one library: `PATCH` with the new `name` and,
when the folder changes, `parentReference.id` of the target folder (which must exist). A list item to the same list:
`PATCH .../fields {"Title"}`. Anything else is a [move between containers](#moves-between-libraries-lists-and-sites).

**delete(path, expected)**: a library, list or site: refused. A folder: refused when SharePoint now has something in
it the agent didn't see (one `children` request); otherwise `DELETE`. A file or item: `DELETE` with `If-Match`.

### Moves between libraries, lists and sites

A rename whose source and target are in different libraries (another site's included) is a copy and a delete:
folders are made with `mkdir`, files read and written (each a new item, `conflictBehavior=fail`), then the original is
deleted with `If-Match`. At most 1,000 entries and 250,000,000 bytes per move, checked as it goes; over that it is
refused before anything more is copied. If the delete fails after the copy, the message says the copy exists. A list
item to another list: the editable columns the target list also has are written as a new item, then the original is
deleted. A file can't move into a list or an item into a library.

`note(change)` returns the `crossMove` message for such a rename, so a host can show it in the change set before
applying (the demo prints it). It needs only what was listed, never a request.

## List items: front matter

Columns come from `GET .../columns`. Left out: hidden columns, names starting with `_`, and the `_SKIP` set. Each
shown column has a key (its `displayName` made safe; when two share one, the internal `name`), its type (the first
of the `_TYPES` facets the column has, else `other`), and whether it can be written:

| Column | Shown | Written |
|---|---|---|
| `text` (and multi-line text) | the string | a string (not a list, object or boolean); at most `maxLength` characters |
| `choice` | the choice, or a list for a multi-choice | must be one of `choices` unless `allowTextEntry`; a list is sent with `@odata.type: Collection(Edm.String)` |
| `number`, `currency` | the number | a number (not a boolean) or null |
| `boolean` | true / false | true or false |
| `dateTime` | the string Graph sends | `2026-12-10` (sent as `2026-12-10T00:00:00Z`) or `2026-12-10T09:00[:00[.f]][Z\|±hh:mm]`; empty: null |
| `personOrGroup`, `lookup` | the `{name}LookupId` value | refused: "a personOrGroup column, not editable here" |
| `calculated` and every other type | the value when it is a string, number, boolean or list | refused the same way |
| `ID`, `Created`, `Modified`, `Author` (`Created By`), `Editor` (`Modified By`), and any column with `readOnly` | `ID` is the item id; `Author` / `Editor` the `createdBy` / `lastModifiedBy` display names | refused: "set by SharePoint" |

Rendering uses the shared front matter writer: `---`, one `key: JSON value` a line in key order, then
`# read-only: A, B, ...` naming the columns that can't be written, then `---`. `Title` is the file name, not a line.

Writing: the shared parser (the same rules as Notion's); text after the front matter is refused. Only keys whose value
differs from the file rendered from the item now are sent; an unknown key, a read-only one, or a bad value is refused
with the messages in the table. A **new** item (`expected` null) is `POST .../items {"fields"}` with `Title` from the
file name and only the writable, non-null values: so `cp "Status page.md" "New task.md"` copies Status, Owner, ...
and SharePoint sets the rest. A list item file is at most 1,000,000 bytes of UTF-8.

## Refusals and the error path

Every refusal is raised by the source's own operation (`write`, `mkdir`, `rename`, `delete`) as a
`MountSourceError` (or `MountConflictError`), never by a check in front of a particular command. The framework turns
it into the same failure whatever wrote the file:

- staged: `apply_changes` stops there and returns it in `MountApplyResult.Error` (the change stays pending);
- live: after **any** command that changed the mount (a built-in like `sed -i` or `cp`, a redirection, a host program
  registered as a command, a Python function the host registered) the framework applies the changes and writes
  `mount: <machine path>: <message>; the change stays pending ...` to that command's error output.

So a refusal reaches an agent writing with bash, with a `python3` program the host offers, or the host's own code, and
a port must not add command-specific checks (a `cp`/`mv` hook, a parser of the script) in place of the source's. The
checks that need no request (names, path length, list item shape, column values) run before the first request of the
operation, so a refused change sends nothing (the hostile tests assert no write request was made). The test
`test_refusals_reach_any_writer_through_the_mount` registers a Python command that writes an illegal name and a bad
choice into a live mount and checks both refusals in its error output.

## Requests

- `Authorization: Bearer <token>` on Graph requests only: the token is asked from the host's function for every
  attempt, and never sent to a download or upload URL. `Accept: application/json`; `Content-Type: application/json`
  with a JSON body; an empty body on POST/PUT/PATCH without one (Graph answers 411 without a length).
- Ids from answers are checked against `[A-Za-z0-9!_.,:-]{1,400}` before they go into a URL; anything else is skipped
  (a listing) or refused ("an id that can't be used"). Paths go in percent-encoded.
- Retries and backoff are the shared client's ([shared](#shared-with-the-notion-connector)): `429` and `503` for any
  method (SharePoint did nothing), other `5xx` and no answer for GET only; up to 4 retries; `Retry-After` (seconds) is
  obeyed by every later request of the source, and one over 60 seconds fails at once; without it 2^attempt seconds.
  A POST/PUT/PATCH/DELETE that got no answer fails with "the change may or may not have been made".
- A JSON answer over 32,000,000 bytes, or one that isn't a JSON object, is an error; `error.code` and `error.message`
  are cut at 100 and 1,000 characters; versions at 1,024.
- HTTP errors: 401 `token`, 403 `denied`, 404 missing, 409/412 conflict, 423 `locked`, 507 `full`, others `refused`.

## Data tables

These are the tables a port copies word for word, kept apart from the code in `graph.py` so that
`spec/connectors/graph.json` can generate them later, as `spec/commands/` does for commands:

| Table | In `graph.py` | |
|---|---|---|
| Messages | `MESSAGES`, `READ_ONLY`, `EXPECTED` | every text the agent or host sees from this connector |
| Limits | `SIMPLE_UPLOAD`, `CHUNK`, `PAGE`, `MAX_LIST_REQUESTS`, `MAX_SITE_DEPTH`, `MAX_PATH`, `MAX_COPY_BYTES`, `MAX_COPY_ENTRIES`, `MAX_ITEM_BYTES` | |
| Name rules | `_ILLEGAL` (`" * : < > ? / \ \|`), `_RESERVED` (`CON PRN AUX NUL COM0-9 LPT0-9 .LOCK DESKTOP.INI`, compared upper-case) | plus: no leading or trailing space, no trailing dot, no `_vti_` anywhere (any case), no `~$` start, not `forms` at a library's top; a path over `MAX_PATH` characters counting the library's server-relative path |
| Column types | `_TYPES` (the facets looked for, in order), `EDITABLE_TYPES`, `_SKIP`, `_SET_BY_SHAREPOINT` | |
| Retries | `common.MAX_RETRIES`, `common.MAX_RETRY_AFTER`, `common.MAX_RESPONSE` | shared with Notion |
| Names from titles | `common.safe_name`, `common.unique`, `common.SLASH`, `common.NAME_BYTES` | shared with Notion |

## The fake and the shared cases

Each port has a fake Graph that answers the requests above from memory; the tests never touch the network. Its state:
sites (with subsites), drives (one per library, plus a lone OneDrive for `drive="me"`), drive items (folders and
files with content, `eTag` `"{guid},N"` changing on every change, `cTag` `"c:{guid},N"` changing on content only),
lists (columns with types, choices, `readOnly`, `hidden`; a template), and items (fields, `eTag`). Ids come from a
counter; the clock starts at 2026-01-01T00:00:00Z and moves one second per change.

It behaves as the real service was seen to: case-insensitive name matching with `nameAlreadyExists`; path and id
addressing; children paging with `@odata.nextLink` (page size settable for tests); `content` answering 302 to a
pre-signed download URL on another host (settable, to test that it gets no token: any request that carries the token
to a non-Graph host is recorded in `leaks`, and every test asserts there are none); simple PUT up to 4 MiB; upload
sessions with `Content-Range` checks; PATCH rename/move; DELETE; `conflictBehavior`; `If-Match` with 412; list
columns, `items?expand=fields`, fields PATCH (refusing read-only columns), POST, DELETE; and injected faults (method,
path pattern, status, headers, body, count). `outline()` prints every library path (with short content) and list item
(id, title, fields as JSON).

`tests/connectors/graph/cases.json` holds the shared cases; its `about` says the format. A port runs each case against
its own fake and compares every `expect` exactly (`python python/tests/test_graph.py --record` rewrites them from the
Python port; review the diff). The hostile cases that need a port's internals (a next link to another host, a
plain-http download, throttling storms with fake sleep, malformed JSON, illegal names sending nothing, 400-character
paths, deep trees, an item deleted between list and read, 412s, partial apply, folder someone added to) are written
per port, following `python/tests/test_graph.py`.

## Live tests and the demo

Opt-in only: `TOPWATER_LIVE_GRAPH=1` and `~/.config/topwater/graph.env` (`GRAPH_TENANT_ID`, `GRAPH_CLIENT_ID`,
`GRAPH_SITE_URL`) with a refresh token in `~/.config/topwater/graph.token.json` (`python/examples/graph_auth.py`
signs in with a device code and rewrites the rotated refresh token, mode 0600, on every refresh). They work only in a
scratch folder `Documents/zz-topwater-test-<time>` (removed with `permanentDelete`) and on scratch items titled
`zz topwater ...` (deleted), and check that the fields the connector reads are where the fake puts them. Never print or
record a token; the tenant and client ids stay out of the repository.

`python/examples/sharepoint_restructure.py` mounts the site staged, runs a bash script that files the Inbox, dedupes
the budgets by a hash of their text (Office files differ in bytes even when their content is the same), renames the
untitled document, marks a tracker item In progress, handles the Marketing subsite's "Copy of" plan, and prints the
change set with notes. `--apply` applies it; `--base` points it at a scratch copy.

## Shared with the Notion connector

Both connectors follow the same plan: the core package's `connectors` folder, a token or token function from the host,
a stdlib transport that can be replaced, a stateful fake plus `tests/connectors/<name>/cases.json`, a design note and a
user guide in `docs/connectors/`, `TOPWATER_LIVE_<NAME>=1` with `~/.config/topwater/<name>.env`, scratch-only live
tests, and a `python/examples/<...>_restructure.py` demo. To reconcile when both are merged:

- **`connectors/common.py`** is written for both: `Api` (token function, rate spacing under a loop-bound lock,
  429/503/5xx/no-answer retries with `Retry-After` shared by every request, response cap, error code and message),
  `front_matter` / `split_front_matter` / `scalar` (the exact rules of notion-design.md "Rows: front matter", plus an
  optional `# comment` line), and `safe_name` (Notion's rule: control characters to spaces, `/` to `∕`, `(.)`, 200
  bytes) and `unique`. The Notion connector still has private copies (`_request`, `_retry_after`, `_front`,
  `_split_front`, `_scalar`, `_name`); it should import these instead, keeping its sticky, created-order naming on top
  of `safe_name`. Its messages use `Notion` where Graph's use `Microsoft Graph`; `Api(service=...)` already does that.
- **Layout and docs rows**: both branches add the same `connectors` row to `tools/layout.json` and the connectors
  paragraph to `docs/architecture.md` §10, and lines to `CHANGELOG.md`, `README.md`, `docs/README.md` and
  `docs/mounts.md`. Those conflict textually only; keep both.
- **`note(change)`** is Graph's. If Notion (or another source) needs to say something about a change, the framework
  could ask every source for it and show it in `mount -c` and `MountChange`: a change to all three ports' `mounts`,
  not made here.
- **Spec**: both have their messages, limits, name rules and type tables as data, ready for `spec/connectors/<name>.json`
  and a generator like `tools/spec-gen/gen.py`; nothing is generated yet.
