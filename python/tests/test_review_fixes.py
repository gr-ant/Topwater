"""Regression tests for behaviour that differed from real bash / GNU tools. Mirrored in ReviewFixTests.cs."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402

SETUP = (
    "printf 'apple 3\\nbanana 1\\ncherry 2\\napple 5\\n' > f.txt; printf 'b\\na\\nc\\nb\\n' > g.txt; "
    "printf 'x,y,z\\n1,2,3\\n4,5,6\\n' > c.csv; seq 12 > n.txt; printf 'noeol' > ne.txt; "
    "mkdir -p d/sub e; echo 'hello world' > d/a.txt; printf 'foo\\nbar\\n' > d/sub/b.log"
)

# (command, expected tool output: stdout + stderr + "[exit code N]")
CASES = [
    # --- shell syntax and expansion ---
    ("echo $'a\\tb' $'it\\'s' $'\\x41\\101\\u00e9' $\"q\"", "a\tb it's AAé q\n"),
    ("IFS=,; x='a,b,,c,'; printf '[%s]' $x; echo", "[a][b][][c]\n"),
    ("x=' a  b '; printf '[%s]' $x; echo", "[a][b]\n"),
    ("[[ abc =~ ^a(b|x)c$ ]] && echo m; [[ abc =~ \"a.c\" ]] || echo literal", "m\nliteral\n"),
    ("echo */; echo d/*/", "d/ e/\nd/sub/\n"),
    ("while IFS= read -r l; do echo \"[$l]\"; done <<< '  sp  '", "[  sp  ]\n"),
    ("IFS=: read x y <<< 'p:q:r'; echo $x $y", "p q:r\n"),
    ("printf 'a\\nb' | while read -r l; do echo $l; done; printf 'x' | { read -r v; echo \"$? $v\"; }", "a\n1 x\n"),
    ("[ 1 -eq 1 -a 2 -eq 2 ] && echo and; [ 1 -eq 2 -o -f f.txt ] && echo or; [ a \\< b ] && echo lt", "and\nor\nlt\n"),
    ("[ abc -eq 1 ]", "[: abc: integer expression expected\n[exit code 2]"),
    ("printf '%e %g %#x %#o %+d|%-4d|\\n' 1234.5 0.0001 255 8 5 7", "1.234500e+03 0.0001 0xff 010 +5|7   |\n"),
    ("printf '%q\\n' 'a b' \"it's\"; printf '%*d|%.*f\\n' 4 7 2 3.14159", "a\\ b\nit\\'s\n   7|3.14\n"),
    ("printf '%d\\n' abc 0x1f 010", "0\n31\n8\nprintf: abc: invalid number\n[exit code 1]"),
    ("echo -e '\\x41\\0102|\\c'; echo", "AB|\n"),
    # --- grep ---
    ("grep -e apple -e cherry f.txt", "apple 3\ncherry 2\napple 5\n"),
    ("grep -x 'apple 3' f.txt; grep -c '[[:digit:]]$' f.txt; grep '\\<ban' f.txt", "apple 3\n4\nbanana 1\n"),
    ("grep -r hello .; grep -r hello", "./d/a.txt:hello world\nd/a.txt:hello world\n"),
    ("grep apple f.txt nope", "f.txt:apple 3\nf.txt:apple 5\ngrep: nope: No such file or directory\n[exit code 2]"),
    ("grep -o 'x*' f.txt; grep --color=never -m1 apple f.txt", "apple 3\n"),
    # --- head / tail / wc / cut / tr / tee / cat ---
    ("head -n -10 n.txt; tail -n 1 ne.txt; echo; head -c -18 n.txt", "1\n2\nnoeol\n1\n2\n3\n4\n5"),
    ("head -n abc n.txt", "head: invalid number of lines: 'abc'\n[exit code 1]"),
    ("wc f.txt; wc -l f.txt n.txt; wc -l < f.txt", " 4  8 34 f.txt\n 4 f.txt\n12 n.txt\n16 total\n4\n"),
    ("cut --complement -c1 f.txt | head -1; cut -d' ' -f1,2 --output-delimiter=: f.txt | head -1", "pple 3\napple:3\n"),
    ("cut -d, -f0 c.csv", "cut: fields and positions are numbered from 1\n[exit code 1]"),
    ("echo 'hello, world' | tr -cd 'a-z\\n'; echo 'a b' | tr -c 'a-z\\n' _; echo 'aa  b' | tr -s ' '", "helloworld\na_b\naa b\n"),
    ("echo hi | tee /dev/stderr 2>&1 >/dev/null", "hi\n"),
    ("printf 'a\\tb\\n\\n\\nc' | cat -sA", "a^Ib$\n$\nc"),
    # --- sort ---
    ("sort -k1,1 -k2,2nr f.txt", "apple 5\napple 3\nbanana 1\ncherry 2\n"),
    ("printf '1K\\n2M\\n512\\n' | sort -h; printf 'a10\\na2\\na1\\n' | sort -V", "512\n1K\n2M\na1\na2\na10\n"),
    ("printf 'k1 9\\nk2 1\\nk1 3\\n' | sort -k1,1 -u; printf 'b 2\\na 2\\nc 1\\n' | sort -k2,2n -s", "k1 9\nk2 1\nc 1\nb 2\na 2\n"),
    ("sort -c g.txt", "sort: g.txt:2: disorder: a\n[exit code 1]"),
    # --- sed ---
    ("sed -n '/apple/{p;=}' f.txt", "apple 3\n1\napple 5\n4\n"),
    ("printf 'a\\nb\\nc\\n' | sed ':a;N;$!ba;s/\\n/,/g'; printf 'a\\nb\\nc\\n' | sed -n '1!G;h;$p'", "a,b,c\nc\nb\na\n"),
    ("echo hello | sed 's/l/L/2'; echo aaa | sed 's/a/b/2g'; echo abc | sed 's/b*/X/g'", "helLo\nabb\nXaXcX\n"),
    ("sed 'y/abc/xyz/' g.txt | head -2; sed '0~4d' n.txt | wc -l; sed '/banana/,+1d' f.txt", "y\nx\n9\napple 3\napple 5\n"),
    ("echo 'hello world' | sed -E 's/(\\w+) (\\w+)/\\U\\2\\E \\u\\1/'", "WORLD Hello\n"),
    ("sed -i.bak 's/a/A/' g.txt; cat g.txt g.txt.bak", "b\nA\nc\nb\nb\na\nc\nb\n"),
    ("sed '2q' n.txt; sed -n '$=' n.txt; printf 'x' | sed p", "1\n2\n12\nx\nx"),
    ("sed 's/[[:space:]]*$//;s/^/>/' f.txt | head -1; sed '/apple/s//PEAR/' f.txt | head -1", ">apple 3\nPEAR 3\n"),
    ("sed -e 1i\\\\top -e '$a\\' -e end g.txt", "top\nb\na\nc\nb\nend\n"),
    ("sed 's/x/\\1/' f.txt", "sed: invalid reference \\1 on `s' command's RHS\n[exit code 1]"),
    # --- jq (was a string-splitting subset: no object construction, interpolation, sort_by, //, arithmetic...) ---
    ("echo '[{\"n\":\"a\",\"v\":2},{\"n\":\"b\",\"v\":1}]' | jq -c 'sort_by(.v) | map({name: .n, double: (.v * 2)})'",
     '[{"name":"b","double":2},{"name":"a","double":4}]\n'),
    ("echo '{\"a\":null,\"items\":[{\"t\":\"x\"},{\"t\":\"y\"},{\"t\":\"x\"}]}' | jq -r '.a // \"none\", "
     "(.items | group_by(.t) | map(\"\\(.[0].t)=\\(length)\") | join(\",\"))'", "none\nx=2,y=1\n"),
    ("jq -nc --arg x hi --argjson n 5 '{x: $x, n: ($n + 1)} | .n += 1 | del(.x)'", '{"n":7}\n'),
    ("jq -nc '[range(5)] | map(select(. % 2 == 0)) | [first, last, length, (add / length)]'", "[0,4,3,2]\n"),
    ("jq -nc 'reduce (1,2,3) as $i (0; . + $i), (1 | [limit(3; repeat(. * 2))]), (\"a-b\" | split(\"-\") | to_entries | from_entries)'",
     '6\n[1,2,4]\n{"0":"a","1":"b"}\n'),
    ("jq -nr '\"2024-05\" | capture(\"(?<y>\\\\d+)-(?<m>\\\\d+)\") | \"\\(.m)/\\(.y)\", ([1,\"a\"] | @csv), (\"x y\" | @uri)'",
     '05/2024\n1,"a"\nx%20y\n'),
    ("jq -nc '{a:{b:[1,2]}} | [paths], (.a.b[0] |= . + 10), (try error(\"boom\") catch .), (.x.y // 3)'",
     '[["a"],["a","b"],["a","b",0],["a","b",1]]\n{"a":{"b":[11,2]}}\n"boom"\n3\n'),
    ("jq -n '0.1 + 0.2, 1e-5, 3/2, 1e17'", "0.30000000000000004\n1e-05\n1.5\n1e+17\n"),
    ("echo '{\"a\":1}' | jq '.a.b'", "jq: error (at <unknown>): Cannot index number with \"b\"\n[exit code 5]"),
    ("jq -n 'foo(1)'", "jq: error: foo/1 is not defined\njq: 1 compile error\n[exit code 3]"),
    ("echo '{\"a\":1} {\"a\":2}' | jq -e '.a > 1'", "false\ntrue\n"),
    # --- arithmetic is 64-bit and wraps (2**N used to hang the C# host and grow without bound in Python) ---
    ("echo $((2**64)) $((3**40)) $((1<<70)) $((99999999999999999999)) $((2**1000000000000))",
     "0 -6289078614652622815 64 7766279631452241919 0\n"),
    ("echo $(( (-9223372036854775807-1) / -1 ))", "-9223372036854775808\n"),
    # --- $(< file), xargs, find, date, seq, paste, type ---
    ("x=$(< f.txt); echo \"$x\" | wc -l", "4\n"),
    ("echo \"'a b' c\" | xargs -n1 echo; printf 'a b\\nc\\n' | xargs -L1 echo; echo a b | xargs -t echo 2>&1",
     "a b\nc\na b\nc\necho a b\na b\n"),
    ("echo a | xargs nope; echo $?", "127\nnope: command not found (run 'help' to list available commands)\n"),
    ("find d -printf '%p %s %y %f %P\\n' | sort", "d 4096 d d \nd/a.txt 12 f a.txt a.txt\nd/sub 4096 d sub sub\nd/sub/b.log 8 f b.log sub/b.log\n"),
    ("find . -path ./d -prune -o -name '*.txt' -print | sort; find . -name '*.txt' -print -quit | wc -l",
     "./f.txt\n./g.txt\n./n.txt\n./ne.txt\n1\n"),
    ("date -d @0; date -d '2024-01-31 10:30 1 month' '+%F %T %A %-d %s'; date -d '3 days ago' +%Z",
     "Thu Jan  1 00:00:00 UTC 1970\n2024-03-02 10:30:00 Saturday 2 1709375400\nUTC\n"),
    ("seq -f '%03g' 3; seq 0 0.1 0.3 | tail -1; seq 6 | paste - - -", "001\n002\n003\n0.3\n1\t2\t3\n4\t5\t6\n"),
    ("type cd ls; type -t echo; command -v cd", "cd is a shell builtin\nls is /usr/bin/ls\nbuiltin\ncd\n"),
    # --- the null command, and tr's fast path ---
    (": > f.txt; wc -c < f.txt; : ${NEWVAR:=dflt}; echo $NEWVAR; while :; do break; done; type :", "0\ndflt\n: is a shell builtin\n"),
    ("echo 'hello' | tr a-y b-z; echo 'hello' | tr -d l", "ifmmp\nheo\n"),
    ("for i in 1 2; do echo $i >> ap.txt; cat ap.txt; done; echo x > ap.txt; echo y >> ap.txt; cat ap.txt; wc -c ap.txt",
     "1\n1\n2\nx\ny\n4 ap.txt\n"),
    # --- set -u, trap EXIT, shopt, printf -v, cd --, cp/mv ---
    ("set -u; echo ${x:-d} \"$@\" ok; echo $undefined; echo never", "d ok\nbash: undefined: unbound variable\n[exit code 127]"),
    ("set -u; (echo $y); echo after", "after\nbash: y: unbound variable\n"),
    ("trap 'echo cleanup' EXIT; echo work; bash -c 'trap \"echo inner\" EXIT; exit 3'; echo \"code $?\"",
     "work\ninner\ncode 3\ncleanup\n"),
    ("shopt -s nullglob; echo *.none end; shopt -s globstar; echo **/*.log; echo d/**", "end\nd/sub/b.log\nd/ d/a.txt d/sub d/sub/b.log\n"),
    ("printf -v v '%03d' 7; echo $v; cd -- d && pwd | sed 's#.*/##'", "007\nd\n"),
    ("cp -v f.txt d/; mv -v g.txt g2.txt; cp f.txt nodir/", "'f.txt' -> 'd/f.txt'\nrenamed 'g.txt' -> 'g2.txt'\n"
     "cp: cannot create regular file 'nodir/': Not a directory\n[exit code 1]"),
    ("cp f.txt /dev/null; wc -c < /dev/null", "0\n"),
    ("tail -f -n 1 f.txt; tail -F g.txt | wc -l", "apple 5\n4\n"),
    ("echo never; in", "bash: syntax error near unexpected token `in'\n[exit code 2]"),
    # --- awk: RS, CONVFMT, big integers, POSIX classes, functions, getline ---
    ("printf 'a b\\nc\\n\\nd\\n' | awk -v RS= '{ print NR \": \" NF }'; echo 'x;y' | awk -v RS=';' '{ print NR, $0 }'",
     "1: 3\n2: 1\n1 x\n2 y\n\n"),
    ("awk 'BEGIN { CONVFMT = \"%.2f\"; x = 3.14159; print (x \"\"), 2^53, 1e16 }'", "3.14 9007199254740992 10000000000000000\n"),
    ("awk '/[[:digit:]]$/ { n++ } END { print n }' f.txt; echo 'a.b' | awk '/a[.]b/'", "4\na.b\n"),
    ("awk 'function fact(n) { return n <= 1 ? 1 : n * fact(n - 1) } "
     "function fill(arr, n,   i) { for (i = 1; i <= n; i++) arr[i] = i * i } "
     "BEGIN { print fact(10); fill(sq, 3); print sq[3], length(sq) }'", "3628800\n9 3\n"),
    ("awk 'BEGIN { while ((getline line < \"g.txt\") > 0) n++; print n } { getline x; print $1 \"|\" x }' f.txt",
     "4\napple|banana 1\ncherry|apple 5\n"),
]


class ReviewFixTests(unittest.TestCase):
    def test_loop_limit_counts_body_runs(self):
        sh = Shell(max_loop_iterations=3)
        self.assertEqual("1\n2\n3\n", sh.run("printf '1\\n2\\n3\\n' | while read -r l; do echo $l; done").to_tool_output())
        self.assertIn("loop aborted after 3 iterations", sh.run("while true; do :; done").stderr)

    def test_cases(self):
        for cmd, expected in CASES:
            with self.subTest(cmd=cmd):
                sh = Shell()
                sh.run(SETUP)
                self.assertEqual(expected, sh.run(cmd).to_tool_output())


if __name__ == "__main__":
    unittest.main()
