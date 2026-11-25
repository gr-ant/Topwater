// Shell built-ins (cd, read, declare, set, trap, ...) and the small system commands (date, uname, id, ...).
import { Args, ShellError, StdinBuffer, DeadlineExceeded, type CommandContext, type CommandHandler, type Deadline } from '../command.js';
import { isName } from '../parser.js';
import { evalArith } from '../arith.js';
import { printfFormat, unescape, shellQuote, quoteValue, strftime } from '../format.js';
import { VirtualFileSystem, ordinal } from '../vfs.js';
import { splitFields, ExitSignal, ReturnSignal, type Shell, type Io } from '../shell.js';

const ioOf = (ctx: CommandContext): Io => ({ stdin: ctx.stdin, out: ctx.stdout, err: ctx.stderr, fds: ctx.fds });
const line = (ctx: CommandContext, s: string) => ctx.stdout.write(s + '\n');

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
    case '-L': case '-h': case '-b': case '-c': case '-p': case '-S': case '-t': case '-g': case '-u': case '-k': return false; // no links, devices, pipes, terminals
    case '-O': case '-G': case '-N': return fs.exists(path);
    case '-v': return shell.isVariableSet(arg);
    case '-o': return shell.optionList.some(([n, on]) => n === arg && on);
  }
  throw new ShellError(`${op}: unary operator expected`, 2);
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
    case '-ef': return shell.resolvePath(left) === shell.resolvePath(right) && shell.agentFs.exists(shell.resolvePath(left));
  }
  const okL = /^\s*[-+]?\d+\s*$/.test(left), okR = /^\s*[-+]?\d+\s*$/.test(right);
  if (!okL || !okR) throw new ShellError(`${okL ? right : left}: integer expression expected`, 2);
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
      throw new ShellError(`${a[1]}: binary operator expected`, 2);
    case 4: if (a[0] === '!') return !testEvaluate(shell, a.slice(1));
  }
  let pos = 0;
  const peek = () => a[pos];
  const or = (): boolean => { let v = and(); while (peek() === '-o') { pos++; const r = and(); v = v || r; } return v; };
  const and = (): boolean => { let v = not(); while (peek() === '-a') { pos++; const r = not(); v = v && r; } return v; };
  const not = (): boolean => { if (peek() === '!') { pos++; return !not(); } return primary(); };
  const primary = (): boolean => {
    const t = peek();
    if (t === undefined) throw new ShellError('argument expected', 2);
    if (t === '(') {
      pos++;
      const v = or();
      if (peek() !== ')') throw new ShellError("`)' expected", 2);
      pos++;
      return v;
    }
    if (pos + 2 < n && BINARY_OPS.has(a[pos + 1])) { pos += 3; return testBinary(shell, t, a[pos - 2], a[pos - 1]); }
    if (t.length === 2 && t[0] === '-' && pos + 1 < n) { pos += 2; return testUnary(shell, t, a[pos - 1]); }
    pos++;
    return t.length > 0;
  };
  const result = or();
  if (pos < n) throw new ShellError('too many arguments', 2);
  return result;
}

// ---------- helpers ----------

const SHELL_KEYWORDS = new Set(['if', 'then', 'else', 'elif', 'fi', 'case', 'esac', 'for', 'select', 'while', 'until', 'do', 'done', 'in', 'function', 'time', '{', '}', '!', '[[', ']]', 'coproc']);
/** Commands that are part of bash itself rather than programs in /usr/bin (for `type` and `command -v`). */
const SHELL_BUILTINS = new Set(['cd', 'echo', 'printf', 'read', 'export', 'unset', 'alias', 'unalias', 'set', 'shift', 'local', 'declare', 'typeset',
  'eval', 'exit', 'return', 'break', 'continue', 'source', '.', 'test', '[', 'true', 'false', 'pwd', 'type', 'command',
  'let', 'history', 'help', 'exec', 'wait', 'trap', 'umask', 'readonly', 'getopts', 'hash', 'jobs', 'kill', 'shopt', ':', 'mapfile', 'readarray',
  'pushd', 'popd', 'dirs', 'builtin', 'ulimit', 'times', 'disown']);
const isShellBuiltin = (sh: Shell, name: string) => SHELL_BUILTINS.has(name) && sh.isBuiltin(name);

function parseCount(ctx: CommandContext, fallback: number): number {
  if (ctx.args.length === 0) return fallback;
  if (!/^-?\d+$/.test(ctx.args[0])) throw new ShellError(`${ctx.args[0]}: numeric argument required`, 2);
  return Number(ctx.args[0]);
}

function parseDuration(s: string): number {
  const m = /^(\d+(?:\.\d+)?|\.\d+)([smhd]?)$/.exec(s);
  if (!m) throw new ShellError(`invalid time interval '${s}'`);
  const n = Number(m[1]);
  return n * ({ m: 60_000, h: 3_600_000, d: 86_400_000 } as Record<string, number>)[m[2]] || n * 1000;
}

function loopJump(ctx: CommandContext, isBreak: boolean): number {
  if (ctx.shell.loopDepth > 0) ctx.shell.loopControl(isBreak, Math.max(1, parseCount(ctx, 1)));
  ctx.stderr.write(`bash: ${ctx.name}: only meaningful in a \`for', \`while', or \`until' loop\n`);
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
function describe(ctx: CommandContext, a: string): boolean {
  const sh = ctx.shell;
  const alias = sh.aliases.get(a);
  const fn = sh.functions.get(a);
  if (alias !== undefined) line(ctx, `${a} is aliased to \`${alias}'`);
  else if (SHELL_KEYWORDS.has(a)) line(ctx, `${a} is a shell keyword`);
  else if (fn) line(ctx, `${a} is a function\n${a} () \n${fn.source}`);
  else if (isShellBuiltin(sh, a)) line(ctx, `${a} is a shell builtin`);
  else if (sh.commands.has(a)) line(ctx, `${a} is /usr/bin/${a}`);
  else { ctx.stderr.write(`bash: type: ${a}: not found\n`); return false; }
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
  if (!input) throw new ShellError(`${u}: invalid file descriptor: Bad file descriptor`);
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
  const bad = new ShellError(`invalid date '${text}'`);
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

const DATE_USAGE = `Usage: date [OPTION]... [+FORMAT]
Print the current date and time, or the one given with -d, in UTC.

  -d, --date=STRING        show the time described by STRING instead of now
  -r, --reference=FILE     show FILE's last modification time
  -u, --utc, --universal   use UTC (the clock is already UTC)
  -I, --iso-8601           print the date as YYYY-MM-DD
  -R, --rfc-email          print like: Fri, 20 Nov 2026 13:30:00 +0000

STRING can combine, in any order:
  dates      2026-11-20   11/20/2026   20 Nov 2026   Nov 20, 2026   20261120
  times      13:30   13:30:05.25   1:30pm   (2026-11-20T13:30:00 also works)
  zones      UTC  Z  GMT  EST  PDT  +0100  -05:00  UTC+2   (converted to UTC)
  weekdays   Monday, fri   (moves forward to that day)
  words      now  today  yesterday  tomorrow
  relative   3 days ago   +2 hours   next week   last month   1 year
  epoch      @1795168800

FORMAT controls the output: %Y %m %d %H %M %S %F %T %s %a %b %A %B %j %Z %z %n %%,
and %-d etc. for no padding. Example: date -d '20 Nov 2026 13:30 +0100' +%s

`;

// ---------- registration ----------

export function registerBuiltins(shell: Shell) {
  const reg = (name: string, handler: CommandHandler, description: string, usage?: string) => shell.register(name, handler, description, usage);

  reg('true', () => 0, 'Do nothing, successfully');
  reg(':', () => 0, 'Do nothing, successfully (the null command)');
  reg('false', () => 1, 'Do nothing, unsuccessfully');

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
  }, 'Print arguments');

  reg('printf', ctx => {
    const args = [...ctx.args];
    let variable: string | undefined;
    if (args.length >= 2 && args[0] === '-v') { variable = args[1]; args.splice(0, 2); }
    if (args[0] === '--') args.shift();
    if (args.length === 0) throw new ShellError('usage: printf [-v var] format [arguments]', 2);
    const errors: string[] = [];
    const text = printfFormat(args[0], args.slice(1), errors);
    if (variable === undefined) ctx.stdout.write(text); else ctx.shell.setVar(variable, text);
    for (const e of errors) ctx.stderr.write(e + '\n');
    return errors.length > 0 ? 1 : 0;
  }, 'Format and print data');

  reg('cd', ctx => {
    const sh = ctx.shell;
    const args = [...ctx.args];
    while (['-L', '-P', '-e', '-@'].includes(args[0])) args.shift();
    if (args[0] === '--') args.shift();
    if (args.length > 1) throw new ShellError('too many arguments');
    const target = args.length === 0 ? sh.environment.get('HOME') ?? '/' : args[0] === '-' ? sh.environment.get('OLDPWD') ?? sh.cwd : args[0];
    const abs = ctx.resolvePath(target);
    if (!ctx.fs.exists(abs)) throw new ShellError(`${target}: ${ctx.fs.whyMissing(abs)}`);
    if (!ctx.fs.isDirectory(abs)) throw new ShellError(`${target}: Not a directory`);
    if (!ctx.fs.canExecute(abs)) throw new ShellError(`${target}: Permission denied`);
    sh.environment.set('OLDPWD', sh.cwd);
    sh.cwd = abs;
    if (args.length === 1 && args[0] === '-') line(ctx, abs);
  }, 'Change the working directory');

  reg('pwd', ctx => line(ctx, ctx.shell.cwd), 'Print the working directory');

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
        if (!isName(name)) throw new ShellError(`\`${a}': not a valid identifier`);
        const target = sh.resolve(name);
        if (eq > 0) sh.setVar(name, a.slice(eq + 1), append);
        else if (!sh.arrays.has(target) && !sh.environment.has(target)) { sh.markShellVariable(target); sh.environment.set(target, ''); }
        if (remove) sh.unexport(target); else sh.setAttrs(target, 'x');
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`bash: export: ${e.message}\n`);
        code = 1;
      }
    }
    return code;
  }, 'Set environment variables');

  reg('unset', async ctx => {
    const sh = ctx.shell;
    let k = 0;
    while (k < ctx.args.length && ctx.args[k].startsWith('-')) k++;
    const flags = ctx.args.slice(0, k).join('');
    let code = 0;
    for (const arg of ctx.args.slice(k)) {
      try {
        const a = await sh.expandSubscript(arg, ioOf(ctx));
        // with neither -v nor -f, a name that is no variable but a function unsets the function
        if (flags.includes('f') || (!flags.includes('v') && !sh.isVariableSet(a) && sh.functions.has(a))) sh.functions.delete(a);
        else sh.unset(a, flags.includes('n'));
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`bash: unset: ${e.message}\n`);
        code = 1;
      }
    }
    return code;
  }, 'Remove variables or functions (-f)');

  const printEnv = (ctx: CommandContext) => {
    for (const k of [...ctx.shell.environment.keys()].sort(ordinal)) if (ctx.shell.isExported(k)) line(ctx, `${k}=${ctx.shell.environment.get(k)}`);
  };

  reg('env', async ctx => {
    // env [-i] [-u NAME] [NAME=VALUE]... [COMMAND [ARG]...]
    const args = [...ctx.args];
    const sets: [string, string][] = [];
    const unsets: string[] = [];
    let clear = false;
    while (args.length) {
      if (args[0] === '-i' || args[0] === '-') { clear = true; args.shift(); continue; }
      if (args[0] === '-u' && args.length > 1) { unsets.push(args[1]); args.splice(0, 2); continue; }
      if (args[0] === '--') { args.shift(); break; }
      const eq = args[0].indexOf('=');
      if (eq > 0) { sets.push([args[0].slice(0, eq), args[0].slice(eq + 1)]); args.shift(); continue; }
      break;
    }
    if (args.length === 0 && sets.length === 0 && !clear && unsets.length === 0) { printEnv(ctx); return 0; }
    const sh = ctx.shell;
    const savedEnv = sh.environment, savedAttrs = sh.varAttrs;
    sh.environment = new Map(clear ? [] : [...savedEnv].filter(([k]) => sh.isExported(k) && !unsets.includes(k)));
    sh.varAttrs = new Map([...sh.environment.keys()].map(k => [k, 'x']));
    for (const [k, v] of sets) { sh.environment.set(k, v); sh.varAttrs.set(k, 'x'); }
    try {
      if (args.length === 0) { printEnv(ctx); return 0; }
      return await sh.invoke(args, ioOf(ctx), true);
    } finally { sh.environment = savedEnv; sh.varAttrs = savedAttrs; }
  }, 'Print the environment, or run a command in a modified one');

  reg('alias', ctx => {
    const sh = ctx.shell;
    let code = 0;
    const args = ctx.args.filter(a => a !== '-p');
    if (args.length === 0) for (const k of [...sh.aliases.keys()].sort(ordinal)) line(ctx, `alias ${k}='${sh.aliases.get(k)}'`);
    for (const a of args) {
      const eq = a.indexOf('=');
      if (eq > 0) sh.aliases.set(a.slice(0, eq), a.slice(eq + 1));
      else if (sh.aliases.has(a)) line(ctx, `alias ${a}='${sh.aliases.get(a)}'`);
      else { ctx.stderr.write(`bash: alias: ${a}: not found\n`); code = 1; }
    }
    return code;
  }, 'Define or list aliases');

  reg('unalias', ctx => {
    if (ctx.args.includes('-a')) { ctx.shell.aliases.clear(); return 0; }
    let code = 0;
    for (const a of ctx.args) if (!ctx.shell.aliases.delete(a)) { ctx.stderr.write(`bash: unalias: ${a}: not found\n`); code = 1; }
    return code;
  }, 'Remove aliases');

  reg('history', ctx => {
    const h = ctx.shell.history;
    const n = ctx.args.length && /^\d+$/.test(ctx.args[0]) ? Number(ctx.args[0]) : h.length;
    for (let i = Math.max(0, h.length - n); i < h.length; i++) line(ctx, `${String(i + 1).padStart(5)}  ${h[i]}`);
  }, 'Show command history');

  reg('which', ctx => {
    let code = 0;
    for (const a of ctx.args.filter(x => !x.startsWith('-'))) {
      if (a.includes('/')) { if (ctx.fs.canExecute(ctx.resolvePath(a)) && ctx.fs.isFile(ctx.resolvePath(a))) line(ctx, a); else code = 1; }
      else if (ctx.shell.commands.has(a) && !(SHELL_BUILTINS.has(a) && !ctx.fs.exists('/usr/bin/' + a))) line(ctx, `/usr/bin/${a}`);
      else code = 1;
    }
    return code;
  }, 'Locate a command');

  reg('type', ctx => {
    const sh = ctx.shell;
    let code = 0;
    const opts = Args.parse(ctx.args, 'atpPf');
    for (const a of opts.positional) {
      if (opts.has('t') || opts.has('p') || opts.has('P')) {
        const kind = sh.aliases.has(a) ? 'alias' : SHELL_KEYWORDS.has(a) ? 'keyword' : sh.functions.has(a) ? 'function'
          : isShellBuiltin(sh, a) ? 'builtin' : sh.commands.has(a) ? 'file' : null;
        if (kind === null) { code = 1; continue; }
        if (opts.has('t')) line(ctx, kind);
        else if (kind === 'file' || (opts.has('P') && sh.commands.has(a))) line(ctx, `/usr/bin/${a}`);
        continue;
      }
      if (!describe(ctx, a)) code = 1;
    }
    return code;
  }, 'Describe a command');

  reg('test', ctx => (testEvaluate(ctx.shell, [...ctx.args]) ? 0 : 1), 'Evaluate a conditional expression');
  reg('[', ctx => {
    if (ctx.args.length === 0 || ctx.args[ctx.args.length - 1] !== ']') throw new ShellError("missing `]'", 2);
    return testEvaluate(ctx.shell, ctx.args.slice(0, -1)) ? 0 : 1;
  }, 'Evaluate a conditional expression');

  const runScript: CommandHandler = async ctx => {
    const io = ioOf(ctx);
    let args = [...ctx.args];
    const sh = ctx.shell;
    const isBash = ctx.name === 'bash' || ctx.name === 'sh';
    let command = false;
    // bash's own options: -c, and -e -u -x -o pipefail ... which the new shell starts with
    const options: [string, boolean][] = [];
    let i = 0;
    for (; isBash && i < args.length && args[i].length > 1 && (args[i][0] === '-' || args[i][0] === '+'); i++) {
      const f = args[i];
      if (f === '--') { i++; break; }
      if (f.startsWith('--')) continue; // --norc, --noprofile, --login
      for (const c of f.slice(1)) {
        if (c === 'c') command = true;
        else if (c === 'o' && i + 1 < args.length) options.push([args[++i], f[0] === '-']);
        else if ('eufxC'.includes(c)) options.push([({ e: 'errexit', u: 'nounset', f: 'noglob', x: 'xtrace', C: 'noclobber' } as Record<string, string>)[c], f[0] === '-']);
      }
    }
    args = args.slice(i);
    const setup = () => { for (const [name, on] of options) sh.setOption(name, on); };
    if (command) {
      if (args.length === 0) throw new ShellError('-c: option requires an argument', 2);
      return sh.runScriptText(args[0], io, true, args.slice(2), { arg0: args[1] ?? ctx.name, setup });
    }
    if (args.length === 0) {
      if (!isBash) throw new ShellError('filename argument required', 2);
      return sh.runScriptText(ctx.stdinText, { ...io, stdin: new StdinBuffer('') }, true, [], { arg0: ctx.name, setup });
    }
    let text: string;
    try { text = ctx.fs.readText(ctx.resolvePath(args[0])); } catch (e) {
      if (e instanceof ShellError) throw new ShellError(`${args[0]}: ${e.message.slice(e.message.lastIndexOf(': ') + 2)}`, isBash ? 127 : 1);
      throw e;
    }
    if (isBash) return sh.runScriptText(text, io, true, args.slice(1), { arg0: args[0], file: args[0], setup });
    sh.sourceDepth++;
    try { return await sh.runScriptText(text, io, false, args.length > 1 ? args.slice(1) : undefined, undefined, args[0]); } catch (e) {
      if (e instanceof ReturnSignal) return e.code; // `return` ends the sourced file only
      throw e;
    } finally { sh.sourceDepth--; }
  };
  reg('bash', runScript, 'Run a command string (-c) or script file');
  reg('sh', runScript, 'Run a command string (-c) or script file');
  reg('source', runScript, 'Run a script in the current shell');
  reg('.', runScript, 'Run a script in the current shell');

  reg('help', ctx => {
    const sh = ctx.shell;
    if (ctx.args.length > 0) {
      const c = sh.commands.get(ctx.args[0]);
      if (!c) throw new ShellError(`no help topics match '${ctx.args[0]}'`);
      line(ctx, c.usage ?? `${c.name}: ${c.description}`);
      return 0;
    }
    const cmds = [...sh.commands.values()].sort((a, b) => ordinal(a.name, b.name));
    const width = Math.max(...cmds.map(c => c.name.length)) + 2;
    line(ctx, "Available commands (run '<command> --help' for details on non-standard ones):");
    for (const c of cmds) line(ctx, `  ${c.name.padEnd(width)}${c.description}`);
  }, 'List available commands');

  // ---------- scripting ----------

  reg('exit', ctx => { throw new ExitSignal(parseCount(ctx, ctx.shell.lastExitCode) & 255); }, 'Exit the shell');
  reg('return', ctx => {
    if (!ctx.shell.inFunction && ctx.shell.sourceDepth === 0) throw new ShellError("can only `return' from a function or sourced script", 2);
    ctx.shell.returnSignal(parseCount(ctx, ctx.shell.lastExitCode) & 255);
  }, 'Return from a function');
  reg('break', ctx => loopJump(ctx, true), 'Exit a loop');
  reg('continue', ctx => loopJump(ctx, false), 'Next loop iteration');

  reg('shift', ctx => {
    const n = parseCount(ctx, 1);
    if (n > ctx.shell.positional.length) return 1;
    ctx.shell.positional = ctx.shell.positional.slice(n);
    return 0;
  }, 'Shift positional parameters');

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
  }, 'Run a command when the shell exits (EXIT), a command fails (ERR) or a function returns (RETURN)');

  const KNOWN_SHOPTS = ['assoc_expand_once', 'autocd', 'cdable_vars', 'cdspell', 'checkhash', 'checkjobs', 'checkwinsize', 'cmdhist',
    'complete_fullquote', 'direxpand', 'dirspell', 'dotglob', 'execfail', 'expand_aliases', 'extdebug', 'extglob', 'extquote',
    'failglob', 'force_fignore', 'globasciiranges', 'globskipdots', 'globstar', 'gnu_errfmt', 'histappend', 'histreedit',
    'histverify', 'hostcomplete', 'huponexit', 'inherit_errexit', 'interactive_comments', 'lastpipe', 'lithist',
    'localvar_inherit', 'localvar_unset', 'login_shell', 'mailwarn', 'no_empty_cmd_completion', 'nocaseglob', 'nocasematch',
    'noexpand_translation', 'nullglob', 'patsub_replacement', 'progcomp', 'progcomp_alias', 'promptvars', 'restricted_shell',
    'shift_verbose', 'sourcepath', 'varredir_close', 'xpg_echo'];
  reg('shopt', ctx => {
    const sh = ctx.shell;
    const a = Args.parse(ctx.args, 'supqo');
    const setting = a.has('s') || a.has('u');
    if (a.has('o')) {
      // shopt -o: the set -o options
      for (const n of a.positional) if (!sh.setOption(n, a.has('s')) && setting) throw new ShellError(`${n}: invalid option name`);
      if (setting) return 0;
      for (const [n, on] of sh.optionList) if (a.positional.length === 0 || a.positional.includes(n)) line(ctx, `${n.padEnd(15)}\t${on ? 'on' : 'off'}`);
      return 0;
    }
    const names = a.positional.length > 0 ? a.positional : KNOWN_SHOPTS;
    if (setting) {
      for (const n of names) {
        if (!KNOWN_SHOPTS.includes(n)) throw new ShellError(`${n}: invalid shell option name`);
        if (a.has('s')) sh.shopts.add(n); else sh.shopts.delete(n);
      }
      return 0;
    }
    let code = 0;
    for (const n of names) {
      if (!KNOWN_SHOPTS.includes(n)) { ctx.stderr.write(`bash: shopt: ${n}: invalid shell option name\n`); code = 1; continue; }
      const on = sh.shopts.has(n);
      if (code === 0 && !on) code = 1;
      if (!a.has('q')) line(ctx, `${n.padEnd(15)}\t${on ? 'on' : 'off'}`);
    }
    return code;
  }, 'Set shell options (nullglob, dotglob, globstar, extglob, nocasematch ...)');

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
          if (!sh.setOption(opt, on)) throw new ShellError(`${opt}: invalid option name`, 2);
        } else if ('eufxC'.includes(c)) sh.setOption(({ e: 'errexit', u: 'nounset', f: 'noglob', x: 'xtrace', C: 'noclobber' } as Record<string, string>)[c], on);
        else if (!'abhkmnpvBEHPT'.includes(c)) throw new ShellError(`${a[0]}${c}: invalid option`, 2); // accepted, no effect here
      }
    }
    if (i < args.length) sh.positional = args.slice(i);
    return 0;
  }, 'Set shell options (-e, -u, -x, -f, -C, -o pipefail) or positional parameters');

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
          default: throw new ShellError(`-${c}: invalid option`, 2);
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
        if (l) line(ctx, l); else { ctx.stderr.write(`bash: ${ctx.name}: ${n}: not found\n`); code = 1; }
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
        if (!isName(name)) throw new ShellError(`\`${arg}': not a valid identifier`);
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
        ctx.stderr.write(`bash: ${ctx.name}: ${e.message}\n`);
        code = 1;
      }
    }
    return code;
  };
  reg('local', declare, 'Declare function-local variables');
  reg('declare', declare, 'Declare variables and their attributes (-a -A -i -r -x -l -u -n -p -f)');
  reg('typeset', declare, 'Declare variables and their attributes (-a -A -i -r -x -l -u -n -p -f)');
  reg('readonly', declare, 'Make variables read-only');

  reg('read', ctx => {
    const a = Args.parse(ctx.args, 'rsea', 'ptdnuNi');
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
  }, 'Read a line from stdin into variables');

  const mapfile: CommandHandler = ctx => {
    const a = Args.parse(ctx.args, 't', 'dnsuCcO');
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
  reg('mapfile', mapfile, 'Read lines from stdin into an array (-t drops the newlines)');
  reg('readarray', mapfile, 'Read lines from stdin into an array (-t drops the newlines)');

  reg('getopts', ctx => {
    if (ctx.args.length < 2) throw new ShellError('usage: getopts optstring name [arg ...]', 2);
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
      else { ctx.stderr.write(`${sh.getVariable('0')}: illegal option -- ${c}\n`); sh.unset('OPTARG'); }
      return done(0, '?');
    }
    if (spec[at + 1] !== ':') { sh.unset('OPTARG'); return done(0, c); }
    if (pos > 1) { sh.setVar('OPTARG', arg.slice(pos)); optind++; pos = 1; }
    else if (optind <= args.length) sh.setVar('OPTARG', args[optind++ - 1]);
    else if (silent) { sh.setVar('OPTARG', c); return done(0, ':'); }
    else {
      ctx.stderr.write(`${sh.getVariable('0')}: option requires an argument -- ${c}\n`);
      sh.unset('OPTARG');
      return done(0, '?');
    }
    return done(0, c);
  }, 'Parse command-line options');

  reg('exec', async ctx => {
    // exec with only redirections is handled by the shell; with a command, the command's status ends the shell
    const args = [...ctx.args];
    while (args[0] === '-c' || args[0] === '-l') args.shift();
    if (args.length === 0) return 0;
    throw new ExitSignal(await ctx.shell.invoke(args, ioOf(ctx), true));
  }, "Replace the shell with a command, or redirect the shell's own input and output");

  reg('wait', ctx => {
    // background jobs ran to completion when they were started: wait just reports their status
    const sh = ctx.shell;
    let code = 0;
    const ids = ctx.args.filter(a => !a.startsWith('-'));
    if (ctx.args.includes('-n') && ids.length === 0) return sh.waitNext();
    if (ids.length === 0) sh.forgetJobs();
    for (const id of ids) {
      if (id.startsWith('%')) { code = 0; continue; }
      if (!/^\d+$/.test(id)) { ctx.stderr.write(`bash: wait: \`${id}': not a pid or valid job spec\n`); code = 2; continue; }
      code = sh.waitFor(Number(id));
      if (code === 127) ctx.stderr.write(`bash: wait: pid ${id} is not a child of this shell\n`);
    }
    return code;
  }, 'Wait for background jobs (they run to completion when started)');
  reg('jobs', () => 0, 'List background jobs (none are ever still running)');
  reg('disown', () => 0, 'Forget background jobs');

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
    if (targets.length === 0) throw new ShellError('usage: kill [-s sigspec | -n signum | -sigspec] pid | jobspec ... or kill -l [sigspec]', 2);
    for (const t of targets) {
      if (t === '4242') { if (signal !== 0) throw new ExitSignal(128 + signal); continue; } // the shell itself
      if (/^\d+$/.test(t) && ctx.shell.isJob(Number(t))) continue; // a finished job: nothing to signal
      ctx.stderr.write(t.startsWith('%') ? `bash: kill: ${t}: no such job\n` : `bash: kill: (${t}) - No such process\n`);
      code = 1;
    }
    return code;
  }, 'Send a signal to a process (background jobs have always finished already)');

  reg('hash', ctx => {
    let code = 0;
    for (const n of ctx.args.filter(a => !a.startsWith('-'))) if (!ctx.shell.commands.has(n)) { ctx.stderr.write(`bash: hash: ${n}: not found\n`); code = 1; }
    return code;
  }, 'Remember command locations');

  reg('builtin', ctx => {
    if (ctx.args.length === 0) return 0;
    if (!isShellBuiltin(ctx.shell, ctx.args[0])) throw new ShellError(`${ctx.args[0]}: not a shell builtin`);
    return ctx.shell.invoke([...ctx.args], ioOf(ctx), true);
  }, 'Run a shell builtin, bypassing a function of the same name');

  const dirs = (sh: Shell) => {
    const home = sh.environment.get('HOME') ?? '';
    const short = (d: string) => (home.length > 1 && (d === home || d.startsWith(home + '/')) ? '~' + d.slice(home.length) : d);
    return [sh.cwd, ...sh.dirStack].map(short).join(' ');
  };
  const changeDir = (ctx: CommandContext, dir: string) => {
    const abs = ctx.resolvePath(dir);
    if (!ctx.fs.isDirectory(abs)) throw new ShellError(`${dir}: No such file or directory`);
    ctx.shell.environment.set('OLDPWD', ctx.shell.cwd);
    ctx.shell.cwd = abs;
  };
  reg('pushd', ctx => {
    const sh = ctx.shell;
    const args = ctx.args.filter(a => a !== '-n');
    const old = sh.cwd;
    if (args.length === 0) {
      if (sh.dirStack.length === 0) throw new ShellError('no other directory');
      changeDir(ctx, sh.dirStack[0]);
      sh.dirStack[0] = old;
    } else { changeDir(ctx, args[0]); sh.dirStack.unshift(old); }
    line(ctx, dirs(sh));
  }, 'Change directory, remembering the old one');
  reg('popd', ctx => {
    const sh = ctx.shell;
    if (sh.dirStack.length === 0) throw new ShellError('directory stack empty');
    changeDir(ctx, sh.dirStack[0]);
    sh.dirStack.shift();
    line(ctx, dirs(sh));
  }, 'Return to the directory pushd left');
  reg('dirs', ctx => {
    if (ctx.args.includes('-c')) { ctx.shell.dirStack.length = 0; return 0; }
    const l = dirs(ctx.shell);
    if (ctx.args.some(a => a === '-p' || a === '-v')) l.split(' ').forEach((d, i) => line(ctx, ctx.args.includes('-v') ? `${String(i).padStart(2)}  ${d}` : d));
    else line(ctx, l);
  }, 'Show the directory stack');

  reg('ulimit', ctx => {
    if (ctx.args.some(a => !a.startsWith('-'))) return 0; // setting a limit: accepted
    line(ctx, ctx.args.includes('-n') ? '1024' : ctx.args.includes('-s') ? '8192' : ctx.args.includes('-c') ? '0' : 'unlimited');
  }, 'Show resource limits');
  reg('umask', ctx => { if (ctx.args.length === 0) line(ctx, '0002'); }, 'Show the file mode creation mask');
  reg('times', ctx => ctx.stdout.write('0m0.010s 0m0.005s\n0m0.000s 0m0.000s\n'), "Show the shell's CPU times");

  reg('eval', ctx => ctx.shell.runScriptText(ctx.args.join(' '), ioOf(ctx), false), 'Run arguments as a shell command');

  reg('let', ctx => {
    if (ctx.args.length === 0) throw new ShellError('expression expected');
    let v = 0n;
    for (const expr of ctx.args) v = evalArith(ctx.shell, expr);
    return v !== 0n ? 0 : 1;
  }, 'Evaluate arithmetic expressions');

  reg('command', async ctx => {
    const sh = ctx.shell;
    const args = [...ctx.args];
    while (args[0] === '-p') args.shift();
    const isDescribe = args[0] === '-v' || args[0] === '-V';
    if (!isDescribe) return args.length === 0 ? 0 : sh.invoke(args, ioOf(ctx), true);
    let code = 0;
    for (const n of args.slice(1)) {
      if (args[0] === '-V') { if (!describe(ctx, n)) code = 1; continue; }
      const al = sh.aliases.get(n);
      if (al !== undefined) line(ctx, `alias ${n}='${al}'`);
      else if (SHELL_KEYWORDS.has(n) || sh.functions.has(n) || isShellBuiltin(sh, n)) line(ctx, n);
      else if (sh.commands.has(n)) line(ctx, `/usr/bin/${n}`);
      else if (n.includes('/') && sh.agentFs.isFile(sh.resolvePath(n)) && sh.agentFs.canExecute(sh.resolvePath(n))) line(ctx, n);
      else code = 1;
    }
    return code;
  }, 'Run a command, or describe it with -v');

  reg('printenv', ctx => {
    if (ctx.args.length === 0) { printEnv(ctx); return 0; }
    let code = 0;
    for (const n of ctx.args) { const v = ctx.shell.environment.get(n); if (v !== undefined && ctx.shell.isExported(n)) line(ctx, v); else code = 1; }
    return code;
  }, 'Print environment variables');

  reg('sleep', async ctx => {
    if (ctx.args.length === 0) throw new ShellError('missing operand');
    const total = ctx.args.reduce((t, a) => t + parseDuration(a), 0);
    const end = Date.now() + total;
    // sleeps really wait, up to the call's (or a `timeout`'s) deadline
    for (;;) {
      ctx.checkTime();
      const left = Math.min(end - Date.now(), ctx.remainingMs() + 1);
      if (left <= 0) break;
      await new Promise(r => setTimeout(r, Math.min(left, 1000)));
    }
    ctx.checkTime();
  }, 'Pause for a number of seconds');

  reg('timeout', async ctx => {
    const args = [...ctx.args];
    while (args.length && args[0].startsWith('-')) { const a = args.shift()!; if ((a === '-s' || a === '-k') && args.length) args.shift(); }
    if (args.length < 2) throw new ShellError('missing operand', 125);
    const sh = ctx.shell;
    const parent = sh.deadline;
    const own: Deadline = { at: Date.now() + parseDuration(args[0]), parent };
    sh.deadline = own;
    try { return await sh.invoke(args.slice(1), ioOf(ctx)); } catch (e) {
      if (e instanceof DeadlineExceeded && e.owner === own) return 124;
      throw e;
    } finally { sh.deadline = parent; }
  }, 'Run a command with a time limit');

  reg('time', async ctx => {
    const start = Date.now();
    const code = ctx.args.length === 0 ? 0 : await ctx.shell.invoke([...ctx.args], ioOf(ctx));
    const t = (Date.now() - start) / 1000;
    ctx.stderr.write(`\nreal\t${Math.floor(t / 60)}m${(t % 60).toFixed(3)}s\nuser\t0m0.000s\nsys\t0m0.000s\n`);
    return code;
  }, 'Time a command');

  reg('clear', () => 0, 'Clear the terminal');

  // ---------- system information ----------

  reg('whoami', ctx => line(ctx, ctx.shell.options.user), 'Print the current user');
  reg('hostname', ctx => line(ctx, ctx.shell.options.hostname), 'Print the host name');
  reg('uname', ctx => {
    const a = Args.parse(ctx.args, 'asnrvmoip', '', 'all=a,kernel-name=s,nodename=n,kernel-release=r,machine=m,operating-system=o');
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
  }, 'Print system information');

  reg('date', ctx => {
    // the simulated machine's clock is UTC (TZ is unset), so output never depends on the host's time zone
    const a = Args.parse(ctx.args, 'uIR', 'dr', 'utc=u,universal=u,iso-8601=I,date=d,reference=r,rfc-email=R');
    let now = Date.now();
    const d = a.value('d'), r = a.value('r');
    if (d !== undefined) now = parseDate(d, now);
    else if (r !== undefined) now = ctx.fs.stat(ctx.resolvePath(r)).modified;
    const fmt = a.positional.find(p => p.startsWith('+'));
    const extra = a.positional.find(p => !p.startsWith('+'));
    if (extra !== undefined) throw new ShellError(`invalid date '${extra}'`);
    line(ctx, strftime(now, a.has('I') ? '%Y-%m-%d' : a.has('R') ? '%a, %d %b %Y %H:%M:%S +0000' : fmt !== undefined ? fmt.slice(1) : '%a %b %e %H:%M:%S %Z %Y'));
  }, 'Print the date and time', DATE_USAGE);

  reg('ps', ctx => ctx.stdout.write('    PID TTY          TIME CMD\n      1 pts/0    00:00:00 bash\n   4242 pts/0    00:00:00 ps\n'), 'List processes');
  reg('id', ctx => {
    const u = ctx.shell.options.user;
    const a = Args.parse(ctx.args, 'ugnGr');
    line(ctx, a.has('u') || a.has('g') || a.has('G') ? (a.has('n') ? u : '1000') : `uid=1000(${u}) gid=1000(${u}) groups=1000(${u})`);
  }, 'Print user and group IDs');
  reg('uptime', ctx => line(ctx, ctx.args.includes('-p') ? 'up 3 days, 4 hours, 12 minutes'
    : ` ${strftime(Date.now(), '%H:%M:%S')} up 3 days,  4:12,  1 user,  load average: 0.08, 0.03, 0.01`), 'Show how long the system has been running');
  reg('free', ctx => ctx.stdout.write(ctx.args.some(x => x.includes('h'))
    ? '               total        used        free      shared  buff/cache   available\nMem:           7.6Gi       1.9Gi       3.8Gi        12Mi       2.1Gi       5.5Gi\nSwap:          2.0Gi          0B       2.0Gi\n'
    : '               total        used        free      shared  buff/cache   available\nMem:         8014880     1992344     3984512       12288     2201024     5748660\nSwap:        2097148           0     2097148\n'), 'Display memory usage');
  reg('nproc', ctx => line(ctx, '4'), 'Print the number of processors');

  // the agent is a regular user: privilege escalation fails the way it does on a real server
  reg('sudo', ctx => {
    const user = ctx.shell.options.user;
    if (ctx.args.length === 1 && (ctx.args[0] === '-V' || ctx.args[0] === '--version')) { line(ctx, 'Sudo version 1.9.15p5'); return 0; }
    if (ctx.args.length === 0) { ctx.stderr.write('usage: sudo -h | -K | -k | -V\nusage: sudo [-AbEHknPS] [-u user] [command [arg ...]]\n'); return 1; }
    if (ctx.args[0] === '-l' || ctx.args[0] === '--list') { ctx.stderr.write(`Sorry, user ${user} may not run sudo on ${ctx.shell.options.hostname}.\n`); return 1; }
    ctx.stderr.write(`${user} is not in the sudoers file.\n`);
    return 1;
  }, 'Execute a command as another user');
  reg('su', ctx => { ctx.stderr.write('Password: \nsu: Authentication failure\n'); return 1; }, 'Run a command with a substitute user ID');
}

export { VirtualFileSystem };
