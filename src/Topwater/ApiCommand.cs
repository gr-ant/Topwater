using System.Net;
using System.Text;

namespace Topwater;

/// <summary>
/// One HTTP API the agent may call through <see cref="ApiCommandExtensions.RegisterApi"/>. The agent picks it by alias and
/// supplies only a path under <see cref="BaseUrl"/>; the host, scheme, port and credential are fixed here.
/// </summary>
public sealed class ApiEndpoint(string baseUrl)
{
    public string BaseUrl { get; } = baseUrl;
    public string? Description { get; init; }
    /// <summary>Returns the credential header's value (e.g. <c>"Bearer " + token</c>). Called in host code for every request; never shown to the agent.</summary>
    public Func<string>? Auth { get; init; }
    /// <summary>The header <see cref="Auth"/> fills, e.g. <c>"X-Api-Key"</c>.</summary>
    public string AuthHeader { get; init; } = "Authorization";
    /// <summary>HTTP methods the agent may use. Read-only by default.</summary>
    public IReadOnlyList<string> Methods { get; init; } = ["GET"];
    /// <summary>Allowed path prefixes relative to the base URL, checked after resolving <c>.</c> and <c>..</c>. Null allows every path under it.</summary>
    public IReadOnlyList<string>? Paths { get; init; }
    /// <summary>Permit a plain <c>http://</c> base URL (local test servers). The credential then travels unencrypted.</summary>
    public bool AllowHttp { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Response bodies beyond this are dropped, with a note on stderr.</summary>
    public int MaxResponseBytes { get; init; } = 1_000_000;
}

public static class ApiCommandExtensions
{
    // headers that cannot carry a credential, change the destination or smuggle a second request
    static readonly string[] AllowedHeaders = ["Accept", "Accept-Language", "Content-Type", "If-Match", "If-None-Match"];
    const int MaxRequestBytes = 1_000_000; // shortcut: fixed upload cap, make it an ApiEndpoint option when a host needs bigger uploads

    static readonly HttpClient DefaultClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Registers <paramref name="program"/> (e.g. <c>api</c>) so the agent can call the given HTTP APIs by alias:
    /// <c>api get jira /issue/PROJ-1</c>. The credential is added in host code and scrubbed from what comes back; the agent
    /// can only choose an alias, an allowed method, and a path under the base URL that matches <see cref="ApiEndpoint.Paths"/>.
    /// Redirects are never followed: a 3xx is reported with its Location and exit code 1.
    /// </summary>
    /// <param name="handler">Sends the requests (tests, proxies). It must not follow redirects itself.</param>
    public static Shell RegisterApi(this Shell shell, string program, IReadOnlyDictionary<string, ApiEndpoint> endpoints, HttpMessageHandler? handler = null)
    {
        var bases = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var (alias, e) in endpoints)
        {
            if (!Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && !(e.AllowHttp && uri.Scheme == Uri.UriSchemeHttp))
                throw new ArgumentException($"{alias}: the base URL must be an absolute https:// URL (set AllowHttp for http://)", nameof(endpoints));
            if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException($"{alias}: the base URL must not contain user info, a query or a fragment", nameof(endpoints));
            if (e.Methods.Any(m => !IsToken(m)) || BadAuthHeader(e.AuthHeader))
                throw new ArgumentException($"{alias}: invalid method or credential header", nameof(endpoints));
            bases[alias] = uri;
        }
        for (var h = handler; h != null; h = (h as DelegatingHandler)?.InnerHandler)
            if (h is HttpClientHandler { AllowAutoRedirect: true } or SocketsHttpHandler { AllowAutoRedirect: true })
                throw new ArgumentException("the handler follows redirects, which would send the credential to wherever they point; set AllowAutoRedirect = false", nameof(handler));
        var client = handler is null ? DefaultClient : new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        string Help()
        {
            var sb = new StringBuilder("Call this application's HTTP APIs. Credentials are added for you.\n\n");
            sb.Append($"Usage: {program} METHOD ENDPOINT PATH [options]\n  e.g. {program} get {endpoints.Keys.FirstOrDefault() ?? "NAME"} /path --query key=value | jq .\n\nEndpoints:\n");
            foreach (var (alias, e) in endpoints)
            {
                sb.Append($"  {alias}  {e.Description}\n      {bases[alias].AbsoluteUri.TrimEnd('/')}  methods: {string.Join(", ", e.Methods.Select(m => m.ToUpperInvariant()))}");
                sb.Append(e.Paths is { Count: > 0 } p ? $"  paths: {string.Join(", ", p)}\n" : "\n");
            }
            sb.Append("\nOptions:\n");
            sb.Append("  -d, --data BODY        request body; @FILE reads a file, - reads stdin (Content-Type defaults to application/json)\n");
            sb.Append("  -q, --query KEY=VALUE  add a query parameter (repeatable)\n");
            sb.Append($"  -H, --header 'NAME: VALUE'  one of {string.Join(", ", AllowedHeaders)}\n");
            sb.Append("  -i, --include          print the status line and response headers before the body\n");
            sb.Append("\nThe response body goes to stdout. A status outside 2xx exits 1 with the status on stderr; redirects are not followed.\n");
            return sb.ToString();
        }

        return shell.Register(program, async ctx =>
        {
            var args = ctx.Args;
            if (args.Count == 0 || args.Any(a => a is "--help" or "-h") || args[0] == "help") { ctx.Stdout.Write(Help()); return 0; }
            int Usage(string message)
            {
                ctx.Stderr.WriteLine($"{program}: {message}");
                ctx.Stderr.WriteLine($"Try `{program} --help`.");
                return 2;
            }

            var positional = new List<string>();
            var query = new List<string>();
            var headers = new List<(string Name, string Value)>();
            string? data = null;
            bool include = false;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i], flag = arg, inline = "";
                int eq = arg.IndexOf('=');
                bool hasInline = arg.StartsWith("--") && eq > 0;
                if (hasInline) { flag = arg[..eq]; inline = arg[(eq + 1)..]; }
                string? Next() => hasInline ? inline : ++i < args.Count ? args[i] : null;
                switch (flag)
                {
                    case "-i" or "--include": include = true; break;
                    case "-d" or "--data":
                        if ((data = Next()) is null) return Usage($"{flag} needs a value");
                        break;
                    case "-q" or "--query":
                        if (Next() is not { } kv || kv.IndexOf('=') is not (var at and > 0)) return Usage($"{flag} expects KEY=VALUE");
                        // escaped, so a value cannot add parameters, a fragment or a line break
                        query.Add(Uri.EscapeDataString(kv[..at]) + "=" + Uri.EscapeDataString(kv[(at + 1)..]));
                        break;
                    case "-H" or "--header":
                        if (Next() is not { } hv || hv.IndexOf(':') is not (var colon and > 0)) return Usage($"{flag} expects 'NAME: VALUE'");
                        var name = AllowedHeaders.FirstOrDefault(h => h.Equals(hv[..colon].Trim(), StringComparison.OrdinalIgnoreCase));
                        var value = hv[(colon + 1)..].Trim();
                        if (name is null) return Usage($"header '{hv[..colon].Trim()}' is not allowed; allowed: {string.Join(", ", AllowedHeaders)}");
                        if (value.Any(char.IsControl)) return Usage("header values cannot contain control characters");
                        headers.Add((name, value));
                        break;
                    default:
                        if (arg.StartsWith('-') && arg.Length > 1) return Usage($"unknown option {arg}");
                        positional.Add(arg);
                        break;
                }
            }
            if (positional.Count != 3) return Usage("expected METHOD ENDPOINT PATH, e.g. " + $"`{program} get {endpoints.Keys.FirstOrDefault() ?? "NAME"} /path`");
            var (method, alias, path) = (positional[0].ToUpperInvariant(), positional[1], positional[2]);
            if (!endpoints.TryGetValue(alias, out var ep)) return Usage($"unknown endpoint '{alias}'; endpoints: {string.Join(", ", endpoints.Keys)}");
            if (!IsToken(method) || !ep.Methods.Any(m => m.Equals(method, StringComparison.OrdinalIgnoreCase)))
                return Usage($"{method} is not allowed for {alias}; allowed: {string.Join(", ", ep.Methods.Select(m => m.ToUpperInvariant()))}");
            var baseUri = bases[alias];
            if (BuildUri(baseUri, ep.Paths, path, query) is not { } uri) return Usage($"path '{Printable(path)}' is not allowed for {alias}" + (ep.Paths is { Count: > 0 } ps ? $"; allowed: {string.Join(", ", ps)}" : ""));

            byte[]? body = null;
            if (data is not null)
            {
                try
                {
                    body = data == "-" ? Encoding.UTF8.GetBytes(ctx.StdinText)
                        : data.StartsWith('@') ? ctx.Fs.ReadAllBytes(ctx.ResolvePath(data[1..]))
                        : Encoding.UTF8.GetBytes(data);
                }
                catch (Exception e) when (e is ShellException or IOException or UnauthorizedAccessException) { return Usage(e.Message); }
                if (body.Length > MaxRequestBytes) return Usage($"the request body is {body.Length} bytes; the limit is {MaxRequestBytes}");
            }

            string credential;
            try { credential = ep.Auth?.Invoke() ?? ""; }
            catch (Exception) { ctx.Stderr.WriteLine($"{program}: the credentials for {alias} are not available"); return 1; }  // the message may describe the secret
            var secrets = new[] { credential, credential.IndexOf(' ') is var sp and > 0 ? credential[(sp + 1)..].Trim() : "" }
                .Where(s => s.Length >= 8).Distinct().OrderByDescending(s => s.Length).ToArray();  // a very short "secret" would shred ordinary output
            string Scrub(string s) => secrets.Aggregate(s, (t, secret) => t.Replace(secret, "[REDACTED]", StringComparison.Ordinal));

            using var request = new HttpRequestMessage(new HttpMethod(method), uri);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            }
            foreach (var (name, value) in headers)
            {
                if (name == "Content-Type") { request.Content ??= new ByteArrayContent([]); request.Content.Headers.Remove(name); }
                if (!(name == "Content-Type" ? request.Content!.Headers.TryAddWithoutValidation(name, value) : request.Headers.TryAddWithoutValidation(name, value)))
                    return Usage($"invalid {name} header");
            }
            if (credential.Length > 0 && (credential.Any(char.IsControl) || !request.Headers.TryAddWithoutValidation(ep.AuthHeader, credential)))
            { ctx.Stderr.WriteLine($"{program}: the credentials for {alias} are not a valid header value"); return 1; }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            timeout.CancelAfter(ep.Timeout);
            string text;
            HttpResponseMessage response;
            bool truncated;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                using (response)
                {
                    // read past the cap by the longest secret, so a credential cut in half at the cap still gets scrubbed
                    int limit = ep.MaxResponseBytes + (secrets.Length > 0 ? secrets[0].Length : 0);
                    var buffer = new MemoryStream();
                    await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token))
                    {
                        var chunk = new byte[16384];
                        for (int n; buffer.Length <= limit && (n = await stream.ReadAsync(chunk, timeout.Token)) > 0;)
                            buffer.Write(chunk, 0, (int)Math.Min(n, limit + 1 - buffer.Length));
                    }
                    truncated = buffer.Length > ep.MaxResponseBytes;
                    text = Scrub(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
                    if (truncated && text.Length > ep.MaxResponseBytes) text = text[..ep.MaxResponseBytes];
                }
            }
            catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { ctx.Stderr.WriteLine($"{program}: {alias} did not answer within {ep.Timeout.TotalSeconds:0.#} s"); return 1; }
            catch (Exception e)
            {
                var reason = e is HttpRequestException ? (e.InnerException?.Message ?? e.Message) : e.GetType().Name;
                ctx.Stderr.WriteLine(Scrub($"{program}: {method} {alias} {Printable(path)} failed: {reason.ReplaceLineEndings(" ")}"));
                return 1;
            }

            int status = (int)response.StatusCode;
            var statusLine = $"HTTP {status} {response.ReasonPhrase}".TrimEnd();
            if (include)
            {
                var sb = new StringBuilder(statusLine).Append('\n');
                foreach (var h in response.Headers.Concat(response.Content.Headers))
                    sb.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).Append('\n');
                ctx.Stdout.Write(Scrub(sb.Append('\n').ToString()));
            }
            ctx.Stdout.Write(text.Length == 0 || text.EndsWith('\n') ? text : text + "\n");
            if (truncated) ctx.Stderr.WriteLine($"{program}: response truncated at {ep.MaxResponseBytes} bytes");
            if (status is >= 200 and < 300) return truncated ? 1 : 0;
            ctx.Stderr.WriteLine(Scrub(status is >= 300 and < 400
                ? $"{program}: {statusLine} (redirect to {response.Headers.Location?.OriginalString ?? "nowhere"} not followed)"
                : $"{program}: {statusLine}"));
            return 1;
        }, $"call HTTP APIs ({string.Join(", ", endpoints.Keys)}); run `{program} --help`", $"{program} METHOD ENDPOINT PATH [options]");
    }

    static bool IsToken(string s) => s.Length > 0 && s.All(char.IsAsciiLetter);
    // the agent may set the allowed headers, so the credential cannot live in one of them
    static bool BadAuthHeader(string name) => name.Length == 0 || name.Any(c => c <= ' ' || c >= 127 || c == ':') ||
        AllowedHeaders.Contains(name, StringComparer.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase);
    static string Printable(string s) => new(s.Select(c => char.IsControl(c) ? '?' : c).ToArray());

    /// <summary>
    /// The URL for <paramref name="path"/> under <paramref name="baseUri"/>, or null when the path could reach anything else:
    /// another origin, a place above the base path, or outside the allowed prefixes.
    /// </summary>
    static Uri? BuildUri(Uri baseUri, IReadOnlyList<string>? allowed, string path, IEnumerable<string> extraQuery)
    {
        int q = path.IndexOf('?');
        string rawPath = q < 0 ? path : path[..q], rawQuery = q < 0 ? "" : path[(q + 1)..];
        // a leading single slash and nothing that can start an authority, user info, fragment, or a second line
        if (!rawPath.StartsWith('/') || rawPath.StartsWith("//") || path.Any(c => c is '\\' or '@' or '#' || char.IsControl(c) || c > 127)) return null;
        if (rawQuery.Contains('%') && !IsCleanlyEscaped(rawQuery)) return null;

        var segments = new List<string>();
        var parts = rawPath[1..].Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            var seg = parts[i];
            if (seg.Length == 0 && i < parts.Length - 1) return null;  // "//" anywhere could read as an authority
            if (seg.Contains('%') && !IsCleanlyEscaped(seg)) return null;
            var decoded = Uri.UnescapeDataString(seg);
            // decoded once and checked, so %2e%2e, %2f and double-encoded (%252e) tricks cannot reach a server that decodes again
            if (decoded.Contains('/') || decoded.Contains('%') || decoded.Any(c => c == '\\' || char.IsControl(c))) return null;
            var dots = decoded.Split(';')[0];
            if (dots is "." or "..")
            {
                if (dots != decoded) return null;  // "..;" is ".." to some Java servers
                if (dots == "..") { if (segments.Count == 0) return null; segments.RemoveAt(segments.Count - 1); }
                if (i == parts.Length - 1) segments.Add("");   // "/a/." is the directory "/a/"
                continue;
            }
            segments.Add(seg);
        }
        var relative = "/" + string.Join('/', segments);
        if (allowed is { Count: > 0 } && !allowed.Any(p => UnderPrefix(relative, p))) return null;

        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var queryText = string.Join('&', new[] { rawQuery }.Where(s => s.Length > 0).Concat(extraQuery));
        var text = baseUri.GetLeftPart(UriPartial.Authority) + basePath + relative + (queryText.Length > 0 ? "?" + queryText : "");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        // belt and braces: whatever the parsing above missed, the result must still be the same origin under the base path
        bool same = uri.Scheme == baseUri.Scheme && uri.IdnHost == baseUri.IdnHost && uri.Port == baseUri.Port && uri.UserInfo.Length == 0
            && (uri.AbsolutePath == basePath || uri.AbsolutePath.StartsWith(basePath + "/", StringComparison.Ordinal));
        return same ? uri : null;
    }

    /// <summary>Every % starts a valid escape.</summary>
    static bool IsCleanlyEscaped(string s)
    {
        for (int i = s.IndexOf('%'); i >= 0; i = s.IndexOf('%', i + 1))
            if (i + 2 >= s.Length || !Uri.IsHexDigit(s[i + 1]) || !Uri.IsHexDigit(s[i + 2])) return false;
        return true;
    }

    /// <summary>Prefix match on whole segments: "/search" allows "/search" and "/search/x" but not "/searchadmin".</summary>
    static bool UnderPrefix(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) && (prefix.EndsWith('/') || path.Length == prefix.Length || path[prefix.Length] == '/');
}
