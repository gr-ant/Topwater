using System.Globalization;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Topwater;

/// <summary>
/// tar, gzip/gunzip/zcat, zip/unzip over the virtual filesystem. Archive bytes cross pipes and redirects as <see cref="ByteText"/>.
/// Archives are untrusted: extraction never leaves the target directory (no `..`, absolute names are made relative, nothing is
/// written under or hard-linked through a symbolic link the archive made, hard links only reach entries already there), and every extraction or
/// decompression stops with an error past <see cref="ShellOptions.MaxExtractBytes"/> / <see cref="ShellOptions.MaxExtractEntries"/>.
/// </summary>
internal static partial class Builtins
{
    /// <summary>What one extraction or decompression may still produce.</summary>
    sealed class ExtractBudget(ShellOptions o, int exitCode)
    {
        long _bytes = o.MaxExtractBytes;
        int _entries = o.MaxExtractEntries;

        public void Entry()
        {
            if (--_entries < 0) throw new ShellException(CommandSpec.Message("tar", "entryLimit", ("limit", o.MaxExtractEntries.ToString(CultureInfo.InvariantCulture))), exitCode);
        }

        public void Charge(long n)
        {
            if ((_bytes -= n) < 0) throw new ShellException(CommandSpec.Message("tar", "byteLimit", ("limit", o.MaxExtractBytes.ToString(CultureInfo.InvariantCulture))), exitCode);
        }

        public byte[] Read(Stream s)
        {
            var ms = new MemoryStream();
            var buf = new byte[81920];
            for (int n; (n = s.Read(buf)) > 0;)
            {
                Charge(n);
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }

        /// <summary>Refuses a zip that states or holds more entries than one extraction may have, before the zip
        /// reader builds an object for every entry (a disk of 47-byte directory records holds millions).</summary>
        public void ZipEntries(MemoryStream zip)
        {
            if (ShellSnapshot.ZipEntryCount(zip, o.MaxExtractEntries) <= o.MaxExtractEntries) return;
            _entries = 0;
            Entry();
        }
    }

    static bool IsGzip(byte[] d) => d.Length >= 2 && d[0] == 0x1f && d[1] == 0x8b;

    static byte[] GzipBytes(byte[] data, CompressionLevel level)
    {
        var ms = new MemoryStream();
        using (var z = new GZipStream(ms, level, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>Decompresses gzip data (all members); null with <paramref name="error"/> set (gzip's wording) if it's corrupt.</summary>
    static byte[]? GunzipBytes(byte[] data, ExtractBudget budget, out string error)
    {
        error = "";
        try
        {
            using var z = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            var plain = budget.Read(z);
            // GZipStream returns what it has for a cut-off stream; a complete one ends with its last member's CRC-32 and size
            if (data.Length >= 18 && BitConverter.ToUInt32(data, data.Length - 4) is var n && n <= plain.Length
                && Crc32(plain.AsSpan(plain.Length - (int)n)) == BitConverter.ToUInt32(data, data.Length - 8)) return plain;
            error = "unexpected end of file";
        }
        catch (InvalidDataException) { error = "invalid compressed data--format violated"; }
        catch (EndOfStreamException) { error = "unexpected end of file"; }
        return null;
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    /// <summary>Header plus trailer bytes of a gzip member, which gzip leaves out of its ratio.</summary>
    static int GzipOverhead(byte[] d)
    {
        int flags = d.Length > 3 ? d[3] : 0, i = 10;
        if ((flags & 4) != 0 && i + 2 <= d.Length) i += 2 + (d[i] | d[i + 1] << 8);
        if ((flags & 8) != 0) i = Array.IndexOf(d, (byte)0, Math.Min(i, d.Length)) + 1;
        if ((flags & 16) != 0 && i > 0) i = Array.IndexOf(d, (byte)0, Math.Min(i, d.Length)) + 1;
        if ((flags & 2) != 0) i += 2;
        return Math.Max(i, 10) + 8;
    }

    static string Ratio(long compressed, long uncompressed) =>
        uncompressed == 0 ? "0.0" : ((uncompressed - compressed) * 100.0 / uncompressed).ToString("0.0", CultureInfo.InvariantCulture);

    static void WriteBytes(TextWriter w, byte[] data) => w.Write(ByteText.Decode(data));

    /// <summary>The leading part GNU tar strips from a member name: leading slashes, or everything through the last `..`.</summary>
    static string TarPrefix(string name)
    {
        int cut = 0;
        while (cut < name.Length && name[cut] == '/') cut++;
        for (int i = 0; i < name.Length;)
        {
            int j = name.IndexOf('/', i);
            if (j < 0) j = name.Length;
            if (name[i..j] == "..") { cut = j; while (cut < name.Length && name[cut] == '/') cut++; }
            i = j + 1;
        }
        return name[..cut];
    }

    /// <summary>--exclude: a pattern (whose * also matches /) against the whole name or any run of its components, so `sub`
    /// leaves out d/sub and everything under it.</summary>
    static bool TarExcluded(string name, List<Regex> patterns)
    {
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
            for (int j = i + 1; j <= parts.Length; j++)
                if (patterns.Any(rx => rx.IsMatch(string.Join('/', parts[i..j])))) return true;
        return false;
    }

    /// <summary>--transform: each s/REGEX/REPLACEMENT/FLAGS (any delimiter, several joined by ;) as (regex, replacement, all, nth).</summary>
    static List<(Regex Rx, string Repl, bool All, int Nth)> TarTransforms(IReadOnlyList<string> exprs)
    {
        ShellException Bad(string key, params (string, string)[] values) =>
            new(CommandSpec.Message("tar", key, values) + "\n" + CommandSpec.Of("tar").TryHelp.TrimEnd('\n'), 2);
        var rules = new List<(Regex, string, bool, int)>();
        foreach (var expr in exprs)
        {
            int i = 0;
            while (i < expr.Length)
            {
                if (expr[i] == ';') { i++; continue; }
                if (expr[i] != 's' || i + 1 >= expr.Length) throw Bad("badTransform");
                char delim = expr[i + 1];
                i += 2;
                var parts = new List<string>();
                for (int n = 0; n < 2; n++)
                {
                    var buf = new StringBuilder();
                    while (i < expr.Length && expr[i] != delim)
                    {
                        if (expr[i] == '\\' && i + 1 < expr.Length)
                        {
                            if (expr[i + 1] == delim) buf.Append(delim); else buf.Append(expr, i, 2);
                            i += 2;
                            continue;
                        }
                        buf.Append(expr[i++]);
                    }
                    if (i >= expr.Length) throw Bad("badTransform");
                    parts.Add(buf.ToString());
                    i++;
                }
                bool all = false, extended = false, icase = false;
                var digits = "";
                for (; i < expr.Length && expr[i] != ';'; i++)
                {
                    char c = expr[i];
                    if (char.IsAsciiDigit(c)) digits += c;
                    else if ("gixsSrRhH".Contains(c)) // s/r/h (which names it applies to) are accepted; it applies to all
                    {
                        all |= c == 'g';
                        icase |= c == 'i';
                        extended |= c == 'x';
                    }
                    else throw Bad("badTransformFlag", ("flag", c.ToString()));
                }
                int nth = digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) : 1;
                if (PosixRegexError(parts[0], extended, grep: false) is { } error) throw Bad("badTransformRegex", ("error", error));
                var rx = new Regex(PosixToNet(parts[0], extended), (icase ? RegexOptions.IgnoreCase : 0) | RegexOptions.CultureInvariant, Shell.RegexTimeout);
                int groups = rx.GetGroupNumbers().Length - 1;
                if (Regex.Matches(parts[1], @"\\([0-9])").Any(m => m.Groups[1].Value[0] - '0' > groups)) throw Bad("badTransformBackReference");
                rules.Add((rx, parts[1], all, nth));
            }
        }
        return rules;
    }

    /// <summary>Applies the --transform rules in order. In a replacement &amp; is the match, \N a group, \&amp; a plain &amp;.</summary>
    static string TarRename(string name, List<(Regex Rx, string Repl, bool All, int Nth)> rules)
    {
        static string Expand(Match m, string repl)
        {
            var output = new StringBuilder();
            for (int k = 0; k < repl.Length; k++)
            {
                char c = repl[k];
                if (c == '&') output.Append(m.Value);
                else if (c == '\\' && k + 1 < repl.Length)
                {
                    char n = repl[++k];
                    output.Append(char.IsAsciiDigit(n) ? m.Groups[n - '0'].Value : n == '&' ? "&" : $"{c}{n}");
                }
                else output.Append(c);
            }
            return output.ToString();
        }
        foreach (var (rx, repl, all, nth) in rules)
        {
            int count = 0;
            name = rx.Replace(name, m => ++count == nth || (all && count > nth) ? Expand(m, repl) : m.Value);
        }
        return name;
    }

    /// <summary>--owner/--group NAME[:ID]: (id, name); a bare number is an id, a bare name gets its id when known.</summary>
    static (int Id, string Name) TarIds(string? value, string user, string fatal)
    {
        if (value == null) return (-1, "");
        var known = new Dictionary<string, int> { ["root"] = 0, [user] = 1000 };
        int colon = value.IndexOf(':');
        string name = colon >= 0 ? value[..colon] : value, ident = colon >= 0 ? value[(colon + 1)..] : "";
        static bool Digits(string x) => x.Length > 0 && x.All(char.IsAsciiDigit);
        if (colon < 0 && Digits(name)) (name, ident) = ("", name);
        if (ident.Length == 0) return (known.GetValueOrDefault(name, 1000), name);
        // shortcut: ids past 7 octal digits are refused (GNU tar stores them in base 256)
        if (!Digits(ident) || ident.TrimStart('0').Length > 7 || int.Parse(ident, CultureInfo.InvariantCulture) > 0x1FFFFF)
            throw new ShellException(CommandSpec.Message("tar", "badId", ("value", ident)) + "\ntar: " + fatal, 2);
        int uid = int.Parse(ident, CultureInfo.InvariantCulture);
        return (uid, name.Length > 0 ? name : known.FirstOrDefault(kv => kv.Value == uid).Key ?? "");
    }

    static bool HasDotDot(string path) => path.Split('/').Contains("..");

    static string Reason(ShellException e) => e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) is var i and >= 0 ? i + 2 : 0)..];

    static void RegisterArchiveCommands(Shell shell)
    {
        shell.Register("tar", Tar);

        foreach (var name in new[] { "gzip", "gunzip", "zcat" })
            shell.Register(name, Gzip);

        shell.Register("zip", Zip);
        shell.Register("unzip", Unzip);
    }

    static int Tar(CommandContext ctx)
    {
        var args = ctx.Args.ToList();
        if (args.Count > 0 && args[0].Length > 0 && args[0][0] != '-')
        {
            // old style: `tar czf out.tgz dir`, where f and C take the words after the bundle, in order
            var bundle = args[0];
            var rest = args.Skip(1).ToList();
            args = [];
            foreach (var c in bundle)
            {
                args.Add("-" + c);
                if (c is not ('f' or 'C')) continue;
                if (rest.Count == 0) throw new ShellException(CommandSpec.Message("tar", "oldOptionNeedsArgument", ("option", c.ToString())) + "\n" + CommandSpec.Of("tar").TryHelp.TrimEnd('\n'), 2);
                args.Add(rest[0]);
                rest.RemoveAt(0);
            }
            args.AddRange(rest);
        }
        var Usage = "\n" + CommandSpec.Of("tar").TryHelp.TrimEnd('\n');
        var a = Args.Parse(args, CommandSpec.Of("tar"));
        var modes = "cxt".Count(a.Has);
        if (modes == 0) throw new ShellException(CommandSpec.Message("tar", "noMode") + Usage, 2);
        if (modes > 1) throw new ShellException(CommandSpec.Message("tar", "manyModes") + Usage, 2);
        int strip = a.Value('\u0001') is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : 0;
        var archive = a.Value('f') ?? "-";
        var fs = ctx.Fs;
        string Fatal(string msg) => msg + "\ntar: " + CommandSpec.Message("tar", "unrecoverable");
        var dir = ctx.ResolvePath(a.Value('C') ?? ".");
        // without -C the working directory is only needed for relative names, so a deleted one still lists /abs.tar
        if (a.Value('C') is { } cdir && !fs.IsDirectory(dir)) throw new ShellException(Fatal(CommandSpec.Message("tar", "cannotOpen", ("file", cdir), ("errno", fs.Exists(dir) ? "Not a directory" : fs.WhyMissing(dir)))), 2);
        var excludes = a.All('\u0002').Select(p => ctx.Shell.Timed(Glob.ToRegex(p, slashes: true))).ToList();
        var rules = TarTransforms(a.All('\u0006')).Select(r => (ctx.Shell.Timed(r.Rx), r.Repl, r.All, r.Nth)).ToList();
        bool numeric = a.Has('\u0005');
        int code = 0;
        void Fail(string msg) { ctx.Stderr.WriteLine("tar: " + msg); code = 2; }
        int Done()
        {
            if (code != 0) ctx.Stderr.WriteLine("tar: " + CommandSpec.Message("tar", "previousErrors"));
            return code;
        }

        if (a.Has('c'))
        {
            if (a.Positional.Count == 0) throw new ShellException(CommandSpec.Message("tar", "emptyArchive") + Usage, 2);
            var archivePath = archive == "-" ? null : ctx.ResolvePath(archive);
            var verbose = archive == "-" ? ctx.Stderr : ctx.Stdout;
            var ms = new MemoryStream();
            bool stripped = false;
            var linked = new List<(string Path, string Member)>(); // files with several names already stored
            var unrecoverable = CommandSpec.Message("tar", "unrecoverable");
            var owner = TarIds(a.Value('\u0003'), ctx.Shell.Options.User, unrecoverable);
            var group = TarIds(a.Value('\u0004'), ctx.Shell.Options.User, unrecoverable);
            using (var w = new TarWriter(ms, TarEntryFormat.Gnu, leaveOpen: true))
            {
            void Add(string name, string path)
                {
                    if (path == archivePath) return; // the archive itself, as GNU tar skips it
                    if (excludes.Count > 0 && TarExcluded(name, excludes)) return;
                    var member = name.TrimStart('/');
                    if (rules.Count > 0) member = TarRename(member.TrimEnd('/'), rules).TrimStart('/') is { Length: > 0 } renamed ? renamed : ".";
                    try
                    {
                        var st = fs.Stat(path, follow: false); // a symbolic link is stored as one, never followed
                        if (st.IsDirectory) member = member.TrimEnd('/') + "/";
                        // a file with several names: the first goes in whole, later ones as links to it
                        var first = st.Links > 1 && !st.IsDirectory ? linked.FirstOrDefault(l => fs.SameFile(l.Path, path, follow: false)).Member : null;
                        var type = st.IsDirectory ? TarEntryType.Directory : first != null ? TarEntryType.HardLink : st.IsSymlink ? TarEntryType.SymbolicLink : TarEntryType.RegularFile;
                        int uid = st.Owner == "root" ? 0 : 1000, gid = uid;
                        string uname = st.Owner, gname = st.Owner;
                        if (owner.Id >= 0) (uid, uname) = owner;
                        if (group.Id >= 0) (gid, gname) = group;
                        if (numeric) uname = gname = "";
                        var entry = new GnuTarEntry(type, member)
                        {
                            Mode = (UnixFileMode)(st.Mode & 0xFFF), ModificationTime = st.Modified,
                            UserName = uname, GroupName = gname, Uid = uid, Gid = gid,
                        };
                        var target = st.LinkTarget;
                        if (rules.Count > 0 && !string.IsNullOrEmpty(target) && first == null) target = TarRename(target, rules);
                        if ((first ?? target) is { } linkName) entry.LinkName = linkName;
                        var children = st.IsDirectory ? fs.List(path) : [];
                        if (type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream(fs.ReadAllBytes(path));
                        if (st.Links > 1 && first == null && !st.IsDirectory) linked.Add((path, member));
                        if (member != "/")
                        {
                            w.WriteEntry(entry);
                            if (a.Has('v')) verbose.WriteLine(member);
                        }
                        foreach (var child in children) Add(name.TrimEnd('/') + "/" + child, path.TrimEnd('/') + "/" + child);
                    }
                    catch (ShellException e) { Fail(CommandSpec.Message("tar", "cannotOpen", ("file", name), ("errno", Reason(e)))); }
                }
                foreach (var f in a.Positional)
                {
                    if (f.StartsWith('/') && !stripped) { stripped = true; ctx.Stderr.WriteLine(CommandSpec.Message("tar", "removingLeading", ("prefix", "/"))); }
                    var path = VirtualFileSystem.Normalize(dir, f);
                    if (!fs.Exists(path, follow: false)) Fail(CommandSpec.Message("tar", "cannotStat", ("file", f), ("errno", fs.WhyMissing(path, follow: false))));
                    else Add(f, path);
                }
            }
            ms.SetLength(Math.Max(1, (ms.Length + 10239) / 10240) * 10240); // GNU tar's default blocking factor: 20 records
            var bytes = a.Has('z') ? GzipBytes(ms.ToArray(), CompressionLevel.Optimal) : ms.ToArray();
            if (archivePath == null) WriteBytes(ctx.Stdout, bytes);
            else fs.WriteAllBytes(archivePath, bytes);
            return Done();
        }

        byte[] data;
        if (archive == "-") data = ByteText.Encode(ctx.StdinText);
        else
        {
            var path = ctx.ResolvePath(archive);
            if (!fs.IsFile(path)) throw new ShellException(Fatal(CommandSpec.Message("tar", "cannotOpen", ("file", archive), ("errno", fs.IsDirectory(path) ? "Is a directory" : fs.WhyMissing(path)))), 2);
            data = fs.ReadAllBytes(path);
        }
        if (IsGzip(data))
        {
            if (!a.Has('z') && archive == "-") throw new ShellException(Fatal(CommandSpec.Message("tar", "compressedArchive")), 2);
            if (GunzipBytes(data, new ExtractBudget(ctx.Shell.Options, 2), out var error) is not { } plain)
            {
                ctx.Stderr.WriteLine(CommandSpec.Message("tar", "gzipChildFailed", ("error", error)));
                throw new ShellException(CommandSpec.Message("tar", "unrecoverable"), 2);
            }
            data = plain;
        }
        else if (a.Has('z'))
        {
            ctx.Stderr.WriteLine(CommandSpec.Message("tar", "gzipChildFailed", ("error", "not in gzip format")));
            throw new ShellException(CommandSpec.Message("tar", "unrecoverable"), 2);
        }

        var budget = new ExtractBudget(ctx.Shell.Options, 2);
        var made = new HashSet<string>(StringComparer.Ordinal); // symbolic links this extraction created
        bool ThroughMadeLink(string path)
        {
            for (var p = VirtualFileSystem.GetParent(path); p.Length > dir.Length && p != "/"; p = VirtualFileSystem.GetParent(p))
                if (made.Contains(p) && fs.IsSymlink(p)) return true;
            return false;
        }
        var wanted = a.Positional.Select(p => p.TrimEnd('/')).ToList();
        var found = new bool[wanted.Count];
        var announced = new HashSet<string>();
        // a --transform result, with any leading / or ../ it produced stripped (and said once), as GNU tar does: the check
        // against leaving the target directory runs on the name that is written
        string[] Renamed(string name, string key)
        {
            var output = rules.Count > 0 ? TarRename(name, rules) : name;
            var cut = TarPrefix(output);
            if (cut.Length > 0 && announced.Add(key + cut)) ctx.Stderr.WriteLine(CommandSpec.Message("tar", key, ("prefix", cut)));
            return output[cut.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        }
        var info = a.Has('O') ? ctx.Stderr : ctx.Stdout;
        int ugWidth = 19;
        using var reader = new TarReader(new MemoryStream(data));
        for (bool first = true; ; first = false)
        {
            TarEntry? e;
            try { e = reader.GetNextEntry(); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException or ArgumentException)
            {
                Fail(first ? CommandSpec.Message("tar", "notTar") : CommandSpec.Message("tar", "unexpectedEof"));
                break;
            }
            if (e == null) break;
            if (e.EntryType == TarEntryType.GlobalExtendedAttributes) continue; // pax global header (git archive): metadata only
            budget.Entry();
            var name = e.Name;
            var prefix = TarPrefix(name);
            var rel = name[prefix.Length..];
            if (excludes.Count > 0 && TarExcluded(rel, excludes)) continue;
            if (wanted.Count > 0)
            {
                int i = wanted.FindIndex(w => rel.TrimEnd('/') == w || rel.StartsWith(w + "/", StringComparison.Ordinal));
                if (i < 0) continue;
                found[i] = true;
            }
            if (prefix.Length > 0 && announced.Add(prefix)) ctx.Stderr.WriteLine(CommandSpec.Message("tar", "removingLeading", ("prefix", prefix)));
            var linkPrefix = e.EntryType == TarEntryType.HardLink ? TarPrefix(e.LinkName) : "";
            if (linkPrefix.Length > 0 && announced.Add("link:" + linkPrefix)) ctx.Stderr.WriteLine(CommandSpec.Message("tar", "removingLeadingLink", ("prefix", linkPrefix)));
            bool isFile = e.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile;
            if (a.Has('t'))
            {
                if (!a.Has('v')) { ctx.Stdout.WriteLine(name); continue; }
                char type = e.EntryType switch
                {
                    TarEntryType.Directory => 'd', TarEntryType.SymbolicLink => 'l', TarEntryType.HardLink => 'h',
                    TarEntryType.CharacterDevice => 'c', TarEntryType.BlockDevice => 'b', TarEntryType.Fifo => 'p', _ => '-',
                };
                var (uname, gname) = e is PosixTarEntry p ? (p.UserName, p.GroupName) : ("", "");
                var ug = numeric ? $"{e.Uid.ToString(CultureInfo.InvariantCulture)}/{e.Gid.ToString(CultureInfo.InvariantCulture)}" : $"{(uname is "" ? e.Uid.ToString(CultureInfo.InvariantCulture) : uname)}/{(gname is "" ? e.Gid.ToString(CultureInfo.InvariantCulture) : gname)}";
                var size = (isFile ? e.Length : 0).ToString(CultureInfo.InvariantCulture);
                ugWidth = Math.Max(ugWidth, ug.Length + 1 + size.Length); // grows like GNU tar's, so later columns stay aligned
                var link = e.EntryType switch { TarEntryType.SymbolicLink => " -> " + e.LinkName, TarEntryType.HardLink => " link to " + e.LinkName[linkPrefix.Length..], _ => "" };
                ctx.Stdout.WriteLine($"{type}{VirtualFileSystem.FormatMode(false, (int)e.Mode)[1..]} {ug}{size.PadLeft(ugWidth - ug.Length)} {e.ModificationTime.UtcDateTime:yyyy-MM-dd HH:mm} {name}{link}");
                continue;
            }
            if (HasDotDot(prefix)) { Fail(CommandSpec.Message("tar", "memberDotDot", ("name", name))); continue; }
            var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= strip) continue;
            var keep = parts.Skip(strip).ToArray();
            if (rules.Count > 0)
            {
                keep = Renamed(string.Join('/', keep), "removingLeading");
                if (keep.Length == 0) continue;
            }
            var target = VirtualFileSystem.Normalize(dir, string.Join('/', keep));
            try
            {
                if (a.Has('O'))
                {
                    if (!isFile) continue;
                    if (a.Has('v')) info.WriteLine(name);
                    WriteBytes(ctx.Stdout, e.DataStream == null ? [] : budget.Read(e.DataStream));
                    continue;
                }
                if (a.Has('v')) info.WriteLine(name);
                // like GNU tar's placeholders for links: nothing lands under (or links to something under) a symbolic link this
                // archive made, so a crafted member can't use one to write outside the target directory
                if (ThroughMadeLink(target)) throw new ShellException($"{target}: Not a directory");
                if (!fs.IsDirectory(dir)) throw new ShellException($"{target}: No such file or directory"); // the working directory was deleted
                if (e.EntryType != TarEntryType.Directory && fs.Exists(target, follow: false) && !fs.IsDirectory(target, follow: false))
                    fs.Delete(target); // a member replaces what is there, never writes through a link
                switch (e.EntryType)
                {
                    case TarEntryType.Directory:
                        fs.CreateDirectory(target, parents: true); // shortcut: the archived directory mode isn't restored, so read-only dirs can't block their own contents
                        break;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                        var bytes = e.DataStream == null ? [] : budget.Read(e.DataStream);
                        fs.CreateDirectory(VirtualFileSystem.GetParent(target), parents: true);
                        fs.WriteAllBytes(target, bytes);
                        fs.Chmod(target, (int)e.Mode & 0x1FF & ~fs.Umask); // shortcut: no -p, so the umask always applies, as for a non-root user
                        fs.SetModifiedTime(target, e.ModificationTime.UtcDateTime);
                        break;
                    case TarEntryType.HardLink:
                        var linkParts = e.LinkName[linkPrefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
                        var linkTarget = VirtualFileSystem.Normalize(dir, string.Join('/', Renamed(string.Join('/', linkParts.Skip(strip)), "removingLeadingLink")));
                        // only to a file this archive (or the target dir) already holds, never through a link it made
                        if (linkParts.Length <= strip || !fs.Exists(linkTarget, follow: false) || fs.IsDirectory(linkTarget, follow: false) || linkTarget == target || ThroughMadeLink(linkTarget))
                        {
                            Fail(CommandSpec.Message("tar", "cannotHardLink", ("name", name), ("target", string.Join('/', linkParts))));
                            break;
                        }
                        fs.CreateDirectory(VirtualFileSystem.GetParent(target), parents: true);
                        fs.CreateHardLink(linkTarget, target);
                        break;
                    case TarEntryType.SymbolicLink:
                        fs.CreateDirectory(VirtualFileSystem.GetParent(target), parents: true);
                        fs.CreateSymlink(target, TarRename(e.LinkName, rules));
                        made.Add(target);
                        fs.SetModifiedTime(target, e.ModificationTime.UtcDateTime, follow: false);
                        break;
                    default:
                        Fail(CommandSpec.Message("tar", "cannotMknod", ("name", name)));
                        break;
                }
            }
            catch (ShellException ex) when (!ex.Message.Contains("extraction limit")) { Fail(CommandSpec.Message("tar", "cannotOpen", ("file", name), ("errno", Reason(ex)))); }
            catch (EndOfStreamException)
            {
                Fail(CommandSpec.Message("tar", "unexpectedEof"));
                break;
            }
        }
        for (int i = 0; i < wanted.Count; i++)
            if (!found[i]) Fail(CommandSpec.Message("tar", "notFound", ("file", a.Positional[i])));
        return Done();
    }

    static int Gzip(CommandContext ctx)
    {
        var a = Args.Parse(ctx.Args, CommandSpec.Of(ctx.Name));
        bool decompress = ctx.Name != "gzip" || a.Has('d') || a.Has('t') || a.Has('l'), toStdout = ctx.Name == "zcat" || a.Has('c');
        var level = a.Has('1') || a.Has('2') || a.Has('3') ? CompressionLevel.Fastest : a.Has('7') || a.Has('8') || a.Has('9') ? CompressionLevel.SmallestSize : CompressionLevel.Optimal;
        var fs = ctx.Fs;
        var budget = new ExtractBudget(ctx.Shell.Options, 1);
        int code = 0;
        void Error(string msg) { ctx.Stderr.WriteLine("gzip: " + msg); code = 1; }
        void Warn(string msg) { if (!a.Has('q')) ctx.Stderr.WriteLine("gzip: " + msg); if (code == 0) code = 2; }
        void NotGzip(string name) { ctx.Stderr.WriteLine(CommandSpec.Message("gzip", "notGzip", ("file", name))); code = 1; }
        var files = a.Positional.Count == 0 ? ["-"] : a.Positional;
        long totalC = 0, totalU = 0, totalH = 0;
        bool header = false;
        foreach (var arg in files)
        {
            var f = arg;
            var path = ctx.ResolvePath(f);
            if (decompress && f != "-" && !fs.Exists(path) && !f.EndsWith(".gz", StringComparison.Ordinal)) { f += ".gz"; path += ".gz"; }
            byte[] data;
            if (f == "-") data = ByteText.Encode(ctx.StdinText);
            else if (fs.IsDirectory(path)) { Warn(CommandSpec.Message("gzip", "isDirectory", ("file", f))); continue; }
            else if (!fs.IsFile(path)) { Error(CommandSpec.Message("gzip", "fileError", ("file", f), ("errno", fs.WhyMissing(path)))); continue; }
            else data = fs.ReadAllBytes(path);
            var shown = f == "-" ? "stdin" : f;

            if (!decompress)
            {
                var packed = GzipBytes(data, level);
                if (f == "-" || toStdout) { WriteBytes(ctx.Stdout, packed); continue; }
                if (f.EndsWith(".gz", StringComparison.Ordinal)) { if (!a.Has('q')) ctx.Stderr.WriteLine("gzip: " + CommandSpec.Message("gzip", "alreadySuffix", ("file", f))); continue; }
                if (fs.Exists(path + ".gz") && !a.Has('f')) { Warn(CommandSpec.Message("gzip", "alreadyExists", ("file", f + ".gz"))); continue; }
                Replace(path, path + ".gz", packed);
                if (a.Has('v')) ctx.Stderr.WriteLine(CommandSpec.Message("gzip", "ratio", ("file", f), ("ratio", Ratio(packed.Length - GzipOverhead(packed), data.Length)), ("action", a.Has('k') ? "created" : "replaced with"), ("target", f + ".gz")));
                continue;
            }

            string? outName = null;
            if (f != "-" && !toStdout && !a.Has('t') && !a.Has('l'))
            {
                var suffix = new[] { ".gz", "-gz", ".z", "_z", ".tgz" }.FirstOrDefault(x => f.EndsWith(x, StringComparison.Ordinal) && f.Length > x.Length);
                if (suffix == null) { Warn(CommandSpec.Message("gzip", "unknownSuffix", ("file", f))); continue; }
                outName = f[..^suffix.Length] + (suffix == ".tgz" ? ".tar" : "");
            }
            if (!IsGzip(data))
            {
                if (a.Has('f') && toStdout) { WriteBytes(ctx.Stdout, data); continue; } // gzip -dcf passes plain data through
                NotGzip(shown);
                continue;
            }
            if (a.Has('l'))
            {
                long c = data.Length, u = data.Length >= 4 ? BitConverter.ToUInt32(data, data.Length - 4) : 0, h = GzipOverhead(data);
                totalC += c; totalU += u; totalH += h;
                var name = f == "-" ? "stdout" : f.EndsWith(".gz", StringComparison.Ordinal) ? f[..^3] : f;
                if (!header) { header = true; ctx.Stdout.WriteLine("         compressed        uncompressed  ratio uncompressed_name"); }
                ctx.Stdout.WriteLine($"{c,19} {u,19} {Ratio(c - h, u),5}% {name}");
                continue;
            }
            if (GunzipBytes(data, budget, out var error) is not { } plain)
            {
                ctx.Stderr.WriteLine(CommandSpec.Message("gzip", "corrupt", ("file", shown), ("error", error)));
                code = 1;
                continue;
            }
            if (a.Has('t')) continue;
            if (outName == null) { WriteBytes(ctx.Stdout, plain); continue; }
            var outPath = ctx.ResolvePath(outName);
            if (fs.Exists(outPath) && !a.Has('f')) { Warn(CommandSpec.Message("gzip", "alreadyExists", ("file", outName))); continue; }
            Replace(path, outPath, plain);
            if (a.Has('v')) ctx.Stderr.WriteLine(CommandSpec.Message("gzip", "ratio", ("file", f), ("ratio", Ratio(data.Length - GzipOverhead(data), plain.Length)), ("action", "replaced with"), ("target", outName)));
        }
        if (header && files.Count > 1)
            ctx.Stdout.WriteLine($"{totalC,19} {totalU,19} {Ratio(totalC - totalH, totalU),5}% (totals)");
        return code;

        // writes the result next to the source with its mode and time, and removes the source unless -k
        void Replace(string from, string to, byte[] bytes)
        {
            var st = fs.Stat(from);
            fs.WriteAllBytes(to, bytes);
            fs.Chmod(to, st.Mode);
            fs.SetModifiedTime(to, st.Modified);
            if (!a.Has('k')) fs.Delete(from);
        }
    }

    static int Zip(CommandContext ctx)
    {
        var a = Args.Parse(ctx.Args, CommandSpec.Of("zip"));
        var fs = ctx.Fs;
        if (a.Positional.Count == 0) throw new ShellException(CommandSpec.Message("zip", "noArchive"), 12);
        var archive = a.Positional[0];
        if (archive != "-" && !VirtualFileSystem.GetName(archive).Contains('.')) archive += ".zip";
        var archivePath = archive == "-" ? null : ctx.ResolvePath(archive);
        var info = archivePath == null ? ctx.Stderr : ctx.Stdout;
        var ms = new MemoryStream();
        if (archivePath != null && fs.IsFile(archivePath)) ms.Write(fs.ReadAllBytes(archivePath));
        int added = 0;
        ms.Position = 0;
        new ExtractBudget(ctx.Shell.Options, 4).ZipEntries(ms);
        try
        {
            using var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true);
            var walking = new HashSet<string>(StringComparer.Ordinal); // real paths of the directories being added
            void Add(string name, string path)
            {
                if (path == archivePath || !fs.Exists(path)) return; // a dangling link has nothing to store
                ctx.CancellationToken.ThrowIfCancellationRequested(); // following links, -r can walk far more than the tree: the call's time limit
                var st = fs.Stat(path);
                var entryName = string.Join('/', name.Split('/').Where(p => p is not ("" or "."))) + (st.IsDirectory ? "/" : "");
                if (entryName != "/")
                {
                    var data = st.IsDirectory ? [] : fs.ReadAllBytes(path);
                    var deflated = data.Length == 0 ? 0 : DeflatedLength(data);
                    bool stored = deflated == 0 || deflated >= data.Length;
                    var old = zip.GetEntry(entryName);
                    old?.Delete();
                    var entry = zip.CreateEntry(entryName, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    entry.LastWriteTime = st.Modified < new DateTime(1980, 1, 1) ? new DateTime(1980, 1, 1) : st.Modified;
                    entry.ExternalAttributes = ((st.IsDirectory ? 0x4000 : 0x8000) | (st.Mode & 0xFFF)) << 16 | (st.IsDirectory ? 0x10 : 0);
                    using (var s = entry.Open()) s.Write(data);
                    added++;
                    if (!a.Has('q')) info.WriteLine($"{(old != null ? "updating" : "  adding")}: {entryName} ({(stored ? "stored 0" : $"deflated {(200L * (data.Length - deflated) / data.Length + 1) >> 1}")}%)");
                }
                if (st.IsDirectory && (a.Has('r') || entryName == "/"))
                {
                    // zip follows links to directories: one leading back up the tree would never end
                    if (fs.RealPath(path, out _) is not { } real || !walking.Add(real)) return;
                    foreach (var child in fs.List(path)) Add(name.TrimEnd('/') + "/" + child, path.TrimEnd('/') + "/" + child);
                    walking.Remove(real);
                }
            }
            var names = a.Positional.Skip(1).ToList();
            foreach (var f in names)
                if (!fs.Exists(ctx.ResolvePath(f)) && !a.Has('q')) info.WriteLine(CommandSpec.Message("zip", "nameNotMatched", ("file", f)));
            foreach (var f in names)
                if (fs.Exists(ctx.ResolvePath(f))) Add(f, ctx.ResolvePath(f));
        }
        catch (InvalidDataException) { throw new ShellException(CommandSpec.Message("zip", "badStructure", ("archive", archive)), 3); }
        if (added == 0)
        {
            var hint = a.Has('r') ? $"try: zip -r {archive} . -i {string.Join(' ', a.Positional.Skip(1))}" : archive;
            info.WriteLine(CommandSpec.Message("zip", "nothingToDo", ("hint", hint)));
            return 12;
        }
        if (archivePath == null) WriteBytes(ctx.Stdout, ms.ToArray());
        else fs.WriteAllBytes(archivePath, ms.ToArray());
        return 0;

        static long DeflatedLength(byte[] data)
        {
            var ms = new MemoryStream();
            using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data);
            return ms.Length;
        }
    }

    static int Unzip(CommandContext ctx)
    {
        var a = Args.Parse(ctx.Args, CommandSpec.Of("unzip"));
        var fs = ctx.Fs;
        if (a.Positional.Count == 0)
        {
            ctx.Stdout.WriteLine("UnZip 6.00 of 20 April 2009, by Debian. Original by Info-ZIP.\n\nUsage: unzip [-Z] [-opts[modifiers]] file[.zip] [list] [-x xlist] [-d exdir]");
            return 0;
        }
        var name = a.Positional[0];
        var path = new[] { name, name + ".zip", name + ".ZIP" }.Select(ctx.ResolvePath).FirstOrDefault(p => fs.IsFile(p));
        if (path == null)
        {
            ctx.Stderr.WriteLine(CommandSpec.Message("unzip", "cannotFind", ("name", name)));
            return 9;
        }
        bool quiet = a.Has('q'), pipe = a.Has('p');
        var budget = new ExtractBudget(ctx.Shell.Options, 1);
        var data = new MemoryStream(fs.ReadAllBytes(path));
        budget.ZipEntries(data);
        ZipArchive zip;
        try { zip = new ZipArchive(data, ZipArchiveMode.Read); }
        catch (InvalidDataException)
        {
            if (!quiet) ctx.Stdout.WriteLine($"Archive:  {name}");
            ctx.Stderr.WriteLine(CommandSpec.Message("unzip", "noEndSignature"));
            ctx.Stderr.WriteLine(CommandSpec.Message("unzip", "noDirectory", ("name", name)));
            return 9;
        }
        using var _ = zip;
        if (!quiet && !pipe) ctx.Stdout.WriteLine($"Archive:  {name}");
        var patterns = a.Positional.Skip(1).Select(p => Glob.ToRegex(p, slashes: true)).ToList();
        var found = new bool[patterns.Count];
        bool Selected(ZipArchiveEntry e)
        {
            if (patterns.Count == 0) return true;
            bool any = false;
            for (int i = 0; i < patterns.Count; i++) if (patterns[i].IsMatch(e.FullName)) any = found[i] = true;
            return any;
        }
        int code = 0;
        if (a.Has('l'))
        {
            ctx.Stdout.WriteLine("  Length      Date    Time    Name\n---------  ---------- -----   ----");
            long total = 0;
            int count = 0;
            foreach (var e in zip.Entries.Where(Selected))
            {
                budget.Entry();
                total += e.Length;
                count++;
                ctx.Stdout.WriteLine($"{e.Length,9}  {e.LastWriteTime.DateTime:yyyy-MM-dd HH:mm}   {e.FullName}");
            }
            ctx.Stdout.WriteLine($"---------                     -------\n{total,9}                     {count} file{(count == 1 ? "" : "s")}");
        }
        else
        {
            var dest = ctx.ResolvePath(a.Value('d') ?? ".");
            var shownDir = a.Value('d') is { } d ? d.TrimEnd('/') + "/" : "";
            bool none = false;
            foreach (var e in zip.Entries.Where(Selected))
            {
                budget.Entry();
                var entryName = e.FullName;
                bool isDir = entryName.EndsWith('/');
                if (pipe)
                {
                    if (!isDir) WriteBytes(ctx.Stdout, Inflate(e));
                    continue;
                }
                var parts = entryName.Split('/');
                if (parts.Contains("..")) { if (!quiet) ctx.Stdout.WriteLine(CommandSpec.Message("unzip", "skippedDotDot", ("name", entryName))); code = 1; }
                if (entryName.StartsWith('/')) { ctx.Stderr.WriteLine(CommandSpec.Message("unzip", "strippedAbsolute", ("name", entryName))); code = 1; }
                var rel = string.Join('/', parts.Where(p => p is not ("" or "." or "..")));
                if (rel == "") continue;
                var target = VirtualFileSystem.Normalize(dest, rel);
                var shown = shownDir + rel + (isDir ? "/" : "");
                try
                {
                    if (isDir)
                    {
                        if (fs.IsDirectory(target)) continue;
                        fs.CreateDirectory(target, parents: true);
                        if (!quiet) ctx.Stdout.WriteLine($"   creating: {shown}");
                        continue;
                    }
                    if (fs.Exists(target) && !a.Has('o'))
                    {
                        if (a.Has('n') || none) continue;
                        // shortcut: no interactive stdin, so the prompt always reads EOF, as unzip does with </dev/null
                        ctx.Stderr.WriteLine(CommandSpec.Message("unzip", "replacePrompt", ("file", shown)));
                        none = true;
                        code = Math.Max(code, 1);
                        continue;
                    }
                    var bytes = Inflate(e);
                    fs.CreateDirectory(VirtualFileSystem.GetParent(target), parents: true);
                    fs.WriteAllBytes(target, bytes);
                    if ((e.ExternalAttributes >> 16 & 0x1FF) is var mode and not 0) fs.Chmod(target, mode);
                    fs.SetModifiedTime(target, DateTime.SpecifyKind(e.LastWriteTime.DateTime, DateTimeKind.Utc)); // zip and this machine both keep UTC clock digits
                    if (!quiet) ctx.Stdout.WriteLine($"{(e.CompressedLength == e.Length ? " extracting" : "  inflating")}: {shown,-22}  ");
                }
                catch (ShellException ex) when (!ex.Message.Contains("extraction limit"))
                {
                    ctx.Stderr.WriteLine(CommandSpec.Message("unzip", "cannotCreate", ("file", shown), ("errno", Reason(ex))));
                    code = Math.Max(code, 1);
                }
            }
        }
        for (int i = 0; i < patterns.Count; i++)
            if (!found[i]) { if (!a.Has('l')) ctx.Stderr.WriteLine(CommandSpec.Message("unzip", "filenameNotMatched", ("pattern", a.Positional[i + 1]))); code = 11; }
        return code;

        byte[] Inflate(ZipArchiveEntry e)
        {
            try
            {
                using var s = e.Open();
                return budget.Read(s);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                throw new ShellException($"{e.FullName}: {ex.Message}", 2);
            }
        }
    }
}
