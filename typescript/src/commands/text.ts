// Text: grep, head, tail, wc, sort, uniq, cut, tr, tee, xargs, sed, nl, rev, tac, paste, comm, join, column, seq, diff. See docs/architecture.md.
import type { Shell } from '../shell.js';
import { Args, specFormat, specOf, specMessage, ShellError, UsageException, quoteL, inputBytes, inputs, reason, splitLines, type CommandContext, type CommandHandler } from '../command.js';
import { GETOPT } from '../spec.generated.js';
import { decodeBytes, encodeText as encodeBytes } from '../bytes.js';
import { escapeRegex, posixRegexError, posixToJs } from '../regex.js';
import { globRegex } from '../glob.js';
import { printfFormat, unescape } from '../format.js';
import { VirtualFileSystem } from '../vfs.js';
import { cannotRun } from '../command.js';
import { cmpCp } from './gitcore.js';
import { run } from './sed.js';
import { diff } from './diff.js';

export const checkEvery = (ctx: CommandContext, i: number) => { if ((i & 7) === 0) ctx.checkTime(); };
const lines2text = (lines: string[]) => (lines.length ? lines.join('\n') + '\n' : '');

/** Output gathered in memory and written once, even when the command is cut short. */
export function buffered(ctx: CommandContext) {
  const parts: string[] = [];
  return { w: (s: string) => { parts.push(s); }, flush: () => { if (parts.length) ctx.stdout.write(parts.join('')); parts.length = 0; } };
}

function intArg(cmd: string, s: string, what: string): number {
  const digits = s.replace(/^[+-]/, '');
  if (!/^\d+$/.test(digits)) throw new ShellError(specMessage(cmd, 'invalidCount', { what, value: s }));
  return parseInt(digits, 10);
}

// ---------- grep ----------

function grepRegex(patterns: string[], o: { E: boolean; F: boolean; i: boolean; w: boolean; x: boolean; P: boolean }): RegExp {
  // JS has no \K: a leading X\KY means (?<=X)Y
  const keep = (p: string) => { const k = p.indexOf('\\K'); return k > 0 ? `(?<=${p.slice(0, k)})${p.slice(k + 2)}` : p; };
  // GNU reports every bad pattern, one line each; the shell adds the first "grep: "
  const errors = o.F || o.P ? [] : patterns.map(p => posixRegexError(p, o.E, true)).filter((e): e is string => e !== null);
  if (errors.length > 0) throw new ShellError(errors.join('\ngrep: '), 2);
  const parts = patterns.map(p => (o.F ? escapeRegex(p) : o.P ? keep(p) : posixToJs(p, o.E)));
  let re = parts.length === 1 ? parts[0] : parts.map(p => `(?:${p})`).join('|');
  if (o.w) re = `(?<!\\w)(?:${re})(?!\\w)`;
  if (o.x) re = `^(?:${re})$`;
  try { return new RegExp(re, 'g' + (o.i ? 'i' : '')); } catch (e) {
    throw new ShellError(specMessage('grep', 'invalidRegex', { detail: (e as Error).message.replace(/^Invalid regular expression: /, '') }), 2);
  }
}

function grep(ctx: CommandContext): number {
  const include: RegExp[] = [], exclude: RegExp[] = [], excludeDir: RegExp[] = [];
  const rest: string[] = [];
  let patterns: string[] = [];
  const args = ctx.args;
  for (let i = 0; i < args.length;) {
    let arg = args[i++];
    if (arg === '--') { rest.push(arg, ...args.slice(i)); break; }
    if ((arg === '--include' || arg === '--exclude' || arg === '--exclude-dir') && i < args.length) arg += '=' + args[i++];
    if (arg.startsWith('--include=')) { include.push(globRegex(arg.slice(10))); continue; }
    if (arg.startsWith('--exclude=')) { exclude.push(globRegex(arg.slice(10))); continue; }
    if (arg.startsWith('--exclude-dir=')) { excludeDir.push(globRegex(arg.slice(14))); continue; }
    let kind: string, value: string;
    const m = arg.startsWith('--') ? null : /^-([a-zA-Z]*?)([ef])([\s\S]*)$/.exec(arg);
    const lng = /^--(regexp|file)(?:=([\s\S]*))?$/.exec(arg);
    if (m && /^[invcrRlLEFwxohHqsIaZP]*$/.test(m[1])) {
      kind = m[2];
      value = m[3];
      if (m[1]) rest.push('-' + m[1]);
      if (value.length === 0) { if (i >= args.length) throw new UsageException(specFormat(GETOPT.missingShortArgument, { option: kind })); value = args[i++]; }
    } else if (lng) {
      kind = lng[1][0];
      value = lng[2] ?? (i < args.length ? args[i++] : '');
    } else { rest.push(arg); continue; }
    if (kind === 'e') patterns.push(value);
    else patterns.push(...splitLines(value === '-' ? ctx.stdinText : ctx.fs.readText(ctx.resolvePath(value))));
  }
  const a = Args.parse(rest, specOf('grep'));
  const positional = [...a.positional];
  if (patterns.length === 0) {
    if (positional.length === 0) throw new UsageException('');
    patterns.push(positional.shift()!);
  }
  patterns = patterns.flatMap(p => (p.length > 0 ? p.split('\n') : [p])); // a pattern with newlines is several patterns

  const recursive = a.has('r') || a.has('R');
  const implicitDot = recursive && positional.length === 0;
  if (implicitDot) positional.push('.');
  if (a.has('E') && !a.has('F') && !a.has('P'))
    for (const p of patterns) {
      const lead = /^[*+?]|(?<!\\)[(|]([*+?])/.exec(p);
      if (lead) ctx.stderr.write(`grep: ${specMessage('grep', 'leadingRepetition', { char: lead[0].slice(-1) })}\n`);
    }
  const re = grepRegex(patterns, { E: a.has('E'), F: a.has('F'), i: a.has('i'), w: a.has('w'), x: a.has('x'), P: a.has('P') });
  const num = (v: string | undefined, what: string) => { if (v === undefined) return 0; if (!/^\d+$/.test(v)) throw new ShellError(`${v}: invalid ${what} argument`, 2); return parseInt(v, 10); };
  const after = num(a.value('A') ?? a.value('C'), 'context length'), before = num(a.value('B') ?? a.value('C'), 'context length');
  const maxCount = a.value('m') !== undefined ? num(a.value('m'), 'max count') : Infinity;

  const files: string[] = [];
  let code = 1, error = false, walked = false;
  const quietErr = a.has('s');
  for (const f of positional) {
    const abs = ctx.resolvePath(f);
    if (!ctx.fs.isDirectory(abs)) { files.push(f); continue; }
    walked = true;
    const above = new Set<string>([ctx.fs.realPath(abs) ?? abs]); // the directories being walked
    if (!recursive) { if (!quietErr) ctx.stderr.write(`grep: ${specMessage('grep', 'openFailed', { file: f, errno: 'Is a directory' })}\n`); error = true; continue; }
    const walk = (dirAbs: string, display: string) => {
      let names: string[];
      try { names = ctx.fs.list(dirAbs); } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        if (!quietErr) ctx.stderr.write(`grep: ${specMessage('grep', 'openFailed', { file: display, errno: 'Permission denied' })}\n`);
        error = true;
        return;
      }
      for (const name of names) {
        ctx.checkTime();
        const child = VirtualFileSystem.normalize(dirAbs, name);
        const childDisplay = display === '.' && implicitDot ? name : display.replace(/\/+$/, '') + '/' + name;
        if (!a.has('R') && ctx.fs.isSymlink(child)) continue; // -r skips links it meets; -R follows them
        if (ctx.fs.isDirectory(child)) {
          if (excludeDir.some(r => r.test(name))) continue;
          const real = ctx.fs.realPath(child);
          if (real === null || above.has(real)) { if (!quietErr) ctx.stderr.write(`grep: ${childDisplay}: warning: recursive directory loop\n`); continue; }
          above.add(real);
          walk(child, childDisplay);
          above.delete(real);
        }
        else if ((include.length === 0 || include.some(r => r.test(name))) && !exclude.some(r => r.test(name))) files.push(childDisplay);
      }
    };
    walk(abs, f);
  }
  if (positional.length > 0 && files.length === 0) return error ? 2 : 1;

  const showName = !a.has('h') && (a.has('H') || files.length > 1 || walked);
  const out = buffered(ctx);
  const invert = a.has('v');
  try {
    for (let [name, text] of inputs(ctx, files, () => { error = true; }, quietErr)) {
      if (name === '(standard input)' && a.value('\u0007') !== undefined) name = a.value('\u0007')!; // --label
      const binary = text.includes('\0') && !a.has('a');
      if (binary && a.has('I')) continue;
      const lines = splitLines(text);
      const hits: number[] = [];
      for (let i = 0; i < lines.length && hits.length < maxCount; i++) {
        checkEvery(ctx, i);
        re.lastIndex = 0;
        if (re.test(lines[i]) !== invert) hits.push(i);
      }
      if (hits.length > 0) code = 0;
      if (a.has('q')) { if (code === 0) return 0; continue; }
      const prefix = showName ? name : null;
      if (a.has('l')) { if (hits.length > 0) out.w(name + '\n'); continue; }
      if (a.has('L')) { if (hits.length === 0) out.w(name + '\n'); continue; }
      if (a.has('c')) { out.w((prefix !== null ? `${prefix}:${hits.length}` : String(hits.length)) + '\n'); continue; }
      if (binary) { if (hits.length > 0) out.w(`grep: ${specMessage('grep', 'binaryFileMatches', { file: name })}\n`); continue; }
      let lastPrinted = -1;
      const hitSet = new Set(hits);
      const ctxOn = before > 0 || after > 0;
      for (const h of hits) {
        const from = Math.max(h - before, lastPrinted + 1, 0), to = Math.min(lines.length - 1, h + after);
        if (ctxOn && lastPrinted >= 0 && from > lastPrinted + 1) out.w('--\n');
        for (let i = from; i <= to; i++) {
          const sep = hitSet.has(i) ? ':' : '-';
          const head = (prefix !== null ? prefix + sep : '') + (a.has('n') ? `${i + 1}${sep}` : '');
          if (a.has('o')) {
            if (hitSet.has(i) && !invert) {
              re.lastIndex = 0;
              for (let m = re.exec(lines[i]); m; m = re.exec(lines[i])) { if (m[0].length > 0) out.w(head + m[0] + '\n'); else re.lastIndex++; }
            }
          } else out.w(head + lines[i] + '\n');
          lastPrinted = i;
        }
      }
    }
  } finally { out.flush(); }
  return error && !(a.has('q') && code === 0) ? 2 : code;
}

// ---------- head / tail ----------

const headTail: CommandHandler = ctx => {
  const head = ctx.name === 'head';
  const a = Args.parse(Args.numericShorthand(ctx.args), specOf(ctx.name));
  // a bad count is reported before any file is opened
  const lines0 = a.value('n'), bytes0 = a.value('c');
  if (lines0 !== undefined) intArg(ctx.name, lines0, 'lines');
  if (bytes0 !== undefined) intArg(ctx.name, bytes0, 'bytes');
  let code = 0;
  const all = [...inputBytes(ctx, a.positional, () => { code = 1; })];
  all.forEach(([name, data], k) => {
    if ((all.length > 1 && !a.has('q')) || a.has('v')) ctx.stdout.write((k > 0 ? '\n' : '') + `==> ${name} <==\n`);
    const bytes = a.value('c');
    if (bytes !== undefined) {
      const n = intArg(ctx.name, bytes, 'bytes');
      const chunk = head
        ? bytes.startsWith('-') ? data.subarray(0, Math.max(0, data.length - n)) : data.subarray(0, Math.min(n, data.length))
        : bytes.startsWith('+') ? data.subarray(Math.min(data.length, Math.max(0, n - 1))) : n > 0 ? data.subarray(Math.max(0, data.length - n)) : data.subarray(0, 0);
      ctx.stdout.write(decodeBytes(chunk));
      return;
    }
    const text = decodeBytes(data);
    const spec = a.value('n') ?? '10';
    const count = intArg(ctx.name, spec, 'lines');
    const lines = splitLines(text);
    const pick = head
      ? spec.startsWith('-') ? lines.slice(0, Math.max(0, lines.length - count)) : lines.slice(0, count)
      : spec.startsWith('+') ? lines.slice(Math.max(0, count - 1)) : lines.slice(Math.max(0, lines.length - count));
    let output = lines2text(pick);
    // the file's last line had no newline, and neither does the output
    if (pick.length > 0 && !text.endsWith('\n') && (!head || pick.length === lines.length)) output = output.slice(0, -1);
    ctx.stdout.write(output);
  });
  return code;
};

// ---------- wc ----------

function wc(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('wc'));
  const all = !a.has('l') && !a.has('w') && !a.has('c') && !a.has('m') && !a.has('L');
  const selected = [all || a.has('l'), all || a.has('w'), a.has('m'), all || a.has('c'), a.has('L')];
  let code = 0;
  const ins = [...inputBytes(ctx, a.positional, () => { code = 1; })];
  const count = selected.filter(Boolean).length;
  // GNU pads every column to one width: none for a single count of a single file, 7 for stdin, else the total size
  const width = count === 1 && a.positional.length <= 1 ? 1 : ins.some(i => i[0] === '(standard input)') ? 7 : String(ins.reduce((s, i) => s + i[1].length, 0)).length;
  const row = (v: number[], name: string | null) => {
    const parts = v.filter((_, k) => selected[k]).map(n => String(n).padStart(width));
    if (name !== null) parts.push(name);
    return parts.join(' ') + '\n';
  };
  const total = [0, 0, 0, 0, 0];
  for (const [name, data] of ins) {
    ctx.checkTime();
    const text = decodeBytes(data);
    let l = 0, w = 0, chars = 0, maxLine = 0, cur = 0, inWord = false;
    for (const ch of text) {
      chars++;
      if (ch === '\n') { l++; maxLine = Math.max(maxLine, cur); cur = 0; } else cur++;
      if (ch === ' ' || ch === '\t' || ch === '\n' || ch === '\r' || ch === '\f' || ch === '\v') inWord = false;
      else if (!inWord) { inWord = true; w++; }
    }
    maxLine = Math.max(maxLine, cur);
    const v = [l, w, chars, data.length, maxLine];
    v.forEach((n, k) => { total[k] = k === 4 ? Math.max(total[4], n) : total[k] + n; });
    ctx.stdout.write(row(v, a.positional.length > 0 ? name : null));
  }
  if (a.positional.length > 1) ctx.stdout.write(row(total, 'total'));
  return code;
}

// ---------- sort ----------

const ORDERING = 'bdfghinrMV';
const MONTHS = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC'];
const isBlank = (c: string) => c === ' ' || c === '\t';

interface SortKey { sf: number; sc: number; ef: number | null; ec: number; bStart: boolean; bEnd: boolean; opts: string }
type VChunk = number | bigint | string;
type KeyVal = string | number | VChunk[];

function parseNumber(s: string, mode: string): number {
  const m = (mode === 'g' ? /^\s*[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?/ : /^\s*-?(?:\d+\.?\d*|\.\d+)/).exec(s);
  if (!m) return mode === 'g' ? -Infinity : 0;
  let v = parseFloat(m[0]);
  if (mode === 'h') { const k = 'KMGTPEZY'.indexOf((s[m[0].length] ?? '').toUpperCase()); if (k >= 0 && s[m[0].length]) v *= 1024 ** (k + 1); }
  return v;
}

function keyValue(text: string, opts: string): KeyVal {
  if (opts.includes('b')) text = text.replace(/^[ \t]+/, '');
  if (opts.includes('d')) text = text.replace(/[^\p{L}\p{N} \t]/gu, '');
  if (opts.includes('i')) text = text.replace(/[^ -~]/g, '');
  if (opts.includes('f')) text = text.toUpperCase();
  if (opts.includes('V')) return (text.match(/\d+|\D+/g) ?? []).map(c => (/^\d/.test(c) ? BigInt(c) : c));
  for (const mode of 'ngh') if (opts.includes(mode)) return parseNumber(text, mode);
  if (opts.includes('M')) { const t = text.trim(); return MONTHS.indexOf(t.slice(0, 3).toUpperCase()) + 1; }
  return text;
}

function cmpVal(a: KeyVal, b: KeyVal): number {
  if (typeof a === 'string' || typeof a === 'number') return a < (b as string | number) ? -1 : a > (b as string | number) ? 1 : 0;
  const o = b as VChunk[];
  for (let i = 0; i < Math.min(a.length, o.length); i++) {
    const x = a[i], y = o[i];
    const kx = typeof x === 'string' ? 1 : 0, ky = typeof y === 'string' ? 1 : 0;
    if (kx !== ky) return kx - ky;
    if (x !== y) return x < y ? -1 : 1;
  }
  return a.length - o.length;
}

/** Splits a stream into records on `eol`, dropping the empty piece after a final terminator. */
const records = (text: string, eol: string) => { const r = text.split(eol); if (r.length && r[r.length - 1] === '') r.pop(); return r; };

function sort(ctx: CommandContext): number {
  // -k may repeat and sit inside a cluster (-nk2), so pull it out before getopt-style parsing
  const specs: string[] = [];
  const rest: string[] = [];
  const args = ctx.args;
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === '--') { rest.push(...args.slice(i)); break; }
    if (arg === '--key' && i + 1 < args.length) { specs.push(args[++i]); continue; }
    if (arg.startsWith('--key=')) { specs.push(arg.slice(6)); continue; }
    if (arg.startsWith('-') && !arg.startsWith('--') && arg.length > 1) {
      let j = 1;
      while (j < arg.length && !'kto'.includes(arg[j]) && !'STm'.includes(arg[j])) j++;
      if (j < arg.length && arg[j] === 'k') {
        const v = arg.slice(j + 1) || args[++i];
        if (v === undefined) throw new ShellError(specMessage('sort', 'optionRequiresArgument', { option: 'k' }), 2);
        specs.push(v);
        if (j > 1) rest.push(arg.slice(0, j));
        continue;
      }
    }
    rest.push(arg);
  }
  const a = Args.parse(rest, specOf('sort'));
  const sep = a.value('t') !== undefined ? unescape(a.value('t')!) : null;
  if (sep !== null && sep.length !== 1) throw new ShellError(specMessage('sort', 'multiCharacterTab', { value: a.value('t')! }), 2);
  const globalOpts = [...ORDERING].filter(c => a.has(c)).join('');
  const keys: SortKey[] = specs.map(spec => {
    const m = /^(\d+)(?:\.(\d+))?([a-zA-Z]*)(?:,(\d+)(?:\.(\d+))?([a-zA-Z]*))?$/.exec(spec);
    if (!m || parseInt(m[1], 10) === 0) throw new ShellError(specMessage('sort', 'invalidKey', { value: spec }), 2);
    const own = m[3] + (m[6] ?? '');
    return {
      sf: parseInt(m[1], 10), sc: m[2] ? parseInt(m[2], 10) : 1, ef: m[4] ? parseInt(m[4], 10) : null, ec: m[5] ? parseInt(m[5], 10) : 0,
      bStart: m[3].includes('b') || a.has('b'), bEnd: (m[6] ?? '').includes('b') || a.has('b'),
      opts: [...own].some(c => 'dfghinrMV'.includes(c)) ? own : globalOpts + own,
    };
  });
  if (keys.length === 0) keys.push({ sf: 0, sc: 1, ef: null, ec: 0, bStart: false, bEnd: false, opts: globalOpts });
  const eol = a.has('z') ? '\0' : '\n';

  const fieldStart = (line: string, f: number): number => {
    let pos = 0;
    if (sep !== null) {
      for (let i = 1; i < f; i++) { const idx = line.indexOf(sep, pos); if (idx < 0) return line.length; pos = idx + 1; }
      return pos;
    }
    for (let i = 1; i < f; i++) {
      while (pos < line.length && isBlank(line[pos])) pos++;
      while (pos < line.length && !isBlank(line[pos])) pos++;
    }
    return pos;
  };
  const extract = (line: string, k: SortKey): string => {
    if (k.sf === 0) return line;
    let s = fieldStart(line, k.sf);
    if (k.bStart) while (s < line.length && isBlank(line[s])) s++;
    s = Math.min(line.length, s + k.sc - 1);
    let e = line.length;
    if (k.ef !== null) {
      let fs = fieldStart(line, k.ef);
      if (k.bEnd) while (fs < line.length && isBlank(line[fs])) fs++;
      if (k.ec > 0) e = Math.min(line.length, fs + k.ec);
      else if (sep !== null) { const idx = line.indexOf(sep, fs); e = idx < 0 ? line.length : idx; }
      else { let p = fs; while (p < line.length && isBlank(line[p])) p++; while (p < line.length && !isBlank(line[p])) p++; e = p; }
    }
    return e > s ? line.slice(s, e) : '';
  };

  let code = 0;
  const named: [string, string][] = [];
  for (const f of a.positional.length > 0 ? a.positional : ['-']) {
    named.push(...inputs(ctx, [f], () => { code = 2; }));
    if (code !== 0) return code; // sort stops at the first file it can't read
  }
  const items = named.flatMap(([, t]) => records(t, eol)).map((line, i) => {
    checkEvery(ctx, i);
    return { line, vals: keys.map(k => keyValue(extract(line, k), k.opts)) };
  });
  type Item = (typeof items)[number];
  const compare = (x: Item, y: Item): number => {
    for (let i = 0; i < keys.length; i++) {
      const c = cmpVal(x.vals[i], y.vals[i]);
      if (c !== 0) return keys[i].opts.includes('r') ? -c : c;
    }
    if (a.has('s') || a.has('u')) return 0;
    const last = x.line < y.line ? -1 : x.line > y.line ? 1 : 0;
    return a.has('r') ? -last : last;
  };
  if (a.has('c') || a.has('C')) {
    for (let i = 1; i < items.length; i++) {
      const c = compare(items[i - 1], items[i]);
      if (c > 0 || (a.has('u') && c === 0)) {
        if (a.has('c')) ctx.stderr.write('sort: ' + specMessage('sort', 'disorder', { file: a.positional.length > 0 ? named[0][0] : '-', line: String(i + 1), text: items[i].line }) + '\n');
        return 1;
      }
    }
    return 0;
  }
  ctx.checkTime();
  let sorted = items.sort(compare); // Array.prototype.sort is stable
  ctx.checkTime();
  if (a.has('u')) sorted = sorted.filter((x, i) => i === 0 || compare(sorted[i - 1], x) !== 0);
  const output = sorted.length ? sorted.map(x => x.line).join(eol) + eol : '';
  const o = a.value('o');
  if (o !== undefined) ctx.fs.writeText(ctx.resolvePath(o), output); else ctx.stdout.write(output);
  return code;
}

// ---------- uniq ----------

function uniq(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('uniq'));
  const skipF = a.value('f') !== undefined ? intArg('uniq', a.value('f')!, 'fields to skip') : 0;
  const skipS = a.value('s') !== undefined ? intArg('uniq', a.value('s')!, 'bytes to skip') : 0;
  const maxW = a.value('w') !== undefined ? intArg('uniq', a.value('w')!, 'bytes to compare') : Infinity;
  const key = (line: string) => {
    let s = line;
    for (let i = 0; i < skipF; i++) s = s.replace(/^[ \t]*[^ \t]*/, '');
    s = s.slice(skipS).slice(0, maxW);
    return a.has('i') ? s.toLowerCase() : s;
  };
  let code = 0;
  const [input, output] = a.positional;
  const text = input === undefined || input === '-' ? ctx.stdinText : [...inputs(ctx, [input], () => { code = 1; })].map(x => x[1]).join('');
  if (code) return code;
  const groups: { line: string; k: string; count: number }[] = [];
  for (const line of splitLines(text)) {
    const k = key(line);
    const g = groups[groups.length - 1];
    if (g && g.k === k) g.count++; else groups.push({ line, k, count: 1 });
  }
  let out = '';
  for (const g of groups) {
    if ((a.has('d') && g.count < 2) || (a.has('u') && g.count > 1)) continue;
    out += (a.has('c') ? `${String(g.count).padStart(7)} ${g.line}` : g.line) + '\n';
  }
  if (output !== undefined) ctx.fs.writeText(ctx.resolvePath(output), out); else ctx.stdout.write(out);
  return 0;
}

// ---------- cut ----------

function parseList(spec: string): [number, number][] {
  const ranges: [number, number][] = [];
  for (const part of spec.split(',')) {
    const m = /^(\d*)(-?)(\d*)$/.exec(part);
    if (!m || (m[1] === '' && m[3] === '') || (!m[2] && m[3])) throw new ShellError(specMessage('cut', 'invalidFieldValue', { part }));
    const lo = m[1] === '' ? 1 : parseInt(m[1], 10);
    const hi = !m[2] ? lo : m[3] === '' ? Infinity : parseInt(m[3], 10);
    if ((m[1] !== '' && lo === 0) || (m[2] === '' && lo === 0)) throw new ShellError(specMessage('cut', 'fieldsFromOne', {}));
    if (hi < lo) throw new ShellError(specMessage('cut', 'invalidDecreasingRange', {}));
    ranges.push([lo, hi]);
  }
  ranges.sort((x, y) => x[0] - y[0]);
  const merged: [number, number][] = [];
  for (const r of ranges) {
    const l = merged[merged.length - 1];
    if (l && r[0] <= l[1]) l[1] = Math.max(l[1], r[1]); else merged.push([...r]);
  }
  return merged;
}

/** The selected items as groups, one per range (or per run of kept items when complementing). */
function pickGroups<T>(items: T[], ranges: [number, number][], complement: boolean): T[][] {
  if (!complement) return ranges.map(([lo, hi]) => items.slice(lo - 1, Math.min(hi, items.length))).filter(g => g.length > 0);
  const groups: T[][] = [];
  let from = 1;
  for (const [lo, hi] of [...ranges, [Infinity, Infinity] as [number, number]]) {
    if (lo > from) { const g = items.slice(from - 1, Math.min(lo - 1, items.length)); if (g.length) groups.push(g); }
    from = hi + 1;
  }
  return groups;
}

function cut(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('cut'));
  const delim = a.value('d') ?? '\t';
  if ([...delim].length !== 1) throw new ShellError(specMessage('cut', 'delimiterNotSingle', {}));
  const bytesMode = a.value('b') !== undefined && a.value('c') === undefined;
  const chars = a.value('c') ?? a.value('b');
  const spec = chars ?? a.value('f');
  if (spec === undefined) throw new UsageException(specMessage('cut', 'listRequired', {}));
  const ranges = parseList(spec);
  const complement = a.has('z');
  const outDelim = a.value('\u0002'); // --output-delimiter (long only)
  let code = 0;
  let out = '';
  for (const [, text] of inputs(ctx, a.positional, () => { code = 1; })) {
    for (const line of splitLines(text)) {
      if (chars !== undefined) {
        const units = bytesMode ? utf8Units(line) : [...line];
        out += pickGroups(units, ranges, complement).map(g => (bytesMode ? decodeBytes(Uint8Array.from(g.map(u => u.charCodeAt(0)))) : g.join(''))).join(outDelim ?? '') + '\n';
        continue;
      }
      if (!line.includes(delim)) { if (!a.has('s')) out += line + '\n'; continue; }
      out += pickGroups(line.split(delim), ranges, complement).flat().join(outDelim ?? delim) + '\n';
    }
  }
  ctx.stdout.write(out);
  return code;
}

/** One single-character string per UTF-8 byte of text (escaped bytes included). */
function utf8Units(text: string): string[] {
  const bytes = encodeBytes(text);
  return Array.from(bytes, b => String.fromCharCode(b));
}

// ---------- tr ----------

const TR_CLASSES: Record<string, string> = {
  upper: 'A-Z', lower: 'a-z', digit: '0-9', alpha: 'a-zA-Z', alnum: 'a-zA-Z0-9', space: ' \t\n\r\f\v', blank: ' \t',
  xdigit: '0-9A-Fa-f', punct: '!-/:-@[-`{-~', cntrl: '\u0000-\u001f\u007f', print: ' -~', graph: '!-~',
};
const TR_ESC: Record<string, string> = { n: '\n', t: '\t', r: '\r', a: '\x07', b: '\b', f: '\f', v: '\v', '\\': '\\' };

/** A tr operand as characters: ranges, [:classes:], [=c=] and backslash escapes expanded. */
function expandSet(spec: string): string[] {
  const toks: { c: string; lit: boolean }[] = [];
  const cps = [...spec];
  for (let i = 0; i < cps.length; i++) {
    const c = cps[i];
    if (c === '[' && cps[i + 1] === ':') {
      const close = cps.indexOf(']', i + 2);
      const name = cps.slice(i + 2, close - 1).join('');
      if (close > 0 && cps[close - 1] === ':' && name in TR_CLASSES) {
        for (const ch of expandRange(TR_CLASSES[name])) toks.push({ c: ch, lit: true });
        i = close;
        continue;
      }
      if (close > 0 && cps[close - 1] === ':') throw new ShellError(specMessage('tr', 'invalidCharacterClass', { name }));
    }
    if (c === '[' && cps[i + 1] === '=' && cps[i + 3] === '=' && cps[i + 4] === ']') { toks.push({ c: cps[i + 2], lit: true }); i += 4; continue; }
    if (c === '\\' && i + 1 < cps.length) {
      const n = cps[i + 1];
      if (/[0-7]/.test(n)) {
        let j = i + 1, oct = '';
        while (j < cps.length && oct.length < 3 && /[0-7]/.test(cps[j])) oct += cps[j++];
        toks.push({ c: String.fromCharCode(parseInt(oct, 8) & 0xff), lit: true });
        i = j - 1;
      } else { toks.push({ c: TR_ESC[n] ?? n, lit: true }); i++; }
      continue;
    }
    toks.push({ c, lit: false });
  }
  const out: string[] = [];
  for (let i = 0; i < toks.length; i++) {
    if (i + 2 < toks.length && toks[i + 1].c === '-' && !toks[i + 1].lit) {
      const lo = toks[i].c.codePointAt(0)!, hi = toks[i + 2].c.codePointAt(0)!;
      if (lo > hi) throw new ShellError(specMessage('tr', 'reverseRange', { range: `${toks[i].c}-${toks[i + 2].c}` }));
      for (let k = lo; k <= hi; k++) out.push(String.fromCodePoint(k));
      i += 2;
    } else out.push(toks[i].c);
  }
  return out;
}
function expandRange(s: string): string[] {
  const cps = [...s], out: string[] = [];
  for (let i = 0; i < cps.length; i++) {
    if (cps[i + 1] === '-' && i + 2 < cps.length) { for (let k = cps[i].codePointAt(0)!; k <= cps[i + 2].codePointAt(0)!; k++) out.push(String.fromCodePoint(k)); i += 2; }
    else out.push(cps[i]);
  }
  return out;
}

function tr(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('tr'));
  if (a.positional.length === 0) throw new UsageException(specMessage('tr', 'missingOperand', {}));
  const del = a.has('d'), squeezeOpt = a.has('s'), complement = a.has('c') || a.has('C');
  if (!del && !squeezeOpt && a.positional.length < 2) throw new ShellError(specMessage('tr', 'missingTranslation', { operand: a.positional[0] }));
  if (a.positional.length > 2) throw new UsageException(specMessage('tr', 'extraOperand', { operand: a.positional[2] }));
  let set1 = expandSet(a.positional[0]);
  const set2 = a.positional.length > 1 ? expandSet(a.positional[1]) : [];
  if (a.has('t') && set2.length > 0) set1 = set1.slice(0, set2.length);
  const in1 = new Set(set1);
  const inSet1 = (ch: string) => in1.has(ch) !== complement;
  const translate = set2.length > 0 && !del;
  const map = new Map<string, string>();
  if (translate && !complement) set1.forEach((c, i) => { if (!map.has(c)) map.set(c, set2[Math.min(i, set2.length - 1)]); });
  const squeezeSet = new Set(set2.length > 0 ? set2 : []);
  const squeeze = set2.length > 0 ? (c: string) => squeezeSet.has(c) : inSet1;
  const out: string[] = [];
  let prev: string | null = null;
  let n = 0;
  for (const ch of ctx.stdinText) {
    checkEvery(ctx, ++n);
    const hit = inSet1(ch);
    if (del && hit) continue;
    const o = translate && hit ? (complement ? set2[set2.length - 1] : map.get(ch)!) : ch;
    if (squeezeOpt && prev === o && squeeze(o)) continue;
    out.push(o);
    prev = o;
  }
  ctx.stdout.write(out.join(''));
  return 0;
}

// ---------- tee ----------

function tee(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('tee'));
  let code = 0;
  const text = ctx.stdinText;
  for (const f of a.positional) {
    const path = ctx.resolvePath(f);
    if (path === '/dev/stdout' || path === '/dev/stderr') { (path === '/dev/stderr' ? ctx.stderr : ctx.stdout).write(text); continue; }
    try { if (a.has('a')) ctx.fs.appendText(path, text); else ctx.fs.writeText(path, text); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      ctx.stderr.write('tee: ' + specMessage('tee', 'writeFailed', { file: f, reason: reason(e) }) + '\n');
      code = 1;
    }
  }
  ctx.stdout.write(text);
  return code;
}

// ---------- xargs ----------

/** Splits xargs input like GNU xargs: blanks separate items; quotes and backslashes protect them. */
function xargsWords(text: string): string[] {
  const items: string[] = [];
  let cur = '', inWord = false;
  for (let i = 0; i < text.length;) {
    const c = text[i];
    if (c === ' ' || c === '\t' || c === '\n') {
      if (inWord) { items.push(cur); cur = ''; inWord = false; }
      i++;
    } else if (c === "'" || c === '"') {
      const j = text.indexOf(c, i + 1);
      if (j < 0 || text.slice(i + 1, j).includes('\n')) throw new ShellError(specMessage('xargs', 'unmatchedQuote', { kind: c === "'" ? 'single' : 'double' }));
      cur += text.slice(i + 1, j);
      inWord = true;
      i = j + 1;
    } else if (c === '\\' && i + 1 < text.length) { cur += text[i + 1]; inWord = true; i += 2; }
    else { cur += c; inWord = true; i++; }
  }
  if (inWord) items.push(cur);
  return items;
}

const chunks = <T>(xs: T[], n: number): T[][] => { const r: T[][] = []; for (let i = 0; i < xs.length; i += n) r.push(xs.slice(i, i + n)); return r; };

async function xargs(ctx: CommandContext): Promise<number> {
  const opts: string[] = [];
  let i = 0;
  for (; i < ctx.args.length && ctx.args[i].startsWith('-') && ctx.args[i] !== '-'; i++) {
    opts.push(ctx.args[i]);
    if (['-I', '-n', '-d', '-L', '-P', '-s', '-E', '-a'].includes(ctx.args[i]) && i + 1 < ctx.args.length) opts.push(ctx.args[++i]);
    if (ctx.args[i] === '--') { opts.pop(); i++; break; }
  }
  const a = Args.parse(opts, specOf('xargs'));
  const command = ctx.args.slice(i);
  if (command.length === 0) command.push('echo');
  const input = a.value('a') !== undefined ? ctx.fs.readText(ctx.resolvePath(a.value('a')!)) : ctx.stdinText;
  const replace = a.value('I') ?? (a.has('i') ? '{}' : undefined);
  const posNum = (v: string, opt: string) => { if (!/^\d+$/.test(v) || parseInt(v, 10) < 1) throw new ShellError(specMessage('xargs', 'invalidValue', { value: v, option: opt })); return parseInt(v, 10); };
  let groups: string[][];
  if (replace !== undefined || a.value('L') !== undefined) {
    const lines = splitLines(input).map(l => (replace !== undefined ? l.replace(/^[ \t]+/, '') : l));
    if (replace !== undefined) groups = lines.filter(l => l.length > 0).map(l => [l]);
    else groups = chunks(lines.map(xargsWords).filter(w => w.length > 0), posNum(a.value('L')!, 'L')).map(c => c.flat());
  } else {
    let items: string[];
    if (a.has('0')) items = input.split('\0').filter(s => s.length > 0);
    else if (a.value('d') !== undefined) {
      const d = unescape(a.value('d')!);
      items = input.split(d.length > 0 ? d[0] : '\n');
      if (items.length > 0 && (items[items.length - 1] === '' || items[items.length - 1] === '\n')) items.pop();
    } else items = xargsWords(input);
    const eof = a.value('E');
    if (eof !== undefined && eof !== '') { const k = items.indexOf(eof); if (k >= 0) items = items.slice(0, k); }
    const per = a.value('n') !== undefined ? posNum(a.value('n')!, 'n') : Math.max(1, items.length);
    groups = chunks(items, per);
  }
  if (groups.length === 0) {
    if (a.has('r') || replace !== undefined) return 0;
    groups.push([]); // like GNU xargs, run the command once even with no input
  }
  let code = 0;
  for (const group of groups) {
    ctx.checkTime();
    const argv = replace !== undefined ? command.map(c => c.split(replace).join(group[0])) : [...command, ...group];
    if (a.has('t')) ctx.stderr.write(argv.join(' ') + '\n');
    const bad = cannotRun(ctx, argv[0]);
    if (bad) { ctx.stderr.write('xargs: ' + specMessage('xargs', 'cannotRun', { command: argv[0], errno: bad[0] }) + '\n'); return bad[1]; }
    const rc = await ctx.invoke(argv);
    if (rc === 127) return 127;
    if (rc === 255) { ctx.stderr.write('xargs: ' + specMessage('xargs', 'exitedWith255', { command: argv[0] }) + '\n'); return 124; }
    if (rc !== 0) code = 123;
  }
  return code;
}

// ---------- nl tac rev paste column seq ----------

function nl(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('nl'));
  const body = a.value('b') ?? 't';
  const width = parseInt(a.value('w') ?? '6', 10);
  const sep = a.value('s') ?? '\t';
  const fmt = a.value('n') ?? 'rn';
  let n = parseInt(a.value('v') ?? '1', 10);
  const inc = parseInt(a.value('i') ?? '1', 10);
  let code = 0, out = '';
  const bodyRe = body.startsWith('p') ? new RegExp(posixToJs(body.slice(1), false)) : null;
  for (const [, text] of inputs(ctx, a.positional, () => { code = 1; })) {
    for (const line of splitLines(text)) {
      const numbered = body === 'a' || (body === 't' && line.length > 0) || (bodyRe !== null && bodyRe.test(line));
      if (!numbered) { out += (line.length === 0 ? '' : ' '.repeat(width) + sep + line) + '\n'; continue; }
      const s = String(n);
      out += (fmt === 'ln' ? s.padEnd(width) : fmt === 'rz' ? s.padStart(width, '0') : s.padStart(width)) + sep + line + '\n';
      n += inc;
    }
  }
  ctx.stdout.write(out);
  return code;
}

/** comm and join: the lines of exactly two files (- is stdin), or null after reporting an unreadable one. */
function twoInputs(ctx: CommandContext, files: string[], sep = '\n'): string[][] | null {
  if (files.length < 2)
    throw new UsageException(files.length ? specMessage(ctx.name, 'missingOperandAfter', { operand: files[0] }) : specMessage(ctx.name, 'missingOperand', {}));
  if (files.length > 2) throw new UsageException(specMessage(ctx.name, 'extraOperand', { operand: files[2] }));
  const out: string[][] = [];
  for (const f of files) {
    const got = [...inputs(ctx, [f], () => {})];
    if (!got.length) return null;
    const lines = got[0][1].split(sep);
    if (lines.length && lines[lines.length - 1] === '') lines.pop();
    out.push(lines);
  }
  return out;
}

const lessCp = (x: string, y: string) => cmpCp(x, y) < 0; // code point order, as the other ports compare

function comm(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('comm'));
  const sep = a.has('z') ? '\0' : '\n';
  const both = twoInputs(ctx, a.positional, sep);
  if (both === null) return 1;
  const [left, right] = both;
  const delim = a.value('\u0003') ?? '\t';
  const show = [!a.has('1'), !a.has('2'), !a.has('3')];
  const indent = ['', show[0] ? delim : '', delim.repeat(Number(show[0]) + Number(show[1]))];
  const check = !a.has('\u0002'), strict = a.has('\u0001');
  const out: string[] = [], counts = [0, 0, 0], last: (string | null)[] = [null, null], unsorted = [false, false];
  const flush = () => { ctx.stdout.write(out.map(x => x + sep).join('')); out.length = 0; };
  /** Notes line as file k's latest; true the first time file k is found out of order. */
  const disorder = (k: number, line: string) => {
    const bad = check && last[k] !== null && lessCp(line, last[k]!) && !unsorted[k];
    unsorted[k] ||= bad;
    last[k] = line;
    return bad;
  };
  let i = 0, j = 0;
  while (i < left.length || j < right.length) {
    ctx.checkTime();
    let col: number, line: string, bad: number[];
    if (j >= right.length || (i < left.length && lessCp(left[i], right[j]))) {
      [col, line, bad] = [0, left[i], disorder(0, left[i]) ? [0] : []];
      i++;
    } else if (i >= left.length || lessCp(right[j], left[i])) {
      [col, line, bad] = [1, right[j], disorder(1, right[j]) ? [1] : []];
      j++;
    } else {
      [col, line] = [2, left[i]];
      bad = ([[0, left[i]], [1, right[j]]] as [number, string][]).filter(([k, x]) => disorder(k, x)).map(([k]) => k);
      i++; j++;
    }
    if (bad.length && strict) { // --check-order stops before the line out of order
      flush();
      ctx.stderr.write('comm: ' + specMessage('comm', 'notSorted', { n: String(bad[0] + 1) }) + '\n');
      return 1;
    }
    counts[col]++;
    if (show[col]) out.push(indent[col] + line);
    for (const k of bad) { // otherwise the line goes out first, then the warning
      flush();
      ctx.stderr.write('comm: ' + specMessage('comm', 'notSorted', { n: String(k + 1) }) + '\n');
    }
  }
  if (a.has('\u0004')) out.push(counts.join(delim) + delim + specMessage('comm', 'total', {}));
  flush();
  if (unsorted.some(Boolean)) {
    ctx.stderr.write('comm: ' + specMessage('comm', 'inputNotSorted', {}) + '\n');
    return 1;
  }
  return 0;
}

function join(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('join'));
  const tab = a.value('t') || undefined; // -t '' splits on blanks, as GNU join does
  if (tab !== undefined && tab.length > 1) throw new ShellError(specMessage('join', 'multiCharTab', { value: tab }));
  const fieldNo = (v: string) => {
    if (!/^[0-9]+$/.test(v) || Number(v) < 1) throw new ShellError(specMessage('join', 'badField', { value: v }));
    return Number(v) - 1;
  };
  let k1 = 0, k2 = 0;
  if (a.value('j') !== undefined) k1 = k2 = fieldNo(a.value('j')!);
  if (a.value('1') !== undefined) k1 = fieldNo(a.value('1')!);
  if (a.value('2') !== undefined) k2 = fieldNo(a.value('2')!);
  const unpaired = new Set<number>();
  for (const v of [...a.all('a'), ...a.all('v')]) {
    if (v !== '1' && v !== '2') throw new ShellError(specMessage('join', 'badFileNumberArg', { value: v }));
    unpaired.add(Number(v));
  }
  const only = new Set(a.all('v').map(Number));
  let fmt: [number, number][] | null = null;
  const ov = a.value('o');
  if (ov !== undefined && ov !== 'auto') {
    fmt = [];
    for (const spec of ov.trim().split(/[ ,]+/)) {
      const m = /^([0-9]+)\.([0-9]+)$/.exec(spec);
      if (spec === '0') fmt.push([0, 0]);
      else if (!m || (m[1] !== '1' && m[1] !== '2')) throw new ShellError(specMessage('join', 'badFileNumber', { spec }));
      else fmt.push([Number(m[1]), fieldNo(m[2])]);
    }
  }
  const both = twoInputs(ctx, a.positional);
  if (both === null) return 1;
  const fields = (line: string) => {
    if (tab !== undefined) return line.split(tab);
    const t = line.replace(/^[ \t]+|[ \t]+$/g, ''); // GNU's blanks: spaces and tabs only
    return t ? t.split(/[ \t]+/) : [];
  };
  const [left, right] = both.map(side => side.map(fields));
  const keys = [k1, k2];
  const fold = a.has('i');
  const key = (f: string[], side: number) => {
    const k = keys[side] < f.length ? f[keys[side]] : '';
    return fold ? k.toLowerCase() : k;
  };
  if (ov === 'auto') {
    fmt = [[0, 0]];
    for (let i = 0; i < (left.length ? left[0].length : 0); i++) if (i !== k1) fmt.push([1, i]);
    for (let i = 0; i < (right.length ? right[0].length : 0); i++) if (i !== k2) fmt.push([2, i]);
  }
  const outSep = tab ?? ' ';
  const empty = a.value('e') ?? '';
  const lines: string[] = [];
  const emit = (f1: string[] | null, f2: string[] | null) => {
    let row: string[];
    const [src, k] = f1 !== null ? [f1, k1] : [f2!, k2];
    if (fmt !== null) {
      row = fmt.map(([side, n]) => {
        if (side === 0) return k < src.length ? src[k] : empty;
        const f = side === 1 ? f1 : f2;
        return f !== null && n < f.length ? f[n] : empty;
      });
    } else {
      row = [k < src.length ? src[k] : ''];
      row.push(...(f1 ?? []).filter((_, i) => i !== k1), ...(f2 ?? []).filter((_, i) => i !== k2));
    }
    lines.push(row.join(outSep));
    if (lines.length >= 1024) { // written as it goes: one repeated key can pair up into far more rows than fit in memory
      ctx.checkTime();
      ctx.stdout.write(lines.map(x => x + '\n').join(''));
      lines.length = 0;
    }
  };
  let i = 0, j = 0;
  if (a.has('\u0001') && (left.length || right.length)) {
    emit(left.length ? left[0] : null, right.length ? right[0] : null);
    i = j = 1;
  }
  while (i < left.length || j < right.length) {
    ctx.checkTime();
    const aKey = i < left.length ? key(left[i], 0) : null;
    const bKey = j < right.length ? key(right[j], 1) : null;
    if (bKey === null || (aKey !== null && lessCp(aKey, bKey))) {
      if (unpaired.has(1)) emit(left[i], null);
      i++;
    } else if (aKey === null || lessCp(bKey, aKey)) {
      if (unpaired.has(2)) emit(null, right[j]);
      j++;
    } else {
      let i2 = i, j2 = j;
      while (i2 < left.length && key(left[i2], 0) === aKey) i2++;
      while (j2 < right.length && key(right[j2], 1) === aKey) j2++;
      if (!only.size) for (const x of left.slice(i, i2)) for (const y of right.slice(j, j2)) emit(x, y);
      [i, j] = [i2, j2];
    }
  }
  ctx.stdout.write(lines.map(x => x + '\n').join(''));
  return 0;
}

function paste(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('paste'));
  let delims = [...unescape(a.value('d') ?? '\t')];
  if (delims.length === 0) delims = [''];
  const D = (i: number) => delims[i % delims.length];
  const join = (cells: string[]) => cells.reduce((s, c, i) => (i === 0 ? c : s + D(i - 1) + c), '');
  let code = 0;
  let columns: string[][];
  const dashes = a.positional.filter(f => f === '-').length;
  if (dashes > 1 && !a.has('s')) {
    // several "-" operands take turns reading lines from stdin (seq 6 | paste - - -)
    const stdinLines = splitLines(ctx.stdinText);
    columns = [];
    let k = 0;
    for (const f of a.positional) {
      if (f === '-') { const start = k++; columns.push(stdinLines.filter((_, idx) => idx % dashes === start)); }
      else columns.push(...[...inputs(ctx, [f], () => { code = 1; })].map(x => splitLines(x[1])));
    }
  } else columns = [...inputs(ctx, a.positional, () => { code = 1; })].map(x => splitLines(x[1]));
  let out = '';
  if (a.has('s')) for (const col of columns) out += join(col) + '\n';
  else {
    const rows = columns.reduce((m, c) => Math.max(m, c.length), 0);
    for (let r = 0; r < rows; r++) { checkEvery(ctx, r); out += join(columns.map(c => c[r] ?? '')) + '\n'; }
  }
  ctx.stdout.write(out);
  return code;
}

function column(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('column'));
  let code = 0;
  const lines = [...inputs(ctx, a.positional, () => { code = 1; })].flatMap(x => splitLines(x[1]));
  if (!a.has('t')) { ctx.stdout.write(lines2text(lines)); return code; }
  const sep = a.value('s');
  const rows = lines.filter(l => l.trim().length > 0).map(l => (sep !== undefined
    ? l.split(new RegExp(`[${unescape(sep).replace(/[\\\]^-]/g, '\\$&')}]`)).filter(Boolean)
    : l.split(/\s+/).filter(Boolean)));
  const outSep = a.value('o') ?? '  ';
  const cols = rows.reduce((m, r) => Math.max(m, r.length), 0);
  const widths = Array.from({ length: cols }, (_, c) => rows.reduce((m, r) => Math.max(m, r[c]?.length ?? 0), 0));
  ctx.stdout.write(rows.map(r => r.map((cell, i) => (i === r.length - 1 ? cell : cell.padEnd(widths[i]))).join(outSep) + '\n').join(''));
  return code;
}

function seq(ctx: CommandContext): number {
  const args = ctx.args;
  const nums: string[] = [];
  let sep = '\n', equalWidth = false, fmt: string | null = null;
  const isNum = (x: string) => /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/.test(x);
  for (let i = 0; i < args.length; i++) {
    const x = args[i];
    if (isNum(x)) nums.push(x);
    else if (x === '-w' || x === '--equal-width') equalWidth = true;
    else if (x === '-s') { if (i + 1 >= args.length) throw new ShellError(specMessage('seq', 'optionRequiresArgument', { option: 's' })); sep = args[++i]; }
    else if (x.startsWith('-s')) sep = x.slice(2);
    else if (x.startsWith('--separator=')) sep = x.slice(12);
    else if (x === '-f' || x === '--format') { if (i + 1 >= args.length) throw new ShellError(specMessage('seq', 'optionRequiresArgument', { option: 'f' })); fmt = args[++i]; }
    else if (x.startsWith('-f')) fmt = x.slice(2);
    else if (x.startsWith('--format=')) fmt = x.slice(9);
    else if (x === '--') continue;
    else throw new ShellError(specMessage('seq', 'invalidOption', { option: x.replace(/^-+/, '') }));
  }
  if (nums.length === 0 || nums.length > 3) throw new UsageException(nums.length === 0 ? specMessage('seq', 'missingOperand', {}) : specMessage('seq', 'extraOperand', { operand: nums[3] }));
  const v = nums.map(Number);
  const first = v.length > 1 ? v[0] : 1, step = v.length === 3 ? v[1] : 1, last = v[v.length - 1];
  if (step === 0) throw new ShellError(specMessage('seq', 'zeroIncrement', {}));
  const decimals = Math.max(...nums.map(x => (/e/i.test(x) || !x.includes('.') ? 0 : x.length - x.indexOf('.') - 1)));
  const count = Math.floor((last - first) / step + 1e-9) + 1;
  if (count > 1_000_000) throw new ShellError(specMessage('seq', 'tooManyNumbers', {}));
  let items: string[] = [];
  for (let k = 0; k < count; k++) {
    const x = first + k * step; // not a running sum, so 0.1 steps don't drift
    items.push(fmt !== null ? printfFormat(fmt, [String(x)]) : x.toFixed(decimals));
  }
  if (equalWidth && items.length > 0) {
    const w = items.reduce((m, s) => Math.max(m, s.length), 0);
    items = items.map(s => (s.startsWith('-') ? '-' + s.slice(1).padStart(w - 1, '0') : s.padStart(w, '0')));
  }
  if (items.length > 0) ctx.stdout.write(items.join(unescape(sep)) + '\n');
  return 0;
}

// ---------- registration ----------

export function registerText(shell: Shell): void {
  shell.register('grep', ctx => grep(ctx));
  // Ubuntu's egrep and fgrep run `grep -E` / `grep -F`, so their messages say "grep:"
  for (const [alias, flag] of [['egrep', '-E'], ['fgrep', '-F']]) {
    shell.register(alias, ctx => {
      const sub = Object.create(ctx, { name: { value: 'grep' }, args: { value: [flag, ...ctx.args] } }) as CommandContext;
      try { return grep(sub); } catch (e) {
        if (!(e instanceof ShellError) || e instanceof UsageException) throw e; // usage errors are worded by the shell
        ctx.stderr.write(`grep: ${e.message}\n`);
        return e.exitCode;
      }
    });
  }
  shell.register('head', headTail);
  shell.register('tail', headTail);
  shell.register('wc', wc);
  shell.register('sort', sort);
  shell.register('uniq', uniq);
  shell.register('cut', cut);
  shell.register('tr', tr);
  shell.register('tee', tee);
  shell.register('xargs', xargs);
  shell.register('sed', run);
  shell.register('nl', nl);
  shell.register('rev', ctx => {
    let code = 0;
    ctx.stdout.write([...inputs(ctx, ctx.args, () => { code = 1; })].map(x => lines2text(splitLines(x[1]).map(l => [...l].reverse().join('')))).join(''));
    return code;
  });
  shell.register('tac', ctx => {
    let code = 0;
    ctx.stdout.write([...inputs(ctx, ctx.args, () => { code = 1; })].map(x => lines2text(splitLines(x[1]).reverse())).join(''));
    return code;
  });
  shell.register('paste', paste);
  shell.register('comm', comm);
  shell.register('join', join);
  shell.register('column', column);
  shell.register('seq', seq);
  shell.register('diff', diff);
}
