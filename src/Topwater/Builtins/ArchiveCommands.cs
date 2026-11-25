using System.Globalization;
using System.Formats.Tar;
using System.IO.Compression;

namespace Topwater;

/// <summary>
/// tar, gzip/gunzip/zcat, zip/unzip over the virtual filesystem. Archive bytes cross pipes and redirects as <see cref="ByteText"/>.
/// Archives are untrusted: extraction never leaves the target directory (no `..`, absolute names are made relative, symlinks
/// aren't created since the filesystem has none, hard links copy only files already extracted), and every extraction or
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
            if (--_entries < 0) throw new ShellException($"archive exceeds the {o.MaxExtractEntries}-entry extraction limit; stopping", exitCode);
        }

        public void Charge(long n)
        {
            if ((_bytes -= n) < 0) throw new ShellException($"output exceeds the {o.MaxExtractBytes}-byte extraction limit; stopping", exitCode);
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

    static bool HasDotDot(string path) => path.Split('/').Contains("..");

    static string Reason(ShellException e) => e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) is var i and >= 0 ? i + 2 : 0)..];

    static void RegisterArchiveCommands(Shell shell)
    {
        shell.Register("tar", Tar, "Create, list or extract tar archives (c/x/t, -f, -z, -v, -C, -O, --strip-components)");

        foreach (var name in new[] { "gzip", "gunzip", "zcat" })
            shell.Register(name, Gzip, name switch
            {
                "gzip" => "Compress or decompress files (-c -d -k -f -l -t -v -1..-9)",
                "gunzip" => "Decompress .gz files (gzip -d)",
                _ => "Decompress .gz files to stdout (gzip -dc)",
            });

        shell.Register("zip", Zip, "Package files into a zip archive (-r, -q)");
        shell.Register("unzip", Unzip, "List or extract a zip archive (-l -o -n -d -p -q)");
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
                if (rest.Count == 0) throw new ShellException($"Old option '{c}' requires an argument.\nTry 'tar --help' or 'tar --usage' for more information.", 2);
                args.Add(rest[0]);
                rest.RemoveAt(0);
            }
            args.AddRange(rest);
        }
        const string Usage = "\nTry 'tar --help' or 'tar --usage' for more information.";
        var a = Args.Parse(args, "cxtzvOpo", "fC\u0001", "create=c,extract=x,get=x,list=t,gzip=z,gunzip=z,ungzip=z,verbose=v,to-stdout=O,file=f,directory=C,strip-components=\u0001,preserve-permissions=p,no-same-owner=o");
        var modes = "cxt".Count(a.Has);
        if (modes == 0) throw new ShellException("You must specify one of the '-Acdtrux', '--delete' or '--test-label' options" + Usage, 2);
        if (modes > 1) throw new ShellException("You may not specify more than one '-Acdtrux', '--delete' or  '--test-label' option" + Usage, 2);
        int strip = a.Value('\u0001') is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : 0;
        var archive = a.Value('f') ?? "-";
        var fs = ctx.Fs;
        string Fatal(string msg) => msg + "\ntar: Error is not recoverable: exiting now";
        var dir = ctx.ResolvePath(a.Value('C') ?? ".");
        if (!fs.IsDirectory(dir)) throw new ShellException(Fatal($"{a.Value('C')}: Cannot open: {(fs.Exists(dir) ? "Not a directory" : fs.WhyMissing(dir))}"), 2);
        int code = 0;
        void Fail(string msg) { ctx.Stderr.WriteLine("tar: " + msg); code = 2; }
        int Done()
        {
            if (code != 0) ctx.Stderr.WriteLine("tar: Exiting with failure status due to previous errors");
            return code;
        }

        if (a.Has('c'))
        {
            if (a.Positional.Count == 0) throw new ShellException("Cowardly refusing to create an empty archive" + Usage, 2);
            var archivePath = archive == "-" ? null : ctx.ResolvePath(archive);
            var verbose = archive == "-" ? ctx.Stderr : ctx.Stdout;
            var ms = new MemoryStream();
            bool stripped = false;
            using (var w = new TarWriter(ms, TarEntryFormat.Gnu, leaveOpen: true))
            {
                void Add(string name, string path)
                {
                    if (path == archivePath) return; // the archive itself, as GNU tar skips it
                    var member = name.TrimStart('/');
                    try
                    {
                        var st = fs.Stat(path);
                        if (st.IsDirectory) member = member.TrimEnd('/') + "/";
                        var entry = new GnuTarEntry(st.IsDirectory ? TarEntryType.Directory : TarEntryType.RegularFile, member)
                        {
                            Mode = (UnixFileMode)(st.Mode & 0xFFF), ModificationTime = st.Modified,
                            UserName = st.Owner, GroupName = st.Owner, Uid = st.Owner == "root" ? 0 : 1000, Gid = st.Owner == "root" ? 0 : 1000,
                        };
                        var children = st.IsDirectory ? fs.List(path) : [];
                        if (!st.IsDirectory) entry.DataStream = new MemoryStream(fs.ReadAllBytes(path));
                        if (member != "/")
                        {
                            w.WriteEntry(entry);
                            if (a.Has('v')) verbose.WriteLine(member);
                        }
                        foreach (var child in children) Add(name.TrimEnd('/') + "/" + child, path.TrimEnd('/') + "/" + child);
                    }
                    catch (ShellException e) { Fail($"{name}: Cannot open: {Reason(e)}"); }
                }
                foreach (var f in a.Positional)
                {
                    if (f.StartsWith('/') && !stripped) { stripped = true; ctx.Stderr.WriteLine("tar: Removing leading `/' from member names"); }
                    var path = VirtualFileSystem.Normalize(dir, f);
                    if (!fs.Exists(path)) Fail($"{f}: Cannot stat: {fs.WhyMissing(path)}");
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
            if (!fs.IsFile(path)) throw new ShellException(Fatal($"{archive}: Cannot open: {(fs.IsDirectory(path) ? "Is a directory" : fs.WhyMissing(path))}"), 2);
            data = fs.ReadAllBytes(path);
        }
        if (IsGzip(data))
        {
            if (!a.Has('z') && archive == "-") throw new ShellException(Fatal("Archive is compressed. Use -z option"), 2);
            if (GunzipBytes(data, new ExtractBudget(ctx.Shell.Options, 2), out var error) is not { } plain)
            {
                ctx.Stderr.Write($"\ngzip: stdin: {error}\ntar: Child returned status 1\n");
                throw new ShellException("Error is not recoverable: exiting now", 2);
            }
            data = plain;
        }
        else if (a.Has('z'))
        {
            ctx.Stderr.Write("\ngzip: stdin: not in gzip format\ntar: Child returned status 1\n");
            throw new ShellException("Error is not recoverable: exiting now", 2);
        }

        var budget = new ExtractBudget(ctx.Shell.Options, 2);
        var wanted = a.Positional.Select(p => p.TrimEnd('/')).ToList();
        var found = new bool[wanted.Count];
        var announced = new HashSet<string>();
        var info = a.Has('O') ? ctx.Stderr : ctx.Stdout;
        int ugWidth = 19;
        using var reader = new TarReader(new MemoryStream(data));
        for (bool first = true; ; first = false)
        {
            TarEntry? e;
            try { e = reader.GetNextEntry(); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException or ArgumentException)
            {
                Fail(first ? "This does not look like a tar archive" : "Unexpected EOF in archive");
                break;
            }
            if (e == null) break;
            if (e.EntryType == TarEntryType.GlobalExtendedAttributes) continue; // pax global header (git archive): metadata only
            budget.Entry();
            var name = e.Name;
            var prefix = TarPrefix(name);
            var rel = name[prefix.Length..];
            if (wanted.Count > 0)
            {
                int i = wanted.FindIndex(w => rel.TrimEnd('/') == w || rel.StartsWith(w + "/", StringComparison.Ordinal));
                if (i < 0) continue;
                found[i] = true;
            }
            if (prefix.Length > 0 && announced.Add(prefix)) ctx.Stderr.WriteLine($"tar: Removing leading `{prefix}' from member names");
            var linkPrefix = e.EntryType == TarEntryType.HardLink ? TarPrefix(e.LinkName) : "";
            if (linkPrefix.Length > 0 && announced.Add("link:" + linkPrefix)) ctx.Stderr.WriteLine($"tar: Removing leading `{linkPrefix}' from hard link targets");
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
                var ug = $"{(uname is "" ? e.Uid.ToString(CultureInfo.InvariantCulture) : uname)}/{(gname is "" ? e.Gid.ToString(CultureInfo.InvariantCulture) : gname)}";
                var size = (isFile ? e.Length : 0).ToString(CultureInfo.InvariantCulture);
                ugWidth = Math.Max(ugWidth, ug.Length + 1 + size.Length); // grows like GNU tar's, so later columns stay aligned
                var link = e.EntryType switch { TarEntryType.SymbolicLink => " -> " + e.LinkName, TarEntryType.HardLink => " link to " + e.LinkName[linkPrefix.Length..], _ => "" };
                ctx.Stdout.WriteLine($"{type}{VirtualFileSystem.FormatMode(false, (int)e.Mode)[1..]} {ug}{size.PadLeft(ugWidth - ug.Length)} {e.ModificationTime.UtcDateTime:yyyy-MM-dd HH:mm} {name}{link}");
                continue;
            }
            if (HasDotDot(prefix)) { Fail($"{name}: Member name contains '..'"); continue; }
            var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= strip) continue;
            var target = VirtualFileSystem.Normalize(dir, string.Join('/', parts.Skip(strip)));
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
                switch (e.EntryType)
                {
                    case TarEntryType.Directory:
                        fs.CreateDirectory(target, parents: true); // shortcut: the archived directory mode isn't restored, so read-only dirs can't block their own contents
                        break;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                        var bytes = e.DataStream == null ? [] : budget.Read(e.DataStream);
                        fs.CreateDirectory(VirtualFileSystem.GetParent(target), parents: true);
                        fs.WriteAllBytes(target, bytes);
                        fs.Chmod(target, (int)e.Mode & 0x1FF);
                        fs.SetModifiedTime(target, e.ModificationTime.UtcDateTime);
                        break;
                    case TarEntryType.HardLink:
                        var linkParts = e.LinkName[linkPrefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
                        var linkTarget = VirtualFileSystem.Normalize(dir, string.Join('/', linkParts.Skip(strip)));
                        // the filesystem has no hard links: copy a file this archive (or the target dir) already holds
                        if (linkParts.Length <= strip || !fs.IsFile(linkTarget) || linkTarget == target)
                        {
                            Fail($"{name}: Cannot hard link to ‘{string.Join('/', linkParts)}’: No such file or directory");
                            break;
                        }
                        var copy = fs.ReadAllBytes(linkTarget);
                        budget.Charge(copy.Length);
                        fs.CreateDirectory(VirtualFileSystem.GetParent(target), parents: true);
                        fs.WriteAllBytes(target, copy);
                        fs.Chmod(target, fs.Stat(linkTarget).Mode);
                        break;
                    case TarEntryType.SymbolicLink:
                        Fail($"{name}: Cannot create symlink to ‘{e.LinkName}’: Operation not supported");
                        break;
                    default:
                        Fail($"{name}: Cannot mknod: Operation not permitted");
                        break;
                }
            }
            catch (ShellException ex) when (!ex.Message.Contains("extraction limit")) { Fail($"{name}: Cannot open: {Reason(ex)}"); }
            catch (EndOfStreamException)
            {
                Fail("Unexpected EOF in archive");
                break;
            }
        }
        for (int i = 0; i < wanted.Count; i++)
            if (!found[i]) Fail($"{a.Positional[i]}: Not found in archive");
        return Done();
    }

    static int Gzip(CommandContext ctx)
    {
        var a = Args.Parse(ctx.Args, "cdkflvqtn123456789", "", "stdout=c,to-stdout=c,decompress=d,uncompress=d,keep=k,force=f,list=l,verbose=v,quiet=q,test=t,no-name=n,fast=1,best=9");
        bool decompress = ctx.Name != "gzip" || a.Has('d') || a.Has('t') || a.Has('l'), toStdout = ctx.Name == "zcat" || a.Has('c');
        var level = a.Has('1') || a.Has('2') || a.Has('3') ? CompressionLevel.Fastest : a.Has('7') || a.Has('8') || a.Has('9') ? CompressionLevel.SmallestSize : CompressionLevel.Optimal;
        var fs = ctx.Fs;
        var budget = new ExtractBudget(ctx.Shell.Options, 1);
        int code = 0;
        void Error(string msg) { ctx.Stderr.WriteLine("gzip: " + msg); code = 1; }
        void Warn(string msg) { if (!a.Has('q')) ctx.Stderr.WriteLine("gzip: " + msg); if (code == 0) code = 2; }
        void NotGzip(string name) { ctx.Stderr.WriteLine($"\ngzip: {name}: not in gzip format"); code = 1; }
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
            else if (fs.IsDirectory(path)) { Warn($"{f} is a directory -- ignored"); continue; }
            else if (!fs.IsFile(path)) { Error($"{f}: {fs.WhyMissing(path)}"); continue; }
            else data = fs.ReadAllBytes(path);
            var shown = f == "-" ? "stdin" : f;

            if (!decompress)
            {
                var packed = GzipBytes(data, level);
                if (f == "-" || toStdout) { WriteBytes(ctx.Stdout, packed); continue; }
                if (f.EndsWith(".gz", StringComparison.Ordinal)) { if (!a.Has('q')) ctx.Stderr.WriteLine($"gzip: {f} already has .gz suffix -- unchanged"); continue; }
                if (fs.Exists(path + ".gz") && !a.Has('f')) { Warn($"{f}.gz already exists;\tnot overwritten"); continue; }
                Replace(path, path + ".gz", packed);
                if (a.Has('v')) ctx.Stderr.WriteLine($"{f}:\t{Ratio(packed.Length - GzipOverhead(packed), data.Length)}% -- {(a.Has('k') ? "created" : "replaced with")} {f}.gz");
                continue;
            }

            string? outName = null;
            if (f != "-" && !toStdout && !a.Has('t') && !a.Has('l'))
            {
                var suffix = new[] { ".gz", "-gz", ".z", "_z", ".tgz" }.FirstOrDefault(x => f.EndsWith(x, StringComparison.Ordinal) && f.Length > x.Length);
                if (suffix == null) { Warn($"{f}: unknown suffix -- ignored"); continue; }
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
                ctx.Stderr.WriteLine($"\ngzip: {shown}: {error}");
                code = 1;
                continue;
            }
            if (a.Has('t')) continue;
            if (outName == null) { WriteBytes(ctx.Stdout, plain); continue; }
            var outPath = ctx.ResolvePath(outName);
            if (fs.Exists(outPath) && !a.Has('f')) { Warn($"{outName} already exists;\tnot overwritten"); continue; }
            Replace(path, outPath, plain);
            if (a.Has('v')) ctx.Stderr.WriteLine($"{f}:\t{Ratio(data.Length - GzipOverhead(data), plain.Length)}% -- replaced with {outName}");
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
        var a = Args.Parse(ctx.Args, "rq", "", "recurse-paths=r,quiet=q");
        var fs = ctx.Fs;
        if (a.Positional.Count == 0) throw new ShellException("\nzip error: Nothing to do! (no archive given)", 12);
        var archive = a.Positional[0];
        if (archive != "-" && !VirtualFileSystem.GetName(archive).Contains('.')) archive += ".zip";
        var archivePath = archive == "-" ? null : ctx.ResolvePath(archive);
        var info = archivePath == null ? ctx.Stderr : ctx.Stdout;
        var ms = new MemoryStream();
        if (archivePath != null && fs.IsFile(archivePath)) ms.Write(fs.ReadAllBytes(archivePath));
        int added = 0;
        try
        {
            using var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true);
            void Add(string name, string path)
            {
                if (path == archivePath) return;
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
                    foreach (var child in fs.List(path)) Add(name.TrimEnd('/') + "/" + child, path.TrimEnd('/') + "/" + child);
            }
            var names = a.Positional.Skip(1).ToList();
            foreach (var f in names)
                if (!fs.Exists(ctx.ResolvePath(f)) && !a.Has('q')) info.WriteLine($"\tzip warning: name not matched: {f}");
            foreach (var f in names)
                if (fs.Exists(ctx.ResolvePath(f))) Add(f, ctx.ResolvePath(f));
        }
        catch (InvalidDataException) { throw new ShellException($"\nzip error: Zip file structure invalid ({archive})", 3); }
        if (added == 0)
        {
            var hint = a.Has('r') ? $"try: zip -r {archive} . -i {string.Join(' ', a.Positional.Skip(1))}" : archive;
            info.WriteLine($"\nzip error: Nothing to do! ({hint})");
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
        var a = Args.Parse(ctx.Args, "lopqn", "d");
        var fs = ctx.Fs;
        if (a.Positional.Count == 0)
        {
            ctx.Stdout.WriteLine("UnZip 6.00 of 20 April 2009, by Debian. Original by Info-ZIP.\n\nUsage: unzip [-Z] [-opts[modifiers]] file[.zip] [list] [-x xlist] [-d exdir]");
            return 0;
        }
        var name = a.Positional[0];
        var path = new[] { name, name + ".zip", name + ".ZIP" }.Select(ctx.ResolvePath).FirstOrDefault(fs.IsFile);
        if (path == null)
        {
            ctx.Stderr.WriteLine($"unzip:  cannot find or open {name}, {name}.zip or {name}.ZIP.");
            return 9;
        }
        bool quiet = a.Has('q'), pipe = a.Has('p');
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(fs.ReadAllBytes(path)), ZipArchiveMode.Read); }
        catch (InvalidDataException)
        {
            if (!quiet) ctx.Stdout.WriteLine($"Archive:  {name}");
            ctx.Stderr.WriteLine("  End-of-central-directory signature not found.  Either this file is not\n  a zipfile, or it constitutes one disk of a multi-part archive.  In the\n  latter case the central directory and zipfile comment will be found on\n  the last disk(s) of this archive.");
            ctx.Stderr.WriteLine($"unzip:  cannot find zipfile directory in one of {name} or\n        {name}.zip, and cannot find {name}.ZIP, period.");
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
        var budget = new ExtractBudget(ctx.Shell.Options, 1);
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
                if (parts.Contains("..")) { if (!quiet) ctx.Stdout.WriteLine($"warning:  skipped \"../\" path component(s) in {entryName}"); code = 1; }
                if (entryName.StartsWith('/')) { ctx.Stderr.WriteLine($"warning:  stripped absolute path spec from {entryName}"); code = 1; }
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
                        ctx.Stderr.WriteLine($"replace {shown}? [y]es, [n]o, [A]ll, [N]one, [r]ename:  NULL\n(EOF or read error, treating as \"[N]one\" ...)");
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
                    ctx.Stderr.WriteLine($"error:  cannot create {shown}\n        {Reason(ex)}");
                    code = Math.Max(code, 1);
                }
            }
        }
        for (int i = 0; i < patterns.Count; i++)
            if (!found[i]) { if (!a.Has('l')) ctx.Stderr.WriteLine($"caution: filename not matched:  {a.Positional[i + 1]}"); code = 11; }
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
