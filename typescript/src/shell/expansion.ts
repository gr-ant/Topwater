// Shell: word expansion, parameters, arithmetic, substitution and field splitting. See docs/architecture.md.
import type { Shell } from '../shell.js';
import { ShellError, ordinal } from '../vfs.js';
import { StdinBuffer, LimitExceeded, tooLarge } from '../command.js';
import { parse, parseQuoted, parseParamWord, isName, ParseError, type Script, type Word, type WordPart } from '../parser.js';
import { evalArith, ArithError } from '../arith.js';
import { escapeGlob, hasWildcard, globRegex, globBody, cachedRegex, expandGlob, braceExpand } from '../glob.js';
import { quoteValue, unescape } from '../format.js';
import { diagnostic } from '../spec.generated.js';
import { attributeFlags } from '../commands/variables.js';
import { type Io, BadSubstitution, LineAbort, ExitSignal } from './types.js';
import { BufferWriter } from './redirection.js';
import { arrayRef, splitOnce, SCRATCH } from './variables.js';

export const PARAM_RE = /^([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!-])([^]*)$/;
export const INDIRECT_RE = /^!([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!-])([^]*)$/;
export const PREFIX_RE = /^!([A-Za-z_][A-Za-z0-9_]*)[*@]$/;

export class ExpansionPart {
  // ---------- parts and arithmetic ----------

  /** @internal */ async expandPart(this: Shell, part: WordPart, io: Io): Promise<string> {
    switch (part.k) {
      case 'lit': case 'exp': return part.text;
      case 'var':
        try { return await this.expandParameter(part.expr, io, part.q); } catch (e) {
          if (e instanceof BadSubstitution) {
            const message = diagnostic('badSubstitution', { text: part.src ?? '${' + part.expr + '}' });
            if (e.ends || this.subshell > 0) throw this.fatal(io, message, 127);
            io.err.write(`${this.errPrefix}${message}\n`);
            throw new LineAbort();
          }
          throw e;
        }
      case 'cmd': return this.capture(part.src, io);
      case 'arith': return (await this.arithExpansion(part.expr, io)).toString();
      case 'proc': return this.processSubstitution(part.src, io);
    }
  }

  /** @internal Expands $(cmd), `cmd` and ${...} inside an arithmetic expression, then evaluates it. */
  async evalArithExpr(this: Shell, expr: string, io: Io): Promise<bigint> {
    // each level of $(( $(( ... )) )) re-reads what is inside it, so unbounded nesting is quadratic work
    if (++this.arithDepth > 100) { this.arithDepth = 0; throw new LimitExceeded('arithmetic is nested too deeply'); }
    try { return evalArith(this, /[$`]/.test(expr) ? await this.expandSingle(parseQuoted(expr, false), io) : expr); } finally { if (this.arithDepth > 0) this.arithDepth--; }
  }

  /** @internal $(( )): unlike (( )) and let, an error here ends the script, as in bash. */
  async arithExpansion(this: Shell, expr: string, io: Io): Promise<bigint> {
    try { return await this.evalArithExpr(expr, io); } catch (e) {
      if (e instanceof ShellError) throw this.fatal(io, e.message, 1);
      throw e;
    }
  }

  // ---------- parameters ----------

  /** @internal An expansion error that ends a non-interactive bash: the shell exits (a subshell with status 1). */
  fatal(this: Shell, io: Io, message: string, code: number): ExitSignal {
    io.err.write(`${this.errPrefix}${message}\n`);
    return new ExitSignal(this.subshell > 0 ? 1 : code);
  }

  /** @internal ${!name...}: the expression with name's value in its place; null if expr is not an indirection. */
  indirectTarget(this: Shell, expr: string): string | null {
    if (expr.length < 2 || expr[0] !== '!' || PREFIX_RE.test(expr) || arrayRef(expr)?.keys) return null;
    const m = INDIRECT_RE.exec(expr);
    if (!m) return null;
    const name = m[1];
    if (this.hasAttr(name, 'n')) return null; // ${!ref} of a nameref is the name it refers to
    const target = this.getVariable(name);
    if (target.length === 0) throw new ShellError(diagnostic('invalidIndirect', { name: name }));
    return target + m[2];
  }

  /** @internal The word of ${x op word}, lexed once per distinct text. */
  paramWord(this: Shell, text: string, quoted: boolean, pattern = false): Word {
    const key = (quoted ? '1' : '0') + (pattern ? 'p' : '') + text;
    let w = this.paramWords.get(key);
    if (w) return w;
    if (this.paramWords.size >= 512) this.paramWords.clear();
    try { w = parseParamWord(text, quoted, pattern); } catch (e) {
      if (e instanceof ParseError) throw new ShellError(e.message);
      throw e;
    }
    this.paramWords.set(key, w);
    return w;
  }
  /** @internal */ paramWordValue(this: Shell, text: string, quoted: boolean, io: Io) { return this.expandSingle(this.paramWord(text, quoted), io); }

  /** @internal ${x/pat/rep}'s replacement: literal pieces and, for an unquoted &, the match (bash 5.2). */
  async replacement(this: Shell, text: string, quoted: boolean, io: Io): Promise<(string | null)[]> {
    const out: (string | null)[] = [];
    const parts = this.paramWord(text, quoted, true).parts;
    for (let i = 0; i < parts.length; i++) {
      const part = parts[i];
      let t = await this.expandPart(part, io);
      if (i === 0 && part.k === 'lit' && !part.q) { const tp = this.tildePrefix(t); if (tp) t = tp[0] + t.slice(tp[1]); }
      if (part.q || part.k !== 'lit') { out.push(t); continue; }
      t.split('&').forEach((piece, k) => { if (k > 0) out.push(null); out.push(piece); });
    }
    return out;
  }

  /** @internal ${name}, ${#name}, ${name:-x}, ${name%pat}, ${name/a/b}, ${name:1:2}, ${name^^}, ${name@Q}, ${!name} ... */
  async expandParameter(this: Shell, expr: string, io: Io, quoted = false): Promise<string> {
    if (expr.length > 1 && expr[0] === '!') {
      const pm = PREFIX_RE.exec(expr);
      if (pm) return [...new Set([...this.environment.keys(), ...this.arrays.keys()])].filter(k => k.startsWith(pm[1]) && k !== SCRATCH).sort(ordinal).join(' ');
      if (isName(expr.slice(1)) && this.hasAttr(expr.slice(1), 'n')) return this.environment.get(expr.slice(1)) ?? '';
      const target = this.indirectTarget(expr);
      if (target !== null) return this.expandParameter(target, io, quoted);
    }
    if (expr.length > 1 && expr[0] === '#') {
      const counted = arrayRef(expr.slice(1));
      if (counted && !counted.keys && counted.tail === '') {
        if (counted.sub === '@' || counted.sub === '*') {
          const a = this.getArray(counted.name);
          return String(a ? a.size : this.lookUp(counted.name) !== undefined ? 1 : 0);
        }
        return String([...(this.getElement(counted.name, await this.subscript(counted.sub, io)) ?? '')].length);
      }
    }
    // as one string, ${a[*]} joins with the first character of IFS and ${a[@]} with a space
    const all = await this.arrayElements(expr, io, quoted);
    if (all) return all.join(arrayRef(expr)!.sub === '*' ? this.ifsJoiner : ' ');
    const one = arrayRef(expr);
    if (one && !one.keys) {
      let element: string | null;
      try { element = this.getElement(one.name, await this.subscript(one.sub, io)); } catch (e) {
        if (e instanceof ArithError) throw this.fatal(io, e.message, 1);
        if (!(e instanceof ShellError)) throw e;
        io.err.write(`${this.errPrefix}${e.message}\n`); // like bash: reported, and the expansion is empty
        element = null;
      }
      if (element === null && this.nounset && !defaultOp(one.tail)) throw this.fatal(io, diagnostic('unboundVariable', { name: `${one.name}[${one.sub}]` }), 127);
      return this.elementOp(element, one.tail, io, quoted);
    }
    if (expr.length > 1 && expr[0] === '#') {
      const n = expr.slice(1);
      if (n === '@' || n === '*') return String(this.positional.length);
      if (PARAM_RE.test(n) && PARAM_RE.exec(n)![2] === '') {
        this.checkSet(n, io);
        return String([...this.getVariable(n)].length);
      }
    }
    const m = PARAM_RE.exec(expr);
    if (!m) throw new BadSubstitution(diagnostic("badSubstitution", { text: "${" + expr + "}" }));
    const name = m[1], rest = m[2];
    const value = this.getVariable(name);
    const dop = defaultOp(rest);
    if (!dop) this.checkSet(name, io); // defaults handle unset variables
    if (rest.length === 0) return value;
    if (dop) {
      const [op, word] = dop;
      const missing = op[0] === ':' ? value.length === 0 : !this.isSet(name);
      switch (op[op.length - 1]) {
        case '-': return missing ? this.paramWordValue(word, quoted, io) : value;
        case '=':
          if (!missing) return value;
          if (!isName(name)) throw new ShellError(diagnostic('cannotAssign', { name: name }));
          this.setVar(name, await this.paramWordValue(word, quoted, io));
          return this.getVariable(name);
        case '+': return missing ? '' : this.paramWordValue(word, quoted, io);
        default:
          if (missing) throw this.fatal(io, `${name}: ${word.length > 0 ? await this.paramWordValue(word, quoted, io) : op[0] === ':' ? 'parameter null or not set' : 'parameter not set'}`, 127);
          return value;
      }
    }
    if (rest[0] === '#' || rest[0] === '%') {
      const longest = rest.length > 1 && rest[1] === rest[0];
      const re = globRegex(await this.pattern(this.paramWord(rest.slice(longest ? 2 : 1), quoted, true), io), false, true);
      const n = value.length;
      if (rest[0] === '#') {
        for (let i = longest ? n : 0; longest ? i >= 0 : i <= n; i += longest ? -1 : 1) if (re.test(value.slice(0, i))) return value.slice(i);
      } else {
        for (let i = longest ? 0 : n; longest ? i <= n : i >= 0; i += longest ? 1 : -1) if (re.test(value.slice(i))) return value.slice(0, i);
      }
      return value;
    }
    if (rest[0] === '/') {
      let body = rest.slice(1);
      const mode = body.length > 0 && '/#%'.includes(body[0]) ? body[0] : ' ';
      if (mode !== ' ') body = body.slice(1);
      // the pattern ends at the first unquoted '/'; after `//` a leading '/' is part of the pattern
      let slash = -1;
      for (let i = mode === '/' && body.startsWith('/') ? 1 : 0; i < body.length; i++) {
        if (body[i] === '\\') { i++; continue; }
        if (body[i] === "'" && !quoted) { const q = body.indexOf("'", i + 1); i = q >= 0 ? q : body.length; continue; }
        if (body[i] === '"') { for (i++; i < body.length && body[i] !== '"'; i++) if (body[i] === '\\') i++; continue; }
        if (body[i] === '/') { slash = i; break; }
      }
      const pat = globBody(await this.pattern(this.paramWord(slash < 0 ? body : body.slice(0, slash), quoted, true), io), true);
      const rep = slash < 0 ? [] : await this.replacement(body.slice(slash + 1), quoted, io);
      const plain = rep.every(p => p !== null) ? rep.join('') : null;
      // an empty pattern matches nothing, except at an anchor: ${x/#/pre-} and ${x/%/-post} add to the ends
      if (pat.length === 0) return mode === '#' ? (plain ?? '') + value : mode === '%' ? value + (plain ?? '') : value;
      const re = cachedRegex(mode === '#' ? '^(?:' + pat + ')' : mode === '%' ? '(?:' + pat + ')$' : pat, mode === '/' ? 'gs' : 's');
      const repLen = rep.reduce((n, p) => n + (p?.length ?? 0), 0);
      // ${x//?/$x} grows with the square of x: refuse before building it
      if (mode === '/' && value.length + repLen * (value.length + 1) > this.options.maxStringBytes) {
        const count = value.match(re)?.length ?? 0;
        if (value.length + repLen * count > this.options.maxStringBytes) throw tooLarge('expansion', this.options.maxStringBytes, 'write the data to a file instead');
      }
      re.lastIndex = 0;
      return value.replace(re, (match: string) => (plain !== null ? plain : rep.map(p => p ?? match).join('')));
    }
    if (rest[0] === '^' || rest[0] === ',' || rest[0] === '~') {
      // ${x^} ${x^^} ${x,} ${x,,} ${x~} ${x~~}, optionally only for characters matching a pattern: ${x^^[aeiou]}
      const every = rest.length > 1 && rest[1] === rest[0];
      const patText = rest.slice(every ? 2 : 1);
      const only = patText.length > 0 ? globRegex(await this.pattern(this.paramWord(patText, quoted), io), false, true) : null;
      const chars = [...value];
      for (let i = 0; i < chars.length && (every || i === 0); i++) {
        const c = chars[i];
        if (only && !only.test(c)) continue;
        const lower = c !== c.toUpperCase();
        chars[i] = rest[0] === '^' || (rest[0] === '~' && lower) ? c.toUpperCase() : c.toLowerCase();
      }
      return chars.join('');
    }
    if (rest[0] === '@' && rest.length <= 2) {
      if (rest[1] === 'a') return attributeFlags(this, this.resolve(name));
      if (!this.isSet(name)) return '';
      switch (rest[1]) {
        case 'Q': case 'K': return quoteValue(value);
        case 'E': return unescape(value, true);
        case 'U': return value.toUpperCase();
        case 'L': return value.toLowerCase();
        case 'u': return value.length === 0 ? value : value[0].toUpperCase() + value.slice(1);
        case 'A': {
          const flags = attributeFlags(this, this.resolve(name));
          return flags ? `declare -${flags} ${name}=${quoteValue(value)}` : `${name}=${quoteValue(value)}`;
        }
        case 'P': return value;
      }
      throw new BadSubstitution(diagnostic("badSubstitution", { text: "${" + expr + "}" }), true);
    }
    if (rest[0] === ':') {
      const spec = splitOnce(rest.slice(1), ':');
      const chars = [...value];
      let offset = Number(await this.evalArithExpr(spec[0], io));
      if (offset < 0) offset += chars.length;
      if (offset < 0 || offset > chars.length) return '';
      const tail = chars.slice(offset);
      if (spec.length === 1) return tail.join('');
      let len = Number(await this.evalArithExpr(spec[1], io));
      if (len < 0 && (len += tail.length) < 0) throw new ShellError(diagnostic('substringNegative', { expression: spec[1].trim() }));
      return tail.slice(0, len).join('');
    }
    throw new BadSubstitution(diagnostic("badSubstitution", { text: "${" + expr + "}" }));
  }

  /** @internal set -u: expanding an unset variable is an error that ends the (sub)shell, like bash. */
  checkSet(this: Shell, name: string, io: Io) {
    if (!this.nounset || name === '@' || name === '*' || this.isSet(name)) return;
    throw this.fatal(io, diagnostic('unboundVariable', { name }), 127);
  }

  // ---------- substitution ----------

  /** @internal $(...) and <(...) run in a subshell that, as in bash, does not inherit set -e (unless shopt inherit_errexit). */
  async runSubstitution(this: Shell, source: string, io: Io): Promise<number> {
    const errexit = this.errexit;
    if (!this.shopts.has('inherit_errexit')) this.errexit = false;
    try { return await this.runScriptText(source, io, true, undefined, undefined, undefined, 'sub'); } finally { this.errexit = errexit; }
  }

  /** @internal <(cmd): writes cmd's output to /dev/fd/N (removed after the tool call) and returns that path. */
  async processSubstitution(this: Shell, source: string, io: Io): Promise<string> {
    const output = new BufferWriter(this.options.maxStringBytes);
    await this.runSubstitution(source, { ...io, stdin: new StdinBuffer(''), out: output });
    const path = `/dev/fd/${63 + this.procSubCount++}`;
    this.fs.writeText(path, output.toString());
    return path;
  }

  /** @internal */ async capture(this: Shell, source: string, io: Io): Promise<string> {
    const fileWord = readFileWord(source);
    if (fileWord) { // $(< file) is the file's contents
      const target = await this.expandSingle(fileWord, io);
      try {
        const text = this.agentFs.readText(this.resolvePath(target));
        this.lastExitCode = 0;
        return text.replace(/\n+$/, '');
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        io.err.write(`${this.errPrefix}${target}: ${e.message.slice(e.message.lastIndexOf(': ') + 2)}\n`);
        this.lastExitCode = 1;
        return '';
      }
    }
    const output = new BufferWriter(this.options.maxStringBytes);
    this.lastExitCode = await this.runSubstitution(source, { ...io, stdin: new StdinBuffer(''), out: output });
    return output.toString().replace(/\n+$/, '');
  }

  // ---------- words ----------

  /** @internal Expands a word without field splitting or globbing (redirect targets, heredocs, case subjects). */
  async expandSingle(this: Shell, word: Word, io: Io): Promise<string> {
    let out = '';
    for (let i = 0; i < word.parts.length; i++) {
      const part = word.parts[i];
      let text = await this.expandPart(part, io);
      if (i === 0 && part.k === 'lit' && !part.q) { const tp = this.tildePrefix(text); if (tp) text = tp[0] + text.slice(tp[1]); }
      if (out.length + text.length > this.options.maxStringBytes) throw tooLarge('expansion', this.options.maxStringBytes, 'write the data to a file instead');
      out += text;
    }
    return out;
  }

  /** @internal An assignment's value: one string, with ~ expanded at the start and after each ':' (PATH=~/bin:~/x). */
  async expandAssignment(this: Shell, word: Word, io: Io): Promise<string> {
    let out = '';
    for (let i = 0; i < word.parts.length; i++) {
      const part = word.parts[i];
      let text = await this.expandPart(part, io);
      if (part.k === 'lit' && !part.q && text.includes('~')) {
        const atStart = i === 0 || out.endsWith(':');
        text = text.replace(/(?<=^|:)~([A-Za-z0-9_.+-]*)(?=\/|:|$)/g, (m0, user: string, index: number) =>
          (index === 0 && !atStart ? null : this.tildeValue(user)) ?? m0);
      }
      out += text;
    }
    return out;
  }

  /** @internal Expands a word into a glob pattern: quoted text is escaped, unquoted text keeps its wildcards. */
  async pattern(this: Shell, word: Word, io: Io): Promise<string> {
    let out = '';
    for (const part of word.parts) {
      const text = await this.expandPart(part, io);
      out += part.q ? escapeGlob(text) : text;
    }
    return out;
  }

  /** @internal ~, ~user, ~+ and ~-: the directory they name, or null for an unknown user. */
  tildeValue(this: Shell, user: string): string | null {
    if (user === '') return this.environment.get('HOME') ?? '/';
    if (user === '+') return this.cwdPath;
    if (user === '-') return this.environment.get('OLDPWD') ?? null;
    if (user === 'root') return '/root';
    if (user === this.options.user) return '/home/' + user;
    return null;
  }

  /** @internal A leading ~ or ~user (up to the first '/'): its directory and how many characters it spans. */
  tildePrefix(this: Shell, text: string): [string, number] | null {
    if (!text.startsWith('~')) return null;
    let end = text.indexOf('/');
    if (end < 0) end = text.length;
    const home = this.tildeValue(text.slice(1, end));
    return home === null ? null : [home, end];
  }

  /** @internal
   * An unquoted ${p:-word} or ${p:+word} whose word applies: the word's parts, so quotes inside the word still keep
   * it from being split (${v:-"a b"} is one field). Null when p's own value applies.
   */
  async alternativeWord(this: Shell, expr: string, io: Io): Promise<WordPart[] | null> {
    const element = arrayRef(expr);
    const match = element ? null : PARAM_RE.exec(expr);
    const rest = element && !element.keys && element.sub !== '@' && element.sub !== '*' ? element.tail : match ? match[2] : '';
    const dop = defaultOp(rest);
    if (!dop || (dop[0].slice(-1) !== '-' && dop[0].slice(-1) !== '+')) return null;
    const [op, word] = dop;
    const value = element ? this.getElement(element.name, await this.subscript(element.sub, io))
      : this.isSet(match![1]) ? this.getVariable(match![1]) : null;
    const missing = op[0] === ':' ? !value : value === null;
    if (op.endsWith('-') ? !missing : missing) return op.endsWith('-') ? null : [];
    // the word's unquoted text is split like any expansion's
    return this.paramWord(word, false).parts.map(p => (p.k === 'lit' && !p.q ? { k: 'exp', text: p.text, q: false } : p));
  }

  /** @internal Expands words into fields: brace expansion, parameters, splitting on IFS, globbing. */
  async expandWords(this: Shell, words: Word[], io: Io): Promise<string[]> {
    const result: string[] = [];
    let total = 0; // all the words together, so {1..10000}$big can't multiply a big value
    const max = this.options.maxStringBytes;
    for (const word of words.flatMap(braceExpand)) {
      // fast path: one unquoted literal without wildcards or tilde
      if (word.parts.length === 1 && word.parts[0].k === 'lit') {
        const p = word.parts[0];
        if (p.q || (!hasWildcard(p.text) && !p.text.startsWith('~'))) { result.push(p.text); continue; }
      }
      interface Field { text: string; pattern: string; glob: boolean; content: boolean }
      const fields: Field[] = [];
      let cur: Field = { text: '', pattern: '', glob: false, content: false };
      const end = () => { if (cur.content) fields.push(cur); cur = { text: '', pattern: '', glob: false, content: false }; };
      const addText = (s: string, quoted: boolean) => {
        if ((total += s.length) > max) throw tooLarge('expansion', max, 'write the data to a file instead');
        cur.text += s;
        cur.pattern += quoted ? escapeGlob(s) : s;
        if (!quoted && hasWildcard(s)) cur.glob = true;
        cur.content = true;
      };
      // unquoted $VAR / $(cmd) / $((...)): split into fields on $IFS
      const addSplit = (text: string) => {
        const ifs = this.ifs;
        const pieces = splitFields(text, ifs);
        if (pieces.length === 0) { if (text.length > 0 && ifs.length > 0) end(); return; }
        if (ifs.includes(text[0])) end();
        pieces.forEach((piece, k) => { if (k > 0) end(); addText(piece, false); });
        if (ifs.includes(text[text.length - 1])) end();
      };
      // "$@" / "${a[@]}": one field per item, and none at all when there are none; unquoted, each item is split
      const addEach = (items: string[], quoted: boolean) => {
        items.forEach((item, k) => { if (k > 0) end(); if (quoted) addText(item, true); else addSplit(item); });
      };
      let parts: WordPart[] = word.parts;
      for (let i = 0; i < parts.length; i++) {
        let part = parts[i];
        if (part.k === 'var') {
          const name = this.indirectTarget(part.expr) ?? part.expr;
          if (name === '@' || (name === '*' && !part.q)) { addEach(this.positional, part.q); continue; }
          if ((name[0] === '@' || name[0] === '*') && name[1] === ':' && !defaultOp(name.slice(1))) {
            // ${@:2} and ${@:2:3} slice the parameters, not the characters; ${@:0} starts at $0
            const spec = splitOnce(name.slice(2), ':');
            const allParams = [this.arg0, ...this.positional];
            let from = Number(await this.evalArithExpr(spec[0], io));
            if (from < 0) from += allParams.length;
            const count = spec.length === 1 ? allParams.length : Number(await this.evalArithExpr(spec[1], io));
            const items = from < 0 || from > allParams.length ? [] : allParams.slice(from, from + Math.max(0, Math.min(count, allParams.length)));
            if (part.q && name[0] === '*') addText(items.join(this.ifsJoiner), true); else addEach(items, part.q);
            continue;
          }
          const ar = arrayRef(name);
          if (ar && (ar.sub === '@' || ar.sub === '*') && (ar.sub === '@' || !part.q)) {
            const elements = await this.arrayElements(name, io, part.q);
            if (elements) { addEach(elements, part.q); continue; }
          }
          if (!part.q && /[-+]/.test(name)) {
            const alternative = await this.alternativeWord(name, io);
            if (alternative) {
              parts = [...parts.slice(0, i), ...alternative, ...parts.slice(i + 1)];
              i--;
              continue;
            }
          }
          if (name !== part.expr) part = { ...part, expr: name };
        }
        let text = await this.expandPart(part, io);
        if (part.k === 'lit') {
          if (i === 0 && !part.q) {
            const tp = this.tildePrefix(text);
            if (tp) {
              addText(tp[0], true); // the home path is never globbed
              text = text.slice(tp[1]);
              if (text.length === 0) continue;
            }
          }
          addText(text, part.q);
          continue;
        }
        if (part.q) addText(text, true);
        else addSplit(text);
      }
      end();
      for (const f of fields) {
        const globbing = f.glob && !this.noglob;
        const matches = globbing ? expandGlob({
          fs: this.agentFs, cwd: this.cwdPath, dotglob: this.shopts.has('dotglob'), globstar: this.shopts.has('globstar'), nocaseglob: this.shopts.has('nocaseglob'),
        }, f.pattern) : [];
        if (matches.length > 0) result.push(...matches);
        else if (!(globbing && this.shopts.has('nullglob'))) result.push(f.text);
      }
    }
    return result;
  }
}

// ---------- helpers ----------

/** ${p-w} ${p:-w} ${p=w} ${p:=w} ${p+w} ${p:+w} ${p?w} ${p:?w}: the operator and the word; null for any other operator. */
export function defaultOp(rest: string): [op: string, word: string] | null {
  const n = rest[0] === ':' ? 2 : 1;
  return rest.length >= n && '-=+?'.includes(rest[n - 1]) ? [rest.slice(0, n), rest.slice(n)] : null;
}

/** Splits text on IFS like bash: whitespace runs collapse and are trimmed; other IFS characters delimit one field each.
 * maxFields > 0 leaves the rest of the line in the last field (`read a b`). */
export function splitFields(text: string, ifs: string, maxFields = 0): string[] {
  if (ifs.length === 0) return text.length > 0 ? [text] : [];
  const ws = [...' \t\n'].filter(c => ifs.includes(c)).join('');
  const other = [...ifs].filter(c => !ws.includes(c)).join('');
  if (ws.length > 0) {
    let s = 0, e = text.length;
    while (s < e && ws.includes(text[s])) s++;
    while (e > s && ws.includes(text[e - 1])) e--;
    text = text.slice(s, e);
  }
  if (text.length === 0) return [];
  const set = (chars: string) => '[' + chars.replace(/[\\\]^[-]/g, '\\$&') + ']';
  const wsRx = set(ws), otherRx = set(other);
  const sep = other.length === 0 ? wsRx + '+' : ws.length === 0 ? otherRx : `${wsRx}*${otherRx}${wsRx}*|${wsRx}+`;
  const re = cachedRegex(sep, 'g');
  const fields: string[] = [];
  let last = 0;
  re.lastIndex = 0;
  for (let m = re.exec(text); m; m = re.exec(text)) {
    if (maxFields > 0 && fields.length === maxFields - 1) break;
    fields.push(text.slice(last, m.index));
    last = m.index + m[0].length;
  }
  fields.push(text.slice(last));
  if (maxFields <= 0 && other.length > 0 && fields.length > 0 && fields[fields.length - 1] === '') fields.pop(); // "a,b," is two fields
  return fields;
}

/** The file word of `$(< file)`, or null when the substitution is anything else. */
export function readFileWord(source: string): Word | null {
  if (!source.includes('<')) return null;
  let script: Script;
  try { script = parse(source); } catch { return null; }
  if (script.items.length !== 1) return null;
  const item = script.items[0];
  if (item.rest.length || item.first.negate || item.first.commands.length !== 1) return null;
  const cmd = item.first.commands[0];
  if (cmd.k !== 'simple' || cmd.words.length || cmd.assigns.length || cmd.redirects.length !== 1 || cmd.redirects[0].op !== '<') return null;
  return cmd.redirects[0].target ?? null;
}
