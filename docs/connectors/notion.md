# Notion

`NotionSource` shows a Notion page and everything below it as a folder in the machine. Mount it staged and the agent
can read, reorganise and edit your pages with ordinary commands; you review the change set and apply it, or drop it.
It is a [mounted source](../mounts.md), so modes, change sets, conflicts, snapshots and recordings work as described
there.

Python only for now; the C# and TypeScript ports follow [the design note](notion-design.md).

```python
from topwater import Shell
from topwater.connectors.notion import NotionSource

shell = Shell()
shell.mount("/mnt/notion", NotionSource(token=lambda: my_token(), root="https://www.notion.so/Team-425832d4414a8087b9a1f1cb08ff38b1"), "staged")
shell.run("cd /mnt/notion && mkdir Wiki/Policies && mv 'Inbox (unsorted)/Expenses.md' Wiki/Policies/")
for change in shell.pending_changes("/mnt/notion"):
    print(change.kind, change.path, change.to or "")
result = shell.apply_changes("/mnt/notion")      # or discard_changes
```

`python/examples/notion_restructure.py` runs a whole restructuring script this way, prints the change set with a diff of
each edited page, and applies it only with `--apply`.

## Setup

1. Create an internal integration at <https://www.notion.so/profile/integrations> (or use your public integration's
   OAuth token). Give it *Read content*, *Update content* and *Insert content*; a read-only mount needs only *Read
   content*.
2. Share the top page with it: on the page, `•••` → *Connections* → add the integration. It sees that page and
   everything below it, nothing else.
3. Pass the page's id or URL as `root` (a database works too) and the token as `token`.

`token` is a string, or a function (it may be `async`) called before every request. Refreshing OAuth tokens is your
application's job: return the current token there and the next request uses it. The token is sent only in the
`Authorization` header to `api.notion.com`; the agent never sees the source object, and the mount's `id` (saved in
snapshots, shown by `mount`) is the root's id unless you pass your own.

| Parameter | Default | |
|---|---|---|
| `token` | | the token, or a function returning it |
| `root` | | the top page or database: an id or a Notion URL |
| `id` | the root's id | your name for this source in snapshots |
| `rate` | `3.0` | requests a second (Notion allows about three on average) |
| `timeout` | `30.0` | seconds per request |
| `transport` | the network | sends one request; tests pass a fake (see the design note) |

## What the agent sees

```
/mnt/notion/
  index.md                      the root page's own text
  Projects/                     a page with subpages is a folder ...
    index.md                    ... and its own text is index.md
    Q4 Launch Plan.md           a page without subpages is a file
    Website Redesign/
      index.md
      Kickoff notes.md
  Inbox (unsorted)/
    Notes.md                    two pages called "Notes": the older one keeps the name,
    Notes (2).md                the other gets " (2)"
    A∕B test.md                 "/" in a title is shown as ∕ (division slash)
    Untitled.md                 a page without a title
  Meeting Notes/                a database is a folder of rows
    Q3 Retro.md
```

- **Pages** are Markdown in [Notion-flavored Markdown](https://developers.notion.com/guides/data-apis/notion-flavored-markdown.md):
  headings, lists, to-dos, tables, `<callout>`, `<toggle>` and so on. Blocks Markdown can't express appear as
  `<unknown url="..." alt="breadcrumb"/>`; leave them in place and they stay in the page.
- **Subpages and databases** are the folder's entries, not part of the text: `index.md` holds the page's own text only,
  and writing it never moves or removes a subpage. A subpage inside a toggle or a column stays in the text as a
  `<page url="...">` tag; keep it as it is.
- **Database rows** are files whose properties come first, as front matter. The title is the file name.

  ```
  ---
  Date: "2026-10-01"
  Done: true
  Tags: ["website", "launch"]
  Type: "Retro"
  ---
  Keep: demos. Change: fewer meetings.
  ```

  Values are JSON (which is also YAML); `Type: Retro`, `Tags: [a, b]` and a `- item` list are understood too. Text,
  number, select, status, multi-select, checkbox, date, URL, e-mail and phone properties can be changed. Formulas,
  rollups, people, relations, files and the times and users Notion sets are shown but can't be changed. A property left
  out of the front matter is left as it is. A new select option is created by naming it.
- `ls -l` shows a page's size as 0 until it has been read (Notion doesn't say how long a page is), and its date is
  when it was last edited.
- A database with several data sources is a folder with one folder per data source.

## What the agent can change

| In the shell | In Notion |
|---|---|
| edit `Page.md` or `Folder/index.md` | the page's text is replaced (`replace_content`); its subpages stay |
| edit a row's file | changed properties and text |
| `echo ... > New.md` in a page folder | a new page; in a database folder, a new row (front matter sets its properties) |
| `mkdir Name` | a new, empty page |
| `mkdir X` beside a page shown as `X.md` | that page becomes the folder `X/`, so pages can be moved into it; `mv X.md X/index.md` keeps its text there |
| `mv A.md Folder/B.md` | the page moves under `Folder`'s page and is retitled `B` |
| `mv` a page into a database folder, or a row out of one | it becomes a row, or a page (a row leaving its database keeps only its title) |
| `mv` a database folder within its folder | the database is renamed |
| `rm Page.md`, `rm -r Folder` | the page (with what is under it) moves to Notion's trash, where it can be restored until the trash is emptied |
| `rm Folder/index.md` alone | nothing: a page's text goes with its folder. To empty a page, write an empty `index.md` |

Refused, with a message the agent sees:

- moving a database to another folder (Notion's API can't move databases);
- files that aren't `.md`, or that aren't UTF-8 text, or larger than 1,000,000 bytes;
- text that adds a `<page>` or `<database>` tag or changes one (a tag's title would rename that page);
- moving or renaming `index.md`; `mkdir` inside a database; moving a page that has subpages into a database;
- changing a computed property, naming a property the database doesn't have, or a value of the wrong kind;
- editing a page so long that Notion sends it cut short (about 20,000 blocks).

## Conflicts

Notion has no version tags; the version of a page is its `last_edited_time`, which Notion keeps to the minute. Before
each change the connector checks that the page is still where it was listed, with the same title and the same
`last_edited_time`. Because an edit in the same minute leaves that time unchanged, a page the agent has read is also
compared with what it read: if its text (or a row's properties) changed in between, the change is a conflict. The
connector's own renames and moves during an apply are not counted as someone else's edit.

A page the agent never read (moved or deleted without opening it) is checked by its time only, so an edit made in Notion
in the same minute as the listing can go unnoticed by a move or a trash. Both are reversible in Notion.

## Limits and costs

- About three requests a second. Mounting lists the top folders (one request per page, to see which pages have
  subpages) for 5 seconds; deeper folders are listed when a command names them, so a large workspace is listed over
  several commands. Reading a page is one request (two for a row). Applying a change takes two to five requests.
- A rate limit (429) is waited out as Notion's `Retry-After` says, up to 60 seconds, four times; reads are also retried
  when Notion fails (5xx) or doesn't answer. A write that got no answer is not repeated: the error says it may or may
  not have been made.
- A page with more than 5,000 top-level blocks shows the subpages among the first 5,000. A database folder lists at
  most 5,000 rows, and never more than the mount's listing budget.
- Names are cut at 200 bytes. Names stay the same for the whole session: when one of two "Notes" pages is moved away
  the other keeps "Notes (2)" until the mount is listed again in a new session.

## Safety

- Notion refuses to delete a subpage or database through a text update unless asked to, and the connector never asks.
  It also takes the page's children out of the agent's view and puts the current ones back on every write, so an edit
  can't move, drop or rename a subpage.
- Every id from Notion is checked to be an id before it is used in a request path; titles become names that are safe
  in a path. Response sizes, pages of results and retries are bounded.
- The default transport connects only to public addresses and ignores proxy settings (the same transport as the
  shell's [web access](../web.md)).
- Deletes go to Notion's trash. Nothing the connector does is permanent before Notion empties the trash.
