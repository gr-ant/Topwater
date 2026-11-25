// Measures where Topwater differs from real bash, using recorded agent sessions.
//
//   dotnet run --project tools/ReplayGaps -- from-runs RUNS_DIR HARNESS_START.zip OUT_DIR
//       saved example-harness transcripts (runs/*.json) -> recordings (HARNESS_START from EXPORT_START=... examples/Harness)
//   dotnet run --project tools/ReplayGaps -- from-corpus SESSIONS.jsonl OUT_DIR [SESSIONS_PER_SOURCE] [MAX_CALLS]
//       normalized public trajectories ({"source","id","commands":[...],"outputs":[...]} per line) -> recordings
//   dotnet run --project tools/ReplayGaps -- measure REPORT.md DETAILS.jsonl WORKERS RECORDINGS_DIR...
//       replays every recording on Topwater and on real bash (ubuntu:24.04 in Docker), classifies each differing call
//       and writes a ranked summary
//
// Commands in third-party recordings are untrusted: they run only inside throwaway containers with no network.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Topwater;
using Topwater.Replay;

var Det = new ShellOptions { StartTime = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), RandomSeed = 1, MaxCallDuration = TimeSpan.FromSeconds(30) };

switch (args.FirstOrDefault())
{
    case "from-runs" when args.Length == 4: return FromRuns(args[1], args[2], args[3]);
    case "from-corpus" when args.Length >= 3: return FromCorpus(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 100, args.Length > 4 ? int.Parse(args[4]) : 40);
    case "measure" when args.Length >= 5: return await Measure(args[1], args[2], int.Parse(args[3]), args[4..]);
    default: Console.Error.WriteLine("usage: from-runs RUNS START.zip OUT | from-corpus SESSIONS.jsonl OUT [N] [MAX] | measure REPORT.md DETAILS.jsonl WORKERS DIR..."); return 2;
}

int FromRuns(string runsDir, string startZip, string outDir)
{
    ShellSnapshot harnessStart;
    using (var f = File.OpenRead(startZip)) harnessStart = ShellSnapshot.Load(f);
    int n = 0;
    foreach (var path in Directory.GetFiles(runsDir, "*.json").Order())
    {
        var doc = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        var name = Path.GetFileNameWithoutExtension(path);
        var model = doc.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "gemini (default)";
        IEnumerable<(string Id, JsonElement Commands, Dictionary<string, string> Meta)> runs = doc.TryGetProperty("results", out var results)
            ? results.EnumerateArray().Select(r => ($"{r.GetProperty("Variant").GetString()}-task{r.GetProperty("Task")}-rep{r.GetProperty("Rep")}", r.GetProperty("Commands"),
                new Dictionary<string, string> { ["variant"] = r.GetProperty("Variant").GetString()!, ["taskId"] = r.GetProperty("Task").ToString(), ["solved"] = r.GetProperty("Solved").ToString() }))
            : [("run", doc.GetProperty("Commands"), new Dictionary<string, string>())];
        foreach (var (id, commands, meta) in runs)
        {
            // the harness's machine is known exactly; the office and duckdb examples' starts are approximated by a fresh shell
            ShellSnapshot start;
            List<string> app;
            if (name.StartsWith("harness")) { start = harnessStart; app = ["orders"]; }
            else
            {
                var sh = new Shell(Det);
                if (name.StartsWith("office")) sh.Fs.WriteAllText("/home/agent/notes.txt", "Q3 review notes (rough)\n");
                sh.Fs.SetOwner("/home/agent", "agent", recursive: true);
                start = sh.Snapshot();
                app = [name.StartsWith("office") ? "officecli" : "duckdb"];
                meta["start"] = "approximate: a fresh shell, not the example's own fixture";
            }
            var rec = new Recording(start, Det) { AppCommands = app };
            rec.Metadata["source"] = "runs";
            rec.Metadata["transcript"] = Path.GetFileName(path);
            rec.Metadata["model"] = model;
            foreach (var (k, v) in meta) rec.Metadata[k] = v;
            int i = 0;
            foreach (var c in commands.EnumerateArray())
                rec.Calls.Add(new RecordedCall(i++, c.GetProperty("Command").GetString()!, c.GetProperty("Stdout").GetString() ?? "", c.GetProperty("Stderr").GetString() ?? "",
                    c.GetProperty("ExitCode").GetInt32(), null, TimeSpan.Zero));
            if (rec.Calls.Count == 0) continue;
            Directory.CreateDirectory(Path.Combine(outDir, "runs"));
            rec.Save(Path.Combine(outDir, "runs", $"{name}-{id}.jsonl"));
            n++;
        }
    }
    Console.WriteLine($"{n} recordings -> {outDir}/runs");
    return 0;
}

int FromCorpus(string sessionsPath, string outDir, int perSource, int maxCalls)
{
    var sh = new Shell(Det);
    // the corpora's agents worked in /testbed (SWE) and /app (terminal tasks): give both machines those directories
    foreach (var d in new[] { "/testbed", "/app" }) { sh.Fs.CreateDirectory(d); sh.Fs.SetOwner(d, "agent"); }
    var start = sh.Snapshot();
    var taken = new Dictionary<string, int>();
    foreach (var line in File.ReadLines(sessionsPath))
    {
        var s = JsonDocument.Parse(line).RootElement;
        var source = s.GetProperty("source").GetString()!;
        if (taken.GetValueOrDefault(source) >= perSource) continue;
        var commands = s.GetProperty("commands").EnumerateArray().Select(c => c.GetString()!).Take(maxCalls).ToList();
        if (commands.Count == 0) continue;
        var outputs = s.TryGetProperty("outputs", out var o) && o.ValueKind == JsonValueKind.Array ? o.EnumerateArray().ToList() : [];
        var rec = new Recording(start, Det);
        rec.Metadata["source"] = source;
        rec.Metadata["id"] = s.GetProperty("id").GetString()!;
        rec.Metadata["license"] = s.TryGetProperty("license", out var l) ? l.GetString() ?? "" : "";
        for (int i = 0; i < commands.Count; i++)
        {
            var out1 = i < outputs.Count && outputs[i].ValueKind == JsonValueKind.Object ? outputs[i] : default;
            rec.Calls.Add(new RecordedCall(i, commands[i],
                out1.ValueKind == JsonValueKind.Object && out1.TryGetProperty("stdout", out var so) ? so.GetString() ?? "" : "", "",
                out1.ValueKind == JsonValueKind.Object && out1.TryGetProperty("exitCode", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0, null, TimeSpan.Zero));
        }
        int k = taken[source] = taken.GetValueOrDefault(source) + 1;
        Directory.CreateDirectory(Path.Combine(outDir, source));
        rec.Save(Path.Combine(outDir, source, $"{k:D4}.jsonl"));
    }
    Console.WriteLine(string.Join(", ", taken.Select(t => $"{t.Key}: {t.Value}")) + $" -> {outDir}");
    return 0;
}

async Task<int> Measure(string reportPath, string detailsPath, int workers, string[] dirs)
{
    var files = dirs.SelectMany(d => Directory.GetFiles(d, "*.jsonl", SearchOption.AllDirectories)).Order().ToList();
    var opts = new DockerTargetOptions { CallTimeout = TimeSpan.FromSeconds(20) };
    // build the image once before the workers start
    await (await DockerTarget.StartAsync(new Shell().Snapshot(), opts)).DisposeAsync();
    var rows = new ConcurrentBag<Row>();
    var changed = new ConcurrentBag<(string File, int Index, string Command)>();
    int done = 0;
    using var gate = new SemaphoreSlim(workers);
    await Task.WhenAll(files.Select(async file =>
    {
        await gate.WaitAsync();
        try
        {
            var rec = Recording.Load(file);
            var source = rec.Metadata.GetValueOrDefault("source", "?");
            await using var linux = await DockerTarget.StartAsync(rec.Start, opts);
            var tw = rec.CreateShell();
            foreach (var call in rec.Calls)
            {
                var appCommand = rec.AppCommands.Any(a => Regex.IsMatch(call.Command, $@"(^|[\s;&|(`$]){Regex.Escape(a)}\b"));
                var l = await linux.RunAsync(call.Command);
                ShellResult t;
                try { t = await tw.RunAsync(call.Command); }
                catch (Exception e) { t = new ShellResult("", "host exception: " + e.Message, 1); }
                if (appCommand) continue; // the app's own programs exist only in Topwater
                if (source == "runs" && (!Same(call.Stdout, t.Stdout) || call.ExitCode != t.ExitCode))
                    changed.Add((Path.GetFileName(file), call.Index, call.Command));
                rows.Add(Classify(source, Path.GetFileName(file), call.Index, call.Command, t, l));
            }
        }
        catch (Exception e) { Console.Error.WriteLine($"{file}: {e.Message}"); }
        finally
        {
            gate.Release();
            var n = Interlocked.Increment(ref done);
            if (n % 20 == 0) Console.WriteLine($"  {n}/{files.Count} recordings, {rows.Count} calls");
        }
    }));

    await using (var details = new StreamWriter(detailsPath))
        foreach (var r in rows.OrderBy(r => r.File).ThenBy(r => r.Index)) details.WriteLine(JsonSerializer.Serialize(r));
    File.WriteAllText(reportPath, Summarize(rows.ToList(), changed.ToList(), files.Count));
    Console.WriteLine($"{rows.Count} calls from {files.Count} recordings -> {reportPath}");
    return 0;
}

static bool Same(string a, string b) => Norm(a) == Norm(b);

// what differs between any two runs, plus bash's "line N:" error prefix, which Topwater leaves out
static string Norm(string s)
{
    s = ReplayNormalizers.Volatile(s);
    s = NotFound().Replace(s, "$1: command not found");
    s = Regex.Replace(s, @"(?m)^bash: line \d+: ", "bash: ");
    return ReplayNormalizers.Whitespace(s);
}

static Row Classify(string source, string file, int index, string command, ShellResult t, ShellResult l)
{
    var word = FirstWord(command);
    var tMissing = NotFound().Matches(t.Stderr).Select(m => m.Groups[1].Value).ToHashSet();
    var lMissing = NotFound().Matches(l.Stderr).Select(m => m.Groups[1].Value).ToHashSet();
    var bothMissing = tMissing.Intersect(lMissing).ToList();
    string kind, key;
    bool stdoutSame = Same(t.Stdout, l.Stdout), stderrSame = Same(t.Stderr, l.Stderr);
    if (stdoutSame && stderrSame && t.ExitCode == l.ExitCode) (kind, key) = ("match", word);
    else if (tMissing.Except(lMissing).FirstOrDefault() is { } missing) (kind, key) = ("missing command", missing);
    else if (BadOption().Match(t.Stderr) is { Success: true } opt && !BadOption().IsMatch(l.Stderr))
        (kind, key) = ("unsupported option", opt.Groups[1].Value + " " + OptionName(opt.Value));
    else if (t.ExitCode != l.ExitCode) (kind, key) = ("wrong exit code", word);
    else if (!stdoutSame) (kind, key) = ("wrong output", word);
    else (kind, key) = ("different error message", word);
    if (l.ExitCode == 124) kind = kind == "match" ? kind : "real bash timed out";
    return new Row(source, file, index, command, kind, key, bothMissing, t.ExitCode, l.ExitCode, Clip(t.Stdout), Clip(l.Stdout), Clip(t.Stderr), Clip(l.Stderr));
}

static string OptionName(string line)
{
    var m = Regex.Match(line, @"(--?[\w-]+)|'(\S+)'|‘(\S+)’|-- '?(\w)");
    return m.Success ? m.Value.Trim('\'', '‘', '’') : line.Trim();
}

static string Clip(string s) => s.Length <= 600 ? s : s[..600] + "...";

// the program a call is mostly about: the first word after cd/env/sudo prefixes
static string FirstWord(string command)
{
    var line = command.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? "";
    for (int guard = 0; guard < 10; guard++)
    {
        var before = line;
        line = Regex.Replace(line, @"^cd\s+(""[^""]*""|'[^']*'|\S+)\s*(&&|;)\s*", "");
        line = Regex.Replace(line, @"^[A-Za-z_][A-Za-z0-9_]*=(""[^""]*""|'[^']*'|\S*)\s+(?=\S)", "");
        line = Regex.Replace(line, @"^(sudo|time|nohup|timeout\s+\S+|env)\s+", "");
        line = Regex.Replace(line, @"^[({!]\s*", "");
        if (line == before) break;
    }
    var w = Regex.Match(line, @"^[^\s;|&()<>]+").Value;
    return w.Length == 0 ? "(other)" : w.StartsWith("./") ? "./script" : w.Contains('/') && Path.GetFileName(w) is { Length: > 0 } b ? b : w;
}

static string Summarize(List<Row> rows, List<(string File, int Index, string Command)> changed, int recordings)
{
    var sb = new System.Text.StringBuilder();
    var bySource = rows.GroupBy(r => r.Source).OrderBy(g => g.Key);
    sb.AppendLine($"Replayed {rows.Count} calls from {recordings} recordings on Topwater and on real bash (ubuntu:24.04 + {string.Join(' ', new DockerTargetOptions().Packages)}).\n");
    sb.AppendLine("| source | calls | match | differ |\n|---|---:|---:|---:|");
    foreach (var g in bySource)
        sb.AppendLine($"| {g.Key} | {g.Count()} | {g.Count(r => r.Kind == "match")} ({100.0 * g.Count(r => r.Kind == "match") / g.Count():0}%) | {g.Count(r => r.Kind != "match")} |");
    sb.AppendLine($"| **all** | {rows.Count} | {rows.Count(r => r.Kind == "match")} ({100.0 * rows.Count(r => r.Kind == "match") / Math.Max(1, rows.Count):0}%) | {rows.Count(r => r.Kind != "match")} |\n");
    sb.AppendLine("| kind | calls |\n|---|---:|");
    foreach (var g in rows.Where(r => r.Kind != "match").GroupBy(r => r.Kind).OrderByDescending(g => g.Count())) sb.AppendLine($"| {g.Key} | {g.Count()} |");
    foreach (var kind in new[] { "missing command", "unsupported option", "wrong exit code", "wrong output", "different error message", "real bash timed out" })
    {
        var groups = rows.Where(r => r.Kind == kind).GroupBy(r => r.Key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Take(40).ToList();
        if (groups.Count == 0) continue;
        sb.AppendLine($"\n### {kind}\n\n| # | key | calls | recordings | example |\n|---:|---|---:|---:|---|");
        int i = 0;
        foreach (var g in groups)
        {
            var ex = g.First();
            sb.AppendLine($"| {++i} | `{Cell(g.Key)}` | {g.Count()} | {g.Select(r => r.File).Distinct().Count()} | `{Cell(ex.Command, 90)}` → {Describe(ex)} |");
        }
    }
    var both = rows.SelectMany(r => r.BothMissing).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(25).ToList();
    if (both.Count > 0)
        sb.AppendLine("\n### not installed on either machine\n\n" + string.Join(", ", both.Select(g => $"`{g.Key}` {g.Count()}")));
    if (changed.Count > 0)
    {
        sb.AppendLine($"\n### saved example runs: calls whose stdout or exit code differ from what the agent saw\n\n{changed.Count} calls (times normalized); first ones:\n");
        foreach (var c in changed.OrderBy(c => c.File).ThenBy(c => c.Index).Take(15)) sb.AppendLine($"- {c.File} #{c.Index}: `{Cell(c.Command, 100)}`");
    }
    return sb.ToString();
}

static string Describe(Row r) => r.Kind switch
{
    "wrong exit code" => $"exit {r.TopwaterExit} vs {r.LinuxExit}; tw: {Cell(FirstLine(r.TopwaterStderr + r.TopwaterStdout), 70)} / bash: {Cell(FirstLine(r.LinuxStderr + r.LinuxStdout), 70)}",
    "wrong output" => $"tw: {Cell(FirstDiff(r.TopwaterStdout, r.LinuxStdout).A, 70)} / bash: {Cell(FirstDiff(r.TopwaterStdout, r.LinuxStdout).B, 70)}",
    _ => $"tw: {Cell(FirstLine(r.TopwaterStderr), 80)} / bash: {Cell(FirstLine(r.LinuxStderr), 80)}",
};

static string FirstLine(string s) => s.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";

static (string A, string B) FirstDiff(string a, string b)
{
    var x = Norm(a).Split('\n'); var y = Norm(b).Split('\n');
    int i = 0;
    while (i < x.Length && i < y.Length && x[i] == y[i]) i++;
    return (i < x.Length ? x[i] : "(end)", i < y.Length ? y[i] : "(end)");
}

static string Cell(string s, int max = 60)
{
    s = s.Replace("\n", "⏎").Replace("|", "\\|").Replace("`", "'");
    return s.Length <= max ? s : s[..max] + "…";
}

record Row(string Source, string File, int Index, string Command, string Kind, string Key, List<string> BothMissing,
    int TopwaterExit, int LinuxExit, string TopwaterStdout, string LinuxStdout, string TopwaterStderr, string LinuxStderr);

partial class Program
{
    [GeneratedRegex(@"(?m)^(?:bash: )?(?:line \d+: )?([^\s:]+): command not found.*$")]
    internal static partial Regex NotFound();
    [GeneratedRegex(@"(?m)^(?:bash: )?(?:line \d+: )?([^\s:]+): .*(?:invalid|unrecognized|unknown|illegal|unsupported|not supported).*option.*$", RegexOptions.IgnoreCase)]
    internal static partial Regex BadOption();
}
