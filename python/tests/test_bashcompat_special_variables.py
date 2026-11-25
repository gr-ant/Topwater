# Ported from tests/Topwater.Tests/BashCompatSpecialVariables.cs on main (recorded from real bash/GNU tools).
import pytest

from bashcompat import check

CASES_MatchesBash = [
    ('echo $0 $#; set -- a b; echo $# "$@" "$*"', 'bash 0\n2 a b a b\n', 0, True),
    ("echo $$ | grep -qE '^[0-9]+$' && echo pid", 'pid\n', 0, True),
    ('echo $- | grep -q h && echo has-h; set -e; case $- in *e*) echo has-e;; esac', 'has-h\nhas-e\n', 0, True),
    ('echo hi; echo $_', 'hi\nhi\n', 0, True),
    ('[[ $BASH_VERSION == 5* ]] && echo v5; echo ${BASH_VERSINFO[0]}', 'v5\n5\n', 0, True),
    ('echo ${#BASH_VERSINFO[@]}', '6\n', 0, True),
    ('[[ -n $HOSTNAME ]] && echo hn', 'hn\n', 0, True),
    ('cd /tmp; cd /; echo $OLDPWD $PWD; cd - >/dev/null; echo $PWD', '/tmp /\n/tmp\n', 0, True),
    ('echo $HOME', '/home/agent\n', 0, True),
    ('[[ $UID =~ ^[0-9]+$ ]] && echo uid; [[ $EUID == $UID ]] && echo same', 'uid\nsame\n', 0, True),
    ('[[ $PPID =~ ^[0-9]+$ ]] && echo ppid', 'ppid\n', 0, True),
    ('[[ $SHLVL =~ ^[0-9]+$ ]] && echo shlvl', 'shlvl\n', 0, True),
    ('printf \'%q\\n\' "$IFS"', "$' \\t\\n'\n", 0, True),
    ('echo "${PS1-unset}" | head -c 5; echo', 'unset\n', 0, True),
    ('echo $OSTYPE', 'linux-gnu\n', 0, True),
    ('[[ $MACHTYPE == x86_64* ]] && echo mt', 'mt\n', 0, True),
    ('f() { echo $FUNCNAME; }; f; echo "[${FUNCNAME:-}]"', 'f\n[]\n', 0, True),
    ('read <<< x; echo $REPLY', 'x\n', 0, True),
    ('echo $OPTIND', '1\n', 0, True),
    ('echo "${BASH_SOURCE[0]:-none}"', 'none\n', 0, True),
    ('echo $BASH | grep -c bash', '1\n', 0, True),
    ('echo ${RANDOM:+r}', 'r\n', 0, True),
    ('x=1; echo ${!x:-indirect-empty}', 'indirect-empty\n', 0, True),
    ('echo "$@" end', 'end\n', 0, True),
    ('echo $10 ${10}; set -- 1 2 3 4 5 6 7 8 9 ten; echo $10 ${10}', '0\n10 ten\n', 0, True),
    ('echo $? ; ls /nope 2>/dev/null; echo $?', '0\n2\n', 0, True),
    ('echo $!', '\n', 0, True),
    ('echo $COLUMNS $LINES end', 'end\n', 0, True),
    ('[[ -n $TERM ]] && echo term', 'term\n', 0, True),
]


@pytest.mark.parametrize("script,stdout,exit_code,quiet", CASES_MatchesBash)
def test_matches_bash(script, stdout, exit_code, quiet):
    check(script, stdout, exit_code, quiet)

