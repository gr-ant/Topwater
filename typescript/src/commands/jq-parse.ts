// The jq language front end: a lexer and a recursive-descent parser that follows jq's precedence
// (| lowest, then `,`, `//`, assignment, or, and, comparison, + -, * / %, unary minus, postfix) and resolves
// function and variable names while it parses, so an undefined name is a compile error before anything runs.

import type { J } from './jq-values.js';

export class CompileError extends Error {}

export type Node =
  | { t: 'id' }
  | { t: 'lit'; v: J }
  | { t: 'pipe' | 'comma' | 'and' | 'or' | 'alt'; l: Node; r: Node }
  | { t: 'idx'; target: Node; key: Node }
  | { t: 'slice'; target: Node; lo: Node | null; hi: Node | null }
  | { t: 'iter'; target: Node }
  | { t: 'arr'; body: Node | null }
  | { t: 'obj'; entries: [Node, Node][] }
  | { t: 'str'; parts: (string | Node)[]; fmt: string | null }
  | { t: 'fmt'; name: string }
  | { t: 'neg'; x: Node }
  | { t: 'bin'; op: string; l: Node; r: Node }
  | { t: 'if'; c: Node; a: Node; b: Node | null }
  | { t: 'try'; body: Node; handler: Node | null }
  | { t: 'var'; name: string }
  | { t: 'as'; src: Node; pat: Pat; body: Node }
  | { t: 'reduce'; src: Node; pat: Pat; init: Node; update: Node }
  | { t: 'foreach'; src: Node; pat: Pat; init: Node; update: Node; extract: Node | null }
  | { t: 'assign'; op: string; l: Node; r: Node }
  | { t: 'call'; name: string; args: Node[] }
  | { t: 'def'; name: string; params: string[]; body: Node; rest: Node }
  | { t: 'label'; name: string; body: Node }
  | { t: 'break'; name: string };

export type Pat =
  | { t: 'var'; name: string }
  | { t: 'arr'; items: Pat[] }
  | { t: 'obj'; entries: [Node, Pat][] };

// ---------- lexer ----------

type Tok = { k: 'num' | 'str' | 'field' | 'var' | 'fmt' | 'id' | 'kw' | 'op' | 'eof'; v: any; pos: number };

const KEYWORDS = new Set(['def', 'if', 'then', 'elif', 'else', 'end', 'as', 'reduce', 'foreach', 'try', 'catch', 'label', 'import', 'include', 'and', 'or']);
const OPS = ['//=', '|=', '+=', '-=', '*=', '/=', '%=', '==', '!=', '<=', '>=', '//', '..', '|', ',', '=', '<', '>', '+', '-', '*', '/', '%', '(', ')', '[', ']', '{', '}', ':', ';', '?', '.'];
const NUMBER = /(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?/y;
const IDENT = /[A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)*/y;
const isDigit = (c: string | undefined) => c !== undefined && c >= '0' && c <= '9';
const isHex4 = (s: string) => /^[0-9a-fA-F]{4}$/.test(s);

function lex(src: string): Tok[] {
  const toks: Tok[] = [];
  let i = 0;
  const n = src.length;
  while (i < n) {
    const c = src[i];
    if (/\s/.test(c)) { i++; continue; }
    if (c === '#') { while (i < n && src[i] !== '\n') i++; continue; }
    if (c === '"') { const [parts, end] = lexString(src, i + 1); toks.push({ k: 'str', v: parts, pos: i }); i = end; continue; }
    if (isDigit(c) || (c === '.' && isDigit(src[i + 1]))) {
      NUMBER.lastIndex = i;
      const m = NUMBER.exec(src)!;
      toks.push({ k: 'num', v: Number(m[0]), pos: i });
      i += m[0].length;
      continue;
    }
    if (c === '.' && /[A-Za-z_]/.test(src[i + 1] ?? '')) {
      IDENT.lastIndex = i + 1;
      const m = /[A-Za-z_][A-Za-z0-9_]*/y;
      m.lastIndex = i + 1;
      const name = m.exec(src)![0];
      toks.push({ k: 'field', v: name, pos: i });
      i += 1 + name.length;
      continue;
    }
    if (c === '$' || c === '@' || /[A-Za-z_]/.test(c)) {
      const start = c === '$' || c === '@' ? i + 1 : i;
      IDENT.lastIndex = start;
      const m = IDENT.exec(src);
      if (!m) throw new CompileError(`syntax error, unexpected INVALID_CHARACTER`);
      i = start + m[0].length;
      toks.push({ k: c === '$' ? 'var' : c === '@' ? 'fmt' : KEYWORDS.has(m[0]) ? 'kw' : 'id', v: m[0], pos: start });
      continue;
    }
    const op = OPS.find(o => src.startsWith(o, i));
    if (!op) throw new CompileError(`syntax error, unexpected INVALID_CHARACTER`);
    toks.push({ k: 'op', v: op, pos: i });
    i += op.length;
  }
  toks.push({ k: 'eof', v: null, pos: n });
  return toks;
}

/** A string literal's body after its opening quote: literal text and the source of each \( ... ) interpolation. */
function lexString(src: string, i: number): [(string | { src: string })[], number] {
  const parts: (string | { src: string })[] = [];
  let buf = '';
  const n = src.length;
  while (i < n && src[i] !== '"') {
    const c = src[i];
    if (c !== '\\') { buf += c; i++; continue; }
    const e = src[i + 1];
    if (e === '(') {
      let depth = 1, j = i + 2;
      while (j < n && depth > 0) {
        if (src[j] === '"') { j = lexString(src, j + 1)[1]; continue; }
        if (src[j] === '(') depth++;
        else if (src[j] === ')') depth--;
        j++;
      }
      if (depth > 0) throw new CompileError('unterminated string interpolation');
      if (buf) { parts.push(buf); buf = ''; }
      parts.push({ src: src.slice(i + 2, j - 1) });
      i = j;
      continue;
    }
    if (e === 'u' && isHex4(src.slice(i + 2, i + 6))) {
      let code = parseInt(src.slice(i + 2, i + 6), 16);
      i += 6;
      if (code >= 0xd800 && code < 0xdc00 && src[i] === '\\' && src[i + 1] === 'u' && isHex4(src.slice(i + 2, i + 6))) {
        const lo = parseInt(src.slice(i + 2, i + 6), 16);
        if (lo >= 0xdc00 && lo <= 0xdfff) { code = 0x10000 + ((code - 0xd800) << 10) + (lo - 0xdc00); i += 6; }
      }
      buf += code >= 0xd800 && code <= 0xdfff ? '�' : String.fromCodePoint(code);
      continue;
    }
    const map: Record<string, string> = { n: '\n', t: '\t', r: '\r', b: '\b', f: '\f', '"': '"', '\\': '\\', '/': '/' };
    if (e === undefined || !(e in map)) throw new CompileError(`invalid escape \\${e ?? ''} in string literal`);
    buf += map[e];
    i += 2;
  }
  if (i >= n) throw new CompileError('unterminated string literal');
  if (buf || parts.length === 0) parts.push(buf);
  return [parts, i + 1];
}

// ---------- scope ----------

interface Scope { parent: Scope | null; kind: 'f' | 'v' | 'l'; name: string }
const has = (s: Scope | null, kind: Scope['kind'], name: string) => { for (; s; s = s.parent) if (s.kind === kind && s.name === name) return true; return false; };

const MAX_DEPTH = 250;
const ID: Node = { t: 'id' };
const lit = (v: J): Node => ({ t: 'lit', v });
const ASSIGN_OPS = ['=', '|=', '+=', '-=', '*=', '/=', '%=', '//='];

export class Parser {
  private toks: Tok[];
  private i = 0;
  private scope: Scope | null;
  private depth: number;

  /** `globals` are the function keys ("name/arity") and `vars` the variable names that exist before the program. */
  constructor(private readonly src: string, private readonly globals: ReadonlySet<string>, private readonly vars: ReadonlySet<string>, scope: Scope | null = null, depth = 0) {
    this.toks = lex(src);
    this.scope = scope;
    this.depth = depth;
  }

  program(): Node {
    const node = this.pipe();
    if (this.peek().k !== 'eof') throw this.unexpected();
    return node;
  }

  /** Top-level definitions of the prelude: [name/arity, params, body] in order. */
  prelude(): { key: string; params: string[]; body: Node }[] {
    const defs: { key: string; params: string[]; body: Node }[] = [];
    let n = this.pipe();
    while (n.t === 'def') { defs.push({ key: `${n.name}/${n.params.length}`, params: n.params, body: n.body }); n = n.rest; }
    return defs;
  }

  // -- token helpers --

  private peek(k = 0): Tok { return this.toks[Math.min(this.i + k, this.toks.length - 1)]; }
  private isOp(...ops: string[]) { const t = this.peek(); return t.k === 'op' && ops.includes(t.v); }
  private isKw(kw: string) { const t = this.peek(); return t.k === 'kw' && t.v === kw; }
  private take(): Tok { return this.toks[this.i++]; }
  private expectOp(op: string) { if (!this.isOp(op)) throw this.unexpected(); this.i++; }
  private expectKw(kw: string) { if (!this.isKw(kw)) throw this.unexpected(); this.i++; }

  private unexpected(): CompileError {
    const t = this.peek();
    const what = t.k === 'eof' ? 'end of file' : t.k === 'str' ? 'QQSTRING_START' : t.k === 'num' ? 'LITERAL' : t.k === 'field' ? 'FIELD' : t.k === 'var' ? '$__loc__' === t.v ? '$__loc__' : 'BINDING' : `'${t.v}'`;
    return new CompileError(`syntax error, unexpected ${what}`);
  }

  private enter() { if (++this.depth > MAX_DEPTH) throw new CompileError('filter is nested too deeply'); }
  private leave() { this.depth--; }

  private withScope<T>(extra: Scope[], f: () => T): T {
    const saved = this.scope;
    for (const s of extra) this.scope = { ...s, parent: this.scope };
    try { return f(); } finally { this.scope = saved; }
  }

  // -- grammar --

  private pipe(): Node {
    this.enter();
    try {
      if (this.isKw('def')) return this.funcDef();
      if (this.isKw('label')) {
        this.i++;
        const t = this.take();
        if (t.k !== 'var') { this.i--; throw this.unexpected(); }
        this.expectOp('|');
        const body = this.withScope([{ parent: null, kind: 'l', name: t.v }], () => this.pipe());
        return { t: 'label', name: t.v, body };
      }
      const left = this.comma();
      if (this.isOp('|')) { this.i++; return { t: 'pipe', l: left, r: this.pipe() }; }
      return left;
    } finally { this.leave(); }
  }

  private funcDef(): Node {
    this.expectKw('def');
    const nameTok = this.take();
    if (nameTok.k !== 'id' && nameTok.k !== 'kw') { this.i--; throw this.unexpected(); }
    const params: string[] = [];
    if (this.isOp('(')) {
      this.i++;
      for (;;) {
        const p = this.take();
        if (p.k === 'var') params.push('$' + p.v);
        else if (p.k === 'id' || p.k === 'kw') params.push(p.v);
        else { this.i--; throw this.unexpected(); }
        if (this.isOp(';')) { this.i++; continue; }
        break;
      }
      this.expectOp(')');
    }
    this.expectOp(':');
    const self: Scope = { parent: null, kind: 'f', name: `${nameTok.v}/${params.length}` };
    const inner: Scope[] = [self];
    for (const p of params) {
      if (p.startsWith('$')) inner.push({ parent: null, kind: 'v', name: p.slice(1) }, { parent: null, kind: 'f', name: `${p.slice(1)}/0` });
      else inner.push({ parent: null, kind: 'f', name: `${p}/0` });
    }
    const body = this.withScope(inner, () => this.pipe());
    this.expectOp(';');
    const rest = this.withScope([self], () => (this.peek().k === 'eof' || this.isOp(')') ? ID : this.pipe()));
    return { t: 'def', name: nameTok.v, params, body, rest };
  }

  private comma(): Node {
    let left = this.alt();
    while (this.isOp(',')) { this.i++; left = { t: 'comma', l: left, r: this.alt() }; }
    return left;
  }

  private alt(): Node {
    const left = this.assign();
    if (this.isOp('//')) { this.i++; return { t: 'alt', l: left, r: this.alt() }; }
    return left;
  }

  private assign(): Node {
    const left = this.or();
    if (this.isOp(...ASSIGN_OPS)) { const op = this.take().v; return { t: 'assign', op, l: left, r: this.or() }; }
    return left;
  }

  private or(): Node {
    let left = this.and();
    while (this.isKw('or')) { this.i++; left = { t: 'or', l: left, r: this.and() }; }
    return left;
  }

  private and(): Node {
    let left = this.compare();
    while (this.isKw('and')) { this.i++; left = { t: 'and', l: left, r: this.compare() }; }
    return left;
  }

  private compare(): Node {
    const left = this.additive();
    if (this.isOp('==', '!=', '<', '<=', '>', '>=')) { const op = this.take().v; return { t: 'bin', op, l: left, r: this.additive() }; }
    return left;
  }

  private additive(): Node {
    let left = this.multiplicative();
    while (this.isOp('+', '-')) { const op = this.take().v; left = { t: 'bin', op, l: left, r: this.multiplicative() }; }
    return left;
  }

  private multiplicative(): Node {
    let left = this.unary();
    while (this.isOp('*', '/', '%')) { const op = this.take().v; left = { t: 'bin', op, l: left, r: this.unary() }; }
    return left;
  }

  private unary(): Node {
    if (this.isOp('-')) { this.i++; this.enter(); try { return { t: 'neg', x: this.unary() }; } finally { this.leave(); } }
    return this.postfix(false);
  }

  /** A term and its suffixes; `noAs` stops before `as` (reduce/foreach sources). */
  private postfix(noAs: boolean): Node {
    this.enter();
    try {
      let node = this.primary();
      for (;;) {
        const t = this.peek();
        if (t.k === 'field') { this.i++; node = { t: 'idx', target: node, key: lit(t.v) }; }
        else if (this.isOp('.') && this.peek(1).k === 'str') { this.i++; node = { t: 'idx', target: node, key: this.stringLit(null) }; }
        else if (this.isOp('[') || (this.isOp('.') && this.peek(1).k === 'op' && this.peek(1).v === '[')) {
          if (this.isOp('.')) this.i++;
          node = this.bracket(node);
        } else if (this.isOp('?')) { this.i++; node = { t: 'try', body: node, handler: null }; }
        else if (!noAs && this.isKw('as')) {
          this.i++;
          const [pat, names] = this.pattern();
          this.expectOp('|');
          const body = this.withScope(names.flatMap(name => [{ parent: null, kind: 'v' as const, name }]), () => this.pipe());
          return { t: 'as', src: node, pat, body };
        } else return node;
      }
    } finally { this.leave(); }
  }

  private bracket(target: Node): Node {
    this.expectOp('[');
    if (this.isOp(']')) { this.i++; return { t: 'iter', target }; }
    if (this.isOp(':')) {
      this.i++;
      const hi = this.pipe();
      this.expectOp(']');
      return { t: 'slice', target, lo: null, hi };
    }
    const idx = this.pipe();
    if (this.isOp(':')) {
      this.i++;
      const hi = this.isOp(']') ? null : this.pipe();
      this.expectOp(']');
      return { t: 'slice', target, lo: idx, hi };
    }
    this.expectOp(']');
    return { t: 'idx', target, key: idx };
  }

  /** A destructuring pattern and the variables it binds. */
  private pattern(): [Pat, string[]] {
    const t = this.peek();
    if (t.k === 'var') { this.i++; return [{ t: 'var', name: t.v }, [t.v]]; }
    if (this.isOp('[')) {
      this.i++;
      const items: Pat[] = [], names: string[] = [];
      for (;;) {
        const [p, n] = this.pattern();
        items.push(p); names.push(...n);
        if (this.isOp(',')) { this.i++; continue; }
        break;
      }
      this.expectOp(']');
      return [{ t: 'arr', items }, names];
    }
    if (this.isOp('{')) {
      this.i++;
      const entries: [Node, Pat][] = [], names: string[] = [];
      for (;;) {
        const k = this.peek();
        if (k.k === 'var') {
          this.i++;
          entries.push([lit(k.v), { t: 'var', name: k.v }]);
          names.push(k.v);
        } else {
          const key = this.objectKey();
          this.expectOp(':');
          const [p, n] = this.pattern();
          entries.push([key, p]); names.push(...n);
        }
        if (this.isOp(',')) { this.i++; continue; }
        break;
      }
      this.expectOp('}');
      return [{ t: 'obj', entries }, names];
    }
    throw this.unexpected();
  }

  private stringLit(fmt: string | null): Node {
    const parts = this.take().v as (string | { src: string })[];
    const out = parts.map(p => typeof p === 'string' ? p : new Parser(p.src, this.globals, this.vars, this.scope, this.depth).program());
    if (out.length === 1 && typeof out[0] === 'string' && fmt === null) return lit(out[0]);
    return { t: 'str', parts: out, fmt };
  }

  private objectKey(): Node {
    const t = this.peek();
    if (t.k === 'id' || t.k === 'kw') { this.i++; return lit(t.v); }
    if (t.k === 'num') { this.i++; return lit(String(t.v)); }
    if (t.k === 'var') { this.i++; return this.variable(t.v); }
    if (t.k === 'str') return this.stringLit(null);
    if (t.k === 'fmt' && this.peek(1).k === 'str') { this.i++; return this.stringLit(t.v); }
    if (this.isOp('(')) {
      this.i++;
      const key = this.pipe();
      this.expectOp(')');
      return key;
    }
    throw this.unexpected();
  }

  private variable(name: string): Node {
    if (name === '__loc__') return lit(new Map<string, J>([['file', '<top-level>'], ['line', 1]]));
    if (!has(this.scope, 'v', name) && !this.vars.has(name)) throw new CompileError(`$${name} is not defined`);
    return { t: 'var', name };
  }

  private primary(): Node {
    const t = this.peek();
    switch (t.k) {
      case 'num': this.i++; return lit(t.v);
      case 'str': return this.stringLit(null);
      case 'fmt': this.i++; return this.peek().k === 'str' ? this.stringLit(t.v) : { t: 'fmt', name: t.v };
      case 'field': this.i++; return { t: 'idx', target: ID, key: lit(t.v) };
      case 'var': this.i++; return this.variable(t.v);
      case 'id': return this.identifier();
      case 'kw': return this.keyword();
      case 'op': break;
      default: throw this.unexpected();
    }
    switch (t.v) {
      case '.':
        this.i++;
        if (this.peek().k === 'str') return { t: 'idx', target: ID, key: this.stringLit(null) };
        return ID;
      case '..': this.i++; return { t: 'call', name: 'recurse', args: [] };
      case '(': { this.i++; const n = this.pipe(); this.expectOp(')'); return n; }
      case '[': {
        this.i++;
        if (this.isOp(']')) { this.i++; return { t: 'arr', body: null }; }
        const n = this.pipe();
        this.expectOp(']');
        return { t: 'arr', body: n };
      }
      case '{': return this.object();
      case '-': this.i++; return { t: 'neg', x: this.postfix(false) };
    }
    throw this.unexpected();
  }

  private identifier(): Node {
    const t = this.take();
    const name = t.v as string;
    if (name === 'true') return lit(true);
    if (name === 'false') return lit(false);
    if (name === 'null') return lit(null);
    if (name === 'break' && this.peek().k === 'var') {
      const label = this.take().v as string;
      if (!has(this.scope, 'l', label)) throw new CompileError(`$*label-${label} is not defined`);
      return { t: 'break', name: label };
    }
    const args: Node[] = [];
    if (this.isOp('(')) {
      this.i++;
      args.push(this.pipe());
      while (this.isOp(';')) { this.i++; args.push(this.pipe()); }
      this.expectOp(')');
    }
    const key = `${name}/${args.length}`;
    if (!has(this.scope, 'f', key) && !this.globals.has(key)) throw new CompileError(`${key} is not defined`);
    return { t: 'call', name, args };
  }

  private keyword(): Node {
    const t = this.take();
    switch (t.v) {
      case 'if': {
        const c = this.pipe();
        this.expectKw('then');
        const a = this.pipe();
        if (this.isKw('elif')) { const rest = this.keywordElif(); return { t: 'if', c, a, b: rest }; }
        let b: Node | null = null;
        if (this.isKw('else')) { this.i++; b = this.pipe(); }
        this.expectKw('end');
        return { t: 'if', c, a, b };
      }
      case 'try': {
        const body = this.postTry();
        let handler: Node | null = null;
        if (this.isKw('catch')) { this.i++; handler = this.postTry(); }
        return { t: 'try', body, handler };
      }
      case 'reduce': case 'foreach': {
        const src = this.postfix(true);
        this.expectKw('as');
        const [pat, names] = this.pattern();
        this.expectOp('(');
        const init = this.pipe();
        this.expectOp(';');
        const scoped = names.map(name => ({ parent: null, kind: 'v' as const, name }));
        const update = this.withScope(scoped, () => this.pipe());
        let extract: Node | null = null;
        if (t.v === 'foreach' && this.isOp(';')) { this.i++; extract = this.withScope(scoped, () => this.pipe()); }
        this.expectOp(')');
        return t.v === 'reduce' ? { t: 'reduce', src, pat, init, update } : { t: 'foreach', src, pat, init, update, extract };
      }
      case 'def': this.i--; return this.funcDef();
      case 'label': this.i--; return this.pipe();
    }
    this.i--;
    throw this.unexpected();
  }

  /** `elif c then a ...` continues as a nested if. */
  private keywordElif(): Node {
    this.expectKw('elif');
    const c = this.pipe();
    this.expectKw('then');
    const a = this.pipe();
    if (this.isKw('elif')) return { t: 'if', c, a, b: this.keywordElif() };
    let b: Node | null = null;
    if (this.isKw('else')) { this.i++; b = this.pipe(); }
    this.expectKw('end');
    return { t: 'if', c, a, b };
  }

  /** The body of try/catch: a postfix term. */
  private postTry(): Node { return this.postfix(true); }

  private object(): Node {
    this.expectOp('{');
    const entries: [Node, Node][] = [];
    while (!this.isOp('}')) {
      const keyTok = this.peek();
      let key = this.objectKey();
      let value: Node;
      if (this.isOp(':')) {
        this.i++;
        value = this.alt();
        while (this.isOp('|')) { this.i++; value = { t: 'pipe', l: value, r: this.alt() }; }
      } else if (keyTok.k === 'var') {
        value = key;
        key = lit(keyTok.v);
      } else value = { t: 'idx', target: ID, key };
      entries.push([key, value]);
      if (!this.isOp(',')) break;
      this.i++;
    }
    this.expectOp('}');
    return { t: 'obj', entries };
  }
}
