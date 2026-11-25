# Topwater reference

Everything the simulated machine supports, in one place.

- [Shell language](#shell-language)
- [Built-in programs](#built-in-programs)
- [Your own commands](#your-own-commands)
- [MCP servers](#mcp-servers)
- [Microsoft.Extensions.AI](#microsoftextensionsai)
- [Authenticated API calls](#authenticated-api-calls)
- [Files and permissions](#files-and-permissions)
- [Documents](#documents)
- [Real host programs](#real-host-programs)
- [The tool description](#the-tool-description)
- [Limits and safety](#limits-and-safety)
- [Not supported](#not-supported)

## Shell language

| Area | Supported |
|---|---|
| Basics | quoting and escapes, `$'...'`, `$VAR`, `$(...)`, backticks, `~` and `~user`, comments, line continuations |
| Globs | `*` `?` `[...]` `[[:alpha:]]`, extglob `@(a\|b)` `!(x)` `*(x)` `+(x)` `?(x)`; `shopt` `nullglob` `dotglob` `globstar` `extglob` `nocaseglob` `nocasematch`; `set -f` |
| Expansion | braces `{a,b}` `{1..10}` `{01..05}` `{a..e..2}` `{$x,y}`, process substitution `<(...)`, arithmetic `$(( ))` and `$[ ]` |
| Parameters | `${x:-d}` `${x-d}` `${x:=d}` `${x:+a}` `${x:?msg}` `${#x}` `${x%pat}` `${x%%pat}` `${x#pat}` `${x##pat}` `${x/a/b}` `${x//a/b}` `${x/#a/b}` `${x/%a/b}` (`&` is the match), `${x:1:3}` `${x: -2}`, `${x^}` `${x^^}` `${x,,}` `${x^^[ab]}` `${x~~}`, `${x@Q}` `@E` `@U` `@L` `@u` `@a` `@A`, `${!name}`, `${!prefix*}` |
| Variables | `declare`/`typeset`/`local`/`readonly`/`export` with `-a -A -i -r -x -l -u -n -g -p -f -F`, namerefs, read-only variables; `$0 $# $@ $* $? $$ $! $- $_`, `LINENO` `FUNCNAME` `BASH_SOURCE` `PIPESTATUS` `BASH_REMATCH` `BASH_VERSION` `BASH_VERSINFO` `SECONDS` `EPOCHSECONDS` `RANDOM` `UID` `PPID` `OPTIND` `OPTARG` `REPLY` `IFS` |
| Control flow | `if`/`elif`/`else`, `for x in`, `for ((;;))`, `while`, `until`, `case` (`;;` `;&` `;;&`), `select`, `break N`, `continue N`, `time` |
| Functions | `name() { }`, `function name { }`, `local`, `return`, `$1` `$@` `$#`, `shift`, recursion, dynamic scope |
| Arrays | `a=(x y)`, `a+=(z)`, `a[i]=v` (the subscript can be an expression with spaces), `${a[i]}` `${a[-1]}`, `"${a[@]}"`, `${#a[@]}`, `${!a[@]}`, `${a[@]:1:2}` (sparse arrays slice by index), `"${a[@]/#/x}"`, `unset 'a[i]'`, `(( a[i]++ ))` anywhere in arithmetic; associative with `declare -A`; `read -a`, `mapfile`/`readarray -t -n -s -O -d -u` |
| Tests | `[ ]`, `test`, `[[ ]]` with globs, `=~` (POSIX classes, groups in `BASH_REMATCH`), `-v` (also `a[k]`), `-o option`, `&&`, `\|\|`, `(( ))`, `let` |
| Lists | `\|` `&&` `\|\|` `;` `!` `{ ...; }` `( ... )`, `VAR=x cmd`, `cmd &` with `$!` and `wait` |
| Redirects | `>` `>>` `>\|` `<` `2>` `2>&1` `>&2` `&>` `&>>` `N>file` `N<file` `N>&M` `N>&-`, heredocs (`<<EOF`, `<<'EOF'`, `<<-`), `<<<`, `/dev/stdout` `/dev/stderr` `/dev/fd/N`, redirects on loops, groups and functions, `exec` with only redirections (they last) and `exec {fd}>file` |
| Options | `set -e` (exempt in conditions, `&&`/`\|\|` lists, `!` and `$(...)`, as in bash), `-u`, `-x`, `-f`, `-C`, `-o pipefail`, `set -o`, `shopt`; options reset at the end of each tool call |
| Traps | `EXIT`, `ERR`, `RETURN`, `DEBUG` (before each simple command); other signals are accepted and never arrive |
| Scripts | `./script.sh args` (needs `chmod +x`), `bash script.sh`, `bash -c`, `bash -euo pipefail ...`, `source`, `eval`, `exit`; a child bash sees only exported variables and `export -f` functions, as in bash |
| Built-ins | `getopts`, `read -r -a -d -n -N -u -t -s -p`, `printf -v` (`%q` `%b` `%(fmt)T` `%*d`), `echo -neE`, `exec`, `wait`, `jobs`, `kill`, `trap`, `type`, `command`, `builtin`, `hash`, `pushd`/`popd`/`dirs`, `ulimit`, `times` |

State persists between tool calls: working directory, variables, functions, aliases and files.

## Built-in programs

| Group | Programs |
|---|---|
| Files | `ls` `cd` `pwd` `cat` `mkdir` `rmdir` `rm` `cp` `mv` `touch` `find` `tree` `stat` `du` `df` `file` `realpath` `readlink` `basename` `dirname` `mktemp` `chmod` `chown` `less` `more` |
| Text | `echo` `printf` `grep` `egrep` `fgrep` `head` `tail` `wc` `sort` `uniq` `cut` `sed` `tr` `tee` `xargs` `awk` `jq` `diff` `nl` `tac` `rev` `paste` `column` `seq` |
| Documents and binary | `to-markdown` `xxd` `hexdump` `od` `strings` `base64` `md5sum` `sha1sum` `sha256sum` `sha512sum` |
| Archives | `tar` (`c` `x` `t`, `-f` `-z` `-v` `-C` `-O` `--strip-components`) `gzip` `gunzip` `zcat` (`-c` `-d` `-k` `-f` `-l` `-t` `-v` `-1`..`-9`) `zip` (`-r` `-q`) `unzip` (`-l` `-o` `-n` `-d` `-p` `-q`) |
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

## Microsoft.Extensions.AI

`Topwater.AI` turns a shell into an `AIFunction`, so any `IChatClient` (OpenAI, Azure OpenAI, Ollama, ...) can use it
through the standard function-invocation middleware. It depends only on `Microsoft.Extensions.AI.Abstractions`;
`UseFunctionInvocation` comes from the `Microsoft.Extensions.AI` package.

```csharp
using Microsoft.Extensions.AI;
using Topwater;
using Topwater.AI;

var shell = new Shell();
shell.Fs.WriteAllText("/home/agent/notes.txt", "one\ntwo\n");

IChatClient client = new ChatClientBuilder(innerClient).UseFunctionInvocation().Build();
var options = new ChatOptions { Tools = [shell.AsAIFunction()] };
List<ChatMessage> history = [new(ChatRole.User, "How many lines are in notes.txt?")];

var response = await client.GetResponseAsync(history, options);
history.AddMessages(response);
Console.WriteLine(response.Text);
```

The function is named `bash`, takes one string parameter `command` (the schema in `BashTool.InputSchemaJson`) and
returns the same text as `BashTool.ExecuteAsync`, `[exit code N]` included. `shell.AsAIFunction("...")` replaces the
description; the wording matters, so measure before you change it. Models may ask for several calls at once: calls
through functions made from the same shell run one at a time, in arrival order. Calling `shell.RunAsync` yourself
at the same time is not covered.

## Authenticated API calls

The machine has no network. `RegisterApi` lets the agent call a few real HTTP APIs that you choose, with
credentials it never sees:

```csharp
shell.RegisterApi("api", new Dictionary<string, ApiEndpoint>
{
    ["jira"] = new("https://acme.atlassian.net/rest/api/3")
    {
        Description = "Issue tracker",
        Auth = () => $"Bearer {secrets.Get("jira")}",   // called by your code, for every request
        Paths = ["/issue/", "/search"],                  // optional allowed path prefixes
    },
    ["crm"] = new("https://crm.example.com/v1")
    {
        Auth = () => secrets.Get("crm"), AuthHeader = "X-Api-Key", Methods = ["GET", "POST"],
    },
});
```

| The agent types | What happens |
|---|---|
| `api --help` | lists the endpoints with their descriptions, base URLs, methods and paths |
| `api get jira /issue/PROJ-1 \| jq .fields.summary` | the response body goes to stdout |
| `api get jira /search --query 'jql=project = X'` | `--query KEY=VALUE` repeats and is URL-encoded |
| `api post crm /contacts --data '{...}'` | also `--data @file.json` and `--data -` (stdin); JSON by default |
| `api get crm /contacts -H 'Accept: text/csv' -i` | `-i` prints the status line and response headers first |

A status outside 2xx exits 1 with the status line on stderr; the body is still printed. A usage error, a method
or path that is not allowed, or a refused header exits 2 without sending anything. A failure (DNS, TLS, refused
connection, the `Timeout`, 30 s by default) is one line on stderr and exit code 1. Bodies beyond
`MaxResponseBytes` (1,000,000) are cut with a note and exit code 1; request bodies are limited to 1,000,000 bytes.
Pass an `HttpMessageHandler` as the last argument to send requests your own way (tests, proxies); it must not
follow redirects, and one that does is refused at registration.

What this guarantees, even if the agent is hostile (for example after a prompt injection):

- **The credential goes only to the base URL.** The agent picks an alias, never a host. A path must start with
  one `/` and cannot contain `\`, `@`, `#`, control characters, non-ASCII characters, `%2f`, malformed or double
  percent-escapes; `.` and `..` (also as `%2e`, and `..;`) are resolved before `Paths` is checked and may not climb
  above the base path. The final URL is checked again for the same scheme, host and port under the base path.
- **Redirects are not followed.** A 3xx is reported with its `Location`, and the agent can make a second call if
  it points back to the same API. Following one would send the credential to wherever it points.
- **The agent cannot read the credential.** It is not in `--help`, error messages or anything echoed. Its value,
  and the token after `Bearer ` or another scheme, is replaced with `[REDACTED]` in response bodies, headers and
  status lines (values shorter than 8 characters are not scrubbed). An exception from `Auth` is reported without
  its message.
- **The agent cannot override it.** It may set only `Accept`, `Accept-Language`, `Content-Type`, `If-Match` and
  `If-None-Match`, so not `Authorization`, `Cookie`, `Host`, `Proxy-*` or your credential header. Header and query
  values cannot add lines, parameters or fragments. The default handler keeps no cookies between calls.
- **Only the methods you list.** GET by default. Base URLs must be `https://`; `AllowHttp = true` permits
  `http://` (local test servers), and then the credential travels in clear.

What it does not guarantee: the agent can do anything the allowed methods, paths and the token itself permit,
including sending data it has read elsewhere to an allowed write endpoint, or reading everything the token can
read. Use a read-only token with the narrowest scope the API offers, list `Paths`, and prefer a `[Command]`
method with typed parameters for anything that writes. Scrubbing catches the credential as sent, not encoded
copies of it (base64, URL-encoded, split across JSON strings). The base URL is yours: check it for look-alike
(IDN) host names before you register it.

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
- Permissions work as on Linux: reaching a path needs search (`x`) permission on every directory above it, so a
  file you put under `/root` (mode 700) is out of reach whatever its own mode. Listing a directory needs `r`;
  creating, deleting or renaming in it needs `w`. `cp -r` skips what the agent can't read. In `/tmp` (sticky)
  the agent can delete or rename only its own entries.
- Your app is not restricted. Files it creates belong to whoever owns the directory, so data you put in the
  agent's home is the agent's to edit, and data you put in `/etc` is read-only for it. That holds while an
  agent command is running, too: only that command, and the commands you registered when it calls them, act
  as the agent.

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
- **File modes.** The execute bit travels both ways. Other permission bits do not.
- **Symlinks.** A link the program creates stays on disk for later calls but is not visible in the agent's
  filesystem, and is never written through.
- **Exit codes.** The program's own, or 124 on timeout and 126 if it cannot be started.

Programs that keep running in the background do not fit well: the call returns once the main process exits, and
whatever the helper writes later is not copied back. Turn that behaviour off if the program allows it (OfficeCLI:
`OFFICECLI_NO_AUTO_RESIDENT=1`). Only files whose size or modification time changed since the last call are
copied, but every call still lists the whole workspace, so keep very large trees out of it or list them in `Exclude`.
A synced file on disk carries the agent's modification time.

**This is not sandboxed.** The program is real and runs with your process's permissions. Topwater points
it at the private folder and redirects path arguments there, which stops casual wandering but is not a security
boundary: a path whose top-level folder exists nowhere (`-o /report.pdf`) is passed through as typed, and the
program can open any file your process can (`git -c alias.x='!cat ~/.ssh/id_rsa' x`). Wrap only tools you would let
the agent use directly, and run them in a real sandbox when the stakes call for it. On Linux with
[bubblewrap](https://github.com/containers/bubblewrap) installed:

```csharp
using var git = shell.RegisterHostCommand("git", options: new() { Launcher = HostSandbox.Bubblewrap(network: true) });
```

The program then sees only the private folder (read-write), a fresh empty `/tmp`, `/usr`, `/bin`, `/lib*`, a few
files from `/etc` needed to run and look up names, and the program itself; not your home directory or anything
else on disk. The network is off unless you pass `network: true` (git clone and push need it). Folders the
program needs besides those (a runtime under `/opt`) go in as read-only extras: `Bubblewrap(false, "/opt/tool")`.
It throws when `bwrap` is missing or cannot create a sandbox (some distributions restrict unprivileged user
namespaces). For anything else (firejail, a container) write your own `Launcher`: it gets the command line and
the private folder and returns the command line to run.

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
- Archives are untrusted. `tar` and `unzip` never write outside the target directory: names with `..` are
  refused (tar) or cleaned (unzip), leading `/` is dropped, symlinks aren't created and hard links only copy files
  inside the target. One extraction or decompression stops with an error past 64 MB (`MaxExtractBytes`) or
  100,000 entries (`MaxExtractEntries`). Binary data survives pipes and redirects (`tar czf - . | gzip -l`,
  `gzip -c f > f.gz`); in the tool output it shows as U+FFFD.
- Tool output is cut at 30,000 characters with a note saying how much was dropped. Error output is cut
  separately, so the error message and exit code are always shown.
- `set -e` and `pipefail` reset after every tool call, so one stray `set -e` can't affect later calls.
- A command that throws becomes an error message and exit code, not a crash.
- One shell is one session. Use a separate `Shell` per conversation; a shell is not safe to share between
  threads.

## Not supported

Real background jobs (`cmd &` runs to completion before the next command, so `wait` only reports its
status), job control (`fg`, `bg`, `%1`), `>(...)`, `coproc`, `compgen`/`complete`, `caller`, running shell
commands from inside `awk` (`print | "cmd"`), symlinks and `ln`, group permissions, and a real `python` or
network access. `sleep` really waits, so wrap calls in your own timeout if that matters.

A few shell behaviours differ from bash on purpose or for simplicity: the last command of a pipeline runs
in the current shell (like `shopt -s lastpipe`), so `cmd | while read ...; done` can set variables; aliases
expand in scripts too; extglob patterns work without `shopt -s extglob`; `declare -f` prints a function as
it was written; `local x` leaves `x` empty rather than unset; `set -x` traces simple commands only; and
`printf` pads by characters, not bytes.
