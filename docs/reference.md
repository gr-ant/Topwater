# Topwater reference

Everything the simulated machine supports, in one place.

- [Shell language](#shell-language)
- [Built-in programs](#built-in-programs)
- [Your own commands](#your-own-commands)
- [MCP servers](#mcp-servers)
- [Files and permissions](#files-and-permissions)
- [Documents](#documents)
- [Real host programs](#real-host-programs)
- [The tool description](#the-tool-description)
- [Limits and safety](#limits-and-safety)
- [Not supported](#not-supported)

## Shell language

| Area | Supported |
|---|---|
| Basics | quoting and escapes, `$VAR`, `$(...)`, backticks, `~`, globs (`*` `?` `[...]`), comments |
| Expansion | braces `{a,b}` `{1..10}` `{01..05}`, process substitution `<(...)`, arithmetic `$(( ))` |
| Parameters | `${x:-d}` `${x:=d}` `${x:+a}` `${x:?msg}` `${#x}` `${x%pat}` `${x%%pat}` `${x#pat}` `${x##pat}` `${x/a/b}` `${x//a/b}` `${x:1:3}` `${x^^}` `${x,,}` |
| Control flow | `if`/`elif`/`else`, `for x in`, `for ((;;))`, `while`, `until`, `case`, `break`, `continue` |
| Functions | `name() { }`, `function name { }`, `local`, `return`, `$1` `$@` `$#`, `shift` |
| Arrays | `a=(x y)`, `a+=(z)`, `a[i]=v`, `${a[i]}` `${a[-1]}`, `"${a[@]}"`, `${#a[@]}`, `${!a[@]}`, `${a[@]:1:2}`, `unset 'a[i]'`; associative with `declare -A`; `read -a`, `mapfile -t` |
| Tests | `[ ]`, `test`, `[[ ]]` with globs, `=~`, `&&`, `\|\|`, `(( ))`, `let` |
| Lists | `\|` `&&` `\|\|` `;` `!` `{ ...; }` `( ... )`, `VAR=x cmd` |
| Redirects | `>` `>>` `<` `2>` `2>&1` `&>`, heredocs (`<<EOF`, `<<'EOF'`, `<<-`), `<<<`, redirects on loops |
| Scripts | `./script.sh args` (needs `chmod +x`), `bash script.sh`, `bash -c`, `source`, `eval`, `exit`, `set -e`, `set -o pipefail` |

State persists between tool calls: working directory, variables, functions, aliases and files.

## Built-in programs

| Group | Programs |
|---|---|
| Files | `ls` `cd` `pwd` `cat` `mkdir` `rmdir` `rm` `cp` `mv` `touch` `find` `tree` `stat` `du` `df` `file` `realpath` `readlink` `basename` `dirname` `mktemp` `chmod` `chown` `less` `more` |
| Text | `echo` `printf` `grep` `head` `tail` `wc` `sort` `uniq` `cut` `sed` `tr` `tee` `xargs` `awk` `jq` `diff` `nl` `tac` `rev` `paste` `column` `seq` |
| Documents and binary | `to-markdown` `xxd` `hexdump` `od` `strings` `base64` `md5sum` `sha1sum` `sha256sum` `sha512sum` |
| Shell | `export` `unset` `env` `printenv` `set` `shift` `local` `declare` `read` `mapfile` `readarray` `alias` `unalias` `history` `which` `type` `command` `test` `[` `true` `false` `bash` `sh` `source` `eval` `let` `sleep` `timeout` `time` `help` |
| System | `uname` `whoami` `hostname` `date` `ps` `id` `uptime` `free` `nproc` `sudo` `su` |

`help` lists everything, including your commands. Standard tools answer `--version` and `--help` the way the
real ones do. `find` supports `-name -iname -path -regex -type -size -mmin -mtime -newer -empty -maxdepth
-mindepth -print -print0 -exec -delete` with `!`, `-o` and parentheses. `sed` is a full engine: addresses, `{}` blocks, hold space, labels and branches, `-i`, `-E`.
`awk` covers patterns, ranges, `BEGIN`/`END`, fields, arrays, control flow, user-defined functions, `getline`
from files, `printf`, and the string functions. `jq` is a real parser with about 90 builtins: object
construction, string interpolation, `select`, `map`, `sort_by`, `group_by`, `to_entries`, `reduce`, `@csv`,
`--arg`, `-r`, `-e`, `-s`.

## Your own commands

```csharp
public class Tools
{
    [Command("orders", Description = "List orders")]
    public Task<List<Order>> Orders([Description("Filter by status")] string? status = null, int limit = 20) => ...;
}

var shell = new Shell().Register(new Tools());
shell.Register("deploy", async ctx => { ...; return 0; });
```

Parameters become `--kebab-case` options and positionals, `bool` becomes a flag, enums become choices, and
lists collect the remaining arguments. `--help` is generated. Return a string (printed), an int (exit code),
nothing, or any other value (printed as JSON). Errors you raise become `command: message` on stderr with a
non-zero exit code; they never crash the agent loop.

## MCP servers

`Topwater.Mcp` mounts an MCP server as one program with a subcommand per tool. It uses the official
`ModelContextProtocol` SDK; the core `Topwater` package has no dependencies.

```csharp
using Topwater.Mcp;

await using var mount = await shell.MountMcpAsync("github", client);                       // an McpClient you created
await using var files = await shell.MountMcpAsync("files", "npx", ["-y", "@modelcontextprotocol/server-filesystem", "/data"]);
```

| The agent types | What happens |
|---|---|
| `github` or `github --help` | lists the tools with the first line of each description |
| `github list-issues --help` | usage, description and options built from the tool's JSON Schema |
| `github list-issues --repo acme/api --limit 5` | calls the tool; the text result goes to stdout |

How arguments map:

- Property `include_drafts` becomes `--include-drafts` (the original spelling also works). Tool names work
  both ways too: `list-issues` and `list_issues`.
- `integer` and `number` are checked before the call; a bad value is a usage error (exit 2), not a tool call.
- `boolean` is a flag: `--draft`, `--no-draft`, or `--draft false`.
- `array` options repeat (`--label a --label b`) or take a JSON array.
- `object` options take JSON. `--json '{...}'` passes the whole argument object, and `--json -` reads it from
  stdin; individual options override it, wherever they appear.
- `Optional[T]` (`anyOf` with `null`) and local `$ref`s are followed. A property that accepts several types is
  passed as JSON when it parses as JSON and as text otherwise.
- A tool error goes to stderr with exit code 1, so `||` and `set -e` work as usual.

`McpMountOptions.Include` hides tools by name, and `Description` sets the text shown in `help`. The calls are
real and reach the server, so expose only what you would let the agent do. Tools only: resources and prompts
are not mounted, images come back as a placeholder, resource links as their URI, and the tool list is read once
at mount time. A server that times out or dies makes the command fail; it does not end the agent's run.

The same mapping is available without MCP for any tools you can describe with a JSON Schema:

```csharp
shell.RegisterTools("crm", specs, async (tool, arguments, ct) => new ToolResult(await Call(tool, arguments)));
```

## Files and permissions

The filesystem is in memory. Put data anywhere through `shell.Fs`:

```csharp
shell.Fs.CreateDirectory("/home/agent/data", parents: true);
shell.Fs.WriteAllText("/home/agent/data/config.json", text);
shell.Fs.WriteAllBytes("/home/agent/logo.png", data);
```

The agent is a regular user called `agent`, not root:

- It owns its home directory and can use `/tmp`.
- `/etc`, `/usr`, `/bin`, `/var/log` and `/root` belong to root. Writing there fails with `Permission denied`,
  and `sudo` answers `agent is not in the sudoers file.`
- `chmod` is real: a script needs `chmod +x` before `./script.sh` runs, and `ls -l` shows the actual modes.
- Your app is not restricted. Files it creates belong to whoever owns the directory, so data you put in the
  agent's home is the agent's to edit, and data you put in `/etc` is read-only for it.

The machine looks like a lived-in Ubuntu 24.04 install: the standard folders, `/etc` files such as `os-release`,
`passwd`, `hosts` and `group`, a `/proc` whose `cpuinfo` and `meminfo` agree with `nproc` and `free`, the usual
dotfiles in the agent's home, and `/bin` and `/usr/bin` listing every program (yours included) with plausible
sizes and install dates. Shell built-ins such as `cd` and `eval` are not listed as programs, as on a real system.

## Documents

`to-markdown FILE...` turns Word, Excel, PowerPoint, PDF, HTML, CSV and JSON files into Markdown, so the agent
can search them with `grep` like any text. From your app:

```csharp
shell.Fs.AddDocument("/home/agent/docs/policy.docx", "/real/path/policy.docx");   // or bytes; alsoMarkdown: true adds policy.docx.md
string text = MarkdownConverter.ToMarkdown(bytes, "report.xlsx");
```

| Format | What comes through |
|---|---|
| `.docx` | headings, paragraphs, bold/italic, lists, tables, links |
| `.xlsx` | each sheet as a table; cached formula results |
| `.pptx` | each slide's title, text, tables and speaker notes |
| `.html` | headings, lists, links, tables, code; scripts, styles and navigation dropped |
| `.csv` `.tsv` `.json` | a table, or a formatted code block |
| `.pdf` | text, using the `pdftotext` program if it is installed on your machine |

No extra packages are needed. Images, charts, footnotes and merged cells are skipped, and Excel dates come
out as serial numbers. Only files are byte-exact; data sent through a pipe is treated as text.

## Real host programs

Some tools are too big to re-implement: git, OfficeCLI, ffmpeg, your own binaries. You can expose a real
program from your machine as a command:

```csharp
using var git = shell.RegisterHostCommand("git");
```

Before each call the agent's workspace (its home directory by default) and `/tmp` are copied to a private folder
on disk; the program runs there, and its changes are copied back.

| Option | Default | What it does |
|---|---|---|
| `Workspace` | the agent's home | the folder mirrored to disk and used as the program's `HOME` |
| `SharedDirectories` | `["/tmp"]` | other folders the program can see |
| `Environment` | empty | extra variables, e.g. `OFFICECLI_NO_AUTO_RESIDENT=1` |
| `Timeout` | 120 s | the program is killed and the command exits 124 |
| `Exclude` | empty | file or folder names left out of the copy in both directions |
| `MaxOutputBytes` | 1,000,000 | output beyond this is dropped |
| `RewritePaths` | on | redirect path arguments into the private folder |
| `Launcher` | none | wrap the command line, for example to run it inside a sandbox |

What the program sees:

- **Paths.** `/home/agent/a.txt`, `--out=/tmp/x`, `src=/tmp/a.md` and `-o/tmp/x` are redirected into the private
  folder, and `/` is that folder's root. A path in a folder that isn't shared (`/etc/hosts`, `/data/x`) is refused
  with a message telling the agent to copy the file to its home first. Paths in its output are shown as the
  agent's paths.
- **Environment.** The agent's own variables (`export GIT_AUTHOR_NAME=...`), then `PATH` from your process,
  `HOME`, `USER`, a private `TMPDIR`, and your `Environment` on top. Nothing else from your process is passed.
- **File modes.** The execute bit travels both ways. Other permission bits and timestamps do not.
- **Symlinks.** A link the program creates stays on disk for later calls but is not visible in the agent's
  filesystem, and is never written through.
- **Exit codes.** The program's own, or 124 on timeout and 126 if it cannot be started.

Programs that keep running in the background do not fit well: the call returns once the main process exits, and
whatever the helper writes later is not copied back. Turn that behaviour off if the program allows it (OfficeCLI:
`OFFICECLI_NO_AUTO_RESIDENT=1`). Every call copies the whole workspace, so keep large files out of it or list
them in `Exclude`.

**This is not sandboxed.** The program is real and runs with your process's permissions. Topwater points
it at the private folder and redirects path arguments there, which stops casual wandering but is not a security
boundary: a path whose top-level folder exists nowhere (`-o /report.pdf`) is passed through as typed, and the
program can open any file your process can. Wrap only tools you would let the agent use directly, and use `Launcher` with a real sandbox
(bubblewrap, firejail, a container) when the stakes call for it.

## The tool description

Give the model one tool: `BashTool.Name`, `BashTool.Description`,
`BashTool.InputSchemaJson` and `BashTool.ExecuteAsync(shell, command)`.

The default description tells the model the truth: it is in a sandboxed shell, Python, git and the network are
not available, and your programs are live. In testing this beat both pretending to be an ordinary machine and
announcing "this is a simulation" (see the README).

## Limits and safety

- Nothing the agent runs touches your machine, unless you register a real host program or mount an MCP server.
- Loops stop after 100,000 iterations (`MaxLoopIterations`), and one tool call gets ten times that in total.
  Hitting a limit ends the whole tool call: runaway loops, functions or scripts that call themselves, `sed`
  branches and `jq` generators that never finish, and regular expressions that take more than 5 seconds.
- Tool output is cut at 30,000 characters with a note saying how much was dropped. Error output is cut
  separately, so the error message and exit code are always shown.
- `set -e` and `pipefail` reset after every tool call, so one stray `set -e` can't affect later calls.
- A command that throws becomes an error message and exit code, not a crash.
- One shell is one session. Use a separate `Shell` per conversation; a shell is not safe to share between
  threads.

## Not supported

`select` menus, background jobs (`&` runs things in order), `>(...)`, running shell commands from
inside `awk` (`print | "cmd"`), symlinks and `ln`, group permissions, and a real `python` or network access.
`sleep` really waits, so wrap calls in your own timeout if that matters.
