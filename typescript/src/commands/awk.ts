import type { Shell } from '../shell.js';
import { ShellError, reason, tooLarge, type CommandContext } from '../command.js';
import { posixToJs } from '../regex.js';

// ---------- values ----------
// A value is a number, a string, an SN (a string from input that is a number when it looks like one) or undefined (uninitialized).

class SN {
  private n?: number;
  private looks?: boolean;
  constructor(readonly s: string) {}
  get isNum(): boolean { return this.looks ??= NUM_RE.test(this.s); }
  num(): number { return this.n ??= strToNum(this.s); }
}
type AV = number | string | SN | undefined;
type Arr = Map<string, AV>;
type Slot = AV | Arr;

const NUM_RE = /^\s*[-+]?(?:\d+\.?\d*(?:[eE][-+]?\d+)?|\.\d+(?:[eE][-+]?\d+)?)\s*$/;
const LEAD_RE = /^\s*[-+]?(?:\d+\.?\d*(?:[eE][-+]?\d+)?|\.\d+(?:[eE][-+]?\d+)?)/;

function strToNum(s: string): number {
  const m = LEAD_RE.exec(s);
  return m ? parseFloat(m[0]) : 0;
}
const toNum = (v: AV): number => (typeof v === 'number' ? v : v === undefined ? 0 : typeof v === 'string' ? strToNum(v) : v.num());
const isNumeric = (v: AV): boolean => typeof v === 'number' || v === undefined || (v instanceof SN && v.isNum);
const truthy = (v: AV): boolean => (typeof v === 'number' ? v !== 0 : v === undefined ? false : typeof v === 'string' ? v.length > 0 : v.isNum ? v.num() !== 0 : v.s.length > 0);

// ---------- number formatting ----------

function expStr(abs: number, prec: number, upper: boolean): string {
  const s = abs.toExponential(prec); // 1.5e+5
  const i = s.indexOf('e');
  const exp = s.slice(i + 1);
  const digits = exp.slice(1).padStart(2, '0');
  return s.slice(0, i) + (upper ? 'E' : 'e') + exp[0] + digits;
}

/** Round-half-even for exact ties, which toFixed gets wrong (C prints 0.125 as 0.12). */
function fixedStr(abs: number, prec: number): string {
  if (abs >= 1e21) return BigInt(abs).toString() + (prec > 0 ? '.' + '0'.repeat(prec) : '');
  prec = Math.min(prec, 100);
  let s = abs.toFixed(prec);
  const scaled = abs * 10 ** prec;
  if (prec <= 15 && scaled < 2 ** 52 && scaled % 1 === 0.5 && scaled / 10 ** prec === abs && Math.floor(scaled) % 2 === 0) s = (Math.floor(scaled) / 10 ** prec).toFixed(prec);
  return s;
}

function genStr(abs: number, prec: number, upper: boolean, alt: boolean): string {
  if (abs === 0) return alt ? (prec <= 1 ? '0.' : '0.' + '0'.repeat(prec - 1)) : '0';
  const p = prec === 0 ? 1 : Math.min(prec, 100);
  const e = abs.toExponential(p - 1);
  const x = parseInt(e.slice(e.indexOf('e') + 1), 10);
  if (x < -4 || x >= p) {
    let mant = e.slice(0, e.indexOf('e'));
    if (!alt && mant.includes('.')) mant = mant.replace(/0+$/, '').replace(/\.$/, '');
    return mant + (upper ? 'E' : 'e') + (x < 0 ? '-' : '+') + String(Math.abs(x)).padStart(2, '0');
  }
  let r = fixedStr(abs, p - 1 - x);
  if (!alt && r.includes('.')) r = r.replace(/0+$/, '').replace(/\.$/, '');
  return r;
}

/** Integral values print as integers however large; others use the %g-style format. */
function numToStr(v: number, fmt: string): string {
  if (Number.isFinite(v) && v === Math.floor(v)) return Math.abs(v) < 1e15 ? String(v === 0 ? 0 : v) : BigInt(v).toString();
  if (Number.isNaN(v)) return Object.is(v, NaN) ? 'nan' : '-nan';
  if (!Number.isFinite(v)) return v > 0 ? 'inf' : '-inf';
  if (fmt === '%.6g') return (v < 0 ? '-' : '') + genStr(Math.abs(v), 6, false, false);
  return sprintfNums(fmt, v);
}
function sprintfNums(fmt: string, v: number): string { return formatSpec(fmt, [v], () => '', 1 << 30); }

const SPEC = /%([-+ #0]*)(\*|\d+)?(?:\.(\*|\d*))?(?:hh|h|ll|l|L|q|j|z|t)?([diouxXeEfFgGcs%])/g;

function formatSpec(fmt: string, args: AV[], str: (v: AV) => string, max: number): string {
  let next = 0;
  const take = (): AV => (next < args.length ? args[next++] : undefined);
  let total = 0;
  const out = fmt.replace(SPEC, (_m, fl: string, w: string | undefined, p: string | undefined, conv: string) => {
    if (conv === '%') return '%';
    let flags = fl;
    let width = w === '*' ? Math.trunc(toNum(take())) : w ? parseInt(w, 10) : 0;
    let prec: number | null = p === undefined ? null : p === '*' ? Math.trunc(toNum(take())) : p ? parseInt(p, 10) : 0;
    if (prec !== null && prec < 0) prec = null;
    if (width < 0) { flags += '-'; width = -width; }
    if (width > max || (prec ?? 0) > max) throw tooLarge('awk: string', max, 'build smaller strings or print as you go');
    const arg = take();
    let body: string, sign = '', numeric = true;
    const signOf = (neg: boolean) => (neg ? '-' : flags.includes('+') ? '+' : flags.includes(' ') ? ' ' : '');
    switch (conv) {
      case 'd': case 'i': {
        const n = toNum(arg);
        if (!Number.isFinite(n)) { sign = signOf(n < 0); body = Number.isNaN(n) ? 'nan' : 'inf'; numeric = false; break; }
        const t = Math.trunc(n);
        sign = signOf(t < 0);
        body = (t < 0 ? -t : t) < 1e21 ? String(Math.abs(t)) : BigInt(Math.abs(t)).toString();
        if (prec !== null) body = body.padStart(prec, '0');
        break;
      }
      case 'o': case 'x': case 'X': case 'u': {
        const n = toNum(arg);
        const t = Number.isFinite(n) ? BigInt.asUintN(64, BigInt(Math.trunc(n))) : 0n;
        body = t.toString(conv === 'u' ? 10 : conv === 'o' ? 8 : 16);
        if (conv === 'X') body = body.toUpperCase();
        if (prec !== null) body = body.padStart(prec, '0');
        if (flags.includes('#') && t !== 0n) sign = conv === 'o' ? '0' : conv === 'x' ? '0x' : conv === 'X' ? '0X' : '';
        break;
      }
      case 'e': case 'E': case 'f': case 'F': case 'g': case 'G': {
        const n = toNum(arg);
        sign = signOf(n < 0 || Object.is(n, -0));
        const a = Math.abs(n);
        if (!Number.isFinite(a)) { body = Number.isNaN(a) ? 'nan' : 'inf'; if (conv === 'E' || conv === 'G' || conv === 'F') body = body.toUpperCase(); numeric = false; break; }
        const pr = Math.min(prec ?? 6, 100);
        body = conv === 'e' || conv === 'E' ? expStr(a, pr, conv === 'E')
          : conv === 'f' || conv === 'F' ? fixedStr(a, pr) + (flags.includes('#') && pr === 0 ? '.' : '')
          : genStr(a, pr, conv === 'G', flags.includes('#'));
        break;
      }
      case 'c':
        numeric = false;
        if (typeof arg === 'number' || (arg instanceof SN && arg.isNum)) {
          const n = Math.trunc(toNum(arg));
          body = n >= 0 && n <= 0x10ffff && !(n >= 0xd800 && n <= 0xdfff && n < 0xdc80) ? String.fromCodePoint(n) : '';
        } else { const s = str(arg); body = s ? String.fromCodePoint(s.codePointAt(0)!) : ''; }
        break;
      default:
        numeric = false;
        body = str(arg);
        if (prec !== null && prec < body.length) body = body.slice(0, prec);
    }
    const len = sign.length + body.length;
    let r: string;
    if (len >= width) r = sign + body;
    else if (flags.includes('-')) r = (sign + body).padEnd(width);
    else if (flags.includes('0') && numeric && !(prec !== null && 'diouxX'.includes(conv))) r = sign + body.padStart(width - sign.length, '0');
    else r = (sign + body).padStart(width);
    total += r.length;
    if (total > max) throw tooLarge('awk: string', max, 'build smaller strings or print as you go');
    return r;
  });
  return out;
}

// ---------- syntax tree ----------

interface Ref { name: string; slot: number; sp: boolean } // slot >= 0: a function parameter; sp: NF, NR or FNR

type Node =
  | { k: 'num'; v: number } | { k: 'str'; v: string } | { k: 're'; src: string }
  | { k: 'var'; r: Ref } | { k: 'field'; e: Node } | { k: 'idx'; r: Ref; keys: Node[] }
  | { k: 'assign'; t: Node; op: string; v: Node }
  | { k: 'cond'; c: Node; a: Node; b: Node }
  | { k: 'and' | 'or'; l: Node; r: Node }
  | { k: 'bin'; op: string; l: Node; r: Node }
  | { k: 'un'; op: string; e: Node }
  | { k: 'incr'; t: Node; inc: boolean; pre: boolean }
  | { k: 'match'; s: Node; re: Node; neg: boolean }
  | { k: 'in'; keys: Node[]; r: Ref }
  | { k: 'cat'; l: Node; r: Node }
  | { k: 'call'; name: string; args: Node[] }
  | { k: 'ucall'; name: string; args: Node[] }
  | { k: 'getline'; target?: Node; file?: Node };

type Stmt =
  | { k: 'block'; body: Stmt[] } | { k: 'expr'; e: Node }
  | { k: 'print'; printf: boolean; args: Node[]; redir?: string; dest?: Node }
  | { k: 'if'; c: Node; then: Stmt; else?: Stmt }
  | { k: 'while'; c: Node; body: Stmt; dw: boolean }
  | { k: 'for'; init?: Node; c?: Node; step?: Node; body: Stmt }
  | { k: 'forin'; v: Node; r: Ref; body: Stmt }
  | { k: 'simple'; kind: 'next' | 'nextfile' | 'exit' | 'break' | 'continue'; arg?: Node }
  | { k: 'delete'; r: Ref; keys?: Node[] }
  | { k: 'return'; v?: Node };

interface Rule { kind: 'BEGIN' | 'END' | 'main'; pattern?: Node; pattern2?: Node; action?: Stmt; inRange: boolean }
interface Func { params: string[]; body: Stmt }

const awkErr = (m: string) => new ShellError(m, 2);

// ---------- lexer ----------

type TK = 'num' | 'str' | 're' | 'name' | 'func' | 'call' | 'kw' | 'op' | 'nl' | 'eof';
interface Tok { k: TK; t: string; n?: number }

const KEYWORDS = new Set(['BEGIN', 'END', 'if', 'else', 'while', 'for', 'do', 'break', 'continue', 'next', 'nextfile', 'exit', 'delete', 'in', 'print', 'printf', 'getline', 'function', 'func', 'return']);
const FUNCS = new Set(['length', 'substr', 'index', 'split', 'sub', 'gsub', 'match', 'sprintf', 'tolower', 'toupper', 'int', 'sqrt', 'exp', 'log', 'sin', 'cos', 'atan2', 'rand', 'srand', 'system', 'close', 'fflush']);
const OPS = ['**=', '+=', '-=', '*=', '/=', '%=', '^=', '**', '==', '<=', '>=', '!=', '++', '--', '&&', '||', '>>', '!~', '{', '}', '(', ')', '[', ']', ';', ',', '+', '-', '*', '/', '%', '^', '!', '>', '<', '|', '?', ':', '~', '$', '='];
const ASSIGN_OPS = new Set(['=', '+=', '-=', '*=', '/=', '%=', '^=', '**=']);

/** Awk string escapes; an unknown escape keeps its backslash (so "\." still reaches a regex as \.). */
function unescapeAwk(s: string): string {
  if (!s.includes('\\')) return s;
  return s.replace(/\\(?:([0-7]{1,3})|([\s\S]))/g, (_m, oct: string | undefined, c: string | undefined) => {
    if (oct) return String.fromCharCode(parseInt(oct, 8) & 0xff);
    switch (c) {
      case 'n': return '\n'; case 't': return '\t'; case 'r': return '\r'; case '\\': return '\\'; case '"': return '"';
      case '/': return '/'; case 'a': return '\x07'; case 'b': return '\b'; case 'f': return '\f'; case 'v': return '\v'; case '\n': return '';
      default: return '\\' + c;
    }
  });
}

function lex(s: string): Tok[] {
  const toks: Tok[] = [];
  const regexAllowed = () => {
    const l = toks[toks.length - 1];
    if (!l) return true;
    if (l.k === 'num' || l.k === 'str' || l.k === 'name' || l.k === 're') return false;
    if (l.k === 'func' && l.t === 'length') return false;
    if (l.k === 'op' && (l.t === ')' || l.t === ']' || l.t === '$' || l.t === '++' || l.t === '--')) return false;
    return true;
  };
  const numRe = /0[xX][0-9a-fA-F]+|(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?/y;
  const nameRe = /[A-Za-z_][A-Za-z0-9_]*/y;
  for (let i = 0; i < s.length;) {
    const c = s[i];
    if (c === ' ' || c === '\t' || c === '\r') { i++; continue; }
    if (c === '\\' && s[i + 1] === '\n') { i += 2; continue; }
    if (c === '\\' && s[i + 1] === '\r' && s[i + 2] === '\n') { i += 3; continue; }
    if (c === '#') { while (i < s.length && s[i] !== '\n') i++; continue; }
    if (c === '\n') { toks.push({ k: 'nl', t: '\n' }); i++; continue; }
    if ((c >= '0' && c <= '9') || (c === '.' && s[i + 1] >= '0' && s[i + 1] <= '9')) {
      numRe.lastIndex = i;
      const t = numRe.exec(s)![0];
      toks.push({ k: 'num', t, n: /^0[xX]/.test(t) ? parseInt(t, 16) : parseFloat(t) });
      i += t.length;
      continue;
    }
    if (c === '"') {
      let j = i + 1;
      while (j < s.length && s[j] !== '"') { if (s[j] === '\\') j++; j++; }
      if (j >= s.length) throw awkErr('syntax error: non-terminated string');
      toks.push({ k: 'str', t: unescapeAwk(s.slice(i + 1, j)) });
      i = j + 1;
      continue;
    }
    if (c === '/' && regexAllowed()) {
      let sb = '', inClass = false, j = i + 1;
      for (; j < s.length && (s[j] !== '/' || inClass); j++) {
        if (s[j] === '\n') throw awkErr('syntax error: newline in regex');
        if (s[j] === '\\' && j + 1 < s.length) { sb += s[j + 1] === '/' ? '/' : s[j] + s[j + 1]; j++; continue; }
        if (s[j] === '[' && !inClass) { inClass = true; sb += '['; if (s[j + 1] === '^') sb += s[++j]; if (s[j + 1] === ']') sb += s[++j]; continue; }
        if (s[j] === ']') inClass = false;
        sb += s[j];
      }
      if (j >= s.length) throw awkErr('syntax error: non-terminated regular expression');
      toks.push({ k: 're', t: sb });
      i = j + 1;
      continue;
    }
    if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c === '_') {
      nameRe.lastIndex = i;
      const w = nameRe.exec(s)![0];
      const j = i + w.length;
      let k: TK = KEYWORDS.has(w) ? 'kw' : FUNCS.has(w) ? 'func' : 'name';
      if (k === 'name' && s[j] === '(') k = 'call'; // a user function call: no space before the (
      toks.push({ k, t: w });
      i = j;
      continue;
    }
    const op = OPS.find(o => s.startsWith(o, i));
    if (!op) throw awkErr(`syntax error: unexpected character '${c}'`);
    toks.push({ k: 'op', t: op });
    i += op.length;
  }
  toks.push({ k: 'eof', t: '' });
  return toks;
}

// ---------- parser ----------

class Parser {
  private t: Tok[];
  private p = 0;
  private params: string[] | null = null;
  readonly functions = new Map<string, Func>();
  constructor(src: string) { this.t = lex(src); }

  private get peek() { return this.t[this.p]; }
  private at(o: number) { return this.t[Math.min(this.p + o, this.t.length - 1)]; }
  private isOp(o: string) { return this.peek.k === 'op' && this.peek.t === o; }
  private isKw(k: string) { return this.peek.k === 'kw' && this.peek.t === k; }
  private accept(o: string) { if (!this.isOp(o)) return false; this.p++; return true; }
  private expect(o: string) { if (!this.accept(o)) throw awkErr(`syntax error: expected '${o}' near '${this.peek.t}'`); }
  private skipNl() { while (this.peek.k === 'nl') this.p++; }
  private skipTerm() { while (this.peek.k === 'nl' || this.isOp(';')) this.p++; }
  private endOfStmt() { const k = this.peek; return k.k === 'nl' || k.k === 'eof' || this.isOp(';') || this.isOp('}'); }

  private ref(name: string): Ref {
    const slot = this.params ? this.params.indexOf(name) : -1;
    return { name, slot, sp: slot < 0 && (name === 'NF' || name === 'NR' || name === 'FNR') };
  }

  parse(): Rule[] {
    const rules: Rule[] = [];
    this.skipTerm();
    while (this.peek.k !== 'eof') {
      const r: Rule = { kind: 'main', inRange: false };
      if (this.isKw('BEGIN') || this.isKw('END')) { r.kind = this.peek.t as 'BEGIN' | 'END'; this.p++; this.skipNl(); r.action = this.block(); }
      else if (this.isKw('function') || this.isKw('func')) {
        this.p++;
        if (this.peek.k !== 'name' && this.peek.k !== 'call') throw awkErr('syntax error: function name expected');
        const name = this.peek.t;
        this.p++;
        this.expect('(');
        const params: string[] = [];
        while (!this.isOp(')')) {
          if (this.peek.k !== 'name') throw awkErr('syntax error: function parameter name expected');
          params.push(this.peek.t);
          this.p++;
          if (!this.accept(',')) break;
          this.skipNl();
        }
        this.expect(')');
        this.skipNl();
        this.params = params;
        this.functions.set(name, { params, body: this.block() });
        this.params = null;
        this.skipTerm();
        continue;
      } else {
        if (!this.isOp('{')) {
          r.pattern = this.expr(false);
          if (this.accept(',')) { this.skipNl(); r.pattern2 = this.expr(false); }
        }
        if (this.isOp('{')) r.action = this.block();
      }
      rules.push(r);
      this.skipTerm();
    }
    return rules;
  }

  private block(): Stmt {
    this.expect('{');
    const body: Stmt[] = [];
    for (;;) {
      this.skipTerm();
      if (this.accept('}')) break;
      if (this.peek.k === 'eof') throw awkErr("syntax error: unexpected end of program, missing '}'");
      body.push(this.statement());
    }
    return { k: 'block', body };
  }

  private endSimple() {
    if (this.peek.k === 'nl' || this.isOp(';')) { this.p++; return; }
    if (this.isOp('}') || this.peek.k === 'eof') return;
    throw awkErr(`syntax error: unexpected '${this.peek.t}'`);
  }

  private statement(): Stmt {
    if (this.isOp('{')) return this.block();
    if (this.isOp(';')) { this.p++; return { k: 'block', body: [] }; }
    if (this.peek.k === 'kw') {
      switch (this.peek.t) {
        case 'if': {
          this.p++;
          this.expect('(');
          const c = this.expr(false);
          this.expect(')');
          this.skipNl();
          const then = this.statement();
          const save = this.p;
          this.skipTerm();
          if (this.isKw('else')) { this.p++; this.skipNl(); return { k: 'if', c, then, else: this.statement() }; }
          this.p = save;
          return { k: 'if', c, then };
        }
        case 'while': {
          this.p++;
          this.expect('(');
          const c = this.expr(false);
          this.expect(')');
          if (this.isOp(';')) { this.p++; return { k: 'while', c, body: { k: 'block', body: [] }, dw: false }; }
          this.skipNl();
          return { k: 'while', c, body: this.statement(), dw: false };
        }
        case 'do': {
          this.p++;
          this.skipNl();
          const body = this.statement();
          this.skipTerm();
          if (!this.isKw('while')) throw awkErr("syntax error: expected 'while' after 'do' body");
          this.p++;
          this.expect('(');
          const c = this.expr(false);
          this.expect(')');
          this.endSimple();
          return { k: 'while', c, body, dw: true };
        }
        case 'for': {
          this.p++;
          this.expect('(');
          if (this.at(0).k === 'name' && this.at(1).k === 'kw' && this.at(1).t === 'in' && this.at(2).k === 'name' && this.at(3).k === 'op' && this.at(3).t === ')') {
            const v: Node = { k: 'var', r: this.ref(this.peek.t) };
            const r = this.ref(this.at(2).t);
            this.p += 4;
            this.skipNl();
            return { k: 'forin', v, r, body: this.statement() };
          }
          const init = this.isOp(';') ? undefined : this.expr(false);
          this.expect(';');
          this.skipNl();
          const c = this.isOp(';') ? undefined : this.expr(false);
          this.expect(';');
          this.skipNl();
          const step = this.isOp(')') ? undefined : this.expr(false);
          this.expect(')');
          if (this.isOp(';')) { this.p++; return { k: 'for', init, c, step, body: { k: 'block', body: [] } }; }
          this.skipNl();
          return { k: 'for', init, c, step, body: this.statement() };
        }
        case 'next': case 'nextfile': case 'break': case 'continue': {
          const kind = this.peek.t as 'next' | 'nextfile' | 'break' | 'continue';
          this.p++;
          this.endSimple();
          return { k: 'simple', kind };
        }
        case 'exit': {
          this.p++;
          const arg = this.endOfStmt() ? undefined : this.expr(false);
          this.endSimple();
          return { k: 'simple', kind: 'exit', arg };
        }
        case 'delete': {
          this.p++;
          if (this.at(0).k !== 'name') throw awkErr('syntax error: delete: expected array name');
          const r = this.ref(this.peek.t);
          this.p++;
          let keys: Node[] | undefined;
          if (this.accept('[')) { keys = this.exprList(false); this.expect(']'); }
          this.endSimple();
          return { k: 'delete', r, keys };
        }
        case 'print': case 'printf': {
          const printf = this.peek.t === 'printf';
          this.p++;
          let args: Node[] = [];
          const atEnd = () => this.endOfStmt() || this.isOp('>') || this.isOp('>>') || this.isOp('|');
          if (this.isOp('(')) {
            const save = this.p;
            this.p++;
            const list = this.exprList(false);
            if (this.accept(')') && atEnd()) args = list;
            else { this.p = save; args = this.exprList(true); }
          } else if (!atEnd()) args = this.exprList(true);
          let redir: string | undefined, dest: Node | undefined;
          if (this.isOp('>') || this.isOp('>>')) { redir = this.peek.t; this.p++; dest = this.concat(true); }
          else if (this.isOp('|')) throw awkErr('output pipes (print | "cmd") are not supported');
          this.endSimple();
          return { k: 'print', printf, args, redir, dest };
        }
        case 'return': {
          this.p++;
          const v = this.endOfStmt() ? undefined : this.expr(false);
          this.endSimple();
          return { k: 'return', v };
        }
      }
    }
    const e = this.expr(false);
    this.endSimple();
    return { k: 'expr', e };
  }

  private exprList(noGt: boolean): Node[] {
    const list = [this.expr(noGt)];
    while (this.accept(',')) { this.skipNl(); list.push(this.expr(noGt)); }
    return list;
  }

  private isLvalue(n: Node) { return n.k === 'var' || n.k === 'field' || n.k === 'idx'; }

  private expr(noGt: boolean): Node {
    const lhs = this.ternary(noGt);
    if (this.peek.k === 'op' && ASSIGN_OPS.has(this.peek.t) && this.isLvalue(lhs)) {
      const op = this.peek.t;
      this.p++;
      this.skipNl();
      return { k: 'assign', t: lhs, op, v: this.expr(noGt) };
    }
    return lhs;
  }

  private ternary(noGt: boolean): Node {
    const c = this.or(noGt);
    if (!this.accept('?')) return c;
    this.skipNl();
    const a = this.expr(noGt);
    this.skipNl();
    this.expect(':');
    this.skipNl();
    return { k: 'cond', c, a, b: this.expr(noGt) };
  }

  private or(noGt: boolean): Node {
    let l = this.and(noGt);
    while (this.accept('||')) { this.skipNl(); l = { k: 'or', l, r: this.and(noGt) }; }
    return l;
  }

  private and(noGt: boolean): Node {
    let l = this.in(noGt);
    while (this.accept('&&')) { this.skipNl(); l = { k: 'and', l, r: this.in(noGt) }; }
    return l;
  }

  private in(noGt: boolean): Node {
    let l = this.match(noGt);
    while (this.isKw('in')) {
      this.p++;
      if (this.peek.k !== 'name') throw awkErr("syntax error: expected array name after 'in'");
      l = { k: 'in', keys: [l], r: this.ref(this.peek.t) };
      this.p++;
    }
    return l;
  }

  private match(noGt: boolean): Node {
    let l = this.comparison(noGt);
    while (this.isOp('~') || this.isOp('!~')) {
      const neg = this.peek.t === '!~';
      this.p++;
      l = { k: 'match', s: l, re: this.comparison(noGt), neg };
    }
    return l;
  }

  private comparison(noGt: boolean): Node {
    const l = this.concat(noGt);
    if (this.isOp('|') && this.at(1).k === 'kw' && this.at(1).t === 'getline') throw awkErr('"command" | getline is not supported');
    if (this.peek.k === 'op' && (['<', '<=', '==', '!=', '>='].includes(this.peek.t) || (this.peek.t === '>' && !noGt))) {
      const op = this.peek.t;
      this.p++;
      return { k: 'bin', op, l, r: this.concat(noGt) };
    }
    return l;
  }

  private startsConcat() {
    const k = this.peek;
    return k.k === 'num' || k.k === 'str' || k.k === 'name' || k.k === 'func' || k.k === 'call' || k.k === 're' || this.isOp('$') || this.isOp('(');
  }

  private getlineTarget(): Node | undefined {
    if (this.peek.k !== 'name' && !this.isOp('$')) return undefined;
    const t = this.primary();
    if (!this.isLvalue(t)) throw awkErr('syntax error: getline: expected a variable');
    return t;
  }

  private concat(noGt: boolean): Node {
    let l = this.additive();
    while (this.startsConcat()) l = { k: 'cat', l, r: this.additive() };
    return l;
  }

  private additive(): Node {
    let l = this.multiplicative();
    while (this.isOp('+') || this.isOp('-')) { const op = this.peek.t; this.p++; l = { k: 'bin', op, l, r: this.multiplicative() }; }
    return l;
  }

  private multiplicative(): Node {
    let l = this.unary();
    while (this.isOp('*') || this.isOp('/') || this.isOp('%')) { const op = this.peek.t; this.p++; l = { k: 'bin', op, l, r: this.unary() }; }
    return l;
  }

  private unary(): Node {
    if (this.isOp('!') || this.isOp('-') || this.isOp('+')) { const op = this.peek.t; this.p++; return { k: 'un', op, e: this.unary() }; }
    return this.power();
  }

  private power(): Node {
    const b = this.postfix();
    if (this.isOp('^') || this.isOp('**')) { this.p++; return { k: 'bin', op: '^', l: b, r: this.unary() }; }
    return b;
  }

  private postfix(): Node {
    const e = this.prefix();
    if (this.isLvalue(e) && (this.isOp('++') || this.isOp('--'))) { const inc = this.peek.t === '++'; this.p++; return { k: 'incr', t: e, inc, pre: false }; }
    return e;
  }

  private prefix(): Node {
    if (this.isOp('++') || this.isOp('--')) {
      const inc = this.peek.t === '++';
      this.p++;
      const t = this.prefix();
      if (!this.isLvalue(t)) throw awkErr('syntax error: ++ or -- needs a variable');
      return { k: 'incr', t, inc, pre: true };
    }
    return this.primary();
  }

  private primary(): Node {
    const t = this.peek;
    switch (t.k) {
      case 'num': this.p++; return { k: 'num', v: t.n! };
      case 'str': this.p++; return { k: 'str', v: t.t };
      case 're': this.p++; return { k: 're', src: t.t };
      case 'op':
        if (t.t === '$') { this.p++; return { k: 'field', e: this.isOp('++') || this.isOp('--') ? this.prefix() : this.primary() }; }
        if (t.t === '(') {
          this.p++;
          const list = this.exprList(false);
          this.expect(')');
          if (list.length === 1) return list[0];
          if (!this.isKw('in')) throw awkErr("syntax error: expected 'in' after (a, b)");
          this.p++;
          if (this.peek.k !== 'name') throw awkErr('syntax error: expected array name');
          const r = this.ref(this.peek.t);
          this.p++;
          return { k: 'in', keys: list, r };
        }
        if (t.t === '-' || t.t === '+' || t.t === '!') return this.unary();
        break;
      case 'func': {
        this.p++;
        if (!this.accept('(')) {
          if (t.t === 'length') return { k: 'call', name: 'length', args: [] };
          throw awkErr(`syntax error: expected '(' after ${t.t}`);
        }
        const args = this.isOp(')') ? [] : this.exprList(false);
        this.expect(')');
        return { k: 'call', name: t.t, args };
      }
      case 'name': {
        this.p++;
        if (this.accept('[')) { const keys = this.exprList(false); this.expect(']'); return { k: 'idx', r: this.ref(t.t), keys }; }
        return { k: 'var', r: this.ref(t.t) };
      }
      case 'kw':
        if (t.t === 'getline') {
          this.p++;
          const target = this.getlineTarget();
          const file = this.accept('<') ? this.primary() : undefined;
          return { k: 'getline', target, file };
        }
        break;
      case 'call': {
        this.p++;
        this.expect('(');
        const args = this.isOp(')') ? [] : this.exprList(false);
        this.expect(')');
        return { k: 'ucall', name: t.t, args };
      }
    }
    throw awkErr(t.k === 'eof' ? 'syntax error: unexpected end of program' : `syntax error: unexpected '${t.t}'`);
  }
}

// ---------- input ----------

/** Reads records from a text according to the current RS: a newline, one character, a regex, or "" (paragraphs). */
class Reader {
  private pos = 0;
  constructor(private text: string, private regex: (rs: string) => RegExp) {}
  skip() { this.pos = this.text.length; }
  next(rs: string): string | null {
    const t = this.text;
    if (rs === '') {
      while (this.pos < t.length && t[this.pos] === '\n') this.pos++;
      if (this.pos >= t.length) return null;
      const m = /\n\n+/g;
      m.lastIndex = this.pos;
      const x = m.exec(t);
      const rec = x ? t.slice(this.pos, x.index) : t.slice(this.pos).replace(/\n+$/, '');
      this.pos = x ? x.index + x[0].length : t.length;
      return rec;
    }
    if (this.pos >= t.length) return null;
    let end: number, after: number;
    if (rs.length === 1) {
      end = t.indexOf(rs, this.pos);
      after = end + 1;
    } else {
      const re = this.regex(rs);
      re.lastIndex = this.pos;
      let m = re.exec(t);
      while (m && m[0].length === 0) { re.lastIndex = m.index + 1; m = m.index >= t.length ? null : re.exec(t); }
      end = m ? m.index : -1;
      after = m ? m.index + m[0].length : 0;
    }
    if (end < 0) { const rec = t.slice(this.pos); this.pos = t.length; return rec; }
    const rec = t.slice(this.pos, end);
    this.pos = after;
    return rec;
  }
}

// ---------- interpreter ----------

const NORMAL = 0, BREAK = 1, CONTINUE = 2, NEXT = 3, EXIT = 4, RETURN = 5, NEXTFILE = 6;
const ASSIGN_RE = /^([A-Za-z_][A-Za-z0-9_]*)=([\s\S]*)$/;
const LOOP_LIMIT = 10_000_000;
const MAX_FIELDS = 1_000_000;
const MAX_CALL_DEPTH = 400;

class ControlEx { constructor(readonly code: number) {} }

class Interp {
  private globals = new Map<string, Slot>();
  private functions = new Map<string, Func>();
  private regexes = new Map<string, RegExp>();
  private frame: Slot[] = [];
  private depth = 0;
  private record = '';
  private fields: string[] | null = [];
  private nr = 0;
  private fnr = 0;
  private exitCode = 0;
  private iterations = 0;
  private ticks = 0;
  private retVal: AV;
  private seed = 0;
  private prevSeed = 0;
  private rng = mulberry(0);
  private readonly maxString: number;
  // input
  private operands: string[] = [];
  private opIdx = 0;
  private readAny = false;
  private reader: Reader | null = null;
  private getlineFiles = new Map<string, Reader>();
  private outFiles = new Set<string>();

  constructor(private ctx: CommandContext) { this.maxString = ctx.shell.options.maxStringBytes; }

  // ----- driver

  run(): number {
    const args = this.ctx.args;
    const sources: string[] = [];
    const assigns: string[] = [];
    let fs: string | undefined;
    let i = 0;
    for (; i < args.length; i++) {
      const a = args[i];
      if (a === '--') { i++; break; }
      if (a === '--version' || (a === '-W' && args[i + 1] === 'version')) { this.ctx.stdout.write('GNU Awk 5.2.1, API 3.2, PMA Avon 8-g1, (GNU MPFR 4.2.1, GNU MP 6.3.0)\n'); return 0; }
      if (a.length < 2 || a[0] !== '-') break;
      const val = (flag: string) => (a.length > 2 ? a.slice(2) : i + 1 < args.length ? args[++i] : (() => { throw awkErr(`option requires an argument -- ${flag}`); })());
      if (a.startsWith('-F')) fs = val('F');
      else if (a.startsWith('-v')) assigns.push(val('v'));
      else if (a.startsWith('-f')) {
        const src = val('f');
        if (src === '-' || src === '/dev/stdin') sources.push(this.ctx.stdinText);
        else try { sources.push(this.ctx.fs.readText(this.ctx.resolvePath(src))); } catch (e) {
          if (!(e instanceof ShellError)) throw e;
          throw awkErr(`can't open file ${src}: ${reason(e)}`);
        }
      } else throw awkErr(`unknown option ${a}`);
    }
    if (sources.length === 0) {
      if (i >= args.length) throw awkErr('usage: awk [-F fs][-v var=value][prog | -f progfile][file ...]');
      sources.push(args[i++]);
    }
    this.operands = args.slice(i);

    const parser = new Parser(sources.join('\n'));
    const rules = parser.parse();
    this.functions = parser.functions;
    const g = this.globals;
    g.set('FS', fs === undefined || fs === ' ' ? ' ' : fs === 't' ? '\t' : unescapeAwk(fs));
    g.set('OFS', ' '); g.set('ORS', '\n'); g.set('RS', '\n'); g.set('SUBSEP', '\x1c');
    g.set('CONVFMT', '%.6g'); g.set('OFMT', '%.6g');
    g.set('RSTART', 0); g.set('RLENGTH', -1); g.set('FILENAME', '');
    const environ: Arr = new Map();
    for (const [k, v] of this.ctx.shell.environment) environ.set(k, new SN(v));
    g.set('ENVIRON', environ);
    const argv: Arr = new Map([['0', 'awk']]);
    this.operands.forEach((o, k) => argv.set(String(k + 1), new SN(o)));
    g.set('ARGV', argv);
    g.set('ARGC', this.operands.length + 1);
    for (const a of assigns) if (!this.assignOperand(a)) throw awkErr(`invalid -v argument \`${a}', expected 'var=value'`);

    try {
      for (const r of rules) if (r.kind === 'BEGIN') this.runTop(r.action!);
      if (rules.some(r => r.kind !== 'BEGIN')) this.processInput(rules);
    } catch (e) { if (!(e instanceof ControlEx && e.code === EXIT)) throw e; }
    try {
      for (const r of rules) if (r.kind === 'END') this.runTop(r.action!);
    } catch (e) { if (!(e instanceof ControlEx && e.code === EXIT)) throw e; }
    return this.exitCode;
  }

  /** Runs a top-level action: next/nextfile/exit and return codes surface as exceptions or codes. */
  private runTop(s: Stmt): number {
    try { return this.exec(s); } catch (e) {
      if (e instanceof ControlEx) return e.code;
      throw e;
    }
  }

  private assignOperand(a: string): boolean {
    const m = ASSIGN_RE.exec(a);
    if (!m) return false;
    this.setGlobalVar(m[1], new SN(unescapeAwk(m[2])));
    return true;
  }

  private setGlobalVar(name: string, v: AV) {
    if (name === 'NF') { this.setNF(toNum(v)); return; }
    if (name === 'NR') { this.nr = toNum(v); return; }
    if (name === 'FNR') { this.fnr = toNum(v); return; }
    if (this.globals.get(name) instanceof Map) throw awkErr(`can't assign to ${name}; it's an array name.`);
    this.globals.set(name, v);
  }

  /** The next record of the main input, moving through the file operands (and applying var=value operands). */
  private mainNext(): string | null {
    for (;;) {
      if (this.reader) {
        const r = this.reader.next(this.gstr('RS'));
        if (r !== null) return r;
        this.reader = null;
      }
      if (this.opIdx >= this.operands.length) {
        if (this.readAny) return null;
        this.readAny = true;
        this.reader = this.makeReader(this.ctx.stdinText);
        this.globals.set('FILENAME', '');
        this.fnr = 0;
        continue;
      }
      const op = this.operands[this.opIdx++];
      if (!op.startsWith('-') && this.assignOperand(op)) continue;
      this.readAny = true;
      let text: string;
      if (op === '-') text = this.ctx.stdinText;
      else {
        const path = this.ctx.resolvePath(op);
        if (!this.ctx.fs.isFile(path)) {
          this.ctx.stderr.write(`awk: cannot open "${op}" (${this.ctx.fs.isDirectory(path) ? 'Is a directory' : 'No such file or directory'})\n`);
          this.exitCode = 2;
          continue;
        }
        try { text = this.ctx.fs.readPipeText(path); } catch (e) {
          if (!(e instanceof ShellError)) throw e;
          this.ctx.stderr.write(`awk: cannot open "${op}" (${reason(e)})\n`);
          this.exitCode = 2;
          continue;
        }
      }
      this.globals.set('FILENAME', op === '-' ? '' : op);
      this.fnr = 0;
      this.reader = this.makeReader(text);
    }
  }

  private makeReader(text: string) { return new Reader(text, rs => this.regex(rs)); }

  private processInput(rules: Rule[]) {
    const main = rules.filter(r => r.kind === 'main');
    for (let rec = this.mainNext(); rec !== null; rec = this.mainNext()) {
      if ((++this.ticks & 255) === 0) this.ctx.checkTime();
      this.nr++; this.fnr++;
      this.setRecord(rec);
      for (const r of main) {
        if (!this.ruleMatches(r)) continue;
        let code = NORMAL;
        if (r.action) code = this.runTop(r.action);
        else this.print({ k: 'print', printf: false, args: [] });
        if (code === NEXT) break;
        if (code === NEXTFILE) { this.reader?.skip(); break; }
        if (code === EXIT) throw new ControlEx(EXIT);
      }
    }
  }

  private ruleMatches(r: Rule): boolean {
    if (!r.pattern) return true;
    if (!r.pattern2) return truthy(this.eval(r.pattern));
    if (r.inRange) { if (truthy(this.eval(r.pattern2))) r.inRange = false; return true; }
    if (!truthy(this.eval(r.pattern))) return false;
    r.inRange = !truthy(this.eval(r.pattern2));
    return true;
  }

  // ----- conversions

  private gstr(name: string): string { const v = this.globals.get(name); return typeof v === 'string' ? v : this.str(v as AV); }

  /** String conversion with CONVFMT. */
  private str(v: AV): string {
    if (typeof v === 'string') return v;
    if (typeof v === 'number') return numToStr(v, this.gstr('CONVFMT') || '%.6g');
    return v === undefined ? '' : v.s;
  }

  private outStr(v: AV): string { return typeof v === 'number' ? numToStr(v, this.gstr('OFMT') || '%.6g') : this.str(v); }

  private key(keys: Node[]): string { return keys.length === 1 ? this.str(this.eval(keys[0])) : keys.map(k => this.str(this.eval(k))).join(this.gstr('SUBSEP')); }

  private tooLong(): never { throw tooLarge('awk: string', this.maxString, 'build smaller strings or print as you go'); }

  // ----- regexes

  private regex(src: string): RegExp {
    let re = this.regexes.get(src);
    if (re) return re;
    try { re = new RegExp(posixToJs(src, true, true), 'gs'); } catch (e) {
      throw awkErr(`invalid regular expression /${src}/: ${(e as Error).message.replace(/^Invalid regular expression: /, '')}`);
    }
    if (this.regexes.size > 500) this.regexes.clear();
    this.regexes.set(src, re);
    return re;
  }

  private reOf(n: Node): RegExp { return this.regex(n.k === 're' ? n.src : this.str(this.eval(n))); }
  private test(re: RegExp, s: string): boolean { re.lastIndex = 0; return re.test(s); }

  // ----- records and fields

  private setRecord(line: string) { this.record = line; this.fields = null; }

  private ensureFields(): string[] {
    if (this.fields) return this.fields;
    let fs = this.gstr('FS');
    const para = this.gstr('RS') === '';
    if (para && fs !== ' ') fs = fs.length === 0 ? '\n' : fs.length > 1 ? `(?:${fs})|\n` : /[\\^$.|?*+()[\]{}]/.test(fs) ? `\\${fs}|\n` : `${fs}|\n`;
    return this.fields = this.splitFields(this.record, fs);
  }

  private splitFields(s: string, fs: string): string[] {
    if (s.length === 0) return [];
    if (fs === ' ') { const t = s.replace(/^[ \t\n]+|[ \t\n]+$/g, ''); return t ? t.split(/[ \t\n]+/) : []; }
    if (fs.length === 1 && fs !== '\\') return s.split(fs);
    if (fs.length === 0) return [...s];
    return this.splitRe(s, this.regex(fs));
  }

  private splitRe(s: string, re: RegExp): string[] {
    const out: string[] = [];
    let pos = 0;
    re.lastIndex = 0;
    for (let m = re.exec(s); m; m = re.exec(s)) {
      if (m[0].length === 0) { re.lastIndex = m.index + 1; if (m.index >= s.length) break; continue; }
      out.push(s.slice(pos, m.index));
      pos = m.index + m[0].length;
    }
    out.push(s.slice(pos));
    return out;
  }

  private rebuild() { this.record = this.fields!.join(this.gstr('OFS')); }

  private getField(i: number): AV {
    if (i < 0) throw awkErr(`trying to access out of range field ${i}`);
    if (i === 0) return new SN(this.record);
    const f = this.ensureFields();
    return i <= f.length ? new SN(f[i - 1]) : undefined;
  }

  private setField(i: number, v: string) {
    if (i < 0) throw awkErr(`trying to access out of range field ${i}`);
    if (i === 0) { this.setRecord(v); return; }
    if (i > MAX_FIELDS) throw awkErr(`field index ${i} too large`);
    const f = this.ensureFields();
    while (f.length < i) f.push('');
    f[i - 1] = v;
    this.rebuild();
  }

  private setNF(n: number) {
    n = Math.max(0, Math.trunc(n));
    if (n > MAX_FIELDS) throw awkErr(`NF ${n} too large`);
    const f = this.ensureFields();
    while (f.length < n) f.push('');
    f.length = n;
    this.rebuild();
  }

  // ----- variables

  private readSlot(r: Ref): Slot { return r.slot >= 0 ? this.frame[r.slot] : this.globals.get(r.name); }

  private getVar(r: Ref): AV {
    if (r.sp) return r.name === 'NF' ? this.ensureFields().length : r.name === 'NR' ? this.nr : this.fnr;
    const v = this.readSlot(r);
    if (v instanceof Map) throw awkErr(`attempt to use array ${r.name} in a scalar context`);
    return v;
  }

  private setVar(r: Ref, v: AV) {
    if (r.slot >= 0) {
      if (this.frame[r.slot] instanceof Map) throw awkErr(`can't assign to ${r.name}; it's an array name.`);
      this.frame[r.slot] = v;
    } else this.setGlobalVar(r.name, v);
  }

  private arr(r: Ref): Arr {
    const v = this.readSlot(r);
    if (v instanceof Map) return v;
    if (v !== undefined) throw awkErr(`can't use scalar ${r.name} as array`);
    const a: Arr = new Map();
    if (r.slot >= 0) this.frame[r.slot] = a; else this.globals.set(r.name, a);
    return a;
  }

  private get(n: Node): AV {
    switch (n.k) {
      case 'var': return this.getVar(n.r);
      case 'field': return this.getField(Math.trunc(toNum(this.eval(n.e))));
      case 'idx': { const a = this.arr(n.r); const k = this.key(n.keys); let v = a.get(k); if (v === undefined && !a.has(k)) a.set(k, v); return v; }
      default: throw awkErr('invalid assignment target');
    }
  }

  private set(n: Node, v: AV) {
    switch (n.k) {
      case 'var': this.setVar(n.r, v); break;
      case 'field': this.setField(Math.trunc(toNum(this.eval(n.e))), this.str(v)); break;
      case 'idx': this.arr(n.r).set(this.key(n.keys), v); break;
      default: throw awkErr('invalid assignment target');
    }
  }

  // ----- statements

  private loopTick() {
    if (++this.iterations > LOOP_LIMIT) throw awkErr('loop iteration limit exceeded');
    if ((this.iterations & 1023) === 0) this.ctx.checkTime();
  }

  private exec(s: Stmt): number {
    switch (s.k) {
      case 'block':
        for (const x of s.body) { const c = this.exec(x); if (c) return c; }
        return NORMAL;
      case 'expr': this.eval(s.e); return NORMAL;
      case 'print': this.print(s); return NORMAL;
      case 'if':
        if (truthy(this.eval(s.c))) return this.exec(s.then);
        return s.else ? this.exec(s.else) : NORMAL;
      case 'while': {
        if (s.dw) {
          do {
            this.loopTick();
            const c = this.exec(s.body);
            if (c === BREAK) break;
            if (c && c !== CONTINUE) return c;
          } while (truthy(this.eval(s.c)));
        } else {
          while (truthy(this.eval(s.c))) {
            this.loopTick();
            const c = this.exec(s.body);
            if (c === BREAK) break;
            if (c && c !== CONTINUE) return c;
          }
        }
        return NORMAL;
      }
      case 'for': {
        if (s.init) this.eval(s.init);
        while (!s.c || truthy(this.eval(s.c))) {
          this.loopTick();
          const c = this.exec(s.body);
          if (c === BREAK) break;
          if (c && c !== CONTINUE) return c;
          if (s.step) this.eval(s.step);
        }
        return NORMAL;
      }
      case 'forin': {
        for (const k of [...this.arr(s.r).keys()]) {
          this.loopTick();
          this.set(s.v, new SN(k));
          const c = this.exec(s.body);
          if (c === BREAK) break;
          if (c && c !== CONTINUE) return c;
        }
        return NORMAL;
      }
      case 'simple':
        switch (s.kind) {
          case 'next': return NEXT;
          case 'nextfile': return NEXTFILE;
          case 'break': return BREAK;
          case 'continue': return CONTINUE;
          default:
            if (s.arg) this.exitCode = Math.trunc(toNum(this.eval(s.arg)));
            return EXIT;
        }
      case 'return':
        if (this.depth === 0) throw awkErr('return outside function');
        this.retVal = s.v ? this.eval(s.v) : undefined;
        return RETURN;
      case 'delete':
        if (!s.keys) this.arr(s.r).clear(); else this.arr(s.r).delete(this.key(s.keys));
        return NORMAL;
    }
  }

  private print(p: Extract<Stmt, { k: 'print' }>) {
    let text: string;
    if (p.printf) {
      if (p.args.length === 0) throw awkErr('printf: no format');
      const fmt = this.str(this.eval(p.args[0]));
      text = formatSpec(fmt, p.args.slice(1).map(a => this.eval(a)), v => this.str(v), this.maxString);
    } else {
      text = (p.args.length === 0 ? this.record : p.args.map(a => this.outStr(this.eval(a))).join(this.gstr('OFS'))) + this.gstr('ORS');
    }
    if (!p.dest) { this.ctx.stdout.write(text); return; }
    const dest = this.str(this.eval(p.dest));
    if (dest === '/dev/stdout' || dest === '-') { this.ctx.stdout.write(text); return; }
    if (dest === '/dev/stderr') { this.ctx.stderr.write(text); return; }
    const path = this.ctx.resolvePath(dest);
    if (!this.outFiles.has(path)) {
      if (p.redir === '>') this.ctx.fs.writeText(path, '');
      this.outFiles.add(path);
    }
    this.ctx.fs.appendText(path, text);
  }

  // ----- expressions

  private compare(a: AV, b: AV): number {
    if (isNumeric(a) && isNumeric(b)) { const x = toNum(a), y = toNum(b); return x < y ? -1 : x > y ? 1 : x === y ? 0 : NaN; }
    const x = this.str(a), y = this.str(b);
    return x < y ? -1 : x > y ? 1 : 0;
  }

  private arith(op: string, l: number, r: number): number {
    switch (op) {
      case '+': return l + r;
      case '-': return l - r;
      case '*': return l * r;
      case '/': if (r === 0) throw awkErr('division by zero'); return l / r;
      case '%': if (r === 0) throw awkErr('division by zero in %'); return l % r;
      case '^': return l ** r;
      default: throw awkErr(`unknown operator ${op}`);
    }
  }

  private eval(n: Node): AV {
    switch (n.k) {
      case 'num': return n.v;
      case 'str': return n.v;
      case 're': return this.test(this.regex(n.src), this.record) ? 1 : 0;
      case 'var': case 'field': case 'idx': return this.get(n);
      case 'assign': {
        let v = this.eval(n.v);
        if (n.op !== '=') v = this.arith(n.op === '**=' ? '^' : n.op[0], toNum(this.get(n.t)), toNum(v));
        this.set(n.t, v);
        return v;
      }
      case 'cond': return truthy(this.eval(n.c)) ? this.eval(n.a) : this.eval(n.b);
      case 'and': return truthy(this.eval(n.l)) && truthy(this.eval(n.r)) ? 1 : 0;
      case 'or': return truthy(this.eval(n.l)) || truthy(this.eval(n.r)) ? 1 : 0;
      case 'bin': {
        const l = this.eval(n.l), r = this.eval(n.r);
        switch (n.op) {
          case '<': return this.compare(l, r) < 0 ? 1 : 0;
          case '<=': return this.compare(l, r) <= 0 ? 1 : 0;
          case '>': return this.compare(l, r) > 0 ? 1 : 0;
          case '>=': return this.compare(l, r) >= 0 ? 1 : 0;
          case '==': return this.compare(l, r) === 0 ? 1 : 0;
          case '!=': return this.compare(l, r) !== 0 ? 1 : 0;
          default: return this.arith(n.op, toNum(l), toNum(r));
        }
      }
      case 'un': {
        const e = this.eval(n.e);
        return n.op === '!' ? (truthy(e) ? 0 : 1) : n.op === '-' ? -toNum(e) : toNum(e);
      }
      case 'incr': {
        const old = toNum(this.get(n.t));
        const nv = old + (n.inc ? 1 : -1);
        this.set(n.t, nv);
        return n.pre ? nv : old;
      }
      case 'match': return this.test(this.reOf(n.re), this.str(this.eval(n.s))) !== n.neg ? 1 : 0;
      case 'in': return this.arr(n.r).has(this.key(n.keys)) ? 1 : 0;
      case 'cat': {
        const l = this.str(this.eval(n.l)), r = this.str(this.eval(n.r));
        if (l.length + r.length > this.maxString) this.tooLong();
        return l + r;
      }
      case 'call': return this.call(n);
      case 'ucall': return this.userCall(n);
      case 'getline': return this.getline(n);
    }
  }

  private call(c: Extract<Node, { k: 'call' }>): AV {
    const a = c.args;
    const S = (i: number) => (i < a.length ? this.str(this.eval(a[i])) : '');
    const N = (i: number) => (i < a.length ? toNum(this.eval(a[i])) : 0);
    switch (c.name) {
      case 'length': {
        if (a.length === 0) return cpLength(this.record);
        const x = a[0];
        if (x.k === 'var' && !x.r.sp && this.readSlot(x.r) instanceof Map) return (this.readSlot(x.r) as Arr).size;
        return cpLength(S(0));
      }
      case 'substr': {
        const s = S(0);
        const m = roundEven(N(1));
        const end = a.length > 2 ? m + roundEven(N(2)) : Infinity;
        const start = Math.max(m, 1);
        const stop = Math.min(end, s.length + 1);
        return Number.isNaN(start) || Number.isNaN(stop) || stop <= start ? '' : s.slice(start - 1, stop - 1);
      }
      case 'index': return S(0).indexOf(S(1)) + 1;
      case 'split': {
        if (a.length < 2 || a[1].k !== 'var') throw awkErr('split: second argument must be an array name');
        const s = S(0);
        let parts: string[];
        if (a.length > 2) parts = a[2].k === 're' ? (s ? this.splitRe(s, this.regex(a[2].src)) : []) : this.splitFields(s, S(2));
        else parts = this.splitFields(s, this.gstr('FS'));
        const dest = this.arr(a[1].r);
        dest.clear();
        parts.forEach((p, i) => dest.set(String(i + 1), new SN(p)));
        return parts.length;
      }
      case 'sub': case 'gsub': return this.subst(c);
      case 'match': {
        const re = this.reOf(a[1]);
        re.lastIndex = 0;
        const m = re.exec(S(0));
        this.globals.set('RSTART', m ? m.index + 1 : 0);
        this.globals.set('RLENGTH', m ? m[0].length : -1);
        return m ? m.index + 1 : 0;
      }
      case 'sprintf': return formatSpec(S(0), a.slice(1).map(x => this.eval(x)), v => this.str(v), this.maxString);
      case 'tolower': return S(0).toLowerCase();
      case 'toupper': return S(0).toUpperCase();
      case 'int': return Math.trunc(N(0));
      case 'sqrt': return Math.sqrt(N(0));
      case 'exp': return Math.exp(N(0));
      case 'log': return Math.log(N(0));
      case 'sin': return Math.sin(N(0));
      case 'cos': return Math.cos(N(0));
      case 'atan2': return Math.atan2(N(0), N(1));
      case 'rand': return this.rng();
      case 'srand': {
        this.prevSeed = this.seed;
        this.seed = a.length ? N(0) : Math.floor(Date.now() / 1000);
        this.rng = mulberry(this.seed);
        return this.prevSeed;
      }
      case 'fflush': return 0;
      case 'close': {
        const path = this.ctx.resolvePath(S(0));
        const had = this.outFiles.delete(path) || this.getlineFiles.delete(path);
        return had ? 0 : -1;
      }
      case 'system': throw awkErr('system() is not supported');
    }
    throw awkErr(`calling undefined function ${c.name}`);
  }

  private subst(c: Extract<Node, { k: 'call' }>): AV {
    const a = c.args;
    if (a.length < 2) throw awkErr(`${c.name}: requires at least 2 arguments`);
    const global = c.name === 'gsub';
    const re = this.reOf(a[0]);
    const repl = this.str(this.eval(a[1]));
    const target: Node = a.length > 2 ? a[2] : { k: 'field', e: { k: 'num', v: 0 } };
    if (!(target.k === 'var' || target.k === 'field' || target.k === 'idx')) { this.eval(target); return 0; }
    const src = this.str(this.get(target));
    let out = '', pos = 0, count = 0, lastEnd = -1, from = 0;
    while (from <= src.length) {
      re.lastIndex = from;
      const m = re.exec(src);
      if (!m) break;
      const len = m[0].length;
      if (len === 0 && m.index === lastEnd) { // no empty match right after a match
        if (m.index >= src.length) break;
        from = m.index + 1;
        continue;
      }
      count++;
      out += src.slice(pos, m.index);
      for (let i = 0; i < repl.length; i++) {
        const ch = repl[i];
        if (ch === '\\' && (repl[i + 1] === '&' || repl[i + 1] === '\\')) out += repl[++i];
        else if (ch === '&') out += m[0];
        else out += ch;
      }
      if (out.length > this.maxString) this.tooLong();
      pos = m.index + len;
      lastEnd = pos;
      if (!global) break;
      if (len === 0) { if (m.index >= src.length) break; from = m.index + 1; } else from = pos;
    }
    if (count > 0) {
      out += src.slice(pos);
      if (out.length > this.maxString) this.tooLong();
      this.set(target, out);
    }
    return count;
  }

  private userCall(c: Extract<Node, { k: 'ucall' }>): AV {
    const fn = this.functions.get(c.name);
    if (!fn) throw awkErr(`function \`${c.name}' not defined`);
    if (c.args.length > fn.params.length) throw awkErr(`function \`${c.name}' called with more arguments than declared`);
    if (this.depth >= MAX_CALL_DEPTH) throw awkErr(`function \`${c.name}': call nesting too deep`);
    if ((++this.ticks & 1023) === 0) this.ctx.checkTime();
    const frame: Slot[] = new Array(fn.params.length).fill(undefined);
    const pending: [number, Ref][] = [];
    c.args.forEach((arg, i) => {
      if (arg.k === 'var' && !arg.r.sp) {
        const v = this.readSlot(arg.r);
        frame[i] = v;
        if (v === undefined) pending.push([i, arg.r]); // may become an array in the callee
      } else frame[i] = this.eval(arg);
    });
    const saved = this.frame;
    this.frame = frame;
    this.depth++;
    let code: number;
    try { code = this.exec(fn.body); } finally { this.depth--; this.frame = saved; }
    if (code === NEXT || code === EXIT || code === NEXTFILE) throw new ControlEx(code);
    for (const [i, r] of pending) if (frame[i] instanceof Map && this.readSlot(r) === undefined) { if (r.slot >= 0) this.frame[r.slot] = frame[i]; else this.globals.set(r.name, frame[i]); }
    if (code === RETURN) { const v = this.retVal; this.retVal = undefined; return v; }
    return undefined;
  }

  private getline(g: Extract<Node, { k: 'getline' }>): AV {
    let rec: string | null;
    if (g.file) {
      const name = this.str(this.eval(g.file));
      const key = name === '-' || name === '/dev/stdin' ? '-' : this.ctx.resolvePath(name);
      let rd = this.getlineFiles.get(key);
      if (!rd) {
        let text: string;
        if (key === '-') text = this.ctx.stdinText;
        else try { text = this.ctx.fs.readPipeText(key); } catch (e) { if (e instanceof ShellError) return -1; throw e; }
        this.getlineFiles.set(key, rd = this.makeReader(text));
      }
      rec = rd.next(this.gstr('RS'));
    } else {
      rec = this.mainNext();
      if (rec !== null) { this.nr++; this.fnr++; }
    }
    if (rec === null) return 0;
    if (g.target) this.set(g.target, new SN(rec)); else this.setRecord(rec);
    return 1;
  }
}

function roundEven(x: number): number {
  const r = Math.round(x);
  return Math.abs(x % 1) === 0.5 && r % 2 !== 0 ? r - 1 : r;
}

function cpLength(s: string): number {
  if (!/[\ud800-\udbff]/.test(s)) return s.length;
  let n = 0;
  for (const _ of s) n++;
  return n;
}

function mulberry(seed: number): () => number {
  let a = (seed | 0) >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export function registerAwk(shell: Shell): void {
  shell.register('awk', ctx => {
    try { return new Interp(ctx).run(); } catch (e) {
      if (e instanceof RangeError) {
        if (/call stack/i.test(e.message)) throw awkErr('expression or function nesting too deep');
        if (/string length/i.test(e.message)) throw tooLarge('awk: string', ctx.shell.options.maxStringBytes, 'build smaller strings or print as you go');
      }
      throw e;
    }
  }, 'Pattern scanning and processing language');
}
