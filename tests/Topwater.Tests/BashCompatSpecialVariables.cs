using Topwater;

namespace Topwater.Tests;

/// <summary>Special variables: $0 $# $@ $- $_ BASH_VERSION UID PPID IFS FUNCNAME PIPESTATUS ... Generated from differential runs: each expected value is what bash 5.2.21 printed.</summary>
public class BashCompatSpecialVariables
{
    [Theory]
    [InlineData("echo $0 $#; set -- a b; echo $# \"$@\" \"$*\"", "bash 0\n2 a b a b\n", 0, true)]
    [InlineData("echo $$ | grep -qE '^[0-9]+$' && echo pid", "pid\n", 0, true)]
    [InlineData("echo $- | grep -q h && echo has-h; set -e; case $- in *e*) echo has-e;; esac", "has-h\nhas-e\n", 0, true)]
    [InlineData("echo hi; echo $_", "hi\nhi\n", 0, true)]
    [InlineData("[[ $BASH_VERSION == 5* ]] && echo v5; echo ${BASH_VERSINFO[0]}", "v5\n5\n", 0, true)]
    [InlineData("echo ${#BASH_VERSINFO[@]}", "6\n", 0, true)]
    [InlineData("[[ -n $HOSTNAME ]] && echo hn", "hn\n", 0, true)]
    [InlineData("cd /tmp; cd /; echo $OLDPWD $PWD; cd - >/dev/null; echo $PWD", "/tmp /\n/tmp\n", 0, true)]
    [InlineData("echo $HOME", "/home/agent\n", 0, true)]
    [InlineData("[[ $UID =~ ^[0-9]+$ ]] && echo uid; [[ $EUID == $UID ]] && echo same", "uid\nsame\n", 0, true)]
    [InlineData("[[ $PPID =~ ^[0-9]+$ ]] && echo ppid", "ppid\n", 0, true)]
    [InlineData("[[ $SHLVL =~ ^[0-9]+$ ]] && echo shlvl", "shlvl\n", 0, true)]
    [InlineData("printf '%q\\n' \"$IFS\"", "$' \\t\\n'\n", 0, true)]
    [InlineData("echo \"${PS1-unset}\" | head -c 5; echo", "unset\n", 0, true)]
    [InlineData("echo $OSTYPE", "linux-gnu\n", 0, true)]
    [InlineData("[[ $MACHTYPE == x86_64* ]] && echo mt", "mt\n", 0, true)]
    [InlineData("f() { echo $FUNCNAME; }; f; echo \"[${FUNCNAME:-}]\"", "f\n[]\n", 0, true)]
    [InlineData("read <<< x; echo $REPLY", "x\n", 0, true)]
    [InlineData("echo $OPTIND", "1\n", 0, true)]
    [InlineData("echo \"${BASH_SOURCE[0]:-none}\"", "none\n", 0, true)]
    [InlineData("echo $BASH | grep -c bash", "1\n", 0, true)]
    [InlineData("echo ${RANDOM:+r}", "r\n", 0, true)]
    [InlineData("x=1; echo ${!x:-indirect-empty}", "indirect-empty\n", 0, true)]
    [InlineData("echo \"$@\" end", "end\n", 0, true)]
    [InlineData("echo $10 ${10}; set -- 1 2 3 4 5 6 7 8 9 ten; echo $10 ${10}", "0\n10 ten\n", 0, true)]
    [InlineData("echo $? ; ls /nope 2>/dev/null; echo $?", "0\n2\n", 0, true)]
    [InlineData("echo $!", "\n", 0, true)]
    [InlineData("echo $COLUMNS $LINES end", "end\n", 0, true)]
    [InlineData("[[ -n $TERM ]] && echo term", "term\n", 0, true)]
    public Task MatchesBash(string script, string stdout, int exitCode, bool quiet) => BashCompat.Check(script, stdout, exitCode, quiet);
}
