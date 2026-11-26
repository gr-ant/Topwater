using System.Formats.Tar;
using System.Text;
using Topwater.Containers;

namespace Topwater.Tests;

/// <summary>Snapshot to Docker build context and rootfs tar to snapshot, without docker.</summary>
public class ContainerTests
{
    record E(string Name, byte[]? Data = null, TarEntryType Type = TarEntryType.RegularFile, int Uid = 1000, int Mode = 420, string? Link = null);

    static E F(string name, string text, int uid = 1000) => new(name, Encoding.UTF8.GetBytes(text), Uid: uid);
    static E D(string name, int uid = 1000) => new(name.TrimEnd('/') + "/", null, TarEntryType.Directory, uid, 493);
    static E L(string name, string target) => new(name, null, TarEntryType.SymbolicLink, Link: target);

    static MemoryStream Tar(params E[] entries)
    {
        var ms = new MemoryStream();
        using (var w = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
            foreach (var e in entries)
            {
                var t = new PaxTarEntry(e.Type, e.Name) { Uid = e.Uid, Mode = (UnixFileMode)e.Mode, ModificationTime = DateTimeOffset.FromUnixTimeSeconds(1700000000) };
                if (e.Link != null) t.LinkName = e.Link;
                if (e.Data != null) t.DataStream = new MemoryStream(e.Data);
                w.WriteEntry(t);
            }
        ms.Position = 0;
        return ms;
    }

    static Dictionary<string, TarEntry> Read(byte[] layer)
    {
        var map = new Dictionary<string, TarEntry>();
        using var r = new TarReader(new MemoryStream(layer));
        while (r.GetNextEntry(copyData: true) is { } e) map[e.Name.TrimEnd('/')] = e;
        return map;
    }

    static string Text(TarEntry e) => new StreamReader(e.DataStream!).ReadToEnd();

    static async Task<ContainerImportResult> Import(MemoryStream tar, ContainerConfig? config = null, string[]? changes = null, ContainerImportOptions? options = null) =>
        await ContainerImport.FromTarAsync(tar, config ?? new ContainerConfig("agent", "/home/agent"), changes, options);

    static Shell Restored(ContainerImportResult r)
    {
        var sh = new Shell(new ShellOptions { User = r.Snapshot.User });
        sh.Restore(r.Snapshot);
        return sh;
    }

    // ---------- export: files ----------

    /// <summary>A fresh machine exports nothing of the user's: only the Topwater bookkeeping files.</summary>
    [Fact]
    public void FreshMachineLayerHoldsOnlyBookkeeping()
    {
        var ctx = new Shell().Snapshot().ToBuildContext();
        Assert.Equal(["etc/topwater", "etc/topwater/paths", "etc/topwater/session.sh"], Read(ctx.Layer).Keys.Order());
    }

    /// <summary>The agent chooses its working directory and variables; a line break in either must not add a Dockerfile instruction.</summary>
    [Fact]
    public async Task LineBreaksCannotInjectDockerfileInstructions()
    {
        var sh = new Shell();
        await sh.RunAsync("mkdir -p $'/tmp/x\\nRUN touch /pwned' && cd $'/tmp/x\\nRUN touch /pwned'; export EVIL=$'a\\nRUN touch /pwned2'; export OK=fine");
        var ctx = sh.Snapshot().ToBuildContext();
        var lines = ctx.Dockerfile.Split('\n');
        Assert.Single(lines, l => l.StartsWith("RUN"));                        // only the generator's own setup step
        Assert.DoesNotContain(lines, l => l.Contains("pwned"));
        Assert.Contains("ENV OK=\"fine\"", lines);                           // ordinary variables still carry over
        Assert.Contains(ctx.Report.NotCarried, i => i.What == "working directory");
        Assert.DoesNotContain(lines, l => l.Contains("EVIL"));              // a multi-line value goes to the quoted session script, never to ENV
    }

    /// <summary>A quote in an account or group name can't break out of the setup script's account list.</summary>
    [Fact]
    public void QuotesInGroupNamesCannotInjectIntoSetup()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/etc/group", sh.Fs.ReadAllText("/etc/group") + "x';touch /pwned;'y:x:1500:\n");
        var setup = sh.Snapshot().ToBuildContext().SetupScript;
        var keep = setup.Split('\n').Single(l => l.StartsWith("keep="));
        var inner = keep["keep='".Length..^1];
        Assert.EndsWith("'", keep);
        Assert.DoesNotContain("'", inner.Replace("'\\''", ""));            // every quote inside is the escaped '\'' form
    }

    /// <summary>useradd and groupadd would read a name starting with "-" as an option: such names are reported and left out.</summary>
    [Fact]
    public void NamesStartingWithADashNeverReachUseraddOrGroupadd()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/etc/passwd", sh.Fs.ReadAllText("/etc/passwd") + "-ou:x:1500:1500::/home/x:/bin/bash\n");
        sh.Fs.WriteAllText("/etc/group", sh.Fs.ReadAllText("/etc/group") + "-og:x:1501:\n");
        sh.Fs.WriteAllText("/srv/f", "x");
        sh.Fs.SetOwner("/srv/f", "-oo");
        var ctx = sh.Snapshot().ToBuildContext();
        Assert.DoesNotMatch("-ou|-og|-oo", ctx.SetupScript);
        Assert.Subset(ctx.Report.NotCarried.Select(i => i.What).ToHashSet(), new HashSet<string> { "account -ou", "account -og", "owner -oo" });
        Assert.Equal(0, Read(ctx.Layer)["srv/f"].Uid);
        foreach (var user in new[] { "-x", "0" })
            Assert.Throws<InvalidOperationException>(() => new Shell(new ShellOptions { User = user }).Snapshot().ToBuildContext());
    }

    /// <summary>Home, shell and comment go to useradd as quoted words; the working directory to WORKDIR escaped.</summary>
    [Fact]
    public async Task PasswdFieldsAndWorkingDirectoryStayData()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/etc/passwd", sh.Fs.ReadAllText("/etc/passwd") + "eve:x:1500:1500:$(touch /p1):/home/e;touch /p2:/bin/sh `touch /p3`\n");
        await sh.RunAsync("mkdir -p '/tmp/$(x) \"y\"' && cd '/tmp/$(x) \"y\"'");
        var ctx = sh.Snapshot().ToBuildContext();
        Assert.Contains("-d '/home/e;touch /p2' -s '/bin/sh `touch /p3`' -c '$(touch /p1)' eve\n", ctx.SetupScript);
        Assert.Contains("WORKDIR \"/tmp/\\$(x) \\\"y\\\"\"\n", ctx.Dockerfile);
    }

    /// <summary>/proc, /dev and the /bin command stubs never go into the layer.</summary>
    [Fact]
    public async Task SimulatedPathsAndStubsAreNotExported()
    {
        var sh = new Shell();
        await sh.RunAsync("echo x > /tmp/t; echo hi > ~/a.txt");
        var names = Read(sh.Snapshot().ToBuildContext().Layer).Keys.ToList();
        Assert.DoesNotContain(names, n => n.StartsWith("proc") || n.StartsWith("dev") || n.StartsWith("usr/bin") || n.StartsWith("bin"));
        Assert.Contains("tmp/t", names);
    }

    /// <summary>A new file lands with its mode, owner ids, mtime and parent directory.</summary>
    [Fact]
    public async Task NewFileKeepsModeOwnerAndMtime()
    {
        var sh = new Shell();
        await sh.RunAsync("echo hi > ~/a.txt; chmod 600 ~/a.txt");
        var secs = long.Parse((await sh.RunAsync("stat -c %Y ~/a.txt")).Stdout.Trim());
        var layer = Read(sh.Snapshot().ToBuildContext().Layer);
        var e = layer["home/agent/a.txt"];
        Assert.Equal((UnixFileMode)384, e.Mode);
        Assert.Equal((1000, 1000), (e.Uid, e.Gid));
        Assert.Equal(secs, e.ModificationTime.ToUnixTimeSeconds());
        Assert.Equal("hi\n", Text(e));
        Assert.Equal(TarEntryType.Directory, layer["home/agent"].EntryType);
    }

    /// <summary>Symbolic links go into the layer as links, other names of a file as hard links to its first.</summary>
    [Fact]
    public async Task LinksAreExportedAsLinks()
    {
        var sh = new Shell();
        await sh.RunAsync("cd; echo hi > a; ln -s a l; ln -s /etc/shadow s; ln a h");
        var layer = Read(sh.Snapshot().ToBuildContext().Layer);
        Assert.Equal((TarEntryType.SymbolicLink, "a"), (layer["home/agent/l"].EntryType, layer["home/agent/l"].LinkName));
        Assert.Equal((TarEntryType.SymbolicLink, "/etc/shadow"), (layer["home/agent/s"].EntryType, layer["home/agent/s"].LinkName));
        Assert.Equal((TarEntryType.HardLink, "home/agent/a"), (layer["home/agent/h"].EntryType, layer["home/agent/h"].LinkName));
    }

    /// <summary>A registered command's stub stays out of the layer and is reported.</summary>
    [Fact]
    public void RegisteredCommandIsReported()
    {
        var sh = new Shell();
        sh.Register("mytool", ctx => 0);
        var ctx = sh.Snapshot().ToBuildContext();
        Assert.DoesNotContain("usr/bin/mytool", Read(ctx.Layer).Keys);
        Assert.Contains(ctx.Report.NotCarried, i => i.What == "registered commands" && i.Why.Contains("mytool"));
    }

    /// <summary>A deleted default file is reported and removed by the setup script.</summary>
    [Fact]
    public async Task DeletedDefaultFileIsRemovedInSetup()
    {
        var sh = new Shell();
        await sh.RunAsync("rm ~/.profile");
        var ctx = sh.Snapshot().ToBuildContext();
        Assert.Contains("/home/agent/.profile", ctx.Report.Deleted);
        Assert.Matches(@"rm -rf -- .*/home/agent/\.profile", ctx.SetupScript);
    }

    /// <summary>Account and Docker-managed files are reported, not copied.</summary>
    [Fact]
    public void AccountAndDockerManagedFilesAreNotCopied()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/etc/passwd", "root:x:0:0::/root:/bin/bash\n");
        sh.Fs.WriteAllText("/etc/hostname", "other\n");
        var ctx = sh.Snapshot().ToBuildContext();
        var names = Read(ctx.Layer).Keys;
        Assert.DoesNotContain("etc/passwd", names);
        Assert.DoesNotContain("etc/hostname", names);
        Assert.Contains(ctx.Report.NotCarried, i => i.What == "/etc/passwd");
        Assert.Contains(ctx.Report.NotCarried, i => i.What == "/etc/hostname");
    }

    /// <summary>A host-written /bin file lands under usr/bin (usrmerge).</summary>
    [Fact]
    public void BinFileIsMergedIntoUsrBin()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/bin/x", "#!/bin/sh\n");
        var layer = Read(sh.Snapshot().ToBuildContext().Layer);
        Assert.Contains("usr/bin/x", layer.Keys);
        Assert.DoesNotContain("bin/x", layer.Keys);
    }

    /// <summary>An owner missing from /etc/passwd gets the next uid, a useradd line and a report item.</summary>
    [Fact]
    public void UnknownOwnerGetsAccount()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/srv/f", "x");
        sh.Fs.SetOwner("/srv/f", "bob");
        var ctx = sh.Snapshot().ToBuildContext();
        Assert.Equal(1001, Read(ctx.Layer)["srv/f"].Uid);
        Assert.Matches(@"useradd .*-u 1001 .*bob", ctx.SetupScript);
        Assert.Contains(ctx.Report.NotCarried, i => i.What == "owner bob");
    }

    // ---------- export: Dockerfile ----------

    /// <summary>FROM, ENV (only changes, escaped), WORKDIR and USER.</summary>
    [Fact]
    public async Task DockerfileHasBaseEnvWorkdirUser()
    {
        var sh = new Shell(new ShellOptions { User = "dev" });
        await sh.RunAsync("""export EXP='a $b "c"'; mkdir -p /tmp/w; cd /tmp/w""");
        var df = sh.Snapshot().ToBuildContext().Dockerfile;
        Assert.Contains("FROM ubuntu:24.04\n", df);
        Assert.Contains("ENV EXP=\"a \\$b \\\"c\\\"\"\n", df);
        Assert.DoesNotContain("ENV PATH", df);
        Assert.Contains("WORKDIR \"/tmp/w\"\n", df);
        Assert.EndsWith("USER dev\n", df);
        Assert.Contains("FROM debian:12\n", sh.Snapshot().ToBuildContext("debian:12").Dockerfile);
    }

    // ---------- export: session script ----------

    /// <summary>The generated rc is valid bash that recreates the state in a fresh shell.</summary>
    [Fact]
    public async Task SessionScriptRecreatesState()
    {
        var sh = new Shell();
        var r = await sh.RunAsync("""
            plain=val; declare -i n=5; arr=(a b c d); unset 'arr[1]'
            declare -A map=(['key two']=v2); nl=$'one\ntwo'
            greet() { echo "hi $1"; }; export -f greet
            alias ll='ls -l'; shopt -s nullglob
            """);
        Assert.Equal(0, r.ExitCode);
        var ctx = sh.Snapshot().ToBuildContext();
        Assert.Contains("unalias -a\n", ctx.SessionScript);
        Assert.Contains("$'one\\ntwo'", ctx.SessionScript);

        var fresh = new Shell();
        var load = await fresh.RunAsync(ctx.SessionScript);
        Assert.Equal(0, load.ExitCode);
        var probe = (await fresh.RunAsync("""
            declare -p plain n arr map nl; type greet; alias ll; shopt nullglob
            """)).ToToolOutput();
        Assert.Contains("plain=\"val\"", probe);
        Assert.Contains("declare -i n=\"5\"", probe);
        Assert.Contains("[0]=\"a\"", probe); Assert.Contains("[3]=\"d\"", probe); Assert.DoesNotContain("[1]=", probe);
        Assert.True(probe.Contains("key two") && probe.Contains("v2"), probe);
        Assert.Contains("greet is a function", probe);
        Assert.Contains("alias ll='ls -l'", probe);
        Assert.Contains("nullglob", probe);
        Assert.Equal("one\ntwo", (await fresh.RunAsync("printf %s \"$nl\"")).Stdout);
    }

    // ---------- import ----------

    /// <summary>Only changed paths and options.Paths are imported; the default machine still works.</summary>
    [Fact]
    public async Task ImportsOnlySelectedPaths()
    {
        var tar = Tar(D("home/agent"), F("home/agent/x.txt", "one two\nthree\n"), F("home/agent/skip.txt", "no"), D("srv"), F("srv/s.txt", "s"), F("opt/o.txt", "o"));
        var r = await Import(tar, changes: ["A /home/agent", "A /home/agent/x.txt"], options: new() { Paths = ["/srv"] });
        var sh = Restored(r);
        Assert.Equal("one two\nthree\n", (await sh.RunAsync("cat ~/x.txt")).Stdout);
        Assert.Equal("2 3 14", string.Join(' ', (await sh.RunAsync("wc < ~/x.txt")).Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim());
        Assert.Equal(0, (await sh.RunAsync("test -f /srv/s.txt")).ExitCode);
        Assert.NotEqual(0, (await sh.RunAsync("test -e ~/skip.txt || test -e /opt/o.txt")).ExitCode);
        Assert.Contains("ls", (await sh.RunAsync("ls /usr/bin")).Stdout);
        Assert.Contains("/home/agent/x.txt", r.Report.Carried);
    }

    /// <summary>Owners come from the tar's /etc/passwd even when it isn't a change; unknown uids stay numbers.</summary>
    [Fact]
    public async Task OwnersMapThroughPasswd()
    {
        var tar = Tar(F("etc/passwd", "carol:x:1234:1234::/home/carol:/bin/bash\n"), F("srv/a", "a", uid: 1234), F("srv/b", "b", uid: 4321));
        var r = await Import(tar, changes: ["A /srv/a", "A /srv/b"]);
        var sh = Restored(r);
        Assert.Equal("carol\n4321\n", (await sh.RunAsync("stat -c '%U' /srv/a /srv/b")).Stdout);
    }

    /// <summary>User, working directory and env come from the config; a numeric user maps via passwd.</summary>
    [Fact]
    public async Task ConfigSetsUserWorkdirEnv()
    {
        var tar = Tar(F("etc/passwd", "carol:x:1234:1234::/home/carol:/bin/bash\n"));
        var r = await Import(tar, new ContainerConfig("1234", "/srv/w", ["FOO=bar", "1BAD=x"]));
        Assert.Equal("carol", r.Snapshot.User);
        var sh = Restored(r);
        Assert.Equal("/srv/w\nbar\n", (await sh.RunAsync("pwd; printenv FOO")).Stdout);
        Assert.Contains(r.Report.NotCarried, i => i.What.Contains("1BAD"));
    }

    /// <summary>Symlinks come back as links, wherever they point (even nowhere, or somewhere the agent can't read); hard links as
    /// copies; an entry below a link (only a crafted archive has one) is reported, never written through it.</summary>
    [Fact]
    public async Task SymlinksComeBackAsLinks()
    {
        var tar = Tar(D("srv"), F("srv/x", "data"), D("srv/d"), F("srv/d/f", "in dir"), L("srv/abs", "/srv/x"), L("srv/rel", "../srv/x"),
            L("srv/dl", "/srv/d"), L("srv/gone", "/nowhere"), L("srv/etc", "/etc"), F("srv/etc/evil", "x"),
            new E("srv/hard", null, TarEntryType.HardLink, Link: "srv/x"));
        var r = await Import(tar, changes: ["A /srv", "A /srv/x", "A /srv/d", "A /srv/d/f", "A /srv/abs", "A /srv/rel", "A /srv/dl", "A /srv/gone", "A /srv/etc",
            "A /srv/etc/evil", "A /srv/hard"]);
        var sh = Restored(r);
        Assert.Equal("datadatain dirdata", (await sh.RunAsync("cat /srv/abs /srv/rel /srv/dl/f /srv/hard")).Stdout);
        Assert.Equal("/srv/x ../srv/x /srv/d /nowhere /etc\n", (await sh.RunAsync("echo $(readlink /srv/abs /srv/rel /srv/dl /srv/gone /srv/etc)")).Stdout);
        Assert.False(sh.Fs.Exists("/etc/evil"));
        Assert.Contains(r.Report.NotCarried, i => i.What == "/srv/etc/evil");
    }

    /// <summary>ELF files are skipped and reported unless IncludeBinaries.</summary>
    [Fact]
    public async Task ElfSkippedUnlessIncluded()
    {
        byte[] elf = [0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3];
        var changes = new[] { "A /srv/prog" };
        var r = await Import(Tar(new E("srv/prog", elf)), changes: changes);
        Assert.DoesNotContain("/srv/prog", r.Report.Carried);
        Assert.Contains(r.Report.NotCarried, i => i.What == "/srv/prog");

        var kept = await Import(Tar(new E("srv/prog", elf)), changes: changes, options: new() { IncludeBinaries = true });
        Assert.Contains("/srv/prog", kept.Report.Carried);
        Assert.Equal(elf, Restored(kept).Fs.ReadAllBytes("/srv/prog"));
    }

    /// <summary>"D" lines delete default files; invalid UTF-8 comes back byte-exact.</summary>
    [Fact]
    public async Task DeletesAndKeepsInvalidUtf8()
    {
        byte[] raw = [0xff, 0xfe, (byte)'a', 0xc3, 0x28, 0];
        var r = await Import(Tar(new E("srv/raw", raw)), changes: ["A /srv/raw", "D /home/agent/.profile"]);
        Assert.Contains("/home/agent/.profile", r.Report.Deleted);
        var sh = Restored(r);
        Assert.NotEqual(0, (await sh.RunAsync("test -e ~/.profile")).ExitCode);
        Assert.Equal(raw, sh.Fs.ReadAllBytes("/srv/raw"));
    }

    /// <summary>Without docker: the exported layer, read back as a container's filesystem, gives the machine back
    /// (files, ~/.bashrc without the hook, the session, history).</summary>
    [Fact]
    public async Task ExportedLayerReadBackGivesTheMachineBack()
    {
        var sh = new Shell();
        await sh.RunAsync("echo hi > ~/a.txt; echo '# mine' >> ~/.bashrc; greet() { echo \"hi $1\"; }; alias ll='ls -l'; x=1; mkdir -p /tmp/w");
        // a line break in a name can't add an entry to /etc/topwater/paths, and the file comes back
        await sh.RunAsync("mkdir -p $'/tmp/x\\nD /home/agent'; touch $'/tmp/x\\nD /home/agent/.profile'; echo nl > $'/tmp/n\\nl'; cd /tmp/w");
        var ctx = sh.Snapshot().ToBuildContext();
        var layer = Read(ctx.Layer);
        var changes = Text(layer["etc/topwater/paths"]).Split('\0'); // docker diff's format, NUL-terminated, which an import also reads from the image
        layer["etc/topwater/paths"].DataStream!.Position = 0;
        var rootfs = new MemoryStream();
        using (var w = new TarWriter(rootfs, TarEntryFormat.Pax, leaveOpen: true))
        {
            w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "etc/passwd") { DataStream = new MemoryStream("root:x:0:0::/root:/bin/bash\nagent:x:1000:1000::/home/agent:/bin/bash\n"u8.ToArray()) });
            foreach (var e in layer.Values) w.WriteEntry(e);
        }
        rootfs.Position = 0;
        var r = await ContainerImport.FromTarAsync(rootfs, new ContainerConfig("agent", "/tmp/w"), changes);

        var back = Restored(r);
        Assert.Equal(sh.Fs.ReadAllText("/home/agent/.bashrc"), back.Fs.ReadAllText("/home/agent/.bashrc"));
        Assert.False(back.Fs.Exists("/etc/topwater"));
        Assert.False(back.Fs.Exists("/home/agent/.bash_history"));
        Assert.Equal(sh.History, back.History);
        Assert.Equal("agent", back.Fs.Stat("/home/agent/a.txt").Owner);
        Assert.True(back.Fs.Exists("/home/agent/.profile"));
        Assert.Equal("nl\n", back.Fs.ReadAllText("/tmp/n\nl"));
        Assert.True(back.Fs.Exists("/tmp/x\nD /home/agent/.profile"));
        const string probe = "pwd; cat ~/a.txt; greet you; alias ll; declare -p x";
        Assert.Equal((await sh.RunAsync(probe)).Stdout, (await back.RunAsync(probe)).Stdout);
    }

    // ---------- hostile input ----------

    /// <summary>Non-canonical entry names throw.</summary>
    [Theory]
    [InlineData("../etc/x")]
    [InlineData("home/../../x")]
    [InlineData("a//b")]
    [InlineData("/abs")]
    [InlineData("a/./b")]
    public async Task NonCanonicalNamesThrow(string name) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(Tar(F(name, "x")), changes: ["A /a/b"]));

    /// <summary>A leading "./" is what some tools write; it is fine.</summary>
    [Fact]
    public async Task LeadingDotSlashIsAccepted()
    {
        var r = await Import(Tar(F("./srv/x", "ok")), changes: ["A /srv/x"]);
        Assert.Contains("/srv/x", r.Report.Carried);
    }

    /// <summary>Selected bytes over MaxFileSystemBytes throw.</summary>
    [Fact]
    public async Task TooManyBytesThrows()
    {
        var limits = new ShellOptions { MaxFileSystemBytes = 1000 };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Import(Tar(new E("srv/big", new byte[2000])), changes: ["A /srv/big"], options: new() { Limits = limits }));
    }

    /// <summary>More selected entries than MaxExtractEntries throw.</summary>
    [Fact]
    public async Task TooManyEntriesThrows()
    {
        var limits = new ShellOptions { MaxExtractEntries = 3 };
        var files = Enumerable.Range(0, 10).Select(i => F($"srv/f{i}", "x")).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Import(Tar(files), options: new() { Paths = ["/srv"], Limits = limits }));
    }

    /// <summary>Garbage is not a tar; a header claiming more data than present is truncated.</summary>
    [Fact]
    public async Task MalformedArchivesThrow()
    {
        var garbage = new MemoryStream(Enumerable.Range(0, 2048).Select(i => (byte)(i * 7 + 13)).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(garbage, changes: ["A /x"]));

        var good = Tar(new E("srv/f", new byte[2000])).ToArray();
        var cut = new MemoryStream(good[..(512 + 100)]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(cut, changes: ["A /srv/f"]));
    }

    /// <summary>Every /home/*/.bash_history is read for the history, imported or not, so each counts against MaxFileSystemBytes.</summary>
    [Fact]
    public async Task HistoriesCountAgainstTheByteLimit()
    {
        var homes = Enumerable.Range(0, 5).Select(i => new E($"home/u{i}/.bash_history", new byte[30_000])).ToArray();
        var limits = new ShellOptions { MaxFileSystemBytes = 100_000 };
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(Tar(homes), options: new() { Limits = limits }));
        await Import(Tar(homes[..2]), options: new() { Limits = limits });
    }

    /// <summary>A time or owner no snapshot can hold is bad container data, reported as such.</summary>
    [Theory]
    [InlineData("mtime", "100000000000000000000")]
    [InlineData("mtime", "-100000000000000")]
    [InlineData("uid", "-5")]
    public async Task BadTimesAndOwnersThrowInvalidData(string key, string value)
    {
        // TarWriter writes its own mtime and uid, so the attribute goes in under a stand-in name of the same length and is renamed after
        var ms = new MemoryStream();
        using (var w = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
            w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "srv/x", new Dictionary<string, string> { [key[..^1] + "X"] = value }) { DataStream = new MemoryStream([1]) });
        var bytes = Encoding.Latin1.GetString(ms.ToArray()).Replace(" " + key[..^1] + "X=", " " + key + "=");
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(new MemoryStream(Encoding.Latin1.GetBytes(bytes)), changes: ["A /srv/x"]));
    }

    /// <summary>A path 2,040 levels deep imports; a working directory past 4,096 characters is refused; a uid of 5,000
    /// digits makes the passwd line ignored and the config user root.</summary>
    [Fact]
    public async Task DeepPathsLongDirectoriesAndHugeIds()
    {
        var deep = string.Concat(Enumerable.Repeat("a/", 2040)) + "f";
        Assert.Contains("/" + deep, (await Import(Tar(F(deep, "x")), changes: ["A /" + deep])).Report.Carried);
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(Tar(), new ContainerConfig("agent", string.Concat(Enumerable.Repeat("/a", 3000)))));
        var huge = new string('9', 5000);
        var r = await Import(Tar(F("etc/passwd", $"big:x:{huge}:1::/home/big:/bin/bash\n"), F("srv/f", "x", uid: 0)), new ContainerConfig(huge), ["A /srv/f"]);
        Assert.Equal("root", r.Snapshot.User);
        Assert.Equal("root", (await Import(Tar(), new ContainerConfig("agent\n"))).Snapshot.User); // a final line break is no name
    }

    /// <summary>Each link to a directory once copied the directory's entries: 20,000 links took minutes.</summary>
    [Fact]
    public async Task ManyDirectoryLinksImportInLinearTime()
    {
        const int n = 20_000;
        var entries = Enumerable.Range(0, n).SelectMany(i => new[] { D($"srv/d{i}"), F($"srv/d{i}/f", "x"), L($"srv/l{i}", $"/srv/d{i}") }).Prepend(D("srv")).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = await Import(Tar(entries), options: new() { Paths = ["/srv"] });
        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 120); // was minutes; now a few seconds
        Assert.Equal("x", (await Restored(r).RunAsync($"cat /srv/l{n - 1}/f")).Stdout);
    }

    /// <summary>A symlink loop comes back as the loop it is: following it fails, nothing hangs.</summary>
    [Fact]
    public async Task SymlinkLoopComesBack()
    {
        var r = await Import(Tar(L("srv/a", "/srv/b"), L("srv/b", "/srv/a")), changes: ["A /srv/a", "A /srv/b"]);
        var run = await Restored(r).RunAsync("readlink /srv/a; cat /srv/a");
        Assert.Equal(("/srv/b\n", "cat: /srv/a: Too many levels of symbolic links\n"), (run.Stdout, run.Stderr));
    }
}
