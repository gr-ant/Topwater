import type { Shell } from '../shell.js';
import { ShellError, splitLines, reason, tooLarge, type CommandContext } from '../command.js';
import { posixToJs } from '../regex.js';

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

function parse(script: string, extended: boolean): Cmd[] {
  const cmds: Cmd[] = [];
  const labels = new Map<string, number>();
  const blocks: number[] = [];
  let lastRe: RegExp | undefined;
  let i = 0;
  const n = script.length;

  const skipBlanks = () => { while (i < n && (script[i] === ' ' || script[i] === '\t')) i++; };
  const isDigit = (c: string | undefined) => c !== undefined && c >= '0' && c <= '9';

  function readDelimited(delim: string, what: 'regex' | 'replacement' | 'y'): string {
    let sb = '';
    while (i < n && script[i] !== delim) {
      if (script[i] === '\\' && i + 1 < n) {
        if (script[i + 1] === delim) sb += delim;
        else sb += script.slice(i, i + 2);
        i += 2;
        continue;
      }
      if (script[i] === '\n' && what !== 'replacement') break;
      sb += script[i++];
    }
    if (i >= n || script[i] !== delim) throw fail(`unterminated \`${what === 'y' ? 'y' : 's'}' command`);
    i++;
    return sb;
  }

  function compile(text: string, flags: string): RegExp {
    if (text.length === 0) { if (!lastRe) throw fail('no previous regular expression'); return lastRe; }
    try { return lastRe = new RegExp(posixToJs(text, extended), 'gs' + flags); } catch (e) {
      if (e instanceof ShellError) throw fail(e.message);
      throw fail(`invalid regular expression: ${(e as Error).message.replace(/^Invalid regular expression: /, '')}`);
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
      const text = readDelimited(delim, 'regex');
      let flags = '';
      while (i < n && (script[i] === 'I' || script[i] === 'M')) flags += script[i++] === 'I' ? 'i' : 'm';
      return { k: 're', re: compile(text, flags) };
    }
    return undefined;
  }

  function readLabel(): string {
    skipBlanks();
    const start = i;
    while (i < n && script[i] !== ';' && script[i] !== '\n' && script[i] !== '}') i++;
    return script.slice(start, i).trim();
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
      if (!c.a2) throw fail("unexpected `,'");
    }
    if (c.a1?.k === 'zero' && c.a2?.k !== 're') throw fail('invalid usage of line address 0');
    skipBlanks();
    while (script[i] === '!') { c.negate = true; i++; skipBlanks(); }
    if (i >= n) throw fail('missing command');
    const op = c.op = script[i++];
    switch (op) {
      case '{': blocks.push(cmds.length); break;
      case '}':
        if (blocks.length === 0) throw fail("unexpected `}'");
        if (c.a1) throw fail("} doesn't want any addresses");
        cmds[blocks.pop()!].target = cmds.length + 1;
        break;
      case 's': {
        if (i >= n) throw fail("unterminated `s' command");
        const delim = script[i++];
        const find = readDelimited(delim, 'regex');
        c.repl = parseReplacement(readDelimited(delim, 'replacement'));
        let flags = '';
        while (i < n && 'gpiImeEw0123456789'.includes(script[i])) {
          const f = script[i];
          if (isDigit(f)) {
            c.nth = readNumber();
            if (c.nth === 0) throw fail("number option to `s' command may not be zero");
            continue;
          }
          i++;
          if (f === 'g') c.global = true;
          else if (f === 'p') c.print = true;
          else if (f === 'i' || f === 'I') flags += 'i';
          else if (f === 'm' || f === 'M') flags += 'm';
          else if (f === 'w') { c.wfile = readToEol(); break; }
          else throw fail("the `e' flag of `s' is not supported");
        }
        c.re = compile(find, flags);
        const groups = new RegExp(c.re.source + '|').exec('')!.length - 1;
        const maxRef = Math.max(0, ...c.repl.map(p => (p.k === 'group' ? p.n : 0)));
        if (maxRef > groups) throw fail(`invalid reference \\${maxRef} on \`s' command's RHS`);
        break;
      }
      case 'y': {
        const yd = script[i++] ?? '\0';
        const src = [...unescapeY(readDelimited(yd, 'y'))];
        const dst = [...unescapeY(readDelimited(yd, 'y'))];
        if (src.length !== dst.length) throw fail("strings for `y' command are different lengths");
        src.forEach((s, k) => c.table.set(s, dst[k]));
        break;
      }
      case 'a': case 'i': case 'c': c.text = readText(); break;
      case 'r': case 'w':
        c.text = readToEol();
        if (!c.text) throw fail(`missing filename in r/R/w/W commands`);
        break;
      case ':': {
        if (c.a1) throw fail(": doesn't want any addresses");
        const label = readLabel();
        if (!label) throw fail('":" lacks a label');
        labels.set(label, cmds.length);
        break;
      }
      case 'b': case 't': case 'T': c.label = readLabel(); break;
      case 'q': case 'Q':
        skipBlanks();
        c.code = isDigit(script[i]) ? readNumber() : 0;
        break;
      case 'l': skipBlanks(); if (isDigit(script[i])) readNumber(); break;
      default:
        if (!'=dDgGhHnNpPxz'.includes(op)) throw fail(`unknown command: \`${op}'`);
    }
    cmds.push(c);
    skipBlanks();
    if (op !== '{' && i < n && !';\n}#'.includes(script[i])) throw fail('extra characters after command');
  }
  if (blocks.length > 0) throw fail("unmatched `{'");
  for (const c of cmds) {
    if (c.op !== 'b' && c.op !== 't' && c.op !== 'T') continue;
    if (c.label && !labels.has(c.label)) throw fail(`can't find label for jump to \`${c.label}'`);
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
function execute(ctx: CommandContext, cmds: Cmd[], text: string, out: Sink, quiet: boolean): number | null {
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
  const flushAppends = (appends: [string, boolean][]) => {
    for (const [t, isFile] of appends) {
      if (!isFile) { write(t); continue; }
      try { const f = ctx.fs.readPipeText(ctx.resolvePath(t)); if (f) { write(f.endsWith('\n') ? f.slice(0, -1) : f); } } catch (e) { if (!(e instanceof ShellError)) throw e; }
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
      if (++steps > STEP_LIMIT) throw fail('too many steps (a branch loops forever?)');
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
        case 'r': appends.push([c.text, true]); break;
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
  const scripts: string[] = [];
  const files: string[] = [];
  let quiet = false, extended = false, separate = false, inPlace = false;
  let suffix = '';
  const args = ctx.args;
  const readScript = (f: string) => {
    try { return ctx.fs.readText(ctx.resolvePath(f)).replace(/\n$/, ''); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      throw fail(`couldn't open file ${f}: ${reason(e)}`);
    }
  };
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
          if (value === undefined) { if (i >= args.length) throw fail(`option '--${name}' requires an argument`); value = args[i++]; }
          scripts.push(name === 'expression' ? value : readScript(value));
          break;
        case 'posix': case 'debug': case 'sandbox': case 'follow-symlinks': case 'unbuffered': break;
        default: throw fail(`unrecognized option '${arg}'`);
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
        if (value.length === 0) { if (i >= args.length) throw fail(`option requires an argument -- '${f}'`); value = args[i++]; }
        scripts.push(f === 'e' ? value : readScript(value));
        break;
      } else throw fail(`invalid option -- '${f}'`);
    }
  }
  if (scripts.length === 0) {
    if (files.length === 0) throw fail('usage: sed [-n] [-i[SUFFIX]] [-E] [-e SCRIPT]... [SCRIPT] [FILE]...');
    scripts.push(files.shift()!);
  }
  const cmds = parse(scripts.join('\n'), extended);
  for (const c of cmds) {
    const w = c.op === 'w' ? c.text : c.wfile; // GNU creates w files up front
    if (w && w !== '/dev/stdout' && w !== '/dev/stderr') ctx.fs.writeText(ctx.resolvePath(w), '');
  }

  if (inPlace) {
    if (files.length === 0) throw fail('no input files');
    let code = 0;
    for (const f of files) {
      const path = ctx.resolvePath(f);
      let original: string;
      try { original = ctx.fs.readText(path); } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`sed: can't read ${f}: ${reason(e)}\n`);
        code = 2;
        continue;
      }
      let result = '';
      const q = execute(ctx, cmds, original, { write: s => { result += s; } }, quiet);
      if (suffix) ctx.fs.writeText(ctx.resolvePath(suffix.includes('*') ? suffix.replaceAll('*', f.slice(f.lastIndexOf('/') + 1)) : f + suffix), original);
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
      ctx.stderr.write(`sed: can't read ${f}: ${reason(e)}\n`);
      error = true;
    }
  }
  const inputs = separate ? texts : [texts.map((t, k) => (t.endsWith('\n') || k === texts.length - 1 ? t : t + '\n')).join('')];
  const q = bufferedRun(ctx.stdout, out => {
    for (const t of inputs) { const r = execute(ctx, cmds, t, out, quiet); if (r !== null) return r; }
    return null;
  });
  return q ?? (error ? 2 : 0);
}

export function registerSed(shell: Shell): void {
  shell.register('sed', run, 'Stream editor');
}
