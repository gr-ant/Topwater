using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Topwater.Browser.Tests;

/// <summary>A benchmark, run only when TOPWATER_BENCH is set: <c>TOPWATER_BENCH=1 dotnet test -c Release --filter BrowserBench</c>.</summary>
public sealed class BenchFactAttribute : ChromiumFactAttribute
{
    public BenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TOPWATER_BENCH") is not { Length: > 0 }) Skip = "a benchmark: set TOPWATER_BENCH=1 to run it";
    }
}

/// <summary>Four encyclopedia-like articles served from memory with a network's delay, opened through the mount as an agent
/// would (examples/BrowserMount --bench does the same against the real Wikipedia).</summary>
[Collection("browser")]
public sealed class BrowserBench(BrowserFixture fixture, ITestOutputHelper output)
{
    static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(40);

    /// <summary>A long article: an infobox, paragraphs full of links, a big inline script and style, images, shared scripts and
    /// styles, and a script that fetches more after the page loaded.</summary>
    static string Article(string name)
    {
        var sb = new StringBuilder($"<!doctype html><html><head><meta charset=utf-8><title>{name}</title>");
        sb.Append("<link rel=stylesheet href=/static/site.css><link rel=stylesheet href=/static/skin.css>");
        sb.Append("<style>").Append(string.Concat(Enumerable.Range(0, 3000).Select(i => $".c{i}{{margin:{i % 7}px}}"))).Append("</style>");
        sb.Append("<script>var config = ").Append('"').Append(new string('x', 300_000)).Append("\";</script>");
        sb.Append("<script src=/static/startup.js></script><script src=/static/jquery.js defer></script></head><body>");
        sb.Append("<nav>").Append(string.Concat(Enumerable.Range(0, 300).Select(i => $"<a href=/wiki/Nav{i}>Nav {i}</a>"))).Append("</nav>");
        sb.Append($"<h1>{name}</h1><table class=infobox><tr><th>Born</th><td>{name} Senior, June 14, 1946</td></tr></table>");
        for (int p = 0; p < 400; p++)
        {
            sb.Append("<p class=\"para c").Append(p).Append("\" style=\"color:#202122\">");
            for (int l = 0; l < 8; l++) sb.Append($"Some words about {name} and <a href=\"/wiki/Topic_{p}_{l}\" title=\"Topic {p} {l}\" class=\"mw-link\">topic {p}.{l}</a>, ");
            sb.Append("</p>");
            if (p % 15 == 0) sb.Append($"<figure><img src=\"/img/{name}_{p}.jpg\" width=250 height=180 alt=\"Figure {p}\"><figcaption>Figure {p}</figcaption></figure>");
        }
        sb.Append("<script src=/static/popups.js async></script></body></html>");
        return sb.ToString();
    }

    static Site Wiki()
    {
        var site = new Site();
        async Task<HttpResponseMessage> Slow(HttpResponseMessage r) { await Task.Delay(Latency); return r; }
        HttpResponseMessage Static(string body, string type)
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
            r.Headers.CacheControl = new() { Public = true, MaxAge = TimeSpan.FromDays(30) };
            return r;
        }
        foreach (var name in new[] { "Donald_Trump", "Joe_Biden", "Barack_Obama", "George_W._Bush" })
        {
            var html = Article(name);
            site.On($"https://wiki.test/wiki/{name}", _ => Slow(Site.Html(html)));
        }
        site.On("https://wiki.test/static/site.css", _ => Slow(Static("body{font-family:sans-serif}" + new string(' ', 50_000), "text/css")));
        site.On("https://wiki.test/static/skin.css", _ => Slow(Static(".infobox{float:right}" + new string(' ', 30_000), "text/css")));
        site.On("https://wiki.test/static/startup.js", _ => Slow(Static("window.started = 1;" + new string(' ', 40_000), "text/javascript")));
        site.On("https://wiki.test/static/jquery.js", _ => Slow(Static("window.$ = 1;" + new string(' ', 90_000), "text/javascript")));
        site.On("https://wiki.test/static/popups.js", _ => Slow(Static(
            "addEventListener('load', () => setTimeout(() => fetch('/api/popups').then(r => r.text()).then(t => document.body.dataset.p = t), 50));", "text/javascript")));
        site.On("https://wiki.test/api/popups", _ => Slow(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }));
        return site.Fallback(req => req.RequestUri!.AbsolutePath.StartsWith("/img/", StringComparison.Ordinal)
            ? Slow(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[30_000]) { Headers = { ContentType = new("image/jpeg") } } })
            : null);
    }

    [BenchFact]
    public async Task FourArticles()
    {
        var site = Wiki();
        var context = await fixture.Browser.NewContextAsync(new() { AcceptDownloads = false, ServiceWorkers = ServiceWorkerPolicy.Block });
        try
        {
            await using var source = new BrowserSource(await context.NewPageAsync(), new BrowserSourceOptions
            {
                Policy = new WebPolicy { Domains = ["wiki.test"], Handler = site, MaxRequestsPerCall = 200, MaxRequests = 5000 },
            });
            var sh = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(60) });
            await sh.MountAsync("/web", source, MountMode.Live);
            await sh.RunAsync("true");
            var times = new List<double>();
            for (int round = 0; round < 2; round++) // the first round warms up the JIT and the browser
            {
                times.Clear();
                foreach (var name in new[] { "Donald_Trump", "Joe_Biden", "Barack_Obama", "George_W._Bush" })
                {
                    var watch = Stopwatch.StartNew();
                    var r = await sh.RunAsync($"echo https://wiki.test/wiki/{name} > /web/url; grep -m1 -i born /web/text.md");
                    times.Add(watch.Elapsed.TotalSeconds);
                    Assert.Contains("Born", r.Stdout);
                    output.WriteLine($"round {round} {name,-16} {watch.Elapsed.TotalSeconds:F2} s");
                }
            }
            output.WriteLine($"total {times.Sum():F2} s, {site.Seen.Count} requests reached the site");
        }
        finally { await context.CloseAsync(); }
    }
}
