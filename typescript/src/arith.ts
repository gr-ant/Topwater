// Bash integer arithmetic ($(( )), (( )), let, for (( ; ; ))): 64-bit signed, wrapping like bash, as BigInt.
import { ShellError } from './vfs.js';

/** What arithmetic needs from the shell: reading and assigning variables and array elements. */
export interface ArithHost {
  /** A variable's text ("" when unset). */
  getVariable(name: string): string;
  /** An element's text, or null when unset. */
  getElement(name: string, subscript: string): string | null;
  /** Assigns name or name[sub]. */
  setVar(name: string, value: string): void;
}

const OPS = ['**=', '<<=', '>>=', '&&', '||', '==', '!=', '<=', '>=', '<<', '>>', '**', '++', '--', '+=', '-=', '*=', '/=', '%=',
  '&=', '|=', '^=', '+', '-', '*', '/', '%', '<', '>', '=', '!', '~', '&', '|', '^', '?', ':', '(', ')', ','];
const ASSIGN_OPS = new Set(['=', '+=', '-=', '*=', '/=', '%=', '<<=', '>>=', '&=', '|=', '^=', '**=']);
const LEVELS = [['||'], ['&&'], ['|'], ['^'], ['&'], ['==', '!='], ['<', '<=', '>', '>='], ['<<', '>>'], ['+', '-'], ['*', '/', '%']];
const wrap = (v: bigint) => BigInt.asIntN(64, v);
const isIdent = (s: string) => /^[A-Za-z_]/.test(s);
const DOLLAR = /\$\{?([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[#?$])\}?/g;
const MAX_NESTING = 1000;

export function evalArith(host: ArithHost, expr: string, depth = 0): bigint {
  if (depth > 20) throw new ShellError(`${expr}: expression recursion level exceeded`);
  if (expr.includes('$')) expr = expr.replace(DOLLAR, (_, name: string) => host.getVariable(name));
  const tokens = tokenize(expr);
  if (tokens.length === 0) return 0n;
  const a = new Arith(host, expr, tokens, depth);
  const v = a.comma(true);
  if (a.p < tokens.length) throw a.syntax(tokens[a.p]);
  return v;
}

function tokenize(s: string): string[] {
  const tokens: string[] = [];
  for (let i = 0; i < s.length;) {
    if (/\s/.test(s[i])) { i++; continue; }
    if (/[A-Za-z0-9_]/.test(s[i])) {
      let j = i;
      while (j < s.length && /[A-Za-z0-9_#@]/.test(s[j])) {
        if (s[j] === '@' && !/^\d+#/.test(s.slice(i, j))) break;
        j++;
      }
      // name[subscript] is one operand, so an element can be read, assigned and incremented anywhere
      if (j < s.length && s[j] === '[' && /[A-Za-z_]/.test(s[i])) {
        let d = 0;
        for (; j < s.length; j++) {
          if (s[j] === '[') d++;
          else if (s[j] === ']' && --d === 0) { j++; break; }
        }
        if (d > 0) throw new ShellError(`${s}: syntax error: bad array subscript`);
      }
      tokens.push(s.slice(i, j));
      i = j;
      continue;
    }
    let op = OPS.find(o => s.startsWith(o, i));
    if (!op) throw new ShellError(`${s}: syntax error: invalid arithmetic operator (error token is "${s.slice(i)}")`);
    // `5--3` (after $a-$b is substituted) is two minus signs; `--` is one operator only next to a name
    const nextIsName = i + 2 < s.length && /[A-Za-z_]/.test(s[i + 2]);
    if ((op === '++' || op === '--') && !nextIsName && !(tokens.length > 0 && isIdent(tokens[tokens.length - 1]))) op = op[0];
    tokens.push(op);
    i += op.length;
  }
  return tokens;
}

class Arith {
  p = 0;
  private nesting = 0;
  constructor(private host: ArithHost, private src: string, private t: string[], private depth: number) {}

  syntax(token: string) { return new ShellError(`${this.src.trim()}: syntax error in expression (error token is "${token}")`); }
  private peek(): string | undefined { return this.t[this.p]; }
  private accept(s: string) { if (this.t[this.p] !== s) return false; this.p++; return true; }

  /** A variable or name[sub] element as a number: empty or unset is 0, other text is evaluated as an expression. */
  private variable(name: string): bigint {
    const open = name.indexOf('[');
    const v = (open < 0 ? this.host.getVariable(name) : this.host.getElement(name.slice(0, open), name.slice(open + 1, -1)) ?? '').trim();
    if (v.length === 0) return 0n;
    // a value like 010 or 0x1f is a literal in its base, as if it were written in the expression
    if (/^[-+]?\d+$/.test(v) && !/^[-+]?0./.test(v)) return wrap(BigInt(v));
    return evalArith(this.host, v, this.depth + 1);
  }
  private set(name: string, v: bigint) { this.host.setVar(name, v.toString()); }

  comma(ev: boolean): bigint {
    let v = this.assign(ev);
    while (this.accept(',')) v = this.assign(ev);
    return v;
  }

  private assign(ev: boolean): bigint {
    if (this.p + 1 < this.t.length && isIdent(this.t[this.p]) && ASSIGN_OPS.has(this.t[this.p + 1])) {
      const name = this.t[this.p], op = this.t[this.p + 1];
      this.p += 2;
      const r = this.assign(ev);
      if (!ev) return r;
      const v = op === '=' ? r : this.apply(op.slice(0, -1), this.variable(name), r, true);
      this.set(name, v);
      return v;
    }
    return this.ternary(ev);
  }

  private ternary(ev: boolean): bigint {
    const c = this.binary(0, ev);
    if (!this.accept('?')) return c;
    const a = this.assign(ev && c !== 0n);
    if (!this.accept(':')) throw this.syntax(this.peek() ?? 'end of expression');
    const b = this.assign(ev && c === 0n);
    return c !== 0n ? a : b;
  }

  private binary(level: number, ev: boolean): bigint {
    if (level === LEVELS.length) return this.power(ev);
    let l = this.binary(level + 1, ev);
    for (let op = this.peek(); op !== undefined && LEVELS[level].includes(op); op = this.peek()) {
      this.p++;
      if (op === '&&') { const r = this.binary(level + 1, ev && l !== 0n); l = l !== 0n && r !== 0n ? 1n : 0n; }
      else if (op === '||') { const r = this.binary(level + 1, ev && l === 0n); l = l !== 0n || r !== 0n ? 1n : 0n; }
      else l = this.apply(op, l, this.binary(level + 1, ev), ev);
    }
    return l;
  }

  private apply(op: string, l: bigint, r: bigint, ev: boolean): bigint {
    if ((op === '/' || op === '%') && r === 0n) {
      if (ev) throw new ShellError(`${this.src.trim()}: division by 0`);
      return 0n;
    }
    const b = (x: boolean) => (x ? 1n : 0n);
    switch (op) {
      case '+': return wrap(l + r);
      case '-': return wrap(l - r);
      case '*': return wrap(l * r);
      case '/': return wrap(l / r);
      case '%': return wrap(l % r);
      case '**': return this.pow(l, r);
      case '<<': return wrap(l << (r & 63n));
      case '>>': return l >> (r & 63n);
      case '&': return l & r;
      case '|': return l | r;
      case '^': return l ^ r;
      case '<': return b(l < r);
      case '<=': return b(l <= r);
      case '>': return b(l > r);
      case '>=': return b(l >= r);
      case '==': return b(l === r);
      case '!=': return b(l !== r);
    }
    throw this.syntax(op);
  }

  private pow(base: bigint, e: bigint): bigint {
    if (e < 0n) throw new ShellError(`${this.src.trim()}: exponent less than 0 (error token is "${e}")`);
    let r = 1n;
    for (; e > 0n; e >>= 1n, base = wrap(base * base)) if (e & 1n) r = wrap(r * base);
    return r;
  }

  private unary(ev: boolean): bigint {
    if (++this.nesting > MAX_NESTING) throw new ShellError('expression is nested too deeply');
    try {
      if (this.accept('+')) return this.unary(ev);
      if (this.accept('-')) return wrap(-this.unary(ev));
      if (this.accept('!')) return this.unary(ev) === 0n ? 1n : 0n;
      if (this.accept('~')) return ~this.unary(ev);
      const pk = this.peek();
      if (pk === '++' || pk === '--') {
        this.p++;
        const name = this.peek();
        if (name === undefined || !isIdent(name)) throw this.syntax(name ?? pk);
        this.p++;
        if (!ev) return 0n;
        const v = wrap(this.variable(name) + (pk === '++' ? 1n : -1n));
        this.set(name, v);
        return v;
      }
      return this.postfix(ev);
    } finally { this.nesting--; }
  }

  /** `**` binds looser than unary minus in bash: -2**2 is 4. Right-associative. */
  private power(ev: boolean): bigint {
    const b = this.unary(ev);
    return this.accept('**') ? this.pow(b, this.power(ev)) : b;
  }

  private postfix(ev: boolean): bigint {
    if (this.accept('(')) {
      const v = this.comma(ev);
      if (!this.accept(')')) throw this.syntax(this.peek() ?? 'end of expression');
      return v;
    }
    const tok = this.peek();
    if (tok === undefined) throw this.syntax('end of expression (operand expected)');
    this.p++;
    if (/^\d/.test(tok)) {
      if (/^0x/i.test(tok)) return parseLiteral(tok.length > 2 ? tok.slice(2) : '0', 16, tok);
      if (tok.includes('#')) {
        const [radix, digits] = [tok.slice(0, tok.indexOf('#')), tok.slice(tok.indexOf('#') + 1)];
        return parseLiteral(digits, /^\d+$/.test(radix) ? Number(radix) : 0, tok);
      }
      if (tok.length > 1 && tok[0] === '0') return parseLiteral(tok, 8, tok);
      return parseLiteral(tok, 10, tok);
    }
    if (!isIdent(tok)) throw this.syntax(tok);
    const pk = this.peek();
    if (pk === '++' || pk === '--') {
      this.p++;
      const old = this.variable(tok);
      if (ev) this.set(tok, wrap(old + (pk === '++' ? 1n : -1n)));
      return old;
    }
    return this.variable(tok);
  }
}

/** A literal in the given base; values past 64 bits wrap around like bash. */
function parseLiteral(digits: string, radix: number, tok: string): bigint {
  if (digits.length === 0 || radix < 2 || radix > 64) throw new ShellError(`${tok}: invalid arithmetic base (error token is "${tok}")`);
  let v = 0n;
  const R = BigInt(radix);
  for (const c of digits) {
    let d: number;
    if (c >= '0' && c <= '9') d = c.charCodeAt(0) - 48;
    else if (c >= 'a' && c <= 'z') d = c.charCodeAt(0) - 97 + 10;
    else if (c >= 'A' && c <= 'Z') d = radix <= 36 ? c.charCodeAt(0) - 65 + 10 : c.charCodeAt(0) - 65 + 36;
    else if (c === '@') d = 62;
    else if (c === '_') d = 63;
    else d = 99;
    if (d >= radix) throw new ShellError(`${tok}: value too great for base (error token is "${tok}")`);
    v = BigInt.asUintN(64, v * R + BigInt(d));
  }
  return wrap(v);
}
