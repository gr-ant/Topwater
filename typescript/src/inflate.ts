// A synchronous DEFLATE (RFC 1951) decoder with gzip (RFC 1952) framing, so archives unpack without node:zlib.
import { crc32 } from './commands/zipfile.js';

export type InflateErrorKind = 'eof' | 'crc' | 'format';
export class InflateError extends Error {
  /** For 'eof' from inflateRaw and gunzip: what was decoded before the data ran out. */
  partial?: Uint8Array;
  constructor(readonly kind: InflateErrorKind, message: string = kind) { super(message); }
}

/** Runs `f`, adding what `out` holds to an 'eof' error. */
function keepPartial<T>(out: Out, f: () => T): T {
  try { return f(); } catch (e) {
    if (e instanceof InflateError && e.kind === 'eof') e.partial = out.buf.subarray(0, out.len);
    throw e;
  }
}
/** The output would exceed the caller's cap. */
export class OutputLimitError extends Error {
  /** `partial`: what was decoded before the limit (only from inflateRaw and gunzip). */
  constructor(readonly partial?: Uint8Array) { super('output limit exceeded'); }
}

const LEN_BASE = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
const LEN_EXTRA = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
const DIST_BASE = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
const DIST_EXTRA = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
const CL_ORDER = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

interface Table { bits: number; t: Uint32Array } // entry: (len << 16) | symbol; 0 = unused

/** A canonical Huffman decoding table indexed by the next `bits` input bits (LSB first). */
function build(lens: ArrayLike<number>, n: number): Table {
  const count = new Uint16Array(16);
  let max = 0;
  for (let i = 0; i < n; i++) { count[lens[i]]++; if (lens[i] > max) max = lens[i]; }
  count[0] = 0;
  let left = 1;
  for (let l = 1; l <= 15; l++) { left = left * 2 - count[l]; if (left < 0) throw new InflateError('format', 'over-subscribed code'); }
  if (left > 0 && max > 1) throw new InflateError('format', 'incomplete code');
  if (max === 0) return { bits: 1, t: new Uint32Array(2) };
  const next = new Uint16Array(16);
  for (let l = 1, code = 0; l <= 15; l++) { code = (code + count[l - 1]) << 1; next[l] = code; }
  const t = new Uint32Array(1 << max);
  for (let s = 0; s < n; s++) {
    const l = lens[s];
    if (!l) continue;
    const c = next[l]++;
    let rev = 0;
    for (let k = 0; k < l; k++) rev |= ((c >> k) & 1) << (l - 1 - k);
    for (let i = rev; i < t.length; i += 1 << l) t[i] = (l << 16) | s;
  }
  return { bits: max, t };
}

let fixed: [Table, Table] | undefined;
function fixedTables(): [Table, Table] {
  if (!fixed) {
    const l = new Uint8Array(288).fill(8, 0, 144).fill(9, 144, 256).fill(7, 256, 280).fill(8, 280);
    fixed = [build(l, 288), build(new Uint8Array(32).fill(5), 32)];
  }
  return fixed;
}

interface Out { buf: Uint8Array; len: number }

/** Inflates one raw DEFLATE stream starting at data[start], appending to `out`; returns the byte offset after it. */
function run(data: Uint8Array, start: number, out: Out, maxOut: number, base: number): number {
  let pos = start, bitbuf = 0, bitcnt = 0;
  const end = data.length;
  const fill = () => { while (bitcnt <= 24) { bitbuf |= (pos < end ? data[pos] : 0) << bitcnt; pos++; bitcnt += 8; } };
  const check = () => { if (pos * 8 - bitcnt > end * 8) throw new InflateError('eof'); };
  const bits = (n: number) => {
    if (n === 0) return 0;
    fill();
    const v = bitbuf & ((1 << n) - 1);
    bitbuf >>>= n; bitcnt -= n;
    return v;
  };
  const ensure = (extra: number) => {
    const need = out.len + extra;
    if (need > maxOut) throw new OutputLimitError(out.buf.subarray(0, out.len));
    if (need > out.buf.length) {
      const nb = new Uint8Array(Math.min(maxOut, Math.max(need, out.buf.length * 2, 1024)));
      nb.set(out.buf.subarray(0, out.len));
      out.buf = nb;
    }
  };
  const sym = (tb: Table): number => {
    fill();
    const e = tb.t[bitbuf & ((1 << tb.bits) - 1)];
    const l = e >>> 16;
    if (l === 0) { check(); throw new InflateError('format', 'invalid code'); }
    bitbuf >>>= l; bitcnt -= l;
    return e & 0xffff;
  };

  for (let last = 0; !last;) {
    last = bits(1);
    const type = bits(2);
    check();
    if (type === 0) {
      bitcnt -= bitcnt & 7; // drop the rest of the current byte
      pos -= bitcnt >> 3; bitcnt = 0; bitbuf = 0; // give back whole bytes still buffered
      if (pos + 4 > end) throw new InflateError('eof');
      const n = data[pos] | (data[pos + 1] << 8), nn = data[pos + 2] | (data[pos + 3] << 8);
      if ((n ^ 0xffff) !== nn) throw new InflateError('format', 'stored length mismatch');
      pos += 4;
      if (pos + n > end) throw new InflateError('eof');
      ensure(n);
      out.buf.set(data.subarray(pos, pos + n), out.len);
      out.len += n; pos += n;
      continue;
    }
    if (type === 3) throw new InflateError('format', 'bad block type');
    let lit: Table, dist: Table;
    if (type === 1) [lit, dist] = fixedTables();
    else {
      const nlen = bits(5) + 257, ndist = bits(5) + 1, ncode = bits(4) + 4;
      check();
      if (nlen > 286 || ndist > 30) throw new InflateError('format', 'too many codes');
      const cl = new Uint8Array(19);
      for (let i = 0; i < ncode; i++) cl[CL_ORDER[i]] = bits(3);
      check();
      const clt = build(cl, 19);
      const lens = new Uint8Array(nlen + ndist);
      for (let i = 0; i < nlen + ndist;) {
        const s = sym(clt);
        if (s < 16) { lens[i++] = s; continue; }
        let v = 0, rep: number;
        if (s === 16) { if (i === 0) throw new InflateError('format', 'no previous length'); v = lens[i - 1]; rep = 3 + bits(2); }
        else if (s === 17) rep = 3 + bits(3);
        else rep = 11 + bits(7);
        check();
        if (i + rep > nlen + ndist) throw new InflateError('format', 'too many lengths');
        while (rep--) lens[i++] = v;
      }
      check();
      if (lens[256] === 0) throw new InflateError('format', 'missing end-of-block');
      lit = build(lens.subarray(0, nlen), nlen);
      dist = build(lens.subarray(nlen), ndist);
    }
    for (;;) {
      const s = sym(lit);
      check();
      if (s < 256) { ensure(1); out.buf[out.len++] = s; continue; }
      if (s === 256) break;
      if (s > 285) throw new InflateError('format', 'invalid length code');
      const li = s - 257;
      const len = LEN_BASE[li] + bits(LEN_EXTRA[li]);
      const ds = sym(dist);
      if (ds > 29) throw new InflateError('format', 'invalid distance code');
      const d = DIST_BASE[ds] + bits(DIST_EXTRA[ds]);
      check();
      if (d > out.len - base) throw new InflateError('format', 'distance too far back');
      ensure(len);
      const b = out.buf;
      for (let i = 0, o = out.len; i < len; i++, o++) b[o] = b[o - d];
      out.len += len;
    }
  }
  return pos - (bitcnt >> 3); // unread whole bytes go back
}

/** Decodes a raw DEFLATE stream; throws OutputLimitError past maxOut bytes of output. */
export function inflateRaw(data: Uint8Array, maxOut = Infinity): Uint8Array {
  const out: Out = { buf: new Uint8Array(Math.min(maxOut, Math.max(1024, data.length * 3))), len: 0 };
  keepPartial(out, () => run(data, 0, out, maxOut, 0));
  return out.buf.subarray(0, out.len);
}

/** Decodes one or more concatenated gzip members; bytes after the last member are ignored. */
export function gunzip(data: Uint8Array, maxOut = Infinity): Uint8Array {
  const out: Out = { buf: new Uint8Array(Math.min(maxOut, Math.max(1024, data.length * 3))), len: 0 };
  let pos = 0;
  return keepPartial(out, () => { do {
    if (pos + 10 > data.length) throw new InflateError('eof');
    if (data[pos] !== 0x1f || data[pos + 1] !== 0x8b || data[pos + 2] !== 8) throw new InflateError('format', 'bad gzip header');
    const flg = data[pos + 3];
    if (flg & 0xe0) throw new InflateError('format', 'unknown header flags');
    const hdr = pos;
    pos += 10;
    if (flg & 4) {
      if (pos + 2 > data.length) throw new InflateError('eof');
      pos += 2 + (data[pos] | (data[pos + 1] << 8));
    }
    for (const f of [8, 16]) if (flg & f) {
      while (pos < data.length && data[pos] !== 0) pos++;
      pos++;
    }
    if (flg & 2) {
      if (pos + 2 > data.length) throw new InflateError('eof');
      if ((crc32(data.subarray(hdr, pos)) & 0xffff) !== (data[pos] | (data[pos + 1] << 8))) throw new InflateError('format', 'header crc mismatch');
      pos += 2;
    }
    if (pos > data.length) throw new InflateError('eof');
    const base = out.len;
    pos = run(data, pos, out, maxOut, base);
    if (pos + 8 > data.length) throw new InflateError('eof');
    const v = new DataView(data.buffer, data.byteOffset, data.byteLength);
    if (crc32(out.buf.subarray(base, out.len)) !== v.getUint32(pos, true)) throw new InflateError('crc');
    // zlib reports a bad ISIZE as a format violation, not a CRC error
    if (((out.len - base) >>> 0) !== v.getUint32(pos + 4, true)) throw new InflateError('format', 'length mismatch');
    pos += 8;
  } while (pos + 1 < data.length && data[pos] === 0x1f && data[pos + 1] === 0x8b);
  return out.buf.subarray(0, out.len); });
}
