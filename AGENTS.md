# Working on Topwater

Rules for anyone changing this repository, human or AI agent. Topwater has three implementations, C# (`src/`),
Python (`python/`) and TypeScript (`typescript/`). C# leads: new features (connectors, integrations, limits) go into C#
only. Python and TypeScript are frozen: they keep passing their suites and get fixes, but no new features, and where
they already share a behaviour with C# they must keep matching it.

## Commands are specified in JSON first

Every built-in command, subcommand, option and message is described in [`spec/`](spec/README.md) before any port
implements it:

1. Write `spec/commands/<name>.json` (schema: `spec/command.schema.json`). If the schema can't express what the
   command needs, extend the schema and `tools/spec-gen/gen.py` first.
2. Run `python3 tools/spec-gen/gen.py` to regenerate the tables for all three ports.
3. Implement the command in each port against the generated table. No port hard-codes a summary, help text,
   option table or message.
4. Record its behaviour from real Ubuntu in `tests/corpus/` with `tests/corpus/record.py`, then run all three suites.

When ports disagree, real bash on ubuntu:24.04 decides. The full checklist is in
[spec/README.md](spec/README.md#adding-or-changing-a-command).

## Clean room

- Write every help text, summary, option description and doc in your own words. Never copy GNU, bash or other GPL
  code or prose, not even temporarily in a commit. Short error messages that real programs print (observable
  behaviour) may match.
- Third-party code or text under a permissive licence needs an entry in `THIRD-PARTY-NOTICES.md` (the copies in
  `python/` and `typescript/` must match it; a test checks).

## Safety

- The agent's `bash` tool must never reach the host: no real files, processes, network or container export/import.
  Anything that touches the host is opt-in by the host application.
- Everything an agent or a loaded file controls is untrusted: bound its size, time and depth, and test hostile input.

## Checking against real bash

Run probes with a time limit and without container logs. A runaway probe once filled the disk:

```sh
timeout 60 docker run --rm --log-driver none ubuntu:24.04 bash -c 'timeout 5 ...'
```

## Source layout

The three ports keep a mirrored layout: the same files, sections and command order, mapped in
[docs/architecture.md](docs/architecture.md). Keep it when you add or move code; `tools/check_layout.py` enforces it
against `tools/layout.json`.

## Tests

| Port | Command |
|---|---|
| C# | `dotnet test -c Release` |
| Python | `cd python && python -m pytest -q` |
| TypeScript | `cd typescript && npm ci && npx vitest run && npm run typecheck` |
| Spec | `python3 tools/spec-gen/gen.py --check && python3 tools/spec-gen/test_gen.py` |
| Layout | `python3 tools/check_layout.py` |

A few tests measure real time and can fail on a heavily loaded machine; rerun them alone before believing them.

## Commits

Plain messages in the style of `git log`. No AI attribution trailers (`Co-Authored-By`, session links).
