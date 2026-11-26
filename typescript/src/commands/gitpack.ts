// Reading git packfiles (.git/objects/pack/*.pack with their v2 .idx), as `git clone` and `git gc` leave them. Read only:
// new objects are still written loose. A port of python/src/topwater/commands/gitpack.py (the reference); keep them in step.
// A pack is agent-controlled input, so both checksums are verified on load, every offset and size is checked, delta chains
// are bounded and walked without recursion, and inflated sizes stay under the shell's string limit.
import { inflateRaw } from '../inflate.js';
import { sha1 } from '../sha1.js';
import { GitError, fatal, msg, type Values } from './gitcore.js';

export const MAX_CHAIN = 4096; // deltas on top of one base (real git refuses to write deeper than 4095)
export const MAX_PACKS = 256;
export const MAX_NAMES = 1_000_000; // objects named by all the packs of one repository
export const CACHE_BYTES = 64 << 20; // object contents one repository keeps for reuse, all caches together
const KINDS: Record<number, string> = { 1: 'commit', 2: 'tree', 3: 'blob', 4: 'tag' };

/** Objects, commits and delta bases one command keeps for reuse, under a single byte budget per repository: an entry
 * over a quarter of it is not kept, and the whole cache is dropped when the next entry would not fit. */
export class ObjectCache {
  static peak = 0; // the most any cache has held, for tests
  private items = new Map<string, unknown>();
  private used = 0;
  constructor(readonly budget = CACHE_BYTES) {}
  get<T>(key: string): T | undefined { return this.items.get(key) as T | undefined; }
  put(key: string, value: unknown, size: number): void {
    if (size > this.budget / 4) return;
    if (this.used + size > this.budget) { this.items.clear(); this.used = 0; }
    this.items.set(key, value);
    this.used += size;
    if (this.used > ObjectCache.peak) ObjectCache.peak = this.used;
  }
}

/** A broken object entry; the caller names the object it was looking for. */
export class Corrupt extends Error {}

const badPack = (path: string, why: string, values: Values = {}): GitError => fatal('badPack', { path, why: msg(why, values) });
const hex = (b: Uint8Array) => { let s = ''; for (const x of b) s += x.toString(16).padStart(2, '0'); return s; };
const sameBytes = (x: Uint8Array, y: Uint8Array) => x.length === y.length && x.every((v, i) => v === y[i]);

export class Pack {
  readonly names: string[] = [];
  readonly offsets = new Map<string, number>();
  constructor(readonly path: string, idx: Uint8Array, readonly pack: Uint8Array, readonly limit: number, readonly check: () => void,
    private readonly cache: ObjectCache) {
    const ipath = path.slice(0, -5) + '.idx';
    const iv = new DataView(idx.buffer, idx.byteOffset, idx.byteLength);
    if (idx.length < 1072 || idx[0] !== 0xff || idx[1] !== 0x74 || idx[2] !== 0x4f || idx[3] !== 0x63) throw badPack(ipath, 'packBadSignature');
    if (iv.getUint32(4) !== 2) throw badPack(ipath, 'packBadVersion', { version: String(iv.getUint32(4)) });
    const fan: number[] = [];
    for (let i = 0; i < 256; i++) fan.push(iv.getUint32(8 + 4 * i));
    const n = fan[255];
    const large = idx.length - 1072 - 28 * n;
    if (fan.some((f, i) => i < 255 && f > fan[i + 1]) || large < 0 || large % 8) throw badPack(ipath, 'packTruncated');
    if (sha1(idx.subarray(0, idx.length - 20)) !== hex(idx.subarray(idx.length - 20))) throw badPack(ipath, 'packBadChecksum');
    if (pack.length < 32 || pack[0] !== 0x50 || pack[1] !== 0x41 || pack[2] !== 0x43 || pack[3] !== 0x4b) throw badPack(path, 'packBadSignature');
    const pv = new DataView(pack.buffer, pack.byteOffset, pack.byteLength);
    const version = pv.getUint32(4), count = pv.getUint32(8);
    if (version !== 2 && version !== 3) throw badPack(path, 'packBadVersion', { version: String(version) });
    if (sha1(pack.subarray(0, pack.length - 20)) !== hex(pack.subarray(pack.length - 20))) throw badPack(path, 'packBadChecksum');
    if (count !== n || !sameBytes(idx.subarray(idx.length - 40, idx.length - 20), pack.subarray(pack.length - 20))) throw badPack(path, 'packNoMatch');
    if (n * 2 > pack.length - 32) throw badPack(ipath, 'packTooMany'); // every entry takes at least two bytes
    const nbig = large / 8, end = pack.length - 20;
    for (let i = 0; i < n; i++) {
      if ((i & 1023) === 0) check();
      const name = hex(idx.subarray(1032 + 20 * i, 1052 + 20 * i));
      const b = idx[1032 + 20 * i];
      if ((i && name <= this.names[i - 1]) || fan[b] <= i || (b > 0 && fan[b - 1] > i)) throw badPack(ipath, 'packNotSorted');
      let off = iv.getUint32(1032 + 24 * n + 4 * i);
      if (off >= 0x80000000) {
        const k = off - 0x80000000;
        if (k >= nbig) throw badPack(ipath, 'packBadOffset');
        const at = 1032 + 28 * n + 8 * k;
        off = iv.getUint32(at) * 0x100000000 + iv.getUint32(at + 4); // exact below 2^53; anything that large is out of range anyway
      }
      if (!(off >= 12 && off < end)) throw badPack(ipath, 'packBadOffset');
      this.names.push(name);
      this.offsets.set(name, off);
    }
  }

  withPrefix(prefix: string): string[] {
    let lo = 0, hi = this.names.length;
    while (lo < hi) { const mid = (lo + hi) >> 1; if (this.names[mid] < prefix) lo = mid + 1; else hi = mid; }
    const out: string[] = [];
    for (let i = lo; i < this.names.length && this.names[i].startsWith(prefix) && out.length < 2; i++) out.push(this.names[i]);
    return out;
  }

  /** The object's kind and contents; Corrupt for a broken entry (the caller still verifies the object id). */
  read(sha: string): [string, Uint8Array] {
    return this.unpack(this.offsets.get(sha)!);
  }

  private byte(i: number): number {
    if (i >= this.pack.length - 20) throw new Corrupt();
    return this.pack[i];
  }

  private unpack(off: number): [string, Uint8Array] {
    const chain: [number, number, number][] = []; // (entry offset, data offset, delta size), newest first
    const seen = new Set<number>();
    let kind: string, data: Uint8Array;
    for (;;) {
      this.check();
      const hit = this.cache.get<[string, Uint8Array]>(`${this.path}:${off}`);
      if (hit) { [kind, data] = hit; break; }
      if (seen.has(off) || chain.length >= MAX_CHAIN) throw new Corrupt();
      seen.add(off);
      let c = this.byte(off);
      const typ = (c >> 4) & 7;
      let size = c & 15, mul = 16, pos = off + 1;
      while (c & 0x80) {
        c = this.byte(pos++);
        size += (c & 0x7f) * mul;
        mul *= 128;
        if (mul > 2 ** 70) throw new Corrupt();
      }
      if (size > this.limit) throw new Corrupt();
      if (KINDS[typ]) {
        kind = KINDS[typ];
        data = this.inflate(pos, size);
        this.remember(off, kind, data);
        break;
      }
      let base: number;
      if (typ === 6) { // offset delta: the base is earlier in this pack
        c = this.byte(pos++);
        let back = c & 0x7f;
        while (c & 0x80) {
          c = this.byte(pos++);
          back = (back + 1) * 128 + (c & 0x7f);
          if (back > off) throw new Corrupt();
        }
        base = off - back;
        if (!(base >= 12 && base < off)) throw new Corrupt();
      } else if (typ === 7) { // reference delta: the base is named by its id (in this pack)
        if (pos + 20 > this.pack.length - 20) throw new Corrupt();
        const b = this.offsets.get(hex(this.pack.subarray(pos, pos + 20)));
        pos += 20;
        if (b === undefined) throw new Corrupt();
        base = b;
      } else throw new Corrupt();
      chain.push([off, pos, size]);
      off = base;
    }
    for (let i = chain.length - 1; i >= 0; i--) {
      const [entry, pos, size] = chain[i];
      this.check(); // each step can copy up to the string limit
      data = applyDelta(data, this.inflate(pos, size), this.limit, this.check);
      this.remember(entry, kind, data);
    }
    return [kind, data];
  }

  private inflate(pos: number, size: number): Uint8Array {
    const p = this.pack;
    const end = p.length - 20;
    if (pos + 2 > end || (p[pos] & 0x0f) !== 8 || p[pos] >> 4 > 7 || p[pos + 1] & 0x20 || ((p[pos] << 8) | p[pos + 1]) % 31 !== 0) throw new Corrupt();
    let out: Uint8Array;
    try {
      out = inflateRaw(p.subarray(pos + 2, end), size); // OutputLimitError past the stated size
    } catch {
      throw new Corrupt();
    }
    if (out.length !== size) throw new Corrupt();
    return out;
  }

  private remember(off: number, kind: string, data: Uint8Array): void {
    this.cache.put(`${this.path}:${off}`, [kind, data], data.length);
  }
}

function varint(d: Uint8Array, i: number): [number, number] {
  let v = 0, mul = 1;
  for (;;) {
    if (i >= d.length) throw new Corrupt();
    const c = d[i++];
    v += (c & 0x7f) * mul;
    mul *= 128;
    if (!(c & 0x80)) return [v, i];
    if (mul > 2 ** 63) throw new Corrupt();
  }
}

/** Rebuilds an object from its base and a delta: a header (base size, result size), then copy and insert steps. */
export function applyDelta(base: Uint8Array, delta: Uint8Array, limit: number, check: () => void): Uint8Array {
  let [src, i] = varint(delta, 0);
  let dst: number;
  [dst, i] = varint(delta, i);
  if (src !== base.length || dst > limit) throw new Corrupt();
  const out = new Uint8Array(dst);
  let len = 0, steps = 0;
  const n = delta.length;
  while (i < n) {
    if ((++steps & 0xfff) === 0) check();
    const c = delta[i++];
    if (c & 0x80) { // copy from the base: offset and size bytes present per bit
      let off = 0, size = 0;
      for (let k = 0; k < 4; k++) if (c & (1 << k)) { if (i >= n) throw new Corrupt(); off += delta[i++] * 2 ** (8 * k); }
      for (let k = 0; k < 3; k++) if (c & (0x10 << k)) { if (i >= n) throw new Corrupt(); size += delta[i++] << (8 * k); }
      size = size || 0x10000;
      if (off + size > base.length || len + size > dst) throw new Corrupt();
      out.set(base.subarray(off, off + size), len);
      len += size;
    } else if (c) { // insert the next c bytes of the delta
      if (i + c > n || len + c > dst) throw new Corrupt();
      out.set(delta.subarray(i, i + c), len);
      len += c;
      i += c;
    } else throw new Corrupt();
  }
  if (len !== dst) throw new Corrupt();
  return out;
}
