import { describe, expect, it } from 'vitest';
import { createHash, randomBytes } from 'node:crypto';
import * as zlib from 'node:zlib';
import { InflateError, OutputLimitError, gunzip, inflateRaw } from '../src/inflate.js';
import { deflate } from '../src/compress.js';
import { md5 } from '../src/md5.js';
import { storedDeflate } from '../src/commands/zipfile.js';

const repetitive = (n: number) => Buffer.from('the quick brown fox jumps over the lazy dog. '.repeat(Math.ceil(n / 45)).slice(0, n));
const samples = [0, 1, 100, 5000, 70000, 300000].flatMap(n => [randomBytes(n), repetitive(n)]);
const kind = (f: () => unknown) => { try { f(); } catch (e) { return e instanceof InflateError ? e.kind : e; } return 'none'; };

describe('inflate', () => {
  it('round-trips zlib raw deflate and gzip at several levels', () => {
    for (const data of samples) for (const level of [1, 6, 9]) {
      expect(Buffer.from(inflateRaw(zlib.deflateRawSync(data, { level })))).toEqual(data);
      expect(Buffer.from(gunzip(zlib.gzipSync(data, { level })))).toEqual(data);
    }
  }, 30_000); // generous: a loaded machine runs slow
  it('decodes fixed-Huffman blocks', () => {
    const data = Buffer.from('abcabcabcabc hello hello');
    expect(Buffer.from(inflateRaw(zlib.deflateRawSync(data, { strategy: zlib.constants.Z_FIXED })))).toEqual(data);
  });
  it('decodes concatenated gzip members and ignores trailing garbage', () => {
    const two = Buffer.concat([zlib.gzipSync('one '), zlib.gzipSync('two')]);
    expect(Buffer.from(gunzip(two)).toString()).toBe('one two');
    expect(Buffer.from(gunzip(Buffer.concat([two, Buffer.from('junk')]))).toString()).toBe('one two');
  });
  it('inflates storedDeflate output, including empty and multi-block input', () => {
    for (const n of [0, 1, 65535, 65536, 200000]) {
      const data = randomBytes(n);
      expect(Buffer.from(inflateRaw(storedDeflate(data)))).toEqual(data);
      expect(zlib.inflateRawSync(storedDeflate(data))).toEqual(data);
    }
  });
  it('round-trips CompressionStream output', async () => {
    const data = repetitive(100000);
    expect(Buffer.from(gunzip(await deflate('gzip', data)))).toEqual(data);
    expect(Buffer.from(inflateRaw(await deflate('deflate-raw', data)))).toEqual(data);
  }, 30_000);
  it('classifies bad input like zlib', () => {
    const gz = zlib.gzipSync(repetitive(5000));
    expect(kind(() => gunzip(gz.subarray(0, gz.length - 20)))).toBe('eof');
    expect(kind(() => inflateRaw(zlib.deflateRawSync(randomBytes(500)).subarray(0, 100)))).toBe('eof');
    const bad = Buffer.from(gz);
    bad[bad.length - 8] ^= 0xff;
    expect(kind(() => gunzip(bad))).toBe('crc');
    expect(kind(() => inflateRaw(Buffer.from([0x07, 0xff, 0xff])))).toBe('format'); // block type 3
    expect(kind(() => gunzip(Buffer.from('not gzip at all, really')))).toBe('format');
  });
  it('stops at the output limit', () => {
    const gz = zlib.gzipSync(Buffer.alloc(1_000_000));
    expect(() => gunzip(gz, 999_999)).toThrow(OutputLimitError);
    expect(gunzip(gz, 1_000_000).length).toBe(1_000_000);
    expect(() => inflateRaw(zlib.deflateRawSync(Buffer.alloc(1_000_000)), 1000)).toThrow(OutputLimitError);
  });
});

describe('md5', () => {
  it('matches known vectors', () => {
    expect(md5(new Uint8Array())).toBe('d41d8cd98f00b204e9800998ecf8427e');
    expect(md5(Buffer.from('abc'))).toBe('900150983cd24fb0d6963f7d28e17f72');
  });
  it('matches node:crypto on awkward lengths and 1 MB', () => {
    for (const n of [55, 56, 63, 64, 65, 1_000_000]) {
      const d = randomBytes(n);
      expect(md5(d)).toBe(createHash('md5').update(d).digest('hex'));
    }
  });
});
