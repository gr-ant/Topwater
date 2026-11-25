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

- the number of zip entries, read from the end of the archive before any entry is listed: at most
  4 x MaxExtractEntries + 1002;
- per manifest or checkpoint: at most MaxExtractEntries files, MaxFileSystemBytes of content, MaxStringBytes of
  variables and of functions, every path normal, every blob present, of the stated size and matching its hash, text
  valid UTF-8, function bodies parsing as one function, hard links naming an earlier non-directory entry;
- at most 1000 checkpoints, holding at most 4 x MaxFileSystemBytes of content together;
- `history.jsonl` and each manifest at most 4 x MaxStringBytes + 1024 x MaxExtractEntries bytes;
- an old recording's side-car snapshot names: segments of `A-Z a-z 0-9 . _ -` separated by `/`, no segment of only dots.
