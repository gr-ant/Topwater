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
]


class ReviewFixTests(unittest.TestCase):
    def test_cases(self):
        for cmd, expected in CASES:
            with self.subTest(cmd=cmd):
                sh = Shell()
                sh.run(SETUP)
                self.assertEqual(expected, sh.run(cmd).to_tool_output())


if __name__ == "__main__":
    unittest.main()
