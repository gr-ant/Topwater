# Limits and safety

- [What is simulated, and what is real](#what-is-simulated-and-what-is-real)
- [Limits](#limits)
- [How the limits behave](#how-the-limits-behave)
- [Differences in Python and TypeScript](#differences-in-python-and-typescript)

## What is simulated, and what is real

**Simulated.** Shell commands are interpreted in your process and read and write only the in-memory filesystem.
They cannot open host files, start processes or use the network. On a shell with nothing registered and no web
access allowed, nothing the agent types reaches the host.

**Real, and your responsibility:**

- **Your functions, MCP servers and registered APIs do real work.** Expose only what you would let the agent do.
  [Authenticated API calls](programs.md#authenticated-api-calls) lists what `RegisterApi` guarantees and what it does
  not.
- **Web access reaches the sites you list.** The agent can read anything there, and send what it has read to any
  allowed site with an allowed method. [Web access](web.md) lists the guarantees.
- **Mounted sources reach the stores you mount.** The agent reads what a source lists, and in staged and live mode
  your application (or, in live mode, the shell) writes its changes back. [Mounted sources](mounts.md#safety-and-limits)
  lists the guarantees.
- **Host programs run with your process's permissions.** Topwater does not sandbox them unless you ask. Read
  [Real host programs](programs.md#real-host-programs) before registering one.
- **Files you load are untrusted.** Snapshots, recordings and container imports are checked and bounded
  ([Loading untrusted files](snapshots-and-recordings.md#loading-untrusted-files)).
- **An exported container image is agent-authored content.** Run it as carefully as the agent itself
  ([Containers](containers.md#safety)).

## Limits

Nothing the agent types can hang the call, crash the host or use up its memory. Every limit is on by default, with
the same defaults in all three implementations. The ones with an option are set on `ShellOptions` (Python: keyword
arguments to `Shell(...)` in snake_case; TypeScript: the `Shell` constructor's options in camelCase, with
`maxCallDurationMs` in milliseconds):

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

## How the limits behave

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
- **Archives** are untrusted. `tar` and `unzip` never write outside the target directory: names with `..` are refused
  (tar) or cleaned (unzip), and leading `/` is dropped. tar extracts symlinks, but like GNU tar it writes nothing
  below, and hard-links nothing through, a symlink the archive itself created (`Cannot open: Not a directory`), and a
  member replaces a symlink in its way rather than writing through it. Hard links only reach entries already inside
  the target. One extraction or decompression stops with an error past 64 MB (`MaxExtractBytes`) or 100,000 entries
  (`MaxExtractEntries`). Binary data survives pipes and redirects (`tar czf - . | gzip -l`, `gzip -c f > f.gz`); in
  the tool output it shows as U+FFFD.
- **Tool output** is cut at 30,000 characters with a note saying how much was dropped. Error output is cut
  separately, so the error message and exit code are always shown.
- **Options.** `set -e` and `pipefail` reset after every tool call, so one stray `set -e` can't affect later calls.
- **Errors.** A command that throws becomes an error message and exit code, not a crash.
- **Sessions.** One shell is one session. Use a separate `Shell` per conversation; a shell is not safe to share between
  threads.

## Differences in Python and TypeScript

The limits table describes .NET. The defaults and messages are the same in Python and TypeScript, with these
differences:

- **A single regular-expression match.** .NET caps one match at 5 s. Python's `re` has no timeout: on the main thread
  a match is interrupted at the call's time limit, but in a worker thread, on Windows, or when the host uses
  `SIGALRM`, a catastrophically backtracking match runs to completion. In Node such a match switches to V8's
  linear-time engine, except for patterns with backreferences, which can overrun the time limit. Browsers have no such
  fallback. Run the shell in a separate process or worker thread if you need a hard stop. Details are in
  [python/README.md](../python/README.md#where-python-differs) and
  [typescript/README.md](../typescript/README.md#differences-from-the-c-version).
- **Nesting** is bounded by Python's recursion limit, or the JavaScript stack, instead of the .NET stack.
- **Memory** has been measured in .NET only.
- **`bc`** is .NET only, so its limits are too.
