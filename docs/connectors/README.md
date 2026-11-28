# Connectors

A connector is a mount source (see [mounts.md](../mounts.md)) for a store outside the machine: it shows the store as
files and folders, and turns what the agent does to them (writes, renames, moves, new folders, deletes) back into the
store's API calls. Mounted `staged`, those become a change set the host reviews before anything is sent. The agent
never sees the token: the host passes a function that returns one, asked before every request.

Connectors are in the .NET package only, namespace `Topwater.Connectors`:

| Connector | User guide | Design | .NET |
|---|---|---|---|
| OneDrive and SharePoint | [graph.md](graph.md) | [graph-design.md](graph-design.md) | `GraphSource` |
| Notion | not in .NET | | a Python version and its design are on the `parked/connectors` branch, not ported |

A connector that needs only HTTP and JSON lives in the core package, with no package dependency; one that needs a
vendor SDK would be a separate package. Signing in stays with the host.

## What they share

`src/Topwater/Connectors/ConnectorCommon.cs` (internal):

- **`ConnectorApi`**: one HTTP client per source. Asks the host's token function before every attempt; spaces
  requests at `Rate` a second; retries 429 and 503 for any method and other 5xx or no answer for GET only, obeying
  `Retry-After` (every request waits, not only the one that got it) up to `MaxRetryAfter`, at most `MaxRetries` times;
  reads at most `MaxResponse` bytes of an answer; turns an error status into `ApiError(status, code, message)`. Every
  request and every wait honours the call's cancellation token. The default handler is the web layer's pinned one,
  which never follows redirects and refuses internal addresses; tests pass a fake `HttpMessageHandler`.
- **`FrontMatter(values, comment)` / `SplitFrontMatter(path, text)`**: a record's fields (a SharePoint list item) as
  `---` lines of `name: JSON value`, sorted by name, with an optional `# comment` line. The parser also takes the YAML
  an agent tends to write instead (plain text, `[a, b]`, `- item` lists, `true`, `~`).
- **`SafeName(title, fallback)` and `Unique(names)`**: a title as one file name (control characters to spaces, `/` to
  `∕`, `.` and `..` bracketed, cut at `NameBytes`), and `Name (2)` for duplicates. A connector keeps its own rules on
  top: Graph checks the names the agent writes against SharePoint's illegal characters and reserved names.
- **`Messages`**: the failures of the shared pieces (no answer, busy, asked to wait too long, not JSON, front matter
  syntax), with `{service}` for the connector's name for its service. Everything else a connector says is in its own
  `Messages` table, beside its limits as constants, so that `spec/connectors/<name>.json` can hold them later.

## Writing a new one

1. **Design note first**, `docs/connectors/<name>-design.md`, like [graph-design.md](graph-design.md): the file view,
   the requests, versions and conflicts, every operation, the messages and limits as tables.
2. **The source**, `src/Topwater/Connectors/<Name>Source.cs`: an `IMountSource` with `ListAsync`, `StatAsync`,
   `ReadAsync` and, as the store allows, `WriteAsync`, `RenameAsync`, `MkdirAsync`, `DeleteAsync`. Use `ConnectorApi`,
   the front matter helpers and `SafeName`; refuse anything unsupported by throwing `MountSourceException` from the
   source method, never with a check in a command. Every id or URL from a response is untrusted: check it before it
   reaches a request path, and bound listings, sizes and depth. Add a row to `tools/layout.json` and
   `docs/architecture.md`.
3. **A fake** of the service in `tests/Topwater.Tests/<Name>Fake.cs`: a stateful `HttpMessageHandler` that answers the
   requests from memory as the real service was seen to, with injectable faults and an `Outline()` of its state. Tests
   never touch the network.
4. **Cases**, `tests/connectors/<name>/cases.json`: scripts run against the fake with the expected output and
   outline; hostile cases that need the source's internals are tests of their own.
5. **Live tests, opt-in**: skipped unless `TOPWATER_LIVE_<NAME>=1` and credentials are given. They change nothing
   outside a scratch area they create and remove, and never print or record a token.
6. A user guide `docs/connectors/<name>.md`, an example in `examples/`, and a line in `CHANGELOG.md`.

## Follow-ups

- **`Note(change)` in the framework.** `GraphSource.Note(change)` explains a change the host should know about (a move
  between libraries is a copy and a delete, losing version history). The framework could ask every source for a note
  and show it in `mount -c` and `MountChange`; not done yet.
- **Spec.** Each connector's messages, limits, name rules and type tables are data in its source file, and the shared
  ones in `ConnectorCommon.Messages`; `spec/connectors/<name>.json` and a generator like `tools/spec-gen/gen.py` could
  produce them. Nothing is generated yet.
- **Live write tests** for Graph (the Python ones on `parked/connectors` work in a scratch folder and on scratch items).
