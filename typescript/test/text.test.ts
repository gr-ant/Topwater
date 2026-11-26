import { describe, expect, test } from 'vitest';
import { makeEnv } from './harness.js';
import { registerText } from '../src/commands/text.js';
import { StdinBuffer, type CommandContext, type CommandHandler } from '../src/command.js';
import { decodeBytes } from '../src/bytes.js';
import { Shell } from '../src/index.js';

function env() {
  const e = makeEnv();
  registerText(e.shell as any);
  return e;
}

const t = (name: string, argv: string[], stdin: string, out: string, code = 0) =>
  test(name, async () => {
    const { run } = env();
    const r = await run(argv, stdin);
    expect(r.stdout).toBe(out);
    expect(r.exitCode).toBe(code);
  });

describe('grep', () => {
  t('basic', ['grep', 'b'], 'abc\nxyz\nbcd\n', 'abc\nbcd\n');
  t('no match exit 1', ['grep', 'q'], 'abc\n', '', 1);
  t('-i', ['grep', '-i', 'ABC'], 'abc\n', 'abc\n');
  t('-v', ['grep', '-v', 'a'], 'a\nb\n', 'b\n');
  t('-c', ['grep', '-c', 'a'], 'a\nb\na\n', '2\n');
  t('-n', ['grep', '-n', 'a'], 'a\nb\na\n', '1:a\n3:a\n');
  t('-o', ['grep', '-o', '[0-9]*'], 'a12b345\n', '12\n345\n');
  t('-w', ['grep', '-w', 'cat'], 'cat\nconcat\ncat dog\n', 'cat\ncat dog\n');
  t('-x', ['grep', '-x', 'cat'], 'cat\ncat dog\n', 'cat\n');
  t('-F literal', ['grep', '-F', 'a.b'], 'a.b\naxb\n', 'a.b\n');
  t('BRE vs -E', ['grep', 'a|b'], 'a|b\na\n', 'a|b\n');
  t('-E alternation', ['grep', '-E', 'x|y'], 'x\ny\nz\n', 'x\ny\n');
  t('BRE \\| ', ['grep', 'x\\|y'], 'x\ny\nz\n', 'x\ny\n');
  t('multiple -e', ['grep', '-e', 'a', '-e', 'b'], 'a\nb\nc\n', 'a\nb\n');
  t('-e combined cluster', ['grep', '-ie', 'A'], 'a\n', 'a\n');
  t('pattern with newline', ['grep', 'a\nb'], 'a\nb\nc\n', 'a\nb\n');
  t('-q', ['grep', '-q', 'a'], 'a\n', '');
  t('context -A', ['grep', '-A1', 'b'], 'a\nb\nc\nd\n', 'b\nc\n');
  t('context -B', ['grep', '-B1', 'c'], 'a\nb\nc\nd\n', 'b\nc\n');
  t('context -C with separator', ['grep', '-C1', '-n', 'b\\|g'], 'a\nb\nc\nd\ne\nf\ng\n', '1-a\n2:b\n3-c\n--\n6-f\n7:g\n');
  t('-m', ['grep', '-m1', 'a'], 'a\na\n', 'a\n');
  t('-P digits', ['grep', '-P', '\\d+'], 'a1\nb\n', 'a1\n');
  t('-P \\K', ['grep', '-oP', 'a\\Kb'], 'ab\n', 'b\n');
  t('stdin name with -H', ['grep', '-H', 'a'], 'a\n', '(standard input):a\n');
  t('egrep-style bracket class', ['grep', '[[:digit:]]\\+'], 'a1\nb\n', 'a1\n');
  t('empty pattern matches all', ['grep', ''], 'a\nb\n', 'a\nb\n');
  t('bad regex', ['grep', '-E', '('], 'a\n', '', 2);
  test('files, -l -L -h and prefixes', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/a', 'x\ny\n');
    fs.writeText('/home/agent/b', 'y\n');
    expect((await run(['grep', 'y', 'a', 'b'])).stdout).toBe('a:y\nb:y\n');
    expect((await run(['grep', '-h', 'y', 'a', 'b'])).stdout).toBe('y\ny\n');
    expect((await run(['grep', '-l', 'x', 'a', 'b'])).stdout).toBe('a\n');
    expect((await run(['grep', '-L', 'x', 'a', 'b'])).stdout).toBe('b\n');
    expect((await run(['grep', '-c', 'y', 'a', 'b'])).stdout).toBe('a:1\nb:1\n');
  });
  test('missing file is exit 2, -s hides message', async () => {
    const { run } = env();
    const r = await run(['grep', 'a', 'nope']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toBe('grep: nope: No such file or directory\n');
    expect((await run(['grep', '-s', 'a', 'nope'])).stderr).toBe('');
  });
  test('-r with include/exclude', async () => {
    const { fs, run } = env();
    fs.mkdir('/home/agent/d/sub', true);
    fs.writeText('/home/agent/d/a.txt', 'hit\n');
    fs.writeText('/home/agent/d/b.log', 'hit\n');
    fs.writeText('/home/agent/d/sub/c.txt', 'hit\n');
    expect((await run(['grep', '-r', 'hit', 'd'])).stdout).toBe('d/a.txt:hit\nd/b.log:hit\nd/sub/c.txt:hit\n');
    expect((await run(['grep', '-r', '--include=*.txt', 'hit', 'd'])).stdout).toBe('d/a.txt:hit\nd/sub/c.txt:hit\n');
    expect((await run(['grep', '-r', '--exclude', '*.txt', 'hit', 'd'])).stdout).toBe('d/b.log:hit\n');
    expect((await run(['grep', '-r', '--exclude-dir=sub', 'hit', 'd'])).stdout).toBe('d/a.txt:hit\nd/b.log:hit\n');
    expect((await run(['grep', 'hit', 'd'])).stderr).toBe('grep: d: Is a directory\n');
  });
  test('-r without operand searches cwd with bare names', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/a.txt', 'hit\n');
    expect((await run(['grep', '-r', 'hit'])).stdout).toBe('a.txt:hit\n');
  });
  test('binary file', async () => {
    const { fs, run } = env();
    fs.writeBytes('/home/agent/b', Uint8Array.from([97, 0, 98, 10]));
    expect((await run(['grep', 'a', 'b'])).stdout).toBe('grep: b: binary file matches\n');
    expect((await run(['grep', '-a', 'a', 'b'])).stdout).toBe('a\0b\n');
  });
  test('-f pattern file', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/p', 'a\nb\n');
    expect((await run(['grep', '-f', 'p'], 'a\nc\nb\n')).stdout).toBe('a\nb\n');
  });
  test('egrep and fgrep', async () => {
    const { run } = env();
    expect((await run(['egrep', 'a|b'], 'a\nc\n')).stdout).toBe('a\n');
    expect((await run(['fgrep', 'a.'], 'a.\nab\n')).stdout).toBe('a.\n');
    expect((await run(['egrep', '('], 'a\n')).stderr).toMatch(/^grep: /);
  });
});

describe('head and tail', () => {
  const L = '1\n2\n3\n4\n5\n';
  t('head default', ['head'], '1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n', '1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n');
  t('head -n', ['head', '-n', '2'], L, '1\n2\n');
  t('head -2', ['head', '-2'], L, '1\n2\n');
  t('head -n -2', ['head', '-n', '-2'], L, '1\n2\n3\n');
  t('head -c', ['head', '-c', '3'], 'abcdef', 'abc');
  t('head -c -2', ['head', '-c', '-2'], 'abcdef', 'abcd');
  t('head no trailing newline', ['head', '-n', '5'], 'a\nb', 'a\nb');
  t('tail -n', ['tail', '-n', '2'], L, '4\n5\n');
  t('tail -n +3', ['tail', '-n', '+3'], L, '3\n4\n5\n');
  t('tail -3', ['tail', '-3'], L, '3\n4\n5\n');
  t('tail -c', ['tail', '-c', '3'], 'abcdef', 'def');
  t('tail -c +3', ['tail', '-c', '+3'], 'abcdef', 'cdef');
  t('tail no trailing newline', ['tail', '-n', '1'], 'a\nb', 'b');
  t('tail -f is accepted', ['tail', '-f', '-n', '1'], 'a\nb\n', 'b\n');
  t('tail -n 0', ['tail', '-n', '0'], L, '');
  test('multiple files get headers', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/a', '1\n2\n');
    fs.writeText('/home/agent/b', '3\n');
    expect((await run(['head', '-n', '1', 'a', 'b'])).stdout).toBe('==> a <==\n1\n\n==> b <==\n3\n');
    expect((await run(['head', '-q', '-n', '1', 'a', 'b'])).stdout).toBe('1\n3\n');
  });
  test('invalid count', async () => {
    const { run } = env();
    const r = await run(['head', '-n', 'x'], 'a\n');
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toBe('head: invalid number of lines: \u2018x\u2019\n');
  });
  test('binary head -c survives', async () => {
    const { run } = env();
    const r = await run(['head', '-c', '2'], decodeBytes(Uint8Array.from([0xff, 0x80, 0x41])));
    expect(r.stdout).toBe(decodeBytes(Uint8Array.from([0xff, 0x80])));
  });
});

describe('wc', () => {
  t('stdin all', ['wc'], 'a b\nc\n', '      2       3       6\n');
  t('-l', ['wc', '-l'], 'a\nb\n', '2\n');
  t('-w', ['wc', '-w'], ' a  b \n', '2\n');
  t('-c unicode', ['wc', '-c'], 'é\n', '3\n');
  t('-m unicode', ['wc', '-m'], 'é\n', '2\n');
  t('-L', ['wc', '-L'], 'abc\nde\n', '3\n');
  test('files and total', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/a', 'x\ny\n');
    fs.writeText('/home/agent/b', 'z\n');
    expect((await run(['wc', 'a', 'b'])).stdout).toBe('2 2 4 a\n1 1 2 b\n3 3 6 total\n');
    expect((await run(['wc', '-l', 'a'])).stdout).toBe('2 a\n');
    expect((await run(['wc', '-l', 'nope'])).exitCode).toBe(1);
  });
});

describe('sort', () => {
  t('plain', ['sort'], 'b\na\nc\n', 'a\nb\nc\n');
  t('C locale order', ['sort'], 'a\nB\n_\n1\n', '1\nB\n_\na\n');
  t('-r', ['sort', '-r'], 'a\nb\n', 'b\na\n');
  t('-n', ['sort', '-n'], '10\n9\n-1\nx\n2\n', '-1\nx\n2\n9\n10\n');
  t('-nr', ['sort', '-nr'], '1\n10\n2\n', '10\n2\n1\n');
  t('-u', ['sort', '-u'], 'a\nb\na\n', 'a\nb\n');
  t('-f', ['sort', '-f'], 'b\nA\na\nB\n', 'A\na\nB\nb\n');
  t('-fu', ['sort', '-fu'], 'a\nA\nb\n', 'a\nb\n');
  t('-k2', ['sort', '-k2'], 'x 3\ny 1\nz 2\n', 'y 1\nz 2\nx 3\n');
  t('-k2n', ['sort', '-k2n'], 'a 10\nb 9\n', 'b 9\na 10\n');
  t('-nk2 cluster', ['sort', '-nk2'], 'a 10\nb 9\n', 'b 9\na 10\n');
  t('-k2,2 -k1,1', ['sort', '-k2,2', '-k1,1'], 'b 1\na 1\nc 0\n', 'c 0\na 1\nb 1\n');
  t('-t and -k', ['sort', '-t,', '-k2'], 'a,z\nb,y\n', 'b,y\na,z\n');
  t('-t: -k3 -n', ['sort', '-t:', '-k3', '-n'], 'a:b:10\nc:d:2\n', 'c:d:2\na:b:10\n');
  t('-k with char offset', ['sort', '-k1.2'], 'xb\nya\n', 'ya\nxb\n');
  t('-k1.2,1.2', ['sort', '-k1.2,1.2'], 'ab\naa\nba\n', 'aa\nba\nab\n');
  t('per-key r', ['sort', '-k1,1', '-k2,2nr'], 'a 1\na 2\nb 1\n', 'a 2\na 1\nb 1\n');
  t('-k with -b', ['sort', '-b', '-k2'], 'x   b\ny a\n', 'y a\nx   b\n');
  t('-h', ['sort', '-h'], '1G\n2M\n3K\n', '3K\n2M\n1G\n');
  t('-V', ['sort', '-V'], 'a10\na2\na1\n', 'a1\na2\na10\n');
  t('-M', ['sort', '-M'], 'MAR\nJAN\nFEB\n', 'JAN\nFEB\nMAR\n');
  t('-g', ['sort', '-g'], '1e2\n5\n-3\n', '-3\n5\n1e2\n');
  t('-d', ['sort', '-d'], 'a-c\nab\n', 'ab\na-c\n');
  t('-s keeps input order', ['sort', '-s', '-k1,1'], 'a 2\nb 1\na 1\n', 'a 2\na 1\nb 1\n');
  t('-c ok', ['sort', '-c'], 'a\nb\n', '');
  t('-c disorder', ['sort', '-c'], 'b\na\n', '', 1);
  t('-z', ['sort', '-z'], 'b\0a\0', 'a\0b\0');
  t('empty', ['sort'], '', '');
  t('no trailing newline', ['sort'], 'b\na', 'a\nb\n');
  test('-o writes file, may be the input', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/f', 'b\na\n');
    const r = await run(['sort', '-o', 'f', 'f']);
    expect(r.stdout).toBe('');
    expect(fs.readText('/home/agent/f')).toBe('a\nb\n');
  });
  test('missing file', async () => {
    const { run } = env();
    const r = await run(['sort', 'nope']);
    expect(r.exitCode).toBe(2);
  });
  test('multi-char tab', async () => {
    const { run } = env();
    expect((await run(['sort', '-t', 'ab'], 'a\n')).exitCode).toBe(2);
  });
});

describe('uniq', () => {
  t('basic', ['uniq'], 'a\na\nb\na\n', 'a\nb\na\n');
  t('-c', ['uniq', '-c'], 'a\na\nb\n', '      2 a\n      1 b\n');
  t('-d', ['uniq', '-d'], 'a\na\nb\n', 'a\n');
  t('-u', ['uniq', '-u'], 'a\na\nb\n', 'b\n');
  t('-i', ['uniq', '-i'], 'a\nA\n', 'a\n');
  t('-f 1', ['uniq', '-f', '1'], 'x a\ny a\nz b\n', 'x a\nz b\n');
  t('-s 1', ['uniq', '-s', '1'], 'xa\nya\nzb\n', 'xa\nzb\n');
  t('-w 1', ['uniq', '-w', '1'], 'ab\nac\nbd\n', 'ab\nbd\n');
});

describe('cut', () => {
  t('-d -f', ['cut', '-d:', '-f2'], 'a:b:c\n', 'b\n');
  t('-f list', ['cut', '-d:', '-f1,3'], 'a:b:c\n', 'a:c\n');
  t('-f range', ['cut', '-d:', '-f2-'], 'a:b:c\n', 'b:c\n');
  t('-f -N', ['cut', '-d:', '-f-2'], 'a:b:c\n', 'a:b\n');
  t('default tab', ['cut', '-f2'], 'a\tb\n', 'b\n');
  t('no delimiter line passes', ['cut', '-d:', '-f2'], 'abc\n', 'abc\n');
  t('-s drops them', ['cut', '-s', '-d:', '-f2'], 'abc\na:b\n', 'b\n');
  t('-c', ['cut', '-c1-3'], 'abcdef\n', 'abc\n');
  t('-c list', ['cut', '-c1,3'], 'abc\n', 'ac\n');
  t('-c unicode', ['cut', '-c2'], 'aéb\n', 'é\n');
  t('-b bytes', ['cut', '-b1-2'], 'abc\n', 'ab\n');
  t('--complement', ['cut', '--complement', '-d:', '-f2'], 'a:b:c\n', 'a:c\n');
  t('--output-delimiter', ['cut', '-d:', '-f1,3', '--output-delimiter=-'], 'a:b:c\n', 'a-c\n');
  t('-c with output delimiter', ['cut', '-c1,3', '--output-delimiter=|'], 'abc\n', 'a|c\n');
  t('field past end', ['cut', '-d:', '-f5'], 'a:b\n', '\n');
  t('zero field', ['cut', '-f0'], 'a\n', '', 1);
  t('no list', ['cut'], 'a\n', '', 1);
});

describe('tr', () => {
  t('translate', ['tr', 'abc', 'xyz'], 'aabbcc\n', 'xxyyzz\n');
  t('range', ['tr', 'a-z', 'A-Z'], 'hello\n', 'HELLO\n');
  t('class', ['tr', '[:lower:]', '[:upper:]'], 'abc\n', 'ABC\n');
  t('-d', ['tr', '-d', 'l'], 'hello\n', 'heo\n');
  t('-d class', ['tr', '-d', '[:digit:]'], 'a1b2\n', 'ab\n');
  t('-s', ['tr', '-s', ' '], 'a   b  c\n', 'a b c\n');
  t('-c -d', ['tr', '-cd', '0-9'], 'a1b2\n', '12');
  t('-c translate', ['tr', '-c', 'a-z', '_'], 'ab1\n', 'ab__');
  t('set2 shorter repeats last', ['tr', 'abc', 'x'], 'abc\n', 'xxx\n');
  t('escapes', ['tr', '\\n', ' '], 'a\nb\n', 'a b ');
  t('tab escape', ['tr', '\\t', ','], 'a\tb\n', 'a,b\n');
  t('octal', ['tr', '\\101', 'x'], 'A\n', 'x\n');
  t('-s with translate', ['tr', '-s', 'a-z', 'A-Z'], 'aabb\n', 'AB\n');
  t('-ds', ['tr', '-ds', 'a', 'b'], 'aabbb\n', 'b\n');
  t('literal dash at end', ['tr', 'a-', 'x'], 'a-\n', 'xx\n');
  t('delete newlines', ['tr', '-d', '\\n'], 'a\nb\n', 'ab');
  t('rot13', ['tr', 'A-Za-z', 'N-ZA-Mn-za-m'], 'Hello\n', 'Uryyb\n');
  t('missing operand', ['tr'], '', '', 1);
  t('one operand without -d', ['tr', 'a'], 'a\n', '', 1);
});

describe('tee', () => {
  test('writes and passes through', async () => {
    const { fs, run } = env();
    const r = await run(['tee', 'o1', 'o2'], 'hi\n');
    expect(r.stdout).toBe('hi\n');
    expect(fs.readText('/home/agent/o1')).toBe('hi\n');
    expect(fs.readText('/home/agent/o2')).toBe('hi\n');
  });
  test('-a appends', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/o', 'a\n');
    await run(['tee', '-a', 'o'], 'b\n');
    expect(fs.readText('/home/agent/o')).toBe('a\nb\n');
  });
  test('bad path reports and exits 1', async () => {
    const { run } = env();
    const r = await run(['tee', '/nonexistent/x'], 'a\n');
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toMatch(/^tee: \/nonexistent\/x: /);
    expect(r.stdout).toBe('a\n');
  });
});

describe('xargs', () => {
  /** Runs xargs with a recording fake for ctx.invoke. */
  async function xargs(args: string[], stdin: string, rc = (_: string[]) => 0) {
    const e = env();
    let handler!: CommandHandler;
    (e.shell as any).register = (n: string, h: CommandHandler) => { if (n === 'xargs') handler = h; };
    (e.shell as any).locate = (n: string) => '/usr/bin/' + n; // every command exists, so xargs runs it
    registerText(e.shell as any);
    const calls: string[][] = [];
    let err = '';
    const ctx = {
      shell: e.shell, name: 'xargs', args, fs: e.fs, stdin: new StdinBuffer(stdin), stdinText: stdin,
      stdout: { write() {} }, stderr: { write: (s: string) => { err += s; } },
      readLine: () => null, resolvePath: (p: string) => p, checkTime() {}, remainingMs: () => 1e9,
      invoke: async (argv: string[]) => { calls.push(argv); return rc(argv); },
    } as unknown as CommandContext;
    let code: number;
    try { code = ((await handler(ctx)) as number) ?? 0; } catch (x) { err += (x as Error).message; code = (x as any).exitCode ?? 1; }
    return { calls, code, err };
  }
  test('one call with all items', async () => { expect((await xargs(['echo'], 'a b\nc\n')).calls).toEqual([['echo', 'a', 'b', 'c']]); });
  test('default command is echo', async () => { expect((await xargs([], 'a\n')).calls).toEqual([['echo', 'a']]); });
  test('-n', async () => { expect((await xargs(['-n', '2', 'echo'], 'a b c\n')).calls).toEqual([['echo', 'a', 'b'], ['echo', 'c']]); });
  test('-n1', async () => { expect((await xargs(['-n1', 'echo'], 'a b\n')).calls).toEqual([['echo', 'a'], ['echo', 'b']]); });
  test('-I', async () => { expect((await xargs(['-I', '{}', 'cp', '{}', '{}.bak'], 'a\nb\n')).calls).toEqual([['cp', 'a', 'a.bak'], ['cp', 'b', 'b.bak']]); });
  test('-I{} attached', async () => { expect((await xargs(['-I{}', 'echo', '<{}>'], 'a b\n')).calls).toEqual([['echo', '<a b>']]); });
  test('-0', async () => { expect((await xargs(['-0', 'echo'], 'a b\0c\0')).calls).toEqual([['echo', 'a b', 'c']]); });
  test('-d', async () => { expect((await xargs(['-d', ',', 'echo'], 'a b,c\n')).calls).toEqual([['echo', 'a b', 'c\n']]); });
  test('-d newline', async () => { expect((await xargs(['-d', '\\n', 'echo'], 'a b\nc\n')).calls).toEqual([['echo', 'a b', 'c']]); });
  test('-L', async () => { expect((await xargs(['-L', '1', 'echo'], 'a b\nc\n')).calls).toEqual([['echo', 'a', 'b'], ['echo', 'c']]); });
  test('quotes and backslashes', async () => { expect((await xargs(['echo'], `'a b' "c d" e\\ f\n`)).calls).toEqual([['echo', 'a b', 'c d', 'e f']]); });
  test('unmatched quote', async () => { expect((await xargs(['echo'], `'a\n`)).err).toMatch(/unmatched single quote/); });
  test('empty input runs once, -r does not', async () => {
    expect((await xargs(['echo'], '')).calls).toEqual([['echo']]);
    expect((await xargs(['-r', 'echo'], '')).calls).toEqual([]);
  });
  test('-t echoes the command', async () => { expect((await xargs(['-t', 'echo'], 'a\n')).err).toBe('echo a\n'); });
  test('exit statuses', async () => {
    expect((await xargs(['x'], 'a\n', () => 1)).code).toBe(123);
    expect((await xargs(['x'], 'a\n', () => 127)).code).toBe(127);
    expect((await xargs(['x'], 'a\n', () => 255)).code).toBe(124);
  });
  test('-P ignored', async () => { expect((await xargs(['-P', '4', '-n', '1', 'echo'], 'a b\n')).calls.length).toBe(2); });
});

describe('nl tac rev paste column', () => {
  t('nl', ['nl'], 'a\n\nb\n', '     1\ta\n\n     2\tb\n');
  t('nl -ba', ['nl', '-ba'], 'a\n\nb\n', '     1\ta\n     2\t\n     3\tb\n');
  t('nl -w -s', ['nl', '-w3', '-s:'], 'a\n', '  1:a\n');
  t('nl -v -i', ['nl', '-v', '10', '-i', '5'], 'a\nb\n', '    10\ta\n    15\tb\n');
  t('nl -n rz', ['nl', '-n', 'rz', '-w', '3'], 'a\n', '001\ta\n');
  t('tac', ['tac'], 'a\nb\nc\n', 'c\nb\na\n');
  t('rev', ['rev'], 'abc\nxy\n', 'cba\nyx\n');
  t('rev unicode', ['rev'], 'aéb\n', 'béa\n');
  t('paste -d -s', ['paste', '-s', '-d,'], 'a\nb\nc\n', 'a,b,c\n');
  t('paste stdin dashes', ['paste', '-', '-'], '1\n2\n3\n4\n', '1\t2\n3\t4\n');
  t('paste -d multiple delims', ['paste', '-d', ',;', '-s'], 'a\nb\nc\nd\n', 'a,b;c,d\n');
  test('paste two files', async () => {
    const { fs, run } = env();
    fs.writeText('/home/agent/a', '1\n2\n3\n');
    fs.writeText('/home/agent/b', 'x\ny\n');
    expect((await run(['paste', 'a', 'b'])).stdout).toBe('1\tx\n2\ty\n3\t\n');
    expect((await run(['paste', '-d:', 'a', 'b'])).stdout).toBe('1:x\n2:y\n3:\n');
  });
  t('column -t', ['column', '-t'], 'a bbb\ncc d\n', 'a   bbb\ncc  d\n');
  t('column -t -s', ['column', '-t', '-s', ':'], 'a:bbb\ncc:d\n', 'a   bbb\ncc  d\n');
  t('column plain', ['column'], 'a\nb\n', 'a\nb\n');
});

describe('seq', () => {
  t('one arg', ['seq', '3'], '', '1\n2\n3\n');
  t('two args', ['seq', '2', '4'], '', '2\n3\n4\n');
  t('step', ['seq', '1', '2', '7'], '', '1\n3\n5\n7\n');
  t('negative step', ['seq', '3', '-1', '1'], '', '3\n2\n1\n');
  t('float step', ['seq', '0', '0.5', '2'], '', '0.0\n0.5\n1.0\n1.5\n2.0\n');
  t('float no drift', ['seq', '0', '0.1', '0.3'], '', '0.0\n0.1\n0.2\n0.3\n');
  t('empty range', ['seq', '5', '1'], '', '');
  t('-w', ['seq', '-w', '8', '10'], '', '08\n09\n10\n');
  t('-w negative', ['seq', '-w', '-1', '1'], '', '-1\n00\n01\n'.replace('-1\n00\n01', '-1\n00\n01'));
  t('-s', ['seq', '-s', ',', '3'], '', '1,2,3\n');
  t('-f', ['seq', '-f', '%03.0f', '2'], '', '001\n002\n');
  t('-f %g', ['seq', '-f', 'n%g', '2'], '', 'n1\nn2\n');
  t('negative operands', ['seq', '-2', '0'], '', '-2\n-1\n0\n');
  t('zero step', ['seq', '1', '0', '5'], '', '', 1);
  t('no operand', ['seq'], '', '', 1);
  t('too many numbers', ['seq', '2000000'], '', '', 1);
  test('too many numbers message', async () => {
    const { run } = env();
    expect((await run(['seq', '2000000'])).stderr).toBe('seq: too many numbers\n');
  });
});

describe('diff', () => {
  const setup = (a: string, b: string) => {
    const e = env();
    e.fs.writeText('/home/agent/a', a);
    e.fs.writeText('/home/agent/b', b);
    return e;
  };
  test('identical', async () => {
    const e = setup('x\n', 'x\n');
    expect(await e.run(['diff', 'a', 'b'])).toMatchObject({ stdout: '', exitCode: 0 });
    expect((await e.run(['diff', '-s', 'a', 'b'])).stdout).toBe('Files a and b are identical\n');
  });
  test('normal change', async () => {
    const e = setup('one\ntwo\nthree\n', 'one\nTWO\nthree\nfour\n');
    const r = await e.run(['diff', 'a', 'b']);
    expect(r.stdout).toBe('2c2\n< two\n---\n> TWO\n3a4\n> four\n');
    expect(r.exitCode).toBe(1);
  });
  test('normal delete and add', async () => {
    const e = setup('a\nb\nc\n', 'a\nc\n');
    expect((await e.run(['diff', 'a', 'b'])).stdout).toBe('2d1\n< b\n');
    const e2 = setup('a\nc\n', 'a\nb\nc\n');
    expect((await e2.run(['diff', 'a', 'b'])).stdout).toBe('1a2\n> b\n');
  });
  test('multi-line ranges', async () => {
    const e = setup('a\nb\nc\nd\n', 'a\nX\nY\nZ\n');
    expect((await e.run(['diff', 'a', 'b'])).stdout).toBe('2,4c2,4\n< b\n< c\n< d\n---\n> X\n> Y\n> Z\n');
  });
  test('unified', async () => {
    const e = setup('one\ntwo\nthree\n', 'one\nTWO\nthree\nfour\n');
    const r = await e.run(['diff', '-u', 'a', 'b']);
    expect(r.stdout).toBe('--- a\n+++ b\n@@ -1,3 +1,4 @@\n one\n-two\n+TWO\n three\n+four\n');
    expect(r.exitCode).toBe(1);
  });
  test('unified omits ,1 and splits hunks', async () => {
    const lines = Array.from({ length: 20 }, (_, i) => `l${i + 1}`);
    const changed = lines.map((l, i) => (i === 1 || i === 17 ? l.toUpperCase() : l));
    const e = setup(lines.join('\n') + '\n', changed.join('\n') + '\n');
    const out = (await e.run(['diff', '-u', 'a', 'b'])).stdout;
    expect(out).toContain('@@ -1,5 +1,5 @@');
    expect(out).toContain('@@ -15,6 +15,6 @@');
  });
  test('-U 0', async () => {
    const e = setup('a\nb\nc\n', 'a\nB\nc\n');
    expect((await e.run(['diff', '-U0', 'a', 'b'])).stdout).toBe('--- a\n+++ b\n@@ -2 +2 @@\n-b\n+B\n');
  });
  test('-q', async () => {
    const e = setup('a\n', 'b\n');
    expect(await e.run(['diff', '-q', 'a', 'b'])).toMatchObject({ stdout: 'Files a and b differ\n', exitCode: 1 });
  });
  test('-i -w -b', async () => {
    const e = setup('Hello  World\n', 'hello world\n');
    expect((await e.run(['diff', '-i', '-b', 'a', 'b'])).exitCode).toBe(0);
    expect((await e.run(['diff', '-w', 'a', 'b'])).exitCode).toBe(1);
  });
  test('stdin as -', async () => {
    const e = setup('x\n', '');
    expect((await e.run(['diff', 'a', '-'], 'x\n')).exitCode).toBe(0);
  });
  test('missing file is exit 2', async () => {
    const e = setup('x\n', 'x\n');
    const r = await e.run(['diff', 'a', 'nope']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toBe('diff: nope: No such file or directory\n');
  });
  test('operand count', async () => {
    const r = await new Shell().run('diff a');
    expect([r.exitCode, r.stderr]).toEqual([2, "diff: missing operand after 'a'\ndiff: Try 'diff --help' for more information.\n"]);
  });
  test('too large', async () => {
    const e = setup('', '');
    const big = 'x\n'.repeat(6000);
    e.fs.writeText('/home/agent/a', big);
    e.fs.writeText('/home/agent/b', big.replace(/x/g, 'y'));
    const r = await e.run(['diff', 'a', 'b']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toBe('diff: files too large to compare\n');
  });
  test('-r', async () => {
    const e = env();
    e.fs.mkdir('/home/agent/d1/s', true);
    e.fs.mkdir('/home/agent/d2/s', true);
    e.fs.writeText('/home/agent/d1/f', 'a\n');
    e.fs.writeText('/home/agent/d2/f', 'b\n');
    e.fs.writeText('/home/agent/d1/s/g', 'same\n');
    e.fs.writeText('/home/agent/d2/s/g', 'same\n');
    e.fs.writeText('/home/agent/d1/only', 'x\n');
    const r = await e.run(['diff', '-r', 'd1', 'd2']);
    expect(r.stdout).toBe('diff -r d1/f d2/f\n1c1\n< a\n---\n> b\nOnly in d1: only\n');
    expect(r.exitCode).toBe(1);
  });
});

describe('very long argument lists', () => {
  // widths were computed with Math.max(...list), which overflows the call stack past ~125,000 items
  test('ls -l and seq -w handle 200,000 items', async () => {
    const sh = new Shell();
    let r = await sh.run('mkdir big && cd big && seq 1 130000 | xargs -n 5000 touch && ls -l | wc -l');
    expect(r.stdout.trim()).toBe('130001');
    r = await sh.run('seq -w 1 200000 | tail -1');
    expect(r.stdout).toBe('200000\n');
  }, 120_000);
});
