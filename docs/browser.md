# A web page as files

`Topwater.Browser` (.NET) mounts a live web page the way Linux shows a device under `/sys`: the page is a directory,
reading a file reads the page (`cat /web/title`), and writing a file acts on it (`echo 1 > .../click`). A real
Chromium, driven by Microsoft Playwright, loads the page; every request it makes is checked and sent by Topwater under
your [web policy](web.md), and the browser itself reaches nothing.

This reaches the network through a real browser process, so it is opt-in: a separate package, and a browser your
application launches and owns. The package and its types are marked `[Experimental("TW002")]` during the previews:
suppress TW002 to use them.

- [Mounting a page](#mounting-a-page)
- [The tree](#the-tree)
- [Acting on the page](#acting-on-the-page)
- [Options](#options)
- [Safety](#safety)
- [What it can't enforce](#what-it-cant-enforce)

## Mounting a page

```bash
dotnet add package Topwater.Browser --prerelease
```

Playwright needs its Chromium once per machine (into `~/.cache/ms-playwright`): call
`Microsoft.Playwright.Program.Main(["install", "chromium"])`, or run `pwsh bin/Debug/net10.0/playwright.ps1 install
chromium` after a build (`--with-deps` also installs the system libraries, on Linux).

```csharp
using Topwater;
using Topwater.Browser;

var policy = new WebPolicy { Domains = ["shop.example.com", "cdn.example.com"], Methods = ["GET", "POST"], MaxRequestsPerCall = 100 };
await using var page = await BrowserSource.LaunchAsync(new BrowserSourceOptions { Policy = policy }, "https://shop.example.com/");

var shell = new Shell();
await shell.MountAsync("/web", page, MountMode.Live);
var r = await shell.RunAsync("cat /web/title; ls /web/elements | head");
```

`LaunchAsync` starts headless Chromium with `BrowserSource.LaunchArgs`, a context with downloads off and service
workers blocked, and a page; the source closes that browser when disposed. To use a browser you launched yourself,
pass a page: `new BrowserSource(page, options)`. Give it a context of its own, launched with `LaunchArgs`, with
`AcceptDownloads = false` and `ServiceWorkers = ServiceWorkerPolicy.Block`, and create the source before the page
loads anything: from then on every request of that context goes through the policy. `OpenAsync(url)` loads a page
from your code, under the same policy; `Page` gives you the Playwright page (the agent never sees it).

Mount it **live**: each write is an action, done right after the command that wrote it. A staged mount would hold
the actions until you apply them, and a read-only one shows the page without acting on it.

`examples/BrowserMount` logs in to a public practice site with a bash script, or lets a model do it through
OpenRouter.

## The tree

```
/web/url               the page's address; write a full http(s):// URL to load another
/web/title             the page's title
/web/status            http: the document's status, state: loading or complete, last: ok or the last action's error,
                       then a line for each request the policy refused and each note (a download blocked, a new window)
/web/text.md           the page's content as Markdown (to-markdown's HTML reader), cut at MaxTextBytes
/web/tree.txt          the accessibility tree, one node a line, each element followed by "-> elements/<its directory>"
/web/screenshot.png    the viewport as a PNG
/web/dialog            the last alert, confirm or prompt and how it was answered
/web/back, forward, reload    any write does it
/web/elements/<role>-<name>/   one directory for each interactive element
    role, name, description, disabled, focused    facts (0 or 1 for yes and no)
    click                  any write clicks it
    press                  write a key, or one a line: Enter, Tab, ArrowDown, Control+A, a
    value                  text boxes, search boxes, comboboxes, selects, sliders, spin buttons, list boxes:
                           the value; a write replaces it (a select takes an option's label or value, one a line)
    options                comboboxes, selects and list boxes: the choices, one a line
    checked                checkboxes, radio buttons, switches: 0 or 1; write 0 or 1
    href                   links: the target
/web/elements/file-<label>/    one for each file field, shown or hidden
    role, name
    upload/                cp a file into it to choose that file
/web/forms/<name>/
    fields.json            {"Email": "", "Password": "********", "Remember me": false}; write an object to fill fields
    submit                 any write submits the form
```

**Names.** An element's directory is its role and its accessible name in lower case, letters and digits kept and
every other run made one `-`, cut at 60 characters: `button-sign-in`, `textbox-email`, `link-next-page`. An element
without a name is its role alone. Elements whose directories would be the same get ` (2)`, ` (3)` in page order, as
the [connectors](connectors/README.md) do: two "Delete" buttons are `button-delete` and `button-delete (2)`. There are
no positions in the names, so a directory keeps its name while the element keeps its role and name and the elements
of the same name before it stay. A form is named by its `aria-label`, `name` or `id` (`forms/login`), or `form`.

Interactive roles are `button`, `link`, `textbox`, `searchbox`, `checkbox`, `radio`, `switch`, `combobox`, `listbox`,
`option` (outside a select), `slider`, `spinbutton`, `tab`, `menuitem`, `menuitemcheckbox`, `menuitemradio` and
`treeitem`. The first `MaxElements` (300) get directories; `tree.txt` shows them all and says how many have none.

The values of password fields are never shown: `value`, `fields.json` and `tree.txt` show `********`.

## Acting on the page

Writing a file is an action. The mount applies the write right after the command that made it, then waits for the
page to settle (its requests stop, and a page it started loading loads, within the time limits) and reads the page
again, so the next command already sees the result:

```bash
cd /web/elements
echo me@example.com > textbox-email/value && echo hunter2 > textbox-password/value && echo 1 > button-sign-in/click && cat /web/url
echo '{"Email": "me@example.com", "Password": "hunter2"}' > /web/forms/login/fields.json && echo > /web/forms/login/submit
echo Sweden > combobox-country/value; echo 1 > checkbox-remember-me/checked; echo Enter > textbox-search/press
cp ~/cv.pdf file-resume/upload/
```

- **One action a command.** Writes are applied after each command in the order the commands ran; several writes
  in one command (`tee a/value b/value`) are applied in the mount's order, not yours.
- **A refused action** prints `mount: <path>: <why>` (`the element is no longer on the page`, `Denmark: not one of
  the options (Norway, Sweden)`, `Blocked by the web policy: ...`) and the file shows the page's value again. Writing
  a fact (`role`, `title`, ...) says which files there take writes.
- **The tree is read again** after every write and at the start of every call, since the page can change on its
  own. Between two commands of one call it is not: run `mount -r /web` to look again.
- **Forms.** `fields.json` uses each field's label (or its `aria-label`, placeholder, `name` or `id`), made unique
  the same way; a name that matches no field, in any case, is refused before anything is filled. Checkboxes and radio
  buttons take `true` or `false`. `submit` first checks the form as the browser would and says what is missing
  (`the form is not complete: Email: Please fill out this field.`), then submits it as pressing Enter in it would.
- **Uploads** take the bytes of a file in the machine (`cp FILE .../upload/`), named as the copy is, up to
  `MaxUploadBytes`; never a path on the host. Each file copied replaces the one before.
- **Dialogs** are dismissed, and `dialog` records them (`confirm: Delete the trout?` and `dismissed`). Write
  `accept`, `accept TEXT` (the answer to a prompt) or `dismiss` to `dialog` to answer the next one; it then shows
  `next: accept` until a dialog comes. A page asking to stay before it unloads is always let go.
- **A new window** the page opens is closed and its address loaded in the page instead, under the policy; `status`
  says so.
- **Downloads** are cancelled, and `status` names the file; nothing is written anywhere.

## Options

| `BrowserSourceOptions` | Default | |
|---|---|---|
| `Policy` | required | the [web policy](web.md): sites, methods, limits, credentials. Add `POST` to `Methods` for forms. `MaxRequestsPerCall` counts the requests of one action and what the page fetches until the next one (a page with fifty images needs more than the default 20); `MaxRequests` counts the source's life |
| `Id` | `page` | your name for the mount in snapshots; never a secret |
| `ActionTimeout` | 10 s | one browser operation: a click, a navigation, a screenshot. A navigation that hasn't answered by then is aborted |
| `SettleTimeout` | 3 s | after an action, how long to wait for the page's requests to stop |
| `MaxElements` | 300 | elements with a directory |
| `MaxTextBytes` | 200,000 | `text.md` and `tree.txt` |
| `MaxScreenshotBytes` | 5,000,000 | `screenshot.png` |
| `MaxValueLength` | 100,000 | characters of a value read or written |
| `MaxUploadBytes` | 1,000,000 | a file given to an upload field (a request body is capped at 1,000,000 bytes by the policy anyway) |

## Safety

What the source enforces, whatever the agent writes and whatever the page does:

- **Every request through the policy.** The source routes every request of the page's browser context (documents,
  frames, scripts, images, `fetch` and XHR, form posts, a new window's) and fetches it itself, the way `curl` does:
  only the hosts, ports and methods you list, https unless `AllowHttp`, no IP literal or name that resolves to a
  private, loopback, link-local (169.254.169.254, the metadata service) or otherwise internal address, the connection
  made to the address that was checked (so DNS rebinding can't swap it), bodies and request counts capped, your
  `Credentials` added for their host only and scrubbed from what comes back. A refused request is aborted and listed
  in `status`. See [what the policy guarantees](web.md#what-it-guarantees).
- **The browser has no network of its own.** `LaunchArgs` make every host name fail to resolve inside Chromium and
  send anything that isn't routed to a proxy that isn't there, with WebRTC kept off UDP, so what Playwright can't
  route (a service worker's requests, prefetching, WebRTC) goes nowhere. All the browser gets is what the source
  fetched for it.
- **Every redirect checked.** Chromium follows a redirect it was handed without routing the next request, so for a
  page the source answers with a page that moves on to the target, which the browser then requests through the
  route; a script's or an image's redirects the source follows itself, each hop checked. A redirect to `file:` or to a
  refused host is refused.
- **Only http and https.** `url` takes nothing else, the route refuses every other scheme, and Chromium won't let a web
  page open a `file:` URL. WebSockets are closed before they connect.
- **No host files.** Downloads are cancelled (and off in the context `LaunchAsync` makes). A file field gets only
  bytes from the machine, through `upload/`. The agent can't run JavaScript: the source's own scripts only read facts
  and submit forms.
- **Nothing of yours in the files.** Cookies stay in the browser context; password fields show `********`; the
  policy's credentials are never shown. What the agent sees of an error is a fixed message, the policy's reason, or
  Playwright's first line when it is plain words, never a stack, a path or a URL from Playwright.
- **Bounded.** Elements, `text.md`, `tree.txt`, the screenshot, values, names (1,000 characters), uploads,
  `fields.json` (1,000,000 bytes, 8 levels), the page HTML read for `text.md` (5,000,000 characters) and the lines of
  the accessibility snapshot (50,000) all have limits. Every Playwright call has a timeout (`ActionTimeout`, or 2 s to
  read a fact), the wait ends at the call's time limit, one browser operation runs at a time and the next waits
  until the last really ended, and a navigation that doesn't answer is aborted after `ActionTimeout`, since
  Playwright can't look into a page while its document loads. A page that never settles costs `SettleTimeout`.

## What it can't enforce

- **A browser launched without `LaunchArgs`** resolves names and connects by itself for what isn't routed (service
  workers unless blocked, WebRTC, prefetching): those requests skip the policy, and DNS rebinding applies to them.
  Use `LaunchAsync`, or launch with `LaunchArgs`, `ServiceWorkers = Block` and `AcceptDownloads = false`.
- **The route is the whole context.** Every page in the context the source was given goes through its policy, and
  the source acts only on its own page: give it a context of its own.
- **The page's own code runs.** Scripts from the sites you allow run in a real browser: they use CPU and memory
  Topwater doesn't cap, can send what the page holds to any allowed site, and could exploit a browser bug. Allow
  only sites you trust, keep Playwright (and so Chromium) up to date, and run the browser in a container if the sites
  are not yours.
- **The session is real.** The agent can't read cookies, but it acts with whatever session the context has: if your
  code signed in, the agent acts as that user.
- **Read, then send.** As with `curl`, the agent can carry what it read on one allowed site to another that accepts
  the methods you allow. Keep `Methods` to what the task needs.
- **Shortcuts:** a 307 or 308 redirect of a POST arrives as a GET; cookies set by a script's or an image's
  intermediate redirect are dropped; a response compressed other than with gzip or deflate is refused (the source
  asks for none); an element is found again by its role, exact name and place among elements of that name, so a page
  that reorders same-named elements between two looks can swap them.
- **Recordings** keep what the source answered (listings and file contents), not the browser's requests: a replay
  shows the same files without opening a browser, and an action not recorded fails as any unrecorded mount call does.
