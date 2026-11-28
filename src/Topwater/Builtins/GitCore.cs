using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Topwater;

/// <summary>Ends the git command: message (whole lines, already worded) on stderr and the exit code.</summary>
internal sealed class GitException(string message, int code = 128) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>git's on-disk formats and algorithms for the `git` command: loose objects, the v2 index, refs, config, ignore
/// rules, revisions, line diffs and three-way merges. Written from the publicly documented formats; reads and writes
/// repositories real git can use. Everything read from .git is untrusted: sizes, depths and walks are bounded, and no path
/// is followed through a symbolic link. A port of python/src/topwater/commands/gitcore.py.</summary>
internal static class GitCore
{
    public const int MaxWalk = 1_000_000;  // commits one history walk may visit
    public const int MaxDepth = 256;  // nested trees
    public const long DiffBudget = 1_000_000;  // Myers steps per file pair before falling back to "all removed, all added"
    public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    public static readonly Regex Hex = new(@"^[0-9a-f]{4,40}\z");
    public static readonly Regex Sha = new(@"^[0-9a-f]{40}\z");
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Msg(string key, params (string, string)[] values) => CommandSpec.Message("git", key, values);
    public static GitException Fatal(string key, params (string, string)[] values) => new(Msg(key, values), 128);

    public static string Sha1Hex(byte[] data) => Hex40(SHA1.HashData(data));

    static string Hex40(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    public static string ObjectId(string kind, byte[] data) => Sha1Hex(Header(kind, data));

    public static byte[] Header(string kind, byte[] data) => [.. Encoding.ASCII.GetBytes($"{kind} {data.Length}\0"), .. data];

    /// <summary>Byte order of the UTF-8 (surrogate-escaped) encoding, as Python's sort by .encode() orders.</summary>
    public static readonly Comparer<string> ByBytes = Comparer<string>.Create((a, b) =>
        ByteText.HasEscapes(a) || ByteText.HasEscapes(b) ? ByteText.Encode(a).AsSpan().SequenceCompareTo(ByteText.Encode(b)) : CodePoints(a, b));

    /// <summary>Code point order (Python's default string order).</summary>
    public static readonly Comparer<string> Ordinal = Comparer<string>.Create(CodePoints);

    static int CodePoints(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            int x = Point(a, ref i), y = Point(b, ref j);
            if (x != y) return x.CompareTo(y);
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    static int Point(string s, ref int i)
    {
        char c = s[i++];
        if (char.IsHighSurrogate(c) && i < s.Length && char.IsLowSurrogate(s[i])) return char.ConvertToUtf32(c, s[i++]);
        return c;
    }

    // ---------- identity and dates ----------

    static readonly string[] Months = "jan feb mar apr may jun jul aug sep oct nov dec".Split(' ');

    /// <summary>GIT_AUTHOR_DATE / --date: '@1700000000 +0100', '1700000000 +0100', ISO 8601 or RFC 2822 -> (epoch, '+hhmm').
    /// Null when the text is not a date.</summary>
    public static (long Epoch, string Zone)? ParseDate(string text, DateTime now)
    {
        var s = text.Trim();
        try
        {
            var m = Regex.Match(s, @"^@?([0-9]+)(?:\s+([+-][0-9]{4}))?\z");
            if (m.Success) return long.TryParse(m.Groups[1].Value, out var e) ? (e, m.Groups[2].Success ? m.Groups[2].Value : "+0000") : null;
            m = Regex.Match(s, @"^(?:\w{3},\s*)?([0-9]{1,2})\s+(\w{3})\s+([0-9]{4})\s+([0-9]{1,2}):([0-9]{2})(?::([0-9]{2}))?\s*([+-][0-9]{4})?\z");
            if (m.Success)
            {
                int mon = Array.IndexOf(Months, m.Groups[2].Value.ToLowerInvariant());
                if (mon < 0) return null;
                var d = new DateTime(I(m, 3), mon + 1, I(m, 1), I(m, 4), I(m, 5), m.Groups[6].Success ? I(m, 6) : 0);
                return WithZone(d, m.Groups[7].Success ? m.Groups[7].Value : "+0000");
            }
            m = Regex.Match(s, @"^([0-9]{4})-([0-9]{2})-([0-9]{2})(?:[T ]([0-9]{1,2}):([0-9]{2})(?::([0-9]{2}))?(?:\.[0-9]+)?)?\s*(Z|[+-][0-9]{2}:?[0-9]{2})?\z");
            if (m.Success)
            {
                var d = new DateTime(I(m, 1), I(m, 2), I(m, 3), m.Groups[4].Success ? I(m, 4) : 0, m.Groups[5].Success ? I(m, 5) : 0, m.Groups[6].Success ? I(m, 6) : 0);
                var z = m.Groups[7].Success ? m.Groups[7].Value : "+0000";
                return WithZone(d, z == "Z" ? "+0000" : z.Replace(":", ""));
            }
        }
        catch (ArgumentOutOfRangeException) { return null; }
        if (s == "now") return (Epoch(now), "+0000");
        return null;
    }

    static int I(Match m, int g) => int.Parse(m.Groups[g].Value, Inv);

    public static long Epoch(DateTime utc) => (long)Math.Floor((DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds);

    static (long, string) WithZone(DateTime local, string zone)
    {
        int off = (int.Parse(zone[1..3], Inv) * 60 + int.Parse(zone[3..5], Inv)) * (zone[0] == '-' ? -1 : 1);
        return (Epoch(local - TimeSpan.FromMinutes(off)), zone);
    }

    public static TimeSpan ZoneDelta(string zone)
    {
        if (zone.Length < 5 || !int.TryParse(zone[1..3], NumberStyles.None, Inv, out var h) || !int.TryParse(zone[3..5], NumberStyles.None, Inv, out var mm)) return TimeSpan.Zero;
        return TimeSpan.FromMinutes((h * 60 + mm) * (zone[0] == '-' ? -1 : 1));
    }

    public static DateTime LocalTime(long epoch, string zone) => DateTime.UnixEpoch.AddSeconds(epoch) + ZoneDelta(zone);

    public static string FormatDate(long epoch, string zone, string style, DateTime now)
    {
        if (style is not ("default" or "" or "short" or "iso" or "iso8601" or "iso-strict" or "iso8601-strict" or "rfc" or "rfc2822" or "raw" or "unix" or "relative"))
            throw new GitException(Msg("badDateFormat", ("format", style)));
        if (style == "raw") return $"{epoch} {zone}";
        if (style == "unix") return epoch.ToString(Inv);
        if (style == "relative") return RelativeDate(epoch, now);
        var t = LocalTime(epoch, zone);
        return style switch
        {
            "default" or "" => t.ToString("ddd MMM", Inv) + " " + t.Day + " " + t.ToString("HH:mm:ss yyyy", Inv) + " " + zone,
            "short" => t.ToString("yyyy-MM-dd", Inv),
            "iso" or "iso8601" => t.ToString("yyyy-MM-dd HH:mm:ss", Inv) + " " + zone,
            "iso-strict" or "iso8601-strict" => t.ToString("yyyy-MM-ddTHH:mm:ss", Inv) + zone[..Math.Min(3, zone.Length)] + ":" + zone[Math.Min(3, zone.Length)..],
            _ => t.ToString("ddd", Inv) + ", " + t.Day + " " + t.ToString("MMM yyyy HH:mm:ss", Inv) + " " + zone,
        };
    }

    public static string RelativeDate(long epoch, DateTime now)
    {
        long diff = Epoch(now) - epoch;
        if (diff < 0) return "in the future";
        foreach (var (limit, unit, size) in new (long, string, long)[] { (90, "second", 1), (90 * 60, "minute", 60), (36 * 3600, "hour", 3600),
                     (14 * 86400, "day", 86400), (70 * 86400, "week", 7 * 86400), (365 * 86400, "month", 30 * 86400) })
            if (diff < limit)
            {
                long n = (diff + size / 2) / size;
                return $"{n} {unit}{(n != 1 ? "s" : "")} ago";
            }
        long years = diff / (365 * 86400);
        return $"{years} year{(years != 1 ? "s" : "")} ago";
    }

    /// <summary>'Name &lt;email&gt; 1700000000 +0000' -> (name, email, epoch, zone).</summary>
    public static (string Name, string Email, long Epoch, string Zone) ParseIdent(string line)
    {
        var m = Regex.Match(line, @"^(.*?) ?<([^<>]*)> ?([0-9]+)? ?([+-][0-9]{4})?\z");
        if (!m.Success) return (line, "", 0, "+0000");
        return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success && long.TryParse(m.Groups[3].Value, out var e) ? e : 0, m.Groups[4].Success ? m.Groups[4].Value : "+0000");
    }

    // ---------- objects ----------

    /// <summary>Tree object -> [(mode, name, sha)]; names are checked so nothing can point outside the work tree.</summary>
    public static List<(string Mode, string Name, string Sha)> ParseTree(byte[] data, string where)
    {
        var output = new List<(string, string, string)>();
        int i = 0;
        while (i < data.Length)
        {
            int sp = Array.IndexOf(data, (byte)' ', i);
            int nul = sp >= 0 ? Array.IndexOf(data, (byte)0, sp + 1) : -1;
            if (sp < 0 || nul < 0 || nul + 21 > data.Length) throw new GitException(Msg("corruptObject", ("object", where)));
            var mode = Encoding.ASCII.GetString(data, i, sp - i);
            var name = ByteText.Decode(data[(sp + 1)..nul]);
            if (!Regex.IsMatch(mode, @"^[0-7]{5,6}\z")) throw new GitException(Msg("corruptObject", ("object", where)));
            CheckComponent(name);
            output.Add((mode == "40000" ? "040000" : mode, name, Hex40(data.AsSpan(nul + 1, 20))));
            i = nul + 21;
        }
        return output;
    }

    public static void CheckComponent(string name)
    {
        if (name is "" or "." or ".." || name.Contains('/') || name.Contains('\0') || name.ToLowerInvariant() == ".git")
            throw new GitException(Msg("invalidPath", ("path", name)));
    }

    public static void CheckPath(string path)
    {
        var parts = path.Split('/');
        if (parts.Length > MaxDepth) throw new GitException(Msg("pathTooDeep", ("path", (path.Length > 200 ? path[..200] : path) + "..."), ("limit", MaxDepth.ToString(Inv))));
        foreach (var part in parts) CheckComponent(part);
    }

    /// <summary>[(mode, name, sha)] -> tree object, in git's order (a directory sorts as if its name ended in '/').</summary>
    public static byte[] TreeBytes(IEnumerable<(string Mode, string Name, string Sha)> entries)
    {
        var keyed = entries.Select(e => (Key: e.Mode == "040000" ? [.. ByteText.Encode(e.Name), (byte)'/'] : ByteText.Encode(e.Name), E: e)).ToList();
        keyed.Sort((x, y) => x.Key.AsSpan().SequenceCompareTo(y.Key));
        var ms = new MemoryStream();
        foreach (var (_, (mode, name, sha)) in keyed)
        {
            ms.Write(Encoding.ASCII.GetBytes((mode == "040000" ? mode.TrimStart('0') : mode) + " "));
            ms.Write(ByteText.Encode(name));
            ms.WriteByte(0);
            ms.Write(Convert.FromHexString(sha));
        }
        return ms.ToArray();
    }

    // ---------- the index ----------

    public sealed class Entry(string path, string sha, string mode, int stage = 0, long mtime = 0, long size = 0)
    {
        public string Path = path, Sha = sha, Mode = mode;
        public int Stage = stage;
        public long Mtime = mtime, Size = size;
        public (string, string) Id => (Mode, Sha);
    }

    static uint U32(byte[] d, int i) => (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);

    static GitException BadIndex(string why, params (string, string)[] values) => new(Msg("badIndex", ("why", Msg(why, values))));

    public static List<Entry> ReadIndex(byte[] data, int limit)
    {
        if (data.Length < 32 || data[0] != 'D' || data[1] != 'I' || data[2] != 'R' || data[3] != 'C') throw BadIndex("indexBadSignature");
        uint version = U32(data, 4), count = U32(data, 8);
        if (version is not (2 or 3)) throw BadIndex("indexBadVersion", ("version", version.ToString(Inv)));
        if (Sha1Hex(data[..^20]) != Hex40(data.AsSpan(data.Length - 20, 20))) throw BadIndex("indexBadChecksum");
        if (count > limit) throw BadIndex("indexTooMany");
        var output = new List<Entry>();
        int i = 12, end = data.Length - 20;
        for (uint n = 0; n < count; n++)
        {
            if (i + 62 > end) throw BadIndex("indexTruncated");
            uint mtime = U32(data, i + 8), mode = U32(data, i + 24), size = U32(data, i + 36);
            var sha = Hex40(data.AsSpan(i + 40, 20));
            int flags = data[i + 60] << 8 | data[i + 61];
            int start = i + 62;
            if ((flags & 0x4000) != 0)
            {
                if (version < 3) throw BadIndex("indexExtendedFlag");
                start += 2;
            }
            int nul = start <= end ? Array.IndexOf(data, (byte)0, start, end - start) : -1;
            if (nul < 0) throw BadIndex("indexTruncated");
            var path = ByteText.Decode(data[start..nul]);
            CheckPath(path);
            output.Add(new Entry(path, sha, Convert.ToString(mode, 8).PadLeft(6, '0'), (flags >> 12) & 3, mtime, size));
            i += ((nul - i) / 8 + 1) * 8;  // entries are NUL-padded to a multiple of eight bytes
        }
        while (i + 8 <= end)  // extensions: optional ones (A-Z first) are skipped, others are not understood
        {
            if (data[i] is < 65 or > 90) throw BadIndex("indexBadExtension", ("name", Encoding.Latin1.GetString(data, i, 4)));
            i = (int)Math.Min(int.MaxValue, i + 8L + U32(data, i + 4));
        }
        return output;
    }

    static void Put32(MemoryStream ms, long v) { var x = (uint)v; ms.Write([(byte)(x >> 24), (byte)(x >> 16), (byte)(x >> 8), (byte)x]); }

    public static byte[] WriteIndex(IEnumerable<Entry> entries)
    {
        var list = entries.Select(e => (Key: ByteText.Encode(e.Path), E: e)).ToList();
        list.Sort((x, y) => x.Key.AsSpan().SequenceCompareTo(y.Key) is var c && c != 0 ? c : x.E.Stage.CompareTo(y.E.Stage));
        var ms = new MemoryStream();
        ms.Write("DIRC"u8);
        Put32(ms, 2);
        Put32(ms, list.Count);
        foreach (var (name, e) in list)
        {
            long start = ms.Length;
            foreach (var v in new long[] { e.Mtime, 0, e.Mtime, 0, 0, 0, Convert.ToInt32(e.Mode, 8), 0, 0, e.Size & 0xFFFFFFFF }) Put32(ms, v);
            ms.Write(Convert.FromHexString(e.Sha));
            int flags = (e.Stage << 12) | Math.Min(name.Length, 0xFFF);
            ms.Write([(byte)(flags >> 8), (byte)flags]);
            ms.Write(name);
            long pad = 8 - (ms.Length - start) % 8;
            for (long k = 0; k < pad; k++) ms.WriteByte(0);
        }
        var body = ms.ToArray();
        return [.. body, .. SHA1.HashData(body)];
    }

    // ---------- config ----------

    static readonly Regex SectionRx = new(@"^\[\s*([A-Za-z0-9.-]+)(?:\s+""((?:[^""\\]|\\.)*)"")?\s*\]");

    /// <summary>git config files: [section], [section "sub"], key = value; names are matched case-insensitively (not subsections).</summary>
    public sealed class Config
    {
        public List<string> Lines;

        public Config(string text = "")
        {
            Lines = [.. text.Split('\n')];
            if (Lines.Count > 0 && Lines[^1] == "") Lines.RemoveAt(Lines.Count - 1);
        }

        /// <summary>(name, value, line index) in file order; name is section[.sub].key with section and key lowercased.</summary>
        public List<(string Name, string Value, int At)> Items()
        {
            var output = new List<(string, string, int)>();
            string? section = null;
            int i = 0;
            while (i < Lines.Count)
            {
                string line = Lines[i];
                int at = i;
                while (line.TrimEnd().EndsWith('\\') && i + 1 < Lines.Count && !InComment(line))
                {
                    i++;
                    line = line.TrimEnd()[..^1] + Lines[i];
                }
                i++;
                var s = line.Trim();
                if (s.Length == 0 || s[0] is '#' or ';') continue;
                if (s.StartsWith('['))
                {
                    var m = SectionRx.Match(s);
                    if (!m.Success) throw new GitException(Msg("badConfig", ("line", (at + 1).ToString(Inv))));
                    var name = m.Groups[1].Value;
                    if (m.Groups[2].Success) section = name.ToLowerInvariant() + "." + Regex.Replace(m.Groups[2].Value, @"\\(.)", "$1");
                    else if (name.Contains('.'))
                    {
                        int dot = name.IndexOf('.');
                        section = name[..dot].ToLowerInvariant() + "." + name[(dot + 1)..];
                    }
                    else section = name.ToLowerInvariant();
                    s = s[(m.Index + m.Length)..].Trim();
                    if (s.Length == 0 || s[0] is '#' or ';') continue;
                }
                if (section == null) throw new GitException(Msg("badConfig", ("line", (at + 1).ToString(Inv))));
                var k = Regex.Match(s, @"^([A-Za-z][A-Za-z0-9-]*)\s*(=\s*(.*))?$");
                if (!k.Success) throw new GitException(Msg("badConfig", ("line", (at + 1).ToString(Inv))));
                output.Add(($"{section}.{k.Groups[1].Value.ToLowerInvariant()}", k.Groups[2].Success ? Value(k.Groups[3].Value) : "true", at));
            }
            return output;
        }

        public string? Get(string name)
        {
            string? val = null;
            foreach (var (n, v, _) in Items()) if (n == name) val = v;
            return val;
        }

        /// <summary>Sets (or with null removes) name; true when something changed.</summary>
        public bool Set(string name, string? value)
        {
            int dot = name.LastIndexOf('.');
            string section = dot >= 0 ? name[..dot] : "", key = name[(dot + 1)..];
            var found = Items().Where(x => x.Name == name).Select(x => x.At).ToList();
            if (value == null)
            {
                for (int k = found.Count - 1; k >= 0; k--) Lines.RemoveAt(found[k]);
                DropEmptySections();
                return found.Count > 0;
            }
            var line = $"\t{key} = {Quote(value)}";
            if (found.Count > 0) { Lines[found[^1]] = line; return true; }
            int? last = null;
            string? cur = null;
            for (int i = 0; i < Lines.Count; i++)
            {
                var s = Lines[i].Trim();
                if (s.StartsWith('['))
                {
                    cur = SectionOf(s);
                    if (cur == section) last = i;
                }
                else if (cur == section && s.Length > 0) last = i;
            }
            if (last is { } l) Lines.Insert(l + 1, line);
            else
            {
                int d = section.IndexOf('.');
                Lines.Add(d >= 0 ? $"[{section[..d]} \"{section[(d + 1)..]}\"]" : $"[{section}]");
                Lines.Add(line);
            }
            return true;
        }

        /// <summary>A section header left with nothing under it goes too (as git does after removing its last key).</summary>
        void DropEmptySections()
        {
            var keep = new List<string>();
            int i = 0;
            while (i < Lines.Count)
            {
                var s = Lines[i].Trim();
                if (s.StartsWith('[') && s.EndsWith(']'))
                {
                    int j = i + 1;
                    while (j < Lines.Count && Lines[j].Trim().Length == 0) j++;
                    if (j >= Lines.Count || Lines[j].Trim().StartsWith('[')) { i = j; continue; }
                }
                keep.Add(Lines[i]);
                i++;
            }
            Lines = keep;
        }

        public string Text() => Lines.Count > 0 ? string.Join("\n", Lines) + "\n" : "";
    }

    static string? SectionOf(string s)
    {
        var m = SectionRx.Match(s);
        if (!m.Success) return null;
        if (m.Groups[2].Success) return m.Groups[1].Value.ToLowerInvariant() + "." + m.Groups[2].Value;
        var name = m.Groups[1].Value;
        int dot = name.IndexOf('.');
        return dot < 0 ? name.ToLowerInvariant() : name[..dot].ToLowerInvariant() + (dot + 1 < name.Length ? "." + name[(dot + 1)..] : "");
    }

    static bool InComment(string line)
    {
        bool q = false;
        foreach (var c in line)
        {
            if (c == '"') q = !q;
            else if (c is '#' or ';' && !q) return true;
        }
        return false;
    }

    static string Value(string raw)
    {
        var sb = new StringBuilder();
        bool q = false;
        raw = raw.TrimEnd();
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '"') q = !q;
            else if (c == '\\' && i + 1 < raw.Length)
            {
                i++;
                sb.Append(raw[i] switch { 'n' => '\n', 't' => '\t', 'b' => '\b', var x => x });
            }
            else if (c is '#' or ';' && !q) break;
            else sb.Append(c);
        }
        return q ? sb.ToString() : sb.ToString().Trim();
    }

    static string Quote(string v)
    {
        var e = v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t");
        return v != v.Trim() || v.Contains('#') || v.Contains(';') ? $"\"{e}\"" : e;
    }

    public static bool? ConfigBool(string? v) => v?.ToLowerInvariant() switch
    {
        null => null,
        "true" or "yes" or "on" or "1" or "" => true,
        "false" or "no" or "off" or "0" => false,
        _ => null,
    };

    public static bool ValidKey(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0) return false;
        return Regex.IsMatch(name[(dot + 1)..], @"^[A-Za-z][A-Za-z0-9-]*\z") && Regex.IsMatch(name[..dot], @"^[A-Za-z0-9-]+(\..+)?\z");
    }

    public static string CanonicalKey(string name)
    {
        int dot = name.IndexOf('.');
        string head = dot >= 0 ? name[..dot] : name, rest = dot >= 0 ? name[(dot + 1)..] : "";
        int last = rest.LastIndexOf('.');
        string sub = last >= 0 ? rest[..last] : "", key = rest[(last + 1)..];
        return last >= 0 && sub.Length > 0 ? $"{head.ToLowerInvariant()}.{sub}.{key.ToLowerInvariant()}" : $"{head.ToLowerInvariant()}.{key.ToLowerInvariant()}";
    }

    // ---------- ignore rules ----------

    /// <summary>A gitignore / pathspec glob as a regex: * and ? stop at '/', ** spans directories, [...] is a class.</summary>
    public static string GlobRegex(string pattern, bool fullPath)
    {
        var sb = new StringBuilder();
        int i = 0, n = pattern.Length;
        while (i < n)
        {
            char c = pattern[i];
            if (c == '*')
            {
                if (string.CompareOrdinal(pattern, i, "**", 0, 2) == 0 && i + 1 < n && (i == 0 || pattern[i - 1] == '/') && (i + 2 == n || pattern[i + 2] == '/'))
                {
                    if (i + 2 == n) { sb.Append(".*"); i += 2; }
                    else { sb.Append("(?:.*/)?"); i += 3; }
                    continue;
                }
                sb.Append(fullPath ? "[^/]*" : ".*");
            }
            else if (c == '?') sb.Append(fullPath ? "[^/]" : ".");
            else if (c == '[')
            {
                int j = i + 1;
                if (j < n && pattern[j] is '!' or '^') j++;
                if (j < n && pattern[j] == ']') j++;
                while (j < n && pattern[j] != ']') j++;
                if (j >= n) sb.Append("\\[");
                else
                {
                    var body = pattern[(i + 1)..j];
                    if (body.Length > 0 && body[0] is '!' or '^') body = "^" + body[1..];
                    sb.Append('[').Append(body.Replace("\\", "\\\\")).Append(']');
                    i = j;
                }
            }
            else if (c == '\\' && i + 1 < n)
            {
                i++;
                sb.Append(Regex.Escape(pattern[i].ToString()));
            }
            else sb.Append(Regex.Escape(c.ToString()));
            i++;
        }
        return sb.ToString();
    }

    /// <summary>A whole-string match of a glob (null when the glob makes no valid pattern).</summary>
    public static Regex? GlobFull(string glob, bool fullPath, RegexOptions options = RegexOptions.Singleline)
    {
        try { return new Regex(@"\A(?:" + GlobRegex(glob, fullPath) + @")\z", options | RegexOptions.CultureInvariant, Shell.RegexTimeout); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>One ignore file's patterns, relative to its directory (base, "" for the top).</summary>
    public sealed class IgnoreRules
    {
        readonly string _base;
        readonly List<(Regex Rx, bool Neg, bool DirOnly, bool Anchored)> _rules = [];

        public IgnoreRules(string text, string @base)
        {
            _base = @base;
            foreach (var raw in text.Split('\n').Take(10_000))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith('#')) continue;
                while (line.EndsWith(' ') && !line.EndsWith("\\ ")) line = line[..^1];
                bool neg = line.StartsWith('!');
                if (neg) line = line[1..];
                else if (line.StartsWith("\\!") || line.StartsWith("\\#")) line = line[1..];
                if (line.Length == 0) continue;
                bool dirOnly = line.EndsWith('/');
                line = line.TrimEnd('/');
                bool anchored = line.Contains('/');
                line = line.TrimStart('/');
                if (GlobFull(line, true) is { } rx) _rules.Add((rx, neg, dirOnly, anchored));
            }
        }

        /// <summary>Null when no pattern speaks about path (relative to the top), else whether it is ignored.</summary>
        public bool? Match(string path, bool isDir)
        {
            var rel = _base.Length > 0 ? (path.Length > _base.Length + 1 ? path[(_base.Length + 1)..] : "") : path;
            var name = rel[(rel.LastIndexOf('/') + 1)..];
            for (int i = _rules.Count - 1; i >= 0; i--)
            {
                var (rx, neg, dirOnly, anchored) = _rules[i];
                if (dirOnly && !isDir) continue;
                if (rx.IsMatch(anchored ? rel : name)) return !neg;
            }
            return null;
        }
    }

    // ---------- line diff ----------
    // Lines are Latin-1 strings: one char per byte, so comparing and hashing them is exact.

    /// <summary>Lines diff, merge and apply hold as one string each: a 32 MB file of short lines costs some 40 times its size.</summary>
    public const int MaxLines = 1_000_000;

    public static List<string> SplitLines(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        int count = text.AsSpan().Count('\n');
        if (count > MaxLines) throw new GitException(Msg("tooLarge", ("what", $"a file of more than {MaxLines} lines")));
        var output = new List<string>(count + 1);
        int start = 0;
        for (int nl; (nl = text.IndexOf('\n', start)) >= 0; start = nl + 1) output.Add(text.Substring(start, nl + 1 - start));
        if (start < text.Length) output.Add(text[start..]);
        return output;
    }

    /// <summary>Changed regions [(a_start, a_end, b_start, b_end)] turning a into b (fewest changes), each slid as far down
    /// as it can go.</summary>
    public static List<(int S1, int E1, int S2, int E2)> Myers(IReadOnlyList<string> a, IReadOnlyList<string> b, Action? check = null)
    {
        int pre = 0;
        while (pre < a.Count && pre < b.Count && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Count - pre && suf < b.Count - pre && a[a.Count - 1 - suf] == b[b.Count - 1 - suf]) suf++;
        int n = a.Count - pre - suf, m = b.Count - pre - suf;
        string A(int x) => a[pre + x];
        string B(int y) => b[pre + y];
        var removed = new bool[a.Count];
        var added = new bool[b.Count];
        if (n == 0 || m == 0)
        {
            for (int i = 0; i < n; i++) removed[pre + i] = true;
            for (int j = 0; j < m; j++) added[pre + j] = true;
        }
        else
        {
            int mx = n + m, off = mx + 1;
            var v = new int[2 * mx + 3];
            Array.Fill(v, -1);
            v[off + 1] = 0;
            var trace = new List<int[]>();
            long steps = 0;
            bool done = false;
            for (int d = 0; d <= mx; d++)
            {
                trace.Add(v[(off - d - 1)..(off + d + 2)]);  // V before step d, for k in -d-1..d+1
                for (int k = -d; k <= d; k += 2)
                {
                    int x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
                    int y = x - k;
                    while (x < n && y < m && A(x) == B(y)) { x++; y++; }
                    v[off + k] = x;
                    if (x >= n && y >= m) { done = true; break; }
                }
                if (done) break;
                steps += d + 1;
                if (steps > DiffBudget) break;
                if (check != null && d % 64 == 0) check();
            }
            if (!done)
            {
                for (int i = 0; i < n; i++) removed[pre + i] = true;
                for (int j = 0; j < m; j++) added[pre + j] = true;
            }
            else
            {
                int x = n, y = m;
                for (int d = trace.Count - 1; d > 0; d--)
                {
                    var vv = trace[d];
                    int k = x - y;
                    int pk = k == -d || (k != d && vv[k - 1 + d + 1] < vv[k + 1 + d + 1]) ? k + 1 : k - 1;
                    int px = vv[pk + d + 1], py = px - pk;
                    if (pk == k + 1) added[pre + py] = true;  // a step down: B[py] inserted
                    else removed[pre + px] = true;  // a step right: A[px] removed
                    (x, y) = (px, py);
                }
            }
        }
        Slide(a, removed);
        Slide(b, added);
        return Regions(removed, added);
    }

    /// <summary>Moves each run of changed lines down while the line after it equals its first line (git's compaction).</summary>
    static void Slide(IReadOnlyList<string> lines, bool[] changed)
    {
        int i = 0, n = lines.Count;
        while (i < n)
        {
            if (!changed[i]) { i++; continue; }
            int j = i;
            while (j < n && changed[j]) j++;
            while (j < n && !changed[j] && lines[i] == lines[j])
            {
                changed[i] = false;
                changed[j] = true;
                i++;
                j++;
                while (j < n && changed[j]) j++;
            }
            i = j;
        }
    }

    static List<(int, int, int, int)> Regions(bool[] removed, bool[] added)
    {
        var output = new List<(int, int, int, int)>();
        int i = 0, j = 0, n = removed.Length, m = added.Length;
        while (i < n || j < m)
        {
            if (i < n && j < m && !removed[i] && !added[j]) { i++; j++; continue; }
            int si = i, sj = j;
            while (i < n && removed[i]) i++;
            while (j < m && added[j]) j++;
            if (si == i && sj == j) { i = Math.Min(i + 1, n); j = Math.Min(j + 1, m); continue; }  // unequal lengths left over: should not happen
            output.Add((si, i, sj, j));
        }
        return output;
    }

    /// <summary>Hunks as text lines ('@@ ... @@', ' x', '-x', '+x', '\ No newline at end of file').</summary>
    public static List<string> Unified(List<string> a, List<string> b, int context, Action? check = null)
    {
        var regions = Myers(a, b, check);
        var output = new List<string>();
        int g = 0;
        while (g < regions.Count)
        {
            int h = g;
            while (h + 1 < regions.Count && regions[h + 1].S1 - regions[h].E1 <= 2 * context) h++;
            int a1 = Math.Max(0, regions[g].S1 - context), a2 = Math.Min(a.Count, regions[h].E1 + context);
            int b1 = Math.Max(0, regions[g].S2 - context), b2 = Math.Min(b.Count, regions[h].E2 + context);
            var func = "";
            for (int k = a1 - 1; k >= 0; k--)
            {
                if (a[k].Length > 0 && (char.IsAsciiLetter(a[k][0]) || a[k][0] is '_' or '$'))
                {
                    var t = Text(a[k].TrimEnd('\n')).TrimEnd();
                    func = " " + (t.Length > 80 ? t[..80] : t).TrimEnd();
                    break;
                }
            }
            static string Rng(int s, int e)
            {
                int cnt = e - s, start = cnt > 0 ? s + 1 : s;
                return cnt == 1 ? $"{start}" : $"{start},{cnt}";
            }
            output.Add($"@@ -{Rng(a1, a2)} +{Rng(b1, b2)} @@{func}");
            int i = a1;
            for (int k = g; k <= h; k++)
            {
                var (s1, e1, s2, e2) = regions[k];
                while (i < s1) Line(output, " ", a[i++]);
                for (int x = s1; x < e1; x++) Line(output, "-", a[x]);
                for (int y = s2; y < e2; y++) Line(output, "+", b[y]);
                i = e1;
            }
            while (i < a2) Line(output, " ", a[i++]);
            g = h + 1;
        }
        return output;
    }

    /// <summary>A Latin-1 line (bytes) as shell text (UTF-8, odd bytes escaped).</summary>
    public static string Text(string latin1) => ByteText.Decode(Encoding.Latin1.GetBytes(latin1));

    static void Line(List<string> output, string sign, string line)
    {
        var text = Text(line);
        if (text.EndsWith('\n')) output.Add(sign + text[..^1]);
        else
        {
            output.Add(sign + text);
            output.Add("\\ No newline at end of file");
        }
    }

    public static bool IsBinary(byte[] data) => Array.IndexOf(data, (byte)0, 0, Math.Min(data.Length, 8000)) >= 0;

    public static (int Ins, int Dels) CountChanges(byte[] a, byte[] b, Action? check = null)
    {
        var regs = Myers(SplitLines(a), SplitLines(b), check);
        return (regs.Sum(r => r.E2 - r.S2), regs.Sum(r => r.E1 - r.S1));
    }

    // ---------- three-way line merge ----------

    /// <summary>(merged bytes, conflicted). Changes from both sides that overlap or touch become a conflict block.</summary>
    public static (byte[] Merged, bool Conflict) MergeLines(byte[] @base, byte[] ours, byte[] theirs, string ourLabel, string theirLabel, Action? check = null)
    {
        List<string> o = SplitLines(@base), a = SplitLines(ours), b = SplitLines(theirs);
        var ra = Myers(o, a, check).Select(r => (r.S1, r.E1, r.S2, r.E2, Side: 0));
        var rb = Myers(o, b, check).Select(r => (r.S1, r.E1, r.S2, r.E2, Side: 1));
        var all = ra.Concat(rb).OrderBy(r => r.S1).ThenBy(r => r.E1).ToList();
        var output = new List<string>();
        int pos = 0, i = 0;
        bool conflict = false;
        while (i < all.Count)
        {
            int s = all[i].S1, e = all[i].E1;
            var group = new List<(int S1, int E1, int S2, int E2, int Side)> { all[i] };
            int j = i + 1;
            while (j < all.Count && all[j].S1 <= e)  // overlapping or touching changes go together
            {
                e = Math.Max(e, all[j].E1);
                group.Add(all[j]);
                j++;
            }
            output.AddRange(Sl(o, pos, s));
            var sides = group.Select(r => r.Side).Distinct().ToList();
            if (sides.Count == 1)
            {
                var side = sides[0] == 0 ? a : b;
                int p = s;  // one side only: replay its regions in order
                foreach (var r in group)
                {
                    output.AddRange(Sl(o, p, r.S1));
                    output.AddRange(Sl(side, r.S2, r.E2));
                    p = r.E1;
                }
                output.AddRange(Sl(o, p, e));
            }
            else
            {
                var va = SideText(o, a, group.Where(r => r.Side == 0), s, e);
                var vb = SideText(o, b, group.Where(r => r.Side == 1), s, e);
                if (va.SequenceEqual(vb)) output.AddRange(va);
                else
                {
                    // trim lines both sides agree on at the edges
                    int k = 0;
                    while (k < va.Count && k < vb.Count && va[k] == vb[k]) k++;
                    int t = 0;
                    while (t < va.Count - k && t < vb.Count - k && va[^(1 + t)] == vb[^(1 + t)]) t++;
                    output.AddRange(va.GetRange(0, k));
                    conflict = true;
                    output.Add(Latin1Of($"<<<<<<< {ourLabel}\n"));
                    output.AddRange(Terminated(va.GetRange(k, va.Count - t - k)));
                    output.Add("=======\n");
                    output.AddRange(Terminated(vb.GetRange(k, vb.Count - t - k)));
                    output.Add(Latin1Of($">>>>>>> {theirLabel}\n"));
                    output.AddRange(va.GetRange(va.Count - t, t));
                }
            }
            pos = e;
            i = j;
        }
        output.AddRange(Sl(o, pos, o.Count));
        return (Encoding.Latin1.GetBytes(string.Concat(output)), conflict);
    }

    /// <summary>Python's l[s:e]: empty when e &lt;= s.</summary>
    static List<string> Sl(List<string> l, int s, int e) => e > s ? l.GetRange(s, Math.Min(e, l.Count) - s) : [];

    static string Latin1Of(string text) => Encoding.Latin1.GetString(ByteText.Encode(text));

    static List<string> Terminated(List<string> lines)
    {
        if (lines.Count > 0 && !lines[^1].EndsWith('\n')) lines[^1] += "\n";
        return lines;
    }

    static List<string> SideText(List<string> o, List<string> side, IEnumerable<(int S1, int E1, int S2, int E2, int Side)> regs, int s, int e)
    {
        var output = new List<string>();
        int p = s;
        foreach (var r in regs)
        {
            output.AddRange(Sl(o, p, r.S1));
            output.AddRange(Sl(side, r.S2, r.E2));
            p = r.E1;
        }
        output.AddRange(Sl(o, p, e));
        return output;
    }

    // ---------- quoting ----------

    /// <summary>A path as git prints it: in double quotes with C escapes when it has control, quote, backslash or non-ASCII bytes.</summary>
    public static string CQuote(string path)
    {
        var raw = ByteText.Encode(path);
        if (!raw.Any(c => c < 0x20 || c >= 0x7F || c is 0x22 or 0x5C)) return path;
        var sb = new StringBuilder("\"");
        foreach (var c in raw)
        {
            if (c is 0x22 or 0x5C) sb.Append('\\').Append((char)c);
            else if (c is >= 7 and <= 13) sb.Append('\\').Append("abtnvfr"[c - 7]);
            else if (c < 0x20 || c >= 0x7F) sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
            else sb.Append((char)c);
        }
        return sb.Append('"').ToString();
    }
}
