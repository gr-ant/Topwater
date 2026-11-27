// Shell: options, scalar variables and their attributes, arrays. See docs/architecture.md.
import type { Shell } from '../shell.js';
import { ShellError } from '../vfs.js';
import { parseWords, parseQuoted, isName, literal, type Word, type WordPart, type Assignment } from '../parser.js';
import { evalArith } from '../arith.js';
import { DIAGNOSTICS, diagnostic } from '../spec.generated.js';
import { type Io, ShellArray } from './types.js';
import { traceQuote } from './invocation.js';
import { defaultOp } from './expansion.js';

export const BASH_VERSINFO = ['5', '2', '21', '1', 'release', 'x86_64-pc-linux-gnu'];
export const SCRATCH = '__topwater_element';

export class VariablesPart {
  // ---------- options ----------

  /** @internal [[ -v name ]], [[ -v a[k] ]]: whether the variable or element is set. */
  isVariableSet(this: Shell, expr: string): boolean {
    const r = arrayRef(expr);
    if (r && !r.keys && r.tail === '') {
      if (r.sub === '@' || r.sub === '*') return (this.getArray(r.name)?.size ?? 0) > 0;
      return this.getElement(r.name, r.sub) !== null;
    }
    return isName(expr) || /^\d+$/.test(expr) ? this.isSet(expr) : false;
  }

  /** @internal set -o NAME / set +o NAME; false for an unknown name. */
  setOption(this: Shell, name: string, on: boolean): boolean {
    switch (name) {
      case 'errexit': this.errexit = on; return true;
      case 'nounset': this.nounset = on; return true;
      case 'pipefail': this.pipefail = on; return true;
      case 'xtrace': this.xtrace = on; return true;
      case 'noglob': this.noglob = on; return true;
      case 'noclobber': this.noclobber = on; return true;
      default: return this.optionList.some(o => o[0] === name); // accepted, no effect here
    }
  }

  /** @internal `local NAME[=VALUE]`: remembers the outer value and attributes, restored when the function returns. */
  declareLocal(this: Shell, name: string, value: string | null) {
    if (this.locals.length === 0) throw new ShellError(DIAGNOSTICS.onlyInFunction);
    const top = this.locals[this.locals.length - 1];
    if (!top.has(name)) { top.set(name, { value: this.environment.get(name), attrs: this.varAttrs.get(name) }); this.varAttrs.delete(name); }
    this.markShellVariable(name);
    this.environment.set(name, value ?? '');
  }

  // ---------- scalars ----------

  /** @internal */
  hasAttr(this: Shell, name: string, attr: string): boolean { return this.varAttrs.size > 0 && (this.varAttrs.get(name)?.includes(attr) ?? false); }

  /** @internal Whether a child bash sees the variable: declared -x, or put in `environment` by the host. */
  isExported(this: Shell, name: string): boolean { return this.hasAttr(name, 'x') || (this.environment.has(name) && !this.unexported.has(name)); }

  /** @internal Records that the shell (not the host) is about to create name, so it is not exported until `export`. */
  markShellVariable(this: Shell, name: string) { if (!this.environment.has(name)) this.unexported.add(name); }

  /** @internal */
  unexport(this: Shell, name: string) { this.setAttrs(name, '', 'x'); this.unexported.add(name); }

  /** @internal */
  setAttrs(this: Shell, name: string, on: string, off = '') {
    let f = this.varAttrs.get(name) ?? '';
    if (on.includes('l')) f = f.replace('u', '');
    if (on.includes('u')) f = f.replace('l', '');
    for (const c of on) if (!f.includes(c)) f += c;
    for (const c of off) f = f.replace(c, '');
    if (f.length === 0) this.varAttrs.delete(name); else this.varAttrs.set(name, f);
  }

  /** @internal Follows namerefs (declare -n ref=name) to the variable they stand for. */
  resolve(this: Shell, name: string): string {
    for (let i = 0; i < 10 && this.hasAttr(name, 'n'); i++) {
      const target = this.environment.get(name);
      if (!target) break;
      name = target;
    }
    return name;
  }

  /**
   * @internal Assigns a variable the way bash does: through namerefs, refusing readonly ones, and applying -i
   * (arithmetic), -l and -u. name[sub] sets an element; a plain assignment to an array sets element 0.
   */
  setVar(this: Shell, name: string, value: string, append = false) {
    name = this.resolve(name);
    const r = arrayRef(name);
    if (r && !r.keys && r.tail === '' && r.sub !== '@' && r.sub !== '*') {
      this.checkWritable(r.name);
      this.setElement(r.name, r.sub, this.converted(this.resolve(r.name), value, append ? this.getElement(r.name, r.sub) : null));
      return;
    }
    if (!isName(name)) throw new ShellError(diagnostic('notValidIdentifier', { name: name }));
    this.checkWritable(name);
    if (name === 'PATH') this.hashed = new Map(); // even to the same value
    if (name === 'RANDOM') { this.random = BigInt.asUintN(64, /^\s*[-+]?\d+\s*$/.test(value) ? BigInt(value.trim()) : 0n); return; }
    if (name === 'SECONDS' && /^-?\d+$/.test(value)) { this.secondsBase = this.now() - Number(value) * 1000; return; }
    const isArray = this.arrays.has(name);
    value = this.converted(name, value, !append ? null : isArray ? this.getElement(name, '0') : this.environment.get(name) ?? null);
    if (isArray) this.setElement(name, '0', value);
    else {
      this.chargeVariable(name, value.length, this.environment.get(name)?.length ?? 0);
      this.markShellVariable(name);
      this.environment.set(name, value);
      if (name === 'IFS') this.ifsUnset = false;
    }
  }

  /** @internal */ converted(this: Shell, name: string, value: string, old: string | null): string {
    const f = this.varAttrs.size ? this.varAttrs.get(name) : undefined;
    if (!f) return (old ?? '') + value;
    if (f.includes('i')) return (evalArith(this, value) + (old ? evalArith(this, old) : 0n)).toString();
    value = (old ?? '') + value;
    return f.includes('l') ? value.toLowerCase() : f.includes('u') ? value.toUpperCase() : value;
  }

  /** @internal */ checkWritable(this: Shell, name: string) {
    if (this.hasAttr(name, 'r')) throw new ShellError(diagnostic('readonlyVariable', { name: name }));
  }

  /** @internal The array called name (through namerefs), including FUNCNAME, BASH_SOURCE, PIPESTATUS and BASH_VERSINFO. */
  getArray(this: Shell, name: string): ShellArray | undefined {
    name = this.resolve(name);
    const a = this.arrays.get(name);
    if (a) return a;
    let items: string[] | null = null;
    if (name === 'FUNCNAME' && this.funcNames.length) items = [...this.funcNames].reverse();
    else if (name === 'BASH_SOURCE' && this.sources.length) items = [...this.sources].reverse();
    else if (name === 'PIPESTATUS') items = this.pipeStatus.map(String);
    else if (name === 'BASH_VERSINFO') items = BASH_VERSINFO;
    if (!items) return undefined;
    const built = new ShellArray(false);
    items.forEach((v, i) => built.set(String(i), v));
    return built;
  }

  /** @internal Shell variables bash sets itself; they are not in the environment `env` prints. */
  shellVariable(this: Shell, name: string): string | undefined {
    switch (name) {
      case 'IFS': return this.ifsUnset ? undefined : ' \t\n';
      case 'OPTIND': return '1';
      case 'BASH': return '/usr/bin/bash';
      case 'BASH_VERSION': return '5.2.21(1)-release';
      case 'UID': case 'EUID': return this.options.user === 'root' ? '0' : '1000';
      case 'PPID': return '4241';
      case 'BASHPID': return '4242';
      case 'SHLVL': return '1';
      case 'OSTYPE': return 'linux-gnu';
      case 'MACHTYPE': return 'x86_64-pc-linux-gnu';
      case 'HOSTTYPE': return 'x86_64';
      case 'LINENO': return String(this.lineno);
      case 'SECONDS': return String(Math.floor((this.now() - this.secondsBase) / 1000));
      case 'EPOCHSECONDS': return String(Math.floor(this.now() / 1000));
      case 'EPOCHREALTIME': return (this.now() / 1000).toFixed(6);
    }
    return undefined;
  }

  /** A variable's value as text: positional and special parameters included; "" when unset. */
  getVariable(this: Shell, name: string): string {
    if (/^\d/.test(name)) {
      const n = Number(name);
      return n === 0 ? this.arg0 : n <= this.positional.length ? this.positional[n - 1] : '';
    }
    switch (name) {
      case '?': return String(this.lastExitCode);
      case '$': return '4242';
      case '!': return this.lastBg === 0 ? '' : String(this.lastBg);
      case '#': return String(this.positional.length);
      case '@': return this.positional.join(' ');
      case '*': return this.positional.join(this.ifsJoiner);
      case '-': return this.optionFlags;
      case '_': return this.lastArg;
      case 'PWD': return this.cwdPath;
      case 'RANDOM': return String(this.nextRandom(32768));
    }
    return this.lookUp(name) ?? '';
  }

  /** @internal A named variable's value (element 0 of an array), or undefined when it is unset. */
  lookUp(this: Shell, name: string): string | undefined {
    name = this.resolve(name);
    const a = this.getArray(name);
    if (a) return a.get('0');
    return this.environment.get(name) ?? this.shellVariable(name);
  }

  /** @internal */ isSet(this: Shell, name: string): boolean {
    if (['?', '$', '#', '@', '*', '-', '_', 'PWD', 'RANDOM', '0'].includes(name)) return true;
    if (/^\d+$/.test(name)) return Number(name) <= this.positional.length;
    if (name === '!') return this.lastBg !== 0;
    return this.lookUp(name) !== undefined;
  }

  // ---------- arrays ----------

  /** @internal The array to change: created if missing, copied first if a saved state still shares it. */
  writable(this: Shell, name: string, assoc = false): ShellArray {
    name = this.resolve(name);
    this.checkWritable(name);
    let a = this.arrays.get(name);
    if (!a) {
      a = new ShellArray(assoc);
      a.epoch = this.epoch;
      const scalar = this.environment.get(name);
      if (scalar !== undefined) { this.environment.delete(name); a.set('0', scalar); } // a plain variable becomes element 0
      this.arrays.set(name, a);
      return a;
    }
    if (a.epoch === this.epoch) return a;
    const c = a.clone(this.epoch);
    this.arrays.set(name, c);
    return c;
  }

  /** @internal `local arr` / `declare -A m`: a fresh array, restored to the caller's when the function returns. */
  declareArray(this: Shell, name: string, assoc: boolean, local: boolean) {
    this.checkWritable(name);
    if (local && this.localArrays.length > 0) {
      const top = this.localArrays[this.localArrays.length - 1];
      if (!top.has(name)) top.set(name, this.arrays.get(name));
    } else {
      const old = this.arrays.get(name);
      if (old && old.assoc === assoc) return;
    }
    if (local && this.locals.length > 0) this.declareLocal(name, null);
    this.environment.delete(name);
    const a = new ShellArray(assoc);
    a.epoch = this.epoch;
    this.arrays.set(name, a);
  }

  /** @internal The storage key for a subscript: the text itself for an associative array, else its arithmetic value. */
  arrayKey(this: Shell, array: ShellArray | undefined, subscript: string, name = ''): string {
    if (array?.assoc) return subscript;
    let i = evalArith(this, subscript);
    if (i < 0n) i += BigInt(array?.next ?? 1);
    if (i < 0n) throw new ShellError(diagnostic('badArraySubscript', { name: name }));
    return i.toString();
  }

  /** @internal One element, or null if unset. A plain variable answers as element 0. */
  getElement(this: Shell, name: string, subscript: string): string | null {
    name = this.resolve(name);
    const a = this.getArray(name);
    if (a) return a.get(this.arrayKey(a, subscript, name)) ?? null;
    const scalar = this.environment.get(name);
    return scalar !== undefined && this.arrayKey(undefined, subscript, name) === '0' ? scalar : null;
  }

  /** @internal */
  setElement(this: Shell, name: string, subscript: string, value: string) {
    const a = this.writable(name);
    const key = this.arrayKey(a, subscript, name);
    const old = a.get(key);
    this.chargeVariable(name, key.length + value.length, old !== undefined ? key.length + old.length : 0);
    a.set(key, value);
  }

  /** @internal unset NAME or unset 'NAME[sub]'; `nameref` (-n) unsets a nameref itself, not what it names. */
  unset(this: Shell, target: string, nameref = false) {
    if (!nameref) target = this.resolve(target);
    const r = arrayRef(target);
    if (r && !r.keys && r.tail === '' && this.arrays.has(r.name) && r.sub !== '@' && r.sub !== '*') {
      const a = this.writable(r.name);
      a.delete(this.arrayKey(a, unquote(r.sub), r.name));
      return;
    }
    const name = r?.name ?? target;
    if (this.hasAttr(name, 'r')) throw new ShellError(diagnostic('cannotUnsetReadonly', { name: name }));
    this.environment.delete(name);
    this.arrays.delete(name);
    this.varAttrs.delete(name);
    this.unexported.delete(name);
    if (name === 'IFS') this.ifsUnset = true;
  }

  /** @internal The elements ${name[@]...} stands for (keys for ${!name[@]}); null if expr is not a whole-array expansion. */
  async arrayElements(this: Shell, expr: string, io: Io, quoted = false): Promise<string[] | null> {
    const r = arrayRef(expr);
    if (!r || (r.sub !== '@' && r.sub !== '*')) return null;
    const a = this.getArray(r.name);
    const name = this.resolve(r.name);
    let items: string[];
    if (!a) { const s = this.environment.get(name); items = s === undefined ? [] : [r.keys ? '0' : s]; }
    else items = r.keys ? a.keys() : a.values();
    if (r.keys || r.tail.length === 0) return items;
    const dop = defaultOp(r.tail);
    if (dop) {
      const [op, word] = dop;
      // ${a[@]:-word}: the array as a whole is tested; empty means no elements (or, with ':', nothing but empty text)
      const missing = items.length === 0 || (op[0] === ':' && items.join(r.sub === '*' ? this.ifsJoiner : ' ').length === 0);
      if (op.endsWith('?') && missing)
        throw this.fatal(io, `${r.name}[${r.sub}]: ${word.length > 0 ? await this.paramWordValue(word, quoted, io) : 'parameter null or not set'}`, 127);
      if (op.endsWith('=') && missing) throw new ShellError(diagnostic('badArraySubscript', { name: `${r.name}[${r.sub}]` }));
      if (op.endsWith('-')) return missing ? [await this.paramWordValue(word, quoted, io)] : items;
      if (op.endsWith('+')) return missing ? [] : [await this.paramWordValue(word, quoted, io)];
      return items;
    }
    if (r.tail[0] === ':') {
      const spec = splitOnce(r.tail.slice(1), ':');
      let from = Number(await this.evalArithExpr(spec[0], io));
      const count = spec.length === 1 ? Infinity : Number(await this.evalArithExpr(spec[1], io));
      if (count < 0) throw new ShellError(diagnostic('substringNegative', { expression: spec[1].trim() }));
      // an indexed array is sliced by index, so a sparse array skips its gaps
      if (a && !a.assoc) {
        const keys = a.keys().map(Number);
        if (from < 0) from += keys.length === 0 ? 0 : keys[keys.length - 1] + 1;
        if (from < 0) return [];
        const firstIdx = keys.findIndex(k => k >= from);
        return firstIdx < 0 ? [] : items.slice(firstIdx, count === Infinity ? undefined : firstIdx + count);
      }
      if (from < 0) from += items.length;
      if (from < 0 || from > items.length) return [];
      return items.slice(from, count === Infinity ? undefined : from + count);
    }
    // any other operator applies to each element: ${files[@]%.txt}, ${names[@]^^}
    const result: string[] = [];
    for (const item of items) result.push(await this.elementOp(item, r.tail, io, quoted));
    return result;
  }

  /** @internal Applies a ${...} operator to one value by expanding it through a scratch variable. */
  async elementOp(this: Shell, value: string | null, rest: string, io: Io, quoted = false): Promise<string> {
    if (rest.length === 0) return value ?? '';
    if (value === null) this.environment.delete(SCRATCH); else this.environment.set(SCRATCH, value);
    try { return await this.expandParameter(SCRATCH + rest, io, quoted); } finally { this.environment.delete(SCRATCH); }
  }

  /** @internal `unset 'a[${#a[@]}-1]'`: name[sub] with the subscript's expansions done. */
  async expandSubscript(this: Shell, target: string, io: Io): Promise<string> {
    const r = arrayRef(target);
    return r && !r.keys && r.tail === '' && /[$`]/.test(r.sub) ? `${r.name}[${await this.subscript(r.sub, io)}]` : target;
  }

  /** @internal */ async subscript(this: Shell, sub: string, io: Io): Promise<string> {
    return /[$`"']/.test(sub) ? this.expandSingle(parseQuoted(unquote(sub), false), io) : sub;
  }

  /** @internal */ async assignArray(this: Shell, a: Assignment, io: Io) {
    const name = this.resolve(a.name);
    if (a.elements === undefined) {
      const key = await this.subscript(a.index!, io);
      const value = await this.expandAssignment(a.value, io);
      if (this.xtrace) io.err.write(`+ ${name}[${key}]${a.append ? '+=' : '='}${traceQuote(value)}\n`);
      this.setVar(`${name}[${key}]`, value, a.append);
      return;
    }
    let target: ShellArray;
    if (a.append) target = this.writable(name);
    else {
      this.checkWritable(name);
      target = new ShellArray(this.arrays.get(name)?.assoc ?? false);
      target.epoch = this.epoch;
      this.environment.delete(name);
    }
    for (const w of parseWords(a.elements)) {
      // [key]=value sets that element; anything else is appended, after the usual splitting and globbing
      const kv = splitSubscript(w);
      if (kv) target.set(this.arrayKey(target, await this.expandSingle(kv[0], io)), await this.expandSingle(kv[1], io));
      else for (const field of await this.expandWords([w], io)) target.set(target.assoc ? field : String(target.next), field);
    }
    this.arrays.set(name, target);
  }

  /** @internal
   * `local a=(x y)`, `declare -A m`, `declare -a list=()`: the array arguments are handled here, because a built-in
   * only receives expanded text. Returns the words left for the built-in, or null if none were array declarations.
   */
  async declareArrays(this: Shell, words: Word[], io: Io): Promise<Word[] | null> {
    const flags = words.slice(1).map(literal).filter((t): t is string => t !== null && t.startsWith('-')).join('');
    if (/[pfF]/.test(flags)) return null;
    const first = literal(words[0]);
    const local = first === 'local' || (this.inFunction && !flags.includes('g'));
    const assoc = flags.includes('A');
    let any = false;
    const attrs = [...flags].filter(c => c === 'r' || c === 'x').join('') + (first === 'readonly' ? 'r' : '');
    const keep: Word[] = [words[0]];
    for (const w of words.slice(1)) {
      const l = literal(w);
      if (l?.startsWith('-')) continue;
      const assignment = w.assign;
      if (assignment?.elements !== undefined) {
        this.declareArray(assignment.name, assoc, local);
        await this.assignArray(assignment, io);
      } else if (!assignment && (assoc || flags.includes('a')) && l !== null && isName(l)) this.declareArray(l, assoc, local);
      else { keep.push(w); continue; }
      if (attrs.length > 0) this.setAttrs(assignment?.name ?? l!, attrs);
      any = true;
    }
    return any ? keep : null;
  }
}

// ---------- helpers ----------

/** Splits name[sub]rest (or !name[sub] for the keys); null when the text is not that shape. */
export function arrayRef(expr: string): { keys: boolean; name: string; sub: string; tail: string } | null {
  const start = expr.startsWith('!') ? 1 : 0, open = expr.indexOf('[');
  if (open <= start || !isName(expr.slice(start, open))) return null;
  let depth = 0, k = open;
  for (; k < expr.length; k++) {
    if (expr[k] === '[') depth++;
    else if (expr[k] === ']' && --depth === 0) break;
  }
  return k < expr.length ? { keys: start === 1, name: expr.slice(start, open), sub: expr.slice(open + 1, k), tail: expr.slice(k + 1) } : null;
}

export const unquote = (s: string) => (s.length >= 2 && (s[0] === '"' || s[0] === "'") && s[s.length - 1] === s[0] ? s.slice(1, -1) : s);

export function splitOnce(s: string, sep: string): string[] {
  const i = s.indexOf(sep);
  return i < 0 ? [s] : [s.slice(0, i), s.slice(i + 1)];
}

/** [key]=value in an array literal: the key and value words, or null. */
export function splitSubscript(w: Word): [Word, Word] | null {
  const first = w.parts[0];
  if (!first || first.k !== 'lit' || first.q || !first.text.startsWith('[')) return null;
  // find the closing ]= in the literal text of the parts
  let depth = 0;
  for (let pi = 0; pi < w.parts.length; pi++) {
    const p = w.parts[pi];
    if (p.k !== 'lit' || p.q) continue;
    for (let i = pi === 0 ? 0 : 0; i < p.text.length; i++) {
      const c = p.text[i];
      if (c === '[') depth++;
      else if (c === ']' && --depth === 0) {
        if (p.text[i + 1] !== '=') return null;
        const keyParts: WordPart[] = [...w.parts.slice(0, pi), { k: 'lit', text: p.text.slice(0, i), q: false }];
        keyParts[0] = { k: 'lit', text: (keyParts[0] as { text: string }).text.slice(1), q: false };
        if (pi === 0) keyParts.splice(0, keyParts.length, { k: 'lit', text: p.text.slice(1, i), q: false });
        const valueParts: WordPart[] = [{ k: 'lit', text: p.text.slice(i + 2), q: false }, ...w.parts.slice(pi + 1)];
        return [{ parts: keyParts }, { parts: valueParts }];
      }
    }
  }
  return null;
}
