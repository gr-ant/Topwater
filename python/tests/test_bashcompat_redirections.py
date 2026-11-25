# Ported from tests/Topwater.Tests/BashCompatRedirections.cs on main (recorded from real bash/GNU tools).
import pytest

from bashcompat import check

CASES_MatchesBash = [
    ('echo a > f; echo b >> f; cat f', 'a\nb\n', 0, True),
    ('ls /nonexistent 2>&1 | head -1 | cut -c1-2', 'ls\n', 0, True),
    ('{ echo out; echo err >&2; } > o.txt 2>&1; cat o.txt', 'out\nerr\n', 0, True),
    ('{ echo out; echo err >&2; } &> o.txt; cat o.txt; { echo more; } &>> o.txt; cat o.txt', 'out\nerr\nout\nerr\nmore\n', 0, True),
    ('{ echo out; echo err >&2; } 2>&1 > o.txt | tr a-z A-Z; cat o.txt', 'ERR\nout\n', 0, True),
    ('echo x >| f; cat f', 'x\n', 0, True),
    ('set -o noclobber; echo a > f; echo b > f; echo "rc=$?"; echo c >| f; cat f', 'rc=1\nc\n', 0, False),
    ('cat < /dev/null; echo "rc=$?"', 'rc=0\n', 0, True),
    ('tr a-z A-Z <<< "hello"', 'HELLO\n', 0, True),
    ("cat <<'EOF'\nliteral $HOME `x` \\n\nEOF", 'literal $HOME `x` \\n\n', 0, True),
    ('cat <<-EOF\n\ttab stripped\n\t\ttwo tabs\n\tEOF', 'tab stripped\ntwo tabs\n', 0, True),
    ('cat <<EOF | tr a-z A-Z\npiped heredoc\nEOF', 'PIPED HEREDOC\n', 0, True),
    ('x=$(cat <<EOF\nin subst\nEOF\n); echo "$x"', 'in subst\n', 0, True),
    ('cat <<A; cat <<B\nfirst\nA\nsecond\nB', 'first\nsecond\n', 0, True),
    ('f() { cat; }; f <<EOF\nto function\nEOF', 'to function\n', 0, True),
    ('while read -r l; do echo "> $l"; done <<EOF\na\nb\nEOF', '> a\n> b\n', 0, True),
    ('cat <<EOF\nline \\\ncontinued\nEOF', 'line continued\n', 0, True),
    ('echo err 1>&2 2>/dev/null; echo ok', 'ok\n', 0, False),
    ('echo to-stderr >&2 2>/dev/null', '', 0, False),
    ('echo hi > /dev/stdout; echo err > /dev/stderr 2>/dev/null; echo x 2>/dev/null >/dev/stderr', 'hi\n', 0, False),
    ('diff <(echo a) <(echo a) && echo same', 'same\n', 0, True),
    ('cat < <(echo procsub)', 'procsub\n', 0, True),
    ('for i in 1 2; do echo $i; done > o.txt; cat o.txt', '1\n2\n', 0, True),
    ('if true; then echo in-if; fi > o.txt; cat o.txt', 'in-if\n', 0, True),
    ('f() { echo from-f; }; f > o.txt; cat o.txt', 'from-f\n', 0, True),
    ('f() { echo from-f; } > o.txt; f; cat o.txt', 'from-f\n', 0, True),
    ('echo a > "file with space"; cat "file with space"', 'a\n', 0, True),
    ('out=o.txt; echo v > $out; cat $out', 'v\n', 0, True),
    ('echo x > /nonexistent/dir/f; echo "rc=$?"', 'rc=1\n', 0, False),
    ('cat nofile; echo "rc=$?"', 'rc=1\n', 0, False),
    ('cat < nofile; echo "rc=$?"', 'rc=1\n', 0, False),
    ('echo a >f 2>&1; cat f', 'a\n', 0, True),
    ('read l < /dev/null; echo "rc=$? [$l]"', 'rc=1 []\n', 0, True),
    ("printf 'x\\ny\\n' > f; while read l; do echo $l; done < f", 'x\ny\n', 0, True),
    ('exec 3>&1; echo via3 >&3', 'via3\n', 0, True),
    ('echo foo 3>/dev/null; echo ok', 'foo\nok\n', 0, True),
    ('{ echo 1; echo 2 >&2; } 2>/dev/null', '1\n', 0, True),
    ('ls nonexist 2>/dev/null || echo failed', 'failed\n', 0, True),
    ('cmd_out=$(echo out; echo err >&2); echo "[$cmd_out]"', '[out]\n', 0, False),
    ('cmd_out=$( { echo out; echo err >&2; } 2>&1 ); echo "[$cmd_out]"', '[out\nerr]\n', 0, True),
    ('echo "a" | tee f | cat; cat f', 'a\na\n', 0, True),
    ('cat <<< "$(echo multi; echo line)"', 'multi\nline\n', 0, True),
    ('cat <<"EOF"\nquoted delim $x\nEOF', 'quoted delim $x\n', 0, True),
    ('cat << EOF\nspaced $((1+1))\nEOF', 'spaced 2\n', 0, True),
]


@pytest.mark.parametrize("script,stdout,exit_code,quiet", CASES_MatchesBash)
def test_matches_bash(script, stdout, exit_code, quiet):
    check(script, stdout, exit_code, quiet)

