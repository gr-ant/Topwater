using System.Text;
using System.Text.Json.Nodes;
using Topwater.Connectors;

namespace Topwater.Tests;

/// <summary>The Microsoft Graph connector (docs/connectors/graph.md): tests/connectors/graph/cases.json against the fake Graph,
/// then hostile answers and limits, then (opt-in, TOPWATER_LIVE_GRAPH=1) a real site: listed, and written in a scratch folder and scratch list items.</summary>
public class GraphTests
{
    static readonly string Root = FindRoot();
    static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "tests", "connectors", "graph", "cases.json")))!;
    static readonly CancellationToken None = CancellationToken.None;

    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tests", "connectors", "graph", "cases.json"))) return d.FullName;
        throw new FileNotFoundException("tests/connectors/graph/cases.json");
    }

    static Func<CancellationToken, ValueTask<string>> Token(string token = "secret-token") => _ => ValueTask.FromResult(token);

    static GraphSource Source(GraphFake fake, string token = "secret-token") => new(Token(token), new GraphOptions { Site = fake.SiteUrl, Transport = fake });

    static MountMode Mode(string mode) => mode switch { "staged" => MountMode.Staged, "live" => MountMode.Live, _ => MountMode.ReadOnly };

    static string ChangeLine(GraphSource src, MountChange c)
    {
        var line = $"{c.Kind.ToString().ToLowerInvariant()} {c.Path}" + (c.To != null ? $" -> {c.To}" : "");
        Assert.Equal(src.Note(c), c.Note); // the source's note comes with PendingChanges (the agent's `mount -c` never shows it)
        return c.Note is { } note ? $"{line} # {note}" : line;
    }

    static async Task<List<JsonNode?>> RunCase(JsonObject c)
    {
        var fake = GraphFake.FromTree((JsonObject)(c["tree"] ?? Cases["tree"])!);
        var src = Source(fake);
        var sh = new Shell();
        await sh.MountAsync("/mnt/sp", src, Mode(c["mode"]!.GetValue<string>()));
        var output = new List<JsonNode?>();
        foreach (var step in ((JsonArray)c["steps"]!).Cast<JsonObject>())
        {
            if (step["run"] is { } run)
            {
                var r = await sh.RunAsync(run.GetValue<string>());
                output.Add(new JsonObject { ["stdout"] = r.Stdout, ["stderr"] = r.Stderr, ["exitCode"] = r.ExitCode });
            }
            else if (step.ContainsKey("changes")) output.Add(new JsonArray([.. sh.PendingChanges("/mnt/sp").Select(x => (JsonNode)ChangeLine(src, x))]));
            else if (step.ContainsKey("apply"))
            {
                var r = await sh.ApplyChangesAsync("/mnt/sp");
                output.Add(new JsonObject
                {
                    ["applied"] = new JsonArray([.. r.Applied.Select(a => (JsonNode)a.Path)]), ["failed"] = r.Failed?.Path, ["conflict"] = r.Conflict, ["error"] = r.Error,
                });
            }
            else if (step.ContainsKey("graph")) output.Add(new JsonArray([.. fake.Outline().Split('\n').Select(l => (JsonNode)l)]));
            else if (step["edit"] is JsonObject e)
            {
                fake.Edit(fake.DriveNamed(e["library"]!.GetValue<string>(), fake.RootSite), e["path"]!.GetValue<string>(), e["content"]!.GetValue<string>());
                output.Add(null);
            }
            else if (step["fault"] is JsonObject f)
            {
                var headers = (f["headers"] as JsonArray ?? []).Select(h => KeyValuePair.Create(h![0]!.GetValue<string>(), h[1]!.GetValue<string>()));
                fake.AddFault(f["method"]!.GetValue<string>(), f["path"]!.GetValue<string>(), f["status"]!.GetValue<int>(), headers, f["body"]?.GetValue<string>(), f["times"]?.GetValue<int>() ?? 1);
                output.Add(null);
            }
        }
        Assert.Empty(fake.Leaks);
        return output;
    }

    public static IEnumerable<object[]> CaseNames() => ((JsonArray)Cases["cases"]!).Select(c => new object[] { c!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task SharedCase(string name)
    {
        var c = ((JsonArray)Cases["cases"]!).Cast<JsonObject>().First(x => x["name"]!.GetValue<string>() == name);
        var got = await RunCase(c);
        var steps = ((JsonArray)c["steps"]!).Cast<JsonObject>().ToList();
        for (int i = 0; i < steps.Count; i++)
            Assert.Equal((steps[i]["expect"]?.ToJsonString() ?? "null") + $" (step {i})", (got[i]?.ToJsonString() ?? "null") + $" (step {i})");
    }

    // ---------- what only this port checks ----------

    static GraphFake Tree(JsonObject? libraries = null) =>
        GraphFake.FromTree(new JsonObject { ["site"] = "S", ["libraries"] = libraries ?? new JsonObject { ["Documents"] = new JsonObject { ["a.txt"] = "a\n" } } });

    static JsonObject Lib(string name, params (string Path, string? Content)[] files)
    {
        var o = new JsonObject();
        foreach (var (p, c) in files) o[p] = c;
        return new JsonObject { [name] = o };
    }

    static async Task<(Shell, GraphSource)> Mounted(GraphFake fake, MountMode mode = MountMode.Staged)
    {
        var src = Source(fake);
        var sh = new Shell();
        await sh.MountAsync("/mnt/sp", src, mode);
        return (sh, src);
    }

    static void NoWaits(GraphSource src) => src.Api.Delay = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task ChildrenArePagedAndANextPageElsewhereIsNotFollowed()
    {
        var fake = Tree(Lib("Documents", [.. Enumerable.Range(0, 23).Select(i => ($"f{i:D2}.txt", (string?)i.ToString()))]));
        fake.PageSize = 5;
        var (sh, _) = await Mounted(fake, MountMode.ReadOnly);
        Assert.Equal("23\n", (await sh.RunAsync("ls /mnt/sp/Documents | wc -l")).Stdout);
        Assert.Equal(5, fake.Requests.Count(r => r.Path.EndsWith("/children")));

        var evil = Tree(Lib("Documents", [.. Enumerable.Range(0, 9).Select(i => ($"f{i}.txt", (string?)"x"))]));
        evil.PageSize = 4;
        evil.NextLinkOverride = "https://evil.example.com/steal?x=1";
        var e = await Assert.ThrowsAsync<ArgumentException>(() => Mounted(evil, MountMode.ReadOnly));
        Assert.Contains("next page on another host; not followed", e.Message);
        Assert.Empty(evil.Leaks);
        Assert.DoesNotContain(evil.Requests, r => r.Path.Contains("steal"));
    }

    [Fact]
    public async Task ADownloadRedirectNeverGetsTheToken()
    {
        var fake = Tree();
        fake.DownloadHost = "https://evil.example.com";
        var (sh, _) = await Mounted(fake, MountMode.ReadOnly);
        Assert.Equal("a\n", (await sh.RunAsync("cat /mnt/sp/Documents/a.txt")).Stdout);
        Assert.Empty(fake.Leaks);
        Assert.Contains(("GET", "/_layouts/15/download.aspx"), fake.Requests);

        fake.DownloadHost = "http://plain.example.com";
        (sh, _) = await Mounted(fake, MountMode.ReadOnly);
        var r = await sh.RunAsync("cat /mnt/sp/Documents/a.txt");
        Assert.Contains("isn't https", r.Stderr);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public async Task AThrottlingStormWaitsThenGivesUp()
    {
        var fake = Tree();
        var src = Source(fake);
        var slept = new List<double>();
        src.Api.Delay = (t, _) => { lock (slept) slept.Add(t.TotalSeconds); return Task.CompletedTask; };
        fake.AddFault("GET", "/drives$", 429, [KeyValuePair.Create("Retry-After", "3")], times: 3);
        Assert.Equal(["Documents"], (await src.ListAsync("/", 10, None)).Select(e => e.Name));
        Assert.True(slept.Count(s => s > 2.9) >= 3 && slept.Max() <= 3.0, string.Join(",", slept)); // Retry-After holds every request, not only the refused one
        fake.AddFault("GET", "/children$", 503, times: 50);
        var e = await Assert.ThrowsAsync<MountSourceException>(() => src.ListAsync("/Documents", 10, None));
        Assert.Contains("busy (HTTP 503); tried 5 times", e.Message);
        fake.ClearFaults();
        fake.AddFault("GET", "/children$", 429, [KeyValuePair.Create("Retry-After", "3600")]);
        e = await Assert.ThrowsAsync<MountSourceException>(() => src.ListAsync("/Documents", 10, None));
        Assert.Contains("asked to wait 3600 seconds", e.Message);
    }

    [Fact]
    public async Task ABackoffEndsWhenTheCallIsCancelled()
    {
        var fake = Tree();
        var src = Source(fake);
        await src.ListAsync("/", 10, None);
        fake.AddFault("GET", "/children$", 429, [KeyValuePair.Create("Retry-After", "30")], times: 5);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => src.ListAsync("/Documents", 10, cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task APostIsNotRetriedAfterAGatewayTimeout()
    {
        var fake = Tree();
        var src = Source(fake);
        NoWaits(src);
        await src.ListAsync("/", 10, None);
        fake.AddFault("POST", "/children$", 504);
        var e = await Assert.ThrowsAsync<MountSourceException>(() => src.MkdirAsync("/Documents/new", None));
        Assert.Contains("SharePoint refused", e.Message);
        Assert.Equal(1, fake.Requests.Count(r => r.Method == "POST"));
    }

    [Fact]
    public async Task MalformedAnswersAreErrorsNotCrashes()
    {
        var fake = Tree();
        var src = Source(fake);
        await src.ListAsync("/", 10, None);
        foreach (var body in new[] { "not json", "[1, 2]", "{\"value\": \"nope\"}", "{\"value\": [{\"name\": 5}, {\"name\": \"x\", \"file\": {}, \"size\": -3}]}", "[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]" })
        {
            fake.AddFault("GET", "/children$", 200, body: body);
            try
            {
                var got = await src.ListAsync("/Documents", 10, None);
                Assert.All(got, x => Assert.True(x.Size is null or >= 0));
            }
            catch (MountSourceException e) { Assert.Contains("isn't a JSON object", e.Message); }
        }
        fake.AddFault("GET", "/drives$", 200, body: "{\"value\": [{\"id\": \"../../me\", \"name\": \"Evil\"}, {\"id\": \"b!x\", \"name\": \"a/b\"}]}");
        Assert.Equal(["a∕b"], (await src.ListAsync("/", 10, None)).Select(e => e.Name));
    }

    [Fact]
    public async Task IllegalNamesAndLongPathsAreRefusedBeforeARequest()
    {
        var fake = Tree();
        var src = Source(fake);
        await src.ListAsync("/Documents", 10, None);
        foreach (var bad in new[] { "a\"b", "a*b", "a:b", "a<b", "a>b", "a?b", "a\\b", "a|b", " lead", "trail ", "trail.", "CON", "nul", ".lock", "desktop.ini", "x_vti_y", "~$lock.docx" })
        {
            int n = fake.Requests.Count;
            var e = await Assert.ThrowsAsync<MountSourceException>(() => src.WriteAsync($"/Documents/{bad}", "x"u8.ToArray(), null, None));
            Assert.Contains("doesn't allow this name", e.Message);
            Assert.All(fake.Requests.Skip(n), r => Assert.Equal("GET", r.Method));
        }
        Assert.Contains("doesn't allow", (await Assert.ThrowsAsync<MountSourceException>(() => src.MkdirAsync("/Documents/forms", None))).Message);
        await src.MkdirAsync("/Documents/deep", None);
        Assert.Contains("too long", (await Assert.ThrowsAsync<MountSourceException>(() => src.WriteAsync("/Documents/deep/" + new string('x', 390), "x"u8.ToArray(), null, None))).Message);
        Assert.NotEmpty(await src.WriteAsync("/Documents/a#b%c.txt", "ok"u8.ToArray(), null, None));
        Assert.Equal("ok", Encoding.UTF8.GetString(await src.ReadAsync("/Documents/a#b%c.txt", 100, None)));
    }

    [Fact]
    public async Task AnItemDeletedBetweenListAndRead()
    {
        var fake = Tree();
        var (sh, _) = await Mounted(fake, MountMode.ReadOnly);
        fake.Remove(fake.DriveNamed("Documents"), "a.txt");
        var r = await sh.RunAsync("cat /mnt/sp/Documents/a.txt");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("deleted in SharePoint since it was listed", r.Stderr);
    }

    [Fact]
    public async Task APartialApplyReportsWhereItStopped()
    {
        var fake = Tree();
        var (sh, src) = await Mounted(fake);
        NoWaits(src);
        await sh.RunAsync("cd /mnt/sp/Documents && echo 1 > one.txt && echo 2 > two.txt && echo 3 > three.txt");
        fake.AddFault("PUT", "three.txt:/content$", 500);
        fake.AddFault("PUT", "two.txt:/content$", 423);
        var r = await sh.ApplyChangesAsync("/mnt/sp");
        Assert.Equal(["/Documents/one.txt"], r.Applied.Select(c => c.Path));
        Assert.Equal("/Documents/three.txt", r.Failed?.Path);
        Assert.Equal(["/Documents/two.txt"], r.NotTried.Select(c => c.Path));
        r = await sh.ApplyChangesAsync("/mnt/sp"); // the 500 was answered once: three.txt goes now, and two.txt is locked
        Assert.Equal(["/Documents/three.txt"], r.Applied.Select(c => c.Path));
        Assert.Equal("/Documents/two.txt", r.Failed?.Path);
        Assert.False(r.Conflict);
        Assert.Contains("locked in SharePoint", r.Error);
        r = await sh.ApplyChangesAsync("/mnt/sp");
        Assert.Equal(["/Documents/two.txt"], r.Applied.Select(c => c.Path));
        Assert.Null(r.Failed);
        Assert.Empty(sh.PendingChanges("/mnt/sp"));
    }

    [Fact]
    public async Task AConflictOnRenameAndDelete()
    {
        var fake = Tree(Lib("Documents", ("a.txt", "a\n"), ("b.txt", "b\n"), ("keep/", null)));
        var (sh, _) = await Mounted(fake);
        await sh.RunAsync("mv /mnt/sp/Documents/a.txt /mnt/sp/Documents/keep/; rm /mnt/sp/Documents/b.txt");
        var d = fake.DriveNamed("Documents");
        fake.Edit(d, "a.txt", "theirs");
        var r = await sh.ApplyChangesAsync("/mnt/sp");
        Assert.True(r.Conflict);
        Assert.Equal("/Documents/a.txt", r.Failed?.Path);
        Assert.Empty(r.Applied);
        sh.DiscardChanges("/mnt/sp");
        await sh.RunAsync("rm /mnt/sp/Documents/b.txt");
        fake.Edit(d, "b.txt", "theirs");
        r = await sh.ApplyChangesAsync("/mnt/sp");
        Assert.True(r.Conflict);
        Assert.NotNull(fake.Find(d, "b.txt"));
    }

    [Fact]
    public async Task AFolderSomeoneAddedToIsNotDeleted()
    {
        var fake = Tree(Lib("Documents", ("old/x.txt", "x")));
        var (sh, _) = await Mounted(fake);
        await sh.RunAsync("rm -r /mnt/sp/Documents/old");
        fake.Add(fake.DriveNamed("Documents"), "old/new.txt", "new");
        var r = await sh.ApplyChangesAsync("/mnt/sp");
        Assert.Equal("/Documents/old", r.Failed?.Path);
        Assert.Contains("isn't empty", r.Error);
        Assert.NotNull(fake.Find(fake.DriveNamed("Documents"), "old/new.txt"));
    }

    [Fact]
    public async Task ALargeFileGoesThroughAnUploadSession()
    {
        var fake = Tree();
        var src = Source(fake);
        var big = Enumerable.Range(0, 256 * (5 * 4096 + 7)).Select(i => (byte)i).ToArray(); // a little over 5 MB, not a multiple of the chunk
        var v = await src.WriteAsync("/Documents/big.bin", big, null, None);
        var d = fake.DriveNamed("Documents");
        Assert.Equal(big, fake.Items[fake.Find(d, "big.bin")!].Content);
        Assert.StartsWith("\"c:", v);
        Assert.Equal(2, fake.Requests.Count(r => r.Path.StartsWith("/up/")));
        Assert.Empty(fake.Leaks);
        await Assert.ThrowsAsync<MountConflictException>(() => src.WriteAsync("/Documents/big.bin", [.. big, 33], "\"c:{stale},1\"", None));
        var v2 = await src.WriteAsync("/Documents/big.bin", [.. big, 33], v, None);
        Assert.Equal([.. big, 33], fake.Items[fake.Find(d, "big.bin")!].Content);
        Assert.NotEqual(v, v2);
        Assert.Equal([.. big, 33], await src.ReadAsync("/Documents/big.bin", 10_000_000, None));
        Assert.Contains("too large", (await Assert.ThrowsAsync<MountSourceException>(() => src.ReadAsync("/Documents/big.bin", 1000, None))).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARenameKeepsTheItemAndAParentRenameKeepsTheVersions()
    {
        var fake = Tree(Lib("Documents", ("p/a.txt", "a")));
        var src = Source(fake);
        var d = fake.DriveNamed("Documents");
        var iid = fake.Find(d, "p/a.txt");
        var before = (await src.StatAsync("/Documents/p/a.txt", None))!.Version;
        await src.RenameAsync("/Documents/p", "/Documents/q", null, None);
        Assert.Equal(before, (await src.StatAsync("/Documents/q/a.txt", None))!.Version);
        await src.MkdirAsync("/Documents/r", None);
        await src.RenameAsync("/Documents/q/a.txt", "/Documents/r/A.txt", before, None);
        Assert.Equal(iid, fake.Find(d, "r/A.txt"));
        Assert.Null(await src.StatAsync("/Documents/r/a.txt", None)); // another case is another name here
    }

    [Fact]
    public async Task SubsitesNestAndAreBounded()
    {
        var node = new JsonObject { ["libraries"] = Lib("Documents", ("leaf.txt", "leaf\n")) };
        for (int i = 6; i >= 1; i--) node = new JsonObject { ["libraries"] = new JsonObject { ["Documents"] = new JsonObject() }, ["subsites"] = new JsonObject { [$"L{i}"] = node } };
        node["site"] = "Top";
        var fake = GraphFake.FromTree(node);
        int saved = GraphSource.MaxSiteDepth;
        GraphSource.MaxSiteDepth = 3;
        try
        {
            var (sh, _) = await Mounted(fake, MountMode.ReadOnly);
            var r = await sh.RunAsync("find /mnt/sp -maxdepth 8 | sort");
            Assert.Contains("/mnt/sp/L1/L2/L3/Documents", r.Stdout);
            Assert.DoesNotContain("/mnt/sp/L1/L2/L3/L4", r.Stdout);
            var (_, src) = await Mounted(fake);
            foreach (var op in new Func<Task>[] { () => src.MkdirAsync("/L1/New", None), () => src.RenameAsync("/L1", "/L9", null, None), () => src.DeleteAsync("/L1", "", None),
                         () => src.WriteAsync("/L1/x.txt", "x"u8.ToArray(), null, None), () => src.MkdirAsync("/Library2", None) })
                Assert.Matches("(aren't|a site holds)", (await Assert.ThrowsAsync<MountSourceException>(op)).Message);
        }
        finally { GraphSource.MaxSiteDepth = saved; }
    }

    [Fact]
    public async Task AFolderMovesToAnotherSiteByCopyAndDelete()
    {
        var fake = GraphFake.FromTree(new JsonObject
        {
            ["site"] = "S", ["libraries"] = Lib("Documents", ("Designs/a.txt", "a"), ("Designs/sub/b.txt", "b")),
            ["subsites"] = new JsonObject { ["T"] = new JsonObject { ["libraries"] = new JsonObject { ["Shared"] = new JsonObject() } } },
        });
        var (sh, src) = await Mounted(fake);
        await sh.RunAsync("mv /mnt/sp/Documents/Designs /mnt/sp/T/Shared/");
        var c = Assert.Single(sh.PendingChanges("/mnt/sp"));
        Assert.StartsWith("copy + delete", src.Note(c));
        var r = await sh.ApplyChangesAsync("/mnt/sp");
        Assert.Null(r.Error);
        Assert.Equal("/T/Shared/Designs/\n/T/Shared/Designs/a.txt ('a')\n/T/Shared/Designs/sub/\n/T/Shared/Designs/sub/b.txt ('b')\n", fake.Outline());
    }

    [Fact]
    public async Task ListItemsMoveBetweenListsAndNewItemsSkipWhatSharePointSets()
    {
        var fake = GraphFake.FromTree((JsonObject)JsonNode.Parse("""
            {"site": "S", "lists": {"A": {"columns": {"Status": ["x", "y"]}, "items": [{"Title": "one", "Status": "y"}]},
                                    "B": {"columns": {"Status": ["x", "y"]}, "items": []}}}
            """)!);
        var (sh, src) = await Mounted(fake);
        await sh.RunAsync("cp /mnt/sp/A/one.md /mnt/sp/A/two.md && mv /mnt/sp/A/one.md /mnt/sp/B/");
        var notes = sh.PendingChanges("/mnt/sp").Select(src.Note).ToList();
        var r = await sh.ApplyChangesAsync("/mnt/sp");
        Assert.Null(r.Error);
        Assert.Contains(notes, n => n != null && n.StartsWith("copy + delete"));
        Assert.Equal("/A#2 'two' {\"Status\": \"y\"}\n/B#1 'one' {\"Status\": \"y\"}\n", fake.Outline());
    }

    [Fact]
    public async Task TheTokenFunctionIsAskedEveryTimeAndARefusalIsClear()
    {
        var fake = Tree();
        int calls = 0;
        var src = new GraphSource(_ => { Interlocked.Increment(ref calls); return ValueTask.FromResult("secret-token"); }, new GraphOptions { Site = fake.SiteUrl, Transport = fake });
        await src.ListAsync("/Documents", 10, None);
        Assert.Equal(fake.Requests.Count, calls);
        var e = await Assert.ThrowsAsync<MountSourceException>(() => Source(fake, "wrong").ListAsync("/", 10, None));
        Assert.Contains("HTTP 401", e.Message);
    }

    [Fact]
    public void TheOptionsAreChecked()
    {
        var fake = Tree();
        Assert.Throws<ArgumentException>(() => new GraphSource(Token(), new GraphOptions()));
        Assert.Throws<ArgumentException>(() => new GraphSource(Token(), new GraphOptions { Site = "a", Drive = "me" }));
        Assert.Throws<ArgumentException>(() => new GraphSource(Token(), new GraphOptions { Drive = "me", ApiUrl = "http://graph.microsoft.com/v1.0" }));
        Assert.Throws<ArgumentException>(() => new GraphSource(Token(), new GraphOptions { Drive = "me", Transport = new HttpClientHandler() })); // follows redirects
        Assert.Equal("graph", new GraphSource(Token(), new GraphOptions { Drive = "me", Transport = fake }).Type);
    }

    [Fact]
    public async Task TheDefaultTransportRefusesInternalAddresses()
    {
        var src = new GraphSource(Token(), new GraphOptions { Drive = "me", ApiUrl = "https://127.0.0.1/v1.0" });
        NoWaits(src);
        var e = await Assert.ThrowsAsync<MountSourceException>(() => src.ListAsync("/", 10, None));
        Assert.Contains("Microsoft Graph did not answer", e.Message);
        var metadata = new GraphSource(Token(), new GraphOptions { Drive = "me", ApiUrl = "https://169.254.169.254/v1.0" });
        NoWaits(metadata);
        e = await Assert.ThrowsAsync<MountSourceException>(() => metadata.MkdirAsync("/x", None));
        Assert.Contains("did not answer", e.Message);
    }

    [Fact]
    public async Task ADriveAlone()
    {
        var fake = new GraphFake();
        var d = fake.AddDrive(null, "OneDrive");
        fake.Add(d, "Notes/plan.md", "# plan\n");
        var src = new GraphSource(Token(), new GraphOptions { Drive = "me", Transport = fake });
        var sh = new Shell();
        await sh.MountAsync("/mnt/od", src, MountMode.Staged);
        Assert.Equal("# plan\n", (await sh.RunAsync("cat /mnt/od/Notes/plan.md")).Stdout);
        await sh.RunAsync("echo more >> /mnt/od/Notes/plan.md");
        Assert.Null((await sh.ApplyChangesAsync("/mnt/od")).Failed);
        Assert.Equal("# plan\nmore\n", Encoding.UTF8.GetString(fake.Items[fake.Find(d, "Notes/plan.md")!].Content));
    }

    [Fact]
    public async Task DeepTreesStopAtTheMountDepth()
    {
        var fake = Tree(Lib("Documents", (string.Join("/", Enumerable.Range(0, 40).Select(i => $"d{i}")) + "/leaf.txt", "x")));
        var (sh, _) = await Mounted(fake, MountMode.ReadOnly);
        var r = await sh.RunAsync("find /mnt/sp/Documents | wc -l");
        Assert.True(int.Parse(r.Stdout.Trim()) <= 40, r.Stdout);
    }

    [Fact]
    public async Task RefusalsReachAnyWriterThroughTheMount()
    {
        // the connector refuses in its own operations, so a refusal reaches whatever wrote the file: here a host command that
        // writes through the file system, not through bash
        var fake = GraphFake.FromTree((JsonObject)Cases["tree"]!.DeepClone());
        var (sh, _) = await Mounted(fake, MountMode.Live);
        sh.Register("writer", ctx =>
        {
            sh.Fs.WriteAllText("/mnt/sp/Documents/a:b.txt", "x\n");
            const string item = "/mnt/sp/Project Tracker/Pricing page.md";
            sh.Fs.WriteAllText(item, sh.Fs.ReadAllText(item).Replace("Status: \"In progress\"", "Status: \"Someday\""));
            return 0;
        });
        await sh.RunAsync("cat '/mnt/sp/Project Tracker/Pricing page.md' >/dev/null");
        var r = await sh.RunAsync("writer");
        Assert.Contains("mount: /mnt/sp/Documents/a:b.txt: /Documents/a:b.txt: SharePoint doesn't allow this name", r.Stderr);
        Assert.Contains("Status: expected one of Not started, In progress, Done, Blocked; the change stays pending", r.Stderr);
        Assert.Null(fake.Find(fake.DriveNamed("Documents"), "a:b.txt"));
        Assert.DoesNotContain("\"Someday\"", fake.Outline());

        var (staged, _) = await Mounted(GraphFake.FromTree((JsonObject)Cases["tree"]!.DeepClone()));
        await staged.RunAsync("echo x > '/mnt/sp/Documents/CON'");
        var res = await staged.ApplyChangesAsync("/mnt/sp");
        Assert.Equal("/Documents/CON", res.Failed?.Path);
        Assert.Contains("doesn't allow this name", res.Error);
    }

    [Fact]
    public async Task OfficeDocumentsAreTheirBytes()
    {
        var fake = Tree();
        fake.Add(fake.DriveNamed("Documents"), "brief.docx", DocumentTests.MakeDocx());
        var (sh, _) = await Mounted(fake, MountMode.ReadOnly);
        var r = await sh.RunAsync("to-markdown /mnt/sp/Documents/brief.docx");
        Assert.Contains("Annual Report", r.Stdout);
    }

    [Fact]
    public void FrontMatterRoundTrips()
    {
        var values = new Dictionary<string, JsonNode?> { ["Status"] = "Done", ["Budget"] = 25.5, ["Tags"] = new JsonArray("a", "b"), ["odd key:"] = true, ["Notes"] = null };
        var text = ConnectorCommon.FrontMatter(values, "read-only: ID");
        Assert.Equal("---\nBudget: 25.5\nNotes: null\nStatus: \"Done\"\nTags: [\"a\",\"b\"]\n\"odd key:\": true\n# read-only: ID\n---\n", text);
        var (back, body) = ConnectorCommon.SplitFrontMatter("/x.md", text);
        Assert.Equal("", body);
        Assert.All(values, kv => Assert.True(ConnectorCommon.Same(kv.Value, back![kv.Key]), kv.Key));
        var (yaml, _) = ConnectorCommon.SplitFrontMatter("/x.md", "---\nStatus: In progress\nDone: yes\nTags:\n  - a\n  - 'b'\nEmpty: ~\n---\n");
        Assert.Equal("{\"Status\":\"In progress\",\"Done\":true,\"Tags\":[\"a\",\"b\"],\"Empty\":null}", new JsonObject(yaml!.Select(kv => KeyValuePair.Create(kv.Key, kv.Value))).ToJsonString());
        Assert.Contains("no closing", Assert.Throws<MountSourceException>(() => ConnectorCommon.SplitFrontMatter("/x.md", "---\na: 1\n")).Message);
        Assert.Contains("one `name: value`", Assert.Throws<MountSourceException>(() => ConnectorCommon.SplitFrontMatter("/x.md", "---\njunk\n---\n")).Message);
        Assert.Equal(["a.md", "a (2).md", "b", "b (2)"], ConnectorCommon.Unique(["a.md", "a.md", "b", "b"]));
        Assert.Equal("(..)", ConnectorCommon.SafeName("..", "x"));
        Assert.Equal("x", ConnectorCommon.SafeName(" \t ", "x"));
        Assert.True(Encoding.UTF8.GetByteCount(ConnectorCommon.SafeName(new string('é', 300), "x")) <= ConnectorCommon.NameBytes);
    }

    // ----- the real service (opt-in: TOPWATER_LIVE_GRAPH=1, TOPWATER_GRAPH_TOKEN, TOPWATER_GRAPH_SITE) -----
    // The writes stay in a scratch folder (Documents/zz-topwater-test-N) and scratch items in Project Tracker (titles starting
    // "zz topwater"), deleted afterwards whatever happens.

    const string GraphApi = "https://graph.microsoft.com/v1.0";

    static (string Token, string Site)? Live()
    {
        if (Environment.GetEnvironmentVariable("TOPWATER_LIVE_GRAPH") != "1") return null;
        var token = Environment.GetEnvironmentVariable("TOPWATER_GRAPH_TOKEN");
        var site = Environment.GetEnvironmentVariable("TOPWATER_GRAPH_SITE");
        Assert.False(string.IsNullOrEmpty(token) || string.IsNullOrEmpty(site), "TOPWATER_LIVE_GRAPH=1 needs TOPWATER_GRAPH_TOKEN and TOPWATER_GRAPH_SITE");
        return (token!, site!);
    }

    static GraphSource LiveSource((string Token, string Site) live) => new(Token(live.Token), new GraphOptions { Site = live.Site });

    static Task<JsonObject> Raw(GraphSource src, string method, string url, JsonObject? body = null) => src.Api.JsonAsync(method, GraphApi + url, body, null, None);

    /// <summary>The site's id and its Documents library's drive id, asked directly (the same requests on the fake and the real service).</summary>
    static async Task<(string Site, string Drive)> Ids(GraphSource src, string site)
    {
        var id = (string)(await Raw(src, "GET", GraphSource.SiteUrl(site) + "?$select=id"))["id"]!;
        var drives = (JsonArray)(await Raw(src, "GET", $"/sites/{id}/drives?$select=id,name"))["value"]!;
        return (id, (string)drives.First(d => (string?)d!["name"] == "Documents")!["id"]!);
    }

    static async Task InScratchFolder((string Token, string Site) live, Func<GraphSource, string, Task> body)
    {
        var src = LiveSource(live);
        var name = $"zz-topwater-test-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await src.MkdirAsync($"/Documents/{name}", None);
        try { await body(src, name); }
        finally
        {
            var (_, drive) = await Ids(src, live.Site);
            var url = $"/drives/{drive}/root:/{Uri.EscapeDataString(name)}:";
            try { await Raw(src, "POST", url + "/permanentDelete"); }
            catch (Exception) { await Raw(src, "DELETE", url); } // the recycle bin, if permanent deletion is not allowed
        }
    }

    [Fact]
    public async Task LiveSiteListsReadOnly()
    {
        if (Live() is not { } live) return;
        var sh = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(120) });
        await sh.MountAsync("/mnt/sp", LiveSource(live), MountMode.ReadOnly);
        var r = await sh.RunAsync("ls /mnt/sp && ls /mnt/sp/Documents | head");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Documents", r.Stdout);
    }

    [Fact]
    public async Task LiveFilesRoundTrip()
    {
        if (Live() is not { } live) return;
        await InScratchFolder(live, async (src, name) =>
        {
            var sh = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(300) });
            await sh.MountAsync("/mnt/sp", src, MountMode.Staged);
            var r = await sh.RunAsync($"cd '/mnt/sp/Documents/{name}' && echo hello > a.txt && mkdir sub && head -c 5000000 /dev/zero | tr '\\0' x > sub/big.txt && mount -c");
            Assert.True(r.ExitCode == 0, r.Stderr);
            var res = await sh.ApplyChangesAsync("/mnt/sp");
            Assert.True(res.Failed == null, res.Error);
            await sh.RunAsync($"cd '/mnt/sp/Documents/{name}' && mv a.txt sub/b.txt && echo again >> sub/b.txt && rm sub/big.txt");
            res = await sh.ApplyChangesAsync("/mnt/sp");
            Assert.True(res.Failed == null, res.Error);
            await sh.RefreshMountAsync("/mnt/sp");
            r = await sh.RunAsync($"cd '/mnt/sp/Documents/{name}' && find . | sort && cat sub/b.txt");
            Assert.True(r.Stdout == ".\n./sub\n./sub/b.txt\nhello\nagain\n", r.Stdout + r.Stderr);
            var st = await src.StatAsync($"/Documents/{name}/sub/b.txt", None);
            Assert.StartsWith("\"c:", st!.Version);
            await Assert.ThrowsAsync<MountConflictException>(() => src.WriteAsync($"/Documents/{name}/sub/b.txt", "x"u8.ToArray(), "\"c:{00000000-0000-0000-0000-000000000000},1\"", None));
            Assert.Contains("doesn't allow", (await Assert.ThrowsAsync<MountSourceException>(() => src.WriteAsync($"/Documents/{name}/CON", "x"u8.ToArray(), null, None))).Message);
        });
    }

    [Fact]
    public async Task LiveListItems()
    {
        if (Live() is not { } live) return;
        var src = LiveSource(live);
        var (site, _) = await Ids(src, live.Site);
        var lists = (JsonArray)(await Raw(src, "GET", $"/sites/{site}/lists?$select=id,displayName"))["value"]!;
        var list = (string)lists.First(l => (string?)l!["displayName"] == "Project Tracker")!["id"]!;
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            foreach (var t in new[] { $"zz topwater {stamp} a", $"zz topwater {stamp} b" })
                await Raw(src, "POST", $"/sites/{site}/lists/{list}/items", new JsonObject { ["fields"] = new JsonObject { ["Title"] = t, ["Status"] = "Not started", ["Budget"] = 10 } });
            var sh = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(120) });
            await sh.MountAsync("/mnt/sp", src, MountMode.Staged);
            string a = $"zz topwater {stamp} a.md", b = $"zz topwater {stamp} b.md", c = $"zz topwater {stamp} c.md";
            var r = await sh.RunAsync($"cd '/mnt/sp/Project Tracker' && ls && cat '{a}'");
            Assert.True(r.Stdout.Contains("Status page.md") && r.Stdout.Contains("Status: \"Not started\"") && r.Stdout.Contains("# read-only: "), r.Stdout + r.Stderr);
            await sh.RunAsync($"cd '/mnt/sp/Project Tracker' && sed -i 's/^Status: .*/Status: \"In progress\"/; s/^Budget: .*/Budget: 25.5/' '{a}' "
                + $"&& rm '{b}' && printf -- '---\\nStatus: \"Done\"\\nDueDate: \"2027-02-01\"\\n---\\n' > '{c}'");
            var res = await sh.ApplyChangesAsync("/mnt/sp");
            Assert.True(res.Failed == null, res.Error);
            await sh.RefreshMountAsync("/mnt/sp");
            r = await sh.RunAsync($"cd '/mnt/sp/Project Tracker' && ls | grep 'zz topwater' && cat '{a}' '{c}'");
            Assert.DoesNotContain(b, r.Stdout);
            foreach (var want in new[] { "Status: \"In progress\"", "Budget: 25.5", "Status: \"Done\"", "DueDate: \"2027-02-01" }) Assert.True(r.Stdout.Contains(want), want + "\n" + r.Stdout);
            var st = (await src.StatAsync($"/Project Tracker/{a}", None))!;
            var text = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(await src.ReadAsync($"/Project Tracker/{a}", 100_000, None)).Replace("\"In progress\"", "\"Blocked\""));
            var stale = st.Version[..st.Version.LastIndexOf(',')] + ",1\"";
            Assert.NotEqual(st.Version, stale);
            await Assert.ThrowsAsync<MountConflictException>(() => src.WriteAsync($"/Project Tracker/{a}", text, stale, None)); // SharePoint checks If-Match on an item's fields
            await sh.RunAsync($"cd '/mnt/sp/Project Tracker' && sed -i 's/^ID: .*/ID: 999/' '{a}'");
            Assert.Contains("ID can't be changed (set by SharePoint)", (await sh.ApplyChangesAsync("/mnt/sp")).Error);
        }
        finally
        {
            var items = (JsonArray)(await Raw(src, "GET", $"/sites/{site}/lists/{list}/items?expand=fields(select=Title)&$top=500"))["value"]!;
            foreach (var it in items)
                if (((string?)it!["fields"]?["Title"] ?? "").StartsWith("zz topwater", StringComparison.Ordinal))
                    await Raw(src, "DELETE", $"/sites/{site}/lists/{list}/items/{(string)it["id"]!}");
        }
    }

    /// <summary>The fields the connector reads from an item, a listing, a site and its drives are where the fake puts them.</summary>
    [Fact]
    public async Task LiveAnswersHaveTheShapeTheFakeGives()
    {
        if (Live() is not { } live) return;
        await InScratchFolder(live, async (src, name) =>
        {
            var fake = Tree();
            var fsrc = Source(fake);
            await fsrc.MkdirAsync("/Documents/f", None);
            var real = await Shapes(src, live.Site, name);
            var faked = await Shapes(fsrc, fake.SiteUrl, "f");
            foreach (var (r0, f0) in real.Zip(faked))
            {
                var r = r0["value"] is JsonArray rv ? (JsonObject)rv[0]! : r0;
                var f = f0["value"] is JsonArray fv ? (JsonObject)fv[0]! : f0;
                foreach (var key in new[] { "id", "name", "eTag", "cTag", "size", "file", "folder" })
                    Assert.True(r.ContainsKey(key) == f.ContainsKey(key), $"{key}: real [{string.Join(",", r.Select(kv => kv.Key))}], fake [{string.Join(",", f.Select(kv => kv.Key))}]");
            }
        });

        static async Task<JsonObject[]> Shapes(GraphSource s, string siteRef, string folder)
        {
            var (site, drive) = await Ids(s, siteRef);
            await s.WriteAsync($"/Documents/{folder}/shape.txt", "x"u8.ToArray(), null, None);
            var f = Uri.EscapeDataString(folder);
            return [await Raw(s, "GET", $"/drives/{drive}/root:/{f}/shape.txt"), await Raw(s, "GET", $"/drives/{drive}/root:/{f}:/children"),
                await Raw(s, "GET", $"/sites/{site}"), await Raw(s, "GET", $"/sites/{site}/drives")];
        }
    }
}
