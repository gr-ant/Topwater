# Topwater.Browser

Mount a live web page in a [Topwater](https://github.com/gr-ant/Topwater) shell, the way Linux shows devices under
`/sys`: the agent reads the page with `cat` and acts on it with `echo`. Backed by Microsoft Playwright; every request
the page makes goes through Topwater's web policy, and the browser itself reaches nothing.

```bash
dotnet add package Topwater.Browser --prerelease
pwsh bin/Debug/net10.0/playwright.ps1 install chromium   # or Microsoft.Playwright.Program.Main(["install", "chromium"])
```

```csharp
using Topwater;
using Topwater.Browser;

var shell = new Shell();
await using var page = await BrowserSource.LaunchAsync(
    new() { Policy = new WebPolicy { Domains = ["example.com"], Methods = ["GET", "POST"] } }, "https://example.com/");
await shell.MountAsync("/web", page, MountMode.Live);
```

What the agent types:

```console
cat /web/title /web/text.md            # the page as Markdown
cat /web/tree.txt                      # its elements, each with its directory
echo me@example.com > /web/elements/textbox-email/value
echo 1 > /web/elements/button-sign-in/click && cat /web/url
```

This reaches the network through a real browser, so it is opt-in by the host. See
[docs/browser.md](https://github.com/gr-ant/Topwater/blob/main/docs/browser.md) for the layout and the safety model.
