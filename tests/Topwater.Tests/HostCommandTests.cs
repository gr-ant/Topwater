using Topwater;

namespace Topwater.Tests;

/// <summary>Real host programs exposed as commands (uses the real git; skipped quietly when it isn't installed).</summary>
public class HostCommandTests
{
    static readonly Dictionary<string, string> GitEnv = new()
    {
        ["GIT_AUTHOR_NAME"] = "Agent", ["GIT_AUTHOR_EMAIL"] = "agent@example.com",
        ["GIT_COMMITTER_NAME"] = "Agent", ["GIT_COMMITTER_EMAIL"] = "agent@example.com", ["GIT_CONFIG_NOSYSTEM"] = "1",
    };

    static bool HasGit => (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Any(d => File.Exists(Path.Combine(d, "git")) || File.Exists(Path.Combine(d, "git.exe")));

    static async Task<string> Out(Shell shell, string cmd)
    {
        var r = await shell.RunAsync(cmd);
        Assert.True(r.ExitCode == 0, $"`{cmd}` exited {r.ExitCode}: {r.Stderr}");
        return r.Stdout;
    }

    [Fact]
    public async Task RealGitWorksOnVirtualFiles()
    {
        if (!HasGit) return;
        var sh = new Shell();
        using var git = sh.RegisterHostCommand("git", options: new HostCommandOptions { Environment = GitEnv });

        await Out(sh, "mkdir proj && cd proj && git init -q -b main && echo hello > a.txt && git add a.txt && git commit -q -m first");
        Assert.Equal(" M a.txt\n", await Out(sh, "echo world >> a.txt && git status --short"));
        Assert.Equal("2\n", await Out(sh, "git commit -qam second && git log --oneline | wc -l"));
        // files the program creates, changes and deletes show up in the virtual filesystem (and vice versa)
        Assert.Equal("a.txt\n", await Out(sh, "git checkout -q -b feature && echo new > b.txt && mkdir d && echo x > d/c.txt && git add . && git commit -qm 'add b' && git checkout -q main && ls"));
        Assert.Equal("a.txt\nb.txt\nd\nnew\n", await Out(sh, "git merge -q feature && ls && cat b.txt"));
        Assert.Equal(" D d/c.txt\n", await Out(sh, "rm -r d && git status --short"));
        Assert.Equal("Git repository\n", await Out(sh, "test -d .git && file .git/index | grep -o 'Git index' >/dev/null; echo 'Git repository'"));
        Assert.True(sh.Fs.ReadAllBytes("/home/agent/proj/.git/index").Length > 0);  // binary files round-trip

        // path arguments can't reach the host's own files, and output shows virtual paths
        var r = await sh.RunAsync("git diff --no-index /etc/hostname a.txt");
        Assert.Contains("can only use files under /home/agent and /tmp", r.Stderr);
        r = await sh.RunAsync("git -C ../../../../../etc status");
        Assert.NotEqual(0, r.ExitCode);
        Assert.DoesNotContain(git.Root, r.Stdout + r.Stderr);
        Assert.Equal("/home/agent/proj\n", await Out(sh, "git rev-parse --show-toplevel"));
    }

    [Fact]
    public void MissingProgramFailsAtRegistration() =>
        Assert.Throws<FileNotFoundException>(() => new Shell().RegisterHostCommand("definitely-not-a-real-program-xyz"));

    [Fact]
    public async Task TimeoutKillsTheProgram()
    {
        if (!File.Exists("/bin/sleep") && !File.Exists("/usr/bin/sleep")) return;
        var sh = new Shell();
        using var slow = sh.RegisterHostCommand("realsleep", "sleep", new HostCommandOptions { Timeout = TimeSpan.FromMilliseconds(300) });
        var r = await sh.RunAsync("realsleep 30");
        Assert.Equal(124, r.ExitCode);
        Assert.Contains("timed out", r.Stderr);
    }

    /// <summary>Virtual symlinks never lead a host program anywhere: a loop or a link to / is not followed, a link to a file
    /// outside the workspace is left out, one to a file inside goes as a copy; no real link is made, and nothing hangs.</summary>
    [Fact]
    public async Task VirtualLinksNeverLeadOutOfTheMirror()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh") && !File.Exists("/usr/bin/sh")) return;
        var sh = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(20) });
        using var real = sh.RegisterHostCommand("realsh", "sh");
        await Out(sh, "mkdir -p proj/d && cd proj && echo in > d/f && echo secret > /tmp/outside && ln -s .. d/up && ln -s / root && ln -s /tmp/outside out && ln -s d/f inside && ln -s d dl");
        var r = await sh.RunAsync("cd ~/proj && realsh -c 'find . | sort; cat inside; find . -type l | wc -l'");
        Assert.True(r.Stderr == "", r.Stderr);
        Assert.Equal(".\n./d\n./d/f\n./inside\nin\n0\n", r.Stdout);
        // the program's run leaves the agent's links as they were
        Assert.Equal("..\n/\n/tmp/outside\nd/f\nd\n", await Out(sh, "cd ~/proj && readlink d/up root out inside dl"));
    }

    [Fact]
    public async Task TmpIsSharedAndKeyValuePathsAreRedirected()
    {
        if (OperatingSystem.IsWindows()) return;
        var sh = new Shell();
        using var cp = sh.RegisterHostCommand("realcp", "cp");
        using var sht = sh.RegisterHostCommand("realsh", "sh");
        await sh.RunAsync("echo scratch > /tmp/in.txt");
        Assert.Equal("scratch\n", await Out(sh, "realcp /tmp/in.txt /tmp/out.txt && cat /tmp/out.txt"));
        Assert.Equal("scratch\n", await Out(sh, "cd /tmp && realcp in.txt ~/home-copy.txt && cat ~/home-copy.txt"));
        // a path after key= is redirected too, so it can't reach the host's real /tmp
        Assert.Equal("scratch\n", await Out(sh, "echo scratch > /tmp/only-virtual-$$.txt; realsh -c 'cat \"${0#src=}\"' src=/tmp/only-virtual-$$.txt"));
    }
}
public class OfficeCliSmoke
{
    [Fact]
    public async Task CreatesADocxWhenOfficeCliIsInstalled()
    {
        var path = (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, "officecli")).FirstOrDefault(File.Exists);
        if (path is null) return; // optional: only runs on machines that have OfficeCLI
        var sh = new Shell();
        // no background process: each call writes the file before it returns, so the copy the agent sees is current
        using var office = sh.RegisterHostCommand("officecli", path, new HostCommandOptions { Environment = { ["OFFICECLI_NO_AUTO_RESIDENT"] = "1", ["OFFICECLI_SKIP_UPDATE"] = "1" } });
        var r = await sh.RunAsync("officecli create /tmp/a.docx && officecli add /tmp/a.docx /body --type paragraph --prop text='Hello from Topwater' && cp /tmp/a.docx ~/a.docx && to-markdown ~/a.docx");
        Assert.True(r.ExitCode == 0, r.Stderr + r.Stdout);
        Assert.Contains("Hello from Topwater", r.Stdout);
        Assert.DoesNotContain("topwater-officecli", r.Stdout + r.Stderr);
    }
}
