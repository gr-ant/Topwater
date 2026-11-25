using Topwater;

namespace Topwater.Tests;

/// <summary>Permission holes from the filesystem review; each expectation is what real Linux and coreutils do for a non-root user.</summary>
public class PermissionTests
{
    static int Oct(string s) => Convert.ToInt32(s, 8);

    /// <summary>The app puts a readable file in root's private home and a root-owned tree with one secret in /srv.</summary>
    static Shell Setup()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/root/notes.txt", "private\n");
        sh.Fs.CreateDirectory("/srv/app/sub", parents: true);
        sh.Fs.WriteAllText("/srv/app/readme", "hello\n");
        sh.Fs.WriteAllText("/srv/app/sub/data", "data\n");
        sh.Fs.WriteAllText("/srv/app/secret", "s3cret\n");
        sh.Fs.Chmod("/srv/app/secret", Oct("600"));
        return sh;
    }

    [Theory] // hole 1: every ancestor needs search permission
    [InlineData("cat /root/notes.txt", "cat: /root/notes.txt: Permission denied\n")]
    [InlineData("cat /root/missing", "cat: /root/missing: Permission denied\n")]
    [InlineData("cat < /root/notes.txt", "bash: /root/notes.txt: Permission denied\n")]
    [InlineData("source /root/notes.txt", "bash: /root/notes.txt: Permission denied\n")]
    [InlineData("cat /root/*", "cat: '/root/*': Permission denied\n")]
    [InlineData("grep -r private /root", "grep: /root: Permission denied\n")]
    [InlineData("ls /root", "ls: cannot open directory '/root': Permission denied\n")]
    [InlineData("ls -l /root/notes.txt", "ls: cannot access '/root/notes.txt': Permission denied\n")]
    [InlineData("stat /root/notes.txt", "stat: cannot statx '/root/notes.txt': Permission denied\n")]
    [InlineData("cd /root", "bash: cd: /root: Permission denied\n")]
    [InlineData("cp /root/notes.txt ~/n", "cp: cannot stat '/root/notes.txt': Permission denied\n")]
    [InlineData("chmod 777 /root/notes.txt", "chmod: cannot access '/root/notes.txt': Permission denied\n")]
    [InlineData("to-markdown /root/notes.txt", "to-markdown: /root/notes.txt: Permission denied\n")]
    [InlineData("echo x > /root/notes.txt", "bash: /root/notes.txt: Permission denied\n")]
    [InlineData("rm /root/notes.txt", "rm: cannot remove '/root/notes.txt': Permission denied\n")]
    public async Task UnsearchableDirectoryHidesItsEntries(string cmd, string err)
    {
        var sh = Setup();
        var r = await sh.RunAsync(cmd);
        // Topwater prints built-in errors without bash's "bash: line 1: " prefix
        Assert.True(r.ExitCode != 0 && r.Stdout == "" && r.Stderr.Replace("bash: ", "") == err.Replace("bash: ", ""), $"{cmd}: [{r.ExitCode}] {r.Stdout}|{r.Stderr}");
        Assert.Equal("private\n", sh.Fs.ReadAllText("/root/notes.txt")); // the app still reads it
    }

    [Fact]
    public async Task TestsInsideUnsearchableDirectoryAreFalse()
    {
        var sh = Setup();
        Assert.Equal("1 1 1 1 1\n", (await sh.RunAsync("[ -e /root/notes.txt ]; a=$?; [ -r /root/notes.txt ]; b=$?; [[ -f /root/notes.txt ]]; c=$?; test -w /root/notes.txt; d=$?; [ -s /root/notes.txt ]; echo $a $b $c $d $?")).Stdout);
        Assert.Equal("ok\n", (await sh.RunAsync("[ -d /root ] && [ ! -r /root ] && echo ok")).Stdout);
    }

    [Fact] // a directory that is readable but not searchable lists names, but nothing behind them
    public async Task ReadOnlyDirectoryListsButDoesNotOpen()
    {
        var sh = Setup();
        sh.Fs.Chmod("/srv/app", Oct("744"));
        Assert.Equal("readme\nsecret\nsub\n", (await sh.RunAsync("ls /srv/app")).Stdout);
        var r = await sh.RunAsync("cat /srv/app/readme");
        Assert.Equal("cat: /srv/app/readme: Permission denied\n", r.Stderr);
    }

    [Fact] // hole 2
    public async Task CopyRecursiveSkipsUnreadableChildren()
    {
        var sh = Setup();
        var r = await sh.RunAsync("cp -r /srv/app ~/x");
        Assert.Equal((1, "cp: cannot open '/srv/app/secret' for reading: Permission denied\n"), (r.ExitCode, r.Stderr));
        Assert.Equal("readme\nsub\nhello\ndata\n", (await sh.RunAsync("ls ~/x; cat ~/x/readme ~/x/sub/data")).Stdout);
        r = await sh.RunAsync("cd /srv && cp -r app/ ~/y; cat ~/y/secret");
        Assert.Equal("cp: cannot open 'app/secret' for reading: Permission denied\ncat: /home/agent/y/secret: No such file or directory\n", r.Stderr);
        r = await sh.RunAsync("cp /srv/app/secret ~/s");
        Assert.Equal((1, "cp: cannot open '/srv/app/secret' for reading: Permission denied\n"), (r.ExitCode, r.Stderr));
        Assert.False(sh.Fs.Exists("/home/agent/s"));
    }

    [Fact] // cp over a file the agent may write but does not own keeps the file (and its owner), like writing into it
    public async Task CopyOntoExistingFileKeepsOwner()
    {
        var sh = Setup();
        sh.Fs.WriteAllText("/tmp/shared", "old");
        sh.Fs.Chmod("/tmp/shared", Oct("666"));
        await sh.RunAsync("echo new > ~/n; cp ~/n /tmp/shared");
        Assert.Equal("new\nroot 666\n", (await sh.RunAsync("cat /tmp/shared; stat -c '%U %a' /tmp/shared")).Stdout);
    }

    [Fact] // hole 3: in a sticky directory only the entry's owner (or the directory's) may delete or rename it
    public async Task StickyTmp()
    {
        var sh = Setup();
        sh.Fs.WriteAllText("/tmp/rootfile", "x");
        sh.Fs.Chmod("/tmp/rootfile", Oct("666"));
        sh.Fs.CreateDirectory("/tmp/rootdir");
        sh.Fs.Chmod("/tmp/rootdir", Oct("777"));
        foreach (var (cmd, err) in new[]
        {
            ("rm /tmp/rootfile", "rm: cannot remove '/tmp/rootfile': Operation not permitted\n"),
            ("rm -rf /tmp/rootdir", "rm: cannot remove '/tmp/rootdir': Operation not permitted\n"),
            ("mv /tmp/rootfile ~/f", "mv: cannot move '/tmp/rootfile' to '/home/agent/f': Operation not permitted\n"),
            ("echo y > ~/f; mv ~/f /tmp/rootfile", "mv: cannot move '/home/agent/f' to '/tmp/rootfile': Operation not permitted\n"),
        })
        {
            var r = await sh.RunAsync(cmd);
            Assert.True(r.ExitCode == 1 && r.Stderr == err, $"{cmd}: [{r.ExitCode}] {r.Stderr}");
        }
        Assert.Equal("x", sh.Fs.ReadAllText("/tmp/rootfile"));
        Assert.True(sh.Fs.IsDirectory("/tmp/rootdir"));
        Assert.Equal("ok\n", (await sh.RunAsync("echo a > /tmp/mine && mv /tmp/mine /tmp/mine2 && rm /tmp/mine2 && echo b > /tmp/rootdir/f && rm /tmp/rootdir/f && echo ok")).Stdout);
    }

    [Fact] // item 4: directory write permission governs creating, removing and renaming entries
    public async Task DirectoryWritePermission()
    {
        var sh = Setup();
        await sh.RunAsync("mkdir -p ro/sub; echo b > ro/b; echo c > ro/sub/c; chmod 555 ro/sub; chmod 555 ro");
        foreach (var (cmd, err) in new[]
        {
            ("touch ro/new", "touch: cannot touch 'ro/new': Permission denied\n"),
            ("echo x > ~/ro/new", "bash: /home/agent/ro/new: Permission denied\n"),
            ("mkdir ro/d", "mkdir: cannot create directory \u2018ro/d\u2019: Permission denied\n"),
            ("rm ro/b", "rm: cannot remove 'ro/b': Permission denied\n"),
            ("rm -r ro/sub", "rm: cannot remove 'ro/sub/c': Permission denied\n"),
            ("mv ro/b x", "mv: cannot move 'ro/b' to 'x': Permission denied\n"),
            ("mv ro/sub x", "mv: cannot move 'ro/sub' to 'x': Permission denied\n"),
            ("echo y > y; mv y ro/", "mv: cannot move 'y' to 'ro/y': Permission denied\n"),
        })
        {
            var r = await sh.RunAsync(cmd);
            Assert.True(r.ExitCode != 0 && r.Stderr == err, $"{cmd}: [{r.ExitCode}] {r.Stderr}");
        }
        Assert.Equal("b\nc\n", (await sh.RunAsync("cat ro/b ro/sub/c")).Stdout);
        // a directory moved to a new parent must itself be writable (its ".." changes)
        var m = await sh.RunAsync("chmod 755 ro; mv ro/sub other");
        Assert.Equal("mv: cannot move 'ro/sub' to 'other': Permission denied\n", m.Stderr);
        Assert.Equal("ok\n", (await sh.RunAsync("chmod 755 ro/sub && mv ro/sub other && echo ok")).Stdout);
    }

    [Fact] // a file the agent can't write but whose directory it owns: `>` is denied, unlink-and-recreate is allowed
    public async Task ReplaceUnwritableFileInOwnDirectory()
    {
        var sh = Setup();
        sh.Fs.WriteAllText("/home/agent/cfg", "app");
        sh.Fs.SetOwner("/home/agent/cfg", "root");
        var r = await sh.RunAsync("echo agent > ~/cfg");
        Assert.Equal("bash: /home/agent/cfg: Permission denied\n", r.Stderr.Replace("bash: line 1: ", "bash: "));
        Assert.Equal("agent root\nagent agent\n", (await sh.RunAsync("stat -c '%U' ~ | tr '\\n' ' '; stat -c %U ~/cfg; rm ~/cfg && echo agent > ~/cfg && cat ~/cfg | tr '\\n' ' '; stat -c %U ~/cfg")).Stdout);
    }

    [Fact] // chmod/chown on files the agent doesn't own
    public async Task ChmodChownNeedOwnership()
    {
        var sh = Setup();
        var r = await sh.RunAsync("chmod 666 /srv/app/secret; chown agent /srv/app/secret");
        Assert.Equal("chmod: changing permissions of '/srv/app/secret': Operation not permitted\nchown: changing ownership of '/srv/app/secret': Operation not permitted\n", r.Stderr);
        Assert.Equal(Oct("600"), sh.Fs.Stat("/srv/app/secret").Mode);
    }

    [Fact] // hole 5: while the agent's command awaits, the app's own calls are not restricted
    public async Task AppIsUnrestrictedWhileAgentCommandAwaits()
    {
        var sh = Setup();
        var run = sh.RunAsync("sleep 0.3");
        sh.Fs.WriteAllText("/etc/app.conf", "x");                    // was: Permission denied, because RunAs was "agent"
        Assert.Equal("private\n", sh.Fs.ReadAllText("/root/notes.txt"));
        using (sh.Fs.AsRoot()) { }
        await run;
        Assert.Null(sh.Fs.RunAs);
        // a command the app registered still runs as the agent, as before
        sh.Register("peek", ctx => { ctx.Stdout.WriteLine(ctx.Fs.CanRead("/root/notes.txt")); return 0; });
        Assert.Equal("False\n", (await sh.RunAsync("peek")).Stdout);
    }
}
