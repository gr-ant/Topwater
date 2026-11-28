// What the connectors share (docs/connectors/README.md): one HTTP client with backoff and bounds for a web API, record
// fields shown as front matter (a SharePoint list item), and names made safe for a file system.
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Topwater.Connectors;

/// <summary>An answer with an error status: the status, the service's error code and message (both cut short).</summary>
internal sealed class ApiError(int status, string code, string message) : Exception($"HTTP {status} {code}: {message}")
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Text { get; } = message;
}

internal static partial class ConnectorCommon
{
    public const int MaxRetries = 4; // tries after the first, for a rate limit or a server that asks to retry
    public const double MaxRetryAfter = 60; // seconds; a longer Retry-After fails the request instead of holding the call
    public const int MaxResponse = 32_000_000; // bytes of one JSON response
    public const int MaxJsonDepth = 64;
    public const string Slash = "∕"; // DIVISION SLASH stands for "/" in a name made from a title
    public const int NameBytes = 200; // a name made from a title is cut to this, leaving room for " (2)" and an extension

    // What the shared pieces say; {service} is the connector's name for its service.
    public static readonly Dictionary<string, string> Messages = new()
    {
        ["noAnswer"] = "{service} did not answer",
        ["noAnswerWrite"] = "{service} did not answer; the change may or may not have been made",
        ["busy"] = "{service} is busy (HTTP {status}); tried {tries} times",
        ["waitTooLong"] = "{service} asked to wait {seconds} seconds (HTTP {status})",
        ["responseTooLarge"] = "{service} sent a response too large to use",
        ["notJson"] = "{service} sent a response that isn't a JSON object (HTTP {status})",
        ["frontMatterOpen"] = "{path}: front matter: no closing ---",
        ["frontMatterLine"] = "{path}: front matter: one `name: value` per line",
    };

    /// <summary>A message template with its {names} filled in.</summary>
    public static string Fill(string template, params (string Name, object Value)[] values)
    {
        foreach (var (n, v) in values) template = template.Replace("{" + n + "}", Convert.ToString(v, CultureInfo.InvariantCulture));
        return template;
    }

    public static string Msg(string key, params (string, object)[] values) => Fill(Messages[key], values);

    // ---------- JSON ----------

    static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A value as compact JSON, non-ASCII text kept as it is.</summary>
    public static string Json(JsonNode? v) => v is null ? "null" : v.ToJsonString(Relaxed);

    /// <summary>Parses JSON text; ok false when it isn't JSON (a JSON null is ok with a null value).</summary>
    public static bool TryParse(string text, out JsonNode? value)
    {
        try { value = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = MaxJsonDepth }); return true; }
        catch (JsonException) { value = null; return false; }
    }

    public static string? Str(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
    public static JsonObject? Obj(JsonNode? n) => n as JsonObject;
    public static JsonValueKind Kind(JsonNode? n) => n is null ? JsonValueKind.Null : n.GetValueKind();
    public static bool IsNumber(JsonNode? n) => Kind(n) == JsonValueKind.Number;
    /// <summary>A JSON number's value (however the node holds it).</summary>
    public static double Num(JsonNode n) => double.Parse(n.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
    public static bool IsBool(JsonNode? n) => Kind(n) is JsonValueKind.True or JsonValueKind.False;

    /// <summary>Equality of JSON values the way Python compares them after json.loads (1 equals 1.0).</summary>
    public static bool Same(JsonNode? a, JsonNode? b)
    {
        var (ka, kb) = (Kind(a), Kind(b));
        if (ka != kb) return false;
        switch (ka)
        {
            case JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False: return true;
            case JsonValueKind.String: return Str(a) == Str(b);
            case JsonValueKind.Number: return Num(a) == Num(b);
            case JsonValueKind.Array:
                var (x, y) = ((JsonArray)a!, (JsonArray)b!);
                return x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second));
            default:
                var (o, p2) = ((JsonObject)a!, (JsonObject)b!);
                return o.Count == p2.Count && o.All(kv => p2.TryGetPropertyValue(kv.Key, out var w) && Same(kv.Value, w));
        }
    }

    /// <summary>A copy of a value that can go into another JSON tree.</summary>
    public static JsonNode? Copy(JsonNode? n) => n?.DeepClone();

    // ---------- front matter ----------

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_ .()/-]*$")]
    private static partial Regex PlainKey();

    /// <summary>Fields as front matter: `---`, one `name: JSON value` a line in name order, an optional `# comment` line, `---`.
    /// A name that isn't plain is written as a JSON string.</summary>
    public static string FrontMatter(IReadOnlyDictionary<string, JsonNode?> values, string? comment = null)
    {
        var sb = new StringBuilder("---\n");
        foreach (var name in values.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var key = PlainKey().IsMatch(name) && !name.EndsWith(' ') ? name : Json(JsonValue.Create(name));
            sb.Append(key).Append(": ").Append(Json(values[name])).Append('\n');
        }
        if (!string.IsNullOrEmpty(comment)) sb.Append("# ").Append(comment).Append('\n');
        return sb.Append("---\n").ToString();
    }

    /// <summary>(the front matter's values, or null without one; the text after it). Values are JSON, or the YAML an agent may
    /// write instead (plain text, [a, b], true, ~); `#` lines are comments; `name:` followed by `- item` lines is a list.</summary>
    public static (Dictionary<string, JsonNode?>? Values, string Body) SplitFrontMatter(string path, string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return (null, text);
        int end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        while (end != -1 && end + 4 < text.Length && text[end + 4] != '\n') end = text.IndexOf("\n---", end + 4, StringComparison.Ordinal);
        if (end == -1) throw new MountSourceException(Msg("frontMatterOpen", ("path", path)));
        var output = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var lines = (end >= 4 ? text[4..end] : "").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var ln = lines[i++];
            if (ln.Trim().Length == 0 || ln.TrimStart().StartsWith('#')) continue;
            string? key = null;
            string rest = "";
            if (ln.StartsWith('"'))
            {
                var bytes = Encoding.UTF8.GetBytes(ln);
                var reader = new Utf8JsonReader(bytes);
                try
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    {
                        key = reader.GetString();
                        rest = Encoding.UTF8.GetString(bytes, (int)reader.BytesConsumed, bytes.Length - (int)reader.BytesConsumed);
                    }
                }
                catch (JsonException) { key = null; }
            }
            else if (ln.Length > 0 && !char.IsWhiteSpace(ln[0]))
            {
                int colon = ln.IndexOf(':');
                (key, rest) = colon < 0 ? (ln, "") : (ln[..colon], ln[colon..]);
            }
            if (key is null || !rest.StartsWith(':')) throw new MountSourceException(Msg("frontMatterLine", ("path", path)));
            var raw = rest[1..].Trim();
            if (raw.Length > 0) { output[key.Trim()] = Scalar(raw); continue; }
            var items = new JsonArray();
            while (i < lines.Length && lines[i].TrimStart().StartsWith("- ", StringComparison.Ordinal))
                items.Add(Scalar(lines[i++].TrimStart()[2..].Trim()));
            output[key.Trim()] = items.Count > 0 ? items : null;
        }
        return (output, text[(end + 4)..].TrimStart('\n'));
    }

    public static JsonNode? Scalar(string raw)
    {
        if (TryParse(raw, out var v)) return v;
        switch (raw)
        {
            case "~" or "Null" or "NULL": return null;
            case "True" or "TRUE" or "yes" or "Yes": return JsonValue.Create(true);
            case "False" or "FALSE" or "no" or "No": return JsonValue.Create(false);
        }
        if (raw.StartsWith('[') && raw.EndsWith(']'))
            return new JsonArray([.. raw[1..^1].Split(',').Where(x => x.Trim().Length > 0).Select(x => (JsonNode)JsonValue.Create(x.Trim().Trim('\'', '"'))!)]);
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'') return JsonValue.Create(raw[1..^1].Replace("''", "'"));
        return JsonValue.Create(raw);
    }

    // ---------- names ----------

    /// <summary>A title as one file name: control characters become spaces, "/" becomes <see cref="Slash"/>, spaces at both ends
    /// go, "." and ".." become "(.)" and "(..)", cut to <see cref="NameBytes"/> of UTF-8; the fallback when nothing is left.</summary>
    public static string SafeName(string? title, string fallback)
    {
        var s = new string([.. (title ?? "").Select(c => c < ' ' || c == '\x7f' ? ' ' : c)]).Replace("/", Slash).Trim();
        if (s.Length == 0) s = fallback;
        if (s is "." or "..") s = $"({s})";
        var b = Encoding.UTF8.GetBytes(s);
        if (b.Length <= NameBytes) return s;
        int n = NameBytes;
        while (n > 0 && (b[n] & 0xC0) == 0x80) n--; // never cut a character in two
        return Encoding.UTF8.GetString(b, 0, n).TrimEnd();
    }

    /// <summary>The names, made unique in order: a later duplicate becomes "name (2)" (before an extension: "a (2).md").</summary>
    public static List<string> Unique(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (var n in names)
        {
            var (stem, ext) = n.EndsWith(".md", StringComparison.Ordinal) ? (n[..^3], ".md") : (n, "");
            var cand = n;
            for (int k = 2; seen.Contains(cand); k++) cand = $"{stem} ({k}){ext}";
            seen.Add(cand);
            output.Add(cand);
        }
        return output;
    }

    /// <summary>Percent-encodes text for a URL the way Python's urllib quote does: letters, digits, _.-~ and <paramref name="safe"/>
    /// stay; everything else is its UTF-8 bytes as %XX.</summary>
    public static string Quote(string s, string safe = "/")
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            char c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~' || safe.Contains(c))) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Sends requests to one service. The token comes from the host's function, asked before every attempt, so a host that
/// refreshes tokens returns the current one. A 429 or 503 is retried for any method (the service did nothing), another 5xx
/// and no answer only for GET; Retry-After holds every later request of this client, up to <see cref="ConnectorCommon.MaxRetryAfter"/>.
/// Every request and every wait honours the caller's cancellation token.
/// </summary>
internal sealed class ConnectorApi
{
    static readonly HttpMessageHandler Pinned = WebSession.PinnedHandler();

    readonly string _service;
    readonly Func<CancellationToken, ValueTask<string>> _token;
    readonly HttpClient _client;
    readonly TimeSpan _timeout, _interval;
    readonly object _gate = new();
    DateTime _next = DateTime.MinValue;

    /// <summary>How the client waits (between tries, for Retry-After, for the rate); tests replace it.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay = Task.Delay;
    /// <summary>The clock the waits are measured on; tests replace it.</summary>
    internal Func<DateTime> Now = () => DateTime.UtcNow;

    public ConnectorApi(string service, Func<CancellationToken, ValueTask<string>> token, HttpMessageHandler? handler, TimeSpan timeout, double? rate)
    {
        for (var h = handler; h != null; h = (h as DelegatingHandler)?.InnerHandler)
            if (h is HttpClientHandler { AllowAutoRedirect: true } or SocketsHttpHandler { AllowAutoRedirect: true })
                throw new ArgumentException("the handler follows redirects, which could send the token to another host; set AllowAutoRedirect = false", nameof(handler));
        _service = service;
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _client = new HttpClient(handler ?? Pinned, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(60);
        _interval = rate is > 0 ? TimeSpan.FromSeconds(1 / rate.Value) : TimeSpan.Zero;
    }

    string M(string key, params (string, object)[] values) => ConnectorCommon.Msg(key, [("service", _service), .. values]);

    async Task Wait(TimeSpan t, CancellationToken ct)
    {
        if (t > TimeSpan.Zero) await Delay(t, ct);
    }

    /// <summary>One request: (status, headers, body cut at <paramref name="limit"/> + 1 bytes). <paramref name="auth"/> false sends
    /// no Authorization header (a pre-signed URL).</summary>
    public async Task<(int Status, List<KeyValuePair<string, string>> Headers, byte[] Body)> SendAsync(string method, string url, byte[]? body,
        IEnumerable<KeyValuePair<string, string>>? headers, CancellationToken ct, bool auth = true, long limit = ConnectorCommon.MaxResponse)
    {
        for (int attempt = 0; attempt <= ConnectorCommon.MaxRetries; attempt++)
        {
            TimeSpan wait;
            lock (_gate)
            {
                var now = Now();
                var start = _next > now ? _next : now;
                _next = start + _interval;
                wait = start - now;
            }
            await Wait(wait, ct);
            bool last = attempt == ConnectorCommon.MaxRetries;
            int status;
            List<KeyValuePair<string, string>> rh;
            byte[] raw;
            try
            {
                using var req = new HttpRequestMessage(new HttpMethod(method), url);
                if (body != null) req.Content = new ByteArrayContent(body);
                foreach (var (k, v) in headers ?? [])
                    if (!req.Headers.TryAddWithoutValidation(k, v)) req.Content?.Headers.TryAddWithoutValidation(k, v);
                if (auth) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await _token(ct));
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeout);
                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                status = (int)resp.StatusCode;
                rh = [.. resp.Headers.Concat(resp.Content.Headers).SelectMany(h => h.Value.Select(v => KeyValuePair.Create(h.Key, v)))];
                raw = await ReadBounded(await resp.Content.ReadAsStreamAsync(cts.Token), limit < long.MaxValue ? limit + 1 : limit, cts.Token);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not MountSourceException)
            {
                if (method == "GET" && !last)
                {
                    await Wait(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                    continue;
                }
                throw new MountSourceException(M(method == "GET" ? "noAnswer" : "noAnswerWrite"));
            }
            if (status == 429 || status == 503 || (status >= 500 && method == "GET"))
            {
                if (last) throw new MountSourceException(M("busy", ("status", status), ("tries", ConnectorCommon.MaxRetries + 1)));
                var after = RetryAfter(rh);
                if (after > ConnectorCommon.MaxRetryAfter) throw new MountSourceException(M("waitTooLong", ("seconds", (long)after.Value), ("status", status)));
                var pause = TimeSpan.FromSeconds(after ?? Math.Pow(2, attempt));
                lock (_gate) { var until = Now() + pause; if (until > _next) _next = until; } // every request waits, not only this one
                await Wait(pause, ct);
                continue;
            }
            return (status, rh, raw);
        }
        throw new InvalidOperationException("not reached");
    }

    static async Task<byte[]> ReadBounded(Stream s, long max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while (ms.Length < max && (n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, max - ms.Length)), ct)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    public static double? RetryAfter(IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach (var (k, v) in headers)
            if (k.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
                return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) ? Math.Max(d, 0) : null;
        return null;
    }

    public static string? Header(IEnumerable<KeyValuePair<string, string>> headers, string name) =>
        headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();

    /// <summary>(code, message) from {"error": {"code", "message"}} or {"code", "message"}, cut at 100 and 1,000 characters.</summary>
    public static (string Code, string Message) ErrorFields(JsonNode? answer)
    {
        var e = ConnectorCommon.Obj(answer?["error"] is JsonObject inner ? inner : answer);
        var code = ConnectorCommon.Str(e?["code"]) ?? "";
        var message = ConnectorCommon.Str(e?["message"]) ?? "";
        return (code.Length > 100 ? code[..100] : code, message.Length > 1000 ? message[..1000] : message);
    }

    /// <summary>A JSON request whose answer must be a JSON object; an error status throws <see cref="ApiError"/>.</summary>
    public async Task<JsonObject> JsonAsync(string method, string url, JsonObject? body, IEnumerable<KeyValuePair<string, string>>? headers, CancellationToken ct)
    {
        var hs = new List<KeyValuePair<string, string>> { new("Accept", "application/json") };
        hs.AddRange(headers ?? []);
        if (body != null) hs.Add(new("Content-Type", "application/json"));
        byte[]? data = body != null ? Encoding.UTF8.GetBytes(body.ToJsonString()) : method is "POST" or "PUT" or "PATCH" ? [] : null; // 411 without a length
        var (status, _, raw) = await SendAsync(method, url, data, hs, ct);
        return Answer(status, raw);
    }

    /// <summary>A response body as a JSON object; an error status throws <see cref="ApiError"/>.</summary>
    public JsonObject Answer(int status, byte[] raw)
    {
        if (raw.Length > ConnectorCommon.MaxResponse) throw new MountSourceException(M("responseTooLarge"));
        JsonNode? answer = null;
        bool ok = raw.Length > 0 && ConnectorCommon.TryParse(Encoding.UTF8.GetString(raw), out answer);
        if (status >= 400)
        {
            var (code, message) = ErrorFields(ok ? answer : null);
            throw new ApiError(status, code, message);
        }
        if (status == 204 || raw.Length == 0) return [];
        return answer as JsonObject ?? throw new MountSourceException(M("notJson", ("status", status)));
    }
}
