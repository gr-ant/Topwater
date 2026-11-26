using Topwater;

namespace Topwater.Tests;

/// <summary>Incremental mirror sync and the bubblewrap launcher (each skipped where its programs are missing).</summary>
public class HostBridgeTests2
{
    static bool Unix => File.Exists("/bin/sh");
    static bool HasBwrap => (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, "bwrap")));

    /// <summary>The bubblewrap launcher, or null (the test is skipped) where bwrap is missing or can't make a sandbox here.</summary>
    static Func<IReadOnlyList<string>, string, IReadOnlyList<string>>? Bwrap(bool network = false)
    {
        if (!HasBwrap) return null;
        // a runner may refuse the namespaces or mounts bwrap needs (seen on GitHub: "Can't remount /newroot/tmp"); only that probe failure skips
        try { return HostSandbox.Bubblewrap(network); }
        catch (InvalidOperationException e) when (e.Message.StartsWith("bwrap cannot create a sandbox", StringComparison.Ordinal)) { return null; }
    }

    static async Task<string> Out(Shell shell, string cmd)
    {
        var r = await shell.RunAsync(cmd);
        Assert.True(r.ExitCode == 0, $"`{cmd}` exited {r.ExitCode}: {r.Stderr}");
        return r.Stdout;
    }

    [Fact]
    public async Task UnchangedFilesAreNotRewrittenAndChangesStillSyncBothWays()
    {
        if (!Unix) return;
        var sh = new Shell();
        using var real = sh.RegisterHostCommand("realsh", "/bin/sh");
        await Out(sh, "echo same > keep.txt && echo one > edit.txt && echo a > prog.txt && realsh -c true");
        var mirror = Path.Combine(real.Root, "home", "agent");
        var keep = Path.Combine(mirror, "keep.txt");
        var stamp = File.GetLastWriteTimeUtc(keep);

        // the agent changes one file to the same size and the program another, right after the export
        Assert.Equal("two\n", await Out(sh, "echo two > edit.txt && realsh -c 'cat edit.txt; echo b > prog.txt'"));
        Assert.Equal("b\n", await Out(sh, "cat prog.txt"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(keep));

        // deletions in both directions still travel
        await Out(sh, "rm keep.txt && realsh -c 'test ! -e keep.txt && rm prog.txt'");
        Assert.False(sh.Fs.Exists("/home/agent/prog.txt"));
        Assert.False(File.Exists(keep));

        // a binary file the program rewrites with the same size comes back
        sh.Fs.WriteAllBytes("/home/agent/bin.dat", [0, 1, 2, 3]);
        await Out(sh, "realsh -c 'printf \"\\000\\011\\012\\013\" > bin.dat'");
        Assert.Equal(new byte[] { 0, 9, 10, 11 }, sh.Fs.ReadAllBytes("/home/agent/bin.dat"));
    }

    [Fact]
    public async Task ManyQuickSameSizeRewritesAreAllSeen()
    {
        if (!Unix) return;
        var sh = new Shell();
        using var real = sh.RegisterHostCommand("realsh", "/bin/sh");
        for (int i = 0; i < 10; i++)
        {
            // the program rewrites the file it was just given, in the same clock tick if the machine is fast
            Assert.Equal($"{i}\n", await Out(sh, $"echo {i} > n.txt && realsh -c 'cat n.txt; echo {(i + 1) % 10} > n.txt'"));
            Assert.Equal($"{(i + 1) % 10}\n", await Out(sh, "cat n.txt"));
        }
    }

    // ---------- bubblewrap ----------

    static (Shell, HostCommand) Sandboxed(Func<IReadOnlyList<string>, string, IReadOnlyList<string>> bwrap)
    {
        var sh = new Shell();
        // RewritePaths off, so these tests hit the sandbox and not the argument redirection
        return (sh, sh.RegisterHostCommand("realsh", "/bin/sh", new HostCommandOptions { Launcher = bwrap, RewritePaths = false }));
    }

    [Fact]
    public async Task BubblewrapRunsTheProgramAndRoundTripsFiles()
    {
        if (Bwrap() is not { } bwrap) return;
        var (sh, cmd) = Sandboxed(bwrap);
        using var _ = cmd;
        Assert.Equal("hello\n", await Out(sh, "mkdir -p proj && cd proj && echo hello > in.txt && realsh -c 'cat in.txt; cp in.txt out.txt; echo t > /tmp/scratch; awk \"BEGIN{exit 0}\"'"));
        Assert.Equal("hello\n", await Out(sh, "cat ~/proj/out.txt"));
        Assert.Equal("/home/agent/proj\n", await Out(sh, "cd ~ && realsh -c 'cd \"$HOME\"/proj && pwd'"));
        Assert.False(sh.Fs.Exists("/tmp/scratch"));   // the program's /tmp is its own
    }

    [Fact]
    public async Task BubblewrapHidesTheRealHomeAndOtherFiles()
    {
        if (Bwrap() is not { } bwrap) return;
        var secret = Path.Combine(Path.GetTempPath(), $"topwater-secret-{Guid.NewGuid():N}");
        File.WriteAllText(secret, "secret");
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var homeFile = Directory.EnumerateFiles(home).FirstOrDefault() ?? Path.Combine(home, ".bashrc");
        try
        {
            var (sh, cmd) = Sandboxed(bwrap);
            using var _ = cmd;
            var r = await sh.RunAsync($"realsh -c 'cat \"$1\" || cat \"$2\" || ls -A \"$3\"' x {secret} {homeFile} {home}");
            Assert.DoesNotContain("secret", r.Stdout);
            Assert.NotEqual(0, r.ExitCode);
            // the same command without the sandbox can read it, so the test means something
            using var plain = sh.RegisterHostCommand("plainsh", "/bin/sh", new HostCommandOptions { RewritePaths = false });
            Assert.Equal("secret", await Out(sh, $"plainsh -c 'cat \"$1\"' x {secret}"));
        }
        finally { File.Delete(secret); }
    }

    [Fact]
    public async Task GitInBubblewrapWorksButItsAliasesCannotReadHostFiles()
    {
        if (!File.Exists("/usr/bin/git") || Bwrap() is not { } bwrap) return;
        var secret = Path.Combine(Path.GetTempPath(), $"topwater-secret-{Guid.NewGuid():N}");
        File.WriteAllText(secret, "secret");
        try
        {
            var sh = new Shell();
            using var git = sh.RegisterHostCommand("git", options: new HostCommandOptions
            {
                Launcher = bwrap,
                Environment = { ["GIT_AUTHOR_NAME"] = "A", ["GIT_AUTHOR_EMAIL"] = "a@x", ["GIT_COMMITTER_NAME"] = "A", ["GIT_COMMITTER_EMAIL"] = "a@x" },
            });
            Assert.Equal("1\n", await Out(sh, "git init -q -b main && echo hi > a.txt && git add a.txt && git commit -qm first && git log --oneline | wc -l"));
            var r = await sh.RunAsync($"git -c alias.x='!cat {secret}' x");
            Assert.NotEqual(0, r.ExitCode);
            Assert.DoesNotContain("secret", r.Stdout);
        }
        finally { File.Delete(secret); }
    }

    [Fact]
    public async Task BubblewrapHasNoNetworkByDefault()
    {
        if (Bwrap() is not { } bwrap) return;
        var (sh, cmd) = Sandboxed(bwrap);
        using var _ = cmd;
        // a fresh network namespace has only the loopback interface
        var ifaces = (await Out(sh, "realsh -c 'cat /proc/net/dev'")).Split('\n').Skip(2).Where(l => l.Contains(':')).Select(l => l.Split(':')[0].Trim());
        Assert.Equal(["lo"], ifaces);
    }

    [Fact]
    public void BubblewrapLauncherBindsTheMirrorAndKeepsTheCommand()
    {
        if (Bwrap(network: true) is not { } net || Bwrap() is not { } off) return;
        var argv = net(["/usr/bin/git", "status"], "/tmp/mirror");
        Assert.Contains("--share-net", argv);
        Assert.Equal(["--bind", "/tmp/mirror", "/tmp/mirror", "--", "/usr/bin/git", "status"], argv.TakeLast(6));
        Assert.DoesNotContain("--share-net", off(["/usr/bin/git"], "/tmp/mirror"));
    }
}
