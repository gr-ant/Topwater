// SHA-1 (FIPS 180-4), synchronous so it runs anywhere (git names its objects by it; crypto.subtle is async only).

const w = new Int32Array(80);

/** One round of 80 steps over the 64-byte block at off; returns nothing, updates h in place. */
function block(h: Int32Array, p: Uint8Array, off: number): void {
  for (let i = 0; i < 16; i++, off += 4) w[i] = (p[off] << 24) | (p[off + 1] << 16) | (p[off + 2] << 8) | p[off + 3];
  for (let i = 16; i < 80; i++) { const x = w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16]; w[i] = (x << 1) | (x >>> 31); }
  let a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], t: number;
  let i = 0;
  for (; i < 20; i++) { t = (((a << 5) | (a >>> 27)) + ((b & c) | (~b & d)) + 0x5a827999 + e + w[i]) | 0; e = d; d = c; c = (b << 30) | (b >>> 2); b = a; a = t; }
  for (; i < 40; i++) { t = (((a << 5) | (a >>> 27)) + (b ^ c ^ d) + 0x6ed9eba1 + e + w[i]) | 0; e = d; d = c; c = (b << 30) | (b >>> 2); b = a; a = t; }
  for (; i < 60; i++) { t = (((a << 5) | (a >>> 27)) + ((b & c) | (b & d) | (c & d)) + 0x8f1bbcdc + e + w[i]) | 0; e = d; d = c; c = (b << 30) | (b >>> 2); b = a; a = t; }
  for (; i < 80; i++) { t = (((a << 5) | (a >>> 27)) + (b ^ c ^ d) + 0xca62c1d6 + e + w[i]) | 0; e = d; d = c; c = (b << 30) | (b >>> 2); b = a; a = t; }
  h[0] = (h[0] + a) | 0; h[1] = (h[1] + b) | 0; h[2] = (h[2] + c) | 0; h[3] = (h[3] + d) | 0; h[4] = (h[4] + e) | 0;
}

/** The SHA-1 digest of data as 40 lowercase hex digits. */
export function sha1(data: Uint8Array): string {
  const h = new Int32Array([0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476, 0xc3d2e1f0]);
  const len = data.length, whole = len & ~63;
  for (let off = 0; off < whole; off += 64) block(h, data, off); // full blocks straight from the input, no copy
  const tail = new Uint8Array(len - whole < 56 ? 64 : 128);
  tail.set(data.subarray(whole));
  tail[len - whole] = 0x80;
  const view = new DataView(tail.buffer);
  view.setUint32(tail.length - 8, Math.floor(len / 0x20000000));
  view.setUint32(tail.length - 4, (len * 8) >>> 0);
  for (let off = 0; off < tail.length; off += 64) block(h, tail, off);
  return Array.from(h, x => (x >>> 0).toString(16).padStart(8, '0')).join('');
}
