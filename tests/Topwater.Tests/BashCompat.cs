using Topwater;

namespace Topwater.Tests;

/// <summary>
/// The check behind the BashCompat* theories: a script run in a fresh shell (in an empty directory, like the
/// differential runs against real bash) gives bash's stdout and exit status, and writes to stderr only when bash did.
/// </summary>
public static class BashCompat
{
    public static async Task Check(string script, string stdout, int exitCode, bool quiet)
    {
        var sh = new Shell();
        await sh.RunAsync("mkdir -p /tmp/w && cd /tmp/w");
        var r = await sh.RunAsync(script);
        Assert.Equal(stdout, r.Stdout);
        Assert.Equal(exitCode, r.ExitCode);
        Assert.True(quiet == (r.Stderr.Length == 0), $"bash {(quiet ? "wrote nothing to" : "wrote to")} stderr; this shell wrote: {r.Stderr}");
    }
}
