using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Topwater.Tests;

/// <summary>
/// A stateful stand-in for Microsoft Graph (v1.0), as an HttpMessageHandler for GraphSource: sites with subsites, document
/// libraries with folders and files (eTag and cTag, children paging, path addressing, content through a pre-signed download
/// redirect, simple PUT and upload sessions, PATCH rename and move, DELETE, conflict behaviour, If-Match), and lists with
/// columns and items (eTags, fields PATCH, read-only columns), plus faults to inject. It answers the way the real service did
/// when it was checked (docs/connectors/graph-design.md, "The fake and the shared cases"); tests/connectors/graph/cases.json
/// runs against it.
/// </summary>
public sealed class GraphFake : HttpMessageHandler
{
    public const string Api = "https://graph.microsoft.com/v1.0";
    public const string Host = "fake.sharepoint.com";
    public const string Upload = "https://upload.fake.sharepoint.com/up/";
    const string IllegalChars = "\"*:<>?/\\|";
    static readonly HashSet<string> Reserved = ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(0, 10).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" }), ".LOCK", "DESKTOP.INI"];
    const int SimpleLimit = 4 * 1024 * 1024;

    public sealed class Site
    {
        public required string Id, DisplayName, Name, WebUrl;
        public string? Parent;
        public List<string> Drives = [], Lists = [], Subsites = [];
    }

    public sealed class Drive
    {
        public required string Id, Name, Root, WebUrl;
        public string? Site;
    }

    public sealed class Item
    {
        public required string Id, Drive, Name, Guid, Modified;
        public string? Parent;
        public bool Folder;
        public byte[] Content = [];
        public int E = 1, C = 1;
    }

    public sealed class GList
    {
        public required string Id, Site, DisplayName, Template;
        public bool Hidden;
        public List<JsonObject> Columns = [];
        public Dictionary<string, ListItem> Items = [];
        public int Next = 1;
    }

    public sealed class ListItem
    {
        public required JsonObject Fields;
        public required string Guid, Created, Modified, By, Editor;
        public int E = 1;
    }

    sealed class Fault
    {
        public required string Method;
        public required Regex Path;
        public int Status, Times;
        public List<KeyValuePair<string, string>> Headers = [];
        public byte[]? Body;
    }

    sealed class Refused(int status, string code, string message) : Exception(message)
    {
        public int Status = status;
        public string Code = code;
    }

    public string Token;
    public DateTime Clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public Dictionary<string, Site> Sites = [];
    public Dictionary<string, Drive> Drives = [];
    public Dictionary<string, Item> Items = [];
    public Dictionary<string, GList> Lists = [];
    readonly Dictionary<string, (string Drive, string? Item, string Parent, string Name, MemoryStream Data)> _sessions = [];
    public List<(string Method, string Path)> Requests = [];
    readonly List<Fault> _faults = [];
    public List<string> Leaks = []; // URLs outside the API that were sent the bearer token
    public string DownloadHost = "https://" + Host; // where content redirects to
    public int PageSize = 200; // the most one page holds
    public string? NextLinkOverride; // a next link to send instead of the real one (hostile tests)
    public string? Me; // the drive /me/drive is
    public string RootSite = "";
    int _n;
    readonly object _lock = new();

    public GraphFake(string token = "secret-token") => Token = token;

    // ----- building and inspecting -----

    int NextId() => ++_n;
    public string Stamp() => Clock.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    public void Tick(double seconds = 1) => Clock = Clock.AddSeconds(seconds);
    public string SiteUrl => Sites[RootSite].WebUrl;
    static string Guid(int n) => $"{n:X8}-0000-4000-8000-{n:X12}";

    public string AddSite(string title, string? parent = null)
    {
        int n = NextId();
        var url = (parent != null ? Sites[parent].WebUrl : $"https://{Host}/sites") + "/" + Regex.Replace(title, "[^A-Za-z0-9]", "");
        var sid = $"{Host},{Guid(n).ToLowerInvariant()},{Guid(n + 1000).ToLowerInvariant()}";
        Sites[sid] = new Site { Id = sid, DisplayName = title, Name = url[(url.LastIndexOf('/') + 1)..], WebUrl = url, Parent = parent };
        if (parent != null) Sites[parent].Subsites.Add(sid);
        else if (RootSite.Length == 0) RootSite = sid;
        return sid;
    }

    public string AddDrive(string? site, string name)
    {
        int n = NextId();
        var did = $"b!fakeDrive{n:D4}";
        var root = NewItem(did, null, "root", folder: true);
        var web = (site != null ? Sites[site].WebUrl : $"https://{Host}/personal/me") + "/" + (name == "Documents" ? "Shared Documents" : name);
        Drives[did] = new Drive { Id = did, Name = name, Root = root, WebUrl = web.Replace(" ", "%20"), Site = site };
        if (site != null) Sites[site].Drives.Add(did);
        if (Me == null && site == null) Me = did;
        return did;
    }

    string NewItem(string drive, string? parent, string name, bool folder = false, byte[]? content = null)
    {
        int n = NextId();
        var iid = $"01FAKE{n:D6}";
        Items[iid] = new Item { Id = iid, Drive = drive, Parent = parent, Name = name, Folder = folder, Content = content ?? [], Guid = Guid(n), Modified = Stamp() };
        return iid;
    }

    /// <summary>A file (content) or a folder (null) at path, with the folders above it.</summary>
    public string Add(string drive, string path, byte[]? content)
    {
        var cur = Drives[drive].Root;
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            var kid = Child(cur, parts[i]);
            if (kid == null) kid = NewItem(drive, cur, parts[i], !last || content == null, !last || content == null ? [] : content);
            else if (last && content != null) SetContent(kid, content);
            cur = kid;
        }
        return cur;
    }

    public string Add(string drive, string path, string? content) => Add(drive, path, content == null ? null : Encoding.UTF8.GetBytes(content));

    public string AddList(string site, string title, JsonObject? columns = null, string template = "genericList", bool hidden = false)
    {
        int n = NextId();
        var lid = Guid(n).ToLowerInvariant();
        var cols = new List<JsonObject>
        {
            new() { ["name"] = "Title", ["displayName"] = "Title", ["text"] = new JsonObject { ["maxLength"] = 255 }, ["readOnly"] = false },
            new() { ["name"] = "ID", ["displayName"] = "ID", ["number"] = new JsonObject(), ["readOnly"] = true },
            new() { ["name"] = "Created", ["displayName"] = "Created", ["dateTime"] = new JsonObject(), ["readOnly"] = true },
            new() { ["name"] = "Modified", ["displayName"] = "Modified", ["dateTime"] = new JsonObject(), ["readOnly"] = true },
            new() { ["name"] = "Author", ["displayName"] = "Created By", ["personOrGroup"] = new JsonObject(), ["readOnly"] = true },
            new() { ["name"] = "Editor", ["displayName"] = "Modified By", ["personOrGroup"] = new JsonObject(), ["readOnly"] = true },
            new() { ["name"] = "ContentType", ["displayName"] = "Content Type", ["readOnly"] = true },
            new() { ["name"] = "Attachments", ["displayName"] = "Attachments", ["boolean"] = new JsonObject(), ["readOnly"] = false },
            new() { ["name"] = "_ColorTag", ["displayName"] = "Color Tag", ["text"] = new JsonObject(), ["readOnly"] = true },
            new() { ["name"] = "LinkTitle", ["displayName"] = "Title", ["computed"] = new JsonObject(), ["readOnly"] = true, ["hidden"] = true },
        };
        foreach (var (name, spec) in columns ?? [])
        {
            var col = new JsonObject { ["name"] = name, ["displayName"] = name, ["readOnly"] = false };
            if (spec is JsonArray choices) col["choice"] = new JsonObject { ["choices"] = choices.DeepClone(), ["allowTextEntry"] = false, ["displayAs"] = "dropDownMenu" };
            else if (spec is JsonObject o) foreach (var (k, v) in o) col[k] = v?.DeepClone();
            else switch (spec!.GetValue<string>())
            {
                case "text": col["text"] = new JsonObject { ["allowMultipleLines"] = false, ["maxLength"] = 255 }; break;
                case "note": col["text"] = new JsonObject { ["allowMultipleLines"] = true }; break;
                case "currency": col["currency"] = new JsonObject { ["locale"] = "en-US" }; break;
                case "calculated": col["calculated"] = new JsonObject { ["formula"] = "=1" }; col["readOnly"] = true; break;
                case var t: col[t] = new JsonObject(); break;
            }
            cols.Add(col);
        }
        for (int i = 0; i < cols.Count; i++)
        {
            cols[i].TryAdd("hidden", false);
            cols[i]["id"] = Guid(n * 100 + i).ToLowerInvariant();
        }
        Lists[lid] = new GList { Id = lid, Site = site, DisplayName = title, Template = template, Hidden = hidden, Columns = cols };
        Sites[site].Lists.Add(lid);
        return lid;
    }

    public string AddItem(string lid, JsonObject fields, string who = "Grant Miller")
    {
        var lst = Lists[lid];
        var iid = lst.Next++.ToString(CultureInfo.InvariantCulture);
        var f = (JsonObject)fields.DeepClone();
        f["id"] = iid;
        f["Created"] = Stamp();
        f["Modified"] = Stamp();
        f["AuthorLookupId"] = "10";
        f["EditorLookupId"] = "10";
        f["ContentType"] = "Item";
        f["Attachments"] = false;
        lst.Items[iid] = new ListItem { Fields = f, Guid = Guid(NextId()).ToLowerInvariant(), Created = Stamp(), Modified = Stamp(), By = who, Editor = who };
        return iid;
    }

    /// <summary>{site, libraries: {name: {path: text, "folder/": null}}, lists: {name: {columns, items: [fields]}}, subsites: {name: tree}}</summary>
    public static GraphFake FromTree(JsonObject tree, string token = "secret-token")
    {
        var fake = new GraphFake(token);
        void Build(JsonObject node, string title, string? parent)
        {
            var sid = fake.AddSite(title, parent);
            foreach (var (lib, files) in node["libraries"] as JsonObject ?? [])
            {
                var d = fake.AddDrive(sid, lib);
                foreach (var (p, v) in files as JsonObject ?? []) fake.Add(d, p, v?.GetValue<string>());
            }
            foreach (var (name, spec) in node["lists"] as JsonObject ?? [])
            {
                var lid = fake.AddList(sid, name, spec!["columns"] as JsonObject, spec["template"]?.GetValue<string>() ?? "genericList", spec["hidden"]?.GetValue<bool>() ?? false);
                foreach (var fields in spec["items"] as JsonArray ?? []) fake.AddItem(lid, (JsonObject)fields!);
            }
            foreach (var (name, sub) in node["subsites"] as JsonObject ?? []) Build((JsonObject)sub!, name, sid);
        }
        Build(tree, tree["site"]?.GetValue<string>() ?? "Site", null);
        return fake;
    }

    public string DriveNamed(string name, string? site = null) => Drives.Values.First(d => d.Name == name && (site == null || d.Site == site)).Id;
    public string ListNamed(string title) => Lists.Values.First(l => l.DisplayName == title).Id;

    public string? Find(string drive, string path)
    {
        string? cur = Drives[drive].Root;
        foreach (var part in path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            if ((cur = Child(cur!, part)) == null) return null;
        return cur;
    }

    /// <summary>Someone else changes a file.</summary>
    public void Edit(string drive, string path, string content) { lock (_lock) SetContent(Find(drive, path)!, Encoding.UTF8.GetBytes(content)); }
    public void Remove(string drive, string path) { lock (_lock) Delete(Find(drive, path)!); }

    /// <summary>The next <paramref name="times"/> requests of <paramref name="method"/> whose URL path matches the regex get this answer instead.</summary>
    public void AddFault(string method, string path, int status, IEnumerable<KeyValuePair<string, string>>? headers = null, string? body = null, int times = 1)
    {
        lock (_lock) _faults.Add(new Fault { Method = method, Path = new Regex(path), Status = status, Headers = [.. headers ?? []], Body = body == null ? null : Encoding.UTF8.GetBytes(body), Times = times });
    }
    public void ClearFaults() { lock (_lock) _faults.Clear(); }

    /// <summary>Every site, library path and list item, one a line: what tests compare (the same text as the Python fake's).</summary>
    public string Outline()
    {
        var output = new List<string>();
        void SiteLines(string sid, string at)
        {
            var s = Sites[sid];
            foreach (var d in s.Drives)
            {
                void Walk(string iid, string p)
                {
                    foreach (var k in ChildrenOf(iid).OrderBy(k => Items[k].Name, StringComparer.Ordinal))
                    {
                        var it = Items[k];
                        var q = $"{p}/{it.Name}";
                        var text = Encoding.UTF8.GetString(it.Content);
                        output.Add(it.Folder ? q + "/" : $"{q} ({PyRepr(text.Length > 40 ? text[..40] : text)})");
                        if (it.Folder) Walk(k, q);
                    }
                }
                Walk(Drives[d].Root, $"{at}{Drives[d].Name}");
            }
            foreach (var lid in s.Lists)
            {
                var lst = Lists[lid];
                foreach (var (iid, it) in lst.Items.OrderBy(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture)))
                {
                    string[] hidden = ["id", "ID", "Created", "Modified", "AuthorLookupId", "EditorLookupId", "ContentType", "Attachments", "Title"];
                    var shown = new JsonObject();
                    foreach (var (k, v) in it.Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                        if (!hidden.Contains(k) && v != null) shown[k] = v.DeepClone();
                    var title = it.Fields["Title"] is JsonValue t && t.GetValueKind() == JsonValueKind.String ? PyRepr(t.GetValue<string>()) : "None";
                    output.Add($"{at}{lst.DisplayName}#{iid} {title} {PyJson(shown)}");
                }
            }
            foreach (var sub in s.Subsites) SiteLines(sub, $"{at}{Sites[sub].DisplayName}/");
        }
        lock (_lock) SiteLines(RootSite, "/");
        return string.Join("\n", output) + "\n";
    }

    /// <summary>Python's repr of a string.</summary>
    static string PyRepr(string s)
    {
        char q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(q);
        foreach (var c in s)
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c == q => "\\" + c,
                _ when c < ' ' || c == '\x7f' => $"\\x{(int)c:x2}",
                _ => c.ToString(),
            });
        return sb.Append(q).ToString();
    }

    /// <summary>Python's json.dumps (", " and ": " between items, non-ASCII escaped) of a value.</summary>
    static string PyJson(JsonNode? n) => n switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => PyJson(JsonValue.Create(kv.Key)) + ": " + PyJson(kv.Value))) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyJson)) + "]",
        _ when n.GetValueKind() == JsonValueKind.String => JsonSerializer.Serialize(n.GetValue<string>()),
        _ => n.ToJsonString(),
    };

    // ----- the transport -----

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var hdr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in request.Headers.NonValidated) hdr.TryAdd(k, v.ToString());
        if (request.Content != null) foreach (var (k, v) in request.Content.Headers.NonValidated) hdr.TryAdd(k, v.ToString());
        var url = request.RequestUri!.OriginalString;
        var u = request.RequestUri;
        var method = request.Method.Method;
        lock (_lock)
        {
            Requests.Add((method, u.AbsolutePath));
            foreach (var f in _faults)
                if (f.Times > 0 && f.Method == method && f.Path.IsMatch(u.AbsolutePath))
                {
                    f.Times--;
                    return Respond(f.Status, f.Headers, f.Body ?? Encoding.UTF8.GetBytes("{\"error\": {\"code\": \"fault\", \"message\": \"injected\"}}"), null);
                }
            bool api = url.StartsWith(Api + "/", StringComparison.Ordinal);
            if (!api && hdr.ContainsKey("Authorization")) Leaks.Add(url);
            try
            {
                if (url.StartsWith(Upload, StringComparison.Ordinal)) return UploadRoute(method, u.AbsolutePath[(u.AbsolutePath.LastIndexOf('/') + 1)..], hdr, body ?? []);
                if (!api) return Download(method, u);
                if (!hdr.TryGetValue("Authorization", out var auth) || auth != "Bearer " + Token)
                    throw new Refused(401, "InvalidAuthenticationToken", "Access token is empty or invalid.");
                var query = new Dictionary<string, string>();
                foreach (var pair in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    if (eq < 0 || eq == pair.Length - 1) continue;
                    query.TryAdd(Uri.UnescapeDataString(pair[..eq].Replace('+', ' ')), Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')));
                }
                JsonObject? data = null;
                if (body is { Length: > 0 } && hdr.TryGetValue("Content-Type", out var ctype) && ctype.Contains("application/json"))
                    data = JsonNode.Parse(body) as JsonObject;
                return Route(method, u.AbsolutePath["/v1.0".Length..], query, hdr, data, body, url);
            }
            catch (Refused e)
            {
                var err = new JsonObject { ["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message, ["innerError"] = new JsonObject { ["date"] = Stamp() } } };
                return Respond(e.Status, null, err, null);
            }
        }
    }

    static HttpResponseMessage Respond(int status, IEnumerable<KeyValuePair<string, string>>? headers, object? data, string? contentType = "application/json")
    {
        var r = new HttpResponseMessage((HttpStatusCode)status);
        byte[] bytes = data switch { byte[] b => b, JsonNode j => Encoding.UTF8.GetBytes(j.ToJsonString()), _ => [] };
        r.Content = new ByteArrayContent(bytes);
        if (data is JsonNode || (data is null && contentType != null)) r.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        foreach (var (k, v) in headers ?? [])
            if (!r.Headers.TryAddWithoutValidation(k, v)) r.Content.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    static HttpResponseMessage Ok(int status, JsonNode? data, params (string, string)[] headers) =>
        Respond(status, headers.Select(h => KeyValuePair.Create(h.Item1, h.Item2)), data);

    // ----- routes -----

    HttpResponseMessage Route(string method, string path, Dictionary<string, string> q, Dictionary<string, string> hdr, JsonObject? data, byte[]? raw, string url)
    {
        var m = Regex.Match(path, "^/sites/([^/:]+):(/.*)$");
        if (m.Success && method == "GET")
        {
            var want = $"https://{Uri.UnescapeDataString(m.Groups[1].Value)}{Uri.UnescapeDataString(m.Groups[2].Value)}".TrimEnd('/').ToLowerInvariant();
            var s = Sites.Values.FirstOrDefault(s => s.WebUrl.ToLowerInvariant() == want) ?? throw new Refused(404, "itemNotFound", "Requested site could not be found");
            return Ok(200, SiteJson(s));
        }
        m = Regex.Match(path, "^/sites/([^/]+)(/.*)?$");
        if (m.Success)
        {
            var s = Sites.GetValueOrDefault(Uri.UnescapeDataString(m.Groups[1].Value)) ?? throw new Refused(404, "itemNotFound", "Requested site could not be found");
            return SiteRoute(method, s, m.Groups[2].Value, q, hdr, data, url);
        }
        if (path == "/me/drive")
        {
            if (Me == null) throw new Refused(404, "itemNotFound", "no drive");
            return Ok(200, DriveJson(Drives[Me]));
        }
        m = Regex.Match(path, "^/drives/([^/]+)(/.*)?$");
        if (m.Success)
        {
            var d = Drives.GetValueOrDefault(Uri.UnescapeDataString(m.Groups[1].Value)) ?? throw new Refused(404, "itemNotFound", "The drive could not be found");
            if (m.Groups[2].Value.Length == 0) return Ok(200, DriveJson(d));
            return DriveRoute(method, d, m.Groups[2].Value, q, hdr, data, raw, url);
        }
        throw new Refused(400, "BadRequest", $"Resource not found for the segment '{path}'.");
    }

    static JsonObject SiteJson(Site s) => new()
    {
        ["id"] = s.Id, ["name"] = s.Name, ["displayName"] = s.DisplayName, ["webUrl"] = s.WebUrl,
        ["createdDateTime"] = "2026-01-01T00:00:00Z", ["siteCollection"] = new JsonObject { ["hostname"] = Host },
    };

    static JsonObject DriveJson(Drive d) => new()
    {
        ["id"] = d.Id, ["name"] = d.Name, ["webUrl"] = d.WebUrl, ["driveType"] = d.Site != null ? "documentLibrary" : "business",
    };

    HttpResponseMessage Paged(IEnumerable<JsonNode> rows, Dictionary<string, string> q, string url)
    {
        var all = rows.ToList();
        int top = Math.Min(Math.Min(q.TryGetValue("$top", out var t) ? int.Parse(t, CultureInfo.InvariantCulture) : PageSize, PageSize), 999);
        int skip = q.TryGetValue("$skiptoken", out var sk) ? int.Parse(sk, CultureInfo.InvariantCulture) : 0;
        var output = new JsonObject { ["value"] = new JsonArray([.. all.Skip(skip).Take(top)]) };
        if (skip + top < all.Count)
            output["@odata.nextLink"] = NextLinkOverride ?? Regex.Replace(url, @"&\$skiptoken=\d+", "") + $"&$skiptoken={skip + top}";
        return Ok(200, output);
    }

    HttpResponseMessage SiteRoute(string method, Site s, string rest, Dictionary<string, string> q, Dictionary<string, string> hdr, JsonObject? data, string url)
    {
        if (rest == "" && method == "GET") return Ok(200, SiteJson(s));
        if (rest == "/drives" && method == "GET") return Paged(s.Drives.Select(d => DriveJson(Drives[d])), q, url);
        if (rest == "/sites" && method == "GET") return Paged(s.Subsites.Select(x => SiteJson(Sites[x])), q, url);
        if (rest == "/lists")
        {
            if (method == "POST") throw new Refused(403, "accessDenied", "Access denied");
            var rows = s.Lists.Select(lid => (JsonNode)new JsonObject
            {
                ["id"] = lid, ["name"] = Lists[lid].DisplayName.Replace(" ", ""), ["displayName"] = Lists[lid].DisplayName,
                ["list"] = new JsonObject { ["hidden"] = Lists[lid].Hidden, ["template"] = Lists[lid].Template, ["contentTypesEnabled"] = false },
            }).ToList();
            rows.AddRange(s.Drives.Select((d, i) => (JsonNode)new JsonObject
            {
                ["id"] = Guid(9000 + i).ToLowerInvariant(), ["name"] = d, ["displayName"] = Drives[d].Name,
                ["list"] = new JsonObject { ["hidden"] = false, ["template"] = "documentLibrary", ["contentTypesEnabled"] = false },
            }));
            return Paged(rows, q, url);
        }
        var m = Regex.Match(rest, "^/lists/([^/]+)(/.*)?$");
        if (!m.Success || !Lists.TryGetValue(m.Groups[1].Value, out var lst) || lst.Site != s.Id) throw new Refused(404, "itemNotFound", "The specified list was not found");
        var sub = m.Groups[2].Value;
        if (sub == "/columns" && method == "GET") return Paged(lst.Columns.Select(c => c.DeepClone()), q, url);
        if (sub == "/items" && method == "GET") return Paged(lst.Items.Keys.OrderBy(k => int.Parse(k, CultureInfo.InvariantCulture)).Select(i => (JsonNode)ListItemJson(lst, i, q)), q, url);
        if (sub == "/items" && method == "POST")
        {
            var fields = data?["fields"] as JsonObject ?? [];
            CheckFields(lst, fields);
            var clean = new JsonObject();
            foreach (var (k, v) in fields) if (!k.Contains('@')) clean[k] = v?.DeepClone();
            var iid = AddItem(lst.Id, clean);
            Tick();
            return Ok(201, ListItemJson(lst, iid, new() { ["expand"] = "fields" }));
        }
        m = Regex.Match(sub, @"^/items/(\d+)(/fields)?$");
        if (!m.Success || !lst.Items.TryGetValue(m.Groups[1].Value, out var it)) throw new Refused(404, "itemNotFound", "Item not found");
        var id = m.Groups[1].Value;
        bool fieldsPath = m.Groups[2].Success;
        if (method == "GET" && !fieldsPath) return Ok(200, ListItemJson(lst, id, q));
        IfMatch(hdr, [ItemETag(it)], "resourceModified");
        if (method == "DELETE" && !fieldsPath)
        {
            lst.Items.Remove(id);
            return Ok(204, null);
        }
        if (method == "PATCH" && fieldsPath)
        {
            CheckFields(lst, data ?? []);
            foreach (var (k, v) in data ?? []) if (!k.Contains('@')) it.Fields[k] = v?.DeepClone();
            Tick();
            it.E++;
            it.Modified = Stamp();
            it.Fields["Modified"] = Stamp();
            it.Editor = "Topwater Agent";
            var output = new JsonObject { ["@odata.etag"] = ItemETag(it) };
            foreach (var (k, v) in it.Fields) output[k] = v?.DeepClone();
            return Ok(200, output);
        }
        throw new Refused(405, "invalidRequest", "Method not allowed");
    }

    static void CheckFields(GList lst, JsonObject fields)
    {
        foreach (var (k, _) in fields)
        {
            if (k.Contains('@')) continue;
            var c = lst.Columns.FirstOrDefault(c => c["name"]!.GetValue<string>() == k) ?? throw new Refused(400, "invalidRequest", $"Field '{k}' is not recognized");
            if (c["readOnly"]?.GetValue<bool>() == true) throw new Refused(400, "invalidRequest", $"Field '{k}' is read-only");
        }
    }

    static string ItemETag(ListItem it) => $"\"{it.Guid},{it.E}\"";

    static JsonObject ListItemJson(GList lst, string iid, Dictionary<string, string> q)
    {
        var it = lst.Items[iid];
        var output = new JsonObject
        {
            ["@odata.etag"] = ItemETag(it), ["eTag"] = ItemETag(it), ["id"] = iid, ["createdDateTime"] = it.Created,
            ["lastModifiedDateTime"] = it.Modified, ["contentType"] = new JsonObject { ["id"] = "0x0100", ["name"] = "Item" },
            ["createdBy"] = new JsonObject { ["user"] = new JsonObject { ["displayName"] = it.By } },
            ["lastModifiedBy"] = new JsonObject { ["user"] = new JsonObject { ["displayName"] = it.Editor } },
            ["parentReference"] = new JsonObject { ["id"] = lst.Id, ["siteId"] = lst.Site },
        };
        if (q.GetValueOrDefault("expand", "").Contains("fields"))
        {
            var f = new JsonObject { ["@odata.etag"] = ItemETag(it) };
            foreach (var (k, v) in it.Fields) f[k] = v?.DeepClone();
            output["fields"] = f;
        }
        return output;
    }

    // ----- drive items -----

    List<string> ChildrenOf(string iid) =>
        [.. Items.Values.Where(v => v.Parent == iid).OrderBy(v => v.Name.ToLowerInvariant(), StringComparer.Ordinal).Select(v => v.Id)];

    string? Child(string iid, string name) => ChildrenOf(iid).FirstOrDefault(k => Items[k].Name.ToLowerInvariant() == name.ToLowerInvariant());

    string PathOf(string iid)
    {
        var parts = new List<string>();
        while (Items[iid].Parent is { } p) { parts.Add(Items[iid].Name); iid = p; }
        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    static (string ETag, string? CTag) Tags(Item it) => ($"\"{{{it.Guid}}},{it.E}\"", it.Folder ? null : $"\"c:{{{it.Guid}}},{it.C}\"");

    JsonObject ItemJson(string iid)
    {
        var it = Items[iid];
        var (e, c) = Tags(it);
        var d = Drives[it.Drive];
        var output = new JsonObject
        {
            ["id"] = iid, ["name"] = it.Parent == null ? "root" : it.Name, ["eTag"] = e, ["lastModifiedDateTime"] = it.Modified,
            ["createdDateTime"] = "2026-01-01T00:00:00Z",
            ["size"] = it.Folder ? Items.Keys.Where(k => Under(k, iid)).Sum(k => (long)Items[k].Content.Length) : it.Content.Length,
        };
        if (it.Parent != null)
        {
            var parentPath = PathOf(it.Parent);
            output["parentReference"] = new JsonObject
            {
                ["driveId"] = d.Id, ["driveType"] = "documentLibrary", ["id"] = it.Parent,
                ["path"] = $"/drives/{d.Id}/root:" + (parentPath == "/" ? "" : parentPath),
            };
        }
        else output["root"] = new JsonObject();
        if (it.Folder) output["folder"] = new JsonObject { ["childCount"] = ChildrenOf(iid).Count };
        else
        {
            output["cTag"] = c;
            output["file"] = new JsonObject { ["mimeType"] = "application/octet-stream", ["hashes"] = new JsonObject { ["quickXorHash"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAA=" } };
        }
        return output;
    }

    bool Under(string? k, string top)
    {
        for (; k != null; k = Items[k].Parent) if (k == top) return true;
        return false;
    }

    void SetContent(string iid, byte[] data)
    {
        var it = Items[iid];
        Tick();
        it.Content = data;
        it.Modified = Stamp();
        it.E++;
        it.C++;
        Touch(it.Parent);
    }

    void Touch(string? iid)
    {
        if (iid != null) Items[iid].E++; // a folder's eTag changes with what it holds
    }

    void Delete(string iid)
    {
        foreach (var k in Items.Keys.Where(k => Under(k, iid)).ToList()) Items.Remove(k);
    }

    static void IfMatch(Dictionary<string, string> hdr, IEnumerable<string?> tags, string code)
    {
        if (hdr.TryGetValue("If-Match", out var want) && want != "*" && !tags.Contains(want))
            throw new Refused(412, code, "ETag does not match current item's value");
    }

    static void CheckName(string name)
    {
        if (name.Length == 0 || name.Any(c => IllegalChars.Contains(c)) || Reserved.Contains(name.ToUpperInvariant()))
            throw new Refused(400, "invalidRequest", "The provided name cannot contain any illegal characters.");
        if (name != name.Trim(' ')) throw new Refused(400, "invalidRequest", "The provided name cannot contain leading, or trailing, spaces.");
        if (name.EndsWith('.')) throw new Refused(400, "invalidRequest", "The provided name cannot contain any illegal characters.");
        if (name.ToLowerInvariant().Contains("_vti_")) throw new Refused(403, "accessDenied", "Access denied");
    }

    /// <summary>(the item or null, its parent's id or "", its name, what follows: "", "/children", "/content", ...)</summary>
    (string? Item, string Parent, string Name, string Tail) Resolve(Drive d, string rest)
    {
        if (rest == "/root" || rest.StartsWith("/root/", StringComparison.Ordinal)) return (d.Root, "", "", rest[5..]);
        var m = Regex.Match(rest, "^/root:(/[^:]*?):?(/children|/content|/createUploadSession)?$");
        if (!m.Success) throw new Refused(400, "BadRequest", "Bad path");
        var parts = m.Groups[1].Value.Split('/')[1..].Where(p => p != "").Select(Uri.UnescapeDataString).ToList();
        string? cur = d.Root, parent = null;
        foreach (var part in parts)
        {
            parent = cur;
            cur = cur != null ? Child(cur, part) : null;
        }
        return (cur, parent ?? "", parts.Count > 0 ? parts[^1] : "", m.Groups[2].Value);
    }

    HttpResponseMessage DriveRoute(string method, Drive d, string rest, Dictionary<string, string> q, Dictionary<string, string> hdr, JsonObject? data, byte[]? raw, string url)
    {
        var (iid, parent, name, tail) = Resolve(d, rest);
        if (tail == "/children" && method == "GET")
        {
            if (iid == null) throw new Refused(404, "itemNotFound", "The resource could not be found.");
            return Paged(ChildrenOf(iid).Select(k => (JsonNode)ItemJson(k)), q, url);
        }
        if (tail == "/children" && method == "POST")
        {
            if (iid == null) throw new Refused(404, "itemNotFound", "The resource could not be found.");
            var fresh = data?["name"]?.GetValue<string>() ?? "";
            CheckName(fresh);
            if (Child(iid, fresh) != null && data?["@microsoft.graph.conflictBehavior"]?.GetValue<string>() == "fail")
                throw new Refused(409, "nameAlreadyExists", "Name already exists");
            Tick();
            var k = NewItem(d.Id, iid, fresh, folder: data?.ContainsKey("folder") == true);
            Touch(iid);
            return Ok(201, ItemJson(k));
        }
        if (tail == "/content" && method == "GET")
        {
            if (iid == null) throw new Refused(404, "itemNotFound", "The resource could not be found.");
            return Respond(302, [KeyValuePair.Create("Location", $"{DownloadHost}/_layouts/15/download.aspx?UniqueId={iid}&tempauth=fake.{iid}")], null);
        }
        if (tail == "/content" && method == "PUT")
        {
            if (raw != null && raw.Length > SimpleLimit) throw new Refused(413, "requestTooLarge", "Request entity too large");
            return Put(d, iid, parent, name, hdr, q, raw ?? []);
        }
        if (tail == "/createUploadSession" && method == "POST")
        {
            if (iid != null)
            {
                var (e, c) = Tags(Items[iid]);
                IfMatch(hdr, [e, c], "notAllowed");
                if ((data?["item"] as JsonObject)?["@microsoft.graph.conflictBehavior"]?.GetValue<string>() == "fail")
                    throw new Refused(409, "nameAlreadyExists", "The specified item name already exists.");
            }
            else if (parent.Length == 0) throw new Refused(404, "itemNotFound", "The resource could not be found.");
            else CheckName(name);
            var token = $"s{NextId()}";
            _sessions[token] = (d.Id, iid, parent, name, new MemoryStream());
            return Ok(200, new JsonObject { ["uploadUrl"] = Upload + token, ["expirationDateTime"] = "2026-01-02T00:00:00Z", ["nextExpectedRanges"] = new JsonArray("0-") });
        }
        if (tail.Length > 0) throw new Refused(405, "invalidRequest", "Method not allowed");
        if (iid == null) throw new Refused(404, "itemNotFound", "The resource could not be found.");
        if (method == "GET") return Ok(200, ItemJson(iid));
        var it = Items[iid];
        if (method == "DELETE")
        {
            if (it.Parent == null) throw new Refused(403, "accessDenied", "Cannot delete the root");
            var (e, c) = Tags(it);
            IfMatch(hdr, [e, c], "resourceModified");
            Delete(iid);
            Touch(it.Parent);
            return Ok(204, null);
        }
        if (method == "PATCH")
        {
            var (e, c) = Tags(it);
            IfMatch(hdr, [e, c], "resourceModified");
            var newName = data?["name"]?.GetValue<string>() ?? it.Name;
            var newParent = (data?["parentReference"] as JsonObject)?["id"]?.GetValue<string>() ?? it.Parent!;
            if (!Items.TryGetValue(newParent, out var np) || !np.Folder || np.Drive != d.Id) throw new Refused(400, "invalidRequest", "Invalid parent reference");
            if (Under(newParent, iid)) throw new Refused(400, "invalidRequest", "Cannot move a folder into itself");
            CheckName(newName);
            var other = Child(newParent, newName);
            if (other != null && other != iid) throw new Refused(409, "nameAlreadyExists", "The specified item name already exists.");
            Tick();
            Touch(it.Parent);
            (it.Name, it.Parent) = (newName, newParent);
            Touch(newParent);
            it.E++;
            return Ok(200, ItemJson(iid));
        }
        throw new Refused(405, "invalidRequest", "Method not allowed");
    }

    HttpResponseMessage Put(Drive d, string? iid, string parent, string name, Dictionary<string, string> hdr, Dictionary<string, string> q, byte[] data)
    {
        if (iid != null)
        {
            if (Items[iid].Folder) throw new Refused(409, "nameAlreadyExists", "A folder has this name");
            if (q.GetValueOrDefault("@microsoft.graph.conflictBehavior") == "fail") throw new Refused(409, "nameAlreadyExists", "The specified item name already exists.");
            var (e, c) = Tags(Items[iid]);
            IfMatch(hdr, [e, c], "notAllowed");
            SetContent(iid, data);
            return Ok(200, ItemJson(iid));
        }
        if (hdr.ContainsKey("If-Match")) throw new Refused(412, "notAllowed", "ETag does not match current item's value");
        if (parent.Length == 0) throw new Refused(404, "itemNotFound", "The resource could not be found.");
        CheckName(name);
        Tick();
        var k = NewItem(d.Id, parent, name, content: data);
        Touch(parent);
        return Ok(201, ItemJson(k));
    }

    HttpResponseMessage UploadRoute(string method, string token, Dictionary<string, string> hdr, byte[] body)
    {
        if (!_sessions.TryGetValue(token, out var s)) throw new Refused(404, "itemNotFound", "The upload session was not found");
        if (method == "DELETE")
        {
            _sessions.Remove(token);
            return Ok(204, null);
        }
        var m = Regex.Match(hdr.GetValueOrDefault("Content-Range", ""), @"^bytes (\d+)-(\d+)/(\d+)$");
        if (method != "PUT" || !m.Success) throw new Refused(400, "invalidRequest", "Bad Content-Range");
        long start = long.Parse(m.Groups[1].Value), end = long.Parse(m.Groups[2].Value), total = long.Parse(m.Groups[3].Value);
        if (start != s.Data.Length || end - start + 1 != body.Length) throw new Refused(416, "invalidRange", "The fragment overlaps or skips");
        if (end + 1 < total && body.Length % (320 * 1024) != 0) throw new Refused(400, "invalidRequest", "A fragment must be a multiple of 320 KiB");
        s.Data.Write(body);
        if (end + 1 < total) return Ok(202, new JsonObject { ["expirationDateTime"] = "2026-01-02T00:00:00Z", ["nextExpectedRanges"] = new JsonArray($"{end + 1}-") });
        _sessions.Remove(token);
        if (s.Item != null && Items.ContainsKey(s.Item))
        {
            SetContent(s.Item, s.Data.ToArray());
            return Ok(200, ItemJson(s.Item));
        }
        Tick();
        var k = NewItem(s.Drive, s.Parent, s.Name, content: s.Data.ToArray());
        return Ok(201, ItemJson(k));
    }

    HttpResponseMessage Download(string method, Uri u)
    {
        var m = Regex.Match(u.Query, "UniqueId=([^&]+)&tempauth=fake\\.([^&]+)");
        if (method != "GET" || !m.Success || m.Groups[1].Value != m.Groups[2].Value) return Respond(401, null, Array.Empty<byte>(), null);
        if (!Items.TryGetValue(m.Groups[1].Value, out var it)) return Respond(404, null, Array.Empty<byte>(), null);
        return Respond(200, [KeyValuePair.Create("Content-Type", "application/octet-stream")], it.Content, null);
    }
}
