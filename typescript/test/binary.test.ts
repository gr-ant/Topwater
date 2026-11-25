import { describe, expect, test } from 'vitest';
import { makeEnv } from './harness.js';
import { registerBinary } from '../src/commands/binary.js';
import { decodeBytes, encodeText } from '../src/bytes.js';

const SAMPLE = Uint8Array.from([...Buffer.from('Hello, World!\n\0\x01\x02'), 0xff, ...Buffer.from(' binary\tstuff here\n')]);

function env() {
  const e = makeEnv();
  registerBinary(e.shell as any);
  e.fs.writeBytes('/home/agent/s.bin', SAMPLE);
  const z = new Uint8Array(0x80);
  z.set(Buffer.from('abcdefghijklmnopqrstuvwxyz'), 0x64);
  z[0x7e] = 0x80; z[0x7f] = 0x81;
  e.fs.writeBytes('/home/agent/z.bin', z);
  return e;
}
const sh = async (argv: string[], stdin = '') => env().run(argv, stdin);

describe('xxd', () => {
  test('default', async () => {
    expect((await sh(['xxd', 's.bin'])).stdout).toBe(
      '00000000: 4865 6c6c 6f2c 2057 6f72 6c64 210a 0001  Hello, World!...\n' +
      '00000010: 02ff 2062 696e 6172 7909 7374 7566 6620  .. binary.stuff \n' +
      '00000020: 6865 7265 0a                             here.\n');
  });
  test('-l -s', async () => { expect((await sh(['xxd', '-l', '8', '-s', '2', 's.bin'])).stdout).toBe('00000002: 6c6c 6f2c 2057 6f72                      llo, Wor\n'); });
  test('-p', async () => { expect((await sh(['xxd', '-p', 's.bin'])).stdout).toBe('48656c6c6f2c20576f726c64210a000102ff2062696e6172790973747566\n6620686572650a\n'); });
  test('stdin', async () => { expect((await sh(['xxd'], 'abc')).stdout).toBe('00000000: 6162 63                                  abc\n'); });
  test('-c 4 -u', async () => { expect((await sh(['xxd', '-c', '4', '-u'], 'xyz!')).stdout).toBe('00000000: 7879 7A21  xyz!\n'); });
  test('invalid columns', async () => { expect((await sh(['xxd', '-c', '0'], 'a')).exitCode).toBe(1); });
  test('missing file', async () => {
    const r = await sh(['xxd', 'nope']);
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toBe('xxd: nope: No such file or directory\n');
  });
  test('binary through stdin pipe', async () => {
    expect((await sh(['xxd', '-p'], decodeBytes(Uint8Array.from([0, 0xff, 0xfe, 0x80])))).stdout).toBe('00fffe80\n');
  });
});

describe('hexdump', () => {
  test('-C', async () => {
    expect((await sh(['hexdump', '-C', 's.bin'])).stdout).toBe(
      '00000000  48 65 6c 6c 6f 2c 20 57  6f 72 6c 64 21 0a 00 01  |Hello, World!...|\n' +
      '00000010  02 ff 20 62 69 6e 61 72  79 09 73 74 75 66 66 20  |.. binary.stuff |\n' +
      '00000020  68 65 72 65 0a                                    |here.|\n00000025\n');
  });
  test('-C -n', async () => { expect((await sh(['hexdump', '-C', '-n', '5', 's.bin'])).stdout).toBe('00000000  48 65 6c 6c 6f                                    |Hello|\n00000005\n'); });
  test('squeezes repeated lines', async () => {
    expect((await sh(['hexdump', '-C', 'z.bin'])).stdout).toBe(
      '00000000  00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00  |................|\n*\n' +
      '00000060  00 00 00 00 61 62 63 64  65 66 67 68 69 6a 6b 6c  |....abcdefghijkl|\n' +
      '00000070  6d 6e 6f 70 71 72 73 74  75 76 77 78 79 7a 80 81  |mnopqrstuvwxyz..|\n00000080\n');
  });
  test('-v keeps duplicates', async () => { expect((await sh(['hexdump', '-C', '-v'], '\0'.repeat(48))).stdout.split('\n').length).toBe(5); });
  test('empty input prints nothing', async () => { expect((await sh(['hexdump', '-C'], '')).stdout).toBe(''); });
});

describe('od', () => {
  test('-c', async () => { expect((await sh(['od', '-c', 's.bin'])).stdout).toBe('0000000   H   e   l   l   o   ,       W   o   r   l   d   !  \\n  \\0 001\n0000020 002 377       b   i   n   a   r   y  \\t   s   t   u   f   f    \n0000040   h   e   r   e  \\n\n0000045\n'); });
  test('-x', async () => { expect((await sh(['od', '-x', 's.bin'])).stdout).toBe('0000000 6548 6c6c 2c6f 5720 726f 646c 0a21 0100\n0000020 ff02 6220 6e69 7261 0979 7473 6675 2066\n0000040 6568 6572 000a\n0000045\n'); });
  test('-An -tx1', async () => { expect((await sh(['od', '-An', '-tx1', 's.bin'])).stdout).toBe(' 48 65 6c 6c 6f 2c 20 57 6f 72 6c 64 21 0a 00 01\n 02 ff 20 62 69 6e 61 72 79 09 73 74 75 66 66 20\n 68 65 72 65 0a\n'); });
  test('-N 4 -c', async () => { expect((await sh(['od', '-N', '4', '-c', 's.bin'])).stdout).toBe('0000000   H   e   l   l\n0000004\n'); });
  test('default octal words', async () => { expect((await sh(['od'], 'ab')).stdout).toBe('0000000 061141\n0000002\n'); });
  test('-A x', async () => { expect((await sh(['od', '-A', 'x', '-t', 'x1'], 'a')).stdout).toBe('000000 61\n000001\n'); });
  test('escape and tab in -c', async () => { expect((await sh(['od', '-c'], '\t\n')).stdout).toBe('0000000  \\t  \\n\n0000002\n'); });
  test('bad type', async () => { expect((await sh(['od', '-t', 'z'], 'a')).exitCode).toBe(1); });
  test('bad radix', async () => { expect((await sh(['od', '-A', 'q'], 'a')).stderr).toMatch(/output address radix/); });
});

describe('strings', () => {
  test('default', async () => { expect((await sh(['strings', 's.bin'])).stdout).toBe('Hello, World!\n binary\tstuff here\n'); });
  test('-n', async () => { expect((await sh(['strings', '-n', '2', 'z.bin'])).stdout).toBe('abcdefghijklmnopqrstuvwxyz\n'); });
  test('-3 shorthand', async () => { expect((await sh(['strings', '-3'], 'ab\0abc\0')).stdout).toBe('abc\n'); });
  test('stdin', async () => { expect((await sh(['strings'], 'hi there\n')).stdout).toBe('hi there\n'); });
  test('final run without terminator', async () => { expect((await sh(['strings'], 'abcd')).stdout).toBe('abcd\n'); });
});

describe('base64', () => {
  test('encode', async () => { expect((await sh(['base64'], 'hello')).stdout).toBe('aGVsbG8=\n'); });
  test('decode', async () => { expect((await sh(['base64', '-d'], 'aGVsbG8=\n')).stdout).toBe('hello'); });
  test('--decode', async () => { expect((await sh(['base64', '--decode'], 'aGk=')).stdout).toBe('hi'); });
  test('wraps at 76', async () => {
    const out = (await sh(['base64'], 'a'.repeat(100))).stdout;
    expect(out.split('\n')[0].length).toBe(76);
    expect(out.endsWith('\n')).toBe(true);
  });
  test('-w 0', async () => { expect((await sh(['base64', '-w', '0'], 'a'.repeat(100))).stdout.split('\n').length).toBe(2); });
  test('-w 4', async () => { expect((await sh(['base64', '-w4'], 'abcdef')).stdout).toBe('YWJj\nZGVm\n'); });
  test('decode multi-line input', async () => { expect((await sh(['base64', '-d'], 'aGVs\nbG8=\n')).stdout).toBe('hello'); });
  test('invalid input', async () => {
    const r = await sh(['base64', '-d'], '!!!');
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toBe('base64: invalid input\n');
  });
  test('-d -i ignores garbage', async () => { expect((await sh(['base64', '-d', '-i'], 'aG!Vs bG8=')).stdout).toBe('hello'); });
  test('binary round trip', async () => {
    const e = env();
    const enc = await e.run(['base64', 's.bin']);
    const dec = await e.run(['base64', '-d'], enc.stdout);
    expect(Buffer.from(encodeText(dec.stdout)).equals(Buffer.from(SAMPLE))).toBe(true);
  });
  test('decode binary', async () => {
    const r = await sh(['base64', '-d'], 'AP/+gGFiYw==\n');
    expect([...encodeText(r.stdout)]).toEqual([0, 0xff, 0xfe, 0x80, 0x61, 0x62, 0x63]);
  });
  test('empty', async () => { expect((await sh(['base64'], '')).stdout).toBe(''); });
});

describe('hashes', () => {
  test('md5sum stdin', async () => { expect((await sh(['md5sum'], 'hello\n')).stdout).toBe('b1946ac92492d2347c6235b4d2611184  -\n'); });
  test('sha1sum', async () => { expect((await sh(['sha1sum'], 'abc')).stdout).toBe('a9993e364706816aba3e25717850c26c9cd0d89d  -\n'); });
  test('sha256sum', async () => { expect((await sh(['sha256sum'], 'abc')).stdout).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  -\n'); });
  test('sha512sum prefix', async () => { expect((await sh(['sha512sum'], 'abc')).stdout).toMatch(/^ddaf35a193617aba[0-9a-f]{112}  -\n$/); });
  test('files and missing file', async () => {
    const e = env();
    e.fs.writeText('/home/agent/a', 'abc');
    const r = await e.run(['sha256sum', 'a', 'nope']);
    expect(r.stdout).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  a\n');
    expect(r.stderr).toBe('sha256sum: nope: No such file or directory\n');
    expect(r.exitCode).toBe(1);
  });
  test('binary data hashes true bytes', async () => {
    const r = await sh(['md5sum'], decodeBytes(Uint8Array.from([0, 0xff, 0xfe, 0x80, 0x61, 0x62, 0x63])));
    expect(r.stdout).toBe('676e0ec24cbc5d824eb54de42140de3d  -\n');
  });
});
