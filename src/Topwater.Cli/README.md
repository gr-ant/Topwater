# Topwater.Cli

The `topwater` command: a [Topwater](https://github.com/gr-ant/Topwater) simulated Linux machine in your terminal, and
as an MCP server that gives any MCP client (Claude Desktop, Cursor, VS Code, agent frameworks) a sandboxed `bash`
tool with no code.

```bash
dotnet tool install -g Topwater.Cli --prerelease
```

Releases from `0.1.0-preview.3` on also carry a standalone binary that needs no .NET; see
[Command line and MCP server](https://github.com/gr-ant/Topwater/blob/main/docs/cli.md#install).

```console
$ topwater                                  # an interactive shell on a fresh machine
$ topwater run "seq 5 | paste -sd+ | bc"    # one command; its exit code is passed through
$ topwater mcp                              # an MCP server over stdio
$ topwater snapshot info machine.topwater
$ topwater replay run.topwater              # re-run a recorded session, report what differs
```

## Claude Desktop

Add to `claude_desktop_config.json` (Settings > Developer > Edit Config):

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

Use the full path (`~/.dotnet/tools/topwater`, or `%USERPROFILE%\.dotnet\tools\topwater.exe` on Windows), since
desktop apps often don't see your shell's `PATH`. Leave out `--state` for a fresh machine every time the app starts.
Cursor, VS Code and other clients take the same `command` and `args`.

## Security model

- The machine is simulated: an interpreter and an in-memory filesystem inside the `topwater` process. Commands never
  run on your computer, there is no network, and nothing the model types reads or writes your files.
- The host is touched only where you say so on the command line:
  - `--state FILE` and `--from FILE` read the snapshot file (checked as untrusted: size caps, path and hash checks);
    `--state` also writes it after every command.
  - `--import HOST:PATH` copies a host file or directory into the machine when it starts. It is a copy, not a mount:
    later changes on either side don't reach the other. Symlinks are skipped, never followed. Everything you import is
    visible to the model, so don't import secrets.
  - `--export PATH:HOST` copies a path out of the machine to the host when the session ends. It overwrites files of the
    same names under HOST and never deletes anything; it does not write through symlinks or outside HOST. Whatever the
    model wrote there lands on your disk, so treat it like any download.
- Limits stop runaway commands: 120 s per command (`--timeout`), a 128 MB disk (`--disk`), plus caps on memory, loops,
  output and archive extraction.
- The `topwater` process itself runs with your permissions, as any program does. The sandbox is the interpreter, not an
  OS-level jail.

## Commands

| Command | What it does |
|---|---|
| `topwater` or `topwater shell` | interactive shell: prompt, line editing, history (up/down), Ctrl+C stops the running command, `exit` or Ctrl+D quits. Output is the text the model would get (`[exit code N]` and limit notes included). With stdin piped, runs it as one script. |
| `topwater run "<cmd>"` | runs one command; stdout and stderr go to yours, and the exit code is passed through. Several words are joined with spaces and parsed by the shell, like `ssh`. |
| `topwater mcp` | MCP server over stdio with one tool, `bash` (one string argument `command`), with the same output as `BashTool` (and its description, minus the sentence about app programs, since there are none). Calls run one at a time. Logs go to stderr. |
| `topwater snapshot save FILE` | builds a machine from the options (e.g. `--import`) and writes it to FILE |
| `topwater snapshot load FILE` | interactive shell on FILE; FILE is not changed |
| `topwater snapshot info FILE` | checks FILE and prints its creation time, user, files and session size (and calls, for a recording) |
| `topwater export FILE` | writes the machine from the options to FILE as a `.topwater` file; a source with history (a recording, or an older `.jsonl` one) keeps it |
| `topwater import FILE --state STATE` | checks FILE (`.topwater`, snapshot or older `.jsonl` recording) and makes its machine the state file |
| `topwater replay FILE` | replays a recording on a fresh machine; exit code 0 when every call matches, 1 with a report when one doesn't |

Options, for every command: `--state FILE`, `--from FILE` (start from FILE when there is no state yet; never written),
`--import HOST:PATH` (repeatable), `--export PATH:HOST` (repeatable), `--timeout SECONDS`, `--disk MB`.

State files, snapshots and recordings are all `.topwater` files (older `.zip` snapshots load as they are). A
recording is refused as `--state`, since the state file is rewritten as the machine alone: use `--from` or `import`. Container
images are not made here: that is `Topwater.Containers`, from code, and nothing the model runs can reach it.
