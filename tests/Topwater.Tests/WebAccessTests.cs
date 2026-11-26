using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Topwater;
using Topwater.AI;

namespace Topwater.Tests;

/// <summary>Answers like tests/web/server.py: the routes in tests/web/routes.json, and /echo with the request it got.</summary>
sealed class FixtureHandler : HttpMessageHandler
{
    static readonly JsonElement Routes = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "web", "routes.json"))).RootElement.GetProperty("routes");
    static readonly string[] Echoed = ["content-type", "accept", "authorization", "user-agent", "x-test"];

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<Dictionary<string, string>> Seen { get; } = [];
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Override { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var headers = request.Headers.NonValidated.Concat(request.Content?.Headers.NonValidated ?? default)
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(", ", h.Value));
        if (request.Headers.Host is { } host) headers["host"] = host;
        Seen.Add(headers);
        if (Override != null) return await Override(request, ct);
        var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var target = request.RequestUri!.PathAndQuery;
        int status; string reason; byte[] data; List<(string, string)> hs;
        if (!Routes.TryGetProperty(target.Split('?')[0], out var r)) (status, reason, hs, data) = (404, "Not Found", [("Content-Type", "text/plain")], "no route\n"u8.ToArray());
        else if (r.TryGetProperty("echo", out _))
        {
            var lines = new List<string> { $"{request.Method.Method} {target}" };
            lines.AddRange(Echoed.Where(headers.ContainsKey).Select(h => $"{h}: {headers[h]}"));
            (status, reason, hs, data) = (200, "OK", [("Content-Type", "text/plain")], [.. Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n\n"), .. body]);
        }
        else
        {
            data = r.TryGetProperty("bodyBase64", out var b64) ? Convert.FromBase64String(b64.GetString()!) : Encoding.UTF8.GetBytes(r.GetProperty("body").GetString()!);
            (status, reason) = (r.GetProperty("status").GetInt32(), r.GetProperty("reason").GetString()!);
            hs = [.. r.GetProperty("headers").EnumerateArray().Select(h => (h[0].GetString()!, h[1].GetString()!))];
        }
        var response = new HttpResponseMessage((HttpStatusCode)status) { ReasonPhrase = reason, Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : data) };
        foreach (var (k, v) in hs.Append(("Content-Length", data.Length.ToString())))
            if (!response.Headers.TryAddWithoutValidation(k, v)) response.Content.Headers.TryAddWithoutValidation(k, v);
        return response;
    }
}

/// <summary>
/// curl and wget against tests/web/cases.jsonl, recorded from real curl 8.5.0 and wget 1.21.4 (tests/web/record.py); the
/// Python and TypeScript ports run the same cases. Then what the policy guarantees: SSRF, caps, credentials, recordings.
/// </summary>
public class WebAccessTests
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "web");
    static readonly Dictionary<string, JsonElement> Cases = File.ReadLines(Path.Combine(Dir, "cases.jsonl")).Where(l => l.Trim() != "")
        .Select(l => JsonDocument.Parse(l).RootElement).ToDictionary(c => c.GetProperty("id").GetString()!);

    public static IEnumerable<object[]> Ids() => Cases.Keys.Select(id => new object[] { id });

    internal static WebPolicy FixturePolicy(HttpMessageHandler handler) => new()
    {
        Domains = ["api.test"], AllowHttp = true, Methods = ["GET", "POST", "PUT", "DELETE", "PATCH"], Handler = handler,
        ExplainBlocked = false, // the shared cases match real curl and wget, which only know that a name does not resolve
    };

    static async Task<(Shell Shell, FixtureHandler Handler)> Make(Func<FixtureHandler, WebPolicy>? policy = null)
    {
        var handler = new FixtureHandler();
        var sh = new Shell().AllowWeb(policy?.Invoke(handler) ?? FixturePolicy(handler));
        await sh.RunAsync("mkdir -p /tmp/w && cd /tmp/w");
        return (sh, handler);
    }

    static readonly JsonElement Routes = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "routes.json"))).RootElement.GetProperty("routes");

    /// <summary>Serves the "raw" routes on a loopback port, byte for byte as tests/web/server.py does, then closes.</summary>
    static (TcpListener Listener, int Port) RawServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using var _ = client;
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.Latin1);
                    var target = (await reader.ReadLineAsync())!.Split(' ')[1];
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    await stream.WriteAsync(Encoding.Latin1.GetBytes(Routes.GetProperty(target).GetProperty("raw").GetString()!));
                    client.Client.Shutdown(SocketShutdown.Send);
                });
            }
        });
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    /// <summary>The real connection path to <see cref="RawServer"/>: the wire cases are malformed responses a fake handler cannot send.</summary>
    static async Task<Shell> WireShell(int port)
    {
        var sh = new Shell().AllowWeb(new WebPolicy
        {
            Domains = [$"api.test:{port}"], AllowHttp = true, ExplainBlocked = false, Handler = WebSession.PinnedHandler(_ => [IPAddress.Loopback], allowLoopback: true),
        });
        await sh.RunAsync("mkdir -p /tmp/w && cd /tmp/w");
        return sh;
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task Case(string id)
    {
        var c = Cases[id];
        var script = c.GetProperty("script").GetString()!;
        ShellResult r;
        if (c.TryGetProperty("wire", out var wire) && wire.GetBoolean())
        {
            var (listener, port) = RawServer();
            try { r = await (await WireShell(port)).RunAsync(script.Replace("http://api.test/", $"http://api.test:{port}/")); }
            finally { listener.Stop(); }
        }
        else r = await (await Make()).Shell.RunAsync(script);
        Assert.Equal(c.GetProperty("stdout").GetString(), r.Stdout);
        if (c.GetProperty("stderr").ValueKind == JsonValueKind.String) Assert.Equal(c.GetProperty("stderr").GetString(), r.Stderr);
        Assert.Equal(c.GetProperty("exitCode").GetInt32(), r.ExitCode);
    }

    [Fact]
    public async Task WithoutAPolicyThereIsNoNetwork()
    {
        var sh = new Shell();
        var r = await sh.RunAsync("curl -sS https://example.com/; echo $?; wget -q https://example.com/; echo $?");
        Assert.Equal("6\n4\n", r.Stdout);
        Assert.Equal("curl: (6) Could not resolve host: example.com\n", r.Stderr);
    }

    [Fact]
    public async Task BlockedHostsExplainThemselvesWhenAPolicyIsSet()
    {
        var (sh, handler) = await Make(h => FixturePolicy(h));
        var explain = new Shell().AllowWeb(new WebPolicy { Domains = ["api.test"], Handler = new FixtureHandler() }); // explaining is the default
        var r = await explain.RunAsync("curl -sS https://evil.test/; curl -sS http://api.test/; curl -sS https://127.0.0.1/; curl -sS -X POST https://api.test/");
        Assert.Equal(
            "curl: (6) Blocked by the web policy: evil.test is not an allowed host (allowed: api.test)\n" +
            "curl: (1) Blocked by the web policy: only https:// URLs are allowed\n" +
            "curl: (7) Blocked by the web policy: 127.0.0.1 is a private or reserved address\n" +
            "curl: (1) Blocked by the web policy: POST requests are not allowed\n", r.Stderr);
        r = await sh.RunAsync("curl -sS -X PROPFIND http://api.test/echo; echo $?");
        Assert.Equal("1\n", r.Stdout);
        Assert.Equal("curl: (1) Blocked by the web policy: PROPFIND requests are not allowed\n", r.Stderr);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HttpsOnlyByDefault()
    {
        var sh = new Shell().AllowWeb(new WebPolicy { Domains = ["api.test"], Handler = new FixtureHandler(), ExplainBlocked = false });
        var r = await sh.RunAsync("curl -sS http://api.test/hello; echo $?; curl -s https://api.test/hello");
        Assert.Equal("1\nhello\n", r.Stdout);
        Assert.Equal("curl: (1) Protocol \"http\" not supported or disabled in libcurl\n", r.Stderr);
    }

    [Theory]
    [InlineData("http://[::ffff:127.0.0.1]/", 7)]
    [InlineData("http://[::ffff:a9fe:a9fe]/", 7)]
    [InlineData("http://[fd00:ec2::254]/", 7)]
    [InlineData("http://[fe80::1]/", 7)]
    [InlineData("http://0/", 7)]
    [InlineData("http://017700000001/", 7)]
    [InlineData("http://127.1/", 7)]
    [InlineData("http://100.100.100.200/", 7)]
    [InlineData("http://api.test.evil.test/", 6)]
    [InlineData("http://evil.test/#@api.test/", 6)]
    [InlineData("http://api.test:80@evil.test/", 6)]
    public async Task InternalAddressesAndLookalikesAreRefused(string url, int code)
    {
        var (sh, handler) = await Make();
        var r = await sh.RunAsync($"curl -s '{url}'; echo $?");
        Assert.Equal($"{code}\n", r.Stdout);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void AddressRanges()
    {
        string[] blocked = ["0.0.0.0", "10.1.2.3", "100.64.0.1", "127.0.0.1", "169.254.169.254", "172.16.0.1", "172.31.255.255", "192.168.1.1", "224.0.0.1", "255.255.255.255",
            "::", "::1", "::ffff:10.0.0.1", "::7f00:1", "64:ff9b::a9fe:a9fe", "2002:7f00:1::", "fc00::1", "fe80::1", "fec0::1", "ff02::1", "2001::1", "2001:db8::1"];
        blocked = [.. blocked, "168.63.129.16", "192.88.99.1", "::ffff:0:a00:1", "100::1", "2001:20::1", "2001:2f::1", "3fff::1", "5f00::1"];
        string[] open = ["8.8.8.8", "1.1.1.1", "172.32.0.1", "100.128.0.1", "2606:4700::1111", "2002:808:808::"];
        foreach (var a in blocked) Assert.True(WebAddresses.IsBlocked(IPAddress.Parse(a).GetAddressBytes()), a);
        foreach (var a in open) Assert.False(WebAddresses.IsBlocked(IPAddress.Parse(a).GetAddressBytes()), a);
        Assert.Equal([127, 0, 0, 1], WebAddresses.ParseIPv4("2130706433"));
        Assert.Equal([127, 0, 0, 1], WebAddresses.ParseIPv4("0177.0.0.01"));
        Assert.Equal([127, 0, 0, 1], WebAddresses.ParseIPv4("0x7f.1"));
        Assert.Null(WebAddresses.ParseIPv4("256.0.0.1"));
        Assert.Null(WebAddresses.ParseIPv4("1.2.3.4.5"));
        Assert.Null(WebAddresses.ParseIPv4("example.com"));
    }

    [Fact]
    public async Task TheCredentialIsTheHostsAndNeverShown()
    {
        const string token = "s3cr3t-T0KEN-value";
        var (sh, handler) = await Make(h => FixturePolicy(h));
        sh.AllowWeb(new WebPolicy
        {
            Domains = ["api.test", "other.test"], AllowHttp = true, Handler = handler,
            Credentials = new Dictionary<string, Func<string>> { ["api.test"] = () => "Bearer " + token },
        });
        var r = await sh.RunAsync("curl -s -H 'Authorization: Bearer mine' -H 'Host: other.test' -u me:pw http://api.test/echo; curl -sv http://api.test/echo -o /dev/null");
        Assert.Contains("authorization: [REDACTED]\n", r.Stdout);
        Assert.DoesNotContain(token, r.Stdout + r.Stderr);
        Assert.DoesNotContain("Authorization", r.Stderr);
        Assert.All(handler.Seen, h => Assert.Equal("Bearer " + token, h["authorization"]));
        Assert.All(handler.Seen, h => Assert.Equal("api.test", h.GetValueOrDefault("host", "api.test")));

        // a redirect to another allowed host gets neither the credential nor the agent's own Authorization
        handler.Seen.Clear();
        handler.Override = (req, _) => Task.FromResult(req.RequestUri!.Host == "api.test"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://other.test/x") } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok\n") });
        r = await sh.RunAsync("curl -sL -H 'Authorization: Bearer mine' http://api.test/go");
        Assert.Equal("ok\n", r.Stdout);
        Assert.Equal("Bearer " + token, handler.Seen[0]["authorization"]);
        Assert.False(handler.Seen[1].ContainsKey("authorization"));
    }

    [Fact]
    public async Task ResponsesAreCutAtTheCap()
    {
        var (sh, _) = await Make(h => FixturePolicy(h));
        sh.AllowWeb(new WebPolicy { Domains = ["api.test"], AllowHttp = true, MaxResponseBytes = 4, Handler = new FixtureHandler() });
        var r = await sh.RunAsync("curl -sS http://api.test/hello; echo \" $?\"; wget -q -O - http://api.test/hello; echo \" $?\"");
        Assert.Equal("hell 63\nhell 1\n", r.Stdout);
        Assert.Equal("curl: (63) Maximum file size exceeded\nwget: the response is larger than the web policy allows (4 bytes)\n", r.Stderr);
        // a gzip bomb stops at the cap too
        var handler = new FixtureHandler();
        var bomb = new MemoryStream();
        using (var z = new System.IO.Compression.GZipStream(bomb, System.IO.Compression.CompressionLevel.Optimal)) z.Write(new byte[1_000_000]);
        handler.Override = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(bomb.ToArray()) { Headers = { { "Content-Encoding", "gzip" } } } });
        sh.AllowWeb(new WebPolicy { Domains = ["api.test"], AllowHttp = true, MaxResponseBytes = 10_000, Handler = handler });
        r = await sh.RunAsync("curl -s --compressed http://api.test/gzip | wc -c; echo ${PIPESTATUS[0]}");
        Assert.Equal("0\n63\n", r.Stdout);
    }

    [Fact]
    public async Task RequestsAreCountedPerCallAndPerShell()
    {
        var handler = new FixtureHandler();
        var sh = new Shell().AllowWeb(new WebPolicy { Domains = ["api.test"], AllowHttp = true, MaxRequestsPerCall = 3, MaxRequests = 5, MaxRedirects = 50, Handler = handler });
        var r = await sh.RunAsync("curl -sSL http://api.test/loop; echo $?");
        Assert.Equal("1\n", r.Stdout);
        Assert.Equal("curl: (1) Blocked by the web policy: the limit of 3 requests in one call was reached\n", r.Stderr);
        r = await sh.RunAsync("for i in 1 2 3; do curl -s http://api.test/hello; done; echo $?");
        Assert.Equal("hello\nhello\n1\n", r.Stdout);
        Assert.Contains("5 requests for this session", (await sh.RunAsync("curl -sS http://api.test/hello")).Stderr);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task RedirectsStopAtThePolicyLimit()
    {
        var (sh, _) = await Make();
        var r = await sh.RunAsync("curl -sSL http://api.test/loop");
        Assert.Equal("curl: (47) Maximum (10) redirects followed\n", r.Stderr);
        r = await sh.RunAsync("wget -O - http://api.test/loop; echo $?");
        Assert.EndsWith("10 redirections exceeded.\n", r.Stderr);
        Assert.Equal("8\n", r.Stdout);
    }

    [Fact]
    public async Task SlowAnswersTimeOut()
    {
        var (sh, handler) = await Make(h => FixturePolicy(h));
        handler.Override = async (_, ct) => { await Task.Delay(10_000, ct); return new HttpResponseMessage(); };
        var (r, took) = await TestHost.Timed(() => sh.RunAsync("curl -sS -m 0.2 http://api.test/hello; echo $?; wget -q -T 0.2 http://api.test/hello; echo $?"));
        Assert.Equal("28\n4\n", r.Stdout);
        Assert.StartsWith("curl: (28) Operation timed out after 200 milliseconds with 0 bytes received\n", r.Stderr);
        Assert.True(took < TimeSpan.FromSeconds(8), took.ToString()); // two 0.2 s limits, not the handler's 10 s
    }

    [Fact]
    public async Task HugeHeadersAreRefused()
    {
        var (sh, handler) = await Make();
        handler.Override = (_, _) =>
        {
            var m = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
            m.Headers.TryAddWithoutValidation("X-Big", new string('a', 70_000));
            return Task.FromResult(m);
        };
        var r = await sh.RunAsync("curl -sS http://api.test/hello; echo $?");
        Assert.Equal("56\n", r.Stdout);
    }

    [Fact]
    public async Task HeaderInjectionIsRefused()
    {
        var (sh, handler) = await Make();
        var r = await sh.RunAsync("curl -sS -H $'X-Test: a\\r\\nX-Evil: b' http://api.test/echo; echo $?");
        Assert.Equal("43\n", r.Stdout);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ForksDoNotInheritWebAccess()
    {
        var (sh, _) = await Make();
        var r = await sh.Fork().RunAsync("curl -s http://api.test/hello; echo $?");
        Assert.Equal("6\n", r.Stdout);
    }

    [Fact]
    public async Task ABodyCutShortReplaysAsItCame()
    {
        var (listener, port) = RawServer();
        try
        {
            var sh = await WireShell(port);
            var recorder = sh.StartRecording();
            var first = await sh.RunAsync($"curl -sS http://api.test:{port}/short-length; curl -sS http://api.test:{port}/empty; wget -q -O- http://api.test:{port}/chunked-short; echo $?");
            recorder.Stop();
            Assert.Equal("shortok\n4\n", first.Stdout);
            var file = new MemoryStream();
            recorder.Save(file);
            file.Position = 0;
            var loaded = Recording.Load(file);
            Assert.Equal([("partial", 200, "short"), ("empty", 0, ""), ("partial", 200, "ok\n")],
                loaded.Calls[0].Web!.Select(x => (x.Error, x.Status, Encoding.UTF8.GetString(x.Body))));
            var report = await loaded.ReplayAsync();
            Assert.True(report.Matches, report.ToString());
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task RecordingsReplayWithoutTheNetwork()
    {
        var (sh, handler) = await Make();
        var recorder = sh.StartRecording();
        await sh.RunAsync("curl -s http://api.test/json");
        await sh.RunAsync("curl -sL http://api.test/redirect -o page; wget -q http://api.test/gzip; curl -sS http://nope.test/");
        await sh.RunAsync("cat page; wc -c < gzip");
        recorder.Stop();
        var file = new MemoryStream();
        recorder.Save(file);
        file.Position = 0;
        var loaded = Recording.Load(file);
        Assert.Equal(["api.test"], loaded.Web!.Domains);
        Assert.Equal(3, loaded.Calls[1].Web!.Count); // the redirect, the page, the gzip file; the refused host never reached the network
        Assert.Equal("hello\n"u8.ToArray(), loaded.Calls[1].Web![1].Body);

        int before = handler.Requests.Count;
        var report = await loaded.ReplayAsync();
        Assert.True(report.Matches, report.ToString());
        Assert.Equal(before, handler.Requests.Count);
        var shell = await loaded.ShellAtAsync(1);
        Assert.Equal("hello\n", shell.Fs.ReadAllText("/tmp/w/page"));

        // asking for something that was not recorded is reported, not fetched
        var other = loaded.CreateShell();
        var r = await other.RunAsync("curl -sS http://api.test/hello");
        Assert.Equal("curl: (7) Topwater replay: GET http://api.test/hello is not in the recording\n", r.Stderr);
    }

    [Fact]
    public async Task RetriesOnAVirtualClockStopAtTheCallsTimeLimit()
    {
        // the waits move the virtual clock and take no real time, so only the call's limit ends the tries
        var sh = new Shell(new ShellOptions { StartTime = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), MaxCallDuration = TimeSpan.FromSeconds(0.5) });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await sh.RunAsync("curl -s --retry 2000000000 http://x.test/");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task WriteOutTimesOnAVirtualClockReplay()
    {
        var sh = new Shell(new ShellOptions { StartTime = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), RandomSeed = 3 }).AllowWeb(FixturePolicy(new FixtureHandler()));
        var recorder = sh.StartRecording();
        var r = await sh.RunAsync("curl -s -o /dev/null -w '%{time_total} %{time_starttransfer} %{speed_download}\\n' http://api.test/hello");
        Assert.Equal("0.000000 0.000000 0\n", r.Stdout);
        recorder.Stop();
        var report = await recorder.Recording.ReplayAsync();
        Assert.True(report.Matches, report.ToString());
    }

    /// <summary>The calls in recordings/web-from-csharp.topwater, which the Python and TypeScript ports load and replay too.</summary>
    internal static readonly string[] WebScenario =
    [
        "curl -s http://api.test/json",
        "curl -sL -o page http://api.test/redirect; wget -q http://api.test/gzip; cat page; wc -c < gzip",
        "curl -sS http://nope.test/; echo $?",
    ];

    [Fact]
    public async Task ARecordingFromThisPortReplays()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "recordings", "web-from-csharp.topwater");
        if (Environment.GetEnvironmentVariable("TOPWATER_WRITE_FIXTURES") != null)
        {
            var sh = new Shell(new ShellOptions { StartTime = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), RandomSeed = 3 }).AllowWeb(FixturePolicy(new FixtureHandler()));
            var recorder = sh.StartRecording(new RecordingOptions { Metadata = { ["source"] = "csharp port", ["task"] = "web fixture" } });
            foreach (var c in WebScenario) await sh.RunAsync(c);
            recorder.Stop();
            recorder.Save(path);
        }
        var rec = Recording.Load(path);
        Assert.Equal(WebScenario, rec.Calls.Select(c => c.Command));
        var report = await rec.ReplayAsync();
        Assert.True(report.Matches, report.ToString());
    }

    [Fact]
    public async Task ARecordingKeepsNoCredential()
    {
        const string token = "s3cr3t-T0KEN-value";
        var handler = new FixtureHandler();
        var sh = new Shell().AllowWeb(new WebPolicy
        {
            Domains = ["api.test"], AllowHttp = true, Handler = handler, Credentials = new Dictionary<string, Func<string>> { ["api.test"] = () => "Bearer " + token },
        });
        var recorder = sh.StartRecording();
        await sh.RunAsync("curl -s http://api.test/echo");
        recorder.Stop();
        var file = new MemoryStream();
        recorder.Save(file);
        Assert.DoesNotContain(token, Encoding.Latin1.GetString(file.ToArray()));
        file.Position = 0;
        var report = await Recording.Load(file).ReplayAsync();
        Assert.True(report.Matches, report.ToString());
    }

    // ---------- board 030 ----------

    const string Secret = "SUPERSECRETTOKEN123";

    /// <summary>A host with a credential whose /echo shows the Authorization header it got, gzipped when asked, and honours Range.</summary>
    static (Shell Shell, FixtureHandler Handler) CredentialHost(Func<FixtureHandler, WebPolicy>? policy = null)
    {
        var handler = new FixtureHandler();
        handler.Override = (req, _) =>
        {
            var text = Encoding.UTF8.GetBytes($"Authorization: {req.Headers.Authorization}\n");
            if (req.Headers.Range?.Ranges.FirstOrDefault() is { From: { } from, To: { } to }) text = text[(int)from..(int)Math.Min(to + 1, text.Length)];
            var gzip = req.Headers.AcceptEncoding.Any(e => e.Value == "gzip");
            if (gzip)
            {
                var ms = new MemoryStream();
                using (var z = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal)) z.Write(text);
                text = ms.ToArray();
            }
            var m = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(text) };
            if (gzip) m.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(m);
        };
        var sh = new Shell().AllowWeb(policy?.Invoke(handler) ?? new WebPolicy
        {
            Domains = ["cred.test"], Handler = handler, Credentials = { ["cred.test"] = () => "Bearer " + Secret }, // the docs' short form compiles
        });
        return (sh, handler);
    }

    [Theory]
    [InlineData("curl -sS --compressed https://cred.test/echo")]
    [InlineData("curl -sS -H 'Accept-Encoding: gzip' https://cred.test/echo | gunzip")]
    [InlineData("curl -sS -H 'Accept-Encoding: gzip' -o p.gz https://cred.test/echo; zcat p.gz")]
    [InlineData("wget -q -O - --header 'Accept-Encoding: gzip' https://cred.test/echo | gunzip")]
    [InlineData("a=$(curl -s -H 'Range: bytes=0-20' https://cred.test/echo); b=$(curl -s -H 'Range: bytes=21-60' https://cred.test/echo); echo \"$a$b\"")]
    public async Task TheCredentialDoesNotLeakThroughEncodingsOrRanges(string script)
    {
        var (sh, handler) = CredentialHost();
        var r = await sh.RunAsync(script);
        Assert.DoesNotContain(Secret, r.Stdout + r.Stderr);
        Assert.True(handler.Requests.Count > 0);
        Assert.All(handler.Requests, q => Assert.Null(q.Headers.Range));
    }

    [Fact]
    public async Task ACompressedResponseIsRecordedDecodedAndScrubbed()
    {
        var (sh, _) = CredentialHost();
        var recorder = sh.StartRecording();
        var r = await sh.RunAsync("curl -sS --compressed https://cred.test/echo");
        Assert.Equal("Authorization: [REDACTED]\n", r.Stdout);
        recorder.Stop();
        var file = new MemoryStream();
        recorder.Save(file);
        file.Position = 0;
        using var zip = new System.IO.Compression.ZipArchive(file);
        foreach (var e in zip.Entries)
        {
            using var reader = new StreamReader(e.Open(), Encoding.Latin1);
            var text = reader.ReadToEnd();
            Assert.DoesNotContain(Secret, text);
            if (text.Length > 1 && text[0] == '\x1f' && text[1] == '\x8b') Assert.Fail($"{e.FullName} holds a gzip body");
        }
    }

    [Fact]
    public async Task AnUnknownEncodingFromACredentialedHostFails()
    {
        var (sh, handler) = CredentialHost();
        handler.Override = (_, _) =>
        {
            var m = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Secret)) };
            m.Content.Headers.ContentEncoding.Add("br");
            return Task.FromResult(m);
        };
        var r = await sh.RunAsync("curl -sS https://cred.test/echo; echo $?");
        Assert.Equal("56\n", r.Stdout);
        Assert.DoesNotContain(Secret, r.Stderr);
    }

    [Fact]
    public async Task RegisterApiDecodesBeforeItScrubs()
    {
        var handler = new FixtureHandler();
        handler.Override = (req, _) =>
        {
            var ms = new MemoryStream();
            using (var z = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal)) z.Write(Encoding.UTF8.GetBytes($"token={req.Headers.Authorization}\n"));
            var m = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ms.ToArray()) };
            m.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(m);
        };
        var sh = new Shell().RegisterApi("api", new Dictionary<string, ApiEndpoint> { ["x"] = new("https://x.test") { Auth = () => "Bearer " + Secret } }, handler);
        var r = await sh.RunAsync("api get x /a");
        Assert.Equal("token=[REDACTED]\n", r.Stdout);
    }

    [Fact]
    public async Task ABlockedHostNamesTheAllowedSites()
    {
        var sh = new Shell().AllowWeb(new WebPolicy { Domains = [.. Enumerable.Range(1, 12).Select(i => $"s{i}.test")], Handler = new FixtureHandler() });
        var r = await sh.RunAsync("curl -sS https://evil.test/; wget -q https://evil.test/; echo $?");
        Assert.Equal("curl: (6) Blocked by the web policy: evil.test is not an allowed host (allowed: s1.test, s2.test, s3.test, s4.test, s5.test, " +
            "s6.test, s7.test, s8.test, s9.test, s10.test, and 2 more)\nwget: blocked by the web policy: evil.test is not an allowed host (allowed: s1.test, " +
            "s2.test, s3.test, s4.test, s5.test, s6.test, s7.test, s8.test, s9.test, s10.test, and 2 more)\n", r.Stderr);
        Assert.Equal("4\n", r.Stdout);
        r = await new Shell().RunAsync("curl -sS https://evil.test/"); // no policy: an offline machine
        Assert.Equal("curl: (6) Could not resolve host: evil.test\n", r.Stderr);
    }

    [Fact]
    public async Task AListedHostAllowsOnlyItsDefaultPortUnlessAPortIsListed()
    {
        var handler = new FixtureHandler();
        var sh = new Shell().AllowWeb(new WebPolicy { Domains = ["api.test", "other.test:8443"], AllowHttp = true, Handler = handler });
        var r = await sh.RunAsync("curl -sS https://api.test:22/; echo $?; curl -s https://api.test:443/hello; curl -s http://api.test/hello; " +
            "curl -s https://other.test:8443/hello; curl -sS https://other.test/; echo $?");
        Assert.Equal("7\nhello\nhello\nhello\n7\n", r.Stdout);
        Assert.Equal("curl: (7) Blocked by the web policy: api.test:22 is not an allowed host (allowed: api.test, other.test:8443)\n" +
            "curl: (7) Blocked by the web policy: other.test is not an allowed host (allowed: api.test, other.test:8443)\n", r.Stderr);
        Assert.Equal(3, handler.Requests.Count);
        var hidden = new Shell().AllowWeb(new WebPolicy { Domains = ["api.test"], Handler = handler, ExplainBlocked = false });
        r = await hidden.RunAsync("curl -sS https://api.test:22/");
        Assert.Equal("curl: (7) Failed to connect to api.test port 22 after 0 ms: Couldn't connect to server\n", r.Stderr);
    }

    [Fact]
    public async Task ACredentialGoesOnlyToItsPort()
    {
        var handler = new FixtureHandler();
        var sh = new Shell().AllowWeb(new WebPolicy
        {
            Domains = ["api.test", "api.test:8443"], Handler = handler,
            Credentials = { ["api.test"] = () => "Bearer one-secret-value", ["api.test:8443"] = () => "Bearer two-secret-value" },
        });
        await sh.RunAsync("curl -s https://api.test/echo; curl -s https://api.test:8443/echo");
        Assert.Equal("Bearer one-secret-value", handler.Seen[0]["authorization"]);
        Assert.Equal("Bearer two-secret-value", handler.Seen[1]["authorization"]);
        Assert.Throws<ArgumentException>(() => new Shell().AllowWeb(new WebPolicy { Domains = ["*.api.test"], Credentials = { ["x.api.test"] = () => "x" } }));
    }

    [Fact]
    public async Task TheAgentsAuthorizationStaysWithItsOrigin()
    {
        var handler = new FixtureHandler();
        handler.Override = (req, _) => Task.FromResult(req.RequestUri!.Port == 443
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://api.test:8443/x") } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok\n") });
        var sh = new Shell().AllowWeb(new WebPolicy { Domains = ["api.test", "api.test:8443"], Handler = handler });
        var r = await sh.RunAsync("curl -sL -H 'Authorization: Bearer mine' -H 'Cookie: a=b' https://api.test/go");
        Assert.Equal("ok\n", r.Stdout);
        Assert.Equal("Bearer mine", handler.Seen[0]["authorization"]);
        Assert.False(handler.Seen[1].ContainsKey("authorization"));
        Assert.False(handler.Seen[1].ContainsKey("cookie"));
    }

    [Fact]
    public async Task AReplayNeverGoesLiveEvenWhenConfigureAllowsTheWeb()
    {
        var sh = new Shell();
        var recorder = sh.StartRecording();
        await sh.RunAsync("curl -sS https://api.test/hello; echo $?");
        recorder.Stop();
        Assert.Null(recorder.Recording.Web);
        var handler = new FixtureHandler();
        var report = await recorder.Recording.ReplayAsync(configure: s => s.AllowWeb(new WebPolicy { Domains = ["api.test"], Handler = handler }));
        Assert.True(report.Matches, report.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AWildcardNeverMatchesAnAddress()
    {
        var handler = new FixtureHandler();
        var r = await new Shell().AllowWeb(new WebPolicy { Domains = ["*.2.3.4"], Handler = handler }).RunAsync("curl -s https://1.2.3.4/; echo $?");
        Assert.Equal("7\n", r.Stdout);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void HandlersThatKeepCookiesAreRefused() =>
        Assert.Throws<ArgumentException>(() => new Shell().AllowWeb(new WebPolicy { Handler = new HttpClientHandler { AllowAutoRedirect = false } }));

    [Fact]
    public void TheToolDescriptionFollowsThePolicy()
    {
        var sh = new Shell();
        Assert.Equal(BashTool.Description, BashTool.DescriptionFor(sh));
        sh.AllowWeb(new WebPolicy { Domains = ["api.github.com", "*.wikipedia.org"], Methods = ["get", "POST"] });
        var expected = BashTool.Description.Replace("Python, network access and package installation are not. ",
            "Python and package installation are not; `curl` and `wget` reach only these sites (methods: GET, POST): api.github.com, *.wikipedia.org. ");
        Assert.NotEqual(BashTool.Description, expected);
        Assert.Equal(expected, BashTool.DescriptionFor(sh));
        Assert.Equal(expected, sh.AsAIFunction().Description);
        Assert.Equal("Mine.", sh.AsAIFunction("Mine.").Description);
        var rec = sh.StartRecording();
        rec.Stop();
        Assert.Equal(BashTool.Description, BashTool.DescriptionFor(rec.Recording.CreateShell())); // a replay reaches no site
        sh.AllowWeb(new WebPolicy { Domains = [.. Enumerable.Range(1, 23).Select(i => $"s{i}.test")] });
        Assert.EndsWith("s20.test, and 3 more. Read Word, Excel, PowerPoint and HTML files with `to-markdown FILE`. " +
            "Other programs on this machine are live interfaces to this application's systems, so their actions are real: run `help` to list them and `<program> --help` for usage.",
            BashTool.DescriptionFor(sh));
    }

    [Fact]
    public void BadPoliciesAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new Shell().AllowWeb(new WebPolicy { Domains = ["exa mple.com"] }));
        Assert.Throws<ArgumentException>(() => new Shell().AllowWeb(new WebPolicy { Domains = ["a.com"], Credentials = new Dictionary<string, Func<string>> { ["b.com"] = () => "x" } }));
        Assert.Throws<ArgumentException>(() => new Shell().AllowWeb(new WebPolicy { Handler = new HttpClientHandler() }));
    }
}

/// <summary>The real connection path, against a loopback server a test-only handler lets through.</summary>
public class WebPinningTests
{
    [Fact]
    public async Task NamesResolveOnceAndConnectToTheCheckedAddress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var host = "";
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var reader = new StreamReader(stream);
            for (string? line; !string.IsNullOrEmpty(line = await reader.ReadLineAsync());)
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = line[5..].Trim();
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nConnection: close\r\n\r\nok\n"u8.ToArray());
        });
        int lookups = 0;
        try
        {
            Assert.False(WebSession.DefaultHandler.UseProxy); // the host's proxy settings are never used (not set in the
            Assert.False(WebSession.DefaultHandler.UseCookies); // environment here: other tests run beside this one)
            IPAddress[] Resolve(string name) { lookups++; return name == "pinned.test" ? [IPAddress.Loopback] : [IPAddress.Parse("169.254.169.254")]; }
            var domains = new[] { $"pinned.test:{port}", $"pinned.test:{port + 1}", "meta.test" };
            var sh = new Shell().AllowWeb(new WebPolicy { Domains = domains, AllowHttp = true, ExplainBlocked = false, Handler = WebSession.PinnedHandler(Resolve, allowLoopback: true) });
            var r = await sh.RunAsync($"curl -sS http://pinned.test:{port}/");
            Assert.Equal("ok\n", r.Stdout);
            Assert.Equal($"pinned.test:{port}", host);
            Assert.Equal(1, lookups);

            r = await sh.RunAsync("curl -sS http://meta.test/; echo $?; wget -q http://meta.test/; echo $?");
            Assert.Equal("6\n4\n", r.Stdout);
            Assert.Equal("curl: (6) Could not resolve host: meta.test\n", r.Stderr);

            // without allowLoopback, loopback is refused like any internal address
            sh = new Shell().AllowWeb(new WebPolicy { Domains = domains, AllowHttp = true, ExplainBlocked = false, Handler = WebSession.PinnedHandler(Resolve) });
            r = await sh.RunAsync($"curl -sS http://pinned.test:{port + 1}/");
            Assert.Equal($"curl: (6) Could not resolve host: pinned.test\n", r.Stderr);
            sh = new Shell().AllowWeb(new WebPolicy { Domains = ["meta.test"], AllowHttp = true, ExplainBlocked = true, Handler = WebSession.PinnedHandler(Resolve) });
            r = await sh.RunAsync("curl -sS http://meta.test/");
            Assert.Equal("curl: (6) Blocked by the web policy: meta.test resolves to a private or reserved address\n", r.Stderr);
        }
        finally
        {
            listener.Stop();
        }
        await serve;
    }
}
