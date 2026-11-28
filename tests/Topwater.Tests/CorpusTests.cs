using System.Text.Json;
using Topwater;

namespace Topwater.Tests;

/// <summary>
/// Runs the shared corpus (tests/corpus/*.jsonl, recorded from real bash and GNU tools; see tests/corpus/README.md).
/// The Python and TypeScript ports run the same files. Cases this implementation cannot pass yet are listed with a
/// reason in tests/corpus/skip/csharp.json.
/// </summary>
public class CorpusTests
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "corpus");

    static readonly Dictionary<string, JsonElement> Cases = Directory.GetFiles(Dir, "*.jsonl").Order()
        .SelectMany(File.ReadLines).Where(l => l.Trim() != "")
        .Select(l => JsonDocument.Parse(l).RootElement).ToDictionary(c => c.GetProperty("id").GetString()!);

    static readonly Dictionary<string, string> Skips =
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Dir, "skip", "csharp.json")))!;

    static bool Skipped(string id) => Skips.ContainsKey(id) || Skips.ContainsKey(id[..id.LastIndexOf('-')])
        // shortcut: .NET 9 and later deflate with zlib-ng, so zip reports 55% where Info-ZIP says 53% (TypeScript skips it
        // too); upgrade if zip gets its own deflate
        || (Environment.Version.Major >= 9 && id == "archives-084");

    public static IEnumerable<object[]> Ids() => Cases.Keys.Where(id => !Skipped(id)).Select(id => new object[] { id });

    [Fact]
    public void SkipListNamesOnlyRealCases() =>
        Assert.Empty(Skips.Keys.Where(k => !Cases.ContainsKey(k) && !Cases.Keys.Any(id => id[..id.LastIndexOf('-')] == k)));

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task Case(string id)
    {
        var c = Cases[id];
        string? Str(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var script = Str("script")!;
        var sh = new Shell();
        await sh.RunAsync("mkdir -p /tmp/w && cd /tmp/w && hash -r");
        if (Str("setup") is { } setup) await sh.RunAsync(setup);
        var r = await sh.RunAsync(script);
        if (Str("toolOutput") is { } toolOutput)
        {
            Assert.Equal(toolOutput, r.ToToolOutput());
            return;
        }
        Assert.Equal(Str("stdout"), r.Stdout);
        if (c.TryGetProperty("exitCode", out var e) && e.ValueKind == JsonValueKind.Number)
            Assert.True(e.GetInt32() == r.ExitCode, $"exit code: expected {e.GetInt32()}, got {r.ExitCode}; stderr: {r.Stderr}\nscript: {script}");
        if (c.TryGetProperty("quiet", out var q) && q.ValueKind is JsonValueKind.True or JsonValueKind.False)
            Assert.True(q.GetBoolean() == (r.Stderr.Length == 0), $"bash {(q.GetBoolean() ? "wrote nothing to" : "wrote to")} stderr; this shell wrote: {r.Stderr}\nscript: {script}");
    }
}
