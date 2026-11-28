# Containers

The `Topwater.Containers` package moves a machine between Topwater and a real Docker container, in both directions.
Python has the same thing as `topwater.containers` ([names](../python/README.md#where-python-differs)); TypeScript
does not have it yet. The samples are C#.

- [Snapshot to container](#snapshot-to-container)
- [Container to snapshot](#container-to-snapshot)
- [What the export does](#what-the-export-does)
- [What the import does](#what-the-import-does)
- [What carries over](#what-carries-over)
- [Safety](#safety)

```bash
dotnet add package Topwater.Containers --prerelease
```

Two uses:

- **Graduate an agent from the simulation to real programs.** Python, git and compilers run in the container, on the
  same files, as the same user, in the same working directory and with the same environment.
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

Container content is untrusted, like [a loaded file](snapshots-and-recordings.md#loading-untrusted-files):

- Every entry name must be canonical (an optional leading `./`, no `..`, `.` or empty segments, no NUL), or the import
  fails with `InvalidDataException`.
- The selected files together may hold at most `MaxFileSystemBytes` and number at most `MaxExtractEntries` (link
  copies count). `/etc/passwd`, the session script and every `/home/*/.bash_history`, read even when not selected,
  count toward `MaxFileSystemBytes` too.
- Anything else malformed (a time or owner no snapshot can hold, a broken header) fails with `InvalidDataException`
  (Python: `InvalidContainerData`).
- A PAX extended header, GNU long name or GNU long link may hold at most 1 MiB (`ContainerImport.MaxTarHeaderBytes`).
  .NET's tar reader holds such a header whole in memory (a 256 MB one took 0.5-1.3 GB), so a larger one is refused
  from the size in its header block, before it is read. The headers are followed block by block, a PAX `size`
  included, exactly as the tar reader finds them.
- Each entry is read only up to the size its header declares. A session script, `/etc/passwd` or `.bash_history`
  larger than `MaxStringBytes` is not read.
- The result goes through `ShellSnapshot.Load` with your limits, so all its checks apply: names, modes, variable names,
  and function bodies parsed again.
- Nothing is written to the real disk. A `session.sh` from an image runs only inside the simulated shell, like any
  agent command, never on the host.

Building an image runs the generated `setup.sh` as root inside the image build. That is docker's sandbox, not your host.
Account and group names from the snapshot reach `setup.sh` only as quoted words; a name starting with `-` (which
`useradd` would read as an option) is not created and is reported, and its files go in as root's.

**An exported image is agent-authored content.** It holds what the agent wrote: its files and scripts, its
`~/.bashrc` and the session script that `~/.bashrc` sources, file modes and owners. Starting a shell in it runs that
content. Treat the image as untrusted, and run it with the same care, limits and isolation you would give the agent
itself. For example `docker run -it --network none --cap-drop ALL --security-opt no-new-privileges --memory 1g
--pids-limit 512 my-agent`.
