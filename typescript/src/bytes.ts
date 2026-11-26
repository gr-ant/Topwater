/**
 * Carries arbitrary bytes through the shell's string pipes and redirects without loss (Python's "surrogateescape"):
 * valid UTF-8 decodes as usual, and each byte that isn't valid UTF-8 becomes the lone surrogate U+DC80..U+DCFF.
 * Text is unaffected; binary output (gzip -c, tar cf -) survives `|` and `>`, and files store the original bytes.
 */
const FIRST = 0xdc80, LAST = 0xdcff;
// ignoreBOM: a leading U+FEFF is content (a CSV from Excel keeps its three bytes through `cat a > b`)
const strict = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
const lossy = new TextDecoder('utf-8', { ignoreBOM: true });
const encoder = new TextEncoder();

export function decodeBytes(data: Uint8Array): string {
  try { return strict.decode(data); } catch { /* has invalid sequences: decode by hand */ }
  let out = '';
  let i = 0;
  while (i < data.length) {
    const b = data[i];
    const len = b < 0x80 ? 1 : b >= 0xc2 && b <= 0xdf ? 2 : b >= 0xe0 && b <= 0xef ? 3 : b >= 0xf0 && b <= 0xf4 ? 4 : 0;
    if (len === 1) { out += String.fromCharCode(b); i++; continue; }
    if (len > 0 && i + len <= data.length) {
      try { out += strict.decode(data.subarray(i, i + len)); i += len; continue; } catch { /* fall through */ }
    }
    out += String.fromCharCode(FIRST + b - 0x80);
    i++;
  }
  return out;
}

export function hasEscapes(text: string): boolean {
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (c >= FIRST && c <= LAST) return true;
  }
  return false;
}

/** The bytes `text` stands for: UTF-8, with escaped bytes restored. */
export function encodeText(text: string): Uint8Array {
  if (!hasEscapes(text)) return encoder.encode(text);
  const parts: number[] = [];
  let start = 0;
  const flush = (end: number) => { if (end > start) for (const b of encoder.encode(text.slice(start, end))) parts.push(b); };
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff && (text.charCodeAt(i + 1) & 0xfc00) === 0xdc00) i++; // a pair's low half (U+10000+) is no escape
    else if (c >= FIRST && c <= LAST) { flush(i); parts.push(c - FIRST + 0x80); start = i + 1; }
  }
  flush(text.length);
  return Uint8Array.from(parts);
}

/** Escaped bytes that together spell valid UTF-8 become those characters (`printf '\\xc3\\xa9'` is é); the rest stay escaped. */
export const rejoin = (text: string): string => hasEscapes(text) ? decodeBytes(encodeText(text)) : text;

/** For text leaving the shell: escaped bytes show as U+FFFD, as a lossy UTF-8 decode would. */
export function printable(text: string): string {
  return hasEscapes(text) ? lossy.decode(encodeText(text)) : text;
}

export function utf8Length(text: string): number {
  let n = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (c < 0x80) n++;
    else if (c < 0x800) n += 2;
    else if (c >= 0xd800 && c <= 0xdbff && i + 1 < text.length && (text.charCodeAt(i + 1) & 0xfc00) === 0xdc00) { n += 4; i++; }
    else if (c >= FIRST && c <= LAST) n++;
    else n += 3;
  }
  return n;
}

/** Text as written by the text API: lossy UTF-8 of raw bytes. */
export function bytesToText(data: Uint8Array): string { return lossy.decode(data); }
export function textToBytes(text: string): Uint8Array { return encodeText(text); }

// Portable stand-ins for Node's Buffer, so the core runs in browsers too.
export function concatBytes(parts: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}
export const toHex = (data: Uint8Array) => Array.from(data, b => b.toString(16).padStart(2, '0')).join('');
export function toBase64(data: Uint8Array): string {
  let s = '';
  for (let i = 0; i < data.length; i += 0x8000) s += String.fromCharCode(...data.subarray(i, i + 0x8000));
  return btoa(s);
}
/** Lenient like Buffer.from(s, 'base64'): skips characters outside the alphabet, accepts URL-safe and missing padding. */
export function fromBase64(text: string): Uint8Array {
  let s = text.replace(/-/g, '+').replace(/_/g, '/');
  const pad = s.indexOf('=');
  if (pad >= 0) s = s.slice(0, pad); // Buffer stops at the first padding character
  s = s.replace(/[^A-Za-z0-9+/]/g, '');
  if (s.length % 4 === 1) s = s.slice(0, -1);
  s += '='.repeat((4 - s.length % 4) % 4);
  return Uint8Array.from(atob(s), c => c.charCodeAt(0));
}
