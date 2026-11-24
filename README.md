<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/topwater_lockup_dark.png">
    <img alt="Topwater" src="assets/brand/topwater_lockup.png" width="420">
  </picture>
</p>

<p align="center">
  <b>Give your agent one tool, <code>bash</code>, and let it drive your app through a Linux shell it already knows.</b><br>
  C# (.NET 8) and Python (3.10+, no dependencies) · in-process · nothing touches the real OS
</p>

---

Models have seen far more shell sessions than they have seen your bespoke JSON tools. Topwater turns your application's
tools into **programs on a sandboxed Linux machine**, so the model can do this:

```console
$ orders --status open --min-total 100 | jq -r '.[].customer'
Acme Corp
Umbrella
$ grep -rn TODO ~/project | awk -F: '{print $1}' | sort | uniq -c
      2 /home/agent/project/src/app.py
      1 /home/agent/project/src/util.py
```

Everything is emulated in-process: a clean-room bash parser and interpreter, an in-memory filesystem, and C#/Python
implementations of ~100 standard utilities (`grep`, `sed`, `awk`, `jq`, `find`, `sort`, `diff`, ...). Your registered
commands sit next to them in `/usr/bin` and get `--help` for free.

## How it works

```
 LLM ──tool call: bash {"command": "orders --status open | jq length"}──▶ Shell.RunAsync / shell.run_async
                                                                           │
          parse (pipes, redirects, $(...), loops, functions, heredocs...)  │
                                                                           ▼
             built-ins (grep, awk, jq...)   ◀── pipe ──▶   your [Command] / @command methods
                                    │                               │
                                    └──── in-memory filesystem ─────┘
 LLM ◀──── stdout + stderr + "[exit code N]" (truncated at 30k chars) ────────────────┘
```

## Quick start

### C#

```csharp
using Topwater;

var shell = new Shell();                            // user "agent", cwd /home/agent
shell.Register(new OrderTools());                   // [Command] methods become programs
shell.Register("deploy", async ctx => { ... });     // or register a handler directly
shell.Fs.WriteAllText("/data/config.json", json);   // mount data for the agent to explore

// Tool definition for your LLM SDK:
//   name: BashTool.Name ("bash")   description: BashTool.Description   schema: BashTool.InputSchemaJson
string result = await BashTool.ExecuteAsync(shell, command);   // when the model calls the tool

public class OrderTools
{
    [Command("orders", Description = "List orders")]
    public async Task<List<Order>> List(
        [Description("Filter by status")] string? status = null,
        int limit = 20,
        bool includeArchived = false) => ...;       // objects are printed as JSON
}
```

### Python

```python
from typing import Annotated
import topwater
from topwater import Shell, command

class OrderTools:
    @command("orders", description="List orders")
    async def list_orders(self, status: Annotated[str | None, "Filter by status"] = None,
                          limit: int = 20, include_archived: bool = False):
        return [...]                                # lists/dicts/dataclasses are printed as JSON

shell = Shell().register(OrderTools())
shell.fs.write_text("/data/config.json", data)

# Tool definition: topwater.TOOL_NAME, topwater.TOOL_DESCRIPTION, topwater.TOOL_INPUT_SCHEMA
result = await topwater.execute_tool(shell, command)   # or shell.run(command).to_tool_output()
```

Either way, `orders --status open --limit 5 --include-archived`, `orders open` (positional) and `orders --help`
all work. Parameters become `--kebab-case` options or positionals, `bool` becomes a flag, and enums, arrays,
stdin and cancellation are supported. Return a string (stdout), an int (exit code), or any object (JSON).

## Try it in your browser

```bash
cd python && PYTHONPATH=src python -m topwater.web --demo     # then open http://127.0.0.1:8000
```

A small web terminal (standard library only) runs your commands in one session, exactly as an agent's tool call
would. It has history, Tab completion and shareable `?run=` links. `--demo` preloads sample files and an `orders`
command. From code, `topwater.web.serve(factory=make_my_shell)` serves your own configured shell. It listens on
localhost and has no authentication.

<p align="center"><img alt="Topwater web terminal" src="assets/screenshots/web-terminal.png" width="760"></p>

## The tool description

The default description is honest about the sandbox and keeps the stakes real:

> Run a command in a sandboxed Linux shell (bash). The working directory, environment variables and files persist
> between calls. Standard command-line tools are available (ls, cat, grep, sed, awk, jq, find, sort, diff, ...);
> Python, network access, git and package installation are not. Other programs on this machine are live interfaces
> to this application's systems, so their actions are real: run `help` to list them and `<program> --help` for usage.

We tested it against two alternatives (see [Results](#results-with-a-real-model)). Pretending to be an ordinary
machine made the model spend turns probing for Python, git and graders. Saying "this is a simulation" didn't help
either. Being upfront about the limits did best.

## What the shell supports

- **Syntax:** quoting and escapes, `$VAR`, `$(...)`, backticks, `~`, globs, brace expansion (`{a,b}`, `{1..10}`, `{01..05}`), process substitution `<(...)`
- **Parameter expansion:** `${x:-d}` `${x:=d}` `${x:+a}` `${x:?msg}` `${#x}` `${x%pat}` `${x%%pat}` `${x#pat}` `${x##pat}` `${x/a/b}` `${x//a/b}` `${x:1:3}` `${x^^}` `${x,,}`
- **Control flow:** `if/elif/else`, `for`, `for ((;;))`, `while`/`until`, `case`, `break`/`continue`, functions with `local`/`return`, `$1`/`$@`/`$#`, `[[ ]]` (globs, `=~`), `$(( ))`/`(( ))`/`let`
- **Lists and redirects:** `|` `&&` `||` `;` `!` `{ }` `( )`, `VAR=x cmd`, `>` `>>` `<` `2>` `2>&1` `&>`, heredocs, `<<<`, redirects on loops (`while read -r -d '' f; do ...; done < <(find . -print0)`)
- **Scripts:** `./script.sh args`, `bash -c`, `source`, `set -e`, `set -o pipefail`, `exit`, `eval`
- **Built-in programs:**
  - files: `ls cd pwd cat mkdir rmdir rm cp mv touch find tree stat du df file realpath readlink basename dirname mktemp chmod less`
  - text: `echo printf grep head tail wc sort uniq cut sed tr tee xargs awk jq diff nl tac rev paste column seq base64 sha256sum`
  - shell: `export env set shift local read alias history which type command test [ bash source eval let sleep timeout time help`
  - system: `uname whoami hostname date ps id uptime free nproc`, with `--version`/`--help` that look like the real GNU tools
- **Realism:** `/etc/os-release` (Ubuntu 24.04), `/etc/passwd`, `/bin` and `/usr/bin` listing every command (yours included), `/dev/null`
- **A regular user account:** the agent runs as `agent`, not root. It owns its home directory and can use `/tmp`,
  while `/etc`, `/usr`, `/bin`, `/var/log` and `/root` belong to root. Writes there fail with `Permission denied`,
  `chmod`/`chown` on them fail with `Operation not permitted`, and `sudo` says `agent is not in the sudoers file`.
  `chmod` on the agent's own files is real: scripts need `chmod +x` before `./script.sh` runs, and `ls -l`/`stat`
  show the actual modes. Your app is unrestricted when it uses `shell.Fs` / `shell.fs` directly, and files it creates
  belong to whoever owns the directory, so data you put in the agent's home is the agent's to edit.
- **Safety:** loops stop after 100,000 iterations (configurable). `set -e` and `pipefail` reset after every tool call.

**Not supported:** arrays, `select`, background jobs (`&` runs in sequence), `>(...)`, `awk` user functions and
`getline`, `jq` object construction, and group permissions.

## Results with a real model

`python/examples/gemini_harness.py` gives Gemini (`gemini-flash-latest`) only the `bash` tool and six everyday tasks,
then checks the results itself. It looks at the answer and at files left on the virtual machine. Two runs per task
per description:

| Tool description | Tasks solved | Avg commands | Avg off-task probes |
|---|---|---|---|
| "an ordinary Linux machine" | 9 / 12 | 10.8 | 2.9 |
| **"a sandboxed shell; no Python, network or git" (default)** | **11 / 12** | **9.4** | **1.7** |
| "a simulated environment" | 9 / 12 | 11.4 | 2.8 |

`python/examples/handbook_quiz.py` loads a 205 KB university student handbook into the filesystem and asks five
questions only the document can answer. Gemini scored **5/5** in two out of two runs, with exact quotes. It worked
the way a person at a terminal would: `find` to locate the file, `grep -n -C 5` per topic, `sed -n` to read
around each hit. When `grep fish` found nothing for "can I keep a fish tank?", it tried `pet`, then `aquarium`.

Every gap these runs exposed became a regression test. That includes the exact backup script Gemini wrote, which
needed `read -d ''`, `find -print0` and `<(...)`.

## Project layout

```
src/Topwater/              C# library (Shell, parser, filesystem, built-ins, [Command] binding)
tests/Topwater.Tests/      xUnit tests
python/src/topwater/       Python port (same behaviour, same test cases)
python/tests/              unittest tests
python/src/topwater/web.py web terminal (python -m topwater.web)
python/examples/           real-model harness and the handbook quiz
assets/                   logo, icon and wordmark (SVG + PNG), screenshots
```

## Development

```bash
dotnet test                                          # C#: 69 tests
cd python && python -m unittest discover -s tests    # Python: 17 tests (80+ cases)
GEMINI_API_KEY=... VARIANTS=plain,restricted,fake REPEAT=2 python examples/gemini_harness.py
```

The two implementations don't share code. Keep them in step by adding each behaviour change as a test case in both
suites.

## License

Not yet chosen. The bash parser is a clean-room implementation, so any license is available.
