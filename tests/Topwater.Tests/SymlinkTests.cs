using Topwater;

namespace Topwater.Tests;

/// <summary>Symbolic and hard links where they could hurt: loops never hang or overflow anything, a link grants no
/// access its target doesn't, and moving or copying through links can't cut a directory off the tree.</summary>
public class SymlinkTests
{
    static async Task<Shell> Looped()
    {
        var sh = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(20) });
        // a link back to its own directory, one to the root, one to itself and a two-link cycle
        await sh.RunAsync("mkdir -p /tmp/w/d/s && cd /tmp/w && echo hi > d/f && ln -s .. d/s/up && ln -s / d/root && ln -s self d/self && ln -s b d/a && ln -s a d/b");
        return sh;
    }

    [Theory]
    [InlineData("find -L d > /dev/null")]
    [InlineData("find d -follow > /dev/null")]
    [InlineData("cp -rL d c")]
    [InlineData("cp -r d c && find c -type l | sort")]
    [InlineData("du -a d")]
    [InlineData("ls -RL d > /dev/null")]
    [InlineData("ls -R d")]
    [InlineData("tar cf t.tar d && tar tf t.tar")]
    [InlineData("zip -qr z.zip d")]
    [InlineData("tree d")]
    [InlineData("shopt -s globstar; echo d/**")]
    [InlineData("rm -r d")]
    [InlineData("cat d/a d/self")]
    [InlineData("realpath d/s/up/s/up/s/up/a")]
    [InlineData("cd d/self")]
    public async Task LoopsEndQuickly(string cmd)
    {
        var sh = await Looped();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = await sh.RunAsync("cd /tmp/w; " + cmd);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{cmd} took {watch.Elapsed}");
        Assert.DoesNotContain("timed out", r.Stderr);
        var snap = sh.Snapshot(); // and the machine still saves and loads
        var ms = new MemoryStream();
        snap.Save(ms);
        ms.Position = 0;
        Shell.Load(ms);
    }

    /// <summary>Two links per level to the level below: no loop, but 2^30 paths. Commands that follow links stop at the
    /// call's time limit (or a size limit) instead of running on; a regression fails at the test's own deadline.</summary>
    [Theory]
    [InlineData("find -L out | wc -l")]
    [InlineData("cp -rL out o2")]
    [InlineData("grep -R x out | wc -l")]
    [InlineData("zip -qr z.zip out")]
    [InlineData("chmod -R u+w out")]
    [InlineData("ls -RL out | wc -l")]
    public async Task LinkFanOutStopsAtTheTimeLimit(string cmd)
    {
        var sh = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(2) });
        await sh.RunAsync("mkdir -p /tmp/w/out/d0 && cd /tmp/w && echo x > out/d0/f && for i in $(seq 1 30); do mkdir out/d$i; ln -s ../d$((i-1)) out/d$i/a; ln -s ../d$((i-1)) out/d$i/b; done");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(() => sh.RunAsync("cd /tmp/w; " + cmd)).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{cmd} took {watch.Elapsed}"); // generous: the limit is 2 s, but a loaded machine runs slow
    }

    /// <summary>cp -rL meets a directory it is already copying (through a link higher up, then down a plain child): it
    /// reports a cyclic link there, in either creation order, instead of copying without end.</summary>
    [Theory]
    [InlineData("mkdir -p w/a; cd w; ln -s /tmp/w/w a/abs; mkdir a/b")]
    [InlineData("mkdir -p w/a/b; cd w; ln -s /tmp/w/w a/abs")]
    public async Task CopyingThroughLinksToAParentEnds(string setup)
    {
        var sh = new Shell(new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(5) });
        var r = await Task.Run(() => sh.RunAsync($"mkdir -p /tmp/w && cd /tmp/w && {setup}; echo x > a/b/f; ln -s .. a/b/up; ln -s . a/self; ln -s a l; ln -s l l2; cp -rL a zz; echo rc=$?; find zz | wc -l"))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("rc=1\n4\n", r.Stdout); // zz, zz/abs, zz/b, zz/b/f
        Assert.Equal(5, r.Stderr.Split('\n').Count(l => l.Contains("cannot copy cyclic symbolic link")));
    }

    [Fact]
    public async Task ALinkGrantsNothingItsTargetDoesNot()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/root/secret", "s\n");
        sh.Fs.WriteAllText("/etc/app.conf", "conf\n");
        var r = await sh.RunAsync("cd /tmp; ln -s /root/secret s; cat s; ln -s /etc/app.conf c; echo x >> c; ln /etc/app.conf h; ln -s /etc e; touch e/new; cat c");
        Assert.Equal("conf\n", r.Stdout);
        foreach (var message in new[] { "cat: s: Permission denied", "c: Permission denied", "ln: failed to create hard link 'h' => '/etc/app.conf': Operation not permitted", "touch: cannot touch 'e/new': Permission denied" })
            Assert.Contains(message, r.Stderr);
        Assert.Equal("conf\n", sh.Fs.ReadAllText("/etc/app.conf"));
        Assert.False(sh.Fs.Exists("/etc/new"));
    }

    [Fact]
    public async Task NoDirectoryMovesIntoItselfThroughALink()
    {
        var sh = new Shell();
        var r = await sh.RunAsync("cd /tmp && mkdir -p d/sub && ln -s d/sub in && mv d in/; mv d in/x; cp -r d in/; ls -R d | head -5");
        Assert.Equal("d:\nsub\n\nd/sub:\n", r.Stdout);
        Assert.Contains("mv: cannot move 'd' to a subdirectory of itself, 'in/d'", r.Stderr);
        Assert.Contains("cp: cannot copy a directory, 'd', into itself, 'in/d'", r.Stderr);
    }

    [Fact]
    public async Task HardLinksShareOneFileAndItsBytes()
    {
        var sh = new Shell(new ShellOptions { MaxFileSystemBytes = 64 << 20 });
        await sh.RunAsync("cd /tmp && printf '%10000s' '' > big");
        var used = sh.Fs.UsedBytes;
        var r = await sh.RunAsync("cd /tmp && for i in 1 2 3; do ln big h$i; done; stat -c %h big; rm big h1 h2; stat -c '%h %s' h3");
        Assert.Equal("4\n1 10000\n", r.Stdout);
        Assert.Equal(used, sh.Fs.UsedBytes);
        await sh.RunAsync("rm /tmp/h3");
        Assert.Equal(used - 10000, sh.Fs.UsedBytes);
    }
}
