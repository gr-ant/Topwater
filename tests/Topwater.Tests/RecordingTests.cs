using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Topwater.AI;
using Topwater.Replay;

namespace Topwater.Tests;

/// <summary>Recordings: capture, save/load, exact replay, difference reports, re-recording, and the docker target.</summary>
public class RecordingTests
{
    static ShellOptions Deterministic => new() { StartTime = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), RandomSeed = 42 };

    const string Mixed = "mkdir -p proj && cd proj && echo $RANDOM > r.txt && mktemp && date && ls -l";

    static async Task<Recording> Record(int snapshotEvery = 0)
    {
        var sh = new Shell(Deterministic);
        sh.Fs.WriteAllText("/home/agent/notes.txt", "alpha\nbeta\n");
        using var rec = sh.StartRecording(new RecordingOptions { Metadata = { ["model"] = "test-model", ["task"] = "demo" }, SnapshotEvery = snapshotEvery });
        await sh.RunAsync(Mixed);
        await BashTool.ExecuteAsync(sh, "x=7; sleep 5; cat ../notes.txt | grep -c a; f() { echo \"f$1\"; }");
        await sh.AsAIFunction().InvokeAsync(new AIFunctionArguments { ["command"] = "echo $x; f 1; nosuchcmd" });
        await sh.RunAsync("for i in 1 2 3; do echo $i; done | wc -l; exit 3");
        return rec.Recording;
    }

    [Fact]
    public async Task Captures_every_call_through_every_entry_point()
    {
        var r = await Record();
        Assert.Equal(4, r.Calls.Count);
        Assert.Equal([0, 1, 2, 3], r.Calls.Select(c => c.Index));
        Assert.Equal("test-model", r.Metadata["model"]);
        Assert.True(r.IsDeterministic);
        Assert.Equal("7\nf1\n", r.Calls[2].Stdout);
        Assert.Equal(127, r.Calls[2].ExitCode);
        Assert.Equal(3, r.Calls[3].ExitCode);
        Assert.Contains("alpha", (await r.CreateShell().RunAsync("cat notes.txt")).Stdout); // the start holds the app's files
    }

    [Fact]
    public async Task Stops_recording()
    {
        var sh = new Shell();
        var rec = sh.StartRecording();
        await sh.RunAsync("echo a");
        Assert.Throws<InvalidOperationException>(() => sh.StartRecording());
        rec.Stop();
        await sh.RunAsync("echo b");
        Assert.Single(rec.Recording.Calls);
        Assert.Null(sh.Recorder);
    }

    [Fact]
    public async Task Replays_exactly_on_a_fresh_shell()
    {
        var r = await Record(snapshotEvery: 2);
        var report = await r.ReplayAsync();
        Assert.True(report.Matches, report.ToString());
        Assert.Equal(4, report.Calls.Count);
        Assert.StartsWith("replay on topwater", report.ToString());
    }

    static byte[] Bytes(Recording r)
    {
        var ms = new MemoryStream();
        r.Save(ms);
        return ms.ToArray();
    }

    static string Entry(byte[] file, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(file));
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    /// <summary>A saved .topwater file with its zip edited.</summary>
    static byte[] Tamper(byte[] file, Action<ZipArchive> edit)
    {
        var ms = new MemoryStream();
        ms.Write(file);
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true)) edit(zip);
        return ms.ToArray();
    }

    static void Put(ZipArchive zip, string name, string text)
    {
        zip.GetEntry(name)?.Delete();
        using var w = new StreamWriter(zip.CreateEntry(name).Open());
        w.Write(text);
    }

    /// <summary>The file with history.jsonl's header line edited.</summary>
    static byte[] TamperHeader(byte[] file, Action<JsonNode> edit)
    {
        var lines = Entry(file, "history.jsonl").Split('\n');
        var header = JsonNode.Parse(lines[0])!;
        edit(header);
        lines[0] = header.ToJsonString();
        return Tamper(file, z => Put(z, "history.jsonl", string.Join('\n', lines)));
    }

    static Recording LoadBytes(byte[] bytes, ShellOptions? limits = null) => Recording.Load(new MemoryStream(bytes), limits: limits);

    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "../../../recordings", name);

    [Fact]
    public async Task Saves_and_loads_a_topwater_file()
    {
        var r = await Record(snapshotEvery: 2);
        var file = Bytes(r);
        using (var zip = new ZipArchive(new MemoryStream(file)))
        {
            var names = zip.Entries.Select(e => e.FullName).Where(n => !n.StartsWith("blobs/")).Order(StringComparer.Ordinal);
            Assert.Equal(["checkpoints/0.json", "checkpoints/2.json", "checkpoints/4.json", "history.jsonl", "manifest.json"], names);
            // blobs are shared: the start's notes.txt is stored once for every checkpoint
            Assert.Single(zip.Entries, e => e.FullName.StartsWith("blobs/") && new StreamReader(e.Open()).ReadToEnd() == "alpha\nbeta\n");
        }
        var lines = Entry(file, "history.jsonl").TrimEnd('\n').Split('\n');
        Assert.Equal(5, lines.Length); // header + one line per call
        Assert.StartsWith("{\"format\":\"topwater-history\",\"version\":1,", lines[0]);
        Assert.Contains("\"startTime\":\"2026-03-04T05:06:07.0000000Z\"", lines[0]);
        Assert.Contains("\"randomSeed\":42", lines[0]);
        Assert.Contains("\"time\":\"2026-03-04T05:06:07.0000000Z\"", lines[1]);
        Assert.Contains("\"files\":[\"/home/agent/proj\",\"/home/agent/proj/r.txt\",\"/tmp/tmp.72SYu7MDwn\"]", lines[1]);
        Assert.Contains("\"time\":\"2026-03-04T05:06:07.0000000Z\"", lines[2]); // the virtual clock moves only with sleep
        Assert.Contains("\"time\":\"2026-03-04T05:06:12.0000000Z\"", lines[3]);
        Assert.DoesNotContain("\"files\"", lines[2]);
        Assert.Equal(Entry(file, "checkpoints/4.json"), Entry(file, "manifest.json"));

        var loaded = LoadBytes(file);
        AssertSame(r, loaded);
        Assert.Equal(r.Calls.Select(c => c.Time), loaded.Calls.Select(c => c.Time));
        Assert.Equal(r.Calls.Select(c => string.Join(' ', c.Files ?? [])), loaded.Calls.Select(c => string.Join(' ', c.Files ?? [])));
        Assert.True((await loaded.ReplayAsync()).Matches);
        // a stream that can't seek, and a path
        AssertSame(r, Recording.Load(new SnapshotTests.ForwardOnly(file)));
        var path = Path.Combine(Directory.CreateTempSubdirectory("topwater-rec-").FullName, "run.topwater");
        try
        {
            r.Save(path);
            AssertSame(r, Recording.Load(path));
            // as a snapshot it is the machine after the last call
            using var f = File.OpenRead(path);
            Assert.Equal("/home/agent/proj\n", (await Shell.Load(f).RunAsync("pwd")).Stdout);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public async Task Lists_the_files_each_call_changed()
    {
        var sh = new Shell(Deterministic); // the clock stands still, so only the calls' own changes count
        sh.Fs.WriteAllText("/home/agent/a", "1");
        using var rec = sh.StartRecording();
        await sh.RunAsync("cat a; ls");
        await sh.RunAsync("echo 2 > a; mkdir d; ln -s a d/l");
        await sh.RunAsync("rm a; chmod 700 d");
        Assert.Null(rec.Recording.Calls[0].Files is { Count: > 0 } ? "changed" : null);
        Assert.Equal(["/home/agent/a", "/home/agent/d", "/home/agent/d/l"], rec.Recording.Calls[1].Files!);
        Assert.Equal(["/home/agent/a", "/home/agent/d"], rec.Recording.Calls[2].Files!);
        // only the last call keeps its snapshot (the end state) unless SnapshotEvery or SnapshotNow asks for more
        Assert.Equal([false, false, true], rec.Recording.Calls.Select(c => c.SnapshotAfter != null));
    }

    [Fact]
    public async Task A_snapshot_is_a_recording_with_no_calls()
    {
        var sh = new Shell();
        await sh.RunAsync("cd /tmp; x=1");
        var ms = new MemoryStream();
        sh.Save(ms);
        var r = LoadBytes(ms.ToArray());
        Assert.Empty(r.Calls);
        Assert.Equal("/tmp 1\n", (await (await r.ShellAtAsync(-1)).RunAsync("echo $PWD $x")).Stdout);
    }

    [Theory]
    // anything but InvalidDataException escaped the old format's reader, and the CLI crashed with a stack trace
    [InlineData("web", "{\"domains\":[null]}")]
    [InlineData("web", "{\"domains\":[\"a.example\"],\"methods\":[null]}")]
    [InlineData("appCommands", "[null]")]
    public void Old_json_lines_recordings_with_nulls_are_invalid(string key, string json)
    {
        var lines = File.ReadAllLines(Fixture("basics.jsonl"));
        var header = JsonNode.Parse(lines[0])!;
        header[key] = JsonNode.Parse(json);
        lines[0] = header.ToJsonString();
        Assert.Throws<InvalidDataException>(() => Recording.Load(new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines)))));
    }

    [Fact]
    public async Task Loads_old_json_lines_recordings_embedded_and_side_car()
    {
        var embedded = Recording.Load(Fixture("basics.jsonl"));
        Assert.Equal(6, embedded.Calls.Count);
        Assert.Null(embedded.Calls[0].Files);
        Assert.True((await embedded.ReplayAsync()).Matches);

        // the side-car layout: snapshots as zips in <name>.snapshots/, the lines naming them
        var dir = Directory.CreateTempSubdirectory("topwater-rec-").FullName;
        try
        {
            var lines = File.ReadAllLines(Fixture("basics.jsonl"));
            Directory.CreateDirectory(Path.Combine(dir, "run.snapshots"));
            for (int i = 0; i < lines.Length; i++)
            {
                var node = JsonNode.Parse(lines[i])!;
                var key = i == 0 ? "start" : "snapshot";
                if (node[key]?["embedded"] is not { } b64) continue;
                File.WriteAllBytes(Path.Combine(dir, "run.snapshots", $"{i}.zip"), Convert.FromBase64String((string)b64!));
                node[key] = new JsonObject { ["file"] = $"run.snapshots/{i}.zip" };
                lines[i] = node.ToJsonString();
            }
            var path = Path.Combine(dir, "run.jsonl");
            File.WriteAllLines(path, lines);
            var side = Recording.Load(path);
            Assert.Equal(embedded.Calls.Select(c => c with { SnapshotAfter = null }), side.Calls.Select(c => c with { SnapshotAfter = null }));
            Assert.True((await side.ReplayAsync()).Matches);
            Assert.Throws<InvalidDataException>(() => Recording.Load(File.OpenRead(path))); // side-car refs need the path

            // converting: Save writes the same recording as a .topwater file
            var converted = LoadBytes(Bytes(side));
            AssertSame(side, converted);
            Assert.True((await converted.ReplayAsync()).Matches);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static void AssertSame(Recording a, Recording b)
    {
        static RecordedCall Plain(RecordedCall c) => c with { Elapsed = default, SnapshotAfter = null, Files = null };
        Assert.Equal(a.Calls.Select(Plain), b.Calls.Select(Plain));
        Assert.Equal(a.Calls.Select(c => c.SnapshotAfter != null), b.Calls.Select(c => c.SnapshotAfter != null));
        Assert.Equal(a.Metadata, b.Metadata);
        Assert.Equal(a.StartTime, b.StartTime);
        Assert.Equal(a.RandomSeed, b.RandomSeed);
        Assert.Equal(a.MaxCallDuration, b.MaxCallDuration);
        Assert.Equal(a.User, b.User);
    }

    [Fact]
    public async Task Refuses_newer_versions_and_garbage()
    {
        Assert.Throws<NotSupportedException>(() => Recording.Load(new MemoryStream("{\"format\":\"topwater-recording\",\"version\":99}\n"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => Recording.Load(new MemoryStream("{\"format\":\"other\",\"version\":1}\n"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => Recording.Load(new MemoryStream("not json\n"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => Recording.Load(new MemoryStream("PK not a zip"u8.ToArray())));
        var file = Bytes(await Record());
        var e = Assert.Throws<NotSupportedException>(() => LoadBytes(TamperHeader(file, h => { h["version"] = 2; h["options"] = "changed"; })));
        Assert.Equal("history format version 2 is newer than this version of Topwater supports (1); upgrade Topwater to read it", e.Message);
        Assert.Contains("not a topwater-history header", Assert.Throws<InvalidDataException>(() => LoadBytes(TamperHeader(file, h => h["format"] = "topwater-recording"))).Message);
        Assert.Contains("line 6 is not JSON", Assert.Throws<InvalidDataException>(() => LoadBytes(Tamper(file, z => Put(z, "history.jsonl", Entry(file, "history.jsonl") + "{oops\n")))).Message);
        Assert.Contains("checkpoints/0.json (the start) is missing", Assert.Throws<InvalidDataException>(() => LoadBytes(Tamper(file, z => z.GetEntry("checkpoints/0.json")!.Delete()))).Message);
        Assert.Contains("past the last call", Assert.Throws<InvalidDataException>(() => LoadBytes(Tamper(file, z => Put(z, "checkpoints/9.json", Entry(file, "checkpoints/0.json"))))).Message);
        foreach (var name in new[] { "checkpoints/01.json", "checkpoints/-1.json", "checkpoints/1.json/x", "checkpoints/١.json", "checkpoints/1.zip" })
            Assert.Contains("unexpected entry", Assert.Throws<InvalidDataException>(() => LoadBytes(Tamper(file, z => Put(z, name, "{}")))).Message);
        // a checkpoint is checked like any snapshot
        Assert.Contains("not a valid Topwater snapshot: bad cwd", Assert.Throws<InvalidDataException>(() =>
            LoadBytes(Tamper(file, z => Put(z, "checkpoints/0.json", Entry(file, "checkpoints/0.json").Replace("\"cwd\":\"/home/agent\"", "\"cwd\":\"../x\""))))).Message);
    }

    [Fact]
    public async Task Limits_in_the_file_can_lower_but_never_raise_the_replay_shells()
    {
        var file = Bytes(await Record());
        var raised = LoadBytes(TamperHeader(file, h =>
        {
            var o = h["options"]!;
            o["maxLoopIterations"] = int.MaxValue; o["maxCallDurationMs"] = long.MaxValue / 2; o["maxFileSystemBytes"] = 1L << 50;
            o["maxStringBytes"] = 1L << 50; o["maxExtractBytes"] = -5; o["maxExtractEntries"] = 10;
        }));
        var d = new ShellOptions();
        Assert.Equal(d.MaxLoopIterations, raised.MaxLoopIterations);
        Assert.Equal(d.MaxCallDuration, raised.MaxCallDuration);
        Assert.Equal(d.MaxFileSystemBytes, raised.MaxFileSystemBytes);
        Assert.Equal(d.MaxStringBytes, raised.MaxStringBytes);
        Assert.Equal(0, raised.MaxExtractBytes);    // lowered, never below zero
        Assert.Equal(10, raised.MaxExtractEntries); // lowered
        var tight = LoadBytes(file, new ShellOptions { MaxLoopIterations = 50 });
        Assert.Equal(50, tight.CreateShell().Options.MaxLoopIterations);

        // no time limit, in the caller's limits or the file's
        var unlimited = new ShellOptions { MaxCallDuration = Timeout.InfiniteTimeSpan };
        Assert.Equal(TimeSpan.FromMinutes(2), LoadBytes(file, unlimited).MaxCallDuration);
        Assert.Equal(TimeSpan.FromSeconds(3), LoadBytes(TamperHeader(file, h => h["options"]!["maxCallDurationMs"] = 3000), unlimited).MaxCallDuration);
        var none = new Recording(new Shell().Snapshot(), unlimited);
        Assert.Equal(Timeout.InfiniteTimeSpan, LoadBytes(Bytes(none), unlimited).MaxCallDuration);
        Assert.Equal(d.MaxCallDuration, LoadBytes(Bytes(none)).MaxCallDuration);
        Assert.Equal(TimeSpan.FromMinutes(2), Recording.Load(Fixture("basics.jsonl"), unlimited).MaxCallDuration);
    }

    [Theory]
    [InlineData("/etc/x.zip")]
    [InlineData("../x.zip")]
    [InlineData("a/../../x.zip")]
    [InlineData("C:x.zip")]
    [InlineData("C:\\Windows\\x.zip")]
    [InlineData("a\\..\\..\\x.zip")]
    [InlineData("\\\\server\\share\\x.zip")]
    [InlineData("https://evil.example/x.zip")]
    [InlineData("file:///etc/x.zip")]
    [InlineData("%2e%2e/x.zip")]
    [InlineData(".%2e/x.zip")]
    [InlineData(" ../x.zip")]
    [InlineData("\t../x.zip")]
    [InlineData("x.zip?y=1")]
    [InlineData("x.zip#f")]
    [InlineData("./x.zip")]
    [InlineData("a//x.zip")]
    [InlineData("x.zip\n")]
    [InlineData("")]
    public void Side_car_names_must_be_plain_relative_names(string path)
    {
        var header = JsonNode.Parse(File.ReadLines(Fixture("basics.jsonl")).First())!;
        header["start"] = new JsonObject { ["file"] = path };
        var opened = false;
        var e = Assert.Throws<InvalidDataException>(() => Recording.Load(new MemoryStream(Encoding.UTF8.GetBytes(header.ToJsonString() + "\n")), _ => { opened = true; return new MemoryStream(); }));
        Assert.False(opened);
        Assert.Contains("is not a plain relative name", e.Message);
    }

    [Fact]
    public async Task Caps_the_number_and_total_size_of_snapshots()
    {
        // old recordings: snapshots embedded in the lines
        var lines = File.ReadAllLines(Fixture("basics.jsonl"));
        var withSnapshot = lines.Skip(1).First(l => l.Contains("\"snapshot\""));
        string Text(int calls) => lines[0] + "\n" + string.Concat(Enumerable.Repeat(withSnapshot + "\n", calls));
        Assert.Contains("more than 1000 snapshots", Assert.Throws<InvalidDataException>(() => Recording.Load(new MemoryStream(Encoding.UTF8.GetBytes(Text(Recording.MaxSnapshots))))).Message);
        Assert.Equal(Recording.MaxSnapshots - 1, Recording.Load(new MemoryStream(Encoding.UTF8.GetBytes(Text(Recording.MaxSnapshots - 1)))).Calls.Count);

        // .topwater files: checkpoints
        var sh = new Shell();
        var rec = sh.StartRecording();
        for (int i = 0; i < Recording.MaxSnapshots; i++) await sh.RunAsync("true");
        rec.Stop();
        var file = Bytes(rec.Recording);
        var start = Entry(file, "checkpoints/0.json");
        byte[] WithCheckpoints(int n) => Tamper(file, z => { for (int i = 1; i < n - 1; i++) Put(z, $"checkpoints/{i}.json", start); }); // with 0 and the last call's
        Assert.Equal(Recording.MaxSnapshots, LoadBytes(WithCheckpoints(Recording.MaxSnapshots)).Calls.Count);
        Assert.Contains("more than 1000 checkpoints", Assert.Throws<InvalidDataException>(() => LoadBytes(WithCheckpoints(Recording.MaxSnapshots + 1))).Message);

        // each snapshot is within the limit, all of them together are not
        var big = new Shell(new ShellOptions { MaxFileSystemBytes = 4096 });
        big.Fs.WriteAllText("/home/agent/big.txt", new string('x', 1500));
        using (var r = big.StartRecording(new RecordingOptions { SnapshotEvery = 1 }))
        {
            for (int i = 0; i < 12; i++) await big.RunAsync("true");
            var e = Assert.Throws<InvalidDataException>(() => LoadBytes(Bytes(r.Recording), new ShellOptions { MaxFileSystemBytes = 4096 }));
            Assert.Contains("MaxFileSystemBytes", e.Message);
        }
    }

    /// <summary>A fixture with history.jsonl replaced by <paramref name="write"/>'s output, deflated as small as it goes.</summary>
    static byte[] WithHistory(Action<Stream> write) => Tamper(File.ReadAllBytes(Fixture("from-csharp.topwater")), z =>
    {
        z.GetEntry("history.jsonl")!.Delete();
        using var s = z.CreateEntry("history.jsonl", CompressionLevel.SmallestSize).Open();
        write(s);
    });

    static void Repeat(Stream s, byte[] chunk, long count)
    {
        var block = Enumerable.Repeat(chunk, 65536 / chunk.Length).SelectMany(b => b).ToArray();
        long left = count * chunk.Length;
        for (; left >= block.Length; left -= block.Length) s.Write(block);
        s.Write(block, 0, (int)left);
    }

    static void RefusedFast(byte[] file, string message, ShellOptions? limits = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => LoadBytes(file, limits)).Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void A_history_that_inflates_far_is_refused_early()
    {
        var header = Encoding.UTF8.GetBytes(Entry(File.ReadAllBytes(Fixture("from-csharp.topwater")), "history.jsonl").Split('\n')[0] + "\n");
        // 236 MB of newlines (a ~240 KB file), and 15.7 million tiny calls
        var newlines = WithHistory(s => { s.Write(header); Repeat(s, "\n"u8.ToArray(), 236L << 20); });
        Assert.True(newlines.Length < 1 << 20);
        RefusedFast(newlines, "more than 100000 calls (MaxExtractEntries)");
        RefusedFast(WithHistory(s => { s.Write(header); Repeat(s, "{\"command\":\"\"}\n"u8.ToArray(), 15_728_625L); }), "more than 100000 calls");
        // one huge line stops at the byte cap: 2 x MaxStringBytes
        var limits = new ShellOptions { MaxStringBytes = 1 << 20 };
        RefusedFast(WithHistory(s => { s.Write(header); Repeat(s, "x"u8.ToArray(), 8 << 20); }), "history.jsonl is larger than 2097152 bytes", limits);
        // exactly the cap of calls still loads
        var few = new ShellOptions { MaxExtractEntries = 1000 };
        Assert.Equal(1000, LoadBytes(WithHistory(s => { s.Write(header); Repeat(s, "{\"command\":\"true\"}\n"u8.ToArray(), 1000); }), few).Calls.Count);
        RefusedFast(WithHistory(s => { s.Write(header); Repeat(s, "{\"command\":\"true\"}\n"u8.ToArray(), 1001); }), "more than 1000 calls", few);
        // and in an old .jsonl, the same count
        var old = File.ReadAllLines(Fixture("basics.jsonl"));
        var text = old[0] + "\n" + new string('\n', 2000);
        Assert.Contains("more than 1000 calls", Assert.Throws<InvalidDataException>(() => Recording.Load(new MemoryStream(Encoding.UTF8.GetBytes(text)), limits: few)).Message);
    }

    [Fact]
    public async Task Checkpoints_share_a_budget_of_entries()
    {
        // directories cost no bytes: the entries are budgeted too, 4 x MaxExtractEntries over all checkpoints
        var limits = new ShellOptions { MaxExtractEntries = 2000 };
        var sh = new Shell();
        Assert.Equal(0, (await sh.RunAsync("mkdir d && cd d && for i in $(seq 1500); do mkdir d$i; done")).ExitCode);
        var rec = sh.StartRecording(new RecordingOptions { SnapshotEvery = 1 });
        for (int i = 0; i < 4; i++) await sh.RunAsync("true");
        rec.Stop();
        var file = Bytes(rec.Recording);
        RefusedFast(file, "files and directories together (4 x MaxExtractEntries)", limits);
        Assert.Equal(4, LoadBytes(file, new ShellOptions { MaxExtractEntries = 3000 }).Calls.Count);

        // one checkpoint's data under many names: refused at the budget, not after building them all
        var start = Entry(file, "checkpoints/0.json");
        var calls = Enumerable.Range(0, 999).Select(i => rec.Recording.Calls[0] with { Index = i, SnapshotAfter = null }).ToList();
        var big = new Recording(rec.Recording.Start, new ShellOptions());
        big.Calls.AddRange(calls);
        RefusedFast(Tamper(Bytes(big), z => { for (int i = 1; i < 1000; i++) Put(z, $"checkpoints/{i}.json", start); }), "files and directories together", limits);
    }

    [Fact]
    public async Task Checkpoints_are_built_only_when_a_replay_needs_one()
    {
        // 60 checkpoints of 1,500 directories: each is checked against the limits on load, none is built
        var sh = new Shell(Deterministic);
        Assert.Equal(0, (await sh.RunAsync("mkdir d && cd d && for i in $(seq 1500); do mkdir d$i; done")).ExitCode);
        var rec = sh.StartRecording(new RecordingOptions { SnapshotEvery = 1 });
        for (int i = 0; i < 60; i++) await sh.RunAsync($"echo {i} > /home/agent/f{i}");
        rec.Stop();
        var file = Bytes(rec.Recording);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var loaded = LoadBytes(file);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.All(loaded.Calls, c => Assert.NotNull(c.SnapshotAfter));
        Assert.Equal("59\n", (await (await loaded.ShellAtAsync(59)).RunAsync("cat /home/agent/f59")).Stdout);
        // every checkpoint built in turn, a few kept: the same machines, and the file saves back the same
        for (int i = 0; i < 60; i++) Assert.Equal($"{i}\n", (await (await loaded.ShellAtAsync(i)).RunAsync($"cat /home/agent/f{i}")).Stdout);
        Assert.Equal(Entry(file, "checkpoints/31.json"), Entry(Bytes(loaded), "checkpoints/31.json"));

        // so a checkpoint that is wrong inside (a blob that doesn't match) fails when it is built, as InvalidDataException
        var blob = "blobs/" + System.Text.RegularExpressions.Regex.Match(Entry(file, "checkpoints/30.json"), "\"path\":\"/home/agent/f29\"[^}]*\"blob\":\"([0-9a-f]+)\"").Groups[1].Value;
        var tampered = LoadBytes(Tamper(file, z => Put(z, blob, "XX\n")));
        Assert.Contains("doesn't match its hash", (await Assert.ThrowsAsync<InvalidDataException>(() => tampered.ShellAtAsync(40))).Message);
    }

    [Fact]
    public async Task A_recording_larger_than_a_snapshot_opens_as_one()
    {
        // checkpoints make a file up to four times a snapshot: Shell.Load reads its machine (manifest.json) all the same
        var limits = new ShellOptions { MaxFileSystemBytes = 400_000, MaxStringBytes = 4_000, MaxExtractEntries = 500 };
        var sh = new Shell(new ShellOptions { StartTime = Deterministic.StartTime, RandomSeed = 1 });
        using var rec = sh.StartRecording(new RecordingOptions { SnapshotEvery = 1 });
        var random = new Random(7);
        for (int i = 0; i < 5; i++)
        {
            var noise = new byte[300_000];
            random.NextBytes(noise);
            sh.Fs.WriteAllBytes("/home/agent/noise", noise);
            await sh.RunAsync("true");
        }
        rec.Stop();
        var file = Bytes(rec.Recording);
        Assert.True(file.Length > ShellSnapshot_MaxArchiveBytes(limits), $"{file.Length} bytes");
        Assert.Equal(5, LoadBytes(file, limits).Calls.Count);
        var end = Shell.Load(new MemoryStream(file), limits);
        Assert.Equal((await sh.RunAsync("md5sum noise")).Stdout, (await end.RunAsync("md5sum noise")).Stdout);
    }

    static long ShellSnapshot_MaxArchiveBytes(ShellOptions o) => 2 * o.MaxFileSystemBytes + 4 * o.MaxStringBytes + 1024L * o.MaxExtractEntries;

    [Fact]
    public async Task Manifest_must_be_the_last_checkpoint()
    {
        var file = Bytes(await Record(snapshotEvery: 2));
        // a reader taking the machine from manifest.json and one replaying to the last checkpoint must see the same
        Assert.Contains("manifest.json is not the machine of checkpoints/4.json, the last checkpoint",
            Assert.Throws<InvalidDataException>(() => LoadBytes(Tamper(file, z => Put(z, "manifest.json", Entry(file, "checkpoints/2.json"))))).Message);
        Assert.Contains("manifest.json is missing", Assert.Throws<InvalidDataException>(() => LoadBytes(Tamper(file, z => z.GetEntry("manifest.json")!.Delete()))).Message);
        Assert.Contains("manifest.json is not a Topwater snapshot manifest", Assert.Throws<InvalidDataException>(() => LoadBytes(Tamper(file, z => Put(z, "manifest.json", "{}")))).Message);
        // the same machine written another way (key order, spacing) is the same machine
        var reordered = JsonNode.Parse(Entry(file, "manifest.json"))!.AsObject();
        var flipped = new JsonObject(reordered.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        Assert.Equal(4, LoadBytes(Tamper(file, z => Put(z, "manifest.json", flipped.ToJsonString(new() { WriteIndented = true })))).Calls.Count);
    }

    [Fact]
    public async Task Comparing_the_manifest_with_the_last_checkpoint_builds_no_tree_of_what_it_ignores()
    {
        // a JSON node per value is ~50 bytes: a 432 KB file of padded manifests once ran the host out of memory
        var file = Bytes(await Record(snapshotEvery: 2));
        var padded = JsonNode.Parse(Entry(file, "manifest.json"))!.AsObject().ToJsonString()[..^1] + ",\"x\":[" + string.Join(',', Enumerable.Repeat("0", 4_000_000)) + "]}";
        var tampered = Tamper(file, z => Put(z, "manifest.json", padded));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(4, LoadBytes(tampered).Calls.Count); // what no reader reads is no difference
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 150_000_000, $"{(GC.GetAllocatedBytesForCurrentThread() - before) >> 20} MB");
    }

    [Fact]
    public async Task Changed_files_come_from_the_filesystem_and_match_comparing_snapshots()
    {
        // the recorder tracks changed nodes; a rerecord compares whole snapshots: the lists must be the same
        var sh = new Shell(Deterministic);
        await sh.RunAsync("mkdir -p a/b c && echo 1 > a/b/x && echo 2 > a/y && ln a/y c/h && ln -s ../a c/s");
        var rec = sh.StartRecording();
        foreach (var command in new[]
        {
            "cat a/y; ls -R", "echo 3 > a/b/x", "echo 3 > a/b/x", "mkdir n && cp -r a n/", "mv a/b c/b", "mv n/a c/b2 && rm -r n",
            "mkdir -p m/b && mv c/b m/ 2>&1; mv -T c/b2 m/b 2>&1; ls m/b", "ln a/y a/z && rm c/h", "mv a/y a/zz", "echo 4 >> a/z",
            "chmod 700 c && touch -d 2020-01-01 c/s", "rm -r m && mkdir m && echo q > m/b", "cd c && rm -rf ../a && mkdir ../a", "true",
            "rm -rf /home/agent/* && mkdir a", "for i in 1 2 3; do echo $i > f$i; done; mv f1 f4; ln f2 f5; rm f2",
        })
            await sh.RunAsync(command);
        rec.Stop();
        var again = await rec.Recording.RerecordAsync(new ShellTarget(rec.Recording.CreateShell()));
        for (int i = 0; i < again.Calls.Count; i++)
            Assert.True(again.Calls[i].Files!.SequenceEqual(rec.Recording.Calls[i].Files ?? []),
                $"call {i}: snapshots say {string.Join(" ", again.Calls[i].Files!)}; the recorder says {string.Join(" ", rec.Recording.Calls[i].Files ?? [])}");
        Assert.Contains("/home/agent/a/zz", rec.Recording.Calls[8].Files!);
        Assert.Equal([], rec.Recording.Calls[2].Files!); // the same content again, on a clock that stands still
    }

    [Fact]
    public async Task Refuses_the_values_every_port_refuses()
    {
        var file = Bytes(await Record());
        var lines = Entry(file, "history.jsonl").TrimEnd('\n').Split('\n');
        byte[] Line(int i, Action<JsonNode> edit)
        {
            var copy = (string[])lines.Clone();
            var node = JsonNode.Parse(copy[i])!;
            edit(node);
            copy[i] = node.ToJsonString();
            return Tamper(file, z => Put(z, "history.jsonl", string.Join('\n', copy) + "\n"));
        }
        string Refused(byte[] f) => Assert.Throws<InvalidDataException>(() => LoadBytes(f)).Message;
        Assert.Contains("line 2: bad index", Refused(Line(1, c => c["index"] = -1)));
        Assert.Contains("line 2 is not JSON", Refused(Line(1, c => c["index"] = 1.5)));
        Assert.Contains("line 2 is not JSON", Refused(Line(1, c => c["index"] = "1")));
        Assert.Contains("line 3: bad elapsedMs", Refused(Line(2, c => c["elapsedMs"] = -1)));
        Assert.Contains("line 3: bad elapsedMs", Refused(Line(2, c => c["elapsedMs"] = 1e300)));
        Assert.Contains("line 2 is not JSON", Refused(Line(1, c => c["files"] = new JsonObject())));
        Assert.Contains("line 2: null in files", Refused(Line(1, c => c["files"] = new JsonArray(null, "/x"))));
        Assert.Contains("line 2: bad time \"2026\"", Refused(Line(1, c => c["time"] = "2026")));
        Assert.Contains("bad startTime", Refused(Line(0, h => h["options"]!["startTime"] = "3/4/2026")));
        Assert.Contains("bad header", Refused(Line(0, h => h["options"]!["maxFileSystemBytes"] = 1.5)));
        Assert.Contains("bad header", Refused(Line(0, h => h["options"]!["randomSeed"] = 1.5)));
        // a byte order mark is not part of the format
        Assert.Contains("the header is not JSON", Refused(Tamper(file, z => Put(z, "history.jsonl", "\uFEFF" + string.Join('\n', lines) + "\n"))));
        // messages name the field, not .NET's types
        var wrongType = Refused(Line(1, c => c["command"] = 5));
        Assert.Contains("has the wrong type", wrongType);
        Assert.DoesNotContain("Topwater.", wrongType);
        Assert.StartsWith("not a valid Topwater recording: ", Refused(Line(0, h => h["created"] = "yesterday")));
    }

    [Fact]
    public async Task Refuses_duplicate_entries_bad_header_names_and_a_directory_that_holds_more_than_it_says()
    {
        var file = Bytes(await Record());
        // two entries of one name: a reader taking the first and one taking the last would see different sessions
        var twice = Tamper(file, z => { using var w = new StreamWriter(z.CreateEntry("history.jsonl").Open()); w.Write("{}"); });
        Assert.Contains("entry \"history.jsonl\" appears twice", Assert.Throws<InvalidDataException>(() => LoadBytes(twice)).Message);
        Assert.Contains("appears twice", Assert.Throws<InvalidDataException>(() => ShellSnapshot.Load(new MemoryStream(twice))).Message);

        foreach (var (field, value) in new[] { ("user", "../../x"), ("user", "agent\n"), ("hostname", "a b") })
            Assert.Contains($"bad {field}", Assert.Throws<InvalidDataException>(() => LoadBytes(TamperHeader(file, h => h["options"]![field] = value))).Message);

        // the end record says 3 entries; the directory holds 1,100
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            for (int i = 0; i < 1100; i++) zip.CreateEntry($"x/{i}");
        var lying = ms.ToArray();
        BitConverter.GetBytes((ushort)3).CopyTo(lying, lying.Length - 22 + 8);
        BitConverter.GetBytes((ushort)3).CopyTo(lying, lying.Length - 22 + 10);
        Assert.Contains("more than 1006 zip entries", Assert.Throws<InvalidDataException>(() => LoadBytes(lying, new ShellOptions { MaxExtractEntries = 1 })).Message);
    }

    [Fact]
    public async Task Saving_to_a_path_replaces_the_file_whole()
    {
        var dir = Directory.CreateTempSubdirectory("topwater-rec-").FullName;
        try
        {
            var path = Path.Combine(dir, "run.topwater");
            File.Copy(Fixture("from-csharp.topwater"), path);
            var sh = new Shell();
            using var rec = sh.StartRecording();
            await sh.RunAsync("echo x > a; ln a b");
            rec.Stop();
            Recording.Load(path).Save(path); // load then save onto itself
            rec.Recording.Save(path);
            var back = Recording.Load(path);
            back.Save(path);
            var end = new Shell();
            end.Restore(Recording.Load(path).Calls[0].SnapshotAfter!);
            Assert.Equal("x\n2\n", (await end.RunAsync("cat b; stat -c %h a")).Stdout);
            Assert.Equal(["run.topwater"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Counts_zip_entries_before_listing_them()
    {
        var file = Bytes(await Record());
        // the end-of-central-directory record claims far more entries than the limits allow
        int eocd = file.Length - 22;
        Assert.Equal(0x06054b50u, BitConverter.ToUInt32(file, eocd));
        BitConverter.GetBytes((ushort)60000).CopyTo(file, eocd + 8);
        BitConverter.GetBytes((ushort)60000).CopyTo(file, eocd + 10);
        var limits = new ShellOptions { MaxExtractEntries = 10 };
        Assert.Contains("60000 zip entries, more than 1042", Assert.Throws<InvalidDataException>(() => LoadBytes(file, limits)).Message);
        Assert.Contains("60000 zip entries", Assert.Throws<InvalidDataException>(() => ShellSnapshot.Load(new MemoryStream(file), limits)).Message);
    }

    [Fact]
    public async Task Reports_differences_per_call()
    {
        var r = await Record();
        r.Calls[1] = r.Calls[1] with { Stdout = "999\n" };
        r.Calls[3] = r.Calls[3] with { ExitCode = 0, Stderr = "boom\n" };
        var report = await r.ReplayAsync();
        Assert.False(report.Matches);
        Assert.Equal([(1, "stdout"), (3, "exitCode"), (3, "stderr")], report.Differences.Select(d => (d.Index, d.Field)));
        var text = report.ToString();
        Assert.Contains("2/4 calls match", text);
        Assert.Contains("exit code: expected 0, got 3", text);
        Assert.Contains("stdout line 1: expected \"999\"", text);

        var stopped = await r.ReplayAsync(new ReplayOptions { StopAfter = 1 });
        Assert.Equal(2, stopped.Calls.Count);
        Assert.Contains("stopped after 2", stopped.ToString());

        var exitOnly = await r.ReplayAsync(new ReplayOptions { Fields = ReplayFields.Stdout });
        Assert.Equal([1], exitOnly.Differences.Select(d => d.Index));
    }

    [Fact]
    public async Task Detects_a_different_start()
    {
        var r = await Record();
        var other = new Shell(Deterministic);
        other.Fs.WriteAllText("/home/agent/notes.txt", "gamma\n");
        await using var target = new ShellTarget(r.CreateShell(start: other.Snapshot()));
        var report = await r.ReplayAsync(target);
        Assert.Equal([1], report.Calls.Where(c => c.Differences.Count > 0).Select(c => c.Expected.Index));
    }

    [Fact]
    public async Task Normalizes_volatile_output()
    {
        var sh = new Shell(); // the host clock: times differ between runs
        sh.Fs.WriteAllText("/home/agent/f", "x");
        var rec = sh.StartRecording();
        await sh.RunAsync("date; mktemp; ls -l f; date +%FT%TZ; date +%s; time true");
        rec.Stop();
        rec.Recording.Calls[0] = rec.Recording.Calls[0] with { Stdout = "Mon Jan  5 01:02:03 UTC 2026\n/tmp/tmp.AAAAAAAAAA\n-rw-r--r-- 1 agent agent 1 Jan  5 01:02 f\n2026-01-05T01:02:03Z\n1767574923\n", Stderr = "\nreal\t0m0.123s\nuser\t0m0.000s\nsys\t0m0.000s\n" };
        Assert.False((await rec.Recording.ReplayAsync()).Matches);
        var report = await rec.Recording.ReplayAsync(new ReplayOptions { Normalize = ReplayNormalizers.Volatile });
        Assert.True(report.Matches, report.ToString());
        Assert.Equal("a\n b", ReplayNormalizers.Whitespace("a  \r\n b\n\n"));
    }

    [Fact]
    public async Task Recovers_the_machine_after_any_call()
    {
        var r = await Record(snapshotEvery: 3);
        var live = new Shell(Deterministic);
        live.Restore(r.Start);
        for (int i = 0; i < r.Calls.Count; i++)
        {
            await live.RunAsync(r.Calls[i].Command);
            var at = await r.ShellAtAsync(i);
            const string probe = "pwd; echo $x $RANDOM; date +%T; ls -R /home/agent";
            Assert.Equal((await live.Fork().RunAsync(probe)).Stdout, (await at.RunAsync(probe)).Stdout);
        }
        Assert.Equal("/home/agent\n", (await (await r.ShellAtAsync(-1)).RunAsync("pwd")).Stdout);
    }

    [Fact]
    public async Task Rerecords_and_warns_about_missing_app_commands()
    {
        var sh = new Shell(Deterministic).Register("greet", ctx => { ctx.Stdout.WriteLine("hello " + string.Join(' ', ctx.Args)); return 0; });
        var rec = sh.StartRecording();
        await sh.RunAsync("greet you");
        rec.Stop();
        Assert.Equal(["greet"], rec.Recording.AppCommands);

        var bare = await rec.Recording.ReplayAsync();
        Assert.False(bare.Matches);
        Assert.Contains("app commands the target doesn't have: greet", bare.ToString());

        void Configure(Shell s) => s.Register("greet", ctx => { ctx.Stdout.WriteLine("hi " + string.Join(' ', ctx.Args)); return 0; });
        Assert.False((await rec.Recording.ReplayAsync(configure: Configure)).Matches);
        await using var target = new ShellTarget(rec.Recording.CreateShell(Configure));
        var updated = await rec.Recording.RerecordAsync(target);
        Assert.Equal("hi you\n", updated.Calls[0].Stdout);
        Assert.True((await updated.ReplayAsync(configure: Configure)).Matches);
    }

    [Fact]
    public async Task A_recording_file_is_a_regression_test()
    {
        await Replay.Replay.AssertMatches("recordings/basics.jsonl");
        var dir = Directory.CreateTempSubdirectory("topwater-rec-").FullName;
        try
        {
            var path = Path.Combine(dir, "bad.jsonl");
            var r = Recording.Load(Path.Combine(AppContext.BaseDirectory, "../../../recordings/basics.jsonl"));
            r.Calls[0] = r.Calls[0] with { Stdout = "changed\n" };
            r.Save(path);
            var e = await Assert.ThrowsAsync<ReplayMismatchException>(() => Replay.Replay.AssertMatches(path));
            Assert.Contains("TOPWATER_RERECORD=1", e.Message);
            Assert.False(e.Report.Matches);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The scenario every port records into recordings/from-&lt;port&gt;.topwater (snapshot every 2 calls). Here on a deterministic
    /// shell (2026-11-25 09:00 UTC, seed 3), as TypeScript does; Python has no virtual clock. Rewrite the C# one with
    /// TOPWATER_WRITE_FIXTURES=1 dotnet test --filter Cross_port.
    /// </summary>
    static readonly string[] CrossPortCalls =
    [
        "mkdir -p work && cd work && printf 'buy milk\\nfix bug\\n' > todo.txt && wc -l todo.txt",
        "greet() { echo \"hi $1\"; }; export TEAM=core; nums=(3 1 2); ln -s todo.txt latest; greet you",
        "rm todo.txt; echo done > out.txt; ls; cat latest; exit 3",
    ];

    [Theory]
    [InlineData("from-csharp.topwater")]
    [InlineData("from-python.topwater")]
    [InlineData("from-typescript.topwater")]
    public async Task Cross_port_topwater_files_load(string file)
    {
        if (file == "from-csharp.topwater" && Environment.GetEnvironmentVariable("TOPWATER_WRITE_FIXTURES") == "1")
        {
            var sh = new Shell(new ShellOptions { StartTime = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), RandomSeed = 3 });
            using var rec = sh.StartRecording(new RecordingOptions { SnapshotEvery = 2, Metadata = { ["source"] = "csharp port", ["task"] = "cross-port fixture" } });
            foreach (var c in CrossPortCalls) await sh.RunAsync(c);
            rec.Save(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Topwater.Tests/recordings", file));
        }
        var r = Recording.Load(Fixture(file));
        Assert.Equal(CrossPortCalls, r.Calls.Select(c => c.Command));
        Assert.EndsWith(" port", r.Metadata["source"]);
        Assert.Equal(3, r.Calls[2].ExitCode);
        Assert.Equal("2 todo.txt\n", r.Calls[0].Stdout);
        Assert.Contains("/home/agent/work", r.Calls[0].Files!);
        Assert.Contains("/home/agent/work/todo.txt", r.Calls[0].Files!);
        Assert.Contains("/home/agent/work/todo.txt", r.Calls[2].Files!);
        Assert.Contains("/home/agent/work/out.txt", r.Calls[2].Files!);
        Assert.NotNull(r.Calls[0].Time);
        var at1 = await r.ShellAtAsync(1);
        Assert.Equal("hi x\ncore 3 1 2\ntodo.txt\n", (await at1.RunAsync("cd /home/agent/work; greet x; echo $TEAM ${nums[@]}; readlink latest")).Stdout);
        Assert.Equal("buy milk\nfix bug\n", (await (await r.ShellAtAsync(0)).RunAsync("cat /home/agent/work/todo.txt")).Stdout);
        using var stream = File.OpenRead(Fixture(file));
        Assert.Equal("done\n", (await Shell.Load(stream).RunAsync("cat /home/agent/work/out.txt; test -e /home/agent/work/todo.txt")).Stdout);
        var report = await r.ReplayAsync(new ReplayOptions { Normalize = ReplayNormalizers.Volatile });
        Assert.True(report.Matches, report.ToString());
    }

    /// <summary>A branch the browser recording viewer saved (typescript/test/viewer-timeline.test.ts writes it).</summary>
    [Fact]
    public async Task Loads_a_branch_saved_by_the_recording_viewer()
    {
        var r = Recording.Load(Fixture("branch-from-viewer.topwater"));
        Assert.Equal(["greet branch > branch.txt; cat latest", "chmod 700 branch.txt; ls"], r.Calls.Skip(2).Select(c => c.Command));
        Assert.Equal("2", r.Metadata["forkedAfterCall"]);
        Assert.Equal("buy milk\nfix bug\n", r.Calls[2].Stdout);
        Assert.NotNull(r.Calls[1].SnapshotAfter);
        var probe = "cat /home/agent/work/branch.txt; stat -c %a /home/agent/work/branch.txt";
        Assert.Equal("hi branch\n700\n", (await (await r.ShellAtAsync(3)).RunAsync(probe)).Stdout);
        using var stream = File.OpenRead(Fixture("branch-from-viewer.topwater"));
        Assert.Equal("hi branch\n700\n", (await Shell.Load(stream).RunAsync(probe)).Stdout);
        Assert.Equal("hi branch\n", (await (await r.ShellAtAsync(2)).RunAsync("cat /home/agent/work/branch.txt")).Stdout); // re-run from the fork's checkpoint
    }

    [Fact]
    public async Task Reads_a_recording_made_by_the_typescript_port()
    {
        var r = Recording.Load(Path.Combine(AppContext.BaseDirectory, "../../../recordings/from-typescript.jsonl"));
        Assert.Equal("typescript", r.TopwaterVersion);
        Assert.True(r.IsDeterministic);
        Assert.Equal(3, r.Calls.Count);
        Assert.NotNull(r.Calls[1].SnapshotAfter);
        var start = r.CreateShell();
        Assert.Equal("buy milk\nfix bug\n", (await start.RunAsync("cat todo.txt")).Stdout);
        Assert.Equal("00ff0a80\n", (await start.RunAsync("xxd -p raw.bin")).Stdout);
        // the TypeScript snapshot's session: functions, exported variables, arrays, aliases and the virtual clock
        var at1 = await r.ShellAtAsync(1);
        Assert.Equal("/home/agent/work\nhello cs\ncore\n3 1 2 31\nll is aliased to `ls -l'\n09:00:00\n",
            (await at1.RunAsync("pwd; greet cs; bash -c 'echo $TEAM'; echo \"${nums[@]} ${ages[ann]}\"; type ll; date +%T")).Stdout);
        var report = await r.ReplayAsync();
        Assert.True(report.Matches, report.ToString());
    }

    [Fact]
    public async Task Replays_on_real_bash_in_docker()
    {
        if (!await DockerTarget.IsAvailableAsync()) return; // skipped without docker
        var sh = new Shell(new ShellOptions { Hostname = "replaybox" });
        sh.Fs.CreateDirectory("/home/agent/data/sub", parents: true);
        sh.Fs.WriteAllText("/home/agent/data/a.csv", "x,y\n1,2\n3,4\n");
        sh.Fs.WriteAllText("/var/log/app.log", "ERROR one\nINFO two\nERROR three\n");
        sh.Fs.SetOwner("/home/agent", "agent", recursive: true);
        await sh.RunAsync("cd data; export GREETING=hello");
        var rec = sh.StartRecording();
        await sh.RunAsync("pwd; echo $GREETING; awk -F, 'NR>1{s+=$2} END{print s}' a.csv");
        await sh.RunAsync("grep -c ERROR /var/log/app.log; n=5; cd sub; f() { echo \"f:$1\"; }");
        await sh.RunAsync("pwd; echo $n; f x; hostname; whoami; ls /home/agent/data");
        await sh.RunAsync("cat missing.txt; exit 4");
        rec.Stop();

        await using var docker = await DockerTarget.StartAsync(rec.Recording.Start, new DockerTargetOptions { Packages = [] });
        var report = await rec.Recording.ReplayAsync(docker);
        Assert.True(report.Matches, report.ToString());
        Assert.StartsWith("replay on docker ubuntu:24.04", report.ToString());
    }

    [Fact]
    public async Task Docker_target_copies_links_as_links_and_runs_by_number()
    {
        // links that loop, or fan out two per level, are never followed on the host; /etc/passwd saying uid 0 changes nothing
        if (!await DockerTarget.IsAvailableAsync()) return; // skipped without docker
        var sh = new Shell();
        var mk = new System.Text.StringBuilder("cd ~; ln -s . self; ln -s / r; mkdir d; ln -s ../d d/up; mkdir -p out/d0; echo x > out/d0/f\n");
        for (int i = 1; i <= 16; i++) mk.Append($"mkdir out/d{i}; ln -s ../d{i - 1} out/d{i}/a; ln -s ../d{i - 1} out/d{i}/b\n");
        Assert.Equal(0, (await sh.RunAsync(mk.ToString())).ExitCode);
        sh.Fs.WriteAllText("/etc/passwd", sh.Fs.ReadAllText("/etc/passwd").Replace("agent:x:1000:1000:", "agent:x:0:0:"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using var docker = await DockerTarget.StartAsync(sh.Snapshot(), new DockerTargetOptions { Packages = [] });
        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 60);
        var r = await docker.RunAsync("id -u; id -g; cd ~; readlink self r d/up out/d16/a; cat out/d2/a/b/f; find out -type l | wc -l");
        Assert.Equal("1000\n1000\n.\n/\n../d\n../d15\nx\n32\n", r.Stdout);
    }

    [Fact]
    public async Task Docker_target_setup_never_runs_names_as_code()
    {
        // an owner or variable name the host put in by hand reaches the container's root setup script as data, or not at all
        if (!await DockerTarget.IsAvailableAsync()) return; // skipped without docker
        var sh = new Shell();
        sh.Fs.WriteAllText("/srv/f", "x");
        sh.Fs.SetOwner("/srv/f", "a;touch /tmp/pwned1;b");
        sh.Environment["a;touch /tmp/pwned2;b"] = "1";
        await using var docker = await DockerTarget.StartAsync(sh.Snapshot(), new DockerTargetOptions { Packages = [] });
        var r = await docker.RunAsync("ls /tmp | grep -c pwned; cat /srv/f; grep -E 'CapEff|NoNewPrivs' /proc/self/status");
        Assert.Equal("0\nxCapEff:\t0000000000000000\nNoNewPrivs:\t1\n", r.Stdout);
    }

    [Fact]
    public async Task Docker_target_stops_reading_output_at_the_cap()
    {
        // a stand-in docker CLI: `exec ... yes` floods stdout forever, everything else succeeds
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("tw-fakedocker").FullName;
        var fake = Path.Combine(dir, "docker");
        File.WriteAllText(fake, "#!/bin/sh\nfor a; do last=$a; done\ncase \" $* \" in *\" -i \"*) cat >/dev/null;; esac\n[ \"$last\" = yes ] && exec yes\nexit 0\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await using var target = await DockerTarget.StartAsync(new Shell().Snapshot(), new DockerTargetOptions { Docker = fake, Packages = [], MaxOutputBytes = 1 << 20 });
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => target.RunAsync("yes"));
            Assert.Contains("more than 1048576 bytes", e.Message);
            Assert.InRange(watch.Elapsed.TotalSeconds, 0, 20);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Replaying_yes_on_real_bash_ends_with_an_error()
    {
        if (!await DockerTarget.IsAvailableAsync()) return; // skipped without docker
        var sh = new Shell();
        var rec = sh.StartRecording();
        await sh.RunAsync("yes | head -c 10");
        await sh.RunAsync("yes");
        rec.Stop();
        await using var docker = await DockerTarget.StartAsync(rec.Recording.Start, new DockerTargetOptions { Packages = [], MaxOutputBytes = 4 << 20, CallTimeout = TimeSpan.FromSeconds(20) });
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => rec.Recording.ReplayAsync(docker));
        Assert.Contains("more than 4194304 bytes", e.Message);
    }
}
