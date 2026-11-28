# Changelog

Topwater is in preview: APIs and file formats may change between previews. The packages are on NuGet, PyPI and npm
(from 0.1.0-preview.4 for PyPI and npm).

## Unreleased

### Connectors (.NET)

- **OneDrive and SharePoint.** `Topwater.Connectors.GraphSource` mounts a OneDrive, a document library or a whole
  SharePoint site (its libraries, lists and subsites) through Microsoft Graph. Files are their real bytes; a list item
  is a `Title.md` file of front matter, one column a line. Moves, renames, new folders, deletes and column edits become
  Graph requests checked with `If-Match`; names and values SharePoint would refuse are refused before anything is
  sent. Signing in stays with the host (a token function); no new package dependency. `examples/GraphMount` signs in
  with Azure.Identity and lets an agent restructure a site, staged. [docs/connectors/graph.md](docs/connectors/graph.md)

### Safety (.NET)

- **jq sorting on deep values.** `sort`, `unique`, `group_by`, `unique_by`, `sort_by`, `min`, `max` and `del` on values
  nested deeper than the stack allows now stop with `maximum nesting level exceeded`, as the other jq helpers do,
  instead of `Failed to compare two elements in the array`. A test runs every recursive jq helper on a deep value from
  a host thread with a 256 KB stack.

## 0.1.0-preview.5 (2026-11-27)

Everything since `0.1.0-preview.4`. With this preview Topwater is parked: it stays available and is maintained
lightly. [ROADMAP.md](ROADMAP.md) lists the work that is parked on branches and the ideas not started.

### Mounted sources

- **Outside files as a folder.** A host can show an outside store of files as a directory in the machine:
  `shell.MountAsync("/mnt/notes", source, MountMode.Staged)` (Python `shell.mount`, TypeScript `shell.mount`). A source
  is a small class with `list`, `stat`, `read` and, if it can, `write`, `delete`, `rename` and `mkdir`; its credentials
  never reach the agent. `LocalFolderSource` (a host directory, read-only unless `writable`; Obsidian vaults work) and
  `MemorySource` (for tests) come with it. In `read-only` mode nothing can change; in `staged` mode the agent's edits
  stay in the machine and `PendingChanges` returns them as a change set (write, delete, rename, mkdir, each with the
  version it was made on) to apply in full or in part (`ApplyChangesAsync` stops at a conflict) or discard; in `live`
  mode they are applied after every command and when the call ends. File contents are fetched when a command names
  them, within the disk cap. A new command, `mount`, lists the mounts, shows pending changes (`-c`), lists a source
  again (`-r`) and drops the agent's changes (`--discard`). Large sources are listed 2,000 entries at a
  time, the rest when a command names it. Snapshots keep each mount's definition and pending changes, not the source's
  files (format version 3 when there is a mount); a snapshot never sets a mode or applies anything, and the changes in
  one loaded from a file wait for `AcceptSnapshotChangesAsync`. Recordings keep the sources' answers, so a replay never
  calls them. The tool description names each mounted folder and its mode. See [Mounted sources](docs/mounts.md).

### Command line and container image

- **Container image.** Each release now pushes `ghcr.io/gr-ant/topwater` for linux/amd64 and
  linux/arm64 (tags: the version, `preview`, and `latest` for releases without a preview suffix):
  `docker run -i --rm ghcr.io/gr-ant/topwater:preview mcp`. It is the standalone Linux binary on Ubuntu's chiseled .NET
  base, runs as a non-root user in `/data`, and carries an SBOM and a signed build provenance attestation. CI builds
  it for amd64 and arm64 and smoke-tests a run, an MCP session and `--state` on every push. See
  [Docker](docs/cli.md#docker).
- **`topwater mcp` answers what it has read before it stops.** When its input closed, the server used to stop at once
  and cancel the requests still running, so `printf ... | topwater mcp` printed no answers and exited 0. It now
  answers every request already read (up to one call limit plus 10 s), then stops.
- **A `--state` file that cannot be written is an error.** `shell`, `run` and `mcp` used to log "could not save" and
  exit 0, losing the machine; they now check at start that the file can be written (exit code 2 with the path), and a
  save that fails later makes them exit with 1. `export` and `snapshot save`, which only read `--state`, are
  unchanged.

### git

- **More git history and checking commands.** `git log -S<string>` and `-G<regex>` (the pickaxe; a commit's diff
  shows only the files that matched), `git log --parents`, `git rev-list --reverse` and `--parents`, `git diff
  --check` (trailing whitespace, a space before a tab in the indent, new blank lines at the end; exit 2), `git
  merge-base` (`-a`, `--octopus`, `--is-ancestor`) and `git fsck` (unreadable, missing and dangling objects, loose and
  packed).
- **`git apply`, `git am`, `git format-patch` and `git bisect`.** `apply` takes unified diffs from `git diff` or
  `diff -u` (`--check`, `--stat`, `--numstat`, `--summary`, `--index`, `--cached`, `-R`, `-p`); each hunk must match
  exactly, at an offset if need be, and nothing is applied when one fails. `am` applies `format-patch` mailboxes as
  commits with their authors (`--continue`, `--skip`, `--abort`, `--show-current-patch`). `bisect` has `start`,
  `good`/`bad` (and `old`/`new`), `skip`, `reset`, `log` and `run`, where exit 125 skips a commit. `apply` and `am`
  keep symbolic links as links and refuse to write beyond one, `am` decodes RFC 2047 encoded `From` and `Subject`
  headers, and `git status` mentions an `am` or `bisect` in progress.
- **`git checkout .` works.** It used to fail with "invalid path 'refs/heads/.'" in the Python port.
- **`git diff --check` reports leftover conflict markers.**

### Other commands

- **`comm` and `join`, `grep --label`, `chmod --reference`.** New programs `comm` (`-1 -2 -3`, `--check-order`,
  `--output-delimiter`, `--total`) and `join` (`-t -1 -2 -j -a -v -o -e -i`, `--header`).
- **`tar --exclude`, `--owner`, `--group`, `--numeric-owner` and `--transform`.** `--exclude` takes a wildcard matched
  against the name or any run of its components; `--transform`/`--xform` renames members with `s/regex/replacement/`
  (flags `g`, `i`, `x`, a number); a name it turns into an absolute or `../` path is stripped back inside the
  target directory, as GNU tar does. Repeated options now keep every value in all three ports. In Python, wildcards
  in `find -name`, `case` and `tar --exclude` are now stopped by the call's time limit like other patterns.
- **jq `def`.** Functions with filter and `$value` parameters, recursion and lexical scope (Python and C#; the
  TypeScript port had them). Nesting stops at 100 calls with "recursion too deep", the same in every port.
- **`tar` from a deleted working directory.** After `rm -rf` of the current directory, `tar tf /abs/a.tar` failed
  with `tar: {file}: Cannot open`. tar now needs the working directory only when given `-C` or relative names, as
  real tar does, and the spec tests check that every message lookup supplies all of its placeholders.
- **`curl --retry-max-time` counts the waits already slept,** so a timer that wakes a millisecond early can no longer
  add a retry.

### Compatibility

- **TypeScript: `Shell`'s internal members now show their types in the `.d.ts`.** The shell's source is split into
  `shell/*.ts` parts (see [docs/architecture.md](docs/architecture.md)). The 97 members that were `private`
  (`execScript`, `expandWords`, `cwdPath`, `lineno`, ...) are now typed members marked `@internal`, like the ones that
  already were; they appear in `dist/shell.d.ts` and the new `dist/shell/*.d.ts`, are not public API and may change.
  The public members and the package entry points' types are unchanged.
- **The three ports follow one source layout.** They now share file names, sections and command
  order ([docs/architecture.md](docs/architecture.md), checked by `tools/check_layout.py`). In Python, `topwater.shell`
  is a package and the commands are split by domain (`topwater.commands.net` is now `topwater.commands.web`); imports
  from the package's top level are unchanged. Every module now imports on its own in a fresh process.

### Benchmark

- **Round 2 results.** Large-document tasks and harder shell tasks, with function tools plus Python and Docker bash
  plus Python as extra arms; see [benchmarks/RESULTS.md](benchmarks/RESULTS.md).

## 0.1.0-preview.4 (2026-11-26)

First release on PyPI (`pip install --pre topwater`) and npm (`npm install topwater@next`).

- **PyPI and npm release jobs.** A release tag can also publish the Python package to PyPI and the TypeScript
  package to npm with trusted publishing, once each is switched on. All three ports' tests pass before anything is
  published, and both take their version from the tag: `v0.1.0-preview.4` is `0.1.0a4` on PyPI (PEP 440 has no
  "preview"; alpha, not release candidate) and `0.1.0-preview.4` on npm, under the `next` tag.
- **More `curl` and `wget` options.** `curl --retry` (with `--retry-delay`, `--retry-max-time`, `--retry-all-errors`,
  `--retry-connrefused`), `--proto`, `--proto-redir`, `--fail-early`, more `-w` variables (`time_total` and the other
  times, `size_upload`, `scheme`, `errormsg`, ...), and `--tlsv1.2`, `--http1.1`, `--no-progress-meter`, `-#`
  accepted and ignored; `wget -nv`, `-P`, `-nc`, `--spider`, with `-t` and `--show-progress` accepted. With several
  URLs and no `--fail-early`, `curl` now exits with the last URL's status, as curl does.
  `--retry` waits at least the server's `Retry-After`; on a virtual clock the `-w` times read 0, so recordings replay.
- **Malformed web responses fail the same way in every port**, as curl does: a body that ends before its
  `Content-Length` or last chunk is written as far as it came (curl 18, wget 4) and recorded that way, as is one the
  connection resets (curl 56); an unreadable status line or header (or a NUL in one) is 8, no response 52, a bad chunk
  size 56; a longer body is cut at its `Content-Length`; folded headers are joined; interim responses (`103 Early
  Hints`) are skipped and count toward the 64 KB header limit. Recordings may hold `error` `partial`, `reset`, `weird`
  and `empty`.
- **A compressed body from a host with a credential is scrubbed only when it is whole.** A gzip or deflate stream cut
  short (by the server, or by the response cap) used to be decoded as far as it went, so the scrub could miss a
  credential cut in two; it now fails, and nothing of any cut-short body from such a host is kept.
- **No global switch for the private-address check.** The test-only hooks that let loopback through were process-wide
  settings in each library; the tests now give their own shell a test-only transport instead.
- **`find -links N`** selects by link count, and `/usr/local/man` is a link to `share/man` as on Ubuntu.
- **git reads packed repositories.** A repository copied in after `git clone` or `git gc`, with its objects in
  `.git/objects/pack` and its refs in `packed-refs`, works for every command; new objects are written loose next to the
  pack. The pack and its index are checked and bounded as hostile input (checksums, offsets, delta depth and cycles,
  inflated sizes). `git gc` and `git repack` are not available: git here writes no packs. This replaces the "history
  is packed" error.
- **git shows a clone's remote-tracking branches.** `branch -r`/`-a`/`-vv`, `log --decorate`, ahead/behind in
  `status` and `status -sb`, `checkout <name>` creating a tracking branch from `origin/<name>`, `@{u}`, and
  `remote -v` listing the remotes in `.git/config`. A detached HEAD is named after what was checked out
  ("HEAD detached at v1"), as real git does.

## 0.1.0-preview.3 (2026-11-26)

Everything since `0.1.0-preview.2`.

### Compatibility

- **Snapshots are format 2.** A default machine now holds symbolic links (`/etc/os-release` and three more), so every
  machine this version saves is snapshot format 2. `0.1.0-preview.2` refuses such a file and asks for an upgrade. This
  version still loads format 1.
- **Recordings are `.topwater` files.** A recording is saved as one zip that holds the machine, the calls and the
  checkpoints, the same file a snapshot is. Older `.jsonl` recordings still load, and saving one again (or
  `topwater export`, or a test run with `TOPWATER_RERECORD=1`) converts it.
- **`command not found` is exactly bash's line.** `bash: line 1: python3: command not found`, status 127, with no
  `(run 'help' ...)` hint after it.
- **Error messages follow bash and GNU.** The shell's own errors start `bash: line N:`, and file and usage errors
  use the GNU wording. Code that matched Topwater's earlier messages needs a look.
- **The default tool description changed:** it now says that git works on local repositories, and on a shell with
  web access it names the sites and methods. Integrations compute it per shell (`BashTool.DescriptionFor(shell)`,
  `tool_description(shell)`, `BashTool.descriptionFor(shell)`) unless you pass your own.
- **`curl` and `wget` exist.** Without a web policy they fail as on an offline machine (`Could not resolve host`,
  exit 6 and 4) where they used to be `command not found` (127).
- **On a UI thread, .NET calls run on the thread pool.** Called from a thread with a synchronization context
  (WinForms, WPF, MAUI, Avalonia), the public async methods (`RunAsync`, `BashTool.ExecuteAsync`, the `AIFunction`,
  replay, MCP and container methods) move their work to the thread pool, so blocking on them no longer deadlocks.
  Host commands then run on a pool thread, not the UI thread; a handler that touches UI objects must marshal itself.
  Console apps and ASP.NET Core are unchanged. See [docs/programs.md](docs/programs.md#your-own-commands).
- **TypeScript needs Node 22.3 or later** (was 22).
- **The CLI refuses a recording as `--state`,** because the state file is rewritten as the machine alone. Use
  `--from`, or `topwater import`.
- **The npm package declares no framework peers.** `ai` and `@openai/agents` are no longer optional peer
  dependencies; install the SDK you use. The package now packs only `dist`.

### The machine

- **`git`** for local repositories, in all three implementations: `init`, `add`, `commit`, `status`, `log`, `diff`,
  `branch`, `checkout`, `switch`, `merge` with conflict markers, `stash`, `tag`, `reset`, `restore` and plumbing. The
  repository is git's own on-disk format, so real git reads it. No network and no packfiles.
- **Symbolic and hard links:** `ln`, `readlink`, link-aware `ls`, `cp`, `mv`, `rm`, `find`, `grep -r`/`-R` and `tar`.
- **Commands are found through `PATH`,** as in bash, with its `hash` table; `#!` scripts count as a nesting level.
- **A more convincing Ubuntu:** `/etc/profile`, `/etc/profile.d`, `/etc/skel`, `/proc/self`, the usual folders under
  `/usr` and `/var`, login shells that read the profile, and places an ordinary user cannot read.
- **`apt`, `apt-get`, `apt-cache` and `dpkg`** exist and say that nothing can be installed.
- **Closer to the real tools:** `cmp`, `diff -r`, `sed` `r` and `R`, `cp --backup`, `date -I` and `%:z`,
  `ls -s`, `--time-style` and `--full-time`, `umask`, `env` options, `awk` division by zero as in mawk.

### Web access

- **Allow-listed web access** in all three implementations: `shell.AllowWeb(new WebPolicy { ... })`,
  `shell.allow_web(WebPolicy(...))`, `shell.allowWeb({ ... })`. `curl` and `wget` reach the hosts you list
  (`name`, `*.name`, `name:port`) with the methods you list, `GET` by default.
- **Guarantees:** internal and private addresses are always refused, connections go to the address that was
  checked, every redirect is checked like the first URL, and nothing of your process's proxy settings or cookies is
  used.
- **Credentials per host,** added in your code and scrubbed from what comes back. For such a host a compressed
  body is decoded before the scrub and the agent's `Range` is dropped. `RegisterApi` has the same fix.
- **A blocked host explains itself** by default (`Blocked by the web policy: ... is not an allowed host`), so the
  agent stops retrying; `ExplainBlocked = false` makes it look like an unknown host.
- **Recordings hold the web exchanges,** and a replay serves them without touching the network.

### Files, recordings and containers

- **The `.topwater` file:** one format for a machine and its history, with JSON Schemas in `spec/topwater-file`.
- **CLI:** `topwater export`, `import` and `replay`; `snapshot info` counts a recording's calls.
- **Python:** snapshots (`snapshot`, `restore`, `fork`, `save`, `load`), recordings (`start_recording`, `shell_at`,
  `replay`) and containers (`topwater.containers`).
- **TypeScript:** recordings save as `.topwater` files and load every older format.
- **Recording viewer:** compare any two moments, continue from any turn as a branch, compare branches, and download
  a branch as its own `.topwater` file.
- **Hardening:** loaders bound the number of zip entries, checkpoints and history lines; container imports count
  histories toward the limit and report bad data as one error type; the Docker replay target drops capabilities and
  caps CPU, memory and file size; host programs never follow the agent's symbolic links out of their folder.

### Integrations

- **.NET:** Microsoft Agent Framework, and the OpenAI and Anthropic SDKs, take `shell.AsAIFunction()` as it is.
- **Python:** adapters for the OpenAI Agents SDK, Claude Agent SDK, Pydantic AI, CrewAI, smolagents and Google ADK,
  next to LangChain and LlamaIndex.
- **TypeScript:** adapters for LangChain.js and LangGraph.js, Mastra and the Claude Agent SDK, next to the Vercel AI
  SDK and OpenAI Agents.
- **Claude Agent SDK:** `bash_options` (Python) and `bashOptions` (TypeScript) build options whose only tool is the
  Topwater shell, with Claude Code's own tools and the developer's settings left out.
- Calls on one shell run one at a time, whichever adapter makes them.

### Distribution and project

- **Standalone `topwater` binaries** for Linux, macOS and Windows on every release, with `install.sh` and
  `install.ps1`.
- **Every package carries** Topwater's `LICENSE` and `THIRD-PARTY-NOTICES.md`.
- **The command spec** (`spec/`): every command, option, help text and message as JSON, from which each
  implementation's tables are generated.
- **`AGENTS.md`:** the rules for contributors.

## 0.1.0-preview.2

Released 2026-11-25. The first release of `Topwater.Containers`, `Topwater.Replay` and `Topwater.Cli`. See the
[release notes](https://github.com/gr-ant/Topwater/releases/tag/v0.1.0-preview.2).

## 0.1.0-preview.1

The first release: `Topwater`, `Topwater.Mcp` and `Topwater.AI`.
