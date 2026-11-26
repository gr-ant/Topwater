using System.Diagnostics;
using System.Text;
using Topwater.Containers;
using Xunit.Abstractions;

namespace Topwater.Tests;

/// <summary>A test that needs a working docker; skipped (not failed) where there is none, so CI without docker stays green.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    static readonly Lazy<bool> Available = new(() => Docker.IsAvailableAsync().GetAwaiter().GetResult());
    public DockerFactAttribute() { if (!Available.Value) Skip = "docker isn't available"; }
}

/// <summary>Topwater to a real container and back, with real docker. Every image and container made here is removed.</summary>
public class ContainerDockerTests(ITestOutputHelper output)
{
    static readonly byte[] BinaryBytes = [0, 1, 2, 0x7f, 0x80, 0xfe, 0xff, (byte)'\n', 0xc3];

    static async Task<Shell> Machine()
    {
        var sh = new Shell();
        sh.Fs.WriteAllBytes("/home/agent/blob.bin", BinaryBytes);
        sh.Fs.CreateDirectory("/data", parents: true);
        sh.Fs.WriteAllText("/data/report.txt", "héllo wörld\n");
        sh.Fs.SetOwner("/data", "bob", recursive: true);
        var r = await sh.RunAsync("""
            mkdir -p proj/src && cd proj
            printf 'hello\n' > a.txt; chmod 600 a.txt; printf '#!/bin/sh\necho ran\n' > src/run.sh; chmod 755 src/run.sh
            touch 'sp ace'; ln -s a.txt alink; ln a.txt ahard
            export EXP='exported $x "q"'; plain='multi
            line'; declare -i n=5; arr=(one 'two words' three); unset 'arr[1]'; declare -A map=([k1]=v1 ['key two']=v2)
            greet() { echo "hi $1 from $(pwd)"; }; export -f greet
            alias ll='ls -l'
            shopt -s nullglob
            rm ~/.profile
            echo done
            """);
        Assert.Equal(0, r.ExitCode);
        return sh;
    }

    static Task<string> Docker(params string[] args) => DockerWithInput(null, args);

    static async Task<string> DockerWithInput(string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (stdin != null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, $"docker {string.Join(' ', args)}: exit {p.ExitCode}: {await stderr}");
        return await stdout;
    }

    static async Task Quietly(params string[] args)
    {
        try { await Docker(args); } catch (Exception) { }
    }

    static string Tag() => "topwater-test-" + Guid.NewGuid().ToString("N")[..12];

    [DockerFact]
    public async Task ASnapshotRunsInARealContainer()
    {
        var tag = Tag();
        try
        {
            var context = await (await Machine()).Snapshot().BuildImageAsync(tag);
            Assert.Contains(context.Report.NotCarried, i => i.What.StartsWith("hostname"));
            Assert.Contains("/home/agent/proj/a.txt", context.Report.Carried);

            var files = await Docker("run", "--rm", tag, "bash", "-c", """
                id -un; pwd; echo "EXP=$EXP"
                stat -c '%n %a %U %G' /home/agent/proj/a.txt /home/agent/proj/src/run.sh /home/agent/proj /data /data/report.txt /home/agent
                cat a.txt; src/run.sh; od -An -tx1 /home/agent/blob.bin; cat /data/report.txt; ls 'sp ace'
                test -e ~/.profile && echo profile-present || echo profile-gone
                test -x /bin/ls && test -L /bin && echo real-bin
                readlink alink; cat alink; stat -c %h a.txt
                """);
            Assert.Equal("""
                agent
                /home/agent/proj
                EXP=exported $x "q"
                /home/agent/proj/a.txt 600 agent agent
                /home/agent/proj/src/run.sh 755 agent agent
                /home/agent/proj 755 agent agent
                /data 755 bob bob
                /data/report.txt 644 bob bob
                /home/agent 750 agent agent
                hello
                ran
                 00 01 02 7f 80 fe ff 0a c3
                héllo wörld
                sp ace
                profile-gone
                real-bin
                a.txt
                hello
                2

                """, files);

            // the session comes back in an interactive bash (commands typed on stdin, as at a terminal): functions, aliases, variables, options, history
            var session = await DockerWithInput("""
                greet you; alias ll; declare -p plain n arr map; shopt nullglob; bash -c 'greet child'
                history | grep -c 'ali[a]s ll='

                """, "run", "-i", "--rm", tag, "bash", "-i");
            Assert.Equal("""
                hi you from /home/agent/proj
                alias ll='ls -l'
                declare -- plain=$'multi\nline'
                declare -i n="5"
                declare -a arr=([0]="one" [2]="three")
                declare -A map=([k1]="v1" ["key two"]="v2" )
                nullglob       	on
                hi child from /home/agent/proj
                1

                """, session);
        }
        finally { await Quietly("rmi", "-f", tag); }
    }

    [DockerFact]
    public async Task ChangesInARealContainerComeBack()
    {
        var tag = Tag();
        var name = tag + "-c";
        try
        {
            await (await Machine()).Snapshot().BuildImageAsync(tag);
            // real programs in the real container: python3 if the image has it, else perl (always in ubuntu)
            await Docker("run", "--name", name, tag, "bash", "-c", """
                echo changed >> a.txt; echo new > new.txt
                if command -v python3 >/dev/null; then python3 -c 'open("calc.txt","w").write(str(6*7)+"\n")'; else perl -e 'open F, ">calc.txt"; print F 6*7, "\n"'; fi
                ln -s a.txt link.txt; ln -s /etc/shadow secret; rm src/run.sh
                mkdir -p /tmp/work && seq 1 5 > /tmp/work/nums
                """);
            var result = await ContainerImport.FromContainerAsync(name, new ContainerImportOptions { Paths = ["/var/log"] });
            Assert.DoesNotContain(result.Report.NotCarried, i => i.What == "/home/agent/proj/secret"); // links come back as links

            var sh = new Shell(new ShellOptions { User = result.Snapshot.User });
            sh.Restore(result.Snapshot);
            var r = await sh.RunAsync("""
                whoami; pwd; echo "EXP=$EXP"
                cat a.txt new.txt calc.txt link.txt; ls src | wc -l; readlink secret; cat secret 2>&1
                sort -rn /tmp/work/nums | head -2; test -s /var/log/dpkg.log && echo dpkg.log came along
                greet back; ll -d . | cut -c1-10; wc -c < /home/agent/blob.bin
                """);
            Assert.Equal("""
                agent
                /home/agent/proj
                EXP=exported $x "q"
                hello
                changed
                new
                42
                hello
                changed
                0
                /etc/shadow
                cat: secret: No such file or directory
                5
                4
                dpkg.log came along
                hi back from /home/agent/proj
                drwxr-xr-x
                9

                """, r.Stdout);
            Assert.Equal(BinaryBytes, sh.Fs.ReadAllBytes("/home/agent/blob.bin"));
        }
        finally
        {
            await Quietly("rm", "-f", name);
            await Quietly("rmi", "-f", tag);
        }
    }

    [DockerFact]
    public async Task TopwaterToContainerToTopwaterComparesEqual()
    {
        var tag = Tag();
        var original = await Machine();
        var snapshot = original.Snapshot();
        try
        {
            var time = Stopwatch.StartNew();
            var context = await snapshot.BuildImageAsync(tag);
            var built = time.Elapsed;
            var result = await ContainerImport.FromImageAsync(tag);
            var size = long.Parse((await Docker("image", "inspect", "-f", "{{.Size}}", tag)).Trim());
            // diagnostics only: a BuildKit build pulls the base image without keeping a local copy, so it may not be there
            long? baseSize = null;
            try { baseSize = long.Parse((await Docker("image", "inspect", "-f", "{{.Size}}", ContainerExport.DefaultBaseImage)).Trim()); }
            catch (Exception) { }
            var over = baseSize is { } b ? $"{(size - b) / 1e3:F0} KB over {ContainerExport.DefaultBaseImage}" : $"{ContainerExport.DefaultBaseImage} not stored locally";
            output.WriteLine($"build {built.TotalSeconds:F1}s, import {(time.Elapsed - built).TotalSeconds:F1}s, image {size / 1e6:F1} MB ({over}), layer {context.Layer.Length / 1e3:F0} KB");

            var back = new Shell(new ShellOptions { User = result.Snapshot.User });
            back.Restore(result.Snapshot);
            original.Restore(snapshot);

            // every file the export carried, byte for byte, with mode, owner and mtime (images keep whole seconds)
            Assert.Equal(Files(original, context.Report.Carried), Files(back, context.Report.Carried));
            Assert.False(back.Fs.Exists("/home/agent/.profile"));
            Assert.False(back.Fs.Exists("/etc/topwater"));
            Assert.Equal(original.Fs.ReadAllText("/home/agent/.bashrc"), back.Fs.ReadAllText("/home/agent/.bashrc"));
            Assert.Equal(original.History, back.History);
            Assert.Equal(original.Cwd, back.Cwd);
            Assert.Equal(original.Options.User, back.Options.User);

            const string probe = """
                echo "EXP=$EXP"; declare -p plain n arr map; declare -f greet; alias; shopt nullglob; bash -c 'greet child'
                """;
            Assert.Equal((await original.RunAsync(probe)).ToToolOutput(), (await back.RunAsync(probe)).ToToolOutput());
        }
        finally { await Quietly("rmi", "-f", tag); }
    }

    static List<string> Files(Shell sh, IEnumerable<string> paths) =>
        paths.Select(p =>
        {
            var st = sh.Fs.Stat(p);
            var t = (st.Modified.Kind == DateTimeKind.Local ? st.Modified.ToUniversalTime() : st.Modified).Ticks / TimeSpan.TicksPerSecond;
            var content = st.IsDirectory ? "" : Convert.ToHexString(sh.Fs.ReadAllBytes(p));
            return $"{p} {st.IsDirectory} {Convert.ToString(st.Mode, 8)} {st.Owner} {t} {content}";
        }).ToList();
}
