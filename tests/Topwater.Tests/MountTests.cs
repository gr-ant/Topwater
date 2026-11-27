using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Topwater.Tests;

/// <summary>Mounted sources (docs/mounts.md): tests/mounts/cases.json, which the Python and TypeScript ports run too, then what
/// only this port checks: hostile sources, limits, snapshots, recordings and the local folder source.</summary>
public class MountTests
{
    static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(FindCases()))!;

    static string FindCases()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tests", "mounts", "cases.json"))) return Path.Combine(d.FullName, "tests", "mounts", "cases.json");
        throw new FileNotFoundException("tests/mounts/cases.json");
    }

    static Dictionary<string, object?> Files(JsonObject o) => o.ToDictionary(kv => kv.Key, kv => (object?)kv.Value?.GetValue<string>());

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
        public string Type => "hostile";
        public string Id => "h";
        public MountCapabilities Capabilities => MountCapabilities.None;

        public Task<IReadOnlyList<MountEntry>> ListAsync(string path)
        {
            List<MountEntry> bad = [new("", false), new(".", true), new("..", true), new("a/b", false), new("nul\0", false), new(new string('x', 256), false),
                new("ok", false, 3, Version: "1"), new("ok", false, 9, Version: "2"), new("neg", false, -1), null!];
            return Task.FromResult<IReadOnlyList<MountEntry>>([.. bad, .. Enumerable.Range(0, count).Select(i => new MountEntry($"z{i}", false, 1, Version: "v"))]);
        }

        public Task<MountEntry?> StatAsync(string path) => Task.FromResult<MountEntry?>(null);

        public Task<byte[]> ReadAsync(string path, long maxBytes) =>
            Task.FromResult(path == "/z0" ? new byte[maxBytes + 1] : "abc"u8.ToArray());
    }

    [Fact]
    public async Task HostileNamesAreSkippedAndListingIsBounded()
    {
        var sh = new Shell(new ShellOptions { MaxExtractEntries = 4 });
        await sh.MountAsync("/mnt/h", new Hostile());
        Assert.Equal("ok\nz0\nz1\nz2\n", (await sh.RunAsync("ls /mnt/h")).Stdout);
        Assert.Equal(["listing stopped at 4 entries", "skipped 9 entries with names that can't be files"], sh.Mounts[0].Warnings.Select(w => w.Split(": ", 2)[1]));
        var r = await sh.RunAsync("cat /mnt/h/ok; cat /mnt/h/z0");
        Assert.Equal("abc", r.Stdout);
        Assert.Equal("cat: /mnt/h/z0: File too large\n", r.Stderr);
        Assert.Equal(2, (await sh.RunAsync("mount -r /mnt/h")).Stderr.Split("mount: /mnt/h:").Length - 1);
    }

    static long FreshBytes() => new Shell().Fs.UsedBytes;

    [Fact]
    public async Task AFileBiggerThanTheDiskIsNotFetched()
    {
        var src = new MemorySource(new Dictionary<string, object?> { ["big.bin"] = new byte[5000], ["small.txt"] = "s\n" });
        var sh = new Shell(new ShellOptions { MaxFileSystemBytes = FreshBytes() + 4000 });
        await sh.MountAsync("/mnt/s", src);
        var r = await sh.RunAsync("cat /mnt/s/big.bin > /dev/null; cat /mnt/s/small.txt");
        Assert.Equal("s\n", r.Stdout);
        Assert.Contains("File too large", r.Stderr);
    }

    [Fact]
    public async Task FetchedFilesAreEvictedToMakeRoom()
    {
        var src = new MemorySource(new Dictionary<string, object?> { ["a"] = Encoding.ASCII.GetBytes(new string('a', 3000)), ["b"] = Encoding.ASCII.GetBytes(new string('b', 3000)) });
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
        var src = new MemorySource(new Dictionary<string, object?> { ["slow.txt"] = "s\n", ["fast.txt"] = "f\n" });
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
        var src = new MemorySource(new Dictionary<string, object?> { ["a.txt"] = "a\n" });
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src, MountMode.Staged);
        bool fail = true;
        src.Hook = (op, path) => fail && op is "read" or "write" ? throw new IOException("the service is down") : Task.CompletedTask;
        Assert.Equal("cat: /mnt/s/a.txt: the service is down\n", (await sh.RunAsync("cat /mnt/s/a.txt")).Stderr);
        fail = false;
        Assert.Equal("a\n", (await sh.RunAsync("cat /mnt/s/a.txt")).Stdout);
        await sh.RunAsync("echo b > /mnt/s/a.txt");
        fail = true;
        var r = await sh.ApplyChangesAsync("/mnt/s");
        Assert.Equal("/a.txt", r.Failed?.Path);
        Assert.Equal("the service is down", r.Error);
        Assert.False(r.Conflict);
        fail = false;
        Assert.Equal("/a.txt", (await sh.ApplyChangesAsync("/mnt/s")).Applied[0].Path);
        Assert.Equal("b\n", Encoding.UTF8.GetString(src.Tree()["/a.txt"]!));
    }

    [Fact]
    public async Task TheSourceNeverSeesPathsOutsideIt()
    {
        var src = new MemorySource(new Dictionary<string, object?> { ["a.txt"] = "a\n" });
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

    [Fact]
    public async Task SnapshotKeepsTheDefinitionAndTheChangesNotTheFiles()
    {
        var src = new MemorySource(new Dictionary<string, object?> { ["a.txt"] = "secret\n", ["b.txt"] = "b\n" });
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src, MountMode.Staged);
        await sh.RunAsync("cat /mnt/s/a.txt; echo new > /mnt/s/b.txt; rm /mnt/s/a.txt; cd /mnt/s");
        var buf = new MemoryStream();
        sh.Save(buf);
        var raw = buf.ToArray();
        Assert.DoesNotContain("secret", Unzipped(raw));
        var loaded = Shell.Load(new MemoryStream(raw));
        var info = loaded.Mounts[0];
        Assert.Equal(("/mnt/s", MountMode.Staged, "memory", "memory"), (info.Path, info.Mode, info.Type, info.Id));
        Assert.Equal("unavailable: no source for memory:memory", info.Error);
        Assert.Equal([MountChangeKind.Write, MountChangeKind.Delete], loaded.PendingChanges("/mnt/s").Select(c => c.Kind));
        Assert.Contains("Permission denied", (await loaded.RunAsync("ls /mnt/s")).Stderr);
        await loaded.MountAsync("/mnt/s", src, MountMode.Staged);
        Assert.Equal("b.txt\nnew\n", (await loaded.RunAsync("ls /mnt/s; cat /mnt/s/b.txt")).Stdout);
        Assert.Equal(sh.PendingChanges("/mnt/s"), loaded.PendingChanges("/mnt/s"));
        // restoring into a shell that has the source lists it again at the next call
        var other = new Shell();
        await other.MountAsync("/mnt/other", src);
        other.Restore(sh.Snapshot());
        Assert.Equal("new\n", (await other.RunAsync("cat /mnt/s/b.txt")).Stdout);
        // the source changed meanwhile: applying conflicts, against the version the change was made on
        src.Set("b.txt", "theirs\n");
        var r = await loaded.ApplyChangesAsync("/mnt/s");
        Assert.True(r.Conflict);
        Assert.Equal("/b.txt", r.Failed?.Path);
    }

    /// <summary>Every entry of a zip, inflated, as one string.</summary>
    static string Unzipped(byte[] raw)
    {
        using var z = new ZipArchive(new MemoryStream(raw));
        var sb = new StringBuilder();
        foreach (var e in z.Entries) { using var s = e.Open(); sb.Append(new StreamReader(s).ReadToEnd()); }
        return sb.ToString();
    }

    [Fact]
    public async Task ACommandInAMountDirectoryFetchesIt()
    {
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", new MemorySource(new Dictionary<string, object?> { ["d/a.txt"] = "hello\n" }));
        Assert.Equal("a.txt:hello\n", (await sh.RunAsync("cd /mnt/s/d && grep -r hello")).Stdout);
    }

    [Fact]
    public Task ARelativeMountPointIsRefused() => Assert.ThrowsAsync<ArgumentException>(() => new Shell().MountAsync("mnt/s", new MemorySource()));

    [Fact]
    public async Task TheTimeLimitStillEndsTheCallDuringARefresh()
    {
        var src = new MemorySource(new Dictionary<string, object?> { ["a"] = "a" });
        var sh = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(0.3) });
        await sh.MountAsync("/mnt/s", src);
        src.Hook = (_, _) => Task.Delay(1000);
        Assert.DoesNotContain("after", (await sh.RunAsync("mount -r /mnt/s; echo after")).Stdout);
    }

    [Fact]
    public async Task AWorkingDirectoryInsideAMountSurvivesARestore()
    {
        var src = new MemorySource(new Dictionary<string, object?> { ["d/a.txt"] = "a\n" });
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src);
        await sh.RunAsync("cd /mnt/s/d");
        var snap = sh.Snapshot();
        await sh.RunAsync("cd /");
        sh.Restore(snap);
        Assert.Equal("/mnt/s/d\na\n", (await sh.RunAsync("pwd; cat a.txt")).Stdout);
    }

    [Fact]
    public async Task ForkUsesTheSameSource()
    {
        var src = new MemorySource(new Dictionary<string, object?> { ["a.txt"] = "a\n" });
        var sh = new Shell();
        await sh.MountAsync("/mnt/s", src, MountMode.Staged);
        await sh.RunAsync("echo b >> /mnt/s/a.txt");
        var fork = sh.Fork();
        Assert.Equal("a\nb\n", (await fork.RunAsync("cat /mnt/s/a.txt")).Stdout);
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
        await sh.MountAsync("/mnt/s", new MemorySource(new Dictionary<string, object?> { ["a"] = "a" }), MountMode.Staged);
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
        var src = new MemorySource(new Dictionary<string, object?> { ["a.txt"] = "a\n", ["d/b.txt"] = "b\n" });
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
        var replayed = loaded.CreateShell();
        var r = await replayed.RunAsync("mount -r /mnt/s; cat /mnt/s/d/b.txt /mnt/s/c.txt");
        Assert.Equal("b\n", r.Stdout);
        Assert.Equal("cat: /mnt/s/c.txt: read /c.txt is not in the Topwater recording\n", r.Stderr);
    }

    [Fact]
    public async Task LocalFolderSourceWorks()
    {
        var tmp = Directory.CreateTempSubdirectory("topwater-mount-");
        try
        {
            var root = Path.Combine(tmp.FullName, "vault");
            Directory.CreateDirectory(Path.Combine(root, "notes"));
            File.WriteAllText(Path.Combine(root, "notes", "a.md"), "# A\n");
            File.WriteAllText(Path.Combine(root, "top.md"), "top\n");
            File.WriteAllText(Path.Combine(tmp.FullName, "outside.txt"), "private\n");
            File.CreateSymbolicLink(Path.Combine(root, "link.md"), Path.Combine(tmp.FullName, "outside.txt"));
            Directory.CreateSymbolicLink(Path.Combine(root, "up"), tmp.FullName);
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
            Assert.Equal("private\n", File.ReadAllText(Path.Combine(tmp.FullName, "outside.txt")));
            await sh.RunAsync("echo again >> /mnt/w/top.md");
            File.WriteAllText(Path.Combine(root, "top.md"), "edited on the host, longer\n");
            r = await sh.ApplyChangesAsync("/mnt/w");
            Assert.True(r.Conflict);
            Assert.Equal("/top.md", r.Failed?.Path);
            Assert.Equal("edited on the host, longer\n", File.ReadAllText(Path.Combine(root, "top.md")));

            // a link anywhere in a path, the last name included, is refused, never followed
            var source = new LocalFolderSource(root, writable: true);
            Assert.Null(await source.StatAsync("/link.md"));
            Assert.Null(await source.StatAsync("/up/outside.txt"));
            await Assert.ThrowsAsync<FileNotFoundException>(() => source.ReadAsync("/up/outside.txt", 100));
            await Assert.ThrowsAsync<FileNotFoundException>(() => source.ReadAsync("/link.md", 100));
            await Assert.ThrowsAsync<ArgumentException>(() => source.ReadAsync("/notes/../top.md", 100));
        }
        finally { tmp.Delete(recursive: true); }
    }
}
