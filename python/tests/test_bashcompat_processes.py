# Ported from tests/Topwater.Tests/BashCompatProcesses.cs on main (recorded from real bash/GNU tools).
import pytest

from bashcompat import check

CASES_MatchesBash = [
    ('x=1; bash -c \'echo "[$x]"\'; export x; bash -c \'echo "[$x]"\'', '[]\n[1]\n', 0, True),
    ('Y=2 bash -c \'echo "$Y"\'; echo "[${Y-}]"', '2\n[]\n', 0, True),
    ('f() { echo fn; }; bash -c \'f\' 2>/dev/null || echo "not inherited"; export -f f; bash -c f', 'not inherited\nfn\n', 0, True),
    ('printf \'echo "0=$0 1=$1 n=$#"\\n\' > s.sh; bash s.sh a b; chmod +x s.sh; ./s.sh c', '0=s.sh 1=a n=2\n0=./s.sh 1=c n=1\n', 0, True),
    ('printf \'#!/bin/bash\\necho "src=${BASH_SOURCE[0]}"\\n\' > t.sh; chmod +x t.sh; ./t.sh; bash t.sh; source t.sh', 'src=./t.sh\nsrc=t.sh\nsrc=t.sh\n', 0, True),
    ('mkdir -p proj/bin; printf \'#!/bin/bash\\nDIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"\\necho "${DIR##*/}"\\n\' > proj/bin/run.sh; chmod +x proj/bin/run.sh; ./proj/bin/run.sh; bash proj/bin/run.sh', 'bin\nbin\n', 0, True),
    ('printf \'echo "$(basename "$0")"\\n\' > named.sh; bash named.sh', 'named.sh\n', 0, True),
    ('bash -c \'echo "$0 $1"\' myname arg1', 'myname arg1\n', 0, True),
    ("bash -c 'echo $#' _ a b c", '3\n', 0, True),
    ('bash -ec \'false; echo not reached\'; echo "rc=$?"', 'rc=1\n', 0, True),
    ('bash -c \'set -o | grep -E "^errexit"\' | tr -s \' \\t\' \' \'', 'errexit off\n', 0, True),
    ('set -e; bash -c \'false; echo child-continues\'; echo "never"', 'child-continues\nnever\n', 0, True),
    ('bash -euo pipefail -c \'false | true; echo no\'; echo "rc=$?"', 'rc=1\n', 0, True),
    ('printf \'set -e\\nfalse\\necho no\\n\' > e.sh; bash e.sh; echo "rc=$?"', 'rc=1\n', 0, True),
    ("printf 'exit 7\\n' > x.sh; bash x.sh; echo $?; sh x.sh; echo $?", '7\n7\n', 0, True),
    ('a=(1 2); bash -c \'echo "${#a[@]}"\'', '0\n', 0, True),
    ('alias hi=\'echo hi\'; bash -c \'hi\' 2>/dev/null || echo "alias not inherited"', 'alias not inherited\n', 0, True),
    ("cd /tmp; bash -c 'pwd'", '/tmp\n', 0, True),
    ("printf 'cd /\\n' > c.sh; bash c.sh; pwd | grep -c /tmp", '1\n', 0, True),
    ('printf \'v=set_by_script\\n\' > v.sh; bash v.sh; echo "[${v-}]"; . ./v.sh; echo "[$v]"', '[]\n[set_by_script]\n', 0, True),
    ("echo 'echo in-stdin-script' | bash", 'in-stdin-script\n', 0, True),
    ('bash <<\'EOF\'\necho "heredoc script $0"\nEOF', 'heredoc script bash\n', 0, True),
    ("export PATH_EXTRA=/x; bash -c 'echo $PATH_EXTRA'", '/x\n', 0, True),
    ("readonly R=1; export R; bash -c 'R=2; echo $R'", '2\n', 0, True),
    ('f() { echo "in f: $0"; }; f', 'in f: bash\n', 0, True),
    ('echo "top: $0"', 'top: bash\n', 0, True),
    ('printf \'f() { echo "f $0 ${FUNCNAME[0]} ${BASH_SOURCE[0]}"; }\\nf\\n\' > fn.sh; bash fn.sh', 'f fn.sh f fn.sh\n', 0, True),
]


@pytest.mark.parametrize("script,stdout,exit_code,quiet", CASES_MatchesBash)
def test_matches_bash(script, stdout, exit_code, quiet):
    check(script, stdout, exit_code, quiet)

