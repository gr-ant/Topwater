// Compression through the web-standard CompressionStream (Node 22 and browsers).
// shortcut: CompressionStream has no level setting; levels are accepted but ignored, vendor a deflate if exact levels matter
export async function deflate(kind: 'gzip' | 'deflate-raw', data: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([data as never]).stream().pipeThrough(new CompressionStream(kind));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

/** Raw DEFLATE decoded natively (much faster than the inflate.ts decoder), failing once the output passes `max` bytes. */
export async function inflateRawNative(data: Uint8Array, max: number): Promise<Uint8Array> {
  const reader = new Blob([data as never]).stream().pipeThrough(new DecompressionStream('deflate-raw')).getReader();
  const chunks: Uint8Array[] = [];
  let size = 0;
  for (let r = await reader.read(); !r.done; r = await reader.read()) {
    if ((size += r.value.length) > max) { await reader.cancel(); throw new Error('output limit exceeded'); }
    chunks.push(r.value);
  }
  const out = new Uint8Array(size);
  let at = 0;
  for (const c of chunks) { out.set(c, at); at += c.length; }
  return out;
}

const LEN_BASE = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
const LEN_EXTRA = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
const DIST_BASE = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
const DIST_EXTRA = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

/**
 * zlib (RFC 1950) compression done synchronously: one fixed-Huffman block with LZ77 matches found through hash chains.
 * Less tight than zlib's own (no dynamic trees) but valid for any reader; for callers that cannot await (git's objects).
 */
export function zlibSync(data: Uint8Array): Uint8Array {
  let out = new Uint8Array(1024 + (data.length * 9 >> 3));
  let o = 0, buf = 0, cnt = 0;
  const put = (v: number, n: number) => { // n bits of v, least significant first
    buf |= v << cnt; cnt += n;
    while (cnt >= 8) {
      if (o >= out.length) { const nb = new Uint8Array(out.length * 2); nb.set(out); out = nb; }
      out[o++] = buf & 255; buf >>>= 8; cnt -= 8;
    }
  };
  const rev = (v: number, n: number) => { let r = 0; for (let i = 0; i < n; i++) { r = (r << 1) | (v & 1); v >>= 1; } return r; };
  const code = (s: number) => { // a literal/length symbol in the fixed code
    if (s < 144) put(rev(0x30 + s, 8), 8);
    else if (s < 256) put(rev(0x190 + s - 144, 9), 9);
    else if (s < 280) put(rev(s - 256, 7), 7);
    else put(rev(0xc0 + s - 280, 8), 8);
  };
  put(0x78, 8); put(0x01, 8);
  put(1, 1); put(1, 2); // the last block, fixed Huffman
  const WIN = 32768, head = new Int32Array(1 << 15).fill(-1), prev = new Int32Array(WIN);
  const hash = (i: number) => ((data[i] << 10) ^ (data[i + 1] << 5) ^ data[i + 2]) & 0x7fff;
  const insert = (i: number) => { if (i + 2 < data.length) { const h = hash(i); prev[i & (WIN - 1)] = head[h]; head[h] = i; } };
  let i = 0;
  while (i < data.length) {
    let best = 0, dist = 0;
    if (i + 2 < data.length) {
      const max = Math.min(258, data.length - i);
      for (let c = head[hash(i)], chain = 32; c >= 0 && i - c <= WIN && chain-- > 0; c = prev[c & (WIN - 1)]) {
        let l = 0;
        while (l < max && data[c + l] === data[i + l]) l++;
        if (l > best) { best = l; dist = i - c; if (l === max) break; }
      }
    }
    if (best >= 3) {
      let k = LEN_BASE.length - 1;
      while (LEN_BASE[k] > best) k--;
      code(257 + k); put(best - LEN_BASE[k], LEN_EXTRA[k]);
      let d = DIST_BASE.length - 1;
      while (DIST_BASE[d] > dist) d--;
      put(rev(d, 5), 5); put(dist - DIST_BASE[d], DIST_EXTRA[d]);
      for (let e = i + best; i < e; i++) insert(i);
    } else { code(data[i]); insert(i); i++; }
  }
  code(256);
  if (cnt) put(0, 8 - cnt);
  let a = 1, b = 0;
  for (let k = 0; k < data.length; k++) { a = (a + data[k]) % 65521; b = (b + a) % 65521; }
  for (const x of [b >> 8, b & 255, a >> 8, a & 255]) put(x, 8);
  return out.slice(0, o);
}
