// A live web page as a mount source (docs/browser.md). Microsoft Playwright drives a browser the host owns; every request the
// page makes is fetched by Topwater under the host's web policy (the browser itself reaches nothing), and the page shows as a
// sysfs-like tree: files to read (url, title, text.md, tree.txt, an element's value) and files whose write is an action (click,
// value, submit). The tree changes after every action, so the source is volatile: the mount lists it again after each write.
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Topwater.Connectors;

namespace Topwater.Browser;

/// <summary>How a <see cref="BrowserSource"/> reaches the web and how much of a page it shows.</summary>
[Experimental("TW002")]
public sealed class BrowserSourceOptions
{
    /// <summary>The sites the page may load anything from, and the limits on its requests (docs/web.md). Required. Forms post:
    /// add "POST" to <see cref="WebPolicy.Methods"/> for them. <see cref="WebPolicy.MaxRequestsPerCall"/> counts the requests of one
    /// action (a navigation, a click, ...) and what the page fetches until the next one.</summary>
    public required WebPolicy Policy { get; init; }
    /// <summary>Your name for the mount in snapshots; never a secret.</summary>
    public string Id { get; init; } = "page";
    /// <summary>The time one browser operation (a click, a navigation, a screenshot) may take; the call's own limit still applies.</summary>
    public TimeSpan ActionTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>After an action, how long to wait for the page's requests to stop (a navigation it started may take up to
    /// <see cref="ActionTimeout"/>).</summary>
    public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Interactive elements shown under elements/; the rest are only in tree.txt.</summary>
    public int MaxElements { get; init; } = 300;
    /// <summary>Bytes of text.md and of tree.txt.</summary>
    public int MaxTextBytes { get; init; } = 200_000;
    /// <summary>Bytes of screenshot.png.</summary>
    public int MaxScreenshotBytes { get; init; } = 5_000_000;
    /// <summary>Characters of a value read or written.</summary>
    public int MaxValueLength { get; init; } = 100_000;
    /// <summary>Bytes of a file given to an upload field.</summary>
    public int MaxUploadBytes { get; init; } = 1_000_000;
}

/// <summary>
/// A web page as a mount source (docs/browser.md). The host launches the browser and owns it; the source routes every request
/// of the page's browser context through Topwater's <see cref="WebPolicy"/> and shows the page as files. Mount it live:
/// <code>
/// await using var page = await BrowserSource.LaunchAsync(new() { Policy = new() { Domains = ["example.com"] } }, "https://example.com/");
/// await shell.MountAsync("/web", page, MountMode.Live);
/// </code>
/// </summary>
[Experimental("TW002")]
public sealed partial class BrowserSource : IMountSource, IAsyncDisposable
{
    /// <summary>Chromium flags that leave the browser no network of its own: every name fails to resolve and anything not
    /// routed through the source meets a proxy that isn't there (service workers, WebRTC, prefetching). <see cref="LaunchAsync"/>
    /// uses them; pass them when you launch the browser yourself.</summary>
    public static IReadOnlyList<string> LaunchArgs { get; } =
    [
        "--host-resolver-rules=MAP * ~NOTFOUND", "--proxy-server=socks5://127.0.0.1:9", "--proxy-bypass-list=<-loopback>",
        "--force-webrtc-ip-handling-policy=disable_non_proxied_udp", "--no-pings",
    ];

    const int ReadMs = 2000; // a read waits this long for its element
    const int MaxHtmlChars = 5_000_000; // of the page's HTML turned into text.md
    const int MaxFieldsJson = 1_000_000;
    const int MaxKept = 20; // refused requests and notes kept for status
    const int MaxInputs = 50; // file and password fields looked at
    static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    static readonly string[] RootFiles = ["url", "title", "status", "text.md", "tree.txt", "screenshot.png", "dialog", "back", "forward", "reload"];
    static readonly string[] RootWritable = ["url", "back", "forward", "reload", "dialog"];
    static readonly HashSet<string> DroppedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "accept-encoding", "if-none-match", "if-modified-since", "if-range", "if-match", "if-unmodified-since" };
    static readonly HashSet<string> DroppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "content-length", "transfer-encoding", "connection", "keep-alive", "content-encoding" };

    // What the agent sees (the mount puts "mount: <machine path>: " before a write's error).
    internal static readonly Dictionary<string, string> Messages = new()
    {
        ["noFile"] = "no such file on the page",
        ["readOnly"] = "{file} can't be written; the files to write here are {writable}",
        ["gone"] = "the element is no longer on the page",
        ["disabled"] = "the element is disabled",
        ["fullUrl"] = "give a full http:// or https:// URL",
        ["timedOut"] = "the page did not answer in time",
        ["loadFailed"] = "the page could not be loaded ({error})",
        ["browserFailed"] = "the browser could not do it",
        ["noBack"] = "there is no earlier page",
        ["noForward"] = "there is no later page",
        ["dialogWord"] = "write accept, accept TEXT (the answer to a prompt) or dismiss",
        ["noOption"] = "{value}: not one of the options ({options})",
        ["useUpload"] = "a file field takes a file: cp FILE elements/{dir}/upload/",
        ["noText"] = "this element takes no text; click it, or write keys to press",
        ["tooLong"] = "longer than {limit} characters",
        ["uploadTooLarge"] = "the file is larger than {limit} bytes",
        ["key"] = "{key}: not a key (Enter, Tab, ArrowDown, Control+A, a)",
        ["checkedWord"] = "write 1 or 0",
        ["fieldsJson"] = "fields.json takes a JSON object of field names and values",
        ["noField"] = "{field}: no such field (the fields are {fields})",
        ["fieldBool"] = "{field}: a checkbox or radio button takes true or false",
        ["incomplete"] = "the form is not complete: {problems}",
        ["tooLarge"] = "too large to show",
        ["responseTooLarge"] = "the response is larger than the web policy allows",
        ["responseCut"] = "the response ended early",
        ["encoding"] = "the response is compressed in a way Topwater doesn't read ({encoding})",
        ["scheme"] = "only http and https may be loaded ({scheme}:)",
        ["badUrl"] = "not a valid URL",
        ["resolve"] = "could not resolve the host",
        ["connect"] = "could not connect",
        ["requestTimedOut"] = "the request timed out",
        ["tls"] = "the TLS handshake failed",
        ["badHeader"] = "the request's headers are malformed or too large",
        ["failed"] = "the request failed",
        ["websocket"] = "WebSockets are not offered to the page",
        ["download"] = "a download was blocked: {name}",
        ["popup"] = "the page opened a new window ({url}); it is shown here instead",
        ["loading"] = "the page was still loading when the action ended",
        ["busy"] = "the page is not responding",
    };

    static string Msg(string key, params (string, object)[] values) => ConnectorCommon.Fill(Messages[key], values);

    readonly IPage _page;
    readonly IBrowserContext _context;
    readonly BrowserSourceOptions _options;
    readonly WebSession _web;
    readonly Func<IRoute, Task> _route;
    readonly Task _ready;
    readonly SemaphoreSlim _gate = new(1, 1); // one action or snapshot at a time, until its browser call really ends
    readonly CancellationTokenSource _life = new();
    readonly object _lock = new();
    PageTree _tree = PageTree.Empty;
    IPlaywright? _playwright;
    IBrowser? _browser;
    // what happened since the last action, for status and dialog (under _lock)
    readonly List<(string Url, string Why)> _refused = [];
    readonly List<string> _notes = [];
    string? _lastError, _navRefused, _dialog, _answer;
    int? _status;
    IPage? _popup;
    int _inflight, _epoch; // requests in flight that the current action started (or the page fetched since)
    long _activity = DateTime.UtcNow.Ticks;
    long _heldUntil; // when the operation holding the gate is past its limit
    bool _navPending;

    /// <summary>Shows <paramref name="page"/>, a page of a browser you launched (pass <see cref="LaunchArgs"/>, and create its
    /// context with downloads off and service workers blocked). Every request of the page's browser context goes through
    /// <see cref="BrowserSourceOptions.Policy"/> from now on: create the source before the page loads anything.</summary>
    public BrowserSource(IPage page, BrowserSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(options);
        _page = page;
        _context = page.Context;
        _options = options;
        _web = new WebSession(options.Policy); // checks the policy
        _route = RouteAsync;
        _page.Dialog += (_, d) => _ = AnswerAsync(d);
        _page.Download += (_, d) =>
        {
            Note(Msg("download", ("name", Cut(d.SuggestedFilename, 200))));
            _ = d.CancelAsync().ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        };
        _page.Popup += (_, p) => OnPopup(p);
        _page.Load += (_, _) => _navPending = false;
        _ready = InstallAsync();
    }

    /// <summary>Keeps only the newest window the page (or a window it opened) opened: a page that opens windows in a loop
    /// would otherwise fill the host's memory with them.</summary>
    void OnPopup(IPage p)
    {
        p.Popup += (_, q) => OnPopup(q);
        IPage? old;
        lock (_lock) { old = _popup; _popup = p; }
        if (old != null) _ = old.CloseAsync().ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }

    async Task InstallAsync()
    {
        await _context.RouteAsync("**/*", _route);
        await _context.RouteWebSocketAsync("**", ws =>
        {
            Refused(ws.Url, Msg("websocket"));
            _ = ws.CloseAsync(new() { Code = 1008, Reason = "blocked" }).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        });
    }

    /// <summary>Launches headless Chromium with <see cref="LaunchArgs"/> (downloads off, service workers blocked), opens a page
    /// and, given <paramref name="url"/>, loads it. The source owns that browser and closes it when disposed.</summary>
    public static Task<BrowserSource> LaunchAsync(BrowserSourceOptions options, string? url = null, CancellationToken cancellationToken = default) => OffContext.Run(async () =>
    {
        var playwright = await Playwright.CreateAsync();
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new() { Args = [.. LaunchArgs] });
            var context = await browser.NewContextAsync(new()
            {
                AcceptDownloads = false, ServiceWorkers = ServiceWorkerPolicy.Block, ViewportSize = new() { Width = 1280, Height = 800 },
            });
            var source = new BrowserSource(await context.NewPageAsync(), options) { _playwright = playwright, _browser = browser };
            await source._ready.WaitAsync(cancellationToken);
            if (url != null) await source.OpenAsync(url, cancellationToken);
            return source;
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    });

    /// <summary>Loads <paramref name="url"/> (under the policy), as writing it to url does. For the host: a start page.</summary>
    public Task OpenAsync(string url, CancellationToken cancellationToken = default) => OffContext.Run(async () =>
    {
        await _ready.WaitAsync(cancellationToken);
        await ActAsync(() => GoAsync(url), cancellationToken);
    });

    /// <summary>The page, for the host (never shown to the agent).</summary>
    public IPage Page => _page;

    public string Type => "browser";
    public string Id => _options.Id;
    public MountCapabilities Capabilities => MountCapabilities.Write;
    public bool Volatile => true;

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        try
        {
            if (_browser != null) await _browser.CloseAsync();
            else await _context.UnrouteAsync("**/*", _route);
        }
        catch (PlaywrightException) { }
        _playwright?.Dispose();
    }

    // ---------- the tree ----------

    /// <summary>The entries of a directory of the tree (name, is a directory), or null when there is no such directory.</summary>
    static List<(string Name, bool Dir)>? Children(string path, PageTree t)
    {
        if (path == "/") return [.. RootFiles.Select(n => (n, false)), ("elements", true), ("forms", true)];
        if (path == "/elements") return [.. t.Elements.Select(e => (e.Dir, true))];
        if (path == "/forms") return [.. t.Forms.Select(f => (f, true))];
        var parts = path.Split('/');
        if (parts.Length >= 3 && parts[1] == "elements" && t.ByDir.TryGetValue(parts[2], out var e))
            return parts.Length == 3 ? [.. ElementFiles(e).Select(f => (f, f == "upload"))] : parts.Length == 4 && parts[3] == "upload" && e.File ? [] : null;
        if (parts.Length == 3 && parts[1] == "forms" && t.Forms.Contains(parts[2])) return [("fields.json", false), ("submit", false)];
        return null;
    }

    static IEnumerable<string> ElementFiles(PageElement e)
    {
        if (e.FileIndex != null) { yield return "role"; yield return "name"; yield return "upload"; yield break; }
        foreach (var f in new[] { "role", "name", "description", "disabled", "focused", "click", "press" }) yield return f;
        if (PageTree.HasValue.Contains(e.Role)) yield return "value";
        if (PageTree.HasOptions.Contains(e.Role)) yield return "options";
        if (PageTree.HasChecked.Contains(e.Role)) yield return "checked";
        if (e.Role == "link") yield return "href";
    }

    static readonly HashSet<string> ElementWritable = ["click", "press", "value", "checked"];

    public async Task<IReadOnlyList<MountEntry>> ListAsync(string path, int maxEntries, CancellationToken cancellationToken)
    {
        await _ready.WaitAsync(cancellationToken);
        if (path == "/") _tree = await GatedAsync(SnapshotAsync, 2 * _options.ActionTimeout, cancellationToken); // a listing from the top is a new look at the page
        var kids = Children(path, _tree) ?? throw new MountSourceException(Msg("noFile"));
        return [.. kids.Take(maxEntries).Select(k => new MountEntry(k.Name, k.Dir, k.Dir ? 0 : null))];
    }

    public Task<MountEntry?> StatAsync(string path, CancellationToken cancellationToken)
    {
        if (path == "/") return Task.FromResult<MountEntry?>(new MountEntry("", true));
        int slash = path.LastIndexOf('/');
        var name = path[(slash + 1)..];
        var hit = Children(slash == 0 ? "/" : path[..slash], _tree)?.FirstOrDefault(k => k.Name == name);
        return Task.FromResult(hit is { Name: not null } k ? new MountEntry(name, k.Dir, k.Dir ? 0 : null) : null);
    }

    /// <summary>Reads the page as it is now: the accessibility snapshot, its elements, the file fields and the forms.</summary>
    async Task<PageTree> SnapshotAsync()
    {
        string yaml = "";
        for (int tries = 0; tries < 3; tries++)
        {
            try
            {
                var body = _page.Locator("body");
                if (await body.CountAsync() > 0) yaml = await body.AriaSnapshotAsync(new() { Timeout = Ms(_options.ActionTimeout) });
                break;
            }
            catch (Exception e) when (e is PlaywrightException or TimeoutException && tries < 2) { await Task.Delay(200); } // between two documents
            catch (Exception e) when (e is PlaywrightException or TimeoutException) { Note(Msg("loading")); }
        }
        List<string> forms = [], files = [];
        try
        {
            forms = [.. (await _page.Locator("form").EvaluateAllAsync<string[]>(FormNamesJs, MaxInputs)).Select(n => PageTree.Slug(n) is { Length: > 0 } s ? s : "form")];
            files = [.. await _page.Locator("input[type=file]").EvaluateAllAsync<string[]>(FileNamesJs, MaxInputs)];
        }
        catch (PlaywrightException) { }
        var tree = new PageTree(PageTree.Parse(yaml), _options.MaxElements) { Forms = ConnectorCommon.Unique(forms) };
        tree.AddFiles(files);
        await MarkPasswordsAsync(tree);
        return tree;
    }

    const string FormNamesJs = """
        (fs, max) => fs.slice(0, max).map(f => (f.getAttribute('aria-label')
          || (f.getAttribute('aria-labelledby') || '').split(/\s+/).map(i => (document.getElementById(i) || {}).innerText || '').join(' ').trim()
          || f.getAttribute('name') || f.id || '').slice(0, 200))
        """;

    // the text of a field's label without the text of controls inside it (a select's options)
    const string LabelOf = """
        const labelOf = e => { const l = e.labels && e.labels[0]; if (!l) return ''; const c = l.cloneNode(true);
          c.querySelectorAll('input,select,textarea,button').forEach(x => x.remove()); return c.textContent.trim().replace(/\s+/g, ' '); };
        """;

    const string FileNamesJs = "(els, max) => { " + LabelOf + """
        return els.slice(0, max).map(e => (labelOf(e) || e.getAttribute('aria-label') || e.name || e.id || '').trim().slice(0, 200)); }
        """;

    /// <summary>Marks the elements that are password fields, whose values are never shown.</summary>
    async Task MarkPasswordsAsync(PageTree tree)
    {
        try
        {
            var fields = _page.Locator("input[type=password]");
            int n = Math.Min(await fields.CountAsync(), MaxInputs);
            for (int i = 0; i < n; i++)
            {
                var node = PageTree.Parse(await fields.Nth(i).AriaSnapshotAsync(new() { Timeout = ReadMs })).FirstOrDefault();
                if (node == null) continue; // hidden
                var same = tree.Elements.Where(e => e.Role == node.Role && e.Name == (node.Name ?? "")).ToList();
                if (same.Count == 1) { same[0].Password = true; continue; }
                var types = await ByRole(node.Role, node.Name ?? "").EvaluateAllAsync<string[]>("els => els.map(e => (e.type || '').toLowerCase())");
                foreach (var e in same) if (e.Nth < types.Length && types[e.Nth] == "password") e.Password = true;
            }
        }
        catch (Exception e) when (e is PlaywrightException or TimeoutException) { }
    }

    ILocator ByRole(string role, string name) =>
        _page.GetByRole(Enum.Parse<AriaRole>(role, ignoreCase: true), new() { Name = name, Exact = true });

    ILocator Locate(PageElement e) => e.FileIndex is int i ? _page.Locator("input[type=file]").Nth(i) : ByRole(e.Role, e.Name).Nth(e.Nth);

    async Task<ILocator> PresentAsync(PageElement e)
    {
        var loc = Locate(e);
        if (await loc.CountAsync() == 0) throw new MountSourceException(Msg("gone"));
        return loc;
    }

    ILocator Form(int index) => _page.Locator("form").Nth(index);

    // ---------- reading ----------

    public async Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        await _ready.WaitAsync(cancellationToken);
        byte[] data;
        try { data = await ReadFileAsync(path, _tree).WaitAsync(cancellationToken); }
        catch (Exception e) when (e is PlaywrightException or System.TimeoutException) { throw new MountSourceException(Safe(e)); }
        return data.Length > maxBytes ? throw new MountSourceException(Msg("tooLarge")) : data;
    }

    static byte[] Text(string s) => Encoding.UTF8.GetBytes(s.Length == 0 || s.EndsWith('\n') ? s : s + "\n");

    async Task<byte[]> ReadFileAsync(string path, PageTree t)
    {
        switch (path)
        {
            case "/url": return Text(_page.Url);
            case "/title": return Text(Cut(await _page.TitleAsync().WaitAsync(TimeSpan.FromMilliseconds(ReadMs)), PageTree.MaxName));
            case "/status": return Text(await StatusAsync());
            case "/text.md": return Text(await MarkdownAsync());
            case "/tree.txt": return Text(t.Render(_options.MaxTextBytes));
            case "/screenshot.png":
                var png = await _page.ScreenshotAsync(new() { Type = ScreenshotType.Png, Timeout = Ms(_options.ActionTimeout), Animations = ScreenshotAnimations.Disabled });
                return png.Length > _options.MaxScreenshotBytes ? throw new MountSourceException(Msg("tooLarge")) : png;
            case "/dialog":
                lock (_lock) return Text((_dialog ?? "") + (_answer != null ? $"next: {_answer}\n" : ""));
            case "/back" or "/forward" or "/reload": return [];
        }
        var parts = path.Split('/');
        if (parts.Length == 4 && parts[1] == "elements" && t.ByDir.TryGetValue(parts[2], out var e) && ElementFiles(e).Contains(parts[3]))
            return Text(await ElementFileAsync(e, parts[3]));
        if (parts.Length == 4 && parts[1] == "forms" && t.Forms.IndexOf(parts[2]) is >= 0 and var form)
        {
            if (parts[3] == "submit") return [];
            if (parts[3] == "fields.json")
            {
                var o = new JsonObject();
                foreach (var f in await FieldsAsync(form)) o[f.Key] = f.Value.ValueKind == JsonValueKind.True ? true : f.Value.ValueKind == JsonValueKind.False ? false : f.Value.ToString();
                return Text(o.ToJsonString(new JsonSerializerOptions(PageTree.Json.Options) { WriteIndented = true }));
            }
        }
        throw new MountSourceException(Msg("noFile"));
    }

    async Task<string> ElementFileAsync(PageElement e, string file)
    {
        switch (file)
        {
            case "role": return e.Role;
            case "name": return e.Name;
            case "click" or "press" or "upload": return "";
            case "disabled": return await Locate(e).IsDisabledAsync(new() { Timeout = ReadMs }) ? "1" : "0";
        }
        var info = await Locate(e).EvaluateAsync<JsonElement>(InfoJs, _options.MaxValueLength, new() { Timeout = ReadMs });
        return file switch
        {
            "options" => string.Join("\n", info.GetProperty("options") is { ValueKind: JsonValueKind.Array } a
                ? a.EnumerateArray().Select(x => x.GetString() ?? "") : _tree.OptionsOf(e.Node)),
            "checked" => info.GetProperty("checked").GetBoolean() ? "1" : "0",
            "focused" => info.GetProperty("focused").GetBoolean() ? "1" : "0",
            "value" => (e.Password && info.GetProperty("value").GetString() is { Length: > 0 }) ? "********" : info.GetProperty("value").GetString() ?? "",
            "href" => info.GetProperty("href").GetString() ?? "",
            _ => info.GetProperty("description").GetString() ?? "",
        };
    }

    // what an element says about itself, in one call
    const string InfoJs = """
        (e, max) => {
          const t = (e.type || '').toLowerCase(), role = e.getAttribute('role');
          let value;
          if (e.tagName === 'SELECT') value = Array.from(e.selectedOptions).map(o => o.label).join('\n');
          else if (t === 'password') value = e.value ? '********' : '';
          else if (t === 'file') value = Array.from(e.files || []).map(f => f.name).join('\n');
          else if (e.tagName === 'INPUT' || e.tagName === 'TEXTAREA') value = String(e.value);
          else if (role === 'listbox') value = Array.from(e.querySelectorAll('[aria-selected=true]')).map(o => o.textContent.trim()).join('\n');
          else if (e.isContentEditable) value = e.innerText;
          else value = e.getAttribute('aria-valuetext') ?? e.getAttribute('aria-valuenow') ?? (e.textContent || '').trim();
          let description = e.getAttribute('aria-description') || '';
          const ids = e.getAttribute('aria-describedby');
          if (!description && ids) description = ids.split(/\s+/).map(i => (document.getElementById(i) || {}).innerText || '').join(' ').trim();
          if (!description) description = e.title || '';
          const checked = (t === 'checkbox' || t === 'radio') ? e.checked : e.getAttribute('aria-checked') === 'true';
          const options = e.tagName === 'SELECT' ? Array.from(e.options).slice(0, 1000).map(o => o.label) : null;
          return { value: value.slice(0, max), description: description.slice(0, 1000), checked, focused: e === document.activeElement,
                   href: e.href ? String(e.href) : (e.getAttribute('href') || ''), options };
        }
        """;

    async Task<string> StatusAsync()
    {
        string state;
        try { state = await _page.EvaluateAsync<string>("() => document.readyState").WaitAsync(TimeSpan.FromMilliseconds(ReadMs)); }
        catch (Exception e) when (e is PlaywrightException or System.TimeoutException) { state = "busy"; }
        var sb = new StringBuilder();
        lock (_lock)
        {
            sb.Append("http: ").Append(_status?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('\n');
            sb.Append("state: ").Append(state).Append('\n');
            sb.Append("last: ").Append(_lastError ?? "ok").Append('\n');
            foreach (var n in _notes) sb.Append("note: ").Append(n).Append('\n');
            foreach (var (url, why) in _refused) sb.Append("refused: ").Append(url).Append(": ").Append(why).Append('\n');
        }
        return sb.ToString();
    }

    async Task<string> MarkdownAsync()
    {
        var html = await _page.EvaluateAsync<string>("m => document.documentElement ? document.documentElement.outerHTML.slice(0, m) : ''", MaxHtmlChars)
            .WaitAsync(_options.ActionTimeout);
        string md;
        try { md = MarkdownConverter.ToMarkdown(Encoding.UTF8.GetBytes(html), "page.html"); }
        catch (ShellException) { md = ""; }
        var bytes = Encoding.UTF8.GetBytes(md);
        if (bytes.Length <= _options.MaxTextBytes) return md;
        int n = _options.MaxTextBytes;
        while (n > 0 && (bytes[n] & 0xC0) == 0x80) n--;
        return Encoding.UTF8.GetString(bytes, 0, n) + "\n[the rest of the page is left out]\n";
    }

    /// <summary>A form's fields as the page shows them: the label (made unique), the index among the form's inputs, the type, the
    /// value (a password's hidden).</summary>
    async Task<List<(string Key, int Index, string Type, JsonElement Value)>> FieldsAsync(int form)
    {
        var raw = await Form(form).EvaluateAsync<JsonElement>(FieldsJs, _options.MaxValueLength, new() { Timeout = ReadMs });
        var items = raw.EnumerateArray().ToList();
        var keys = ConnectorCommon.Unique(items.Select(i => i.GetProperty("label").GetString() is { Length: > 0 } l ? l : "field"));
        return [.. items.Select((i, k) => (keys[k], i.GetProperty("k").GetInt32(), i.GetProperty("type").GetString() ?? "", i.GetProperty("v")))];
    }

    const string FieldsJs = "(f, max) => { " + LabelOf + """
          const out = [];
          Array.from(f.querySelectorAll('input,select,textarea')).forEach((e, k) => {
            const t = e.tagName === 'SELECT' ? 'select' : e.tagName === 'TEXTAREA' ? 'textarea' : (e.type || 'text').toLowerCase();
            if (['hidden', 'submit', 'button', 'reset', 'image'].includes(t) || out.length >= 500) return;
            const label = (labelOf(e) || e.getAttribute('aria-label') || e.placeholder || e.name || e.id || t);
            let v;
            if (t === 'checkbox' || t === 'radio') v = e.checked;
            else if (t === 'select') v = Array.from(e.selectedOptions).map(o => o.label).join('\n');
            else if (t === 'file') v = Array.from(e.files || []).map(x => x.name).join('\n');
            else if (t === 'password') v = e.value ? '********' : '';
            else v = String(e.value).slice(0, max);
            out.push({ k, label: label.trim().replace(/\s+/g, ' ').slice(0, 200), type: t, v });
          });
          return out;
        }
        """;

    // ---------- writing: actions ----------

    public async Task<string> WriteAsync(string path, byte[] data, string? expectedVersion, CancellationToken cancellationToken)
    {
        await _ready.WaitAsync(cancellationToken);
        var t = _tree;
        var parts = path.Split('/');
        var text = Encoding.UTF8.GetString(data);
        var line = text.TrimEnd('\n', '\r');
        Func<Task>? action = null;
        string? dir = null;
        string[] writable = [];
        if (parts.Length == 2)
        {
            (dir, writable) = ("/", RootWritable);
            action = parts[1] switch
            {
                "url" => () => GoAsync(line),
                "back" => async () => { if (await _page.GoBackAsync(new() { WaitUntil = WaitUntilState.Commit, Timeout = Ms(_options.ActionTimeout) }) == null) Note(Msg("noBack")); },
                "forward" => async () => { if (await _page.GoForwardAsync(new() { WaitUntil = WaitUntilState.Commit, Timeout = Ms(_options.ActionTimeout) }) == null) Note(Msg("noForward")); },
                "reload" => () => _page.ReloadAsync(new() { WaitUntil = WaitUntilState.Commit, Timeout = Ms(_options.ActionTimeout) }),
                _ => null,
            };
            if (parts[1] == "dialog")
            {
                if (line is not ("accept" or "dismiss") && !line.StartsWith("accept ", StringComparison.Ordinal)) throw new MountSourceException(Msg("dialogWord"));
                lock (_lock) _answer = Cut(line, _options.MaxValueLength);
                return "";
            }
        }
        else if (parts.Length >= 4 && parts[1] == "elements" && t.ByDir.TryGetValue(parts[2], out var e))
        {
            (dir, writable) = ($"elements/{e.Dir}", [.. ElementFiles(e).Where(ElementWritable.Contains), .. e.File ? ["upload/"] : Array.Empty<string>()]);
            if (parts.Length == 5 && parts[3] == "upload" && e.File) action = () => UploadAsync(e, parts[4], data);
            else if (parts.Length == 4 && ElementFiles(e).Contains(parts[3])) action = parts[3] switch
            {
                "click" => async () => await (await PresentAsync(e)).ClickAsync(new() { Timeout = Ms(_options.ActionTimeout) }),
                "value" => () => SetValueAsync(e, text.EndsWith('\n') ? text[..^1] : text),
                "checked" => async () => await (await PresentAsync(e)).SetCheckedAsync(Bool(line) ?? throw new MountSourceException(Msg("checkedWord")), new() { Timeout = Ms(_options.ActionTimeout) }),
                "press" => () => PressAsync(e, line),
                _ => null,
            };
        }
        else if (parts.Length == 4 && parts[1] == "forms" && t.Forms.IndexOf(parts[2]) is >= 0 and var form)
        {
            (dir, writable) = ($"forms/{parts[2]}", ["fields.json", "submit"]);
            action = parts[3] switch { "fields.json" => () => FillAsync(form, data), "submit" => () => SubmitAsync(form), _ => null };
        }
        if (action == null)
        {
            int slash = path.LastIndexOf('/');
            bool exists = dir != null && Children(slash == 0 ? "/" : path[..slash], t)?.Any(k => k.Name == parts[^1]) == true;
            throw new MountSourceException(exists ? Msg("readOnly", ("file", parts[^1]), ("writable", string.Join(", ", writable))) : Msg("noFile"));
        }
        await ActAsync(action, cancellationToken);
        return "";
    }

    /// <summary>Does one action, then waits for the page to settle: its requests stop (a navigation it started loads) within
    /// the time limits, and a window it opened is shown here instead. Errors become text the agent may see.</summary>
    async Task ActAsync(Func<Task> action, CancellationToken cancellationToken) => await GatedAsync(async () =>
    {
        lock (_lock) { _refused.Clear(); _notes.Clear(); _lastError = _navRefused = null; _epoch++; _inflight = 0; _navPending = false; }
        _web.BeginCall(); // the policy's per-call request limit counts one action
        try
        {
            await action();
            await SettleAsync();
            await PopupAsync();
        }
        catch (Exception e) when (e is PlaywrightException or System.TimeoutException or MountSourceException)
        {
            var why = e is MountSourceException m ? m.Message : Safe(e);
            lock (_lock) { why = _navRefused ?? why; _lastError = why; }
            throw new MountSourceException(why);
        }
        return true;
    }, 3 * _options.ActionTimeout + 2 * _options.SettleTimeout, cancellationToken); // the action, its settling and a window's page

    async Task SettleAsync()
    {
        var start = DateTime.UtcNow;
        await Task.Delay(100); // a submit or a script schedules its request a moment later
        while (true)
        {
            var now = DateTime.UtcNow;
            bool quiet = Interlocked.CompareExchange(ref _inflight, 0, 0) == 0 && now - new DateTime(Interlocked.Read(ref _activity), DateTimeKind.Utc) >= Quiet && !_navPending;
            if (quiet) return;
            if (now - start >= (_navPending ? _options.ActionTimeout : _options.SettleTimeout)) { if (_navPending) Note(Msg("loading")); return; }
            await Task.Delay(50);
        }
    }

    async Task PopupAsync()
    {
        IPage? popup;
        lock (_lock) { popup = _popup; _popup = null; }
        if (popup == null) return;
        try { await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = Ms(_options.SettleTimeout) }); }
        catch (Exception e) when (e is PlaywrightException or TimeoutException) { }
        var url = popup.Url;
        try { await popup.CloseAsync(); } catch (PlaywrightException) { }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        Note(Msg("popup", ("url", Cut(url, 300))));
        await GoAsync(url);
        await SettleAsync();
    }

    async Task GoAsync(string url)
    {
        if (!url.Contains("://", StringComparison.Ordinal)) throw new MountSourceException(Msg("fullUrl"));
        try { WebUrl.Parse(url); }
        catch (WebFailure f) { throw new MountSourceException(Why(f)); }
        await _page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Commit, Timeout = Ms(_options.ActionTimeout) });
    }

    async Task SetValueAsync(PageElement e, string value)
    {
        if (value.Length > _options.MaxValueLength) throw new MountSourceException(Msg("tooLong", ("limit", _options.MaxValueLength)));
        var loc = await PresentAsync(e);
        var kind = await loc.EvaluateAsync<string>("e => [e.tagName, (e.type || '').toLowerCase(), e.isContentEditable ? 'editable' : ''].join(' ')", null, new() { Timeout = ReadMs });
        var k = kind.Split(' ');
        if (k[0] == "SELECT") await SelectAsync(loc, value);
        else if (k[1] == "file") throw new MountSourceException(Msg("useUpload", ("dir", e.Dir)));
        else if (k[1] == "range")
            await loc.EvaluateAsync("(e, v) => { e.value = v; e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true })); }", value);
        else if (k[0] is "INPUT" or "TEXTAREA" || k[2] == "editable") await loc.FillAsync(value, new() { Timeout = Ms(_options.ActionTimeout) });
        else if (e.Role == "listbox")
        {
            var option = loc.GetByRole(AriaRole.Option, new() { Name = value, Exact = true });
            if (await option.CountAsync() == 0) throw new MountSourceException(Msg("noOption", ("value", Cut(value, 100)), ("options", Cut(string.Join(", ", _tree.OptionsOf(e.Node)), 300))));
            await option.First.ClickAsync(new() { Timeout = Ms(_options.ActionTimeout) });
        }
        else throw new MountSourceException(Msg("noText"));
    }

    /// <summary>Chooses options of a select by label (or value), one a line; an unknown one is refused before anything changes.</summary>
    async Task SelectAsync(ILocator select, string value)
    {
        var options = await select.EvaluateAsync<string[][]>("e => Array.from(e.options).map(o => [o.label, o.value])", null, new() { Timeout = ReadMs });
        var chosen = new List<SelectOptionValue>();
        foreach (var want in value.Split('\n'))
        {
            var hit = options.FirstOrDefault(o => o[0] == want) ?? options.FirstOrDefault(o => o[1] == want)
                ?? throw new MountSourceException(Msg("noOption", ("value", Cut(want, 100)), ("options", Cut(string.Join(", ", options.Select(o => o[0])), 300))));
            chosen.Add(new SelectOptionValue { Value = hit[1] });
        }
        await select.SelectOptionAsync(chosen, new() { Timeout = Ms(_options.ActionTimeout) });
    }

    async Task PressAsync(PageElement e, string keys)
    {
        var list = keys.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var key in list)
            if (key.Length is 0 or > 40 || list.Length > 20 || key.Any(c => char.IsControl(c)) || (key.Length > 1 && key.Contains(' ')))
                throw new MountSourceException(Msg("key", ("key", Cut(key, 40))));
        var loc = await PresentAsync(e);
        foreach (var key in list) await loc.PressAsync(key, new() { Timeout = Ms(_options.ActionTimeout) });
    }

    async Task UploadAsync(PageElement e, string name, byte[] data)
    {
        if (data.Length > _options.MaxUploadBytes) throw new MountSourceException(Msg("uploadTooLarge", ("limit", _options.MaxUploadBytes)));
        var loc = await PresentAsync(e);
        // shortcut: each file written replaces the one before, so a multiple-file field gets the last; collect them when asked for
        await loc.SetInputFilesAsync(new FilePayload { Name = name, MimeType = Mime(name), Buffer = data }, new() { Timeout = Ms(_options.ActionTimeout) });
    }

    static string Mime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".txt" or ".md" => "text/plain", ".csv" => "text/csv", ".json" => "application/json", ".html" or ".htm" => "text/html",
        ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    /// <summary>Fills a form from a JSON object of field labels and values; every name is checked before anything is filled.</summary>
    async Task FillAsync(int form, byte[] data)
    {
        if (data.Length > MaxFieldsJson) throw new MountSourceException(Msg("fieldsJson"));
        JsonObject values;
        try { values = JsonNode.Parse(data, documentOptions: new() { MaxDepth = 8 }) as JsonObject ?? throw new MountSourceException(Msg("fieldsJson")); }
        catch (JsonException) { throw new MountSourceException(Msg("fieldsJson")); }
        var fields = await FieldsAsync(form);
        var todo = new List<((string Key, int Index, string Type, JsonElement Value) Field, JsonNode? Value)>();
        foreach (var (key, value) in values)
        {
            var f = fields.FirstOrDefault(x => x.Key == key);
            if (f.Key == null) f = fields.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); // "Password" for a field labelled "password"
            if (f.Key == null) throw new MountSourceException(Msg("noField", ("field", Cut(key, 100)), ("fields", Cut(string.Join(", ", fields.Select(x => x.Key)), 300))));
            if (f.Type == "file") throw new MountSourceException(Msg("useUpload", ("dir", "file-...")));
            todo.Add((f, value));
        }
        foreach (var (f, value) in todo)
        {
            var loc = Form(form).Locator("input,select,textarea").Nth(f.Index);
            var s = value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : value?.ToJsonString() ?? "";
            if (f.Type is "checkbox" or "radio")
                await loc.SetCheckedAsync(Bool(s) ?? throw new MountSourceException(Msg("fieldBool", ("field", Cut(f.Key, 100)))), new() { Timeout = Ms(_options.ActionTimeout) });
            else if (f.Type == "select") await SelectAsync(loc, s);
            else if (s.Length > _options.MaxValueLength) throw new MountSourceException(Msg("tooLong", ("limit", _options.MaxValueLength)));
            else await loc.FillAsync(s, new() { Timeout = Ms(_options.ActionTimeout) });
        }
    }

    async Task SubmitAsync(int form)
    {
        var problems = await Form(form).EvaluateAsync<string[]>("f => { " + LabelOf + """
            return Array.from(f.elements).filter(e => e.willValidate && !e.checkValidity()).slice(0, 10)
              .map(e => (labelOf(e) || e.getAttribute('aria-label') || e.name || e.type).trim() + ': ' + e.validationMessage); }
            """, null, new() { Timeout = ReadMs });
        if (problems.Length > 0) throw new MountSourceException(Msg("incomplete", ("problems", Cut(string.Join("; ", problems), 500))));
        await Form(form).EvaluateAsync("f => f.requestSubmit ? f.requestSubmit() : f.submit()", null, new() { Timeout = Ms(_options.ActionTimeout) });
    }

    static bool? Bool(string s) => s.Trim().ToLowerInvariant() switch { "1" or "true" or "on" or "yes" => true, "0" or "false" or "off" or "no" or "" => false, _ => null };

    async Task AnswerAsync(IDialog d)
    {
        string? answer;
        lock (_lock) { answer = _answer; _answer = null; }
        bool accept = d.Type == "beforeunload" || answer?.StartsWith("accept", StringComparison.Ordinal) == true; // leaving a page is never held up
        lock (_lock) _dialog = $"{d.Type}: {Cut(d.Message, 1000)}\n{(accept ? "accepted" : "dismissed")}\n";
        try
        {
            if (accept) await d.AcceptAsync(answer is { Length: > 7 } ? answer[7..] : null);
            else await d.DismissAsync();
        }
        catch (PlaywrightException) { }
    }

    // ---------- the page's requests ----------

    /// <summary>Every request of the browser context: checked and sent by Topwater's web session (the policy, its limits, the
    /// connection pinned to an address it checked), and the answer handed to the browser. A refused request is aborted and
    /// shown in status.</summary>
    async Task RouteAsync(IRoute route)
    {
        var req = route.Request;
        bool nav = req.IsNavigationRequest, main = false;
        try { main = nav && req.Frame == _page.MainFrame; } catch (PlaywrightException) { }
        int epoch = _epoch;
        Interlocked.Increment(ref _inflight);
        Interlocked.Exchange(ref _activity, DateTime.UtcNow.Ticks);
        if (main) _navPending = true;
        try
        {
            WebUrl url;
            try { url = WebUrl.Parse(req.Url); }
            catch (WebFailure f) { await RefuseAsync(route, req.Url, Why(f), main, "blockedbyclient"); return; }
            var headers = new List<KeyValuePair<string, string>>();
            foreach (var (k, v) in await req.AllHeadersAsync())
                if (!k.StartsWith(':') && !DroppedRequestHeaders.Contains(k)) headers.Add(new(k, v));
            WebResponse r;
            // while the page's own document is loading, Playwright can't look into the page at all: a navigation gets the
            // action's time and is then aborted, whoever started it
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            if (main) stop.CancelAfter(_options.ActionTimeout);
            using var abort = main ? stop.Token.Register(() =>
            {
                Refused(req.Url, Msg("timedOut"));
                _ = route.AbortAsync("timedout").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            }) : default;
            try
            {
                // a navigation's redirect goes back to the page (below); a subresource's is followed here, each hop checked
                r = nav ? await _web.SendAsync(url, req.Method, headers, req.PostDataBuffer, _options.Policy.Timeout, stop.Token).WaitAsync(stop.Token)
                    : (await _web.FetchAsync(url, req.Method, null, headers, req.PostDataBuffer, true, -1, _options.Policy.Timeout, null, stop.Token)).Response;
            }
            catch (WebFailure f) { await RefuseAsync(route, req.Url, Why(f), main, f.Kind switch { WebError.Resolve => "namenotresolved", WebError.Timeout => "timedout", WebError.Connect => "connectionrefused", WebError.Policy => "blockedbyclient", _ => "failed" }); return; }
            if (r.Truncated || r.Error != null) { await RefuseAsync(route, req.Url, Msg(r.Truncated ? "responseTooLarge" : "responseCut"), main, "failed"); return; }
            var body = r.Body;
            var encoding = r.Header("Content-Encoding")?.Trim().ToLowerInvariant();
            if (encoding is { Length: > 0 } and not "identity")
            {
                body = (encoding is "gzip" or "x-gzip" or "deflate" ? WebSession.Inflate(body, encoding != "deflate", _options.Policy.MaxResponseBytes + 1) : null)!;
                if (body == null || body.Length > _options.Policy.MaxResponseBytes) { await RefuseAsync(route, req.Url, Msg("encoding", ("encoding", Cut(encoding, 40))), main, "failed"); return; }
            }
            if (main) lock (_lock) _status = r.Status;
            var fulfil = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in r.Headers.Where(h => !DroppedResponseHeaders.Contains(h.Key)).GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
                fulfil[g.Key] = string.Join(g.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) ? "\n" : ", ", g.Select(h => h.Value));
            if (nav && r.Status is 301 or 302 or 303 or 307 or 308 && r.Header("Location") is { Length: > 0 } location)
            {
                WebUrl next;
                try { next = url.Resolve(location); }
                catch (WebFailure f) { await RefuseAsync(route, location, Why(f), main, "blockedbyclient"); return; }
                // the browser follows a fulfilled redirect without routing it again, so the page is sent on to it instead; the
                // new request is routed (and checked) like any other, and cookies the redirect set are kept.
                // shortcut: a 307/308 POST arrives as a GET
                fulfil.Remove("location");
                fulfil["content-type"] = "text/html; charset=utf-8";
                await route.FulfillAsync(new() { Status = 200, Headers = fulfil, Body = $"<!doctype html><meta http-equiv=\"refresh\" content=\"0; url={WebUtility.HtmlEncode(next.ToString())}\">" });
                return;
            }
            await route.FulfillAsync(new() { Status = r.Status, Headers = fulfil, BodyBytes = body });
        }
        catch (PlaywrightException) { } // the page or the request went away meanwhile
        catch (OperationCanceledException) { try { await route.AbortAsync("aborted"); } catch (PlaywrightException) { } }
        finally
        {
            if (epoch == _epoch) Interlocked.Decrement(ref _inflight); // one an earlier action left behind no longer counts
            Interlocked.Exchange(ref _activity, DateTime.UtcNow.Ticks);
        }
    }

    async Task RefuseAsync(IRoute route, string url, string why, bool main, string code)
    {
        Refused(url, why);
        if (main) lock (_lock) { _navRefused ??= why; _navPending = false; }
        await route.AbortAsync(code);
    }

    void Refused(string url, string why)
    {
        lock (_lock)
        {
            if (_refused.Count >= MaxKept) _refused.RemoveAt(0);
            _refused.Add((Cut(url, 300), why));
        }
    }

    void Note(string note)
    {
        lock (_lock)
        {
            if (_notes.Count >= MaxKept) _notes.RemoveAt(0);
            if (!_notes.Contains(note)) _notes.Add(note);
        }
    }

    static string Why(WebFailure f) => f.Blocked is { } why ? CommandSpec.Message("curl", "blocked", ("reason", why)) : f.Kind switch
    {
        WebError.Protocol => Msg("scheme", ("scheme", f.Host)),
        WebError.BadUrl or WebError.BadHostname => Msg("badUrl"),
        WebError.Resolve => Msg("resolve"),
        WebError.Connect => Msg("connect"),
        WebError.Timeout => Msg("requestTimedOut"),
        WebError.Tls => Msg("tls"),
        WebError.BadHeader => Msg("badHeader"),
        _ => Msg("failed"),
    };

    // ---------- helpers ----------

    /// <summary>Runs one browser operation at a time. The caller stops waiting at its token or at <paramref name="limit"/>; the
    /// gate stays closed until the operation really ends (each has its own timeout), so the next one never overlaps it. Some
    /// Playwright calls have no timeout and never end while the page's script never yields: past its limit an operation still
    /// holds the gate, and the next fails at once instead of waiting out every call.</summary>
    async Task<T> GatedAsync<T>(Func<Task<T>> run, TimeSpan limit, CancellationToken cancellationToken)
    {
        var left = new DateTime(Interlocked.Read(ref _heldUntil), DateTimeKind.Utc) - DateTime.UtcNow;
        if (!await _gate.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, cancellationToken)) throw new MountSourceException(Msg("busy"));
        Interlocked.Exchange(ref _heldUntil, (DateTime.UtcNow + limit).Ticks);
        Task<T> task;
        try { task = run(); }
        catch { _gate.Release(); throw; }
        _ = task.ContinueWith(t => { _ = t.Exception; _gate.Release(); }, TaskScheduler.Default);
        try { return await task.WaitAsync(limit, cancellationToken); }
        catch (System.TimeoutException) { throw new MountSourceException(Msg("timedOut")); }
    }

    static float Ms(TimeSpan t) => (float)Math.Max(1, t.TotalMilliseconds);

    static string Cut(string? s, int max) => s is null ? "" : s.Length > max ? s[..max] : s;

    /// <summary>What the agent may see of a browser error: a known kind, or Playwright's first line when it is plain words
    /// (never a path, a URL or a stack).</summary>
    static string Safe(Exception e)
    {
        if (e is System.TimeoutException) return Msg("timedOut");
        if (NetError().Match(e.Message) is { Success: true } net) return Msg("loadFailed", ("error", net.Value));
        var first = Prefix().Replace(e.Message.Split('\n')[0], "").Trim();
        return first.Length is > 0 and <= 200 && Plain().IsMatch(first) ? first : Msg("browserFailed");
    }

    [GeneratedRegex(@"net::ERR_[A-Z_]+")]
    private static partial Regex NetError();
    [GeneratedRegex(@"^(\w+\.\w+: |Error: )+")]
    private static partial Regex Prefix();
    [GeneratedRegex(@"^[\p{L}\p{N} ,.'""<>()=_-]+$")]
    private static partial Regex Plain();
}
