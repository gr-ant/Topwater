// The zip container: reading and writing archives with node:zlib's raw deflate and a CRC-32. Shared by zip/unzip and
// the Office-document converter.

import * as zlib from 'node:zlib';

const enc = new TextEncoder();
const dec = new TextDecoder();
const concat = (parts: Uint8Array[]) => Buffer.concat(parts);

const CRC_TABLE = Uint32Array.from({ length: 256 }, (_, i) => { let c = i; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
export function crc32(data: Uint8Array): number {
  let c = 0xffffffff;
  for (const b of data) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}


export interface ZipEntry {
  name: string; method: number; crc: number; usize: number; time: number; date: number; ext: number; flags: number;
  /** The compressed bytes exactly as stored. */
  data: Uint8Array;
}
export class ZipFormatError extends Error {}

export function dosTime(ms: number): [time: number, date: number] {
  const d = new Date(Math.max(ms, Date.UTC(1980, 0, 1)));
  return [(d.getUTCHours() << 11) | (d.getUTCMinutes() << 5) | (d.getUTCSeconds() >> 1), ((d.getUTCFullYear() - 1980) << 9) | ((d.getUTCMonth() + 1) << 5) | d.getUTCDate()];
}
export const fromDos = (time: number, date: number) => Date.UTC(1980 + (date >> 9), Math.max(0, ((date >> 5) & 15) - 1), date & 31 || 1, time >> 11, (time >> 5) & 63, (time & 31) * 2);

export function readZip(bytes: Uint8Array): ZipEntry[] {
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let eocd = -1;
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 22 - 65535); i--) if (v.getUint32(i, true) === 0x06054b50) { eocd = i; break; }
  if (eocd < 0) throw new ZipFormatError('no end of central directory');
  const count = v.getUint16(eocd + 10, true);
  let pos = v.getUint32(eocd + 16, true);
  const entries: ZipEntry[] = [];
  for (let n = 0; n < count; n++) {
    if (pos + 46 > bytes.length || v.getUint32(pos, true) !== 0x02014b50) throw new ZipFormatError('bad central directory');
    const flags = v.getUint16(pos + 8, true), method = v.getUint16(pos + 10, true);
    const csize = v.getUint32(pos + 20, true), usize = v.getUint32(pos + 24, true);
    const nameLen = v.getUint16(pos + 28, true), extraLen = v.getUint16(pos + 30, true), commentLen = v.getUint16(pos + 32, true);
    const local = v.getUint32(pos + 42, true);
    const name = dec.decode(bytes.subarray(pos + 46, pos + 46 + nameLen));
    if (local + 30 > bytes.length || v.getUint32(local, true) !== 0x04034b50) throw new ZipFormatError('bad local header');
    const start = local + 30 + v.getUint16(local + 26, true) + v.getUint16(local + 28, true);
    if (start + csize > bytes.length) throw new ZipFormatError('truncated');
    entries.push({
      name, method, flags, usize, crc: v.getUint32(pos + 16, true), time: v.getUint16(pos + 12, true), date: v.getUint16(pos + 14, true),
      ext: v.getUint32(pos + 38, true), data: bytes.subarray(start, start + csize),
    });
    pos += 46 + nameLen + extraLen + commentLen;
  }
  return entries;
}

export function writeZip(entries: ZipEntry[]): Uint8Array {
  const out: Uint8Array[] = [], central: Uint8Array[] = [];
  let offset = 0;
  for (const e of entries) {
    const name = enc.encode(e.name);
    const flags = e.flags | (/[^\x00-\x7f]/.test(e.name) ? 0x800 : 0);
    const lh = Buffer.alloc(30);
    lh.writeUInt32LE(0x04034b50, 0); lh.writeUInt16LE(e.method === 0 ? 10 : 20, 4); lh.writeUInt16LE(flags, 6); lh.writeUInt16LE(e.method, 8);
    lh.writeUInt16LE(e.time, 10); lh.writeUInt16LE(e.date, 12); lh.writeUInt32LE(e.crc, 14); lh.writeUInt32LE(e.data.length, 18);
    lh.writeUInt32LE(e.usize, 22); lh.writeUInt16LE(name.length, 26);
    const ch = Buffer.alloc(46);
    ch.writeUInt32LE(0x02014b50, 0); ch.writeUInt16LE(0x031e, 4); ch.writeUInt16LE(e.method === 0 ? 10 : 20, 6); ch.writeUInt16LE(flags, 8);
    ch.writeUInt16LE(e.method, 10); ch.writeUInt16LE(e.time, 12); ch.writeUInt16LE(e.date, 14); ch.writeUInt32LE(e.crc, 16);
    ch.writeUInt32LE(e.data.length, 20); ch.writeUInt32LE(e.usize, 24); ch.writeUInt16LE(name.length, 28); ch.writeUInt32LE(e.ext >>> 0, 38);
    ch.writeUInt32LE(offset, 42);
    out.push(lh, name, e.data);
    central.push(ch, name);
    offset += 30 + name.length + e.data.length;
  }
  const dir = concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(entries.length, 8); end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(dir.length, 12); end.writeUInt32LE(offset, 16);
  return concat([...out, dir, end]);
}

/** A zip of the given files (text or bytes), deflated; for building archives in code and tests. */
export function buildZip(files: [name: string, content: string | Uint8Array][], when = Date.UTC(2020, 0, 1)): Uint8Array {
  const [time, date] = dosTime(when);
  return writeZip(files.map(([name, content]) => {
    const raw = typeof content === 'string' ? enc.encode(content) : content;
    return { name, method: 8, flags: 0, crc: crc32(raw), usize: raw.length, time, date, ext: (0o100644 << 16) >>> 0, data: zlib.deflateRawSync(raw) };
  }));
}

