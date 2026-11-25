using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace Topwater.Tests;

/// <summary>Snapshot, Restore, Fork, Save and Load: every kind of machine state survives, and copies share nothing mutable.</summary>
public class SnapshotTests(ITestOutputHelper output)
{
    static readonly byte[] BinaryBytes = [0, 1, 2, 0x7f, 0x80, 0xfe, 0xff, (byte)'\n', 0xc3];

    /// <summary>A shell holding one of everything a snapshot captures.</summary>
    static async Task<Shell> Setup()
    {
        var sh = new Shell();
        sh.Fs.WriteAllBytes("/home/agent/blob.bin", BinaryBytes);
        sh.Fs.CreateDirectory("/data", parents: true);
        sh.Fs.WriteAllText("/data/report.txt", "héllo wörld\nline 2\n");
        sh.Fs.SetOwner("/data", "bob", recursive: true);
        var r = await sh.RunAsync("""
            mkdir -p proj/src && cd proj
            printf 'hello\n' > a.txt; chmod 600 a.txt; printf 'x' > src/run.sh; chmod 755 src/run.sh
            echo one >> log.txt; echo two >> log.txt
            touch $'odd\nname' 'sp ace'
            declare -i n=5; readonly R=fixed; export EXP=exported; declare -l low=ABC; declare -u up=abc
            target=val; declare -n ref=target; plain='multi
            line'
            arr=(one 'two words' three); unset 'arr[1]'; arr+=(four)
            declare -A map=([k1]=v1 ['key two']=v2)
            declare -ra roarr=(x y)
            greet() { echo "hi $1 from $(pwd)"; }; export -f greet
            count() { local i; for ((i=0; i<$1; i++)); do printf '%s,' $i; done; echo; }
            alias ll='ls -l' gs='git status'
            set -- p1 'p 2' p3
            shopt -s nullglob globstar
            trap ':' ERR; trap 'echo int' INT
            pushd /tmp >/dev/null; pushd /home/agent/proj >/dev/null
            echo bg & wait
            false
            """);
        Assert.Equal(1, r.ExitCode);
        return sh;
    }

    /// <summary>What the agent can observe about the session (run last: it adds to the history).</summary>
    static async Task<string> Probe(Shell sh) => (await sh.RunAsync("""
        echo "status=$? args=$# [$1] [$2] [$3] pwd=$PWD"
        declare -p n R EXP low up ref target plain arr map roarr
        n+=2; echo "n=$n ref=$ref"; up=xyz; echo "up=$up"
        greet bob; count 3; bash -c 'echo "child sees EXP=$EXP"; greet child'
        declare -f count
        alias; dirs; shopt nullglob globstar dotglob; trap -p ERR INT
        echo "${!map[@]}" "${map[key two]}" "${!arr[@]}"; arr+=(five); echo "${!arr[@]}"
        ls -l a.txt src/run.sh | cut -c1-10; cat log.txt; ls /home/agent/proj
        R=changed
        """)).ToToolOutput();

    /// <summary>Every path's metadata and exact bytes, as the host sees them.</summary>
    static List<string> Files(Shell sh)
    {
        var list = new List<string>();
        void Walk(string path)
        {
            var st = sh.Fs.Stat(path);
            var t = st.Modified.Kind == DateTimeKind.Local ? st.Modified.ToUniversalTime() : st.Modified;
            list.Add($"{path} {st.IsDirectory} {st.Size} {Convert.ToString(st.Mode, 8)} {st.Owner} {t.Ticks}"
                + (st.IsDirectory ? "" : " " + Convert.ToHexString(sh.Fs.ReadAllBytes(path))));
            if (st.IsDirectory) foreach (var name in sh.Fs.List(path)) Walk(path == "/" ? "/" + name : path + "/" + name);
        }
        Walk("/");
        return list;
    }

    /// <summary>Changes every kind of state, so a restore has something to undo.</summary>
    static async Task Mess(Shell sh)
    {
        sh.Fs.WriteAllBytes("/home/agent/blob.bin", [9, 9]);
        sh.Fs.Chmod("/data/report.txt", 0);
        await sh.RunAsync("""
            cd /; rm -rf /home/agent/proj; echo new > /tmp/new.txt; echo three >> /tmp/log.txt
            n=99; unset low target; unalias ll; unset -f greet; arr[0]=CHANGED; map[k1]=CHANGED; map[k3]=new
            set --; shopt -u nullglob; trap - ERR INT; popd; popd; alias extra=x
            """);
    }

    static byte[] Save(Shell sh)
    {
        var ms = new MemoryStream();
        sh.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A stream that can only be read forward, like a network or a pipe.</summary>
    sealed class ForwardOnly(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("restore twice")]
    [InlineData("fork")]
    [InlineData("save and load")]
    [InlineData("load from a forward-only stream")]
    [InlineData("load into a configured shell")]
    public async Task EveryKindOfStateSurvives(string how)
    {
        var expected = await Probe(await Setup()); // a twin, so probing doesn't touch the shell under test
        var sh = await Setup();
        var files = Files(sh);
        var history = sh.History.ToList();
        var env = new Dictionary<string, string>(sh.Environment);
        Shell copy;
        switch (how)
        {
            case "restore":
                var snap = sh.Snapshot();
                await Mess(sh);
                sh.Restore(snap);
                copy = sh;
                break;
            case "restore twice":
                var once = sh.Snapshot();
                sh.Restore(once);
                await Mess(sh);
                sh.Restore(once);
                copy = sh;
                break;
            case "fork":
                copy = sh.Fork();
                await Mess(sh);
                break;
            case "save and load":
                copy = Shell.Load(new MemoryStream(Save(sh)));
                break;
            case "load from a forward-only stream":
                copy = Shell.Load(new ForwardOnly(Save(sh)));
                break;
            default:
                copy = new Shell();
                copy.Restore(ShellSnapshot.Load(new MemoryStream(Save(sh)), copy.Options));
                break;
        }
        Assert.Equal(files, Files(copy));
        Assert.Equal(history, copy.History);
        Assert.Equal(env, copy.Environment);
        Assert.Equal(1, copy.LastExitCode);
        Assert.Equal("/home/agent/proj", copy.Cwd);
        var probe = await Probe(copy);
        Assert.Equal(expected, probe);
        // and the probe saw real values, not two matching blanks
        foreach (var part in new[] { "status=1 args=3 [p1] [p 2] [p3] pwd=/home/agent/proj", "declare -i n=\"5\"", "declare -r R=\"fixed\"", "declare -x EXP=\"exported\"",
            "declare -l low=\"abc\"", "declare -n ref=\"target\"", "declare -a arr=([0]=\"one\" [2]=\"three\" [3]=\"four\")", "n=7 ref=val", "up=XYZ", ": R: readonly variable",
            "hi bob from /home/agent/proj", "0,1,2,", "child sees EXP=exported", "hi child from", "alias gs='git status'", "alias ll='ls -l'", "~/proj /tmp",
            "nullglob       \ton", "globstar       \ton", "dotglob        \toff", "trap -- ':' ERR", "trap -- 'echo int' INT", "key two", "v2", "0 2 3 4", "-rw-------", "-rwxr-xr-x", "one\ntwo\n" })
            Assert.Contains(part, probe);
        Assert.Equal(BinaryBytes, copy.Fs.ReadAllBytes("/home/agent/blob.bin"));
        Assert.Equal("bob", copy.Fs.Stat("/data/report.txt").Owner);
        Assert.Contains("odd\nname", copy.Fs.List("/home/agent/proj"));
    }

    [Fact]
    public async Task ASnapshotNeverSeesLaterChanges()
    {
        var sh = new Shell();
        await sh.RunAsync("a=(1 2 3); declare -A m=([k]=v); s=before; echo one >> f.txt; printf 'bin\\x00\\x01' > b.bin");
        var snap = sh.Snapshot();
        // array writes, appends to a file being built, binary rewrites, mode and directory changes
        await sh.RunAsync("a[0]=X; a+=(4); m[k]=X; s=after; echo two >> f.txt; printf 'zz' > b.bin; chmod 000 f.txt; mkdir d; cd d");
        sh.Fs.WriteAllBytes("/home/agent/b.bin", [7]);
        var later = sh.Fork();
        sh.Restore(snap);
        Assert.Equal("1 2 3|v|before|one|/home/agent\n", (await sh.RunAsync("echo \"${a[*]}|${m[k]}|$s|$(cat f.txt)|$PWD\"")).Stdout);
        Assert.Equal([.. "bin"u8, 0, 1], sh.Fs.ReadAllBytes("/home/agent/b.bin"));
        Assert.Equal("-rw-r--r--", (await sh.RunAsync("ls -l f.txt | cut -c1-10")).Stdout.Trim());
        // restoring again after more changes gives the same machine
        await sh.RunAsync("a[1]=Y; echo three >> f.txt; rm b.bin");
        sh.Restore(snap);
        Assert.Equal("1 2 3|one\n", (await sh.RunAsync("echo \"${a[*]}|$(cat f.txt)\"")).Stdout);
        Assert.True(sh.Fs.Exists("/home/agent/b.bin"));
        // the fork taken before the restore kept the later state
        Assert.Equal("X 2 3 4|after\n", (await later.RunAsync("echo \"${a[*]}|$s\"")).Stdout);
    }

    [Fact]
    public async Task AForkAndItsParentShareNothingMutable()
    {
        var parent = new Shell();
        await parent.RunAsync("a=(1 2); declare -A m=([k]=v); x=1; f() { echo f1; }; alias q=echo; echo base > file; set -- p; pushd /tmp >/dev/null");
        var fork = parent.Fork();
        await parent.RunAsync("a[0]=P; m[k]=P; x=P; f() { echo fP; }; unalias q; echo P >> ~/file; set -- P; popd; mkdir ~/pd; chmod 700 ~/file");
        Assert.Equal("1 2|v|1|f1|q=echo|base|p|/tmp ~|no pd|-rw-r--r--\n", (await fork.RunAsync(
            "echo \"${a[*]}|${m[k]}|$x|$(f)|$(alias q | cut -d' ' -f2 | tr -d \"'\")|$(cat ~/file)|$1|$(dirs)|$( [ -e ~/pd ] && echo pd || echo no pd)|$(ls -l ~/file | cut -c1-10)\"")).Stdout);
        await fork.RunAsync("a[1]=F; m[n]=F; y=F; echo F > ~/file; cd /; f() { echo fF; }");
        Assert.Equal("P 2|P||fP\n", (await parent.RunAsync("echo \"${a[*]}|${m[k]}${m[n]}|$y|$(f)\"")).Stdout);
        Assert.Equal("base\nP\n", parent.Fs.ReadAllText("/home/agent/file"));
        Assert.Equal("F\n", fork.Fs.ReadAllText("/home/agent/file"));
        Assert.Equal("/home/agent", parent.Cwd);
        Assert.Equal("/", fork.Cwd);
        Assert.NotSame(parent.Fs, fork.Fs);
        Assert.NotSame(parent.Environment, fork.Environment);
    }

    [Fact]
    public async Task ForkSharesRegisteredHandlers()
    {
        int calls = 0;
        var parent = new Shell().Register("tick", _ => ++calls);
        var fork = parent.Fork();
        await parent.RunAsync("tick");
        await fork.RunAsync("tick");
        Assert.Equal(2, calls); // one handler, so its state (here a counter; for an MCP mount, its connection) is shared
        Assert.True(fork.Fs.IsFile("/usr/bin/tick"));
        Assert.Equal("tick", fork.Commands["tick"].Name);
    }

    [Fact]
    public async Task RestoreKeepsThisShellsRegistrations()
    {
        var other = new Shell().Register("theirs", ctx => { ctx.Stdout.WriteLine("theirs"); return 0; });
        await other.RunAsync("echo data > d.txt");
        var mine = new Shell().Register("mine", ctx => { ctx.Stdout.WriteLine("mine ran"); return 0; });
        mine.Restore(other.Snapshot());
        Assert.Equal("data\nmine ran\n", (await mine.RunAsync("cat d.txt; mine")).Stdout);
        Assert.Equal(127, (await mine.RunAsync("theirs")).ExitCode);
        Assert.True(mine.Fs.IsFile("/usr/bin/mine"));
        Assert.False(mine.Fs.Exists("/usr/bin/theirs"));
        Assert.Contains("mine", (await mine.RunAsync("help")).Stdout);
    }

    [Fact]
    public async Task LoadGivesAShellWithoutHostRegistrationsToRegisterAgain()
    {
        var sh = new Shell().Register("orders", ctx => { ctx.Stdout.WriteLine("3 orders"); return 0; });
        var bytes = Save(sh);
        var loaded = Shell.Load(new MemoryStream(bytes));
        Assert.Equal(127, (await loaded.RunAsync("orders")).ExitCode);
        Assert.False(loaded.Fs.Exists("/usr/bin/orders"));
        loaded.Register("orders", ctx => { ctx.Stdout.WriteLine("3 orders"); return 0; });
        Assert.Equal("3 orders\n", (await loaded.RunAsync("orders")).Stdout);
        // or configure first and restore into it
        var configured = new Shell().Register("orders", ctx => { ctx.Stdout.WriteLine("configured"); return 0; });
        configured.Restore(ShellSnapshot.Load(new MemoryStream(bytes), configured.Options));
        Assert.Equal("configured\n", (await configured.RunAsync("orders")).Stdout);
    }

    [Fact]
    public async Task LoadUsesTheSnapshotsUserAndHostname()
    {
        var sh = new Shell(new ShellOptions { User = "dev", Hostname = "box" });
        await sh.RunAsync("echo hi > note");
        var snap = ShellSnapshot.Load(new MemoryStream(Save(sh)));
        Assert.Equal(("dev", "box"), (snap.User, snap.Hostname));
        var loaded = Shell.Load(new MemoryStream(Save(sh)));
        Assert.Equal("dev@box /home/dev hi\n", (await loaded.RunAsync("echo \"$(whoami)@$(hostname) $PWD $(cat note)\"")).Stdout);
    }

    [Fact]
    public void TheSavedFormatIsAZipWithAVersionedManifestAndDeduplicatedBlobs()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/home/agent/a", "same");
        sh.Fs.WriteAllText("/home/agent/b", "same");
        using var zip = new ZipArchive(new MemoryStream(Save(sh)));
        var manifest = JsonNode.Parse(zip.GetEntry("manifest.json")!.Open())!;
        Assert.Equal("topwater-snapshot", (string?)manifest["format"]);
        Assert.Equal(1, (int)manifest["version"]!); // no links: written as version 1, which older Topwater reads
        var files = manifest["files"]!.AsArray();
        var a = files.Single(f => (string?)f!["path"] == "/home/agent/a")!;
        Assert.Equal("0644", (string?)a["mode"]);
        Assert.Equal("agent", (string?)a["owner"]);
        Assert.EndsWith("Z", (string?)a["mtime"]);
        Assert.Equal((string?)a["blob"], (string?)files.Single(f => (string?)f!["path"] == "/home/agent/b")!["blob"]);
        Assert.Equal("/home/agent", (string?)manifest["cwd"]);
        Assert.Contains(manifest["variables"]!.AsArray(), v => (string?)v!["name"] == "HOME" && (bool?)v["exported"] == true);
        // all the /usr/bin stubs are one blob
        Assert.True(zip.Entries.Count(e => e.FullName.StartsWith("blobs/")) < files.Count / 2);
    }

    /// <summary>The ports share the saved format: snapshots/from-python.zip (saved by the Python port, and
    /// from-csharp.zip, which the Python tests load) give the same machine. Both were made with the setup below.</summary>
    [Theory]
    [InlineData("from-python.zip")]
    [InlineData("from-csharp.zip")]
    public async Task ASnapshotSavedByAnotherPortLoads(string file)
    {
        // setup: mkdir -p proj/src && cd proj; printf 'hello\n' > a.txt; chmod 600 a.txt; echo AAH+/wo= | base64 -d > bin.dat;
        // echo 'héllo wörld' > u.txt; declare -i n=5; export EXP='exp "q" $x'; plain=$'multi\nline'; arr=(one 'two words' three);
        // unset 'arr[1]'; declare -A map=([k1]=v1 ['key two']=v2); greet() { echo "hi $1"; }; export -f greet; alias ll='ls -l';
        // set -- p1 'p 2'; shopt -s nullglob; trap 'echo int' INT
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "../../../snapshots", file));
        var sh = Shell.Load(stream);
        var r = await sh.RunAsync("echo \"$PWD $n $EXP [$plain] ${!arr[@]} ${arr[2]} ${map[key two]} $1|$2\"; greet you; alias ll; shopt nullglob; " +
            "trap -p INT; ls -l a.txt | cut -c1-10; od -An -tx1 bin.dat; cat u.txt; bash -c 'greet child'; declare -p n arr; whoami");
        Assert.Equal("/home/agent/proj 5 exp \"q\" $x [multi\nline] 0 2 three v2 p1|p 2\nhi you\nalias ll='ls -l'\nnullglob       \ton\n" +
            "trap -- 'echo int' INT\n-rw-------\n 00 01 fe ff 0a\nhéllo wörld\nhi child\ndeclare -i n=\"5\"\ndeclare -a arr=([0]=\"one\" [2]=\"three\")\nagent\n", r.Stdout);
    }

    // ---------- untrusted input ----------

    /// <summary>A saved snapshot with its manifest (and optionally its zip) edited.</summary>
    static byte[] Tamper(Shell sh, Action<JsonNode> edit, Action<ZipArchive>? zipEdit = null)
    {
        var ms = new MemoryStream();
        ms.Write(Save(sh));
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("manifest.json")!;
            JsonNode manifest;
            using (var s = entry.Open()) manifest = JsonNode.Parse(s)!;
            edit(manifest);
            entry.Delete();
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open())) w.Write(manifest.ToJsonString());
            zipEdit?.Invoke(zip);
        }
        return ms.ToArray();
    }

    static JsonNode FileEntry(JsonNode manifest, string path) => manifest["files"]!.AsArray().Single(f => (string?)f!["path"] == path)!;

    static string Rejected(byte[] bytes, ShellOptions? options = null) =>
        Assert.Throws<InvalidDataException>(() => ShellSnapshot.Load(new MemoryStream(bytes), options)).Message;

    [Fact]
    public void ANewerFormatVersionIsRefusedClearly()
    {
        var e = Assert.Throws<NotSupportedException>(() => ShellSnapshot.Load(new MemoryStream(Tamper(new Shell(), m => { m["version"] = 3; m["files"] = "changed shape"; }))));
        Assert.Equal("snapshot format version 3 is newer than this version of Topwater supports (2); upgrade Topwater to load it", e.Message);
    }

    [Fact]
    public void NotASnapshotIsRefused()
    {
        Assert.Contains("not a valid Topwater snapshot", Rejected(Encoding.UTF8.GetBytes("hello, not a zip")));
        Assert.Contains("not a Topwater snapshot manifest", Rejected(Tamper(new Shell(), m => m["format"] = "something-else")));
        var empty = new MemoryStream();
        using (new ZipArchive(empty, ZipArchiveMode.Create, leaveOpen: true)) { }
        Assert.Contains("manifest.json is missing", Rejected(empty.ToArray()));
    }

    [Theory]
    [InlineData("/home/agent/../../etc/evil")]
    [InlineData("home/agent/relative")]
    [InlineData("/home//agent/x")]
    [InlineData("/home/agent/./x")]
    [InlineData("/home/agent/x/")]
    [InlineData("/home/agent/nul\0byte")]
    [InlineData("")]
    public void PathTricksAreRefused(string path)
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/home/agent/x", "x");
        Assert.Contains("bad path", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["path"] = path)));
    }

    [Fact]
    public void EntriesMustBeWellFormed()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/home/agent/x", "content");
        Assert.Contains("unknown type \"socket\"", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["type"] = "socket")));
        Assert.Contains("bad symlink target", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["type"] = "symlink")));
        Assert.Contains("bad mode", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["mode"] = "9999")));
        Assert.Contains("owner", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["owner"] = "../root")));
        Assert.Contains("mtime", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["mtime"] = "yesterday")));
        Assert.Contains("File exists", Rejected(Tamper(sh, m => m["files"]!.AsArray().Add(FileEntry(m, "/home/agent/x").DeepClone()))));
        Assert.Contains("parent is not a directory", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["path"] = "/nowhere/x")));
        Assert.Contains("bad user", Rejected(Tamper(sh, m => m["user"] = "../../etc")));
        Assert.Contains("bad cwd", Rejected(Tamper(sh, m => m["cwd"] = "../up")));
        Assert.Contains("bad variable name", Rejected(Tamper(sh, m => m["variables"]!.AsArray().Add(new JsonObject { ["name"] = "a;b", ["value"] = "x" }))));
        Assert.Contains("bad index", Rejected(Tamper(sh, m => m["variables"]!.AsArray().Add(JsonNode.Parse("""{"name":"a","array":{"items":[["-1","x"]]}}""")))));
    }

    [Fact]
    public async Task LinksSurviveSaveLoadForkAndRestore()
    {
        var sh = new Shell();
        await sh.RunAsync("mkdir -p d/s; echo hi > d/f; ln -s f d/l; ln -s /etc d/etc; ln -s nowhere d/dang; ln -s . d/s/self; ln -s loop2 loop1; ln -s loop1 loop2; ln d/f d/h; ln -s f d/sl; ln d/sl d/sl2");
        var bytes = Save(sh);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            var manifest = JsonNode.Parse(zip.GetEntry("manifest.json")!.Open())!;
            Assert.Equal(2, (int)manifest["version"]!);
            Assert.Equal("symlink", (string?)FileEntry(manifest, "/home/agent/d/l")["type"]);
            Assert.Equal("f", (string?)FileEntry(manifest, "/home/agent/d/l")["target"]);
            Assert.Equal("/home/agent/d/f", (string?)FileEntry(manifest, "/home/agent/d/h")["link"]);
        }
        const string probe = "readlink d/l d/etc d/dang loop1; cat d/l d/s/self/self/../f; stat -c '%h %F' d/f d/h d/sl2; echo more >> d/h; cat d/f; cat loop1 2>&1";
        const string want = "f\n/etc\nnowhere\nloop2\nhi\nhi\n2 regular file\n2 regular file\n2 symbolic link\nhi\nmore\ncat: loop1: Too many levels of symbolic links\n";
        foreach (var copy in new[] { Shell.Load(new MemoryStream(bytes)), sh.Fork() })
            Assert.Equal(want, (await copy.RunAsync(probe)).Stdout);
        var snap = sh.Snapshot();
        await sh.RunAsync("rm -r d loop1");
        sh.Restore(snap);
        Assert.Equal(want, (await sh.RunAsync(probe)).Stdout);
    }

    [Fact]
    public void LinksInASnapshotAreChecked()
    {
        var sh = new Shell();
        sh.Fs.CreateDirectory("/home/agent/d");
        sh.Fs.WriteAllText("/home/agent/x", "x");
        sh.Fs.CreateSymlink("/home/agent/l", "/etc");
        // an entry can't be put through a link (it would land in /etc), nor be a hard link to a directory, a later or a missing entry
        Assert.Contains("parent is not a directory", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["path"] = "/home/agent/l/x")));
        Assert.Contains("not an earlier file", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["link"] = "/home/agent/d")));
        Assert.Contains("not an earlier file", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["link"] = "/home/agent/missing")));
        Assert.Contains("not an earlier file", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/l")["link"] = "/home/agent/x")));
        Assert.Contains("bad link", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["link"] = "../etc/passwd")));
        Assert.Contains("bad symlink target", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/l")["target"] = "a\0b")));
        Assert.Contains("bad symlink target", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/l")["target"] = new string('a', 4097))));
        // a link to anywhere, even a loop, loads: it is only a path, resolved later with the agent's permissions
        var loaded = Shell.Load(new MemoryStream(Tamper(sh, m => FileEntry(m, "/home/agent/l")["target"] = "l")));
        Assert.Equal("l", loaded.Fs.ReadLink("/home/agent/l"));
    }

    [Fact]
    public void BlobsMustMatchTheirSizeAndHash()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/home/agent/x", "content");
        string? blob = null;
        void Replace(ZipArchive zip, byte[] data)
        {
            zip.GetEntry("blobs/" + blob)!.Delete();
            using var s = zip.CreateEntry("blobs/" + blob).Open();
            s.Write(data);
        }
        Assert.Contains("doesn't match its hash", Rejected(Tamper(sh, m => blob = (string?)FileEntry(m, "/home/agent/x")["blob"], z => Replace(z, "CONTENT"u8.ToArray()))));
        // a blob far bigger than its entry claims (a zip bomb) stops at the claimed size
        Assert.Contains("is larger than 7 bytes", Rejected(Tamper(sh, m => blob = (string?)FileEntry(m, "/home/agent/x")["blob"], z => Replace(z, new byte[10 << 20]))));
        Assert.Contains("is missing", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["blob"] = new string('0', 64))));
        Assert.Contains("bad /home/agent/x: blob", Rejected(Tamper(sh, m => FileEntry(m, "/home/agent/x")["blob"] = "../../etc/passwd")));
    }

    [Fact]
    public void FunctionsMustBeOneDefinition()
    {
        var sh = new Shell();
        void Add(JsonNode m, string name, string body) => (m["functions"] ??= new JsonArray()).AsArray().Add(new JsonObject { ["name"] = name, ["body"] = body });
        Assert.Contains("not one function definition", Rejected(Tamper(sh, m => Add(m, "f", "{ :; }; rm -rf /"))));
        Assert.Contains("not one function definition", Rejected(Tamper(sh, m => Add(m, "f; rm -rf /; g", "{ :; }"))));
        Assert.Contains("function f:", Rejected(Tamper(sh, m => Add(m, "f", "{ unterminated"))));
    }

    [Fact]
    public async Task SnapshotsOverTheLimitsAreRefused()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/home/agent/big", new string('x', 100_000));
        await sh.RunAsync("v=$(printf '%0500d' 0)");
        var bytes = Save(sh);
        Assert.Contains("MaxFileSystemBytes", Rejected(bytes, new ShellOptions { MaxFileSystemBytes = 50_000 }));
        Assert.Contains("MaxExtractEntries", Rejected(bytes, new ShellOptions { MaxExtractEntries = 100 }));
        Assert.Contains("MaxStringBytes", Rejected(bytes, new ShellOptions { MaxStringBytes = 400 }));
        // a snapshot larger than any it could hold is refused before it is read
        Assert.Contains("larger than", Rejected(bytes, new ShellOptions { MaxFileSystemBytes = 100, MaxStringBytes = 100, MaxExtractEntries = 1 }));
        Assert.Contains("larger than", Assert.Throws<InvalidDataException>(() =>
            ShellSnapshot.Load(new ForwardOnly(bytes), new ShellOptions { MaxFileSystemBytes = 100, MaxStringBytes = 100, MaxExtractEntries = 1 })).Message);
        // within the limits it loads, and the loaded shell enforces them as usual
        var loaded = Shell.Load(new MemoryStream(bytes), new ShellOptions { MaxFileSystemBytes = 200_000 });
        Assert.Equal(500, loaded.Environment["v"].Length);
        var full = await loaded.RunAsync("cat big big > more"); Assert.Contains("No space left on device", full.ToToolOutput());
    }

    [Fact]
    public async Task SnapshotCost()
    {
        // a realistic session: ~3,000 files and ~6 MB of data, a few hundred variables, a big array, some history
        var sh = new Shell();
        var line = new string('l', 63) + "\n";
        for (int d = 0; d < 30; d++)
        {
            sh.Fs.CreateDirectory($"/home/agent/repo/dir{d}", parents: true);
            for (int f = 0; f < 100; f++) sh.Fs.WriteAllText($"/home/agent/repo/dir{d}/file{f}.txt", string.Concat(Enumerable.Repeat(line, 30 + f % 7)));
        }
        sh.Fs.WriteAllBytes("/home/agent/data.bin", new byte[512 * 1024]);
        await sh.RunAsync("for i in {1..300}; do declare v$i=value$i; done; a=({1..20000}); declare -A m; for i in {1..2000}; do m[k$i]=$i; done");
        for (int i = 0; i < 200; i++) await sh.RunAsync($"echo turn {i} > /dev/null");
        sh.Snapshot(); // warm up

        const int rounds = 20;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        ShellSnapshot snap = null!;
        for (int i = 0; i < rounds; i++) snap = sh.Snapshot();
        var perSnapshot = clock.Elapsed / rounds;
        long allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / rounds;
        clock.Restart();
        for (int i = 0; i < rounds; i++) sh.Restore(snap);
        var perRestore = clock.Elapsed / rounds;
        clock.Restart();
        for (int i = 0; i < rounds; i++) sh.Fork();
        var perFork = clock.Elapsed / rounds;
        var ms = new MemoryStream();
        clock.Restart();
        snap.Save(ms);
        var save = clock.Elapsed;
        clock.Restart();
        ShellSnapshot.Load(new MemoryStream(ms.ToArray()));
        var load = clock.Elapsed;
        var files = Files(sh).Count;
        output.WriteLine($"{files} entries, {sh.Fs.UsedBytes / 1024} KB in files: snapshot {perSnapshot.TotalMilliseconds:0.00} ms and {allocated / 1024} KB, " +
            $"restore {perRestore.TotalMilliseconds:0.00} ms, fork {perFork.TotalMilliseconds:0.00} ms, save {save.TotalMilliseconds:0} ms ({ms.Length / 1024} KB), load {load.TotalMilliseconds:0} ms");
        Assert.True(perSnapshot < TimeSpan.FromMilliseconds(250), $"snapshot took {perSnapshot}");
        Assert.True(allocated < sh.Fs.UsedBytes / 4, $"a snapshot allocated {allocated} bytes: contents must be shared, not copied");
    }
}
