# Topwater reference

Everything the simulated machine supports, in one place. Code samples show the C# library; the Python and TypeScript
ports behave the same, and [python/README.md](../python/README.md) and [typescript/README.md](../typescript/README.md)
show their APIs and list what they don't have yet.

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
- [Command line and MCP server](#command-line-and-mcp-server)
- [Snapshots](#snapshots)
- [Containers](#containers)
- [Deterministic runs](#deterministic-runs)
- [Recordings](#recordings)
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
| Files | `ls` `cd` `pwd` `cat` `mkdir` `rmdir` `rm` `cp` `mv` `ln` `touch` `find` `tree` `stat` `du` `df` `file` `realpath` `readlink` `basename` `dirname` `mktemp` `chmod` `chown` `less` `more` |
| Text | `echo` `printf` `grep` `egrep` `fgrep` `head` `tail` `wc` `sort` `uniq` `cut` `sed` `tr` `tee` `xargs` `awk` `jq` `bc` `diff` `cmp` `nl` `tac` `rev` `paste` `column` `seq` |
| Documents and binary | `to-markdown` `xxd` `hexdump` `od` `strings` `base64` `md5sum` `sha1sum` `sha256sum` `sha512sum` |
| Archives | `tar` (`c` `x` `t`, `-f` `-z` `-v` `-C` `-O` `--strip-components`) `gzip` `gunzip` `zcat` (`-c` `-d` `-k` `-f` `-l` `-t` `-v` `-1`..`-9`) `zip` (`-r` `-q`) `unzip` (`-l` `-o` `-n` `-d` `-p` `-q`) |
| Shell | `export` `unset` `env` `printenv` `set` `shift` `local` `declare` `read` `mapfile` `readarray` `alias` `unalias` `history` `which` `type` `command` `test` `[` `true` `false` `bash` `sh` `source` `eval` `let` `sleep` `timeout` `time` `help` |
| System | `uname` `whoami` `hostname` `date` `ps` `id` `uptime` `free` `nproc` `sudo` `su` `apt` `apt-get` `apt-cache` `dpkg` (these four only say that nothing can be installed; see below) |

`help` lists everything, including your commands. Standard tools answer `--version` and `--help` the way the
real ones do. The full list, with each command's options and the ports that support them, is in
[`spec/commands`](../spec/README.md). `find` supports `-name -iname -path -regex -type -size -mmin -mtime -newer -empty -maxdepth
-mindepth -print -print0 -exec -delete` with `!`, `-o` and parentheses. `sed` is a full engine: addresses, `{}` blocks, hold space, labels and branches, `-i`, `-E`.
`ls` also takes `-s`, `--time-style` (`full-iso`, `long-iso`, `iso`, `+FORMAT`) and `--full-time`.

Error messages follow bash 5.2 and GNU coreutils, grep and sed on Ubuntu 24.04: bash's own errors start
`bash: line N:` as under `bash -c` (`./s.sh: line N:` in a script, `environment: line N:` in a function), each call
counting its lines from 1; a sed script error says where it is (`-e expression #1, char 5:`), and a bad grep or sed
regex gets GNU's message. `awk` behaves as Ubuntu's `mawk`: `--version` says so, and dividing by zero gives `inf`,
`-inf` or `-nan` instead of stopping the program.
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

### Semantic Kernel

Semantic Kernel (1.4x and later) wraps any `AIFunction` as a kernel function, so the same function is a plugin with no
extra package:

```csharp
using Microsoft.SemanticKernel;
using Topwater.AI;

kernel.Plugins.AddFromFunctions("shell", [shell.AsAIFunction().AsKernelFunction()]);
var settings = new PromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };
var answer = await kernel.InvokePromptAsync("How many lines are in notes.txt?", new(settings));
```

The model sees it as `shell-bash` (Semantic Kernel prefixes the plugin name), with the same description and schema.

Tested with Semantic Kernel 1.82. For new agents Microsoft points to Agent Framework, below.

### Microsoft Agent Framework

Agent Framework (`Microsoft.Agents.AI`, the successor to Semantic Kernel agents and AutoGen) builds agents on
`IChatClient` and takes `AITool`s, so the shell's function goes straight in:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Topwater;
using Topwater.AI;

var shell = new Shell();
shell.Fs.WriteAllText("/home/agent/notes.txt", "one\ntwo\n");

AIAgent agent = chatClient.AsAIAgent(instructions: "You are a helpful assistant.", tools: [shell.AsAIFunction()]);
AgentSession session = await agent.CreateSessionAsync();
AgentResponse response = await agent.RunAsync("How many lines are in notes.txt?", session);
Console.WriteLine(response.Text);
```

The agent invokes the tool itself; there is no `UseFunctionInvocation` to add. Tested with `Microsoft.Agents.AI` 1.28.
`examples/AgentFrameworkAgent` is a complete agent on OpenAI.

### OpenAI and Anthropic SDKs

Both official SDKs give an `IChatClient`, which then takes the function as above:

```csharp
IChatClient openai = new OpenAIClient(key).GetChatClient(model).AsIChatClient();   // package Microsoft.Extensions.AI.OpenAI
IChatClient claude = new AnthropicClient().AsIChatClient(model);                   // package Anthropic
```

If you drive the OpenAI SDK without `IChatClient`, `shell.AsAIFunction().AsOpenAIChatTool()` (or `AsOpenAIResponseTool()`)
gives its tool type; send the tool call's `command` to `BashTool.ExecuteAsync`. Tested with `OpenAI` 2.15 and `Anthropic`
13.14, against a fake HTTP server.

### MCP clients

Clients that speak MCP (Claude Desktop, Claude Code, Cursor, VS Code, or your own `ModelContextProtocol` client) need no
code: run [`topwater mcp`](#command-line-and-mcp-server), which serves the same `bash` tool over stdio. `Topwater.Mcp`
is the other direction: it mounts MCP servers inside the shell.

## Command line and MCP server

The `Topwater.Cli` package is a .NET global tool, `topwater`:

```bash
dotnet tool install -g Topwater.Cli --prerelease
topwater                                   # interactive shell
topwater run "ls -la /etc | head"          # one command, exit code passed through
topwater mcp --state ~/.topwater/m.topwater   # MCP server over stdio, machine kept across restarts
topwater snapshot save|load|info FILE
topwater export copy.topwater --state ~/.topwater/m.topwater
topwater import run.topwater --state ~/.topwater/m.topwater
topwater replay run.topwater
```

`topwater mcp` serves one tool, `bash`, built from `shell.AsAIFunction()`: the name, schema and output text are those of
`BashTool`, `[exit code N]` and limit notes included; the description is `BashTool`'s without the last sentence about
the app's own programs, since the standalone server has none. Calls run one at a time. Claude Desktop
(`claude_desktop_config.json`):

```json
{ "mcpServers": { "topwater": { "command": "/Users/you/.dotnet/tools/topwater", "args": ["mcp"] } } }
```

Every command takes the same options:

| Option | Effect |
|---|---|
| `--state FILE` | load the machine from FILE if it exists; save it after every command (through a temporary file) and on exit |
| `--from FILE` | start from FILE when there is no `--state` file yet; FILE is never written |
| `--import HOST:PATH` | copy a host file or directory into the machine at start, owned by the agent. A copy, not a mount; symlinks are skipped. Refused if it would not fit on the simulated disk |
| `--export PATH:HOST` | copy PATH out to the host on exit. Overwrites same-named files, deletes nothing, never writes through a host symlink or outside HOST |
| `--timeout SECONDS`, `--disk MB` | `MaxCallDuration` and `MaxFileSystemBytes` |

Without `--import` and `--export`, the only host files the command touches are the `--state` and `--from` snapshots,
which are loaded as untrusted (see [Loading untrusted snapshots](#loading-untrusted-snapshots)). The machine runs inside the
`topwater` process with no network and no host programs; the process itself has your permissions, like any program.

| Command | Effect |
|---|---|
| `topwater export FILE` | writes the machine the options give (`--state`, `--from`, `--import`) to FILE as a [`.topwater` file](#the-file-format). If the source has history (a recording, or an older `.jsonl` one), the history comes along, which also converts old recordings |
| `topwater import FILE --state STATE` | checks FILE (a `.topwater` file, a snapshot or an older `.jsonl` recording) and writes its machine to STATE: the manifest, or for an old recording the machine after its last call. STATE may not be a recording |
| `topwater replay FILE` | replays a recording on a fresh machine and prints the report; exit code 0 when every call matches, 1 when one doesn't. A run without a virtual clock is compared with `ReplayNormalizers.Volatile` |

None of these is reachable from the agent's `bash` tool, and none touches Docker; container images are built from
code with `Topwater.Containers`. The full security notes are in the package README (`src/Topwater.Cli/README.md`).

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

The machine looks like a lived-in Ubuntu 24.04 install: the standard folders (including the usual empty ones under
`/usr`, `/usr/local`, `/usr/share` and `/var`), `/etc` files such as `os-release` (a link to `/usr/lib/os-release`, as
on Ubuntu), `lsb-release`, `issue`, `shells`, `debian_version`, `passwd`, `hosts` and `group`, `/etc/profile`,
`/etc/bash.bashrc` and `/etc/profile.d`, the skeleton dotfiles in `/etc/skel`, a `/proc` whose `cpuinfo` and `meminfo` agree with `nproc` and `free`, the usual
dotfiles in the agent's home, and `/bin` and `/usr/bin` listing every program (yours included) with plausible
sizes and install dates. The dotfiles are our own text with the effect of Ubuntu's: a login shell (`bash -l`,
`bash --login`) reads `/etc/profile`, which runs `/etc/profile.d/*.sh`, then the first of `~/.bash_profile`,
`~/.bash_login` and `~/.profile`; `~/.bashrc` sets the prompt and aliases (`ls --color=auto`, `ll`, ...) only in an
interactive shell, so the agent's calls, like `bash -c`, read none of them. `/proc/self` points at the shell's own
process (`$$`). As for an ordinary user on Ubuntu, `/root`, `/var/cache/ldconfig`, `/var/cache/apt/archives/partial`
and `/proc/tty/driver` can't be read, so `find /` prints `Permission denied` for them and exits 1. Shell built-ins such as `cd` and `eval` are not listed as programs, as on a real system.

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
  filesystem, and is never written through. The agent's own links never become real ones: a link to a file inside the
  shared directories goes out as a copy of that file, and links to directories or to anything outside are left out.
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

using (var file = File.Create("machine.topwater")) shell.Save(file);   // or before.Save(file)
Shell later = Shell.Load(File.OpenRead("machine.topwater"));            // built-ins only: register your commands again
shell.Restore(ShellSnapshot.Load(File.OpenRead("machine.topwater"), shell.Options));  // or load into a shell you configured
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
- **The shell's options** (`ShellOptions`: limits, user, host name, clock, seed), which belong to whoever creates the
  shell. Without options, `Shell.Load` uses the snapshot's user and host name. A shell made with a virtual clock or a
  random seed (see [Deterministic runs](#deterministic-runs)) also saves the clock's time and the generator's state,
  and restoring into such a shell carries on from there; otherwise `RANDOM` stays unpredictable.

`Restore` replaces the filesystem object's contents in place. If several shells share one `ShellOptions.FileSystem`,
they all see the restored files.

### The saved format

`Save` writes a [`.topwater` file](#the-file-format) without history: a zip. Zip is self-describing, every platform
can open it (`unzip -l machine.topwater`), and the contents of different files can be read one at a time. The JSON
Schemas for everything in it are in [`spec/topwater-file`](../spec/topwater-file/README.md). It contains:

- `manifest.json`: `"format": "topwater-snapshot"`, `"version": 1` or `2`, the creation time, `user`, `hostname` and
  `cwd`, then `files` and the session. Each file entry has its absolute `path`, `type` (`dir`, `file` or `symlink`),
  `mode` as octal (`"0644"`), `owner`, `mtime` (UTC, ISO 8601, 100 ns precision), for files `size`, `binary` (bytes
  rather than UTF-8 text), an optional `reportedSize` and the `blob` that holds the contents, and for symlinks the
  `target` they hold. A file or symlink with several names (hard links) is saved in full under each, and every name
  after the first adds `link`: the path of its first name, so loading makes them one file again. Parents come before
  their children. `variables` lists `name`, `value`, `attrs` (the `declare` letters), `exported` and `array` (`assoc`,
  `next`, `items` as `[key, value]` pairs). `functions` lists `name`, `body` as written, and `exported`. Then
  `aliases`, `set`, `shopt`, `traps`, `positional`, `dirStack`, `history`, `lastExitCode` and the rest. A
  deterministic shell adds `clock` (the virtual time, ISO 8601 UTC) and `random` (the generator's state, a decimal
  string); both are optional and absent otherwise.
- `blobs/<sha256>`: each distinct file content once, named by its SHA-256. Identical files (all the `/usr/bin` stubs)
  are stored once, and after loading they share one copy in memory again.

Version 2 added `symlink` entries and `link`. A machine without links is still saved as version 1, which older
Topwater reads. A newer `version` than this Topwater knows fails with `NotSupportedException`: `snapshot format
version 3 is newer than this version of Topwater supports (2); upgrade Topwater to load it`. Later versions of
Topwater are meant to keep reading versions 1 and 2.

### Loading untrusted snapshots

`Load` treats the file as hostile and reads it only into memory. Given a `.topwater` file with history, it loads
`manifest.json`, the machine after the last call. Nothing is written to disk. Anything wrong fails the
whole load with `InvalidDataException` (`not a valid Topwater snapshot: ...`), and nothing is half-loaded. The limits
come from the `ShellOptions` you pass (the defaults otherwise):

- The file contents together may hold at most `MaxFileSystemBytes`. A snapshot of a shell whose app wrote more than
  that (the app's writes are never refused) needs a larger value to load.
- Files and directories may number at most `MaxExtractEntries`. The zip's entries are counted before any is listed,
  and may number at most 4 × `MaxExtractEntries` + 1,002 (room for a recording's checkpoints); two with one name are
  refused.
- Variables and array elements together may hold at most `MaxStringBytes` characters, and so may function bodies.
- `manifest.json` may be at most 4 × `MaxStringBytes` plus 1 KB per allowed entry. The whole file may be at most
  twice `MaxFileSystemBytes` plus that, and a stream that can't seek is read only up to that size.
- Each blob is read only up to the size its entry declares, so a zip bomb stops there. It must then match its size
  and SHA-256 hash.
- Paths must be absolute and canonical: no `.` or `..` segments, no empty segments, no trailing `/`, no NUL. A parent
  must come before its children, and a path may appear only once. Only directories, regular files and symlinks
  exist: devices and other types are refused. A parent is found without following symlinks, so no entry lands
  anywhere but its own path. A symlink's `target` is 1-4096 characters without NUL and may point anywhere, even at
  itself: it is only resolved later, with the agent's permissions, and 40 links in one lookup fail as on Linux. A
  `link` must name an earlier file or symlink, never a directory. Modes must be octal up to `7777`. Owners, the user and the host name must be
  plain names, so `"user": "../../etc"` can't steer the home directory.
- Variable names must be valid identifiers, and indexed arrays must have non-negative integer indices. Each function
  body is parsed again as `name() body` and must come back as exactly that one function, so a body can't smuggle in
  extra commands. Exit codes must be 0-255, and `set` options must be ones the shell has.

### Toward real containers

The manifest's real paths, octal modes, owner names, UTC modification times, user and exported flags are what the
`Topwater.Containers` package uses to move a machine into a real container and back. See [Containers](#containers).

## Containers

The `Topwater.Containers` package moves a machine between Topwater and a real Docker container, in both directions.

```bash
dotnet add package Topwater.Containers --prerelease
```

Two uses:

- **Graduate an agent from the simulation to real programs.** Python, git and compilers run in the container, on the same
  files, as the same user, in the same working directory and with the same environment.
- **Give an agent a safe copy of a real machine.** Import a server's `/var/log` and `/etc` as a snapshot. The agent
  investigates it in the simulation, where it can be forked and rewound and cannot touch the server.

### Snapshot to container

```csharp
using Topwater;
using Topwater.Containers;

var context = await shell.Snapshot().BuildImageAsync("my-agent");   // docker build
Console.WriteLine(context.Report);                                  // what didn't carry over
```

```bash
docker run -it my-agent    # a bash prompt in the machine: files, user, cwd, variables, functions, history
```

To build somewhere else, take the build context and write it out:

```csharp
BuildContext ctx = snapshot.ToBuildContext();
ctx.WriteTo("build");                                         // Dockerfile, setup.sh, layer.tar
using (var f = File.Create("context.tar")) ctx.WriteTar(f);   // the same three files as one tar: docker build - < context.tar
```

`BuildContext` has `Dockerfile`, `SetupScript`, `SessionScript` (the text of `/etc/topwater/session.sh`), `Layer`
(the bytes of `layer.tar`) and `Report`. Both methods take an optional `baseImage`, `ContainerExport.DefaultBaseImage`
(`"ubuntu:24.04"`) by default. `ToBuildContext` is pure: no process, no disk.

### Container to snapshot

```csharp
var result = await ContainerImport.FromContainerAsync("web-1", new ContainerImportOptions { Paths = ["/var/log", "/etc"] });
var shell = new Shell(new ShellOptions { User = result.Snapshot.User });
shell.Restore(result.Snapshot);
Console.WriteLine(result.Report);
```

| Method | Source |
|---|---|
| `FromContainerAsync(container, options)` | a running or stopped container |
| `FromImageAsync(image, options)` | an image: a container is created from it (never started), imported and removed |
| `FromTarAsync(rootfs, config, changes, options)` | no process: a `docker export` tar, a `ContainerConfig(User, WorkingDir, Env)` and `docker diff` lines |

| `ContainerImportOptions` | Default | What it does |
|---|---|---|
| `Paths` | empty | paths imported whole, with everything below them, on top of what the container changed |
| `IncludeBinaries` | `false` | import ELF executables as inert files. Off, they are skipped and reported: Topwater can't run them, and they are most of a root filesystem's bytes |
| `RestoreSession` | `true` | for an image Topwater exported, re-create the session from `session.sh` |
| `Limits` | defaults | a `ShellOptions` for `MaxFileSystemBytes`, `MaxStringBytes`, `MaxExtractEntries` and the host name of the new machine. The user comes from the container |

`Docker.Executable` (default `"docker"`) names the CLI to run, and `await Docker.IsAvailableAsync()` says whether it
reaches a daemon. Without `Paths`, an import brings only what the container changed since its image, so a stock
container gives an almost empty machine.

### What the export does

The export diffs the snapshot against a fresh default `Shell` of the same user and host name, and writes only the files
that differ in content, mode, owner or type. A change to the modification time alone (`touch` on an existing default
file) is not detected.

Never exported:

- `/proc`, `/sys`, `/dev` and `/run`, which Topwater simulates.
- The 16-byte program stubs in `/bin` and `/usr/bin`, including those of registered commands (which are reported).
- `/etc/passwd`, `/etc/group`, `/etc/shadow` and `/etc/gshadow`. Accounts are created with `useradd` and `groupadd`
  instead, because copying the files would replace the image's account database.
- `/etc/hostname`, `/etc/hosts` and `/etc/resolv.conf`, which docker writes at run time.

Paths under `/bin`, `/sbin`, `/lib` and `/lib64` land under `/usr`, because Ubuntu's merged `/usr` makes them symlinks
in the image. The parent directories of exported files go in the layer too, so they keep the snapshot's owner and
mode. Files deleted relative to the default machine are removed in `setup.sh` with `rm -rf`, except those the real
system needs (reported).

The generated `Dockerfile`:

1. `FROM` the base image.
2. `COPY` and `RUN` `setup.sh`, as root. It creates the accounts, removes deleted files and appends a two-line hook to
   the user's `~/.bashrc`.
3. `ADD layer.tar /`. The tar has numeric uid and gid, octal modes and modification times.
4. `ENV` for each exported variable that differs from a fresh machine's. `PATH`, `HOME`, `LANG` and the like are left
   alone when unchanged, so the image's real values apply.
5. `WORKDIR` is the snapshot's working directory, and `USER` its user.

Accounts: every account in the snapshot's `/etc/passwd`, plus every owner of an exported file, is created (unless the
image has it) with the snapshot's uid and gid. An owner missing from `/etc/passwd` gets the next free uid (reported).
An account of the base image that holds an id the snapshot needs gives way: `ubuntu:24.04` ships a `ubuntu` user with
uid and gid 1000, which `agent` needs.

Shell state that isn't files goes into `/etc/topwater/session.sh`, which `~/.bashrc` sources, so interactive shells
(`docker run -it`) get it:

- variables that are not plain exported strings: non-exported ones, arrays, integer, readonly, nameref and case
  attributes, values with newlines. Exported values with control characters go here too, instead of `ENV`.
- functions (and `export -f`), and aliases (after `unalias -a`, so the `~/.bashrc` aliases give way to the snapshot's).
- `set -o` options, `shopt` differences from Topwater's defaults, traps and positional parameters.

History goes to `~/.bash_history`, timestamped so a multi-line command stays one entry. `/etc/topwater/paths` lists the
paths the export wrote or deleted, in `docker diff`'s format but each ended by a NUL rather than a line break (a path
can hold a line break), so an import can find them again.

### What the import does

1. `docker inspect` gives `Config.User` (a name, a uid, or `name:group` / `uid:gid`; empty means root),
   `Config.Env` and `Config.WorkingDir` (default `/`).
2. `docker diff` gives the changes (`A`, `C` and `D` lines). For an image Topwater made, `/etc/topwater/paths` is read
   too, with `docker cp`, because those files are in the image, not in the container's diff.
3. `docker export` is streamed (never written to disk), and only the selected paths are kept: the changed ones and
   `Paths`.
4. They are layered on a fresh default Topwater machine of that user, so the simulated programs still work. `D` lines
   delete from that machine.

Details:

- **Owners** map through the container's own `/etc/passwd`, read from the export stream even when `/etc` isn't
  imported. An unknown uid becomes the number as the owner name.
- **Links.** A symlink comes back as a symlink holding the same target, wherever it points: following it later is
  subject to the agent's permissions. An entry below a symlink (only a crafted archive has one) is skipped and
  reported. A hard link whose target is in the imported set becomes a copy; other hard links are skipped and
  reported, as are device files, fifos and other types.
- **Simulated paths.** `/proc`, `/sys`, `/dev`, `/run` and docker's `/etc/hostname`, `/etc/hosts` and
  `/etc/resolv.conf` stay Topwater's.
- **Topwater images.** The hook is stripped from `~/.bashrc`, `~/.bash_history` becomes the shell history,
  `/etc/topwater` is not imported, and with `RestoreSession` `session.sh` runs in the new simulated shell. For any
  image, the user's `.bash_history` becomes the history.
- **Text or bytes.** Valid UTF-8 comes in as text, anything else as exact bytes.

### What carries over

| | Topwater to container | Container to Topwater |
|---|---|---|
| Files | content, mode, owner, modification time to the second | the same |
| Directories | yes, with their owner and mode | yes |
| Deleted files | removed in `setup.sh` | `D` lines delete from the default machine |
| Owners | accounts created with the snapshot's uid and gid | mapped by the container's `/etc/passwd`; unknown uids become numbers |
| Exported variables | `ENV` | from `Config.Env` |
| Other variables, arrays, functions, aliases, options, traps | `session.sh` | only for images Topwater exported, by running `session.sh` in the simulated shell. A real container has no shell session to read |
| History | `~/.bash_history` | the user's `.bash_history` |
| Working directory | `WORKDIR` | `Config.WorkingDir` |
| User | `USER` | `Config.User` |
| Host name | not carried: docker sets it at run time, use `--hostname` | not carried: `Limits.Hostname`, default `topwater` |
| Registered commands | not carried | not applicable |
| Symlinks | symlinks; other names of a file as hard links, or copies when the first name isn't exported | symlinks; hard links as copies, when the target is imported |
| Binaries | stubs are not carried | skipped unless `IncludeBinaries` |
| Group ownership | each file gets its owner's primary group | dropped: Topwater files have an owner and no group |

Not carried:

- The host name, and registered commands (C# handlers, MCP mounts and host programs have no program in the container).
- Per-process state: `$?`, `PIPESTATUS`, `$_`, `$!`, `SECONDS`, `getopts`, finished jobs and the `pushd` stack.
- `set -o errexit`, which is left off: in an interactive shell it would close the terminal at the first failing command.
- Topwater's limits. Use `docker run --memory` and the like.
- Sub-second modification times: the layer and `docker export` keep whole seconds.
- Groups.
- Container to Topwater only: real processes, and mounts and volumes (`docker export` does not include volumes). The
  image's unchanged files stay out unless you name them in `Paths`.

In the round-trip test (a machine with a dozen user files), `docker build` took about 3 s and the import about 2 s. The
image was 16 KB over `ubuntu:24.04` (78 MB) and the layer 12 KB (Linux x64, Docker 29, `ubuntu:24.04` already pulled).

### Safety

These are host APIs. Nothing in the package registers a command, so the agent inside the shell can never trigger an
export, a build or an import. Only `Docker` starts processes: the docker CLI, with an argument list and no shell.
The core Topwater package still starts none and has no dependencies, and this package depends on nothing but Topwater
(tar is `System.Formats.Tar`). Export is mostly in memory: the layer is held in memory.

Container content is untrusted, like `ShellSnapshot.Load`:

- Every entry name must be canonical (an optional leading `./`, no `..`, `.` or empty segments, no NUL), or the import
  fails with `InvalidDataException`.
- The selected files together may hold at most `MaxFileSystemBytes` and number at most `MaxExtractEntries` (link
  copies count). `/etc/passwd`, the session script and every `/home/*/.bash_history`, read even when not selected,
  count toward `MaxFileSystemBytes` too.
- Anything else malformed (a time or owner no snapshot can hold, a broken header) fails with `InvalidDataException`
  (Python: `InvalidContainerData`).
- Each entry is read only up to the size its header declares. A session script, `/etc/passwd` or `.bash_history`
  larger than `MaxStringBytes` is not read.
- The result goes through `ShellSnapshot.Load` with your limits, so all its checks apply: names, modes, variable names,
  and function bodies parsed again.
- Nothing is written to the real disk. A `session.sh` from an image runs only inside the simulated shell, like any
  agent command, never on the host.

Building an image runs the generated `setup.sh` as root inside the image build. That is docker's sandbox, not your host.
Account and group names from the snapshot reach `setup.sh` only as quoted words; a name starting with `-` (which
`useradd` would read as an option) is not created and is reported, and its files go in as root's.

**An exported image is agent-authored content.** It holds what the agent wrote: its files and scripts, its `~/.bashrc` and the session script that `~/.bashrc` sources, file modes and owners. Starting a shell in it runs that content. Treat the image as untrusted, and run it with the same care, limits and isolation you would give the agent itself.
For example `docker run -it --network none --cap-drop ALL --security-opt no-new-privileges --memory 1g --pids-limit 512 my-agent`.

The docker tests are marked `[DockerFact]` and skip when docker isn't available.

## Deterministic runs

By default the machine reads the host's clock and picks random numbers unpredictably, like a real one. To make a run
repeat exactly (for recordings, regression tests and replays) give it a virtual clock and a seed:

```csharp
var shell = new Shell(new ShellOptions
{
    StartTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),   // the machine's time starts here
    RandomSeed = 1,                                                       // $RANDOM, mktemp names
});
await shell.RunAsync("date; sleep 3600; date");   // returns at once: Fri Jan  2 03:04:05 UTC 2026, then 04:04:05
```

| Option | Effect when set |
|---|---|
| `StartTime` | The clock starts at this time and stands still. `sleep` moves it forward instantly instead of waiting; `timeout N` measures virtual time, so `timeout 5 sleep 10` returns 124 at once with the clock 5 s on. `date`, file modification times, `SECONDS`, `EPOCHSECONDS`, `EPOCHREALTIME`, `uptime`, `time`, `find -mmin`/`-mtime`, jq's `now` and printf's `%(fmt)T` all read it. The system files' "install" date is 142 days before it, instead of before today. |
| `RandomSeed` | `$RANDOM` and the names `mktemp` makes come from a generator seeded with it. `RANDOM=n` reseeds it, as in bash (with or without the option). |

Both are off (null) by default. Everything else is already deterministic: process ids (`$$`, `$!`, `ps`), the host
name and user, `/proc` contents, awk's `rand()` (seeded with 0, as in gawk), and the order in which awk's `for (k in a)`
and jq's object keys come out (insertion order, which depends only on the input). The per-call time limit
(`MaxCallDuration`) still runs on real time, so a runaway loop is stopped either way. A snapshot of a deterministic
shell carries the clock and the generator's state, so restoring it and running the same commands gives the same output.

## Recordings

A recording is a session you can replay anywhere: the options and starting machine, then every tool call with what it
returned. Replay it on a fresh shell as a regression test, on another starting machine or Topwater version to see what
changed, or on real bash in an Ubuntu container to see where the simulation differs from Linux.

```csharp
var shell = new Shell(new ShellOptions { StartTime = DateTimeOffset.Parse("2026-11-25T09:00:00Z"), RandomSeed = 1 });
// ... write the task's files ...
using var recorder = shell.StartRecording(new RecordingOptions
{
    Metadata = { ["model"] = "gemini-flash-latest", ["task"] = "Find every TODO" },
    SnapshotEvery = 5,                       // a snapshot after every 5th call, for jumping to a turn
});
// run the agent: RunAsync, BashTool.ExecuteAsync and AsAIFunction calls are all recorded
recorder.Stop();                             // before your own checks, which also go through RunAsync
recorder.Save("runs/todo.topwater");         // or Save(stream)

var recording = Recording.Load("runs/todo.topwater");   // also reads snapshots and older .jsonl recordings
ReplayReport report = await recording.ReplayAsync(configure: s => s.Register(new OrderTools()));
Console.WriteLine(report);                   // "replay on topwater 0.1.0-preview.1: 12/12 calls match"
```

`StartRecording` snapshots the machine as the recording's start; every `RunAsync` call after it is added until `Stop`.
Only one recording runs per shell at a time. Each `RecordedCall` holds the command, stdout, stderr, exit code, trailer,
wall time, `Time` (the shell's clock when the call started) and `Files` (the paths it added, changed or removed). The
last call always keeps a snapshot, so the file holds the end state; `SnapshotEvery` keeps more, and
`recorder.SnapshotNow()` attaches one to the last call on demand.

### Replaying

`recording.ReplayAsync(target, options)` runs each recorded command on an `IReplayTarget` and compares stdout, stderr,
exit code and trailer call by call. The `ReplayReport` lists the calls that ran, each with what came back and its
`ReplayDifference`s (index, command, field, expected, actual). `Matches` says whether every call matched, and
`ToString()` gives a summary pointing at the first differing line of each field.

| Target | How |
|---|---|
| A fresh Topwater shell at the recording's start | `recording.ReplayAsync(options, configure)`. `configure` registers your app's commands; the report warns about any the recording used that the target lacks (`recording.AppCommands`). |
| Another starting machine, or a shell you built | `new ShellTarget(recording.CreateShell(configure, start: otherSnapshot))`, or `new ShellTarget(anyShell)` |
| Another Topwater version | load the same file in a build of that version and replay |
| Real bash in Docker | `await DockerTarget.StartAsync(recording.Start)` from the `Topwater.Replay` package (below) |
| Anything else | implement `IReplayTarget` (`Name`, `RunAsync(command)`, `DisposeAsync`) |

`ReplayOptions`:

- `Fields`: which of `Stdout`, `Stderr`, `ExitCode`, `Trailer` to compare (all by default).
- `Normalize`: applied to both sides' text before comparing (default: exact). `ReplayNormalizers.Volatile` replaces
  times and dates (ISO 8601, `date`, `ls -l`), epoch seconds, mktemp names, `time` durations and uptimes with
  placeholders; `ReplayNormalizers.Whitespace` ignores line endings and trailing whitespace; `ReplayNormalizers.Chain`
  combines them.
- `StopAfter`: stop after this many differing calls. `OnCall`: called after each call, for progress output.

A recording made with a virtual clock and a seed (`recording.IsDeterministic`) replays exactly on the same Topwater
version. One made without them still replays, but needs `ReplayNormalizers.Volatile` for anything that prints the time.

### The machine at any turn

`await recording.ShellAtAsync(i)` gives a shell as it was right after call `i` (-1: before the first). It restores the
nearest snapshot at or before `i` and runs the calls after it again, which is exact for a deterministic recording. A
viewer that steps through a run turn by turn can use this, and `SnapshotEvery` bounds how much it re-runs.

### Recordings as regression tests

The `Topwater.Replay` package (`dotnet add package Topwater.Replay --prerelease`) turns a recording file into a test.
It works with any test framework: a mismatch throws `ReplayMismatchException` carrying the report.

```csharp
[Fact]
public Task Todo_session_still_behaves() =>
    Replay.AssertMatches("recordings/todo.topwater", configure: s => s.Register(new OrderTools()));
```

A relative path is relative to the test's source file, so recordings sit next to the tests without being copied to
the output folder. A deterministic recording is compared exactly; others with `ReplayNormalizers.Volatile`. When a
change is intended, run the tests with `TOPWATER_RERECORD=1`: `AssertMatches` then rewrites each file with what this
Topwater returns instead of comparing (header, metadata and start kept; snapshots retaken where there were some),
as a `.topwater` file even if it was an older `.jsonl` one.
Review the diff and commit it. `recording.RerecordAsync(target)` does the same in code.

### Real bash in Docker

`DockerTarget` (in `Topwater.Replay`, which starts processes; the core package never does) replays on real bash in a
throwaway container:

```csharp
await using var linux = await DockerTarget.StartAsync(recording.Start, new DockerTargetOptions
{
    Image = "ubuntu:24.04",                    // the default
    Packages = ["jq", "tree", "file", "bc"],   // added once and cached as a local image; [] uses the image as is
    CallTimeout = TimeSpan.FromSeconds(30),    // a call past it is killed and returns 124
});
var report = await recording.ReplayAsync(linux, new ReplayOptions { Normalize = ReplayNormalizers.Volatile });
```

The container has no network unless `Network = true`, 1 GB of memory and no swap, one CPU, 512 processes, files of at most
1 GB, no capabilities but `CHOWN`, `DAC_OVERRIDE` and `FOWNER` (which the root setup step needs to write files with their
owners), and `no-new-privileges`, so a setuid program gains nothing. The 1 GB limit is per file: nothing caps the
container's total disk use, so a recording can still fill the docker host's disk. Commands run by number, uid and gid
1000 (the snapshot's `/etc/passwd` cannot turn that into root); only a snapshot whose user is `root` runs as root,
and then with those three capabilities. The snapshot's symbolic links go in as links, never followed. It gets the snapshot's host name and user (uid 1000, in place of the
image's `ubuntu`), the files a fresh Topwater machine doesn't have or has differently (with modes, owners and times;
not `/proc`, `/dev` or the program stubs, which the image provides for real), the variables and the working
directory. Each call runs as `bash -c` under that user; the working directory, variables and functions are saved after
each call and restored before the next, so state carries over as in Topwater. Aliases and `set`/`shopt` options
don't carry over. `DockerTarget.IsAvailableAsync()` tells tests whether to skip. User, owner and variable names go into
its root setup script quoted, and names no account or variable can have are left out.

`tools/ReplayGaps` uses this to measure Topwater against Linux on many sessions at once: it converts the example
harness's saved transcripts (`runs/*.json`) and public agent trajectories into recordings, replays each on Topwater
and in a container, and ranks the differences (missing commands, unsupported options, wrong exit codes, wrong output).
The example harness itself now saves a deterministic recording of every run next to its transcript.

### In TypeScript and in the browser

All three ports read and write the same `.topwater` files. The TypeScript port (`Recording`, `ShellSnapshot`,
`startRecording`; see `typescript/README.md`) has the same virtual clock and random generator, so a deterministic recording made here
re-runs there with the same times and random numbers. Python (`shell.start_recording()`, `Recording.load()`; see
`python/README.md`) has no virtual clock, so a replay of its recordings needs a normalizer for anything that prints the time. The TypeScript demo site has a recording viewer (`replay.html`) that steps
through a recording turn by turn, shows the files after each turn and what the turn changed, compares any two moments,
and continues from any turn in a terminal on that machine. Commands typed there form a branch of the timeline, which
can be compared with the original and downloaded as its own `.topwater` file: the recording's calls up to the fork,
then the branch's, with metadata `forkedFrom` and `forkedAfterCall`, and checkpoints only at the fork and the end (a
reader re-runs the branch's calls in between).

### The file format

Snapshots and recordings are one format, the `.topwater` file: a zip. A snapshot is a `.topwater` file without
history. The JSON Schemas are in [`spec/topwater-file`](../spec/topwater-file/README.md).

| Entry | |
|---|---|
| `manifest.json` | the machine: [the snapshot manifest](#the-saved-format). With history, the machine after the last call |
| `blobs/<sha256>` | file contents, each stored once for the whole file, shared by the manifest and every checkpoint |
| `history.jsonl` | with history only: a header line, then one line per call |
| `checkpoints/<n>.json` | with history only: the machine after `n` calls, the manifest's shape. `0` is the start and always there |

Because the machine is always `manifest.json`, a recording opens anywhere a snapshot does (`Shell.Load`, `--from`,
`snapshot info`), also in a Topwater that predates history. The CLI refuses a recording as `--state`, which it would
rewrite as the machine alone: start from it with `--from`, or copy its machine to a new state file with
`topwater import`.

`history.jsonl` is JSON Lines (UTF-8, `\n` endings): one line per call, so `jq` and `grep` work on it after
`unzip -p run.topwater history.jsonl`. Line 1 is the header:

```json
{"format":"topwater-history","version":1,"created":"2026-11-25T09:00:00.0000000Z","topwater":"0.1.0-preview.1+...",
 "options":{"user":"agent","hostname":"topwater","startTime":"2026-11-25T09:00:00.0000000Z","randomSeed":1,
            "maxLoopIterations":100000,"maxExtractBytes":67108864,"maxExtractEntries":100000,"maxCallDurationMs":120000,
            "maxFileSystemBytes":134217728,"maxStringBytes":33554432},
 "appCommands":["orders"],
 "metadata":{"model":"gemini-flash-latest","task":"Find every TODO"}}
```

(shown wrapped; in the file it is one line)

- `options` are the `ShellOptions` that shape behaviour. `startTime` and `randomSeed` are absent when not set;
  `maxCallDurationMs` is -1 for no limit.
- `appCommands` (optional) are the registered commands that aren't built-ins; a replay target needs them.
- `metadata` (optional) is free-form string pairs.

Every later line is one call, in order:

```json
{"index":0,"command":"grep -rn TODO ~/project","stdout":"...","stderr":"","exitCode":0,"trailer":"[stopped: ...]","elapsedMs":12.5,"time":"2026-11-25T09:00:00.0000000Z","files":["/home/agent/project/notes.txt"]}
```

`trailer` (the note a safety limit adds) and `files` (paths the call added, changed or removed, sorted) are present
only when set. `time` is the shell's clock when the call started. `elapsedMs` is wall time, kept for information and
never compared. `index` counts from 0. The checkpoint after call `index` is `checkpoints/<index + 1>.json`, if there is one.

Version rules: readers ignore fields they don't know, so later versions may add optional fields without changing
`version`. A change that older readers would misread raises `version`; loading a newer version fails with
`NotSupportedException`, and later Topwater versions keep reading version 1. Loading treats the file as untrusted:
every manifest and checkpoint loads under the default limits or the `ShellOptions` passed to `Load` (never limits the
file names) with every check [`ShellSnapshot.Load`](#loading-untrusted-snapshots) makes. The replay shell's limits are
the file's, clamped to the same bound: a recording can lower limits, never raise them. At most 1,000 checkpoints load;
together they may hold at most 4 x `MaxFileSystemBytes` of content and 4 x `MaxExtractEntries` files and directories.
`history.jsonl` is read line by line as it inflates, up to 2 x `MaxStringBytes` bytes and `MaxExtractEntries` calls.
Checkpoint names must be `0` or a number without leading zeros no greater than the number of calls, and no two zip
entries may share a name.

Older files still load: snapshot zips are `.topwater` files already, and the JSON Lines recordings that came before
(header `"format":"topwater-recording"`, snapshots embedded as base64 zips or in side-car files) load with the same
checks. A side-car name must be plain segments of letters, digits, `.`, `_` and `-` separated by `/`, none made only of
dots; a custom `openSnapshot` must treat it as nothing else. `Save` always writes `.topwater`, so loading and saving
converts an old recording.

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
  refused (tar) or cleaned (unzip), and leading `/` is dropped. tar extracts symlinks, but like GNU tar it writes
  nothing below, and hard-links nothing through, a symlink the archive itself created (`Cannot open: Not a
  directory`), and a member replaces a symlink in its way rather than writing through it. Hard links only reach
  entries already inside the target. One extraction or decompression stops with an error past 64 MB (`MaxExtractBytes`) or
  100,000 entries (`MaxExtractEntries`). Binary data survives pipes and redirects (`tar czf - . | gzip -l`,
  `gzip -c f > f.gz`); in the tool output it shows as U+FFFD.
- Tool output is cut at 30,000 characters with a note saying how much was dropped. Error output is cut
  separately, so the error message and exit code are always shown.
- `set -e` and `pipefail` reset after every tool call, so one stray `set -e` can't affect later calls.
- A command that throws becomes an error message and exit code, not a crash.
- One shell is one session. Use a separate `Shell` per conversation; a shell is not safe to share between
  threads.

### Programs that are not here

A command the machine doesn't have fails exactly as in bash: `bash: line 1: python3: command not found` on stderr,
status 127. The first time in a session that this message goes straight to the tool output, one more line follows:
`(run 'help' to list the available commands; nothing else can be installed on this machine)`. The hint is left out
when stderr is redirected or captured (`2>&1`, `2>/dev/null`, `out=$(cmd 2>&1)`), so a script that inspects the
message, or `${out:0:7}`, sees what bash prints; earlier versions appended the hint to every such line, which leaked
into captured output. Showing it once is enough for an agent to learn that `help` exists without making later output
differ from bash's.

What each commonly tried program does:

| Program | Here | Why |
|---|---|---|
| `python`, `python3`, `pip`, `pip3`, `node`, `npm`, `perl` | command not found (127) | not part of the machine; a fake interpreter would mislead. Ubuntu itself ships `perl`, so this one differs from a real install |
| `apt`, `apt-get`, `apt-cache` | exist; every subcommand prints `E: Packages cannot be installed on this machine: ...` and exits 100, apt's error status | agents reach for apt when a program is missing; saying plainly that nothing can be installed stops them retrying |
| `dpkg` | exists; prints `dpkg: error: packages cannot be installed or listed ...` and exits 2 | same, with dpkg's status |
| `sudo apt ...`, `sudo dpkg ...` | the package manager's own message | more useful than the sudoers refusal every other `sudo` command gets |
| `snap` | command not found | not in Ubuntu's server or container images either |
| `git`, `curl`, `wget` | see their own sections when present | |

## Not supported

Real background jobs (`cmd &` runs to completion before the next command, so `wait` only reports its
status), job control (`fg`, `bg`, `%1`), `>(...)`, `coproc`, `compgen`/`complete`, `caller`, running shell
commands from inside `awk` (`print | "cmd"`), group permissions, and a real `python` or
network access. `sleep` really waits, up to the call's time limit (`MaxCallDuration`).

A few shell behaviours differ from bash on purpose or for simplicity: the last command of a pipeline runs
in the current shell (like `shopt -s lastpipe`), so `cmd | while read ...; done` can set variables; aliases
expand in scripts too; extglob patterns work without `shopt -s extglob`; `declare -f` prints a function as
it was written; `local x` leaves `x` empty rather than unset; `set -x` traces simple commands only; and
`printf` pads by characters, not bytes.
