# Mounted sources

A mount shows an outside store of files (a folder on the host, an Obsidian vault, and later Notion, SharePoint or
anything else with pages and folders) as a directory in the machine. The agent uses ordinary commands on it: `ls`,
`cat`, `grep -r`, `find`, `cp`, `mv`, `rm`, `to-markdown`. Your application decides what the agent's changes do: nothing
(read-only), a change set you review and apply (staged), or writes straight through (live).

- [Mounting a source](#mounting-a-source)
- [Modes](#modes)
- [Change sets](#change-sets)
- [Writing a source](#writing-a-source)
- [How it works](#how-it-works)
- [Safety and limits](#safety-and-limits)
- [Snapshots and recordings](#snapshots-and-recordings)

| | .NET | Python | TypeScript |
|---|---|---|---|
| Mount | `await shell.MountAsync(path, source, MountMode.Staged)` | `shell.mount(path, source, "staged")` (`mount_async`) | `await shell.mount(path, source, 'staged')` |
| Change set | `shell.PendingChanges(path)` | `shell.pending_changes(path)` | `shell.pendingChanges(path)` |
| Apply | `await shell.ApplyChangesAsync(path, changes?)` | `shell.apply_changes(path, changes=None)` (`apply_changes_async`) | `await shell.applyChanges(path, changes?)` |
| Discard | `shell.DiscardChanges(path)` | `shell.discard_changes(path)` | `shell.discardChanges(path)` |
| Refresh | `await shell.RefreshMountAsync(path)` | `shell.refresh_mount(path)` (`refresh_mount_async`) | `await shell.refreshMount(path)` |
| Unmount, list | `shell.Unmount(path)`, `shell.Mounts` | `shell.unmount(path)`, `shell.mounts` | `shell.unmount(path)`, `shell.mounts` |
| Sources | `IMountSource`, `LocalFolderSource`, `MemorySource` | `MountSource`, `LocalFolderSource`, `MemorySource` | `MountSource`, `LocalFolderSource`, `MemorySource` |

## Mounting a source

```csharp
var shell = new Shell();
await shell.MountAsync("/mnt/vault", new LocalFolderSource("/home/me/Obsidian/Work", id: "work-vault"));   // read-only
await shell.MountAsync("/mnt/notes", new LocalFolderSource("/srv/notes", writable: true, id: "notes"), MountMode.Staged);

await shell.RunAsync("grep -rl 'quarterly plan' /mnt/vault; sed -i 's/Draft/Final/' /mnt/notes/plan.md");

foreach (var change in shell.PendingChanges("/mnt/notes"))
    Console.WriteLine($"{change.Kind} {change.Path}");          // Write /plan.md
var result = await shell.ApplyChangesAsync("/mnt/notes");       // or a subset: ApplyChangesAsync(path, chosen)
if (result.Failed is not null) Console.WriteLine($"stopped at {result.Failed.Path}: {result.Error}");
```

The mount point must be an absolute path that is missing or an empty directory, outside any other mount. Its parents
are created. `MountAsync` lists the source before it returns, so a bad credential or a missing folder fails there,
in your code, not in front of the agent.

The agent sees one command of its own, `mount`:

```
$ mount
local-folder:work-vault on /mnt/vault type local-folder (ro)
local-folder:notes on /mnt/notes type local-folder (rw,staged)
$ mount --changes /mnt/notes
write /mnt/notes/plan.md
$ mount -r /mnt/vault        # list the source again
```

## Modes

| Mode | What the agent's changes do |
|---|---|
| `read-only` (default) | Nothing: the files and directories belong to root and can't be written (`Permission denied`). |
| `staged` | They stay in the machine. The agent sees its own edits; you get them as a change set to apply, apply in part, or discard. |
| `live` | After every command, its changes are applied to the source. A change that fails or conflicts stays pending, and the command's error output says so. |

In staged and live mode the files belong to the agent's user.

## Change sets

`PendingChanges(path)` compares the mount's files with what was read from the source and returns the operations
that would make the source match, in an order that can be applied one after another:

| Kind | Fields | Meaning |
|---|---|---|
| `delete` | `path`, `version` | remove a file or empty directory (a name about to be replaced comes first) |
| `rename` | `path`, `to`, `version` | move a file or directory to a name that doesn't exist |
| `mkdir` | `path` | create a directory |
| `write` | `path`, `version`, `content` | create a file (`version` null) or replace one |
| `delete` | `path`, `version` | remove a file or directory that is gone (children before their parent) |

Paths are the source's own (`/plan.md`, with `/` the mount point). `version` is what the source said the entry was
when it was read; null means "must not exist yet". A removed directory is one `delete` per entry, children first, so
every file is checked on its own.

`ApplyChangesAsync(path, changes)` applies all of them, or the ones you pass (from the same list), in order. Before
each it asks the source for the entry's current version (`stat`); if it differs from `version`, or a rename's target
already exists, it stops with a conflict. A source error also stops it. The result has `Applied` (what went through),
`Failed` (the change it stopped at, or null), `Error` and `Conflict`. What was applied becomes the mount's new
starting point; the rest stays pending. `DiscardChanges` drops every pending change and shows the source's files
again (re-read lazily).

Symbolic links, permission changes and a second hard link inside a mount are not part of a change set.

## Writing a source

A source is a small class you write around the outside store. Credentials stay inside it: the agent never sees the
object, only the files it lists. Every method may be asynchronous (return a `Task`, a coroutine or a `Promise`).

| Member | Required | |
|---|---|---|
| `type`, `id` | yes | `type` names the kind of store (`local-folder`, `notion`); `id` is your name for this one (a workspace, a site). Both are saved in snapshots, so they must not hold secrets. |
| `capabilities` | yes | which of `write`, `delete`, `rename`, `mkdir` it supports; empty for a read-only source. Staged and live mounts need `write`. |
| `list(path)` | yes | the entries of a directory: `name`, `isDir`, `size`, `modified`, `version` |
| `stat(path)` | yes | one entry, or null when missing |
| `read(path, maxBytes)` | yes | a file's bytes; may refuse past `maxBytes` |
| `write(path, data, expectedVersion)` | with `write` | create (`expectedVersion` null: must not exist) or replace; returns the new version |
| `delete(path, expectedVersion)` | with `delete` | |
| `rename(path, to, expectedVersion)` | with `rename` | |
| `mkdir(path)` | with `mkdir` | |

`version` is opaque: an ETag, a revision number, a modification time. Raise `MountConflictException` /
`MountConflictError` when the expected version no longer matches; any other error is reported as a failure.

```python
class WikiSource(MountSource):
    type, capabilities = "wiki", frozenset({"write"})

    def __init__(self, client, space):
        self.id, self._client, self._space = space, client, space   # the client holds the token

    async def list(self, path):
        return [MountEntry(p.slug + ".md", False, p.size, p.edited, p.etag) for p in await self._client.pages(self._space, path)]
    ...
```

Two sources come with Topwater:

- **`LocalFolderSource(root, writable=False, id=None)`** shows a host directory. Read-only unless `writable`. Symbolic
  links in the folder are skipped, never followed, and nothing outside `root` is reached. It works for Obsidian vaults
  and any other folder of notes. The version is the file's modification time and size.
- **`MemorySource`** keeps files in memory: for tests, and as an example. `set(path, content)` and `remove(path)` change
  it from the outside (each change gets a new version), and `hook(op, path)`, called before every operation, can sleep
  or throw to simulate latency, failures and conflicts.

## How it works

The machine's filesystem is synchronous and the shell is not, so a mount is filled at the points where the shell can
wait for the source:

- **Listing** happens when you mount, on `mount -r` or `RefreshMountAsync`, and at the start of the first call after a
  restore. The whole tree is listed then (bounded, see below), so `ls`, globbing, `find` and tab-free paths work
  without waiting.
- **Content** is fetched lazily. Before each command the shell fetches the files the command names (every argument
  that is a path in a mount, a directory meaning everything under it; with no such argument, the working directory
  when it is inside a mount) and the files it redirects from or appends to. Commands that never read content (`ls`,
  `find`, `stat`, `mv`, `rm`, ...) fetch nothing. A file read that wasn't foreseen (a script reading a path from a
  variable, say) fails with `not fetched from the mounted source yet (run the command again)` and the file is fetched
  right after the command, so running it again works.
- Fetched content stays in the machine as long as the disk cap (`MaxFileSystemBytes`) has room; when it doesn't, the
  oldest unchanged fetched files go back to being placeholders.
- **Your changes** are the files themselves: the mount remembers which file it read with which version (and a hash
  of its content), and a change set is the difference.

## Safety and limits

Everything a source returns is treated as untrusted:

- Names that are empty, `.` or `..`, contain `/` or NUL, or are longer than 255 bytes are skipped, as are duplicates.
- A listing stops at `MaxExtractEntries` entries (100,000 by default) for the whole mount, and 32 directories deep.
  `mount` then shows a warning.
- A file larger than the free disk space is not fetched (`File too large`). A source returning more bytes than it
  was allowed is refused.
- During a call, every source call is bounded by the call's time limit; a slow source fails that command, not the
  session. Your own calls (`MountAsync`, `ApplyChangesAsync`) are not time-limited.
- Paths given to a source are always canonical (`/a/b.md`), built from names that passed the checks above, so the
  agent cannot make a source reach outside its root with `..` or a symbolic link.

## Snapshots and recordings

A snapshot saves each mount's definition (path, mode, source `type` and `id`) and its pending changes with their
content, never the source's files. Loading or restoring it brings the mount back with the source of the same `type`
and `id` the shell already has (from an earlier `MountAsync`, or a fork's parent). Without one, the mount point is an
empty directory the agent can't open, `mount` shows it as `(unavailable: no source for type:id)`, and its changes are
kept: call `MountAsync` with a matching source at the same path and they come back, checked against the versions they
were made on.

A recording saves what the sources answered (listings, file contents, results of writes) with each call, and what the
mounts held when it started. A shell built from the recording answers from those and never calls a source; a question
that wasn't recorded fails with `<op> <path> is not in the Topwater recording`.
