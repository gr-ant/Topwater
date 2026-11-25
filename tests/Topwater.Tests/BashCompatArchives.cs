using System.Formats.Tar;
using System.IO.Compression;
using Topwater;

namespace Topwater.Tests;

/// <summary>
/// tar, gzip/gunzip/zcat, zip/unzip: the checks that need the host side (real archive bytes). The recorded cases are in
/// tests/corpus/archives.jsonl.
/// </summary>
public class BashCompatArchives
{
    // made by the system tools: tar czf --owner=0 --group=0 --numeric-owner d; gzip -c d/bin.dat; zip -qrX d
    // d/a.txt is "hello\n", d/sub/b.txt "x\ny\n", d/bin.dat the bytes 00 ff fe 80 61 62 63
    const string RealTgz = "H4sIAAAAAAAAA+3WSwrCMBAG4Kx7ipygSSSP8yRGUCgKtoW68+RqWsFFFj4Wk4j+3yZZFDLwM9OJgpGTiXNmOZP8XO5KG62dVsbO39mVkowb+tIYG/vBHzkv8dQ3iiLs9m30A+Ebc8DW6if5uyx/l+6MS8KaHv48f3a9nH1Y164CKonCt8NE2f3v9L/N+1+lA/1fwHbTdYemdhVQSxT9GIh3wM/3Pyelwv5Xwj3/QPoPeD3/dZ6/M5j/RUzNCdMfAAAAAAAAAAAAAADgh9wAKjqjJQAoAAA=";
    const string RealGz = "H4sICHXDk2UAA2Jpbi5kYXQAY/j/ryExKRkAZbSQqQcAAAA=";
    const string RealZip = "UEsDBAoAAAAAAIMYIlgAAAAAAAAAAAAAAAACAAAAZC9QSwMECgAAAAAAgxgiWGW0kKkHAAAABwAAAAkAAABkL2Jpbi5kYXQA//6AYWJjUEsDBAoAAAAAAIMYIlggMDo2BgAAAAYAAAAHAAAAZC9hLnR4dGhlbGxvClBLAwQKAAAAAACDGCJYAAAAAAAAAAAAAAAABgAAAGQvc3ViL1BLAwQKAAAAAACDGCJYGPlwnAQAAAAEAAAACwAAAGQvc3ViL2IudHh0eAp5ClBLAQIeAwoAAAAAAIMYIlgAAAAAAAAAAAAAAAACAAAAAAAAAAAAEAD9QQAAAABkL1BLAQIeAwoAAAAAAIMYIlhltJCpBwAAAAcAAAAJAAAAAAAAAAAAAAC0gSAAAABkL2Jpbi5kYXRQSwECHgMKAAAAAACDGCJYIDA6NgYAAAAGAAAABwAAAAAAAAABAAAAtIFOAAAAZC9hLnR4dFBLAQIeAwoAAAAAAIMYIlgAAAAAAAAAAAAAAAAGAAAAAAAAAAAAEAD9QXkAAABkL3N1Yi9QSwECHgMKAAAAAACDGCJYGPlwnAQAAAAEAAAACwAAAAAAAAABAAAAtIGdAAAAZC9zdWIvYi50eHRQSwUGAAAAAAUABQAJAQAAygAAAAAA";
    static readonly byte[] Bin = [0x00, 0xff, 0xfe, 0x80, 0x61, 0x62, 0x63];

    static async Task<Shell> Fresh(ShellOptions? options = null)
    {
        var sh = new Shell(options);
        await sh.RunAsync("mkdir -p /tmp/w && cd /tmp/w");
        return sh;
    }

    [Fact]
    public async Task ArchivesFromTheRealToolsExtract()
    {
        var sh = await Fresh();
        sh.Fs.WriteAllBytes("/tmp/w/r.tgz", Convert.FromBase64String(RealTgz));
        sh.Fs.WriteAllBytes("/tmp/w/r.gz", Convert.FromBase64String(RealGz));
        sh.Fs.WriteAllBytes("/tmp/w/r.zip", Convert.FromBase64String(RealZip));
        var r = await sh.RunAsync("tar tvzf r.tgz | cut -c1-31,49-; gzip -l r.gz; unzip -l r.zip; mkdir t z; tar xzf r.tgz -C t; unzip -q r.zip -d z; zcat r.gz > g; cat t/d/a.txt z/d/sub/b.txt");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("""
            drwxrwxr-x 0/0               0 d/
            -rw-rw-r-- 0/0               7 d/bin.dat
            -rw-rw-r-- 0/0               6 d/a.txt
            drwxrwxr-x 0/0               0 d/sub/
            -rw-rw-r-- 0/0               4 d/sub/b.txt
                     compressed        uncompressed  ratio uncompressed_name
                             35                   7 -28.6% r
            Archive:  r.zip
              Length      Date    Time    Name
            ---------  ---------- -----   ----
                    0  2024-01-02 03:04   d/
                    7  2024-01-02 03:04   d/bin.dat
                    6  2024-01-02 03:04   d/a.txt
                    0  2024-01-02 03:04   d/sub/
                    4  2024-01-02 03:04   d/sub/b.txt
            ---------                     -------
                   17                     5 files
            hello
            x
            y

            """, r.Stdout);
        foreach (var f in new[] { "t/d/bin.dat", "z/d/bin.dat", "g" }) Assert.Equal(Bin, sh.Fs.ReadAllBytes("/tmp/w/" + f));
        Assert.Equal("-rw-rw-r--", VirtualFileSystem.FormatMode(false, sh.Fs.Stat("/tmp/w/t/d/a.txt").Mode));
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 6), sh.Fs.Stat("/tmp/w/z/d/a.txt").Modified); // zip keeps 2-second local times (03:04:05 rounds up)
    }

    [Fact]
    public async Task ArchivesMadeHereAreStandard()
    {
        var sh = await Fresh();
        sh.Fs.CreateDirectory("/tmp/w/d/sub", parents: true);
        sh.Fs.WriteAllBytes("/tmp/w/d/bin.dat", Bin);
        sh.Fs.WriteAllText("/tmp/w/d/sub/" + new string('n', 120), "long name\n");
        var r = await sh.RunAsync("tar czf a.tgz d; gzip -k d/bin.dat; zip -qr a.zip d");
        Assert.Equal(0, r.ExitCode);

        // read back with .NET's own readers, independent of the commands under test
        using (var tar = new TarReader(new GZipStream(new MemoryStream(sh.Fs.ReadAllBytes("/tmp/w/a.tgz")), CompressionMode.Decompress)))
        {
            var names = new List<string>();
            while (tar.GetNextEntry() is { } e)
            {
                names.Add(e.Name);
                if (e.Name == "d/bin.dat") Assert.Equal(Bin, new BinaryReader(e.DataStream!).ReadBytes(100));
            }
            Assert.Equal(["d/", "d/bin.dat", "d/sub/", "d/sub/" + new string('n', 120)], names);
        }
        using (var gz = new GZipStream(new MemoryStream(sh.Fs.ReadAllBytes("/tmp/w/d/bin.dat.gz")), CompressionMode.Decompress))
            Assert.Equal(Bin, new BinaryReader(gz).ReadBytes(100));
        using var zip = new ZipArchive(new MemoryStream(sh.Fs.ReadAllBytes("/tmp/w/a.zip")));
        Assert.Equal(Bin, new BinaryReader(zip.GetEntry("d/bin.dat")!.Open()).ReadBytes(100));
        Assert.Equal(0x81A4u, (uint)zip.GetEntry("d/bin.dat")!.ExternalAttributes >> 16); // regular file, 0644, for unzip to restore
    }

    [Fact]
    public async Task BinaryDataSurvivesPipesAndRedirects()
    {
        var sh = await Fresh();
        var data = new byte[5000];
        new Random(1).NextBytes(data);
        sh.Fs.WriteAllBytes("/tmp/w/bin", data);
        var r = await sh.RunAsync("""
            gzip -c bin > b.gz; cat b.gz | gunzip > o1; gunzip -c < b.gz > o2
            tar czf - bin | gzip > double.gz; zcat double.gz | gunzip | tar xOf - > o3
            tar cf - bin | tar xOf - > o4; cat bin | gzip | gzip -d > o5; zcat b.gz | head -c 10 > o6
            cat bin > o7; cat bin >> o7; x=$(gzip -c bin | base64 -w0); echo "$x" | base64 -d | gunzip > o8
            """);
        Assert.Equal("", r.Stderr);
        for (int i = 1; i <= 8; i++)
            Assert.Equal(i == 6 ? data[..10] : i == 7 ? [.. data, .. data] : data, sh.Fs.ReadAllBytes($"/tmp/w/o{i}"));
        // text is untouched, and binary written to the terminal shows as U+FFFD rather than as escape characters
        r = await sh.RunAsync("echo 'héllo wörld' | gzip | gunzip; printf 'a\\n' > t; cat t | gzip > t.gz; zcat t.gz; head -c 4 o1 | od -An -tx1 | wc -w; cat b.gz | head -c 3");
        Assert.Equal("héllo wörld\na\n4\n\u001f�\b", r.Stdout);
        Assert.Equal("a\n", sh.Fs.ReadAllText("/tmp/w/t"));
    }

    static byte[] Tar(Action<TarWriter> build, bool gzip = false)
    {
        var ms = new MemoryStream();
        using (var w = new TarWriter(ms, TarEntryFormat.Gnu, leaveOpen: true)) build(w);
        if (!gzip) return ms.ToArray();
        var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true)) z.Write(ms.ToArray());
        return gz.ToArray();
    }

    static void File(TarWriter w, string name, byte[]? data = null) =>
        w.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data ?? "x"u8.ToArray()) });

    static void Link(TarWriter w, TarEntryType type, string name, string target) => w.WriteEntry(new GnuTarEntry(type, name) { LinkName = target });

    [Fact]
    public async Task TarNeverWritesOutsideTheTarget()
    {
        var sh = await Fresh();
        sh.Fs.WriteAllBytes("/tmp/w/evil.tar", Tar(w =>
        {
            File(w, "../up.txt");
            File(w, "/abs.txt");
            File(w, "ok/../../up2.txt");
            File(w, "../../../../../../etc/evil");
            File(w, "good.txt", "good\n"u8.ToArray());
            Link(w, TarEntryType.SymbolicLink, "lnk", "/etc");
            File(w, "lnk/passwd");
            Link(w, TarEntryType.HardLink, "hl", "../outside");
            Link(w, TarEntryType.HardLink, "hl2", "/etc/passwd");
            Link(w, TarEntryType.HardLink, "hl3", "good.txt");
        }));
        sh.Fs.WriteAllText("/tmp/w/outside", "secret\n");
        var r = await sh.RunAsync("mkdir x && tar xf evil.tar -C x; echo rc=$?; cd x && find . | sort; cat hl3");
        Assert.Equal("rc=2\n.\n./abs.txt\n./good.txt\n./hl3\n./lnk\n./lnk/passwd\ngood\n", r.Stdout);
        Assert.Contains("tar: Removing leading `../' from member names", r.Stderr);
        Assert.Contains("tar: ../up.txt: Member name contains '..'", r.Stderr);
        Assert.Contains("tar: ok/../../up2.txt: Member name contains '..'", r.Stderr);
        Assert.Contains("tar: Removing leading `/' from member names", r.Stderr);
        Assert.Contains("tar: lnk: Cannot create symlink to ‘/etc’: Operation not supported", r.Stderr);
        Assert.Contains("tar: hl: Cannot hard link to ‘outside’: No such file or directory", r.Stderr);
        Assert.Contains("tar: hl2: Cannot hard link to ‘etc/passwd’: No such file or directory", r.Stderr);
        foreach (var p in new[] { "/tmp/up.txt", "/tmp/w/up.txt", "/tmp/up2.txt", "/abs.txt", "/etc/evil", "/tmp/w/x/hl" }) Assert.False(sh.Fs.Exists(p), p);
        Assert.Equal("secret\n", sh.Fs.ReadAllText("/tmp/w/outside"));
        Assert.StartsWith("root:", sh.Fs.ReadAllText("/etc/passwd"));
    }

    [Fact]
    public async Task UnzipNeverWritesOutsideTheTarget()
    {
        var sh = await Fresh();
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in new[] { "../up.txt", "/abs.txt", "ok/../../up2.txt", "a/./b/../c.txt", "../../../../etc/evil" })
                using (var s = z.CreateEntry(name).Open()) s.Write("x"u8);
        sh.Fs.WriteAllBytes("/tmp/w/evil.zip", ms.ToArray());
        var r = await sh.RunAsync("mkdir x && cd x && unzip -q ../evil.zip; echo rc=$?; find . | sort");
        Assert.Equal("rc=1\n.\n./a\n./a/b\n./a/b/c.txt\n./abs.txt\n./etc\n./etc/evil\n./ok\n./ok/up2.txt\n./up.txt\n", r.Stdout);
        Assert.Equal("warning:  stripped absolute path spec from /abs.txt\n", r.Stderr);
        foreach (var p in new[] { "/tmp/w/up.txt", "/tmp/up2.txt", "/abs.txt", "/etc/evil" }) Assert.False(sh.Fs.Exists(p), p);
    }

    [Fact]
    public async Task DecompressionBombsStopAtTheLimit()
    {
        var sh = await Fresh(new ShellOptions { MaxExtractBytes = 1 << 20, MaxExtractEntries = 50 });
        var zeros = new byte[2 << 20];
        sh.Fs.WriteAllBytes("/tmp/w/bomb.gz", Gzip(zeros));
        sh.Fs.WriteAllBytes("/tmp/w/bomb.tgz", Tar(w => File(w, "z", zeros), gzip: true));
        sh.Fs.WriteAllBytes("/tmp/w/bomb.tar", Tar(w => File(w, "z", zeros)));
        // a small file copied by many hard links, and many tiny entries
        sh.Fs.WriteAllBytes("/tmp/w/links.tar", Tar(w => { File(w, "f", new byte[300_000]); for (int i = 0; i < 10; i++) Link(w, TarEntryType.HardLink, $"l{i}", "f"); }));
        sh.Fs.WriteAllBytes("/tmp/w/many.tar", Tar(w => { for (int i = 0; i < 60; i++) File(w, $"f{i}"); }));
        var zip = new MemoryStream();
        using (var z = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            using (var s = z.CreateEntry("z").Open()) s.Write(zeros);
        sh.Fs.WriteAllBytes("/tmp/w/bomb.zip", zip.ToArray());

        var limit = "output exceeds the 1048576-byte extraction limit; stopping";
        foreach (var (cmd, code, message) in new[]
        {
            ("gunzip bomb.gz", 1, $"gunzip: {limit}"),
            ("zcat bomb.gz > /dev/null", 1, $"zcat: {limit}"),
            ("cat bomb.gz | gzip -d > out", 1, $"gzip: {limit}"),
            ("tar xzf bomb.tgz -C x", 2, $"tar: {limit}"),
            ("tar xf bomb.tar -C x", 2, $"tar: {limit}"),
            ("tar xOf bomb.tar > /dev/null", 2, $"tar: {limit}"),
            ("tar xf links.tar -C x", 2, $"tar: {limit}"),
            ("tar tf many.tar > /dev/null", 2, "tar: archive exceeds the 50-entry extraction limit; stopping"),
            ("unzip -q bomb.zip -d x", 1, $"unzip: {limit}"),
            ("unzip -p bomb.zip > /dev/null", 1, $"unzip: {limit}"),
        })
        {
            var r = await sh.RunAsync($"rm -rf x; mkdir x; {cmd}");
            Assert.Equal((code, message + "\n"), (r.ExitCode, r.Stderr));
        }
        Assert.True(sh.Fs.IsFile("/tmp/w/bomb.gz"));
        Assert.False(sh.Fs.Exists("/tmp/w/bomb"));
        Assert.False(sh.Fs.Exists("/tmp/w/x/z"));
    }

    static byte[] Gzip(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    [Fact]
    public async Task CorruptGzipFailsInsteadOfReturningPartialData()
    {
        var sh = await Fresh();
        var data = Gzip(System.Text.Encoding.ASCII.GetBytes(string.Join('\n', Enumerable.Range(1, 1000))));
        sh.Fs.WriteAllBytes("/tmp/w/cut.gz", data[..(data.Length / 2)]);
        var flipped = (byte[])data.Clone();
        flipped[^6] ^= 0xFF; // inside the CRC-32
        sh.Fs.WriteAllBytes("/tmp/w/bad.gz", flipped);
        var r = await sh.RunAsync("gunzip cut.gz; echo rc=$?; zcat bad.gz | wc -c; ls");
        Assert.Equal("rc=1\n0\nbad.gz\ncut.gz\n", r.Stdout);
        Assert.Equal("\ngzip: cut.gz: unexpected end of file\n\ngzip: bad.gz: invalid compressed data--format violated\n", r.Stderr);
    }
}
