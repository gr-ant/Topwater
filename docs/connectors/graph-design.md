# Microsoft Graph connector: design

The connector is C# only: `src/Topwater/Connectors/GraphSource.cs`, with what connectors share in
`Connectors/ConnectorCommon.cs`. It was first written in Python (branch `parked/connectors`, `connectors/graph.py`),
and this note, written as the plan for the port, now describes the C# code: the file view, the requests, the checks
and the messages. Python and TypeScript don't have it. The user guide is [graph.md](graph.md); the mount contract is
[mounts.md](../mounts.md#writing-a-source). [Where the C# code differs from the Python
reference](#differences-from-the-python-reference) and [what connectors share](#shared-pieces) are at the end.

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
- [Live tests and the example](#live-tests-and-the-example)
- [Shared pieces](#shared-pieces)
- [Differences from the Python reference](#differences-from-the-python-reference)

## Placement and API

Graph needs only HTTPS and JSON, so the connector is in the core `Topwater` package, namespace `Topwater.Connectors`,
with no package dependency (`HttpClient` only). Signing in is not: the host passes a function that returns a current
token (MSAL, Azure.Identity; `examples/GraphMount` uses Azure.Identity's `DeviceCodeCredential`).

| | |
|---|---|
| Files | `src/Topwater/Connectors/GraphSource.cs`, `Connectors/ConnectorCommon.cs` |
| Constructor | `new GraphSource(Func<CancellationToken, ValueTask<string>> token, GraphOptions options)`; options: `Site`, `Drive`, `Id`, `Transport` (an `HttpMessageHandler`), `Timeout` (60 s), `ApiUrl`, `Rate` |
| Transport | `HttpClient` over the handler; the default is the shell's pinned web handler (`AllowAutoRedirect = false`, every connection checked against internal addresses); a host handler that follows redirects is refused |
| Fake | `tests/Topwater.Tests/GraphFake.cs` (an `HttpMessageHandler`), run by `GraphTests.cs` |

`Type` is `graph`; `Id` defaults to `Site` or `Drive`. `Capabilities`: write, delete, rename, mkdir. Give exactly one
of `Site` (a web URL `https://host/sites/x`, `host:/sites/x`, or a Graph site id) and `Drive` (a drive id, or `me`).
`ApiUrl` must be `https://`. The default transport never follows redirects and refuses loopback, private, link-local
(the cloud metadata service) and the other internal ranges the web layer refuses (`WebAddresses.IsBlocked`), at
connect time, so a name that resolves to one is refused too. Cancellation: the call's token goes to every request,
every token request and every backoff delay; each request also has its own `Timeout`.

The source also has `Note(MountChange) -> string?`: what the host should know before applying a change. Today it says
when a rename is a [copy and a delete](#moves-between-libraries-lists-and-sites).

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
3. **Subsites** (`GET /sites/{s}/sites`), sorted by `displayName`, while the subsite is at most `MaxSiteDepth` (8)
   levels below the mounted site; deeper ones are not shown.

Names: the title made safe by `SafeName` ([data tables](#data-tables)); the fallback is `Library`, `List` or `Site`.
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

`ListAsync(path, max)`:

- A site folder: libraries, lists and subsites (three paged requests, at most 2,000 each), named as above, at most
  `max`. The names are kept per site path; every later operation resolves a path through them, and a name that isn't
  there causes one fresh listing of that site before "No such file or directory".
- A library folder: `GET /drives/{d}/root/children` or `/drives/{d}/root:/{path}:/children` with
  `$select=id,name,size,eTag,cTag,lastModifiedDateTime,folder,file`.
- A list folder: its columns (`GET /sites/{s}/lists/{l}/columns`, fetched again with every listing of the list), then
  `GET /sites/{s}/lists/{l}/items?expand=fields`. The names are kept: a later operation on `Title.md` uses that item id.

Paging: `$top=200` (or `max` when smaller), then `@odata.nextLink`, at most 50 requests or `max` entries. A next link
that isn't under `ApiUrl` fails the listing ("not followed"); a repeated link stops it.

## Versions and conflicts

The framework stats each change's path before applying it and compares versions; the source checks again itself:

- File writes send `If-Match: <cTag>` (simple PUT and `createUploadSession`). A new file sends
  `@microsoft.graph.conflictBehavior=fail` instead, so one created meanwhile is a conflict.
- Deleting or renaming a file sends `If-Match` with the version read (renames of folders don't: a folder's `eTag` says
  nothing about what the agent saw).
- List items: `PATCH .../fields` and `DELETE .../items/{id}` send `If-Match: <eTag>`.
- `409` and `412` become a conflict (`MountConflictException`) with Graph's code; a path that vanished between the listing
  and the change is a conflict too ("deleted in SharePoint since it was read").
- `StatAsync` treats a name Graph matched in another case as missing (SharePoint matches without case, the mount doesn't).

## Operations

**ReadAsync(path, max)**: a list item: rendered from a fresh `GET .../items/{id}?expand=fields`. A file: `GET .../content`
with the token; a redirect (`301`, `302`, `303`, `307`, `308`) is followed once **without** the token, and only to an
`https` URL. `404`: missing ("deleted in SharePoint since it was listed"). More than `max` bytes: "File too large".

**WriteAsync(path, data, expected)**: in a list: [list items](#list-items-front-matter). At a site's level: refused
("put files in a library"). In a library: [name checks](#data-tables) first, before any request; then up to 4 MiB one
`PUT .../content`, above that `createUploadSession` and chunks of 3,276,800 bytes (10 × 320 KiB) with `Content-Range`,
sent without the token; any failure (cancellation included) deletes the session, with a 10-second limit of its own. Returns the new `cTag`.

**MkdirAsync(path)**: in a library: `POST .../children` with `{"name", "folder": {}, "@microsoft.graph.conflictBehavior":
"fail"}`. At a site's level: refused ("new libraries, lists and sites aren't created from here"). In a list: refused.

**RenameAsync(path, to, expected)**: a library, list or site: refused. Inside one library: `PATCH` with the new `name` and,
when the folder changes, `parentReference.id` of the target folder (which must exist). A list item to the same list:
`PATCH .../fields {"Title"}`. Anything else is a [move between containers](#moves-between-libraries-lists-and-sites).

**DeleteAsync(path, expected)**: a library, list or site: refused. A folder: refused when SharePoint now has something in
it the agent didn't see (one `children` request); otherwise `DELETE`. A file or item: `DELETE` with `If-Match`.

### Moves between libraries, lists and sites

A rename whose source and target are in different libraries (another site's included) is a copy and a delete:
folders are made with `mkdir`, files read and written (each a new item, `conflictBehavior=fail`), then the original is
deleted with `If-Match`. At most 1,000 entries and 250,000,000 bytes per move, checked as it goes; over that it is
refused before anything more is copied. If the delete fails after the copy, the message says the copy exists. A list
item to another list: the editable columns the target list also has are written as a new item, then the original is
deleted. A file can't move into a list or an item into a library.

`Note(change)` returns the `crossMove` message for such a rename, so a host can show it in the change set before
applying (`examples/GraphMount` prints it). It needs only what was listed, never a request.

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
| `dateTime` | the string Graph sends | `2026-12-10` (sent as `2026-12-10T00:00:00Z`) or `2026-12-10T09:00[:00[.f]][Z\|±hh:mm]` (ASCII digits); empty: null |
| `personOrGroup`, `lookup` | the `{name}LookupId` value | refused: "a personOrGroup column, not editable here" |
| `calculated` and every other type | the value when it is a string, number, boolean or list | refused the same way |
| `ID`, `Created`, `Modified`, `Author` (`Created By`), `Editor` (`Modified By`), and any column with `readOnly` | `ID` is the item id; `Author` / `Editor` the `createdBy` / `lastModifiedBy` display names | refused: "set by SharePoint" |

Rendering uses the shared front matter writer (`ConnectorCommon.FrontMatter`): `---`, one `key: JSON value` a line in key order, then
`# read-only: A, B, ...` naming the columns that can't be written, then `---`. `Title` is the file name, not a line.

Writing: the shared parser (`ConnectorCommon.SplitFrontMatter`); text after the front matter is refused. Only keys whose value
differs from the file rendered from the item now are sent; an unknown key, a read-only one, or a bad value is refused
with the messages in the table. A **new** item (`expected` null) is `POST .../items {"fields"}` with `Title` from the
file name and only the writable, non-null values: so `cp "Status page.md" "New task.md"` copies Status, Owner, ...
and SharePoint sets the rest. A list item file is at most 1,000,000 bytes of UTF-8.

## Refusals and the error path

Every refusal is thrown by the source's own operation (`WriteAsync`, `MkdirAsync`, `RenameAsync`, `DeleteAsync`) as a
`MountSourceException` (or `MountConflictException`), never by a check in front of a particular command. A Graph error
answer that no step handles itself becomes the path's message from the table under [requests](#requests), never "the
source failed". The framework turns
it into the same failure whatever wrote the file:

- staged: `ApplyChangesAsync` stops there and returns it in `MountApplyResult.Error` (the change stays pending);
- live: after **any** command that changed the mount (a built-in like `sed -i` or `cp`, a redirection, a host program
  registered as a command, a C# handler the host registered) the framework applies the changes and writes
  `mount: <machine path>: <message>; the change stays pending ...` to that command's error output.

So a refusal reaches an agent writing with bash, with a program the host offers, or the host's own code, and nothing
adds command-specific checks (a `cp`/`mv` hook, a parser of the script) in place of the source's. The
checks that need no request (names, path length, list item shape, column values) run before the first request of the
operation, so a refused change sends nothing (the hostile tests assert no write request was made). The test
`RefusalsReachAnyWriterThroughTheMount` registers a C# command that writes an illegal name and a bad choice into a
live mount through `shell.Fs` and checks both refusals in its error output.

## Requests

- `Authorization: Bearer <token>` on Graph requests only: the token is asked from the host's function for every
  attempt, and never sent to a download or upload URL. `Accept: application/json`; `Content-Type: application/json`
  with a JSON body; an empty body on POST/PUT/PATCH without one (Graph answers 411 without a length).
- Ids from answers are checked against `[A-Za-z0-9!_.,:-]{1,400}` before they go into a URL; anything else is skipped
  (a listing) or refused ("an id that can't be used"). Paths go in percent-encoded.
- Retries and backoff are the shared client's ([shared pieces](#shared-pieces)): `429` and `503` for any
  method (SharePoint did nothing), other `5xx` and no answer for GET only; up to 4 retries; `Retry-After` (seconds) is
  obeyed by every later request of the source, and one over 60 seconds fails at once; without it 2^attempt seconds.
  A POST/PUT/PATCH/DELETE that got no answer fails with "the change may or may not have been made".
- A JSON answer over 32,000,000 bytes, nested deeper than 64, or one that isn't a JSON object, is an error; `error.code` and `error.message`
  are cut at 100 and 1,000 characters; versions at 1,024.
- HTTP errors: 401 `token`, 403 `denied`, 404 missing, 409/412 conflict, 423 `locked`, 507 `full`, others `refused`.

## Data tables

The tables are data, kept apart from the code at the top of `GraphSource.cs` and `ConnectorCommon.cs`, so that a
`spec/connectors/graph.json` could generate them later, as `spec/commands/` does for commands:

| Table | In the code | |
|---|---|---|
| Messages | `GraphSource.Messages`, `ReadOnly`, `Expected` | every text the agent or host sees from this connector |
| Limits | `SimpleUpload`, `Chunk`, `Page`, `MaxListRequests`, `MaxSiteDepth`, `MaxPath`, `MaxCopyBytes`, `MaxCopyEntries`, `MaxItemBytes`, `MaxVersion` | |
| Name rules | `Illegal` (`" * : < > ? / \ \|`), `Reserved` (`CON PRN AUX NUL COM0-9 LPT0-9 .LOCK DESKTOP.INI`, compared upper-case) | plus: no leading or trailing space, no trailing dot, no `_vti_` anywhere (any case), no `~$` start, not `forms` at a library's top; a path over `MaxPath` characters counting the library's server-relative path |
| Column types | `Types` (the facets looked for, in order), `EditableTypes`, `Skip`, `SetBySharePoint` | |
| Retries | `ConnectorCommon.MaxRetries`, `MaxRetryAfter`, `MaxResponse`, `MaxJsonDepth`, `Messages` | shared by every connector |
| Names from titles | `ConnectorCommon.SafeName`, `Unique`, `Slash`, `NameBytes` | shared by every connector |

## The fake and the shared cases

`tests/Topwater.Tests/GraphFake.cs` is a fake Graph, an `HttpMessageHandler` that answers the requests above from
memory; the tests never touch the network. Its state: sites (with subsites), drives (one per library, plus a lone
OneDrive for `Drive = "me"`), drive items (folders and files with content, `eTag` `"{guid},N"` changing on every
change, `cTag` `"c:{guid},N"` changing on content only), lists (columns with types, choices, `readOnly`, `hidden`; a
template), and items (fields, `eTag`). Ids come from a counter; the clock starts at 2026-01-01T00:00:00Z and moves one
second per change.

It behaves as the real service was seen to: case-insensitive name matching with `nameAlreadyExists`; path and id
addressing; children paging with `@odata.nextLink` (`PageSize` settable for tests); `content` answering 302 to a
pre-signed download URL on another host (`DownloadHost`, to test that it gets no token: any request that carries the
token to a non-Graph host is recorded in `Leaks`, and every test asserts there are none); simple PUT up to 4 MiB;
upload sessions with `Content-Range` checks; PATCH rename/move; DELETE; `conflictBehavior`; `If-Match` with 412; list
columns, `items?expand=fields`, fields PATCH (refusing read-only columns), POST, DELETE; and injected faults
(`AddFault`: method, path pattern, status, headers, body, count). `Outline()` prints every library path (with short
content) and list item (id, title, fields as JSON) in the text the Python fake printed, so the cases recorded from the
Python version still compare exactly.

`tests/connectors/graph/cases.json` holds the shared cases; its `about` says the format. `GraphTests.SharedCase` runs
each against the fake and compares every `expect` exactly. The hostile cases that need the source's internals (a next
link to another host, a plain-http download, throttling storms with the waits replaced, cancellation during a backoff,
malformed and deeply nested JSON, illegal names sending nothing, 400-character paths, deep trees, an item deleted
between list and read, 412s, partial apply, a folder someone added to, internal addresses refused by the default
transport) are tests in `GraphTests.cs`, ported from the Python suite.

## Live tests and the example

`GraphTests.LiveSiteListsReadOnly` runs only with `TOPWATER_LIVE_GRAPH=1`, `TOPWATER_GRAPH_SITE` (a site URL) and
`TOPWATER_GRAPH_TOKEN` (an access token got elsewhere); it mounts the site read-only and lists it. It never changes
the tenant and never prints the token. The Python suite on the `parked/connectors` branch has write tests (a scratch
folder `Documents/zz-topwater-test-<time>` removed with `permanentDelete`, scratch items titled `zz topwater ...`);
they are not ported yet.

`examples/GraphMount` signs in with Azure.Identity's `DeviceCodeCredential` (settings `GRAPH_TENANT_ID`,
`GRAPH_CLIENT_ID`, `GRAPH_SITE_URL` from the environment or `~/.config/topwater/graph.env`), mounts the site staged,
lets a model (through OpenRouter) or a bash script restructure it, prints the change set with the source's notes, and
applies it only with `--apply`.

## Shared pieces

`Connectors/ConnectorCommon.cs` holds what any connector needs, internal to the package:

- **`ConnectorApi`**: one HTTP client per source. Asks the host's token function before every attempt (never for a
  pre-signed URL); spaces requests at `Rate` a second; retries 429 and 503 for any method and other 5xx or no answer
  for GET only, obeying `Retry-After` (every later request of the source waits, not only the one that got it) up to
  `MaxRetryAfter`, at most `MaxRetries` times; reads at most `MaxResponse` bytes of an answer (or the caller's limit for
  a download); turns an error status into `ApiError(status, code, message)`. `JsonAsync` sends and expects JSON
  objects; `SendAsync` is the raw request. Every wait goes through `Delay` (tests replace it) and honours the token.
- **`FrontMatter(values, comment)` / `SplitFrontMatter(path, text)`**: a record's fields as `---` lines of
  `name: JSON value`, sorted by name, with an optional `# comment` line. The parser also takes the YAML an agent tends
  to write instead (plain text, `[a, b]`, `- item` lists, `true`, `~`).
- **`SafeName(title, fallback)` and `Unique(names)`**: a title as one file name (control characters to spaces, `/` to
  `∕`, `.` and `..` bracketed, cut at `NameBytes` of UTF-8), and `Name (2)` for duplicates.
- **`Messages`**: the failures of the shared pieces (no answer, busy, asked to wait too long, not JSON, front matter
  syntax), with `{service}` for the connector's name for its service.

## Differences from the Python reference

The C# code keeps the Python behaviour and messages (the shared cases recorded from Python pass unchanged), except:

- **Errors.** A Graph error answer that Python let escape from a listing of a site or a list (shown to the agent as "the
  source failed") becomes the path's message (`denied`, `missing`, `refused`, ...) like every other.
- **Threads.** The framework fetches four files at a time, on threads in .NET. Site names and list columns are kept in
  concurrent maps, and a list's item names are replaced as a whole, never changed in place.
- **One columns request per list listing**, not two.
- **Site URLs** are decoded before they are encoded again, so `https://host/sites/My%20Site` and
  `https://host/sites/My Site` name the same site.
- **Dates** accept ASCII digits only.
- **Transport.** A host's `HttpMessageHandler` that follows redirects is refused (it could carry the token to another
  host); the default is the web layer's pinned handler.
- **Live tests** are a read-only listing; the write tests are not ported.
- **Notion** is not ported (its design stays on `parked/connectors`).
