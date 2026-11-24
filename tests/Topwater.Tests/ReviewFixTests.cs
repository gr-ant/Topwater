namespace Topwater.Tests;

/// <summary>Regression tests for behaviour that differed from real bash / GNU tools. Mirrors python/tests/test_review_fixes.py.</summary>
public class ReviewFixTests
{
    const string Setup = "printf 'apple 3\\nbanana 1\\ncherry 2\\napple 5\\n' > f.txt; printf 'b\\na\\nc\\nb\\n' > g.txt; printf 'x,y,z\\n1,2,3\\n4,5,6\\n' > c.csv; seq 12 > n.txt; printf 'noeol' > ne.txt; mkdir -p d/sub e; echo 'hello world' > d/a.txt; printf 'foo\\nbar\\n' > d/sub/b.log";

    [Theory]
    [InlineData("echo $'a\\tb' $'it\\'s' $'\\x41\\101\\u00e9' $\"q\"", "a\tb it's AA\u00e9 q\n")]
    [InlineData("IFS=,; x='a,b,,c,'; printf '[%s]' $x; echo", "[a][b][][c]\n")]
    [InlineData("x=' a  b '; printf '[%s]' $x; echo", "[a][b]\n")]
    [InlineData("[[ abc =~ ^a(b|x)c$ ]] && echo m; [[ abc =~ \"a.c\" ]] || echo literal", "m\nliteral\n")]
    [InlineData("echo */; echo d/*/", "d/ e/\nd/sub/\n")]
    [InlineData("while IFS= read -r l; do echo \"[$l]\"; done <<< '  sp  '", "[  sp  ]\n")]
    [InlineData("IFS=: read x y <<< 'p:q:r'; echo $x $y", "p q:r\n")]
    [InlineData("printf 'a\\nb' | while read -r l; do echo $l; done; printf 'x' | { read -r v; echo \"$? $v\"; }", "a\n1 x\n")]
    [InlineData("[ 1 -eq 1 -a 2 -eq 2 ] && echo and; [ 1 -eq 2 -o -f f.txt ] && echo or; [ a \\< b ] && echo lt", "and\nor\nlt\n")]
    [InlineData("[ abc -eq 1 ]", "[: abc: integer expression expected\n[exit code 2]")]
    [InlineData("printf '%e %g %#x %#o %+d|%-4d|\\n' 1234.5 0.0001 255 8 5 7", "1.234500e+03 0.0001 0xff 010 +5|7   |\n")]
    [InlineData("printf '%q\\n' 'a b' \"it's\"; printf '%*d|%.*f\\n' 4 7 2 3.14159", "a\\ b\nit\\'s\n   7|3.14\n")]
    [InlineData("printf '%d\\n' abc 0x1f 010", "0\n31\n8\nprintf: abc: invalid number\n[exit code 1]")]
    [InlineData("echo -e '\\x41\\0102|\\c'; echo", "AB|\n")]
    [InlineData("grep -e apple -e cherry f.txt", "apple 3\ncherry 2\napple 5\n")]
    [InlineData("grep -x 'apple 3' f.txt; grep -c '[[:digit:]]$' f.txt; grep '\\<ban' f.txt", "apple 3\n4\nbanana 1\n")]
    [InlineData("grep -r hello .; grep -r hello", "./d/a.txt:hello world\nd/a.txt:hello world\n")]
    [InlineData("grep apple f.txt nope", "f.txt:apple 3\nf.txt:apple 5\ngrep: nope: No such file or directory\n[exit code 2]")]
    [InlineData("grep -o 'x*' f.txt; grep --color=never -m1 apple f.txt", "apple 3\n")]
    [InlineData("head -n -10 n.txt; tail -n 1 ne.txt; echo; head -c -18 n.txt", "1\n2\nnoeol\n1\n2\n3\n4\n5")]
    [InlineData("head -n abc n.txt", "head: invalid number of lines: 'abc'\n[exit code 1]")]
    [InlineData("wc f.txt; wc -l f.txt n.txt; wc -l < f.txt", " 4  8 34 f.txt\n 4 f.txt\n12 n.txt\n16 total\n4\n")]
    [InlineData("cut --complement -c1 f.txt | head -1; cut -d' ' -f1,2 --output-delimiter=: f.txt | head -1", "pple 3\napple:3\n")]
    [InlineData("cut -d, -f0 c.csv", "cut: fields and positions are numbered from 1\n[exit code 1]")]
    [InlineData("echo 'hello, world' | tr -cd 'a-z\\n'; echo 'a b' | tr -c 'a-z\\n' _; echo 'aa  b' | tr -s ' '", "helloworld\na_b\naa b\n")]
    [InlineData("echo hi | tee /dev/stderr 2>&1 >/dev/null", "hi\n")]
    [InlineData("printf 'a\\tb\\n\\n\\nc' | cat -sA", "a^Ib$\n$\nc")]
    [InlineData("sort -k1,1 -k2,2nr f.txt", "apple 5\napple 3\nbanana 1\ncherry 2\n")]
    [InlineData("printf '1K\\n2M\\n512\\n' | sort -h; printf 'a10\\na2\\na1\\n' | sort -V", "512\n1K\n2M\na1\na2\na10\n")]
    [InlineData("printf 'k1 9\\nk2 1\\nk1 3\\n' | sort -k1,1 -u; printf 'b 2\\na 2\\nc 1\\n' | sort -k2,2n -s", "k1 9\nk2 1\nc 1\nb 2\na 2\n")]
    [InlineData("sort -c g.txt", "sort: g.txt:2: disorder: a\n[exit code 1]")]
    [InlineData("sed -n '/apple/{p;=}' f.txt", "apple 3\n1\napple 5\n4\n")]
    [InlineData("printf 'a\\nb\\nc\\n' | sed ':a;N;$!ba;s/\\n/,/g'; printf 'a\\nb\\nc\\n' | sed -n '1!G;h;$p'", "a,b,c\nc\nb\na\n")]
    [InlineData("echo hello | sed 's/l/L/2'; echo aaa | sed 's/a/b/2g'; echo abc | sed 's/b*/X/g'", "helLo\nabb\nXaXcX\n")]
    [InlineData("sed 'y/abc/xyz/' g.txt | head -2; sed '0~4d' n.txt | wc -l; sed '/banana/,+1d' f.txt", "y\nx\n9\napple 3\napple 5\n")]
    [InlineData("echo 'hello world' | sed -E 's/(\\w+) (\\w+)/\\U\\2\\E \\u\\1/'", "WORLD Hello\n")]
    [InlineData("sed -i.bak 's/a/A/' g.txt; cat g.txt g.txt.bak", "b\nA\nc\nb\nb\na\nc\nb\n")]
    [InlineData("sed '2q' n.txt; sed -n '$=' n.txt; printf 'x' | sed p", "1\n2\n12\nx\nx")]
    [InlineData("sed 's/[[:space:]]*$//;s/^/>/' f.txt | head -1; sed '/apple/s//PEAR/' f.txt | head -1", ">apple 3\nPEAR 3\n")]
    [InlineData("sed -e 1i\\\\top -e '$a\\' -e end g.txt", "top\nb\na\nc\nb\nend\n")]
    [InlineData("sed 's/x/\\1/' f.txt", "sed: invalid reference \\1 on `s' command's RHS\n[exit code 1]")]
    public async Task MatchesBash(string cmd, string expected)
    {
        var shell = new Shell();
        await shell.RunAsync(Setup);
        Assert.Equal(expected, (await shell.RunAsync(cmd)).ToToolOutput());
    }
}
