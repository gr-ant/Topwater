using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Topwater;

namespace Topwater.Tests;

/// <summary>git: identity, deterministic ids on the virtual clock, and hostile repositories (corrupt or oversized objects,
/// bad indexes, links inside .git, tree entries that point outside the work tree). The everyday behaviour is pinned by
/// tests/corpus/git.jsonl; python/tests/test_git.py has the same checks.</summary>
public class GitTests
{
    const string Ident = "export GIT_AUTHOR_NAME=A GIT_AUTHOR_EMAIL=a@example.com GIT_COMMITTER_NAME=A GIT_COMMITTER_EMAIL=a@example.com "
        + "GIT_AUTHOR_DATE='2026-01-02T03:04:05Z' GIT_COMMITTER_DATE='2026-01-02T03:04:05Z'";

    static async Task<Shell> Repo(ShellOptions? options = null)
    {
        var sh = new Shell(options);
        var r = await sh.RunAsync($"{Ident}; mkdir -p /tmp/w && cd /tmp/w && git init -q && echo a > f && git add f && git commit -qm one");
        Assert.True(r.ExitCode == 0, r.Stderr);
        return sh;
    }

    static byte[] Zlib(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>Writes an object the way a hostile repository could (raw: the stored bytes, whatever they hash to).</summary>
    static string Put(Shell sh, string kind, byte[] body, byte[]? raw = null)
    {
        byte[] data = [.. Encoding.ASCII.GetBytes($"{kind} {body.Length}\0"), .. body];
        var sha = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
        sh.Fs.CreateDirectory($"/tmp/w/.git/objects/{sha[..2]}", true);
        sh.Fs.WriteAllBytes($"/tmp/w/.git/objects/{sha[..2]}/{sha[2..]}", raw ?? Zlib(data));
        return sha;
    }

    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    static string CommitWithTree(Shell sh, byte[] entries)
    {
        var tree = Put(sh, "tree", entries);
        return Put(sh, "commit", B($"tree {tree}\nauthor A <a@b> 1 +0000\ncommitter A <a@b> 1 +0000\n\nx\n"));
    }

    [Fact]
    public async Task DiffsOfHugeFilesAndCommitsStayBounded()
    {
        // a line was three strings: git show of a 30 MB file of short lines took 2.75 GB, and a commit naming that blob at 200
        // paths grew without end
        var sh = await Repo();
        sh.Fs.WriteAllText("/tmp/w/big", string.Concat(Enumerable.Repeat("a\n", 1_100_000)));
        var r = await sh.RunAsync("git add big; git commit -qm big; git show > /dev/null; echo $?");
        Assert.Equal("128\n", r.Stdout);
        Assert.Contains("fatal: a file of more than 1000000 lines is too large for this machine's limits", r.Stderr);
        var sha = Put(sh, "blob", B(string.Concat(Enumerable.Repeat("x\n", 120_000))));
        var tree = new MemoryStream();
        for (int i = 0; i < 200; i++) { tree.Write(B($"100644 p{i:D3}\0")); tree.Write(Convert.FromHexString(sha)); }
        var commit = CommitWithTree(sh, tree.ToArray());
        r = await sh.RunAsync($"git show {commit} | wc -l");
        Assert.Contains("output too large", r.Stderr);
    }

    [Fact]
    public async Task ConfigContinuationsAndManyPackedBranchesHonourTheTimeLimit()
    {
        // joining continued lines was quadratic (59 s from a 350 KB config), and branch -vv parsed packed-refs once per
        // branch (98 s for 5,000): neither checked the call's time limit
        var sh = await Repo(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(20) });
        sh.Fs.AppendAllText("/tmp/w/.git/config", "[core]\n" + string.Concat(Enumerable.Repeat("a = b\\\n", 50_000)) + "a = c\n");
        var (r, took) = await TestHost.Timed(() => sh.RunAsync("git config core.a"));
        Assert.True(took < TimeSpan.FromSeconds(5), $"took {took}");
        Assert.StartsWith("fatal: bad config line", r.Stderr);
        sh.Fs.WriteAllText("/tmp/w/.git/config", "[core]\n\tbare = false\n");
        var head = (await sh.RunAsync("git rev-parse HEAD")).Stdout.Trim();
        sh.Fs.WriteAllText("/tmp/w/.git/packed-refs", string.Concat(Enumerable.Range(0, 5000).Select(i => $"{head} refs/heads/b{i:D4}\n")));
        (r, took) = await TestHost.Timed(() => sh.RunAsync("git branch -vv | wc -l"));
        Assert.Equal("5001\n", r.Stdout);
        Assert.True(took < TimeSpan.FromSeconds(10), $"took {took}");
    }

    [Fact]
    public async Task FsckBoundsTheLinksItKeeps()
    {
        // fsck kept every tree entry as a new string until the end: 8 trees of 800,000 entries (a 15 MB repo) took 2.1 GB
        var sh = await Repo();
        var blob = new byte[20];
        foreach (var prefix in new[] { "a", "b" })
        {
            var tree = new MemoryStream();
            for (int i = 0; i < 600_000; i++) { tree.Write(B($"100644 {prefix}{i:D7}\0")); tree.Write(blob); }
            Put(sh, "tree", tree.ToArray());
        }
        var r = await sh.RunAsync("git fsck");
        Assert.Equal((128, "fatal: the object graph is too large for this machine's limits\n"), (r.ExitCode, r.Stderr));
    }

    [Fact]
    public async Task ACommitWithoutAnIdentityFailsLikeRealGit()
    {
        var r = await new Shell().RunAsync("cd /tmp && git init -q r && cd r && git commit -q --allow-empty -m x");
        Assert.Equal(128, r.ExitCode);
        Assert.Contains("*** Please tell me who you are.", r.Stderr);
        Assert.EndsWith("fatal: unable to auto-detect email address (got 'agent@topwater.(none)')\n", r.Stderr);
    }

    [Fact]
    public async Task TheHostCanPresetAnIdentityThroughTheEnvironmentOrGitconfig()
    {
        var sh = new Shell();
        sh.Environment["GIT_AUTHOR_NAME"] = sh.Environment["GIT_COMMITTER_NAME"] = "Host Set";
        sh.Environment["GIT_AUTHOR_EMAIL"] = sh.Environment["GIT_COMMITTER_EMAIL"] = "host@example.com";
        var r = await sh.RunAsync("cd /tmp && git init -q r && cd r && git commit -q --allow-empty -m x && git log --format='%an <%ae>'");
        Assert.Equal("Host Set <host@example.com>\n", r.Stdout);
        sh = new Shell();
        sh.Fs.WriteAllText("/home/agent/.gitconfig", "[user]\n\tname = File Set\n\temail = file@example.com\n");
        r = await sh.RunAsync("cd /tmp && git init -q r && cd r && git commit -q --allow-empty -m x && git log --format='%an <%ae>'");
        Assert.Equal("File Set <file@example.com>\n", r.Stdout);
    }

    [Fact]
    public async Task TwoDeterministicShellsMakeTheSameCommitIds()
    {
        async Task<string> Run()
        {
            var sh = new Shell(new ShellOptions { StartTime = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), RandomSeed = 3 });
            var r = await sh.RunAsync("export GIT_AUTHOR_NAME=A GIT_AUTHOR_EMAIL=a@b GIT_COMMITTER_NAME=A GIT_COMMITTER_EMAIL=a@b; "
                + "cd /tmp && git init -q r && cd r && echo x > f && git add f && git commit -qm one && sleep 5 && git commit -q --allow-empty -m two && git log --format='%H %ct'");
            Assert.True(r.ExitCode == 0, r.Stderr);
            return r.Stdout;
        }
        var first = await Run();
        Assert.Equal(first, await Run());
        Assert.Contains($" {new DateTimeOffset(2026, 3, 4, 5, 6, 12, TimeSpan.Zero).ToUnixTimeSeconds()}\n", first);
    }

    [Fact]
    public async Task AnObjectWhoseContentsDoNotMatchItsNameIsCorrupt()
    {
        var sh = await Repo();
        var head = (await sh.RunAsync("git rev-parse HEAD")).Stdout.Trim();
        var path = $"/tmp/w/.git/objects/{head[..2]}/{head[2..]}";
        sh.Fs.WriteAllBytes(path, Zlib(B("commit 5\0hello")));
        var r = await sh.RunAsync("git log");
        Assert.Equal(128, r.ExitCode);
        Assert.Equal($"fatal: object {head} is corrupt\n", r.Stderr);
        sh.Fs.WriteAllBytes(path, B("not zlib at all"));
        Assert.Equal(128, (await sh.RunAsync("git status")).ExitCode);
    }

    [Fact]
    public async Task AnOversizedObjectStopsAtTheStringLimit()
    {
        var sh = await Repo(new ShellOptions { MaxStringBytes = 1 << 20 });
        var sha = Put(sh, "blob", new byte[4 << 20]);  // inflates past the limit: never held whole
        var r = await sh.RunAsync($"git cat-file -s {sha}");
        Assert.Equal(128, r.ExitCode);
        Assert.Contains("too large", r.Stderr);
    }

    [Fact]
    public async Task AGarbageIndexIsReportedNotTrusted()
    {
        var sh = await Repo();
        sh.Fs.WriteAllBytes("/tmp/w/.git/index", [.. B("DIRC\0\0\0\u0002\0\0\0\u0005"), .. Enumerable.Repeat((byte)0xff, 40)]);
        var r = await sh.RunAsync("git status");
        Assert.Equal(128, r.ExitCode);
        Assert.StartsWith("fatal: index file corrupt", r.Stderr);
    }

    [Fact]
    public async Task OddRevisionsAndPatternsFailCleanly()
    {
        var sh = await Repo();
        var r = await sh.RunAsync("git rev-parse $'HEAD\\n'");
        Assert.Equal(128, r.ExitCode);
        Assert.StartsWith("fatal: ambiguous argument", r.Stderr);
        r = await sh.RunAsync("git log --oneline -- '[z-a]'; git status -s -- '[z-a]'");
        Assert.Equal((0, "", ""), (r.ExitCode, r.Stdout, r.Stderr));
    }

    [Fact]
    public async Task GitNeverFollowsASymbolicLinkInsideDotGit()
    {
        var sh = await Repo();
        await sh.RunAsync("mkdir -p /tmp/elsewhere/heads && echo 0123 > /tmp/elsewhere/heads/x && rm -rf .git/refs && ln -s /tmp/elsewhere .git/refs");
        var r = await sh.RunAsync("git branch evil");
        Assert.Equal(128, r.ExitCode);
        Assert.Contains("symbolic link", r.Stderr);
        Assert.Equal("0123\n", (await sh.RunAsync("cat /tmp/elsewhere/heads/x")).Stdout);
    }

    [Fact]
    public async Task TreeEntriesThatLeaveTheWorkTreeAreRefused()
    {
        var sh = await Repo();
        var blob = Put(sh, "blob", B("owned\n"));
        foreach (var name in new[] { "..", ".git", "a/b" })
        {
            var c = CommitWithTree(sh, [.. B("100644 " + name + "\0"), .. Convert.FromHexString(blob)]);
            var r = await sh.RunAsync($"git checkout -q {c}");
            Assert.True(r.ExitCode == 128 && r.Stderr.Contains("invalid path"), $"{name}: {r.Stderr}");
        }
        Assert.False(sh.Fs.Exists("/tmp/owned"));
        Assert.Equal("", (await sh.RunAsync("git status -s")).Stdout);
    }

    [Fact]
    public async Task CheckoutReplacesASymbolicLinkDirectoryInsteadOfWritingThroughIt()
    {
        var sh = await Repo();
        await sh.RunAsync("mkdir d && echo x > d/f && git add d && git commit -qm d && rm -rf d && mkdir /tmp/outside && ln -s /tmp/outside d");
        Assert.Equal(" D d/f\n?? d\n", (await sh.RunAsync("git status -s")).Stdout);
        await sh.RunAsync("rm d && ln -s /tmp/outside d && git checkout -- d/f");
        Assert.Empty(sh.Fs.List("/tmp/outside"));
        Assert.False(sh.Fs.IsSymlink("/tmp/w/d"));
        Assert.Equal("x\n", (await sh.RunAsync("cat d/f")).Stdout);
    }

    [Fact]
    public async Task DeeplyNestedTreesEndWithAnError()
    {
        var sh = await Repo();
        var blob = Put(sh, "blob", B("x\n"));
        var tree = Put(sh, "tree", [.. B("100644 f\0"), .. Convert.FromHexString(blob)]);
        for (int i = 0; i < 300; i++) tree = Put(sh, "tree", [.. B("40000 d\0"), .. Convert.FromHexString(tree)]);
        var c = Put(sh, "commit", B($"tree {tree}\nauthor A <a@b> 1 +0000\ncommitter A <a@b> 1 +0000\n\nx\n"));
        var r = await sh.RunAsync($"git show --stat {c}");
        Assert.Equal(128, r.ExitCode);
        Assert.Contains("nested too deeply", r.Stderr);
    }

    [Fact]
    public async Task ALongHistoryWalkRespectsTheCallTimeLimit()
    {
        var sh = await Repo(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(1) });
        var r = await sh.RunAsync("for i in $(seq 400); do git commit -q --allow-empty -m $i; done");
        Assert.Contains(r.ExitCode, new[] { 0, 124 });
        r = await sh.RunAsync("git log --oneline | wc -l");
        Assert.Contains(r.ExitCode, new[] { 0, 124 });
    }

    public static TheoryData<string> PackCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "git", "packs.json"))).RootElement.EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    /// <summary>tests/git/packs.json (made by tests/git/make_packs.py), shared with the other ports.</summary>
    [Theory]
    [MemberData(nameof(PackCases))]
    public async Task PackfilesGoodAndHostile(string name)
    {
        var c = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "git", "packs.json"))).RootElement
            .EnumerateArray().First(x => x.GetProperty("name").GetString() == name);
        bool bound = c.TryGetProperty("cacheBound", out var cb) && cb.GetBoolean();
        // the cache case builds twenty 32 MB objects, slow under load
        double limit = c.TryGetProperty("timeLimit", out var tl) ? tl.GetDouble() : bound ? 60 : 10;
        var sh = await Repo(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(limit) });
        sh.Fs.CreateDirectory("/tmp/w/.git/objects/pack", true);
        foreach (var f in c.GetProperty("files").EnumerateObject())
        {
            using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(f.Value.GetString()!)), CompressionMode.Decompress);
            var ms = new MemoryStream();
            gz.CopyTo(ms);
            sh.Fs.WriteAllBytes($"/tmp/w/.git/objects/pack/{f.Name}", ms.ToArray());
        }
        GitObjectCache.Peak = 0;
        long baseline = GC.GetTotalMemory(true), peak = 0;
        using var sampler = bound ? new Timer(_ => { var m = GC.GetTotalMemory(true); if (m > Interlocked.Read(ref peak)) Interlocked.Exchange(ref peak, m); }, null, 0, 50) : null;  // live memory: a forced collection per sample
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = await sh.RunAsync(c.GetProperty("script").GetString()!);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(bound ? 30 : (c.TryGetProperty("timeLimit", out _) ? limit : 2) + 3));
        Assert.True(c.GetProperty("exitCode").GetInt32() == r.ExitCode, r.Stderr);
        if (bound)
        {
            // every cache of the repository shares one budget, so twenty 32 MB objects are never all held at once
            Assert.InRange(GitObjectCache.Peak, 1, GitPack.CacheBytes);
            Assert.True(Interlocked.Read(ref peak) - baseline < 400L << 20, $"{(Interlocked.Read(ref peak) - baseline) >> 20} MB");
        }
        if (c.TryGetProperty("stdout", out var stdout)) Assert.Equal(stdout.GetString(), r.Stdout);
        if (c.TryGetProperty("stderr", out var stderr))
        {
            Assert.Contains(stderr.GetString()!, r.Stderr);
            Assert.StartsWith("fatal: ", r.Stderr);
        }
    }

    [Fact]
    public async Task PackedRefsNamesThatLeaveRefsAreIgnored()
    {
        var sh = await Repo();
        await sh.RunAsync("s=$(git rev-parse HEAD); printf '%s refs/heads/../../outside\\n%s refs/remotes/../x\\n%s refs/remotes/o/cr\\r\\n"
            + "%s refs/tags/.hidden\\n%s refs/heads/ok\\n' $s $s $s $s $s > .git/packed-refs");
        var r = await sh.RunAsync("git branch -a; git log --oneline --decorate -1; git tag");
        Assert.True(r.ExitCode == 0 && !r.Stdout.Contains("..") && !r.Stdout.Contains("hidden") && !r.Stdout.Contains('\r') && r.Stdout.Contains("ok"), r.Stdout);
        r = await sh.RunAsync("git checkout x; echo $?; cat .git/config");
        Assert.StartsWith("1\n", r.Stdout);
        Assert.DoesNotContain("remote = ..", r.Stdout);
    }

    [Fact]
    public async Task UpstreamErrorsAndAmbiguousCheckout()
    {
        var sh = await Repo();
        var r = await sh.RunAsync("git rev-parse @{u}; git rev-parse nosuch@{u}; git checkout -q --detach; git rev-parse HEAD@{upstream}");
        Assert.Equal("fatal: no upstream configured for branch 'master'\nfatal: no such branch: 'nosuch'\nfatal: HEAD does not point to a branch\n", r.Stderr);
        await sh.RunAsync("git checkout -q master; for r in origin up; do git config remote.$r.fetch \"+refs/heads/*:refs/remotes/$r/*\"; mkdir -p .git/refs/remotes/$r;"
            + " git rev-parse HEAD > .git/refs/remotes/$r/b; done");
        r = await sh.RunAsync("git checkout b");
        Assert.Equal(128, r.ExitCode);
        Assert.Equal("fatal: 'b' matched multiple (2) remote tracking branches\n", r.Stderr);
        await sh.RunAsync("rm -r .git/refs/remotes/up; echo f > b");
        r = await sh.RunAsync("git checkout b");
        Assert.Equal(128, r.ExitCode);
        Assert.Contains("could be both a local file and a tracking branch", r.Stderr);
    }

    /// <summary>A pack of whole (undeltified) objects with its v2 index, as git would write it.</summary>
    static (byte[] Pack, byte[] Idx) BuildPack(List<(string Kind, byte[] Data)> objects)
    {
        var types = new Dictionary<string, int> { ["commit"] = 1, ["tree"] = 2, ["blob"] = 3, ["tag"] = 4 };
        var pack = new List<byte>(Encoding.ASCII.GetBytes("PACK"));
        pack.AddRange([0, 0, 0, 2, .. BitConverter.GetBytes(objects.Count).Reverse()]);
        var entries = new List<(string Name, int Offset)>();
        foreach (var (kind, data) in objects)
        {
            entries.Add((Convert.ToHexString(SHA1.HashData([.. Encoding.ASCII.GetBytes($"{kind} {data.Length}\0"), .. data])).ToLowerInvariant(), pack.Count));
            long size = data.Length;
            int b = (types[kind] << 4) | (int)(size & 15);
            size >>= 4;
            while (size > 0) { pack.Add((byte)(b | 0x80)); b = (int)(size & 0x7F); size >>= 7; }
            pack.Add((byte)b);
            pack.AddRange(Zlib(data));
        }
        pack.AddRange(SHA1.HashData(pack.ToArray()));
        entries.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
        var idx = new List<byte> { 0xff, (byte)'t', (byte)'O', (byte)'c', 0, 0, 0, 2 };
        for (int f = 0; f < 256; f++) idx.AddRange(BitConverter.GetBytes(entries.Count(e => Convert.ToInt32(e.Name[..2], 16) <= f)).Reverse());
        foreach (var e in entries) idx.AddRange(Convert.FromHexString(e.Name));
        foreach (var e in entries) idx.AddRange([0, 0, 0, 0]);
        foreach (var e in entries) idx.AddRange(BitConverter.GetBytes(e.Offset).Reverse());
        idx.AddRange(pack.Skip(pack.Count - 20));
        idx.AddRange(SHA1.HashData(idx.ToArray()));
        return (pack.ToArray(), idx.ToArray());
    }

    [Fact]
    public async Task PackedRepositoriesTakeNewLooseObjects()
    {
        var sh = await Repo();
        await sh.RunAsync("echo b > g && git add g && git commit -qm two");
        // pack everything the way git gc would leave it: objects only in a pack
        var objects = new List<(string, byte[])>();
        foreach (var sha in (await sh.RunAsync("find .git/objects -type f | cut -d/ -f3,4 | tr -d /")).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var z = new ZLibStream(new MemoryStream(sh.Fs.ReadAllBytes($"/tmp/w/.git/objects/{sha[..2]}/{sha[2..]}")), CompressionMode.Decompress);
            var ms = new MemoryStream();
            z.CopyTo(ms);
            var raw = ms.ToArray();
            int sp = Array.IndexOf(raw, (byte)' '), nul = Array.IndexOf(raw, (byte)0);
            objects.Add((Encoding.ASCII.GetString(raw, 0, sp), raw[(nul + 1)..]));
        }
        var (pack, idx) = BuildPack(objects);
        await sh.RunAsync("rm -rf .git/objects/?? && mkdir -p .git/objects/pack");
        sh.Fs.WriteAllBytes("/tmp/w/.git/objects/pack/pack-1.pack", pack);
        sh.Fs.WriteAllBytes("/tmp/w/.git/objects/pack/pack-1.idx", idx);
        var r = await sh.RunAsync("git log --oneline && git status -s && echo c > h && git add h && git commit -qm three && git log --oneline | wc -l"
            + " && find .git/objects -type f | grep -vc pack && git rev-parse --short HEAD~2 | wc -c");
        Assert.True(r.ExitCode == 0, r.Stderr);
        Assert.EndsWith("3\n3\n8\n", r.Stdout);
        r = await sh.RunAsync("git gc; git repack");
        Assert.Equal(128, r.ExitCode);
        Assert.Contains("not available", r.Stderr);
    }

    [Fact]
    public async Task A5000ObjectPackReadsQuickly()
    {
        var sh = await Repo();
        var objects = new List<(string, byte[])>();
        var text = new StringBuilder();
        for (int n = 0; n < 5000; n++)
        {
            text.Append($"line {n}\n");
            var all = text.ToString();
            objects.Add(("blob", Encoding.ASCII.GetBytes(all.Length > 2000 ? all[^2000..] : all)));
        }
        var (pack, idx) = BuildPack(objects);
        sh.Fs.CreateDirectory("/tmp/w/.git/objects/pack", true);
        sh.Fs.WriteAllBytes("/tmp/w/.git/objects/pack/pack-1.pack", pack);
        sh.Fs.WriteAllBytes("/tmp/w/.git/objects/pack/pack-1.idx", idx);
        string Id(byte[] d) => Convert.ToHexString(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {d.Length}\0"), .. d])).ToLowerInvariant();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = await sh.RunAsync($"git cat-file -s {Id(objects[4999].Item2)}; git cat-file -t {Id(objects[10].Item2)[..8]}");
        Assert.Equal("2000\nblob\n", r.Stdout);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GraphRefusesHistoryItCannotDraw()
    {
        var sh = await Repo();
        var r = await sh.RunAsync("git checkout -qb side && git commit -q --allow-empty -m s && git checkout -q master && "
            + "git commit -q --allow-empty -m m && git merge -q side -m merged && git log --graph --oneline");
        Assert.Equal(128, r.ExitCode);
        Assert.Contains("git log --format='%h %p %s'", r.Stderr);
        Assert.Equal("", r.Stdout);
        Assert.Equal(128, (await sh.RunAsync("git log --graph --oneline --all side")).ExitCode);
        Assert.Equal(2, (await sh.RunAsync("git log --graph --oneline side")).Stdout.Split("* ").Length - 1);
    }

    static void WriteIndexWithPath(Shell sh, string blob, string path)
    {
        static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        var entry = new List<byte>();
        foreach (var v in new uint[] { 0, 0, 0, 0, 0, 0, 0x81A4, 0, 0, 2 }) entry.AddRange(U32(v));
        entry.AddRange(Convert.FromHexString(blob));
        entry.AddRange([0x0F, 0xFF]);
        entry.AddRange(B(path));
        entry.AddRange(new byte[8 - entry.Count % 8]);
        byte[] body = [.. B("DIRC"), .. U32(2), .. U32(1), .. entry];
        sh.Fs.WriteAllBytes("/tmp/w/.git/index", [.. body, .. SHA1.HashData(body)]);
    }

    [Fact]
    public async Task APathTooDeepInTheIndexIsRefusedQuickly()
    {
        var sh = await Repo(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(1) });
        var blob = (await sh.RunAsync("git rev-parse HEAD:f")).Stdout.Trim();
        foreach (var depth in new[] { 5_000, 30_000 })
        {
            WriteIndexWithPath(sh, blob, string.Join("/", Enumerable.Repeat("d", depth)) + "/f");
            foreach (var cmd in new[] { "git status", "git commit -qm x", "git checkout -- ." })
            {
                var r = await sh.RunAsync(cmd);
                Assert.True(r.ExitCode == 128 && r.Stderr.Contains("directories deep"), $"{depth} {cmd}: {r.ExitCode} {r.Stderr}");
            }
        }
    }

    [Fact]
    public async Task ASymbolicHeadCannotLeadOutsideDotGit()
    {
        var sh = await Repo();
        sh.Fs.WriteAllText("/tmp/w/.git/HEAD", "ref: refs/heads/../../../../victim/newref\n");
        Assert.Equal(128, (await sh.RunAsync("git commit -q --allow-empty -m x")).ExitCode);
        Assert.False(sh.Fs.Exists("/tmp/victim"));
        sh.Fs.WriteAllText("/tmp/w/.git/HEAD", "ref: ../../victim\n");
        Assert.Contains("not a git repository", (await sh.RunAsync("git status")).Stderr);
    }

    [Fact]
    public async Task AgentPatternsStopAtTheTimeLimit()
    {
        var sh = await Repo(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(1) });
        var star = string.Concat(Enumerable.Repeat("*a", 30)) + "b";
        sh.Fs.WriteAllText("/tmp/w/.gitignore", star + "\n");
        sh.Fs.WriteAllText("/tmp/w/" + new string('a', 200), "x");
        foreach (var cmd in new[] { "git status", $"git add -- '{star}'", "git log --grep='(a+)+b'" })
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var r = await sh.RunAsync(cmd);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), cmd);
            Assert.DoesNotContain("Exception", r.Stderr);
        }
    }

    [Fact]
    public async Task EditorSettingsAndIdentityCleanup()
    {
        var sh = await Repo();
        var r = await sh.RunAsync("EDITOR=vim git commit --allow-empty");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("no editor can run", r.Stderr);
        Assert.Equal("one\n", (await sh.RunAsync("GIT_EDITOR=true git commit -q --amend && git log --format=%s")).Stdout);
        r = await sh.RunAsync("GIT_AUTHOR_NAME=$'Evil\\nName <x>' git commit -q --allow-empty -m n && git log -1 --format='%an' && git status -s");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("EvilName x\n", r.Stdout);
    }

    [Fact]
    public async Task ApplyRefusesPathsOutsideTheWorkTree()
    {
        var sh = await Repo();
        foreach (var name in new[] { "../escape", ".git/config", "/etc/passwd" })
        {
            sh.Fs.WriteAllBytes("/tmp/p.diff", Encoding.UTF8.GetBytes($"--- a/{name}\n+++ b/{name}\n@@ -0,0 +1 @@\n+x\n".Replace("a//", "a/").Replace("b//", "b/")));
            var r = await sh.RunAsync("cd /tmp/w && git apply /tmp/p.diff; echo $?");
            Assert.NotEqual("0", r.Stdout.Trim().Split('\n')[^1]);
        }
        Assert.False(sh.Fs.Exists("/tmp/escape"));
        Assert.DoesNotContain("x\n", (await sh.RunAsync("cat /tmp/w/.git/config")).Stdout);
    }

    [Fact]
    public async Task BisectRunWithSkipsStillFindsTheCommit()
    {
        var sh = await Repo();
        var r = await sh.RunAsync("cd /tmp/w && for i in 2 3 4 5 6; do echo $i > f; git commit -qam c$i; done; git bisect start HEAD HEAD~5 >/dev/null; "
            + "git bisect run sh -c 'n=$(cat f); [ $n = 3 ] && exit 125; [ $n -lt 5 ]' | tail -1; git log -1 --format=%s");
        Assert.Equal("bisect found first bad commit\nc5\n", r.Stdout);
    }

    [Fact]
    public async Task TarTransformCannotLeaveTheTargetDirectory()
    {
        var buf = new MemoryStream();
        using (var t = new System.Formats.Tar.TarWriter(buf, System.Formats.Tar.TarEntryFormat.Gnu, leaveOpen: true))
        {
            foreach (var (name, data) in new[] { ("pkg-1.0/ok.txt", "fine\n"), ("pkg-../pkg-../pkg-../esc1", "pwned\n") })
                t.WriteEntry(new System.Formats.Tar.GnuTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(data)) });
            t.WriteEntry(new System.Formats.Tar.GnuTarEntry(System.Formats.Tar.TarEntryType.HardLink, "pkg-1.0/hl") { LinkName = "pkg-../pkg-../pkg-../victim" });
        }
        var sh = new Shell();
        await sh.RunAsync("mkdir -p /tmp/w/a/b/c");
        sh.Fs.WriteAllBytes("/tmp/w/h.tar", buf.ToArray());
        sh.Fs.WriteAllText("/tmp/w/victim", "secret\n");
        var r = await sh.RunAsync("cd /tmp/w/a/b/c && tar -xf /tmp/w/h.tar --transform='s,pkg-,,g'; echo $?; "
            + "tar -xf /tmp/w/h.tar --transform='s,^pkg-1.0,../../../../esc,'; echo $?");
        Assert.Equal("2\n2\n", r.Stdout);
        Assert.Equal("pwned\n", sh.Fs.ReadAllText("/tmp/w/a/b/c/esc1"));
        Assert.False(sh.Fs.Exists("/tmp/w/esc1") || sh.Fs.Exists("/esc") || sh.Fs.Exists("/tmp/esc"));
        Assert.Equal("fine\n", sh.Fs.ReadAllText("/tmp/w/a/b/c/esc/ok.txt"));
        Assert.Equal("secret\n", sh.Fs.ReadAllText("/tmp/w/victim"));
    }
}
