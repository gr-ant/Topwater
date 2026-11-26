using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public async Task PackedRepositoriesGetAClearMessage()
    {
        var sh = await Repo();
        await sh.RunAsync("mkdir -p .git/objects/pack && touch .git/objects/pack/pack-1.pack .git/objects/pack/pack-1.idx");
        Assert.Equal(128, (await sh.RunAsync("git cat-file -p 0123456789012345678901234567890123456789")).ExitCode);
        await sh.RunAsync("rm -rf .git/objects/$(git rev-parse HEAD | cut -c1-2)");
        var r = await sh.RunAsync("git log");
        Assert.Equal(128, r.ExitCode);
        Assert.Contains("do not delete .git", r.Stderr);
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
}
