// jq's operations on plain values: arithmetic, indexing and slicing, paths, the @formats, regex, and dates.

import { JqError, cmp, cpIndex, cpLength, cpSlice, dump, errType, fmtNum, isObj, typeName, truthy, type J } from './jq-values.js';
import { tooLarge } from '../command.js';
import { toBase64, fromBase64 } from '../bytes.js';

/** The `jq: value` limit (ctx.shell.options.maxStringBytes); set per run, which is synchronous. */
let maxBytes = 32 << 20;
export function setMaxBytes(n: number) { maxBytes = n; }
export function sized(bytes: number) {
  if (bytes > maxBytes) throw tooLarge('jq: value', maxBytes, 'produce smaller values or stream them with .[]');
}

export const toStr = (v: J): string => typeof v === 'string' ? v : dump(v);

// ---------- arithmetic ----------

export function arith(op: string, a: J, b: J): J {
  switch (op) {
    case '+':
      if (a === null) return b;
      if (b === null) return a;
      if (typeof a === 'number' && typeof b === 'number') return a + b;
      if (typeof a === 'string' && typeof b === 'string') { sized(a.length + b.length); return a + b; }
      if (Array.isArray(a) && Array.isArray(b)) { sized(8 * (a.length + b.length)); return a.concat(b); }
      if (isObj(a) && isObj(b)) { const r = new Map(a); for (const [k, v] of b) r.set(k, v); return r; }
      break;
    case '-':
      if (typeof a === 'number' && typeof b === 'number') return a - b;
      if (Array.isArray(a) && Array.isArray(b)) return a.filter(x => b.every(y => cmp(x, y) !== 0));
      break;
    case '*':
      if (typeof a === 'number' && typeof b === 'number') return a * b;
      if ((typeof a === 'string' && typeof b === 'number') || (typeof a === 'number' && typeof b === 'string')) {
        const [s, k] = typeof a === 'string' ? [a, b as number] : [b as string, a as number];
        if (!(k > 0)) return null;
        const n = Math.ceil(k);
        sized(s.length * n);
        return s.repeat(n);
      }
      if (isObj(a) && isObj(b)) return deepMerge(a, b);
      break;
    case '/': case '%':
      if (typeof a === 'number' && typeof b === 'number') {
        if (op === '/') { if (b === 0) throw new JqError(`${errType(a)} and ${errType(b)} cannot be divided because the divisor is zero`); return a / b; }
        const y = Math.trunc(b);
        if (y === 0) throw new JqError(`${errType(a)} and ${errType(b)} cannot be divided because the divisor is zero`);
        return (Math.trunc(a) % y) || 0;
      }
      if (op === '/' && typeof a === 'string' && typeof b === 'string') return splitString(a, b);
      break;
  }
  const verb = op === '+' ? 'added' : op === '-' ? 'subtracted' : op === '*' ? 'multiplied' : 'divided';
  throw new JqError(`${errType(a)} and ${errType(b)} cannot be ${verb}`);
}

function deepMerge(a: Map<string, J>, b: Map<string, J>): Map<string, J> {
  const r = new Map(a);
  for (const [k, v] of b) { const x = r.get(k); r.set(k, isObj(x ?? null) && isObj(v) ? deepMerge(x as Map<string, J>, v) : v); }
  return r;
}

export function splitString(s: string, sep: string): J[] {
  if (s === '') return [];
  return sep === '' ? Array.from(s) : s.split(sep);
}

// ---------- indexing ----------

export function iterate(v: J): J[] {
  if (Array.isArray(v)) return v;
  if (isObj(v)) return [...v.values()];
  throw new JqError(v === null ? 'Cannot iterate over null' : `Cannot iterate over ${errType(v)}`);
}

export function index(v: J, k: J): J {
  if (typeof k === 'string') {
    if (v === null) return null;
    if (isObj(v)) return v.get(k) ?? null;
    throw new JqError(`Cannot index ${typeName(v)} with "${k}"`);
  }
  if (typeof k === 'number') {
    if (v === null) return null;
    if (Array.isArray(v)) {
      if (Number.isNaN(k)) return null;
      let i = Math.floor(k);
      if (i < 0) i += v.length;
      return i >= 0 && i < v.length ? v[i] : null;
    }
    throw new JqError(`Cannot index ${typeName(v)} with number`);
  }
  if (isObj(k) && (v === null || Array.isArray(v) || typeof v === 'string')) return slice(v, k.get('start') ?? null, k.get('end') ?? null);
  if (Array.isArray(k) && Array.isArray(v)) return indices(v, k);
  if (v === null && (k === null)) return null;
  throw new JqError(`Cannot index ${typeName(v)} with ${typeName(k)}`);
}

function bounds(n: number, lo: J, hi: J): [number, number] {
  const f = (x: J, dflt: number, round: (n: number) => number) => {
    if (x === null) return dflt;
    if (typeof x !== 'number') throw new JqError('Start and end indices of an array slice must be numbers');
    let i = round(x);
    if (i < 0) i += n;
    return Math.max(0, Math.min(n, i));
  };
  const a = f(lo, 0, Math.floor);
  return [a, Math.max(a, f(hi, n, Math.ceil))];
}

export function slice(v: J, lo: J, hi: J): J {
  if (v === null) return null;
  if (Array.isArray(v)) { const [a, b] = bounds(v.length, lo, hi); return v.slice(a, b); }
  if (typeof v === 'string') { const [a, b] = bounds(cpLength(v), lo, hi); return cpSlice(v, a, b); }
  throw new JqError(`Cannot index ${typeName(v)} with object`);
}

export function indices(v: J, sub: J): J {
  if (v === null) return null;
  if (typeof v === 'string' && typeof sub === 'string') {
    if (sub === '') return null;
    const r: J[] = [];
    for (let i = v.indexOf(sub); i >= 0; i = v.indexOf(sub, i + 1)) r.push(cpIndex(v, i));
    return r;
  }
  if (Array.isArray(v)) {
    const needle = Array.isArray(sub) ? sub : [sub];
    if (needle.length === 0) return null;
    const r: J[] = [];
    for (let i = 0; i + needle.length <= v.length; i++) if (needle.every((x, k) => cmp(v[i + k], x) === 0)) r.push(i);
    return r;
  }
  throw new JqError(`Cannot determine indices of ${typeName(sub)} in ${typeName(v)}`);
}

// ---------- paths ----------

export function getPath(v: J, path: J[]): J {
  for (const k of path) { if (v === null) return null; v = index(v, k); }
  return v;
}

/** setpath; containers listed in `owned` are copies this operation made, so they may be changed in place. */
export function setPath(v: J, path: J[], i: number, value: J, owned: Set<object>): J {
  if (i === path.length) return value;
  const k = path[i];
  if (typeof k === 'string') {
    if (v !== null && !isObj(v)) throw new JqError(`Cannot index ${typeName(v)} with "${k}"`);
    const m = v === null ? new Map<string, J>() : owned.has(v) ? v : new Map(v);
    owned.add(m);
    m.set(k, setPath(m.get(k) ?? null, path, i + 1, value, owned));
    return m;
  }
  if (typeof k === 'number') {
    if (v !== null && !Array.isArray(v)) throw new JqError(`Cannot index ${typeName(v)} with number`);
    const base: J[] = v ?? [];
    let idx = Math.trunc(k);
    if (idx < 0) { idx += base.length; if (idx < 0) throw new JqError('Out of bounds negative array index'); }
    if (idx > 536870911) throw new JqError('Array index too large');
    sized(8 * (idx + 1));
    const r = owned.has(base) ? base : base.slice();
    owned.add(r);
    while (r.length <= idx) r.push(null);
    r[idx] = setPath(r[idx], path, i + 1, value, owned);
    return r;
  }
  if (isObj(k)) {
    if (v !== null && !Array.isArray(v)) throw new JqError(`Cannot update field at object index of ${typeName(v)}`);
    const base: J[] = v ?? [];
    const [a, b] = bounds(base.length, k.get('start') ?? null, k.get('end') ?? null);
    const repl = setPath(base.slice(a, b), path, i + 1, value, new Set());
    if (!Array.isArray(repl)) throw new JqError('A slice of an array can only be assigned another array');
    return [...base.slice(0, a), ...repl, ...base.slice(b)];
  }
  throw new JqError(`Invalid path component ${dump(k)}`);
}

function delPath(v: J, path: J[], i: number, owned: Set<object>): J {
  if (v === null) return null;
  const k = path[i];
  if (i < path.length - 1) {
    const child = index(v, k);
    return child === null ? v : setPath(v, [k], 0, delPath(child, path, i + 1, owned), owned);
  }
  if (typeof k === 'string') {
    if (!isObj(v)) throw new JqError(`Cannot delete field at object index of ${typeName(v)}`);
    if (!v.has(k)) return v;
    const m = owned.has(v) ? v : new Map(v);
    owned.add(m);
    m.delete(k);
    return m;
  }
  if (typeof k === 'number') {
    if (!Array.isArray(v)) throw new JqError(`Cannot delete field at array index of ${typeName(v)}`);
    let idx = Math.trunc(k);
    if (idx < 0) idx += v.length;
    if (idx < 0 || idx >= v.length) return v;
    const r = owned.has(v) ? v : v.slice();
    owned.add(r);
    r.splice(idx, 1);
    return r;
  }
  if (isObj(k) && Array.isArray(v)) {
    const [a, b] = bounds(v.length, k.get('start') ?? null, k.get('end') ?? null);
    return [...v.slice(0, a), ...v.slice(b)];
  }
  throw new JqError(`Cannot delete field at ${typeName(k)} index of ${typeName(v)}`);
}

export function delPaths(v: J, paths: J[]): J {
  for (const p of paths) if (!Array.isArray(p)) throw new JqError('Path must be specified as an array');
  const owned = new Set<object>();
  for (const p of [...paths].sort((a, b) => cmp(b, a))) { if ((p as J[]).length === 0) return null; v = delPath(v, p as J[], 0, owned); }
  return v;
}

export function* allPaths(v: J, prefix: J[] = []): Generator<J[]> {
  if (Array.isArray(v)) for (let i = 0; i < v.length; i++) { const p = [...prefix, i]; yield p; yield* allPaths(v[i], p); }
  else if (isObj(v)) for (const [k, x] of v) { const p = [...prefix, k]; yield p; yield* allPaths(x, p); }
}

export function* toStream(v: J, path: J[] = []): Generator<J> {
  const entries: [J, J][] = Array.isArray(v) ? v.map((x, i) => [i, x]) : isObj(v) ? [...v] : [];
  if (entries.length === 0) { yield [path, v]; return; }
  for (const [k, x] of entries) yield* toStream(x, [...path, k]);
  yield [[...path, entries[entries.length - 1][0]]];
}

// ---------- builtins on values ----------

export function length(v: J): number {
  if (v === null) return 0;
  if (typeof v === 'boolean') throw new JqError(`${errType(v)} has no length`);
  if (typeof v === 'number') return Math.abs(v);
  if (typeof v === 'string') return cpLength(v);
  return Array.isArray(v) ? v.length : v.size;
}

export function keys(v: J, sort = true): J[] {
  if (isObj(v)) return sort ? [...v.keys()].sort((a, b) => cmp(a, b)) : [...v.keys()];
  if (Array.isArray(v)) return v.map((_, i) => i);
  throw new JqError(`${errType(v)} has no keys`);
}

export function toEntries(v: J): J[] {
  if (isObj(v)) return [...v].map(([key, value]) => new Map<string, J>([['key', key], ['value', value]]));
  if (Array.isArray(v)) return v.map((value, i) => new Map<string, J>([['key', i], ['value', value]]));
  throw new JqError(`${errType(v)} has no keys`);
}

export function fromEntries(entries: J): J {
  const r = new Map<string, J>();
  for (const e of iterate(entries)) {
    let key: J = null, value: J = null;
    if (isObj(e)) {
      key = e.get('key') ?? null;
      if (key === null) for (const n of ['k', 'name', 'Name', 'K', 'Key']) { const x = e.get(n) ?? null; if (truthy(x)) { key = x; break; } }
      value = e.has('value') ? e.get('value')! : e.has('v') ? e.get('v')! : e.get('Value') ?? null;
    } else if (e === null) throw new JqError('Cannot check whether null has a string key');
    else throw new JqError(`Cannot index ${typeName(e)} with "key"`);
    r.set(typeof key === 'string' ? key : dump(key), value);
  }
  return r;
}

export function contains(a: J, b: J): boolean {
  if (isObj(a) && isObj(b)) { for (const [k, y] of b) { if (!a.has(k) || !contains(a.get(k)!, y)) return false; } return true; }
  if (Array.isArray(a) && Array.isArray(b)) return b.every(y => a.some(x => contains(x, y)));
  if (typeof a === 'string' && typeof b === 'string') return a.includes(b);
  if (typeName(a) === typeName(b)) return cmp(a, b) === 0;
  throw new JqError(`${errType(a)} and ${errType(b)} cannot have their containment checked`);
}

export function flatten(v: J, depth: number): J[] {
  if (!Array.isArray(v)) throw new JqError(`Cannot iterate over ${errType(v)}`);
  if (depth < 0) throw new JqError('flatten depth must not be negative');
  const r: J[] = [];
  const go = (l: J[], d: number) => { for (const x of l) if (Array.isArray(x) && d > 0) go(x, d - 1); else r.push(x); };
  go(v, depth);
  return r;
}

export function toNumber(v: J): number {
  if (typeof v === 'number') return v;
  if (typeof v === 'string') {
    if (/^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*$/.test(v)) return Number(v);
    throw new JqError(`Cannot parse '${v}' as number`);
  }
  throw new JqError(`${errType(v)} cannot be parsed as a number`);
}

export const numArg = (v: J): number => typeof v === 'number' ? v : (() => { throw new JqError(`${errType(v)} number required`); })();
export function strArg(what: string, v: J): string {
  if (typeof v === 'string') return v;
  throw new JqError(`${errType(v)} cannot be ${what}`);
}

export function sorted(v: J): J[] {
  if (!Array.isArray(v)) throw new JqError(`${errType(v)} cannot be sorted, as it is not an array`);
  return v.slice().sort(cmp);
}

export function implode(v: J): string {
  if (!Array.isArray(v)) throw new JqError(`${errType(v)} cannot be imploded, must be an array of codepoints`);
  return v.map(c => {
    if (typeof c !== 'number') throw new JqError('Unicode codepoint must be numeric');
    return c >= 0 && c <= 0x10ffff && !(c >= 0xd800 && c <= 0xdfff) ? String.fromCodePoint(c) : '\ufffd';
  }).join('');
}

export function trimWhite(s: string, left: boolean, right: boolean): string {
  let a = 0, b = s.length;
  const ws = (c: string) => ' \t\n\r\f\v'.includes(c);
  if (left) while (a < b && ws(s[a])) a++;
  if (right) while (b > a && ws(s[b - 1])) b--;
  return s.slice(a, b);
}

// ---------- @formats ----------

export function format(name: string, v: J): string {
  switch (name) {
    case 'text': return toStr(v);
    case 'json': return dump(v);
    case 'csv': case 'tsv': {
      if (!Array.isArray(v)) throw new JqError(`${errType(v)} cannot be ${name}-formatted, only an array can be`);
      return v.map(x => {
        if (Array.isArray(x) || isObj(x)) throw new JqError(`${errType(x)} is not valid in a csv row`);
        if (x === null) return '';
        if (typeof x === 'string') return name === 'csv' ? '"' + x.replace(/"/g, '""') + '"' : x.replace(/\\/g, '\\\\').replace(/\t/g, '\\t').replace(/\n/g, '\\n').replace(/\r/g, '\\r');
        return typeof x === 'number' ? fmtNum(x) : String(x);
      }).join(name === 'csv' ? ',' : '\t');
    }
    case 'html': return toStr(v).replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[c]!);
    case 'uri': return [...new TextEncoder().encode(toStr(v))].map(b => /[A-Za-z0-9\-_.~]/.test(String.fromCharCode(b)) ? String.fromCharCode(b) : '%' + b.toString(16).toUpperCase().padStart(2, '0')).join('');
    case 'sh': return (Array.isArray(v) ? v : [v]).map(x => {
      if (Array.isArray(x) || isObj(x)) throw new JqError(`${errType(x)} can not be escaped for shell`);
      return typeof x === 'string' ? "'" + x.replace(/'/g, "'\\''") + "'" : toStr(x);
    }).join(' ');
    case 'base64': return toBase64(new TextEncoder().encode(toStr(v)));
    case 'base64d': {
      const t = toStr(v).replace(/=+$/, '');
      if (!/^[A-Za-z0-9+/]*$/.test(t) || t.length % 4 === 1) throw new JqError(`${errType(v)} is not valid base64 data`);
      return new TextDecoder().decode(fromBase64(t));
    }
  }
  throw new JqError(`${name} is not a valid format`);
}

// ---------- regular expressions ----------

export interface Rx { re: RegExp; global: boolean; names: (string | null)[] }

/** The capture groups of a pattern in order, with their names (null when unnamed). */
function groupNames(src: string): (string | null)[] {
  const names: (string | null)[] = [];
  let inClass = false;
  for (let i = 0; i < src.length; i++) {
    const c = src[i];
    if (c === '\\') { i++; continue; }
    if (inClass) { if (c === ']') inClass = false; continue; }
    if (c === '[') { inClass = true; continue; }
    if (c !== '(') continue;
    if (src[i + 1] !== '?') names.push(null);
    else if (src[i + 2] === '<' && src[i + 3] !== '=' && src[i + 3] !== '!') names.push(/^[^>]+/.exec(src.slice(i + 3))?.[0] ?? null);
  }
  return names;
}

/** Oniguruma's x flag: drop unescaped whitespace and # comments outside classes. */
function stripExtended(src: string): string {
  let out = '', inClass = false;
  for (let i = 0; i < src.length; i++) {
    const c = src[i];
    if (c === '\\') { out += c + (src[++i] ?? ''); continue; }
    if (inClass) { out += c; if (c === ']') inClass = false; continue; }
    if (c === '[') { inClass = true; out += c; continue; }
    if (/\s/.test(c)) continue;
    if (c === '#') { while (i < src.length && src[i] !== '\n') i++; continue; }
    out += c;
  }
  return out;
}

export function makeRegex(pattern: J, flags: J): Rx {
  if (Array.isArray(pattern)) { flags = pattern[1] ?? null; pattern = pattern[0] ?? null; }
  if (typeof pattern !== 'string') throw new JqError(`${errType(pattern)} cannot be matched, as it is not a string`);
  if (flags !== null && typeof flags !== 'string') throw new JqError(`${errType(flags)} is not a string`);
  const f = flags ?? '';
  let global = false, ignore = false, extended = false, dotall = false;
  for (const c of f) {
    if (c === 'g') global = true;
    else if (c === 'i') ignore = true;
    else if (c === 'x') extended = true;
    else if (c === 'p') dotall = true;
    else if (!'snl'.includes(c)) throw new JqError(`${f} is not a valid modifier string`);
  }
  let src = extended ? stripExtended(pattern) : pattern;
  src = src.replace(/\\h/g, '[0-9a-fA-F]').replace(/\\H/g, '[^0-9a-fA-F]');
  const opts = 'dg' + (ignore ? 'i' : '') + (dotall ? 's' : '');
  let re: RegExp;
  try { re = new RegExp(src, opts + 'u'); } catch {
    try { re = new RegExp(src, opts); } catch (e) { throw new JqError(`${pattern} (at offset 0) is not a valid regex: ${(e as Error).message}`); }
  }
  return { re, global, names: groupNames(src) };
}

export function* matches(rx: Rx, s: string, global = rx.global): Generator<RegExpExecArray> {
  let pos = 0;
  while (pos <= s.length) {
    rx.re.lastIndex = pos;
    const m = rx.re.exec(s);
    if (!m) return;
    yield m;
    if (!global) return;
    pos = m[0].length === 0 ? m.index + 1 : m.index + m[0].length;
  }
}

export function matchObject(rx: Rx, s: string, m: RegExpExecArray): J {
  const ind = (m as unknown as { indices: ([number, number] | undefined)[] }).indices;
  const caps: J[] = [];
  for (let g = 1; g < m.length; g++) {
    const r = ind[g];
    caps.push(new Map<string, J>([
      ['offset', r ? cpIndex(s, r[0]) : -1], ['length', r ? cpLength(m[g]) : 0],
      ['string', r ? m[g] : null], ['name', rx.names[g - 1] ?? null],
    ]));
  }
  return new Map<string, J>([['offset', cpIndex(s, m.index)], ['length', cpLength(m[0])], ['string', m[0]], ['captures', caps]]);
}

export function captureObject(rx: Rx, m: RegExpExecArray): Map<string, J> {
  const r = new Map<string, J>();
  rx.names.forEach((n, i) => { if (n !== null) r.set(n, m[i + 1] ?? null); });
  return r;
}

// ---------- dates ----------

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

export function gmtime(sec: number): J[] {
  const whole = Math.floor(sec);
  const d = new Date(whole * 1000);
  if (Number.isNaN(d.getTime())) throw new JqError('error converting number of seconds since epoch to datetime');
  const y = d.getUTCFullYear();
  const yday = Math.floor((Date.UTC(y, d.getUTCMonth(), d.getUTCDate()) - Date.UTC(y, 0, 1)) / 86400000);
  return [y, d.getUTCMonth(), d.getUTCDate(), d.getUTCHours(), d.getUTCMinutes(), d.getUTCSeconds() + (sec - whole), d.getUTCDay(), yday];
}

export function mktime(v: J): number {
  if (!Array.isArray(v)) throw new JqError('mktime requires array of 6 numbers');
  if (v.length < 6 || v.some(x => typeof x !== 'number')) throw new JqError('mktime requires parsed datetime inputs');
  const [y, mo, d, h, mi, s] = v as number[];
  return Math.floor(Date.UTC(y, mo, d, h, mi, Math.floor(s)) / 1000);
}

export function strftime(fmt: string, v: J): string {
  const bd = typeof v === 'number' ? gmtime(v) : v;
  if (!Array.isArray(bd) || bd.length < 6 || bd.some(x => typeof x !== 'number')) throw new JqError('strftime/1 requires parsed datetime inputs');
  const [Y, M, D, h, m, sf] = bd as number[];
  const s = Math.floor(sf);
  const t = Date.UTC(Y, M, D, h, m, s);
  const date = new Date(t);
  const wday = date.getUTCDay();
  const yday = Math.floor((Date.UTC(Y, M, D) - Date.UTC(Y, 0, 1)) / 86400000);
  const p2 = (n: number) => String(n).padStart(2, '0');
  const h12 = h % 12 === 0 ? 12 : h % 12;
  const spec = (c: string): string => {
    switch (c) {
      case 'Y': return String(Y); case 'C': return p2(Math.floor(Y / 100)); case 'y': return p2(Y % 100);
      case 'm': return p2(M + 1); case 'd': return p2(D); case 'e': return String(D).padStart(2, ' ');
      case 'H': return p2(h); case 'I': return p2(h12); case 'M': return p2(m); case 'S': return p2(s);
      case 'p': return h < 12 ? 'AM' : 'PM'; case 'Z': return 'UTC'; case 'z': return '+0000';
      case 'a': return DAYS[wday].slice(0, 3); case 'A': return DAYS[wday];
      case 'b': case 'h': return MONTHS[M].slice(0, 3); case 'B': return MONTHS[M];
      case 'j': return String(yday + 1).padStart(3, '0'); case 'u': return String(wday || 7); case 'w': return String(wday);
      case 's': return String(Math.floor(t / 1000));
      case 'T': return `${p2(h)}:${p2(m)}:${p2(s)}`; case 'R': return `${p2(h)}:${p2(m)}`;
      case 'D': return `${p2(M + 1)}/${p2(D)}/${p2(Y % 100)}`; case 'F': return `${Y}-${p2(M + 1)}-${p2(D)}`;
      case 'c': return `${spec('a')} ${spec('b')} ${spec('e')} ${spec('T')} ${Y}`;
      case 'x': return spec('D'); case 'X': return spec('T');
      case 'n': return '\n'; case 't': return '\t'; case '%': return '%';
      case 'U': return p2(Math.floor((yday + 7 - wday) / 7)); case 'W': return p2(Math.floor((yday + 7 - ((wday + 6) % 7)) / 7));
    }
    return '%' + c;
  };
  return fmt.replace(/%([A-Za-z%])/g, (_, c) => spec(c));
}

/** strptime for the usual numeric and name specifiers; the result is a broken-down time like gmtime's. */
export function strptime(input: string, fmt: string): J {
  const fields: string[] = [];
  const pattern = fmt.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replace(/%([A-Za-z%])/g, (_, c: string) => {
    const group = (re: string) => { fields.push(c); return `(${re})`; };
    switch (c) {
      case 'Y': return group('[+-]?\\d{1,4}'); case 'm': case 'd': case 'H': case 'M': case 'S': case 'y': case 'I': case 'e': return group('\\s?\\d{1,2}');
      case 'j': return group('\\d{1,3}'); case 'b': case 'B': case 'h': return group('[A-Za-z]+'); case 'a': case 'A': return group('[A-Za-z]+');
      case 'p': return group('[AaPp][Mm]'); case 'Z': return group('[A-Za-z]+'); case 'z': return group('[+-]\\d{2}:?\\d{2}|Z');
      case 's': return group('\\d+'); case 'T': return (() => { fields.push('H', 'M', 'S'); return '(\\d{1,2}):(\\d{1,2}):(\\d{1,2})'; })();
      case 'F': return (() => { fields.push('Y', 'm', 'd'); return '(\\d{1,4})-(\\d{1,2})-(\\d{1,2})'; })();
      case '%': return '%';
    }
    return '';
  });
  const m = new RegExp('^' + pattern + '$').exec(input);
  if (!m) throw new JqError(`date "${input}" does not match format "${fmt}"`);
  let Y = 1900, M = 0, D = 1, h = 0, mi = 0, s = 0, pm: boolean | null = null, epoch: number | null = null;
  fields.forEach((c, i) => {
    const x = m[i + 1].trim();
    const n = Number(x);
    switch (c) {
      case 'Y': Y = n; break; case 'y': Y = n < 69 ? 2000 + n : 1900 + n; break;
      case 'm': M = n - 1; break; case 'd': case 'e': D = n; break;
      case 'H': case 'I': h = n; break; case 'M': mi = n; break; case 'S': s = n; break;
      case 'b': case 'B': case 'h': M = Math.max(0, MONTHS.findIndex(mn => mn.toLowerCase().startsWith(x.slice(0, 3).toLowerCase()))); break;
      case 'p': pm = x.toLowerCase() === 'pm'; break; case 's': epoch = n; break;
      case 'j': M = 0; D = n; break;
    }
  });
  if (pm !== null) h = (h % 12) + (pm ? 12 : 0);
  if (epoch !== null) return gmtime(epoch);
  return gmtime(Date.UTC(Y, M, D, h, mi, s) / 1000);
}

export function fromIsoDate(v: J): number {
  const s = strArg('parsed as a date', v);
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})Z$/.exec(s);
  if (!m) throw new JqError(`date "${s}" does not match format "%Y-%m-%dT%H:%M:%SZ"`);
  return Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]) / 1000;
}
