// Parser for the shell language: a hand-written recursive-descent parser that reads characters directly (no separate
// token stream), because how bash splits text into tokens depends on where the parser is ([[ ]], (( )), case, =~).

export class ParseError extends Error {}

/** One piece of a word. `q` is true for quoted text (no splitting or globbing). */
export type WordPart =
  | { k: 'lit'; text: string; q: boolean }
  /** $name or ${expr}: expr is the raw text between the braces, interpreted at expansion time. */
  | { k: 'var'; expr: string; q: boolean }
  | { k: 'cmd'; src: string; q: boolean }
  | { k: 'arith'; expr: string; q: boolean }
  /** <(cmd): replaced by the path of a file holding cmd's output. */
  | { k: 'proc'; src: string; q: true }
  /** Unquoted text that came out of an expansion (the word of ${x:-a b}): split and globbed like one. */
  | { k: 'exp'; text: string; q: false };

export interface Word {
  parts: WordPart[];
  /** Set when the word has the shape of an assignment (name=value, name[i]=value, name=(...)). */
  assign?: Assignment;
}

export interface Assignment {
  name: string;
  value: Word;
  /** The raw subscript of name[sub]=value. */
  index?: string;
  /** The source of name=(...). */
  elements?: string;
  append: boolean;
}

export interface HereDoc { delimiter: string; expand: boolean; strip: boolean; body: Word }

export interface Redirect {
  fd: number;
  /** < > >> >| <> << <<< >& <& &> &>> */
  op: string;
  target?: Word;
  here?: HereDoc;
  /** {name}>file: the shell picks the fd and stores it in this variable. */
  fdVar?: string;
}

export type Command =
  | { k: 'simple'; assigns: Assignment[]; words: Word[]; redirects: Redirect[]; line: number }
  | { k: 'group'; body: Script; subshell: boolean; redirects: Redirect[] }
  | { k: 'if'; branches: [Script, Script][]; else: Script | null; redirects: Redirect[] }
  | { k: 'loop'; cond: Script; body: Script; until: boolean; redirects: Redirect[] }
  | { k: 'for'; name: string; items: Word[] | null; body: Script; select: boolean; redirects: Redirect[] }
  | { k: 'arithfor'; init: string; cond: string; step: string; body: Script; redirects: Redirect[] }
  | { k: 'case'; subject: Word; clauses: CaseClause[]; redirects: Redirect[] }
  | { k: 'func'; name: string; body: Command; source: string; redirects: Redirect[] }
  | { k: 'arith'; expr: string; redirects: Redirect[]; line: number }
  | { k: 'cond'; items: Word[]; redirects: Redirect[]; line: number };

export interface CaseClause { patterns: Word[]; body: Script; terminator: ';;' | ';&' | ';;&' }
export interface Pipeline { commands: Command[]; negate: boolean; timed: boolean; posixTime: boolean }
export interface AndOr { first: Pipeline; rest: [op: '&&' | '||', next: Pipeline][]; background: boolean }
export interface Script { items: AndOr[] }

export const isName = (s: string) => /^[A-Za-z_][A-Za-z0-9_]*$/.test(s);
/** The text of a word made of one unquoted literal, else null. */
export const literal = (w: Word): string | null => (w.parts.length === 1 && w.parts[0].k === 'lit' && !w.parts[0].q ? w.parts[0].text : null);
export const lit = (text: string, q = false): Word => ({ parts: [{ k: 'lit', text, q }] });

const META = ' \t\n;&|<>()';
const KEYWORDS = new Set(['if', 'then', 'elif', 'else', 'fi', 'while', 'until', 'for', 'select', 'do', 'done', 'case', 'esac', 'function', '{', '}', '[[', ']]', '!', 'time', 'in', 'coproc']);
const MAX_DEPTH = 400;

const parseCache = new Map<string, Script>();

/** Parses a script, caching by text (loops and functions re-run the same substitutions). */
export function parse(text: string): Script {
  const hit = parseCache.get(text);
  if (hit) return hit;
  const script = new Parser(text).parseScript();
  if (parseCache.size > 2000) parseCache.clear();
  if (text.length < 10_000) parseCache.set(text, script);
  return script;
}

/** Splits text into words the way the parser reads a simple command's arguments (aliases, array elements). */
export function parseWords(text: string): Word[] {
  const p = new Parser(text);
  const words: Word[] = [];
  for (;;) {
    p.skipBlankAndNewlines();
    if (p.atEnd()) return words;
    const w = p.readWord();
    if (!w) throw new ParseError(`syntax error near unexpected token \`${text[p.pos]}'`);
    words.push(w);
  }
}

/** Lexes text as the inside of double quotes (or a heredoc body when `heredoc`): $ and ` expand, quotes are literal. */
export function parseQuoted(text: string, heredoc = true): Word {
  const p = new Parser(text);
  return { parts: p.readDoubleQuoted(true, heredoc) };
}

/** Lexes the word of ${x op word}: quotes apply, and when the expansion is itself in double quotes, ' is literal. */
export function parseParamWord(text: string, inDq: boolean, pattern = false): Word {
  const p = new Parser(text);
  const parts: WordPart[] = [];
  while (!p.atEnd()) p.readWordPart(parts, { inDq, paramWord: true, pattern });
  return { parts: merge(parts) };
}

function merge(parts: WordPart[]): WordPart[] {
  const out: WordPart[] = [];
  for (const p of parts) {
    const last = out[out.length - 1];
    if (p.k === 'lit' && last?.k === 'lit' && last.q === p.q) out[out.length - 1] = { k: 'lit', text: last.text + p.text, q: p.q };
    else out.push(p);
  }
  return out;
}

/** pattern: a ${x/pat/rep} or ${x%pat} word, where a backslash always quotes the next character. */
interface PartOptions { inDq?: boolean; paramWord?: boolean; regex?: boolean; cond?: boolean; pattern?: boolean }

class Parser {
  pos = 0;
  private depth = 0;
  private pendingHeredocs: HereDoc[] = [];
  private lineStarts: number[] | null = null;

  constructor(private readonly src: string, start = 0) { this.pos = start; }

  atEnd() { return this.pos >= this.src.length; }
  private ch(off = 0) { return this.src[this.pos + off] ?? ''; }
  private startsWith(s: string) { return this.src.startsWith(s, this.pos); }

  private line(pos = this.pos): number {
    if (!this.lineStarts) {
      this.lineStarts = [0];
      for (let i = 0; i < this.src.length; i++) if (this.src[i] === '\n') this.lineStarts.push(i + 1);
    }
    let lo = 0, hi = this.lineStarts.length - 1;
    while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (this.lineStarts[mid] <= pos) lo = mid; else hi = mid - 1; }
    return lo + 1;
  }

  private fail(token?: string): never {
    if (this.atEnd() && token === undefined) throw new ParseError('syntax error: unexpected end of file');
    const t = token ?? (this.ch() === '\n' ? 'newline' : this.peekTokenText());
    throw new ParseError(`syntax error near unexpected token \`${t}'`);
  }

  private peekTokenText(): string {
    for (const op of [';;&', ';;', ';&', '&&', '||', '|&', '>>', '<<', ';', '&', '|', '(', ')', '<', '>']) if (this.startsWith(op)) return op;
    const m = /^[^\s;&|<>()]+/.exec(this.src.slice(this.pos, this.pos + 100));
    return m ? m[0] : this.ch();
  }

  // ---------- whitespace, comments, newlines ----------

  /** Skips spaces, tabs, line continuations and comments (not newlines). */
  skipBlank() {
    for (;;) {
      const c = this.ch();
      if (c === ' ' || c === '\t' || c === '\r') { this.pos++; continue; }
      if (c === '\\' && this.ch(1) === '\n') { this.pos += 2; continue; }
      if (c === '#') { while (!this.atEnd() && this.ch() !== '\n') this.pos++; continue; }
      return;
    }
  }

  private eatNewline() {
    this.pos++;
    if (this.pendingHeredocs.length) this.readHeredocBodies();
  }

  skipBlankAndNewlines() {
    for (;;) {
      this.skipBlank();
      if (this.ch() === '\n') { this.eatNewline(); continue; }
      return;
    }
  }

  /** Skips separators (newlines, and ';' when allowed) between commands. */
  private skipSeparators() { this.skipBlankAndNewlines(); }

  /** The reserved word at the cursor (when followed by a delimiter), else null. Doesn't move. */
  private peekKeyword(): string | null {
    this.skipBlank();
    const m = /^(\[\[|\]\]|[{}!]|[a-z]+)/.exec(this.src.slice(this.pos, this.pos + 10));
    if (!m || !KEYWORDS.has(m[1])) return null;
    const after = this.src[this.pos + m[1].length] ?? '';
    if (after !== '' && !META.includes(after) && !(m[1] === '}' && after === '}')) return null;
    if (m[1] === '!' && after === '(') return null; // extglob or a subshell needs a blank; !(x) is a pattern
    return m[1];
  }

  private expectKeyword(word: string) {
    this.skipSeparators();
    if (this.peekKeyword() !== word) {
      if (this.atEnd()) throw new ParseError(`syntax error: unexpected end of file (expected \`${word}')`);
      this.fail();
    }
    this.pos += word.length;
  }

  // ---------- grammar ----------

  parseScript(): Script {
    const script = this.parseList(new Set());
    this.skipSeparators();
    if (!this.atEnd()) this.fail();
    return script;
  }

  /** A list of and-or items until EOF, one of `stops` (keywords) or a closing token. */
  private parseList(stops: Set<string>, closeParen = false): Script {
    const items: AndOr[] = [];
    for (;;) {
      this.skipSeparators();
      if (this.atEnd()) break;
      const kw = this.peekKeyword();
      if (kw && stops.has(kw)) break;
      const c = this.ch();
      if (c === ')' && closeParen) break;
      if (this.startsWith(';;') || this.startsWith(';&')) break;
      if (c === ')' || c === ';' || c === '&' || c === '|') this.fail();
      const item = this.parseAndOr();
      items.push(item);
      this.skipBlank();
      if (this.startsWith(';;') || this.startsWith(';&')) break;
      if (this.ch() === ';') { this.pos++; continue; }
      if (this.ch() === '&' && this.ch(1) !== '&') { this.pos++; item.background = true; continue; }
      if (this.ch() === '\n') { this.eatNewline(); continue; }
      if (this.atEnd()) break;
      const kw2 = this.peekKeyword();
      if (kw2 && stops.has(kw2)) break;
      if (this.ch() === ')' && closeParen) break;
      this.fail();
    }
    return { items };
  }

  private parseAndOr(): AndOr {
    const first = this.parsePipeline();
    const rest: [('&&' | '||'), Pipeline][] = [];
    for (;;) {
      this.skipBlank();
      const op = this.startsWith('&&') ? '&&' : this.startsWith('||') ? '||' : null;
      if (!op) break;
      this.pos += 2;
      this.skipBlankAndNewlines();
      rest.push([op, this.parsePipeline()]);
    }
    return { first, rest, background: false };
  }

  private parsePipeline(): Pipeline {
    let negate = false, timed = false, posixTime = false;
    for (;;) {
      const kw = this.peekKeyword();
      if (kw === '!') { this.pos++; negate = !negate; continue; }
      if (kw === 'time' && !timed) {
        this.pos += 4;
        timed = true;
        this.skipBlank();
        if (this.startsWith('-p') && (META.includes(this.ch(2)) || this.ch(2) === '')) { this.pos += 2; posixTime = true; }
        continue;
      }
      break;
    }
    const commands: Command[] = [];
    this.skipBlank();
    if (timed && (this.atEnd() || ';\n&|)'.includes(this.ch()))) return { commands, negate, timed, posixTime };
    for (;;) {
      const cmd = this.parseCommand();
      commands.push(cmd);
      this.skipBlank();
      if (this.ch() === '|' && this.ch(1) !== '|') {
        if (this.ch(1) === '&') { this.pos++; cmd.redirects.push({ fd: 2, op: '>&', target: lit('1') }); }
        this.pos++;
        this.skipBlankAndNewlines();
        continue;
      }
      break;
    }
    return { commands, negate, timed, posixTime };
  }

  private parseCommand(): Command {
    if (++this.depth > MAX_DEPTH) throw new ParseError('command is nested too deeply');
    try {
      const cmd = this.parseCommandInner();
      if (cmd.k !== 'simple' && cmd.k !== 'func') this.parseRedirects(cmd.redirects);
      return cmd;
    } finally { this.depth--; }
  }

  private parseCommandInner(): Command {
    this.skipBlank();
    if (this.atEnd()) this.fail();
    if (this.startsWith('((')) {
      const arith = this.tryArithCommand();
      if (arith !== null) return { k: 'arith', expr: arith, redirects: [], line: this.line() };
    }
    if (this.ch() === '(') {
      this.pos++;
      const body = this.parseList(new Set(), true);
      this.skipSeparators();
      if (this.ch() !== ')') { if (this.atEnd()) throw new ParseError("unexpected EOF while looking for matching `)'"); this.fail(); }
      this.pos++;
      return { k: 'group', body, subshell: true, redirects: [] };
    }
    const kw = this.peekKeyword();
    switch (kw) {
      case '{': {
        this.pos++;
        const body = this.parseList(new Set(['}']));
        this.expectKeyword('}');
        return { k: 'group', body, subshell: false, redirects: [] };
      }
      case 'if': return this.parseIf();
      case 'while': case 'until': {
        this.pos += kw.length;
        const cond = this.parseList(new Set(['do']));
        this.expectKeyword('do');
        const body = this.parseList(new Set(['done']));
        this.expectKeyword('done');
        return { k: 'loop', cond, body, until: kw === 'until', redirects: [] };
      }
      case 'for': case 'select': return this.parseFor(kw === 'select');
      case 'case': return this.parseCase();
      case 'function': {
        this.pos += 8;
        this.skipBlank();
        const m = /^[^\s;&|<>()]+/.exec(this.src.slice(this.pos));
        if (!m) this.fail();
        this.pos += m[0].length;
        this.skipBlank();
        if (this.startsWith('()')) this.pos += 2;
        else if (this.ch() === '(') { this.pos++; this.skipBlank(); if (this.ch() !== ')') this.fail(); this.pos++; }
        return this.functionBody(m[0]);
      }
      case '[[': return this.parseCond();
      case 'coproc': throw new ParseError("'coproc' is not supported by this shell");
      case 'then': case 'elif': case 'else': case 'fi': case 'do': case 'done': case 'esac': case '}': case 'in': case ']]':
        this.fail(kw);
    }
    return this.parseSimple();
  }

  private functionBody(name: string): Command {
    this.skipBlankAndNewlines();
    const start = this.pos;
    const body = this.parseCommand();
    return { k: 'func', name, body, source: this.src.slice(start, this.pos).trim(), redirects: [] };
  }

  /** `(( expr ))` at the cursor: the expression, or null when the parens close separately (`( (cmd) )`). */
  private tryArithCommand(): string | null {
    const end = this.matchArith(this.pos + 2);
    if (end < 0) return null;
    const expr = this.src.slice(this.pos + 2, end);
    this.pos = end + 2;
    return expr;
  }

  /** Index of the `))` closing an arithmetic expression whose text starts at i, or -1. */
  private matchArith(i: number): number {
    let depth = 0;
    for (; i < this.src.length; i++) {
      const c = this.src[i];
      if (c === '\\') { i++; continue; }
      if (c === "'" || c === '"') { const e = this.src.indexOf(c, i + 1); if (e < 0) return -1; i = e; continue; }
      if (c === '(') depth++;
      else if (c === ')') {
        if (depth === 0) return this.src[i + 1] === ')' ? i : -1;
        depth--;
      }
    }
    return -1;
  }

  private parseIf(): Command {
    this.pos += 2;
    const branches: [Script, Script][] = [];
    let elseBody: Script | null = null;
    for (;;) {
      const cond = this.parseList(new Set(['then']));
      this.expectKeyword('then');
      const body = this.parseList(new Set(['elif', 'else', 'fi']));
      branches.push([cond, body]);
      this.skipSeparators();
      const kw = this.peekKeyword();
      if (kw === 'elif') { this.pos += 4; continue; }
      if (kw === 'else') { this.pos += 4; elseBody = this.parseList(new Set(['fi'])); }
      this.expectKeyword('fi');
      return { k: 'if', branches, else: elseBody, redirects: [] };
    }
  }

  private parseFor(select: boolean): Command {
    this.pos += select ? 6 : 3;
    this.skipBlank();
    if (!select && this.startsWith('((')) {
      const end = this.matchArith(this.pos + 2);
      if (end < 0) throw new ParseError('syntax error: expected `for (( init; cond; step ))\'');
      const parts = splitTopLevel(this.src.slice(this.pos + 2, end), ';');
      if (parts.length !== 3) throw new ParseError('syntax error: expected `for (( init; cond; step ))\'');
      this.pos = end + 2;
      this.skipBlank();
      if (this.ch() === ';') this.pos++;
      const body = this.doGroup();
      return { k: 'arithfor', init: parts[0].trim(), cond: parts[1].trim(), step: parts[2].trim(), body, redirects: [] };
    }
    const nameWord = this.readWord();
    const name = nameWord && literal(nameWord);
    if (!name || !isName(name)) this.fail(name ?? undefined);
    let items: Word[] | null = null;
    this.skipBlankAndNewlines();
    if (this.peekKeyword() === 'in') {
      this.pos += 2;
      items = [];
      for (;;) {
        this.skipBlank();
        if (this.ch() === ';' || this.ch() === '\n' || this.atEnd()) break;
        const w = this.readWord();
        if (!w) this.fail();
        items.push(w);
      }
    }
    this.skipBlank();
    if (this.ch() === ';') this.pos++;
    const body = this.doGroup();
    return { k: 'for', name, items, body, select, redirects: [] };
  }

  private doGroup(): Script {
    this.skipSeparators();
    if (this.peekKeyword() === '{') { // for x in a; { ...; } is accepted by bash
      this.pos++;
      const body = this.parseList(new Set(['}']));
      this.expectKeyword('}');
      return body;
    }
    this.expectKeyword('do');
    const body = this.parseList(new Set(['done']));
    this.expectKeyword('done');
    return body;
  }

  private parseCase(): Command {
    this.pos += 4;
    this.skipBlank();
    const subject = this.readWord();
    if (!subject) this.fail();
    this.skipBlankAndNewlines();
    if (this.peekKeyword() !== 'in') this.fail();
    this.pos += 2;
    const clauses: CaseClause[] = [];
    for (;;) {
      this.skipSeparators();
      if (this.atEnd()) throw new ParseError('syntax error: unexpected end of file (expected `esac\')');
      if (this.peekKeyword() === 'esac') { this.pos += 4; break; }
      if (this.ch() === '(') this.pos++;
      const patterns: Word[] = [];
      for (;;) {
        this.skipBlank();
        const w = this.readWord({ cond: false });
        if (!w) this.fail();
        patterns.push(w);
        this.skipBlank();
        if (this.ch() === '|') { this.pos++; continue; }
        if (this.ch() === ')') { this.pos++; break; }
        this.fail();
      }
      const body = this.parseList(new Set(['esac']));
      this.skipBlank();
      let terminator: CaseClause['terminator'] = ';;';
      if (this.startsWith(';;&')) { terminator = ';;&'; this.pos += 3; }
      else if (this.startsWith(';;')) this.pos += 2;
      else if (this.startsWith(';&')) { terminator = ';&'; this.pos += 2; }
      else {
        this.skipSeparators();
        if (this.peekKeyword() !== 'esac') this.fail();
      }
      clauses.push({ patterns, body, terminator });
    }
    return { k: 'case', subject, clauses, redirects: [] };
  }

  private parseCond(): Command {
    const line = this.line();
    this.pos += 2;
    const items: Word[] = [];
    let afterRegexOp = false;
    for (;;) {
      this.skipBlankAndNewlines();
      if (this.atEnd()) throw new ParseError("syntax error: unexpected end of file (expected `]]')");
      if (this.startsWith(']]') && (META.includes(this.ch(2)) || this.ch(2) === '')) { this.pos += 2; break; }
      let w: Word | null = null;
      if (!afterRegexOp) {
        for (const op of ['&&', '||', '(', ')', '<', '>', '!']) {
          if (this.startsWith(op) && !(op === '!' && !' \t\n'.includes(this.ch(1)))) { w = lit(op); this.pos += op.length; break; }
        }
      }
      if (!w) w = this.readWord({ regex: afterRegexOp, cond: true });
      if (!w) this.fail();
      items.push(w);
      afterRegexOp = literal(w) === '=~';
    }
    return { k: 'cond', items, redirects: [], line };
  }

  private parseSimple(): Command {
    const line = this.line();
    const assigns: Assignment[] = [];
    const words: Word[] = [];
    const redirects: Redirect[] = [];
    for (;;) {
      this.skipBlank();
      if (this.atEnd()) break;
      if (this.tryRedirect(redirects)) continue;
      const c = this.ch();
      if (c === '\n' || c === ';' || c === '&' || c === '|' || c === ')') break;
      if (c === '(') {
        if (words.length === 1 && assigns.length === 0 && literal(words[0]) !== null) {
          // name () compound-command
          this.pos++;
          this.skipBlank();
          if (this.ch() !== ')') this.fail();
          this.pos++;
          return this.functionBody(literal(words[0])!);
        }
        if (words.length === 0 && assigns.length === 0) this.fail();
        this.fail();
      }
      const w = this.readWord();
      if (!w) this.fail();
      if (words.length === 0 && w.assign) { assigns.push(w.assign); continue; }
      words.push(w);
    }
    if (words.length === 0 && assigns.length === 0 && redirects.length === 0) this.fail();
    return { k: 'simple', assigns, words, redirects, line };
  }

  private parseRedirects(into: Redirect[]) {
    for (;;) {
      this.skipBlank();
      if (!this.tryRedirect(into)) return;
    }
  }

  private static readonly REDIRECT = /^(\{[A-Za-z_][A-Za-z0-9_]*\}|\d+)?(&>>|&>|<<<|<<-|<<|<>|>>|>\||>&|<&|>|<)/;

  private tryRedirect(into: Redirect[]): boolean {
    const m = Parser.REDIRECT.exec(this.src.slice(this.pos, this.pos + 40));
    if (!m) return false;
    const op = m[2];
    if ((op === '<' || op === '>') && this.src[this.pos + m[0].length] === '(' && !m[1]) {
      if (op === '>') throw new ParseError('process substitution >(...) is not supported by this shell (write to a file, then read it)');
      return false; // <(cmd) is a word
    }
    let fd = op[0] === '<' ? 0 : 1;
    let fdVar: string | undefined;
    if (m[1]?.startsWith('{')) fdVar = m[1].slice(1, -1);
    else if (m[1]) fd = Number(m[1]);
    this.pos += m[0].length;
    this.skipBlank();
    if (op === '<<' || op === '<<-') {
      const start = this.pos;
      const w = this.readWord();
      if (!w) this.fail(this.atEnd() ? 'newline' : undefined);
      const raw = this.src.slice(start, this.pos);
      const quoted = /['"\\]/.test(raw);
      const delimiter = w.parts.map(p => (p.k === 'lit' ? p.text : p.k === 'var' ? '$' + p.expr : '')).join('');
      const here: HereDoc = { delimiter, expand: !quoted, strip: op === '<<-', body: lit('', true) };
      this.pendingHeredocs.push(here);
      into.push({ fd, op: '<<', here, fdVar });
      return true;
    }
    const target = this.readWord();
    if (!target) this.fail(this.atEnd() || this.ch() === '\n' ? 'newline' : undefined);
    into.push({ fd, op: op === '<>' ? '<' : op, target, fdVar });
    return true;
  }

  private readHeredocBodies() {
    const docs = this.pendingHeredocs;
    this.pendingHeredocs = [];
    for (const doc of docs) {
      const lines: string[] = [];
      for (;;) {
        if (this.atEnd()) break;
        let nl = this.src.indexOf('\n', this.pos);
        if (nl < 0) nl = this.src.length;
        let text = this.src.slice(this.pos, nl);
        this.pos = Math.min(nl + 1, this.src.length);
        if (doc.strip) text = text.replace(/^\t+/, '');
        if (text === doc.delimiter) break;
        lines.push(text + '\n');
      }
      const body = lines.join('');
      doc.body = doc.expand ? { parts: new Parser(body).readDoubleQuoted(true, true) } : lit(body, true);
    }
  }

  // ---------- words ----------

  /** Reads one word at the cursor (null if none starts here). Recognizes assignments (name=, name[i]=, name+=, name=(...)). */
  readWord(opts: PartOptions = {}): Word | null {
    const start = this.pos;
    const parts: WordPart[] = [];
    let assign: Assignment | undefined;
    if (!opts.regex && !opts.cond) {
      const m = /^([A-Za-z_][A-Za-z0-9_]*)(\[)?/.exec(this.src.slice(this.pos, this.pos + 256));
      if (m) {
        let i = this.pos + m[1].length;
        let index: string | undefined;
        if (m[2]) {
          const close = matchBracket(this.src, i);
          if (close > 0) { index = this.src.slice(i + 1, close); i = close + 1; } else i = -1;
        }
        if (i > 0) {
          const append = this.src[i] === '+' && this.src[i + 1] === '=';
          if (append || this.src[i] === '=') {
            const prefix = this.src.slice(this.pos, i + (append ? 2 : 1));
            this.pos = i + (append ? 2 : 1);
            let elements: string | undefined;
            const valueParts: WordPart[] = [];
            if (this.ch() === '(' && index === undefined) {
              const end = this.matchParen(this.pos);
              elements = this.src.slice(this.pos + 1, end);
              this.pos = end + 1;
            } else {
              while (!this.atEnd() && !META.includes(this.ch())) this.readWordPart(valueParts, opts);
            }
            const value = { parts: merge(valueParts) };
            assign = { name: m[1], value, index, elements, append };
            parts.push({ k: 'lit', text: prefix, q: false });
            if (elements !== undefined) parts.push({ k: 'lit', text: `(${elements})`, q: false });
            else parts.push(...value.parts);
          }
        }
      }
    }
    if (!assign) {
      while (!this.atEnd()) {
        const c = this.ch();
        if (opts.regex) {
          if (c === ' ' || c === '\t' || c === '\n') break;
          if (c === '(' || c === ')' || c === '|' || c === '<' || c === '>' || c === '&' || c === ';') {
            if ((c === '&' && this.ch(1) === '&') || (c === '|' && this.ch(1) === '|')) break;
            parts.push({ k: 'lit', text: c, q: false }); this.pos++; continue;
          }
        } else if (META.includes(c)) {
          if ((c === '<' || c === '>') && this.ch(1) === '(' && this.pos === start) {
            if (c === '>') throw new ParseError('process substitution >(...) is not supported by this shell (write to a file, then read it)');
            const end = this.matchParen(this.pos + 1);
            parts.push({ k: 'proc', src: this.src.slice(this.pos + 2, end), q: true });
            this.pos = end + 1;
            continue;
          }
          if (c === '(' && parts.length > 0) {
            // extglob: @(a|b) *(x) +(x) ?(x) !(x), and name+=( handled above
            const last = parts[parts.length - 1];
            if (last.k === 'lit' && !last.q && /[@*+?!]$/.test(last.text)) {
              const end = this.matchParen(this.pos);
              parts.push({ k: 'lit', text: this.src.slice(this.pos, end + 1), q: false });
              this.pos = end + 1;
              continue;
            }
          }
          break;
        }
        if (c === '#' && this.pos === start) break; // a comment
        this.readWordPart(parts, opts);
      }
    }
    if (this.pos === start) {
      // extglob at the very start of a word: !(x) @(x)
      if (/[@*+?!]/.test(this.ch()) && this.ch(1) === '(') {
        const end = this.matchParen(this.pos + 1);
        parts.push({ k: 'lit', text: this.src.slice(this.pos, end + 1), q: false });
        this.pos = end + 1;
        const rest = this.readWord(opts);
        if (rest) parts.push(...rest.parts);
        return { parts: merge(parts) };
      }
      return null;
    }
    if (parts.length === 0) parts.push({ k: 'lit', text: '', q: true }); // "" or a removed continuation
    return { parts: merge(parts), assign };
  }

  /** Reads one piece of a word (quotes, an expansion, an escape or plain text) and appends it. */
  readWordPart(parts: WordPart[], opts: PartOptions) {
    const c = this.ch();
    if (c === '\\') {
      if (this.ch(1) === '\n') { this.pos += 2; return; }
      if (this.pos + 1 >= this.src.length) { parts.push({ k: 'lit', text: '\\', q: false }); this.pos++; return; }
      if (opts.paramWord && opts.inDq && !opts.pattern) {
        // inside "${x:-...}" a backslash escapes only $ ` " \ and }
        const n = this.ch(1);
        parts.push({ k: 'lit', text: '$`"\\}'.includes(n) ? n : '\\' + n, q: true });
        this.pos += 2;
        return;
      }
      parts.push({ k: 'lit', text: this.ch(1), q: true });
      this.pos += 2;
      return;
    }
    if (c === "'" && !opts.inDq) {
      const end = this.src.indexOf("'", this.pos + 1);
      if (end < 0) throw new ParseError("unexpected EOF while looking for matching `''");
      parts.push({ k: 'lit', text: this.src.slice(this.pos + 1, end), q: true });
      this.pos = end + 1;
      return;
    }
    if (c === '"') {
      this.pos++;
      const inner = this.readDoubleQuoted(false, false);
      parts.push(...(inner.length ? inner : [{ k: 'lit', text: '', q: true } as WordPart]));
      return;
    }
    if (c === '$' && this.ch(1) === "'" && (!opts.inDq || opts.paramWord)) {
      const end = findAnsiEnd(this.src, this.pos + 2);
      if (end < 0) throw new ParseError("unexpected EOF while looking for matching `''");
      parts.push({ k: 'lit', text: ansiC(this.src.slice(this.pos + 2, end)), q: true });
      this.pos = end + 1;
      return;
    }
    if (c === '$' && this.ch(1) === '"') {
      this.pos += 2;
      const inner = this.readDoubleQuoted(false, false);
      parts.push(...(inner.length ? inner : [{ k: 'lit', text: '', q: true } as WordPart]));
      return;
    }
    if (c === '$' || c === '`') {
      const p = this.readDollar(false);
      if (p) { parts.push(p); return; }
    }
    // plain text up to the next special character
    let end = this.pos + 1;
    const stop = opts.regex ? ' \t\n\\\'"$`' : opts.paramWord ? '\\\'"$`' : ' \t\n;&|<>()\\\'"$`';
    while (end < this.src.length && !stop.includes(this.src[end])) {
      if (!opts.regex && !opts.paramWord && '@*+?!'.includes(this.src[end]) && this.src[end + 1] === '(') { end++; break; }
      end++;
    }
    parts.push({ k: 'lit', text: this.src.slice(this.pos, end), q: false });
    this.pos = end;
  }

  /**
   * Reads the inside of "..." up to the closing quote (or to the end when `toEnd`). In a heredoc body (`heredoc`)
   * double quotes are ordinary characters.
   */
  readDoubleQuoted(toEnd: boolean, heredoc: boolean): WordPart[] {
    const parts: WordPart[] = [];
    let text = '';
    const flush = () => { if (text) { parts.push({ k: 'lit', text, q: true }); text = ''; } };
    for (;;) {
      if (this.atEnd()) {
        if (toEnd) break;
        throw new ParseError('unexpected EOF while looking for matching `"\'');
      }
      const c = this.ch();
      if (c === '"' && !heredoc) { this.pos++; break; }
      if (c === '\\') {
        const n = this.ch(1);
        if (n === '\n') { this.pos += 2; continue; }
        if (n === '$' || n === '`' || n === '\\' || (n === '"' && !heredoc)) { text += n; this.pos += 2; continue; }
        text += c; this.pos++;
        continue;
      }
      if (c === '$' || c === '`') {
        const p = this.readDollar(true);
        if (p) { flush(); parts.push(p); continue; }
      }
      text += c;
      this.pos++;
    }
    flush();
    return parts;
  }

  /** $name, ${...}, $(...), $((...)), $[...] or `...` at the cursor; null for a lone $. */
  private readDollar(q: boolean): WordPart | null {
    const c = this.ch();
    if (c === '`') {
      let i = this.pos + 1, src = '';
      for (; i < this.src.length && this.src[i] !== '`'; i++) {
        if (this.src[i] === '\\' && '`$\\'.includes(this.src[i + 1] ?? '')) { src += this.src[++i]; continue; }
        src += this.src[i];
      }
      if (i >= this.src.length) throw new ParseError("unexpected EOF while looking for matching ``'");
      this.pos = i + 1;
      return { k: 'cmd', src, q };
    }
    const n = this.ch(1);
    if (n === '(') {
      if (this.ch(2) === '(') {
        const end = this.matchArith(this.pos + 3);
        if (end >= 0) {
          const expr = this.src.slice(this.pos + 3, end);
          this.pos = end + 2;
          return { k: 'arith', expr, q };
        }
      }
      const end = this.matchParen(this.pos + 1);
      const src = this.src.slice(this.pos + 2, end);
      this.pos = end + 1;
      return { k: 'cmd', src, q };
    }
    if (n === '[') {
      const end = matchBracket(this.src, this.pos + 1);
      if (end < 0) throw new ParseError("unexpected EOF while looking for matching `]'");
      const expr = this.src.slice(this.pos + 2, end);
      this.pos = end + 1;
      return { k: 'arith', expr, q };
    }
    if (n === '{') {
      const end = this.matchBrace(this.pos + 2, q);
      const expr = this.src.slice(this.pos + 2, end);
      this.pos = end + 1;
      return { k: 'var', expr, q };
    }
    const m = /^([A-Za-z_][A-Za-z0-9_]*|[0-9]|[@*#?$!\-])/.exec(this.src.slice(this.pos + 1, this.pos + 200));
    if (!m) return null;
    this.pos += 1 + m[1].length;
    return { k: 'var', expr: m[1], q };
  }

  /** Index of the `}` closing a ${ whose body starts at i. */
  private matchBrace(i: number, inDq: boolean): number {
    let depth = 1;
    for (; i < this.src.length; i++) {
      const c = this.src[i];
      if (c === '\\') { i++; continue; }
      if (c === "'" && !inDq) { const e = this.src.indexOf("'", i + 1); if (e < 0) break; i = e; continue; }
      if (c === '"') {
        for (i++; i < this.src.length && this.src[i] !== '"'; i++) {
          if (this.src[i] === '\\') i++;
          else if (this.src[i] === '$' && this.src[i + 1] === '(') i = this.matchParen(i + 1);
        }
        continue;
      }
      if (c === '$' && this.src[i + 1] === '(') { i = this.matchParen(i + 1); continue; }
      if (c === '$' && this.src[i + 1] === '{') { depth++; i++; continue; }
      if (c === '`') { const e = this.src.indexOf('`', i + 1); if (e < 0) break; i = e; continue; }
      if (c === '}' && --depth === 0) return i;
    }
    throw new ParseError("unexpected EOF while looking for matching `}'");
  }

  /** Index of the `)` matching the `(` at i, parsing the inside as shell code so quotes, case and comments count. */
  private matchParen(i: number): number {
    const sub = new Parser(this.src, i + 1);
    sub.depth = this.depth + 1;
    if (sub.depth > MAX_DEPTH) throw new ParseError('command is nested too deeply');
    // extglob groups and array literals are plain text with balanced parens
    const before = this.src[i - 1] ?? '';
    if (/[@*+?!=]/.test(before) && this.src[i - 2] !== '$') return matchPlainParen(this.src, i);
    sub.parseList(new Set(), true);
    sub.skipSeparators();
    if (sub.ch() !== ')') {
      if (sub.atEnd()) throw new ParseError("unexpected EOF while looking for matching `)'");
      sub.fail();
    }
    return sub.pos;
  }
}

/** The `]` matching the `[` at i (nested brackets and quotes allowed), or -1. */
function matchBracket(s: string, i: number): number {
  let depth = 0;
  for (; i < s.length; i++) {
    const c = s[i];
    if (c === '\\') { i++; continue; }
    if (c === "'" || c === '"') { const e = s.indexOf(c, i + 1); if (e < 0) return -1; i = e; continue; }
    if (c === '[') depth++;
    else if (c === ']' && --depth === 0) return i;
    else if (c === '\n') return -1;
  }
  return -1;
}

function matchPlainParen(s: string, i: number): number {
  let depth = 0;
  for (; i < s.length; i++) {
    const c = s[i];
    if (c === '\\') { i++; continue; }
    if (c === "'" || c === '"') { const e = s.indexOf(c, i + 1); if (e < 0) break; i = e; continue; }
    if (c === '(') depth++;
    else if (c === ')' && --depth === 0) return i;
  }
  throw new ParseError("unexpected EOF while looking for matching `)'");
}

function splitTopLevel(s: string, sep: string): string[] {
  const out: string[] = [];
  let depth = 0, start = 0;
  for (let i = 0; i < s.length; i++) {
    if (s[i] === '(') depth++;
    else if (s[i] === ')') depth--;
    else if (s[i] === sep && depth === 0) { out.push(s.slice(start, i)); start = i + 1; }
  }
  out.push(s.slice(start));
  return out;
}

function findAnsiEnd(s: string, i: number): number {
  for (; i < s.length; i++) {
    if (s[i] === '\\') { i++; continue; }
    if (s[i] === "'") return i;
  }
  return -1;
}

/** Decodes the escapes of $'...'. */
export function ansiC(s: string): string {
  let out = '';
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c !== '\\' || i + 1 >= s.length) { out += c; continue; }
    const n = s[++i];
    const simple: Record<string, string> = { n: '\n', t: '\t', r: '\r', a: '\x07', b: '\b', e: '\x1b', E: '\x1b', f: '\f', v: '\v', '\\': '\\', "'": "'", '"': '"', '?': '?' };
    if (n in simple) { out += simple[n]; continue; }
    if (n >= '0' && n <= '7') {
      let j = i; let v = '';
      while (j < s.length && v.length < 3 && s[j] >= '0' && s[j] <= '7') v += s[j++];
      const code = parseInt(v, 8) & 0xff;
      if (code === 0) return out; // a NUL ends the string, as in C
      out += code >= 0x80 ? String.fromCharCode(0xdc00 + code) : String.fromCharCode(code);
      i = j - 1;
      continue;
    }
    if (n === 'x' || n === 'u' || n === 'U') {
      const max = n === 'x' ? 2 : n === 'u' ? 4 : 8;
      const m = new RegExp(`^[0-9a-fA-F]{1,${max}}`).exec(s.slice(i + 1));
      if (!m) { out += '\\' + n; continue; }
      const code = parseInt(m[0], 16);
      if (code === 0) return out;
      out += n === 'x' && code >= 0x80 ? String.fromCharCode(0xdc00 + code) : String.fromCodePoint(code);
      i += m[0].length;
      continue;
    }
    if (n === 'c' && i + 1 < s.length) { out += String.fromCharCode(s[++i].toUpperCase().charCodeAt(0) & 0x1f); continue; }
    out += '\\' + n;
  }
  return out;
}
