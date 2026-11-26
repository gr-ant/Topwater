# Topwater.Containers

Move a [Topwater](https://github.com/gr-ant/Topwater) machine into a real Docker container and back. Build an image from
a snapshot to give an agent real programs (python, git, compilers) on the same files, user, working directory and
environment. Or import a container as a snapshot, so an agent can investigate a copy of a real machine in the simulation,
where it can be forked and rewound.

```bash
dotnet add package Topwater.Containers --prerelease
```

Snapshot to container:

```csharp
using Topwater;
using Topwater.Containers;

var context = await shell.Snapshot().BuildImageAsync("my-agent");
Console.WriteLine(context.Report);   // what didn't carry over
```

```bash
docker run -it my-agent
```

Container to snapshot:

```csharp
var result = await ContainerImport.FromContainerAsync("web-1", new ContainerImportOptions { Paths = ["/var/log", "/etc"] });
var shell = new Shell(new ShellOptions { User = result.Snapshot.User });
shell.Restore(result.Snapshot);
```

Files (content, mode, owner, modification time to the second), the user, the working directory, exported variables
and history carry over. Other variables, functions, aliases and options go into an image, and come back from images
Topwater made. Left behind, and listed in the `Report`: registered commands, the host name, per-process state,
Topwater's limits, groups, and for an import, real processes, volumes and (unless you name them in `Paths`) the image's
unchanged files.

These are host APIs. Nothing here registers a command, so the agent can never trigger an export, a build or an import.
Container content is untrusted: paths must be canonical, the import is bounded by `MaxFileSystemBytes`,
`MaxStringBytes` and `MaxExtractEntries`, nothing is written to disk, and the result goes through the same checks as
`ShellSnapshot.Load`.

An exported image holds what the agent wrote: its files and scripts, its `~/.bashrc` and the session script that
`~/.bashrc` sources, file modes and owners. Starting a shell in it runs that content. Treat the image as untrusted,
and run it with the same care, limits and isolation you would give the agent itself. See
[Containers](https://github.com/gr-ant/Topwater/blob/main/docs/containers.md).
