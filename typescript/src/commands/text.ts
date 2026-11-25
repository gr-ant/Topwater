import type { Shell } from '../shell.js';
import { Args, ShellError, UsageException, quoteL, inputBytes, inputs, reason, splitLines, type CommandContext, type CommandHandler } from '../command.js';
import { decodeBytes, encodeText as encodeBytes } from '../bytes.js';
import { escapeRegex, posixToJs } from '../regex.js';
import { globRegex } from '../glob.js';
import { printfFormat, unescape } from '../format.js';
import { VirtualFileSystem } from '../vfs.js';

const checkEvery = (ctx: CommandContext, i: number) => { if ((i & 7) === 0) ctx.checkTime(); };
const lines2text = (lines: string[]) => (lines.length ? lines.join('\n') + '\n' : '');

/** Output gathered in memory and written once, even when the command is cut short. */
function buffered(ctx: CommandContext) {
  const parts: string[] = [];
  return { w: (s: string) => { parts.push(s); }, flush: () => { if (parts.length) ctx.stdout.write(parts.join('')); parts.length = 0; } };
}

function intArg(s: string, what: string): number {
  const digits = s.replace(/^[+-]/, '');
  if (!/^\d+$/.test(digits)) throw new ShellError(`invalid number of ${what}: ${quoteL(s)}`);
  return parseInt(digits, 10);
}

// ---------- grep ----------

function grepRegex(patterns: string[], o: { E: boolean; F: boolean; i: boolean; w: boolean; x: boolean; P: boolean }): RegExp {
  // JS has no \K: a leading X\KY means (?<=X)Y
  const keep = (p: string) => { const k = p.indexOf('\\K'); return k > 0 ? `(?<=${p.slice(0, k)})${p.slice(k + 2)}` : p; };
  const parts = patterns.map(p => (o.F ? escapeRegex(p) : o.P ? keep(p) : posixToJs(p, o.E)));
  let re = parts.length === 1 ? parts[0] : parts.map(p => `(?:${p})`).join('|');
  if (o.w) re = `(?<!\\w)(?:${re})(?!\\w)`;
  if (o.x) re = `^(?:${re})$`;
  try { return new RegExp(re, 'g' + (o.i ? 'i' : '')); } catch (e) {
    throw new ShellError(`invalid regular expression: ${(e as Error).message.replace(/^Invalid regular expression: /, '')}`, 2);
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
      if (value.length === 0) { if (i >= args.length) throw new UsageException(`option requires an argument -- '${kind}'`); value = args[i++]; }
    } else if (lng) {
      kind = lng[1][0];
      value = lng[2] ?? (i < args.length ? args[i++] : '');
    } else { rest.push(arg); continue; }
    if (kind === 'e') patterns.push(value);
    else patterns.push(...splitLines(value === '-' ? ctx.stdinText : ctx.fs.readText(ctx.resolvePath(value))));
  }
  const a = Args.parse(rest, 'invcrRlLEFwxohHqsIaZPU', 'ABCm',
    'binary-files=I,text=a,ignore-case=i,line-number=n,invert-match=v,count=c,recursive=r,files-with-matches=l,files-without-match=L,'
    + 'extended-regexp=E,fixed-strings=F,word-regexp=w,line-regexp=x,only-matching=o,quiet=q,silent=q,max-count=m,perl-regexp=P,'
    + 'no-filename=h,with-filename=H,no-messages=s,color=U,colour=U,line-buffered=U,after-context=A,before-context=B,context=C,'
    + 'dereference-recursive=R');
  const positional = [...a.positional];
  if (patterns.length === 0) {
    if (positional.length === 0) throw new UsageException('');
    patterns.push(positional.shift()!);
  }
  patterns = patterns.flatMap(p => (p.length > 0 ? p.split('\n') : [p])); // a pattern with newlines is several patterns

  const recursive = a.has('r') || a.has('R');
  const implicitDot = recursive && positional.length === 0;
  if (implicitDot) positional.push('.');
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
    if (!recursive) { if (!quietErr) ctx.stderr.write(`grep: ${f}: Is a directory\n`); error = true; continue; }
    const walk = (dirAbs: string, display: string) => {
      let names: string[];
      try { names = ctx.fs.list(dirAbs); } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        if (!quietErr) ctx.stderr.write(`grep: ${display}: Permission denied\n`);
        error = true;
        return;
      }
      for (const name of names) {
        ctx.checkTime();
        const child = VirtualFileSystem.normalize(dirAbs, name);
        const childDisplay = display === '.' && implicitDot ? name : display.replace(/\/+$/, '') + '/' + name;
        if (ctx.fs.isDirectory(child)) { if (!excludeDir.some(r => r.test(name))) walk(child, childDisplay); }
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
    for (const [name, text] of inputs(ctx, files, () => { error = true; }, quietErr)) {
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
      if (binary) { if (hits.length > 0) out.w(`grep: ${name}: binary file matches\n`); continue; }
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
  const a = Args.parse(Args.numericShorthand(ctx.args), head ? 'qv' : 'qvfF', 'nc', 'lines=n,bytes=c,quiet=q,silent=q,verbose=v' + (head ? '' : ',follow=f,retry=F'));
  // a bad count is reported before any file is opened
  const lines0 = a.value('n'), bytes0 = a.value('c');
  if (lines0 !== undefined) intArg(lines0, 'lines');
  if (bytes0 !== undefined) intArg(bytes0, 'bytes');
  let code = 0;
  const all = [...inputBytes(ctx, a.positional, () => { code = 1; })];
  all.forEach(([name, data], k) => {
    if ((all.length > 1 && !a.has('q')) || a.has('v')) ctx.stdout.write((k > 0 ? '\n' : '') + `==> ${name} <==\n`);
    const bytes = a.value('c');
    if (bytes !== undefined) {
      const n = intArg(bytes, 'bytes');
      const chunk = head
        ? bytes.startsWith('-') ? data.subarray(0, Math.max(0, data.length - n)) : data.subarray(0, Math.min(n, data.length))
        : bytes.startsWith('+') ? data.subarray(Math.min(data.length, Math.max(0, n - 1))) : n > 0 ? data.subarray(Math.max(0, data.length - n)) : data.subarray(0, 0);
      ctx.stdout.write(decodeBytes(chunk));
      return;
    }
    const text = decodeBytes(data);
    const spec = a.value('n') ?? '10';
    const count = intArg(spec, 'lines');
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
  const a = Args.parse(ctx.args, 'lwcmL', '', 'lines=l,words=w,bytes=c,chars=m,max-line-length=L');
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
        if (v === undefined) throw new ShellError("option requires an argument -- 'k'", 2);
        specs.push(v);
        if (j > 1) rest.push(arg.slice(0, j));
        continue;
      }
    }
    rest.push(arg);
  }
  const a = Args.parse(rest, 'rnufhVgsbcCdiMRzmZ', 'toST',
    'reverse=r,numeric-sort=n,unique=u,ignore-case=f,field-separator=t,output=o,general-numeric-sort=g,human-numeric-sort=h,'
    + 'version-sort=V,stable=s,ignore-leading-blanks=b,check=c,dictionary-order=d,month-sort=M,random-sort=R,ignore-nonprinting=i,'
    + 'zero-terminated=z,merge=m,buffer-size=S,temporary-directory=T,debug=Z');
  const sep = a.value('t') !== undefined ? unescape(a.value('t')!) : null;
  if (sep !== null && sep.length !== 1) throw new ShellError(`multi-character tab '${a.value('t')}'`, 2);
  const globalOpts = [...ORDERING].filter(c => a.has(c)).join('');
  const keys: SortKey[] = specs.map(spec => {
    const m = /^(\d+)(?:\.(\d+))?([a-zA-Z]*)(?:,(\d+)(?:\.(\d+))?([a-zA-Z]*))?$/.exec(spec);
    if (!m || parseInt(m[1], 10) === 0) throw new ShellError(`invalid number at field start: invalid count at start of ${quoteL(spec)}`, 2);
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
        if (a.has('c')) ctx.stderr.write(`sort: ${a.positional.length > 0 ? named[0][0] : '-'}:${i + 1}: disorder: ${items[i].line}\n`);
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
  const a = Args.parse(ctx.args, 'cduiD', 'fsw', 'count=c,repeated=d,unique=u,ignore-case=i,skip-fields=f,skip-chars=s,check-chars=w');
  const skipF = a.value('f') !== undefined ? intArg(a.value('f')!, 'fields to skip') : 0;
  const skipS = a.value('s') !== undefined ? intArg(a.value('s')!, 'bytes to skip') : 0;
  const maxW = a.value('w') !== undefined ? intArg(a.value('w')!, 'bytes to compare') : Infinity;
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
    if (!m || (m[1] === '' && m[3] === '') || (!m[2] && m[3])) throw new ShellError(`invalid field value '${part}'`);
    const lo = m[1] === '' ? 1 : parseInt(m[1], 10);
    const hi = !m[2] ? lo : m[3] === '' ? Infinity : parseInt(m[3], 10);
    if ((m[1] !== '' && lo === 0) || (m[2] === '' && lo === 0)) throw new ShellError('fields and positions are numbered from 1');
    if (hi < lo) throw new ShellError('invalid decreasing range');
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
  const a = Args.parse(ctx.args, 'snz', 'dfcbO', 'delimiter=d,fields=f,characters=c,bytes=b,only-delimited=s,complement=z,output-delimiter=O');
  const delim = a.value('d') ?? '\t';
  if ([...delim].length !== 1) throw new ShellError('the delimiter must be a single character');
  const bytesMode = a.value('b') !== undefined && a.value('c') === undefined;
  const chars = a.value('c') ?? a.value('b');
  const spec = chars ?? a.value('f');
  if (spec === undefined) throw new UsageException('you must specify a list of bytes, characters, or fields');
  const ranges = parseList(spec);
  const complement = a.has('z');
  const outDelim = a.value('O');
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
      if (close > 0 && cps[close - 1] === ':') throw new ShellError(`invalid character class '${name}'`);
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
      if (lo > hi) throw new ShellError(`range-endpoints of '${toks[i].c}-${toks[i + 2].c}' are in reverse collating sequence order`);
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
  const a = Args.parse(ctx.args, 'dscCt', '', 'delete=d,squeeze-repeats=s,complement=c,truncate-set1=t');
  if (a.positional.length === 0) throw new UsageException('missing operand');
  const del = a.has('d'), squeezeOpt = a.has('s'), complement = a.has('c') || a.has('C');
  if (!del && !squeezeOpt && a.positional.length < 2) throw new ShellError(`missing operand after '${a.positional[0]}'\nTwo strings must be given when translating.`);
  if (a.positional.length > 2) throw new UsageException(`extra operand ${quoteL(a.positional[2])}`);
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
  const a = Args.parse(ctx.args, 'ai', '', 'append=a,ignore-interrupts=i');
  let code = 0;
  const text = ctx.stdinText;
  for (const f of a.positional) {
    const path = ctx.resolvePath(f);
    if (path === '/dev/stdout' || path === '/dev/stderr') { (path === '/dev/stderr' ? ctx.stderr : ctx.stdout).write(text); continue; }
    try { if (a.has('a')) ctx.fs.appendText(path, text); else ctx.fs.writeText(path, text); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      ctx.stderr.write(`tee: ${f}: ${reason(e)}\n`);
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
      if (j < 0 || text.slice(i + 1, j).includes('\n')) throw new ShellError(`unmatched ${c === "'" ? 'single' : 'double'} quote; by default quotes are special to xargs unless you use the -0 option`);
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
  const a = Args.parse(opts, '0rtpxi', 'InLdPsEa', 'null=0,no-run-if-empty=r,verbose=t,max-args=n,max-lines=L,delimiter=d,'
    + 'max-procs=P,replace=I,arg-file=a,interactive=p,exit=x,max-chars=s,eof=E');
  const command = ctx.args.slice(i);
  if (command.length === 0) command.push('echo');
  const input = a.value('a') !== undefined ? ctx.fs.readText(ctx.resolvePath(a.value('a')!)) : ctx.stdinText;
  const replace = a.value('I') ?? (a.has('i') ? '{}' : undefined);
  const posNum = (v: string, opt: string) => { if (!/^\d+$/.test(v) || parseInt(v, 10) < 1) throw new ShellError(`value ${v} for -${opt} option should be >= 1`); return parseInt(v, 10); };
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
    const rc = await ctx.invoke(argv);
    if (rc === 127) return 127;
    if (rc === 255) { ctx.stderr.write(`xargs: ${argv[0]}: exited with status 255; aborting\n`); return 124; }
    if (rc !== 0) code = 123;
  }
  return code;
}

// ---------- nl tac rev paste column seq ----------

function nl(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, '', 'bswnvi');
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

function paste(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, 's', 'd', 'serial=s,delimiters=d');
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
  const a = Args.parse(ctx.args, 'tn', 'so', 'table=t,separator=s,output-separator=o');
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
    else if (x === '-s') { if (i + 1 >= args.length) throw new ShellError("option requires an argument -- 's'"); sep = args[++i]; }
    else if (x.startsWith('-s')) sep = x.slice(2);
    else if (x.startsWith('--separator=')) sep = x.slice(12);
    else if (x === '-f' || x === '--format') { if (i + 1 >= args.length) throw new ShellError("option requires an argument -- 'f'"); fmt = args[++i]; }
    else if (x.startsWith('-f')) fmt = x.slice(2);
    else if (x.startsWith('--format=')) fmt = x.slice(9);
    else if (x === '--') continue;
    else throw new ShellError(`invalid option -- '${x.replace(/^-+/, '')}'`);
  }
  if (nums.length === 0 || nums.length > 3) throw new UsageException(nums.length === 0 ? 'missing operand' : `extra operand ${quoteL(nums[3])}`);
  const v = nums.map(Number);
  const first = v.length > 1 ? v[0] : 1, step = v.length === 3 ? v[1] : 1, last = v[v.length - 1];
  if (step === 0) throw new ShellError("invalid Zero increment value: '0'");
  const decimals = Math.max(...nums.map(x => (/e/i.test(x) || !x.includes('.') ? 0 : x.length - x.indexOf('.') - 1)));
  const count = Math.floor((last - first) / step + 1e-9) + 1;
  if (count > 1_000_000) throw new ShellError('too many numbers');
  let items: string[] = [];
  for (let k = 0; k < count; k++) {
    const x = first + k * step; // not a running sum, so 0.1 steps don't drift
    items.push(fmt !== null ? printfFormat(fmt, [String(x)]) : x.toFixed(decimals));
  }
  if (equalWidth && items.length > 0) {
    const w = Math.max(...items.map(s => s.length));
    items = items.map(s => (s.startsWith('-') ? '-' + s.slice(1).padStart(w - 1, '0') : s.padStart(w, '0')));
  }
  if (items.length > 0) ctx.stdout.write(items.join(unescape(sep)) + '\n');
  return 0;
}

// ---------- diff ----------

interface DOp { t: 'eq' | 'del' | 'ins'; a: number; b: number }

function diffOps(ctx: CommandContext, x: string[], y: string[]): DOp[] {
  if (x.length * y.length > 25_000_000) throw new ShellError('files too large to compare', 2);
  const w = y.length + 1;
  const lcs = new Int32Array((x.length + 1) * w);
  for (let i = x.length - 1; i >= 0; i--) {
    checkEvery(ctx, i);
    for (let j = y.length - 1; j >= 0; j--) lcs[i * w + j] = x[i] === y[j] ? lcs[(i + 1) * w + j + 1] + 1 : Math.max(lcs[(i + 1) * w + j], lcs[i * w + j + 1]);
  }
  const ops: DOp[] = [];
  let i = 0, j = 0;
  while (i < x.length || j < y.length) {
    if (i < x.length && j < y.length && x[i] === y[j]) ops.push({ t: 'eq', a: i++, b: j++ });
    else if (j < y.length && (i === x.length || lcs[i * w + j + 1] >= lcs[(i + 1) * w + j])) ops.push({ t: 'ins', a: i, b: j++ });
    else ops.push({ t: 'del', a: i++, b: j });
  }
  // deletions before insertions inside each change block, like GNU diff
  for (let k = 0; k < ops.length;) {
    if (ops[k].t === 'eq') { k++; continue; }
    let e = k;
    while (e < ops.length && ops[e].t !== 'eq') e++;
    const block = ops.slice(k, e);
    ops.splice(k, block.length, ...block.filter(o => o.t === 'del'), ...block.filter(o => o.t === 'ins'));
    k = e;
  }
  return ops;
}

const dRange = (start: number, count: number) => (count === 1 ? `${start}` : `${start},${start + count - 1}`);

function diffNormal(ops: DOp[], x: string[], y: string[]): string {
  let out = '';
  for (let i = 0; i < ops.length;) {
    if (ops[i].t === 'eq') { i++; continue; }
    let j = i;
    while (j < ops.length && ops[j].t !== 'eq') j++;
    const block = ops.slice(i, j);
    const dels = block.filter(o => o.t === 'del'), ins = block.filter(o => o.t === 'ins');
    const aStart = Math.min(...block.map(o => o.a)), bStart = Math.min(...block.map(o => o.b));
    if (dels.length && ins.length) out += `${dRange(aStart + 1, dels.length)}c${dRange(bStart + 1, ins.length)}\n`;
    else if (dels.length) out += `${dRange(aStart + 1, dels.length)}d${bStart}\n`;
    else out += `${aStart}a${dRange(bStart + 1, ins.length)}\n`;
    for (const d of dels) out += '< ' + x[d.a] + '\n';
    if (dels.length && ins.length) out += '---\n';
    for (const n of ins) out += '> ' + y[n.b] + '\n';
    i = j;
  }
  return out;
}

function diffUnified(ops: DOp[], x: string[], y: string[], an: string, bn: string, context: number): string {
  let out = `--- ${an}\n+++ ${bn}\n`;
  const changes = ops.flatMap((o, i) => (o.t !== 'eq' ? [i] : []));
  const rng = (start: number, len: number) => (len === 1 ? `${start}` : `${start},${len}`);
  for (let c = 0; c < changes.length;) {
    const first = changes[c];
    let last = first;
    while (c + 1 < changes.length && changes[c + 1] - last <= 2 * context + 1) last = changes[++c];
    c++;
    const hunk = ops.slice(Math.max(0, first - context), Math.min(ops.length, last + context + 1));
    const aLen = hunk.filter(o => o.t !== 'ins').length, bLen = hunk.filter(o => o.t !== 'del').length;
    const aStart = Math.min(...hunk.map(o => o.a)) + (aLen > 0 ? 1 : 0), bStart = Math.min(...hunk.map(o => o.b)) + (bLen > 0 ? 1 : 0);
    out += `@@ -${rng(aStart, aLen)} +${rng(bStart, bLen)} @@\n`;
    for (const o of hunk) out += (o.t === 'eq' ? ' ' + x[o.a] : o.t === 'del' ? '-' + x[o.a] : '+' + y[o.b]) + '\n';
  }
  return out;
}

function diff(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, 'uqsiwbBNr', 'U', 'unified=u,brief=q,report-identical-files=s,ignore-case=i,ignore-all-space=w,ignore-space-change=b,recursive=r');
  if (a.positional.length !== 2) throw new UsageException(a.positional.length < 2 ? `missing operand after '${a.positional[a.positional.length - 1] ?? 'diff'}'` : `extra operand '${a.positional[2]}'`);
  const key = (l: string) => {
    if (a.has('w')) l = l.replace(/\s+/g, '');
    else if (a.has('b')) l = l.replace(/\s+$/, '').replace(/\s+/g, ' ');
    return a.has('i') ? l.toLowerCase() : l;
  };
  const read = (p: string): string => {
    if (p === '-') return ctx.stdinText;
    const abs = ctx.resolvePath(p);
    if (!ctx.fs.exists(abs)) throw new ShellError(`${p}: No such file or directory`, 2);
    if (ctx.fs.isDirectory(abs)) throw new ShellError(`${p}: Is a directory`, 2);
    return ctx.fs.readPipeText(abs);
  };
  const context = a.value('U') !== undefined ? parseInt(a.value('U')!, 10) : 3;
  let differ = false;
  const out = buffered(ctx);
  const files = (an: string, bn: string, header: boolean) => {
    const x = splitLines(read(an)), y = splitLines(read(bn));
    const ops = diffOps(ctx, x.map(key), y.map(key));
    if (ops.every(o => o.t === 'eq')) { if (a.has('s')) out.w(`Files ${an} and ${bn} are identical\n`); return; }
    differ = true;
    if (a.has('q')) { out.w(`Files ${an} and ${bn} differ\n`); return; }
    if (header) out.w(`diff ${a.has('u') ? '-ru' : '-r'} ${an} ${bn}\n`.replace('-ru', '-r -u'));
    out.w(a.has('u') || a.value('U') !== undefined ? diffUnified(ops, x, y, an, bn, context) : diffNormal(ops, x, y));
  };
  const dirs = (an: string, bn: string) => {
    const names = new Set([...ctx.fs.list(ctx.resolvePath(an)), ...ctx.fs.list(ctx.resolvePath(bn))]);
    for (const name of [...names].sort()) {
      const pa = `${an.replace(/\/+$/, '')}/${name}`, pb = `${bn.replace(/\/+$/, '')}/${name}`;
      const ea = ctx.fs.exists(ctx.resolvePath(pa)), eb = ctx.fs.exists(ctx.resolvePath(pb));
      if (!ea || !eb) { differ = true; out.w(`Only in ${ea ? an : bn}: ${name}\n`); continue; }
      if (ctx.fs.isDirectory(ctx.resolvePath(pa)) && ctx.fs.isDirectory(ctx.resolvePath(pb))) dirs(pa, pb);
      else files(pa, pb, true);
    }
  };
  try {
    const [an, bn] = a.positional;
    if (a.has('r') && an !== '-' && bn !== '-' && ctx.fs.isDirectory(ctx.resolvePath(an)) && ctx.fs.isDirectory(ctx.resolvePath(bn))) dirs(an, bn);
    else files(an, bn, false);
  } finally { out.flush(); }
  return differ ? 1 : 0;
}

// ---------- registration ----------

export function registerText(shell: Shell): void {
  shell.register('grep', ctx => grep(ctx), 'Search text with regular expressions');
  // Ubuntu's egrep and fgrep run `grep -E` / `grep -F`, so their messages say "grep:"
  for (const [alias, flag] of [['egrep', '-E'], ['fgrep', '-F']]) {
    shell.register(alias, ctx => {
      const sub = Object.create(ctx, { name: { value: 'grep' }, args: { value: [flag, ...ctx.args] } }) as CommandContext;
      try { return grep(sub); } catch (e) {
        if (!(e instanceof ShellError) || e instanceof UsageException) throw e; // usage errors are worded by the shell
        ctx.stderr.write(`grep: ${e.message}\n`);
        return e.exitCode;
      }
    }, `Search text, like grep ${flag}`);
  }
  shell.register('head', headTail, 'Print the first lines of files');
  shell.register('tail', headTail, 'Print the last lines of files');
  shell.register('wc', wc, 'Count lines, words and bytes');
  shell.register('sort', sort, 'Sort lines');
  shell.register('uniq', uniq, 'Collapse adjacent duplicate lines');
  shell.register('cut', cut, 'Select fields or characters from lines');
  shell.register('tr', tr, 'Translate or delete characters');
  shell.register('tee', tee, 'Copy stdin to stdout and files');
  shell.register('xargs', xargs, 'Build and run commands from stdin');
  shell.register('nl', nl, 'Number lines');
  shell.register('tac', ctx => {
    let code = 0;
    ctx.stdout.write([...inputs(ctx, ctx.args, () => { code = 1; })].map(x => lines2text(splitLines(x[1]).reverse())).join(''));
    return code;
  }, 'Print lines in reverse order');
  shell.register('rev', ctx => {
    let code = 0;
    ctx.stdout.write([...inputs(ctx, ctx.args, () => { code = 1; })].map(x => lines2text(splitLines(x[1]).map(l => [...l].reverse().join('')))).join(''));
    return code;
  }, 'Reverse each line');
  shell.register('paste', paste, 'Merge lines of files');
  shell.register('column', column, 'Format input into aligned columns (-t)');
  shell.register('seq', seq, 'Print a sequence of numbers');
  shell.register('diff', diff, 'Compare files line by line (-u for unified)');
}
