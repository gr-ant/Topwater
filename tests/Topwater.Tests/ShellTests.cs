using System.ComponentModel;
using Topwater;

namespace Topwater.Tests;

public class ShellTests
{
    static async Task<ShellResult> Run(Shell shell, string cmd) => await shell.RunAsync(cmd);

    static async Task<string> Out(Shell shell, string cmd)
    {
        var r = await shell.RunAsync(cmd);
        Assert.True(r.ExitCode == 0, $"`{cmd}` exited {r.ExitCode}: {r.Stderr}");
        return r.Stdout;
    }

    [Fact]
    public async Task FilesystemPersistsAcrossCalls()
    {
        var sh = new Shell();
        await Out(sh, "mkdir -p proj/src && cd proj");
        await Out(sh, "echo 'line one' > src/a.txt && echo 'line two' >> src/a.txt");
        Assert.Equal("/home/agent/proj\n", await Out(sh, "pwd"));
        Assert.Equal("line one\nline two\n", await Out(sh, "cat src/a.txt"));
        await Out(sh, "cp src/a.txt src/b.txt && mv src/b.txt c.txt && touch src/z.md");
        Assert.Equal("c.txt\nsrc\n", await Out(sh, "ls"));
        Assert.Equal("src/a.txt\nsrc/z.md\n", await Out(sh, "ls src/*"));
        Assert.Equal("./src/a.txt\n", await Out(sh, "find . -name '*.txt' -path '*src*'"));
        Assert.Equal(".\n├── c.txt\n└── src\n    ├── a.txt\n    └── z.md\n\n1 directory, 3 files\n", await Out(sh, "tree"));
        await Out(sh, "rm -r src");
        Assert.False(sh.Fs.Exists("/home/agent/proj/src"));
    }

    [Fact]
    public async Task HeredocsAndRedirects()
    {
        var sh = new Shell();
        await Out(sh, "NAME=world\ncat > hi.txt <<EOF\nhello $NAME\nEOF\ncat > raw.txt <<'EOF'\nhello $NAME\nEOF");
        Assert.Equal("hello world\n", sh.Fs.ReadAllText("/home/agent/hi.txt"));
        Assert.Equal("hello $NAME\n", sh.Fs.ReadAllText("/home/agent/raw.txt"));

        var r = await Run(sh, "cat nope 2>&1 > out.txt; cat missing > all.txt 2>&1");
        Assert.Contains("No such file", r.Stdout);
        Assert.Contains("No such file", sh.Fs.ReadAllText("/home/agent/all.txt"));
        Assert.Equal("", await Out(sh, "cat missing 2>/dev/null; true"));
        Assert.Equal("1\n3\n", await Out(sh, "wc -l < hi.txt; wc -l <<< 'x' > /dev/null; printf 'a\\nb\\nc\\n' | wc -l"));
    }

    [Fact]
    public async Task TextTools()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/home/agent/log.txt", "info start\nERROR disk full\ninfo retry\nerror again\ninfo done\n");
        Assert.Equal("2:ERROR disk full\n4:error again\n", await Out(sh, "grep -in error log.txt"));
        Assert.Equal("2\n", await Out(sh, "grep -ci 'error' log.txt"));
        Assert.Equal("info start\n--\ninfo retry\nerror again\n", await Out(sh, "grep -B1 -i 'start\\|again' log.txt | grep -v ERROR"));
        Assert.Equal("ERROR disk full\n", await Out(sh, "grep -E 'disk|nothing' log.txt"));
        Assert.Equal("./log.txt:3\n", await Out(sh, "grep -rc info . | grep -v '/\\.'"));
        Assert.Equal("info done\n", await Out(sh, "tail -1 log.txt"));
        Assert.Equal("info start\nERROR disk full\n", await Out(sh, "head -n 2 log.txt"));
        Assert.Equal("ERROR disk full\ninfo retry\n", await Out(sh, "sed -n '2,3p' log.txt"));
        Assert.Equal("INFO start\n", await Out(sh, "sed 's/info/INFO/g' log.txt | head -1"));
        Assert.Equal("[info] start\n", await Out(sh, "sed -E 's/^(\\w+) /[\\1] /' log.txt | head -1"));
        await Out(sh, "sed -i '/^info/d' log.txt");
        Assert.Equal("ERROR disk full\nerror again\n", await Out(sh, "cat log.txt"));
        Assert.Equal("      2 b\n      1 a\n", await Out(sh, "printf 'b\\na\\nb\\n' | sort | uniq -c | sort -rn"));
        Assert.Equal("b,c\n", await Out(sh, "echo a,b,c | cut -d, -f2-"));
        Assert.Equal("HELLO\n", await Out(sh, "echo hello | tr a-z A-Z"));
        Assert.Equal("hi\n", await Out(sh, "echo hi | tee copy.txt && cat copy.txt >/dev/null"));
        Assert.Equal("ERROR disk full\n", await Out(sh, "ls *.txt | grep log | xargs grep -h disk"));
    }

    [Fact]
    public async Task JqSubset()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/home/agent/o.json", """{"orders":[{"id":1,"status":"open","total":5},{"id":2,"status":"shipped","total":7.5}]}""");
        Assert.Equal("2\n", await Out(sh, "jq '.orders | length' o.json"));
        Assert.Equal("open\nshipped\n", await Out(sh, "jq -r '.orders[].status' o.json"));
        Assert.Equal("2\n", await Out(sh, "jq '.orders[] | select(.status == \"shipped\") | .id' o.json"));
        Assert.Equal("[1,2]\n", await Out(sh, "cat o.json | jq -c '[.orders[].id]'"));
        Assert.Equal("12.5\n", await Out(sh, "jq 'map(.total) | add' <<< \"$(jq -c .orders o.json)\""));
        Assert.Equal("[\"orders\"]\n", await Out(sh, "jq -c keys o.json"));
    }

    [Fact]
    public async Task ErrorsAreBashLike()
    {
        var sh = new Shell();
        var r = await Run(sh, "frobnicate --now");
        Assert.Equal(127, r.ExitCode);
        Assert.Contains("frobnicate: command not found", r.Stderr);

        r = await Run(sh, "echo 'unterminated");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("unexpected EOF", r.Stderr);

        r = await Run(sh, "coproc cat");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("not supported", r.Stderr);

        r = await Run(sh, "cat nope.txt");
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("cat: nope.txt: No such file or directory\n", r.Stderr);
        Assert.Equal("cat: nope.txt: No such file or directory\n[exit code 1]", r.ToToolOutput());

        r = await Run(sh, "ls -y");
        Assert.Equal("ls: invalid option -- 'y'\nTry 'ls --help' for more information.\n", r.Stderr);
    }

    [Fact]
    public async Task FakeSystem()
    {
        var sh = new Shell(new ShellOptions { User = "dev", Hostname = "box" });
        Assert.Equal("dev\nbox\nLinux\n/home/dev\n", await Out(sh, "whoami; hostname; uname; echo $HOME"));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}\n$", await Out(sh, "date +%F"));
        Assert.Equal("/usr/bin/grep\n", await Out(sh, "which grep"));
        Assert.Contains("1  whoami; hostname", await Out(sh, "history"));
        await Out(sh, "alias ll='ls -la'");
        Assert.Contains("..", await Out(sh, "ll"));
    }

    // ---------- custom tools ----------

    public enum Units { Metric, Imperial }

    public class WeatherTools
    {
        [Command("weather", Description = "Get the forecast for a city")]
        public string Forecast([Description("City name")] string city, int days = 1, Units units = Units.Metric, bool verbose = false) =>
            $"{city}: {days} day(s), {units}{(verbose ? " (verbose)" : "")}";

        [Command("orders")]
        public async Task<object> Orders(string[] ids)
        {
            await Task.Yield();
            return ids.Select(id => new { id, status = "open" });
        }

        [Command("count-lines")]
        public static int CountLines(CommandContext ctx)
        {
            ctx.Stdout.WriteLine(Builtins_SplitCount(ctx.StdinText));
            return 0;
        }

        static int Builtins_SplitCount(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    [Fact]
    public async Task AttributeCommands()
    {
        var sh = new Shell().Register(new WeatherTools());
        Assert.Equal("Paris: 1 day(s), Metric\n", await Out(sh, "weather Paris"));
        Assert.Equal("Oslo: 3 day(s), Imperial (verbose)\n", await Out(sh, "weather --city Oslo --days=3 --units imperial --verbose"));
        Assert.Contains("\"status\": \"open\"", await Out(sh, "orders 1 2"));
        Assert.Equal("2\n", await Out(sh, "printf 'a\\nb\\n' | count-lines"));
        Assert.Equal("Paris: 1 day(s), Metric\n", await Out(sh, "weather Paris > w.txt && cat w.txt"));

        var help = await Out(sh, "weather --help");
        Assert.Contains("Usage: weather --city <text> [--days <int>]", help);
        Assert.Contains("City name (required)", help);
        Assert.Contains("(metric|imperial)", help);

        var r = await Run(sh, "weather");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("missing required option --city", r.Stderr);
        r = await Run(sh, "weather Rome --days soon");
        Assert.Contains("invalid value 'soon' for --days: expected int", r.Stderr);
        Assert.Contains("weather", await Out(sh, "help"));
    }

    [Fact]
    public async Task FluentCommands()
    {
        var sh = new Shell();
        sh.Register("deploy", async ctx =>
        {
            await ctx.Stdout.WriteLineAsync($"deploying {string.Join(",", ctx.Args)}");
            return ctx.Args.Count == 0 ? 1 : 0;
        }, "Deploy a service");
        Assert.Equal("deploying api\n", await Out(sh, "deploy api"));
        Assert.Equal(1, (await Run(sh, "deploy")).ExitCode);
        Assert.Equal("/usr/bin/deploy\n", await Out(sh, "which deploy"));
    }

    [Fact]
    public async Task ToolOutput()
    {
        var sh = new Shell();
        Assert.Equal("(no output)", await BashTool.ExecuteAsync(sh, "true"));
        var big = (await sh.RunAsync("seq_missing")).ToToolOutput();
        Assert.EndsWith("[exit code 127]", big);
        sh.Fs.WriteAllText("/big.txt", new string('x', 40_000));
        Assert.Contains("output truncated", await BashTool.ExecuteAsync(sh, "cat /big.txt"));
    }
}
