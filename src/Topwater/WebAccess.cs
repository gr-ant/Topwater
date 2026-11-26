using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Topwater;

/// <summary>
/// The web access a shell's <c>curl</c> and <c>wget</c> get through <see cref="Shell.AllowWeb"/>. Without one the machine has
/// no network: every host fails to resolve. See "Web access" in docs/reference.md.
/// </summary>
public sealed class WebPolicy
{
    /// <summary>Host names the agent may reach: <c>api.github.com</c> exactly, or <c>*.wikipedia.org</c> for its subdomains (not the name itself).</summary>
    public IReadOnlyList<string> Domains { get; init; } = [];
    /// <summary>HTTP methods the agent may use; GET also allows HEAD.</summary>
    public IReadOnlyList<string> Methods { get; init; } = ["GET"];
    /// <summary>Permit plain <c>http://</c> as well as <c>https://</c>.</summary>
    public bool AllowHttp { get; init; }
    /// <summary>A response body is cut here (read as it streams in, never whole) and the command fails.</summary>
    public long MaxResponseBytes { get; init; } = 10_000_000;
    /// <summary>Requests (each redirect counts) in one tool call.</summary>
    public int MaxRequestsPerCall { get; init; } = 20;
    /// <summary>Requests over the shell's life.</summary>
    public int MaxRequests { get; init; } = 500;
    /// <summary>Time limit for each request; the call's own limit (MaxCallDuration) still applies.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>The most redirects one command follows; each is checked against this policy like the first URL.</summary>
    public int MaxRedirects { get; init; } = 10;
    /// <summary>The Authorization header for a host (exact name, one of <see cref="Domains"/>), called in host code for every
    /// request to it. The agent never sees it, cannot replace it, and it is scrubbed from what comes back.</summary>
    public IReadOnlyDictionary<string, Func<string>> Credentials { get; init; } = new Dictionary<string, Func<string>>();
    /// <summary>Say "blocked by the web policy" instead of failing like an unknown host (or an unreachable address).</summary>
    public bool ExplainBlocked { get; init; }
    /// <summary>Sends the requests instead of the network (tests, proxies). It must not follow redirects. The private-address
    /// check happens where the name is resolved, so a handler that connects somewhere itself must do it.</summary>
    public HttpMessageHandler? Handler { get; init; }
}

/// <summary>One request a recorded call made and what came back (or how it failed), stored so a replay never touches the network.</summary>
public sealed record WebExchange(string Method, string Url, int Status, string Reason, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body)
{
    /// <summary>How the request failed instead (resolve, blocked, connect, timeout, tls, recv); null when it got an answer.</summary>
    public string? Error { get; init; }
}

public sealed partial class Shell
{
    /// <summary>The web access in force (<see cref="AllowWeb"/>), or null: no network.</summary>
    internal WebSession? Web { get; private set; }

    /// <summary>
    /// Lets the agent's <c>curl</c> and <c>wget</c> reach the hosts in <paramref name="policy"/>, over https unless it allows http.
    /// Every URL and redirect is checked against it; names are resolved once, private, loopback, link-local and other
    /// internal addresses are refused, and the connection goes to the address that was checked. A fork does not inherit it.
    /// </summary>
    public Shell AllowWeb(WebPolicy policy)
    {
        Web = new WebSession(policy);
        return this;
    }

    internal void AllowWeb(WebSession session) => Web = session;
}

/// <summary>Why a request failed; each command turns it into its own message and exit status.</summary>
internal enum WebError { BadUrl, BadHostname, Protocol, Resolve, Connect, Timeout, Tls, Recv, TooLarge, Redirects, Policy, Replay, BadHeader }

internal sealed class WebFailure(WebError kind, string host = "", int port = 0, string? blocked = null) : Exception(kind.ToString())
{
    public WebError Kind { get; } = kind;
    public string Host { get; } = host;
    public int Port { get; } = port;
    /// <summary>Set when the policy refused it: what an explaining message says.</summary>
    public string? Blocked { get; init; } = blocked;
    public string Detail { get; init; } = "";
}

/// <summary>A URL as Topwater reads it, the same in every port: http or https, a host (IPv4 in any inet_aton form is
/// canonical dotted quad), a port and the path with its query. User info is kept for basic auth, never as the host.</summary>
internal sealed record WebUrl(string Scheme, string Host, int Port, string PathAndQuery, string? UserInfo, byte[]? Ip)
{
    public bool DefaultPort => Port == (Scheme == "https" ? 443 : 80);
    public string HostForUrl => Host.Contains(':') ? $"[{Host}]" : Host;
    public override string ToString() => $"{Scheme}://{HostForUrl}{(DefaultPort ? "" : ":" + Port)}{PathAndQuery}";

    /// <summary>Parses <paramref name="text"/>; a missing scheme means http, as in curl. Throws <see cref="WebFailure"/>.</summary>
    public static WebUrl Parse(string text)
    {
        int sep = text.IndexOf("://", StringComparison.Ordinal);
        string scheme = sep < 0 ? "http" : text[..sep].ToLowerInvariant(), rest = sep < 0 ? text : text[(sep + 3)..];
        if (sep >= 0 && (scheme.Length == 0 || !scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))) throw new WebFailure(WebError.BadUrl);
        if (scheme is not ("http" or "https")) throw new WebFailure(WebError.Protocol, scheme);
        if (rest.Any(c => c <= ' ' || c >= 127)) throw new WebFailure(WebError.BadUrl);
        int end = rest.IndexOfAny(['/', '?', '#']);
        string authority = end < 0 ? rest : rest[..end], tail = end < 0 ? "" : rest[end..];
        int hash = tail.IndexOf('#');
        if (hash >= 0) tail = tail[..hash];
        if (!tail.StartsWith('/')) tail = "/" + tail;
        string? user = null;
        int at = authority.IndexOf('@'); // the first @, as curl reads it: "a@b@host" has the host "b@host", which is refused
        if (at >= 0) { user = authority[..at]; authority = authority[(at + 1)..]; }
        string host, portText = "";
        if (authority.StartsWith('['))
        {
            int close = authority.IndexOf(']');
            if (close < 0) throw new WebFailure(WebError.BadUrl);
            host = authority[1..close].ToLowerInvariant();
            var after = authority[(close + 1)..];
            if (after.Length > 0) { if (!after.StartsWith(':')) throw new WebFailure(WebError.BadUrl); portText = after[1..]; }
            if (WebAddresses.ParseIPv6(host) is not { } v6) throw new WebFailure(WebError.BadUrl);
            return new(scheme, host, ParsePort(portText, scheme), tail, user, v6);
        }
        int colon = authority.IndexOf(':');
        host = (colon < 0 ? authority : authority[..colon]).ToLowerInvariant();
        if (colon >= 0) portText = authority[(colon + 1)..];
        if (host.EndsWith('.')) host = host[..^1];
        if (host.Length == 0 || host.Length > 253 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_') || host.Contains(".."))
            throw new WebFailure(WebError.BadHostname);
        var v4 = WebAddresses.ParseIPv4(host);
        return new(scheme, v4 is null ? host : string.Join('.', v4), ParsePort(portText, scheme), tail, user, v4);
    }

    static int ParsePort(string text, string scheme)
    {
        if (text.Length == 0) return scheme == "https" ? 443 : 80;
        if (text.Length > 5 || !text.All(char.IsAsciiDigit) || int.Parse(text) is var p && (p < 1 || p > 65535)) throw new WebFailure(WebError.BadUrl);
        return p;
    }

    /// <summary>A redirect's Location against this URL: absolute, scheme-relative, absolute-path or relative.</summary>
    public WebUrl Resolve(string location)
    {
        if (location.Contains("://")) return Parse(location);
        var origin = $"{Scheme}://{HostForUrl}{(DefaultPort ? "" : ":" + Port)}";
        if (location.StartsWith("//")) return Parse(Scheme + ":" + location);
        if (location.StartsWith('/')) return Parse(origin + location);
        var path = PathAndQuery.Split('?')[0];
        return Parse(origin + path[..(path.LastIndexOf('/') + 1)] + location);
    }
}

/// <summary>Which addresses the agent may never reach, whatever the policy says.</summary>
internal static class WebAddresses
{
    /// <summary>IPv4 the way inet_aton reads it: 1 to 4 parts, each decimal, 0x hex or 0 octal (127.1, 2130706433, 0x7f.0.0.1).</summary>
    public static byte[]? ParseIPv4(string s)
    {
        var parts = s.Split('.');
        if (parts.Length > 4) return null;
        var nums = new ulong[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            int radix = 10;
            if (p.StartsWith("0x") || p.StartsWith("0X")) { radix = 16; p = p[2..]; if (p.Length == 0) { nums[i] = 0; continue; } }
            else if (p.Length > 1 && p[0] == '0') { radix = 8; p = p[1..]; }
            if (p.Length == 0 || p.Length > 12) return null;
            ulong v = 0;
            foreach (var c in p)
            {
                int d = char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiHexDigitLower(c) ? c - 'a' + 10 : char.IsAsciiHexDigitUpper(c) ? c - 'A' + 10 : 99;
                if (d >= radix) return null;
                v = v * (ulong)radix + (ulong)d;
            }
            nums[i] = v;
        }
        ulong last = nums[^1];
        for (int i = 0; i < nums.Length - 1; i++) if (nums[i] > 255) return null;
        if (last >= 1UL << (8 * (5 - nums.Length))) return null;
        ulong value = last;
        for (int i = 0; i < nums.Length - 1; i++) value |= nums[i] << (8 * (3 - i));
        return [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    public static byte[]? ParseIPv6(string s) =>
        s.Contains(':') && !s.Contains('%') && IPAddress.TryParse(s, out var a) && a.AddressFamily == AddressFamily.InterNetworkV6 ? a.GetAddressBytes() : null;

    /// <summary>Loopback, private, link-local (169.254.169.254, the cloud metadata service), CGNAT, multicast, reserved,
    /// unspecified, documentation and benchmarking ranges; for IPv6 also ULA, site-local, Teredo, and any IPv4 it embeds.</summary>
    public static bool IsBlocked(byte[] a)
    {
        if (a.Length == 4)
        {
            byte x = a[0], y = a[1];
            return x is 0 or 10 or 127 || x >= 224 || x == 100 && (y & 0xC0) == 64 || x == 169 && y == 254 || x == 172 && (y & 0xF0) == 16
                || x == 192 && y == 168 || x == 192 && y == 0 && a[2] is 0 or 2 || x == 198 && (y & 0xFE) == 18 || x == 198 && y == 51 && a[2] == 100
                || x == 203 && y == 0 && a[2] == 113;
        }
        if (a.Length != 16) return true;
        bool Zero(int from, int to) { for (int i = from; i < to; i++) if (a[i] != 0) return false; return true; }
        if (Zero(0, 10) && a[10] == 0xff && a[11] == 0xff) return IsBlocked(a[12..]);               // ::ffff:a.b.c.d
        if (Zero(0, 12)) return true;                                                               // ::, ::1, ::a.b.c.d
        if (a[0] == 0 && a[1] == 0x64 && a[2] == 0xff && a[3] == 0x9b) return IsBlocked(a[12..]);   // 64:ff9b::/96 NAT64
        if (a[0] == 0x20 && a[1] == 0x02) return IsBlocked(a[2..6]);                                // 2002::/16 6to4
        if (a[0] == 0x20 && a[1] == 0x01 && (a[2] == 0 && a[3] == 0 || a[2] == 0x0d && a[3] == 0xb8)) return true; // Teredo, documentation
        return (a[0] & 0xFE) == 0xFC || a[0] == 0xFE && (a[1] & 0xC0) is 0x80 or 0xC0 || a[0] == 0xFF; // ULA, link/site-local, multicast
    }

    // test-only: let a test reach a loopback server through the real resolver and connection path. Not on WebPolicy, so
    // no application setting can switch the address check off.
    internal static Func<string, IPAddress[]>? ResolveForTests;
    internal static bool AllowLoopbackForTests;
}

internal sealed record WebResponse(WebUrl Url, string Method, int Status, string Reason, List<KeyValuePair<string, string>> Headers, byte[] Body, bool Truncated)
{
    public string? Header(string name) => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>One shell's web access: the policy, its counters, and the exchanges of the current call (for recordings), or the
/// recorded exchanges a replay serves instead of the network.</summary>
internal sealed class WebSession
{
    public const int MaxHeaderBytes = 64 * 1024;
    public const int MaxRequestBytes = 1_000_000; // shortcut: fixed upload cap like RegisterApi's; make it a policy option when a host needs more
    static readonly HashSet<string> DroppedHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive", "Upgrade", "TE", "Trailer", "Proxy-Authorization", "Proxy-Connection" };

    static readonly HttpClient DefaultClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None,
        MaxResponseHeadersLength = MaxHeaderBytes / 1024, PooledConnectionLifetime = TimeSpan.FromMinutes(1), ConnectCallback = ConnectPinnedAsync,
    }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    readonly HttpClient _client;
    readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    readonly List<string> _suffixes = [];
    readonly HashSet<string> _methods = new(StringComparer.Ordinal);
    readonly Dictionary<string, Func<string>> _credentials = new(StringComparer.Ordinal);
    readonly List<WebExchange> _log = [];
    readonly IReadOnlyList<WebExchange>? _replay;
    int _replayAt, _total, _thisCall;

    public WebPolicy Policy { get; }

    public WebSession(WebPolicy policy, IReadOnlyList<WebExchange>? replay = null)
    {
        Policy = policy;
        foreach (var d in policy.Domains)
        {
            var name = d.Trim().TrimEnd('.').ToLowerInvariant();
            bool wild = name.StartsWith("*.");
            var bare = wild ? name[2..] : name;
            if (bare.Length == 0 || !bare.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_') || bare.Contains('*'))
                throw new ArgumentException($"{d}: a domain is a host name, or *.name for its subdomains", nameof(policy));
            if (wild) _suffixes.Add("." + bare); else _exact.Add(bare);
        }
        foreach (var m in policy.Methods)
        {
            if (m.Length == 0 || !m.All(char.IsAsciiLetter)) throw new ArgumentException($"{m}: not an HTTP method", nameof(policy));
            _methods.Add(m.ToUpperInvariant());
        }
        if (_methods.Contains("GET")) _methods.Add("HEAD");
        foreach (var (host, auth) in policy.Credentials)
        {
            var h = host.Trim().TrimEnd('.').ToLowerInvariant();
            if (!_exact.Contains(h)) throw new ArgumentException($"{host}: credentials need the exact host name in Domains", nameof(policy));
            _credentials[h] = auth;
        }
        if (policy.MaxRedirects < 0 || policy.MaxRequests < 0 || policy.MaxRequestsPerCall < 0 || policy.MaxResponseBytes < 0)
            throw new ArgumentException("limits cannot be negative", nameof(policy));
        for (var h = policy.Handler; h != null; h = (h as DelegatingHandler)?.InnerHandler)
            if (h is HttpClientHandler { AllowAutoRedirect: true } or SocketsHttpHandler { AllowAutoRedirect: true })
                throw new ArgumentException("the handler follows redirects, which would skip the policy check of each hop; set AllowAutoRedirect = false", nameof(policy));
        _client = policy.Handler is { } handler ? new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan } : DefaultClient;
        _replay = replay;
    }

    /// <summary>A new tool call: its request count starts again, and its exchanges are collected afresh.</summary>
    public void BeginCall() { lock (_log) { _thisCall = 0; _log.Clear(); } }
    public List<WebExchange> TakeLog() { lock (_log) { var l = _log.ToList(); _log.Clear(); return l; } }
    public bool HasCredential(string host) => _credentials.ContainsKey(host);

    public bool HostAllowed(string host) => _exact.Contains(host) || _suffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal));

    /// <summary>Checks the URL against the policy, then sends one request (no redirects). Throws <see cref="WebFailure"/>.</summary>
    public async Task<WebResponse> SendAsync(WebUrl url, string method, List<KeyValuePair<string, string>> headers, byte[]? body, TimeSpan timeout, CancellationToken ct)
    {
        string Explain(string why) => Policy.ExplainBlocked ? why : null!;
        if (url.Ip is { } ip)
        {
            if (WebAddresses.IsBlocked(ip)) throw new WebFailure(WebError.Connect, url.Host, url.Port, Explain($"{url.Host} is a private or reserved address"));
            if (!_exact.Contains(url.Host)) throw new WebFailure(WebError.Connect, url.Host, url.Port, Explain($"{url.Host} is not an allowed host"));
        }
        else if (!HostAllowed(url.Host)) throw new WebFailure(WebError.Resolve, url.Host, url.Port, Explain($"{url.Host} is not an allowed host"));
        if (url.Scheme == "http" && !Policy.AllowHttp)
            throw new WebFailure(WebError.Protocol, "http") { Blocked = Policy.ExplainBlocked ? "only https:// URLs are allowed" : null };
        if (!_methods.Contains(method)) throw new WebFailure(WebError.Policy, blocked: $"{method} requests are not allowed");
        if (body is { Length: > MaxRequestBytes }) throw new WebFailure(WebError.Policy, blocked: $"the request body is larger than {MaxRequestBytes} bytes");
        if (headers.Any(h => h.Key.Length == 0 || !h.Key.All(c => c > ' ' && c < 127 && c != ':') || h.Value.Any(c => c is '\r' or '\n' or '\0'))
            || headers.Sum(h => h.Key.Length + h.Value.Length + 4) > 16 * 1024)
            throw new WebFailure(WebError.BadHeader);
        lock (_log)
        {
            if (_thisCall >= Policy.MaxRequestsPerCall) throw new WebFailure(WebError.Policy, blocked: $"the limit of {Policy.MaxRequestsPerCall} requests in one call was reached");
            if (_total >= Policy.MaxRequests) throw new WebFailure(WebError.Policy, blocked: $"the limit of {Policy.MaxRequests} requests for this session was reached");
            _thisCall++; _total++;
        }

        var sent = headers.Where(h => !DroppedHeaders.Contains(h.Key) && !h.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)).ToList();
        string[] secrets = [];
        if (_credentials.TryGetValue(url.Host, out var auth))
        {
            sent.RemoveAll(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)); // the agent cannot replace it
            string credential;
            try { credential = auth() ?? ""; }
            catch (Exception) { throw new WebFailure(WebError.Policy, blocked: $"the credentials for {url.Host} are not available"); } // the message may describe the secret
            if (credential.Any(char.IsControl)) throw new WebFailure(WebError.Policy, blocked: $"the credentials for {url.Host} are not a valid header value");
            if (credential.Length > 0) sent.Add(new("Authorization", credential));
            secrets = ApiCommandExtensions.SecretsOf(credential);
        }

        WebExchange exchange;
        if (_replay != null) exchange = NextRecorded(method, url.ToString());
        else exchange = await ExchangeAsync(url, method, sent, body, timeout, secrets, ct);
        lock (_log) _log.Add(exchange);
        if (exchange.Error is { } error)
            throw new WebFailure(Enum.Parse<WebError>(error == "blocked" ? "Resolve" : error, ignoreCase: true), url.Host, url.Port,
                error == "blocked" ? Explain($"{url.Host} resolves to a private or reserved address") : null)
            { Detail = ((long)timeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        bool truncated = exchange.Body.Length > Policy.MaxResponseBytes;
        return new(url, method, exchange.Status, exchange.Reason, [.. exchange.Headers], truncated ? exchange.Body[..(int)Policy.MaxResponseBytes] : exchange.Body, truncated);
    }

    WebExchange NextRecorded(string method, string url)
    {
        lock (_log)
        {
            if (_replayAt < _replay!.Count && _replay[_replayAt] is var next && next.Method == method && next.Url == url) { _replayAt++; return next; }
        }
        throw new WebFailure(WebError.Replay) { Detail = $"{method} {url}" };
    }

    async Task<WebExchange> ExchangeAsync(WebUrl url, string method, List<KeyValuePair<string, string>> headers, byte[]? body, TimeSpan timeout, string[] secrets, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(url.ToString()));
        if (body != null) request.Content = new ByteArrayContent(body);
        foreach (var (name, value) in headers)
        {
            bool content = name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);
            if (content) { request.Content ??= new ByteArrayContent([]); request.Content.Headers.Remove(name); }
            if (!(content ? request.Content!.Headers.TryAddWithoutValidation(name, value) : request.Headers.TryAddWithoutValidation(name, value)))
                throw new WebFailure(WebError.BadHeader);
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        WebExchange Failed(string kind) => new(method, url.ToString(), 0, "", [], []) { Error = kind };
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            var list = new List<KeyValuePair<string, string>>();
            foreach (var h in response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated))
                foreach (var v in h.Value) list.Add(new(h.Key, v));
            if (list.Sum(h => h.Key.Length + h.Value.Length + 4) > MaxHeaderBytes) return Failed("recv");
            var buffer = new MemoryStream();
            if (method != "HEAD")
            {
                // past the cap by the longest secret, so a credential cut in half at the cap is still scrubbed
                long cap = Policy.MaxResponseBytes + (secrets.Length > 0 ? secrets[0].Length : 0);
                await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
                var chunk = new byte[16384];
                for (int n; buffer.Length <= cap && (n = await stream.ReadAsync(chunk, limit.Token)) > 0;)
                    buffer.Write(chunk, 0, (int)Math.Min(n, cap + 1 - buffer.Length));
            }
            return new(method, url.ToString(), (int)response.StatusCode, response.ReasonPhrase ?? "",
                [.. list.Select(h => new KeyValuePair<string, string>(h.Key, Scrub(h.Value, secrets)))], ScrubBytes(buffer.ToArray(), secrets));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failed("timeout"); }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException or AuthenticationException or WebFailure)
        {
            for (Exception? x = e; x != null; x = x.InnerException)
            {
                if (x is WebFailure f) return Failed(f.Blocked != null ? "blocked" : f.Kind.ToString().ToLowerInvariant());
                if (x is AuthenticationException) return Failed("tls");
                if (x is SocketException s)
                    return Failed(s.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain ? "resolve" : s.SocketErrorCode == SocketError.TimedOut ? "timeout" : "connect");
            }
            return Failed("recv");
        }
    }

    static string Scrub(string s, string[] secrets) => secrets.Aggregate(s, (t, secret) => t.Replace(secret, "[REDACTED]", StringComparison.Ordinal));

    static byte[] ScrubBytes(byte[] data, string[] secrets)
    {
        foreach (var secret in secrets)
        {
            var needle = Encoding.UTF8.GetBytes(secret);
            if (data.AsSpan().IndexOf(needle) < 0) continue;
            var output = new MemoryStream();
            var rest = data.AsSpan();
            for (int i; (i = rest.IndexOf(needle)) >= 0; rest = rest[(i + needle.Length)..]) { output.Write(rest[..i]); output.Write("[REDACTED]"u8); }
            output.Write(rest);
            data = output.ToArray();
        }
        return data;
    }

    /// <summary>Resolves the name once, refuses it if any address is internal, and connects to an address it checked: the
    /// TLS handshake and Host header still use the name, and nothing resolves it a second time (DNS rebinding).</summary>
    static async ValueTask<Stream> ConnectPinnedAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        try { addresses = WebAddresses.ResolveForTests?.Invoke(host) ?? await Dns.GetHostAddressesAsync(host, ct); }
        catch (SocketException) { throw new WebFailure(WebError.Resolve, host); }
        if (addresses.Length == 0) throw new WebFailure(WebError.Resolve, host);
        foreach (var a in addresses)
        {
            var bytes = (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).GetAddressBytes();
            if (WebAddresses.IsBlocked(bytes) && !(WebAddresses.AllowLoopbackForTests && IPAddress.IsLoopback(a)))
                throw new WebFailure(WebError.Resolve, host, blocked: "private");
        }
        foreach (var a in addresses)
        {
            var socket = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(new IPEndPoint(a, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch (SocketException) { socket.Dispose(); }
        }
        throw new WebFailure(WebError.Connect, host);
    }

    /// <summary>
    /// Sends <paramref name="method"/> to <paramref name="url"/>, following redirects when <paramref name="follow"/> (up to
    /// <paramref name="maxRedirects"/>, never more than the policy's), each hop checked again. <paramref name="onHop"/> sees
    /// every response, the final one included. Throws <see cref="WebFailure"/>.
    /// </summary>
    public async Task<(WebResponse Response, int Redirects)> FetchAsync(WebUrl url, string method, string? forcedMethod, List<KeyValuePair<string, string>> headers, byte[]? body,
        bool follow, int maxRedirects, TimeSpan timeout, Action<WebResponse>? onHop, CancellationToken ct)
    {
        int limit = maxRedirects < 0 ? Policy.MaxRedirects : Math.Min(maxRedirects, Policy.MaxRedirects);
        var firstHost = url.Host;
        for (int redirects = 0; ; redirects++)
        {
            var hopHeaders = url.Host == firstHost ? headers
                : headers.Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).ToList();
            var response = await SendAsync(url, forcedMethod ?? method, hopHeaders, body, timeout, ct);
            onHop?.Invoke(response);
            if (!follow || response.Status is not (301 or 302 or 303 or 307 or 308) || response.Header("Location") is not { Length: > 0 } location) return (response, redirects);
            if (redirects >= limit) throw new WebFailure(WebError.Redirects, url.Host, url.Port) { Detail = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            url = url.Resolve(location);
            if (response.Status == 303 && method != "HEAD" || response.Status is 301 or 302 && method == "POST")
            {
                method = "GET";
                body = null;
                headers = headers.Where(h => !h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }
    }
}
