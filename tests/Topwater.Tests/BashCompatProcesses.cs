using Topwater;

namespace Topwater.Tests;

/// <summary>Child shells (bash -c, bash file, ./script): what they inherit, $0, BASH_SOURCE and bash -e. Generated from differential runs: each expected value is what bash 5.2.21 printed.</summary>
public class BashCompatProcesses
{
    [Theory]
    [InlineData("x=1; bash -c 'echo \"[$x]\"'; export x; bash -c 'echo \"[$x]\"'", "[]\n[1]\n", 0, true)]
    [InlineData("Y=2 bash -c 'echo \"$Y\"'; echo \"[${Y-}]\"", "2\n[]\n", 0, true)]
    [InlineData("f() { echo fn; }; bash -c 'f' 2>/dev/null || echo \"not inherited\"; export -f f; bash -c f", "not inherited\nfn\n", 0, true)]
    [InlineData("printf 'echo \"0=$0 1=$1 n=$#\"\\n' > s.sh; bash s.sh a b; chmod +x s.sh; ./s.sh c", "0=s.sh 1=a n=2\n0=./s.sh 1=c n=1\n", 0, true)]
    [InlineData("printf '#!/bin/bash\\necho \"src=${BASH_SOURCE[0]}\"\\n' > t.sh; chmod +x t.sh; ./t.sh; bash t.sh; source t.sh", "src=./t.sh\nsrc=t.sh\nsrc=t.sh\n", 0, true)]
    [InlineData("mkdir -p proj/bin; printf '#!/bin/bash\\nDIR=\"$(cd \"$(dirname \"${BASH_SOURCE[0]}\")\" && pwd)\"\\necho \"${DIR##*/}\"\\n' > proj/bin/run.sh; chmod +x proj/bin/run.sh; ./proj/bin/run.sh; bash proj/bin/run.sh", "bin\nbin\n", 0, true)]
    [InlineData("printf 'echo \"$(basename \"$0\")\"\\n' > named.sh; bash named.sh", "named.sh\n", 0, true)]
    [InlineData("bash -c 'echo \"$0 $1\"' myname arg1", "myname arg1\n", 0, true)]
    [InlineData("bash -c 'echo $#' _ a b c", "3\n", 0, true)]
    [InlineData("bash -ec 'false; echo not reached'; echo \"rc=$?\"", "rc=1\n", 0, true)]
    [InlineData("bash -c 'set -o | grep -E \"^errexit\"' | tr -s ' \\t' ' '", "errexit off\n", 0, true)]
    [InlineData("set -e; bash -c 'false; echo child-continues'; echo \"never\"", "child-continues\nnever\n", 0, true)]
    [InlineData("bash -euo pipefail -c 'false | true; echo no'; echo \"rc=$?\"", "rc=1\n", 0, true)]
    [InlineData("printf 'set -e\\nfalse\\necho no\\n' > e.sh; bash e.sh; echo \"rc=$?\"", "rc=1\n", 0, true)]
    [InlineData("printf 'exit 7\\n' > x.sh; bash x.sh; echo $?; sh x.sh; echo $?", "7\n7\n", 0, true)]
    [InlineData("a=(1 2); bash -c 'echo \"${#a[@]}\"'", "0\n", 0, true)]
    [InlineData("alias hi='echo hi'; bash -c 'hi' 2>/dev/null || echo \"alias not inherited\"", "alias not inherited\n", 0, true)]
    [InlineData("cd /tmp; bash -c 'pwd'", "/tmp\n", 0, true)]
    [InlineData("printf 'cd /\\n' > c.sh; bash c.sh; pwd | grep -c /tmp", "1\n", 0, true)]
    [InlineData("printf 'v=set_by_script\\n' > v.sh; bash v.sh; echo \"[${v-}]\"; . ./v.sh; echo \"[$v]\"", "[]\n[set_by_script]\n", 0, true)]
    [InlineData("echo 'echo in-stdin-script' | bash", "in-stdin-script\n", 0, true)]
    [InlineData("bash <<'EOF'\necho \"heredoc script $0\"\nEOF", "heredoc script bash\n", 0, true)]
    [InlineData("export PATH_EXTRA=/x; bash -c 'echo $PATH_EXTRA'", "/x\n", 0, true)]
    [InlineData("readonly R=1; export R; bash -c 'R=2; echo $R'", "2\n", 0, true)]
    [InlineData("f() { echo \"in f: $0\"; }; f", "in f: bash\n", 0, true)]
    [InlineData("echo \"top: $0\"", "top: bash\n", 0, true)]
    [InlineData("printf 'f() { echo \"f $0 ${FUNCNAME[0]} ${BASH_SOURCE[0]}\"; }\\nf\\n' > fn.sh; bash fn.sh", "f fn.sh f fn.sh\n", 0, true)]
    public Task MatchesBash(string script, string stdout, int exitCode, bool quiet) => BashCompat.Check(script, stdout, exitCode, quiet);
}
