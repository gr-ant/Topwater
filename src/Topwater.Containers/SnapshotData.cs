using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Topwater.Containers;

/// <summary>One file or directory of a snapshot. <see cref="Data"/> is null for a directory.</summary>
internal sealed record FileItem(string Path, bool IsDirectory, int Mode, string Owner, DateTime Mtime, byte[]? Data = null, bool Binary = false, long? ReportedSize = null)
{
    public bool SameContent(FileItem other) =>
        IsDirectory == other.IsDirectory && Mode == other.Mode && Owner == other.Owner
        && (IsDirectory || Data.AsSpan().SequenceEqual(other.Data));
}

/// <summary>
/// A snapshot opened through its saved format (manifest.json + blobs, see ShellSnapshot.Save). This package reads and builds
/// snapshots only this way: the versioned format is the contract, so core needs no internals exposed, and everything this
/// package builds goes back through <see cref="ShellSnapshot.Load"/> and its checks for untrusted input.
/// </summary>
internal sealed class SnapshotData
{
    public required JsonObject Manifest { get; init; }
    /// <summary>By path, ordinal order (parents before children).</summary>
    public required SortedDictionary<string, FileItem> Files { get; init; }

    public string User => (string)Manifest["user"]!;
    public string Cwd => (string)Manifest["cwd"]!;

    public static SnapshotData Read(ShellSnapshot snapshot)
    {
        var ms = new MemoryStream();
        snapshot.Save(ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        JsonObject manifest;
        using (var s = zip.GetEntry("manifest.json")!.Open()) manifest = JsonNode.Parse(s)!.AsObject();
        var files = new SortedDictionary<string, FileItem>(StringComparer.Ordinal);
        var blobs = new Dictionary<string, byte[]>();
        foreach (var f in manifest["files"]!.AsArray())
        {
            var path = (string)f!["path"]!;
            bool dir = (string)f["type"]! == "dir";
            byte[]? data = null;
            if (!dir)
            {
                var blob = (string?)f["blob"];
                if (blob == null) data = [];
                else if (!blobs.TryGetValue(blob, out data))
                {
                    var raw = new MemoryStream();
                    using (var s = zip.GetEntry("blobs/" + blob)!.Open()) s.CopyTo(raw);
                    blobs[blob] = data = raw.ToArray();
                }
            }
            files[path] = new FileItem(path, dir, Convert.ToInt32((string)f["mode"]!, 8), (string)f["owner"]!,
                DateTime.Parse((string)f["mtime"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                data, (bool?)f["binary"] ?? false, (long?)f["reportedSize"]);
        }
        manifest.Remove("files");
        return new SnapshotData { Manifest = manifest, Files = files };
    }

    /// <summary>A fresh machine for <paramref name="user"/>: what a new Shell holds before the agent does anything.</summary>
    public static SnapshotData Default(string user, string hostname) =>
        Read(new Shell(new ShellOptions { User = user, Hostname = hostname }).Snapshot());

    /// <summary>Writes this as a saved snapshot and loads it back with <paramref name="limits"/>: the format checks and limits apply.</summary>
    public ShellSnapshot ToSnapshot(ShellOptions limits)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var written = new HashSet<string>(StringComparer.Ordinal);
            var files = new JsonArray();
            foreach (var f in Files.Values)
            {
                var entry = new JsonObject
                {
                    ["path"] = f.Path, ["type"] = f.IsDirectory ? "dir" : "file",
                    ["mode"] = Convert.ToString(f.Mode, 8).PadLeft(4, '0'), ["owner"] = f.Owner,
                    ["mtime"] = f.Mtime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
                };
                if (!f.IsDirectory)
                {
                    entry["size"] = f.Data!.Length;
                    if (f.Binary) entry["binary"] = true;
                    if (f.ReportedSize != null) entry["reportedSize"] = f.ReportedSize;
                    if (f.Data.Length > 0)
                    {
                        var blob = Convert.ToHexString(SHA256.HashData(f.Data)).ToLowerInvariant();
                        entry["blob"] = blob;
                        if (written.Add(blob))
                            using (var s = zip.CreateEntry("blobs/" + blob, CompressionLevel.Fastest).Open()) s.Write(f.Data);
                    }
                }
                files.Add(entry);
            }
            var manifest = (JsonObject)Manifest.DeepClone();
            manifest["files"] = files;
            using var m = zip.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
            using var w = new Utf8JsonWriter(m);
            manifest.WriteTo(w);
        }
        ms.Position = 0;
        return ShellSnapshot.Load(ms, limits);
    }

    // ---------- variables (the manifest's "variables" list) ----------

    public IEnumerable<JsonObject> Variables => Manifest["variables"]?.AsArray().Select(v => v!.AsObject()) ?? [];

    public JsonObject? Variable(string name) => Variables.FirstOrDefault(v => (string?)v["name"] == name);

    /// <summary>Sets an exported scalar variable, replacing whatever had the name.</summary>
    public void SetExported(string name, string value)
    {
        var list = Manifest["variables"] as JsonArray ?? (JsonArray)(Manifest["variables"] = new JsonArray());
        if (Variable(name) is { } old) list.Remove(old);
        list.Add(new JsonObject { ["name"] = name, ["value"] = value, ["exported"] = true });
    }

    /// <summary>Text of a file (UTF-8), or null when it isn't a file here.</summary>
    public string? Text(string path) => Files.TryGetValue(path, out var f) && !f.IsDirectory ? Encoding.UTF8.GetString(f.Data!) : null;

    /// <summary>Removes <paramref name="path"/> and everything below it.</summary>
    public void RemoveTree(string path)
    {
        foreach (var p in Files.Keys.Where(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal)).ToList()) Files.Remove(p);
    }

}

/// <summary>Accounts from an /etc/passwd file.</summary>
internal sealed record Account(string Name, int Uid, int Gid, string Gecos, string Home, string Shell)
{
    public static List<Account> ParsePasswd(string? text)
    {
        var list = new List<Account>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var p = line.Split(':');
            if (p.Length == 7 && p[0].Length > 0 && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
                && int.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out var gid))
                list.Add(new(p[0], uid, gid, p[4], p[5], p[6]));
        }
        return list;
    }

    /// <summary>name, gid from an /etc/group file.</summary>
    public static List<(string Name, int Gid)> ParseGroup(string? text)
    {
        var list = new List<(string, int)>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var p = line.Split(':');
            if (p.Length >= 3 && p[0].Length > 0 && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var gid)) list.Add((p[0], gid));
        }
        return list;
    }
}
