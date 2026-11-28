// What one look at the page found (docs/browser.md): Playwright's accessibility snapshot read into nodes, the interactive ones
// named as directories under elements/, and the overview tree.txt shows.
using System.Text;
using System.Text.Json;
using Topwater.Connectors;

namespace Topwater.Browser;

/// <summary>One line of the accessibility snapshot: its depth, role, accessible name, [attributes] and the text after the colon.</summary>
internal sealed record AriaNode(int Depth, string Role, string? Name, string Attrs, string? Value, int Parent);

/// <summary>An interactive element: its directory, and how to find it again (role, exact name, which of the same).</summary>
internal sealed class PageElement(string dir, string role, string name, int nth, int node)
{
    public string Dir { get; } = dir;
    public string Role { get; } = role;
    public string Name { get; } = name;
    public int Nth { get; } = nth;
    public int Node { get; } = node;
    public int? FileIndex { get; init; } // a file field: which input[type=file] it is (shown or not); it has upload/
    public bool File => FileIndex != null;
    public bool Password { get; set; }   // its value is never shown
}

internal sealed class PageTree
{
    // roles an agent acts on; each becomes a directory under elements/
    internal static readonly HashSet<string> Interactive = ["button", "link", "textbox", "searchbox", "checkbox", "radio", "switch", "combobox",
        "listbox", "option", "slider", "spinbutton", "tab", "menuitem", "menuitemcheckbox", "menuitemradio", "treeitem"];
    internal static readonly HashSet<string> HasValue = ["textbox", "searchbox", "combobox", "slider", "spinbutton", "listbox"];
    internal static readonly HashSet<string> HasOptions = ["combobox", "listbox"];
    internal static readonly HashSet<string> HasChecked = ["checkbox", "radio", "switch", "menuitemcheckbox", "menuitemradio"];
    internal const int MaxLines = 50_000;  // snapshot lines read
    internal const int MaxName = 1000;     // characters of a name kept
    internal const int SlugChars = 60;     // characters of a name in a directory name

    public static readonly PageTree Empty = new([], 0);

    public List<AriaNode> Nodes { get; }
    public List<PageElement> Elements { get; } = [];
    public Dictionary<string, PageElement> ByDir { get; } = new(StringComparer.Ordinal);
    public int Left { get; private set; } // interactive elements past the limit, not under elements/
    public List<string> Forms { get; init; } = [];

    public PageTree(List<AriaNode> nodes, int maxElements)
    {
        Nodes = nodes;
        var seen = new Dictionary<(string, string), int>();
        var found = new List<(string Base, string Role, string Name, int Nth, int Node)>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (!Interactive.Contains(n.Role) || n.Role == "option" && Ancestor(i, "combobox")) continue; // a select's options are its options file
            var name = n.Name ?? "";
            int nth = seen.GetValueOrDefault((n.Role, name));
            seen[(n.Role, name)] = nth + 1;
            if (found.Count >= maxElements) { Left++; continue; }
            var slug = Slug(name);
            found.Add((slug.Length == 0 ? n.Role : $"{n.Role}-{slug}", n.Role, name, nth, i));
        }
        var dirs = ConnectorCommon.Unique(found.Select(f => f.Base));
        for (int k = 0; k < found.Count; k++)
        {
            var e = new PageElement(dirs[k], found[k].Role, found[k].Name, found[k].Nth, found[k].Node);
            Elements.Add(e);
            ByDir[e.Dir] = e;
        }
    }

    /// <summary>Adds the file fields (their labels in page order) as file-&lt;label&gt;: pages often hide the real input behind a
    /// styled button, so they come from the page's inputs, not the accessibility snapshot.</summary>
    public void AddFiles(IReadOnlyList<string> labels)
    {
        var dirs = ConnectorCommon.Unique(labels.Select(l => Slug(l) is { Length: > 0 } s ? $"file-{s}" : "file"));
        for (int i = 0; i < labels.Count; i++)
        {
            var e = new PageElement(dirs[i], "file", labels[i], 0, -1) { FileIndex = i };
            Elements.Add(e);
            ByDir[e.Dir] = e;
        }
    }

    bool Ancestor(int i, string role)
    {
        for (int p = Nodes[i].Parent; p >= 0; p = Nodes[p].Parent) if (Nodes[p].Role == role) return true;
        return false;
    }

    /// <summary>The names of the option nodes below a node.</summary>
    public IEnumerable<string> OptionsOf(int node)
    {
        for (int i = node + 1; i < Nodes.Count && Nodes[i].Depth > Nodes[node].Depth; i++)
            if (Nodes[i].Role == "option") yield return Nodes[i].Name ?? "";
    }

    /// <summary>Lower-case letters and digits of a name, other runs as one "-", at most <see cref="SlugChars"/> characters.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var r in name.ToLowerInvariant().EnumerateRunes())
        {
            if (sb.Length >= SlugChars) break;
            if (Rune.IsLetterOrDigit(r)) sb.Append(r.ToString());
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>The overview: one line a node, indented by depth, each element followed by its directory; values of password
    /// fields hidden; cut at <paramref name="maxBytes"/>.</summary>
    public string Render(int maxBytes)
    {
        var at = Elements.ToDictionary(e => e.Node);
        var sb = new StringBuilder();
        int bytes = 0;
        for (int i = 0; i < Nodes.Count; i++)
        {
            var n = Nodes[i];
            if (n.Role.StartsWith('/')) continue; // properties such as /url: href shows them
            var line = new StringBuilder().Append(' ', n.Depth * 2).Append(n.Role);
            if (n.Name != null) line.Append(' ').Append(JsonSerializer.Serialize(n.Name, Json.Options));
            if (n.Attrs.Length > 0) line.Append(' ').Append(n.Attrs);
            at.TryGetValue(i, out var e);
            if (n.Value is { Length: > 0 } v) line.Append(": ").Append(e?.Password == true ? "********" : v);
            if (e != null) line.Append("  -> elements/").Append(e.Dir);
            var text = line.Append('\n').ToString();
            bytes += Encoding.UTF8.GetByteCount(text);
            if (bytes > maxBytes) { sb.Append("[the rest of the page is left out]\n"); break; }
            sb.Append(text);
        }
        if (Left > 0) sb.Append($"[{Left} more interactive elements are not under elements/]\n");
        return sb.ToString();
    }

    // ---------- reading the snapshot ----------

    /// <summary>Reads Playwright's YAML snapshot: "- role "name" [attrs]: text" lines, two spaces a level, a line quoted as a
    /// whole when it holds characters YAML would misread.</summary>
    public static List<AriaNode> Parse(string yaml)
    {
        var nodes = new List<AriaNode>();
        var stack = new List<int>(); // the last node at each depth
        foreach (var raw in yaml.Split('\n'))
        {
            if (nodes.Count >= MaxLines) break;
            int indent = 0;
            while (indent < raw.Length && raw[indent] == ' ') indent++;
            if (!raw.AsSpan(indent).StartsWith("- ")) continue;
            int depth = indent / 2;
            var rest = raw[(indent + 2)..].TrimEnd('\r');
            string key, tail;
            if (rest.StartsWith('\'') && Quoted(rest, '\'') is var (k1, t1)) (key, tail) = (k1, t1);
            else if (rest.StartsWith('"') && Quoted(rest, '"') is var (k2, t2)) (key, tail) = (k2, t2);
            else
            {
                int colon = rest.IndexOf(": ", StringComparison.Ordinal);
                if (colon < 0 && rest.EndsWith(':')) colon = rest.Length - 1;
                (key, tail) = colon < 0 ? (rest, "") : (rest[..colon], rest[colon..]);
            }
            var value = tail.StartsWith(": ") ? Unquote(tail[2..].Trim()) : null;
            var (role, name, attrs) = Key(key);
            if (role.Length == 0) continue;
            while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
            int parent = stack.Count > 0 ? stack[^1] : -1;
            if (stack.Count < depth) depth = stack.Count; // a jump in depth: under the last node
            nodes.Add(new AriaNode(depth, role, name is { Length: > MaxName } ? name[..MaxName] : name, attrs, value is { Length: > MaxName } ? value[..MaxName] : value, parent));
            stack.Add(nodes.Count - 1);
        }
        return nodes;
    }

    /// <summary>A YAML-quoted key at the start of <paramref name="s"/>, and what follows it; null when it doesn't close.</summary>
    static (string Key, string Tail)? Quoted(string s, char q)
    {
        var sb = new StringBuilder();
        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];
            if (q == '\'' && c == '\'')
            {
                if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i++; continue; }
                return (sb.ToString(), s[(i + 1)..]);
            }
            if (q == '"' && c == '\\' && i + 1 < s.Length) { sb.Append(c).Append(s[++i]); continue; }
            if (q == '"' && c == '"') return (Json.String("\"" + sb + "\"") ?? sb.ToString(), s[(i + 1)..]);
            sb.Append(c);
        }
        return null;
    }

    /// <summary>role, "name" (a JSON string), then [attributes].</summary>
    static (string Role, string? Name, string Attrs) Key(string key)
    {
        int sp = key.IndexOf(' ');
        var role = sp < 0 ? key : key[..sp];
        if (sp < 0) return (role, null, "");
        var rest = key[(sp + 1)..];
        string? name = null;
        if (rest.StartsWith('"'))
        {
            int i = 1;
            for (; i < rest.Length && rest[i] != '"'; i++) if (rest[i] == '\\') i++;
            name = Json.String(rest[..Math.Min(i + 1, rest.Length)]);
            rest = i + 1 < rest.Length ? rest[(i + 1)..].TrimStart() : "";
        }
        return (role, name, rest.StartsWith('[') ? rest : "");
    }

    static string Unquote(string v) =>
        v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? Json.String(v) ?? v
        : v.Length >= 2 && v[0] == '\'' && v[^1] == '\'' ? v[1..^1].Replace("''", "'") : v;

    internal static class Json
    {
        public static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public static string? String(string quoted)
        {
            try { return JsonSerializer.Deserialize<string>(quoted); }
            catch (JsonException) { return null; }
        }
    }
}
