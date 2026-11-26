# The `.topwater` file

One file for a machine and, optionally, the calls that led to it. All three ports write it and read it; these schemas
are the contract between them. A snapshot is a `.topwater` file without history.

```
spec/topwater-file/
  manifest.schema.json     manifest.json: the machine (files' metadata and the session)
  history.schema.json      one line of history.jsonl: the header, then one line per call
  checkpoint.schema.json   checkpoints/<n>.json: the machine after n calls (the manifest's shape)
```

## Layout

A zip:

| Entry | |
|---|---|
| `manifest.json` | the machine now. With history: after the last call |
| `blobs/<sha256>` | file contents, each stored once for the whole file; manifest and checkpoints name them by hash |
| `history.jsonl` | only with history: a header line, then one line per call |
| `checkpoints/<n>.json` | only with history: the machine after `n` calls. `0` (the start) is always there; `n` is `0` or a number without leading zeros, at most the number of calls |

A reader that only wants the machine reads `manifest.json` and ignores the rest, so a file with history also loads in a
Topwater that predates it. To rebuild the machine after call `i`, restore the newest checkpoint at or before `i + 1` and
run the calls after it again; that is exact for a deterministic history (`startTime` and `randomSeed` in the header)
replayed on the same port and version.

Zips are recognised by their contents (`PK` at the start), not by their name. Readers also accept the two formats this
replaced: a snapshot zip (`manifest.json` and `blobs/` only, the same thing) and a JSON Lines recording
(`"format":"topwater-recording"`, snapshots embedded as base64 zips or in side-car files).

## Versions

`manifest.json` and checkpoints keep the snapshot manifest's versioning (`"format":"topwater-snapshot"`, version 1,
or 2 when there are symbolic or hard links). `history.jsonl` starts at `"format":"topwater-history"`, version 1. A
reader refuses a newer version with an error that says so, before looking at anything that version might have changed.

## What readers check beyond the schemas

A file is untrusted input. Every port enforces the same limits, taken from the caller's shell options (never from the
file; the header's limits can only lower them):

- the number of zip entries, counted before any entry is listed (the end record's count, and the directory walked
  up to the cap): at most 4 x MaxExtractEntries + 1002. No two entries may have the same name, and none a NUL;
- per manifest or checkpoint: at most MaxExtractEntries files, MaxFileSystemBytes of content, MaxStringBytes of
  variables and of functions, every path normal, every blob present, of the stated size and matching its hash, text
  valid UTF-8, function bodies parsing as one function, hard links naming an earlier non-directory entry;
- at most 1000 checkpoints, holding at most 4 x MaxFileSystemBytes of content and 4 x MaxExtractEntries files and
  directories together, each manifest's count charged before its entries are built;
- each manifest at most 4 x MaxStringBytes + 1024 x MaxExtractEntries bytes;
- `history.jsonl` read line by line as it inflates, at most 2 x MaxStringBytes bytes and MaxExtractEntries + 1 lines
  (header and calls; blank lines count). An old `.jsonl` recording has the same line cap;
- an old recording's side-car snapshot names: segments of `A-Z a-z 0-9 . _ -` separated by `/`, no segment of only dots.
- with history, `manifest.json` must be the same machine as the last checkpoint (the same JSON, whatever its key order
  or spacing), or the file is refused: a reader of either must see one machine;
- `history.jsonl` has no byte order mark; a call's `index` is a whole number from 0, `elapsedMs` a number from 0 to
  10^12, `files` a list of paths, times ISO 8601 date and time (`2026-03-04T05:06:07Z`; no zone means UTC).

A reader may check checkpoints after the first against the counts above without building them, and build each when a
replay first needs it; anything wrong inside one (a blob, a path, the session) is then reported at that point.

## Web exchanges

A shell with web access (`AllowWeb`) adds `web` to the header: its policy without the credentials' values
(`credentials` lists only the host names) and without its handler. A call that made requests adds `web`: each request
as it went out of the policy check and what came back, in order, redirects included. A response's body is a blob
(`body` names it, `size` is its length; no `body` when empty), stored after credentials were scrubbed; a request that
failed below the policy has `error` (`resolve`, `blocked`, `connect`, `timeout`, `tls`, `recv`; `weird`: an unreadable
status line or header; `empty`: closed before any response) instead of a status. A body that ended before its
`Content-Length` or last chunk has a status, its body as far as it came, and `error: "partial"`.
Requests the policy refused are not stored: a replay under the same policy refuses them again.

A shell built from the recording serves these in order, starting at the first call it runs, and never uses the
network. A reader checks each blob as it does a file's (present, the stated size, matching its hash), every body
together at most MaxFileSystemBytes, `status` from 100 to 999, and `error` one of the nine. Files without `web` load as
before; the history version stays 1, since a Topwater without web access has no `curl` to replay anyway.

## `files`

A call's `files` is informational: the paths whose entry (type, mode, owner, mtime, content or link) differs between
the machine before and after the call, as that port sees it. It differs by port where machines do: on a shell without
a virtual clock every write moves mtimes, so parent directories show up; on a deterministic shell the clock stands
still and a write that leaves content as it was lists nothing. Never compare it across ports.
