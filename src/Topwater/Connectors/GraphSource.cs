// OneDrive and SharePoint through Microsoft Graph as a mount source (docs/connectors/graph.md, design in
// docs/connectors/graph-design.md). A drive (a OneDrive, or one document library) is its folders and files, Office documents
// as their real bytes. A site is a folder of its document libraries, its lists and its subsites, by name; when two of them
// share a name, the list gets " (list)" and the subsite " (site)". A list is a folder of items, each `Title.md` holding the
// item's columns as front matter. The token never reaches the agent, and is never sent anywhere but the Graph API.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Topwater.Connectors.ConnectorCommon;
using Headers = System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>;

namespace Topwater.Connectors;

/// <summary>What <see cref="GraphSource"/> mounts and how it reaches Microsoft Graph. Give exactly one of <see cref="Site"/> and
/// <see cref="Drive"/>.</summary>
public sealed class GraphOptions
{
    /// <summary>A SharePoint site: its web URL (https://contoso.sharepoint.com/sites/Team), "host:/sites/Team", or its Graph id.</summary>
    public string? Site { get; init; }
    /// <summary>One drive: a drive id, or "me" for the signed-in user's OneDrive.</summary>
    public string? Drive { get; init; }
    /// <summary>Your name for the mount in snapshots (default: <see cref="Site"/> or <see cref="Drive"/>); never a secret.</summary>
    public string? Id { get; init; }
    /// <summary>Sends the requests (tests pass a fake). Default: the network, never following redirects and refusing internal
    /// addresses, like the shell's web access. A handler that follows redirects is refused.</summary>
    public HttpMessageHandler? Transport { get; init; }
    /// <summary>The time limit of one request.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>The Graph endpoint; must be https.</summary>
    public string ApiUrl { get; init; } = GraphSource.Graph;
    /// <summary>Requests a second at most (null: no spacing).</summary>
    public double? Rate { get; init; }
}

/// <summary>
/// A OneDrive or SharePoint document library, or a SharePoint site with its libraries, lists and subsites, as a mount source
/// (docs/connectors/graph.md). <c>token</c> returns a current access token for Microsoft Graph and is asked before every
/// request; signing in is the host's job.
/// <code>
/// await shell.MountAsync("/mnt/sp", new GraphSource(GetTokenAsync, new() { Site = "https://contoso.sharepoint.com/sites/Team" }), MountMode.Staged);
/// </code>
/// </summary>
public sealed partial class GraphSource : IMountSource
{
    public const string Graph = "https://graph.microsoft.com/v1.0";
    internal const int SimpleUpload = 4 * 1024 * 1024; // bytes a single PUT may send; more goes through an upload session
    internal const int Chunk = 10 * 320 * 1024; // an upload session's chunk: a multiple of 320 KiB
    internal const int Page = 200; // entries asked for per page
    internal const int MaxListRequests = 50; // pages one listing may take
    internal static int MaxSiteDepth = 8; // subsites below the mounted site that are shown (tests lower it)
    internal const int MaxPath = 400; // characters in a decoded SharePoint path, the library's own path included
    internal const long MaxCopyBytes = 250_000_000; // what a move between libraries may copy
    internal const int MaxCopyEntries = 1000;
    internal const int MaxItemBytes = 1_000_000; // a list item file
    internal const int MaxVersion = 1024;
    const string Select = "id,name,size,eTag,cTag,lastModifiedDateTime,folder,file";

    [GeneratedRegex("^[A-Za-z0-9!_.,:-]{1,400}$")]
    private static partial Regex IdPattern();
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]+)?)?(Z|[+-][0-9]{2}:[0-9]{2})?)?$")]
    private static partial Regex DatePattern();

    const string Illegal = "\"*:<>?/\\|";
    static readonly HashSet<string> Reserved = ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(0, 10).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" }), ".LOCK", "DESKTOP.INI"];
    // columns every list has that say nothing about the item, or that the file name already shows (Title)
    static readonly HashSet<string> Skip = ["Title", "ContentType", "Attachments", "Edit", "LinkTitle", "LinkTitleNoMenu", "DocIcon", "ItemChildCount",
        "FolderChildCount", "AppAuthor", "AppEditor", "ComplianceAssetId"];
    static readonly HashSet<string> SetBySharePoint = ["ID", "Created", "Modified", "Author", "Editor"];
    static readonly string[] Types = ["text", "choice", "number", "currency", "boolean", "dateTime", "personOrGroup", "lookup", "calculated",
        "hyperlinkOrPicture", "term", "geolocation", "thumbnail", "contentApprovalStatus"];
    static readonly HashSet<string> EditableTypes = ["text", "choice", "number", "currency", "boolean", "dateTime"];

    // What the agent sees (the framework puts "mount: <machine path>: " before an apply failure).
    internal static readonly Dictionary<string, string> Messages = new()
    {
        ["token"] = "Microsoft Graph refused the token (HTTP 401)",
        ["denied"] = "{path}: access denied (the app or the signed-in user lacks permission)",
        ["missing"] = "{path}: No such file or directory",
        ["goneSinceListed"] = "{path}: No such file or directory (deleted in SharePoint since it was listed)",
        ["changed"] = "{path}: changed in SharePoint since it was read",
        ["changedCode"] = "{path}: changed in SharePoint since it was read ({code})",
        ["deleted"] = "{path}: deleted in SharePoint since it was read",
        ["exists"] = "{path}: already exists",
        ["locked"] = "{path}: locked in SharePoint (checked out or open for editing)",
        ["full"] = "{path}: the library is out of storage",
        ["refused"] = "{path}: SharePoint refused the change: {reason}",
        ["cantOpen"] = "the {what} can't be opened: {reason}",
        ["nextPage"] = "Microsoft Graph sent a next page on another host; not followed",
        ["notJson"] = "Microsoft Graph sent a response that isn't a JSON object (HTTP {status})",
        ["badId"] = "Microsoft Graph sent an id that can't be used",
        ["badName"] = "{path}: SharePoint doesn't allow this name (no \" * : < > ? \\ |, leading or trailing spaces, trailing dot, "
                      + "reserved names such as CON or .lock, _vti_, or ~$ at the start)",
        ["tooLong"] = "{path}: the path is too long for SharePoint (over {max} characters with the library's own path)",
        ["notDir"] = "{path}: Not a directory",
        ["isDir"] = "{path}: Is a directory",
        ["tooLarge"] = "{path}: File too large",
        ["downloadNotHttps"] = "{path}: the download link isn't https",
        ["downloadFailed"] = "{path}: the download failed (HTTP {status})",
        ["noUploadLink"] = "{path}: Microsoft Graph sent no usable upload link",
        ["uploadEnded"] = "{path}: the upload ended without the file being saved",
        ["filesInLibrary"] = "{path}: a site holds libraries, lists and subsites; put files in a library",
        ["noCreateContainer"] = "{path}: new libraries, lists and sites aren't created from here; create them in SharePoint",
        ["noRenameContainer"] = "{path}: libraries, lists and sites aren't renamed or moved from here; do it in SharePoint",
        ["noDeleteContainer"] = "{path}: libraries, lists and sites aren't deleted from here; do it in SharePoint",
        ["notEmpty"] = "{path}: the folder isn't empty in SharePoint (someone added to it); not deleted",
        ["noTargetFolder"] = "{path}: the folder it moves to doesn't exist",
        ["fileToLibrary"] = "{path}: files and folders can only move to a library",
        ["itemToList"] = "{path}: a list item can only move to a list",
        ["itemsAreMd"] = "{path}: a list holds items, one .md file each",
        ["noFolderInList"] = "{path}: a list holds items, not folders",
        ["itemTooLarge"] = "{path}: too large for a list item (over {max} bytes)",
        ["itemUtf8"] = "{path}: a list item is UTF-8 text",
        ["itemText"] = "{path}: a list item holds only its columns, as front matter between --- lines",
        ["noColumn"] = "{path}: no column {name} in this list",
        ["readOnlyColumn"] = "{path}: {name} can't be changed ({why})",
        ["notEditable"] = "{path}: {name} can't be changed here",
        ["expected"] = "{path}: {name}: expected {what}",
        ["copyTooMany"] = "{path}: too many entries to move between libraries (over {max}); move it in SharePoint",
        ["copyTooLarge"] = "{path}: too large to move between libraries (over {max} bytes); move it in SharePoint",
        ["copiedNotDeleted"] = "{path}: copied to {to}, but the original could not be deleted: {reason}",
        // not an error: what Note(change) tells the host about a move between libraries, lists or sites
        ["crossMove"] = "copy + delete: {path} moves to another library, list or site; the copy gets a new id and loses version "
                        + "history and sharing",
    };
    // the {why} of "readOnlyColumn"
    internal static readonly Dictionary<string, string> ReadOnly = new() { ["sharepoint"] = "set by SharePoint", ["type"] = "a {type} column, not editable here" };
    // the {what} of "expected" for each column type
    internal static readonly Dictionary<string, string> Expected = new()
    {
        ["text"] = "text",
        ["maxLength"] = "at most {max} characters",
        ["choiceName"] = "a choice's name",
        ["choice"] = "one of {choices}",
        ["number"] = "a number",
        ["boolean"] = "true or false",
        ["dateTime"] = "a date (\"2026-12-10\") or a date and time (\"2026-12-10T09:00:00Z\")",
    };

    static string M(string key, params (string, object)[] values) => Fill(Messages[key], values);

    /// <summary>A path that names nothing.</summary>
    sealed class Missing(string message) : MountSourceException(message);

    /// <summary>A name at a site's level: a library (drive), a list or a subsite.</summary>
    sealed record Container(string Kind, string Id, string Prefix = "");

    sealed record SiteNames(List<KeyValuePair<string, Container>> Order, Dictionary<string, Container> ByName);

    /// <summary>What a path names. Kind: site, drive (a path in a library), list (the list's folder), item (a list item file) or
    /// new-in-site. Top: the mount path of the library, list or site it is in, which tells whether a move crosses one.</summary>
    sealed record Loc(string Kind, string Top, string Site = "", string Drive = "", string List = "", string Rel = "/", string Prefix = "");

    /// <summary>Key: the front matter's name (the display name; the internal name when two share one). Name: the key in an
    /// item's fields. ReadOnly: why it can't be changed here, "" when it can.</summary>
    sealed record Column(string Key, string Name, string Type, string ReadOnly, string[] Choices, bool FreeChoice, long? MaxLength);

    sealed class ListData
    {
        public volatile List<Column> Columns = [];
        public volatile Dictionary<string, string> Names = new(StringComparer.Ordinal); // file name -> item id, as last listed; replaced, never changed

        public void Change(Action<Dictionary<string, string>> change)
        {
            lock (this) { var n = new Dictionary<string, string>(Names, StringComparer.Ordinal); change(n); Names = n; }
        }
    }

    readonly string? _siteRef, _driveRef;
    readonly string _apiUrl;
    Loc? _root;
    readonly ConcurrentDictionary<string, SiteNames> _containers = new(StringComparer.Ordinal); // a site's mount path -> its names
    readonly ConcurrentDictionary<string, ListData> _lists = new(StringComparer.Ordinal); // list id -> columns and item names

    internal ConnectorApi Api { get; }

    public GraphSource(Func<CancellationToken, ValueTask<string>> token, GraphOptions options)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(options);
        if ((options.Site is null) == (options.Drive is null)) throw new ArgumentException("give one of Site and Drive", nameof(options));
        if (!options.ApiUrl.StartsWith("https://", StringComparison.Ordinal) || !Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out _))
            throw new ArgumentException("ApiUrl: an https URL", nameof(options));
        _siteRef = options.Site;
        _driveRef = options.Drive;
        Id = options.Id ?? options.Site ?? options.Drive ?? "";
        _apiUrl = options.ApiUrl.TrimEnd('/');
        Api = new ConnectorApi("Microsoft Graph", token, options.Transport, options.Timeout, options.Rate);
    }

    public string Type => "graph";
    public string Id { get; }
    public MountCapabilities Capabilities => MountCapabilities.Write | MountCapabilities.Delete | MountCapabilities.Rename | MountCapabilities.Mkdir;

    // ----- requests -----

    static Headers H(params (string, string)[] hs) => [.. hs.Select(h => KeyValuePair.Create(h.Item1, h.Item2))];
    static Headers? IfMatch(string? version) => string.IsNullOrEmpty(version) ? null : H(("If-Match", version));

    Task<JsonObject> CallAsync(string method, string url, JsonObject? body, Headers? headers, CancellationToken ct) =>
        Api.JsonAsync(method, url.StartsWith("https://", StringComparison.Ordinal) ? url : _apiUrl + url, body, headers, ct);

    /// <summary>A GET whose 404 is null.</summary>
    async Task<JsonObject?> GetAsync(string url, CancellationToken ct)
    {
        try { return await CallAsync("GET", url, null, null, ct); }
        catch (ApiError e) when (e.Status == 404) { return null; }
    }

    /// <summary>The `value` of every page, following @odata.nextLink only back to the same API, up to <paramref name="max"/>.</summary>
    async Task<List<JsonObject>> PagesAsync(string url, int max, CancellationToken ct)
    {
        var output = new List<JsonObject>();
        url = $"{_apiUrl}{url}{(url.Contains('?') ? '&' : '?')}$top={Math.Max(1, Math.Min(Page, max))}";
        for (int i = 0; i < MaxListRequests; i++)
        {
            var got = await CallAsync("GET", url, null, null, ct);
            if (got["value"] is JsonArray rows) output.AddRange(rows.OfType<JsonObject>());
            var next = Str(got["@odata.nextLink"]);
            if (output.Count >= max || next is null || next == url) break;
            if (!next.StartsWith(_apiUrl + "/", StringComparison.Ordinal)) throw new MountSourceException(M("nextPage"));
            url = next;
        }
        return output.Count > max ? output[..max] : output;
    }

    static Exception Refusal(string path, ApiError e) => e.Status switch
    {
        401 => new MountSourceException(M("token")),
        403 => new MountSourceException(M("denied", ("path", path))),
        404 => new Missing(M("missing", ("path", path))),
        409 or 412 => new MountConflictException(M("changedCode", ("path", path), ("code", e.Code.Length > 0 ? e.Code : e.Status.ToString(CultureInfo.InvariantCulture)))),
        423 => new MountSourceException(M("locked", ("path", path))),
        507 => new MountSourceException(M("full", ("path", path))),
        _ => new MountSourceException(M("refused", ("path", path), ("reason", e.Text.Length > 0 ? e.Text : e.Code.Length > 0 ? e.Code : e.Status.ToString(CultureInfo.InvariantCulture)))),
    };

    /// <summary>Runs an operation; an error answer that nothing more specific caught becomes the agent's message for the path.</summary>
    static async Task<T> Guard<T>(string path, Func<Task<T>> run)
    {
        try { return await run(); }
        catch (ApiError e) { throw Refusal(path, e); }
    }

    // ----- where a path leads -----

    async Task<Loc> StartAsync(CancellationToken ct)
    {
        if (_root is { } r) return r;
        try
        {
            if (_driveRef is not null)
            {
                var d = await CallAsync("GET", (_driveRef == "me" ? "/me/drive" : $"/drives/{SafeId(_driveRef)}") + "?$select=id,webUrl", null, null, ct);
                _root = new Loc("drive", "/", Drive: SafeId(Str(d["id"])), Prefix: WebPath(Str(d["webUrl"])));
            }
            else
            {
                var s = await CallAsync("GET", SiteUrl(_siteRef!) + "?$select=id,webUrl", null, null, ct);
                _root = new Loc("site", "/", Site: SafeId(Str(s["id"])));
            }
        }
        catch (ApiError e)
        {
            throw new MountSourceException(M("cantOpen", ("what", _driveRef is not null ? "drive" : "site"), ("reason", Refusal("/", e).Message)));
        }
        return _root;
    }

    static string Sortable(JsonNode? n) => Str(n) ?? n?.ToJsonString() ?? "None";

    /// <summary>A site's libraries, lists and subsites by name (libraries keep their name; on a clash a list gets " (list)" and a
    /// subsite " (site)"; what is still taken gets " (2)").</summary>
    async Task<SiteNames> SiteNamesAsync(string sitePath, string siteId, int depth, bool fresh, CancellationToken ct)
    {
        if (!fresh && _containers.TryGetValue(sitePath, out var known)) return known;
        var drives = await PagesAsync($"/sites/{siteId}/drives?$select=id,name,webUrl", 2000, ct);
        var lists = await PagesAsync($"/sites/{siteId}/lists?$select=id,displayName,list", 2000, ct);
        var subs = depth < MaxSiteDepth ? await PagesAsync($"/sites/{siteId}/sites?$select=id,displayName,name", 2000, ct) : [];
        var found = new List<(string Name, Container C)>();
        foreach (var d in drives.OrderBy(d => Sortable(d["name"]), StringComparer.Ordinal))
            if (Str(d["id"]) is { } id && OkId(id))
                found.Add((SafeName(Str(d["name"]), "Library"), new Container("drive", id, WebPath(Str(d["webUrl"])))));
        var taken = found.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (kind, rows, tag) in new[] { ("list", lists, " (list)"), ("site", subs, " (site)") })
            foreach (var row in rows.OrderBy(x => Sortable(x["displayName"]), StringComparer.Ordinal))
            {
                if (Str(row["id"]) is not { } id || !OkId(id)) continue;
                if (kind == "list")
                {
                    if (row["list"] is not JsonObject info) continue;
                    var template = (Str(info["template"]) ?? "").ToLowerInvariant();
                    if (Truthy(info["hidden"]) || template.Contains("library") || template.Contains("catalog")) continue; // libraries are drives; hidden and system lists are left out
                }
                var title = Str(row["displayName"]);
                var n = SafeName(string.IsNullOrEmpty(title) ? Str(row["name"]) : title, kind == "list" ? "List" : "Site");
                found.Add((taken.Contains(n) ? n + tag : n, new Container(kind, id)));
                taken.Add(n);
            }
        var names = Unique(found.Select(f => f.Name));
        var order = names.Zip(found, (n, f) => KeyValuePair.Create(n, f.C)).ToList();
        var result = new SiteNames(order, order.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
        _containers[sitePath] = result;
        return result;
    }

    static bool Truthy(JsonNode? n) => Kind(n) switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => Str(n)!.Length > 0,
        JsonValueKind.Number => Num(n!) != 0,
        JsonValueKind.Array or JsonValueKind.Object => true,
        _ => false,
    };

    async Task<Loc> WhereAsync(string path, CancellationToken ct)
    {
        var root = await StartAsync(ct);
        if (root.Kind == "drive") return root with { Rel = path };
        var parts = path == "/" ? [] : path.Split('/')[1..];
        string sitePath = "/", site = root.Site;
        int depth = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var names = await SiteNamesAsync(sitePath, site, depth, false, ct);
            if (!names.ByName.TryGetValue(parts[i], out var c))
            {
                names = await SiteNamesAsync(sitePath, site, depth, true, ct);
                if (!names.ByName.TryGetValue(parts[i], out c)) throw new Missing(M("missing", ("path", path)));
            }
            var here = "/" + string.Join('/', parts[..(i + 1)]);
            if (c.Kind == "site")
            {
                (sitePath, site, depth) = (here, c.Id, depth + 1);
                continue;
            }
            var rest = parts[(i + 1)..];
            if (c.Kind == "drive") return new Loc("drive", here, Site: site, Drive: c.Id, Rel: "/" + string.Join('/', rest), Prefix: c.Prefix);
            if (rest.Length > 1) throw new Missing(M("missing", ("path", path)));
            return new Loc(rest.Length > 0 ? "item" : "list", here, Site: site, List: c.Id, Rel: rest.Length > 0 ? rest[0] : "/");
        }
        return new Loc("site", sitePath, Site: site);
    }

    /// <summary>The library, list or site a path is in, from what was listed (no request); null when not known.</summary>
    string? TopOf(string path)
    {
        if (_root is null || _root.Kind == "drive") return "/";
        var parts = path.Split('/')[1..];
        var sitePath = "/";
        for (int i = 0; i < parts.Length; i++)
        {
            if (!_containers.TryGetValue(sitePath, out var names) || !names.ByName.TryGetValue(parts[i], out var c)) return null;
            var here = "/" + string.Join('/', parts[..(i + 1)]);
            if (c.Kind != "site") return here;
            sitePath = here;
        }
        return sitePath;
    }

    /// <summary>What the host should know about a change before applying it: a rename between two libraries, lists or sites is a
    /// copy and a delete (the copy gets a new id, without the original's version history, sharing or metadata). Null otherwise.
    /// Needs only what was listed, never a request.</summary>
    public string? Note(MountChange change)
    {
        if (change.Kind != MountChangeKind.Rename || change.To is null) return null;
        var (a, b) = (TopOf(change.Path), TopOf(change.To));
        return a is null || b is null || a == b ? null : M("crossMove", ("path", change.Path));
    }

    // ----- drive requests -----

    /// <summary>The URL of a path in a drive, addressed by path: /drives/D/root or /drives/D/root:/a/b:, plus a tail.</summary>
    static string Dp(Loc loc, string? rel = null, string tail = "")
    {
        rel ??= loc.Rel;
        return rel == "/" ? $"/drives/{loc.Drive}/root{tail}" : $"/drives/{loc.Drive}/root:{Quote(rel)}:{tail}";
    }

    async Task<JsonObject?> ItemAsync(Loc loc, string path, CancellationToken ct, string? rel = null)
    {
        JsonObject? got;
        try { got = await GetAsync(Dp(loc, rel) + $"?$select={Select}", ct); }
        catch (ApiError e) { throw Refusal(path, e); }
        rel ??= loc.Rel;
        if (got is null || (rel != "/" && Str(got["name"]) != rel[(rel.LastIndexOf('/') + 1)..])) return null; // SharePoint matches names without case; another case is another name here
        return got;
    }

    static void CheckName(string path, Loc loc, string rel)
    {
        var name = rel[(rel.LastIndexOf('/') + 1)..];
        if (name.Any(c => Illegal.Contains(c)) || name != name.Trim(' ') || name.EndsWith('.') || Reserved.Contains(name.ToUpperInvariant())
            || name.Contains("_vti_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~$", StringComparison.Ordinal)
            || (rel.Count(c => c == '/') == 1 && name.Equals("forms", StringComparison.OrdinalIgnoreCase)))
            throw new MountSourceException(M("badName", ("path", path)));
        if (loc.Prefix.Length + rel.Length > MaxPath) throw new MountSourceException(M("tooLong", ("path", path), ("max", MaxPath)));
    }

    // ----- list requests -----

    async Task<ListData> ColumnsAsync(Loc loc, bool fresh, CancellationToken ct)
    {
        var data = _lists.GetOrAdd(loc.List, _ => new ListData());
        if (data.Columns.Count > 0 && !fresh) return data;
        var rows = await PagesAsync($"/sites/{loc.Site}/lists/{loc.List}/columns", 2000, ct);
        var cols = new List<Column>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in rows)
        {
            if (Str(c["name"]) is not { } name || Truthy(c["hidden"]) || Skip.Contains(name) || name.StartsWith('_')) continue;
            var t = Types.FirstOrDefault(x => c[x] is JsonObject) ?? "other";
            var display = Str(c["displayName"]);
            var key = SafeName(string.IsNullOrEmpty(display) ? name : display, name);
            if (keys.Contains(key)) key = name;
            keys.Add(key);
            var why = SetBySharePoint.Contains(name) || Truthy(c["readOnly"]) ? ReadOnly["sharepoint"] : EditableTypes.Contains(t) ? "" : Fill(ReadOnly["type"], ("type", t));
            var ch = c["choice"] as JsonObject;
            var choices = ch?["choices"] is JsonArray a ? a.Select(Str).OfType<string>().ToArray() : [];
            long? maxLength = t == "text" ? Int(c["text"]?["maxLength"]) : null;
            cols.Add(new Column(key, name, t, why, choices, Truthy(ch?["allowTextEntry"]), maxLength));
        }
        data.Columns = cols;
        return data;
    }

    static (int, string) NumericKey(string id)
    {
        if (id.Length == 0 || !id.All(char.IsAsciiDigit)) return (0, "");
        var t = id.TrimStart('0');
        return (t.Length, t);
    }

    async Task<List<(string Name, JsonObject Item)>> ListItemsAsync(Loc loc, int max, CancellationToken ct)
    {
        var data = await ColumnsAsync(loc, true, ct);
        var items = (await PagesAsync($"/sites/{loc.Site}/lists/{loc.List}/items?expand=fields", max, ct))
            .Where(it => Str(it["id"]) is { } id && OkId(id) && it["fields"] is JsonObject && Str((it["contentType"] as JsonObject)?["name"]) != "Folder")
            .OrderBy(it => NumericKey(Str(it["id"])!)).ToList();
        var names = Unique(items.Select(it => SafeName(Str(it["fields"]!["Title"]), $"Item {Str(it["id"])}") + ".md"));
        data.Change(n => { n.Clear(); foreach (var (name, it) in names.Zip(items)) n[name] = Str(it["id"])!; });
        return [.. names.Zip(items)];
    }

    async Task<JsonObject?> ItemOfAsync(Loc loc, string path, CancellationToken ct)
    {
        if (!_lists.TryGetValue(loc.List, out var data) || !data.Names.ContainsKey(loc.Rel))
        {
            await ListItemsAsync(loc, 5000, ct);
            data = _lists[loc.List];
        }
        if (!data.Names.TryGetValue(loc.Rel, out var iid)) return null;
        try { return await GetAsync($"/sites/{loc.Site}/lists/{loc.List}/items/{iid}?expand=fields", ct); }
        catch (ApiError e) { throw Refusal(path, e); }
    }

    static byte[] Render(ListData data, JsonObject item)
    {
        var f = item["fields"] as JsonObject ?? [];
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var c in data.Columns)
        {
            JsonNode? v;
            if (c.Name is "Author" or "Editor")
                v = Copy(((item[c.Name == "Author" ? "createdBy" : "lastModifiedBy"] as JsonObject)?["user"] as JsonObject)?["displayName"]);
            else if (c.Name == "ID") // not among an item's fields: its id
                v = Str(item["id"]) is { } id && id.Length is > 0 and < 19 && id.All(char.IsAsciiDigit) ? JsonValue.Create(long.Parse(id, CultureInfo.InvariantCulture)) : null;
            else if (c.Type is "personOrGroup" or "lookup")
                v = Copy(f.TryGetPropertyValue(c.Name + "LookupId", out var lk) ? lk : f[c.Name]);
            else v = Copy(f[c.Name]);
            values[c.Key] = v is JsonObject ? null : v;
        }
        var ro = data.Columns.Where(c => c.ReadOnly.Length > 0).Select(c => c.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return Encoding.UTF8.GetBytes(FrontMatter(values, ro.Count > 0 ? "read-only: " + string.Join(", ", ro) : null));
    }

    /// <summary>The fields to send for an item file: those whose value differs from the current file's.</summary>
    static JsonObject Fields(string path, ListData data, string text, byte[]? current)
    {
        var (fm, body) = SplitFrontMatter(path, text);
        if (body.Trim().Length > 0) throw new MountSourceException(M("itemText", ("path", path)));
        var now = current is null ? null : SplitFrontMatter(path, Encoding.UTF8.GetString(current)).Values;
        var byKey = data.Columns.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var output = new JsonObject();
        foreach (var (key, v) in fm ?? [])
        {
            if (!byKey.TryGetValue(key, out var c)) throw new MountSourceException(M("noColumn", ("path", path), ("name", key)));
            if ((now is not null && now.TryGetValue(key, out var was) && Same(was, v)) || (current is null && (c.ReadOnly.Length > 0 || v is null)))
                continue; // a new item: what SharePoint sets is left to it
            if (c.ReadOnly.Length > 0) throw new MountSourceException(M("readOnlyColumn", ("path", path), ("name", key), ("why", c.ReadOnly)));
            output[c.Name] = FieldValue(path, c, v);
            if (c.Type == "choice" && v is JsonArray) output[c.Name + "@odata.type"] = "Collection(Edm.String)";
        }
        return output;
    }

    static JsonNode? FieldValue(string path, Column c, JsonNode? v)
    {
        MountSourceException Bad(string what) => new(M("expected", ("path", path), ("name", c.Key), ("what", what)));
        switch (c.Type)
        {
            case "text":
                if (v is JsonArray or JsonObject || IsBool(v)) throw Bad(Expected["text"]);
                var s = v is null ? "" : Str(v) ?? v.ToJsonString();
                if (c.MaxLength is > 0 && s.Length > c.MaxLength) throw Bad(Fill(Expected["maxLength"], ("max", c.MaxLength)));
                return s;
            case "choice":
                foreach (var x in v is JsonArray arr ? [.. arr] : v is null ? [] : new JsonNode?[] { v })
                {
                    if (Str(x) is not { } name) throw Bad(Expected["choiceName"]);
                    if (!c.FreeChoice && !c.Choices.Contains(name)) throw Bad(Fill(Expected["choice"], ("choices", string.Join(", ", c.Choices))));
                }
                return Copy(v);
            case "number" or "currency":
                if (v is not null && !IsNumber(v)) throw Bad(Expected["number"]);
                return Copy(v);
            case "boolean":
                if (!IsBool(v)) throw Bad(Expected["boolean"]);
                return Copy(v);
            case "dateTime":
                if (v is null || Str(v) == "") return null;
                if (Str(v) is not { } d || !DatePattern().IsMatch(d)) throw Bad(Expected["dateTime"]);
                return d.Contains('T') ? d : d + "T00:00:00Z";
        }
        throw new MountSourceException(M("notEditable", ("path", path), ("name", c.Key)));
    }

    // ----- the source -----

    public Task<IReadOnlyList<MountEntry>> ListAsync(string path, int maxEntries, CancellationToken cancellationToken) => Guard(path, async () =>
    {
        var ct = cancellationToken;
        var loc = await WhereAsync(path, ct);
        if (loc.Kind == "site")
        {
            int depth = loc.Top == "/" ? 0 : loc.Top.Count(c => c == '/');
            var names = await SiteNamesAsync(loc.Top, loc.Site, depth, true, ct);
            return (IReadOnlyList<MountEntry>)[.. names.Order.Take(maxEntries).Select(kv => new MountEntry(kv.Key, true, 0, null, $"{kv.Value.Kind}:{kv.Value.Id}"))];
        }
        if (loc.Kind == "list")
        {
            var items = await ListItemsAsync(loc, maxEntries, ct);
            var data = _lists[loc.List];
            return [.. items.Select(x => new MountEntry(x.Name, false, Render(data, x.Item).Length, When(x.Item), Text(x.Item["eTag"])))];
        }
        if (loc.Kind == "item") throw new MountSourceException(M("notDir", ("path", path)));
        var rows = await PagesAsync(Dp(loc, tail: "/children") + $"?$select={Select}", maxEntries, ct);
        return [.. rows.Select(Entry).OfType<MountEntry>()];
    });

    public Task<MountEntry?> StatAsync(string path, CancellationToken cancellationToken) => Guard(path, async () =>
    {
        var ct = cancellationToken;
        Loc loc;
        try { loc = await WhereAsync(path, ct); }
        catch (Missing) { return null; }
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (loc.Kind is "site" or "list" || (loc.Kind == "drive" && loc.Rel == "/")) return new MountEntry(name, true, 0, null, "");
        if (loc.Kind == "item")
        {
            var it = await ItemOfAsync(loc, path, ct);
            return it is null ? null : new MountEntry(name, false, Render(_lists[loc.List], it).Length, When(it), Text(it["eTag"]));
        }
        var e = await ItemAsync(loc, path, ct) is { } item ? Entry(item) : null;
        return e is null ? null : e with { Name = name };
    });

    public Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken) => Guard(path, async () =>
    {
        var ct = cancellationToken;
        var loc = await WhereAsync(path, ct);
        if (loc.Kind == "item")
        {
            var it = await ItemOfAsync(loc, path, ct) ?? throw new Missing(M("missing", ("path", path)));
            return Render(_lists[loc.List], it);
        }
        if (loc.Kind != "drive" || loc.Rel == "/") throw new MountSourceException(M("isDir", ("path", path)));
        var (status, rh, raw) = await Api.SendAsync("GET", _apiUrl + Dp(loc, tail: "/content"), null, null, ct, limit: maxBytes);
        if (status is 301 or 302 or 303 or 307 or 308)
        {
            var where = ConnectorApi.Header(rh, "Location") ?? "";
            if (!Uri.TryCreate(where, UriKind.Absolute, out var u) || u.Scheme != "https") throw new MountSourceException(M("downloadNotHttps", ("path", path)));
            (status, _, raw) = await Api.SendAsync("GET", where, null, null, ct, auth: false, limit: maxBytes); // pre-signed: no token
        }
        if (status == 404) throw new Missing(M("goneSinceListed", ("path", path)));
        if (status >= 300) throw new MountSourceException(M("downloadFailed", ("path", path), ("status", status)));
        if (raw.Length > maxBytes) throw new MountSourceException(M("tooLarge", ("path", path)));
        return raw;
    });

    public Task<string> WriteAsync(string path, byte[] data, string? expectedVersion, CancellationToken cancellationToken) => Guard(path, async () =>
    {
        var ct = cancellationToken;
        var loc = await WhereNewAsync(path, ct);
        if (loc.Kind == "item") return await WriteItemAsync(loc, path, data, expectedVersion, ct);
        if (loc.Kind != "drive" || loc.Rel == "/") throw new MountSourceException(M("filesInLibrary", ("path", path)));
        CheckName(path, loc, loc.Rel);
        JsonObject got;
        try
        {
            if (data.Length <= SimpleUpload)
            {
                var url = Dp(loc, tail: "/content") + (string.IsNullOrEmpty(expectedVersion) ? "?@microsoft.graph.conflictBehavior=fail" : "");
                var hs = H(("Content-Type", "application/octet-stream"));
                hs.AddRange(IfMatch(expectedVersion) ?? []);
                var (status, _, raw) = await Api.SendAsync("PUT", _apiUrl + url, data, hs, ct);
                got = Api.Answer(status, raw);
            }
            else got = await UploadAsync(loc, path, data, expectedVersion, ct);
        }
        catch (ApiError e) { throw Refusal(path, e); }
        return Tag(got);
    });

    async Task<JsonObject> UploadAsync(Loc loc, string path, byte[] data, string? expected, CancellationToken ct)
    {
        var s = await CallAsync("POST", Dp(loc, tail: "/createUploadSession"),
            new JsonObject { ["item"] = new JsonObject { ["@microsoft.graph.conflictBehavior"] = string.IsNullOrEmpty(expected) ? "fail" : "replace" } }, IfMatch(expected), ct);
        var url = Str(s["uploadUrl"]);
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") throw new MountSourceException(M("noUploadLink", ("path", path)));
        try
        {
            for (int start = 0; start < data.Length; start += Chunk)
            {
                var part = data[start..Math.Min(data.Length, start + Chunk)];
                var range = $"bytes {start}-{start + part.Length - 1}/{data.Length}";
                var (status, _, raw) = await Api.SendAsync("PUT", url, part, H(("Content-Range", range)), ct, auth: false); // pre-signed
                if (status is 200 or 201) return Api.Answer(status, raw);
                if (status != 202) Api.Answer(status, raw);
            }
            throw new MountSourceException(M("uploadEnded", ("path", path)));
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // the call may be out of time; the session goes anyway
            try { await Api.SendAsync("DELETE", url, null, null, cleanup.Token, auth: false); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>Where a path that may not exist yet would go: its parent must exist.</summary>
    async Task<Loc> WhereNewAsync(string path, CancellationToken ct)
    {
        try { return await WhereAsync(path, ct); }
        catch (Missing)
        {
            int slash = path.LastIndexOf('/');
            var (parent, name) = (path[..slash], path[(slash + 1)..]);
            var loc = await WhereAsync(parent.Length == 0 ? "/" : parent, ct);
            return loc.Kind switch
            {
                "drive" => loc with { Rel = loc.Rel.TrimEnd('/') + "/" + name },
                "list" => new Loc("item", loc.Top, Site: loc.Site, List: loc.List, Rel: name),
                "site" => new Loc("new-in-site", loc.Top, Site: loc.Site, Rel: name),
                _ => throw new MountSourceException(M("notDir", ("path", path))),
            };
        }
    }

    async Task<string> WriteItemAsync(Loc loc, string path, byte[] data, string? expected, CancellationToken ct)
    {
        if (!loc.Rel.EndsWith(".md", StringComparison.Ordinal)) throw new MountSourceException(M("itemsAreMd", ("path", path)));
        if (data.Length > MaxItemBytes) throw new MountSourceException(M("itemTooLarge", ("path", path), ("max", MaxItemBytes)));
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { throw new MountSourceException(M("itemUtf8", ("path", path))); }
        var cols = await ColumnsAsync(loc, false, ct);
        var items = $"/sites/{loc.Site}/lists/{loc.List}/items";
        JsonObject got;
        try
        {
            if (expected is null)
            {
                var fields = Fields(path, cols, text, null);
                fields["Title"] = loc.Rel[..^3];
                got = await CallAsync("POST", items, new JsonObject { ["fields"] = fields }, null, ct);
                if (Str(got["id"]) is { } id && OkId(id)) cols.Change(n => n[loc.Rel] = id);
                return Text(got["eTag"]);
            }
            var it = await ItemOfAsync(loc, path, ct) ?? throw new MountConflictException(M("deleted", ("path", path)));
            var changed = Fields(path, cols, text, Render(cols, it));
            if (changed.Count == 0) return Text(it["eTag"]);
            got = await CallAsync("PATCH", $"{items}/{Str(it["id"])}/fields", changed, IfMatch(expected), ct);
        }
        catch (ApiError e) { throw Refusal(path, e); }
        var tag = Text(got["@odata.etag"]);
        return tag.Length > 0 ? tag : Text((await ItemOfAsync(loc, path, ct))?["eTag"]);
    }

    public Task DeleteAsync(string path, string? expectedVersion, CancellationToken cancellationToken) => Guard(path, async () =>
    {
        var ct = cancellationToken;
        var loc = await WhereAsync(path, ct);
        if (loc.Kind is "site" or "list" || (loc.Kind == "drive" && loc.Rel == "/")) throw new MountSourceException(M("noDeleteContainer", ("path", path)));
        if (loc.Kind == "item")
        {
            var it = await ItemOfAsync(loc, path, ct) ?? throw new MountConflictException(M("deleted", ("path", path)));
            await CallAsync("DELETE", $"/sites/{loc.Site}/lists/{loc.List}/items/{Str(it["id"])}", null, IfMatch(expectedVersion), ct);
            _lists[loc.List].Change(n => n.Remove(loc.Rel));
            return true;
        }
        var item = await ItemAsync(loc, path, ct) ?? throw new MountConflictException(M("deleted", ("path", path)));
        if (item.ContainsKey("folder"))
        {
            if ((await PagesAsync(Dp(loc, tail: "/children") + "?$select=id,name", 1, ct)).Count > 0) throw new MountSourceException(M("notEmpty", ("path", path)));
            await CallAsync("DELETE", Dp(loc), null, null, ct);
        }
        else await CallAsync("DELETE", Dp(loc), null, IfMatch(expectedVersion), ct);
        return true;
    });

    public Task<string?> MkdirAsync(string path, CancellationToken cancellationToken) => Guard(path, async () =>
    {
        var loc = await WhereNewAsync(path, cancellationToken);
        if (loc.Kind == "new-in-site") throw new MountSourceException(M("noCreateContainer", ("path", path)));
        if (loc.Kind is "item" or "list") throw new MountSourceException(M("noFolderInList", ("path", path)));
        if (loc.Kind != "drive" || loc.Rel == "/") throw new MountConflictException(M("exists", ("path", path)));
        CheckName(path, loc, loc.Rel);
        int slash = loc.Rel.LastIndexOf('/');
        var (parent, name) = (loc.Rel[..slash], loc.Rel[(slash + 1)..]);
        var got = await CallAsync("POST", Dp(loc, parent.Length == 0 ? "/" : parent, "/children"),
            new JsonObject { ["name"] = name, ["folder"] = new JsonObject(), ["@microsoft.graph.conflictBehavior"] = "fail" }, null, cancellationToken);
        return Text(got["eTag"]) is { Length: > 0 } v ? v : null;
    });

    public Task<string?> RenameAsync(string path, string to, string? expectedVersion, CancellationToken cancellationToken) => Guard(path, async () =>
    {
        var ct = cancellationToken;
        var src = await WhereAsync(path, ct);
        var dst = await WhereNewAsync(to, ct);
        if (src.Kind is "site" or "list" || (src.Kind == "drive" && src.Rel == "/")) throw new MountSourceException(M("noRenameContainer", ("path", path)));
        if (src.Kind == "item")
        {
            if (dst.Kind != "item") throw new MountSourceException(M("itemToList", ("path", to)));
            if (dst.List != src.List) return await CopyItemAsync(src, dst, path, to, expectedVersion, ct);
            if (!dst.Rel.EndsWith(".md", StringComparison.Ordinal)) throw new MountSourceException(M("itemsAreMd", ("path", to)));
            var it = await ItemOfAsync(src, path, ct) ?? throw new MountConflictException(M("deleted", ("path", path)));
            var id = Str(it["id"])!;
            var patched = await CallAsync("PATCH", $"/sites/{src.Site}/lists/{src.List}/items/{id}/fields", new JsonObject { ["Title"] = dst.Rel[..^3] }, IfMatch(expectedVersion), ct);
            _lists[src.List].Change(n => { n[dst.Rel] = n.Remove(src.Rel, out var was) ? was : id; });
            return Text(patched["@odata.etag"]) is { Length: > 0 } v ? v : null;
        }
        if (dst.Kind != "drive" || dst.Rel == "/") throw new MountSourceException(M("fileToLibrary", ("path", to)));
        CheckName(to, dst, dst.Rel);
        if (dst.Drive != src.Drive) return await CopyDriveAsync(src, dst, path, to, expectedVersion, ct);
        var item = await ItemAsync(src, path, ct) ?? throw new MountConflictException(M("deleted", ("path", path)));
        var body = new JsonObject { ["name"] = dst.Rel[(dst.Rel.LastIndexOf('/') + 1)..] };
        string Parent(string rel) => rel[..rel.LastIndexOf('/')] is { Length: > 0 } p ? p : "/";
        if (Parent(dst.Rel) != Parent(src.Rel))
        {
            var p = await ItemAsync(dst, to, ct, Parent(dst.Rel));
            if (p is null || !p.ContainsKey("folder") || Str(p["id"]) is not { } pid || !OkId(pid)) throw new MountSourceException(M("noTargetFolder", ("path", to)));
            body["parentReference"] = new JsonObject { ["id"] = pid };
        }
        var got = await CallAsync("PATCH", Dp(src), body, item.ContainsKey("folder") ? null : IfMatch(expectedVersion), ct);
        return Tag(got) is { Length: > 0 } version ? version : null;
    });

    // ----- moves between libraries, lists and sites: a copy and a delete -----

    async Task<string?> CopyDriveAsync(Loc src, Loc dst, string path, string to, string? expected, CancellationToken ct)
    {
        var it = await ItemAsync(src, path, ct) ?? throw new MountConflictException(M("deleted", ("path", path)));
        bool file = it.ContainsKey("file");
        if (file && !string.IsNullOrEmpty(expected) && Tag(it) != expected)
            throw new MountConflictException(M("changed", ("path", path)));
        var budget = new long[] { MaxCopyEntries, MaxCopyBytes };
        var version = await CopyTreeAsync(src, dst, path, to, it, budget, ct);
        try { await CallAsync("DELETE", Dp(src), null, file ? IfMatch(expected) : null, ct); }
        catch (ApiError e) { throw new MountSourceException(M("copiedNotDeleted", ("path", path), ("to", to), ("reason", Refusal(path, e).Message))); }
        return version;
    }

    async Task<string?> CopyTreeAsync(Loc src, Loc dst, string path, string to, JsonObject it, long[] budget, CancellationToken ct)
    {
        if (--budget[0] < 0) throw new MountSourceException(M("copyTooMany", ("path", path), ("max", MaxCopyEntries)));
        if (it.ContainsKey("folder"))
        {
            var version = await MkdirAsync(to, ct);
            foreach (var child in await PagesAsync(Dp(src, tail: "/children") + $"?$select={Select}", MaxCopyEntries, ct))
            {
                if (Str(child["name"]) is not { } name || Entry(child) is null) continue;
                var s = src with { Rel = src.Rel.TrimEnd('/') + "/" + name };
                var d = dst with { Rel = dst.Rel.TrimEnd('/') + "/" + name };
                CheckName(to + "/" + name, d, d.Rel);
                await CopyTreeAsync(s, d, path + "/" + name, to + "/" + name, child, budget, ct);
            }
            return version;
        }
        long size = Int(it["size"]) ?? MaxCopyBytes;
        budget[1] -= size;
        if (budget[1] < 0) throw new MountSourceException(M("copyTooLarge", ("path", path), ("max", MaxCopyBytes)));
        var data = await ReadAsync(path, Math.Max(size, 0) + 1, ct);
        return await WriteAsync(to, data, null, ct);
    }

    async Task<string?> CopyItemAsync(Loc src, Loc dst, string path, string to, string? expected, CancellationToken ct)
    {
        var it = await ItemOfAsync(src, path, ct) ?? throw new MountConflictException(M("deleted", ("path", path)));
        if (!string.IsNullOrEmpty(expected) && Text(it["eTag"]) != expected) throw new MountConflictException(M("changed", ("path", path)));
        var (fm, _) = SplitFrontMatter(path, Encoding.UTF8.GetString(Render(_lists[src.List], it)));
        var target = await ColumnsAsync(dst, false, ct);
        var keep = target.Columns.Where(c => c.ReadOnly.Length == 0).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var text = FrontMatter((fm ?? []).Where(kv => keep.Contains(kv.Key) && kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
        var version = await WriteItemAsync(dst, to, Encoding.UTF8.GetBytes(text), null, ct);
        try { await CallAsync("DELETE", $"/sites/{src.Site}/lists/{src.List}/items/{Str(it["id"])}", null, IfMatch(expected), ct); }
        catch (ApiError e) { throw new MountSourceException(M("copiedNotDeleted", ("path", path), ("to", to), ("reason", Refusal(path, e).Message))); }
        _lists[src.List].Change(n => n.Remove(src.Rel));
        return version;
    }

    // ----- helpers -----

    static MountEntry? Entry(JsonObject it)
    {
        if (Str(it["name"]) is not { } name) return null;
        if (it["folder"] is JsonObject) return new MountEntry(name, true, 0, When(it), Text(it["eTag"]));
        if (it["file"] is JsonObject)
        {
            long? size = Int(it["size"]) is >= 0 and var n ? n : null;
            return new MountEntry(name, false, size, When(it), Tag(it));
        }
        return null; // a OneNote notebook, a link or anything else that is neither a file nor a folder
    }

    /// <summary>A JSON integer, or null (a fraction, a string, anything else).</summary>
    static long? Int(JsonNode? n) => IsNumber(n) && long.TryParse(n!.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    static DateTime? When(JsonObject it) =>
        DateTimeOffset.TryParse(Str(it["lastModifiedDateTime"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.UtcDateTime : null;

    static string Text(JsonNode? v) => Text(Str(v));
    /// <summary>A file's version: its cTag (changes with the content only), else its eTag.</summary>
    static string Tag(JsonObject it) => Text(Str(it["cTag"]) is { Length: > 0 } c ? c : Str(it["eTag"]));
    static string Text(string? s) => s is null ? "" : s.Length > MaxVersion ? s[..MaxVersion] : s;

    static bool OkId(string? v) => v is not null && IdPattern().IsMatch(v);

    /// <summary>An id from a response or the host, checked before it goes into a URL's path.</summary>
    static string SafeId(string? v) => OkId(v) ? v! : throw new MountSourceException(M("badId"));

    static string WebPath(string? url) => url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) ? Uri.UnescapeDataString(u.AbsolutePath) : "";

    /// <summary>The Graph URL of a site from its web URL (https://host/sites/x), host:/sites/x, or its id.</summary>
    static string SiteUrl(string site)
    {
        if (site.StartsWith("https://", StringComparison.Ordinal))
        {
            var u = new Uri(site);
            var p = Uri.UnescapeDataString(u.AbsolutePath).TrimEnd('/');
            return $"/sites/{Quote(u.Host.ToLowerInvariant())}:{Quote(p.Length == 0 ? "/" : p)}";
        }
        int colon = site.IndexOf(':');
        if (colon >= 0 && site.Contains('/')) return $"/sites/{Quote(site[..colon])}:{Quote(site[(colon + 1)..])}";
        return $"/sites/{SafeId(site)}";
    }
}
