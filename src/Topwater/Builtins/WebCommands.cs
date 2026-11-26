using System.Globalization;
using System.Text;

namespace Topwater;

/// <summary>curl and wget over the shell's <see cref="WebSession"/>; without one every host fails to resolve.</summary>
internal static partial class Builtins
{
    static readonly WebSession NoNetwork = new(WebSession.NoNetwork);

    static void RegisterWebCommands(Shell shell)
    {
        shell.Register("curl", Curl);
        shell.Register("wget", Wget);
    }

    static TimeSpan Seconds(double s) => s <= 0 || s > 86400 ? TimeSpan.FromDays(1) : TimeSpan.FromSeconds(s);

    static List<KeyValuePair<string, string>> SetHeader(List<KeyValuePair<string, string>> headers, string name, string? value)
    {
        int i = headers.FindIndex(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (value == null) { if (i >= 0) headers.RemoveAt(i); }
        else if (i >= 0) headers[i] = new(headers[i].Key, value);
        else headers.Add(new(name, value));
        return headers;
    }

    /// <summary>The last path segment of the URL, as -O and wget name the file (empty for a directory).</summary>
    static string RemoteName(WebUrl url)
    {
        var path = url.PathAndQuery.Split('?')[0];
        return path[(path.LastIndexOf('/') + 1)..];
    }

    // ---------- curl ----------

    static readonly Dictionary<string, char> CurlLong = new(StringComparer.Ordinal)
    {
        ["data"] = 'd', ["data-ascii"] = 'd', ["data-binary"] = '\u0001', ["data-raw"] = '\u0002', ["data-urlencode"] = '\u0003', ["json"] = '\u0004',
        ["get"] = 'G', ["request"] = 'X', ["header"] = 'H', ["user-agent"] = 'A', ["user"] = 'u', ["location"] = 'L', ["max-redirs"] = '\u0005',
        ["include"] = 'i', ["head"] = 'I', ["output"] = 'o', ["remote-name"] = 'O', ["fail"] = 'f', ["fail-with-body"] = '\u0006', ["silent"] = 's',
        ["show-error"] = 'S', ["write-out"] = 'w', ["max-time"] = 'm', ["connect-timeout"] = '\u0007', ["compressed"] = '\u0008', ["verbose"] = 'v',
        ["insecure"] = 'k', ["help"] = 'h', ["version"] = 'V', ["retry"] = '\u0010', ["retry-delay"] = '\u0011', ["retry-max-time"] = '\u0012',
        ["retry-all-errors"] = '\u0013', ["retry-connrefused"] = '\u0014', ["proto"] = '\u0015', ["proto-redir"] = '\u0016', ["fail-early"] = '\u0017',
        ["no-progress-meter"] = '\u0018', ["tlsv1"] = '\u0019', ["tlsv1.0"] = '\u0019', ["tlsv1.1"] = '\u0019', ["tlsv1.2"] = '\u0019', ["tlsv1.3"] = '\u0019',
        ["ssl-reqd"] = '\u0019', ["http1.1"] = '\u0019', ["http2"] = '\u0019', ["tls-max"] = '\u001a', ["progress-bar"] = '\u001b',
    };
    const string CurlFlags = "GLiIOfsSvkhV\u0006\u0008\u0013\u0014\u0017\u0018\u0019\u001b";
    const string CurlValued = "dXHAuowm\u0001\u0002\u0003\u0004\u0005\u0007\u0010\u0011\u0012\u0015\u0016\u001a";

    // what --proto names: the protocols curl 8.5 on Ubuntu knows (Topwater itself only speaks http and https)
    static readonly string[] CurlProtocols = ["dict", "file", "ftp", "ftps", "gopher", "gophers", "http", "https", "imap", "imaps", "ldap", "ldaps", "mqtt",
        "pop3", "pop3s", "rtmp", "rtmpe", "rtmps", "rtmpt", "rtmpte", "rtmpts", "rtsp", "scp", "sftp", "smb", "smbs", "smtp", "smtps", "telnet", "tftp", "ws", "wss"];

    /// <summary>--proto and --proto-redir: from <paramref name="start"/>, each comma-separated name added (+ or none), removed (-) or
    /// made the only one (=); "all" is every protocol. An unknown name gets a warning and is skipped; null when a sign other
    /// than these, or none of a name, makes it a usage error.</summary>
    static HashSet<string>? ProtoSet(string spec, IEnumerable<string> start, Action<string> warn)
    {
        var set = new HashSet<string>(start, StringComparer.Ordinal);
        foreach (var token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = 0;
            char action = '+';
            for (; i < token.Length && !char.IsAsciiLetterOrDigit(token[i]); i++)
                if (token[i] is '+' or '-' or '=') action = token[i]; else return null;
            if (i == token.Length) return null;
            var name = token[i..].ToLowerInvariant();
            if (action == '=') set.Clear();
            string[] names = name == "all" ? CurlProtocols : CurlProtocols.Contains(name) ? [name] : [];
            if (names.Length == 0) { warn(CommandSpec.Message("curl", "unknownProtocol", ("name", token[i..]))); continue; }
            if (action == '-') set.ExceptWith(names); else set.UnionWith(names);
        }
        return set;
    }

    /// <summary>curl's "Warning: " lines, wrapped at 79 columns (the width curl assumes when stderr is not a terminal).</summary>
    static string CurlWarning(string text)
    {
        const int width = 79 - 9;
        var sb = new StringBuilder();
        for (var rest = text; rest.Length > 0;)
        {
            if (rest.Length <= width) { sb.Append(CommandSpec.Message("curl", "warning", ("text", rest))).Append('\n'); break; }
            int cut = width - 1;
            while (cut > 0 && rest[cut] is not (' ' or '\t')) cut--;
            if (cut == 0) cut = width - 1;
            sb.Append(CommandSpec.Message("curl", "warning", ("text", rest[..(cut + 1)]))).Append('\n');
            rest = rest[(cut + 1)..];
        }
        return sb.ToString();
    }

    static async Task<int> Curl(CommandContext ctx)
    {
        var web = ctx.Shell.Web ?? NoNetwork;
        int Usage(string message)
        {
            ctx.Stderr.WriteLine(message);
            ctx.Stderr.Write(CommandSpec.Of("curl").TryHelp);
            return 2;
        }
        var opts = new List<(char Key, string? Value, string Name)>();
        var urls = new List<string>();
        var args = ctx.Args;
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--") && arg.Length > 2)
            {
                if (!CurlLong.TryGetValue(arg[2..], out var key)) return Usage(CommandSpec.Message("curl", "unknownOption", ("option", arg)));
                if (CurlValued.Contains(key))
                {
                    if (++i >= args.Count) return Usage(CommandSpec.Message("curl", "needsParameter", ("option", arg)));
                    opts.Add((key, args[i], arg));
                }
                else opts.Add((key, null, arg));
            }
            else if (arg.StartsWith('-') && arg.Length > 1)
            {
                for (int j = 1; j < arg.Length; j++)
                {
                    char c = arg[j];
                    if (c == '#') { opts.Add(('\u001b', null, "-#")); continue; }
                    if (CurlValued.Contains(c) && char.IsAsciiLetter(c))
                    {
                        string? value = j + 1 < arg.Length ? arg[(j + 1)..] : ++i < args.Count ? args[i] : null;
                        if (value == null) return Usage(CommandSpec.Message("curl", "needsParameter", ("option", "-" + c)));
                        opts.Add((c, value, "-" + c));
                        break;
                    }
                    if (!CurlFlags.Contains(c) || !char.IsAsciiLetter(c)) return Usage(CommandSpec.Message("curl", "unknownOption", ("option", "-" + c)));
                    opts.Add((c, null, "-" + c));
                }
            }
            else urls.Add(arg);
        }

        bool Has(char k) => opts.Any(o => o.Key == k);
        string? Last(char k) => opts.LastOrDefault(o => o.Key == k).Value;
        if (Has('h')) { ctx.Stdout.Write(CommandSpec.Of("curl").Help); return 0; }
        if (Has('V')) { ctx.Stdout.WriteLine(CommandSpec.Of("curl").Version); return 0; }
        if (urls.Count == 0) { ctx.Stderr.Write(CommandSpec.Of("curl").TryHelp); return 2; }

        bool silent = Has('s') && !Has('S'), head = Has('I'), include = Has('i') || head, verbose = Has('v');
        bool fail = Has('f'), failWithBody = Has('\u0006'), follow = Has('L'), compressed = Has('\u0008'), get = Has('G');
        void Error(int code, string message) { if (!silent) ctx.Stderr.WriteLine(CommandSpec.Message("curl", "error", ("code", code.ToString(CultureInfo.InvariantCulture)), ("message", message))); }

        double maxTime = 0;
        int maxRedirs = -1;
        foreach (var (key, value, name) in opts.Where(o => o.Key is 'm' or '\u0007' or '\u0005'))
        {
            if (key == '\u0005')
            {
                if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out maxRedirs)) return Usage(CommandSpec.Message("curl", "badNumber", ("option", name)));
            }
            else if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s < 0) return Usage(CommandSpec.Message("curl", "badNumber", ("option", name)));
            else maxTime = maxTime == 0 ? s : Math.Min(maxTime, s);
        }
        var timeout = maxTime > 0 && Seconds(maxTime) < web.Policy.Timeout ? Seconds(maxTime) : web.Policy.Timeout;

        // --retry, --retry-delay, --retry-max-time: whole seconds, not negative
        var retry = new long[3];
        foreach (var (key, value, name) in opts.Where(o => o.Key is '\u0010' or '\u0011' or '\u0012'))
        {
            if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return Usage(CommandSpec.Message("curl", "badNumber", ("option", name)));
            if (n < 0) return Usage(CommandSpec.Message("curl", "negativeNumber", ("option", name)));
            retry[key - '\u0010'] = n;
        }
        var (retries, retryDelay, retryMaxTime) = (retry[0], retry[1], retry[2]);
        bool retryAll = Has('\u0013'), retryRefused = Has('\u0014'), failEarly = Has('\u0017');
        HashSet<string> protocols = [.. CurlProtocols], redirProtocols = ["http", "https", "ftp", "ftps"];
        foreach (var (key, value, name) in opts.Where(o => o.Key is '\u0015' or '\u0016'))
        {
            var set = ProtoSet(value!, key == '\u0015' ? CurlProtocols : ["http", "https", "ftp", "ftps"], w => ctx.Stderr.Write(CurlWarning(w)));
            if (set == null) return Usage(CommandSpec.Message("curl", "badlyUsed", ("option", name)));
            if (key == '\u0015') protocols = set; else redirProtocols = set;
        }

        // the body: -d values joined with &, as curl does
        var parts = new List<byte[]>();
        bool json = false, anyData = false;
        foreach (var (key, value, name) in opts.Where(o => o.Key is 'd' or '\u0001' or '\u0002' or '\u0003' or '\u0004'))
        {
            anyData = true;
            json |= key == '\u0004';
            byte[] bytes;
            try
            {
                if (key == '\u0003') bytes = Encoding.UTF8.GetBytes(UrlEncodeData(value!));
                else if (key != '\u0002' && value!.StartsWith('@'))
                {
                    var file = value[1..];
                    bytes = file == "-" ? ByteText.Encode(ctx.StdinText) : ctx.Fs.ReadAllBytes(ctx.ResolvePath(file));
                    if (key is 'd' or '\u0004') bytes = bytes.Where(b => b is not ((byte)'\r' or (byte)'\n')).ToArray();
                }
                else bytes = ByteText.Encode(value!);
            }
            catch (ShellException)
            {
                ctx.Stderr.WriteLine(CommandSpec.Message("curl", "readFile", ("option", name)));
                ctx.Stderr.Write(CommandSpec.Of("curl").TryHelp);
                return 26;
            }
            parts.Add(bytes);
        }
        byte[]? body = anyData ? Join(parts, json ? [] : "&"u8.ToArray()) : null;
        string? query = get && body != null ? Encoding.UTF8.GetString(body) : null;
        if (get) body = null;

        var headers = new List<KeyValuePair<string, string>> { new("User-Agent", "curl/8.5.0"), new("Accept", "*/*") };
        if (body != null) SetHeader(headers, "Content-Type", json ? "application/json" : "application/x-www-form-urlencoded");
        if (json) SetHeader(headers, "Accept", "application/json");
        if (compressed) SetHeader(headers, "Accept-Encoding", "deflate, gzip");
        if (Last('A') is { } agent) SetHeader(headers, "User-Agent", agent.Length == 0 ? null : agent);
        foreach (var h in opts.Where(o => o.Key == 'H').Select(o => o.Value!))
        {
            int colon = h.IndexOf(':'), semi = h.IndexOf(';');
            if (colon > 0 && (semi < 0 || colon < semi))
            {
                var v = h[(colon + 1)..].Trim();
                SetHeader(headers, h[..colon].Trim(), v.Length == 0 ? null : v);
            }
            else if (semi > 0 && h[(semi + 1)..].Trim().Length == 0) SetHeader(headers, h[..semi].Trim(), "");
        }
        string? user = Last('u');

        string method = head ? "HEAD" : body != null ? "POST" : "GET";
        string? forced = Last('X');
        var outputs = new Queue<(char Key, string? Value)>(opts.Where(o => o.Key is 'o' or 'O').Select(o => (o.Key, o.Value)));
        int exit = 0;

        foreach (var raw in urls)
        {
            var output = outputs.Count > 0 ? outputs.Dequeue() : ('\0', null);
            var sinceFirst = ctx.Shell.StartTimer();
            int code = 0, redirects = 0;
            WebResponse? last = null;
            Dictionary<string, string> vars = [];
            for (long tries = 0; ; tries++)
            {
                var buffer = new MemoryStream();
                code = redirects = 0;
                last = null;
                string message = "", urlText = raw;
                string? target = null;
                bool retriable = true;
                var hops = new List<(WebResponse Response, double At, int RequestBytes)>();
                ctx.CancellationToken.ThrowIfCancellationRequested(); // on a virtual clock the waits take no time: the call's limit ends the tries
                var clock = ctx.Shell.StartTimer(); // the shell's clock, so a recording on a virtual one replays
                try
                {
                    if (query != null) urlText += (raw.Contains('?') ? "&" : "?") + query;
                    var url = WebUrl.Parse(urlText);
                    if (!protocols.Contains(url.Scheme)) throw new WebFailure(WebError.Protocol, url.Scheme);
                    var sent = new List<KeyValuePair<string, string>>(headers);
                    var userInfo = user ?? (url.UserInfo is { } ui ? Uri.UnescapeDataString(ui) : null);
                    if (userInfo != null && !sent.Any(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
                        sent.Add(new("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(userInfo.Contains(':') ? userInfo : userInfo + ":"))));
                    target = output.Key == 'O' ? RemoteName(url) : output.Value;
                    if (target is "") throw new WebFailure(WebError.Recv) { Detail = "noRemoteName" };

                    void OnHop(WebResponse r)
                    {
                        last = r;
                        // shortcut: the body counts toward the first request only; a 303 drops it, other redirects resend it
                        var request = $"{r.Method} {r.Url.PathAndQuery} HTTP/1.1\r\nHost: {r.Url.HostForUrl}{(r.Url.DefaultPort ? "" : ":" + r.Url.Port)}\r\n"
                            + string.Concat(sent.Select(h => $"{h.Key}: {h.Value}\r\n")) + (body != null && hops.Count == 0 ? $"Content-Length: {body.Length}\r\n" : "") + "\r\n";
                        hops.Add((r, clock().TotalSeconds, Encoding.UTF8.GetByteCount(request) + (body != null && hops.Count == 0 ? body.Length : 0)));
                        if (verbose)
                        {
                            var sb = new StringBuilder($"> {r.Method} {r.Url.PathAndQuery} HTTP/1.1\r\n> Host: {r.Url.HostForUrl}{(r.Url.DefaultPort ? "" : ":" + r.Url.Port)}\r\n");
                            foreach (var (k, v) in sent) sb.Append($"> {k}: {v}\r\n");
                            sb.Append($"> \r\n< HTTP/1.1 {r.Status} {r.Reason}\r\n");
                            foreach (var (k, v) in r.Headers) sb.Append($"< {k}: {v}\r\n");
                            ctx.Stderr.Write(sb.Append("< \r\n").ToString());
                        }
                        if (include) buffer.Write(Encoding.UTF8.GetBytes(StatusBlock(r)));
                        // --proto-redir (and --proto) decide where a redirect may lead
                        if (follow && r.Status is 301 or 302 or 303 or 307 or 308 && r.Header("Location") is { Length: > 0 } location
                            && r.Url.Resolve(location).Scheme is var next && !(protocols.Contains(next) && redirProtocols.Contains(next)))
                            throw new WebFailure(WebError.Protocol, next);
                    }
                    (last, redirects) = await web.FetchAsync(url, method, forced, sent, body, follow, maxRedirs, timeout, OnHop, ctx.CancellationToken);
                    var data = last.Body;
                    if (compressed && !last.Truncated && last.Header("Content-Encoding")?.Trim().ToLowerInvariant() is "gzip" or "x-gzip" or "deflate" && data.Length > 0)
                    {
                        data = WebSession.Inflate(data, last.Header("Content-Encoding")!.Trim().ToLowerInvariant() != "deflate", web.Policy.MaxResponseBytes + 1, whole: false) ?? throw new WebFailure(WebError.Recv);
                        if (data.Length > web.Policy.MaxResponseBytes) throw new WebFailure(WebError.TooLarge);
                    }
                    if ((fail || failWithBody) && last.Status >= 400)
                    {
                        if (failWithBody) buffer.Write(data);
                        code = 22;
                        message = CommandSpec.Message("curl", "httpError", ("status", last.Status.ToString(CultureInfo.InvariantCulture)));
                    }
                    else
                    {
                        if (!head) buffer.Write(data);
                        if (last.Truncated) { code = 63; message = CommandSpec.Message("curl", "tooLarge"); }
                        else if (last.Error != null) (code, message) = CutShort(last);
                    }
                    if (target is { } t && t != "-" && (code is 0 or 18 or 56 or 63 || failWithBody))
                    {
                        try { ctx.Fs.WriteAllBytes(ctx.ResolvePath(t), buffer.ToArray()); }
                        catch (ShellException) { code = 23; message = CommandSpec.Message("curl", "writeFailed"); }
                        buffer.SetLength(0);
                    }
                }
                catch (WebFailure f)
                {
                    (code, message) = CurlError(f, web.Policy.ExplainBlocked);
                    retriable = f.Blocked == null && f.Kind != WebError.Replay; // the web policy's answer would not change
                }
                if (buffer.Length > 0) ctx.Stdout.Write(ByteText.Decode(buffer.ToArray()));
                if (code != 0) Error(code, message);

                double total = clock().TotalSeconds;
                long size = last?.Body.Length ?? 0, upload = body?.Length ?? 0;
                string Secs(double x) => x.ToString("0.000000", CultureInfo.InvariantCulture);
                vars = new()
                {
                    ["errormsg"] = code == 0 ? "" : message, ["filename_effective"] = target is null or "-" ? "" : target, ["url"] = urlText,
                    ["scheme"] = last?.Url.Scheme.ToUpperInvariant() ?? "", ["http_version"] = last == null ? "0" : "1.1",
                    ["remote_port"] = last?.Url.Port.ToString(CultureInfo.InvariantCulture) ?? "",
                    ["num_connects"] = hops.Where((h, k) => k == 0 || (h.Response.Url.Host, h.Response.Url.Port) != (hops[k - 1].Response.Url.Host, hops[k - 1].Response.Url.Port)).Count().ToString(CultureInfo.InvariantCulture),
                    ["size_upload"] = upload.ToString(CultureInfo.InvariantCulture), ["size_header"] = hops.Sum(h => Encoding.UTF8.GetByteCount(StatusBlock(h.Response))).ToString(CultureInfo.InvariantCulture),
                    ["size_request"] = hops.Sum(h => h.RequestBytes).ToString(CultureInfo.InvariantCulture),
                    ["speed_download"] = (total > 0 ? (long)(size / total) : 0).ToString(CultureInfo.InvariantCulture),
                    ["speed_upload"] = (total > 0 && hops.Count > 0 ? (long)(upload / total) : 0).ToString(CultureInfo.InvariantCulture),
                    // the phases before the first response byte are not timed apart
                    ["time_total"] = Secs(total), ["time_namelookup"] = Secs(0), ["time_connect"] = Secs(0), ["time_appconnect"] = Secs(0), ["time_pretransfer"] = Secs(0),
                    ["time_starttransfer"] = Secs(hops.Count > 0 ? hops[^1].At : 0), ["time_redirect"] = Secs(hops.Count > 1 ? hops[^2].At : 0),
                };

                // --retry: a timeout or unresolved name, a refused connection (--retry-connrefused), a transient HTTP status, or anything (--retry-all-errors)
                string? why = !retriable || tries >= retries ? null
                    : code is 6 or 28 ? "retryTimeout"
                    : code == 7 && retryRefused ? "retryRefused"
                    : code is 0 or 22 && last?.Status is 408 or 429 or 500 or 502 or 503 or 504 ? "retryHttp"
                    : retryAll && code != 0 ? "retryAll" : null;
                if (why == null || retryMaxTime > 0 && sinceFirst().TotalSeconds >= retryMaxTime) break;
                long wait = retryDelay > 0 ? retryDelay : Math.Min(600, 1L << (int)Math.Min(tries, 10));
                // an HTTP retry waits at least the Retry-After seconds (shortcut: the HTTP-date form is not read)
                if (why == "retryHttp" && long.TryParse(last?.Header("Retry-After"), NumberStyles.None, CultureInfo.InvariantCulture, out var after) && after > wait) wait = after;
                if (!Has('s')) // -S brings back errors, not warnings
                    ctx.Stderr.Write(CurlWarning(CommandSpec.Message("curl", "retrying", ("why", CommandSpec.Message("curl", why)), ("seconds", wait.ToString(CultureInfo.InvariantCulture)),
                        ("left", (retries - tries).ToString(CultureInfo.InvariantCulture)))));
                await ctx.Shell.SleepAsync(TimeSpan.FromSeconds(Math.Min(wait, 86400)), ctx.CancellationToken); // a longer wait never ends before the call does
            }
            exit = code; // without --fail-early, the last URL's status
            if (Last('w') is { } format) ctx.Stdout.Write(WriteOut(ctx, format, last, redirects, code, method, forced, vars));
            if (failEarly && code != 0) break;
        }
        return exit;
    }

    /// <summary>A response whose body ended early (written as far as it came): closed, or reset.</summary>
    static (int Code, string Message) CutShort(WebResponse r)
    {
        if (r.Error == "reset") return (56, CommandSpec.Message("curl", "reset"));
        var length = r.Header("Content-Length")?.Trim() ?? "";
        return long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > r.Body.Length
            ? (18, CommandSpec.Message("curl", "partial", ("bytes", (n - r.Body.Length).ToString(CultureInfo.InvariantCulture))))
            : (18, CommandSpec.Message("curl", "partialChunked"));
    }

    static (int Code, string Message) CurlError(WebFailure f, bool explain)
    {
        string M(string key, params (string, string)[] values) => CommandSpec.Message("curl", key, values);
        if (f.Blocked != null && (explain || f.Kind == WebError.Policy)) return (f.Kind switch { WebError.Resolve => 6, WebError.Connect => 7, _ => 1 }, M("blocked", ("reason", f.Blocked)));
        return f.Kind switch
        {
            WebError.BadUrl => (3, M("badUrl")),
            WebError.BadHostname => (3, M("badHostname")),
            WebError.Protocol => (1, M("protocol", ("scheme", f.Host))),
            WebError.Resolve => (6, M("resolve", ("host", f.Host))),
            WebError.Connect => (7, M("connect", ("host", f.Host), ("port", f.Port.ToString(CultureInfo.InvariantCulture)))),
            WebError.Timeout => (28, M("timeout", ("ms", f.Detail), ("bytes", "0"))),
            WebError.Tls => (60, M("tls", ("host", f.Host))),
            WebError.Redirects => (47, M("redirects", ("max", f.Detail))),
            WebError.BadHeader => (43, M("badHeader")),
            WebError.TooLarge => (63, M("tooLarge")),
            WebError.Replay => (7, M("replay", ("request", f.Detail))),
            WebError.Partial => (18, M("partialChunked")),
            WebError.Reset => (56, M("reset")),
            WebError.Weird => (8, M("weird")),
            WebError.Empty => (52, M("empty")),
            WebError.Recv when f.Detail == "noRemoteName" => (23, M("noRemoteName")),
            _ => (56, M("recv")),
        };
    }

    static string StatusBlock(WebResponse r)
    {
        var sb = new StringBuilder($"HTTP/1.1 {r.Status} {r.Reason}\r\n");
        foreach (var (k, v) in r.Headers) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        return sb.Append("\r\n").ToString();
    }

    static byte[] Join(List<byte[]> parts, byte[] separator)
    {
        var ms = new MemoryStream();
        for (int i = 0; i < parts.Count; i++) { if (i > 0) ms.Write(separator); ms.Write(parts[i]); }
        return ms.ToArray();
    }

    /// <summary>--data-urlencode: "name=value" encodes the value, "=value" and "value" encode it all; space is +.</summary>
    static string UrlEncodeData(string s)
    {
        int eq = s.IndexOf('=');
        string Enc(string v) => string.Concat(Encoding.UTF8.GetBytes(v).Select(b =>
            char.IsAsciiLetterOrDigit((char)b) || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' ? ((char)b).ToString() : b == ' ' ? "+" : $"%{b:X2}"));
        return eq < 0 ? Enc(s) : eq == 0 ? Enc(s[1..]) : s[..eq] + "=" + Enc(s[(eq + 1)..]);
    }

    static string WriteOut(CommandContext ctx, string format, WebResponse? r, int redirects, int code, string method, string? forced, Dictionary<string, string> vars)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '\\' && i + 1 < format.Length && format[i + 1] is 'n' or 't' or 'r' or '\\')
            {
                sb.Append(format[++i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => '\\' });
                continue;
            }
            if (c != '%' || i + 1 >= format.Length) { sb.Append(c); continue; }
            if (format[i + 1] == '%') { sb.Append('%'); i++; continue; }
            int close = format.IndexOf('}', i);
            if (format[i + 1] != '{' || close < 0) { sb.Append(c); continue; }
            var name = format[(i + 2)..close];
            i = close;
            string? location = r?.Header("Location");
            sb.Append(name switch
            {
                "http_code" or "response_code" => (r?.Status ?? 0).ToString("000", CultureInfo.InvariantCulture),
                "content_type" => r?.Header("Content-Type") ?? "",
                "size_download" => (r?.Body.Length ?? 0).ToString(CultureInfo.InvariantCulture),
                "url_effective" => r?.Url.ToString() ?? "",
                "redirect_url" => r != null && r.Status is >= 300 and < 400 && location is { Length: > 0 } ? TryResolve(r.Url, location) : "",
                "num_redirects" => redirects.ToString(CultureInfo.InvariantCulture),
                "method" => forced ?? r?.Method ?? method,
                "exitcode" => code.ToString(CultureInfo.InvariantCulture),
                _ => vars.TryGetValue(name, out var v) ? v : Unknown(name),
            });
        }
        return sb.ToString();

        string Unknown(string name) { ctx.Stderr.WriteLine(CommandSpec.Message("curl", "unknownWriteOut", ("name", name))); return ""; }
        static string TryResolve(WebUrl url, string location) { try { return url.Resolve(location).ToString(); } catch (WebFailure) { return location; } }
    }

    // ---------- wget ----------

    static readonly Dictionary<string, char> WgetLong = new(StringComparer.Ordinal)
    {
        ["output-document"] = 'O', ["quiet"] = 'q', ["server-response"] = 'S', ["header"] = '\u0001', ["post-data"] = '\u0002', ["post-file"] = '\u0003',
        ["user-agent"] = 'U', ["timeout"] = 'T', ["max-redirect"] = '\u0004', ["no-check-certificate"] = '\u0005', ["help"] = 'h', ["version"] = 'V',
        ["no-verbose"] = '\u0006', ["directory-prefix"] = 'P', ["no-clobber"] = '\u0007', ["spider"] = '\u0008', ["tries"] = 't', ["show-progress"] = '\u0009',
    };
    const string WgetValued = "OUTPt\u0001\u0002\u0003\u0004";

    static async Task<int> Wget(CommandContext ctx)
    {
        var web = ctx.Shell.Web ?? NoNetwork;
        string M(string key, params (string, string)[] values) => CommandSpec.Message("wget", key, values);
        int Usage(string message, int code = 2)
        {
            ctx.Stderr.WriteLine(message);
            ctx.Stderr.Write(CommandSpec.Of("wget").TryHelp);
            return code;
        }
        var opts = new List<(char Key, string? Value)>();
        var urls = new List<string>();
        var args = ctx.Args;
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--") { urls.AddRange(args.Skip(i + 1)); break; }
            if (arg.StartsWith("--"))
            {
                int eq = arg.IndexOf('=');
                var name = eq < 0 ? arg[2..] : arg[2..eq];
                if (!WgetLong.TryGetValue(name, out var key)) return Usage(M("unrecognizedOption", ("option", eq < 0 ? arg : arg[..eq])));
                if (WgetValued.Contains(key))
                {
                    string? value = eq >= 0 ? arg[(eq + 1)..] : ++i < args.Count ? args[i] : null;
                    if (value == null) return Usage(M("longRequiresArgument", ("option", arg)));
                    opts.Add((key, value));
                }
                else opts.Add((key, null));
            }
            else if (arg.StartsWith('-') && arg.Length > 1)
            {
                for (int j = 1; j < arg.Length; j++)
                {
                    char c = arg[j];
                    // -nv and -nc: wget's two-letter short options
                    if (c == 'n' && j + 1 < arg.Length && arg[j + 1] is 'v' or 'c') { opts.Add((arg[++j] == 'v' ? '\u0006' : '\u0007', null)); continue; }
                    if (!"OqSUThVPt".Contains(c)) return Usage(M("invalidOption", ("option", c.ToString())));
                    if (WgetValued.Contains(c))
                    {
                        string? value = j + 1 < arg.Length ? arg[(j + 1)..] : ++i < args.Count ? args[i] : null;
                        if (value == null) return Usage(M("requiresArgument", ("option", c.ToString())));
                        opts.Add((c, value));
                        break;
                    }
                    opts.Add((c, null));
                }
            }
            else urls.Add(arg);
        }
        bool Has(char k) => opts.Any(o => o.Key == k);
        string? Last(char k) => opts.LastOrDefault(o => o.Key == k).Value;
        if (Has('h')) { ctx.Stdout.Write(CommandSpec.Of("wget").Help); return 0; }
        if (Has('V')) { ctx.Stdout.WriteLine(CommandSpec.Of("wget").Version); return 0; }
        if (urls.Count == 0) return Usage(M("missingUrl"), 1);

        bool quiet = Has('q'), server = Has('S'), brief = Has('\u0006'), spider = Has('\u0008'), noClobber = Has('\u0007');
        string? prefix = Last('P') is { Length: > 0 } dir ? dir.TrimEnd('/') + "/" : null;
        var timeout = web.Policy.Timeout;
        if (Last('T') is { } t)
        {
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s < 0) return Usage(M("badNumber", ("option", "--timeout"), ("value", t)));
            if (s > 0 && Seconds(s) < timeout) timeout = Seconds(s);
        }
        int maxRedirect = 20;
        if (Last('\u0004') is { } mr && !int.TryParse(mr, NumberStyles.None, CultureInfo.InvariantCulture, out maxRedirect))
            return Usage(M("badNumber", ("option", "--max-redirect"), ("value", mr)));

        var headers = new List<KeyValuePair<string, string>> { new("User-Agent", "Wget/1.21.4"), new("Accept", "*/*"), new("Accept-Encoding", "identity") };
        byte[]? body = null;
        if (Last('\u0002') is { } post) body = ByteText.Encode(post);
        if (Last('\u0003') is { } postFile)
        {
            try { body = ctx.Fs.ReadAllBytes(ctx.ResolvePath(postFile)); }
            catch (ShellException e) { ctx.Stderr.WriteLine(M("readFile", ("file", postFile), ("errno", Errno(e)))); return 3; }
        }
        if (body != null) SetHeader(headers, "Content-Type", "application/x-www-form-urlencoded");
        if (Last('U') is { } ua) SetHeader(headers, "User-Agent", ua);
        foreach (var h in opts.Where(o => o.Key == '\u0001').Select(o => o.Value!))
        {
            int colon = h.IndexOf(':');
            if (colon <= 0) return Usage(M("invalidHeader", ("header", h)));
            SetHeader(headers, h[..colon].Trim(), h[(colon + 1)..].Trim());
        }

        // -O FILE is created before anything is fetched, and every body is appended to it
        string? document = Last('O');
        if (document is { } doc && doc != "-")
        {
            try { ctx.Fs.WriteAllBytes(ctx.ResolvePath(doc), []); }
            catch (ShellException e) { ctx.Stderr.WriteLine(M("writeFailed", ("file", doc), ("errno", Errno(e)))); return 1; }
        }

        string Now() => ctx.Shell.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        void Say(string line) { if (!quiet && !brief) ctx.Stderr.WriteLine(line); } // the full report
        void Note(string line) { if (!quiet) ctx.Stderr.WriteLine(line); } // also under -nv
        int exit = 0;
        foreach (var raw in urls)
        {
            int code = 0;
            WebUrl url;
            try { url = WebUrl.Parse(raw); }
            catch (WebFailure f)
            {
                ctx.Stderr.WriteLine(f.Kind == WebError.Protocol ? M("scheme", ("url", raw), ("scheme", f.Host)) : M("badUrl", ("url", raw)));
                exit = 1;
                continue;
            }
            // the file a download is saved to: -O, or the URL's last segment (index.html), under -P
            string FileFor(WebUrl u) => RemoteName(u) is { Length: > 0 } name ? (prefix ?? "") + Uri.UnescapeDataString(name) : (prefix ?? "") + "index.html";
            if (noClobber && document == null && !spider && ctx.Fs.Exists(ctx.ResolvePath(FileFor(url))))
            {
                Say(M("clobber", ("file", FileFor(url))));
                Say("");
                continue;
            }
            if (spider) Say(M("spiderMode"));
            Say(M("start", ("time", Now()), ("url", url.ToString())));
            int hop = 0;
            void OnHop(WebResponse r)
            {
                if (hop++ > 0) { if (spider) Say(M("spiderMode")); Say(M("start", ("time", Now()), ("url", r.Url.ToString()))); }
                Say(M("sent", ("status", server ? "" : $"{r.Status} {r.Reason}")));
                if (server)
                {
                    var sb = new StringBuilder($"  HTTP/1.1 {r.Status} {r.Reason}\n");
                    foreach (var (k, v) in r.Headers) sb.Append("  ").Append(k).Append(": ").Append(v).Append('\n');
                    ctx.Stderr.Write(sb.ToString());
                }
                if (r.Status is 301 or 302 or 303 or 307 or 308 && r.Header("Location") is { Length: > 0 } loc) Say(M("following", ("location", loc)));
            }
            try
            {
                var (r, _) = await web.FetchAsync(url, spider ? "HEAD" : body != null ? "POST" : "GET", null, [.. headers], body, true, maxRedirect, timeout, OnHop, ctx.CancellationToken);
                if (r.Status >= 400)
                {
                    if (brief) Note(M("nvErrorUrl", ("url", r.Url.ToString())));
                    Note(spider ? M("spiderMissing") : M("httpError", ("time", Now()), ("status", $"{r.Status}: {r.Reason}")));
                    Say("");
                    code = 8;
                }
                else if (spider)
                {
                    var type = r.Header("Content-Type");
                    Say(M("length", ("length", (r.Header("Content-Length") ?? M("lengthUnspecified")) + (type != null ? $" [{type}]" : ""))));
                    Say(M("spiderExists"));
                    Say("");
                    if (brief) Note(M("nvSpider", ("time", Now()), ("url", r.Url.ToString()), ("status", $"{r.Status} {r.Reason}")));
                }
                else
                {
                    var length = r.Header("Content-Length");
                    var type = r.Header("Content-Type");
                    Say(M("length", ("length", (length ?? M("lengthUnspecified")) + (type != null ? $" [{type}]" : ""))));
                    string? file = document;
                    if (file == null)
                    {
                        file = FileFor(url);
                        if (prefix != null)
                            try { ctx.Fs.CreateDirectory(ctx.ResolvePath(prefix), parents: true); }
                            catch (ShellException e) { ctx.Stderr.WriteLine(M("writeFailed", ("file", file), ("errno", Errno(e)))); exit = 3; continue; }
                        if (ctx.Fs.Exists(ctx.ResolvePath(file)))
                        {
                            int n = 1;
                            while (ctx.Fs.Exists(ctx.ResolvePath($"{file}.{n}"))) n++;
                            file = $"{file}.{n}";
                        }
                    }
                    Say(M("saving", ("file", file == "-" ? "STDOUT" : file)));
                    if (file == "-") ctx.Stdout.Write(ByteText.Decode(r.Body));
                    else
                    {
                        try
                        {
                            var path = ctx.ResolvePath(file);
                            ctx.Fs.WriteAllBytes(path, document != null ? [.. ctx.Fs.ReadAllBytes(path), .. r.Body] : r.Body);
                        }
                        catch (ShellException e) { ctx.Stderr.WriteLine(M("writeFailed", ("file", file), ("errno", Errno(e)))); exit = 3; continue; }
                    }
                    Say("");
                    var size = r.Body.Length.ToString(CultureInfo.InvariantCulture) + (length != null ? "/" + length : "");
                    if (r.Error != null) // the body ended early: kept as far as it came
                    {
                        Note(M(r.Error == "reset" ? "readError" : "closed", ("time", Now()), ("size", r.Body.Length.ToString(CultureInfo.InvariantCulture))));
                        Say("");
                        code = 4;
                    }
                    else
                    {
                        Say(file == "-" ? M("written", ("time", Now()), ("size", size)) : M("saved", ("time", Now()), ("file", file), ("size", size)));
                        Say("");
                    }
                    if (brief && r.Error == null) Note(M("nvSaved", ("time", Now()), ("url", r.Url.ToString()), ("size", size), ("file", file)));
                    if (r.Truncated) { ctx.Stderr.WriteLine(M("tooLarge", ("limit", web.Policy.MaxResponseBytes.ToString(CultureInfo.InvariantCulture)))); code = 1; }
                }
            }
            catch (WebFailure f)
            {
                (code, var message) = WgetError(f, web.Policy.ExplainBlocked);
                if (message.Length > 0 && (!quiet || f.Blocked != null && web.Policy.ExplainBlocked || f.Kind is WebError.Policy or WebError.Replay or WebError.BadHeader)) ctx.Stderr.WriteLine(message);
            }
            if (code != 0) exit = code;
        }
        return exit;
    }

    static (int Code, string Message) WgetError(WebFailure f, bool explain)
    {
        string M(string key, params (string, string)[] values) => CommandSpec.Message("wget", key, values);
        if (f.Blocked != null && (explain || f.Kind == WebError.Policy)) return (f.Kind is WebError.Resolve or WebError.Connect ? 4 : 1, M("blocked", ("reason", f.Blocked)));
        return f.Kind switch
        {
            WebError.BadUrl or WebError.BadHostname => (1, M("badUrl", ("url", f.Host))),
            WebError.Protocol => (1, M("scheme", ("url", f.Host + "://"), ("scheme", f.Host))),
            WebError.Resolve => (4, M("resolve", ("host", f.Host))),
            WebError.Connect => (4, M("connect", ("host", f.Host), ("port", f.Port.ToString(CultureInfo.InvariantCulture)))),
            WebError.Timeout => (4, M("timeout")),
            WebError.Tls => (5, M("tls", ("host", f.Host))),
            WebError.Redirects => (8, M("redirects", ("max", f.Detail))),
            WebError.BadHeader => (2, M("invalidHeader", ("header", ""))),
            WebError.Replay => (4, M("replay", ("request", f.Detail))),
            _ => (4, M("recv")),
        };
    }

    static string Errno(ShellException e) => e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..];
}
