import { describe, expect, it, beforeEach } from 'vitest';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import * as zlib from 'node:zlib';
import { makeEnv } from './harness.js';
import { registerArchives } from '../src/commands/archive.js';
import { decodeBytes, encodeText } from '../src/bytes.js';

let env = makeEnv();
const H = '/home/agent';

beforeEach(() => {
  env = makeEnv();
  registerArchives(env.shell as any);
});

const run = (argv: string[], stdin = '') => env.run(argv, stdin);
const put = (path: string, text: string) => { env.fs.mkdir(path.slice(0, path.lastIndexOf('/')) || '/', true); env.fs.writeText(path, text); };
const putBytes = (path: string, bytes: Uint8Array) => env.fs.writeBytes(path, bytes);
const cat = (path: string) => env.fs.readText(path);

const sample = () => { tree(); put(`${H}/t.txt`, 'top\n'); };

/** The sample tree used by most tests: d/a.txt, d/sub/b.txt. */
function tree() {
  put(`${H}/d/a.txt`, 'hello\n');
  put(`${H}/d/sub/b.txt`, 'x\ny\n');
}

// ---- a tiny tar writer for crafting hostile archives ----
function tarMember(name: string, o: { type?: string; data?: string | Uint8Array; link?: string; mode?: number; size?: number } = {}): Uint8Array {
  const data = typeof o.data === 'string' ? new TextEncoder().encode(o.data) : o.data ?? new Uint8Array(0);
  const h = new Uint8Array(512);
  const put = (off: number, s: string) => h.set(new TextEncoder().encode(s), off);
  put(0, name);
  put(100, (o.mode ?? 0o644).toString(8).padStart(7, '0') + '\0');
  put(108, '0001750\0'); put(116, '0001750\0');
  put(124, (o.size ?? data.length).toString(8).padStart(11, '0') + '\0');
  put(136, '14000000000\0');
  h[156] = (o.type ?? '0').charCodeAt(0);
  put(157, o.link ?? '');
  put(257, 'ustar\0'); put(263, '00');
  h.fill(0x20, 148, 156);
  let sum = 0;
  for (const b of h) sum += b;
  put(148, sum.toString(8).padStart(6, '0') + '\0 ');
  const pad = new Uint8Array((512 - data.length % 512) % 512);
  return Buffer.concat([h, data, pad]);
}
const tarOf = (...members: Uint8Array[]) => Buffer.concat([...members, new Uint8Array(1024)]);

describe('tar: create, list, extract', () => {
  it('lists what it created, in directory order', async () => {
    tree();
    expect((await run(['tar', 'cf', 't.tar', 'd'])).exitCode).toBe(0);
    const r = await run(['tar', 'tf', 't.tar']);
    expect(r.stdout).toBe('d/\nd/a.txt\nd/sub/\nd/sub/b.txt\n');
    expect(r.exitCode).toBe(0);
  });
  it('pads the archive to 10240 bytes like GNU tar', async () => {
    tree();
    await run(['tar', 'cf', 't.tar', 'd']);
    expect(env.fs.readBytes(`${H}/t.tar`).length).toBe(10240);
    const r = await run(['tar', 'cvf', '-', 'd']);
    expect(r.stdout).toBe(r.stdout); // verbose names go to stderr when the archive is on stdout
    expect(encodeText(r.stdout).length).toBe(10240);
    expect(r.stderr).toBe('d/\nd/a.txt\nd/sub/\nd/sub/b.txt\n');
  });
  it('-v prints each name while creating and extracting', async () => {
    tree();
    expect((await run(['tar', 'cvf', 't.tar', 'd'])).stdout).toBe('d/\nd/a.txt\nd/sub/\nd/sub/b.txt\n');
    env.fs.mkdir(`${H}/o`);
    expect((await run(['tar', 'xvf', 't.tar', '-C', 'o'])).stdout).toBe('d/\nd/a.txt\nd/sub/\nd/sub/b.txt\n');
  });
  it('extracts into -C, restoring contents, mode and time', async () => {
    tree();
    env.fs.chmod(`${H}/d/a.txt`, 0o600);
    env.fs.setModifiedTime(`${H}/d/a.txt`, Date.UTC(2020, 4, 6, 7, 8, 9));
    await run(['tar', 'cf', 't.tar', 'd']);
    env.fs.mkdir(`${H}/o`);
    expect((await run(['tar', 'xf', 't.tar', '-C', 'o'])).exitCode).toBe(0);
    expect(cat(`${H}/o/d/a.txt`)).toBe('hello\n');
    expect(cat(`${H}/o/d/sub/b.txt`)).toBe('x\ny\n');
    expect(env.fs.stat(`${H}/o/d/a.txt`).mode).toBe(0o600);
    expect(env.fs.stat(`${H}/o/d/a.txt`).modified).toBe(Date.UTC(2020, 4, 6, 7, 8, 9));
  });
  it('-tv shows type, mode, owner/group, size, time and name', async () => {
    tree();
    env.fs.setModifiedTime(`${H}/d/a.txt`, Date.UTC(2020, 4, 6, 7, 8, 9));
    await run(['tar', 'cf', 't.tar', 'd/a.txt']);
    const r = await run(['tar', 'tvf', 't.tar']);
    expect(r.stdout).toMatch(/^-rw-r--r-- \S+\/\S+ +6 2020-05-06 07:08 d\/a\.txt\n$/);
  });
  it('-O writes file contents to stdout', async () => {
    tree();
    await run(['tar', 'cf', 't.tar', 'd']);
    expect((await run(['tar', 'xOf', 't.tar', 'd/a.txt'])).stdout).toBe('hello\n');
    expect((await run(['tar', 'xOvf', 't.tar', 'd/a.txt'])).stdout).toBe('hello\n');
  });
  it('--strip-components drops leading directories on extract', async () => {
    tree();
    await run(['tar', 'cf', 't.tar', 'd']);
    env.fs.mkdir(`${H}/o`);
    await run(['tar', 'xf', 't.tar', '-C', 'o', '--strip-components=1']);
    expect(env.fs.list(`${H}/o`)).toEqual(['a.txt', 'sub']);
    expect(cat(`${H}/o/sub/b.txt`)).toBe('x\ny\n');
  });
  it('selects members by name, directories bringing their contents', async () => {
    tree();
    await run(['tar', 'cf', 't.tar', 'd']);
    expect((await run(['tar', 'tf', 't.tar', 'd/sub'])).stdout).toBe('d/sub/\nd/sub/b.txt\n');
    const r = await run(['tar', 'tf', 't.tar', 'd/a.txt', 'nosuch']);
    expect(r.stdout).toBe('d/a.txt\n');
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain('tar: nosuch: Not found in archive');
    expect(r.stderr).toContain('tar: Exiting with failure status due to previous errors');
  });
  it('-C on create names members relative to the directory', async () => {
    tree();
    await run(['tar', 'czf', 't3.tgz', '-C', 'd', '.']);
    expect((await run(['tar', 'tzf', 't3.tgz'])).stdout).toBe('./\n./a.txt\n./sub/\n./sub/b.txt\n');
  });
  it('accepts old-style bundled options and several files', async () => {
    tree();
    put(`${H}/t.txt`, 'top\n');
    expect((await run(['tar', 'cf', 't.tar', 'd/a.txt', 't.txt'])).exitCode).toBe(0);
    expect((await run(['tar', 'tf', 't.tar'])).stdout).toBe('d/a.txt\nt.txt\n');
    expect((await run(['tar', '-c', '-f', 'u.tar', 'd/a.txt', 't.txt'])).exitCode).toBe(0);
    expect((await run(['tar', '-t', '-f', 'u.tar'])).stdout).toBe('d/a.txt\nt.txt\n');
    env.fs.mkdir(`${H}/o`);
    expect((await run(['tar', 'xf', 'u.tar', '-C', 'o'])).exitCode).toBe(0);
    expect((await run(['tar', 'xCf', 'o', 'u.tar'])).exitCode).toBe(0);
  });
  it('round-trips binary files byte for byte', async () => {
    const bin = Uint8Array.from([0, 255, 254, 128, 97, 98, 99]);
    putBytes(`${H}/bin.dat`, bin);
    await run(['tar', 'czf', 'x.tgz', 'bin.dat']);
    env.fs.mkdir(`${H}/o`);
    await run(['tar', 'xzf', 'x.tgz', '-C', 'o']);
    expect([...env.fs.readBytes(`${H}/o/bin.dat`)]).toEqual([...bin]);
  });
  it('handles names longer than 100 bytes (ustar prefix and pax)', async () => {
    const long = `${'dir'.repeat(20)}/${'sub'.repeat(20)}/file.txt`; // > 100 bytes, splits at a slash
    const huge = 'x'.repeat(130) + '.txt'; // no slash to split: needs pax
    put(`${H}/${long}`, 'one');
    put(`${H}/${huge}`, 'two');
    await run(['tar', 'cf', 'l.tar', long, huge]);
    expect((await run(['tar', 'tf', 'l.tar'])).stdout).toBe(`${long}\n${huge}\n`);
    env.fs.mkdir(`${H}/o`);
    await run(['tar', 'xf', 'l.tar', '-C', 'o']);
    expect(cat(`${H}/o/${long}`)).toBe('one');
    expect(cat(`${H}/o/${huge}`)).toBe('two');
  });
  it('does not put the archive in itself', async () => {
    put(`${H}/d/a.txt`, 'a');
    await run(['tar', 'cf', 'd/t.tar', 'd']);
    expect((await run(['tar', 'tf', 'd/t.tar'])).stdout).toBe('d/\nd/a.txt\n');
  });
  it('extract overwrites existing files', async () => {
    put(`${H}/d/a.txt`, 'new');
    await run(['tar', 'cf', 't.tar', 'd']);
    put(`${H}/d/a.txt`, 'old');
    await run(['tar', 'xf', 't.tar']);
    expect(cat(`${H}/d/a.txt`)).toBe('new');
  });
});

describe('tar: gzip and pipes', () => {
  it('-z writes and reads gzip; reading a file detects gzip by itself', async () => {
    tree();
    await run(['tar', 'czf', 't.tgz', 'd']);
    expect([...env.fs.readBytes(`${H}/t.tgz`).subarray(0, 2)]).toEqual([0x1f, 0x8b]);
    expect((await run(['tar', 'tzf', 't.tgz'])).stdout).toBe('d/\nd/a.txt\nd/sub/\nd/sub/b.txt\n');
    expect((await run(['tar', 'tf', 't.tgz'])).stdout).toBe('d/\nd/a.txt\nd/sub/\nd/sub/b.txt\n');
  });
  it('works through pipes, as binary text', async () => {
    tree();
    const made = await run(['tar', 'czf', '-', 'd']);
    const listed = await run(['tar', 'tzf', '-'], made.stdout);
    expect(listed.stdout).toBe('d/\nd/a.txt\nd/sub/\n'.replace('d/sub/\n', 'd/sub/\nd/sub/b.txt\n'));
    const unz = await run(['gzip', '-dc'], made.stdout);
    expect((await run(['tar', 'xOf', '-', 'd/a.txt'], unz.stdout)).stdout).toBe('hello\n');
    expect((await run(['tar', 'tf', '-'], made.stdout)).exitCode).toBe(2); // a gzip stream on stdin needs -z
  });
  it('-z on a plain archive and a non-archive fail with exit 2', async () => {
    tree();
    await run(['tar', 'cf', 't.tar', 'd']);
    expect((await run(['tar', 'xzf', 't.tar'])).exitCode).toBe(2);
    put(`${H}/notar`, 'hi\n');
    const r = await run(['tar', 'tf', 'notar']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain('This does not look like a tar archive');
  });
});

describe('tar: errors', () => {
  it('reports usage and missing files with exit 2', async () => {
    expect((await run(['tar'])).exitCode).toBe(2);
    expect((await run(['tar', 'xf'])).exitCode).toBe(2);
    const r = await run(['tar', 'xf', 'nonexist.tar']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain('tar: nonexist.tar: Cannot open: No such file or directory');
    expect((await run(['tar', 'cf', 'x.tar'])).stderr).toContain('Cowardly refusing to create an empty archive');
  });
  it('a missing input still writes the archive, with exit 2', async () => {
    const r = await run(['tar', 'cf', 'x.tar', 'nosuch']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain('tar: nosuch: Cannot stat: No such file or directory');
    expect((await run(['tar', 'tf', 'x.tar'])).exitCode).toBe(0);
  });
  it('strips a leading slash with a notice', async () => {
    put('/tmp/nosuch-archive-test/f', 'x');
    const r = await run(['tar', 'cf', 'a.tar', '/tmp/nosuch-archive-test']);
    expect(r.stderr).toContain("Removing leading `/' from member names");
    expect((await run(['tar', 'tf', 'a.tar'])).stdout).toBe('tmp/nosuch-archive-test/\ntmp/nosuch-archive-test/f\n');
  });
  it('a truncated archive reports Unexpected EOF', async () => {
    tree();
    await run(['tar', 'cf', 't.tar', 'd']);
    putBytes(`${H}/cut.tar`, env.fs.readBytes(`${H}/t.tar`).subarray(0, 700));
    const r = await run(['tar', 'xf', 'cut.tar']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain('Unexpected EOF in archive');
  });
});

describe('tar: hostile archives', () => {
  it('refuses names with .. and writes nothing outside the target', async () => {
    put(`${H}/o/keep`, 'k');
    putBytes(`${H}/evil.tar`, tarOf(tarMember('../evil.txt', { data: 'pwned' }), tarMember('a/../../evil2.txt', { data: 'pwned' }), tarMember('ok.txt', { data: 'fine' })));
    const r = await run(['tar', 'xf', 'evil.tar', '-C', 'o']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain("Member name contains '..'");
    expect(env.fs.exists(`${H}/evil.txt`)).toBe(false);
    expect(env.fs.exists(`${H}/evil2.txt`)).toBe(false);
    expect(env.fs.exists('/evil2.txt')).toBe(false);
    expect(cat(`${H}/o/ok.txt`)).toBe('fine');
  });
  it('makes absolute names relative to the target', async () => {
    putBytes(`${H}/abs.tar`, tarOf(tarMember('/tmp/abs.txt', { data: 'x' })));
    env.fs.mkdir(`${H}/o`);
    const r = await run(['tar', 'xf', 'abs.tar', '-C', 'o']);
    expect(r.stderr).toContain("Removing leading `/' from member names");
    expect(cat(`${H}/o/tmp/abs.txt`)).toBe('x');
    expect(env.fs.exists('/tmp/abs.txt')).toBe(false);
  });
  it('creates no symlinks and reports them', async () => {
    putBytes(`${H}/sym.tar`, tarOf(tarMember('link', { type: '2', link: '/etc/passwd' }), tarMember('dev', { type: '3' })));
    env.fs.mkdir(`${H}/o`);
    const r = await run(['tar', 'xf', 'sym.tar', '-C', 'o']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain('Cannot create symlink');
    expect(env.fs.exists(`${H}/o/link`)).toBe(false);
    expect(env.fs.exists(`${H}/o/dev`)).toBe(false);
  });
  it('hard links copy only files inside the target', async () => {
    putBytes(`${H}/hard.tar`, tarOf(tarMember('a', { data: 'AAA' }), tarMember('b', { type: '1', link: 'a' }), tarMember('c', { type: '1', link: '../../etc/passwd' }), tarMember('d', { type: '1', link: '/etc/passwd' })));
    env.fs.mkdir(`${H}/o`);
    const r = await run(['tar', 'xf', 'hard.tar', '-C', 'o']);
    expect(cat(`${H}/o/b`)).toBe('AAA');
    expect(env.fs.exists(`${H}/o/c`)).toBe(false);
    expect(env.fs.exists(`${H}/o/d`)).toBe(false);
    expect(r.exitCode).toBe(2);
  });
  it('stops past the byte limit with the limit message', async () => {
    env.shell.options.maxExtractBytes = 1000;
    putBytes(`${H}/big.tar`, tarOf(tarMember('one', { data: 'x'.repeat(600) }), tarMember('two', { data: 'x'.repeat(600) }), tarMember('three', { data: 'y' })));
    env.fs.mkdir(`${H}/o`);
    const r = await run(['tar', 'xf', 'big.tar', '-C', 'o']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toBe('tar: output exceeds the 1000-byte extraction limit; stopping\n');
    expect(env.fs.exists(`${H}/o/three`)).toBe(false);
  });
  it('stops past the entry limit', async () => {
    env.shell.options.maxExtractEntries = 3;
    putBytes(`${H}/many.tar`, tarOf(...[1, 2, 3, 4, 5].map(i => tarMember(`f${i}`, { data: 'x' }))));
    const r = await run(['tar', 'tf', 'many.tar']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toBe('tar: archive exceeds the 3-entry extraction limit; stopping\n');
    expect(r.stdout).toBe('f1\nf2\nf3\n');
  });
  it('a gzip bomb stops decompressing at the limit', async () => {
    env.shell.options.maxExtractBytes = 1 << 20;
    putBytes(`${H}/bomb.tgz`, zlib.gzipSync(Buffer.alloc(8 << 20)));
    const r = await run(['tar', 'tzf', 'bomb.tgz']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toBe(`tar: output exceeds the ${1 << 20}-byte extraction limit; stopping\n`);
  });
  it('a header claiming more data than exists is a short read, not an allocation', async () => {
    const lie = tarMember('huge', { data: 'abc', size: 0o777777777 });
    putBytes(`${H}/lie.tar`, Buffer.from(lie.subarray(0, 512 + 3)));
    const r = await run(['tar', 'xf', 'lie.tar']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain('Unexpected EOF');
  });
  it('writes past the disk cap fail per file', async () => {
    tree();
    await run(['tar', 'cf', 't.tar', 'd']);
    env.fs.mkdir(`${H}/o`);
    env.fs.chmod(`${H}/o`, 0o777);
    env.fs.capacity = env.fs.usedBytes + 2;
    const r = await env.run(['tar', 'xf', 't.tar', '-C', 'o'], '', 10_000, env.fs.as('agent'));
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain('Cannot open: No space left on device');
  });
});

describe('gzip, gunzip, zcat', () => {
  it('compresses in place and restores', async () => {
    put(`${H}/f`, 'hello\n');
    expect((await run(['gzip', 'f'])).exitCode).toBe(0);
    expect(env.fs.list(H)).toContain('f.gz');
    expect(env.fs.exists(`${H}/f`)).toBe(false);
    expect((await run(['gunzip', 'f.gz'])).exitCode).toBe(0);
    expect(cat(`${H}/f`)).toBe('hello\n');
    expect(env.fs.exists(`${H}/f.gz`)).toBe(false);
  });
  it('-k keeps the source; an existing target needs -f', async () => {
    put(`${H}/f`, 'hello\n');
    await run(['gzip', '-k', 'f']);
    expect(env.fs.list(H)).toEqual(expect.arrayContaining(['f', 'f.gz']));
    const r = await run(['gzip', '-k', 'f']);
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain('f.gz already exists');
    expect((await run(['gzip', '-f', '-k', 'f'])).exitCode).toBe(0);
  });
  it('-c and pipes', async () => {
    put(`${H}/f`, 'hello\n');
    const z = await run(['gzip', '-c', 'f']);
    expect(env.fs.exists(`${H}/f`)).toBe(true);
    expect((await run(['gunzip'], z.stdout)).stdout).toBe('hello\n');
    expect((await run(['gzip'], 'hi\n').then(r => run(['gunzip'], r.stdout))).stdout).toBe('hi\n');
    expect((await run(['zcat'], z.stdout)).stdout).toBe('hello\n');
  });
  it('decompresses concatenated members', async () => {
    put(`${H}/f`, 'hello\n');
    const z = (await run(['gzip', '-c', 'f'])).stdout;
    expect((await run(['gunzip'], z + z)).stdout).toBe('hello\nhello\n');
  });
  it('compression levels all round-trip', async () => {
    put(`${H}/big.txt`, Array.from({ length: 1000 }, (_, i) => i + 1).join('\n') + '\n');
    for (const l of ['-1', '-6', '-9']) expect((await run(['gzip', l, '-c', 'big.txt']).then(r => run(['gzip', '-dc'], r.stdout))).stdout).toBe(cat(`${H}/big.txt`));
  });
  it('binary data survives', async () => {
    const bin = Uint8Array.from([0, 255, 254, 128, 97, 98, 99]);
    putBytes(`${H}/bin.dat`, bin);
    await run(['gzip', 'bin.dat']);
    await run(['gunzip', 'bin.dat.gz']);
    expect([...env.fs.readBytes(`${H}/bin.dat`)]).toEqual([...bin]);
    const z = await run(['gzip', '-c', 'bin.dat']);
    const back = await run(['gunzip'], z.stdout);
    expect([...encodeText(back.stdout)]).toEqual([...bin]);
  });
  it('-l lists sizes, -t tests', async () => {
    put(`${H}/f`, 'hello\n');
    await run(['gzip', 'f']);
    const r = await run(['gzip', '-l', 'f.gz']);
    expect(r.stdout.split('\n')[0]).toBe('         compressed        uncompressed  ratio uncompressed_name');
    expect(r.stdout.split('\n')[1]).toMatch(/^ +\d+ +6 +[-\d.]+% f$/);
    expect((await run(['gzip', '-t', 'f.gz'])).exitCode).toBe(0);
    const two = await run(['gzip', '-l', 'f.gz', 'f.gz']);
    expect(two.stdout).toContain('(totals)');
  });
  it('-dcf passes plain data through', async () => {
    put(`${H}/f`, 'hello\n');
    expect((await run(['gzip', '-dcf', 'f'])).stdout).toBe('hello\n');
  });
  it('-v reports the ratio on stderr', async () => {
    put(`${H}/f`, 'hello\n');
    expect((await run(['gzip', '-v', 'f'])).stderr).toMatch(/^f:\t[-\d.]+% -- replaced with f\.gz\n$/);
  });
  it('errors: missing, not gzip, directory, bad suffix, truncated, corrupt', async () => {
    expect((await run(['gunzip', 'nosuch.gz'])).exitCode).toBe(1);
    expect((await run(['zcat', 'nosuch'])).exitCode).toBe(1);
    expect((await run(['gzip', 'nosuch'])).exitCode).toBe(1);
    expect((await run(['gunzip'], 'plain\n')).exitCode).toBe(1);
    put(`${H}/f`, 'hello\n');
    expect((await run(['gzip', '-d', 'f'])).exitCode).toBe(2); // unknown suffix
    expect((await run(['gzip', '-t', 'f'])).exitCode).toBe(1);
    expect((await run(['gzip', '-l', 'f'])).exitCode).toBe(1);
    env.fs.mkdir(`${H}/dd`);
    expect((await run(['gzip', 'dd'])).exitCode).toBe(2);
    expect((await run(['gzip', '-q', 'dd'])).stderr).toBe('');
    put(`${H}/e`, 'abc');
    const r = await run(['zcat', 'f.gz', 'e']);
    expect(r.exitCode).toBe(1);
    const z = zlib.gzipSync(Buffer.from('some text that compresses '.repeat(20)));
    const cut = await run(['gunzip'], decodeBytes(z.subarray(0, z.length - 10)));
    expect(cut.exitCode).toBe(1);
    expect(cut.stderr).toContain('unexpected end of file');
    expect(cut.stdout).toBe(''); // no partial data
    const bad = Buffer.from(z);
    bad[bad.length - 6] ^= 0xff; // damage the CRC
    const corrupt = await run(['gunzip'], decodeBytes(bad));
    expect(corrupt.exitCode).toBe(1);
    expect(corrupt.stdout).toBe('');
    const flipped = Buffer.from(z);
    flipped[15] ^= 0xff;
    expect((await run(['gunzip'], decodeBytes(flipped))).exitCode).toBe(1);
  });
  it('stops a decompression bomb at the extraction limit', async () => {
    env.shell.options.maxExtractBytes = 1 << 20;
    putBytes(`${H}/bomb.gz`, zlib.gzipSync(Buffer.alloc(8 << 20)));
    const r = await run(['gunzip', '-c', 'bomb.gz']);
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toBe(`gzip: output exceeds the ${1 << 20}-byte extraction limit; stopping\n`);
    expect(r.stdout).toBe('');
  });
  it('.tgz decompresses to .tar', async () => {
    put(`${H}/f`, 'hello\n');
    putBytes(`${H}/t.tgz`, zlib.gzipSync(Buffer.from('hello\n')));
    await run(['gunzip', 't.tgz']);
    expect(cat(`${H}/t.tar`)).toBe('hello\n');
  });
});

describe('zip and unzip', () => {
  it('adds files and reports like Info-ZIP', async () => {
    put(`${H}/t.txt`, 'top\n');
    const r = await run(['zip', 'a.zip', 't.txt']);
    expect(r.stdout).toBe('  adding: t.txt (stored 0%)\n');
    expect(r.exitCode).toBe(0);
    expect((await run(['zip', '-q', 'b.zip', 't.txt', 'nosuch'])).stdout).toBe('');
    expect((await run(['zip', 'c.zip', 't.txt', 'nosuch'])).stdout).toBe('\tzip warning: name not matched: nosuch\n  adding: t.txt (stored 0%)\n');
    expect((await run(['zip', 'a', 't.txt'])).exitCode).toBe(0); // .zip is appended
    expect(env.fs.exists(`${H}/a.zip`)).toBe(true);
  });
  it('-r recurses, directories first; updating replaces by name', async () => {
    sample();
    expect((await run(['zip', '-r', 'a.zip', 'd'])).stdout).toBe('  adding: d/ (stored 0%)\n  adding: d/a.txt (stored 0%)\n  adding: d/sub/ (stored 0%)\n  adding: d/sub/b.txt (stored 0%)\n');
    expect((await run(['zip', 'a.zip', 't.txt'])).stdout).toBe('  adding: t.txt (stored 0%)\n');
    expect((await run(['zip', 'a.zip', 't.txt'])).stdout).toBe('updating: t.txt (stored 0%)\n');
    expect((await run(['unzip', '-l', 'a.zip'])).stdout).toMatch(/5 files\n$/);
  });
  it('compressible data is deflated and prints the percentage', async () => {
    put(`${H}/big.txt`, Array.from({ length: 1000 }, (_, i) => i + 1).join('\n') + '\n');
    const r = await run(['zip', 'big.zip', 'big.txt']);
    expect(r.stdout).toMatch(/^  adding: big\.txt \(deflated 5\d%\)\n$/); // Info-ZIP's own deflate gets 53%
    env.fs.delete(`${H}/big.txt`);
    expect((await run(['unzip', 'big.zip'])).stdout).toBe('Archive:  big.zip\n  inflating: big.txt                 \n');
    expect(cat(`${H}/big.txt`)).toBe(Array.from({ length: 1000 }, (_, i) => i + 1).join('\n') + '\n');
  });
  it('unzip -l lists like unzip', async () => {
    sample();
    await run(['zip', '-qr', 'a.zip', 't.txt', 'd']);
    const r = await run(['unzip', '-l', 'a.zip']);
    const lines = r.stdout.split('\n').map(l => l.slice(0, 9) + l.slice(29));
    expect(lines).toEqual(['Archive: ', '  Length  Name', '--------- ----', '        4 t.txt', '        0 d/', '        6 d/a.txt', '        0 d/sub/', '        4 d/sub/b.txt', '--------- -------', '       14 5 files', '']);
    expect((await run(['unzip', '-l', 'a.zip', '*.txt'])).stdout).toContain('t.txt');
    const none = await run(['unzip', '-l', 'a.zip', 'nosuch']);
    expect(none.exitCode).toBe(11);
    expect(none.stdout).toBe('Archive:  a.zip\n  Length      Date    Time    Name\n---------  ---------- -----   ----\n---------                     -------\n        0                     0 files\n');
  });
  it('extracts into -d, quietly with -q, to stdout with -p', async () => {
    sample();
    await run(['zip', '-qr', 'a.zip', 't.txt', 'd']);
    env.fs.mkdir(`${H}/o`);
    const r = await run(['unzip', 'a.zip', '-d', 'o']);
    expect(r.stdout).toBe('Archive:  a.zip\n extracting: o/t.txt                 \n   creating: o/d/\n extracting: o/d/a.txt               \n   creating: o/d/sub/\n extracting: o/d/sub/b.txt           \n');
    expect(cat(`${H}/o/d/sub/b.txt`)).toBe('x\ny\n');
    expect((await run(['unzip', '-q', 'a.zip', '-d', 'p'])).stdout).toBe('');
    expect(env.fs.list(`${H}/p`)).toEqual(['d', 't.txt']);
    expect((await run(['unzip', '-p', 'a.zip', 'd/a.txt', 't.txt'])).stdout).toBe('top\nhello\n'); // archive order
  });
  it('existing files: skipped with a notice, -o overwrites, -n keeps', async () => {
    sample();
    await run(['zip', '-q', 'a.zip', 't.txt']);
    put(`${H}/o/t.txt`, 'old');
    const r = await run(['unzip', '-q', 'a.zip', '-d', 'o']);
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toContain('replace o/t.txt?');
    expect(cat(`${H}/o/t.txt`)).toBe('old');
    expect((await run(['unzip', '-n', 'a.zip', '-d', 'o'])).exitCode).toBe(0);
    expect(cat(`${H}/o/t.txt`)).toBe('old');
    expect((await run(['unzip', '-oq', 'a.zip', '-d', 'o'])).exitCode).toBe(0);
    expect(cat(`${H}/o/t.txt`)).toBe('top\n');
  });
  it('restores mode and time', async () => {
    put(`${H}/s.sh`, '#!/bin/sh\n');
    env.fs.chmod(`${H}/s.sh`, 0o755);
    env.fs.setModifiedTime(`${H}/s.sh`, Date.UTC(2021, 1, 3, 4, 5, 6));
    await run(['zip', '-q', 'a.zip', 's.sh']);
    env.fs.delete(`${H}/s.sh`);
    await run(['unzip', '-q', 'a.zip']);
    expect(env.fs.stat(`${H}/s.sh`).mode).toBe(0o755);
    expect(env.fs.stat(`${H}/s.sh`).modified).toBe(Date.UTC(2021, 1, 3, 4, 5, 6));
  });
  it('zip to stdout and from a directory', async () => {
    sample();
    const z = await run(['zip', '-qr', '-', 'd', 't.txt']);
    putBytes(`${H}/s.zip`, encodeText(z.stdout));
    expect((await run(['unzip', '-p', 's.zip', 't.txt'])).stdout).toBe('top\n');
  });
  it('errors: missing archive, not a zip, nothing to do', async () => {
    const m = await run(['unzip', 'nosuch.zip']);
    expect(m.exitCode).toBe(9);
    expect(m.stderr).toContain('cannot find or open nosuch.zip');
    put(`${H}/bad.zip`, 'notzip\n');
    const b = await run(['unzip', '-l', 'bad.zip']);
    expect(b.exitCode).toBe(9);
    expect(b.stdout).toBe('Archive:  bad.zip\n');
    const n = await run(['zip', 'y.zip', 'nosuch']);
    expect(n.exitCode).toBe(12);
    expect(n.stdout).toBe('\tzip warning: name not matched: nosuch\n\nzip error: Nothing to do! (y.zip)\n');
    expect((await run(['zip', '-r', 'e.zip', 'nosuch'])).stdout).toContain('Nothing to do! (try: zip -r e.zip . -i nosuch)');
    expect((await run(['zip'])).exitCode).toBe(12);
    expect((await run(['zip', 'bad.zip', 'bad.zip'])).exitCode).toBe(3);
  });
});

describe('zip: hostile archives', () => {
  /** Builds a zip by hand: [name, data, method] triples; stored unless a deflated payload is given. */
  function craft(entries: [string, Uint8Array | string, ('stored' | 'deflate')?][]): Uint8Array {
    const parts: Buffer[] = [], central: Buffer[] = [];
    let offset = 0;
    for (const [name, content, how] of entries) {
      const raw = Buffer.from(typeof content === 'string' ? content : content);
      const body = how === 'deflate' ? zlib.deflateRawSync(raw) : raw;
      const nm = Buffer.from(name);
      const crc = zlib.crc32 ? zlib.crc32(raw) : 0;
      const lh = Buffer.alloc(30);
      lh.writeUInt32LE(0x04034b50, 0); lh.writeUInt16LE(20, 4); lh.writeUInt16LE(how === 'deflate' ? 8 : 0, 8);
      lh.writeUInt32LE(crc, 14); lh.writeUInt32LE(body.length, 18); lh.writeUInt32LE(raw.length, 22); lh.writeUInt16LE(nm.length, 26);
      const ch = Buffer.alloc(46);
      ch.writeUInt32LE(0x02014b50, 0); ch.writeUInt16LE(20, 6); ch.writeUInt16LE(how === 'deflate' ? 8 : 0, 10);
      ch.writeUInt32LE(crc, 16); ch.writeUInt32LE(body.length, 20); ch.writeUInt32LE(raw.length, 24); ch.writeUInt16LE(nm.length, 28); ch.writeUInt32LE(offset, 42);
      parts.push(lh, nm, body); central.push(ch, nm);
      offset += 30 + nm.length + body.length;
    }
    const dir = Buffer.concat(central);
    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(entries.length, 8); end.writeUInt16LE(entries.length, 10); end.writeUInt32LE(dir.length, 12); end.writeUInt32LE(offset, 16);
    return Buffer.concat([...parts, dir, end]);
  }

  it('cleans .. and absolute names so nothing leaves the target', async () => {
    putBytes(`${H}/evil.zip`, craft([['../evil.txt', 'x'], ['a/../../evil2.txt', 'y'], ['/abs/evil3.txt', 'z'], ['ok.txt', 'fine']]));
    env.fs.mkdir(`${H}/o`);
    const r = await run(['unzip', '-q', 'evil.zip', '-d', 'o']);
    expect(r.exitCode).toBe(1);
    expect(env.fs.exists(`${H}/evil.txt`)).toBe(false);
    expect(env.fs.exists(`${H}/evil2.txt`)).toBe(false);
    expect(env.fs.exists('/abs/evil3.txt')).toBe(false);
    expect(env.fs.list(`${H}/o`).sort()).toEqual(['a', 'abs', 'evil.txt', 'ok.txt'].sort().filter(n => env.fs.exists(`${H}/o/${n}`)));
    expect(cat(`${H}/o/evil.txt`)).toBe('x');
    expect(cat(`${H}/o/abs/evil3.txt`)).toBe('z');
    expect(cat(`${H}/o/ok.txt`)).toBe('fine');
  });
  it('stops a deflate bomb at the extraction limit, whatever the sizes say', async () => {
    env.shell.options.maxExtractBytes = 1 << 20;
    putBytes(`${H}/bomb.zip`, craft([['zeros', Buffer.alloc(8 << 20), 'deflate']]));
    env.fs.mkdir(`${H}/o`);
    const r = await run(['unzip', 'bomb.zip', '-d', 'o']);
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toBe(`unzip: output exceeds the ${1 << 20}-byte extraction limit; stopping\n`);
    expect(env.fs.exists(`${H}/o/zeros`)).toBe(false);
  });
  it('stops past the entry limit', async () => {
    env.shell.options.maxExtractEntries = 2;
    putBytes(`${H}/many.zip`, craft([['a', '1'], ['b', '2'], ['c', '3']]));
    const r = await run(['unzip', '-l', 'many.zip']);
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toBe('unzip: archive exceeds the 2-entry extraction limit; stopping\n');
  });
  it('rejects a bad CRC', async () => {
    const z = Buffer.from(craft([['f', 'hello']]));
    z[14] ^= 0xff;
    const c = Buffer.from(z);
    // the CRC lives in the central directory too, which is what unzip trusts
    const cd = c.indexOf(Buffer.from([0x50, 0x4b, 0x01, 0x02]));
    c[cd + 16] ^= 0xff;
    putBytes(`${H}/crc.zip`, c);
    const r = await run(['unzip', '-p', 'crc.zip']);
    expect(r.exitCode).not.toBe(0);
    expect(r.stdout).toBe('');
  });
});

// ---- interoperability with the real tools, when they are installed ----
const have = (cmd: string) => spawnSync('which', [cmd]).status === 0;
const real = have('tar') && have('gzip') && have('unzip') && have('zip');

describe.skipIf(!real)('interop with the real tar, gzip, zip and unzip', () => {
  let dir: string;
  beforeEach(() => { dir = mkdtempSync(join(tmpdir(), 'topwater-archive-')); });
  const cleanup = () => rmSync(dir, { recursive: true, force: true });

  it('real tar reads our tar and gzip', async () => {
    try {
      tree();
      const z = await run(['tar', 'czf', '-', 'd']);
      writeFileSync(join(dir, 'x.tgz'), encodeText(z.stdout));
      const out = execFileSync('tar', ['tzvf', join(dir, 'x.tgz')]).toString();
      expect(out).toMatch(/drwxr-xr-x .* d\/\n/);
      expect(out).toMatch(/-rw-r--r-- .* 6 .* d\/a\.txt\n/);
      execFileSync('tar', ['xzf', join(dir, 'x.tgz'), '-C', dir]);
      expect(readFileSync(join(dir, 'd/sub/b.txt'), 'utf8')).toBe('x\ny\n');
      const gz = await run(['gzip', '-c', 'd/a.txt']);
      writeFileSync(join(dir, 'a.gz'), encodeText(gz.stdout));
      expect(execFileSync('gzip', ['-dc', join(dir, 'a.gz')]).toString()).toBe('hello\n');
      execFileSync('gzip', ['-t', join(dir, 'a.gz')]);
    } finally { cleanup(); }
  });
  it('our tar reads real tar archives: ustar, GNU long names, pax', async () => {
    try {
      const long = join(dir, 'src', 'a'.repeat(60), 'b'.repeat(60));
      mkdirSync(long, { recursive: true });
      writeFileSync(join(long, 'file.txt'), 'long\n');
      writeFileSync(join(dir, 'src', 'é ü.txt'), 'unicode\n');
      writeFileSync(join(dir, 'src', 'top.txt'), 'top\n');
      for (const fmt of ['ustar', 'gnu', 'pax', 'oldgnu']) {
        const name = `r-${fmt}.tar`;
        execFileSync('tar', ['--format=' + fmt, '-cf', join(dir, name), '-C', dir, 'src']);
        putBytes(`${H}/${name}`, readFileSync(join(dir, name)));
        const listed = (await run(['tar', 'tf', name])).stdout.split('\n').filter(Boolean);
        const expected = execFileSync('tar', ['-tf', join(dir, name)]).toString().split('\n').filter(Boolean);
        expect(listed).toEqual(expected);
        env.fs.mkdir(`${H}/o-${fmt}`);
        const r = await run(['tar', 'xf', name, '-C', `o-${fmt}`]);
        expect(r.exitCode).toBe(0);
        expect(cat(`${H}/o-${fmt}/src/top.txt`)).toBe('top\n');
        expect(cat(`${H}/o-${fmt}/src/${'a'.repeat(60)}/${'b'.repeat(60)}/file.txt`)).toBe('long\n');
      }
      // real gzip output, including a multi-member file
      writeFileSync(join(dir, 'g1'), 'one\n');
      execFileSync('gzip', ['-k', join(dir, 'g1')]);
      const g = readFileSync(join(dir, 'g1.gz'));
      expect((await run(['gunzip'], decodeBytes(Buffer.concat([g, g])))).stdout).toBe('one\none\n');
    } finally { cleanup(); }
  });
  it('real unzip reads our zip; our unzip reads real zip', async () => {
    try {
      sample();
      const z = await run(['zip', '-qr', '-', 'd', 't.txt']);
      writeFileSync(join(dir, 'ours.zip'), encodeText(z.stdout));
      const t = spawnSync('unzip', ['-t', join(dir, 'ours.zip')]);
      expect(t.status).toBe(0);
      expect(execFileSync('unzip', ['-p', join(dir, 'ours.zip'), 'd/a.txt']).toString()).toBe('hello\n');
      execFileSync('unzip', ['-q', join(dir, 'ours.zip'), '-d', join(dir, 'x')]);
      expect(readFileSync(join(dir, 'x/d/sub/b.txt'), 'utf8')).toBe('x\ny\n');
      // and the other way, including deflated and empty files and a directory
      mkdirSync(join(dir, 'src/sub'), { recursive: true });
      writeFileSync(join(dir, 'src/big.txt'), 'line of text\n'.repeat(500));
      writeFileSync(join(dir, 'src/empty'), '');
      writeFileSync(join(dir, 'src/sub/n.txt'), 'n\n');
      execFileSync('zip', ['-qr', join(dir, 'real.zip'), 'src'], { cwd: dir });
      putBytes(`${H}/real.zip`, readFileSync(join(dir, 'real.zip')));
      expect((await run(['unzip', '-l', 'real.zip'])).stdout).toMatch(/src\/big\.txt/);
      env.fs.mkdir(`${H}/ro`);
      const r = await run(['unzip', '-q', 'real.zip', '-d', 'ro']);
      expect(r.exitCode).toBe(0);
      expect(cat(`${H}/ro/src/big.txt`)).toBe('line of text\n'.repeat(500));
      expect(cat(`${H}/ro/src/empty`)).toBe('');
      expect(cat(`${H}/ro/src/sub/n.txt`)).toBe('n\n');
      // updating a real zip keeps its other entries
      put(`${H}/t.txt`, 'top\n');
      await run(['zip', '-q', 'real.zip', 't.txt']);
      expect((await run(['unzip', '-l', 'real.zip'])).stdout).toMatch(/src\/big\.txt[\s\S]*t\.txt/);
      writeFileSync(join(dir, 'upd.zip'), env.fs.readBytes(`${H}/real.zip`));
      expect(spawnSync('unzip', ['-t', '-q', join(dir, 'upd.zip')]).status).toBe(0);
    } finally { cleanup(); }
  });
});
