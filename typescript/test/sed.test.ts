import { describe, expect, test } from 'vitest';
import { makeEnv } from './harness.js';
import { registerText } from '../src/commands/text.js';

function env() {
  const e = makeEnv();
  registerText(e.shell as any); // registers sed
  return e;
}

const t = (name: string, argv: string[], stdin: string, out: string, code = 0) =>
  test(name, async () => {
    const { run } = env();
    const r = await run(['sed', ...argv], stdin);
    expect(r.stdout).toBe(out);
    expect(r.exitCode).toBe(code);
  });

const L = 'one\ntwo\nthree\nfour\nfive\n';

describe('sed substitution', () => {
  t('basic', ['s/o/0/'], 'foo\n', 'f0o\n');
  t('global', ['s/o/0/g'], 'foo\n', 'f00\n');
  t('nth', ['s/o/0/2'], 'foo\n', 'fo0\n');
  t('nth and g', ['s/a/X/2g'], 'aaaa\n', 'aXXX\n');
  t('ampersand', ['s/b/[&]/'], 'abc\n', 'a[b]c\n');
  t('escaped ampersand', ['s/b/\\&/'], 'abc\n', 'a&c\n');
  t('BRE groups', ['s/\\(a\\)\\(b\\)/\\2\\1/'], 'ab\n', 'ba\n');
  t('ERE groups', ['-E', 's/(a)(b)/\\2\\1/'], 'ab\n', 'ba\n');
  t('-r', ['-r', 's/a+/X/'], 'caaat\n', 'cXt\n');
  t('BRE literal plus and parens', ['s/a+(b)/X/'], 'a+(b)\n', 'X\n');
  t('BRE \\+ \\?', ['s/a\\+b\\?/X/'], 'aaab\n', 'X\n');
  t('ERE interval', ['-E', 's/a{2}/X/'], 'aaa\n', 'Xa\n');
  t('BRE interval', ['s/a\\{2\\}/X/'], 'aaa\n', 'Xa\n');
  t('alternation', ['-E', 's/cat|dog/pet/g'], 'cat dog\n', 'pet pet\n');
  t('custom delimiter', ['s|/usr|/opt|'], '/usr/bin\n', '/opt/bin\n');
  t('escaped delimiter', ['s/a\\/b/X/'], 'a/b\n', 'X\n');
  t('ignore case flag', ['s/HELLO/x/I'], 'hello\n', 'x\n');
  t('i flag', ['s/HELLO/x/i'], 'hello\n', 'x\n');
  t('p flag with -n', ['-n', 's/a/b/p'], 'a\nc\n', 'b\n');
  t('newline in replacement', ['s/,/\\n/g'], 'a,b\n', 'a\nb\n');
  t('\\n in regex after N', ['N;s/\\n/+/'], 'a\nb\n', 'a+b\n');
  t('tab in replacement', ['s/,/\\t/'], 'a,b\n', 'a\tb\n');
  t('empty regex reuses last', ['/a/s//X/'], 'cat\n', 'cXt\n');
  t('case conversion upper', ['s/\\w\\+/\\U&/'], 'abc def\n', 'ABC def\n');
  t('case conversion first', ['s/\\(.\\)\\(.*\\)/\\u\\1\\2/'], 'abc\n', 'Abc\n');
  t('\\U \\E', ['-E', 's/(a)(b)(c)/\\U\\1\\E\\2\\U\\3/'], 'abc\n', 'AbC\n');
  t('\\L', ['s/.*/\\L&/'], 'ABC\n', 'abc\n');
  t('empty matches', ['s/b*/X/g'], 'abc\n', 'XaXcX\n');
  t('anchors', ['s/^/> /'], 'a\nb\n', '> a\n> b\n');
  t('dollar', ['s/$/!/'], 'a\n', 'a!\n');
  t('bracket class', ['s/[[:digit:]]\\+/N/g'], 'a12b3\n', 'aNbN\n');
  t('bracket negation', ['s/[^a-c]/_/g'], 'abcdef\n', 'abc___\n');
  t('dot matches', ['s/a.c/X/'], 'abc\n', 'X\n');
  t('star at start is literal', ['s/*/X/'], 'a*b\n', 'aXb\n');
  t('word boundary', ['s/\\bcat\\b/dog/g'], 'cat concat cat\n', 'dog concat dog\n');
  t('\\< \\>', ['s/\\<a/X/g'], 'a ba a\n', 'X ba X\n');
  t('multiple -e', ['-e', 's/a/b/', '-e', 's/b/c/'], 'a\n', 'c\n');
  t('semicolons', ['s/a/b/;s/b/c/'], 'a\n', 'c\n');
  t('unmatched reference error', ['s/a/\\1/'], 'a\n', '', 1);
  t('m flag', ['N;s/^/>/mg'], 'a\nb\n', '>a\n>b\n');
  t('no trailing newline preserved', ['s/a/b/'], 'a', 'b');
});

describe('sed addresses and commands', () => {
  t('line number', ['2d'], L, 'one\nthree\nfour\nfive\n');
  t('last line', ['$d'], L, 'one\ntwo\nthree\nfour\n');
  t('regex address', ['/t/d'], L, 'one\nfour\nfive\n');
  t('custom regex delimiter', ['\\,t,d'], L, 'one\nfour\nfive\n');
  t('range', ['2,4d'], L, 'one\nfive\n');
  t('regex range', ['/two/,/four/d'], L, 'one\nfive\n');
  t('range to $', ['3,$d'], L, 'one\ntwo\n');
  t('negation', ['2!d'], L, 'two\n');
  t('step', ['0~2d'], L, 'one\nthree\nfive\n');
  t('first~step', ['1~2d'], L, 'two\nfour\n');
  t('addr,+N', ['2,+1d'], L, 'one\nfour\nfive\n');
  t('addr,~N', ['2,~4d'], L, 'one\nfive\n');
  t('0,/re/', ['0,/o/d'], L, 'two\nthree\nfour\nfive\n');
  t('1,/re/ does not end on line 1', ['1,/o/d'], L, 'three\nfour\nfive\n');
  t('range where end line < start', ['4,2d'], L, 'one\ntwo\nthree\nfive\n');
  t('regex flag I', ['/ONE/Id'], L, 'two\nthree\nfour\nfive\n');
  t('-n p', ['-n', '2,3p'], L, 'two\nthree\n');
  t('-n with $p', ['-n', '$p'], L, 'five\n');
  t('p duplicates', ['2p'], 'a\nb\n', 'a\nb\nb\n');
  t('block', ['-n', '2,3{p;p}'], L, 'two\ntwo\nthree\nthree\n');
  t('nested block', ['-n', '/t/{/w/p}'], L, 'two\n');
  t('block with negation', ['2!{s/^/-/}'], 'a\nb\nc\n', '-a\nb\n-c\n');
  t('block closing brace after command', ['-n', '/a/{p}'], 'a\nb\n', 'a\n');
  t('= prints line number', ['-n', '$='], L, '5\n');
  t('= each', ['='], 'a\nb\n', '1\na\n2\nb\n');
  t('y', ['y/abc/xyz/'], 'aabbcc\n', 'xxyyzz\n');
  t('y with escapes', ['y/,/\\n/'], 'a,b\n', 'a\nb\n');
  t('q', ['2q'], L, 'one\ntwo\n');
  t('q with exit code', ['q5'], L, 'one\n', 5);
  t('Q', ['2Q'], L, 'one\n');
  t('Q exit', ['Q3'], L, '', 3);
  t('a one-liner', ['2a hello'], 'x\ny\nz\n', 'x\ny\nhello\nz\n');
  t('a classic', ['2a\\\nhello'], 'x\ny\nz\n', 'x\ny\nhello\nz\n');
  t('a with leading whitespace kept via backslash', ['a\\  hi'], 'x\n', 'x\n  hi\n');
  t('a two lines', ['a\\\nl1\\\nl2'], 'x\n', 'x\nl1\nl2\n');
  t('i', ['2i hello'], 'x\ny\n', 'x\nhello\ny\n');
  t('c', ['2c changed'], 'x\ny\nz\n', 'x\nchanged\nz\n');
  t('c on range prints once', ['2,3c changed'], 'x\ny\nz\nw\n', 'x\nchanged\nw\n');
  t('c on $', ['$c end'], 'x\ny\n', 'x\nend\n');
  t('a then d still appends', ['a added\nd'], 'x\n', 'added\n');
  t('i before a', ['a A\ni I'], 'x\n', 'I\nx\nA\n');
  t('a at last line without newline', ['$a end'], 'x', 'x\nend\n');
  t('}-terminated a in block via -e', ['-e', '/x/{', '-e', 'a hi', '-e', '}'], 'x\n', 'x\nhi\n');
});

describe('sed hold space and flow', () => {
  t('tac', ['-n', '1!G;h;$p'], 'a\nb\nc\n', 'c\nb\na\n');
  t('x', ['x'], 'a\nb\n', '\na\n');
  t('G', ['G'], 'a\nb\n', 'a\n\nb\n\n');
  t('H and x', ['-n', 'H;${x;s/\\n/,/g;p}'], 'a\nb\nc\n', ',a,b,c\n');
  t('g', ['1h;2g'], 'a\nb\n', 'a\na\n');
  t('N join', ['N;s/\\n/ /'], 'a\nb\nc\nd\n', 'a b\nc d\n');
  t('N at EOF prints', ['N;s/\\n/ /'], 'a\nb\nc\n', 'a b\nc\n');
  t('N loop join all', [':a;N;$!ba;s/\\n/,/g'], 'a\nb\nc\n', 'a,b,c\n');
  t('P and D', ['$!N;P;D'], 'a\nb\nc\n', 'a\nb\nc\n');
  t('delete duplicates', ['$!N;/^\\(.*\\)\\n\\1$/!P;D'], 'a\na\nb\nb\nc\n', 'a\nb\nc\n');
  t('n', ['n;d'], 'a\nb\nc\nd\n', 'a\nc\n');
  t('n at EOF', ['n;d'], 'a\nb\nc\n', 'a\nc\n');
  t('b label', ['b end;s/a/X/;:end'], 'a\n', 'a\n');
  t('b to end of script', ['/a/b;s/$/!/'], 'a\nb\n', 'a\nb!\n');
  t('t', ['s/a/X/;t;s/$/!/'], 'a\nb\n', 'X\nb!\n');
  t('t loop', [':a;s/^\\(x*\\)a/\\1x/;ta'], 'aaab\n', 'xxxb\n');
  t('T', ['s/a/X/;T;s/$/!/'], 'a\nb\n', 'X!\nb\n');
  t('z', ['z;s/^$/E/'], 'a\n', 'E\n');
  t('l', ['-n', 'l'], 'a\tb\\\n', 'a\\tb\\\\$\n');
  t('l wraps', ['-n', 'l'], 'x'.repeat(100) + '\n', 'x'.repeat(69) + '\\\n' + 'x'.repeat(31) + '$\n');
  t('l non-printable', ['-n', 'l'], 'a\x01\n', 'a\\001$\n');
  t('comment', ['# hi\ns/a/b/'], 'a\n', 'b\n');
  t('whitespace before command', ['2 d'], L, 'one\nthree\nfour\nfive\n');
  t('empty script', [''], 'a\n', 'a\n');
  t('empty input', ['p'], '', '');
});

describe('sed files', () => {
  test('files and multiple inputs share line numbers', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/a', '1\n2\n');
    fs.writeText('/home/agent/b', '3\n4\n');
    expect((await run(['sed', '-n', '3p', 'a', 'b'])).stdout).toBe('3\n');
    expect((await run(['sed', '-n', '$p', 'a', 'b'])).stdout).toBe('4\n');
  });
  test('-s treats files separately', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/a', '1\n2\n');
    fs.writeText('/home/agent/b', '3\n4\n');
    expect((await run(['sed', '-s', '-n', '$p', 'a', 'b'])).stdout).toBe('2\n4\n');
  });
  test('file without trailing newline then another', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/a', 'x');
    fs.writeText('/home/agent/b', 'y\n');
    expect((await run(['sed', 's/^/-/', 'a', 'b'])).stdout).toBe('-x\n-y\n');
  });
  test('-i edits in place', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/f', 'hello\nworld\n');
    const r = await run(['sed', '-i', 's/o/0/g', 'f']);
    expect(r.stdout).toBe('');
    expect(fs.readText('/home/agent/f')).toBe('hell0\nw0rld\n');
  });
  test('-i with suffix keeps a backup', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/f', 'a\n');
    await run(['sed', '-i.bak', 's/a/b/', 'f']);
    expect(fs.readText('/home/agent/f')).toBe('b\n');
    expect(fs.readText('/home/agent/f.bak')).toBe('a\n');
  });
  test('-i with -n and p', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/f', 'a\nb\n');
    await run(['sed', '-n', '-i', '2p', 'f']);
    expect(fs.readText('/home/agent/f')).toBe('b\n');
  });
  test('-i without files fails', async () => {
    const { run } = env();
    const r = await run(['sed', '-i', 's/a/b/']);
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toBe('sed: no input files\n');
  });
  test('-f script file', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/s.sed', 's/a/b/\ns/b/c/\n');
    expect((await run(['sed', '-f', 's.sed'], 'a\n')).stdout).toBe('c\n');
  });
  test('missing file', async () => {
    const { run } = env();
    const r = await run(['sed', 'p', 'nope']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toBe('sed: can\'t read nope: No such file or directory\n');
  });
  test('w file and r file', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/ins', 'INSERTED\n');
    const r = await run(['sed', '-n', '/b/w out.txt\n/a/r ins\n/a/p'], 'a\nb\n');
    expect(r.stdout).toBe('a\nINSERTED\n');
    expect(fs.readText('/home/agent/out.txt')).toBe('b\n');
  });
  test('s///w /dev/stdout', async () => {
    const { run } = env();
    const r = await run(['sed', '-n', 's/a/b/w /dev/stdout'], 'a\n');
    expect(r.stdout).toBe('b\n');
  });
  test('stdin via -', async () => {
    const { run } = env();
    expect((await run(['sed', 's/a/b/', '-'], 'a\n')).stdout).toBe('b\n');
  });
  test('--expression and --quiet long options', async () => {
    const { run } = env();
    expect((await run(['sed', '--quiet', '--expression=2p'], 'a\nb\n')).stdout).toBe('b\n');
  });
  test('-E combined flags -nE', async () => {
    const { run } = env();
    expect((await run(['sed', '-nE', 's/(a)/[\\1]/p'], 'a\n')).stdout).toBe('[a]\n');
  });
});

describe('sed errors and limits', () => {
  const bad = (name: string, script: string, msg: RegExp, exitCode = 1) =>
    test(name, async () => {
      const { run } = env();
      const r = await run(['sed', script], 'a\n');
      expect(r.exitCode).toBe(exitCode);
      expect(r.stderr).toMatch(msg);
    });
  bad('unknown command', 'k', /unknown command/);
  bad('unterminated s', 's/a/b', /unterminated `s' command/);
  bad('unmatched {', '{p', /unmatched `\{'/);
  bad('unexpected }', 'p}', /unexpected `\}'/);
  bad('missing label', 'b nowhere', /can't find label for jump to `nowhere'/, 4);
  bad('extra characters', 'pp', /extra characters after command/);
  bad('y lengths', 'y/ab/c/', /different lengths/);
  bad('invalid regex', 's/\\(/x/', /char 7: Unmatched \( or \\\(/);
  bad('zero address', '0p', /invalid usage of line address 0/);
  test('no script', async () => {
    const { run } = env();
    const r = await run(['sed']);
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toMatch(/^Usage: sed \[OPTION\]/);
  });
  test('infinite loop is stopped', async () => {
    const { run } = env();
    const r = await run(['sed', ':a;ba'], 'x\n');
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toBe('sed: too many steps (a branch loops forever?)\n');
  });
  test('time limit interrupts', async () => {
    const { run } = env();
    await expect(run(['sed', ':a;ba'], 'x\n', 5)).rejects.toThrow();
  });
  test('pattern space limit', async () => {
    const { shell, run } = env();
    shell.options.maxStringBytes = 1000;
    await expect(run(['sed', ':a;s/^/xxxxxxxx/;ba'], 'x\n')).rejects.toThrow(/sed: pattern space too large \(limit 1000 bytes\); process the input in smaller pieces/);
    await expect(run(['sed', ':a;N;ba'], 'x'.repeat(400) + '\n' + 'y'.repeat(400) + '\n' + 'z'.repeat(400) + '\n')).rejects.toThrow(/pattern space too large/);
  });
});
