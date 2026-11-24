using System.Net;
using System.Net.Sockets;
using System.Text;
using Topwater;

namespace Topwater.Tests;

/// <summary>The `api` command: authenticated HTTP calls the agent can make but not redirect or read the credential of.</summary>
public class ApiCommandTests
{
    const string Token = "s3cr3t-T0KEN-value";

    sealed record Seen(string Method, Uri Uri, Dictionary<string, string> Headers, string Body);

    /// <summary>Records every request and answers with <see cref="Respond"/>.</summary>
    sealed class Stub : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new(request.Method.Method, request.RequestUri!, headers, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return Respond(request);
        }
    }

    static (Shell shell, Stub stub) Make(Action<Dictionary<string, ApiEndpoint>>? more = null)
    {
        var stub = new Stub();
        var endpoints = new Dictionary<string, ApiEndpoint>
        {
            ["jira"] = new("https://acme.atlassian.net/rest/api/3")
            {
                Description = "Issue tracker", Auth = () => $"Bearer {Token}", Paths = ["/issue/", "/search"],
            },
            ["crm"] = new("https://crm.example.com/v1/")
            {
                Description = "CRM", Auth = () => Token, AuthHeader = "X-Api-Key", Methods = ["GET", "POST"],
            },
        };
        more?.Invoke(endpoints);
        return (new Shell().RegisterApi("api", endpoints, stub), stub);
    }

    [Fact]
    public async Task GetsWithTheCredentialAndPipesTheBody()
    {
        var (sh, stub) = Make();
        var r = await sh.RunAsync("api get jira /issue/PROJ-1 --query 'fields=summary,status' -q 'x=a b&c=d#e' | jq -r .ok");
        Assert.Equal((0, "true\n"), (r.ExitCode, r.Stdout));
        var req = Assert.Single(stub.Requests);
        Assert.Equal("GET", req.Method);
        Assert.Equal("https://acme.atlassian.net/rest/api/3/issue/PROJ-1?fields=summary%2Cstatus&x=a%20b%26c%3Dd%23e", req.Uri.AbsoluteUri);
        Assert.Equal($"Bearer {Token}", req.Headers["Authorization"]);

        await sh.RunAsync("api get crm /contacts");
        Assert.Equal(Token, stub.Requests[1].Headers["X-Api-Key"]);
        Assert.False(stub.Requests[1].Headers.ContainsKey("Authorization"));
        Assert.Equal("https://crm.example.com/v1/contacts", stub.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task HelpListsEndpointsWithoutTheCredential()
    {
        var (sh, _) = Make();
        var help = (await sh.RunAsync("api --help")).Stdout;
        Assert.Contains("jira  Issue tracker", help);
        Assert.Contains("methods: GET  paths: /issue/, /search", help);
        Assert.Contains("crm  CRM", help);
        Assert.Contains("methods: GET, POST", help);
        Assert.DoesNotContain(Token, help);
        Assert.Contains("api", (await sh.RunAsync("help")).Stdout);
    }

    [Theory]
    [InlineData("https://evil.com/x")]
    [InlineData("//evil.com/x")]
    [InlineData("/issue//evil.com")]
    [InlineData("issue/1")]
    [InlineData(@"/\evil.com/x")]
    [InlineData(@"\\evil.com\x")]
    [InlineData("/issue/1@evil.com")]
    [InlineData("/issue/../../admin")]
    [InlineData("/issue/../admin")]
    [InlineData("/issue/%2e%2e/admin")]
    [InlineData("/issue/%2E%2E/%2e%2e/admin")]
    [InlineData("/issue/.%2e/admin")]
    [InlineData("/issue/%252e%252e/admin")]
    [InlineData("/issue/..;/admin")]
    [InlineData("/issue%2f..%2fadmin")]
    [InlineData("/issue/%zz")]
    [InlineData("/searchadmin")]
    [InlineData("/admin?x=/issue/")]
    [InlineData("/issue/1#frag")]
    [InlineData("/issue/１")]
    public async Task PathsCannotLeaveTheBaseOrTheAllowedPrefixes(string path)
    {
        var (sh, stub) = Make();
        sh.Environment["P"] = path;
        var r = await sh.RunAsync("api get jira \"$P\"");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("is not allowed", r.Stderr);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData("/x/../../y")]
    [InlineData("/%2e%2e/other")]
    [InlineData("/..")]
    public async Task WithoutPrefixesPathsStillCannotClimbAboveTheBase(string path)
    {
        var (sh, stub) = Make();
        sh.Environment["P"] = path;
        Assert.Equal(2, (await sh.RunAsync("api get crm \"$P\"")).ExitCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task ControlCharactersAreRefusedEverywhere()
    {
        var (sh, stub) = Make();
        Assert.Equal(2, (await sh.RunAsync("api get jira $'/issue/1\\r\\nX-Evil: 1'")).ExitCode);
        Assert.Equal(2, (await sh.RunAsync("api get jira /issue/%0d%0aX")).ExitCode);
        Assert.Equal(2, (await sh.RunAsync("api get jira /issue/1 -H $'Accept: a\\r\\nAuthorization: x'")).ExitCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task DotSegmentsInsideTheAllowedAreaResolve()
    {
        var (sh, stub) = Make();
        Assert.Equal(0, (await sh.RunAsync("api get jira /issue/A/../B/./C")).ExitCode);
        Assert.Equal(0, (await sh.RunAsync("api get jira /search")).ExitCode);
        Assert.Equal(0, (await sh.RunAsync("api get jira '/search/x?jql=project%3DX'")).ExitCode);
        Assert.Equal(["/rest/api/3/issue/B/C", "/rest/api/3/search", "/rest/api/3/search/x"], stub.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.Equal("?jql=project%3DX", stub.Requests[2].Uri.Query);
    }

    [Fact]
    public async Task MethodsAreAllowListedAndGetIsTheDefault()
    {
        var (sh, stub) = Make();
        foreach (var cmd in new[] { "api post jira /issue --data '{}'", "api delete jira /issue/1", "api put crm /contacts/1 -d x", "api 'GET /x HTTP/1.1' crm /a" })
        {
            var r = await sh.RunAsync(cmd);
            Assert.Equal(2, r.ExitCode);
            Assert.Contains("not allowed", r.Stderr);
        }
        Assert.Empty(stub.Requests);
        Assert.Equal(0, (await sh.RunAsync("api POST crm /contacts -d '{\"a\":1}'")).ExitCode);
        Assert.Equal(("POST", "{\"a\":1}", "application/json"), (stub.Requests[0].Method, stub.Requests[0].Body, stub.Requests[0].Headers["Content-Type"]));
    }

    [Fact]
    public async Task BodyFromFileAndStdin()
    {
        var (sh, stub) = Make();
        sh.Fs.WriteAllText("/home/agent/c.json", "{\"from\":\"file\"}");
        Assert.Equal(0, (await sh.RunAsync("api post crm /contacts --data @c.json")).ExitCode);
        Assert.Equal(0, (await sh.RunAsync("echo '{\"from\":\"stdin\"}' | api post crm /contacts --data - -H 'content-type: text/plain'")).ExitCode);
        Assert.Equal("{\"from\":\"file\"}", stub.Requests[0].Body);
        Assert.Equal(("{\"from\":\"stdin\"}\n", "text/plain"), (stub.Requests[1].Body, stub.Requests[1].Headers["Content-Type"]));

        var missing = await sh.RunAsync("api post crm /contacts --data @nope.json");
        Assert.Equal(2, missing.ExitCode);
        var root = await sh.RunAsync("api post crm /contacts --data @/root/secret");
        Assert.Equal(2, root.ExitCode);
        sh.Fs.WriteAllText("/home/agent/big", new string('x', 1_000_001));
        Assert.Equal(2, (await sh.RunAsync("api post crm /contacts --data @big")).ExitCode);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Theory]
    [InlineData("Authorization: Bearer mine")]
    [InlineData("X-Api-Key: mine")]
    [InlineData("Cookie: a=b")]
    [InlineData("Host: evil.com")]
    [InlineData("Proxy-Authorization: x")]
    [InlineData("Transfer-Encoding: chunked")]
    [InlineData("Content-Length: 5")]
    public async Task TheAgentCannotSetSensitiveHeaders(string header)
    {
        var (sh, stub) = Make();
        sh.Environment["H"] = header;
        var r = await sh.RunAsync("api get crm /contacts -H \"$H\"");
        Assert.Equal(2, r.ExitCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task AllowedHeadersPassThrough()
    {
        var (sh, stub) = Make();
        Assert.Equal(0, (await sh.RunAsync("api get crm /contacts -H 'accept: text/csv'")).ExitCode);
        Assert.Equal("text/csv", stub.Requests[0].Headers["Accept"]);
    }

    [Fact]
    public async Task EchoedCredentialsAreScrubbed()
    {
        var (sh, stub) = Make();
        stub.Respond = _ =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent($"{{\"you_sent\":\"Bearer {Token}\",\"raw\":\"{Token}\"}}"), ReasonPhrase = $"Bad {Token}" };
            res.Headers.Add("X-Echo", Token);
            return res;
        };
        var r = await sh.RunAsync("api get jira /issue/1 -i");
        Assert.Equal(1, r.ExitCode);
        Assert.DoesNotContain(Token, r.Stdout + r.Stderr);
        Assert.Contains("\"raw\":\"[REDACTED]\"", r.Stdout);
        Assert.Contains("X-Echo: [REDACTED]", r.Stdout);
        Assert.Contains("HTTP 401", r.Stderr);
    }

    [Fact]
    public async Task ACredentialCutByTheSizeCapIsStillScrubbed()
    {
        var (sh, stub) = Make(e => e["big"] = new("https://big.example") { Auth = () => Token, AuthHeader = "X-Key", MaxResponseBytes = 100 });
        stub.Respond = _ => new(HttpStatusCode.OK) { Content = new StringContent(new string('a', 95) + Token + new string('b', 5000)) };
        var r = await sh.RunAsync("api get big /x");
        Assert.Equal(1, r.ExitCode);
        Assert.DoesNotContain(Token[..5], r.Stdout);
        Assert.Equal(101, r.Stdout.Length);   // the cap plus a newline
        Assert.Contains("truncated at 100 bytes", r.Stderr);
    }

    [Fact]
    public async Task ErrorsAndRedirectsExitOneWithTheBodyKept()
    {
        var (sh, stub) = Make();
        stub.Respond = _ => new(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"no such issue"}""") };
        var r = await sh.RunAsync("api get jira /issue/X | jq -r .error");
        Assert.Equal("no such issue\n", r.Stdout);
        Assert.Contains("api: HTTP 404 Not Found", r.Stderr);
        Assert.Equal(1, (await sh.RunAsync("api get jira /issue/X")).ExitCode);

        stub.Respond = _ => { var res = new HttpResponseMessage(HttpStatusCode.Found); res.Headers.Location = new Uri("https://evil.com/steal"); return res; };
        r = await sh.RunAsync("api get jira /issue/X");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("HTTP 302 Found (redirect to https://evil.com/steal not followed)", r.Stderr);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task FailuresBecomeOneLineErrors()
    {
        var (sh, stub) = Make();
        stub.Respond = _ => throw new HttpRequestException("boom", new SocketException((int)SocketError.HostNotFound));
        var r = await sh.RunAsync("api get jira /issue/1");
        Assert.Equal(1, r.ExitCode);
        Assert.StartsWith("api: GET jira /issue/1 failed:", r.Stderr);
        Assert.Single(r.Stderr.Trim().Split('\n'));

        stub.Respond = _ => throw new InvalidOperationException($"handler bug, header was Bearer {Token}");
        r = await sh.RunAsync("api get jira /issue/1");
        Assert.Equal((1, "api: GET jira /issue/1 failed: InvalidOperationException\n"), (r.ExitCode, r.Stderr));
    }

    [Fact]
    public async Task TheTimeoutIsEnforcedByTheCommand()
    {
        var stub = new SlowHandler();
        var sh = new Shell().RegisterApi("api", new Dictionary<string, ApiEndpoint> { ["s"] = new("https://s.example") { Timeout = TimeSpan.FromMilliseconds(100) } }, stub);
        var r = await sh.RunAsync("api get s /x");
        Assert.Equal((1, "api: s did not answer within 0.1 s\n"), (r.ExitCode, r.Stderr));
    }

    sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task AuthFailuresDoNotLeakTheirMessage()
    {
        var (sh, stub) = Make(e => e["vault"] = new("https://v.example") { Auth = () => throw new InvalidOperationException($"vault said {Token}") });
        var r = await sh.RunAsync("api get vault /x");
        Assert.Equal((1, "api: the credentials for vault are not available\n"), (r.ExitCode, r.Stderr));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task UsageErrorsExitTwoWithoutACall()
    {
        var (sh, stub) = Make();
        foreach (var cmd in new[] { "api get", "api get nope /x", "api get jira /issue/1 --bogus", "api get jira /issue/1 -q novalue", "api get jira /issue/1 extra" })
            Assert.Equal(2, (await sh.RunAsync(cmd)).ExitCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void RegistrationRefusesUnsafeConfiguration()
    {
        static void Reg(ApiEndpoint e, HttpMessageHandler? h = null) => new Shell().RegisterApi("api", new Dictionary<string, ApiEndpoint> { ["x"] = e }, h);
        Assert.Throws<ArgumentException>(() => Reg(new("http://plain.example")));
        Assert.Throws<ArgumentException>(() => Reg(new("https://user:pw@h.example")));
        Assert.Throws<ArgumentException>(() => Reg(new("https://h.example/?k=v")));
        Assert.Throws<ArgumentException>(() => Reg(new("/relative")));
        Assert.Throws<ArgumentException>(() => Reg(new("https://h.example") { AuthHeader = "Accept" }));
        Assert.Throws<ArgumentException>(() => Reg(new("https://h.example"), new HttpClientHandler()));
        Assert.Throws<ArgumentException>(() => Reg(new("https://h.example"), new LoggingHandler { InnerHandler = new SocketsHttpHandler() }));
        Reg(new("http://localhost") { AllowHttp = true }, new HttpClientHandler { AllowAutoRedirect = false });
    }

    sealed class LoggingHandler : DelegatingHandler;

    [Fact]
    public async Task RealSocketRoundTripWithoutFollowingRedirects()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var seen = new List<string>();
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var c = await listener.GetContextAsync();
                seen.Add($"{c.Request.Url!.AbsolutePath} {c.Request.Headers["Authorization"]}");
                if (c.Request.Url.AbsolutePath.EndsWith("/moved")) { c.Response.StatusCode = 301; c.Response.RedirectLocation = $"http://127.0.0.1:{port}/elsewhere"; }
                else { var b = Encoding.UTF8.GetBytes($"[\"echo {c.Request.Headers["Authorization"]}\"]"); c.Response.OutputStream.Write(b); }
                c.Response.Close();
            }
        });
        var sh = new Shell().RegisterApi("api", new Dictionary<string, ApiEndpoint>
        {
            ["local"] = new($"http://localhost:{port}/api") { AllowHttp = true, Auth = () => $"Bearer {Token}" },
        });
        var ok = await sh.RunAsync("api get local /items | jq -r '.[0]'");
        Assert.Equal((0, "echo [REDACTED]\n"), (ok.ExitCode, ok.Stdout));
        var moved = await sh.RunAsync("api get local /moved");
        Assert.Equal(1, moved.ExitCode);
        Assert.Contains("HTTP 301", moved.Stderr);
        Assert.Contains("not followed", moved.Stderr);
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([$"/api/items Bearer {Token}", $"/api/moved Bearer {Token}"], seen);
    }
}
