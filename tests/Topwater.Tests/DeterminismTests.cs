namespace Topwater.Tests;

/// <summary>ShellOptions.StartTime and RandomSeed: a run repeats exactly, including across snapshots.</summary>
public class DeterminismTests
{
    static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    static Shell Make(int seed = 7) => new(new ShellOptions { StartTime = Start, RandomSeed = seed });

    const string Volatile = """
        date; date +%s.%N; echo $RANDOM $RANDOM $EPOCHSECONDS; mktemp; mktemp -d; touch f; ls -l f /usr/bin/ls
        sleep 90; echo $SECONDS; uptime; jq -n now; printf '%(%F %T)T\n' -1; find . -maxdepth 1 -name f -mmin -2
        time sleep 2
        """;

    [Fact]
    public async Task Same_options_give_the_same_output()
    {
        var a = await Make().RunAsync(Volatile);
        var b = await Make().RunAsync(Volatile);
        Assert.Equal(0, a.ExitCode);
        Assert.Equal(a.Stdout, b.Stdout);
        Assert.Equal(a.Stderr, b.Stderr);
        Assert.StartsWith("Fri Jan  2 03:04:05 UTC 2026\n1767323045.", a.Stdout);
        Assert.Contains("2026-01-02 03:05:35", a.Stdout); // printf %T after the 90 s sleep
        Assert.Contains("\n90\n", a.Stdout);
        Assert.Contains("real\t0m2.000s", a.Stderr);
        Assert.Contains("./f", a.Stdout);
        Assert.Contains("Aug 13 09:13 /usr/bin/ls", a.Stdout); // program files were "installed" 142 days before the start
    }

    [Fact]
    public async Task Archive_times_do_not_depend_on_the_host_time_zone()
    {
        var sh = Make();
        var r = await sh.RunAsync("echo x > f; tar czf a.tgz f; tar tzvf a.tgz; zip -q z.zip f; unzip -l z.zip | grep ' f$'; mkdir out; tar xzf a.tgz -C out; unzip -q z.zip -d out2; date -r out/f +%T; date -r out2/f +%T");
        Assert.Contains("2026-01-02 03:04 f", r.Stdout);
        Assert.Matches(@"2026-01-02 03:04 +f\n", r.Stdout);
        Assert.EndsWith("03:04:05\n03:04:04\n", r.Stdout); // zip keeps 2-second precision
    }

    [Fact]
    public async Task Different_seeds_differ()
    {
        var a = await Make(1).RunAsync("echo $RANDOM $RANDOM; mktemp");
        var b = await Make(2).RunAsync("echo $RANDOM $RANDOM; mktemp");
        Assert.NotEqual(a.Stdout, b.Stdout);
    }

    [Fact]
    public async Task Sleep_moves_the_clock_without_waiting()
    {
        var sh = Make();
        var (r, took) = await TestHost.Timed(() => sh.RunAsync("sleep 3600; date -u +%H:%M:%S; sleep 1d; date +%F"));
        Assert.True(took < TimeSpan.FromSeconds(5), $"took {took}");
        Assert.Equal("04:04:05\n2026-01-03\n", r.Stdout);
    }

    [Fact]
    public async Task Timeout_fires_in_virtual_time()
    {
        var sh = Make();
        var r = await sh.RunAsync("timeout 5 sleep 10; echo $?; date +%T; timeout 10 sleep 1; echo $?; date +%T; timeout 3 timeout 8 sleep 20; echo $?; date +%T");
        Assert.Equal("124\n03:04:10\n0\n03:04:11\n124\n03:04:14\n", r.Stdout);
    }

    [Fact]
    public async Task Files_get_virtual_times()
    {
        var sh = Make();
        await sh.RunAsync("echo a > a; sleep 60; echo b > b");
        Assert.Equal(Start.UtcDateTime, sh.Fs.Stat("/home/agent/a").Modified);
        Assert.Equal(Start.UtcDateTime.AddMinutes(1), sh.Fs.Stat("/home/agent/b").Modified);
        Assert.Equal("b\na\n", (await sh.RunAsync("ls -t")).Stdout);
    }

    [Fact]
    public async Task Assigning_RANDOM_reseeds()
    {
        var sh = new Shell();
        var r = await sh.RunAsync("RANDOM=5; a=\"$RANDOM $RANDOM\"; RANDOM=5; b=\"$RANDOM $RANDOM\"; [ \"$a\" = \"$b\" ] && echo same");
        Assert.Equal("same\n", r.Stdout);
    }

    [Fact]
    public async Task Snapshots_carry_the_clock_and_random_state()
    {
        var sh = Make();
        await sh.RunAsync("echo $RANDOM; sleep 30");
        var snap = sh.Snapshot();
        Assert.Equal(Start.UtcDateTime.AddSeconds(30), snap.Created);
        const string next = "echo $RANDOM $RANDOM; date +%T; mktemp";
        var first = await sh.RunAsync(next);

        sh.Restore(snap);
        var fork = sh.Fork();
        Assert.Equal(first.Stdout, (await sh.RunAsync(next)).Stdout);
        Assert.Equal(first.Stdout, (await fork.RunAsync(next)).Stdout);

        var saved = new MemoryStream();
        snap.Save(saved);
        saved.Position = 0;
        var loaded = Make(seed: 99); // the snapshot's state wins over the seed it was created with
        loaded.Restore(ShellSnapshot.Load(saved));
        Assert.Equal(first.Stdout, (await loaded.RunAsync(next)).Stdout);
    }

    [Fact]
    public async Task Off_by_default()
    {
        var sh = new Shell();
        var r = await sh.RunAsync("date +%Y");
        Assert.Equal(DateTime.UtcNow.Year.ToString() + "\n", r.Stdout);
        var snap = new MemoryStream();
        sh.Save(snap);
        snap.Position = 0;
        using var zip = new System.IO.Compression.ZipArchive(snap);
        using var m = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        var manifest = m.ReadToEnd();
        Assert.DoesNotContain("\"clock\"", manifest);
        Assert.DoesNotContain("\"random\"", manifest);
    }
}
