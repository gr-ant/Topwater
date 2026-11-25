// jq's data model: values, number/string formatting, ordering, and the JSON reader. Objects are Maps so that
// insertion order survives for numeric-looking keys, as jq keeps it.

export type J = null | boolean | number | string | J[] | Map<string, J>;

/** A runtime error that `try`/`?`/`//` can catch; `value` is what `catch` receives. */
export class JqError extends Error {
  constructor(public readonly value: J) { super(typeof value === 'string' ? value : dump(value)); }
}

export const isObj = (v: J): v is Map<string, J> => v instanceof Map;

export function typeName(v: J): string {
  return v === null ? 'null' : typeof v === 'boolean' ? 'boolean' : typeof v === 'number' ? 'number'
    : typeof v === 'string' ? 'string' : Array.isArray(v) ? 'array' : 'object';
}

export const truthy = (v: J) => v !== null && v !== false;

/** jq's error-message excerpt of a value: at most 14 characters, the last three replaced by "..." when cut. */
export function brief(v: J): string {
  const t = dump(v);
  return t.length <= 14 ? t : t.slice(0, 11) + '...';
}
export const errType = (v: J) => `${typeName(v)} (${brief(v)})`;

// ---------- numbers ----------

/** Shortest round-trip digits laid out as jq 1.7 prints them: 1e+17, 1e-05, 0.0001, 100000000000000. */
export function fmtNum(v: number): string {
  if (Number.isNaN(v)) return 'null';
  if (!Number.isFinite(v)) return v > 0 ? '1.7976931348623157e+308' : '-1.7976931348623157e+308';
  if (v === 0) return Object.is(v, -0) ? '-0' : '0';
  const [mant, exp] = Math.abs(v).toExponential().split('e');
  const digits = mant.replace('.', '');
  const decpt = Number(exp) + 1;
  const sign = v < 0 ? '-' : '';
  if (decpt <= -4 || decpt > digits.length + 15) {
    const e = decpt - 1;
    return `${sign}${digits[0]}${digits.length > 1 ? '.' + digits.slice(1) : ''}e${e < 0 ? '-' : '+'}${String(Math.abs(e)).padStart(2, '0')}`;
  }
  if (decpt <= 0) return `${sign}0.${'0'.repeat(-decpt)}${digits}`;
  if (decpt >= digits.length) return sign + digits + '0'.repeat(decpt - digits.length);
  return `${sign}${digits.slice(0, decpt)}.${digits.slice(decpt)}`;
}

// ---------- strings ----------

const hasAstral = (s: string) => /[\ud800-\udfff]/.test(s);
/** String length / slicing in code points, as jq counts. */
export const cpLength = (s: string) => hasAstral(s) ? Array.from(s).length : s.length;
export const cpSlice = (s: string, a: number, b: number) => hasAstral(s) ? Array.from(s).slice(a, b).join('') : s.slice(a, b);
/** UTF-16 index to code-point index. */
export const cpIndex = (s: string, i: number) => hasAstral(s) ? Array.from(s.slice(0, i)).length : i;

/** Code-point order (UTF-8 byte order), which differs from UTF-16 order only for astral vs. U+E000..U+FFFF. */
export function cmpStr(a: string, b: string): number {
  if (a === b) return 0;
  const n = Math.min(a.length, b.length);
  let i = 0;
  while (i < n && a.charCodeAt(i) === b.charCodeAt(i)) i++;
  if (i === n) return a.length < b.length ? -1 : 1;
  const fix = (c: number) => c >= 0xe000 ? c - 0x800 : c >= 0xd800 ? c + 0x2000 : c;
  return fix(a.charCodeAt(i)) < fix(b.charCodeAt(i)) ? -1 : 1;
}

const ESC: Record<string, string> = { '"': '\\"', '\\': '\\\\', '\n': '\\n', '\t': '\\t', '\r': '\\r', '\b': '\\b', '\f': '\\f' };

export function quote(s: string, ascii = false): string {
  // eslint-disable-next-line no-control-regex
  const re = ascii ? /["\\\u0000-\u001f\u007f-￿]/g : /["\\\u0000-\u001f\u007f]/g;
  return '"' + s.replace(re, c => ESC[c] ?? '\\u' + c.charCodeAt(0).toString(16).padStart(4, '0')) + '"';
}

// ---------- output ----------

export function dump(v: J, indent = '', sort = false, ascii = false): string {
  const out: string[] = [];
  const w = (x: J, level: number) => {
    if (x === null) out.push('null');
    else if (typeof x === 'boolean') out.push(x ? 'true' : 'false');
    else if (typeof x === 'number') out.push(fmtNum(x));
    else if (typeof x === 'string') out.push(quote(x, ascii));
    else {
      const isArr = Array.isArray(x);
      const entries: [string | null, J][] = isArr ? (x as J[]).map(e => [null, e]) : sort ? sortedEntries(x as Map<string, J>) : [...(x as Map<string, J>)];
      if (entries.length === 0) { out.push(isArr ? '[]' : '{}'); return; }
      out.push(isArr ? '[' : '{');
      entries.forEach(([k, e], i) => {
        if (i > 0) out.push(',');
        if (indent) out.push('\n' + indent.repeat(level + 1));
        if (k !== null) out.push(quote(k, ascii), indent ? ': ' : ':');
        w(e, level + 1);
      });
      if (indent) out.push('\n' + indent.repeat(level));
      out.push(isArr ? ']' : '}');
    }
  };
  w(v, 0);
  return out.join('');
}

function sortedEntries(m: Map<string, J>): [string, J][] {
  return [...m].sort((a, b) => cmpStr(a[0], b[0]));
}
export const sortedKeys = (m: Map<string, J>) => [...m.keys()].sort(cmpStr);

// ---------- ordering ----------

const rank = (v: J) => v === null ? 0 : v === false ? 1 : v === true ? 2 : typeof v === 'number' ? 3 : typeof v === 'string' ? 4 : Array.isArray(v) ? 5 : 6;

/** jq's total order: null < false < true < numbers < strings < arrays < objects. */
export function cmp(a: J, b: J): number {
  const ra = rank(a), rb = rank(b);
  if (ra !== rb) return ra < rb ? -1 : 1;
  if (typeof a === 'number') { const y = b as number; return Number.isNaN(a) || a < y ? -1 : a === y ? 0 : 1; }
  if (typeof a === 'string') return cmpStr(a, b as string);
  if (Array.isArray(a)) {
    const y = b as J[];
    for (let i = 0; i < Math.min(a.length, y.length); i++) { const c = cmp(a[i], y[i]); if (c) return c; }
    return a.length === y.length ? 0 : a.length < y.length ? -1 : 1;
  }
  if (isObj(a)) {
    const y = b as Map<string, J>;
    const ka = sortedKeys(a), kb = sortedKeys(y);
    const c = cmp(ka, kb);
    if (c) return c;
    for (const k of ka) { const d = cmp(a.get(k)!, y.get(k)!); if (d) return d; }
  }
  return 0;
}

// ---------- JSON input ----------

export class JsonError extends Error {
  constructor(message: string, public readonly pos: number) { super(message); }
}

const MAX_JSON_DEPTH = 2000;
const NUM = /-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/y;
const WORD = /[A-Za-z0-9_.+-]+/y;

/** Reads whitespace-separated JSON values from text, one at a time. */
export class JsonReader {
  pos = 0;
  constructor(private readonly s: string) {}

  private skip() { while (this.pos < this.s.length && ' \t\r\n'.includes(this.s[this.pos])) this.pos++; }

  /** The next value, or undefined at the end of the text. */
  next(): J | undefined {
    this.skip();
    if (this.pos >= this.s.length) return undefined;
    return this.value(0);
  }

  /** "line L, column C" of a position, as jq reports it. */
  where(pos: number): string {
    let line = 1, col = 0;
    for (let i = 0; i < Math.min(pos, this.s.length); i++) { if (this.s[i] === '\n') { line++; col = 0; } else col++; }
    return `line ${line}, column ${col}`;
  }
  lineAt(pos: number): number { let n = 0; for (let i = 0; i < Math.min(pos, this.s.length); i++) if (this.s[i] === '\n') n++; return n; }

  private value(depth: number): J {
    if (depth > MAX_JSON_DEPTH) throw new JsonError('Exceeds depth limit for parsing', this.pos);
    this.skip();
    const s = this.s;
    if (this.pos >= s.length) throw new JsonError('Unfinished JSON term at EOF', this.pos);
    const c = s[this.pos];
    if (c === '{') {
      this.pos++;
      const obj = new Map<string, J>();
      this.skip();
      if (s[this.pos] === '}') { this.pos++; return obj; }
      for (;;) {
        this.skip();
        if (this.pos >= s.length) throw new JsonError('Unfinished JSON term at EOF', this.pos);
        if (s[this.pos] !== '"') throw new JsonError('Object keys must be strings', this.pos + 1);
        const key = this.str();
        this.skip();
        if (s[this.pos] !== ':') throw new JsonError(this.pos >= s.length ? 'Unfinished JSON term at EOF' : 'Objects must consist of key:value pairs', this.pos + 1);
        this.pos++;
        obj.set(key, this.value(depth + 1));
        this.skip();
        if (s[this.pos] === ',') { this.pos++; continue; }
        if (s[this.pos] === '}') { this.pos++; return obj; }
        throw new JsonError(this.pos >= s.length ? 'Unfinished JSON term at EOF' : 'Expected separator between values', this.pos + 1);
      }
    }
    if (c === '[') {
      this.pos++;
      const list: J[] = [];
      this.skip();
      if (s[this.pos] === ']') { this.pos++; return list; }
      for (;;) {
        list.push(this.value(depth + 1));
        this.skip();
        if (s[this.pos] === ',') { this.pos++; continue; }
        if (s[this.pos] === ']') { this.pos++; return list; }
        throw new JsonError(this.pos >= s.length ? 'Unfinished JSON term at EOF' : 'Expected separator between values', this.pos + 1);
      }
    }
    if (c === '"') return this.str();
    if (c === ']' || c === '}') throw new JsonError(`Unmatched '${c}'`, this.pos + 1);
    NUM.lastIndex = this.pos;
    const m = NUM.exec(s);
    if (m) {
      const end = this.pos + m[0].length;
      if (end >= s.length || /[\s,\]}:[{"]/.test(s[end])) { this.pos = end; return Number(m[0]); }
    }
    WORD.lastIndex = this.pos;
    const w = WORD.exec(s);
    const word = w ? w[0] : c;
    this.pos += word.length;
    if (word === 'true') return true;
    if (word === 'false') return false;
    if (word === 'null') return null;
    if (word === 'nan' || word === 'NaN') return NaN;
    throw new JsonError(/^[A-Za-z]/.test(word) ? 'Invalid literal' : 'Invalid numeric literal', this.pos);
  }

  private str(): string {
    const s = this.s;
    this.pos++;
    let out = '';
    for (;;) {
      let start = this.pos;
      while (this.pos < s.length && s[this.pos] !== '"' && s[this.pos] !== '\\') this.pos++;
      out += s.slice(start, this.pos);
      if (this.pos >= s.length) throw new JsonError('Unfinished string at EOF', this.pos);
      if (s[this.pos++] === '"') return fixSurrogates(out);
      const e = s[this.pos++];
      switch (e) {
        case 'n': out += '\n'; break;
        case 't': out += '\t'; break;
        case 'r': out += '\r'; break;
        case 'b': out += '\b'; break;
        case 'f': out += '\f'; break;
        case '"': case '\\': case '/': out += e; break;
        case 'u': {
          const hex = s.slice(this.pos, this.pos + 4);
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) throw new JsonError('Invalid escape', this.pos);
          out += String.fromCharCode(parseInt(hex, 16));
          this.pos += 4;
          break;
        }
        default: throw new JsonError('Invalid escape', this.pos);
      }
      start = this.pos;
    }
  }
}

/** Lone surrogates (invalid in JSON text, and reserved here for undecodable bytes) become U+FFFD, as in jq. */
export function fixSurrogates(s: string): string {
  return /[\ud800-\udfff]/.test(s) ? s.replace(/[\ud800-\udbff](?![\udc00-\udfff])|(?<![\ud800-\udbff])[\udc00-\udfff]/g, '�') : s;
}

/** Parses exactly one JSON value (fromjson, --argjson). */
export function parseJson(text: string): J {
  const r = new JsonReader(text);
  let v: J | undefined;
  try { v = r.next(); } catch (e) { if (e instanceof JsonError) throw new JqError(`${e.message} at ${r.where(e.pos)} (while parsing '${text}')`); throw e; }
  if (v === undefined) throw new JqError(`Expected JSON value (while parsing '${text}')`);
  try { if (r.next() !== undefined) throw new JqError(`Unexpected extra JSON values (while parsing '${text}')`); } catch (e) {
    if (e instanceof JsonError) throw new JqError(`${e.message} at ${r.where(e.pos)} (while parsing '${text}')`);
    throw e;
  }
  return v;
}
