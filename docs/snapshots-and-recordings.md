# Snapshots and recordings

A snapshot is the machine at one moment. A recording is a snapshot plus every call that followed. Both are one
file, the [`.topwater` file](#the-file-format), which all three implementations read and write.

| | .NET | Python | TypeScript |
|---|---|---|---|
| [Snapshots](#snapshots) | `shell.Snapshot()`, `Restore()`, `Fork()`, `Save()`, `Shell.Load()` | `shell.snapshot()`, `restore()`, `fork()`, `save()`, `Shell.load()` | `ShellSnapshot.capture(shell)`, `save()`, `load()`, `restore()`; no fork |
| [Deterministic runs](#deterministic-runs) | `StartTime`, `RandomSeed` | not yet | `startTime`, `randomSeed` |
| [Recordings](#recordings) | `shell.StartRecording()`, `Recording.Load()`, `ReplayAsync()` | `shell.start_recording()`, `Recording.load()`, `replay()` | `startRecording(shell)`, `Recording.load()`, `replay()` |
| Recordings as tests, and on real bash | `Replay.AssertMatches()`, `DockerTarget` (package `Topwater.Replay`) | not yet | not yet |
| [Containers](containers.md) | package `Topwater.Containers` | `topwater.containers` | not yet |

The samples below are C#. [python/README.md](../python/README.md) and [typescript/README.md](../typescript/README.md)
show the same calls in their own names.

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
- **Mounted sources' files.** A [mount](mounts.md#snapshots-and-recordings) is saved as its definition and pending
  changes; a snapshot loaded from a file can't change a mount's mode, and its changes wait for your
  `AcceptSnapshotChangesAsync`.
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

`Save` writes a [`.topwater` file](#the-file-format) without history, and `Load` treats what it reads as
[untrusted](#loading-untrusted-files).

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
Console.WriteLine(report);                   // "replay on topwater 0.1.0-preview.3: 12/12 calls match"
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

A shell with [web access](web.md#recordings) records every request and its response with the call. A shell built
from the recording serves those responses in order and never touches the network.

### The machine at any turn

`await recording.ShellAtAsync(i)` gives a shell as it was right after call `i` (-1: before the first). It restores the
nearest snapshot at or before `i` and runs the calls after it again, which is exact for a deterministic recording. A
viewer that steps through a run turn by turn can use this, and `SnapshotEvery` bounds how much it re-runs.

### Recordings as regression tests

The `Topwater.Replay` package (`dotnet add package Topwater.Replay --prerelease`) turns a recording file into a test.
It works with any test framework: a mismatch throws `ReplayMismatchException` carrying the report.

```csharp
using Topwater.Replay;

[Fact]
public Task Todo_session_still_behaves() =>
    Replay.AssertMatches("recordings/todo.topwater", configure: s => s.Register(new OrderTools()));
```

A relative path is relative to the test's source file, so recordings sit next to the tests without being copied to the
output folder. A deterministic recording is compared exactly; others with `ReplayNormalizers.Volatile`. When a change
is intended, run the tests with `TOPWATER_RERECORD=1`: `AssertMatches` then rewrites each file with what this Topwater
returns instead of comparing (header, metadata and start kept; snapshots retaken where there were some), as a
`.topwater` file even if it was an older `.jsonl` one. Review the diff and commit it.
`recording.RerecordAsync(target)` does the same in code.

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

The container has no network unless `Network = true`, 1 GB of memory and no swap, one CPU, 512 processes, files of at
most 1 GB, no capabilities but `CHOWN`, `DAC_OVERRIDE` and `FOWNER` (which the root setup step needs to write files
with their owners), and `no-new-privileges`, so a setuid program gains nothing. The 1 GB limit is per file: nothing
caps the container's total disk use, so a recording can still fill the docker host's disk. Each docker command's
stdout and stderr are read up to `MaxOutputBytes` each (64 MiB by default, twice what one Topwater call can print):
past it the command is killed and the call throws `InvalidOperationException`, so replaying a recording of `yes` ends
with that error instead of filling the host's memory. Commands run by number, uid
and gid 1000 (the snapshot's `/etc/passwd` cannot turn that into root); only a snapshot whose user is `root` runs as
root, and then with those three capabilities. The snapshot's symbolic links go in as links, never followed. It gets
the snapshot's host name and user (uid 1000, in place of the image's `ubuntu`), the files a fresh Topwater machine
doesn't have or has differently (with modes, owners and times; not `/proc`, `/dev` or the program stubs, which the
image provides for real), the variables and the working directory. Each call runs as `bash -c` under that user; the
working directory, variables and functions are saved after each call and restored before the next, so state carries
over as in Topwater. Aliases and `set`/`shopt` options don't carry over. `DockerTarget.IsAvailableAsync()` tells tests
whether to skip. User, owner and variable names go into its root setup script quoted, and names no account or variable
can have are left out.

`tools/ReplayGaps` uses this to measure Topwater against Linux on many sessions at once: it converts the example
harness's saved transcripts (`runs/*.json`) and public agent trajectories into recordings, replays each on Topwater
and in a container, and ranks the differences (missing commands, unsupported options, wrong exit codes, wrong output).
The example harness itself now saves a deterministic recording of every run next to its transcript.

### The recording viewer

The [replay viewer](https://gr-ant.github.io/Topwater/replay.html) is a static page built from the TypeScript
implementation (`replay.html` in the [browser build](../typescript/README.md#in-the-browser)). It runs entirely in the
browser: the file you open is not uploaded. Open a `.topwater` file or an older `.jsonl` recording and it:

- steps through the recording turn by turn, with each turn's command and output;
- shows the files as they were after each turn, and a diff of what the turn changed;
- compares any two moments;
- opens a terminal on the machine as of any turn. Commands typed there form a branch of the timeline, which can be
  compared with the original or another branch and downloaded as its own `.topwater` file.

A downloaded branch holds the recording's calls up to the fork, then the branch's, with metadata `forkedFrom` and
`forkedAfterCall`, and checkpoints only at the fork and the end. A reader re-runs the branch's calls in between.

### Across implementations

All three implementations read and write the same `.topwater` files. TypeScript has the same virtual clock and
random generator as .NET, so a deterministic recording made in one re-runs in the other with the same times and
random numbers. Python has no virtual clock, so a replay of its recordings needs a normalizer for anything that
prints the time. Where the implementations' commands differ, so does the output.

## The file format

Snapshots and recordings are one format, the `.topwater` file: a zip. A snapshot is a `.topwater` file without
history. Zip is self-describing, every platform can open it (`unzip -l machine.topwater`), and the contents of
different files can be read one at a time. The JSON Schemas are in
[`spec/topwater-file`](../spec/topwater-file/README.md).

| Entry | |
|---|---|
| `manifest.json` | the machine: [the manifest](#the-manifest). With history, the machine after the last call |
| `blobs/<sha256>` | file contents, each stored once for the whole file, shared by the manifest and every checkpoint |
| `history.jsonl` | with history only: a header line, then one line per call |
| `checkpoints/<n>.json` | with history only: the machine after `n` calls, the manifest's shape. `0` is the start and always there |

Because the machine is always `manifest.json`, a recording opens anywhere a snapshot does (`Shell.Load`, `--from`,
`snapshot info`), also in a Topwater that predates history. The CLI refuses a recording as `--state`, which it would
rewrite as the machine alone: start from it with `--from`, or copy its machine to a new state file with
`topwater import`.

### The manifest

The machine itself is two kinds of entry:

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

Version 2 added `symlink` entries and `link`. A machine without any link is written as version 1, but a default
machine has four (`/etc/os-release` among them), so in practice every machine saved by this version is version 2 and
`0.1.0-preview.2` cannot load it. Version 3 added `mounts` ([mounted sources](mounts.md#snapshots-and-recordings)):
only a machine with a mount is written as version 3. A newer `version` than this Topwater knows fails with
`NotSupportedException`: `snapshot format version 4 is newer than this version of Topwater supports (3); upgrade
Topwater to load it`. Later versions of Topwater are meant to keep reading versions 1 to 3.

### The history

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
never compared. `index` counts from 0. The checkpoint after call `index` is `checkpoints/<index + 1>.json`, if there is
one.

Version rules: readers ignore fields they don't know, so later versions may add optional fields without changing
`version`. A change that older readers would misread raises `version`; loading a newer version fails with
`NotSupportedException`, and later Topwater versions keep reading version 1. Loading treats the file as untrusted:
every manifest and checkpoint loads under the default limits or the `ShellOptions` passed to `Load` (never limits the
file names) with every check described in [Loading untrusted files](#loading-untrusted-files). The replay shell's
limits are the file's, clamped to the same bound: a recording can lower limits, never raise them. At most 1,000
checkpoints load; together they may hold at most 4 x `MaxFileSystemBytes` of content and 4 x `MaxExtractEntries` files
and directories. `history.jsonl` is read line by line as it inflates, up to 2 x `MaxStringBytes` bytes and
`MaxExtractEntries` calls. Checkpoint names must be `0` or a number without leading zeros no greater than the number
of calls, and no two zip entries may share a name.

Older files still load: snapshot zips are `.topwater` files already, and the JSON Lines recordings that came before
(header `"format":"topwater-recording"`, snapshots embedded as base64 zips or in side-car files) load with the same
checks. Such a file may be at most `MaxFileSystemBytes` bytes (128 MB at the defaults): a larger one is refused from
its length before it is read, and a stream that can't seek is read only up to that size. A side-car name must be plain segments of letters, digits, `.`, `_` and `-` separated by `/`, none made only of
dots; a custom `openSnapshot` must treat it as nothing else. `Save` always writes `.topwater`, so loading and saving
converts an old recording.

## Loading untrusted files

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
  must come before its children, and a path may appear only once. Only directories, regular files and symlinks exist:
  devices and other types are refused. A parent is found without following symlinks, so no entry lands anywhere but
  its own path. A symlink's `target` is 1-4096 characters without NUL and may point anywhere, even at itself: it is
  only resolved later, with the agent's permissions, and 40 links in one lookup fail as on Linux. A `link` must name
  an earlier file or symlink, never a directory. Modes must be octal up to `7777`. Owners, the user and the host name
  must be plain names, so `"user": "../../etc"` can't steer the home directory.
- Variable names must be valid identifiers, and indexed arrays must have non-negative integer indices. Each function
  body is parsed again as `name() body` and must come back as exactly that one function, so a body can't smuggle in
  extra commands. Exit codes must be 0-255, and `set` options must be ones the shell has.

