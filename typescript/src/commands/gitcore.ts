// git's on-disk formats and algorithms for the `git` command: loose objects, the v2 index, refs, config, ignore rules,
// line diffs and three-way merges. A port of python/src/topwater/commands/gitcore.py (the reference); keep them in step.
// Everything read from .git is untrusted: sizes, depths and walks are bounded, and no path is followed through a link.
import { specMessage } from '../command.js';
import { decodeBytes, encodeText } from '../bytes.js';
import { sha1 } from '../sha1.js';

export const MAX_WALK = 1_000_000; // commits one history walk may visit
export const MAX_DEPTH = 256; // nested trees, and directories in one path
export const DIFF_BUDGET = 1_000_000; // Myers steps per file pair before falling back to "all removed, all added"
export const EMPTY_TREE = '4b825dc642cb6eb9a060e54bf8d69288fbee4904';
export const HEX = /^[0-9a-f]{4,40}$/;
export const SHA = /^[0-9a-f]{40}$/;

/** Ends the git command: message (whole lines, already worded) on stderr and the exit code. */
export class GitError extends Error {
  constructor(message: string, readonly code = 128) { super(message); }
}

/** A date git cannot read. */
export class DateError extends Error {}

export type Values = Record<string, string>;
export const msg = (key: string, values: Values = {}) => specMessage('git', key, values);
export const fatal = (key: string, values: Values = {}, code = 128) => new GitError(msg(key, values), code);

const enc = new TextEncoder();
export function objectId(kind: string, data: Uint8Array): string {
  const head = enc.encode(`${kind} ${data.length}\0`);
  const all = new Uint8Array(head.length + data.length);
  all.set(head);
  all.set(data, head.length);
  return sha1(all);
}

// strings as Python sees them: code points, and byte order for paths

export const cpLen = (s: string) => { let n = 0; for (const _ of s) n++; return n; };
export const cpSlice = (s: string, a: number, b?: number) => [...s].slice(a, b).join('');
export const ljust = (s: string, n: number) => s + ' '.repeat(Math.max(0, n - cpLen(s)));
export const rjust = (s: string, n: number) => ' '.repeat(Math.max(0, n - cpLen(s))) + s;

/** Code point order (Python's plain sort of str). */
export function cmpCp(a: string, b: string): number {
  if (a === b) return 0;
  const n = Math.min(a.length, b.length);
  for (let i = 0; i < n; i++) {
    const x = a.charCodeAt(i), y = b.charCodeAt(i);
    if (x === y) continue;
    const sx = x >= 0xd800 && x <= 0xdfff, sy = y >= 0xd800 && y <= 0xdfff;
    if (!sx && !sy) return x - y;
    return (a.codePointAt(i) ?? 0) - (b.codePointAt(i) ?? 0);
  }
  return a.length - b.length;
}

export function cmpBytes(x: Uint8Array, y: Uint8Array): number {
  const n = Math.min(x.length, y.length);
  for (let i = 0; i < n; i++) if (x[i] !== y[i]) return x[i] - y[i];
  return x.length - y.length;
}

/** Byte order of the UTF-8 (surrogate-escaped) text: how git sorts paths. */
export const cmpPath = (a: string, b: string) => a === b ? 0 : cmpBytes(encodeText(a), encodeText(b));
export const sortPaths = (xs: Iterable<string>) => [...xs].sort(cmpPath);
export const sortCp = (xs: Iterable<string>) => [...xs].sort(cmpCp);

export const isDigits = (s: string | undefined) => s !== undefined && /^\d+$/.test(s);
/** str.partition: [head, found separator, tail]. */
export function partition(s: string, sep: string): [string, string, string] {
  const i = s.indexOf(sep);
  return i < 0 ? [s, '', ''] : [s.slice(0, i), sep, s.slice(i + sep.length)];
}
export function rpartition(s: string, sep: string): [string, string, string] {
  const i = s.lastIndexOf(sep);
  return i < 0 ? ['', '', s] : [s.slice(0, i), sep, s.slice(i + sep.length)];
}
export const rstripChars = (s: string, chars: string) => { let e = s.length; while (e > 0 && chars.includes(s[e - 1])) e--; return s.slice(0, e); };
export const lstripChars = (s: string, chars: string) => { let i = 0; while (i < s.length && chars.includes(s[i])) i++; return s.slice(i); };
export const stripChars = (s: string, chars: string) => lstripChars(rstripChars(s, chars), chars);

// ---------- identity and dates ----------

const MONTHS = 'jan feb mar apr may jun jul aug sep oct nov dec'.split(' ');
const MON = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const DAY = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

/** Seconds since the epoch of a naive UTC date, refusing what Python's datetime refuses. */
function utcSeconds(y: number, mo: number, d: number, h: number, mi: number, s: number): number {
  const days = [31, (y % 4 === 0 && y % 100 !== 0) || y % 400 === 0 ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  if (y < 1 || y > 9999 || mo < 1 || mo > 12 || d < 1 || d > days[mo - 1] || h > 23 || mi > 59 || s > 59) throw new DateError();
  const t = new Date(0);
  t.setUTCFullYear(y, mo - 1, d);
  t.setUTCHours(h, mi, s, 0);
  return Math.floor(t.getTime() / 1000);
}

const zoneMinutes = (zone: string) => (Number(zone.slice(1, 3)) * 60 + Number(zone.slice(3, 5))) * (zone[0] === '-' ? -1 : 1);

/** GIT_AUTHOR_DATE / --date: '@1700000000 +0100', '1700000000 +0100', ISO 8601 or RFC 2822 -> [epoch, '+hhmm']. */
export function parseDate(text: string, nowMs: number): [number, string] {
  const s = text.trim();
  let m = /^@?(\d+)(?:\s+([+-]\d{4}))?$/.exec(s);
  if (m) return [Number(m[1]), m[2] ?? '+0000'];
  m = /^(?:\w{3},\s*)?(\d{1,2})\s+(\w{3})\s+(\d{4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?\s*([+-]\d{4})?$/.exec(s);
  if (m) {
    const mon = MONTHS.indexOf(m[2].toLowerCase());
    if (mon < 0) throw new DateError();
    return withZone(utcSeconds(+m[3], mon + 1, +m[1], +m[4], +m[5], +(m[6] ?? 0)), m[7] ?? '+0000');
  }
  m = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{1,2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?)?\s*(Z|[+-]\d{2}:?\d{2})?$/.exec(s);
  if (m) {
    const z = m[7] ?? '+0000';
    return withZone(utcSeconds(+m[1], +m[2], +m[3], +(m[4] ?? 0), +(m[5] ?? 0), +(m[6] ?? 0)), z === 'Z' ? '+0000' : z.replace(':', ''));
  }
  if (s === 'now') return [Math.floor(nowMs / 1000), '+0000'];
  throw new DateError();
}

const withZone = (local: number, zone: string): [number, string] => [local - zoneMinutes(zone) * 60, zone];

function zoneDelta(zone: string): number {
  const v = zoneMinutes(zone);
  return Number.isFinite(v) && /^[+-]?\d{4}/.test(zone) ? v : 0;
}

/** The wall-clock time in zone, as a Date read through its getUTC* methods. */
export const localTime = (epoch: number, zone: string) => new Date((epoch + zoneDelta(zone) * 60) * 1000);

const p2 = (n: number) => String(n).padStart(2, '0');
const ymd = (t: Date) => `${t.getUTCFullYear()}-${p2(t.getUTCMonth() + 1)}-${p2(t.getUTCDate())}`;
const hms = (t: Date) => `${p2(t.getUTCHours())}:${p2(t.getUTCMinutes())}:${p2(t.getUTCSeconds())}`;

export function formatDate(epoch: number, zone: string, style = 'default', nowMs?: number): string {
  const t = localTime(epoch, zone);
  switch (style) {
    case 'default': case '': return `${DAY[t.getUTCDay()]} ${MON[t.getUTCMonth()]} ${t.getUTCDate()} ${hms(t)} ${t.getUTCFullYear()} ${zone}`;
    case 'short': return ymd(t);
    case 'iso': case 'iso8601': return `${ymd(t)} ${hms(t)} ${zone}`;
    case 'iso-strict': case 'iso8601-strict': return `${ymd(t)}T${hms(t)}${zone.slice(0, 3)}:${zone.slice(3)}`;
    case 'rfc': case 'rfc2822': return `${DAY[t.getUTCDay()]}, ${t.getUTCDate()} ${MON[t.getUTCMonth()]} ${t.getUTCFullYear()} ${hms(t)} ${zone}`;
    case 'raw': return `${epoch} ${zone}`;
    case 'unix': return String(epoch);
    case 'relative': return relativeDate(epoch, nowMs ?? Date.now());
  }
  throw new GitError(msg('badDateFormat', { format: style }));
}

export function relativeDate(epoch: number, nowMs: number): string {
  const diff = Math.floor(nowMs / 1000) - epoch;
  if (diff < 0) return 'in the future';
  for (const [limit, unit, size] of [[90, 'second', 1], [90 * 60, 'minute', 60], [36 * 3600, 'hour', 3600], [14 * 86400, 'day', 86400],
    [70 * 86400, 'week', 7 * 86400], [365 * 86400, 'month', 30 * 86400]] as [number, string, number][]) {
    if (diff < limit) {
      const n = Math.floor((diff + Math.floor(size / 2)) / size);
      return `${n} ${unit}${n !== 1 ? 's' : ''} ago`;
    }
  }
  const years = Math.floor(diff / (365 * 86400));
  return `${years} year${years !== 1 ? 's' : ''} ago`;
}

// ---------- objects ----------

export class Commit {
  constructor(readonly sha: string, readonly tree: string, readonly parents: string[], readonly author: string,
    readonly committer: string, readonly message: string) {}

  get subject(): string {
    const m = lstripChars(this.message, '\n');
    const first = partition(m, '\n\n')[0];
    return first.split('\n').join(' ').trim();
  }

  get body(): string {
    const [, sep, rest] = partition(lstripChars(this.message, '\n'), '\n\n');
    return sep ? lstripChars(rest, '\n') : '';
  }
}

/** 'Name <email> 1700000000 +0000' -> [name, email, epoch, zone]. */
export function parseIdent(line: string): [string, string, number, string] {
  const m = /^(.*?) ?<([^<>]*)> ?(\d+)? ?([+-]\d{4})?$/.exec(line);
  if (!m) return [line, '', 0, '+0000'];
  return [m[1], m[2], Number(m[3] ?? 0), m[4] ?? '+0000'];
}

export type TreeEntry = [mode: string, name: string, sha: string];
const hex = (b: Uint8Array) => { let s = ''; for (const x of b) s += x.toString(16).padStart(2, '0'); return s; };
const unhex = (s: string) => { const b = new Uint8Array(s.length / 2); for (let i = 0; i < b.length; i++) b[i] = parseInt(s.slice(i * 2, i * 2 + 2), 16); return b; };

/** Tree object -> [mode, name, sha][]; names are checked so nothing can point outside the work tree. */
export function parseTree(data: Uint8Array, where: string): TreeEntry[] {
  const out: TreeEntry[] = [];
  let i = 0;
  while (i < data.length) {
    const sp = data.indexOf(0x20, i);
    const nul = sp >= 0 ? data.indexOf(0, sp + 1) : -1;
    if (sp < 0 || nul < 0 || nul + 21 > data.length) throw fatal('corruptObject', { object: where });
    const mode = String.fromCharCode(...data.subarray(i, sp)).replace(/[^\x00-\x7f]/g, '\ufffd');
    const name = decodeBytes(data.subarray(sp + 1, nul));
    if (!/^[0-7]{5,6}$/.test(mode)) throw fatal('corruptObject', { object: where });
    checkComponent(name);
    out.push([mode === '40000' ? mode.padStart(6, '0') : mode, name, hex(data.subarray(nul + 1, nul + 21))]);
    i = nul + 21;
  }
  return out;
}

export function checkComponent(name: string): void {
  if (!name || name === '.' || name === '..' || name.includes('/') || name.includes('\0') || name.toLowerCase() === '.git')
    throw fatal('invalidPath', { path: name });
}

export function checkPath(path: string): void {
  const parts = path.split('/');
  if (parts.length > MAX_DEPTH) throw new GitError(msg('pathTooDeep', { path: path.slice(0, 200) + '...', limit: String(MAX_DEPTH) }));
  for (const part of parts) checkComponent(part);
}

/** [mode, name, sha][] -> tree object, in git's order (a directory sorts as if its name ended in '/'). */
export function treeBytes(entries: TreeEntry[]): Uint8Array {
  const keyed = entries.map(e => {
    const n = encodeText(e[1]);
    let key = n;
    if (e[0] === '040000') { key = new Uint8Array(n.length + 1); key.set(n); key[n.length] = 0x2f; }
    return { e, n, key };
  }).sort((a, b) => cmpBytes(a.key, b.key));
  const parts: Uint8Array[] = [];
  for (const { e, n } of keyed) {
    parts.push(enc.encode((e[0] === '040000' ? lstripChars(e[0], '0') : e[0]) + ' '), n, new Uint8Array([0]), unhex(e[2]));
  }
  return concat(parts);
}

export function concat(parts: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}

// ---------- the index ----------

export class Entry {
  constructor(public path: string, public sha: string, public mode: string, public stage = 0, public mtime = 0, public size = 0) {}
}

export function readIndex(data: Uint8Array, limit: number): Entry[] {
  const bad = (key: string, v: Values = {}) => new GitError(msg('badIndex', { why: msg(key, v) }));
  if (data.length < 32 || String.fromCharCode(...data.subarray(0, 4)) !== 'DIRC') throw bad('indexBadSignature');
  const v = new DataView(data.buffer, data.byteOffset, data.byteLength);
  const version = v.getUint32(4), count = v.getUint32(8);
  if (version !== 2 && version !== 3) throw bad('indexBadVersion', { version: String(version) });
  if (sha1(data.subarray(0, data.length - 20)) !== hex(data.subarray(data.length - 20))) throw bad('indexBadChecksum');
  if (count > limit) throw bad('indexTooMany');
  const out: Entry[] = [];
  let i = 12;
  const end = data.length - 20;
  for (let n = 0; n < count; n++) {
    if (i + 62 > end) throw bad('indexTruncated');
    const mtime = v.getUint32(i + 8), mode = v.getUint32(i + 24), size = v.getUint32(i + 36);
    const sha = hex(data.subarray(i + 40, i + 60));
    const flags = v.getUint16(i + 60);
    let start = i + 62;
    if (flags & 0x4000) {
      if (version < 3) throw bad('indexExtendedFlag');
      start += 2;
    }
    const rel = data.subarray(start, end).indexOf(0);
    if (rel < 0) throw bad('indexTruncated');
    const nul = start + rel;
    const path = decodeBytes(data.subarray(start, nul));
    checkPath(path);
    out.push(new Entry(path, sha, mode.toString(8).padStart(6, '0'), (flags >> 12) & 3, mtime, size));
    i += (Math.floor((nul - i) / 8) + 1) * 8; // entries are NUL-padded to a multiple of eight bytes
  }
  while (i + 8 <= end) { // extensions: optional ones (A-Z first) are skipped, others are not understood
    const sig = data.subarray(i, i + 4), size = v.getUint32(i + 4);
    if (!(sig[0] >= 65 && sig[0] <= 90)) throw bad('indexBadExtension', { name: String.fromCharCode(...sig) });
    i += 8 + size;
  }
  return out;
}

export function writeIndex(entries: Entry[]): Uint8Array {
  const sorted = entries.map(e => ({ e, name: encodeText(e.path) })).sort((a, b) => cmpBytes(a.name, b.name) || a.e.stage - b.e.stage);
  const parts: Uint8Array[] = [];
  const head = new Uint8Array(12);
  new DataView(head.buffer).setUint32(0, 0x44495243);
  new DataView(head.buffer).setUint32(4, 2);
  new DataView(head.buffer).setUint32(8, entries.length);
  parts.push(head);
  for (const { e, name } of sorted) {
    const len = 62 + name.length;
    const rec = new Uint8Array(len + (8 - len % 8));
    const v = new DataView(rec.buffer);
    v.setUint32(0, e.mtime >>> 0);
    v.setUint32(8, e.mtime >>> 0);
    v.setUint32(24, parseInt(e.mode, 8));
    v.setUint32(36, e.size >>> 0);
    rec.set(unhex(e.sha), 40);
    v.setUint16(60, (e.stage << 12) | Math.min(name.length, 0xfff));
    rec.set(name, 62);
    parts.push(rec);
  }
  const body = concat(parts);
  return concat([body, unhex(sha1(body))]);
}

// ---------- config ----------

const SECTION = /^\[\s*([A-Za-z0-9.-]+)(?:\s+"((?:[^"\\\n]|\\[^\n])*)")?\s*\]/;

/** git config files: [section], [section "sub"], key = value; names are matched case-insensitively (not subsections). */
export class Config {
  lines: string[];
  constructor(text = '') {
    this.lines = text.split('\n');
    if (this.lines.length && this.lines[this.lines.length - 1] === '') this.lines.pop();
  }

  /** [name, value, line index] in file order; name is section[.sub].key with section and key lowercased. */
  items(): [string, string, number][] {
    const out: [string, string, number][] = [];
    let section: string | null = null;
    let i = 0;
    while (i < this.lines.length) {
      let line = this.lines[i];
      const at = i;
      while (line.trimEnd().endsWith('\\') && i + 1 < this.lines.length && !inComment(line)) {
        i++;
        line = line.trimEnd().slice(0, -1) + this.lines[i];
      }
      i++;
      let s = line.trim();
      if (!s || '#;'.includes(s[0])) continue;
      if (s.startsWith('[')) {
        const m = SECTION.exec(s);
        if (!m) throw new GitError(msg('badConfig', { line: String(at + 1) }));
        const name = m[1];
        if (m[2] !== undefined) section = name.toLowerCase() + '.' + m[2].replace(/\\(.)/g, '$1');
        else if (name.includes('.')) { const [head, , sub] = partition(name, '.'); section = head.toLowerCase() + '.' + sub; }
        else section = name.toLowerCase();
        s = s.slice(m[0].length).trim();
        if (!s || '#;'.includes(s[0])) continue;
      }
      if (section === null) throw new GitError(msg('badConfig', { line: String(at + 1) }));
      const m = /^([A-Za-z][A-Za-z0-9-]*)\s*(=\s*(.*))?$/.exec(s);
      if (!m) throw new GitError(msg('badConfig', { line: String(at + 1) }));
      out.push([`${section}.${m[1].toLowerCase()}`, m[2] !== undefined ? value(m[3]) : 'true', at]);
    }
    return out;
  }

  get(name: string): string | null {
    let val: string | null = null;
    for (const [n, v] of this.items()) if (n === name) val = v;
    return val;
  }

  /** Sets (or with null removes) name; true when something changed. */
  set(name: string, val: string | null): boolean {
    const [section, , key] = rpartition(name, '.');
    const found = this.items().filter(([n]) => n === name).map(x => x[2]);
    if (val === null) {
      for (const at of [...found].reverse()) this.lines.splice(at, 1);
      this.dropEmptySections();
      return found.length > 0;
    }
    const line = `\t${key} = ${quoteValue(val)}`;
    if (found.length) { this.lines[found[found.length - 1]] = line; return true; }
    let last: number | null = null;
    let cur: string | null = null;
    this.lines.forEach((l, i) => {
      const s = l.trim();
      if (s.startsWith('[')) {
        cur = sectionOf(s);
        if (cur === section) last = i;
      } else if (cur === section && s) last = i;
    });
    if (last !== null) this.lines.splice(last + 1, 0, line);
    else {
      const [head, , sub] = partition(section, '.');
      this.lines.push(sub ? `[${head} "${sub}"]` : `[${head}]`);
      this.lines.push(line);
    }
    return true;
  }

  /** A section header left with nothing under it goes too (as git does after removing its last key). */
  private dropEmptySections(): void {
    const keep: string[] = [];
    let i = 0;
    while (i < this.lines.length) {
      const s = this.lines[i].trim();
      if (s.startsWith('[') && s.endsWith(']')) {
        let j = i + 1;
        while (j < this.lines.length && !this.lines[j].trim()) j++;
        if (j >= this.lines.length || this.lines[j].trim().startsWith('[')) { i = j; continue; }
      }
      keep.push(this.lines[i]);
      i++;
    }
    this.lines = keep;
  }

  text(): string { return this.lines.length ? this.lines.join('\n') + '\n' : ''; }
}

function sectionOf(s: string): string | null {
  const m = SECTION.exec(s);
  if (!m) return null;
  if (m[2] !== undefined) return m[1].toLowerCase() + '.' + m[2];
  const [head, , sub] = partition(m[1], '.');
  return head.toLowerCase() + (sub ? '.' + sub : '');
}

function inComment(line: string): boolean {
  let q = false;
  for (const c of line) {
    if (c === '"') q = !q;
    else if ((c === '#' || c === ';') && !q) return true;
  }
  return false;
}

function value(raw: string): string {
  let out = '', q = false, i = 0;
  raw = raw.trimEnd();
  while (i < raw.length) {
    const c = raw[i];
    if (c === '"') q = !q;
    else if (c === '\\' && i + 1 < raw.length) {
      i++;
      out += ({ n: '\n', t: '\t', b: '\b' } as Record<string, string>)[raw[i]] ?? raw[i];
    } else if ((c === '#' || c === ';') && !q) break;
    else out += c;
    i++;
  }
  return q ? out : out.trim();
}

function quoteValue(v: string): string {
  const e = v.replaceAll('\\', '\\\\').replaceAll('"', '\\"').replaceAll('\n', '\\n').replaceAll('\t', '\\t');
  return v !== v.trim() || v.includes('#') || v.includes(';') ? `"${e}"` : e;
}

export function configBool(v: string | null): boolean | null {
  if (v === null) return null;
  v = v.toLowerCase();
  if (['true', 'yes', 'on', '1', ''].includes(v)) return true;
  if (['false', 'no', 'off', '0'].includes(v)) return false;
  return null;
}

export function validKey(name: string): boolean {
  const [section, , key] = rpartition(name, '.');
  return !!section && /^[A-Za-z][A-Za-z0-9-]*$/.test(key) && /^[A-Za-z0-9-]+(\..+)?$/.test(section);
}

export function canonicalKey(name: string): string {
  const [head, , rest] = partition(name, '.');
  const [sub, , key] = rpartition(rest, '.');
  return sub ? `${head.toLowerCase()}.${sub}.${key.toLowerCase()}` : `${head.toLowerCase()}.${key.toLowerCase()}`;
}

// ---------- ignore rules ----------

const reEscape = (c: string) => c.replace(/[\\^$.*+?()[\]{}|/-]/g, '\\$&');

/** A gitignore / pathspec glob as a regex source: * and ? stop at '/', ** spans directories, [...] is a class. */
export function globRegex(pattern: string, fullPath: boolean): string {
  let out = '';
  let i = 0;
  const n = pattern.length;
  while (i < n) {
    const c = pattern[i];
    if (c === '*') {
      if (pattern.startsWith('**', i) && (i === 0 || pattern[i - 1] === '/') && (i + 2 === n || pattern[i + 2] === '/')) {
        if (i + 2 === n) { out += '.*'; i += 2; } else { out += '(?:.*/)?'; i += 3; }
        continue;
      }
      out += fullPath ? '[^/]*' : '.*';
    } else if (c === '?') {
      out += fullPath ? '[^/]' : '.';
    } else if (c === '[') {
      let j = i + 1;
      if (j < n && '!^'.includes(pattern[j])) j++;
      if (j < n && pattern[j] === ']') j++;
      while (j < n && pattern[j] !== ']') j++;
      if (j >= n) out += '\\[';
      else {
        let body = pattern.slice(i + 1, j);
        let neg = '';
        if (body[0] === '!' || body[0] === '^') { neg = '^'; body = body.slice(1); }
        body = body.replaceAll('\\', '\\\\');
        if (body[0] === ']') body = '\\' + body; // a leading ] is a member (Python's re reads it so; JS needs the escape)
        out += '[' + neg + body + ']';
        i = j;
      }
    } else if (c === '\\' && i + 1 < n) {
      i++;
      out += reEscape(pattern[i]);
    } else out += reEscape(c);
    i++;
  }
  return out;
}

/** The glob as a whole-string regex, or null when it does not compile. */
export function globFull(pattern: string, fullPath: boolean): RegExp | null {
  try { return new RegExp('^(?:' + globRegex(pattern, fullPath) + ')$', 's'); } catch { return null; }
}

/** One ignore file's patterns, relative to its directory (base, '' for the top). */
export class IgnoreRules {
  rules: [RegExp, boolean, boolean, boolean][] = [];
  constructor(text: string, readonly base: string, private readonly check?: () => void) {
    for (let line of text.split('\n').slice(0, 10_000)) {
      line = rstripChars(line, '\r');
      if (!line || line.startsWith('#')) continue;
      while (line.endsWith(' ') && !line.endsWith('\\ ')) line = line.slice(0, -1);
      const neg = line.startsWith('!');
      if (neg) line = line.slice(1);
      else if (line.startsWith('\\!') || line.startsWith('\\#')) line = line.slice(1);
      if (!line) continue;
      const dirOnly = line.endsWith('/');
      line = rstripChars(line, '/');
      const anchored = line.includes('/');
      line = lstripChars(line, '/');
      const rx = globFull(line, true);
      if (rx) this.rules.push([rx, neg, dirOnly, anchored]);
    }
  }

  /** null when no pattern speaks about path (relative to the top), else whether it is ignored. */
  match(path: string, isDir: boolean): boolean | null {
    const rel = this.base ? path.slice(this.base.length + 1) : path;
    const name = rel.slice(rel.lastIndexOf('/') + 1);
    for (let k = this.rules.length - 1; k >= 0; k--) {
      if (k % 256 === 0) this.check?.(); // a long ignore file over many paths stays under the call's time limit
      const [rx, neg, dirOnly, anchored] = this.rules[k];
      if (dirOnly && !isDir) continue;
      if (rx.test(anchored ? rel : name)) return !neg;
    }
    return null;
  }
}

// ---------- line diff ----------
// lines are surrogate-escaped text, each with its newline

export function splitLines(data: Uint8Array | string): string[] {
  const lines = (typeof data === 'string' ? data : decodeBytes(data)).split('\n');
  if (lines[lines.length - 1] === '') {
    lines.pop();
    return lines.map(l => l + '\n');
  }
  const last = lines.pop()!;
  return [...lines.map(l => l + '\n'), last];
}

export type Region = [number, number, number, number];

/** Changed regions [a_start, a_end, b_start, b_end] turning a into b (fewest changes), each slid as far down as it can go. */
export function myers(a: string[], b: string[], check?: () => void): Region[] {
  let pre = 0;
  while (pre < a.length && pre < b.length && a[pre] === b[pre]) pre++;
  let suf = 0;
  while (suf < a.length - pre && suf < b.length - pre && a[a.length - 1 - suf] === b[b.length - 1 - suf]) suf++;
  const A = a.slice(pre, a.length - suf), B = b.slice(pre, b.length - suf);
  const n = A.length, m = B.length;
  const removed = new Array<boolean>(a.length).fill(false), added = new Array<boolean>(b.length).fill(false);
  const all = () => {
    for (let i = 0; i < n; i++) removed[pre + i] = true;
    for (let j = 0; j < m; j++) added[pre + j] = true;
  };
  if (n === 0 || m === 0) all();
  else {
    const mx = n + m, off = mx + 1;
    const v = new Int32Array(2 * mx + 3).fill(-1);
    v[off + 1] = 0;
    const trace: Int32Array[] = [];
    let steps = 0, done = false;
    for (let d = 0; d <= mx; d++) {
      trace.push(v.slice(off - d - 1, off + d + 2)); // V before step d, for k in -d-1..d+1
      for (let k = -d; k <= d; k += 2) {
        let x = k === -d || (k !== d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
        let y = x - k;
        while (x < n && y < m && A[x] === B[y]) { x++; y++; }
        v[off + k] = x;
        if (x >= n && y >= m) { done = true; break; }
      }
      if (done) break;
      steps += d + 1;
      if (steps > DIFF_BUDGET) break;
      if (check && d % 64 === 0) check();
    }
    if (!done) all();
    else {
      let x = n, y = m;
      for (let d = trace.length - 1; d > 0; d--) {
        const vv = trace[d];
        const k = x - y;
        const pk = k === -d || (k !== d && vv[k - 1 + d + 1] < vv[k + 1 + d + 1]) ? k + 1 : k - 1;
        const px = vv[pk + d + 1], py = px - pk;
        if (pk === k + 1) added[pre + py] = true; // a step down: B[py] inserted
        else removed[pre + px] = true; // a step right: A[px] removed
        x = px; y = py;
      }
    }
  }
  slide(a, removed);
  slide(b, added);
  return regions(removed, added);
}

/** Moves each run of changed lines down while the line after it equals its first line (git's compaction). */
function slide(lines: string[], changed: boolean[]): void {
  let i = 0;
  const n = lines.length;
  while (i < n) {
    if (!changed[i]) { i++; continue; }
    let j = i;
    while (j < n && changed[j]) j++;
    while (j < n && !changed[j] && lines[i] === lines[j]) {
      changed[i] = false; changed[j] = true;
      i++; j++;
      while (j < n && changed[j]) j++;
    }
    i = j;
  }
}

function regions(removed: boolean[], added: boolean[]): Region[] {
  const out: Region[] = [];
  let i = 0, j = 0;
  const n = removed.length, m = added.length;
  while (i < n || j < m) {
    if (i < n && j < m && !removed[i] && !added[j]) { i++; j++; continue; }
    const si = i, sj = j;
    while (i < n && removed[i]) i++;
    while (j < m && added[j]) j++;
    if (si === i && sj === j) { i = Math.min(i + 1, n); j = Math.min(j + 1, m); continue; }
    out.push([si, i, sj, j]);
  }
  return out;
}

/** Hunks as text lines ('@@ ... @@', ' x', '-x', '+x', '\ No newline at end of file'). */
export function unified(a: string[], b: string[], context: number, regs?: Region[], check?: () => void): string[] {
  regs = regs ?? myers(a, b, check);
  const out: string[] = [];
  let g = 0;
  while (g < regs.length) {
    let h = g;
    while (h + 1 < regs.length && regs[h + 1][0] - regs[h][1] <= 2 * context) h++;
    const a1 = Math.max(0, regs[g][0] - context), a2 = Math.min(a.length, regs[h][1] + context);
    const b1 = Math.max(0, regs[g][2] - context), b2 = Math.min(b.length, regs[h][3] + context);
    let func = '';
    for (let k = a1 - 1; k >= 0; k--) {
      if (/^[A-Za-z_$]/.test(a[k])) { func = ' ' + cpSlice(rstripChars(a[k], '\n').trimEnd(), 0, 80).trimEnd(); break; }
    }
    const rng = (s: number, e: number) => { const cnt = e - s; const start = cnt ? s + 1 : s; return cnt === 1 ? `${start}` : `${start},${cnt}`; };
    out.push(`@@ -${rng(a1, a2)} +${rng(b1, b2)} @@${func}`);
    let i = a1;
    for (let k = g; k <= h; k++) {
      const [s1, e1, s2, e2] = regs[k];
      while (i < s1) { line(out, ' ', a[i]); i++; }
      for (let x = s1; x < e1; x++) line(out, '-', a[x]);
      for (let y = s2; y < e2; y++) line(out, '+', b[y]);
      i = e1;
    }
    while (i < a2) { line(out, ' ', a[i]); i++; }
    g = h + 1;
  }
  return out;
}

function line(out: string[], sign: string, text: string): void {
  if (text.endsWith('\n')) out.push(sign + text.slice(0, -1));
  else { out.push(sign + text); out.push('\\ No newline at end of file'); }
}

export function isBinary(data: Uint8Array): boolean {
  return data.subarray(0, 8000).indexOf(0) >= 0;
}

export function countChanges(a: Uint8Array, b: Uint8Array, check?: () => void): [number, number] {
  const regs = myers(splitLines(a), splitLines(b), check);
  let ins = 0, dels = 0;
  for (const r of regs) { ins += r[3] - r[2]; dels += r[1] - r[0]; }
  return [ins, dels];
}

// ---------- three-way line merge ----------

/** [merged bytes, conflicted]. Changes from both sides that overlap or touch become a conflict block. */
export function mergeLines(base: Uint8Array, ours: Uint8Array, theirs: Uint8Array, ourLabel: string, theirLabel: string,
  check?: () => void): [Uint8Array, boolean] {
  const o = splitLines(base), a = splitLines(ours), b = splitLines(theirs);
  type R = [number, number, number, number, number];
  const ra: R[] = myers(o, a, check).map(([s, e, x, y]) => [s, e, x, y, 0]);
  const rb: R[] = myers(o, b, check).map(([s, e, x, y]) => [s, e, x, y, 1]);
  const all = [...ra, ...rb].sort((p, q) => p[0] - q[0] || p[1] - q[1]);
  const out: string[] = [];
  let pos = 0, conflict = false, i = 0;
  const push = (xs: string[]) => { for (const x of xs) out.push(x); };
  while (i < all.length) {
    const s = all[i][0];
    let e = all[i][1];
    const group = [all[i]];
    let j = i + 1;
    while (j < all.length && all[j][0] <= e) { e = Math.max(e, all[j][1]); group.push(all[j]); j++; } // overlapping or touching changes go together
    push(o.slice(pos, s));
    const sides = new Set(group.map(r => r[4]));
    if (sides.size === 1) {
      const side = sides.has(0) ? a : b;
      let p = s;
      for (const r of group) { push(o.slice(p, r[0])); push(side.slice(r[2], r[3])); p = r[1]; }
      push(o.slice(p, e));
    } else {
      const va = sideText(o, a, group.filter(r => r[4] === 0), s, e);
      const vb = sideText(o, b, group.filter(r => r[4] === 1), s, e);
      if (va.length === vb.length && va.every((x, k) => x === vb[k])) push(va);
      else {
        let k = 0;
        while (k < va.length && k < vb.length && va[k] === vb[k]) k++;
        let t = 0;
        while (t < va.length - k && t < vb.length - k && va[va.length - 1 - t] === vb[vb.length - 1 - t]) t++;
        push(va.slice(0, k));
        conflict = true;
        out.push(`<<<<<<< ${ourLabel}\n`);
        push(terminated(va.slice(k, va.length - t)));
        out.push('=======\n');
        push(terminated(vb.slice(k, vb.length - t)));
        out.push(`>>>>>>> ${theirLabel}\n`);
        push(va.slice(va.length - t));
      }
    }
    pos = e;
    i = j;
  }
  push(o.slice(pos));
  return [encodeText(out.join('')), conflict];
}

const terminated = (lines: string[]) =>
  lines.length && !lines[lines.length - 1].endsWith('\n') ? [...lines.slice(0, -1), lines[lines.length - 1] + '\n'] : lines;

function sideText(o: string[], side: string[], regs: number[][], s: number, e: number): string[] {
  const out: string[] = [];
  let p = s;
  for (const r of regs) { out.push(...o.slice(p, r[0]), ...side.slice(r[2], r[3])); p = r[1]; }
  out.push(...o.slice(p, e));
  return out;
}

// ---------- quoting ----------

/** A path as git prints it: in double quotes with C escapes when it has control, quote, backslash or non-ASCII bytes. */
export function cQuote(path: string): string {
  const raw = encodeText(path);
  if (!raw.some(c => c < 0x20 || c >= 0x7f || c === 0x22 || c === 0x5c)) return path;
  let out = '';
  for (const c of raw) {
    if (c === 0x22 || c === 0x5c) out += '\\' + String.fromCharCode(c);
    else if (c >= 7 && c <= 13) out += '\\' + 'abtnvfr'[c - 7];
    else if (c < 0x20 || c >= 0x7f) out += '\\' + c.toString(8).padStart(3, '0');
    else out += String.fromCharCode(c);
  }
  return '"' + out + '"';
}
