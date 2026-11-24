# Topwater reference

Everything the simulated machine supports, in one place. The C# and Python libraries behave the same.

- [Shell language](#shell-language)
- [Built-in programs](#built-in-programs)
- [Your own commands](#your-own-commands)
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
| Shell | `export` `unset` `env` `printenv` `set` `shift` `local` `declare` `read` `alias` `unalias` `history` `which` `type` `command` `test` `[` `true` `false` `bash` `sh` `source` `eval` `let` `sleep` `timeout` `time` `help` |
| System | `uname` `whoami` `hostname` `date` `ps` `id` `uptime` `free` `nproc` `sudo` `su` |

`help` lists everything, including your commands. Standard tools answer `--version` and `--help` the way the
real ones do. `find` supports `-name -iname -path -regex -type -size -mmin -mtime -newer -empty -maxdepth
-mindepth -print -print0 -exec -delete` with `!`, `-o` and parentheses. `sed` is a full engine: addresses, `{}` blocks, hold space, labels and branches, `-i`, `-E`.
`awk` covers patterns, ranges, `BEGIN`/`END`, fields, arrays, control flow, user-defined functions, `getline`
from files, `printf`, and the string functions. `jq` is a real parser with about 90 builtins: object
construction, string interpolation, `select`, `map`, `sort_by`, `group_by`, `to_entries`, `reduce`, `@csv`,
`--arg`, `-r`, `-e`, `-s`.

## Your own commands

**Python**

```python
from typing import Annotated
from topwater import Shell, command, CommandContext

class Tools:
    @command("orders", description="List orders")
    async def orders(self, status: Annotated[str | None, "Filter by status"] = None, limit: int = 20):
        return await db.fetch_orders(status, limit)      # lists, dicts and dataclasses print as JSON

    @command("count-lines")
    def count_lines(self, ctx: CommandContext) -> int:   # ctx gives stdin, stdout, the filesystem
        ctx.print(len(ctx.stdin.splitlines()))
        return 0                                         # an int is the exit code

shell = Shell().register(Tools())
shell.register_command("deploy", handler)                # or a plain handler(ctx)
```

**C#**

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

## Files and permissions

The filesystem is in memory. Put data anywhere through `shell.fs` (Python) or `shell.Fs` (C#):

```python
shell.fs.write_text("/home/agent/data/config.json", text)
shell.fs.write_bytes("/home/agent/logo.png", data)
```

The agent is a regular user called `agent`, not root:

- It owns its home directory and can use `/tmp`.
- `/etc`, `/usr`, `/bin`, `/var/log` and `/root` belong to root. Writing there fails with `Permission denied`,
  and `sudo` answers `agent is not in the sudoers file.`
- `chmod` is real: a script needs `chmod +x` before `./script.sh` runs, and `ls -l` shows the actual modes.
- Your app is not restricted. Files it creates belong to whoever owns the directory, so data you put in the
  agent's home is the agent's to edit, and data you put in `/etc` is read-only for it.

The machine looks like Ubuntu 24.04: `/etc/os-release`, `/etc/passwd`, `/bin` and `/usr/bin` listing every
command (yours included), and `/dev/null`.

## Documents

`to-markdown FILE...` turns Word, Excel, PowerPoint, PDF, HTML, CSV and JSON files into Markdown, so the agent
can search them with `grep` like any text. From your app:

```python
shell.fs.add_document("/home/agent/docs/policy.docx", "/real/path/policy.docx")   # or bytes
shell.fs.add_document("/home/agent/docs/policy.docx", data, also_markdown=True)   # also writes policy.docx.md

from topwater.convert import to_markdown
text = to_markdown(data, "report.xlsx")
```

```csharp
shell.Fs.AddDocument("/home/agent/docs/policy.docx", "/real/path/policy.docx");
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

```python
from topwater.host import register_host_command
register_host_command(shell, "git", env={"GIT_AUTHOR_NAME": "Bot", "GIT_AUTHOR_EMAIL": "bot@example.com"})
```

```csharp
using var git = shell.RegisterHostCommand("git");
```

Before each call the agent's workspace (its home directory by default) is copied to a private folder on disk;
the program runs there, and its changes are copied back. Options: `workspace`, `env`, `timeout`, `exclude`,
and `launcher` (wrap the command line, for example to run it inside a sandbox).

**This opens a door in the box.** The program is real and runs with your process's permissions. Topwater points
it at the private folder and redirects path arguments there, which stops casual wandering but is not a security
boundary. Wrap only tools you would let the agent use directly, and use `launcher` with a real sandbox
(bubblewrap, firejail, a container) when the stakes call for it.

## The tool description

Give the model one tool. In Python: `topwater.TOOL_NAME`, `TOOL_DESCRIPTION`, `TOOL_INPUT_SCHEMA`, and
`await topwater.execute_tool(shell, command)`. In C#: `BashTool.Name`, `BashTool.Description`,
`BashTool.InputSchemaJson` and `BashTool.ExecuteAsync(shell, command)`.

The default description tells the model the truth: it is in a sandboxed shell, Python, git and the network are
not available, and your programs are live. In testing this beat both pretending to be an ordinary machine and
announcing "this is a simulation" (see the README).

## Limits and safety

- Nothing the agent runs touches your machine, unless you register a real host program.
- Loops stop after 100,000 iterations (`max_loop_iterations` / `MaxLoopIterations`).
- Tool output is cut at 30,000 characters with a note saying how much was dropped.
- `set -e` and `pipefail` reset after every tool call, so one stray `set -e` can't affect later calls.
- A command that throws becomes an error message and exit code, not a crash.
- One shell is one session. Use a separate `Shell` per conversation; a shell is not safe to share between
  threads.

## Not supported

Arrays, `select` menus, background jobs (`&` runs things in order), `>(...)`, running shell commands from
inside `awk` (`print | "cmd"`), symlinks and `ln`, group permissions, and a real `python` or network access.
`sleep` really waits, so wrap calls in your own timeout if that matters.
