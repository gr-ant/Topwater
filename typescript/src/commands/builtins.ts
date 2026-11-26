// Shell built-ins (cd, read, declare, set, trap, ...) and the small system commands (date, uname, id, ...).
import { COMMANDS, DIAGNOSTICS, ERRNO, GETOPT, diagnostic } from '../spec.generated.js';
import { Args, specFormat, specOf, specMessage, ShellError, UsageException, isShellBuiltin, quoteL, StdinBuffer, DeadlineExceeded, type CommandContext, type CommandHandler, type Deadline } from '../command.js';
import { isName } from '../parser.js';
import { evalArith } from '../arith.js';
import { printfFormat, unescape, shellQuote, quoteValue, strftime } from '../format.js';
import { VirtualFileSystem, ordinal } from '../vfs.js';
import { splitFields, ExitSignal, ReturnSignal, type Shell, type Io } from '../shell.js';

const ioOf = (ctx: CommandContext): Io => ({ stdin: ctx.stdin, out: ctx.stdout, err: ctx.stderr, fds: ctx.fds });
const line = (ctx: CommandContext, s: string) => ctx.stdout.write(s + '\n');

/** Why `name` can't be run as a program by env or timeout ([errno, exit status]), or null if it can. */
const ISO_UNITS = ['hours', 'minutes', 'date', 'seconds', 'ns'];
const ISO_FORMATS = ['%Y-%m-%dT%H%:z', '%Y-%m-%dT%H:%M%:z', '%Y-%m-%d', '%Y-%m-%dT%H:%M:%S%:z', '%Y-%m-%dT%H:%M:%S,%N%:z'];

export function cannotRun(ctx: CommandContext, name: string): [string, number] | null {
  if (!name.includes('/')) return ctx.shell.locate(name) !== null ? null : [ERRNO.ENOENT, 127]; // a shell builtin is no program
  const abs = ctx.resolvePath(name);
  if (!ctx.fs.exists(abs)) { const why = ctx.fs.whyMissing(abs); return [why, why === ERRNO.EACCES ? 126 : 127]; }
  return ctx.fs.isDirectory(abs) || !ctx.fs.canExecute(abs) ? [ERRNO.EACCES, 126] : null;
}

/** umask's u=rwx,g+w,o-x applied to the permissions `perm` (what the mask leaves). */
function symbolicMode(mode: string, perm: number): number {
  for (const clause of mode.split(',')) {
    let k = 0, who = 0;
    for (; k < clause.length && 'ugoa'.includes(clause[k]); k++) who |= { u: 0o700, g: 0o070, o: 0o007, a: 0o777 }[clause[k] as 'u']!;
    if (who === 0) who = 0o777;
    if (k >= clause.length) throw new ShellError(specMessage('umask', 'invalidOperator', { char: ' ' }));
    while (k < clause.length) {
      const op = clause[k++];
      if (!'+-='.includes(op)) throw new ShellError(specMessage('umask', 'invalidOperator', { char: op }));
      let bits = 0;
      for (; k < clause.length && !'+-='.includes(clause[k]); k++) {
        const b = { r: 0o444, w: 0o222, x: 0o111 }[clause[k] as 'r'];
        if (b === undefined) throw new ShellError(specMessage('umask', 'invalidSymbolic', { char: clause[k] }));
        bits |= b;
      }
      bits &= who;
      perm = op === '+' ? perm | bits : op === '-' ? perm & ~bits : (perm & ~who) | bits;
    }
  }
  return perm;
}

// ---------- test / [ ----------

/** A unary test (-f, -z, ...). */
export function testUnary(shell: Shell, op: string, arg: string): boolean {
  const path = shell.resolvePath(arg);
  const fs = shell.agentFs;
  switch (op) {
    case '-z': return arg.length === 0;
    case '-n': return arg.length > 0;
    case '-e': return fs.exists(path);
    case '-r': return fs.canRead(path);
    case '-w': return fs.canWrite(path);
    case '-x': return fs.canExecute(path);
    case '-f': return fs.isFile(path);
    case '-d': return fs.isDirectory(path);
    case '-s': return fs.isFile(path) && fs.stat(path).size > 0;
    case '-L': case '-h': return fs.isSymlink(path);
    case '-b': case '-c': case '-p': case '-S': case '-t': case '-g': case '-u': case '-k': return false; // no devices, pipes, terminals
    case '-O': case '-G': case '-N': return fs.exists(path);
    case '-v': return shell.isVariableSet(arg);
    case '-o': return shell.optionList.some(([n, on]) => n === arg && on);
  }
  throw new ShellError(specMessage('test', 'unaryExpected', { op: op }), 2);
}

/** -nt / -ot: a missing file is older than any existing one, as in bash. */
export function testNewer(shell: Shell, left: string, right: string): boolean {
  const time = (p: string) => (shell.agentFs.exists(shell.resolvePath(p)) ? shell.agentFs.stat(shell.resolvePath(p)).modified : null);
  const l = time(left), r = time(right);
  return l !== null && (r === null || l > r);
}

const BINARY_OPS = new Set(['=', '==', '!=', '<', '>', '-eq', '-ne', '-lt', '-le', '-gt', '-ge', '-nt', '-ot', '-ef']);

function testBinary(shell: Shell, left: string, op: string, right: string): boolean {
  switch (op) {
    case '=': case '==': return left === right;
    case '!=': return left !== right;
    case '<': return left < right;
    case '>': return left > right;
    case '-nt': return testNewer(shell, left, right);
    case '-ot': return testNewer(shell, right, left);
    case '-ef': return shell.agentFs.sameFile(shell.resolvePath(left), shell.resolvePath(right));
  }
  const okL = /^\s*[-+]?\d+\s*$/.test(left), okR = /^\s*[-+]?\d+\s*$/.test(right);
  if (!okL || !okR) throw new ShellError(specMessage('test', 'integerExpected', { value: okL ? right : left }), 2);
  const l = BigInt(left.trim()), r = BigInt(right.trim());
  return op === '-eq' ? l === r : op === '-ne' ? l !== r : op === '-lt' ? l < r : op === '-le' ? l <= r : op === '-gt' ? l > r : l >= r;
}

/** test / [ ]: POSIX rules for up to 3 arguments, then ! ( ) -a -o with the usual precedence. */
function testEvaluate(shell: Shell, a: string[]): boolean {
  const n = a.length;
  switch (n) {
    case 0: return false;
    case 1: return a[0].length > 0;
    case 2: return a[0] === '!' ? a[1].length === 0 : testUnary(shell, a[0], a[1]);
    case 3:
      if (BINARY_OPS.has(a[1])) return testBinary(shell, a[0], a[1], a[2]);
      if (a[1] === '-a' || a[1] === '-o') return a[1] === '-a' ? a[0].length > 0 && a[2].length > 0 : a[0].length > 0 || a[2].length > 0;
      if (a[0] === '!') return !testEvaluate(shell, a.slice(1));
      if (a[0] === '(' && a[2] === ')') return a[1].length > 0;
      throw new ShellError(specMessage('test', 'binaryExpected', { op: a[1] }), 2);
    case 4: if (a[0] === '!') return !testEvaluate(shell, a.slice(1));
  }
  let pos = 0;
  const peek = () => a[pos];
  const or = (): boolean => { let v = and(); while (peek() === '-o') { pos++; const r = and(); v = v || r; } return v; };
  const and = (): boolean => { let v = not(); while (peek() === '-a') { pos++; const r = not(); v = v && r; } return v; };
  const not = (): boolean => { if (peek() === '!') { pos++; return !not(); } return primary(); };
  const primary = (): boolean => {
    const t = peek();
    if (t === undefined) throw new ShellError(specMessage('test', 'argumentExpected'), 2);
    if (t === '(') {
      pos++;
      const v = or();
      if (peek() !== ')') throw new ShellError(specMessage('test', 'parenExpected'), 2);
      pos++;
      return v;
    }
    if (pos + 2 < n && BINARY_OPS.has(a[pos + 1])) { pos += 3; return testBinary(shell, t, a[pos - 2], a[pos - 1]); }
    if (t.length === 2 && t[0] === '-' && pos + 1 < n) { pos += 2; return testUnary(shell, t, a[pos - 1]); }
    pos++;
    return t.length > 0;
  };
  const result = or();
  if (pos < n) throw new ShellError(specMessage('test', 'tooManyArguments'), 2);
  return result;
}

// ---------- helpers ----------

const SHELL_KEYWORDS = new Set(['if', 'then', 'else', 'elif', 'fi', 'case', 'esac', 'for', 'select', 'while', 'until', 'do', 'done', 'in', 'function', 'time', '{', '}', '!', '[[', ']]', 'coproc']);

function parseCount(ctx: CommandContext, fallback: number): number {
  if (ctx.args.length === 0) return fallback;
  if (!/^-?\d+$/.test(ctx.args[0])) throw new ShellError(specMessage(ctx.name, 'numericArgument', { value: ctx.args[0] }), 2);
  return Number(ctx.args[0]);
}

function parseDuration(s: string): number {
  const m = /^(\d+(?:\.\d+)?|\.\d+)([smhd]?)$/.exec(s);
  if (!m) throw new ShellError(specMessage('sleep', 'invalidTimeInterval', { value: s }));
  const n = Number(m[1]);
  return n * ({ m: 60_000, h: 3_600_000, d: 86_400_000 } as Record<string, number>)[m[2]] || n * 1000;
}

function loopJump(ctx: CommandContext, isBreak: boolean): number {
  if (ctx.shell.loopDepth > 0) ctx.shell.loopControl(isBreak, Math.max(1, parseCount(ctx, 1)));
  ctx.stderr.write(`${ctx.shell.errPrefix}${ctx.name}: ${specMessage(ctx.name, 'onlyInLoop')}\n`);
  return 0;
}

/**
 * `read a b c`: the fields for each name. The last name gets the rest of the line less trailing IFS whitespace,
 * and a single trailing IFS delimiter is dropped when the rest is just one field ("x,y," gives b=y, as in bash).
 */
function readFields(text: string, ifs: string, count: number): string[] {
  const ws = [...' \t\n'].filter(c => ifs.includes(c));
  const isWs = (c: string) => ws.includes(c);
  const fields: string[] = [];
  let i = 0;
  while (i < text.length && isWs(text[i])) i++;
  while (fields.length < count - 1 && i < text.length) {
    const start = i;
    while (i < text.length && !ifs.includes(text[i])) i++;
    fields.push(text.slice(start, i));
    while (i < text.length && isWs(text[i])) i++;
    if (i < text.length && ifs.includes(text[i]) && !isWs(text[i])) { i++; while (i < text.length && isWs(text[i])) i++; }
  }
  const trimWs = (t: string) => { let e = t.length; while (e > 0 && isWs(t[e - 1])) e--; return t.slice(0, e); };
  let rest = trimWs(text.slice(i));
  if (rest.length > 0 && ifs.includes(rest[rest.length - 1]) && !isWs(rest[rest.length - 1]) && ![...trimWs(rest.slice(0, -1))].some(c => ifs.includes(c)))
    rest = trimWs(rest.slice(0, -1));
  if (i < text.length || fields.length < count) fields.push(rest);
  return fields;
}

/** `type NAME` / `command -V NAME`: what the name runs, or false (with a message) when nothing. */
function describe(ctx: CommandContext, a: string, all = false): boolean {
  const sh = ctx.shell;
  const alias = sh.aliases.get(a);
  const fn = sh.functions.get(a);
  const lines: string[] = [];
  if (alias !== undefined) lines.push(`${a} is aliased to \`${alias}'`);
  if (SHELL_KEYWORDS.has(a)) lines.push(`${a} is a shell keyword`);
  if (fn) lines.push(`${a} is a function\n${a} () \n${fn.source}`);
  if (isShellBuiltin(sh, a)) lines.push(`${a} is a shell builtin`);
  if (all) lines.push(...sh.locateAll(a).map(p => `${a} is ${p}`));
  else if (!lines.length) {
    const l = sh.locate(a);
    if (l) lines.push(l[1] ? `${a} is hashed (${l[0].startsWith('/') || l[0].startsWith('./') ? '' : './'}${l[0]})` : `${a} is ${l[0].includes('/') ? l[0] : sh.resolvePath(l[0])}`);
  }
  if (!lines.length) { ctx.stderr.write(`${ctx.shell.errPrefix}type: ${specMessage('type', 'notFound', { name: a })}\n`); return false; }
  for (const l of all ? lines : lines.slice(0, 1)) line(ctx, l);
  return true;
}

/** A variable's attribute letters in bash's order (a A i n r t x l u). */
export function attributeFlags(sh: Shell, name: string): string {
  const attrs = sh.varAttrs.get(name) ?? '';
  const array = sh.arrays.get(name);
  return [...'aAinrtxlu'].filter(c => (c === 'a' ? array !== undefined && !array.assoc : c === 'A' ? array?.assoc === true : c === 'x' ? sh.isExported(name) : attrs.includes(c))).join('');
}

/** `declare -p NAME`: a line that recreates the variable when evaluated; null when there is no such variable. */
function declareLine(sh: Shell, name: string): string | null {
  const flags = attributeFlags(sh, name);
  const head = `declare -${flags || '-'} ${name}`;
  // eslint-disable-next-line no-control-regex
  const q = (v: string) => (/[\x00-\x1f\x7f]/.test(v) ? shellQuote(v) : '"' + v.replace(/[\\"$`]/g, '\\$&') + '"');
  const a = sh.arrays.get(name);
  if (a) {
    if (a.size === 0) return head + '=()';
    const items = a.keys().map(k => `[${a.assoc && !/^[A-Za-z0-9_]+$/.test(k) ? q(k) : k}]=${q(a.get(k)!)}`);
    return head + '=(' + (a.assoc ? items.map(i => i + ' ').join('') : items.join(' ')) + ')';
  }
  const value = sh.environment.get(name);
  return value !== undefined ? head + '=' + q(value) : flags ? head : null;
}

function allVariableNames(sh: Shell): string[] {
  return [...new Set([...sh.environment.keys(), ...sh.arrays.keys()])].sort(ordinal);
}

function inputFor(ctx: CommandContext, u: string | undefined): StdinBuffer {
  if (u === undefined || u === '0') return ctx.stdin;
  const fd = /^\d+$/.test(u) ? Number(u) : -1;
  const f = ctx.fds?.get(fd);
  const input = f instanceof StdinBuffer ? f : ctx.shell.inputFd(fd);
  if (!input) throw new ShellError(specMessage(ctx.name, 'badFileDescriptor', { fd: u }));
  return input;
}

// ---------- date -d ----------

const MONTH_ABBR = ['jan', 'feb', 'mar', 'apr', 'may', 'jun', 'jul', 'aug', 'sep', 'oct', 'nov', 'dec'];
const DAY_ABBR = ['sun', 'mon', 'tue', 'wed', 'thu', 'fri', 'sat'];
const ZONE_HOURS: Record<string, number> = { utc: 0, gmt: 0, ut: 0, z: 0, est: -5, edt: -4, cst: -6, cdt: -5, mst: -7, mdt: -6, pst: -8, pdt: -7 };
const DATE_UNITS: Record<string, number> = { second: 1, sec: 1, minute: 60, min: 60, hour: 3600, day: 86400, week: 604800, fortnight: 1209600 };

/**
 * date -d, from GNU date's documented input formats: items in any order, each optional. Calendar dates
 * (2026-11-20, 11/20/2026, 20 Nov 2026, Nov 20, 2026, 20261120), times (13:30[:00[.5]], 1pm), zones
 * (UTC, Z, EST..., +0100, +01:00), a day of the week, today/now/yesterday/tomorrow, @EPOCH, and relative items
 * ("3 days ago", "next week", "+2 hours") applied last. Results are UTC (ms since the epoch).
 */
export function parseDate(text: string, now: number): number {
  const bad = new ShellError(specMessage('date', 'invalidDate', { value: text }));
  let s = text.trim().toLowerCase();
  const epoch = /^@(-?\d+(?:\.\d+)?)$/.exec(s);
  if (epoch) return Number(epoch[1]) * 1000;
  let year: number | undefined, month: number | undefined, day: number | undefined, zone: number | undefined;
  let time: number | undefined; // ms into the day
  let weekday: number | undefined, dayShift: number | undefined;
  const relative: [number, string][] = [];
  const MONTHS = '(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\\.?';
  let m: RegExpExecArray | null = null;
  const eat = (pattern: string) => {
    m = new RegExp('^(?:' + pattern + ')').exec(s);
    if (!m) return false;
    s = s.slice(m[0].length).replace(/^[ \t,]+/, '');
    return true;
  };
  const g = (i: number) => Number(m![i]);
  const has = (i: number) => m![i] !== undefined;
  const monthOf = (i: number) => MONTH_ABBR.indexOf(m![i].slice(0, 3)) + 1;
  const fullYear = (y: number, digits: number) => (digits > 2 ? y : y < 69 ? 2000 + y : 1900 + y);
  const setDate = (y: number | undefined, mo: number, d: number) => { if (day !== undefined) throw bad; year = y ?? year; month = mo; day = d; };
  const setZone = (minutes: number) => { if (zone !== undefined) throw bad; zone = minutes; };
  let afterTime = false;
  while (s.length > 0) {
    // a signed number right after a time of day is its zone ("10:00 +2" is UTC+2), anywhere else it's relative
    if (afterTime && eat('([+-])\\s*(\\d{1,2}:\\d{2}|\\d{1,4})\\b')) {
      afterTime = false;
      const v = m![2].replace(':', '');
      const minutes = v.length <= 2 ? Number(v) * 60 : Number(v.slice(0, -2)) * 60 + Number(v.slice(-2));
      setZone(m![1] === '-' ? -minutes : minutes);
      continue;
    }
    let wasTime = false;
    if (eat('(\\d{4})-(\\d{1,2})-(\\d{1,2})(?:t(?=\\d))?')) setDate(g(1), g(2), g(3));
    else if (eat('(\\d{1,2})/(\\d{1,2})(?:/(\\d{2}|\\d{4}))?\\b')) setDate(has(3) ? fullYear(g(3), m![3].length) : undefined, g(1), g(2));
    else if (eat('(\\d{4})(\\d{2})(\\d{2})\\b')) setDate(g(1), g(2), g(3));
    else if (eat('(\\d{1,2}):(\\d{2})(?::(\\d{2})(?:[.,](\\d+))?)?\\s*(?:(a|p)\\.?m\\.?\\b)?')) {
      if (time !== undefined) throw bad;
      let h = g(1);
      if (has(5)) { if (h < 1 || h > 12) throw bad; h = (h % 12) + (m![5] === 'p' ? 12 : 0); }
      const frac = has(4) ? Number('0.' + m![4]) : 0;
      if (h > 23 || g(2) > 59 || (has(3) && g(3) > 60)) throw bad;
      time = ((h * 60 + g(2)) * 60 + (has(3) ? g(3) : 0) + frac) * 1000;
      wasTime = true;
    } else if (eat('(\\d{1,2})\\s*(a|p)\\.?m\\.?\\b')) {
      if (time !== undefined || g(1) < 1 || g(1) > 12) throw bad;
      time = ((g(1) % 12) + (m![2] === 'p' ? 12 : 0)) * 3_600_000;
      wasTime = true;
    } else if (eat('(\\d{1,2})(?:st|nd|rd|th)?[\\s-]*' + MONTHS + '(?:[\\s-]+(\\d{4})\\b)?')) setDate(has(3) ? g(3) : undefined, monthOf(2), g(1));
    else if (eat(MONTHS + '\\s*(\\d{1,2})(?:st|nd|rd|th)?\\b(?:,?\\s*(\\d{4})\\b)?')) setDate(has(3) ? g(3) : undefined, monthOf(1), g(2));
    else if (eat('(next|last|this|[+-]?\\s*\\d+)?\\s*(second|sec|minute|min|hour|day|week|fortnight|month|year)s?\\b(\\s+ago\\b)?')) {
      const word = has(1) ? m![1].replace(/\s/g, '') : '1';
      const n = word === 'next' ? 1 : word === 'last' ? -1 : word === 'this' ? 0 : Number(word);
      relative.push([has(3) ? -n : n, m![2]]);
    } else if (eat('(utc|gmt|ut|z|[ecmp][sd]t)\\b(?:([+-])(\\d{1,2})(?::?(\\d{2}))?)?')) {
      let offset = ZONE_HOURS[m![1]] * 60;
      if (has(2)) offset += (m![2] === '-' ? -1 : 1) * (g(3) * 60 + (has(4) ? g(4) : 0));
      setZone(offset);
    } else if (eat('([+-])(\\d{2}):?(\\d{2})\\b')) setZone((m![1] === '-' ? -1 : 1) * (g(2) * 60 + g(3)));
    else if (eat('(sun|mon|tue|wed|thu|fri|sat)[a-z]*\\.?\\b')) {
      if (weekday !== undefined) throw bad;
      weekday = DAY_ABBR.indexOf(m![1]);
    } else if (eat('(today|now|yesterday|tomorrow)\\b')) {
      if (dayShift !== undefined) throw bad;
      dayShift = m![1] === 'yesterday' ? -1 : m![1] === 'tomorrow' ? 1 : 0;
    } else if (year === undefined && day !== undefined && eat('(\\d{4})\\b')) year = g(1); // "Fri Nov 20 13:30:00 UTC 2026"
    else throw bad;
    afterTime = wasTime;
  }
  // absolute items set the date and time; anything not given comes from now, and a date alone means midnight
  const absolute = day !== undefined || time !== undefined || weekday !== undefined;
  const n = new Date(now);
  const y = year ?? n.getUTCFullYear(), mo = month ?? n.getUTCMonth() + 1, d = day ?? n.getUTCDate();
  if (mo < 1 || mo > 12 || d < 1 || d > new Date(Date.UTC(y, mo, 0)).getUTCDate()) throw bad;
  let t = Date.UTC(y, mo - 1, d);
  t += time ?? (absolute ? 0 : now - Date.UTC(n.getUTCFullYear(), n.getUTCMonth(), n.getUTCDate()));
  if (zone !== undefined) t -= zone * 60_000;
  if (weekday !== undefined) t += ((((weekday - new Date(t).getUTCDay()) % 7) + 7) % 7) * 86_400_000;
  if (dayShift !== undefined) t += dayShift * 86_400_000;
  for (const [count, unit] of relative) {
    if (unit === 'month' || unit === 'year') {
      // like GNU date, a day past the end of the month rolls over (Jan 31 + 1 month = Mar 2 or 3)
      const dt = new Date(t);
      const months = dt.getUTCMonth() + count * (unit === 'year' ? 12 : 1);
      t = Date.UTC(dt.getUTCFullYear(), months, 1, dt.getUTCHours(), dt.getUTCMinutes(), dt.getUTCSeconds(), dt.getUTCMilliseconds()) + (dt.getUTCDate() - 1) * 86_400_000;
    } else t += count * DATE_UNITS[unit] * 1000;
  }
  return t;
}

// ---------- registration ----------

export function registerBuiltins(shell: Shell) {
  const reg = (name: string, handler: CommandHandler) => shell.register(name, handler);

  reg('true', () => 0);
  reg(':', () => 0);
  reg('false', () => 1);

  reg('echo', ctx => {
    const args = [...ctx.args];
    let newline = true, escapes = false;
    while (args.length > 0 && /^-[neE]+$/.test(args[0])) {
      if (args[0].includes('n')) newline = false;
      if (args[0].includes('e')) escapes = true;
      if (args[0].includes('E')) escapes = false;
      args.shift();
    }
    let text = args.join(' ');
    if (escapes) {
      const stop = text.indexOf('\\c'); // \c: print nothing more
      if (stop >= 0) { text = text.slice(0, stop); newline = false; }
      text = unescape(text, true, true);
    }
    ctx.stdout.write(newline ? text + '\n' : text);
  });

  reg('printf', ctx => {
    const args = [...ctx.args];
    let variable: string | undefined;
    if (args.length >= 2 && args[0] === '-v') { variable = args[1]; args.splice(0, 2); }
    if (args[0] === '--') args.shift();
    if (args.length === 0) throw new ShellError(specMessage('printf', 'usage'), 2);
    const errors: string[] = [];
    const text = printfFormat(args[0], args.slice(1), errors, () => ctx.shell.now());
    for (const e of errors) ctx.stderr.write(ctx.shell.errPrefix + e + '\n'); // bash reports them as it goes, then writes the text
    if (variable === undefined) ctx.stdout.write(text); else ctx.shell.setVar(variable, text);
    return errors.length > 0 ? 1 : 0;
  });

  reg('cd', ctx => {
    const sh = ctx.shell;
    const args = [...ctx.args];
    let physical = false;
    while (['-L', '-P', '-e', '-@'].includes(args[0])) { const f = args.shift()!; if (f === '-L' || f === '-P') physical = f === '-P'; }
    if (args[0] === '--') args.shift();
    if (args.length > 1) throw new ShellError(specMessage('cd', 'tooManyArguments'));
    const target = args.length === 0 ? sh.environment.get('HOME') ?? '/' : args[0] === '-' ? sh.environment.get('OLDPWD') ?? sh.cwd : args[0];
    // like bash: the new directory is the old one's logical path plus the operand, ".." removing a name (-L, the default);
    // if that isn't a directory, or with -P, the physical path, every symbolic link resolved
    let abs = VirtualFileSystem.normalize(sh.cwd, target);
    if (physical || !ctx.fs.isDirectory(abs)) abs = ctx.resolvePath(target);
    if (!ctx.fs.exists(abs)) throw new ShellError(specMessage('cd', 'failed', { dir: target, errno: ctx.fs.whyMissing(abs) }));
    if (!ctx.fs.isDirectory(abs)) throw new ShellError(specMessage('cd', 'failed', { dir: target, errno: 'Not a directory' }));
    if (!ctx.fs.canExecute(abs)) throw new ShellError(specMessage('cd', 'failed', { dir: target, errno: 'Permission denied' }));
    if (physical) abs = ctx.fs.realPath(abs) ?? abs;
    sh.environment.set('OLDPWD', sh.cwd);
    sh.cwd = abs;
    if (args.length === 1 && args[0] === '-') line(ctx, abs);
  });

  // -P: the physical path, symbolic links resolved; -L (the default): the logical one cd kept
  reg('pwd', ctx => line(ctx, ctx.args.findLast(x => x === '-L' || x === '-P') === '-P' ? ctx.fs.realPath(ctx.shell.cwd) ?? ctx.shell.cwd : ctx.shell.cwd));

  reg('export', ctx => {
    const sh = ctx.shell;
    let k = 0;
    while (k < ctx.args.length && ctx.args[k].startsWith('-')) k++;
    const flags = ctx.args.slice(0, k).join('');
    const args = ctx.args.slice(k);
    const remove = flags.includes('n');
    if (flags.includes('f')) {
      // export -f: functions a child bash also gets
      for (const f of args) if (remove) sh.exportedFunctions.delete(f); else if (sh.functions.has(f)) sh.exportedFunctions.add(f);
      return 0;
    }
    if (args.length === 0) {
      for (const n of allVariableNames(sh)) if (sh.isExported(n)) { const l = declareLine(sh, n); if (l) line(ctx, l); }
      return 0;
    }
    let code = 0;
    for (const a of args) {
      const eq = a.indexOf('=');
      const append = eq > 0 && a[eq - 1] === '+';
      const name = eq > 0 ? a.slice(0, append ? eq - 1 : eq) : a;
      try {
        if (!isName(name)) throw new ShellError(specFormat(DIAGNOSTICS.notValidIdentifier, { name: a }));
        const target = sh.resolve(name);
        if (eq > 0) sh.setVar(name, a.slice(eq + 1), append);
        else if (!sh.arrays.has(target) && !sh.environment.has(target)) { sh.markShellVariable(target); sh.environment.set(target, ''); }
        if (remove) sh.unexport(target); else sh.setAttrs(target, 'x');
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`${ctx.shell.errPrefix}export: ${e.message}\n`);
        code = 1;
      }
    }
    return code;
  });

  reg('unset', async ctx => {
    const sh = ctx.shell;
    let k = 0;
    while (k < ctx.args.length && ctx.args[k].startsWith('-')) k++;
    const flags = ctx.args.slice(0, k).join('');
    let code = 0;
    for (const arg of ctx.args.slice(k)) {
      try {
        if (!flags.includes('f') && !isName(arg.split('[')[0])) {
          if (flags.includes('v')) throw new ShellError(specFormat(DIAGNOSTICS.notValidIdentifier, { name: arg }));
          continue; // plain unset passes over what can be neither a variable nor a function, silently
        }
        const a = await sh.expandSubscript(arg, ioOf(ctx));
        // with neither -v nor -f, a name that is no variable but a function unsets the function
        if (flags.includes('f') || (!flags.includes('v') && !sh.isVariableSet(a) && sh.functions.has(a))) sh.functions.delete(a);
        else sh.unset(a, flags.includes('n'));
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`${ctx.shell.errPrefix}unset: ${e.message}\n`);
        code = 1;
      }
    }
    return code;
  });

  const printEnv = (ctx: CommandContext) => {
    for (const k of [...ctx.shell.environment.keys()].sort(ordinal)) if (ctx.shell.isExported(k)) line(ctx, `${k}=${ctx.shell.environment.get(k)}`);
  };

  reg('env', async ctx => {
    // env [-i] [-u NAME] [-C DIR] [-0] [NAME=VALUE]... [COMMAND [ARG]...]; options end at the first other word
    const args = [...ctx.args];
    const sets: [string, string][] = [];
    const unsets: string[] = [];
    let clear = false, nul = false, dir: string | undefined;
    const option = (c: string, value: () => string) => {
      if (c === 'i') clear = true;
      else if (c === '0') nul = true;
      else if (c === 'u') unsets.push(value());
      else dir = value();
    };
    const LONG: Record<string, string> = { 'ignore-environment': 'i', unset: 'u', chdir: 'C', null: '0' };
    let options = true; // after --, only NAME=VALUE words
    while (args.length) {
      const a = args[0];
      if (options && a === '-') { clear = true; args.shift(); continue; }
      if (options && a === '--') { args.shift(); options = false; continue; }
      if (options && a.startsWith('--')) {
        args.shift();
        const eq = a.indexOf('='), name = eq < 0 ? a.slice(2) : a.slice(2, eq);
        const c = LONG[name];
        if (c === undefined) throw new UsageException(specFormat(GETOPT.unrecognizedOption, { option: a }));
        option(c, () => eq >= 0 ? a.slice(eq + 1) : args.length ? args.shift()! : (() => { throw new UsageException(specFormat(GETOPT.missingLongArgument, { option: name })); })());
        continue;
      }
      if (options && a.length > 1 && a[0] === '-') {
        args.shift();
        for (let k = 1; k < a.length; k++) {
          const c = a[k];
          if (!'i0uC'.includes(c)) throw new UsageException(specFormat(GETOPT.invalidOption, { option: c }));
          if (c === 'u' || c === 'C') {
            const rest = a.slice(k + 1);
            option(c, () => rest || (args.length ? args.shift()! : (() => { throw new UsageException(specFormat(GETOPT.missingShortArgument, { option: c })); })()));
            break;
          }
          option(c, () => '');
        }
        continue;
      }
      const eq = a.indexOf('=');
      if (eq > 0) { sets.push([a.slice(0, eq), a.slice(eq + 1)]); args.shift(); continue; }
      break;
    }
    const print = () => {
      for (const k of [...ctx.shell.environment.keys()].sort(ordinal)) if (ctx.shell.isExported(k)) ctx.stdout.write(`${k}=${ctx.shell.environment.get(k)}${nul ? '\0' : '\n'}`);
    };
    if (args.length === 0 && sets.length === 0 && !clear && unsets.length === 0) { print(); return 0; }
    const sh = ctx.shell;
    const savedEnv = sh.environment, savedAttrs = sh.varAttrs, savedCwd = sh.cwd;
    sh.environment = new Map(clear ? [] : [...savedEnv].filter(([k]) => sh.isExported(k) && !unsets.includes(k)));
    sh.varAttrs = new Map([...sh.environment.keys()].map(k => [k, 'x']));
    for (const [k, v] of sets) { sh.environment.set(k, v); sh.varAttrs.set(k, 'x'); }
    try {
      if (dir !== undefined) {
        const abs = ctx.resolvePath(dir);
        if (!ctx.fs.isDirectory(abs)) throw new ShellError(specMessage('env', 'cannotChdir', { dir, errno: ctx.fs.exists(abs) ? ERRNO.ENOTDIR : ctx.fs.whyMissing(abs) }), 125);
        const pwd = sh.environment.get('PWD');
        sh.cwd = abs;
        if (pwd === undefined) sh.environment.delete('PWD'); else sh.environment.set('PWD', pwd); // env -C leaves PWD alone
      }
      if (args.length === 0) { print(); return 0; }
      // with no PATH, env searches /bin and /usr/bin, as execvp does
      if (!sh.environment.has('PATH') && !args[0].includes('/')) {
        const found = ['/bin/', '/usr/bin/'].find(d => ctx.fs.isFile(d + args[0]));
        if (found) args[0] = found + args[0];
      }
      // env runs programs: a name that is none is env's own error, not the shell's
      const bad = cannotRun(ctx, args[0]);
      if (bad) throw new ShellError(specMessage('env', 'runFailed', { command: args[0], errno: bad[0] }), bad[1]);
      return await sh.invoke(args, ioOf(ctx), true);
    } finally {
      if (sh.cwd !== savedCwd) try { sh.cwd = savedCwd; } catch { /* removed meanwhile */ }
      sh.environment = savedEnv; sh.varAttrs = savedAttrs;
    }
  });

  reg('alias', ctx => {
    const sh = ctx.shell;
    let code = 0;
    const args = ctx.args.filter(a => a !== '-p');
    if (args.length === 0) for (const k of [...sh.aliases.keys()].sort(ordinal)) line(ctx, `alias ${k}='${sh.aliases.get(k)}'`);
    for (const a of args) {
      const eq = a.indexOf('=');
      if (eq > 0) sh.aliases.set(a.slice(0, eq), a.slice(eq + 1));
      else if (sh.aliases.has(a)) line(ctx, `alias ${a}='${sh.aliases.get(a)}'`);
      else { ctx.stderr.write(`${ctx.shell.errPrefix}alias: ${specMessage('alias', 'notFound', { name: a })}\n`); code = 1; }
    }
    return code;
  });

  reg('unalias', ctx => {
    const usage = () => { ctx.stderr.write(`unalias: ${specMessage('unalias', 'usage')}\n`); return 2; };
    let i = 0;
    for (; i < ctx.args.length && ctx.args[i].length > 1 && ctx.args[i][0] === '-'; i++) {
      if (ctx.args[i] === '--') { i++; break; }
      const bad = [...ctx.args[i].slice(1)].find(c => c !== 'a');
      if (bad !== undefined) { ctx.stderr.write(`${ctx.shell.errPrefix}unalias: ${specMessage('unalias', 'builtinInvalidOption', { option: '-' + bad })}\n`); return usage(); }
      ctx.shell.aliases.clear();
      return 0;
    }
    if (i >= ctx.args.length) return usage();
    let code = 0;
    for (const a of ctx.args.slice(i)) if (!ctx.shell.aliases.delete(a)) { ctx.stderr.write(`${ctx.shell.errPrefix}unalias: ${specMessage('unalias', 'notFound', { name: a })}\n`); code = 1; }
    return code;
  });

  reg('history', ctx => {
    const h = ctx.shell.history;
    const n = ctx.args.length && /^\d+$/.test(ctx.args[0]) ? Number(ctx.args[0]) : h.length;
    for (let i = Math.max(0, h.length - n); i < h.length; i++) line(ctx, `${String(i + 1).padStart(5)}  ${h[i]}`);
  });

  reg('which', ctx => {
    let code = 0;
    for (const a of ctx.args.filter(x => !x.startsWith('-'))) {
      const found = ctx.shell.locateAll(a);
      if (!found.length) code = 1;
      for (const p of ctx.args.includes('-a') ? found : found.slice(0, 1)) line(ctx, p);
    }
    return code;
  });

  reg('type', ctx => {
    const sh = ctx.shell;
    let code = 0;
    const opts = Args.parse(ctx.args, specOf('type'));
    for (const a of opts.positional) {
      if (opts.has('t') || opts.has('p') || opts.has('P')) {
        let kind = sh.aliases.has(a) ? 'alias' : SHELL_KEYWORDS.has(a) ? 'keyword' : sh.functions.has(a) ? 'function'
          : isShellBuiltin(sh, a) ? 'builtin' : null;
        let paths: string[] = [];
        if (kind === null || opts.has('P')) { const l = opts.has('a') ? null : sh.locate(a); paths = opts.has('a') ? sh.locateAll(a) : l ? [l[0]] : []; }
        kind ??= paths.length ? 'file' : null;
        if (kind === null) { code = 1; continue; }
        if (opts.has('t')) line(ctx, kind);
        else for (const p of paths) line(ctx, p);
        continue;
      }
      if (!describe(ctx, a, opts.has('a'))) code = 1;
    }
    return code;
  });

  reg('test', ctx => (testEvaluate(ctx.shell, [...ctx.args]) ? 0 : 1));
  reg('[', ctx => {
    if (ctx.args.length === 0 || ctx.args[ctx.args.length - 1] !== ']') throw new ShellError(specMessage('[', 'missingBracket'), 2);
    return testEvaluate(ctx.shell, ctx.args.slice(0, -1)) ? 0 : 1;
  });

  const runScript: CommandHandler = async ctx => {
    const io = ioOf(ctx);
    let args = [...ctx.args];
    const sh = ctx.shell;
    const isBash = ctx.name === 'bash' || ctx.name === 'sh';
    let command = false, login = false;
    // bash's own options: -c, and -e -u -x -o pipefail ... which the new shell starts with
    const options: [string, boolean][] = [];
    let i = 0;
    for (; isBash && i < args.length && args[i].length > 1 && (args[i][0] === '-' || args[i][0] === '+'); i++) {
      const f = args[i];
      if (f === '--') { i++; break; }
      if (f.startsWith('--')) { login ||= f === '--login'; continue; } // --norc, --noprofile
      for (const c of f.slice(1)) {
        if (c === 'c') command = true;
        else if (c === 'l' && f[0] === '-') login = true;
        else if (c === 'o' && i + 1 < args.length) options.push([args[++i], f[0] === '-']);
        else if ('eufxC'.includes(c)) options.push([({ e: 'errexit', u: 'nounset', f: 'noglob', x: 'xtrace', C: 'noclobber' } as Record<string, string>)[c], f[0] === '-']);
      }
    }
    args = args.slice(i);
    const setup = () => { for (const [name, on] of options) sh.setOption(name, on); };
    if (command) {
      if (args.length === 0) throw new ShellError(specMessage(ctx.name, 'cOptionArgument'), 2);
      return sh.runScriptText(args[0], io, true, args.slice(2), { arg0: args[1] ?? ctx.name, setup, login });
    }
    if (args.length === 0) {
      if (!isBash) throw new ShellError(specMessage(ctx.name, 'filenameRequired'), 2);
      return sh.runScriptText(ctx.stdinText, { ...io, stdin: new StdinBuffer('') }, true, [], { arg0: ctx.name, setup, login });
    }
    let text: string;
    try { text = ctx.fs.readText(ctx.resolvePath(args[0])); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      // a new bash reports its missing script itself, so without a line; source reports it at its own line
      ctx.stderr.write(`${isBash ? 'bash: ' : ctx.shell.errPrefix}${args[0]}: ${e.message.slice(e.message.lastIndexOf(': ') + 2)}\n`);
      return isBash ? 127 : 1;
    }
    if (isBash) return sh.runScriptText(text, io, true, args.slice(1), { arg0: args[0], file: args[0], setup, login });
    sh.sourceDepth++;
    try { return await sh.runScriptText(text, io, false, args.length > 1 ? args.slice(1) : undefined, undefined, args[0]); } catch (e) {
      if (e instanceof ReturnSignal) return e.code; // `return` ends the sourced file only
      throw e;
    } finally { sh.sourceDepth--; }
  };
  reg('bash', runScript);
  reg('sh', runScript);
  reg('source', runScript);
  reg('.', runScript);

  reg('help', ctx => {
    const sh = ctx.shell;
    if (ctx.args.length > 0) {
      const c = sh.commands.get(ctx.args[0]);
      if (!c) throw new ShellError(specMessage('help', 'noTopics', { topic: ctx.args[0] }));
      line(ctx, c.usage ?? `${c.name}: ${c.description}`);
      return 0;
    }
    const cmds = [...sh.commands.values()].sort((a, b) => ordinal(a.name, b.name));
    const width = Math.max(...cmds.map(c => c.name.length)) + 2;
    line(ctx, "Available commands (run '<command> --help' for details on non-standard ones):");
    for (const c of cmds) line(ctx, `  ${c.name.padEnd(width)}${c.description}`);
  });

  // ---------- scripting ----------

  reg('exit', ctx => {
    if (ctx.args.length > 0 && !/^-?\d+$/.test(ctx.args[0])) {
      // bash still exits, with status 2
      ctx.stderr.write(`${ctx.shell.errPrefix}exit: ${specMessage('exit', 'numericArgument', { value: ctx.args[0] })}\n`);
      throw new ExitSignal(2);
    }
    throw new ExitSignal(parseCount(ctx, ctx.shell.lastExitCode) & 255);
  });
  reg('return', ctx => {
    if (!ctx.shell.inFunction && ctx.shell.sourceDepth === 0) throw new ShellError(specMessage('return', 'notInFunction'), 2);
    ctx.shell.returnSignal(parseCount(ctx, ctx.shell.lastExitCode) & 255);
  });
  reg('break', ctx => loopJump(ctx, true));
  reg('continue', ctx => loopJump(ctx, false));

  reg('shift', ctx => {
    const n = parseCount(ctx, 1);
    if (n > ctx.shell.positional.length) return 1;
    ctx.shell.positional = ctx.shell.positional.slice(n);
    return 0;
  });

  reg('trap', ctx => {
    // EXIT runs when the tool call or script ends, ERR after a failing command, RETURN as a function returns;
    // other signals never arrive here
    const args = ctx.args.filter(a => a !== '--');
    const traps = ctx.shell.traps;
    const sigName = (sig: string) => {
      let name = sig.toUpperCase();
      if (name.startsWith('SIG')) name = name.slice(3);
      return name === '0' ? 'EXIT' : name;
    };
    if (args.length === 0 || args[0] === '-p') {
      const only = new Set(args.slice(1).map(sigName));
      for (const [sig, action] of traps) if (only.size === 0 || only.has(sig)) line(ctx, `trap -- ${quoteValue(action)} ${sig}`);
      return 0;
    }
    if (args[0] === '-l') { line(ctx, ' 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 9) SIGKILL\t15) SIGTERM'); return 0; }
    const [act, signals] = args.length === 1 ? [null, args] : [args[0], args.slice(1)];
    for (const sig of signals) if (act === null || act === '-') traps.delete(sigName(sig)); else traps.set(sigName(sig), act);
    return 0;
  });

  const KNOWN_SHOPTS = ['assoc_expand_once', 'autocd', 'cdable_vars', 'cdspell', 'checkhash', 'checkjobs', 'checkwinsize', 'cmdhist',
    'complete_fullquote', 'direxpand', 'dirspell', 'dotglob', 'execfail', 'expand_aliases', 'extdebug', 'extglob', 'extquote',
    'failglob', 'force_fignore', 'globasciiranges', 'globskipdots', 'globstar', 'gnu_errfmt', 'histappend', 'histreedit',
    'histverify', 'hostcomplete', 'huponexit', 'inherit_errexit', 'interactive_comments', 'lastpipe', 'lithist',
    'localvar_inherit', 'localvar_unset', 'login_shell', 'mailwarn', 'no_empty_cmd_completion', 'nocaseglob', 'nocasematch',
    'noexpand_translation', 'nullglob', 'patsub_replacement', 'progcomp', 'progcomp_alias', 'promptvars', 'restricted_shell',
    'shift_verbose', 'sourcepath', 'varredir_close', 'xpg_echo'];
  reg('shopt', ctx => {
    const sh = ctx.shell;
    const a = Args.parse(ctx.args, specOf('shopt'));
    const setting = a.has('s') || a.has('u');
    if (a.has('o')) {
      // shopt -o: the set -o options
      for (const n of a.positional) if (!sh.setOption(n, a.has('s')) && setting) throw new ShellError(specMessage('shopt', 'invalidOptionName', { name: n }));
      if (setting) return 0;
      for (const [n, on] of sh.optionList) if (a.positional.length === 0 || a.positional.includes(n)) line(ctx, `${n.padEnd(15)}\t${on ? 'on' : 'off'}`);
      return 0;
    }
    const names = a.positional.length > 0 ? a.positional : KNOWN_SHOPTS;
    if (setting) {
      for (const n of names) {
        if (!KNOWN_SHOPTS.includes(n)) throw new ShellError(specMessage('shopt', 'invalidShellOptionName', { name: n }));
        if (a.has('s')) sh.shopts.add(n); else sh.shopts.delete(n);
      }
      return 0;
    }
    let code = 0;
    for (const n of names) {
      if (!KNOWN_SHOPTS.includes(n)) { ctx.stderr.write(`${ctx.shell.errPrefix}shopt: ${specMessage('shopt', 'invalidShellOptionName', { name: n })}\n`); code = 1; continue; }
      const on = sh.shopts.has(n);
      if (code === 0 && !on) code = 1;
      if (!a.has('q')) line(ctx, `${n.padEnd(15)}\t${on ? 'on' : 'off'}`);
    }
    return code;
  });

  reg('set', ctx => {
    const args = [...ctx.args];
    const sh = ctx.shell;
    if (args.length === 0) {
      for (const n of allVariableNames(sh)) {
        const a = sh.arrays.get(n);
        if (a) { const l = declareLine(sh, n); if (l) line(ctx, `${n}=${l.slice(l.indexOf('=') + 1)}`); }
        else line(ctx, `${n}=${shellQuote(sh.environment.get(n)!)}`);
      }
      return 0;
    }
    let i = 0;
    for (; i < args.length; i++) {
      const a = args[i];
      if (a === '--') { sh.positional = args.slice(i + 1); return 0; }
      if (a === '-') { i++; break; }
      if (a.length < 2 || (a[0] !== '-' && a[0] !== '+')) break;
      const on = a[0] === '-';
      for (const c of a.slice(1)) {
        if (c === 'o') {
          if (i + 1 >= args.length) {
            // set -o / set +o alone: list the options (as commands for +o)
            for (const [n, value] of sh.optionList) line(ctx, on ? `${n.padEnd(15)}\t${value ? 'on' : 'off'}` : `set ${value ? '-' : '+'}o ${n}`);
            continue;
          }
          const opt = args[++i];
          if (!sh.setOption(opt, on)) throw new ShellError(specMessage('set', 'invalidOptionName', { name: opt }), 2);
        } else if ('eufxC'.includes(c)) sh.setOption(({ e: 'errexit', u: 'nounset', f: 'noglob', x: 'xtrace', C: 'noclobber' } as Record<string, string>)[c], on);
        else if (!'abhkmnpvBEHPT'.includes(c)) throw new ShellError(specMessage('set', 'builtinInvalidOption', { option: a[0] + c }), 2); // accepted, no effect here
      }
    }
    if (i < args.length) sh.positional = args.slice(i);
    return 0;
  });

  const declare: CommandHandler = ctx => {
    const sh = ctx.shell;
    let on = ctx.name === 'readonly' ? 'r' : '', off = '', listOnly = '';
    let print = false, functions = false, functionNames = false, global = false;
    const names: string[] = [];
    for (const a of ctx.args) {
      if (names.length > 0 || a.length < 2 || (a[0] !== '-' && a[0] !== '+') || a === '--') { if (a !== '--' || names.length > 0) names.push(a); continue; }
      for (const c of a.slice(1)) {
        switch (c) {
          case 'p': print = true; break;
          case 'f': functions = true; break;
          case 'F': functionNames = true; break;
          case 'g': global = true; break;
          case 'a': case 'A': listOnly += c; break; // arrays were declared before the built-in ran
          case 't': break;
          case 'i': case 'r': case 'x': case 'l': case 'u': case 'n': if (a[0] === '-') on += c; else off += c; break;
          default: throw new ShellError(specMessage(ctx.name, 'builtinInvalidOption', { option: '-' + c }), 2);
        }
      }
    }
    if (functions || functionNames) {
      let code = 0;
      for (const n of names.length === 0 ? [...sh.functions.keys()].sort(ordinal) : names) {
        const f = sh.functions.get(n);
        if (!f) { code = 1; continue; }
        line(ctx, functionNames ? (names.length === 0 ? `declare -f ${n}` : n) : `${n} () \n${f.source}`);
      }
      return code;
    }
    if (names.length === 0) {
      // declare / declare -p / readonly / declare -x ...: list what has those attributes
      for (const n of allVariableNames(sh)) {
        if (n === '__topwater_element') continue;
        if (![...on + listOnly].every(c => attributeFlags(sh, n).includes(c))) continue;
        const l = declareLine(sh, n);
        if (!l) continue;
        line(ctx, print || on.length + listOnly.length > 0 ? l : `${n}=${sh.arrays.has(n) ? l.slice(l.indexOf('=') + 1) : shellQuote(sh.environment.get(n)!)}`);
      }
      return 0;
    }
    let code = 0;
    if (print) {
      for (const n of names) {
        const l = declareLine(sh, n);
        if (l) line(ctx, l); else { ctx.stderr.write(`${ctx.shell.errPrefix}${ctx.name}: ${specMessage(ctx.name, 'notFound', { name: n })}\n`); code = 1; }
      }
      return code;
    }
    const local = ctx.name === 'local' || ((ctx.name === 'declare' || ctx.name === 'typeset') && sh.inFunction && !global);
    for (const arg of names) {
      const eq = arg.indexOf('=');
      const append = eq > 0 && arg[eq - 1] === '+';
      const name = eq > 0 ? arg.slice(0, append ? eq - 1 : eq) : arg;
      const value = eq > 0 ? arg.slice(eq + 1) : null;
      try {
        if (!isName(name)) throw new ShellError(specFormat(DIAGNOSTICS.notValidIdentifier, { name: arg }));
        if (local && !sh.arrays.has(name)) sh.declareLocal(name, null); // shortcut: `local x` is empty here, unset in bash
        else if (local && sh.arrays.has(name) && value !== null) sh.declareLocal(name, null);
        if (on.includes('n')) {
          // a nameref holds the name of the variable it stands for
          sh.setAttrs(name, 'n');
          sh.markShellVariable(name);
          if (value !== null) sh.environment.set(name, value);
          continue;
        }
        const target = sh.resolve(name);
        sh.setAttrs(target, on.replace('r', ''), off);
        if (value !== null) sh.setVar(name, value, append);
        else if (!sh.environment.has(target) && !sh.arrays.has(target)) { sh.markShellVariable(target); sh.environment.set(target, ''); }
        if (on.includes('r')) sh.setAttrs(target, 'r');
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`${ctx.shell.errPrefix}${ctx.name}: ${e.message}\n`);
        code = 1;
      }
    }
    return code;
  };
  reg('local', declare);
  reg('declare', declare);
  reg('typeset', declare);
  reg('readonly', declare);

  reg('read', ctx => {
    const a = Args.parse(ctx.args, specOf('read'));
    const sh = ctx.shell;
    const names = a.positional.length > 0 ? a.positional : ['REPLY'];
    const input = inputFor(ctx, a.value('u'));
    const dv = a.value('d');
    const delim = dv !== undefined ? (dv.length === 0 ? '\0' : dv[0]) : '\n';
    let text: string | null;
    const exact = a.value('N'), upTo = a.value('n');
    if (exact !== undefined) text = input.readChars(Number(exact), null);
    else if (upTo !== undefined) text = input.readChars(Number(upTo), delim);
    else {
      text = input.readLine(delim);
      // without -r, a backslash at the end of a line joins it to the next one
      while (!a.has('r') && text !== null && input.lastTerminated && delim === '\n' && /(^|[^\\])(\\\\)*\\$/.test(text)) {
        const more = input.readLine();
        if (more === null) break;
        text = text.slice(0, -1) + more;
      }
    }
    if (text === null) { for (const n of names) sh.setVar(n, ''); return 1; }
    if (!a.has('r')) text = text.replace(/\\(.)/gs, '$1');
    const ifs = sh.ifs;
    if (a.has('a')) {
      // read -a NAME: every field becomes an element
      sh.unset(names[0]);
      const array = sh.writable(names[0]);
      for (const field of splitFields(text, ifs)) { sh.chargeVariable(names[0], field.length, 0); array.set(String(array.next), field); }
    } else if (a.positional.length === 0) sh.setVar('REPLY', text);
    else {
      const fields = readFields(text, ifs, names.length);
      names.forEach((n, i) => sh.setVar(n, fields[i] ?? ''));
    }
    // like bash, a final line with no trailing newline is still assigned, but read reports end of input
    return input.lastTerminated ? 0 : 1;
  });

  const mapfile: CommandHandler = ctx => {
    const a = Args.parse(ctx.args, specOf(ctx.name));
    const sh = ctx.shell;
    const name = a.positional[0] ?? 'MAPFILE';
    const input = inputFor(ctx, a.value('u'));
    const dv = a.value('d');
    const delim = dv !== undefined ? (dv.length === 0 ? '\0' : dv[0]) : '\n';
    const max = Number(a.value('n') ?? 0);
    let skip = Number(a.value('s') ?? 0);
    let index = Number(a.value('O') ?? 0);
    if (a.value('O') === undefined) sh.unset(name); // -O keeps what the array already holds
    const array = sh.writable(name);
    for (let count = 0; max === 0 || count < max;) {
      const l = input.readLine(delim);
      if (l === null) break;
      if (skip > 0) { skip--; continue; }
      const item = a.has('t') || !input.lastTerminated ? l : l + delim;
      sh.chargeVariable(name, item.length, 0);
      array.set(String(index++), item);
      count++;
    }
    return 0;
  };
  reg('mapfile', mapfile);
  reg('readarray', mapfile);

  reg('getopts', ctx => {
    if (ctx.args.length < 2) throw new ShellError(specMessage('getopts', 'usage'), 2);
    const sh = ctx.shell;
    const [spec, name] = ctx.args;
    const args = ctx.args.length > 2 ? ctx.args.slice(2) : sh.positional;
    const silent = spec.startsWith(':');
    const oi = Number(sh.getVariable('OPTIND'));
    let optind = Number.isInteger(oi) && oi > 0 ? oi : 1;
    // where we are inside a group of options like -abc; restarts when the script resets OPTIND
    let pos = sh.getoptsState.optind === optind ? sh.getoptsState.pos : 1;
    const done = (code: number, result: string) => {
      sh.setVar(name, result);
      sh.getoptsState = { optind, pos };
      sh.setVar('OPTIND', String(optind));
      return code;
    };
    if (optind > args.length) return done(1, '?');
    const arg = args[optind - 1];
    if (pos === 1) {
      if (arg === '--') { optind++; return done(1, '?'); }
      if (arg.length < 2 || arg[0] !== '-') return done(1, '?');
    }
    const c = arg[pos];
    const at = c === ':' ? -1 : spec.indexOf(c);
    if (++pos >= arg.length) { optind++; pos = 1; }
    if (at < 0) {
      if (silent) sh.setVar('OPTARG', c);
      else { ctx.stderr.write(`${specMessage('getopts', 'illegalOption', { script: sh.getVariable('0'), option: c })}\n`); sh.unset('OPTARG'); }
      return done(0, '?');
    }
    if (spec[at + 1] !== ':') { sh.unset('OPTARG'); return done(0, c); }
    if (pos > 1) { sh.setVar('OPTARG', arg.slice(pos)); optind++; pos = 1; }
    else if (optind <= args.length) sh.setVar('OPTARG', args[optind++ - 1]);
    else if (silent) { sh.setVar('OPTARG', c); return done(0, ':'); }
    else {
      ctx.stderr.write(`${specMessage('getopts', 'missingArgument', { script: sh.getVariable('0'), option: c })}\n`);
      sh.unset('OPTARG');
      return done(0, '?');
    }
    return done(0, c);
  });

  reg('exec', async ctx => {
    // exec with only redirections is handled by the shell; with a command, the command's status ends the shell
    const args = [...ctx.args];
    while (args[0] === '-c' || args[0] === '-l') args.shift();
    if (args.length === 0) return 0;
    throw new ExitSignal(await ctx.shell.invoke(args, ioOf(ctx), true));
  });

  reg('wait', ctx => {
    // background jobs ran to completion when they were started: wait just reports their status
    const sh = ctx.shell;
    let code = 0;
    const ids = ctx.args.filter(a => !a.startsWith('-'));
    if (ctx.args.includes('-n') && ids.length === 0) return sh.waitNext();
    if (ids.length === 0) sh.forgetJobs();
    for (const id of ids) {
      if (id.startsWith('%')) { code = 0; continue; }
      if (!/^\d+$/.test(id)) { ctx.stderr.write(`${ctx.shell.errPrefix}wait: ${specMessage('wait', 'notPid', { id: id })}\n`); code = 2; continue; }
      code = sh.waitFor(Number(id));
      if (code === 127) ctx.stderr.write(`${ctx.shell.errPrefix}wait: ${specMessage('wait', 'notChild', { pid: id })}\n`);
    }
    return code;
  });
  reg('jobs', () => 0);
  reg('disown', () => 0);

  reg('kill', ctx => {
    if (ctx.args[0] === '-l') { line(ctx, ' 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 9) SIGKILL\t15) SIGTERM'); return 0; }
    let signal = 15, code = 0;
    const targets: string[] = [];
    for (let i = 0; i < ctx.args.length; i++) {
      let a = ctx.args[i];
      if ((a === '-s' || a === '-n') && i + 1 < ctx.args.length) a = '-' + ctx.args[++i];
      if (a.startsWith('-') && targets.length === 0) {
        const sig = a.slice(1).toUpperCase().replace('SIG', '');
        signal = /^\d+$/.test(sig) ? Number(sig) : ({ HUP: 1, INT: 2, QUIT: 3, KILL: 9, TERM: 15 } as Record<string, number>)[sig] ?? 15;
        continue;
      }
      targets.push(a);
    }
    if (targets.length === 0) throw new ShellError(specMessage('kill', 'usage'), 2);
    for (const t of targets) {
      if (t === '4242') { if (signal !== 0) throw new ExitSignal(128 + signal); continue; } // the shell itself
      if (/^\d+$/.test(t) && ctx.shell.isJob(Number(t))) continue; // a finished job: nothing to signal
      ctx.stderr.write(`${ctx.shell.errPrefix}kill: ${t.startsWith('%') ? specMessage('kill', 'noSuchJob', { job: t }) : specMessage('kill', 'noSuchProcess', { pid: t })}\n`);
      code = 1;
    }
    return code;
  });

  reg('hash', ctx => {
    const sh = ctx.shell;
    sh.syncHashed();
    const args = ctx.args;
    let code = 0, i = 0, show = false, del = false, list = false, path: string | null = null;
    for (; i < args.length && args[i].length > 1 && args[i][0] === '-'; i++) {
      if (args[i] === '--') { i++; break; }
      for (const ch of args[i].slice(1)) {
        if (ch === 'r') sh.hashed.clear();
        else if (ch === 't') show = true;
        else if (ch === 'd') del = true;
        else if (ch === 'p') { if (i + 1 >= args.length) throw new UsageException('-p: option requires an argument'); path = args[++i]; break; }
        else if (ch === 'l') list = true;
        else throw new UsageException(specMessage('hash', 'builtinInvalidOption', { option: '-' + ch }));
      }
    }
    const names = args.slice(i);
    if ((show || del) && !names.length) {
      ctx.stderr.write(`${sh.errPrefix}hash: -${show ? 't' : 'd'}: option requires an argument\n`);
      return 1;
    }
    // -l: each entry as the command that would hash it again
    const reusable = (n: string, p: string) => `builtin hash -p ${p} ${n}`;
    if (list && !names.length) ctx.stdout.write([...sh.hashed].map(([n, [p]]) => reusable(n, p) + '\n').join(''));
    else if (i === 0 && !names.length) {
      if (!sh.hashed.size) line(ctx, 'hash: hash table empty');
      else ctx.stdout.write('hits\tcommand\n' + [...sh.hashed.values()].map(([p, n]) => `${String(n).padStart(4)}\t${p}\n`).join(''));
    }
    const notFound = (n: string) => { ctx.stderr.write(`${sh.errPrefix}hash: ${specMessage('hash', 'notFound', { name: n })}\n`); code = 1; };
    for (const n of names) {
      const hashed = show ? sh.hashLookup(n) : null;
      if (hashed !== null) line(ctx, list ? reusable(n, hashed) : names.length > 1 ? `${n}\t${hashed}` : hashed);
      else if (del && sh.hashed.delete(n)) { /* removed */ }
      else if (path !== null) sh.hashed.set(n, [path, 0]);
      else if (show || del) notFound(n);
      else if (n.includes('/') || isShellBuiltin(sh, n) || sh.functions.has(n) || sh.isUnfiled(n)) { /* nothing to remember */ }
      else { const found = sh.searchPathAll(n)[0]; if (found) sh.hashed.set(n, [found, 0]); else notFound(n); }
    }
    return code;
  });

  reg('builtin', ctx => {
    if (ctx.args.length === 0) return 0;
    if (!isShellBuiltin(ctx.shell, ctx.args[0])) throw new ShellError(specMessage('builtin', 'notBuiltin', { name: ctx.args[0] }));
    return ctx.shell.invoke([...ctx.args], ioOf(ctx), true);
  });

  const dirs = (sh: Shell) => {
    const home = sh.environment.get('HOME') ?? '';
    const short = (d: string) => (home.length > 1 && (d === home || d.startsWith(home + '/')) ? '~' + d.slice(home.length) : d);
    return [sh.cwd, ...sh.dirStack].map(short).join(' ');
  };
  const changeDir = (ctx: CommandContext, dir: string) => {
    const abs = ctx.resolvePath(dir);
    if (!ctx.fs.isDirectory(abs)) throw new ShellError(specMessage('pushd', 'noDirectory', { dir }));
    ctx.shell.environment.set('OLDPWD', ctx.shell.cwd);
    ctx.shell.cwd = abs;
  };
  reg('pushd', ctx => {
    const sh = ctx.shell;
    const args = ctx.args.filter(a => a !== '-n');
    const old = sh.cwd;
    if (args.length === 0) {
      if (sh.dirStack.length === 0) throw new ShellError(specMessage('pushd', 'noOtherDirectory'));
      changeDir(ctx, sh.dirStack[0]);
      sh.dirStack[0] = old;
    } else { changeDir(ctx, args[0]); sh.dirStack.unshift(old); }
    line(ctx, dirs(sh));
  });
  reg('popd', ctx => {
    const sh = ctx.shell;
    if (sh.dirStack.length === 0) throw new ShellError(specMessage('popd', 'stackEmpty'));
    changeDir(ctx, sh.dirStack[0]);
    sh.dirStack.shift();
    line(ctx, dirs(sh));
  });
  reg('dirs', ctx => {
    if (ctx.args.includes('-c')) { ctx.shell.dirStack.length = 0; return 0; }
    const l = dirs(ctx.shell);
    if (ctx.args.some(a => a === '-p' || a === '-v')) l.split(' ').forEach((d, i) => line(ctx, ctx.args.includes('-v') ? `${String(i).padStart(2)}  ${d}` : d));
    else line(ctx, l);
  });

  reg('ulimit', ctx => {
    if (ctx.args.some(a => !a.startsWith('-'))) return 0; // setting a limit: accepted
    line(ctx, ctx.args.includes('-n') ? '1024' : ctx.args.includes('-s') ? '8192' : ctx.args.includes('-c') ? '0' : 'unlimited');
  });
  reg('umask', ctx => {
    const fs = ctx.shell.agentFs;
    let i = 0, symbolic = false, reusable = false;
    for (; i < ctx.args.length && ctx.args[i].length > 1 && ctx.args[i][0] === '-'; i++) {
      if (ctx.args[i] === '--') { i++; break; }
      for (const c of ctx.args[i].slice(1)) {
        if (c === 'S') symbolic = true;
        else if (c === 'p') reusable = true;
        else {
          ctx.stderr.write(`${ctx.shell.errPrefix}umask: ${specMessage('umask', 'builtinInvalidOption', { option: '-' + c })}\n`);
          ctx.stderr.write(`umask: ${specMessage('umask', 'usage')}\n`);
          return 2;
        }
      }
    }
    const mode = ctx.args[i];
    if (mode !== undefined) {
      if (/^\d/.test(mode)) {
        // up to 7777 is accepted; only the permission bits are kept (umask 1000 is 0000)
        if (!/^[0-7]+$/.test(mode) || parseInt(mode, 8) > 0o7777) throw new ShellError(specMessage('umask', 'octalOutOfRange', { value: mode }));
        fs.umask = parseInt(mode, 8) & 0o777;
      } else fs.umask = 0o777 & ~symbolicMode(mode, 0o777 & ~fs.umask);
      if (!symbolic) return 0;
    }
    const m = fs.umask;
    const text = symbolic ? ['u', 'g', 'o'].map((who, k) => {
      const bits = (~m >> (6 - 3 * k)) & 7;
      return who + '=' + (bits & 4 ? 'r' : '') + (bits & 2 ? 'w' : '') + (bits & 1 ? 'x' : '');
    }).join(',') : m.toString(8).padStart(4, '0');
    line(ctx, reusable ? `umask ${symbolic ? '-S ' : ''}${text}` : text);
  });
  reg('times', ctx => ctx.stdout.write('0m0.010s 0m0.005s\n0m0.000s 0m0.000s\n'), );

  reg('eval', ctx => ctx.shell.runScriptText(ctx.args.join(' '), ioOf(ctx), false, undefined, undefined, undefined, 'eval'));

  reg('let', ctx => {
    if (ctx.args.length === 0) throw new ShellError(specMessage('let', 'expressionExpected'));
    let v = 0n;
    for (const expr of ctx.args) v = evalArith(ctx.shell, expr);
    return v !== 0n ? 0 : 1;
  });

  reg('command', async ctx => {
    const sh = ctx.shell;
    const args = [...ctx.args];
    let standardPath = false;
    while (args[0] === '-p') { args.shift(); standardPath = true; }
    const isDescribe = args[0] === '-v' || args[0] === '-V';
    if (!isDescribe && args.length > 0 && standardPath && !args[0].includes('/') && !isShellBuiltin(sh, args[0])) {
      // -p: search the standard directories, not $PATH
      const dir = ['/usr/bin/', '/bin/'].find(d => ctx.fs.isFile(d + args[0]) && ctx.fs.canExecute(d + args[0]));
      if (dir === undefined) { ctx.stderr.write(diagnostic('commandNotFound', { command: args[0] }) + '\n'); return 127; }
      args[0] = dir + args[0];
    }
    if (!isDescribe) return args.length === 0 ? 0 : sh.invoke(args, ioOf(ctx), true);
    let code = 0;
    for (const n of args.slice(1)) {
      if (args[0] === '-V') { if (!describe(ctx, n)) code = 1; continue; }
      const al = sh.aliases.get(n);
      if (al !== undefined) line(ctx, `alias ${n}='${al}'`);
      else if (SHELL_KEYWORDS.has(n) || sh.functions.has(n) || isShellBuiltin(sh, n)) line(ctx, n);
      else { const l = sh.locate(n); if (l) line(ctx, l[0]); else code = 1; }
    }
    return code;
  });

  reg('printenv', ctx => {
    if (ctx.args.length === 0) { printEnv(ctx); return 0; }
    let code = 0;
    for (const n of ctx.args) { const v = ctx.shell.environment.get(n); if (v !== undefined && ctx.shell.isExported(n)) line(ctx, v); else code = 1; }
    return code;
  });

  reg('sleep', async ctx => {
    if (ctx.args.length === 0) throw new UsageException(specMessage('sleep', 'missingOperand', {}));
    const total = ctx.args.reduce((t, a) => t + parseDuration(a), 0);
    if (ctx.shell.virtualNow !== undefined) { ctx.shell.sleepVirtual(total); return; }
    const end = Date.now() + total;
    // sleeps really wait, up to the call's (or a `timeout`'s) deadline
    for (;;) {
      ctx.checkTime();
      const left = Math.min(end - Date.now(), ctx.remainingMs() + 1);
      if (left <= 0) break;
      await new Promise(r => setTimeout(r, Math.min(left, 1000)));
    }
    ctx.checkTime();
  });

  reg('timeout', async ctx => {
    const args = [...ctx.args];
    while (args.length && args[0].startsWith('-')) { const a = args.shift()!; if ((a === '-s' || a === '-k') && args.length) args.shift(); }
    if (args.length < 2) throw new ShellError(specMessage('timeout', 'missingOperand', {}), 125);
    const bad = cannotRun(ctx, args[1]);
    if (bad) throw new ShellError(specMessage('timeout', 'failedToRun', { command: args[1], errno: bad[0] }), bad[1]);
    const sh = ctx.shell;
    const parent = sh.deadline;
    const own: Deadline = { at: Date.now() + parseDuration(args[0]), parent };
    sh.deadline = own;
    try { return await sh.withVirtualTimeout(parseDuration(args[0]), () => sh.invoke(args.slice(1), ioOf(ctx))) ?? 124; } catch (e) {
      if (e instanceof DeadlineExceeded && e.owner === own) return 124;
      throw e;
    } finally { sh.deadline = parent; }
  });

  reg('time', async ctx => {
    const start = ctx.shell.now();
    const code = ctx.args.length === 0 ? 0 : await ctx.shell.invoke([...ctx.args], ioOf(ctx));
    const t = (ctx.shell.now() - start) / 1000;
    ctx.stderr.write(`\nreal\t${Math.floor(t / 60)}m${(t % 60).toFixed(3)}s\nuser\t0m0.000s\nsys\t0m0.000s\n`);
    return code;
  });

  reg('clear', () => 0);

  // ---------- system information ----------

  reg('whoami', ctx => line(ctx, ctx.shell.options.user));
  reg('hostname', ctx => line(ctx, ctx.shell.options.hostname));
  reg('uname', ctx => {
    const a = Args.parse(ctx.args, specOf('uname'));
    const all = a.has('a');
    const parts: string[] = [];
    if (all || a.has('s') || a.flags.size === 0) parts.push('Linux');
    if (all || a.has('n')) parts.push(ctx.shell.options.hostname);
    if (all || a.has('r')) parts.push('6.8.0-generic');
    if (all || a.has('v')) parts.push('#1 SMP PREEMPT_DYNAMIC');
    if (all || a.has('m')) parts.push('x86_64');
    if (a.has('p') && !all) parts.push('x86_64');
    if (a.has('i') && !all) parts.push('x86_64');
    if (all || a.has('o')) parts.push('GNU/Linux');
    line(ctx, parts.join(' '));
  });

  reg('date', ctx => {
    // the simulated machine's clock is UTC (TZ is unset), so output never depends on the host's time zone
    // -I[UNIT] / --iso-8601[=UNIT] take an optional value, so they are read here, before the shared parser
    let iso: string | undefined;
    const args = ctx.args.filter(x => {
      const unit = x === '--iso-8601' ? '' : x.startsWith('--iso-8601=') ? x.slice(11) : x.startsWith('-I') ? x.slice(2) : undefined;
      if (unit === undefined) return true;
      const found = unit === '' ? 'date' : ISO_UNITS.find(u => u.startsWith(unit));
      if (found === undefined) throw new UsageException(specMessage('date', 'invalidIso', { value: unit }));
      iso = found;
      return false;
    });
    const a = Args.parse(args, specOf('date'));
    let now = ctx.shell.now();
    const d = a.value('d'), r = a.value('r');
    if (d !== undefined) now = parseDate(d, now);
    else if (r !== undefined) now = ctx.fs.stat(ctx.resolvePath(r)).modified;
    const fmt = a.positional.find(p => p.startsWith('+'));
    const extra = a.positional.find(p => !p.startsWith('+'));
    if (extra !== undefined) throw new ShellError(specMessage('date', 'invalidDate', { value: extra }));
    line(ctx, strftime(now, iso !== undefined ? ISO_FORMATS[ISO_UNITS.indexOf(iso)] : a.has('R') ? '%a, %d %b %Y %H:%M:%S +0000' : fmt !== undefined ? fmt.slice(1) : '%a %b %e %H:%M:%S %Z %Y'));
  });

  reg('ps', ctx => ctx.stdout.write('    PID TTY          TIME CMD\n      1 pts/0    00:00:00 bash\n   4242 pts/0    00:00:00 ps\n'));
  reg('id', ctx => {
    const u = ctx.shell.options.user;
    const a = Args.parse(ctx.args, specOf('id'));
    line(ctx, a.has('u') || a.has('g') || a.has('G') ? (a.has('n') ? u : '1000') : `uid=1000(${u}) gid=1000(${u}) groups=1000(${u})`);
  });
  reg('uptime', ctx => line(ctx, ctx.args.includes('-p') ? 'up 3 days, 4 hours, 12 minutes'
    : ` ${strftime(ctx.shell.now(), '%H:%M:%S')} up 3 days,  4:12,  1 user,  load average: 0.08, 0.03, 0.01`));
  reg('free', ctx => ctx.stdout.write(ctx.args.some(x => x.includes('h'))
    ? '               total        used        free      shared  buff/cache   available\nMem:           7.6Gi       1.9Gi       3.8Gi        12Mi       2.1Gi       5.5Gi\nSwap:          2.0Gi          0B       2.0Gi\n'
    : '               total        used        free      shared  buff/cache   available\nMem:         8014880     1992344     3984512       12288     2201024     5748660\nSwap:        2097148           0     2097148\n'));
  reg('nproc', ctx => line(ctx, '4'));

  // the agent is a regular user: privilege escalation fails the way it does on a real server
  reg('sudo', ctx => {
    const user = ctx.shell.options.user;
    if (ctx.args.length === 1 && (ctx.args[0] === '-V' || ctx.args[0] === '--version')) { line(ctx, 'Sudo version 1.9.15p5'); return 0; }
    if (ctx.args.length === 0) { ctx.stderr.write(specMessage('sudo', 'usage') + '\n'); return 1; }
    if (ctx.args[0] === '-l' || ctx.args[0] === '--list') { ctx.stderr.write(specMessage('sudo', 'listDenied', { user: user, host: ctx.shell.options.hostname }) + '\n'); return 1; }
    // `sudo apt install x`: the package manager's own answer (nothing can be installed) says more than a refusal
    const k = ctx.args.findIndex(a => !a.startsWith('-'));
    if (k >= 0 && ['apt', 'apt-get', 'apt-cache', 'dpkg'].includes(ctx.args[k])) return ctx.invoke(ctx.args.slice(k));
    ctx.stderr.write(specMessage('sudo', 'notSudoer', { user: user }) + '\n');
    return 1;
  });

  // no package sources and no network: package managers say so, with their usual failure status
  for (const name of ['apt', 'apt-get', 'apt-cache']) reg(name, ctx => { ctx.stderr.write(specMessage(name, 'unavailable') + '\n'); return 100; });
  reg('dpkg', ctx => { ctx.stderr.write('dpkg: ' + specMessage('dpkg', 'unavailable') + '\n'); return 2; });
  reg('su', ctx => { ctx.stderr.write(specMessage('su', 'authenticationFailure') + '\n'); return 1; });
}

export { VirtualFileSystem };
