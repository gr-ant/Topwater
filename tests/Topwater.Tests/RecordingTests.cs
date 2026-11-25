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

    [Fact]
    public async Task Saves_and_loads_embedded_and_side_car()
    {
        var r = await Record(snapshotEvery: 2);
        var ms = new MemoryStream();
        r.Save(ms);
        var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal(5, lines.Length); // header + one line per call
        Assert.StartsWith("{\"format\":\"topwater-recording\",\"version\":1,", lines[0]);
        Assert.Contains("\"startTime\":\"2026-03-04T05:06:07.0000000Z\"", lines[0]);
        Assert.Contains("\"randomSeed\":42", lines[0]);
        Assert.Contains("\"snapshot\":{\"embedded\":", lines[2]);
        Assert.DoesNotContain("\"snapshot\"", lines[1]);

        ms.Position = 0;
        var loaded = Recording.Load(ms);
        AssertSame(r, loaded);
        Assert.True((await loaded.ReplayAsync()).Matches);

        var dir = Directory.CreateTempSubdirectory("topwater-rec-").FullName;
        try
        {
            var path = Path.Combine(dir, "run.jsonl");
            r.Save(path, sidecarSnapshots: true);
            Assert.True(File.Exists(Path.Combine(dir, "run.snapshots", "start.zip")));
            Assert.True(File.Exists(Path.Combine(dir, "run.snapshots", "call-0001.zip")));
            Assert.Contains("\"file\":\"run.snapshots/call-0003.zip\"", File.ReadAllText(path));
            var side = Recording.Load(path);
            AssertSame(r, side);
            Assert.True((await side.ReplayAsync()).Matches);
            ms.Position = 0;
            Assert.Throws<InvalidDataException>(() => Recording.Load(File.OpenRead(path))); // side-car refs need the path
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static void AssertSame(Recording a, Recording b)
    {
        Assert.Equal(a.Calls.Select(c => c with { Elapsed = default, SnapshotAfter = null }), b.Calls.Select(c => c with { Elapsed = default, SnapshotAfter = null }));
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
        var ms = new MemoryStream();
        (await Record()).Save(ms);
        var broken = System.Text.Encoding.UTF8.GetString(ms.ToArray()) + "{oops\n";
        Assert.Throws<InvalidDataException>(() => Recording.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(broken))));
    }

    static async Task<string> RecordingText(int snapshotEvery = 0)
    {
        var ms = new MemoryStream();
        (await Record(snapshotEvery)).Save(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    static Recording LoadText(string text, Func<string, Stream>? open = null, ShellOptions? limits = null) =>
        Recording.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), open, limits);

    [Fact]
    public async Task Limits_in_the_file_can_lower_but_never_raise_the_replay_shells()
    {
        var header = System.Text.Json.Nodes.JsonNode.Parse((await RecordingText()).Split('\n')[0])!;
        var o = header["options"]!;
        o["maxLoopIterations"] = int.MaxValue; o["maxCallDurationMs"] = long.MaxValue / 2; o["maxFileSystemBytes"] = 1L << 50;
        o["maxStringBytes"] = 1L << 50; o["maxExtractBytes"] = -5; o["maxExtractEntries"] = 10;
        var raised = LoadText(header.ToJsonString() + "\n");
        var d = new ShellOptions();
        Assert.Equal(d.MaxLoopIterations, raised.MaxLoopIterations);
        Assert.Equal(d.MaxCallDuration, raised.MaxCallDuration);
        Assert.Equal(d.MaxFileSystemBytes, raised.MaxFileSystemBytes);
        Assert.Equal(d.MaxStringBytes, raised.MaxStringBytes);
        Assert.Equal(0, raised.MaxExtractBytes);    // lowered, never below zero
        Assert.Equal(10, raised.MaxExtractEntries); // lowered
        var tight = LoadText(header.ToJsonString() + "\n", limits: new ShellOptions { MaxLoopIterations = 50 });
        Assert.Equal(50, tight.CreateShell().Options.MaxLoopIterations);
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
    public async Task Side_car_paths_must_be_plain_relative_names(string path)
    {
        var text = (await RecordingText()).Split('\n')[0];
        var header = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        header["start"] = new System.Text.Json.Nodes.JsonObject { ["file"] = path };
        var opened = false;
        Assert.Throws<InvalidDataException>(() => LoadText(header.ToJsonString() + "\n", _ => { opened = true; return new MemoryStream(); }));
        Assert.False(opened);
    }

    [Fact]
    public async Task Caps_the_number_and_total_size_of_snapshots()
    {
        var lines = (await RecordingText(snapshotEvery: 1)).TrimEnd('\n').Split('\n');
        var many = lines[0] + "\n" + string.Concat(Enumerable.Repeat(lines[1] + "\n", Recording.MaxSnapshots));
        Assert.Contains("more than 1000 snapshots", Assert.Throws<InvalidDataException>(() => LoadText(many)).Message);
        Assert.Equal(Recording.MaxSnapshots - 1, LoadText(lines[0] + "\n" + string.Concat(Enumerable.Repeat(lines[1] + "\n", Recording.MaxSnapshots - 1))).Calls.Count);

        // each snapshot is within the limit, all of them together are not
        var big = new Shell(new ShellOptions { MaxFileSystemBytes = 4096 });
        big.Fs.WriteAllText("/home/agent/big.txt", new string('x', 1500));
        using (var rec = big.StartRecording(new RecordingOptions { SnapshotEvery = 1 }))
        {
            for (int i = 0; i < 12; i++) await big.RunAsync("true");
            var ms = new MemoryStream();
            rec.Recording.Save(ms);
            ms.Position = 0;
            var e = Assert.Throws<InvalidDataException>(() => Recording.Load(ms, limits: new ShellOptions { MaxFileSystemBytes = 4096 }));
            Assert.Contains("MaxFileSystemBytes", e.Message);
        }
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
        rec.Recording.Calls[0] = rec.Recording.Calls[0] with { Stdout = "Mon Jan  5 01:02:03 UTC 2026\n/tmp/tmp.AAAAAAAAAA\n-rw-r--r-- 1 agent agent      1 Jan 05 01:02 f\n2026-01-05T01:02:03Z\n1767574923\n", Stderr = "\nreal\t0m0.123s\nuser\t0m0.000s\nsys\t0m0.000s\n" };
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
}
