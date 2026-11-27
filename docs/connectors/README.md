# Connectors

A connector is a mount source (see [mounts.md](../mounts.md)) for a store outside the machine: it shows the store as
files and folders, and turns what the agent does to them (writes, renames, moves, new folders, deletes) back into the
store's API calls. Mounted `staged`, those become a change set the host reviews before anything is sent. The agent
never sees the token: the host passes a token, or a function asked for it before every request.

| Connector | User guide | Design for every port | Python |
|---|---|---|---|
| Notion | [notion.md](notion.md) | [notion-design.md](notion-design.md) | `topwater.connectors.notion.NotionSource` |
| OneDrive and SharePoint | [graph.md](graph.md) | [graph-design.md](graph-design.md) | `topwater.connectors.graph.GraphSource` |

Python is the reference; C# and TypeScript implement the design notes (`tools/layout.json` lists their files as
pending until they exist). A connector that needs only HTTP and JSON lives in the core package; one that needs a
vendor SDK would be a separate package.

## What they share

`python/src/topwater/connectors/common.py` (C# `Connectors/ConnectorCommon.cs`, TS `connectors/common.ts`):

- **`Api(service, token, transport, timeout=, rate=)`**: one HTTP client per source. Asks the token (a string, or a
  function, sync or async) before every attempt; spaces requests at `rate` a second under a lock; retries 429 and 503
  for any method and other 5xx or no answer for GET only, obeying `Retry-After` (every request waits, not only the one
  that got it) up to `MAX_RETRY_AFTER`, at most `MAX_RETRIES` times; caps a response at `MAX_RESPONSE`; turns an error
  status into `ApiError(status, code, message)`. `json()` sends and expects JSON objects; `send()` is the raw request
  (with `auth=False` for a pre-signed URL). The default transport is webaccess's `pinned_transport`, which never follows
  redirects and refuses internal addresses; tests pass a fake. Each connector picks its rate: Notion 3 a second, its
  documented average; Graph none, since it says when to slow down with 429 and `Retry-After`.
- **`front_matter(values, comment=None)` / `split_front_matter(path, text)`**: a record's fields (a Notion database
  row, a SharePoint list item) as `---` lines of `name: JSON value`, sorted by name, with an optional `# comment` line.
  The parser also takes the YAML an agent tends to write instead (plain text, `[a, b]`, `- item` lists, `true`, `~`).
  The rules are in [notion-design.md](notion-design.md#rows-front-matter); both connectors use exactly these.
- **`safe_name(title, fallback)` and `unique(names)`**: a title as one file name (control characters to spaces, `/` to
  `∕`, `.` and `..` bracketed, cut at `NAME_BYTES`), and `Name (2)` for duplicates. A connector keeps its own rules on
  top: Notion's names are sticky and given in creation order; Graph checks the names the agent writes against
  SharePoint's illegal characters and reserved names.
- **`MESSAGES`**: the failures of the shared pieces (no answer, busy, asked to wait too long, not JSON, front matter
  syntax), with `{service}` for the connector's name for its service. Everything else a connector says is in its own
  `MESSAGES` table, beside its limits as constants, so that `spec/connectors/<name>.json` can hold them later.

## Writing a new one

1. **Design note first**, `docs/connectors/<name>-design.md`, like the two above: the file view, the requests, versions
   and conflicts, every operation, the messages and limits as tables. The other ports implement that note.
2. **The source**, `python/src/topwater/connectors/<name>.py`: a `MountSource` with `list`, `stat`, `read` and, as the
   store allows, `write`, `rename`, `mkdir`, `delete`. Use `Api`, the front matter helpers and `safe_name`; refuse
   anything unsupported by raising `MountSourceError` from the source method, never with a check in a command. Every
   id or URL from a response is untrusted: parse it before it reaches a request path, and bound listings, sizes and
   depth. Add rows to `tools/layout.json` and `docs/architecture.md`.
3. **A fake** of the service, `python/tests/<name>_fake.py`: a stateful transport that answers the requests from
   memory as the real service was seen to, with injectable faults and an `outline()` of its state. Tests never touch
   the network.
4. **Shared cases**, `tests/connectors/<name>/cases.json`: scripts run against the fake with the expected output and
   outline, which every port runs; hostile cases that need a port's internals are written per port.
5. **Live tests, opt-in**: skipped unless `TOPWATER_LIVE_<NAME>=1` and credentials in `~/.config/topwater/<name>.env`.
   They work only in a scratch area they create and remove, check that the fields the connector reads are where the
   fake puts them, and never print or record a token.
6. A user guide `docs/connectors/<name>.md`, an example in `python/examples/`, and a line in `CHANGELOG.md`.

## Follow-ups

- **`note(change)` in the framework.** `GraphSource.note(change)` explains a change the host should know about (a move
  between libraries is a copy and a delete, losing version history). Only Graph has one. The framework could ask every
  source for a note and show it in `mount -c` and `MountChange`; that is a change to all three ports' mounts, not made
  yet.
- **Spec.** Each connector's messages, limits, name rules and type tables are data in its module, and the shared ones
  in `common.MESSAGES`; `spec/connectors/<name>.json` and a generator like `tools/spec-gen/gen.py` would produce them
  for every port. Nothing is generated yet.
- **C# and TypeScript** ports of `common`, `notion` and `graph`.
