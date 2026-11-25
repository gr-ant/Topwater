# Ported from tests/Topwater.Tests/BashCompatFileDescriptors.cs on main (recorded from real bash/GNU tools).
import pytest

from bashcompat import check

CASES_MatchesBash = [
    ('printf \'a\\nb\\n\' > f; while read -u 3 line; do echo "got $line"; done 3< f', 'got a\ngot b\n', 0, True),
    ('printf \'x\\n\' > f; read -u 4 v 4< f; echo "$v"', 'x\n', 0, True),
    ('{ echo to3 >&3; } 3> out.txt; cat out.txt', 'to3\n', 0, True),
    ('{ echo out; echo err >&2; } 3>&1 1>&2 2>&3 2>/dev/null | cat', '', 0, False),
    ('echo hi > /dev/fd/1; echo err > /dev/fd/2 2>/dev/null', 'hi\n', 0, False),
    ('exec 5>&1; echo via5 >&5; exec 5>&-; echo closed >&5; echo "rc=$?"', 'via5\nrc=1\n', 0, False),
    ('echo x >&7; echo "rc=$?"', 'rc=1\n', 0, False),
    ('cat <&3; echo "rc=$?"', 'rc=1\n', 0, False),
    ('exec 3<<<"line one"; cat <&3', 'line one\n', 0, True),
]


@pytest.mark.parametrize("script,stdout,exit_code,quiet", CASES_MatchesBash)
def test_matches_bash(script, stdout, exit_code, quiet):
    check(script, stdout, exit_code, quiet)

