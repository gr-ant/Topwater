# Command line and MCP server

`topwater` is the simulated machine as a program: a shell in your terminal, and an MCP server that gives any MCP
client (Claude Desktop, Claude Code, Cursor, VS Code) a sandboxed `bash` tool with no code.

- [Install](#install)
- [Commands](#commands)
- [Options](#options)
- [MCP server](#mcp-server)
- [Docker](#docker)
- [What touches your computer](#what-touches-your-computer)

## Install

As a .NET tool (needs .NET 10 or later):

```bash
dotnet tool install -g Topwater.Cli --prerelease
```

As a standalone binary, no .NET needed. Releases from `0.1.0-preview.3` on carry binaries for Linux, macOS and
Windows (x64 and arm64); until that release is out, use the .NET tool or build from source.

```bash
curl -fsSL https://gr-ant.github.io/Topwater/install.sh | sh          # Linux, macOS: installs to ~/.local/bin
```

```powershell
irm https://gr-ant.github.io/Topwater/install.ps1 | iex               # Windows: then topwater works in PowerShell and CMD
```

The scripts check the download against the release's `SHA256SUMS`. `TOPWATER_VERSION` picks a release tag and
`TOPWATER_INSTALL_DIR` the folder. The binaries are also on the [releases
page](https://github.com/gr-ant/Topwater/releases).

From source: `dotnet run --project src/Topwater.Cli -- run "ls /"`.

## Commands

```bash
topwater                                      # an interactive shell on a fresh machine
topwater run "ls -la /etc | head"             # one command; its exit code is passed through
topwater mcp --state ~/.topwater/m.topwater   # an MCP server over stdio; the machine is kept across restarts
topwater snapshot info machine.topwater
topwater replay run.topwater                  # re-run a recorded session and report what differs
```

| Command | What it does |
|---|---|
| `topwater` or `topwater shell` | An interactive shell: prompt, line editing, history, Ctrl+C stops the running command, `exit` or Ctrl+D quits. Output is the text the model would get, `[exit code N]` and limit notes included. With stdin piped, runs it as one script |
| `topwater run "<cmd>"` | Runs one command. stdout and stderr go to yours, and the exit code is passed through. Several words are joined with spaces and parsed by the shell, like `ssh` |
| `topwater mcp` | An [MCP server](#mcp-server) over stdio with one tool, `bash` |
| `topwater snapshot save FILE` | Builds a machine from the options (for example `--import`) and writes it to FILE |
| `topwater snapshot load FILE` | An interactive shell on FILE. FILE is not changed |
| `topwater snapshot info FILE` | Checks FILE and prints its creation time, user, files and session size, and for a recording its calls |
| `topwater export FILE` | Writes the machine the options give (`--state`, `--from`, `--import`) to FILE as a [`.topwater` file](snapshots-and-recordings.md#the-file-format). A source with history (a recording, or an older `.jsonl` one) keeps it, which also converts old recordings |
| `topwater import FILE --state STATE` | Checks FILE (a `.topwater` file, a snapshot or an older `.jsonl` recording) and writes its machine to STATE. For a recording that is the machine after its last call |
| `topwater replay FILE` | Replays a recording on a fresh machine and prints the report. Exit code 0 when every call matches, 1 when one does not. A run without a virtual clock is compared with `ReplayNormalizers.Volatile` |

## Options

Every command takes the same options:

| Option | Effect |
|---|---|
| `--state FILE` | Load the machine from FILE if it exists. Save it after every command (through a temporary file) and on exit. If FILE's folder cannot be written, `shell`, `run` and `mcp` stop at start (exit code 2); a save that fails later makes them exit with 1 |
| `--from FILE` | Start from FILE when there is no `--state` file yet. FILE is never written |
| `--import HOST:PATH` | Copy a host file or directory into the machine at start, owned by the agent. A copy, not a mount; symlinks are skipped. Refused if it would not fit on the simulated disk. Repeatable |
| `--export PATH:HOST` | Copy PATH out to the host on exit. Overwrites same-named files, deletes nothing, never writes through a host symlink or outside HOST. Repeatable |
| `--timeout SECONDS` | Time limit for one command (default 120) |
| `--disk MB` | Size of the simulated disk (default 128) |

State files, snapshots and recordings are all `.topwater` files, and older snapshot zips load as they are. A
recording is refused as `--state`, because the state file is rewritten as the machine alone and the history would be
lost: start from it with `--from`, or copy its machine to a new state file with `topwater import`.

## MCP server

`topwater mcp` serves one tool, `bash`, with one string argument `command`. The name, schema and output text are
those of `BashTool`, `[exit code N]` and limit notes included. The description is `BashTool`'s without the last
sentence about the app's own programs, since the standalone server has none. Calls run one at a time. Logs go to
stderr. When its input closes, the server answers every request it has already read, then stops, so a script can pipe
requests in and close the pipe.

Claude Desktop (`claude_desktop_config.json`, under Settings > Developer > Edit Config):

```json
{
  "mcpServers": {
    "topwater": {
      "command": "/Users/you/.dotnet/tools/topwater",
      "args": ["mcp", "--state", "/Users/you/.topwater/machine.topwater"]
    }
  }
}
```

Use the full path to the binary (`~/.dotnet/tools/topwater` for the .NET tool, `~/.local/bin/topwater` for the
install script, `%USERPROFILE%\.dotnet\tools\topwater.exe` on Windows): desktop apps often do not see your shell's
`PATH`. Leave out `--state` for a fresh machine every time the app starts. Cursor, VS Code and other clients take the
same `command` and `args`.

## Docker

From `0.1.0-preview.5` on, each release is also a container image, `ghcr.io/gr-ant/topwater`, for linux/amd64 and
linux/arm64. Tags: the version (`0.1.0-preview.5`), `preview` for the newest release, and `latest` for the newest
release without a preview suffix (there is none yet, so use `preview`).

```bash
docker run -it --rm ghcr.io/gr-ant/topwater:preview                          # an interactive shell
docker run --rm ghcr.io/gr-ant/topwater:preview run "ls -la /etc | head"
docker run -i --rm ghcr.io/gr-ant/topwater:preview mcp                       # the MCP server over stdio
```

The entrypoint is `topwater`, so the arguments are the [commands](#commands) and [options](#options) above; with none
it opens a shell. `-i` keeps stdin open, which the MCP server and piped scripts need, and `-t` gives the shell a
terminal.

The image runs as a non-root user (`app`, uid 1654) in `/data`, which that user owns, so relative paths such as
`--state machine.topwater` land there. Keep the machine across runs in a named volume:

```bash
docker run -i --rm -v topwater:/data ghcr.io/gr-ant/topwater:preview mcp --state machine.topwater
```

For a host folder instead, on Linux add `--user "$(id -u):$(id -g)"` so the container can write it and the files are
yours: `docker run -i --rm --user "$(id -u):$(id -g)" -v ~/.topwater:/data ...`. `--import` and `--export` take paths
inside the container, so mount the host folder first: `-v "$PWD:/work" ... run --import /work/src:/home/agent/src`.

MCP clients start the container as the server. Claude Desktop (Cursor takes the same `command` and `args`):

```json
{
  "mcpServers": {
    "topwater": {
      "command": "docker",
      "args": ["run", "-i", "--rm", "-v", "topwater:/data", "ghcr.io/gr-ant/topwater:preview", "mcp", "--state", "machine.topwater"]
    }
  }
}
```

VS Code (`.vscode/mcp.json`):

```json
{
  "servers": {
    "topwater": {
      "type": "stdio",
      "command": "docker",
      "args": ["run", "-i", "--rm", "-v", "topwater:/data", "ghcr.io/gr-ant/topwater:preview", "mcp", "--state", "machine.topwater"]
    }
  }
}
```

Leave out `-v` and `--state` for a fresh machine every time. The image is the release's standalone Linux binary on
Ubuntu's chiseled .NET base (no shell, no package manager; about 50 MB), and the container sees no host files except
the ones you mount. Each image has a signed build provenance attestation:
`gh attestation verify oci://ghcr.io/gr-ant/topwater:0.1.0-preview.5 -R gr-ant/Topwater`. To build it yourself, see
[`docker/Dockerfile`](../docker/Dockerfile).

## What touches your computer

- **The machine is simulated.** It is an interpreter and an in-memory filesystem inside the `topwater` process.
  No command starts a process on your computer, there is no network and no host programs, and nothing the model
  types reads or writes your files.
- **The host is touched only where you say so on the command line.**
  - `--state` and `--from` read the file, checked as [untrusted](snapshots-and-recordings.md#loading-untrusted-files).
    `--state` also writes it.
  - `--import` copies into the machine at start. Everything you import is visible to the model, so do not import
    secrets.
  - `--export` copies out at exit. Whatever the model wrote there lands on your disk, so treat it like any download.
- **Limits stop runaway commands:** 120 s per command, a 128 MB disk, and the other [limits](limits.md).
- **The process has your permissions,** as any program does. The sandbox is the interpreter, not an OS-level jail.

None of these commands is reachable from the agent's `bash` tool, and none touches Docker. Container images are made
from code with [`Topwater.Containers`](containers.md).
