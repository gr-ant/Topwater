# Python (opt-in)

`python3` runs real CPython 3.14, compiled to WebAssembly, inside the machine. It reads and writes the machine's files
and nothing else. It is off unless your application turns it on, and its interpreter (about 8 MB) comes in a separate
add-on package, so the core packages stay small and dependency-free.

- [Enabling it](#enabling-it)
- [What works](#what-works)
- [Differences from python3 on Ubuntu](#differences-from-python3-on-ubuntu)
- [Limits](#limits)
- [Security model](#security-model)
- [Mounts, web access, snapshots and recordings](#mounts-web-access-snapshots-and-recordings)
- [Porting](#porting)
- [Credits](#credits)

## Enabling it

| | .NET | Python | TypeScript |
|---|---|---|---|
| Install | `dotnet add package Topwater.Python` | `pip install 'topwater[python]'` | `npm install topwater-python` |
| Enable | `shell.AllowPython(new PythonOptions { ... })` | `shell.allow_python(PythonOptions(...))` | `await shell.allowPython({ ... })` |
| Status | available | available | available |

```python
from topwater import Shell
from topwater.commands.python import PythonOptions

shell = Shell().allow_python()                       # or allow_python(PythonOptions(max_memory=128 << 20))
shell.run("echo 'name,qty\nbolt,3\nnut,5' > /tmp/parts.csv")
print(shell.run("""python3 -c '
import csv
rows = list(csv.DictReader(open("/tmp/parts.csv")))
print(sum(int(r["qty"]) for r in rows))'""").stdout)     # 8
```

```csharp
using Topwater;
using Topwater.Python;

var shell = new Shell().AllowPython();               // or AllowPython(new PythonOptions { MaxMemory = 128L << 20 })
```

`topwater[python]` installs `topwater-python`, which holds the interpreter (`python.wasm.xz`, `python314.zip`) and
depends on [`wasmtime`](https://pypi.org/project/wasmtime/). The core `topwater` package imports neither until
`allow_python` is called. The .NET add-on is the `Topwater.Python` package: it carries the interpreter inside its
assembly and depends on the [`Wasmtime`](https://www.nuget.org/packages/Wasmtime) NuGet package; the core `Topwater`
package references neither.

```ts
import { Shell } from 'topwater';

const shell = await new Shell().allowPython();          // or allowPython({ maxMemory: 128 << 20 })
console.log((await shell.run("python3 -c 'print(sum(range(10)))'")).stdout);   // 45
```

The npm add-on, `topwater-python`, has no dependencies: Node and browsers run WebAssembly themselves. `allowPython`
registers the command at once and returns a promise that resolves when the interpreter is loaded and compiled (under a
second) or rejects with `PythonUnavailable`. Node needs version 22.3 or later. A browser page must be cross-origin
isolated, since the program runs in a Web Worker that shares memory with the page's thread: serve it with
`Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Embedder-Policy: require-corp` (or `credentialless`).
Without them `allowPython` rejects with that explanation and `python3` prints it; nothing ever runs on the page's own
thread, where a busy loop could not be stopped. A Content-Security-Policy must allow `worker-src blob:`. In a browser,
give `artifacts` the URL of a folder you serve the two files from (same origin, or with CORS and
`Cross-Origin-Resource-Policy`).

.NET and JavaScript have no xz decoder, so their add-ons carry the same module recompressed with gzip
(`python.wasm.gz`, written reproducibly from `python.wasm.xz` by `tools/python_wasm_gz.py`) and check the SHA-256 of
the decompressed module.

`allow_python` compiles the interpreter the first time a process uses it (2 to 3 seconds) and keeps it for every
shell after that; each call then costs a few milliseconds. It raises `PythonUnavailable` (.NET:
`PythonUnavailableException`) in your code when the add-on is missing or its files do not match the hashes Topwater
expects, rather than failing in front of the agent.

| `PythonOptions` | Default | |
|---|---|---|
| `max_memory` (`MaxMemory`) | 256 MB | WebAssembly memory for one call. The interpreter starts at about 40 MB; past the limit a program gets `MemoryError` |
| `artifacts` (`Artifacts`) | the add-on's | A folder holding `python.wasm.xz` and `python314.zip` (the repository's `assets/python-wasm`); for .NET, `python.wasm.gz` and `python314.zip` |
| `cache_dir` (`CacheDir`) | none | A folder of yours where the compiled interpreter is kept between processes, so only the first process compiles it. It holds machine code: give it the same trust as your own program files |

TypeScript takes `maxMemory` and `artifacts` (a path or URL of a folder holding `python.wasm.gz` and `python314.zip`);
it compiles once per process and memory limit, and has no `cache_dir`.

Until `allow_python` is called, `python3` is `command not found`, as on a fresh Ubuntu image. There is no `python`
alias (Ubuntu has none without the `python-is-python3` package), no `pip` and no interactive prompt.

## What works

- `python3 -c CODE`, `python3 FILE`, `python3 -m MODULE`, a program on stdin (`python3`, `python3 -`), `sys.argv`,
  exit statuses (`sys.exit`, an uncaught exception is 1 with the traceback on stderr), `#!/usr/bin/env python3` scripts,
  pipelines, redirections and `$(...)`.
- The standard library that makes sense without processes or sockets: `json`, `csv`, `re`, `sqlite3`, `zlib`, `gzip`,
  `zipfile`, `hashlib`, `decimal`, `datetime`, `pathlib`, `shutil`, `tempfile`, `xml.etree`, `tomllib`, `asyncio`,
  `dataclasses`, `argparse`, `statistics` and the rest of the pure-Python modules.
- Files: `open`, `os`, `pathlib` and `shutil` work on the machine's files as the agent's user, with its permissions,
  umask and disk cap. A file the program leaves open is saved when it ends, as a real process's is.
- HTTP through `urllib.request`, `requests` and `httpx` (bashkit's compatible subsets, built in), only to the sites your
  [web policy](web.md) allows.
- The environment holds the exported variables and `PWD`; `os.getcwd()` is the shell's directory.

Not there: `pip` and third-party packages, processes (`subprocess` raises `OSError`, `os.system` does not exist), threads (`RuntimeError`),
sockets (`OSError`), `ctypes`, `multiprocessing`, `tkinter`, `curses`, `bz2` and `lzma`. Nothing is kept between calls:
each call starts from the same freshly started interpreter.

## Differences from python3 on Ubuntu

Ubuntu 24.04 has Python 3.12; this is 3.14. The [corpus](../tests/corpus/README.md) area `python` records only
behaviour the two share. Beyond the version:

| | Ubuntu | Topwater |
|---|---|---|
| `python3 --version` | `Python 3.12.3` | `Python 3.14.8` |
| `--help`, `-h` | CPython's help | a short help of our own (`spec/commands/python3.json`) |
| Error numbers | Linux's (`[Errno 2] No such file or directory`) | WASI's (`[Errno 44] No such file or directory`); the names (`FileNotFoundError`) and texts match |
| `isatty()` | depends on the terminal | always false: stdio is a pipe, as in a tool call |
| Output | streamed | collected and written when the program ends (pipelines in the machine run one stage after another anyway) |
| Reading a mounted file the command did not name | — | fetched from the source while the program waits ([mounts](#mounts-web-access-snapshots-and-recordings)) |
| `os.getuid()` and other process calls | the real ids | absent, as in any WASI build of Python |
| `random`, `time` | — | real randomness and the real clock, like `date` and `$RANDOM` in Topwater's Python port, which has no virtual clock. In .NET the wall clock is the shell's, so a shell with `StartTime` shows its virtual time; randomness stays real |

## Limits

| Limit | Default | What happens |
|---|---|---|
| Time | the call's limit (`max_call_duration`, 120 s), or `timeout N` | the program is stopped: exit 124 under `timeout`, or the call ends with the usual time-limit note. A busy loop is interrupted by WebAssembly epochs (every 10 ms; TypeScript terminates the program's worker), a `time.sleep` wakes at the deadline |
| Memory | 256 MB (`max_memory`) | `MemoryError` inside Python; the next call starts fresh |
| Recursion | Python's own limit (1,000) | `RecursionError`. C-level recursion past the 2 MB WebAssembly stack (TypeScript: the worker's 32 MB stack in Node, the browser's own in a page) ends the program with `python3: fatal error: the interpreter ran out of stack` (exit 1) |
| Files | the disk cap (`max_file_system_bytes`, 128 MB) | `OSError: [Errno 51] No space left on device` at the write that would pass it |
| Open-file buffers | `max_memory` in all | `No space left on device` |
| Output | `max_string_bytes` (32 MB), stdout and stderr together | the call ends with the usual "output too large" note |
| Open files | 1,024 descriptors | `OSError: [Errno 33]` |
| HTTP | the web policy's request, size and time limits | `urllib.error.URLError` / `requests.ConnectionError` with the policy's reason |

## Security model

Isolation comes from the WebAssembly boundary, not from Python. The program can only call the 44 WASI preview1
functions and two HTTP functions that Topwater implements itself; it has no way to name a host file, socket, process
or thread.

- **Files** are the machine's virtual files, reached through the same filesystem calls every command makes, as the
  agent's user: read-only mounts and root-owned files refuse writes with `PermissionError`, staged mounts record the
  writes as pending changes, and `..` stops at `/`, which is the whole machine. Topwater never uses wasmtime's own
  WASI, which maps real directories.
- **The standard library** is served from memory at `/usr/local/lib/python314.zip`, read-only. Commands don't see it.
- **The network** is Topwater's [web access](web.md): every request (and every redirect, which the program follows
  itself) is checked against your policy, counts toward its limits, gets its credentials and is recorded. Without a
  policy every host fails to resolve, as for `curl`. The program never opens a socket or sees TLS.
- **The interpreter** is checked against pinned SHA-256 hashes every time it is loaded.
- **Hostile programs** (a busy loop, a huge allocation, deep recursion, a file that fills the disk, symbolic link
  loops, `..` paths, closing or exhausting descriptors) fail inside Python or end the program; the tests in
  `python/tests/test_python.py`, `tests/Topwater.Tests/PythonTests.cs` and `typescript/test/python.test.ts` cover
  each.

## Mounts, web access, snapshots and recordings

A mounted file the command names (`python3 /mnt/notes/report.py`) is fetched before the program starts, like for any
command. One the program finds by itself (`open("/mnt/notes/plan.md")`, `os.listdir` of a folder not listed yet) is
fetched while the program waits, within the call's time; other commands say "run the command again" instead.

`python3` keeps nothing between calls, so snapshots and forks need nothing new: a fork keeps the command. A recording
holds the program's HTTP exchanges with the others, and replays them; to replay calls that ran `python3`, enable it in
the recording's `configure` callback (`recording.shell_at(n, configure=lambda s: s.allow_python())`; TypeScript
`recording.shellAt(n, s => void s.allowPython())`, after an `allowPython` with the same options has resolved once).

## Porting

The Python port is the reference (`python/src/topwater/wasi.py`, `python/src/topwater/commands/python.py`). The C# port
follows it in the add-on (`src/Topwater.Python/Wasi.cs` and `PythonCommand.cs`; the core keeps only the registrar,
`Builtins/PythonCommand.cs`, so it never references Wasmtime), and TypeScript in `typescript/src/wasi.ts` and
`typescript/src/commands/python.ts`, mirrored in `tools/layout.json`. The contract:

- **Guest contract** (bashkit's): instantiate the module with only `wasi_snapshot_preview1` and `bashkit` imports,
  call `_initialize` if exported, then `bashkit_run() -> i32` (the exit status, unless `proc_exit` ends it first). argv
  is `python3` and the arguments; the environment is the exported variables plus `PWD`. Exactly one preopen, `/`, at
  fd 3: the snapshot cached it. One fresh instance per call.
- **WASI** over the virtual filesystem: every import listed in `wasi.py` (`_SIGNATURES`), with the errno values,
  structure layouts and behaviour there; sockets answer `ENOTSUP`. Files open as whole buffers and are written back on
  close, `fd_sync` and exit. Filesystem errors map to WASI errnos by their text (`errno_of`); a mounted file not
  fetched yet is fetched and the call retried once.
- **HTTP bridge**: `bashkit.http_request(ptr, len, timeout_ms, out_len_ptr)` and `bashkit.http_take(ptr, len)`;
  the record layout is in `wasi.py` (http bridge section). Requests go through the port's web session, one hop each.
- **Limits**: epoch interruption (10 ms ticks) for time, the store's memory limit, a 2 MB WebAssembly stack, the
  shell's output and disk limits. .NET checks the deadline and the call's cancellation (`timeout N`) at every tick
  through the store's epoch callback, since `timeout` ends a call by cancelling it.
- **TypeScript** can't interrupt WebAssembly on the thread running it, so each call's guest runs in a fresh Worker
  (Node `worker_threads`, a Web Worker in browsers; plain JavaScript started from a string, so there is no file to
  locate). Before compiling, `wasi.ts` rewrites the module's memory to be shared and to have `maxMemory` as its maximum
  (JavaScript has no store limiter; a failed `memory.grow` is `MemoryError`). Every WASI import in the worker posts its
  arguments to the shell's thread and waits with `Atomics.wait`; the shell's thread runs the same functions as
  `wasi.py` on the shared memory, awaiting mount sources and the web session when it needs to, then wakes the worker.
  Open files stay on the shell's thread, so they are saved even when the program is stopped. The shell's thread never
  waits on the guest: at the deadline it terminates the worker.

## Credits

The interpreter is [bashkit](https://github.com/everruns/bashkit)'s CPython build (MIT), copied unchanged into
`assets/python-wasm` (`PROVENANCE` names the commit; `guest/build.sh` there rebuilds it from pinned sources). Topwater's
WASI host translates bashkit's (`crates/bashkit/src/builtins/cpython`) onto its own filesystem. CPython is under the
PSF License; the interpreter also contains zlib, SQLite, libmpdec, Expat, HACL* and wasi-libc. Their notices are in
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
