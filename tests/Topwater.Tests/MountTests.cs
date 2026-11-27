using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Topwater.Tests;

/// <summary>Mounted sources (docs/mounts.md): tests/mounts/cases.json, which the Python and TypeScript ports run too, then what
/// only this port checks: hostile sources, limits, snapshots, recordings and the local folder source.</summary>
public class MountTests
{
    static readonly string Root = FindRoot();
    static readonly string MountsDir = Path.Combine(Root, "tests", "mounts");
    static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(MountsDir, "cases.json")))!;

    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tests", "mounts", "cases.json"))) return d.FullName;
        throw new FileNotFoundException("tests/mounts/cases.json");
    }

    static Dictionary<string, object?> Files(JsonObject o) => o.ToDictionary(kv => kv.Key, kv => (object?)kv.Value?.GetValue<string>());

    static MemorySource Memory(params (string Path, object Content)[] files) => new(files.ToDictionary(f => f.Path, f => (object?)f.Content));

    static JsonObject ChangeJson(MountChange c)
    {
        var d = new JsonObject { ["kind"] = c.Kind.ToString().ToLowerInvariant(), ["path"] = c.Path };
        if (c.To != null) d["to"] = c.To;
        if (c.Version != null) d["version"] = c.Version;
        if (c.Content != null) d["content"] = Encoding.UTF8.GetString(c.Content);
        return d;
    }

    static JsonObject SourceJson(MemorySource src)
    {
        var o = new JsonObject();
        foreach (var (p, v) in src.Tree()) o[p] = v == null ? null : Encoding.UTF8.GetString(v);
        return o;
    }

    /// <summary>Runs a case's steps and returns what each produced, in the shape the file's `expect` holds.</summary>
    static async Task<List<JsonNode?>> RunCase(JsonObject c)
    {
        var src = new MemorySource(Files((JsonObject)Cases["files"]!));
        var sh = new Shell();
        var mode = c["mode"]!.GetValue<string>() switch { "staged" => MountMode.Staged, "live" => MountMode.Live, _ => MountMode.ReadOnly };
        await sh.MountAsync("/mnt/src", src, mode);
        var output = new List<JsonNode?>();
        foreach (var step in ((JsonArray)c["steps"]!).Cast<JsonObject>())
        {
            if (step["run"] is { } run)
            {
                var r = await sh.RunAsync(run.GetValue<string>());
                output.Add(new JsonObject { ["stdout"] = r.Stdout, ["stderr"] = r.Stderr, ["exitCode"] = r.ExitCode });
            }
            else if (step.ContainsKey("changes")) output.Add(new JsonArray([.. sh.PendingChanges("/mnt/src").Select(ChangeJson)]));
            else if (step.ContainsKey("apply") || step.ContainsKey("applySubset"))
            {
                var pending = sh.PendingChanges("/mnt/src");
                var chosen = step["applySubset"] is JsonArray idx ? idx.Select(i => pending[i!.GetValue<int>()]).ToList() : null;
                var r = await sh.ApplyChangesAsync("/mnt/src", chosen);
                output.Add(new JsonObject { ["applied"] = new JsonArray([.. r.Applied.Select(a => (JsonNode)a.Path)]), ["failed"] = r.Failed?.Path, ["conflict"] = r.Conflict });
            }
            else if (step.ContainsKey("reads"))
            {
                output.Add(new JsonArray([.. src.Calls.Where(x => x.Op == "read").Select(x => (JsonNode)x.Path)]));
                src.Calls.Clear();
            }
            else if (step.ContainsKey("source")) output.Add(SourceJson(src));
            else if (step["sourceSet"] is JsonArray set) { src.Set(set[0]!.GetValue<string>(), set[1]!.GetValue<string>()); output.Add(null); }
            else if (step["sourceRemove"] is { } rm) { src.Remove(rm.GetValue<string>()); output.Add(null); }
            else if (step.ContainsKey("discard")) { sh.DiscardChanges("/mnt/src"); output.Add(null); }
        }
        return output;
    }

    public static IEnumerable<object[]> CaseNames() => ((JsonArray)Cases["cases"]!).Select(c => new object[] { c!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task SharedCase(string name)
    {
        var c = ((JsonArray)Cases["cases"]!).Cast<JsonObject>().Single(x => x["name"]!.GetValue<string>() == name);
        var got = await RunCase(c);
        var steps = (JsonArray)c["steps"]!;
        for (int i = 0; i < steps.Count; i++)
            Assert.True(JsonNode.DeepEquals(steps[i]!["expect"], got[i]), $"step {i} ({steps[i]!.ToJsonString()}):\n{got[i]?.ToJsonString()}");
    }

    // ---------- what only this port checks ----------

    /// <summary>A source that lists names no file can have, a duplicate, and an endless supply of entries.</summary>
    sealed class Hostile(int count = 5) : IMountSource
    {
        public int Asked;
        public string Type => "hostile";
        public string Id => "h";
        public MountCapabilities Capabilities => MountCapabilities.None;

        public Task<IReadOnlyList<MountEntry>> ListAsync(string path, int maxEntries, CancellationToken cancellationToken)
        {
            Asked = maxEntries;
            List<MountEntry> bad = [new("", false), new(".", true), new("..", true), new("a/b", false), new("nul\0", false), new(new string('x', 256), false),
                new("ok", false, 3, Version: "1"), new("ok", false, 9, Version: "2"), new("neg", false, -1), null!];
            return Task.FromResult<IReadOnlyList<MountEntry>>([.. bad, .. Enumerable.Range(0, count).Select(i => new MountEntry($"z{i}", false, 1, Version: "v"))]);
        }

        public Task<MountEntry?> StatAsync(string path, CancellationToken cancellationToken) => Task.FromResult<MountEntry?>(null);

        public Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult(path == "/z0" ? new byte[maxBytes + 1] : "abc"u8.ToArray());
    }

    /// <summary>A MemorySource seen through a store that doesn't know sizes before reading, or that changes an entry's version
    /// when it moves (as Drive's ETags do).</summary>
    sealed class Wrapped(MemorySource inner, bool unknownSizes = false, bool newVersionOnRename = false) : IMountSource
    {
        public string Type => inner.Type;
        public string Id => inner.Id;
        public MountCapabilities Capabilities => inner.Capabilities;

        public async Task<IReadOnlyList<MountEntry>> ListAsync(string path, int maxEntries, CancellationToken ct) =>
            [.. (await inner.ListAsync(path, maxEntries, ct)).Select(e => unknownSizes && !e.IsDir ? e with { Size = null } : e)];
        public Task<MountEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);
        public Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken ct) => inner.ReadAsync(path, maxBytes, ct);
        public Task<string> WriteAsync(string path, byte[] data, string? expectedVersion, CancellationToken ct) => inner.WriteAsync(path, data, expectedVersion, ct);
        public Task DeleteAsync(string path, string? expectedVersion, CancellationToken ct) => inner.DeleteAsync(path, expectedVersion, ct);
        public Task<string?> MkdirAsync(string path, CancellationToken ct) => inner.MkdirAsync(path, ct);

        public async Task<string?> RenameAsync(string path, string to, string? expectedVersion, CancellationToken ct)
        {
            await inner.RenameAsync(path, to, expectedVersion, ct);
            if (!newVersionOnRename) return null;
            inner.Set(to, inner.Tree()[to]!);
            return (await inner.StatAsync(to, ct))!.Version;
        }
    }

    [Fact]
    public async Task HostileNamesAreSkippedAndListingIsBounded()
    {
        var sh = new Shell(new ShellOptions { MaxExtractEntries = 4 });
        var h = new Hostile();
        await sh.MountAsync("/mnt/h", h);
        Assert.Equal("ok\nz0\nz1\nz2\n", (await sh.RunAsync("ls /mnt/h")).Stdout);
        Assert.Equal(["skipped 9 entries with names that can't be files", "listing stopped at 4 entries"], sh.Mounts[0].Warnings.Select(w => w.Split(": ", 2)[1]));
        Assert.Equal(4, h.Asked); // the source is told how many entries are wanted
        var r = await sh.RunAsync("cat /mnt/h/ok; cat /mnt/h/z0");
        Assert.Equal("abc", r.Stdout);
        Assert.Equal("cat: /mnt/h/z0: File too large\n", r.Stderr);
        Assert.Equal(2, (await sh.RunAsync("mount -r /mnt/h")).Stderr.Split("mount: /mnt/h:").Length - 1);
    }

    static long FreshBytes() => new Shell().Fs.UsedBytes;

    [Fact]
    public async Task AFileBiggerThanTheDiskIsNotFetched()
    {
        var src = Memory(("big.bin", new byte[5000]), ("small.txt", "s\n"));
        var sh = new Shell(new ShellOptions { MaxFileSystemBytes = FreshBytes() + 4000 });
        await sh.MountAsync("/mnt/s", src);
        var r = await sh.RunAsync("cat /mnt/s/big.bin > /dev/null; cat /mnt/s/small.txt");
        Assert.Equal("s\n", r.Stdout);
        Assert.Contains("File too large", r.Stderr);
    }

    [Fact]
    public async Task FetchedFilesAreEvictedToMakeRoom()
    {
        var src = Memory(("a", Encoding.ASCII.GetBytes(new string('a', 3000))), ("b", Encoding.ASCII.GetBytes(new string('b', 3000))));
        var sh = new Shell(new ShellOptions { MaxFileSystemBytes = FreshBytes() + 4000 });
        await sh.MountAsync("/mnt/s", src);
        Assert.Equal("3000 /mnt/s/a\n", (await sh.RunAsync("wc -c /mnt/s/a")).Stdout);
        Assert.Equal("3000 /mnt/s/b\n", (await sh.RunAsync("wc -c /mnt/s/b")).Stdout);
        Assert.Equal("3000 /mnt/s/a\n", (await sh.RunAsync("wc -c /mnt/s/a")).Stdout);
        Assert.Equal([("read", "/a"), ("read", "/b"), ("read", "/a")], src.Calls.Where(c => c.Op == "read"));
    }

    [Fact]
    public async Task ASlowSourceFailsTheCommandNotTheSession()
    {
        var src = Memory(("slow.txt", "s\n"), ("fast.txt", "f\n"));
        var sh = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(0.5) });
        await sh.MountAsync("/mnt/s", src);
        src.Hook = async (op, path) => { if (path == "/slow.txt") await Task.Delay(5000); };
        var r = await sh.RunAsync("cat /mnt/s/slow.txt");
        Assert.True(r.Stderr.Contains("the source did not answer in time") || r.ExitCode == 124, r.Stderr);
        Assert.Equal("f\n", (await sh.RunAsync("cat /mnt/s/fast.txt")).Stdout);
    }

    [Fact]
    public async Task SourceFailuresAreReportedAndRetried()
    {
        var src = Memory(("a.txt", "a\n"));
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src, MountMode.Staged);
        bool fail = true;
        src.Hook = (op, path) => fail && op is "read" or "write" ? throw new IOException("GET https://api/x?sig=SECRET 503") : Task.CompletedTask;
        Assert.Equal("cat: /mnt/s/a.txt: the source failed (run the command again to retry)\n", (await sh.RunAsync("cat /mnt/s/a.txt")).Stderr);
        fail = false;
        Assert.Equal("a\n", (await sh.RunAsync("cat /mnt/s/a.txt")).Stdout);
        await sh.RunAsync("echo b > /mnt/s/a.txt");
        fail = true;
        var r = await sh.ApplyChangesAsync("/mnt/s");
        Assert.Equal("/a.txt", r.Failed?.Path);
        Assert.Equal("GET https://api/x?sig=SECRET 503", r.Error); // the host sees it all
        Assert.False(r.Conflict);
        fail = false;
        Assert.Equal("/a.txt", (await sh.ApplyChangesAsync("/mnt/s")).Applied[0].Path);
        Assert.Equal("b\n", Encoding.UTF8.GetString(src.Tree()["/a.txt"]!));
    }

    [Fact]
    public async Task TheSourceNeverSeesPathsOutsideIt()
    {
        var src = Memory(("a.txt", "a\n"));
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src, MountMode.Staged);
        await sh.RunAsync("cd /mnt/s; cat ../../etc/hostname; ln -s /etc /mnt/s/etc; cat /mnt/s/etc/hostname; echo x > /mnt/s/etc/../b");
        Assert.All(src.Calls, c => Assert.True(c.Path.StartsWith('/') && !c.Path.Contains(".."), c.Path));
        Assert.Empty(sh.PendingChanges("/mnt/s")); // the link is not a change; ../b left the mount
    }

    [Fact]
    public async Task MountPointRules()
    {
        var sh = new Shell();
        var src = new MemorySource();
        await Assert.ThrowsAsync<ArgumentException>(() => sh.MountAsync("/", src));
        await Assert.ThrowsAsync<ArgumentException>(() => sh.MountAsync("/etc", src)); // not empty
        await Assert.ThrowsAsync<ArgumentException>(() => sh.MountAsync("/mnt/x", src, (MountMode)7));
        await Assert.ThrowsAsync<ArgumentException>(() => sh.MountAsync("/mnt/x", new Hostile(), MountMode.Staged)); // can't write
        await sh.MountAsync("/mnt/x", src);
        await Assert.ThrowsAsync<ArgumentException>(() => sh.MountAsync("/mnt/x/y", src));
        await Assert.ThrowsAsync<ArgumentException>(() => sh.MountAsync("/mnt/x", src));
        sh.Unmount("/mnt/x");
        Assert.Empty(sh.Mounts);
        Assert.Equal("", (await sh.RunAsync("ls -A /mnt/x")).Stdout);
    }

    /// <summary>Every entry of a zip, inflated, as one string.</summary>
    static string Unzipped(byte[] raw)
    {
        using var z = new ZipArchive(new MemoryStream(raw));
        var sb = new StringBuilder();
        foreach (var e in z.Entries) { using var s = e.Open(); sb.Append(new StreamReader(s).ReadToEnd()); }
        return sb.ToString();
    }

    static JsonNode Manifest(byte[] raw)
    {
        using var z = new ZipArchive(new MemoryStream(raw));
        return JsonNode.Parse(new StreamReader(z.GetEntry("manifest.json")!.Open()).ReadToEnd())!;
    }

    [Fact]
    public async Task SnapshotKeepsTheDefinitionAndTheChangesNotTheFiles()
    {
        var src = Memory(("a.txt", "secret\n"), ("b.txt", "b\n"));
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src, MountMode.Staged);
        await sh.RunAsync("cat /mnt/s/a.txt; echo new > /mnt/s/b.txt; rm /mnt/s/a.txt; cd /mnt/s");
        var buf = new MemoryStream();
        sh.Save(buf);
        var raw = buf.ToArray();
        Assert.DoesNotContain("secret", Unzipped(raw));
        Assert.Equal(3, Manifest(raw)["version"]!.GetValue<int>());
        var loaded = Shell.Load(new MemoryStream(raw));
        var info = loaded.Mounts[0];
        Assert.Equal(("/mnt/s", MountMode.Staged, "memory", "memory", 2), (info.Path, info.Mode, info.Type, info.Id, info.Held));
        Assert.Equal("unavailable: no source for memory:memory", info.Error);
        Assert.Empty(loaded.PendingChanges("/mnt/s"));
        Assert.Equal([MountChangeKind.Write, MountChangeKind.Delete], loaded.SnapshotChanges("/mnt/s").Select(c => c.Kind));
        Assert.Contains("Permission denied", (await loaded.RunAsync("ls /mnt/s")).Stderr);
        await loaded.MountAsync("/mnt/s", src, MountMode.Staged);
        // a file's changes wait for the host: not in the files, not pending, until it accepts them
        Assert.Equal("a.txt\nb.txt\nb\n", (await loaded.RunAsync("ls /mnt/s; cat /mnt/s/b.txt")).Stdout);
        Assert.Empty(loaded.PendingChanges("/mnt/s"));
        Assert.Contains("2 changes from a saved session wait for the host", (await loaded.RunAsync("mount")).Stdout);
        await loaded.AcceptSnapshotChangesAsync("/mnt/s");
        Assert.Equal("b.txt\nnew\n", (await loaded.RunAsync("ls /mnt/s; cat /mnt/s/b.txt")).Stdout);
        Assert.Equal(sh.PendingChanges("/mnt/s"), loaded.PendingChanges("/mnt/s"));
        // a snapshot taken in this process comes back as it was, with no source call
        var other = new Shell();
        await other.MountAsync("/mnt/other", src);
        await other.MountAsync("/mnt/s", src, MountMode.Staged);
        other.Unmount("/mnt/other");
        int before = src.Calls.Count;
        other.Restore(sh.Snapshot());
        Assert.Equal("b.txt\n", (await other.RunAsync("ls /mnt/s")).Stdout);
        Assert.Equal(sh.PendingChanges("/mnt/s"), other.PendingChanges("/mnt/s"));
        Assert.DoesNotContain(src.Calls.Skip(before), c => c.Op == "list");
        // the source changed meanwhile: applying conflicts, against the version the change was made on
        src.Set("b.txt", "theirs\n");
        var r = await loaded.ApplyChangesAsync("/mnt/s");
        Assert.True(r.Conflict);
        Assert.Equal("/b.txt", r.Failed?.Path);
    }

    [Fact]
    public async Task ACommandInAMountDirectoryFetchesIt()
    {
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", Memory(("d/a.txt", "hello\n")));
        Assert.Equal("a.txt:hello\n", (await sh.RunAsync("cd /mnt/s/d && grep -r hello")).Stdout);
    }

    [Fact]
    public async Task AFileDeletedInARenamedDirectory()
    {
        var src = Memory(("d/a.txt", "a\n"), ("d/b.txt", "b\n"));
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src, MountMode.Staged);
        await sh.RunAsync("rm /mnt/s/d/b.txt; mv /mnt/s/d /mnt/s/e");
        Assert.Equal([(MountChangeKind.Rename, "/d", "/e"), (MountChangeKind.Delete, "/e/b.txt", null)], sh.PendingChanges("/mnt/s").Select(c => (c.Kind, c.Path, c.To)));
        Assert.Null((await sh.ApplyChangesAsync("/mnt/s")).Failed);
        Assert.Equal(new[] { "/e", "/e/a.txt" }, src.Tree().Keys);
    }

    [Fact]
    public Task ARelativeMountPointIsRefused() => Assert.ThrowsAsync<ArgumentException>(() => new Shell().MountAsync("mnt/s", new MemorySource()));

    [Fact]
    public async Task TheTimeLimitStillEndsTheCallDuringARefresh()
    {
        var src = Memory(("a", "a"));
        var sh = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(0.3) });
        await sh.MountAsync("/mnt/s", src);
        src.Hook = (_, _) => Task.Delay(1000);
        Assert.DoesNotContain("after", (await sh.RunAsync("mount -r /mnt/s; echo after")).Stdout);
    }

    [Fact]
    public async Task AWorkingDirectoryInsideAMountSurvivesARestore()
    {
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", Memory(("d/a.txt", "a\n")));
        await sh.RunAsync("cd /mnt/s/d");
        var snap = sh.Snapshot();
        await sh.RunAsync("cd /");
        sh.Restore(snap);
        Assert.Equal("/mnt/s/d\na\n", (await sh.RunAsync("pwd; cat a.txt")).Stdout);
    }

    [Fact]
    public async Task ForkUsesTheSameSource()
    {
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", Memory(("a.txt", "a\n")), MountMode.Staged);
        await sh.RunAsync("echo b >> /mnt/s/a.txt");
        Assert.Equal("a\nb\n", (await sh.Fork().RunAsync("cat /mnt/s/a.txt")).Stdout);
    }

    [Fact]
    public void OldSnapshotsStillLoad()
    {
        var buf = new MemoryStream();
        new Shell().Save(buf);
        Assert.DoesNotContain("\"mounts\"", Unzipped(buf.ToArray()));
        Assert.Empty(Shell.Load(new MemoryStream(buf.ToArray())).Mounts);
    }

    [Fact]
    public async Task BadMountsInASnapshotAreRefused()
    {
        var buf = new MemoryStream();
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", Memory(("a", "a")), MountMode.Staged);
        await sh.RunAsync("echo b > /mnt/s/a");
        sh.Save(buf);
        string[] bad = ["""{"path": "/mnt/../etc"}""", """{"mode": "wild"}""", """{"path": "/"}""",
            """{"changes": [{"kind": "write", "path": "/a/../b", "size": 0}]}""",
            """{"changes": [{"kind": "write", "path": "/a", "size": 3, "blob": "0000000000000000000000000000000000000000000000000000000000000000"}]}""",
            """{"changes": [{"kind": "explode", "path": "/a"}]}"""];
        foreach (var b in bad)
        {
            using var z = new ZipArchive(new MemoryStream(buf.ToArray()));
            var m = JsonNode.Parse(new StreamReader(z.GetEntry("manifest.json")!.Open()).ReadToEnd())!;
            var mount = (JsonObject)m["mounts"]![0]!;
            foreach (var (k, v) in (JsonObject)JsonNode.Parse(b)!) mount[k] = v?.DeepClone();
            var output = new MemoryStream();
            using (var w = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var e in z.Entries)
                {
                    using var s = w.CreateEntry(e.FullName).Open();
                    if (e.FullName == "manifest.json") s.Write(Encoding.UTF8.GetBytes(m.ToJsonString()));
                    else e.Open().CopyTo(s);
                }
            output.Position = 0;
            Assert.Throws<InvalidDataException>(() => ShellSnapshot.Load(output));
        }
    }

    [Fact]
    public async Task ARecordingReplaysWithoutTheSource()
    {
        var src = Memory(("a.txt", "a\n"), ("d/b.txt", "b\n"));
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src, MountMode.Live);
        await sh.RunAsync("cat /mnt/s/a.txt"); // fetched before the recording starts
        var rec = sh.StartRecording();
        await sh.RunAsync("cat /mnt/s/a.txt /mnt/s/d/b.txt; echo c > /mnt/s/c.txt; mount -r /mnt/s; ls /mnt/s");
        src.Set("a.txt", "theirs\n");
        await sh.RunAsync("echo mine > /mnt/s/a.txt");
        rec.Stop();
        var buf = new MemoryStream();
        rec.Save(buf);
        var loaded = Recording.Load(new MemoryStream(buf.ToArray()));
        var before = src.Calls.Count;
        var report = await loaded.ReplayAsync();
        Assert.True(report.Matches, string.Join("\n", report.Differences.Select(d => d.ToString())));
        Assert.Equal(before, src.Calls.Count);
        var r = await loaded.CreateShell().RunAsync("mount -r /mnt/s; cat /mnt/s/d/b.txt /mnt/s/c.txt");
        Assert.Equal("b\n", r.Stdout);
        Assert.Equal("cat: /mnt/s/c.txt: read /c.txt is not in the Topwater recording (run the command again to retry)\n", r.Stderr);
    }

    sealed class TempDir : IDisposable
    {
        public readonly string Path = Directory.CreateTempSubdirectory("topwater-mount-").FullName;
        public string this[string rel] => System.IO.Path.Combine(Path, rel);
        public void Dispose()
        {
            foreach (var d in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories)) try { File.SetUnixFileMode(d, (UnixFileMode)0x1C0); } catch (IOException) { }
            Directory.Delete(Path, recursive: true);
        }
    }

    static List<string> Tree(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p)).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task LocalFolderSourceWorks()
    {
        using var tmp = new TempDir();
        var root = tmp["vault"];
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        File.WriteAllText(Path.Combine(root, "notes", "a.md"), "# A\n");
        File.WriteAllText(Path.Combine(root, "top.md"), "top\n");
        File.WriteAllText(tmp["outside.txt"], "private\n");
        File.CreateSymbolicLink(Path.Combine(root, "link.md"), tmp["outside.txt"]);
        Directory.CreateSymbolicLink(Path.Combine(root, "up"), tmp.Path);
        var sh = new Shell();
        await sh.MountAsync("/mnt/v", new LocalFolderSource(root));
        Assert.Equal("/mnt/v\n/mnt/v/notes\n/mnt/v/notes/a.md\n/mnt/v/top.md\n", (await sh.RunAsync("find /mnt/v | sort")).Stdout);
        Assert.Equal("# A\n", (await sh.RunAsync("cat /mnt/v/notes/a.md")).Stdout);
        Assert.Contains("Permission denied", (await sh.RunAsync("echo x > /mnt/v/top.md")).Stderr);
        await Assert.ThrowsAsync<ArgumentException>(() => sh.MountAsync("/mnt/w", new LocalFolderSource(root), MountMode.Staged)); // read-only unless writable

        await sh.MountAsync("/mnt/w", new LocalFolderSource(root, writable: true, id: "vault"), MountMode.Staged);
        await sh.RunAsync("echo more >> /mnt/w/top.md; mv /mnt/w/notes/a.md /mnt/w/notes/b.md; mkdir /mnt/w/new; echo n > /mnt/w/new/n.md");
        var r = await sh.ApplyChangesAsync("/mnt/w");
        Assert.True(r.Failed == null, r.Error);
        Assert.Equal("top\nmore\n", File.ReadAllText(Path.Combine(root, "top.md")));
        Assert.Equal("# A\n", File.ReadAllText(Path.Combine(root, "notes", "b.md")));
        Assert.False(File.Exists(Path.Combine(root, "notes", "a.md")));
        Assert.Equal("n\n", File.ReadAllText(Path.Combine(root, "new", "n.md")));
        Assert.Equal("private\n", File.ReadAllText(tmp["outside.txt"]));
        await sh.RunAsync("echo again >> /mnt/w/top.md");
        File.WriteAllText(Path.Combine(root, "top.md"), "edited on the host, longer\n");
        r = await sh.ApplyChangesAsync("/mnt/w");
        Assert.True(r.Conflict);
        Assert.Equal("/top.md", r.Failed?.Path);
        Assert.Equal("edited on the host, longer\n", File.ReadAllText(Path.Combine(root, "top.md")));

        // a link anywhere in a path, the last name included, is never followed
        var source = new LocalFolderSource(root, writable: true);
        Assert.Null(await source.StatAsync("/link.md", default));
        await Assert.ThrowsAsync<MountSourceException>(() => source.ReadAsync("/up/outside.txt", 100, default));
        await Assert.ThrowsAsync<MountSourceException>(() => source.ReadAsync("/link.md", 100, default));
        await Assert.ThrowsAsync<MountSourceException>(() => source.ReadAsync("/notes/../top.md", 100, default));
    }

    // ---------- review 046: regression tests ----------

    static string HostFolder(TempDir tmp)
    {
        var root = tmp["notes"];
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "keep.md"), "keep\n");
        File.WriteAllText(Path.Combine(root, "a.md"), "hello\n");
        return root;
    }

    static readonly string[] HostTree = ["a.md", "docs", Path.Combine("docs", "keep.md")];

    /// <summary>hostile.snap (security review 046): mode live, a rename, a mkdir and a write into the host's folder.</summary>
    [Theory]
    [InlineData(MountMode.Staged, true, false)]
    [InlineData(MountMode.ReadOnly, true, false)]
    [InlineData(MountMode.ReadOnly, false, false)]
    [InlineData(MountMode.Staged, true, true)]
    public async Task AHostileSnapshotNeverWritesToARealSource(MountMode mode, bool writable, bool unmount)
    {
        using var tmp = new TempDir();
        var root = HostFolder(tmp);
        var sh = new Shell();
        await sh.MountAsync("/mnt/notes", new LocalFolderSource(root, writable, "notes"), mode);
        if (unmount) sh.Unmount("/mnt/notes");
        sh.Restore(ShellSnapshot.Load(File.OpenRead(Path.Combine(MountsDir, "hostile.snap"))));
        foreach (var c in new[] { "true", "echo hi > /mnt/notes/x.md; ls /mnt/notes", "mount" }) await sh.RunAsync(c);
        Assert.Equal(HostTree, Tree(root));
        var m = sh.Mounts[0];
        Assert.Equal(unmount ? MountMode.Live : mode, m.Mode);
        Assert.Equal(unmount, m.Error != null);
        if (!unmount)
        {
            Assert.Equal(3, m.Held);
            Assert.Contains(string.Join(",", sh.PendingChanges("/mnt/notes").Select(c => c.Path)), new[] { "", "/x.md" }); // only the agent's own
            Assert.Contains("keep.md", (await sh.RunAsync("ls /mnt/notes/docs")).Stdout);
        }
    }

    [Fact]
    public async Task AForkOfAReplayShellNeverReachesTheRealSource()
    {
        var src = new MemorySource(new Dictionary<string, object?> { ["a.md"] = "one\n" }, id: "m");
        var sh = new Shell();
        await sh.MountAsync("/mnt/m", src, MountMode.Live);
        var rec = sh.StartRecording();
        await sh.RunAsync("cat /mnt/m/a.md");
        rec.Stop();
        var buf = new MemoryStream();
        rec.Save(buf);
        var real = new MemorySource(new Dictionary<string, object?> { ["a.md"] = "one\n" }, id: "m");
        var replayShell = Recording.Load(new MemoryStream(buf.ToArray())).CreateShell(s => s.MountAsync("/mnt/m", real, MountMode.Live).GetAwaiter().GetResult());
        int n = real.Calls.Count;
        await replayShell.RunAsync("echo replayed > /mnt/m/r.md");
        await replayShell.Fork().RunAsync("echo forked > /mnt/m/fork.md");
        Assert.Equal(n, real.Calls.Count);
        Assert.False(real.Tree().ContainsKey("/fork.md"));
    }

    [Fact]
    public async Task UnmountForgetsTheSource()
    {
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", Memory(("a.md", "x\n")), MountMode.Staged);
        var snap = sh.Snapshot();
        sh.Unmount("/mnt/s");
        sh.Restore(snap);
        Assert.Equal("unavailable: no source for memory:memory", sh.Mounts[0].Error);
    }

    static void MakeFifo(string path) => Process.Start("mkfifo", [path])!.WaitForExit();

    [Fact]
    public async Task AFifoNeverBlocksTheCall()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var tmp = new TempDir();
        var root = tmp["v"];
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.md"), "a\n");
        MakeFifo(Path.Combine(root, "fifo"));
        var sh = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(5) });
        await sh.MountAsync("/mnt/v", new LocalFolderSource(root));
        Assert.Equal("a.md\n", (await sh.RunAsync("ls /mnt/v")).Stdout); // not listed
        File.Delete(Path.Combine(root, "a.md"));
        MakeFifo(Path.Combine(root, "a.md")); // a listed file replaced by a FIFO
        var watch = Stopwatch.StartNew();
        var r = await sh.RunAsync("cat /mnt/v/a.md");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Contains("not a regular file", r.Stderr);
    }

    [Fact]
    public async Task BackslashNamesNeverReachAFolder()
    {
        using var tmp = new TempDir();
        var root = HostFolder(tmp);
        var sh = new Shell();
        await sh.MountAsync("/mnt/n", new LocalFolderSource(root, writable: true, id: "n"), MountMode.Live);
        var r = await sh.RunAsync(@"echo evil > '/mnt/n/..\..\outside\win.txt'");
        Assert.Contains("not a name a file can have here", r.Stderr);
        Assert.DoesNotContain(root, r.Stderr);
        Assert.Equal(HostTree, Tree(root));
        r = await sh.RunAsync("echo ok > /mnt/n/good.md"); // a change that failed doesn't hold up the next ones, and is reported once
        Assert.Equal("", r.Stderr);
        Assert.Equal("ok\n", File.ReadAllText(Path.Combine(root, "good.md")));
    }

    [Fact]
    public async Task SourceErrorsReachTheAgentOnlyWhenMeantForIt()
    {
        var src = Memory(("a.md", "a\n"), ("b.md", "b\n"));
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src);
        src.Hook = (op, path) => path switch
        {
            "/a.md" => throw new MountSourceException("a.md: the page is locked"),
            "/b.md" => throw new InvalidOperationException("GET https://api/x?sig=SECRET 503"),
            _ => Task.CompletedTask,
        };
        var r = await sh.RunAsync("cat /mnt/s/a.md /mnt/s/b.md");
        Assert.Contains("the page is locked", r.Stderr);
        Assert.DoesNotContain("SECRET", r.Stderr);
        Assert.Contains("the source failed", r.Stderr);
    }

    [Fact]
    public async Task LocalFolderErrorsShowNoHostPath()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var root = HostFolder(tmp);
        var sh = new Shell();
        await sh.MountAsync("/mnt/n", new LocalFolderSource(root, writable: true, id: "n"), MountMode.Live);
        File.SetUnixFileMode(Path.Combine(root, "docs"), (UnixFileMode)0x140); // 0500
        ShellResult r;
        try { r = await sh.RunAsync("echo x > /mnt/n/docs/new.md"); }
        finally { File.SetUnixFileMode(Path.Combine(root, "docs"), (UnixFileMode)0x1ED); }
        Assert.DoesNotContain(tmp.Path, r.Stdout + r.Stderr);
        Assert.Contains("mount: /mnt/n/docs/new.md", r.Stderr);
    }

    [Fact]
    public async Task LongNamesWriteAndLeaveNoTemporaryFile()
    {
        using var tmp = new TempDir();
        var root = HostFolder(tmp);
        var sh = new Shell();
        await sh.MountAsync("/mnt/n", new LocalFolderSource(root, writable: true, id: "n"), MountMode.Live);
        var name = new string('y', 250);
        Assert.Equal("", (await sh.RunAsync($"echo x > /mnt/n/{name}")).Stderr);
        Assert.Equal("x\n", File.ReadAllText(Path.Combine(root, name)));
        Assert.DoesNotContain(Directory.EnumerateFiles(root), f => f.EndsWith(".tmp"));
    }

    static async Task WithListBudget(int budget, Func<Task> run)
    {
        var saved = MountManager.ListBudget;
        MountManager.ListBudget = budget;
        try { await run(); }
        finally { MountManager.ListBudget = saved; }
    }

    [Fact]
    public Task ALargeSourceIsListedInParts() => WithListBudget(5, async () =>
    {
        var files = new Dictionary<string, object?>();
        for (int i = 0; i < 4; i++) for (int j = 0; j < 3; j++) files[$"d{i}/f{j}.txt"] = $"{i}{j}\n";
        var src = new MemorySource(files);
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src);
        Assert.Equal("f0.txt\nf1.txt\nf2.txt\n", (await sh.RunAsync("ls /mnt/s/d0")).Stdout); // named: listed before the command
        Assert.Equal("/mnt/s/d0/f1.txt\n", (await sh.RunAsync("echo /mnt/s/*/f1.txt")).Stdout); // the glob looked into unlisted ones: listed after
        Assert.Equal("/mnt/s/d0/f1.txt /mnt/s/d1/f1.txt /mnt/s/d2/f1.txt /mnt/s/d3/f1.txt\n", (await sh.RunAsync("echo /mnt/s/*/f1.txt")).Stdout);
        var fresh = new Shell();
        await fresh.MountAsync("/mnt/s", src);
        var awk = "awk 'BEGIN { while ((getline l < \"/mnt/s/d3/f0.txt\") > 0) print l }'"; // a path inside a program: nothing named it
        Assert.Equal("", (await fresh.RunAsync(awk)).Stdout); // d3 was not listed: listed after the command
        Assert.Equal("", (await fresh.RunAsync(awk)).Stdout); // f0.txt was not fetched: fetched after the command
        Assert.Equal("30\n", (await fresh.RunAsync(awk)).Stdout);
        Assert.Equal("32\n12\n", (await sh.RunAsync("cat /mnt/s/d3/f2.txt; find /mnt/s -type f | wc -l")).Stdout);
    });

    [Fact]
    public Task ARestoreFromAFileDoesNotListEverythingInOneCall() => WithListBudget(3, async () =>
    {
        var src = new MemorySource(Enumerable.Range(0, 10).ToDictionary(i => $"d{i}/f.txt", _ => (object?)"x"));
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src);
        var buf = new MemoryStream();
        sh.Save(buf);
        var loaded = Shell.Load(new MemoryStream(buf.ToArray()));
        await loaded.MountAsync("/mnt/s", src); // adopts the unavailable mount
        int before = src.Calls.Count;
        await loaded.RunAsync("true");
        Assert.DoesNotContain(src.Calls.Skip(before), c => c.Op == "list");
    });

    [Fact]
    public async Task UnknownSizesShowAfterFetching()
    {
        var sh = new Shell();
        await sh.MountAsync("/mnt/p", new Wrapped(Memory(("page.md", "twelve bytes")), unknownSizes: true));
        Assert.Equal("0\n", (await sh.RunAsync("stat -c %s /mnt/p/page.md")).Stdout);
        Assert.Equal("12\n", (await sh.RunAsync("cat /mnt/p/page.md > /dev/null; stat -c %s /mnt/p/page.md")).Stdout);
    }

    [Fact]
    public async Task ARenameTakesTheVersionTheSourceReturns()
    {
        var inner = Memory(("a.md", "a\n"));
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", new Wrapped(inner, newVersionOnRename: true), MountMode.Live);
        await sh.RunAsync("mv /mnt/s/a.md /mnt/s/b.md");
        var r = await sh.RunAsync("echo more >> /mnt/s/b.md");
        Assert.Equal("", r.Stderr);
        Assert.Equal("a\nmore\n", Encoding.UTF8.GetString(inner.Tree()["/b.md"]!));
    }

    [Fact]
    public async Task MountNamesWhatTheSourceCannotDo()
    {
        var src = Memory(("a.md", "a\n"));
        src.Capabilities = MountCapabilities.Write;
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src, MountMode.Staged);
        Assert.Equal("memory:memory on /mnt/s type memory (rw,staged,no-delete,no-rename,no-mkdir)\n", (await sh.RunAsync("mount")).Stdout);
    }

    [Fact]
    public async Task TheToolDescriptionNamesTheMounts()
    {
        var sh = new Shell();
        await sh.MountAsync("/mnt/vault", new MemorySource(id: "v"));
        await sh.MountAsync("/mnt/notes", new MemorySource(id: "n"), MountMode.Staged);
        var d = BashTool.DescriptionFor(sh);
        Assert.Contains("`/mnt/vault` is a folder from outside, read-only.", d);
        Assert.Contains("`/mnt/notes` is a folder from outside; your changes", d);
    }

    [Fact]
    public async Task AReplacedFolderWhoseFilesMovedOut()
    {
        var src = Memory(("a/x.md", "x\n"), ("a/y.md", "y\n"), ("b/z.md", "z\n"), ("c.md", "c\n"), ("d/e/f.md", "f\n"));
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src, MountMode.Staged);
        await sh.RunAsync("echo n > /mnt/s/b/w4.md; mv /mnt/s/a /mnt/s/tmpx && mv /mnt/s/d /mnt/s/a && mv /mnt/s/tmpx /mnt/s/d; mkdir /mnt/s/d/n7; " +
            "mv /mnt/s/d/y.md /mnt/s/m0; rm -r /mnt/s/d; echo n > /mnt/s/a/w3.md");
        Assert.Null((await sh.ApplyChangesAsync("/mnt/s")).Failed);
        Assert.Equal(["/a", "/a/e", "/a/e/f.md", "/a/w3.md", "/b", "/b/w4.md", "/b/z.md", "/c.md", "/m0"], src.Tree().Keys.ToList());
        Assert.Equal(["f\n", "n\n", "n\n", "z\n", "c\n", "y\n"], src.Tree().Values.Where(v => v != null).Select(v => Encoding.UTF8.GetString(v!)).ToList());
    }

    /// <summary>Random mkdir, mv, rm and edits on a staged mount: applying the change set makes the source match the mount.</summary>
    [Fact]
    public async Task RandomChangeSetsApplyToWhatTheMountShows()
    {
        for (int seed = 0; seed < 120; seed++)
        {
            var rnd = new Random(seed);
            var src = Memory(("a/x.md", "x\n"), ("a/y.md", "y\n"), ("b/z.md", "z\n"), ("c.md", "c\n"), ("d/e/f.md", "f\n"));
            var sh = new Shell();
            await sh.MountAsync("/mnt/s", src, MountMode.Staged);
            async Task<string> Run(string c) => (await sh.RunAsync(c)).Stdout;
            for (int k = rnd.Next(1, 9); k > 0; k--)
            {
                var files = (await Run("cd /mnt/s && find . -mindepth 1")).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
                var dirs = new List<string>();
                foreach (var p in files) if ((await Run($"test -d /mnt/s/{p} && echo y")).Length > 0) dirs.Add(p);
                dirs.Add(".");
                var pick = files.Count > 0 ? files[rnd.Next(files.Count)] : ".";
                string Dir() => dirs[rnd.Next(dirs.Count)];
                switch (new[] { "mkdir", "mv", "mv", "rm", "edit", "new", "swap" }[rnd.Next(7)])
                {
                    case "mkdir": await Run($"mkdir /mnt/s/{Dir()}/n{rnd.Next(10)}"); break;
                    case "mv" when files.Count > 0: await Run($"mv /mnt/s/{pick} /mnt/s/{Dir()}/m{rnd.Next(10)} 2>/dev/null"); break;
                    case "rm" when files.Count > 0: await Run($"rm -r /mnt/s/{pick}"); break;
                    case "edit" when files.Count > 0: await Run($"test -f /mnt/s/{pick} && echo e >> /mnt/s/{pick}"); break;
                    case "new": await Run($"echo n > /mnt/s/{Dir()}/w{rnd.Next(10)}.md 2>/dev/null"); break;
                    case "swap" when files.Count > 1:
                        var a = files[rnd.Next(files.Count)];
                        var b = files.Where(f => f != a).ElementAt(rnd.Next(files.Count - 1));
                        await Run($"mv /mnt/s/{a} /mnt/s/tmpx 2>/dev/null && mv /mnt/s/{b} /mnt/s/{a} 2>/dev/null && mv /mnt/s/tmpx /mnt/s/{b} 2>/dev/null");
                        break;
                }
            }
            var r = await sh.ApplyChangesAsync("/mnt/s");
            Assert.True(r.Failed == null, $"seed {seed}: {r.Failed} {r.Error}");
            var want = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            foreach (var p in (await Run("cd /mnt/s && find . -mindepth 1 | sort")).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var full = "/mnt/s/" + p[2..];
                want["/" + p[2..]] = (await Run($"test -d {full} && echo d")).Length > 0 ? null : await Run($"cat {full}");
            }
            Assert.Equal(want, new SortedDictionary<string, string?>(src.Tree().ToDictionary(kv => kv.Key, kv => kv.Value == null ? null : Encoding.UTF8.GetString(kv.Value)), StringComparer.Ordinal));
        }
    }

    // ---------- files from every port ----------

    static readonly string[] FixtureCommands =
    [
        "cat /mnt/s/a.txt; echo more >> /mnt/s/a.txt; mv /mnt/s/d/b.txt /mnt/s/d/c.txt; rm /mnt/s/old.txt; mkdir /mnt/s/e",
        "echo y > /mnt/l/x.txt", "echo z > /mnt/l/x.txt",
    ];

    /// <summary>The scenario each port records into tests/mounts/mounts-from-&lt;port&gt;.topwater (TOPWATER_WRITE_FIXTURES=1 rewrites it).</summary>
    static async Task<byte[]> MakeFixture()
    {
        var src = Memory(("a.txt", "a\n"), ("d/b.txt", "b\n"), ("old.txt", "o\n"));
        var live = new MemorySource(new Dictionary<string, object?> { ["x.txt"] = "x\n" }, id: "live");
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src, MountMode.Staged);
        await sh.MountAsync("/mnt/l", live, MountMode.Live);
        var rec = sh.StartRecording(new RecordingOptions { Metadata = { ["source"] = "csharp port" } });
        await sh.RunAsync(FixtureCommands[0]);
        await sh.RunAsync(FixtureCommands[1]);
        live.Set("x.txt", "theirs\n");
        await sh.RunAsync(FixtureCommands[2]);
        rec.Stop();
        var buf = new MemoryStream();
        rec.Save(buf);
        return buf.ToArray();
    }

    [Fact]
    public async Task ThisPortsMountFixtureIsCurrent()
    {
        var path = Path.Combine(MountsDir, "mounts-from-csharp.topwater");
        if (System.Environment.GetEnvironmentVariable("TOPWATER_WRITE_FIXTURES") == "1" || !File.Exists(path)) File.WriteAllBytes(path, await MakeFixture());
        await CheckFixture(File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("python")]
    [InlineData("typescript")]
    public async Task MountFilesFromOtherPorts(string port)
    {
        var path = Path.Combine(MountsDir, $"mounts-from-{port}.topwater");
        if (!File.Exists(path)) return; // written by that port's tests
        await CheckFixture(File.ReadAllBytes(path));
    }

    static async Task CheckFixture(byte[] raw)
    {
        var sh = Shell.Load(new MemoryStream(raw));
        Assert.Equal([("/mnt/l", MountMode.Live, "memory", "live", 1), ("/mnt/s", MountMode.Staged, "memory", "memory", 4)],
            sh.Mounts.OrderBy(m => m.Path, StringComparer.Ordinal).Select(m => (m.Path, m.Mode, m.Type, m.Id, m.Held)));
        Assert.Equal([(MountChangeKind.Rename, "/d/b.txt", "/d/c.txt"), (MountChangeKind.Mkdir, "/e", null), (MountChangeKind.Write, "/a.txt", null), (MountChangeKind.Delete, "/old.txt", null)],
            sh.SnapshotChanges("/mnt/s").Select(c => (c.Kind, c.Path, c.To)));
        Assert.Equal("z\n"u8.ToArray(), sh.SnapshotChanges("/mnt/l")[0].Content);
        var rec = Recording.Load(new MemoryStream(raw));
        Assert.Equal(FixtureCommands, rec.Calls.Select(c => c.Command));
        Assert.Contains(rec.MountStart, x => x.Op == "list");
        Assert.Contains(rec.Calls[0].Mounts!, x => x.Op == "read");
        Assert.Contains(rec.Calls[1].Mounts!, x => x.Op == "write");
        Assert.Contains("changed in the source", rec.Calls[2].Stderr);
        var report = await rec.ReplayAsync();
        Assert.True(report.Matches, string.Join("\n", report.Differences.Select(d => d.ToString())));
    }

    /// <summary>docs/mounts.md's complete C# source is the WikiSource below, as written there; it runs.</summary>
    [Fact]
    public async Task TheDocumentedSourceRuns()
    {
        static string Lines(string s) => string.Join("\n", s.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()));
        var doc = Lines(File.ReadAllText(Path.Combine(Root, "docs", "mounts.md")));
        int start = doc.IndexOf("sealed class WikiSource", StringComparison.Ordinal);
        int end = doc.IndexOf("\n}\n", start, StringComparison.Ordinal) + 2;
        Assert.Contains(doc[start..end], Lines(File.ReadAllText(Path.Combine(Root, "tests", "Topwater.Tests", "MountTests.cs"))));

        var shell = new Shell();
        var wiki = new WikiSource("team", new() { ["plan.md"] = "# Plan\nDraft\n" });
        await shell.MountAsync("/mnt/wiki", wiki, MountMode.Staged);
        await shell.RunAsync("sed -i 's/Draft/Final/' /mnt/wiki/plan.md; echo '# Notes' > /mnt/wiki/notes.md");
        var result = await shell.ApplyChangesAsync("/mnt/wiki");
        Assert.Null(result.Failed);
        Assert.Equal("# Plan\nFinal\n", Encoding.UTF8.GetString(await wiki.ReadAsync("/plan.md", 100, default)));
        Assert.Equal("# Notes\n", Encoding.UTF8.GetString(await wiki.ReadAsync("/notes.md", 100, default)));
    }
}

sealed class WikiSource(string space, Dictionary<string, string> pages) : IMountSource
{
    readonly Dictionary<string, byte[]> _pages = pages.ToDictionary(p => p.Key, p => System.Text.Encoding.UTF8.GetBytes(p.Value));
    public string Type => "wiki";
    public string Id => space;                       // saved in snapshots: never the token
    public MountCapabilities Capabilities => MountCapabilities.Write | MountCapabilities.Delete;

    static string ETag(byte[] data) => Convert.ToHexString(SHA1.HashData(data))[..12].ToLowerInvariant();

    byte[] Page(string path) => path.Count(c => c == '/') == 1 && _pages.TryGetValue(path[1..], out var d)
        ? d : throw new MountSourceException($"{path}: no such page");

    public Task<IReadOnlyList<MountEntry>> ListAsync(string path, int maxEntries, CancellationToken cancellationToken) =>
        path == "/" ? Task.FromResult<IReadOnlyList<MountEntry>>(
                [.. _pages.OrderBy(p => p.Key, StringComparer.Ordinal).Take(maxEntries).Select(p => new MountEntry(p.Key, false, p.Value.Length, null, ETag(p.Value)))])
            : throw new MountSourceException($"{path}: pages have no folders");

    public Task<MountEntry?> StatAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(path == "/" ? new MountEntry("", true)
            : _pages.TryGetValue(path[1..], out var d) ? new MountEntry(path[1..], false, d.Length, null, ETag(d)) : null);

    public Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        var data = Page(path);
        return data.Length > maxBytes ? throw new MountSourceException($"{path}: too large") : Task.FromResult(data);
    }

    public Task<string> WriteAsync(string path, byte[] data, string? expectedVersion, CancellationToken cancellationToken)
    {
        var now = path.Length > 1 && _pages.TryGetValue(path[1..], out var d) ? ETag(d) : null;
        if (now != expectedVersion) throw new MountConflictException($"{path}: edited by someone else");
        if (path.Count(c => c == '/') != 1 || !path.EndsWith(".md")) throw new MountSourceException($"{path}: a page is a .md file at the top");
        _pages[path[1..]] = data;
        return Task.FromResult(ETag(data));
    }

    public Task DeleteAsync(string path, string? expectedVersion, CancellationToken cancellationToken)
    {
        if (ETag(Page(path)) != expectedVersion) throw new MountConflictException($"{path}: edited by someone else");
        _pages.Remove(path[1..]);
        return Task.CompletedTask;
    }
}
