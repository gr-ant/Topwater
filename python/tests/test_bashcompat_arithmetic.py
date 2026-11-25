# Ported from tests/Topwater.Tests/BashCompatArithmetic.cs on main (recorded from real bash/GNU tools).
import pytest

from bashcompat import check

CASES_MatchesBash = [
    ('echo $(( 2 + 3 * 4 )) $(( (2+3)*4 )) $(( 7 / 2 )) $(( -7 / 2 )) $(( 7 % 3 )) $(( -7 % 3 )) $(( 2 ** 10 ))', '14 20 3 -3 1 -1 1024\n', 0, True),
    ('echo $(( 1 << 4 )) $(( 256 >> 2 )) $(( 5 & 3 )) $(( 5 | 3 )) $(( 5 ^ 3 )) $(( ~5 )) $(( !5 )) $(( !0 ))', '16 64 1 7 6 -6 0 1\n', 0, True),
    ('echo $(( 3 > 2 )) $(( 3 < 2 )) $(( 3 >= 3 )) $(( 3 <= 2 )) $(( 3 == 3 )) $(( 3 != 3 )) $(( 1 && 0 )) $(( 1 || 0 ))', '1 0 1 0 1 0 0 1\n', 0, True),
    ('x=5; echo $(( x > 3 ? 10 : 20 )) $(( x < 3 ? 10 : x < 6 ? 30 : 40 ))', '10 30\n', 0, True),
    ('x=1; echo $(( x += 2, x * 10 )); echo $x', '30\n3\n', 0, True),
    ('x=10; (( x -= 3 )); (( x *= 2 )); (( x /= 3 )); (( x %= 3 )); echo $x; (( x <<= 3 )); (( x |= 1 )); echo $x', '1\n9\n', 0, True),
    ('i=5; echo $(( i++ )) $i $(( ++i )) $i $(( i-- )) $(( --i )) $i', '5 6 7 7 7 5 5\n', 0, True),
    ('a=3 b=4; echo $(( a*b )) $(( $a*$b )) $((a+b))', '12 12 7\n', 0, True),
    ('x=y; y=z; z=7; echo $(( x + 1 ))', '8\n', 0, True),
    ('x="3 + 4"; echo $(( x * 2 )) $(( $x * 2 ))', '14 11\n', 0, True),
    ('(( 0 )); echo $?; (( 5 )); echo $?; (( x = 0 )); echo $?', '1\n0\n1\n', 0, True),
    ('let x=5+3 y=x*2; echo $x $y; let "z = 1 << 3"; echo $z; let 0; echo $?', '8 16\n8\n1\n', 0, True),
    ('echo $(( $(echo 3) + ${#PWD} * 0 ))', '3\n', 0, True),
    ('echo $(( 1 / 0 )); echo after', '', 1, False),
    ('x=0; (( 5 / x )); echo "rc=$?"', 'rc=1\n', 0, False),
    ('echo $(( 9223372036854775807 + 1 )) $(( 2 ** 63 )) $(( -9223372036854775808 / -1 ))', '-9223372036854775808 -9223372036854775808 -9223372036854775808\n', 0, True),
    ('echo $(( 010 )) $(( 08 ))', '', 1, False),
    ('echo $(( 0x10 + 0X0a + 8#17 + 2#1111 ))', '56\n', 0, True),
    ('n=; echo $(( n + 1 )); unset m; echo $(( m * 2 ))', '1\n0\n', 0, True),
    ('echo $((  5  ))  $(( -5 )) $(( +5 )) $(( - -5 )) $(( 5--3 ))', '5 -5 5 5 8\n', 0, True),
    ('for ((i=0, j=10; i<3; i++, j--)); do echo "$i $j"; done', '0 10\n1 9\n2 8\n', 0, True),
    ('for ((;;)); do echo once; break; done', 'once\n', 0, True),
    ("for (( i = 3 ; i > 0 ; i-- )); do printf '%d ' $i; done; echo", '3 2 1 \n', 0, True),
    ('x=5; (( x++ )) && echo yes; y=0; (( y++ )) || echo "zero then $y"', 'yes\nzero then 1\n', 0, True),
    ('r=$RANDOM; (( r >= 0 && r < 32768 )) && echo ok', 'ok\n', 0, True),
    ('echo $(( 2**3**2 )) $(( -2**2 )) $(( (-2)**2 ))', '512 4 4\n', 0, True),
    ('x=4; echo $(( x**2 )) $(( x<<1 )) $(( x>>1 ))', '16 8 2\n', 0, True),
    ('echo $(( 1, 2, 3 ))', '3\n', 0, True),
    ('echo $(( 2 ** -1 ))', '', 1, False),
    ('echo $(( 1 + ))', '', 1, False),
    ('echo $(( abc ))', '0\n', 0, True),
    ('x=5; echo $(( x = x * 2 + 1 )) $x', '11 11\n', 0, True),
    ('a=1; b=2; (( a < b )) && echo less; (( a == 1 && b == 2 )) && echo both', 'less\nboth\n', 0, True),
    ('n=10; while (( n > 7 )); do echo $n; (( n-- )); done', '10\n9\n8\n', 0, True),
    ('total=0; for n in 1 2 3; do total=$((total + n)); done; echo $total', '6\n', 0, True),
    ('x=12; echo $(( x % 5 == 2 ))', '1\n', 0, True),
    ('echo $(( 7 / -2 )) $(( 7 % -2 ))', '-3 1\n', 0, True),
    ('pct=$(( 100 * 3 / 7 )); echo "$pct%"', '42%\n', 0, True),
    ('echo $(( 0 ? 1 : 0 ? 2 : 3 ))', '3\n', 0, True),
    ('x=3; echo $(( x == 3 ? x++ : x-- )) $x', '3 4\n', 0, True),
    ('echo $(( 1 && 0 || 1 )) $(( 1 | 2 ^ 3 & 4 ))', '1 3\n', 0, True),
    ("echo $(( 'a' ))", '', 1, False),
    ('x=010; echo $(( x + 1 ))', '9\n', 0, True),
    ('x=1e3; echo $(( x ))', '', 1, False),
    ('declare -i i=0; i+=5; i=i*2; echo $i', '10\n', 0, True),
    ('echo $(( 16#FF )) $(( 16#ff )) $(( 36#Z ))', '255 255 35\n', 0, True),
    ('echo $(( 3 > 2 > 1 ))', '0\n', 0, True),
    ('v=$(( 1 + 2 )); echo "$v" $(( v ))', '3 3\n', 0, True),
    ('echo $(( ${#PWD} > 0 ))', '1\n', 0, True),
    ('echo $((x=3, y=x+1, x*y))', '12\n', 0, True),
    ('let i++; echo $i; let ++i; echo $i', '1\n2\n', 0, True),
    ("let 'x = 2 * (3 + 4)'; echo $x", '14\n', 0, True),
    ('(( big = 1 << 62 )); echo $big', '4611686018427387904\n', 0, True),
]


@pytest.mark.parametrize("script,stdout,exit_code,quiet", CASES_MatchesBash)
def test_matches_bash(script, stdout, exit_code, quiet):
    check(script, stdout, exit_code, quiet)

