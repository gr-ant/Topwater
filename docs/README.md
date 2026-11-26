# Topwater documentation

Start with the [README](../README.md) for the quickstart. These pages are the reference. Samples are C#; the
[Python](../python/README.md) and [TypeScript](../typescript/README.md) pages show the same calls in their own names
and say where each implementation differs.

| Page | What is in it |
|---|---|
| [The machine](machine.md) | The shell language, the built-in programs, `git`, files and permissions, documents, what is not there |
| [Your programs](programs.md) | Your functions, JSON Schema tools, MCP servers, authenticated HTTP APIs and real host programs as commands |
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
| [CHANGELOG.md](../CHANGELOG.md) | What changed between releases |
| [AGENTS.md](../AGENTS.md) | Rules for contributors, human or AI |
