# Ported from tests/Topwater.Tests/BashCompatLoops.cs on main (recorded from real bash/GNU tools).
import pytest

from bashcompat import check

CASES_MatchesBash = [
    ('time { echo in-group; } 2>/dev/null', 'in-group\n', 0, False),
    ('time for i in 1; do echo $i; done 2>/dev/null', '1\n', 0, False),
    ('x=$( { time echo hi; } 2>&1 ); echo "${x%%$\'\\n\'*}"', 'hi\n', 0, True),
    ("trap 'echo dbg' DEBUG; echo one", 'dbg\none\n', 0, True),
    ('wait -n 2>/dev/null; echo "rc=$?"', 'rc=127\n', 0, True),
    ('exec {fd}>out.txt; echo "fd=$fd"; echo data >&$fd; exec {fd}>&-; cat out.txt', 'fd=10\ndata\n', 0, True),
    ('read -t 0 <<< "x"; echo "rc=$?"', 'rc=0\n', 0, True),
    ('declare -g G=1; f() { declare -g H=2; local L=3; }; f; echo "$G $H ${L:-nolocal}"', '1 2 nolocal\n', 0, True),
    ('f() { local -a arr=(1 2 3); echo "${arr[@]}"; }; f; echo "[${arr[*]}]"', '1 2 3\n[]\n', 0, True),
    ('f() { local -A m=([a]=1); echo "${m[a]}"; }; f; declare -p m 2>/dev/null || echo gone', '1\ngone\n', 0, True),
    ('a=(1 2); export a; bash -c \'echo "[${a[@]}]"\'', '[]\n', 0, True),
    ('f() { echo "$1"; }; export -f f; bash -c \'f hi\' 2>/dev/null || echo "no exported fn"', 'hi\n', 0, True),
    ('g() { local x=1; h() { echo "h sees $x"; }; h; }; g', 'h sees 1\n', 0, True),
    ('f() { local -n r1=$1; local -n r2=r1; r2=deep; }; f target; echo $target', 'deep\n', 0, True),
    ('f() { local v=$1; shift; echo "$v + $*"; }; f a b c', 'a + b c\n', 0, True),
    ('x=global; f() { local x=local; unset x; echo "[${x-unset}]"; }; f', '[unset]\n', 0, True),
    ('f() { local x; x=1; g; echo "f: $x"; }; g() { local x=2; }; f', 'f: 1\n', 0, True),
    ('arr=(a b c); f() { local i; for i in "${!arr[@]}"; do arr[i]=${arr[i]^}; done; }; f; echo "${arr[@]}"', 'A B C\n', 0, True),
    ('for ((i=0; i<3; i++)); do :; done; echo $i', '3\n', 0, True),
    ('for ((i=0, s=0; i<=10; s+=i, i++)); do :; done; echo $s', '55\n', 0, True),
    ('i=10; while (( i-- > 7 )); do echo $i; done', '9\n8\n7\n', 0, True),
    ('n=0; until (( n >= 3 )); do (( n++ )); done; echo $n', '3\n', 0, True),
    ('for x in $(seq 3); do echo -n $x; done; echo', '123\n', 0, True),
    ('for x in "$(seq 3)"; do echo "[$x]"; done', '[1\n2\n3]\n', 0, True),
    ('while read -r a; do echo $a; done < <(echo one; echo two)', 'one\ntwo\n', 0, True),
    ("count=0; while read -r _; do ((count++)); done <<< $'x\\ny\\nz'; echo $count", '3\n', 0, True),
    ('lines=$(printf \'a\\nb\\n\'); while IFS= read -r l; do echo "<$l>"; done <<< "$lines"', '<a>\n<b>\n', 0, True),
    ('outer=0; for i in 1 2 3; do for j in 1 2 3; do (( j == 2 )) && break; (( outer++ )); done; done; echo $outer', '3\n', 0, True),
    ('for i in 1 2 3; do echo $i; done | sort -r | head -1', '3\n', 0, True),
    ('echo $(for i in a b; do echo $i; done)', 'a b\n', 0, True),
    ('f() { for a in "$@"; do [[ $a == stop ]] && return 5; done; return 0; }; f x stop y; echo $?', '5\n', 0, True),
    ('s=0; for n in 1 2 3 4; do (( n % 2 )) || continue; (( s += n )); done; echo $s', '4\n', 0, True),
    ('case "$(echo hi)" in hi) echo matched;; esac', 'matched\n', 0, True),
    ('case a in (a) echo paren-pattern;; esac', 'paren-pattern\n', 0, True),
    ('case "x y" in "x y") echo quoted-pattern;; esac', 'quoted-pattern\n', 0, True),
    ('v=b; case $v in a|b) echo ab;; esac; case $v in [abc]) echo class;; esac', 'ab\nclass\n', 0, True),
    ('case 1 in *) echo first;; 1) echo second;; esac', 'first\n', 0, True),
    ('case "" in *) echo star-matches-empty;; esac', 'star-matches-empty\n', 0, True),
    ('case abc in *b*) echo contains;; esac; case "a.b" in *.*) echo dotted;; esac', 'contains\ndotted\n', 0, True),
    ('x=\'*\'; case abc in $x) echo unquoted-var-glob;; esac; case abc in "$x") echo no;; *) echo quoted-var-literal;; esac', 'unquoted-var-glob\nquoted-var-literal\n', 0, True),
    ('if [ -d / ] && [ -f /etc/passwd ]; then echo sys; else echo nosys; fi', 'sys\n', 0, True),
    ('[[ -d /tmp ]] && echo tmp-dir', 'tmp-dir\n', 0, True),
    ('[ -e /dev/null ] && echo devnull', 'devnull\n', 0, True),
    ('test -x /bin/bash || echo not-exec; [ -r /etc/hostname ] && echo readable', 'readable\n', 0, True),
    ('echo "$(echo "$(echo "$(echo deep)")")"', 'deep\n', 0, True),
    ('echo `echo \\`echo inner\\``', 'inner\n', 0, True),
    ('x=$(echo $((3+4))); echo $x', '7\n', 0, True),
    ('s="x"; s+="y"; s+=z; echo $s; n=1; n+=1; echo $n', 'xyz\n11\n', 0, True),
    ('a=1; a+=(2); echo "${a[@]}"', '1 2\n', 0, True),
    ('arr=(x); arr+=y; echo "${arr[@]}"', 'xy\n', 0, True),
    ('declare -A m; m+=([a]=1); m[a]+=2; echo ${m[a]}', '12\n', 0, True),
]


@pytest.mark.parametrize("script,stdout,exit_code,quiet", CASES_MatchesBash)
def test_matches_bash(script, stdout, exit_code, quiet):
    check(script, stdout, exit_code, quiet)

