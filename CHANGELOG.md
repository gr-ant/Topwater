# Changelog

Topwater is in preview: APIs and file formats may change between previews. The .NET packages are on NuGet; the
Python and TypeScript packages are not published yet and are used from the repository.

## Unreleased (0.1.0-preview.3)

Everything on `main` since `0.1.0-preview.2`.

### Compatibility

- **Snapshots are format 2.** A default machine now holds symbolic links (`/etc/os-release` and three more), so every
  machine this version saves is snapshot format 2. `0.1.0-preview.2` refuses such a file and asks for an upgrade. This
  version still loads format 1.
- **Recordings are `.topwater` files.** A recording is saved as one zip that holds the machine, the calls and the
  checkpoints, the same file a snapshot is. Older `.jsonl` recordings still load, and saving one again (or
  `topwater export`, or a test run with `TOPWATER_RERECORD=1`) converts it.
- **`command not found` is exactly bash's line.** `bash: line 1: python3: command not found`, status 127, with no
  `(run 'help' ...)` hint after it.
- **Error messages follow bash and GNU.** The shell's own errors start `bash: line N:`, and file and usage errors
  use the GNU wording. Code that matched Topwater's earlier messages needs a look.
- **The default tool description changed:** it now says that git works on local repositories, and on a shell with
  web access it names the sites and methods. Integrations compute it per shell (`BashTool.DescriptionFor(shell)`,
  `tool_description(shell)`, `BashTool.descriptionFor(shell)`) unless you pass your own.
- **`curl` and `wget` exist.** Without a web policy they fail as on an offline machine (`Could not resolve host`,
  exit 6 and 4) where they used to be `command not found` (127).
- **On a UI thread, .NET calls run on the thread pool.** Called from a thread with a synchronization context
  (WinForms, WPF, MAUI, Avalonia), the public async methods (`RunAsync`, `BashTool.ExecuteAsync`, the `AIFunction`,
  replay, MCP and container methods) move their work to the thread pool, so blocking on them no longer deadlocks.
  Host commands then run on a pool thread, not the UI thread; a handler that touches UI objects must marshal itself.
  Console apps and ASP.NET Core are unchanged. See [docs/programs.md](docs/programs.md#your-own-commands).
- **TypeScript needs Node 22.3 or later** (was 22).
- **The CLI refuses a recording as `--state`,** because the state file is rewritten as the machine alone. Use
  `--from`, or `topwater import`.
- **The npm package declares no framework peers.** `ai` and `@openai/agents` are no longer optional peer
  dependencies; install the SDK you use. The package now packs only `dist`.

### The machine

- **`git`** for local repositories, in all three implementations: `init`, `add`, `commit`, `status`, `log`, `diff`,
  `branch`, `checkout`, `switch`, `merge` with conflict markers, `stash`, `tag`, `reset`, `restore` and plumbing. The
  repository is git's own on-disk format, so real git reads it. No network and no packfiles.
- **Symbolic and hard links:** `ln`, `readlink`, link-aware `ls`, `cp`, `mv`, `rm`, `find`, `grep -r`/`-R` and `tar`.
- **Commands are found through `PATH`,** as in bash, with its `hash` table; `#!` scripts count as a nesting level.
- **A more convincing Ubuntu:** `/etc/profile`, `/etc/profile.d`, `/etc/skel`, `/proc/self`, the usual folders under
  `/usr` and `/var`, login shells that read the profile, and places an ordinary user cannot read.
- **`apt`, `apt-get`, `apt-cache` and `dpkg`** exist and say that nothing can be installed.
- **Closer to the real tools:** `cmp`, `diff -r`, `sed` `r` and `R`, `cp --backup`, `date -I` and `%:z`,
  `ls -s`, `--time-style` and `--full-time`, `umask`, `env` options, `awk` division by zero as in mawk.

### Web access

- **Allow-listed web access** in all three implementations: `shell.AllowWeb(new WebPolicy { ... })`,
  `shell.allow_web(WebPolicy(...))`, `shell.allowWeb({ ... })`. `curl` and `wget` reach the hosts you list
  (`name`, `*.name`, `name:port`) with the methods you list, `GET` by default.
- **Guarantees:** internal and private addresses are always refused, connections go to the address that was
  checked, every redirect is checked like the first URL, and nothing of your process's proxy settings or cookies is
  used.
- **Credentials per host,** added in your code and scrubbed from what comes back. For such a host a compressed
  body is decoded before the scrub and the agent's `Range` is dropped. `RegisterApi` has the same fix.
- **A blocked host explains itself** by default (`Blocked by the web policy: ... is not an allowed host`), so the
  agent stops retrying; `ExplainBlocked = false` makes it look like an unknown host.
- **Recordings hold the web exchanges,** and a replay serves them without touching the network.

### Files, recordings and containers

- **The `.topwater` file:** one format for a machine and its history, with JSON Schemas in `spec/topwater-file`.
- **CLI:** `topwater export`, `import` and `replay`; `snapshot info` counts a recording's calls.
- **Python:** snapshots (`snapshot`, `restore`, `fork`, `save`, `load`), recordings (`start_recording`, `shell_at`,
  `replay`) and containers (`topwater.containers`).
- **TypeScript:** recordings save as `.topwater` files and load every older format.
- **Recording viewer:** compare any two moments, continue from any turn as a branch, compare branches, and download
  a branch as its own `.topwater` file.
- **Hardening:** loaders bound the number of zip entries, checkpoints and history lines; container imports count
  histories toward the limit and report bad data as one error type; the Docker replay target drops capabilities and
  caps CPU, memory and file size; host programs never follow the agent's symbolic links out of their folder.

### Integrations

- **.NET:** Microsoft Agent Framework, and the OpenAI and Anthropic SDKs, take `shell.AsAIFunction()` as it is.
- **Python:** adapters for the OpenAI Agents SDK, Claude Agent SDK, Pydantic AI, CrewAI, smolagents and Google ADK,
  next to LangChain and LlamaIndex.
- **TypeScript:** adapters for LangChain.js and LangGraph.js, Mastra and the Claude Agent SDK, next to the Vercel AI
  SDK and OpenAI Agents.
- **Claude Agent SDK:** `bash_options` (Python) and `bashOptions` (TypeScript) build options whose only tool is the
  Topwater shell, with Claude Code's own tools and the developer's settings left out.
- Calls on one shell run one at a time, whichever adapter makes them.

### Distribution and project

- **Standalone `topwater` binaries** for Linux, macOS and Windows on every release, with `install.sh` and
  `install.ps1`.
- **Every package carries** Topwater's `LICENSE` and `THIRD-PARTY-NOTICES.md`.
- **The command spec** (`spec/`): every command, option, help text and message as JSON, from which each
  implementation's tables are generated.
- **`AGENTS.md`:** the rules for contributors.

## 0.1.0-preview.2

Released 2026-11-25. The first release of `Topwater.Containers`, `Topwater.Replay` and `Topwater.Cli`. See the
[release notes](https://github.com/gr-ant/Topwater/releases/tag/v0.1.0-preview.2).

## 0.1.0-preview.1

The first release: `Topwater`, `Topwater.Mcp` and `Topwater.AI`.
