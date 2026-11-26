import type { Shell } from '../shell.js';
import { ShellError, cannotRead, specFormat, specMessage, specOf, splitLines, reason, tooLarge, type CommandContext } from '../command.js';
import { GETOPT } from '../spec.generated.js';
import { CLASS_SYNTAX, posixRegexError, posixToJs } from '../regex.js';

type Addr =
  | { k: 'line'; n: number } | { k: 'last' } | { k: 're'; re: RegExp } | { k: 'step'; first: number; step: number }
  | { k: 'zero' } | { k: 'plus'; n: number } | { k: 'mult'; n: number };

type ReplPart = { k: 'lit'; text: string } | { k: 'group'; n: number } | { k: 'case'; op: string };

interface Cmd {
  a1?: Addr; a2?: Addr;
  negate: boolean; inRange: boolean; rangeEnd: number;
  op: string;
  re?: RegExp;
  repl: ReplPart[];
  global: boolean; print: boolean; nth: number;
  wfile?: string; // s///w, or the w command's file
  text: string; label: string;
  target: number; // jump target for b/t/T and for skipping a { block
  code: number;
  table: Map<string, string>;
}

const STEP_LIMIT = 10_000_000;
const fail = (msg: string) => new ShellError(msg, 1);

// spec/commands/sed.json; also printed after a usage error
const USAGE = specOf('sed').help!;

/** Parses a sed script into a flat command list; { } blocks and b/t/T labels become jump targets.
 * `parts` are each -e expression and -f file, with where GNU says an error is ("-e expression #2", "file x.sed"). */
function parse(parts: { text: string; where: string }[], extended: boolean): Cmd[] {
  const script = parts.map(p => p.text).join('\n');
  const starts = [0];
  for (let k = 1; k < parts.length; k++) starts.push(starts[k - 1] + parts[k - 1].text.length + 1);
  const cmds: Cmd[] = [];
  const labels = new Map<string, number>();
  const blocks: { cmd: number; at: number }[] = [];
  let lastRe: RegExp | undefined;
  let i = 0;
  const n = script.length;

  // GNU's place for an error: how many characters of the expression were read (or the line of the file) at pos
  const err = (message: string, pos: number, charZero = false) => {
    let k = 0;
    for (let q = 1; q < starts.length; q++) if (starts[q] < pos) k = q;
    pos = Math.min(pos, starts[k] + parts[k].text.length);
    const { where } = parts[k];
    const at = where.startsWith('file ')
      ? ` line ${1 + script.slice(starts[k], pos).split('\n').length - 1}`
      : `, char ${charZero ? 0 : pos - starts[k]}`;
    return fail(`${where}${at}: ${message}`);
  };

  const skipBlanks = () => { while (i < n && (script[i] === ' ' || script[i] === '\t')) i++; };
  const isDigit = (c: string | undefined) => c !== undefined && c >= '0' && c <= '9';

  // what: "regex" (of s), "addr" (an address regex), "replacement" or "y"; a regex's [brackets] may hold the delimiter
  function readDelimited(delim: string, what: 'regex' | 'addr' | 'replacement' | 'y'): string {
    const regex = what === 'regex' || what === 'addr';
    let inBracket = false;
    let sb = '';
    while (i < n && (script[i] !== delim || inBracket)) {
      const ch = script[i];
      if (ch === '\\' && i + 1 < n) {
        if (script[i + 1] === delim) sb += delim;
        else if (inBracket) { sb += ch; i++; continue; } // a backslash is literal in brackets
        else sb += script.slice(i, i + 2);
        i += 2;
        continue;
      }
      if (ch === '\n') break; // a newline in the replacement needs a backslash before it
      if (regex && !inBracket && ch === '[') {
        sb += ch;
        i++;
        if (i < n && script[i] === '^') sb += script[i++];
        if (i < n && script[i] === ']') sb += script[i++];
        inBracket = true;
        continue;
      }
      if (inBracket && ch === '[' && i + 1 < n && ':.='.includes(script[i + 1])) {
        const close = script.indexOf(script[i + 1] + ']', i + 2);
        if (close >= 0 && !script.slice(i, close).includes('\n')) {
          sb += script.slice(i, close + 2);
          i = close + 2;
          continue;
        }
      }
      if (ch === ']') inBracket = false;
      sb += ch;
      i++;
    }
    if (i >= n || script[i] !== delim)
      throw err(what === 'addr' ? specMessage('sed', 'unterminatedAddress') : what === 'y' ? specMessage('sed', 'unterminatedY') : specMessage('sed', 'unterminatedS'), i);
    i++;
    return sb;
  }

  function compile(text: string, flags: string, errorAt: number): RegExp {
    if (text.length === 0) { if (!lastRe) throw err(specMessage('sed', 'noPreviousRegex'), errorAt); return lastRe; }
    const error = posixRegexError(text, extended, false);
    if (error !== null) throw error === CLASS_SYNTAX ? new ShellError(error, 4) : err(error, errorAt);
    try { return lastRe = new RegExp(posixToJs(text, extended), 'gs' + flags); } catch (e) {
      throw err(specMessage('sed', 'invalidRegex', { detail: (e as Error).message.replace(/^Invalid regular expression: /, '') }), errorAt);
    }
  }

  function readNumber(): number {
    const start = i;
    while (i < n && isDigit(script[i])) i++;
    return parseInt(script.slice(start, i), 10);
  }

  function readAddr(second: boolean): Addr | undefined {
    if (i >= n) return undefined;
    const ch = script[i];
    if (second && (ch === '+' || ch === '~') && isDigit(script[i + 1])) { i++; return { k: ch === '+' ? 'plus' : 'mult', n: readNumber() }; }
    if (isDigit(ch)) {
      const num = readNumber();
      if (!second && script[i] === '~') { i++; return { k: 'step', first: num, step: isDigit(script[i]) ? readNumber() : 0 }; }
      return num === 0 && !second ? { k: 'zero' } : { k: 'line', n: num };
    }
    if (ch === '$') { i++; return { k: 'last' }; }
    if (ch === '/' || (ch === '\\' && i + 1 < n)) {
      const delim = ch === '/' ? '/' : script[i + 1];
      i += ch === '/' ? 1 : 2;
      const text = readDelimited(delim, 'addr');
      let flags = '';
      while (i < n && (script[i] === 'I' || script[i] === 'M')) flags += script[i++] === 'I' ? 'i' : 'm';
      skipBlanks();
      return { k: 're', re: compile(text, flags, i) };
    }
    return undefined;
  }

  function readLabel(): string {
    skipBlanks();
    const start = i;
    while (i < n && !';\n} \t'.includes(script[i])) i++; // `:x p` is the label x, then p
    return script.slice(start, i);
  }

  function readToEol(): string {
    skipBlanks();
    const start = i;
    while (i < n && script[i] !== '\n') i++;
    return script.slice(start, i);
  }

  // text for a/i/c: `a text` or `a\` NEWLINE text, where a trailing backslash continues the text
  function readText(): string {
    skipBlanks();
    if (i >= n) throw err(specMessage('sed', 'expectedBackslash'), i);
    if (script[i] === '\\') {
      i++;
      if (script[i] === '\n') i++;
    }
    let sb = '';
    while (i < n && script[i] !== '\n') {
      if (script[i] === '\\' && i + 1 < n) { sb += script[i + 1]; i += 2; continue; }
      sb += script[i++];
    }
    return sb;
  }

  const unescapeY = (s: string) => s.replace(/\\(.)/gs, (_, c: string) => (c === 'n' ? '\n' : c === 't' ? '\t' : c));

  for (;;) {
    while (i < n && (/\s/.test(script[i]) || script[i] === ';')) i++;
    if (i >= n) break;
    if (script[i] === '#') { while (i < n && script[i] !== '\n') i++; continue; }
    const c: Cmd = { negate: false, inRange: false, rangeEnd: 0, op: '', repl: [], global: false, print: false, nth: 1, text: '', label: '', target: 0, code: 0, table: new Map() };
    c.a1 = readAddr(false);
    if (c.a1 && script[i] === ',') {
      i++;
      skipBlanks();
      c.a2 = readAddr(true);
      if (!c.a2) throw err(specMessage('sed', 'unexpectedComma'), Math.min(i + 1, n));
    }
    if (c.a1?.k === 'zero' && c.a2?.k !== 're') throw err(specMessage('sed', 'lineZero'), Math.min(i + 1, n));
    skipBlanks();
    if (script[i] === '!') {
      c.negate = true;
      i++;
      skipBlanks();
      if (script[i] === '!') throw err(specMessage('sed', 'multipleBang'), i + 1);
    }
    if (i >= n) throw err(specMessage('sed', 'missingCommand'), i);
    const op = c.op = script[i++];
    switch (op) {
      case '{': blocks.push({ cmd: cmds.length, at: i }); break;
      case '}':
        if (blocks.length === 0) throw err(specMessage('sed', 'unexpectedBrace'), i);
        if (c.a1) throw err(specMessage('sed', 'braceAddresses'), i);
        cmds[blocks.pop()!.cmd].target = cmds.length + 1;
        break;
      case 's': {
        if (i >= n) throw err(specMessage('sed', 'unterminatedS'), i);
        const delim = script[i++];
        const find = readDelimited(delim, 'regex');
        c.repl = parseReplacement(readDelimited(delim, 'replacement'));
        let flags = '';
        let numbered = false;
        while (i < n) {
          const f = script[i];
          if (isDigit(f)) {
            c.nth = readNumber();
            if (numbered) throw err(specMessage('sed', 'multipleNumbers'), i);
            if (c.nth === 0) throw err(specMessage('sed', 'zeroNumber'), i);
            numbered = true;
            continue;
          }
          if (!'gpiImMew'.includes(f)) break;
          i++;
          if (f === 'g') { if (c.global) throw err(specMessage('sed', 'multipleG'), i); c.global = true; }
          else if (f === 'p') { if (c.print) throw err(specMessage('sed', 'multipleP'), i); c.print = true; }
          else if (f === 'i' || f === 'I') flags += 'i';
          else if (f === 'm' || f === 'M') flags += 'm';
          else if (f === 'w') {
            c.wfile = readToEol();
            if (c.wfile.trim().length === 0) throw err(specMessage('sed', 'missingFilename'), i);
            break;
          } else throw err(specMessage('sed', 'flagNotSupported', { flag: f }), i);
        }
        skipBlanks();
        if (i < n && !';\n}#'.includes(script[i])) throw err(specMessage('sed', 'unknownSOption'), i + 1);
        // GNU compiles the regex once it has read the character that ends the command
        const compiledAt = i < n && (script[i] === ';' || script[i] === '\n') ? i + 1 : i;
        c.re = compile(find, flags, compiledAt);
        const groups = new RegExp(c.re.source + '|').exec('')!.length - 1;
        const maxRef = Math.max(0, ...c.repl.map(p => (p.k === 'group' ? p.n : 0)));
        if (maxRef > groups) throw err(specMessage('sed', 'invalidReference', { number: String(maxRef) }), compiledAt);
        break;
      }
      case 'y': {
        const yd = script[i++] ?? '\0';
        const src = [...unescapeY(readDelimited(yd, 'y'))];
        const dst = [...unescapeY(readDelimited(yd, 'y'))];
        if (src.length !== dst.length) throw err(specMessage('sed', 'yLengths'), i);
        src.forEach((s, k) => c.table.set(s, dst[k]));
        break;
      }
      case 'a': case 'i': case 'c': c.text = readText(); break;
      case 'r': case 'R': case 'w': // the file name is the rest of the line, as in GNU sed
        c.text = readToEol();
        if (!c.text) throw err(specMessage('sed', 'missingFilename'), i);
        break;
      case ':': {
        if (c.a1) throw err(specMessage('sed', 'colonAddresses'), i);
        const label = readLabel();
        if (!label) throw err(specMessage('sed', 'colonLabel'), i);
        labels.set(label, cmds.length);
        break;
      }
      case 'b': case 't': case 'T': c.label = readLabel(); break;
      case 'v': readLabel(); break; // "requires GNU sed VERSION": always met
      case 'q': case 'Q':
        skipBlanks();
        c.code = isDigit(script[i]) ? readNumber() : 0;
        break;
      case 'l': skipBlanks(); if (isDigit(script[i])) readNumber(); break;
      default:
        if (!'=dDgGhHnNpPxz'.includes(op)) throw err(specMessage('sed', 'unknownCommand', { op }), i);
    }
    cmds.push(c);
    skipBlanks();
    if (op !== '{' && !':btT'.includes(op) && i < n && !';\n}#'.includes(script[i])) throw err(specMessage('sed', 'extraCharacters'), i + 1);
  }
  if (blocks.length > 0) throw err(specMessage('sed', 'unmatchedBrace'), blocks[blocks.length - 1].at, true);
  for (const c of cmds) {
    if (c.op !== 'b' && c.op !== 't' && c.op !== 'T') continue;
    if (c.label && !labels.has(c.label)) throw new ShellError(specMessage('sed', 'missingLabel', { label: c.label }), 4);
    c.target = c.label ? labels.get(c.label)! : cmds.length;
  }
  return cmds;
}

/** A sed replacement: literals, groups (& is 0), and \U \L \u \l \E case markers. */
function parseReplacement(r: string): ReplPart[] {
  const parts: ReplPart[] = [];
  for (let i = 0; i < r.length; i++) {
    const c = r[i];
    if (c === '\\' && i + 1 < r.length) {
      const nx = r[++i];
      if (nx >= '0' && nx <= '9') parts.push({ k: 'group', n: +nx });
      else if ('ULulE'.includes(nx)) parts.push({ k: 'case', op: nx });
      else parts.push({ k: 'lit', text: nx === 'n' ? '\n' : nx === 't' ? '\t' : nx === 'r' ? '\r' : nx });
    } else parts.push(c === '&' ? { k: 'group', n: 0 } : { k: 'lit', text: c });
  }
  return parts;
}

function expand(parts: ReplPart[], m: RegExpExecArray, maxString: number, base: number): string {
  let out = '';
  let mode: string | null = null, one: string | null = null;
  for (const p of parts) {
    if (p.k === 'case') {
      if (p.op === 'U' || p.op === 'L' || p.op === 'E') { mode = p.op === 'E' ? null : p.op; one = null; } else one = p.op;
      continue;
    }
    let text = p.k === 'group' ? m[p.n] ?? '' : p.text;
    if (mode) text = mode === 'U' ? text.toUpperCase() : text.toLowerCase();
    if (one && text.length > 0) { text = (one === 'u' ? text[0].toUpperCase() : text[0].toLowerCase()) + text.slice(1); one = null; }
    if (base + out.length + text.length > maxString) throw tooLarge('sed: pattern space', maxString, 'process the input in smaller pieces');
    out += text;
  }
  return out;
}

function substitute(c: Cmd, ps: string, maxString: number): string | null {
  const re = c.re!;
  re.lastIndex = 0;
  let out = '', pos = 0, count = 0, prevEnd = -1, done = false;
  for (let m = re.exec(ps); m; m = re.exec(ps)) {
    if (m[0].length === 0) re.lastIndex++;
    if (m[0].length === 0 && m.index === prevEnd) continue; // no empty match right after the previous match: "abc" s/b*/X/g -> XaXcX
    prevEnd = m.index + m[0].length;
    if (++count < c.nth) continue;
    out += ps.slice(pos, m.index);
    out += expand(c.repl, m, maxString, out.length);
    pos = m.index + m[0].length;
    done = true;
    if (!c.global) break;
  }
  return done ? out + ps.slice(pos) : null;
}

function matches(c: Cmd, ps: string, n: number, last: boolean): boolean {
  const hit = (a: Addr): boolean => {
    switch (a.k) {
      case 'line': return a.n === n;
      case 'last': return last;
      case 're': a.re.lastIndex = 0; return a.re.test(ps);
      case 'step': return a.step <= 0 ? n === a.first : n >= a.first && (n - a.first) % a.step === 0;
      case 'zero': return n === 1;
      default: return false;
    }
  };
  let result: boolean;
  if (!c.a1) result = true;
  else if (!c.a2) result = hit(c.a1);
  else if (c.inRange) {
    const a2 = c.a2;
    const ended = a2.k === 'line' ? n >= a2.n : a2.k === 'plus' ? n >= c.rangeEnd : a2.k === 'mult' ? a2.n <= 0 || n % a2.n === 0 : hit(a2);
    c.inRange = !ended;
    result = true;
  } else if (hit(c.a1)) {
    const a2 = c.a2;
    if (c.a1.k === 'zero') c.inRange = !hit(a2);
    else if (a2.k === 'line') c.inRange = a2.n > n;
    else if (a2.k === 'plus') { c.rangeEnd = n + a2.n; c.inRange = a2.n > 0; }
    else if (a2.k === 'mult') c.inRange = a2.n > 0 && n % a2.n !== 0;
    else if (a2.k === 'last') c.inRange = !last;
    else c.inRange = true;
    result = true;
  } else result = false;
  return result !== c.negate;
}

function listLine(ps: string): string {
  const esc: Record<string, string> = { '\\': '\\\\', '\x07': '\\a', '\b': '\\b', '\f': '\\f', '\n': '\\n', '\r': '\\r', '\t': '\\t', '\v': '\\v' };
  const parts = [...ps].map(ch => esc[ch] ?? (ch.charCodeAt(0) < 32 || ch.charCodeAt(0) === 127 ? '\\' + ch.charCodeAt(0).toString(8).padStart(3, '0') : ch));
  let out = '', width = 0;
  for (const p of parts) {
    if (width + p.length > 69) { out += '\\\n'; width = 0; }
    out += p;
    width += p.length;
  }
  return out + '$';
}

interface Sink { write(s: string): void }

/** Runs a parsed script over text. Returns the q/Q exit code if the script quit, else null. */
/** @param readFile a file's text for r and R, or null when it can't be read (sed then adds nothing) */
function execute(ctx: CommandContext, cmds: Cmd[], text: string, out: Sink, quiet: boolean, readFile: (f: string) => string | null): number | null {
  const rLines = new Map<string, string[]>(); // R: each file's lines not yet used
  const maxString = ctx.shell.options.maxStringBytes;
  const lines = splitLines(text);
  const trailing = text.endsWith('\n');
  for (const c of cmds) c.inRange = false;
  let hold = '';
  let idx = 0, n = 0;
  let pendingNewline = false;

  const write = (s: string, fromInput = false) => {
    if (pendingNewline) { out.write('\n'); pendingNewline = false; }
    out.write(s);
    // the last input line had no newline: don't invent one (unless more output follows)
    if (fromInput && idx >= lines.length && !trailing) pendingNewline = true; else out.write('\n');
  };
  const sinkFor = (file: string): Sink => file === '/dev/stdout' ? out : file === '/dev/stderr' ? ctx.stderr : { write: s => ctx.fs.appendText(ctx.resolvePath(file), s) };
  // a and R add a line; r adds a file as it is
  const flushAppends = (appends: [string, boolean][]) => {
    for (const [t, raw] of appends) {
      if (!raw) { write(t); continue; }
      if (pendingNewline) { out.write('\n'); pendingNewline = false; }
      out.write(t);
    }
    appends.length = 0;
  };

  let exitCode: number | null = null;
  let steps = 0;
  let ps = '';
  let reread = true;
  while (exitCode === null) {
    if (reread) {
      if (idx >= lines.length) break;
      ps = lines[idx++];
      n++;
    }
    reread = true;
    const appends: [string, boolean][] = [];
    let tflag = false, autoprint = !quiet;
    for (let pc = 0; pc < cmds.length;) {
      if (++steps > STEP_LIMIT) throw fail(specMessage('sed', 'tooManySteps'));
      if ((steps & 1023) === 0) ctx.checkTime();
      if (ps.length > maxString || hold.length > maxString) throw tooLarge('sed: pattern space', maxString, 'process the input in smaller pieces');
      const c = cmds[pc++];
      if (!matches(c, ps, n, idx >= lines.length)) {
        if (c.op === '{') pc = c.target;
        continue;
      }
      let stop = false;
      switch (c.op) {
        case 's': {
          const r = substitute(c, ps, maxString);
          if (r !== null) {
            ps = r;
            tflag = true;
            if (c.print) write(ps, true);
            if (c.wfile) sinkFor(c.wfile).write(ps + '\n');
          }
          break;
        }
        case 'p': write(ps, true); break;
        case 'P': write(ps.split('\n', 1)[0], true); break;
        case 'd': autoprint = false; stop = true; break;
        case 'D':
          autoprint = false;
          stop = true;
          if (ps.includes('\n')) { ps = ps.slice(ps.indexOf('\n') + 1); reread = false; }
          break;
        case 'n':
          if (idx >= lines.length) { stop = true; break; } // no next line: GNU sed ends here, printing the pattern space
          if (!quiet) write(ps, true);
          flushAppends(appends);
          ps = lines[idx++];
          n++;
          break;
        case 'N':
          if (idx >= lines.length) { stop = true; break; }
          ps += '\n' + lines[idx++];
          n++;
          break;
        case 'g': ps = hold; break;
        case 'G': ps += '\n' + hold; break;
        case 'h': hold = ps; break;
        case 'H': hold += '\n' + ps; break;
        case 'x': [ps, hold] = [hold, ps]; break;
        case 'z': ps = ''; break;
        case 'y': ps = [...ps].map(ch => c.table.get(ch) ?? ch).join(''); break;
        case '=': write(String(n)); break;
        case 'l': write(listLine(ps)); break;
        case 'a': appends.push([c.text, false]); break;
        case 'r': { const content = readFile(c.text); if (content) appends.push([content, true]); break; }
        case 'R': {
          let queue = rLines.get(c.text);
          if (!queue) { const all = readFile(c.text); rLines.set(c.text, queue = all === null ? [] : splitLines(all)); }
          if (queue.length) appends.push([queue.shift()!, false]);
          break;
        }
        case 'w': sinkFor(c.text).write(ps + '\n'); break;
        case 'i': write(c.text); break;
        case 'c':
          if (!c.a2 || !c.inRange || c.negate) write(c.text);
          autoprint = false;
          stop = true;
          break;
        case 'b': pc = c.target; break;
        case 't': if (tflag) { tflag = false; pc = c.target; } break;
        case 'T': if (!tflag) pc = c.target; else tflag = false; break;
        case 'q': exitCode = c.code; stop = true; break;
        case 'Q': exitCode = c.code; autoprint = false; stop = true; break;
      }
      if (stop) break;
    }
    if (autoprint) write(ps, true);
    flushAppends(appends);
  }
  return exitCode;
}

/** Output buffered and handed to the writer even if the run is cut short. */
function bufferedRun(target: Sink, f: (out: Sink) => number | null): number | null {
  const parts: string[] = [];
  try { return f({ write: s => { parts.push(s); } }); } finally { target.write(parts.join('')); }
}

function run(ctx: CommandContext): number {
  // a bad command line: the complaint, then the whole usage text, on stderr
  const bad = (message: string) => { ctx.stderr.write((message.length > 0 ? `sed: ${message}\n` : '') + USAGE); return 1; };
  const scripts: { text: string; where: string }[] = [];
  let expressions = 0;
  const addScript = (kind: string, value: string) => {
    if (kind === 'e') { scripts.push({ text: value, where: `-e expression #${++expressions}` }); return; }
    let text: string;
    try { text = value === '-' ? ctx.stdinText : ctx.fs.isDirectory(ctx.resolvePath(value)) ? '' : ctx.fs.readText(ctx.resolvePath(value)); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      throw new ShellError(specMessage('sed', 'cannotOpenScript', { file: value, errno: reason(e) }), 4);
    }
    scripts.push({ text, where: `file ${value}` });
  };
  const files: string[] = [];
  let quiet = false, extended = false, separate = false, inPlace = false;
  let suffix = '';
  const args = ctx.args;
  for (let i = 0; i < args.length;) {
    const arg = args[i++];
    if (arg === '--') { files.push(...args.slice(i)); break; }
    if (arg.startsWith('--')) {
      let name = arg.slice(2);
      let value: string | undefined;
      const eq = name.indexOf('=');
      if (eq >= 0) { value = name.slice(eq + 1); name = name.slice(0, eq); }
      switch (name) {
        case 'quiet': case 'silent': quiet = true; break;
        case 'regexp-extended': extended = true; break;
        case 'separate': separate = true; break;
        case 'in-place': inPlace = true; suffix = value ?? ''; break;
        case 'expression': case 'file':
          if (value === undefined) { if (i >= args.length) return bad(specFormat(GETOPT.missingLongArgument, { option: name })); value = args[i++]; }
          addScript(name[0], value);
          break;
        case 'posix': case 'debug': case 'sandbox': case 'follow-symlinks': case 'unbuffered': break;
        case 'help': ctx.stdout.write(USAGE); return 0;
        default: return bad(specFormat(GETOPT.unrecognizedOption, { option: arg }));
      }
      continue;
    }
    if (arg.length < 2 || arg[0] !== '-') { files.push(arg); continue; }
    for (let k = 1; k < arg.length; k++) {
      const f = arg[k];
      if (f === 'n') quiet = true;
      else if (f === 'E' || f === 'r') extended = true;
      else if (f === 's') separate = true;
      else if (f === 'u') { /* unbuffered: nothing to do */ }
      else if (f === 'i') { inPlace = true; suffix = arg.slice(k + 1); break; } // like GNU, -i takes the rest of the word as a backup suffix
      else if (f === 'e' || f === 'f') {
        let value = arg.slice(k + 1);
        if (value.length === 0) { if (i >= args.length) return bad(specFormat(GETOPT.missingShortArgument, { option: f })); value = args[i++]; }
        addScript(f, value);
        break;
      } else return bad(specFormat(GETOPT.invalidOption, { option: f }));
    }
  }
  if (scripts.length === 0) {
    if (files.length === 0) return bad('');
    addScript('e', files.shift()!);
  }
  if (scripts[0].text.startsWith('#n')) quiet = true; // like GNU sed, a script starting with #n is sed -n
  const cmds = parse(scripts, extended);
  for (const c of cmds) {
    const w = c.op === 'w' ? c.text : c.wfile; // GNU creates w files up front
    if (w && w !== '/dev/stdout' && w !== '/dev/stderr') ctx.fs.writeText(ctx.resolvePath(w), '');
  }

  let stdinRead = false;
  const readFile = (f: string): string | null => {
    if (f === '/dev/stdin') { if (stdinRead) return ''; stdinRead = true; return ctx.stdinText; } // stdin is read once
    try { const path = ctx.resolvePath(f); return ctx.fs.isFile(path) ? ctx.fs.readText(path) : null; } catch (e) {
      if (e instanceof ShellError) return null;
      throw e;
    }
  };

  if (inPlace) {
    if (files.length === 0) throw fail(specMessage('sed', 'noInputFiles'));
    let code = 0;
    for (const f of files) {
      const path = ctx.resolvePath(f);
      let original: string;
      try { original = ctx.fs.readText(path); } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        if (ctx.fs.isDirectory(path)) { ctx.stderr.write(`sed: ${specMessage('sed', 'notRegularFile', { file: f })}\n`); code = 4; continue; }
        ctx.stderr.write(`sed: ${cannotRead('sed', f, reason(e))}\n`);
        code = 2;
        continue;
      }
      let result = '';
      const q = execute(ctx, cmds, original, { write: s => { result += s; } }, quiet, readFile);
      if (suffix) ctx.fs.writeText(ctx.resolvePath(suffix.includes('*') ? suffix.replaceAll('*', f.slice(f.lastIndexOf('/') + 1)) : f + suffix), original);
      if (ctx.fs.isSymlink(path)) ctx.fs.delete(path); // like GNU sed -i, the edited text replaces the link; its target stays as it was
      ctx.fs.writeText(path, result);
      if (q !== null) return q;
    }
    return code;
  }
  let error = false;
  const texts: string[] = [];
  if (files.length === 0) texts.push(ctx.stdinText);
  for (const f of files) {
    if (f === '-') { texts.push(ctx.stdinText); continue; }
    try { texts.push(ctx.fs.readPipeText(ctx.resolvePath(f))); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      ctx.stderr.write(`sed: ${cannotRead('sed', f, reason(e))}\n`);
      error = true;
    }
  }
  const inputs = separate ? texts : [texts.map((t, k) => (t.endsWith('\n') || k === texts.length - 1 ? t : t + '\n')).join('')];
  const q = bufferedRun(ctx.stdout, out => {
    for (const t of inputs) { const r = execute(ctx, cmds, t, out, quiet, readFile); if (r !== null) return r; }
    return null;
  });
  return q ?? (files.some(f => f !== '-' && ctx.fs.isDirectory(ctx.resolvePath(f))) ? 4 : error ? 2 : 0); // 4: a read error (a directory)
}

export function registerSed(shell: Shell): void {
  shell.register('sed', run);
}
