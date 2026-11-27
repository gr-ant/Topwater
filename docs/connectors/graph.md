# OneDrive and SharePoint (Microsoft Graph)

`GraphSource` mounts a OneDrive, a SharePoint document library, or a whole SharePoint site (its libraries, lists and
subsites) through Microsoft Graph v1.0. The agent works on it with ordinary commands; in staged mode you get the
result as a [change set](../mounts.md#change-sets) to review and apply. The design every port follows is in
[graph-design.md](graph-design.md).

Python is the reference implementation; .NET and TypeScript follow it.

```python
from topwater import Shell
from topwater.connectors.graph import GraphSource

source = GraphSource(token=get_graph_token, site="https://contoso.sharepoint.com/sites/Team")
shell = Shell()
shell.mount("/mnt/sp", source, "staged")
shell.run("cd /mnt/sp/Documents && mkdir -p Archive && mv Inbox/*2024* Archive/")
for change in shell.pending_changes("/mnt/sp"):
    print(change.kind, change.path, change.to or "", source.note(change) or "")
result = shell.apply_changes("/mnt/sp")
```

`GraphSource(token, *, site=None, drive=None, id=None, transport=None, timeout=60, api_url=..., rate=None)`:

| Argument | |
|---|---|
| `token` | an access token for Graph, or a function (may be `async`) called before every request that returns the current one. Signing in and refreshing is your application's job (MSAL, Azure.Identity). |
| `site` | a site's URL (`https://contoso.sharepoint.com/sites/Team`), `host:/sites/path`, or its Graph id |
| `drive` | a drive id, or `"me"` for the signed-in user's OneDrive. Give `site` or `drive`, not both. |
| `id` | your name for it in snapshots (default: the site URL or the drive id); never a secret |
| `transport` | sends one request (the web layer's transport); tests pass a fake. The default never follows redirects and refuses internal addresses. |

## Setting up an app registration

1. In the Microsoft Entra admin center, **App registrations > New registration**. Any name; supported account types as
   your tenant needs. For the development sign-in below add the redirect platform **Mobile and desktop applications**
   with `https://login.microsoftonline.com/common/oauth2/nativeclient`.
2. **Authentication > Advanced settings > Allow public client flows: Yes** (the device-code sign-in needs it).
3. **API permissions > Add > Microsoft Graph > Delegated**: `Files.ReadWrite.All`, `Sites.ReadWrite.All`,
   `offline_access`, `User.Read`. `Sites.Manage.All` is optional: it allows creating and deleting lists and columns,
   which the connector never does (the sandbox app has it for setting up test lists). For a read-only mount `Files.Read.All` and
   `Sites.Read.All` are enough. Grant admin consent if your tenant requires it.
4. Note the tenant id and the application (client) id.

For development, `python/examples/graph_auth.py` reads `GRAPH_TENANT_ID`, `GRAPH_CLIENT_ID` and `GRAPH_SITE_URL` from
`~/.config/topwater/graph.env` (or the environment), signs in with a device code
(`python python/examples/graph_auth.py sign-in`) and keeps the refresh token in `~/.config/topwater/graph.token.json`
(mode 0600, rewritten whenever Microsoft rotates it). Its `RefreshTokenProvider` is a `token` function. It is an
example, not part of the package: a real application keeps its tokens its own way.

## What the agent sees

```
/mnt/sp/
  Documents/                  a document library: its folders and files
    Inbox/Budget final.xlsx
  Project Tracker/            a list: one file per item
    Status page.md
    Website Redesign.md
  Contacts/
    Alex Rivera.md
    Alex Rivera (2).md        two items with one title
  Marketing/                  a subsite: its own libraries, lists and subsites
    Documents/
    Campaign Calendar/
```

- **Libraries** keep their name. **Lists** and **subsites** are shown by their display name; when a list or a subsite
  has the name of something already at that level, it gets ` (list)` or ` (site)` (`Projects/` is the library,
  `Projects (site)/` the subsite). Hidden lists, system lists and galleries are left out; document libraries appear
  once, as libraries. Subsites are shown 8 levels deep.
- **Files are their real bytes.** `to-markdown` reads Word and Excel files, `sha256sum` and `cmp` compare them, and a
  PDF is the PDF (`to-markdown` has no PDF reader; a host can offer `pdftotext`). Office files carry their own
  metadata, so two copies with the same content usually differ in bytes: compare `to-markdown` output instead. Sizes and modification times come from SharePoint; a file is downloaded only when a command
  reads it.
- **A list item** is `Title.md` (titles sorted by item id; a repeated title gets ` (2)`, a `/` becomes `∕`, an empty
  title is `Item 7.md`). It holds the item's columns as front matter, one `Name: JSON value` a line, and a comment
  naming the columns that can't be changed:

  ```
  ---
  Budget: 12000.0
  Created: "2026-11-27T14:50:01Z"
  Created By: "Grant Miller"
  DueDate: "2026-12-10T08:00:00Z"
  ID: 4
  Modified: "2026-11-27T14:50:01Z"
  Modified By: "Grant Miller"
  Notes: "Waiting on vendor"
  Owner: "Sam"
  Status: "Blocked"
  # read-only: Created, Created By, ID, Modified, Modified By
  ---
  ```

## What can be changed

| Agent does | In SharePoint |
|---|---|
| writes a file in a library | upload (one request up to 4 MB, an upload session in 3.2 MB chunks above that) |
| `mkdir` in a library | a new folder |
| `mv` inside a library | a rename or move of the same item: id, version history and sharing are kept |
| `mv` to another library, list or site | a copy and a delete (`source.note(change)` says so; the copy is a new item) |
| `rm` | the item goes to the site's recycle bin |
| edits a list item's front matter | the changed columns are updated |
| writes a new `Name.md` in a list | a new item titled `Name` |
| renames an item file | its Title changes |
| `rm` an item file | the item goes to the recycle bin |

Refused with a message the agent sees (the change stays pending; `mount --discard` drops it):

- changing a column SharePoint sets (`ID`, `Created`, `Modified`, `Created By`, `Modified By`), a calculated column, or
  one of a type not editable here (person, lookup, managed metadata, hyperlink, ...): `Status page.md: ID can't be
  changed (set by SharePoint)`;
- a choice that isn't one of the column's choices, a number that isn't one, a date that isn't `2026-12-10` or
  `2026-12-10T09:00:00Z`, a column the list doesn't have, text after the front matter;
- names SharePoint doesn't allow: `" * : < > ? / \ |`, leading or trailing spaces, a trailing dot, `CON`, `PRN`, `AUX`,
  `NUL`, `COM0`-`COM9`, `LPT0`-`LPT9`, `.lock`, `desktop.ini`, anything containing `_vti_`, a name starting with
  `~$`, `forms` at the top of a library; a path over 400 characters with the library's own path;
- creating, renaming or deleting a library, a list or a subsite, and files outside a library;
- deleting a folder someone added to since it was read.

New items get only the columns you can change: `cp "Status page.md" "New task.md"` makes a new item with the same
Status, Owner, ... and SharePoint sets the rest.

Refusals come from the source's own operations, so they reach whatever wrote the file: in a staged mount
`apply_changes` stops at the refused change and returns the message; in a live mount the command that made the change
(a shell command, a redirection, a program or Python function the host registered as a command) gets
`mount: PATH: MESSAGE; the change stays pending` on its error output. Name, path, column and value checks run before
any request, so a refused change sends nothing.

## Conflicts

Every change is checked against what was read: a file by its content tag (cTag, which a rename elsewhere doesn't
change), a list item by its eTag. `ApplyChangesAsync` / `apply_changes` asks for the current version first and the
request itself carries `If-Match`, so an edit made in SharePoint in between is a conflict (`MountApplyResult.Conflict`),
not an overwrite. The result says which change it stopped at and which were not tried.

## Limits and failures

- Throttling: `429` and `503` are retried (up to 5 tries, any method: SharePoint did nothing), `Retry-After` is obeyed
  by every following request, and a wait over 60 seconds fails the request. Other `5xx` and no answer are retried only
  for reads; a write that got no answer says the change may or may not have been made.
- A listing reads at most 50 pages of 200 entries. `@odata.nextLink` is followed only back to the Graph API.
- A move between libraries copies at most 1,000 entries and 250 MB; bigger moves are refused (move them in SharePoint).
- A site is listed for at most 5 seconds when it is mounted (like any source); folders below that are listed when a
  command names them. A glob inside a script over a folder not listed yet says `not listed from the mounted source
  yet (run the command again)`: run it again, or list first (`find DIR >/dev/null`).
- A list item file is at most 1 MB. Items in folders inside a list are shown flat; folders in lists are not shown.
- Names compare without case in SharePoint: `Report.docx` and `report.docx` can't both exist, and writing one when the
  other exists fails. A rename that changes only case works.
- In a site mount, the framework's temporary name for a replaced folder (`/.topwater-move-N`, see
  [change sets](../mounts.md#change-sets)) would land at the site's level and is refused; replace such a folder in two
  steps.

## Safety

- The token is asked from your function for each request, sent only to the Graph API, and never shown to the agent,
  logged or saved in a snapshot or recording. Download links (SharePoint answers a content request with a redirect to
  a pre-signed URL) and upload sessions are pre-signed: they get no token, and must be `https`.
- Ids from answers are checked before they go into a URL; names go in percent-encoded.
- Everything an answer holds is bounded: 32 MB of JSON, 1,024 characters of version, the listing limits above, and the
  mount's own limits ([safety and limits](../mounts.md#safety-and-limits)).
- Deletes go to the recycle bin. Nothing deletes a library, a list or a site.

## Example

`python/examples/sharepoint_restructure.py` mounts a site staged, runs a bash script an agent could have written (file
the Inbox into Projects and Archive, drop a budget whose text is the same as another's, name the untitled document,
mark a Project Tracker item In progress, deal with a "Copy of" in the Marketing subsite) and prints the change set,
with the source's note on any move between libraries. Nothing is written without `--apply`; `--base FOLDER` runs it on
a copy inside the libraries instead.

## Tests

`python/tests/test_graph.py` runs `tests/connectors/graph/cases.json` against a fake Graph (`python/tests/graph_fake.py`)
and the hostile cases. With `TOPWATER_LIVE_GRAPH=1` and a signed-in `python/examples/graph_auth.py`, it also runs against the real site
in `GRAPH_SITE_URL`: in a scratch folder and on scratch list items, deleted afterwards.
