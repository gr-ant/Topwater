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
- [Snapshots](#snapshots)
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
| Text | `echo` `printf` `grep` `egrep` `fgrep` `head` `tail` `wc` `sort` `uniq` `cut` `sed` `tr` `tee` `xargs` `awk` `jq` `bc` `diff` `nl` `tac` `rev` `paste` `column` `seq` |
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
`bc` is an arbitrary-precision calculator: a `BigInteger` mantissa plus a scale, parsed to an AST and evaluated.
It matches GNU bc 1.07.1 digit-for-digit on `+ - * / % ^` and `sqrt`, bc's scale rules, truncation, `ibase`/`obase`
(input bases up to 16, and output bases above 16 as space-separated digit groups) and the 70-column line wrapping
with a trailing backslash, plus the full language (variables, `a[i]` arrays, `define` functions with `auto`
locals and array parameters, `if`/`while`/`for`, `break`/`continue`/`return`, `print`, strings, `length`,
`scale`, relational and boolean operators, `++`/`--`, the assignment ops, and `last`/`.`). With `-l` the math
library `s c a l e j` is available at the default scale 20; those are computed here from Taylor series and
argument reduction (not GNU's `libmath.b`) and are accurate to the requested scale, within a last digit or two
of GNU at large scales. One `bc` invocation is stopped after 5 s and any single number is capped at 100000
digits, so `while(1){}`, `9^9^9` and `scale=100000; sqrt(2)` end with an error instead of hanging; `read()` is
not supported.

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

`to-markdown FILE...` turns Word, Excel, PowerPoint, HTML, CSV and JSON files into Markdown, so the agent
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
| `.pdf` | not converted: parsing PDF needs a native program, and running one on agent-supplied bytes would reach your machine. To support PDFs, attach the real one: `shell.RegisterHostCommand("pdftotext")` (optionally under bubblewrap); the agent then runs `pdftotext -layout FILE.pdf -` |

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

## Snapshots

A snapshot is the whole machine at one moment: every file and the whole session. Use it to undo an agent's bad
turn, try several approaches from one starting point, build test fixtures once, or keep a conversation's machine
across restarts.

```csharp
ShellSnapshot before = shell.Snapshot();   // in memory
await shell.RunAsync("rm -rf src");
shell.Restore(before);                     // src is back, and so are variables, cwd, ...

Shell attempt = shell.Fork();              // an independent copy to try something on

using (var file = File.Create("machine.zip")) shell.Save(file);   // or before.Save(file)
Shell later = Shell.Load(File.OpenRead("machine.zip"));            // built-ins only: register your commands again
shell.Restore(ShellSnapshot.Load(File.OpenRead("machine.zip"), shell.Options));  // or load into a shell you configured
```

A snapshot is immutable. Later changes to the shell never reach it, restoring it twice gives the same machine, and a
fork shares nothing mutable with its parent. Taking one is cheap enough for every turn, because file contents
(strings and byte arrays, never changed in place) and arrays are shared rather than copied. Arrays are copied the
next time either side writes to them. Measured with ~3,300 files and 6.7 MB of data, 300 variables, a 20,000-element
array and 200 history entries (Release, Linux x64): `Snapshot` takes about 5 ms and allocates about 0.5 MB (one
small object per file and directory, plus copies of the session's dictionaries). `Restore` and `Fork` take about 6 ms.
`Save` takes 0.3 s and `Load` 0.2 s.

What is captured:

| Part | What |
|---|---|
| Files | every file and directory: text or exact bytes, mode (including the sticky bit), owner, modification time, and the size `ls -l` reports for a program stub |
| Variables | values and `declare` attributes (integer, readonly, nameref, lower/upper case, exported), whether a variable is exported to child shells, indexed and associative arrays (with sparse indices) |
| Functions | every definition, and which are exported with `export -f` |
| Session | aliases, working directory, `pushd` stack, `shopt` settings, `set` options, traps, positional parameters, `$?`, `$_`, `PIPESTATUS`, finished background jobs and `$!`, `getopts` position, whether `IFS` is unset, history, the start time behind `SECONDS` |

What is not captured, on purpose:

- **Per-call state.** Function locals, open file descriptors (`exec 3>file`), the time and loop budgets, and anything
  else that exists only while a call runs. Take snapshots between calls, not from inside a running command.
- **Your registrations.** Commands from `Register`, `RegisterTools`, `RegisterApi`, MCP mounts and host programs are
  code, not data. `Restore` keeps the shell's own registrations and puts the `/usr/bin` entries back in line with
  them. `Fork` shares the registered handlers with the parent, so a stateful handler is used by both (an MCP mount's
  connection serves both shells). Host programs are not carried into a fork, because they are bound to the parent's
  files and disk mirror: register them again on the fork. `Load` gives a shell with only the built-ins, so register
  your commands again, or configure a shell and restore into it as shown above.
- **`RANDOM`**, which has no seed to save, and the shell's options (`ShellOptions`: limits, user, host name), which
  belong to whoever creates the shell. Without options, `Shell.Load` uses the snapshot's user and host name.

`Restore` replaces the filesystem object's contents in place. If several shells share one `ShellOptions.FileSystem`,
they all see the restored files.

### The saved format

`Save` writes a zip file. Zip is self-describing, every platform can open it (`unzip -l machine.zip`), and the
contents of different files can be read one at a time. It contains:

- `manifest.json`: `"format": "topwater-snapshot"`, `"version": 1`, the creation time, `user`, `hostname` and `cwd`,
  then `files` and the session. Each file entry has its absolute `path`, `type` (`dir` or `file`), `mode` as octal
  (`"0644"`), `owner`, `mtime` (UTC, ISO 8601, 100 ns precision), and for files `size`, `binary` (bytes rather than
  UTF-8 text), an optional `reportedSize` and the `blob` that holds the contents. Parents come before their
  children. `variables` lists `name`, `value`, `attrs` (the `declare` letters), `exported` and `array` (`assoc`,
  `next`, `items` as `[key, value]` pairs). `functions` lists `name`, `body` as written, and `exported`. Then
  `aliases`, `set`, `shopt`, `traps`, `positional`, `dirStack`, `history`, `lastExitCode` and the rest.
- `blobs/<sha256>`: each distinct file content once, named by its SHA-256. Identical files (all the `/usr/bin` stubs)
  are stored once, and after loading they share one copy in memory again.

A newer `version` than this Topwater knows fails with `NotSupportedException`: `snapshot format version 2 is newer
than this version of Topwater supports (1); upgrade Topwater to load it`. Later versions of Topwater are meant to keep
reading version 1.

### Loading untrusted snapshots

`Load` treats the file as hostile and reads it only into memory. Nothing is written to disk. Anything wrong fails the
whole load with `InvalidDataException` (`not a valid Topwater snapshot: ...`), and nothing is half-loaded. The limits
come from the `ShellOptions` you pass (the defaults otherwise):

- The file contents together may hold at most `MaxFileSystemBytes`. A snapshot of a shell whose app wrote more than
  that (the app's writes are never refused) needs a larger value to load.
- Files and directories, and zip entries, may number at most `MaxExtractEntries`.
- Variables and array elements together may hold at most `MaxStringBytes` characters, and so may function bodies.
- `manifest.json` may be at most 4 × `MaxStringBytes` plus 1 KB per allowed entry. The whole file may be at most
  twice `MaxFileSystemBytes` plus that, and a stream that can't seek is read only up to that size.
- Each blob is read only up to the size its entry declares, so a zip bomb stops there. It must then match its size
  and SHA-256 hash.
- Paths must be absolute and canonical: no `.` or `..` segments, no empty segments, no trailing `/`, no NUL. A parent
  must come before its children, and a path may appear only once. Only directories and regular files exist: symlinks,
  devices and other types are refused. Modes must be octal up to `7777`. Owners, the user and the host name must be
  plain names, so `"user": "../../etc"` can't steer the home directory.
- Variable names must be valid identifiers, and indexed arrays must have non-negative integer indices. Each function
  body is parsed again as `name() body` and must come back as exactly that one function, so a body can't smuggle in
  extra commands. Exit codes must be 0-255, and `set` options must be ones the shell has.

### Toward real containers

The saved format is meant to become an export to a real Ubuntu 24.04 container. That is why the manifest uses real
absolute paths, octal modes, owner names, UTC modification times, the user, the host name, the working directory, and
each variable's exported flag (the environment a process would start with). An exporter needs to write the `files`
as a tar layer (`docker import`), map owner names to the uid and gid in the snapshot's `/etc/passwd` and `/etc/group`,
leave out what the simulation invents (`/proc`, `/dev`, the 16-byte program stubs in `/usr/bin` and `/bin`, which a
real image provides), and set `Env`, `WorkingDir` and `User` in the image config from the exported variables, `cwd`
and `user`. Shell-session state (functions, aliases, history) could go into the user's `~/.bashrc` or be dropped. The
registered commands have no container equivalent and would have to be provided as real programs.

## Limits and safety

- Nothing the agent runs touches your machine, unless you register a real host program or mount an MCP server.
- Nothing the agent types can hang the call, crash the host or use up its memory. Every limit is on by default;
  the ones with an option are set on `ShellOptions`:

| Limit | Default | Option | What the agent sees | Exit code | Ends the call |
|---|---|---|---|---|---|
| Time for one tool call | 120 s | `MaxCallDuration` | trailer `[stopped: this call exceeded the 120 s time limit; split the work into shorter calls. Running it again will hit the same limit. Files and variables changed before this point are kept.]` | 124 | yes |
| All files together | 128 MB | `MaxFileSystemBytes` | the real error: `bash: /path: No space left on device` (redirects), `cp: error writing 'x': No space left on device`, `tar: x: Cannot open: No space left on device`, `<cmd>: /path: No space left on device` | 1, for that command | no |
| One string, and all variables together | 32 MB | `MaxStringBytes` | `bash: <what> too large (limit 32 MB); <what to do instead>` on stderr, and the same text in the trailer (see below) | 1 | yes |
| One loop | 100,000 iterations | `MaxLoopIterations` | `bash: loop aborted after 100000 iterations (the limit for one loop); process the whole input with one command (awk, sed, xargs) instead of a shell loop`, and trailer | 1 | yes |
| All loops in one call | 10 × `MaxLoopIterations` | `MaxLoopIterations` | `bash: loop aborted after 1000000 iterations in this call (the limit for all loops in one call); ...`, and trailer | 1 | yes |
| One archive extraction or decompression | 64 MB, 100,000 entries | `MaxExtractBytes`, `MaxExtractEntries` | `tar: output exceeds the 67108864-byte extraction limit; stopping` (and the entry-count form) | 2 or 1, as the real program | no |
| Nesting | scripts 100, functions 200, arithmetic 100, and the .NET stack | none | `bash: maximum script nesting level exceeded (100)`, `bash: f: maximum function nesting level exceeded (200)`, `bash: arithmetic is nested too deeply`, `bash: maximum nesting level exceeded`, and trailer | 1 | yes |
| One regular-expression match | 5 s | none | `bash: pattern matching took too long`, and trailer | 1 | yes |
| One `awk`, `sed`, `jq` run | 10M loop iterations, 10M steps, 20M steps | none | `awk: loop iteration limit exceeded`, `sed: too many steps (a branch loops forever?)`, `jq: error (at <unknown>): too many steps (possible infinite loop)` | 2, 1, 5 | no |
| One `bc` run | 5 s, and 100000 digits per number | none | `bc: computation stopped: exceeded the 5 s limit (lower scale or use smaller operands)`, `bc: number too large` | 1 | no |
| Brace expansion, `seq`, `diff` | 10,000 items, 1M numbers, 25M table cells | none | `bash: brace expansion: too many items`, `seq: too many numbers`, `diff: files too large to compare` | 1 (2 for diff) | no |
| Tool output | 30,000 characters of stdout, 5,000 of stderr | `ToToolOutput(maxChars)` | `... [output truncated, N more characters]` | | |
| Host programs | 120 s, 1 MB of output | `HostCommandOptions.Timeout`, `MaxOutputBytes` | `prog: timed out after 120s` (the program is killed; the call goes on) | 124 | no |

- **The trailer.** A limit that ends the call adds a bracketed last line to the tool output: `[stopped: <the
  message>. Running it again will hit the same limit. Files and variables changed before this point are
  kept.]`. It comes after `[exit code N]`, is never cut by the output caps, and shows even when the command sent
  its stderr to `/dev/null`. Output produced before the limit is returned with it. `ShellResult.Trailer` holds
  it for callers that don't use `ToToolOutput`. The next call runs normally.
- **Time.** The call's time limit is also what `sleep`, `timeout`, host programs and the built-ins see as
  cancellation: `sed`, `jq`, `awk` and `grep` check it as they go, so the call stops close to the limit and
  `timeout 5 awk '...'` interrupts awk. A single regular-expression match is not interruptible and may run up to
  5 s past the limit, and one synchronous step of another built-in on a capped input (a big `sort` or `diff`)
  may take a few seconds. When the time limit ends a call, an `EXIT` trap does not get to run.
- **Disk.** `df` shows the cap as the disk's size and what the files really hold as used. Sizes are UTF-8
  bytes, the same as `ls -l`. A write that would go past the cap fails whole (nothing is written) and the call
  goes on; `rm` frees the space again. Your app's own writes (`WriteAllText`, `AddDocument`, ... outside a call)
  are counted but never refused, so the agent may find the disk already full. A filesystem shared between
  shells takes its cap from the last `Shell` created on it.
- **Strings.** Counted in characters (one per byte for ASCII text; in memory .NET uses two bytes per
  character). `<what>` names what grew: `output` (a command's buffered output: the call's stdout or stderr, a
  pipeline stage, `$(...)`, `<(...)`), `x: value` (one variable or element), `variables in total`, `expansion`
  (one word, all the words of one command together, or `${x//pat/rep}`), `awk: string`, `sed: pattern space`,
  `jq: value` (strings, and arrays at 8 bytes per element). Each message names what to do instead, such as
  `write the data to a file instead`. A redirect (`> file`) is not limited by it: the shell writes through to
  the file every megabyte, so files are bounded by the disk cap. A pipeline whose stage outputs more than
  32 MB fails even though the file fits on the disk; filter earlier or raise the option.
- **Memory.** Measured on .NET 8 (Release, workstation GC, Linux x64), one `Shell` at the defaults running every
  exhaustion recipe in turn as separate calls (string doubling in bash, awk, sed and jq; `printf '%0999999999d'`;
  `${x//?/$x}`; `{1..10000}$big`; a million `a[i]=$big`; filling the disk with appends, `cp -r` and `tar x`;
  piping and capturing a 100 MB file): peak process working set **about 0.8 GB** (762 MB with
  `DOTNET_GCHeapHardLimit=4GB`, 816 MB without). Run one at a time in a fresh process, the string recipes peak
  at 240-370 MB and the disk recipes at 360-720 MB. What stays live after the calls is about 230 MB, nearly all of
  it a full disk: text files are held as .NET strings, two bytes per character, so 128 MB of text costs about
  256 MB, and reading a file that was built by appends briefly holds it twice. Plan on roughly 1 GB per hostile
  session at the defaults, less with lower `MaxFileSystemBytes` and `MaxStringBytes`. The time-limit recipes
  (`sleep 99999`, a shell loop of awk runs, slow regex over many lines) stay under 60 MB.
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
network access. `sleep` really waits, up to the call's time limit (`MaxCallDuration`).

A few shell behaviours differ from bash on purpose or for simplicity: the last command of a pipeline runs
in the current shell (like `shopt -s lastpipe`), so `cmd | while read ...; done` can set variables; aliases
expand in scripts too; extglob patterns work without `shopt -s extglob`; `declare -f` prints a function as
it was written; `local x` leaves `x` empty rather than unset; `set -x` traces simple commands only; and
`printf` pads by characters, not bytes.
