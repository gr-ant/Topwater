using System.Net;
using System.Security.Cryptography;
using System.Text;
using Topwater.Python;

namespace Topwater.Tests;

/// <summary>python3 (Topwater.Python: PythonCommand.cs over Wasi.cs): the command in the shell, its limits, and hostile programs.
/// The Python port runs the same checks (python/tests/test_python.py).</summary>
public class PythonTests
{
    static Shell PyShell(ShellOptions? options = null) => new Shell(options).AllowPython();

    static Task<ShellResult> Run(string script, ShellOptions? options = null) => PyShell(options).RunAsync(script);

    // ---------- the command ----------

    [Fact]
    public async Task OptInOnly()
    {
        Assert.False(new Shell().Commands.ContainsKey("python3"));
        var r = await new Shell().RunAsync("python3 -c 'print(1)'");
        Assert.Equal(127, r.ExitCode);
        Assert.Contains("command not found", r.Stderr);
    }

    [Fact]
    public async Task ProgramArgvStdinAndExitCodes()
    {
        var r = await Run("python3 -c 'import sys; print(sys.argv[1:], sys.stdin.read().upper())' a 'b c' <<< hi");
        Assert.Equal("['a', 'b c'] HI\n\n", r.Stdout);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(7, (await Run("python3 -c 'raise SystemExit(7)'")).ExitCode);
        r = await Run("python3 -c 'import sys; sys.exit(\"bye\")'");
        Assert.Equal((1, "bye\n"), (r.ExitCode, r.Stderr));
        r = await Run("python3 -c '1/0'");
        Assert.Equal(1, r.ExitCode);
        Assert.EndsWith("ZeroDivisionError: division by zero\n", r.Stderr);
    }

    [Fact]
    public async Task FilesAreTheMachinesFiles()
    {
        var sh = PyShell();
        await sh.RunAsync("mkdir -p /tmp/w && cd /tmp/w && printf 'a\\nb\\n' > in.txt");
        Assert.Equal("A\nB\n", (await sh.RunAsync("python3 -c 'open(\"out.txt\", \"w\").write(open(\"in.txt\").read().upper())' && cat out.txt")).Stdout);
        // bytes survive both ways
        await sh.RunAsync("printf '\\377\\000\\001' > bin");
        Assert.Equal("[255, 0, 1]\n", (await sh.RunAsync("python3 -c 'print(list(open(\"bin\", \"rb\").read()))'")).Stdout);
        await sh.RunAsync("python3 -c 'open(\"bin2\", \"wb\").write(bytes([0, 200, 255]))'");
        Assert.Equal(new byte[] { 0, 200, 255 }, sh.Fs.ReadAllBytes("/tmp/w/bin2"));
        // a file left open is saved at exit, as a real process's is
        Assert.Equal("kept", (await sh.RunAsync("python3 -c 'f = open(\"u\", \"w\"); f.write(\"kept\")'; cat u")).Stdout);
        // owner and mode come from the agent's user and umask, as for any command
        Assert.Equal("-rw-r--r-- agent\n", (await sh.RunAsync("ls -l out.txt | cut -d' ' -f1,3")).Stdout);
    }

    [Fact]
    public async Task ScriptsModulesShebangAndSubstitution()
    {
        var sh = PyShell();
        await sh.RunAsync("mkdir -p /tmp/w && cd /tmp/w");
        await sh.RunAsync("printf 'import sys\\nprint(\"script\", sys.argv)\\n' > s.py");
        Assert.Equal("script ['s.py', 'x']\n", (await sh.RunAsync("python3 s.py x")).Stdout);
        await sh.RunAsync("printf '#!/usr/bin/env python3\\nprint(\"shebang\")\\n' > t.py && chmod +x t.py");
        Assert.Equal("shebang\n", (await sh.RunAsync("./t.py")).Stdout);
        await sh.RunAsync("printf 'print(\"module\", __name__)\\n' > m.py");
        Assert.Equal("module __main__\n", (await sh.RunAsync("python3 -m m")).Stdout);
        Assert.Equal("1024\n", (await sh.RunAsync("echo 'print(2**10)' | python3")).Stdout);
        Assert.Equal("[in]\n", (await sh.RunAsync("x=$(python3 -c 'print(\"in\")'); echo \"[$x]\"")).Stdout);
        Assert.Equal("2\n", (await sh.RunAsync("python3 -c 'print(\"a\\nb\")' | wc -l")).Stdout);
        var r = await sh.RunAsync("python3 nope.py");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("can't open file", r.Stderr);
    }

    [Fact]
    public async Task EnvironmentIsTheExportedVariables()
    {
        var r = await Run("export A=1; B=2; C=3 python3 -c 'import os; print(os.environ.get(\"A\"), os.environ.get(\"B\"), os.environ.get(\"C\"), os.getcwd())'");
        Assert.Equal("1 None 3 /home/agent\n", r.Stdout);
    }

    [Fact]
    public async Task NoStateBetweenCalls()
    {
        var sh = PyShell();
        await sh.RunAsync("python3 -c 'import builtins, sys; builtins.LEFT = 1; sys.path.append(\"/x\")'");
        Assert.Equal("False False\n", (await sh.RunAsync("python3 -c 'import builtins, sys; print(hasattr(builtins, \"LEFT\"), \"/x\" in sys.path)'")).Stdout);
    }

    [Fact]
    public async Task ForkAndSnapshotKeepWorking()
    {
        var sh = PyShell();
        await sh.RunAsync("echo 'print(5)' > /tmp/p.py");
        Assert.Equal("5\n", (await sh.Fork().RunAsync("python3 /tmp/p.py")).Stdout);
        var snap = sh.Snapshot();
        await sh.RunAsync("rm /tmp/p.py");
        sh.Restore(snap);
        Assert.Equal("5\n", (await sh.RunAsync("python3 /tmp/p.py")).Stdout);
    }

    [Fact]
    public async Task RecordingReplaysWithPythonEnabled()
    {
        var sh = PyShell();
        var rec = sh.StartRecording();
        await sh.RunAsync("python3 -c 'open(\"/tmp/r\", \"w\").write(\"rec\")'");
        rec.Stop();
        var again = await rec.Recording.ShellAtAsync(0, s => s.AllowPython()); // runs call 0 again
        Assert.Equal("rec\n", (await again.RunAsync("python3 -c 'print(open(\"/tmp/r\").read())'")).Stdout);
    }

    [Fact]
    public void UnavailableAddOnFailsInHostCode()
    {
        var dir = Directory.CreateTempSubdirectory("topwater-python").FullName;
        try
        {
            Assert.Throws<PythonUnavailableException>(() => new Shell().AllowPython(new PythonOptions { Artifacts = dir }));
            File.WriteAllText(Path.Combine(dir, "python.wasm.gz"), "not the interpreter");
            File.WriteAllText(Path.Combine(dir, "python314.zip"), "not the stdlib");
            var e = Assert.Throws<PythonUnavailableException>(() => new Shell().AllowPython(new PythonOptions { Artifacts = dir }));
            Assert.Contains("sha256", e.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CompileCacheDir()
    {
        var dir = Directory.CreateTempSubdirectory("topwater-python-cache").FullName;
        try
        {
            Wasi.GetRuntime(null, dir);
            Assert.Single(Directory.GetFiles(dir, "*.cwasm"));
            Wasi.ForgetRuntimes();
            var sh = new Shell().AllowPython(new PythonOptions { CacheDir = dir }); // deserialized, not compiled
            Assert.Equal("3\n", (await sh.RunAsync("python3 -c 'print(3)'")).Stdout);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The add-on's gzipped module is the vendored xz's module (tools/python_wasm_gz.py writes it; --check in CI
    /// compares the bytes): the hash it is checked against at load is the design note's, and the xz is still MANIFEST's.</summary>
    [Fact]
    public void TheCarriedInterpreterIsTheVendoredOne()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "assets", "python-wasm", "MANIFEST"))) root = root.Parent!;
        var assets = Path.Combine(root.FullName, "assets", "python-wasm");
        var manifest = File.ReadAllText(Path.Combine(assets, "MANIFEST"));
        static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        Assert.Contains($"{Sha(File.ReadAllBytes(Path.Combine(assets, "python.wasm.xz")))}  python.wasm.xz", manifest);
        Assert.Contains($"{Wasi.StdlibSha256}  python314.zip", manifest);
        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(assets, "python.wasm.gz")), System.IO.Compression.CompressionMode.Decompress);
        using var wasm = new MemoryStream();
        gz.CopyTo(wasm);
        Assert.Equal(Wasi.ModuleSha256, Sha(wasm.ToArray()));
    }

    // ---------- mounts ----------

    [Fact]
    public async Task ReadOnlyMountRefusesWrites()
    {
        var sh = PyShell();
        var src = new MemorySource(new Dictionary<string, object?> { ["a.txt"] = "source\n" });
        await sh.MountAsync("/mnt/ro", src);
        Assert.Equal("source\n", (await sh.RunAsync("python3 -c 'print(open(\"/mnt/ro/a.txt\").read(), end=\"\")'")).Stdout);
        var r = await sh.RunAsync("python3 -c 'open(\"/mnt/ro/a.txt\", \"w\").write(\"x\")'");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("PermissionError", r.Stderr);
        Assert.Contains("PermissionError", (await sh.RunAsync("python3 -c 'open(\"/mnt/ro/new.txt\", \"w\")'")).Stderr);
        Assert.All(src.Calls, c => Assert.Contains(c.Op, new[] { "list", "stat", "read" }));
    }

    [Fact]
    public async Task StagedMountWritesBecomeChanges()
    {
        var sh = PyShell();
        var src = new MemorySource(new Dictionary<string, object?> { ["a.txt"] = "old\n" });
        await sh.MountAsync("/mnt/st", src, MountMode.Staged);
        await sh.RunAsync("python3 -c 'open(\"/mnt/st/a.txt\", \"a\").write(\"more\\n\"); open(\"/mnt/st/b.txt\", \"w\").write(\"b\\n\")'");
        var changes = sh.PendingChanges("/mnt/st").Select(c => (c.Kind, c.Path, Encoding.UTF8.GetString(c.Content ?? []))).OrderBy(c => c.Path).ToList();
        Assert.Equal([(MountChangeKind.Write, "/a.txt", "old\nmore\n"), (MountChangeKind.Write, "/b.txt", "b\n")], changes);
        Assert.DoesNotContain(src.Calls, c => c.Op == "write"); // nothing reached the source
    }

    [Fact]
    public async Task UnfetchedMountedFilesAreFetchedOnDemand()
    {
        var sh = PyShell();
        var src = new MemorySource(new Dictionary<string, object?> { ["d/a.txt"] = "fetched\n" });
        await sh.MountAsync("/mnt/m", src);
        // the program finds the file itself: the command line names no mounted path, so nothing is fetched before it starts
        var r = await sh.RunAsync("python3 -c 'import os; d = \"/mnt/\" + \"m/d\"; print(os.listdir(d), open(d + \"/a.txt\").read(), end=\"\")'");
        Assert.True(r.Stdout == "['a.txt'] fetched\n", r.Stderr);
        Assert.Contains(src.Calls, c => c.Op == "read");
    }

    [Fact]
    public async Task ShellsRunPythonAtTheSameTime()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Run($"python3 -c 'print({i} * {i})'")));
        Assert.Equal(["0\n", "1\n", "4\n", "9\n"], results.Select(r => r.Stdout));
    }

    // ---------- the web ----------

    sealed class Transport : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/redirect")) return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://api.test/data") }, Content = new ByteArrayContent([]) };
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"url\": \"{url}\", \"body\": \"{body}\"}}", Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task HttpGoesThroughTheWebPolicy()
    {
        var r = await Run("python3 -c 'import urllib.request; urllib.request.urlopen(\"https://example.com\")'");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("Could not resolve host: example.com", r.Stderr);
        var sh = PyShell().AllowWeb(new WebPolicy { Domains = ["api.test"], Methods = ["GET", "POST"], Handler = new Transport() });
        r = await sh.RunAsync("python3 -c 'import urllib.request, json; print(json.load(urllib.request.urlopen(\"https://api.test/redirect\"))[\"url\"])'");
        Assert.True(r.Stdout == "https://api.test/data\n", r.Stderr);
        r = await sh.RunAsync("python3 -c 'import requests; print(requests.post(\"https://api.test/p\", data=b\"hi\").json()[\"body\"])'");
        Assert.True(r.Stdout == "hi\n", r.Stderr);
        Assert.Contains("other.test is not an allowed host", (await sh.RunAsync("python3 -c 'import urllib.request; urllib.request.urlopen(\"https://other.test/\")'")).Stderr);
        Assert.Contains("only https:// URLs are allowed", (await sh.RunAsync("python3 -c 'import urllib.request; urllib.request.urlopen(\"http://api.test/\")'")).Stderr);
    }

    [Fact]
    public async Task HttpRequestsCountAgainstThePolicy()
    {
        var sh = PyShell().AllowWeb(new WebPolicy { Domains = ["api.test"], MaxRequestsPerCall = 2, Handler = new Transport() });
        var r = await sh.RunAsync("python3 -c '\nimport urllib.request\nfor _ in range(3): urllib.request.urlopen(\"https://api.test/x\")'");
        Assert.Contains("the limit of 2 requests in one call was reached", r.Stderr);
    }

    [Fact]
    public void HttpBridgeRejectsBadRecords()
    {
        static byte[] Record(int? count, params string[] fields)
        {
            var output = new MemoryStream();
            void Field(byte[] f) { output.Write(BitConverter.GetBytes((uint)f.Length)); output.Write(f); }
            foreach (var f in fields[..2]) Field(Encoding.UTF8.GetBytes(f));
            output.Write(BitConverter.GetBytes((uint)(count ?? 0)));
            foreach (var f in fields[2..]) Field(Encoding.UTF8.GetBytes(f));
            return output.ToArray();
        }
        var ok = Wasi.DecodeRequest(Record(null, "get", "https://a/", ""));
        Assert.Equal(("GET", "https://a/", 0, 0), (ok.Method, ok.Url, ok.Headers.Count, ok.Body.Length));
        foreach (var (bad, why) in new (byte[], string)[]
        {
            (Record(null, "TRACE", "https://a/", ""), "unsupported"), (Record(null, "GET", "https://a/ x", ""), "spaces"),
            (Record(1000, "GET", "https://a/", ""), "too many"), ([.. Record(null, "GET", "https://a/", ""), (byte)'x'], "trailing"),
            (Record(1, "GET", "https://a/", "Bad Name", "v", ""), "header name"), (Record(1, "GET", "https://a/", "X", "a\r\nb", ""), "header value"),
            ([0xff, 0xff, 0xff, 0xff], "too long"),
        })
            Assert.Contains(why, Assert.Throws<FormatException>(() => Wasi.DecodeRequest(bad)).Message);
    }

    // ---------- hostile programs ----------

    [Fact]
    public async Task BusyLoopAndSleepEndAtTheTimeLimit()
    {
        Assert.Equal("124\n", (await Run("timeout 1 python3 -c 'while True: pass'; echo $?")).Stdout);
        Assert.Equal("124\n", (await Run("timeout 1 python3 -c 'import time; time.sleep(60)'; echo $?")).Stdout);
        var r = await Run("python3 -c 'while True: pass'", new ShellOptions { MaxCallDuration = TimeSpan.FromSeconds(1) });
        Assert.Equal(124, r.ExitCode);
        Assert.NotNull(r.Trailer);
    }

    [Fact]
    public async Task MemoryIsCapped()
    {
        var sh = new Shell().AllowPython(new PythonOptions { MaxMemory = 96L << 20 });
        var r = await sh.RunAsync("python3 -c 'x = bytearray(200 << 20)'");
        Assert.Equal(1, r.ExitCode);
        Assert.EndsWith("MemoryError\n", r.Stderr);
        r = await sh.RunAsync("python3 -c '\nx = []\nwhile True: x.append(bytearray(1 << 20))'");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("MemoryError", r.Stderr);
        Assert.Equal("1\n", (await sh.RunAsync("python3 -c 'print(1)'")).Stdout); // the next call starts fresh
    }

    [Fact]
    public async Task RecursionAndStack()
    {
        var r = await Run("python3 -c 'def f(): f()\nf()'");
        Assert.Equal(1, r.ExitCode);
        Assert.EndsWith("RecursionError: maximum recursion depth exceeded\n", r.Stderr);
        r = await Run("python3 -c 'import json; json.loads(\"[\" * 200000)'; echo next");
        Assert.Equal("next\n", r.Stdout);
        Assert.True(r.Stderr.Contains("RecursionError") || r.Stderr.Contains("ran out of stack"), r.Stderr);
    }

    [Fact]
    public async Task HugeFilesHitTheDiskCap()
    {
        var sh = PyShell(new ShellOptions { MaxFileSystemBytes = 8L << 20 });
        var r = await sh.RunAsync("python3 -c '\nf = open(\"/tmp/big\", \"wb\")\nwhile True: f.write(b\"x\" * 65536)'");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("No space left on device", r.Stderr);
        Assert.True(sh.Fs.UsedBytes <= 8L << 20);
        Assert.Equal(1, (await sh.RunAsync("python3 -c 'import os; os.truncate(\"/tmp/big\", 1 << 40)'")).ExitCode);
    }

    [Fact]
    public async Task OutputFloodEndsTheCall()
    {
        var r = await Run("python3 -c 'while True: print(\"x\" * 1000)'", new ShellOptions { MaxStringBytes = 1 << 20 });
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("output too large", r.Stderr);
    }

    [Fact]
    public async Task PathsStayInTheMachine()
    {
        var sh = PyShell();
        var hostFile = typeof(PythonTests).Assembly.Location; // exists on the host, not in the machine
        Assert.Contains("FileNotFoundError", (await sh.RunAsync($"python3 -c 'open(\"{hostFile}\")'")).Stderr);
        Assert.Equal("topwater\n", (await sh.RunAsync("python3 -c 'print(open(\"/../../../etc/hostname\").read(), end=\"\")'")).Stdout);
        await sh.RunAsync("ln -s /etc/hostname /tmp/link && ln -s /tmp/loop /tmp/loop");
        Assert.Equal("topwater\n", (await sh.RunAsync("python3 -c 'print(open(\"/tmp/link\").read(), end=\"\")'")).Stdout);
        Assert.Contains("[Errno 32]", (await sh.RunAsync("python3 -c 'open(\"/tmp/loop\")'")).Stderr); // ELOOP, in WASI's numbering
    }

    [Fact]
    public async Task TheStdlibZipIsReadOnlyAndInvisibleToCommands()
    {
        var sh = PyShell();
        var r = await sh.RunAsync($"python3 -c 'open(\"{Wasi.StdlibPath}\", \"ab\")'");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("OSError", r.Stderr);
        Assert.Equal(1, (await sh.RunAsync($"python3 -c 'import os; os.remove(\"{Wasi.StdlibPath}\")'")).ExitCode);
        Assert.Equal("1\n", (await sh.RunAsync("python3 -c 'import json; print(json.dumps(1))'")).Stdout);
        Assert.Equal("", (await sh.RunAsync("ls /usr/local/lib")).Stdout);
    }

    [Fact]
    public async Task DescriptorAbuseFailsCleanly()
    {
        Assert.Contains("Bad file descriptor", (await Run("python3 -c 'import os; os.read(999, 1)'")).Stderr);
        var r = await Run("python3 -c '\nfs = [open(\"/etc/hostname\") for _ in range(5000)]'");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("OSError", r.Stderr);
        Assert.Equal("after\n", (await Run("python3 -c 'import os; os.close(3); os.close(0); os.close(1); print(\"lost\")'; echo after")).Stdout);
    }

    [Theory]
    [InlineData("import subprocess; subprocess.run([\"ls\"])", "OSError")]
    [InlineData("import socket; socket.socket().connect((\"1.1.1.1\", 80))", "OSError")]
    [InlineData("import threading; threading.Thread(target=print).start()", "RuntimeError")]
    public async Task NoProcessesThreadsOrSockets(string code, string error)
    {
        var r = await Run($"python3 -c '{code}'");
        Assert.Equal(1, r.ExitCode);
        Assert.True(r.Stderr.Contains(error), r.Stderr);
    }

    [Fact]
    public async Task PermissionsHold()
    {
        Assert.Contains("PermissionError", (await Run("python3 -c 'open(\"/etc/passwd\", \"a\").write(\"x\")'")).Stderr);
        Assert.Contains("PermissionError", (await Run("python3 -c 'import os; os.listdir(\"/root\")'")).Stderr);
    }
}
