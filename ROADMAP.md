# Roadmap

Topwater is parked after `0.1.0-preview.5`: `main` is what ships, it is maintained lightly, and the work below is
either finished enough to resume on a branch or only an idea. Nothing on this page is in a release.

## Parked on branches

### `parked/connectors`: Notion and Microsoft Graph mount sources

[Mounted sources](docs/mounts.md) for two hosted stores, written as Python reference implementations:

- **Notion:** a page tree as Markdown files and folders, databases as directories of row files.
- **Microsoft Graph:** OneDrive and SharePoint document libraries, SharePoint lists and subsites.
- A shared client (paging, rate limits, retries), front matter and naming rules both use; fakes of both APIs and
  shared cases in `tests/connectors/`; examples that restructure a Notion page tree and a SharePoint site.
- Design notes for the C# and TypeScript ports in `docs/connectors/`.

Next steps:

1. A JSON connector spec, `spec/connectors/*.json`, generated into each port the way `spec/commands` is, so the
   mapping from a store to files is written once.
2. The C# and TypeScript ports, run against the shared cases.

### `parked/python3`: an opt-in `python3`

CPython 3.14 compiled to WebAssembly, from [bashkit](https://github.com/everruns/bashkit)'s build, as an opt-in
add-on in all three ports (`Topwater.Python`, `topwater[python]`, `topwater-python`). It reads and writes the
machine's files, reaches only the sites a web policy allows, and has its own corpus area recorded from `python3` on
Ubuntu. Without the add-on, `python3` stays `command not found`.

The findings of its last review are fixed. Remaining before it could ship:

- Run all three suites once more on the final branch (C# and TypeScript last ran mid-change), and a last review.
- Create the `topwater-python` projects on PyPI and npm (trusted publishers, or a token for the first publish);
  NuGet creates `Topwater.Python` on the first push. The release jobs are written but have never run.
- Known gap: two hard-link names of one file opened at once get separate buffers.

## Ideas not started

| Idea | What it would be |
|---|---|
| Approval steps for mounts | Rules that decide which staged or live changes apply on their own and which wait for a person |
| Rollback journal for mounts | Undo of applied changes through the store's own version history or trash |
| `SKILL.md` generation | A skill file describing the registered commands, MCP tools and mounts, for agents that read skills |
| AgentVFS compatibility | Mount sources that follow the [AgentVFS](https://agentvfs.org) interface, and the other way round |
| Corpus for other shells | Run Topwater's [corpus](tests/corpus/README.md) of real-bash cases against other virtual shells |
| Document search | Embedding search over the machine's documents, as a command |
| Agent runner | A small loop that runs a model against a shell, records it and reports, beyond the examples |
| Simulated network | Hosts and services inside the machine, so `curl` can reach something without real web access |

Smaller gaps between the ports are listed where they apply: the tables in [README.md](README.md) and
[docs/](docs/README.md) mark what Python or TypeScript does not have yet.
