import { describe, expect, test } from 'vitest';
import { makeEnv } from './harness.js';
import { registerAwk } from '../src/commands/awk.js';

function env() {
  const e = makeEnv();
  registerAwk(e.shell as any);
  return e;
}

const t = (name: string, argv: string[], stdin: string, out: string, code = 0) =>
  test(name, async () => {
    const { run } = env();
    const r = await run(['awk', ...argv], stdin);
    expect(r.stdout).toBe(out);
    expect(r.exitCode).toBe(code);
  });

describe('awk basics', () => {
  t('print field', ['{print $2}'], 'a b c\nd e f\n', 'b\ne\n');
  t('-F colon', ['-F:', '{print $2}'], 'a:b\n', 'b\n');
  t('-F tab escape', ['-F\\t', '{print $2}'], 'a b\tc d\n', 'c d\n');
  t('-F regex', ['-F[,;]', '{print $3}'], 'a,b;c\n', 'c\n');
  t('-v', ['-v', 'x=5', 'BEGIN{print x+1}'], '', '6\n');
  t('NR NF', ['{print NR, NF}'], 'a b\nc\n\n', '1 2\n2 1\n3 0\n');
  t('pattern', ['/b/'], 'abc\nxyz\n', 'abc\n');
  t('numeric pattern', ['$1 > 2'], '1\n3\n10\n', '3\n10\n');
  t('range', ['NR==2,NR==3'], '1\n2\n3\n4\n', '2\n3\n');
  t('BEGIN END', ['BEGIN{print "s"} {n++} END{print n}'], 'a\nb\n', 's\n2\n');
  t('sum', ['{s+=$1} END{print s}'], '1\n2\n3.5\n', '6.5\n');
  t('OFS rebuild', ['BEGIN{OFS="-"}{$1=$1; print}'], 'a b c\n', 'a-b-c\n');
  t('assign field extends', ['{$5="x"; print; print NF}'], 'a b\n', 'a b   x\n5\n');
  t('NF assign', ['{NF=2; print}'], 'a b c d\n', 'a b\n');
  t('$NF', ['{print $NF, $(NF-1)}'], 'a b c\n', 'c b\n');
  t('string compare vs number', ['{print ($1 == 10)}'], '10.0\n', '1\n');
  t('strnum text', ['{print ($1 < $2)}'], 'abc abd\n', '1\n');
  t('uninit', ['BEGIN{print x+0, "[" x "]", length(x)}'], '', '0 [] 0\n');
  t('printf', ['BEGIN{printf "%5.2f|%-4d|%04d|%s|%c|%x|%e\\n", 3.14159, 42, 7, "hi", 65, 255, 1234.5}'], '', ' 3.14|42  |0007|hi|A|ff|1.234500e+03\n');
  t('printf %c string', ['BEGIN{printf "%c%c\\n", "hello", 66}'], '', 'hB\n');
  t('printf %5s %.2s', ['BEGIN{printf "[%5s][%.2s][%-5s]\\n", "ab", "xyz", "q"}'], '', '[   ab][xy][q    ]\n');
  t('printf %*d', ['BEGIN{printf "%*d|\\n", 5, 42}'], '', '   42|\n');
  t('printf %g', ['BEGIN{printf "%g %g %g\\n", 100000, 1000000, 0.0001}'], '', '100000 1e+06 0.0001\n');
  t('printf %.0f ties', ['BEGIN{printf "%.0f %.0f %.2f\\n", 0.5, 2.5, 0.125}'], '', '0 2 0.12\n');
  t('printf %d negative', ['BEGIN{printf "%d %i\\n", -3.9, "12abc"}'], '', '-3 12\n');
  t('%%', ['BEGIN{printf "100%%\\n"}'], '', '100%\n');
  t('number output', ['BEGIN{print 0.1+0.2, 1e6, 1/3, 100/3, 2^53, 1e30, -0}'], '', '0.3 1000000 0.333333 33.3333 9007199254740992 1000000000000000019884624838656 0\n');
  t('CONVFMT', ['BEGIN{CONVFMT="%.2g"; a=3.14159; b=a ""; print b}'], '', '3.1\n');
  t('OFMT', ['BEGIN{OFMT="%.2f"; print 3.14159, 3.14159 ""}'], '', '3.14 3.14159\n');
  t('integer division', ['BEGIN{print int(7/2), 7%3, -7%3, 2**3, 2^0.5}'], '', '3 1 -1 8 1.41421\n');
  t('increments', ['BEGIN{i=5; print i++, i, ++i, i--, --i}'], '', '5 6 7 7 5\n');
  t('ternary and logic', ['BEGIN{x=3; print (x>2 ? "big" : "small"), (1 && 0), (1 || 0), !x}'], '', 'big 0 1 0\n');
  t('concat', ['BEGIN{a="x"; b=a "y" 1+2; print b}'], '', 'xy3\n');
  t('regex match op', ['$1 ~ /^a/ {print "A:" $0} $1 !~ /^a/ {print "N:" $0}'], 'ab\nba\n', 'A:ab\nN:ba\n');
  t('dynamic regex', ['BEGIN{r="^[0-9]+$"} $0 ~ r'], '12\nab\n', '12\n');
  t('regex bracket class', ['/[[:digit:]]+/{print "d"}'], 'a1\nb\n', 'd\n');
  t('regex interval', ['/^a{2}$/'], 'aa\naaa\n', 'aa\n');
  t('regex escaped slash', ['/a\\/b/'], 'a/b\n', 'a/b\n');
  t('length forms', ['{print length, length($0), length()}'], 'abc\n', '3 3 3\n');
  t('exit code', ['{exit 3}'], 'a\n', '', 3);
  t('exit in BEGIN runs END', ['BEGIN{print "a"; exit 1} END{print "end"}'], '', 'a\nend\n', 1);
  t('next', ['/x/{next} {print}'], 'x\ny\n', 'y\n');
  t('stdout redirect names', ['BEGIN{print "a" > "/dev/stdout"}'], '', 'a\n');
});

describe('awk strings and functions', () => {
  t('substr', ['BEGIN{s="hello"; print substr(s,2,3), substr(s,0), substr(s,-1,3), substr(s,4), substr(s,2,100), "[" substr(s,6) "]"}'], '', 'ell hello h lo ello []\n');
  t('index', ['BEGIN{print index("hello","ll"), index("a","z")}'], '', '3 0\n');
  t('split', ['BEGIN{n=split("a:b:c",arr,":"); print n, arr[1], arr[3]; n=split("",e); print n}'], '', '3 a c\n0\n');
  t('split regex', ['BEGIN{n=split("a1b22c",arr,/[0-9]+/); print n, arr[2], arr[3]}'], '', '3 b c\n');
  t('split default and empty sep', ['BEGIN{n=split("  a  b ",x); m=split("abc",y,""); print n, m, y[2]}'], '', '2 3 b\n');
  t('sub', ['{n=sub(/o/,"0"); print n, $0}'], 'foo\n', '1 f0o\n');
  t('gsub', ['{n=gsub(/o/,"0"); print n, $0}'], 'foo\n', '2 f00\n');
  t('gsub &', ['{gsub(/[aeiou]/,"<&>"); print}'], 'hello\n', 'h<e>ll<o>\n');
  t('gsub escaped &', ['{gsub(/o/,"\\\\&"); print}'], 'foo\n', 'f&&\n');
  t('gsub empty matches', ['BEGIN{s="abc"; gsub(/x*/,"-",s); print s; t="abc"; gsub(/b*/,"-",t); print t}'], '', '-a-b-c-\n-a-c-\n');
  t('gsub on field', ['{gsub(/a/,"b",$2); print}'], 'a a a\n', 'a b a\n');
  t('gsub anchors', ['{gsub(/^ +| +$/,""); print "[" $0 "]"}'], '  hi  \n', '[hi]\n');
  t('match', ['BEGIN{print match("foobar",/o+/), RSTART, RLENGTH; print match("x",/z/), RSTART, RLENGTH}'], '', '2 2 2\n0 0 -1\n');
  t('sprintf', ['BEGIN{s=sprintf("%03d-%s",5,"x"); print s}'], '', '005-x\n');
  t('case', ['BEGIN{print toupper("aBc"), tolower("aBc")}'], '', 'ABC abc\n');
  t('math', ['BEGIN{printf "%.4f %.4f %.4f %.4f %.4f\\n", sqrt(2), exp(1), log(10), sin(1), atan2(1,1)*4}'], '', '1.4142 2.7183 2.3026 0.8415 3.1416\n');
  t('rand deterministic range', ['BEGIN{srand(1); x=rand(); print (x>=0 && x<1)}'], '', '1\n');
  t('srand returns previous', ['BEGIN{srand(5); print srand(7)}'], '', '5\n');
  t('user function', ['function f(a,b){return a*b} BEGIN{print f(3,4)}'], '', '12\n');
  t('recursion', ['function fib(n){return n<2?n:fib(n-1)+fib(n-2)} BEGIN{print fib(15)}'], '', '610\n');
  t('array by reference', ['function fill(a){a["x"]=1; a["y"]=2} BEGIN{fill(arr); print length(arr), arr["y"]}'], '', '2 2\n');
  t('locals', ['function f(x,  loc){loc=x*2; return loc} BEGIN{loc="g"; print f(2), loc}'], '', '4 g\n');
  t('function missing return', ['function f(){x=1} BEGIN{v=f(); print "[" v "]"}'], '', '[]\n');
  t('func keyword', ['func f(){return 1} BEGIN{print f()}'], '', '1\n');
  t('uninit array param becomes array', ['function f(a){a[1]=5} function g(b){f(b); return b[1]} BEGIN{print g(z)}'], '', '5\n');
});

describe('awk arrays', () => {
  t('count words', ['{c[$1]++} END{for(k in c) print k, c[k]}'], 'a\nb\na\n', 'a 2\nb 1\n');
  t('in operator', ['BEGIN{a["x"]=1; print ("x" in a), ("y" in a)}'], '', '1 0\n');
  t('in does not create', ['BEGIN{if ("y" in a) print "no"; print length(a)}'], '', '0\n');
  t('delete', ['BEGIN{a[1];a[2]; delete a[1]; print length(a); delete a; print length(a)}'], '', '1\n0\n');
  t('multi-dim', ['BEGIN{a[1,2]=3; for(k in a){split(k,p,SUBSEP); print p[1], p[2]}; print ((1,2) in a)}'], '', '1 2\n1\n');
  t('ENVIRON', ['BEGIN{print ENVIRON["HOME"]}'], '', '/home/agent\n');
  t('ARGV', ['BEGIN{print ARGC, ARGV[1]}', 'foo'], '', '2 foo\n');
  t('referencing creates', ['BEGIN{x=a["k"]; print length(a)}'], '', '1\n');
});

describe('awk control flow', () => {
  t('for', ['BEGIN{for(i=1;i<=3;i++) s=s i; print s}'], '', '123\n');
  t('while break continue', ['BEGIN{while(1){i++; if(i<3) continue; if(i>5) break; s=s i}; print s}'], '', '345\n');
  t('do while', ['BEGIN{do {i++} while(i<5); print i}'], '', '5\n');
  t('nested if else', ['BEGIN{x=2; if(x==1) print "a"; else if(x==2) print "b"; else print "c"}'], '', 'b\n');
  t('multiline program', ['BEGIN {\n  if (1)\n    print "y"\n  else\n    print "n"\n}\n'], '', 'y\n');
  t('semicolon rules', ['BEGIN{a=1};END{print a}'], 'x\n', '1\n');
  t('comments', ['# c\nBEGIN{print 1 # trailing\n}'], '', '1\n');
  t('line continuation', ['BEGIN{print 1 + \\\n 2}'], '', '3\n');
  t('newline after &&', ['BEGIN{if (1 &&\n 1) print "ok"}'], '', 'ok\n');
  t('for in with delete', ['BEGIN{a[1];a[2];a[3]; for(k in a) delete a[k]; print length(a)}'], '', '0\n');
  t('regex literal after paren', ['BEGIN{x="ab"; if (x ~ /b/) print "y"; print 6 / 2 / 3}'], '', 'y\n1\n');
});

describe('awk records and getline', () => {
  t('RS single char', ['BEGIN{RS=";"}{print NR ":" $0}'], 'a;b;c', '1:a\n2:b\n3:c\n');
  t('RS paragraph', ['BEGIN{RS=""}{print NR ": " $1 "/" $NF "/" NF}'], 'a b\nc\n\n\nd e\n', '1: a/c/3\n2: d/e/2\n');
  t('RS paragraph with FS', ['BEGIN{RS="";FS=":"}{print NF}'], 'a:b\nc:d\n', '4\n');
  t('RS regex', ['BEGIN{RS="[0-9]+"}{print}'], 'a1b22c', 'a\nb\nc\n');
  t('ORS', ['BEGIN{ORS="|"}{print}END{printf "\\n"}'], 'a\nb\n', 'a|b|\n');
  t('getline plain', ['NR==1{getline; print "got " $0}'], 'a\nb\nc\n', 'got b\n');
  t('getline var', ['NR==1{getline x; print x, NR}'], 'a\nb\n', 'b 2\n');
  t('getline return at EOF', ['BEGIN{while ((getline l) > 0) n++; print n}'], 'a\nb\n', '2\n');
  t('no trailing newline', ['{print}'], 'a\nb', 'a\nb\n');
  t('empty input END', ['END{print NR}'], '', '0\n');
  t('NR in END and $0', ['END{print NR, $0}'], 'a\nb\n', '2 b\n');
  t('FS single space string', ['BEGIN{FS=" "}{print $2}'], '  a   b\n', 'b\n');
  t('FS single char literal', ['BEGIN{FS="|"}{print $2}'], 'a|b|c\n', 'b\n');
  t('FS empty splits chars', ['BEGIN{FS=""}{print $2, NF}'], 'abc\n', 'b 3\n');
  t('tab FS keeps empties', ['-F\\t', '{print NF}'], 'a\t\tb\n', '3\n');
});

describe('awk files', () => {
  test('file operands, FILENAME, FNR', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/a', '1\n2\n');
    fs.writeText('/home/agent/b', '3\n');
    const r = await run(['awk', '{print FILENAME, FNR, NR}', 'a', 'b']);
    expect(r.stdout).toBe('a 1 1\na 2 2\nb 1 3\n');
  });
  test('assignment operand between files', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/a', 'x\n');
    const r = await run(['awk', '{print v, $0}', 'v=1', 'a', 'v=2', 'a']);
    expect(r.stdout).toBe('1 x\n2 x\n');
  });
  test('missing file', async () => {
    const { run } = env();
    const r = await run(['awk', '{print}', 'nope']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toBe('awk: cannot open "nope" (No such file or directory)\n');
  });
  test('-f program files, several', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/p1.awk', 'function f(x){return x+1}\n');
    fs.writeText('/home/agent/p2.awk', 'BEGIN{print f(1)}\n');
    const r = await run(['awk', '-f', 'p1.awk', '-f', 'p2.awk']);
    expect(r.stdout).toBe('2\n');
  });
  test('print > file, >> file, close and getline < file', async () => {
    const { fs, run } = env();
    const r = await run(['awk', 'BEGIN{print "a" > "o.txt"; print "b" > "o.txt"; close("o.txt"); print "c" >> "o.txt"; close("o.txt"); while ((getline l < "o.txt") > 0) print "got " l}']);
    expect(r.stdout).toBe('got a\ngot b\ngot c\n');
    expect(fs.readText('/home/agent/o.txt')).toBe('a\nb\nc\n');
  });
  test('> truncates once per run; reopen after close truncates', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/o', 'old\n');
    await run(['awk', 'BEGIN{print "x" > "o"; print "y" > "o"}']);
    expect(fs.readText('/home/agent/o')).toBe('x\ny\n');
  });
  test('print > /dev/stderr', async () => {
    const { run } = env();
    const r = await run(['awk', 'BEGIN{print "e" > "/dev/stderr"}']);
    expect(r.stderr).toBe('e\n');
    expect(r.stdout).toBe('');
  });
  test('getline < missing file is -1', async () => {
    const { run } = env();
    const r = await run(['awk', 'BEGIN{print (getline x < "nope")}']);
    expect(r.stdout).toBe('-1\n');
  });
  test('stdin as - operand', async () => {
    const { run } = env();
    const r = await run(['awk', '{print}', '-'], 'z\n');
    expect(r.stdout).toBe('z\n');
  });
});

describe('awk errors and limits', () => {
  test('syntax error', async () => {
    const { run } = env();
    const r = await run(['awk', 'BEGIN{print (}']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toMatch(/^awk: syntax error/);
  });
  test('division by zero is inf or -nan, as in mawk', async () => {
    const { run } = env();
    const r = await run(['awk', 'BEGIN{print 1/0, -1/0, 5%0; printf "%d %g\\n", 1/0, 0/0}']);
    expect([r.exitCode, r.stdout, r.stderr]).toEqual([0, 'inf -inf -nan\n9200000000000000000 -nan\n', '']);
  });
  test('usage', async () => {
    const { run } = env();
    expect((await run(['awk'])).exitCode).toBe(2);
  });
  test('system and pipes unsupported', async () => {
    const { run } = env();
    expect((await run(['awk', 'BEGIN{system("ls")}'])).stderr).toMatch(/not supported/);
    expect((await run(['awk', 'BEGIN{print "a" | "cat"}'])).stderr).toMatch(/not supported/);
    expect((await run(['awk', 'BEGIN{"ls" | getline x}'])).stderr).toMatch(/not supported/);
  });
  test('loop iteration limit', async () => {
    const { run } = env();
    const r = await run(['awk', 'BEGIN{while(1) i++}'], '', Infinity); // no call deadline: only the iteration limit can stop it
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toBe('awk: loop iteration limit exceeded\n');
  }, 30_000); // generous: a loaded machine runs slow
  test('time limit interrupts', async () => {
    const { run } = env();
    await expect(run(['awk', 'BEGIN{for(i=0;i<9000000;i++) for(j=0;j<1;j++) x=x+1}'], '', 20)).rejects.toThrow();
  });
  test('string limit', async () => {
    const { shell, run } = env();
    shell.options.maxStringBytes = 1000;
    await expect(run(['awk', 'BEGIN{s="x"; for(i=0;i<20;i++) s = s s}'])).rejects.toThrow(/awk: string too large \(limit 1000 bytes\); build smaller strings or print as you go/);
    await expect(run(['awk', 'BEGIN{printf "%5000d", 1}'])).rejects.toThrow(/too large/);
  });
  test('deep recursion is an error, not a crash', async () => {
    const { run } = env();
    const r = await run(['awk', 'function f(n){return f(n+1)} BEGIN{f(1)}']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toMatch(/nesting too deep/);
  });
  test('deeply nested expression is an error, not a crash', async () => {
    const { run } = env();
    const prog = 'BEGIN{print ' + '('.repeat(200000) + '1' + ')'.repeat(200000) + '}';
    const r = await run(['awk', prog]);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toMatch(/nesting too deep/);
  });
  test('moderate recursion works', async () => {
    const { run } = env();
    const r = await run(['awk', 'function s(n){return n<=0?0:n+s(n-1)} BEGIN{print s(300)}']);
    expect(r.stdout).toBe('45150\n');
  });
});
