# Topwater documentation

Start with the [README](../README.md) for the quickstart. These pages are the reference. Samples are C#; the
[Python](../python/README.md) and [TypeScript](../typescript/README.md) pages show the same calls in their own names
and say where each implementation differs.

| Page | What is in it |
|---|---|
| [The machine](machine.md) | The shell language, the built-in programs, `git`, files and permissions, documents, what is not there |
| [Your programs](programs.md) | Your functions, JSON Schema tools, MCP servers, authenticated HTTP APIs and real host programs as commands |
| [Web access](web.md) | `curl` and `wget` to the sites you allow: the policy, the guarantees, recordings |
| [Mounted sources](mounts.md) | Outside files as a folder: modes, change sets, writing a source, safety |
| [Connectors](connectors/README.md) | OneDrive and SharePoint through Microsoft Graph (.NET) |
| [Connect to a model](integrations.md) | The tool definition, and the adapters for agent frameworks |
| [Command line and MCP server](cli.md) | The `topwater` command: install, commands, options, MCP clients |
| [Snapshots and recordings](snapshots-and-recordings.md) | Undo, fork, save and load; deterministic runs; record and replay; the `.topwater` file |
| [Containers](containers.md) | A machine as a Docker image, and a container as a machine |
| [Limits and safety](limits.md) | What is simulated, what is real, and every limit |

Elsewhere in the repository:

| Page | What is in it |
|---|---|
| [python/README.md](../python/README.md) | The Python package and its agent framework adapters |
| [typescript/README.md](../typescript/README.md) | The TypeScript package, its adapters and the browser build |
| [spec/README.md](../spec/README.md) | The command spec: every command, option, help text and message, as JSON |
| [spec/topwater-file](../spec/topwater-file/README.md) | JSON Schemas for the `.topwater` file |
| [tests/corpus](../tests/corpus/README.md) | The cases recorded from real bash that all three implementations run |
| [tests/web](../tests/web/README.md) | The `curl` and `wget` cases recorded from the real programs |
| [docs/architecture.md](architecture.md) | The source layout: how the three implementations mirror each other |
| [CHANGELOG.md](../CHANGELOG.md) | What changed between releases |
| [ROADMAP.md](../ROADMAP.md) | What is parked on branches, and ideas not started |
| [AGENTS.md](../AGENTS.md) | Rules for contributors, human or AI |
