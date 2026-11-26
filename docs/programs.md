# Your programs

How your functions, MCP servers, HTTP APIs and real programs become commands on the machine. Everything here does
real work when the agent calls it, so expose only what you would let the agent do.

| | .NET | Python | TypeScript |
|---|---|---|---|
| [Your functions](#your-own-commands) | `[Command]` methods, `shell.Register(obj)` | `@command` methods, `shell.register(obj)` | `shell.register(name, handler, description)` |
| [Tools described by a JSON Schema](#tools-described-by-a-json-schema) | `shell.RegisterTools(...)` | `register_tools(shell, ...)` | `shell.registerTools(...)` |
| [An MCP server](#mcp-servers) | `shell.MountMcpAsync(...)` (package `Topwater.Mcp`) | `topwater.mcp.mount_mcp(shell, ...)` | `shell.mountMcp(...)` |
| [An HTTP API](#authenticated-api-calls), credentials hidden from the agent | `shell.RegisterApi(...)` | `register_api(shell, ...)` | `shell.registerApi(...)` |
| [A real program from your machine](#real-host-programs) | `shell.RegisterHostCommand(...)` | `register_host_command(shell, ...)` | not yet |

The samples below are C#. [python/README.md](../python/README.md) and [typescript/README.md](../typescript/README.md)
show the same calls in their own names.

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

## Tools described by a JSON Schema

The same mapping is available without MCP, for any tools you can describe with a JSON Schema:

```csharp
shell.RegisterTools("crm", specs, async (tool, arguments, ct) => new ToolResult(await Call(tool, arguments)));
```

## Authenticated API calls

The machine has no [network](machine.md#network). `RegisterApi` lets the agent call a few real HTTP APIs that you choose, with
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

## Real host programs

Some tools are too big to re-implement: ffmpeg, DuckDB, OfficeCLI, your own binaries. You can expose a real
program from your machine as a command:

```csharp
using var duckdb = shell.RegisterHostCommand("duckdb");
```

A host program registered under a built-in's name takes its place: `RegisterHostCommand("git")` gives the agent
real git instead of [the built-in one](machine.md#git).

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
- **Environment.** The agent's own variables (`export NAME=...`), then `PATH` from your process,
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
else on disk. The network is off unless you pass `network: true` (real git needs it to clone and push). Folders the
program needs besides those (a runtime under `/opt`) go in as read-only extras: `Bubblewrap(false, "/opt/tool")`.
It throws when `bwrap` is missing or cannot create a sandbox (some distributions restrict unprivileged user
namespaces). For anything else (firejail, a container) write your own `Launcher`: it gets the command line and
the private folder and returns the command line to run.
