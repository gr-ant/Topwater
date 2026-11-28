using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Topwater.Browser.Tests;

/// <summary>The browser source (docs/browser.md) against a real Chromium, every page served from memory.</summary>
[Collection("browser")]
public sealed class BrowserTests(BrowserFixture fixture) : IAsyncLifetime
{
    readonly List<IBrowserContext> _contexts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _contexts) await c.CloseAsync();
    }

    static BrowserSourceOptions Options(Site site, int maxElements = 300) => new()
    {
        Policy = new WebPolicy { Domains = ["app.test", "cdn.test"], Methods = ["GET", "POST"], Handler = site, MaxRequestsPerCall = 100 },
        ActionTimeout = TimeSpan.FromSeconds(5),
        MaxElements = maxElements,
        MaxTextBytes = 50_000,
    };

    async Task<(Shell Shell, BrowserSource Source)> Open(Site site, string start = "https://app.test/", BrowserSourceOptions? options = null, ShellOptions? shell = null)
    {
        var context = await fixture.Browser.NewContextAsync(new() { AcceptDownloads = false, ServiceWorkers = ServiceWorkerPolicy.Block });
        _contexts.Add(context);
        var source = new BrowserSource(await context.NewPageAsync(), options ?? Options(site));
        await source.OpenAsync(start);
        var sh = new Shell(shell ?? new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(60) });
        await sh.MountAsync("/web", source, MountMode.Live);
        return (sh, source);
    }

    static async Task<string> Out(Shell sh, string command)
    {
        var r = await sh.RunAsync(command);
        Assert.True(r.Stderr == "", $"{command}\n{r.Stderr}");
        return r.Stdout;
    }

    const string Home = """
        <!doctype html><meta charset="utf-8"><title>Shop</title>
        <h1>Welcome to the shop</h1>
        <p>Fresh fish every day.</p>
        <a href="/next">Next page</a>
        <form aria-label="Login" action="/login" method="post">
          <label>Email <input name="email" required></label>
          <label>Password <input name="password" type="password"></label>
          <label><input type="checkbox" name="remember"> Remember me</label>
          <label>Country <select name="country"><option>Norway</option><option>Sweden</option></select></label>
          <button>Sign in</button>
        </form>
        """;

    static Site Shop() => new Site()
        .Page("https://app.test/", Home)
        .Page("https://app.test/next", "<title>Next</title><h1>The next page</h1><a href=\"/\">Home</a>")
        .On("https://app.test/login", async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();
            var r = new HttpResponseMessage(HttpStatusCode.SeeOther);
            r.Headers.Location = new Uri("/account", UriKind.Relative);
            r.Headers.Add("Set-Cookie", $"who={Uri.EscapeDataString(body)}; Path=/");
            return r;
        })
        .On("https://app.test/account", req =>
        {
            var cookie = req.Headers.TryGetValues("Cookie", out var c) ? string.Join(";", c) : "";
            return Task.FromResult(Site.Html($"<title>Account</title><p id=who>{WebUtility.HtmlEncode(Uri.UnescapeDataString(cookie))}</p>"));
        });

    [ChromiumFact]
    public async Task ThePageIsATreeOfFiles()
    {
        var (sh, _) = await Open(Shop());
        Assert.Equal("back\ndialog\nelements\nforms\nforward\nreload\nscreenshot.png\nstatus\ntext.md\ntitle\ntree.txt\nurl\n", await Out(sh, "ls /web"));
        Assert.Equal("Shop\nhttps://app.test/\n", await Out(sh, "cat /web/title /web/url"));
        Assert.Equal("button-sign-in\ncheckbox-remember-me\ncombobox-country\nlink-next-page\ntextbox-email\ntextbox-password\n", await Out(sh, "ls /web/elements"));
        Assert.Equal("click\ndescription\ndisabled\nfocused\nname\npress\nrole\nvalue\n", await Out(sh, "ls /web/elements/textbox-email"));
        Assert.Equal("link\nNext page\nhttps://app.test/next\n0\n", await Out(sh, "cd /web/elements/link-next-page && cat role name href disabled"));
        Assert.Equal("Norway\nSweden\n", await Out(sh, "cat /web/elements/combobox-country/options"));
        Assert.Equal("login\n", await Out(sh, "ls /web/forms"));
        var tree = await Out(sh, "cat /web/tree.txt");
        Assert.Contains("heading \"Welcome to the shop\" [level=1]\n", tree);
        Assert.Contains("button \"Sign in\"  -> elements/button-sign-in\n", tree);
        Assert.Contains("Fresh fish every day.", await Out(sh, "cat /web/text.md"));
        Assert.Contains("http: 200\nstate: complete\nlast: ok\n", await Out(sh, "cat /web/status"));
        Assert.Contains("PNG image data", await Out(sh, "file /web/screenshot.png"));
    }

    [ChromiumFact]
    public async Task AClickNavigatesAndTheNextCommandSeesTheNewPage()
    {
        var site = Shop();
        var (sh, _) = await Open(site);
        Assert.Equal("https://app.test/next\nNext\nlink-home\n", await Out(sh, "echo 1 > /web/elements/link-next-page/click && cat /web/url /web/title && ls /web/elements"));
        Assert.Equal("https://app.test/\n", await Out(sh, "echo > /web/back; cat /web/url"));
        Assert.Equal("https://app.test/next\n", await Out(sh, "echo > /web/forward; cat /web/url"));
        int loads = site.Seen.Count(s => s == "GET https://app.test/next");
        Assert.Equal("https://app.test/next\n", await Out(sh, "echo > /web/reload; cat /web/url"));
        Assert.Equal(loads + 1, site.Seen.Count(s => s == "GET https://app.test/next"));
        Assert.Equal("https://app.test/\n", await Out(sh, "echo https://app.test/ > /web/url; cat /web/url"));
    }

    [ChromiumFact]
    public async Task ALoginFormFieldByField()
    {
        var (sh, _) = await Open(Shop());
        var e = "/web/elements";
        Assert.Equal("a@b.c\n********\n", await Out(sh, $"echo a@b.c > {e}/textbox-email/value && echo hunter2 > {e}/textbox-password/value && cat {e}/textbox-email/value {e}/textbox-password/value"));
        Assert.DoesNotContain("hunter2", await Out(sh, "cat /web/tree.txt /web/forms/login/fields.json"));
        Assert.Equal("1\nSweden\n", await Out(sh, $"echo 1 > {e}/checkbox-remember-me/checked; echo Sweden > {e}/combobox-country/value; cat {e}/checkbox-remember-me/checked {e}/combobox-country/value"));
        // the POST answers 303 with a cookie: the page follows it, and the cookie comes back
        var r = await Out(sh, $"echo 1 > {e}/button-sign-in/click && cat /web/url /web/title /web/text.md");
        Assert.StartsWith("https://app.test/account\nAccount\n", r);
        Assert.Contains("email=a%40b.c&password=hunter2&remember=on&country=Sweden", r);
        Assert.Contains("http: 200", await Out(sh, "cat /web/status"));
    }

    [ChromiumFact]
    public async Task ALoginFormThroughFieldsJson()
    {
        var (sh, _) = await Open(Shop());
        var fields = JsonNode.Parse(await Out(sh, "cat /web/forms/login/fields.json"))!.AsObject();
        Assert.Equal(["Email", "Password", "Remember me", "Country"], fields.Select(f => f.Key));
        Assert.Equal("Norway", fields["Country"]!.GetValue<string>());
        Assert.False(fields["Remember me"]!.GetValue<bool>());
        // a field the form doesn't have changes nothing
        var bad = await sh.RunAsync("echo '{\"Email\": \"x\", \"Phone\": \"1\"}' > /web/forms/login/fields.json");
        Assert.Contains("Phone: no such field (the fields are Email, Password, Remember me, Country)", bad.Stderr);
        // a required field left empty: the submit says so
        Assert.Contains("the form is not complete: Email: ", (await sh.RunAsync("echo > /web/forms/login/submit")).Stderr);
        var r = await Out(sh, """echo '{"Email": "q@r.s", "Password": "pw", "Remember me": true, "Country": "Sweden"}' > /web/forms/login/fields.json && echo > /web/forms/login/submit && cat /web/title /web/text.md""");
        Assert.StartsWith("Account\n", r);
        Assert.Contains("email=q%40r.s&password=pw&remember=on&country=Sweden", r);
    }

    [ChromiumFact]
    public async Task KeysCheckboxesAndSelects()
    {
        var site = new Site().Page("https://app.test/", """
            <title>Keys</title>
            <input aria-label="Search" onkeydown="if (event.key === 'Enter') document.title = 'searched ' + this.value">
            <div role="switch" aria-checked="false" aria-label="Dark mode" tabindex="0" style="width: 40px; height: 20px" onclick="this.setAttribute('aria-checked', this.getAttribute('aria-checked') === 'true' ? 'false' : 'true')"></div>
            <input type="range" aria-label="Volume" min="0" max="100" value="30" oninput="document.getElementById('v').textContent = this.value"><span id="v">30</span>
            """);
        var (sh, _) = await Open(site);
        var e = "/web/elements";
        Assert.Equal("searched trout\n", await Out(sh, $"echo trout > {e}/textbox-search/value && echo Enter > {e}/textbox-search/press && cat /web/title"));
        Assert.Equal("1\n", await Out(sh, $"cat {e}/textbox-search/focused"));
        Assert.Equal("0\n1\n", await Out(sh, $"cat {e}/switch-dark-mode/checked; echo 1 > {e}/switch-dark-mode/click; cat {e}/switch-dark-mode/checked"));
        Assert.Equal("70\n", await Out(sh, $"echo 70 > {e}/slider-volume/value; cat {e}/slider-volume/value"));
        Assert.Contains("Volume", await Out(sh, "cat /web/tree.txt"));
        Assert.Contains("ArrowNorth: not a key", (await sh.RunAsync($"printf 'Arrow North' > {e}/textbox-search/press")).Stderr.Replace("Arrow North", "ArrowNorth"));
    }

    [ChromiumFact]
    public async Task DialogsAreDismissedUnlessAccepted()
    {
        var site = new Site().Page("https://app.test/", """
            <title>Dialogs</title>
            <button onclick="document.getElementById('out').textContent = confirm('Delete the trout?') ? 'deleted' : 'kept'">Delete</button>
            <button onclick="document.getElementById('out').textContent = prompt('Your name?') || 'nobody'">Name</button>
            <p id="out">-</p>
            """);
        var (sh, _) = await Open(site);
        Assert.Equal("confirm: Delete the trout?\ndismissed\n", await Out(sh, "echo 1 > /web/elements/button-delete/click; cat /web/dialog"));
        Assert.Contains("kept", await Out(sh, "cat /web/text.md"));
        Assert.Equal("next: accept\n", await Out(sh, "echo accept > /web/dialog; cat /web/dialog | tail -1"));
        Assert.Contains("deleted", await Out(sh, "echo 1 > /web/elements/button-delete/click; cat /web/text.md"));
        Assert.Contains("Grant", await Out(sh, "echo 'accept Grant' > /web/dialog; echo 1 > /web/elements/button-name/click; cat /web/text.md"));
        Assert.Contains("write accept, accept TEXT", (await sh.RunAsync("echo maybe > /web/dialog")).Stderr);
    }

    [ChromiumFact]
    public async Task AnUploadSendsAFileFromTheMachine()
    {
        string? received = null;
        var site = new Site()
            .Page("https://app.test/", """
                <title>Upload</title>
                <form action="/upload" method="post" enctype="multipart/form-data" aria-label="Send">
                  <label for="cv">Resume</label><input id="cv" type="file" name="cv" style="display:none">
                  <button>Send</button>
                </form>
                """)
            .On("https://app.test/upload", async req =>
            {
                received = await req.Content!.ReadAsStringAsync();
                return Site.Html("<title>Got it</title>");
            });
        var (sh, _) = await Open(site);
        Assert.Contains("file-resume\n", await Out(sh, "ls /web/elements"));
        Assert.Equal("cv.txt\n", await Out(sh, "printf 'trout fisher' > /tmp/cv.txt && cp /tmp/cv.txt /web/elements/file-resume/upload/ && cat /web/forms/send/fields.json | grep -o 'cv.txt'"));
        Assert.Equal("Got it\n", await Out(sh, "echo 1 > /web/elements/button-send/click; cat /web/title"));
        Assert.Contains("filename=\"cv.txt\"", received);
        Assert.Contains("trout fisher", received);
    }

    [ChromiumFact]
    public async Task TheTreeIsReadAgainAfterEveryActionWithStableNames()
    {
        var site = new Site().Page("https://app.test/", """
            <title>List</title>
            <ul id="l"><li>a <button onclick="out.textContent='first'">Delete</button></li><li>b <button onclick="out.textContent='second'">Delete</button></li></ul>
            <button onclick="const b = document.createElement('button'); b.textContent = 'Added'; document.body.append(b)">Add</button>
            <p id="out">-</p>
            """);
        var (sh, _) = await Open(site);
        Assert.Equal("button-add\nbutton-delete\nbutton-delete (2)\n", await Out(sh, "ls /web/elements"));
        Assert.Equal("button-add\nbutton-added\nbutton-delete\nbutton-delete (2)\n", await Out(sh, "echo 1 > /web/elements/button-add/click && ls /web/elements"));
        Assert.Contains("second", await Out(sh, "echo 1 > '/web/elements/button-delete (2)/click' && cat /web/text.md"));
        Assert.Contains("first", await Out(sh, "echo 1 > /web/elements/button-delete/click && cat /web/text.md"));
        Assert.Equal("Delete\n", await Out(sh, "cat '/web/elements/button-delete (2)/name'"));
    }

    [ChromiumFact]
    public async Task ThePolicyRefusesWhatItDoesNotAllow()
    {
        var site = Shop()
            .Page("https://app.test/mixed", """
                <title>Mixed</title><img src="https://evil.test/pixel.png"><img src="https://cdn.test/ok.png">
                <a href="file:///etc/passwd">Secrets</a><a href="/away">Away</a>
                <script>try { new WebSocket('wss://app.test/live') } catch (e) {}</script>
                """)
            .On("https://app.test/away", _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://evil.test/landing");
                return Task.FromResult(r);
            });
        var (sh, _) = await Open(site);
        var r = await sh.RunAsync("echo https://evil.test/ > /web/url");
        Assert.Contains("mount: /web/url: Blocked by the web policy: evil.test is not an allowed host (allowed: app.test, cdn.test)", r.Stderr);
        r = await sh.RunAsync("echo https://169.254.169.254/latest/meta-data/ > /web/url");
        Assert.Contains("mount: /web/url: Blocked by the web policy: 169.254.169.254 is a private or reserved address", r.Stderr);
        r = await sh.RunAsync("echo file:///etc/passwd > /web/url");
        Assert.Contains("mount: /web/url: only http and https may be loaded (file:)", r.Stderr);
        Assert.Equal("https://app.test/mixed\n", await Out(sh, "echo https://app.test/mixed > /web/url; cat /web/url"));
        var status = await Out(sh, "cat /web/status");
        Assert.Contains("refused: https://evil.test/pixel.png: Blocked by the web policy: evil.test is not an allowed host", status);
        Assert.Contains("refused: wss://app.test/live: WebSockets are not offered to the page", status);
        // Chromium itself won't open a local file from a web page; were it asked to, the route would refuse the file: scheme
        Assert.Equal("https://app.test/mixed\n", (await sh.RunAsync("echo 1 > /web/elements/link-secrets/click; cat /web/url")).Stdout);
        Assert.Contains("evil.test is not an allowed host", (await sh.RunAsync("echo https://app.test/mixed > /web/url; echo 1 > /web/elements/link-away/click; cat /web/status")).Stdout);
        Assert.Contains(site.Seen, s => s == "GET https://cdn.test/ok.png");
        Assert.DoesNotContain(site.Seen, s => s.Contains("evil.test") || s.Contains("169.254") || s.Contains("file:"));
    }

    [ChromiumFact]
    public async Task ReadOnlyFilesRefuseWrites()
    {
        var (sh, _) = await Open(Shop());
        var r = await sh.RunAsync("echo x > /web/title; echo x > /web/elements/link-next-page/role; cat /web/title");
        Assert.Contains("mount: /web/title: title can't be written; the files to write here are url, back, forward, reload, dialog", r.Stderr);
        Assert.Contains("mount: /web/elements/link-next-page/role: role can't be written; the files to write here are click, press", r.Stderr);
        Assert.Equal("Shop\n", r.Stdout); // the file shows the page again
        Assert.Contains("mount: /web/elements/button-sign-in/value: no such file on the page", (await sh.RunAsync("echo 1 > /web/elements/button-sign-in/value")).Stderr);
        Assert.Contains("Denmark: not one of the options (Norway, Sweden)", (await sh.RunAsync("echo Denmark > /web/elements/combobox-country/value")).Stderr);
    }

    [ChromiumFact]
    public async Task HugePagesAreBounded()
    {
        var big = new StringBuilder("<title>Big</title>");
        for (int i = 0; i < 3000; i++) big.Append($"<p>Paragraph {i} about rivers and fish.</p><button>Item {i}</button>");
        var (sh, _) = await Open(new Site().Page("https://app.test/", big.ToString()), options: Options(new Site().Page("https://app.test/", big.ToString()), maxElements: 50));
        Assert.Equal("50\n", await Out(sh, "ls /web/elements | wc -l"));
        var tree = await Out(sh, "cat /web/tree.txt");
        Assert.Contains("[2950 more interactive elements are not under elements/]", tree);
        var text = await Out(sh, "wc -c < /web/text.md");
        Assert.True(int.Parse(text.Trim()) <= 50_100, text);
        Assert.Contains("[the rest of the page is left out]", await Out(sh, "tail -1 /web/text.md"));
    }

    [ChromiumFact]
    public async Task APageThatNeverAnswersCannotHoldTheCall()
    {
        var site = Shop().On("https://app.test/slow", async _ => { await Task.Delay(TimeSpan.FromSeconds(60)); return Site.Html("late"); });
        var (sh, _) = await Open(site, shell: new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(3) });
        var clock = Stopwatch.StartNew();
        var r = await sh.RunAsync("echo https://app.test/slow > /web/url");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), clock.Elapsed.ToString());
        Assert.NotEqual(0, r.ExitCode);
        await Task.Delay(TimeSpan.FromSeconds(3)); // the navigation's own timeout ends it
        var after = await sh.RunAsync("echo https://app.test/ > /web/url; cat /web/title");
        Assert.True(after.Stdout.Contains("Shop"), after.Stderr + after.Stdout + after.ExitCode);
    }

    [ChromiumFact]
    public async Task APageWhoseScriptNeverYieldsCannotHoldEveryCall()
    {
        var site = new Site().Page("https://app.test/", "<title>Spin</title><script>setTimeout(() => { for (;;) {} }, 1000)</script>");
        var options = Options(site);
        options = new() { Policy = options.Policy, ActionTimeout = TimeSpan.FromSeconds(1), SettleTimeout = TimeSpan.FromSeconds(1) };
        var (sh, _) = await Open(site, options: options, shell: new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(20) });
        await Task.Delay(1500);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 3; i++)
        {
            var r = await sh.RunAsync("echo hi");
            Assert.True(r.ExitCode == 0 && r.Stdout == "hi\n", $"{r.ExitCode} {r.Stdout} {r.Stderr} after {clock.Elapsed}");
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
    }

    [ChromiumFact]
    public async Task WhatThePageFetchedIsNotKept()
    {
        var images = string.Concat(Enumerable.Range(0, 60).Select(i => $"<img src=\"/i{i}.png\">"));
        var site = new Site().Page("https://app.test/", "<title>Start</title>").Page("https://app.test/gallery", $"<title>Gallery</title>{images}");
        for (int i = 0; i < 60; i++)
            site.On($"https://app.test/i{i}.png", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2_000_000]) }));
        var (sh, _) = await Open(site);
        long before = GC.GetTotalMemory(forceFullCollection: true);
        Assert.Equal("Gallery\n", await Out(sh, "echo https://app.test/gallery > /web/url; cat /web/title"));
        Assert.Equal(61, site.Seen.Count(s => s.StartsWith("GET https://app.test/", StringComparison.Ordinal) && s != "GET https://app.test/"));
        long kept = GC.GetTotalMemory(forceFullCollection: true) - before;
        // 120 MB of images went by; what stays (about 50 MB, the same for 30 images) doesn't grow with them
        Assert.True(kept < 100_000_000, $"{kept / 1_000_000} MB kept");
    }

    [ChromiumFact]
    public async Task APageCannotLiftTheLimitsByReplacingBuiltIns()
    {
        // the source's scripts run beside the page's, which can replace what they call
        var site = new Site().Page("https://app.test/", """
            <title>Tamper</title><form aria-label="F"><textarea aria-label="Note"></textarea></form>
            <script>
            document.querySelector('textarea').value = 'x'.repeat(200000);
            String.prototype.slice = function () { return String(this) };
            </script>
            """);
        var options = Options(site);
        options = new() { Policy = options.Policy, MaxValueLength = 1000, MaxTextBytes = 50_000 };
        var (sh, _) = await Open(site, options: options);
        Assert.Equal("1001\n", await Out(sh, "wc -c < /web/elements/textbox-note/value")); // and a newline
        Assert.True(int.Parse(await Out(sh, "wc -c < /web/forms/f/fields.json")) < 1100);
    }

    [ChromiumFact]
    public async Task ANewWindowIsShownInThePageAndDownloadsAreBlocked()
    {
        var site = new Site()
            .Page("https://app.test/", "<title>Links</title><a href=\"/other\" target=\"_blank\">Other</a><a href=\"/file.zip\">Get</a>")
            .Page("https://app.test/other", "<title>Other</title>")
            .On("https://app.test/file.zip", _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                r.Content.Headers.ContentType = new("application/zip");
                r.Content.Headers.ContentDisposition = new("attachment") { FileName = "file.zip" };
                return Task.FromResult(r);
            });
        var (sh, _) = await Open(site);
        Assert.Contains("a download was blocked: file.zip", await Out(sh, "echo 1 > /web/elements/link-get/click; cat /web/status"));
        Assert.Equal("Other\n", await Out(sh, "echo 1 > /web/elements/link-other/click; cat /web/title"));
    }

    [ChromiumFact]
    public async Task APageThatKeepsOpeningWindowsKeepsAtMostOne()
    {
        var site = new Site()
            .Page("https://app.test/", "<title>Flood</title><button>Go</button><script>setInterval(() => { for (let i = 0; i < 20; i++) { const w = window.open(''); w && w.setInterval(() => w.open(''), 20) } }, 50)</script>");
        var (sh, source) = await Open(site);
        await Task.Delay(2000);
        Assert.InRange(source.Page.Context.Pages.Count, 1, 3);
        await sh.RunAsync("echo 1 > /web/elements/button-go/click");
        Assert.InRange(source.Page.Context.Pages.Count, 1, 3);
    }

    [ChromiumFact]
    public async Task LaunchOpensAPageItOwns()
    {
        var site = Shop();
        await using var source = await BrowserSource.LaunchAsync(Options(site), "https://app.test/");
        var sh = new Shell();
        await sh.MountAsync("/web", source, MountMode.Live);
        Assert.Equal("Shop\n", await Out(sh, "cat /web/title"));
        Assert.Equal("browser:page on /web type browser (rw,live,no-delete,no-rename,no-mkdir)\n", (await sh.RunAsync("mount")).Stdout.Split('\n')[0] + "\n");
    }
}
