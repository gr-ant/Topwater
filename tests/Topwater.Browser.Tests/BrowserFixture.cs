using System.Net;
using System.Text;
using Microsoft.Playwright;

namespace Topwater.Browser.Tests;

/// <summary>A test that needs Chromium. It installs it the first time (into the user's Playwright cache); where that fails it is
/// skipped with the reason, except in CI, where it runs and fails.</summary>
public sealed class ChromiumFactAttribute : FactAttribute
{
    static readonly Lazy<string?> Missing = new(() =>
    {
        try { return Microsoft.Playwright.Program.Main(["install", "chromium"]) == 0 ? null : "the Chromium install failed"; }
        catch (Exception e) { return $"Chromium can't be installed: {e.Message}"; }
    });

    public ChromiumFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CI") is not { Length: > 0 } && Missing.Value is { } why)
            Skip = $"{why} (run `pwsh bin/Release/net10.0/playwright.ps1 install chromium`)";
    }
}

/// <summary>One Chromium for all the tests, launched with the source's flags; each test opens its own context.</summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    IPlaywright? _playwright;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Microsoft.Playwright.Program.Main(["install", "chromium"]);
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Args = [.. BrowserSource.LaunchArgs] });
    }

    public async Task DisposeAsync()
    {
        if (Browser != null) await Browser.CloseAsync();
        _playwright?.Dispose();
    }
}

[CollectionDefinition("browser")]
public sealed class BrowserCollection : ICollectionFixture<BrowserFixture>;

/// <summary>Sites served from memory through the web policy's handler: nothing reaches the network. Seen lists every request.</summary>
public sealed class Site : HttpMessageHandler
{
    readonly Dictionary<string, Func<HttpRequestMessage, Task<HttpResponseMessage>>> _routes = new(StringComparer.Ordinal);
    public List<string> Seen { get; } = [];

    /// <summary>Serves <paramref name="html"/> at a URL without its query.</summary>
    public Site Page(string url, string html) => On(url, _ => Task.FromResult(Html(html)));

    public Site On(string url, Func<HttpRequestMessage, Task<HttpResponseMessage>> answer)
    {
        _routes[url] = answer;
        return this;
    }

    public static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Seen) Seen.Add($"{request.Method} {request.RequestUri}");
        var key = request.RequestUri!.GetLeftPart(UriPartial.Path);
        return _routes.TryGetValue(key, out var answer) ? await answer(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<title>Not found</title>", Encoding.UTF8, "text/html") };
    }
}
