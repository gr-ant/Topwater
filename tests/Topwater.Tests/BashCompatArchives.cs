using System.Formats.Tar;
using System.IO.Compression;
using Topwater;

namespace Topwater.Tests;

/// <summary>
/// tar, gzip/gunzip/zcat, zip/unzip. The theory is generated from differential runs: each expected value is what GNU tar 1.35,
/// gzip 1.12, Zip 3.0 and UnZip 6.00 printed. Compressed bytes differ from the real tools', so cases compare listings,
/// messages, exit codes and round-tripped contents, never compressed sizes.
/// </summary>
public class BashCompatArchives
{
    [Theory]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cf t.tar d; tar tf t.tar", "d/\nd/a.txt\nd/sub/\nd/sub/b.txt\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cvf t.tar d", "d/\nd/a.txt\nd/sub/\nd/sub/b.txt\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar czf t.tgz d && tar tzf t.tgz && tar tf t.tgz", "d/\nd/a.txt\nd/sub/\nd/sub/b.txt\nd/\nd/a.txt\nd/sub/\nd/sub/b.txt\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; tar cf - d | tar tf -", "d/\nd/a.txt\nd/sub/\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; tar czf - d | tar tzf -", "d/\nd/a.txt\nd/sub/\n", 0, true)]
    [InlineData("mkdir -p d; echo hello > d/a.txt; tar czf - d | tar tf -; echo \"rc=$?\"", "rc=2\n", 0, false)]
    [InlineData("tar xf nonexist.tar; echo \"rc=$?\"", "rc=2\n", 0, false)]
    [InlineData("tar cf x.tar nosuch; echo \"rc=$?\"; tar tf x.tar; echo \"rc=$?\"", "rc=2\nrc=0\n", 0, false)]
    [InlineData("tar; echo \"rc=$?\"", "rc=2\n", 0, false)]
    [InlineData("tar xf; echo \"rc=$?\"", "rc=2\n", 0, false)]
    [InlineData("mkdir d; echo hello > d/a.txt; tar cf t.tar d; tar tf t.tar d/a.txt nosuch; echo \"rc=$?\"", "d/a.txt\nrc=2\n", 0, false)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cf t.tar d; mkdir o && tar xf t.tar -C o && find o | sort && cat o/d/sub/b.txt", "o\no/d\no/d/a.txt\no/d/sub\no/d/sub/b.txt\nx\ny\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cf t.tar d; mkdir o; tar xvf t.tar -C o", "d/\nd/a.txt\nd/sub/\nd/sub/b.txt\n", 0, true)]
    [InlineData("mkdir d; echo hello > d/a.txt; tar cf t.tar d; tar xOf t.tar d/a.txt", "hello\n", 0, true)]
    [InlineData("mkdir d; echo hello > d/a.txt; tar cf t.tar d; tar xOvf t.tar d/a.txt", "hello\n", 0, false)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cf t.tar d; mkdir o && tar xf t.tar -C o --strip-components=1 && find o | sort", "o\no/a.txt\no/sub\no/sub/b.txt\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cf t.tar d; tar --strip-components=1 -tf t.tar", "d/\nd/a.txt\nd/sub/\nd/sub/b.txt\n", 0, true)]
    [InlineData("mkdir d; echo hello > d/a.txt; tar cf t.tar d; tar xzf t.tar; echo \"rc=$?\"", "rc=2\n", 0, false)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; tar czf t3.tgz -C d . ; tar tzf t3.tgz", "./\n./a.txt\n./sub/\n", 0, true)]
    [InlineData("echo hi > notar; tar tf notar; echo \"rc=$?\"", "rc=2\n", 0, false)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cf t.tar d; tar tf t.tar d/sub", "d/sub/\nd/sub/b.txt\n", 0, true)]
    [InlineData("mkdir d; echo hello > d/a.txt; echo top > t.txt; tar -c -f t.tar d/a.txt t.txt; tar -t -f t.tar", "d/a.txt\nt.txt\n", 0, true)]
    [InlineData("tar cf t.tar /tmp/nosuch-archive-test; echo \"rc=$?\"", "rc=2\n", 0, false)]
    [InlineData("mkdir d; echo hello > d/a.txt; tar cvf - d | wc -c", "10240\n", 0, false)]
    [InlineData("mkdir d; echo hello > d/a.txt; tar czf x.tgz d; mkdir o; tar xzf - -C o < x.tgz; cat o/d/a.txt", "hello\n", 0, true)]
    [InlineData("mkdir d; echo hello > d/a.txt; tar cf - d | gzip > x.gz; zcat x.gz | tar xOf -", "hello\n", 0, true)]
    [InlineData("mkdir d; echo hello > d/a.txt; tar czf x.tgz d; cat x.tgz | tar xzf - -O d/a.txt", "hello\n", 0, true)]
    [InlineData("mkdir d; echo hello > d/a.txt; tar czf x.tgz d; tar xf - < x.tgz; echo \"rc=$?\"", "rc=2\n", 0, false)]
    [InlineData("mkdir d; echo hello > d/a.txt; tar czf - d | gzip -l | tail -1 | awk '{print $2, $4}'", "10240 stdout\n", 0, true)]
    [InlineData("echo AP/+gGFiYw== | base64 -d > bin.dat; tar czf x.tgz bin.dat; mkdir o; tar xzf x.tgz -C o; md5sum bin.dat o/bin.dat | cut -c1-32", "676e0ec24cbc5d824eb54de42140de3d\n676e0ec24cbc5d824eb54de42140de3d\n", 0, true)]
    [InlineData("echo AP/+gGFiYw== | base64 -d > bin.dat; gzip -c bin.dat | gunzip | md5sum; md5sum < bin.dat", "676e0ec24cbc5d824eb54de42140de3d  -\n676e0ec24cbc5d824eb54de42140de3d  -\n", 0, true)]
    [InlineData("echo AP/+gGFiYw== | base64 -d > bin.dat; gzip -c bin.dat > b.gz; gunzip -c b.gz > b2; md5sum < b2; wc -c < b2", "676e0ec24cbc5d824eb54de42140de3d  -\n7\n", 0, true)]
    [InlineData("echo AP/+gGFiYw== | base64 -d > bin.dat; cat bin.dat | gzip | gzip -d | od -An -tx1", " 00 ff fe 80 61 62 63\n", 0, true)]
    [InlineData("echo AP/+gGFiYw== | base64 -d > bin.dat; gzip bin.dat; ls; gunzip bin.dat.gz; ls; od -An -tx1 bin.dat", "bin.dat.gz\nbin.dat\n 00 ff fe 80 61 62 63\n", 0, true)]
    [InlineData("echo hello > f; gzip f; ls; gzip f.gz; echo \"rc=$?\"; gunzip f.gz; ls; cat f", "f.gz\nrc=0\nf\nhello\n", 0, false)]
    [InlineData("echo hello > f; gzip -k f; ls; gzip -k f; echo \"rc=$?\"", "f\nf.gz\nrc=2\n", 0, false)]
    [InlineData("echo hello > f; gzip -k f; gzip -f -k f; echo \"rc=$?\"; ls", "rc=0\nf\nf.gz\n", 0, true)]
    [InlineData("echo hello > f; gzip -c f | gunzip", "hello\n", 0, true)]
    [InlineData("echo hello > f; gzip f; cat f.gz | gunzip", "hello\n", 0, true)]
    [InlineData("echo hello > f; gzip f; zcat f.gz; zcat f.gz f.gz", "hello\nhello\nhello\n", 0, true)]
    [InlineData("echo hello > f; printf abc > e; gzip f; zcat f.gz e; echo \"rc=$?\"", "hello\nrc=1\n", 0, false)]
    [InlineData("printf abc > e; gunzip e; echo \"rc=$?\"", "rc=2\n", 0, false)]
    [InlineData("gunzip nosuch.gz; echo \"rc=$?\"", "rc=1\n", 0, false)]
    [InlineData("zcat nosuch; echo \"rc=$?\"", "rc=1\n", 0, false)]
    [InlineData("gzip nosuch; echo \"rc=$?\"", "rc=1\n", 0, false)]
    [InlineData("echo plain | gunzip; echo \"rc=$?\"", "rc=1\n", 0, false)]
    [InlineData("echo plain | gzip -d; echo \"rc=$?\"", "rc=1\n", 0, false)]
    [InlineData("echo hello > f; gzip -d f; echo \"rc=$?\"", "rc=2\n", 0, false)]
    [InlineData("mkdir dd; gzip dd; echo \"rc=$?\"", "rc=2\n", 0, false)]
    [InlineData("mkdir dd; gzip -q dd; echo \"rc=$?\"", "rc=2\n", 0, true)]
    [InlineData("echo hello > f; gzip -l f; echo \"rc=$?\"", "rc=1\n", 0, false)]
    [InlineData("gzip -l nosuch.gz; echo \"rc=$?\"", "rc=1\n", 0, false)]
    [InlineData("seq 1000 > big.txt; gzip -1 big.txt; gunzip big.txt.gz; md5sum < big.txt", "53d025127ae99ab79e8502aae2d9bea6  -\n", 0, true)]
    [InlineData("seq 1000 > big.txt; gzip -9 -c big.txt | gzip -dc | tail -1", "1000\n", 0, true)]
    [InlineData("echo hello > f; gzip -c f > x.gz; cat x.gz x.gz | gunzip", "hello\nhello\n", 0, true)]
    [InlineData("echo hello > f; gzip -c f > x.gz; gzip -c f >> x.gz; zcat x.gz", "hello\nhello\n", 0, true)]
    [InlineData("echo hello > f; gzip -t f; echo \"rc=$?\"", "rc=1\n", 0, false)]
    [InlineData("echo hello > f; gzip -c f | gzip -t; echo \"rc=$?\"", "rc=0\n", 0, true)]
    [InlineData("echo hello > f; gzip -c f > t.tgz; gunzip t.tgz; ls; cat t.tar", "f\nt.tar\nhello\n", 0, true)]
    [InlineData("echo hello > f; gzip -dcf f", "hello\n", 0, true)]
    [InlineData("echo hello > f; gzip -f f; ls; gzip -d f.gz -k; ls", "f.gz\nf\nf.gz\n", 0, true)]
    [InlineData("seq 1000 > big.txt; gzip -c big.txt | head -c 100 | gunzip > /dev/null; echo \"rc=$?\"", "rc=1\n", 0, false)]
    [InlineData("echo hello > f; gzip -v f 2>&1 | cut -d: -f1", "f\n", 0, true)]
    [InlineData("gzip -dc < nosuch; echo \"rc=$?\"", "rc=1\n", 0, false)]
    [InlineData("echo hi | gzip | gunzip", "hi\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; echo top > t.txt; zip a.zip t.txt", "  adding: t.txt (stored 0%)\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; zip -r a.zip d", "  adding: d/ (stored 0%)\n  adding: d/a.txt (stored 0%)\n  adding: d/sub/ (stored 0%)\n  adding: d/sub/b.txt (stored 0%)\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; echo top > t.txt; zip a.zip t.txt; zip -r a.zip d; zip a.zip t.txt", "  adding: t.txt (stored 0%)\n  adding: d/ (stored 0%)\n  adding: d/a.txt (stored 0%)\n  adding: d/sub/ (stored 0%)\n  adding: d/sub/b.txt (stored 0%)\nupdating: t.txt (stored 0%)\n", 0, true)]
    [InlineData("echo top > t.txt; zip -q b.zip t.txt nosuch; echo \"rc=$?\"", "rc=0\n", 0, true)]
    [InlineData("echo top > t.txt; zip b.zip t.txt nosuch; echo \"rc=$?\"", "\tzip warning: name not matched: nosuch\n  adding: t.txt (stored 0%)\nrc=0\n", 0, true)]
    [InlineData("mkdir d; zip c.zip d", "  adding: d/ (stored 0%)\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; echo top > t.txt; zip -qr a.zip t.txt d; unzip -l a.zip | cut -c1-9,30-", "Archive: \n  Length  Name\n--------- ----\n        4 t.txt\n        0 d/\n        6 d/a.txt\n        0 d/sub/\n        4 d/sub/b.txt\n--------- -------\n       14 5 files\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; echo top > t.txt; zip -qr a.zip t.txt d; mkdir o && unzip a.zip -d o; cat o/d/a.txt", "Archive:  a.zip\n extracting: o/t.txt                 \n   creating: o/d/\n extracting: o/d/a.txt               \n   creating: o/d/sub/\n extracting: o/d/sub/b.txt           \nhello\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; echo top > t.txt; zip -qr a.zip t.txt d; mkdir o && unzip -q a.zip -d o; unzip -o a.zip -d o", "Archive:  a.zip\n extracting: o/t.txt                 \n extracting: o/d/a.txt               \n extracting: o/d/sub/b.txt           \n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; echo top > t.txt; zip -qr a.zip t.txt d; unzip -q -o a.zip -d o; find o | sort", "o\no/d\no/d/a.txt\no/d/sub\no/d/sub/b.txt\no/t.txt\n", 0, true)]
    [InlineData("mkdir -p d; echo hello > d/a.txt; echo top > t.txt; zip -qr a.zip t.txt d; unzip -p a.zip d/a.txt t.txt", "top\nhello\n", 0, true)]
    [InlineData("unzip nosuch.zip; echo \"rc=$?\"", "rc=9\n", 0, false)]
    [InlineData("echo top > t.txt; zip -q a.zip t.txt; unzip -l a.zip nosuch; echo \"rc=$?\"", "Archive:  a.zip\n  Length      Date    Time    Name\n---------  ---------- -----   ----\n---------                     -------\n        0                     0 files\nrc=11\n", 0, true)]
    [InlineData("zip -r e.zip nosuch; echo \"rc=$?\"", "\tzip warning: name not matched: nosuch\n\nzip error: Nothing to do! (try: zip -r e.zip . -i nosuch)\nrc=12\n", 0, true)]
    [InlineData("zip y.zip nosuch; echo \"rc=$?\"", "\tzip warning: name not matched: nosuch\n\nzip error: Nothing to do! (y.zip)\nrc=12\n", 0, true)]
    [InlineData("mkdir -p d; echo hello > d/a.txt; echo top > t.txt; zip -qr a.zip t.txt d; mkdir o; cd o && unzip -n ../a.zip; echo \"rc=$?\"", "Archive:  ../a.zip\n extracting: t.txt                   \n   creating: d/\n extracting: d/a.txt                 \nrc=0\n", 0, true)]
    [InlineData("mkdir -p d; echo hello > d/a.txt; echo top > t.txt; zip -qr a.zip t.txt d; mkdir o; cd o && unzip -q ../a.zip; unzip ../a.zip t.txt nosuch < /dev/null; echo \"rc=$?\"", "Archive:  ../a.zip\nrc=11\n", 0, false)]
    [InlineData("echo notzip > bad.zip; unzip -l bad.zip; echo \"rc=$?\"", "Archive:  bad.zip\nrc=9\n", 0, false)]
    [InlineData("seq 1000 > big.txt; echo AP/+gGFiYw== | base64 -d > bin.dat; zip big.zip big.txt bin.dat; rm big.txt bin.dat; unzip big.zip; md5sum big.txt; od -An -tx1 bin.dat", "  adding: big.txt (deflated 53%)\n  adding: bin.dat (stored 0%)\nArchive:  big.zip\n  inflating: big.txt                 \n extracting: bin.dat                 \n53d025127ae99ab79e8502aae2d9bea6  big.txt\n 00 ff fe 80 61 62 63\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; cd d && zip -r ../g.zip . ; unzip -l ../g.zip | cut -c1-9,30-", "  adding: a.txt (stored 0%)\n  adding: sub/ (stored 0%)\n  adding: sub/b.txt (stored 0%)\nArchive: \n  Length  Name\n--------- ----\n        6 a.txt\n        0 sub/\n        4 sub/b.txt\n--------- -------\n       10 3 files\n", 0, true)]
    [InlineData("mkdir -p d; echo hello > d/a.txt; echo top > t.txt; zip -qr a.zip d t.txt; unzip -d o3 a.zip t.txt; ls o3", "Archive:  a.zip\n extracting: o3/t.txt                \nt.txt\n", 0, true)]
    [InlineData("mkdir -p d; echo hello > d/a.txt; echo top > t.txt; zip -qr - d t.txt > s.zip; unzip -l s.zip | cut -c1-9,30-", "Archive: \n  Length  Name\n--------- ----\n        0 d/\n        6 d/a.txt\n        4 t.txt\n--------- -------\n       10 3 files\n", 0, true)]
    [InlineData("echo top > t.txt; zip a t.txt; ls", "  adding: t.txt (stored 0%)\na.zip\nt.txt\n", 0, true)]
    [InlineData("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; zip -qr a.zip d; unzip -l a.zip \"d/*.txt\" | cut -c1-9,30-", "Archive: \n  Length  Name\n--------- ----\n        6 d/a.txt\n        4 d/sub/b.txt\n--------- -------\n       10 2 files\n", 0, true)]
    public Task MatchesRealTools(string script, string stdout, int exitCode, bool quiet) => BashCompat.Check(script, stdout, exitCode, quiet);

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
