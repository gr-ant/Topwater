/**
 * Carries arbitrary bytes through the shell's string pipes and redirects without loss (Python's "surrogateescape"):
 * valid UTF-8 decodes as usual, and each byte that isn't valid UTF-8 becomes the lone surrogate U+DC80..U+DCFF.
 * Text is unaffected; binary output (gzip -c, tar cf -) survives `|` and `>`, and files store the original bytes.
 */
const FIRST = 0xdc80, LAST = 0xdcff;
const strict = new TextDecoder('utf-8', { fatal: true });
const lossy = new TextDecoder('utf-8');
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
    if (c >= FIRST && c <= LAST) { flush(i); parts.push(c - FIRST + 0x80); start = i + 1; }
  }
  flush(text.length);
  return Uint8Array.from(parts);
}

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
