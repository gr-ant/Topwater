using System.Text.Json;
using Topwater;

namespace Topwater.Tests;

/// <summary>
/// Drift checks against spec/commands/*.json (read here directly, so a stale generated table fails too): the built-ins
/// are exactly the spec's, and their help listing, --help, --version, options and usage errors come from it.
/// The Python and TypeScript ports run the same checks (test_command_spec.py, command-spec.test.ts).
/// </summary>
public class CommandSpecTests
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "spec");

    static readonly Dictionary<string, JsonElement> Spec = Directory.GetFiles(Path.Combine(Dir, "commands"), "*.json")
        .Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement)
        .Where(c => !c.TryGetProperty("ports", out var p) || p.EnumerateArray().Any(x => x.GetString() == "cs"))
        .ToDictionary(c => c.GetProperty("name").GetString()!);

    static readonly JsonElement Defaults = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "shell.json"))).RootElement.GetProperty("defaults");

    public static IEnumerable<object[]> Names() => Spec.Keys.Order(StringComparer.Ordinal).Select(n => new object[] { n });

    static string? Str(JsonElement c, string key) => c.TryGetProperty(key, out var v) ? v.GetString() : null;
    static bool Bool(JsonElement c, string key, bool fallback) => c.TryGetProperty(key, out var v) ? v.GetBoolean() : fallback;
    static string Lines(JsonElement a, string name, string summary) =>
        string.Concat(a.EnumerateArray().Select(l => l.GetString()!.Replace("{name}", name).Replace("{summary}", summary) + "\n"));
    static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    static string TryHelp(JsonElement c)
    {
        var shown = Str(c, "reportsAs") ?? Str(c, "name")!;
        return Lines(c.TryGetProperty("tryHelp", out var t) ? t : Defaults.GetProperty("tryHelp"), shown, "");
    }

    static IEnumerable<(string Arg, bool Valued)> Options(JsonElement c)
    {
        if (!c.TryGetProperty("options", out var opts)) yield break;
        foreach (var o in opts.EnumerateArray())
        {
            if (o.TryGetProperty("ports", out var p) && !p.EnumerateArray().Any(x => x.GetString() == "cs")) continue;
            bool valued = o.TryGetProperty("arg", out _);
            if (Str(o, "short") is { } s) yield return ("-" + s, valued);
            if (o.TryGetProperty("long", out var longs)) foreach (var l in longs.EnumerateArray()) yield return ("--" + l.GetString(), valued);
        }
    }

    [Fact]
    public void BuiltinsAreExactlyTheSpecCommands()
    {
        var registered = new Shell().Commands.Keys.ToHashSet();
        Assert.Empty(Spec.Keys.Where(n => !registered.Contains(n)));   // in the spec, not registered
        Assert.Empty(registered.Where(n => !Spec.ContainsKey(n)));     // registered, not in the spec (add it, or mark the port missing)
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task HelpAndVersionComeFromTheSpec(string name)
    {
        var c = Spec[name];
        var cmd = new Shell().Commands[name];
        var summary = Str(c, "summary")!;
        var help = c.TryGetProperty("help", out var h) ? Lines(h, name, summary) : null;
        Assert.Equal(summary, cmd.Description);
        Assert.Equal(help, cmd.Usage);
        if (!Bool(c, "standardOptions", true)) return;
        var sh = new Shell();
        var r = await sh.RunAsync($"{Q(name)} --help");
        Assert.Equal(help ?? Lines(Defaults.GetProperty("help"), name, summary), r.Stdout);
        r = await sh.RunAsync($"{Q(name)} --version");
        Assert.Equal((Str(c, "version") ?? Str(Defaults, "version")!.Replace("{name}", name)) + "\n", r.Stdout);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task OptionsAndUsageErrorsComeFromTheSpec(string name)
    {
        var c = Spec[name];
        if (Str(c, "parser") != "args") return;
        foreach (var (arg, valued) in Options(c))
        {
            var r = await new Shell().RunAsync($"cd /tmp && {Q(name)} {arg}{(valued ? " 1" : "")} </dev/null");
            Assert.DoesNotContain("invalid option", r.Stderr);
            Assert.DoesNotContain("unrecognized option", r.Stderr);
        }
        var bad = await new Shell().RunAsync($"{Q(name)} --no-such-option </dev/null");
        if (Bool(c, "shellBuiltin", false)) { Assert.Contains("unrecognized option '--no-such-option'", bad.Stderr); return; }
        Assert.Equal($"{Str(c, "reportsAs") ?? name}: unrecognized option '--no-such-option'\n{TryHelp(c)}", bad.Stderr);
        Assert.Equal(c.TryGetProperty("usageExitCode", out var code) ? code.GetInt32() : 1, bad.ExitCode);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task OptionsOutsideTheSpecAreRejected(string name)
    {
        var c = Spec[name];
        if (Str(c, "parser") != "args") return;
        var accepted = Options(c).Select(o => o.Arg).ToHashSet();
        var candidates = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(ch => "-" + ch).ToHashSet();
        foreach (var o in c.GetProperty("options").EnumerateArray())   // also the ones listed for other ports only
        {
            if (Str(o, "short") is { } s) candidates.Add("-" + s);
            if (o.TryGetProperty("long", out var longs)) foreach (var l in longs.EnumerateArray()) candidates.Add("--" + l.GetString());
        }
        var sh = new Shell();
        foreach (var arg in candidates.Where(a => !accepted.Contains(a)).Order(StringComparer.Ordinal))
        {
            var r = await sh.RunAsync($"cd /tmp && {Q(name)} {arg} </dev/null");
            Assert.True(r.Stderr.Contains("invalid option") || r.Stderr.Contains("unrecognized option"), $"{name} {arg}: {r.Stderr}");
        }
    }

    public static IEnumerable<object[]> Subcommands() =>
        Spec.Values.Where(c => c.TryGetProperty("subcommands", out _))
            .SelectMany(c => c.GetProperty("subcommands").EnumerateObject()
                .Where(s => !s.Value.TryGetProperty("ports", out var p) || p.EnumerateArray().Any(x => x.GetString() == "cs"))
                .Select(s => new object[] { c.GetProperty("name").GetString()!, s.Name }))
            .OrderBy(x => (string)x[0], StringComparer.Ordinal).ThenBy(x => (string)x[1], StringComparer.Ordinal);

    /// <summary>The generated table's entry for a subcommand (internal to the library, so read by reflection).</summary>
    static (string Summary, string Help) Table(string name, string sub)
    {
        var spec = typeof(Shell).Assembly.GetType("Topwater.CommandSpec")!;
        var commands = (System.Collections.IDictionary)spec.GetField("Commands", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var entry = commands[name]!;
        var subs = (System.Collections.IDictionary)entry.GetType().GetProperty("Subcommands")!.GetValue(entry)!;
        var s = subs[sub]!;
        return ((string)s.GetType().GetProperty("Summary")!.GetValue(s)!, (string)s.GetType().GetProperty("Help")!.GetValue(s)!);
    }

    [Theory]
    [MemberData(nameof(Subcommands))]
    public async Task SubcommandsComeFromTheSpec(string name, string sub)
    {
        var s = Spec[name].GetProperty("subcommands").GetProperty(sub);
        var (summary, help) = Table(name, sub);
        Assert.Equal(Str(s, "summary"), summary);
        var sh = new Shell();
        Assert.Equal(help, (await sh.RunAsync($"{Q(name)} help {sub}")).Stdout);
        Assert.Equal(help, (await sh.RunAsync($"{Q(name)} {sub} --help")).Stdout);
        if (Bool(s, "unsupported", false))
        {
            var u = await sh.RunAsync($"{Q(name)} {sub}");
            Assert.Equal(128, u.ExitCode);
            Assert.StartsWith("fatal: ", u.Stderr);
            return;
        }
        foreach (var (arg, valued) in Options(s))
        {
            var r = await new Shell().RunAsync($"cd /tmp && {Q(name)} init -q r && cd r && {Q(name)} {sub} {arg}{(valued ? " 1" : "")} </dev/null");
            Assert.False(r.Stderr.Contains("unknown option") || r.Stderr.Contains("unknown switch"), $"{sub} {arg}: {r.Stderr}");
        }
        if (sub is "help" or "version" or "rev-parse" or "stash") return;  // these read their own arguments
        var bad = await new Shell().RunAsync($"cd /tmp && {Q(name)} init -q r && cd r && {Q(name)} {sub} --no-such-option </dev/null");
        Assert.Equal($"error: unknown option `no-such-option'\n{help}", bad.Stderr);
        Assert.Equal(129, bad.ExitCode);
    }
}
