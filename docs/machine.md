# The machine

What the agent finds when it types a command: the shell language, the programs, `git`, the filesystem and what is
missing on purpose. All three implementations behave the same unless a line says otherwise.

- [Shell language](#shell-language)
- [Built-in programs](#built-in-programs)
- [git](#git)
- [Files and permissions](#files-and-permissions)
- [The system](#the-system)
- [Documents](#documents)
- [Network](#network)
- [Programs that are not here](#programs-that-are-not-here)
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

The working directory, variables, functions, aliases and files persist between tool calls. `set` and `shopt` options
do not: they reset after every call, so one stray `set -e` cannot affect later calls.

## Built-in programs

| Group | Programs |
|---|---|
| Files | `ls` `cd` `pwd` `cat` `mkdir` `rmdir` `rm` `cp` `mv` `ln` `touch` `find` `tree` `stat` `du` `df` `file` `realpath` `readlink` `basename` `dirname` `mktemp` `chmod` `chown` `chgrp` `umask` `less` `more` |
| Text | `echo` `printf` `grep` `egrep` `fgrep` `head` `tail` `wc` `sort` `uniq` `cut` `sed` `tr` `tee` `xargs` `awk` `jq` `bc` `diff` `cmp` `nl` `tac` `rev` `paste` `column` `seq` |
| Documents and binary | `to-markdown` `xxd` `hexdump` `od` `strings` `base64` `md5sum` `sha1sum` `sha256sum` `sha512sum` |
| Archives | `tar` (`c` `x` `t`, `-f` `-z` `-v` `-C` `-O` `--strip-components`) `gzip` `gunzip` `zcat` (`-c` `-d` `-k` `-f` `-l` `-t` `-v` `-1`..`-9`) `zip` (`-r` `-q`) `unzip` (`-l` `-o` `-n` `-d` `-p` `-q`) |
| Shell | `export` `unset` `env` `printenv` `set` `shift` `local` `declare` `read` `mapfile` `readarray` `alias` `unalias` `history` `which` `type` `command` `test` `[` `true` `false` `bash` `sh` `source` `eval` `let` `sleep` `timeout` `time` `help` |
| System | `uname` `whoami` `hostname` `date` `ps` `id` `uptime` `free` `nproc` `clear` `sudo` `su` `apt` `apt-get` `apt-cache` `dpkg` (the six from `sudo` on only refuse; see [Programs that are not here](#programs-that-are-not-here)) |
| Version control | `git`: see [git](#git) |
| Web | `curl` `wget`: offline unless the host allows sites; see [Network](#network) |

`help` lists every program, yours included. Standard tools answer `--help` and `--version`. The options each command
accepts, and which implementation accepts them, are in [`spec/commands`](../spec/README.md).

| Program | Notes |
|---|---|
| `find` | `-name -iname -path -regex -type -size -mmin -mtime -newer -empty -maxdepth -mindepth -print -print0 -exec -delete`, with `!`, `-o` and parentheses |
| `sed` | a full engine: addresses, `{}` blocks, hold space, labels and branches, `r` and `R`, `-i`, `-E` |
| `ls` | the usual options plus `-s`, `--time-style` (`full-iso`, `long-iso`, `iso`, `+FORMAT`) and `--full-time` |
| `awk` | patterns, ranges, `BEGIN`/`END`, fields, arrays, control flow, user-defined functions, `getline` from files, `printf` and the string functions. It behaves as Ubuntu's `mawk`: `--version` says so, and dividing by zero gives `inf`, `-inf` or `-nan` |
| `jq` | a real parser with about 90 builtins: object construction, string interpolation, `select`, `map`, `sort_by`, `group_by`, `to_entries`, `reduce`, `@csv`, `--arg`, `-r`, `-e`, `-s` |
| `bc` | .NET only. Arbitrary precision, the full language (variables, arrays, `define`, `if`/`while`/`for`, `print`, `ibase`/`obase`) and the `-l` math library. Output matches GNU bc 1.07.1 digit for digit on `+ - * / % ^` and `sqrt`; the `-l` functions are accurate to the requested scale, within a last digit or two of GNU at large scales. One run stops after 5 s and a number is capped at 100,000 digits. `read()` is not supported |

Error messages follow bash 5.2 and GNU coreutils, grep and sed on Ubuntu 24.04. The shell's own errors start
`bash: line N:` as under `bash -c` (`./s.sh: line N:` in a script, `environment: line N:` in a function), and each call
counts its lines from 1. A sed script error says where it is (`-e expression #1, char 5:`), and a bad grep or sed
regex gets GNU's message.

## git

`git` is Topwater's own implementation of the common commands of git 2.43, for repositories in the machine. There is
no network: `clone`, `fetch`, `pull`, `push` and `submodule` answer that, `remote` lists nothing, and less common
local commands (`rebase`, `cherry-pick`, `revert`, `blame`, ...) say they are not available and name a substitute.
`git help` lists what is there and `git help <command>` describes each one; options are in
[`spec/commands/git.json`](../spec/commands/git.json).

| | |
|---|---|
| Work | `init` `status` (`-s`, `-b`, `--porcelain`, `-u`) `add` (`-A`, `-u`, `-f`, `-n`) `rm` (`--cached`, `-r`) `mv` `commit` (`-m`, `-F`, `-a`, `--amend`, `--no-edit`, `--allow-empty`, `--author`, `--date`, paths) `restore` (`--staged`, `--source`) `reset` (`--soft`, `--mixed`, `--hard`, paths) `stash` (`push`, `save`, `list`, `show`, `pop`, `apply`, `drop`, `clear`, `-u`) `clean` (`-n`, `-f`, `-d`, `-x`, `-X`) |
| History | `log` (`--oneline`, `-n`/`-<n>`, `--stat`, `-p`, `--format`/`--pretty` with `%H %h %s %b %an %ae %ad %d` and more, `--graph` for linear history, `--all`, `--author`, `--grep`, `--since`, `a..b`, `-- paths`) `show` (commits, tags, `rev:path`) `diff` (work tree, `--cached`, commits, `a..b`, `a...b`, `--stat`, `--name-only`, `--name-status`, `--numstat`, `-U<n>`, `--quiet`) |
| Branches | `branch` (`-d`, `-D`, `-m`, `-v`, `--show-current`) `checkout` (`-b`, `-B`, `--detach`, `-- paths`) `switch` (`-c`, `-C`, `--detach`, `-`) `merge` (fast-forward, three-way with conflict markers, `--no-ff`, `--ff-only`, `--abort`, `--continue`) `tag` (`-a`, `-m`, `-d`, `-l`) |
| Plumbing | `rev-parse` `rev-list` `ls-files` `cat-file` `config` (`--global`, `--local`, `--get`, `--unset`, `--list`), `git -C dir`, `git -c name=value` |

Revisions: `HEAD`, `@`, branch and tag names, full or short ids, `~n`, `^n`, `^{tree}`, `rev:path`, `@{-1}`.
`.gitignore` files (with `!`, `dir/`, `**` and anchored patterns) and `.git/info/exclude` apply.

**Format.** The repository is git's own: zlib-compressed loose objects named by their SHA-1, a version 2 index, loose
refs (packed-refs are read and updated), reflogs for `HEAD` and branches, and stash entries in `refs/stash`'s reflog.
Real git reads what Topwater writes (`git fsck --strict` is clean) and Topwater reads what real git writes, except
packfiles: a repository whose objects were packed (by `git gc` or a clone) gets a clear error.

**Identity.** As in real git, a commit needs an email: from `GIT_AUTHOR_EMAIL`/`GIT_COMMITTER_EMAIL`, `user.email`
(`git config --global` writes `~/.gitconfig`) or `EMAIL`; otherwise `git commit` fails with git's "Please tell me who
you are" message. The name defaults to the user's name. To preset an identity, the host sets those environment
variables on the shell or writes `~/.gitconfig` into the machine before the agent starts, which saves the agent a turn:

```csharp
shell.Fs.WriteAllText("/home/agent/.gitconfig", "[user]\n\tname = Agent\n\temail = agent@example.com\n[init]\n\tdefaultBranch = main\n");
```
```python
shell.fs.write_text("/home/agent/.gitconfig", "[user]\n\tname = Agent\n\temail = agent@example.com\n[init]\n\tdefaultBranch = main\n")
```
```ts
shell.fs.writeText('/home/agent/.gitconfig', '[user]\n\tname = Agent\n\temail = agent@example.com\n[init]\n\tdefaultBranch = main\n');
```

Without `init.defaultBranch` the first branch is `master`, as in git 2.43. Commit times come from the
shell's clock, or `GIT_AUTHOR_DATE`/`GIT_COMMITTER_DATE`. In C# and TypeScript the clock is the virtual one with
`StartTime`, so recordings replay to the same commit ids; Python has no virtual clock, so its ids repeat only with
those variables set. Each loose object takes a filesystem block, so `du` shows about 4 KB per object (10,000 small
commits read as about 120 MB).

**Limits.** Objects larger than the string limit, histories over a million commits, trees nested over 256 deep and
diffs that would take too long (they fall back to "all lines changed") are bounded, and long walks stop at the call's
time limit. Everything under `.git` is untrusted: objects are checked against their ids, the index against its
checksum, no path inside `.git` is followed through a symbolic link, and tree or index entries named `..`, `.git` or
containing `/` are refused, so a hostile repository cannot write outside its work tree.

**Differences from real git.** No packfiles, submodules, hooks, editor, pager or colors; reflogs are written but there
is no `reflog` command or `@{n}`; `--graph` draws linear history only; when a change could be placed at several equal
positions, diffs place it at the lowest one, where real git's indent heuristic sometimes picks another.

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
- Symbolic and hard links work (`ln`, `ln -s`, `readlink`), and 40 links in one lookup fail as on Linux.
- `chmod` is real: a script needs `chmod +x` before `./script.sh` runs, and `ls -l` shows the actual modes.
- Permissions work as on Linux: reaching a path needs search (`x`) permission on every directory above it, so a
  file you put under `/root` (mode 700) is out of reach whatever its own mode. Listing a directory needs `r`;
  creating, deleting or renaming in it needs `w`. `cp -r` skips what the agent can't read. In `/tmp` (sticky)
  the agent can delete or rename only its own entries.
- Your app is not restricted. Files it creates belong to whoever owns the directory, so data you put in the
  agent's home is the agent's to edit, and data you put in `/etc` is read-only for it. That holds while an
  agent command is running, too: only that command, and the commands you registered when it calls them, act
  as the agent.

## The system

The machine looks like an Ubuntu 24.04 install that has been in use for a while:

- **Folders.** The standard tree, including the usual empty folders under `/usr`, `/usr/local`, `/usr/share` and `/var`.
- **`/etc`.** `os-release` (a link to `/usr/lib/os-release`, as on Ubuntu), `lsb-release`, `issue`, `shells`,
  `debian_version`, `passwd`, `group`, `hosts`, `profile`, `bash.bashrc`, `profile.d` and the skeleton dotfiles in
  `/etc/skel`.
- **`/proc`.** `cpuinfo` and `meminfo` agree with `nproc` and `free`. `/proc/self` points at the shell's own process
  (`$$`).
- **Programs.** `/bin` and `/usr/bin` list every program, yours included, with plausible sizes and install dates.
  Shell built-ins such as `cd` and `eval` have no file there, as on a real system. Commands are found through `PATH`,
  with bash's `hash` table.
- **Dotfiles.** The agent's home has `.bashrc`, `.profile` and `.bash_logout`. They are our own text with the effect
  of Ubuntu's. A login shell (`bash -l`) reads `/etc/profile`, which runs `/etc/profile.d/*.sh`, then the first of
  `~/.bash_profile`, `~/.bash_login` and `~/.profile`. `~/.bashrc` sets the prompt and aliases only in an interactive
  shell, so the agent's calls, like `bash -c`, read none of them.
- **Unreadable places.** As for an ordinary user on Ubuntu, `/root`, `/var/cache/ldconfig`,
  `/var/cache/apt/archives/partial` and `/proc/tty/driver` cannot be read, so `find /` prints `Permission denied` for
  them and exits 1.

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

## Network

By default the machine has no network. `curl` and `wget` exist, but every host fails to resolve
(`curl: (6) Could not resolve host`), as on a machine that is offline. `git clone`, `fetch`, `pull` and `push` say
there is no network, and there is no `ssh`.

The host can open it in four ways:

- [Web access](web.md): `curl` and `wget` reach the sites the host lists, and only those.
- [An HTTP API](programs.md#authenticated-api-calls) called by alias, with a credential the agent never sees.
- [An MCP server](programs.md#mcp-servers).
- [A real host program](programs.md#real-host-programs).

## Programs that are not here

A command the machine does not have fails exactly as in bash: `bash: line 1: python3: command not found` on stderr,
status 127, and nothing else. Captured output (`out=$(cmd 2>&1)`) is therefore what bash gives. The tool description
already tells the model what the machine lacks and that `help` lists what it has.

What each commonly tried program does:

| Program | Here | Why |
|---|---|---|
| `python`, `python3`, `pip`, `pip3`, `node`, `npm`, `perl` | command not found (127) | not part of the machine; a fake interpreter would mislead. Ubuntu itself ships `perl`, so this one differs from a real install |
| `apt`, `apt-get`, `apt-cache` | exist; every subcommand prints `E: Packages cannot be installed on this machine: ...` and exits 100, apt's error status | agents reach for apt when a program is missing; saying plainly that nothing can be installed stops them retrying |
| `dpkg` | exists; prints `dpkg: error: packages cannot be installed or listed ...` and exits 2 | same, with dpkg's status |
| `sudo apt ...`, `sudo dpkg ...` | the package manager's own message | more useful than the sudoers refusal every other `sudo` command gets |
| `snap` | command not found | not in Ubuntu's server or container images either |
| `curl`, `wget` | exist; fail with `Could not resolve host` unless the host allowed the site | see [Network](#network) |
| `ssh` | command not found | the machine has no [network](#network) |

## Not supported

- Real background jobs. `cmd &` runs to completion before the next command, so `wait` only reports its status.
- Job control (`fg`, `bg`, `%1`), `>(...)`, `coproc`, `compgen`/`complete` and `caller`.
- Shell commands run from inside `awk` (`print | "cmd"`).
- Group permissions: a file has an owner and no group of its own.
- A real `python`, and network access beyond [web access](web.md) to the sites the host allows.

`sleep` really waits, up to the call's time limit, unless the shell has a
[virtual clock](snapshots-and-recordings.md#deterministic-runs).

A few shell behaviours differ from bash, on purpose or for simplicity:

- The last command of a pipeline runs in the current shell (like `shopt -s lastpipe`), so
  `cmd | while read ...; done` can set variables.
- Aliases expand in scripts too, and extglob patterns work without `shopt -s extglob`.
- `declare -f` prints a function as it was written.
- `local x` leaves `x` empty rather than unset.
- `set -x` traces simple commands only.
- `printf` pads by characters, not bytes.
