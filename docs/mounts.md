# Mounted sources

A mount shows an outside store of files (a folder on the host, an Obsidian vault, or anything else with pages and
folders that you write a source for) as a directory in the machine. The agent uses ordinary commands on it: `ls`,
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
| A loaded snapshot's changes | `SnapshotChanges(path)`, `await AcceptSnapshotChangesAsync(path)` | `snapshot_changes(path)`, `accept_snapshot_changes(path)` | `snapshotChanges(path)`, `await acceptSnapshotChanges(path)` |
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
in your code, not in front of the agent. The computed tool description (`BashTool.DescriptionFor(shell)`,
`tool_description(shell)`, `toolDescription(shell)`) adds one sentence per mount, so the model knows the folder is
there and what its changes do.

The agent sees one command of its own, `mount`:

```
$ mount
local-folder:work-vault on /mnt/vault type local-folder (ro)
wiki:team on /mnt/wiki type wiki (rw,staged,no-delete,no-rename)
$ mount --changes /mnt/wiki
write /mnt/wiki/plan.md
$ mount --discard /mnt/wiki   # drop my changes there
$ mount -r /mnt/vault         # list the source again
```

The options after `rw` name what the source can't do: `rm` and `mv` there work in the machine but can't be applied.
Warnings (a listing cut short, names skipped) and changes waiting from a saved session are printed under the line.

## Modes

The mode is always the one you pass to `MountAsync`; a snapshot can't change it.

| Mode | What the agent's changes do |
|---|---|
| `read-only` (default) | Nothing: the files and directories belong to root and can't be written (`Permission denied`). |
| `staged` | They stay in the machine. The agent sees its own edits; you get them as a change set to apply, apply in part, or discard. |
| `live` | They are applied to the source after every command (once its redirections are written) and again when the call ends. A change that fails or conflicts stays pending, the command's error output says so, and it is not tried again until the agent changes something else. `mount --discard DIR` drops it. |

In staged and live mode the files belong to the agent's user.

## Change sets

`PendingChanges(path)` compares the mount's files with what was read from the source and returns the operations
that would make the source match, in an order that can be applied one after another:

1. a `delete` for each name about to be replaced by something else;
2. `rename` and `mkdir` in the order the new tree has them, parents first, so a folder exists before files move into it
   and a rename inside a renamed folder names the folder's new path. When a rename's target is still taken by an entry
   that moves later (a swap), that entry first goes to a temporary name (`NAME.topwater-swap-1`);
3. `write`: a new file (`version` null) or a changed one;
4. `delete` for what is gone, children before their parent.

| Kind | Fields |
|---|---|
| `write` | `path`, `version`, `content` |
| `delete` | `path`, `version` |
| `rename` | `path`, `to`, `version` |
| `mkdir` | `path` |

Paths are the source's own (`/plan.md`, with `/` the mount point). `version` is what the source said the entry was
when it was read; null means "must not exist yet". A file moved out of the mount (`mv /mnt/notes/a.md /tmp/`) is
fetched before the move, and a `delete` is never produced for a file whose content is nowhere else: one still only a
placeholder somewhere in the machine is fetched first.

`ApplyChangesAsync(path, changes)` applies all of them, or the ones you pass (from the same list; they are matched by
value, content included), in order. A subset must include what it depends on, such as the `mkdir` above a `write`.
Before each change it asks the source for the entry's current version (`stat`); if it differs from `version`, or a
rename's target already exists, it stops with a conflict. A source error also stops it. The result has `Applied`,
`Failed` (the change it stopped at, or null), `Error` (the source's full text, for you), `Conflict` and `NotTried`.
What was applied becomes the mount's new starting point, with the version the source returned for a write, rename or
mkdir; the rest stays pending. `DiscardChanges` drops every pending change and shows the source's files again.

Symbolic links, permission changes and a second hard link inside a mount stay in the machine and are not part of a
change set: `mount -c` shows nothing for them.

## Writing a source

A source is a small class you write around the outside store. Credentials stay inside it: the agent never sees the
object, only the files it lists.

| Member | Required | |
|---|---|---|
| `type`, `id` | yes | `type` names the kind of store (`local-folder`, `wiki`); `id` is your name for this one (a workspace, a site). Both are saved in snapshots, so they must not hold secrets. |
| `capabilities` | yes | which of `write`, `delete`, `rename`, `mkdir` it supports; empty for a read-only source. Staged and live mounts need `write`. |
| `list(path, maxEntries)` | yes | the entries of a directory: `name`, `isDir`, `size` (null: not known until read), `modified`, `version`. More than `maxEntries` are not used, so a source that pages can stop there. |
| `stat(path)` | yes | one entry, or null when missing |
| `read(path, maxBytes)` | yes | a file's bytes; may refuse past `maxBytes` |
| `write(path, data, expectedVersion)` | with `write` | create (`expectedVersion` null: must not exist) or replace; returns the new version |
| `delete(path, expectedVersion)` | with `delete` | |
| `rename(path, to, expectedVersion)` | with `rename` | returns the new version, or null when it didn't change |
| `mkdir(path)` | with `mkdir` | returns the new directory's version, or null |

Every method may be asynchronous (a `Task`, a coroutine or a `Promise`), and in .NET and TypeScript every method gets a
cancellation token (`CancellationToken`, `{ signal }`) that fires at the call's time limit: pass it to your HTTP client,
and sleep on it when backing off from a rate limit. In Python an `async` method is cancelled at the limit; a
synchronous method can't be interrupted, so a remote source should be `async`.

`version` is opaque: an ETag, a revision number, a modification time. Raise `MountConflictException` /
`MountConflictError` when the expected version no longer matches. Raise `MountSourceException` / `MountSourceError`
with a message the agent may see ("plan.md: the page is locked"); anything else you throw is shown to the agent as
`the source failed`, and its full text (which may hold a URL with a token) goes only to you, in `MountInfo.Error` and
`MountApplyResult.Error`.

**The source owns names.** Paths are the only key, so a store addressed by ids (Notion, Drive, SharePoint) keeps its
own map from the paths it listed to ids. Names must be unique in a directory and must be names a file can have: no
`/`, NUL, `.` or `..`; entries that break this are skipped (`mount` says how many). Make titles safe and unique
(`Plan.md`, `Plan (2).md`) and keep them stable between listings, or renames appear from nowhere. A page that is both a
document and a container can be `Page.md` beside `Page/`; a database a directory of row files.

A complete read-write source in Python, a wiki of pages kept in a dictionary with ETags (the tests run it):

```python
import hashlib
from topwater import Shell
from topwater.mounts import MountConflictError, MountEntry, MountSource, MountSourceError


class WikiSource(MountSource):
    """Pages as Markdown files in one folder. A real one would call the wiki's API with its token."""
    type = "wiki"
    capabilities = frozenset({"write", "delete"})

    def __init__(self, space: str, pages: dict[str, str]):
        self.id = space  # saved in snapshots: never the token
        self._pages = {name: text.encode() for name, text in pages.items()}

    @staticmethod
    def _etag(data: bytes) -> str:
        return hashlib.sha1(data).hexdigest()[:12]

    def _page(self, path: str) -> bytes:
        data = self._pages.get(path.lstrip("/"))
        if data is None or path.count("/") != 1:
            raise MountSourceError(f"{path}: no such page")
        return data

    async def list(self, path, max_entries):
        if path != "/":
            raise MountSourceError(f"{path}: pages have no folders")
        return [MountEntry(name, False, len(data), None, self._etag(data))
                for name, data in sorted(self._pages.items())][:max_entries]

    async def stat(self, path):
        if path == "/":
            return MountEntry("", True)
        data = self._pages.get(path.lstrip("/"))
        return None if data is None else MountEntry(path[1:], False, len(data), None, self._etag(data))

    async def read(self, path, max_bytes):
        data = self._page(path)
        if len(data) > max_bytes:
            raise MountSourceError(f"{path}: too large")
        return data

    async def write(self, path, data, expected_version):
        now = self._pages.get(path.lstrip("/"))
        if (None if now is None else self._etag(now)) != expected_version:
            raise MountConflictError(f"{path}: edited by someone else")
        if path.count("/") != 1 or not path.endswith(".md"):
            raise MountSourceError(f"{path}: a page is a .md file at the top")
        self._pages[path[1:]] = bytes(data)
        return self._etag(data)

    async def delete(self, path, expected_version):
        if self._etag(self._page(path)) != expected_version:
            raise MountConflictError(f"{path}: edited by someone else")
        del self._pages[path[1:]]


shell = Shell()
wiki = WikiSource("team", {"plan.md": "# Plan\nDraft\n"})
shell.mount("/mnt/wiki", wiki, "staged")
shell.run("sed -i 's/Draft/Final/' /mnt/wiki/plan.md; echo '# Notes' > /mnt/wiki/notes.md")
print(shell.apply_changes("/mnt/wiki"))   # applies the write to plan.md and the new notes.md
```

The same in C#:

```csharp
using System.Security.Cryptography;
using Topwater;

sealed class WikiSource(string space, Dictionary<string, string> pages) : IMountSource
{
    readonly Dictionary<string, byte[]> _pages = pages.ToDictionary(p => p.Key, p => System.Text.Encoding.UTF8.GetBytes(p.Value));
    public string Type => "wiki";
    public string Id => space;                       // saved in snapshots: never the token
    public MountCapabilities Capabilities => MountCapabilities.Write | MountCapabilities.Delete;

    static string ETag(byte[] data) => Convert.ToHexString(SHA1.HashData(data))[..12].ToLowerInvariant();

    byte[] Page(string path) => path.Count(c => c == '/') == 1 && _pages.TryGetValue(path[1..], out var d)
        ? d : throw new MountSourceException($"{path}: no such page");

    public Task<IReadOnlyList<MountEntry>> ListAsync(string path, int maxEntries, CancellationToken cancellationToken) =>
        path == "/" ? Task.FromResult<IReadOnlyList<MountEntry>>(
                [.. _pages.OrderBy(p => p.Key, StringComparer.Ordinal).Take(maxEntries).Select(p => new MountEntry(p.Key, false, p.Value.Length, null, ETag(p.Value)))])
            : throw new MountSourceException($"{path}: pages have no folders");

    public Task<MountEntry?> StatAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(path == "/" ? new MountEntry("", true)
            : _pages.TryGetValue(path[1..], out var d) ? new MountEntry(path[1..], false, d.Length, null, ETag(d)) : null);

    public Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        var data = Page(path);
        return data.Length > maxBytes ? throw new MountSourceException($"{path}: too large") : Task.FromResult(data);
    }

    public Task<string> WriteAsync(string path, byte[] data, string? expectedVersion, CancellationToken cancellationToken)
    {
        var now = path.Length > 1 && _pages.TryGetValue(path[1..], out var d) ? ETag(d) : null;
        if (now != expectedVersion) throw new MountConflictException($"{path}: edited by someone else");
        if (path.Count(c => c == '/') != 1 || !path.EndsWith(".md")) throw new MountSourceException($"{path}: a page is a .md file at the top");
        _pages[path[1..]] = data;
        return Task.FromResult(ETag(data));
    }

    public Task DeleteAsync(string path, string? expectedVersion, CancellationToken cancellationToken)
    {
        if (ETag(Page(path)) != expectedVersion) throw new MountConflictException($"{path}: edited by someone else");
        _pages.Remove(path[1..]);
        return Task.CompletedTask;
    }
}

// var shell = new Shell();
// await shell.MountAsync("/mnt/wiki", new WikiSource("team", new() { ["plan.md"] = "# Plan\nDraft\n" }), MountMode.Staged);
```

And in TypeScript:

```ts
import { createHash } from 'node:crypto';
import { MountConflictError, MountSourceError, Shell, type MountEntry, type MountSource } from 'topwater';

class WikiSource implements MountSource {
  readonly type = 'wiki';
  readonly capabilities = ['write', 'delete'] as const;
  private pages: Map<string, Uint8Array>;

  constructor(readonly id: string, pages: Record<string, string>) {   // id is saved in snapshots: never the token
    this.pages = new Map(Object.entries(pages).map(([k, v]) => [k, new TextEncoder().encode(v)]));
  }

  private static etag(data: Uint8Array) { return createHash('sha1').update(data).digest('hex').slice(0, 12); }

  private page(path: string): Uint8Array {
    const data = path.split('/').length === 2 ? this.pages.get(path.slice(1)) : undefined;
    if (!data) throw new MountSourceError(`${path}: no such page`);
    return data;
  }

  async list(path: string, maxEntries: number): Promise<MountEntry[]> {
    if (path !== '/') throw new MountSourceError(`${path}: pages have no folders`);
    return [...this.pages].sort(([a], [b]) => (a < b ? -1 : 1)).slice(0, maxEntries)
      .map(([name, data]) => ({ name, isDir: false, size: data.length, version: WikiSource.etag(data) }));
  }

  async stat(path: string): Promise<MountEntry | null> {
    if (path === '/') return { name: '', isDir: true };
    const data = this.pages.get(path.slice(1));
    return data ? { name: path.slice(1), isDir: false, size: data.length, version: WikiSource.etag(data) } : null;
  }

  async read(path: string, maxBytes: number) {
    const data = this.page(path);
    if (data.length > maxBytes) throw new MountSourceError(`${path}: too large`);
    return data;
  }

  async write(path: string, data: Uint8Array, expectedVersion: string | null) {
    const now = this.pages.get(path.slice(1));
    if ((now ? WikiSource.etag(now) : null) !== expectedVersion) throw new MountConflictError(`${path}: edited by someone else`);
    if (path.split('/').length !== 2 || !path.endsWith('.md')) throw new MountSourceError(`${path}: a page is a .md file at the top`);
    this.pages.set(path.slice(1), data);
    return WikiSource.etag(data);
  }

  async delete(path: string, expectedVersion: string | null) {
    if (WikiSource.etag(this.page(path)) !== expectedVersion) throw new MountConflictError(`${path}: edited by someone else`);
    this.pages.delete(path.slice(1));
  }
}

// const shell = new Shell();
// await shell.mount('/mnt/wiki', new WikiSource('team', { 'plan.md': '# Plan\nDraft\n' }), 'staged');
```

Two sources come with Topwater:

- **`LocalFolderSource(root, writable=False, id=None)`** shows a host directory. Read-only unless `writable`. Only
  regular files and directories are listed: symbolic links, FIFOs, sockets and devices are left out, never followed or
  opened, and a file is opened without blocking and checked to be a regular file before it is read. Names with `\`
  are refused on every host, and on Windows also `:`, reserved device names (`CON`, `NUL`, ...) and a trailing dot or
  space. Errors name the path within the folder, never the host's path. It works for Obsidian vaults and any other
  folder of notes. The version is the file's modification time and size.
- **`MemorySource`** keeps files in memory: for tests, and as a test double. `set(path, content)` and `remove(path)`
  change it from the outside (each change gets a new version), and `hook(op, path)`, called before every operation,
  can sleep or throw to simulate latency, failures and conflicts. `calls` lists what was asked.

## How it works

The machine's filesystem is synchronous and the shell is not, so a mount is filled at the points where the shell can
wait for the source:

- **Listing.** Mounting lists the source breadth first until 2,000 entries or 5 seconds; directories below that are
  listed when a command names them (one level, or everything below for a command that walks a directory: `find`,
  `grep -r`, `cp`, `du`, `tree`, `tar`, `rm`, ...). A directory looked into that nothing named (a path inside a script)
  says `not listed from the mounted source yet (run the command again)` and is listed right after the command. A
  vault of a few thousand notes is listed whole when mounted. `mount -r` lists the source again.
- **Content** is fetched lazily, four files at a time: before a command, the files it names (a directory meaning
  everything below), the file it redirects from or appends to, and for a recursive `grep` given no path, the
  working directory. Commands that never read content (`ls`, `find`, `stat`, `rm`, `date`, ...) fetch nothing. A read
  that wasn't foreseen fails with `not fetched from the mounted source yet (run the command again)` and the file is
  fetched right after. When the call's time runs out during a large fetch, the command says how many files it got;
  what was fetched is kept.
- Fetched content stays in the machine as long as the disk cap (`MaxFileSystemBytes`) has room; when it doesn't, the
  oldest unchanged fetched files go back to being placeholders. A file whose size the source doesn't know shows as 0
  bytes until it is fetched.
- **Your changes** are the files themselves: the mount remembers which file it read with which version (and a hash
  of its content), and a change set is the difference.

## Safety and limits

Everything a source returns is treated as untrusted:

- Names that are empty, `.` or `..`, contain `/` or NUL, or are longer than 255 bytes are skipped, as are duplicates.
- A mount holds at most `MaxExtractEntries` entries (100,000 by default) and is listed 32 directories deep. `list` is
  told how many entries are wanted and anything past that is not read.
- A file larger than the free disk space is not fetched (`File too large`). A source returning more bytes than it
  was allowed is refused.
- During a call, every asynchronous source call is bounded by the call's time limit (and cancelled through its
  token). A synchronous call can't be interrupted: `LocalFolderSource` never blocks (no FIFO is opened), and a source
  of your own that blocks holds the call until it returns.
- Paths given to a source are always canonical (`/a/b.md`), built from names that passed the checks above, so the
  agent cannot make a source reach outside its root with `..` or a symbolic link.
- `LocalFolderSource` on Linux and macOS opens every name relative to its directory without following links, so a
  directory swapped for a link to somewhere else while it works is not followed. In .NET, TypeScript and on Windows
  it checks each name for a link just before use, which leaves a small window: if another user, a sync tool or a
  program you don't trust can change the folder while the agent works in it, it could swap a directory for a link in
  between and make a read or write land outside the folder. Don't mount a folder others can write to.
- A file in the folder that is a hard link to a file outside it is that file: reading it reads the outside file. A
  write replaces the name in the folder and leaves the outside file alone.

## Snapshots and recordings

A snapshot saves each mount's definition (path, mode, source `type` and `id`) and its pending changes with their
content, never the source's files. A file with mounts is format version 3, so a Topwater that doesn't know mounts
refuses it instead of loading the machine without them.

**A snapshot can't write to a source.** Restoring one gives each mount the source and the mode you mounted with
(`MountAsync`), never the file's; without a source of the same `type` and `id` (`Unmount` forgets it), the mount point
is an empty directory the agent can't open and `mount` shows `(unavailable: no source for type:id)`. The pending
changes in a snapshot loaded from a file are as untrusted as the file: they are kept apart (`SnapshotChanges`), not
in the files, not in `PendingChanges`, never applied by live mode, and `mount` says they wait for you. Call
`AcceptSnapshotChangesAsync` to put them in the files (they are then pending changes like the agent's, checked against
the versions they were made on), or `DiscardChanges` to drop them. A snapshot taken in the same process
(`Snapshot()`, `Fork()`) is trusted and comes back as it was, with its listing and changes, without a source call; a
file's mounts are listed again at the next call, within the listing budget, and a listing that fails leaves the mount
unavailable for that call only.

A recording saves what the sources answered (listings, file contents, results of writes) with each call, and what the
mounts held when it started. A shell built from the recording, and any fork of it, answers from those and never calls
a source, even one `configure` mounted; a question that wasn't recorded fails with `<op> <path> is not in the Topwater
recording`. Errors are recorded as the agent saw them.
