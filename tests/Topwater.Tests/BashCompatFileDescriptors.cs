using Topwater;

namespace Topwater.Tests;

/// <summary>File descriptors 3 and up: per command, through exec, and read -u. Generated from differential runs: each expected value is what bash 5.2.21 printed.</summary>
public class BashCompatFileDescriptors
{
    [Theory]
    [InlineData("printf 'a\\nb\\n' > f; while read -u 3 line; do echo \"got $line\"; done 3< f", "got a\ngot b\n", 0, true)]
    [InlineData("printf 'x\\n' > f; read -u 4 v 4< f; echo \"$v\"", "x\n", 0, true)]
    [InlineData("{ echo to3 >&3; } 3> out.txt; cat out.txt", "to3\n", 0, true)]
    [InlineData("{ echo out; echo err >&2; } 3>&1 1>&2 2>&3 2>/dev/null | cat", "", 0, false)]
    [InlineData("echo hi > /dev/fd/1; echo err > /dev/fd/2 2>/dev/null", "hi\n", 0, false)]
    [InlineData("exec 5>&1; echo via5 >&5; exec 5>&-; echo closed >&5; echo \"rc=$?\"", "via5\nrc=1\n", 0, false)]
    [InlineData("echo x >&7; echo \"rc=$?\"", "rc=1\n", 0, false)]
    [InlineData("cat <&3; echo \"rc=$?\"", "rc=1\n", 0, false)]
    [InlineData("exec 3<<<\"line one\"; cat <&3", "line one\n", 0, true)]
    public Task MatchesBash(string script, string stdout, int exitCode, bool quiet) => BashCompat.Check(script, stdout, exitCode, quiet);
}
