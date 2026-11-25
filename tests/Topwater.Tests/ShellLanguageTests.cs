using Topwater;

namespace Topwater.Tests;

/// <summary>Shell-language behaviour that the differential BashCompat* runs can't express: host state, limits, persistence.</summary>
public class ShellLanguageTests
{
    [Fact]
    public async Task HostVariablesAreExportedAndShellVariablesAreNot()
    {
        var sh = new Shell();
        sh.Environment["API_URL"] = "https://example.test";
        var r = await sh.RunAsync("local_only=1; bash -c 'echo \"[$API_URL] [$local_only] [$HOME]\"'; export local_only; bash -c 'echo $local_only'");
        Assert.Equal("[https://example.test] [] [/home/agent]\n1\n", r.Stdout);
        r = await sh.RunAsync("export -n API_URL; bash -c 'echo \"[$API_URL]\"'");
        Assert.Equal("[]\n", r.Stdout);
    }

    [Theory]
    [InlineData("trap 'false' ERR; false; echo survived")]                       // the ERR trap's own failure doesn't re-trigger it
    [InlineData("trap 'echo d >/dev/null' DEBUG; f() { :; }; f; f; echo survived")] // nor does the DEBUG trap run for its own commands
    [InlineData("declare -n a=b; declare -n b=a; echo \"[$a]\"; echo survived")]    // a nameref cycle stops
    [InlineData("echo {1..10000}{1..10000} >/dev/null; echo survived")]
    [InlineData("while :; do wait; done; echo survived")]
    [InlineData("f() { f; }; trap 'echo r >/dev/null' RETURN; f; echo survived")]
    public async Task RunawayConstructsEnd(string cmd)
    {
        var r = await new Shell().RunAsync(cmd);
        Assert.DoesNotContain("internal error", r.Stderr);
    }

    [Fact]
    public async Task ExecRedirectionLastsForTheCallAndScriptsKeepTheirOwn()
    {
        var sh = new Shell();
        var r = await sh.RunAsync("exec 2>&1; ls /nope; printf 'exec >inner.log\\necho hidden\\n' > s.sh; bash s.sh; echo shown; cat inner.log");
        Assert.Equal("ls: cannot access '/nope': No such file or directory\nshown\nhidden\n", r.Stdout);
        Assert.Equal("", r.Stderr);
        r = await sh.RunAsync("echo err >&2");   // the next tool call starts with its own stdout and stderr
        Assert.Equal("err\n", r.Stderr);
    }

    [Fact]
    public async Task OptionsResetBetweenCallsButVariablesPersist()
    {
        var sh = new Shell();
        await sh.RunAsync("set -euxf -o noclobber; declare -i n=2; readonly R=1; shopt -s nocasematch");
        var r = await sh.RunAsync("echo $- ; n+=3; echo $n; [[ A == a ]] && echo nocase; R=2");
        Assert.Equal("hBc\n5\nnocase\n", r.Stdout);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("R: readonly variable", r.Stderr);
    }
}
