// File commands: ls cat mkdir touch rm rmdir cp mv find tree stat du df chmod chown file mktemp realpath ...
import { Args, ShellError, UsageException, inputs, splitLines, quote, quoteAlways, quoteL, type CommandContext, type CommandHandler } from '../command.js';
import { VirtualFileSystem, NO_SPACE, type FileStat } from '../vfs.js';
import { globRegex } from '../glob.js';
import { strftime } from '../format.js';
import { parseDate } from './builtins.js';
import type { Shell } from '../shell.js';

const norm = VirtualFileSystem.normalize;
const nameOf = VirtualFileSystem.getName;
const line = (ctx: CommandContext, s: string) => ctx.stdout.write(s + '\n');
const trimEnd = (s: string, c: string) => { let e = s.length; while (e > 0 && s[e - 1] === c) e--; return s.slice(0, e); };

/** A filesystem error "/abs/dir/x: reason" as [arg/x, reason]: the path shown the way the user typed it. */
function typed(e: Error, arg: string, abs: string): [string, string] {
  const i = e.message.lastIndexOf(': ');
  const path = i < 0 ? abs : e.message.slice(0, i);
  const base = trimEnd(abs, '/');
  return [path === abs ? arg : path.startsWith(base + '/') ? trimEnd(arg, '/') + path.slice(base.length) : path, e.message.slice(i + 2)];
}

/** ls -h: powers of 1024, rounded up; one decimal below 10 (4.0K, 1.1M), whole numbers above (121K). */
function humanSize(n: number): string {
  if (n < 1024) return String(n);
  let v = n, u = -1;
  do { v /= 1024; u++; } while (v >= 1024 && u < 5);
  const tenths = Math.ceil(v * 10) / 10;
  if (tenths < 10) return tenths.toFixed(1) + 'KMGTPE'[u];
  v = Math.ceil(v);
  return v >= 1024 && u < 5 ? '1.0' + 'KMGTPE'[u + 1] : String(v) + 'KMGTPE'[u];
}
const fail = (message: string): never => { throw new ShellError(message); };
const fmt1 = (v: number) => String(Math.round(v * 10) / 10);

/** ls -l's date: "Nov 25 16:52" within the last six months, else "Jan  2  2020" (also for future times). */
const lsDate = (t: number, now: number) => strftime(t, t <= now && (now - t) / 1000 < 15778476 ? '%b %e %H:%M' : '%b %e  %Y');

/** ls -l rows, each column as wide as its widest entry (owners left-aligned, numbers right-aligned); `widthFrom`
 * are more paths that count toward the widths (ls sizes the columns for its file operands over all operands). */
function longEntries(ctx: CommandContext, entries: [abs: string, name: string][], human: boolean, widthFrom: string[] = []): string[] {
  const now = ctx.fs.clock();
  const size = (st: FileStat) => (human ? humanSize(st.size) : String(st.size));
  const rows = entries.map(([abs, name]) => ({ st: ctx.fs.stat(abs), name }));
  if (rows.length === 0) return [];
  const all = [...rows.map(r => r.st), ...widthFrom.map(p => ctx.fs.stat(p))];
  const width = (f: (st: FileStat) => string) => all.reduce((m, st) => Math.max(m, f(st).length), -Infinity);
  const lw = width(st => String(st.links)), ow = width(st => st.owner), sw = width(size);
  return rows.map(({ st, name }) => `${VirtualFileSystem.formatMode(st.isDirectory, st.mode)} ${String(st.links).padStart(lw)} ` +
    `${st.owner.padEnd(ow)} ${st.owner.padEnd(ow)} ${size(st).padStart(sw)} ${lsDate(st.modified, now)} ${name}`);
}

/** touch -t [[CC]YY]MMDDhhmm[.ss]; a two-digit year 69-99 is 19YY, else 20YY. */
function touchStamp(t: string, now: number): number | null {
  const m = /^(\d{2}|\d{4})?(\d{2})(\d{2})(\d{2})(\d{2})(?:\.(\d{2}))?$/.exec(t);
  if (!m) return null;
  const y = m[1] ?? '';
  const year = y.length === 0 ? new Date(now).getUTCFullYear() : y.length === 4 ? Number(y) : Number(y) + (Number(y) >= 69 ? 1900 : 2000);
  const [mo, d, h, mi, sec] = [m[2], m[3], m[4], m[5], m[6] ?? '0'].map(Number);
  const ms = Date.UTC(year, mo - 1, d, h, mi, sec);
  const back = new Date(ms);
  return back.getUTCMonth() === mo - 1 && back.getUTCDate() === d && h < 24 && mi < 60 && sec < 60 ? ms : null;
}

function showNonprinting(s: string): string {
  let out = '';
  for (const ch of s) {
    const c = ch.charCodeAt(0);
    out += ch === '\t' || (c >= 32 && c < 127) || c > 159 ? ch : c === 127 ? '^?' : c < 32 ? '^' + String.fromCharCode(c + 64) : 'M-^' + String.fromCharCode(c - 64);
  }
  return out;
}

/** chmod's mode argument: octal, or symbolic clauses like u+x,go-w. */
export function parseMode(spec: string, mode: number, isDir: boolean): number {
  if (/^[0-7]{1,4}$/.test(spec)) return parseInt(spec, 8);
  for (const clause of spec.split(',')) {
    const m = /^([ugoa]*)([-+=])([rwxXt]*)$/.exec(clause);
    if (!m) throw new ShellError(`invalid mode: '${spec}'`);
    const who = m[1] === '' || m[1] === 'a' ? 'ugo' : m[1].replace('a', 'ugo');
    let bits = 0, sticky = false;
    for (const c of m[3]) {
      if (c === 'r') bits |= 4;
      else if (c === 'w') bits |= 2;
      else if (c === 'x') bits |= 1;
      else if (c === 'X') { if (isDir || mode & 0o111) bits |= 1; }
      else if (c === 't') sticky = true;
    }
    let mask = 0, clear = 0;
    for (const w of who) {
      const shift = w === 'u' ? 6 : w === 'g' ? 3 : 0;
      mask |= bits << shift;
      clear |= 7 << shift;
    }
    const st = sticky ? 0o1000 : 0;
    mode = m[2] === '+' ? mode | mask | st : m[2] === '-' ? mode & ~(mask | st) : (mode & ~clear) | mask | st;
  }
  return mode;
}

// ---------- find ----------

interface Entry { abs: string; name: string; display: string; depth: number; start: string }
type Pred = (e: Entry) => Promise<boolean> | boolean;
class Quit extends Error {}

const TIME_FORMATS: Record<string, string> = {
  Y: '%Y', m: '%m', d: '%d', H: '%H', M: '%M', S: '%S.0000000000', T: '%H:%M:%S.0000000000', F: '%Y-%m-%d', '+': '%Y-%m-%d+%H:%M:%S.0000000000',
  a: '%a', b: '%b', y: '%y', D: '%m/%d/%y', R: '%H:%M',
};

/** find with real expressions: ( ) ! -not -a -o, tests, and actions (-print -print0 -printf -exec -delete). */
class Find {
  private args: string[] = [];
  private p = 0;
  private maxDepth = Infinity;
  private minDepth = 0;
  private hasAction = false;
  private prune = false;
  private readonly deletes: string[] = [];
  private readonly batches: { cmd: string[]; paths: string[] }[] = [];
  constructor(private ctx: CommandContext) {}

  async run(): Promise<number> {
    const ctx = this.ctx;
    const starts: string[] = [];
    let k = 0;
    while (k < ctx.args.length && !ctx.args[k].startsWith('-') && ctx.args[k] !== '!' && ctx.args[k] !== '(') starts.push(ctx.args[k++]);
    this.args = ctx.args.slice(k);
    if (starts.length === 0) starts.push('.');
    const expr: Pred = this.args.length === 0 ? () => true : this.parseOr();
    if (this.p < this.args.length) throw new ShellError(`unexpected \`${this.args[this.p]}'`);
    let code = 0;
    let visited = 0;
    const walk = async (abs: string, display: string, depth: number, start: string): Promise<void> => {
      if (++visited % 500 === 0) ctx.checkTime();
      this.prune = false;
      if (depth >= this.minDepth && (await expr({ abs, name: nameOf(abs), display, depth, start })) && !this.hasAction) line(ctx, display);
      if (this.prune || depth >= this.maxDepth || !ctx.fs.isDirectory(abs)) return;
      let names: string[];
      try { names = ctx.fs.list(abs); } catch { ctx.stderr.write(`find: ${quoteL(display)}: Permission denied\n`); code = 1; return; }
      for (const name of names) await walk(norm(abs, name), display.endsWith('/') ? display + name : display + '/' + name, depth + 1, start);
    };
    try {
      for (const s of starts) {
        const abs = ctx.resolvePath(s);
        if (!ctx.fs.exists(abs)) { ctx.stderr.write(`find: ${quoteL(s)}: ${ctx.fs.whyMissing(abs)}\n`); code = 1; continue; }
        await walk(abs, s, 0, s);
      }
    } catch (e) { if (!(e instanceof Quit)) throw e; }
    for (const { cmd, paths } of this.batches)
      if (paths.length > 0 && (await ctx.invoke(cmd.flatMap(x => (x === '{}' ? paths : [x])))) !== 0) code = 1;
    for (const path of [...this.deletes].sort((a, b) => b.length - a.length)) {
      try { if (ctx.fs.exists(path)) ctx.fs.delete(path, true); } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`find: cannot delete '${path}': ${e.message.slice(e.message.lastIndexOf(': ') + 2)}\n`);
        code = 1;
      }
    }
    return code;
  }

  private is(s: string) { return this.args[this.p] === s; }

  private parseOr(): Pred {
    let l = this.parseAnd();
    while (this.is('-o') || this.is('-or')) {
      this.p++;
      const left = l, right = this.parseAnd();
      l = async e => (await left(e)) || (await right(e));
    }
    return l;
  }

  private parseAnd(): Pred {
    let l = this.parseNot();
    while (this.p < this.args.length && !this.is('-o') && !this.is('-or') && !this.is(')')) {
      if (this.is('-a') || this.is('-and')) this.p++;
      const left = l, right = this.parseNot();
      l = async e => (await left(e)) && (await right(e));
    }
    return l;
  }

  private parseNot(): Pred {
    if (this.is('!') || this.is('-not')) {
      this.p++;
      const inner = this.parseNot();
      return async e => !(await inner(e));
    }
    if (this.is('(')) {
      this.p++;
      const inner = this.parseOr();
      if (!this.is(')')) throw new ShellError("invalid expression; expected `)'");
      this.p++;
      return inner;
    }
    return this.primary();
  }

  /** find -printf: %p %f %h %P %s %d %y %m %M %u %g %k %t %T@ %Tk ... with optional width, plus \n \t \0 escapes. */
  private printf(fmt: string, e: Entry): string {
    const st = this.ctx.fs.stat(e.abs);
    let out = '';
    for (let i = 0; i < fmt.length;) {
      const c = fmt[i];
      if (c === '\\' && i + 1 < fmt.length) {
        const n = fmt[i + 1];
        out += ({ n: '\n', t: '\t', '0': '\0', '\\': '\\', r: '\r' } as Record<string, string>)[n] ?? '\\' + n;
        i += 2;
        continue;
      }
      if (c !== '%' || i + 1 >= fmt.length) { out += c; i++; continue; }
      const m = /^%([-+ #0]*)(\d*)(?:\.(\d+))?/.exec(fmt.slice(i))!;
      let j = i + m[0].length;
      if (j >= fmt.length) { out += fmt.slice(i); break; }
      const d = fmt[j++];
      if (d === '%') { out += '%'; i = j; continue; }
      let value: string;
      if (d === 'T' || d === 'A' || d === 'C') {
        const k = fmt[j++] ?? '';
        value = k === '@' ? (st.modified / 1000).toFixed(10) : TIME_FORMATS[k] ? strftime(st.modified, TIME_FORMATS[k]) : '%' + d + k;
      } else if (d === 't') value = strftime(st.modified, '%a %b %d %H:%M:%S.0000000000 %Y');
      else {
        const trimmed = trimEnd(e.display, '/');
        const baseName = trimmed.length === 0 ? '/' : trimmed.slice(trimmed.lastIndexOf('/') + 1);
        switch (d) {
          case 'p': value = e.display; break;
          case 'f': value = e.depth === 0 ? baseName : e.name; break;
          case 'h': value = trimmed.includes('/') ? e.display.slice(0, e.display.lastIndexOf('/')) || '/' : '.'; break;
          case 'P': value = e.depth > 0 ? e.display.slice(e.start.length).replace(/^\/+/, '') : ''; break;
          case 'H': value = e.start; break;
          case 's': value = String(st.size); break;
          case 'd': value = String(e.depth); break;
          case 'y': case 'Y': value = st.isDirectory ? 'd' : 'f'; break;
          case 'm': value = (st.mode & 0o7777).toString(8); break;
          case 'M': value = VirtualFileSystem.formatMode(st.isDirectory, st.mode); break;
          case 'u': case 'g': value = st.owner; break;
          case 'n': value = '1'; break;
          case 'k': value = String(Math.ceil(st.size / 4096) * 4); break;
          case 'b': value = String(Math.ceil(st.size / 4096) * 8); break;
          default: value = '%' + d;
        }
      }
      const width = m[2] ? Number(m[2]) : 0;
      out += m[1].includes('-') ? value.padEnd(width) : value.padStart(width);
      i = j;
    }
    return out;
  }

  private primary(): Pred {
    const tok = this.args[this.p++];
    const arg = () => {
      if (this.p >= this.args.length) throw new ShellError(`missing argument to \`${tok}'`);
      return this.args[this.p++];
    };
    const fs = this.ctx.fs, ctx = this.ctx;
    switch (tok) {
      case '-name': case '-iname': { const re = globRegex(arg(), tok === '-iname'); return e => re.test(e.name); }
      case '-path': case '-ipath': case '-wholename': { const re = globRegex(arg(), tok === '-ipath', true); return e => re.test(e.display); }
      case '-regex': case '-iregex': {
        const src = arg();
        let re: RegExp;
        try { re = new RegExp('^(?:' + src + ')$', tok === '-iregex' ? 'i' : ''); } catch { throw new ShellError(`invalid regular expression '${src}'`); }
        return e => re.test(e.display);
      }
      case '-type': {
        const t = arg();
        if (!['f', 'd', 'l'].includes(t) && !/^[fdl](,[fdl])*$/.test(t)) throw new ShellError(`Unknown argument to -type: ${t}`);
        const types = t.split(',');
        return e => types.some(x => x !== 'l' && fs.isDirectory(e.abs) === (x === 'd'));
      }
      case '-maxdepth': this.maxDepth = Number(arg()); return () => true;
      case '-mindepth': this.minDepth = Number(arg()); return () => true;
      case '-mmin': case '-mtime': {
        const spec = arg();
        const unit = tok === '-mmin' ? 1 : 1440, n = Number(spec.replace(/^[+-]/, ''));
        return e => {
          const age = (ctx.shell.now() - fs.stat(e.abs).modified) / 60_000 / unit;
          return spec[0] === '-' ? age < n : spec[0] === '+' ? age > n : Math.floor(age) === n;
        };
      }
      case '-newer': { const refTime = fs.stat(ctx.resolvePath(arg())).modified; return e => fs.stat(e.abs).modified > refTime; }
      case '-size': {
        const m = /^([+-]?)(\d+)([cwbkMG]?)$/.exec(arg());
        if (!m) throw new ShellError('invalid -size argument');
        const unit = ({ c: 1, w: 2, k: 1024, M: 1 << 20, G: 1 << 30 } as Record<string, number>)[m[3]] ?? 512;
        const n = Number(m[2]);
        return e => {
          if (fs.isDirectory(e.abs)) return false;
          const size = Math.ceil(fs.stat(e.abs).size / unit);
          return m[1] === '+' ? size > n : m[1] === '-' ? size < n : size === n;
        };
      }
      case '-empty': return e => (fs.isDirectory(e.abs) ? fs.list(e.abs).length === 0 : fs.stat(e.abs).size === 0);
      case '-readable': return e => fs.canRead(e.abs);
      case '-writable': return e => fs.canWrite(e.abs);
      case '-executable': return e => fs.canExecute(e.abs);
      case '-user': { const u = arg(); return e => fs.stat(e.abs).owner === u; }
      case '-perm': {
        const spec = arg();
        const mode = parseInt(spec.replace(/^[-/]/, ''), 8);
        return e => {
          const m = fs.stat(e.abs).mode & 0o7777;
          return spec[0] === '-' ? (m & mode) === mode : spec[0] === '/' ? (m & mode) !== 0 : m === mode;
        };
      }
      case '-true': return () => true;
      case '-false': return () => false;
      case '-print': this.hasAction = true; return e => { line(ctx, e.display); return true; };
      case '-print0': this.hasAction = true; return e => { ctx.stdout.write(e.display + '\0'); return true; };
      case '-printf': { this.hasAction = true; const f = arg(); return e => { ctx.stdout.write(this.printf(f, e)); return true; }; }
      case '-ls': this.hasAction = true; return e => { line(ctx, longEntries(ctx, [[e.abs, e.display]], false)[0]); return true; };
      case '-quit': return () => { throw new Quit(); };
      case '-prune': return () => { this.prune = true; return true; };
      case '-delete': this.hasAction = true; return e => { this.deletes.push(e.abs); return true; };
      case '-exec': case '-execdir': case '-ok': {
        this.hasAction = true;
        const cmd: string[] = [];
        while (this.p < this.args.length && this.args[this.p] !== ';' && !(this.args[this.p] === '+' && cmd[cmd.length - 1] === '{}')) cmd.push(this.args[this.p++]);
        if (this.p >= this.args.length) throw new ShellError(`missing argument to \`${tok}'`);
        if (cmd.length === 0) throw new ShellError(`invalid argument \`${this.args[this.p]}' to \`${tok}'`);
        if (this.args[this.p++] === '+') {
          const batch = { cmd, paths: [] as string[] };
          this.batches.push(batch);
          return e => { batch.paths.push(e.display); return true; };
        }
        return async e => (await ctx.invoke(cmd.map(x => x.replaceAll('{}', e.display)))) === 0;
      }
    }
    throw new ShellError(`unknown predicate \`${tok}' (supported: -name -iname -path -regex -type -size -mmin -mtime -newer -empty ` +
      '-maxdepth -mindepth -prune -print -print0 -printf -exec -delete -quit ! -not -a -o ( ))');
  }
}

// ---------- registration ----------

export function registerFiles(shell: Shell) {
  const reg = (name: string, handler: CommandHandler, description: string, usage?: string) => shell.register(name, handler, description, usage);

  reg('ls', ctx => {
    const a = Args.parse(ctx.args, 'alhA1dFrtRSCZ', '', 'all=a,almost-all=A,human-readable=h,directory=d,reverse=r,recursive=R,color=1');
    const paths = a.positional.length > 0 ? a.positional : ['.'];
    const lng = a.has('l'), human = a.has('h');
    let code = 0;
    const fs = ctx.fs;
    const show = (entries: [string, string][], widthFrom?: string[]) => {
      const named = entries.map(([abs, name]): [string, string] => [abs, name + (a.has('F') && fs.isDirectory(abs) && name !== '.' && name !== '..' ? '/' : '')]);
      for (const l of lng ? longEntries(ctx, named, human, widthFrom) : named.map(e => e[1])) line(ctx, l);
    };
    const exists = (p: string) => fs.exists(ctx.resolvePath(p));
    // operands are listed like directory entries: byte order (the C locale), or by -t / -S, reversed by -r
    const ordered = (ps: string[]) => {
      let list = [...ps].sort();
      if (a.has('t')) list = list.sort((x, y) => fs.stat(ctx.resolvePath(y)).modified - fs.stat(ctx.resolvePath(x)).modified);
      if (a.has('S')) list = list.sort((x, y) => fs.stat(ctx.resolvePath(y)).size - fs.stat(ctx.resolvePath(x)).size);
      return a.has('r') ? list.reverse() : list;
    };
    const files = ordered(paths.filter(p => exists(p) && (a.has('d') || !fs.isDirectory(ctx.resolvePath(p)))));
    for (const p of paths.filter(p => !exists(p))) { ctx.stderr.write(`ls: cannot access '${p}': ${fs.whyMissing(ctx.resolvePath(p))}\n`); code = 2; }
    show(files.map(f => [ctx.resolvePath(f), f]), paths.filter(p => !files.includes(p)).map(p => ctx.resolvePath(p)).filter(p => fs.exists(p)));
    const dirs = ordered(paths.filter(p => !files.includes(p) && fs.isDirectory(ctx.resolvePath(p))));
    let first = files.length === 0;
    const listDir = (abs: string, label: string) => {
      if (paths.length > 1 || a.has('R')) line(ctx, (first ? '' : '\n') + label + ':');
      first = false;
      if (!fs.canRead(abs)) { ctx.stderr.write(`ls: cannot open directory '${label}': Permission denied\n`); code = 2; return; }
      let entries = fs.list(abs).filter(n => a.has('a') || a.has('A') || !n.startsWith('.'));
      const st = (n: string) => fs.stat(norm(abs, n));
      if (a.has('t')) entries = [...entries].sort((x, y) => st(y).modified - st(x).modified);
      if (a.has('S')) entries = [...entries].sort((x, y) => st(y).size - st(x).size);
      if (a.has('a')) entries.unshift('.', '..');
      if (a.has('r')) entries.reverse();
      if (lng) {
        const kib = entries.reduce((sum, n) => sum + st(n).blocks / 2, 0);
        line(ctx, `total ${human ? humanSize(kib * 1024) : kib}`);
      }
      show(entries.map(n => [norm(abs, n), n]));
      if (!a.has('R')) return;
      for (const n of entries) {
        if (n === '.' || n === '..') continue;
        const child = norm(abs, n);
        if (fs.isDirectory(child)) listDir(child, trimEnd(label, '/') + '/' + n);
      }
    };
    for (const d of dirs) listDir(ctx.resolvePath(d), d);
    return code;
  }, 'List directory contents');

  reg('cat', ctx => {
    const a = Args.parse(ctx.args, 'nbsAETveu', '', 'number=n,number-nonblank=b,squeeze-blank=s,show-all=A,show-ends=E,show-tabs=T,show-nonprinting=v');
    const ends = a.has('A') || a.has('E') || a.has('e'), tabs = a.has('A') || a.has('T'), nonprint = a.has('A') || a.has('v') || a.has('e');
    const plain = !(a.has('n') || a.has('b') || a.has('s') || ends || tabs || nonprint);
    let code = 0, lineNo = 0;
    let prevBlank = false;
    for (const [, text] of inputs(ctx, a.positional, () => { code = 1; })) {
      if (plain) { ctx.stdout.write(text); continue; }
      let out = '';
      for (let l of splitLines(text)) {
        if (a.has('s') && l.length === 0 && prevBlank) continue;
        prevBlank = l.length === 0;
        if (nonprint) l = showNonprinting(l);
        if (tabs) l = l.replaceAll('\t', '^I');
        if (!(a.has('b') && l.length === 0) && (a.has('n') || a.has('b'))) out += `${String(++lineNo).padStart(6)}\t`;
        out += l + (ends ? '$\n' : '\n');
      }
      if (out.length > 0 && !text.endsWith('\n')) out = out.slice(0, ends ? -2 : -1); // an unterminated last line gets neither a newline nor a $
      ctx.stdout.write(out);
    }
    return code;
  }, 'Print files');

  reg('mkdir', ctx => {
    const a = Args.parse(ctx.args, 'pvZ', 'm', 'parents=p,verbose=v,mode=m');
    if (a.positional.length === 0) throw new UsageException('missing operand');
    let code = 0;
    for (const p of a.positional) {
      const abs = ctx.resolvePath(p);
      try {
        if (!a.has('p') && ctx.fs.exists(abs)) throw new ShellError(`${abs}: File exists`);
        ctx.fs.mkdir(abs, a.has('p'));
        const mode = a.value('m');
        if (mode !== undefined) ctx.fs.chmod(abs, parseMode(mode, 0o755, true));
        if (a.has('v')) line(ctx, `mkdir: created directory '${p}'`);
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`mkdir: cannot create directory ${quoteL(p)}: ${typed(e, p, '')[1]}\n`);
        code = 1;
      }
    }
    return code;
  }, 'Create directories');

  reg('touch', ctx => {
    const a = Args.parse(ctx.args, 'acmhf', 'dtr', 'no-create=c,date=d,reference=r,time=f,no-dereference=h');
    if (a.positional.length === 0) throw new UsageException('missing file operand');
    let when: number | undefined;
    const d = a.value('d'), r = a.value('r'), t = a.value('t');
    if (r !== undefined) {
      const abs = ctx.resolvePath(r);
      if (!ctx.fs.exists(abs)) throw new ShellError(`failed to get attributes of ${quoteAlways(r)}: ${ctx.fs.whyMissing(abs)}`);
      when = ctx.fs.stat(abs).modified;
    }
    if (d !== undefined) {
      try { when = parseDate(d, when ?? ctx.fs.clock()); } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        throw new ShellError(`invalid date format ${quoteL(d)}`);
      }
    }
    if (t !== undefined) when = touchStamp(t, ctx.fs.clock()) ?? fail(`invalid date format ${quoteL(t)}`);
    let code = 0;
    for (const p of a.positional) {
      const path = ctx.resolvePath(p);
      if (a.has('c') && !ctx.fs.exists(path)) continue;
      try {
        // an explicit time needs ownership, not write permission
        if (when === undefined) ctx.fs.touch(path);
        else { if (!ctx.fs.exists(path)) ctx.fs.touch(path); ctx.fs.setModifiedTime(path, when); }
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`touch: cannot touch ${quoteAlways(p)}: ${typed(e, p, path)[1]}\n`);
        code = 1;
      }
    }
    return code;
  }, 'Create empty files / update timestamps');

  reg('rm', ctx => {
    const a = Args.parse(ctx.args, 'rRfvdi', '', 'recursive=r,force=f,verbose=v,dir=d');
    const recursive = a.has('r') || a.has('R');
    if (a.positional.length === 0 && !a.has('f')) throw new UsageException('missing operand');
    let code = 0;
    for (const p of a.positional) {
      const abs = ctx.resolvePath(p);
      if (!ctx.fs.exists(abs)) {
        if (!a.has('f') || ctx.fs.whyMissing(abs) !== 'No such file or directory') { ctx.stderr.write(`rm: cannot remove '${p}': ${ctx.fs.whyMissing(abs)}\n`); code = 1; }
        continue;
      }
      if (ctx.fs.isDirectory(abs) && !recursive && !a.has('d')) { ctx.stderr.write(`rm: cannot remove '${p}': Is a directory\n`); code = 1; continue; }
      if (recursive && /(^|\/)\.\.?\/*$/.test(p)) { ctx.stderr.write(`rm: refusing to remove '.' or '..' directory: skipping '${p}'\n`); code = 1; continue; }
      try {
        ctx.fs.delete(abs, recursive);
        if (a.has('v')) line(ctx, `removed ${ctx.fs.exists(abs) ? '' : ''}'${p}'`);
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        const [path, why] = typed(e, p, abs);
        ctx.stderr.write(`rm: cannot remove '${path}': ${why}\n`);
        code = 1;
      }
    }
    return code;
  }, 'Remove files or directories');

  reg('rmdir', ctx => {
    const a = Args.parse(ctx.args, 'pv', '', 'parents=p,verbose=v,ignore-fail-on-non-empty=i');
    if (a.positional.length === 0) throw new UsageException('missing operand');
    let code = 0;
    for (const arg of a.positional) {
      // -p: a/b/c, then a/b, then a
      for (let p = trimEnd(arg, '/'); p.length > 0; p = a.has('p') && p.lastIndexOf('/') > 0 ? trimEnd(p.slice(0, p.lastIndexOf('/')), '/') : '') {
        const abs = ctx.resolvePath(p);
        let why: string | null = !ctx.fs.isDirectory(abs) ? (ctx.fs.exists(abs) ? 'Not a directory' : ctx.fs.whyMissing(abs)) : null;
        if (why === null) {
          try { ctx.fs.delete(abs); if (a.has('v')) line(ctx, `rmdir: removing directory, ${quoteAlways(p)}`); } catch (e) {
            if (!(e instanceof ShellError)) throw e;
            why = typed(e, p, abs)[1];
          }
        }
        if (why !== null) { ctx.stderr.write(`rmdir: failed to remove ${quoteAlways(p)}: ${why}\n`); code = 1; break; }
      }
    }
    return code;
  }, 'Remove empty directories');

  const copyOrMove: CommandHandler = ctx => {
    const move = ctx.name === 'mv';
    const a = Args.parse(ctx.args, move ? 'fvniTZ' : 'rRfvanpiTZ', 't', 'recursive=r,force=f,verbose=v,no-clobber=n,archive=a,target-directory=t,no-target-directory=T');
    const fs = ctx.fs;
    const positional = [...a.positional];
    const targetDir = a.value('t');
    if (targetDir !== undefined) positional.push(targetDir);
    if (positional.length < 2)
      throw new UsageException(positional.length === 0 ? 'missing file operand' : `missing destination file operand after ${quoteAlways(positional[0])}`);
    const sources = positional.slice(0, -1);
    const destArg = positional[positional.length - 1];
    const dest = ctx.resolvePath(destArg);
    const destIsDir = fs.isDirectory(dest) && !a.has('T');
    if ((sources.length > 1 || destArg.endsWith('/')) && !destIsDir) {
      if (sources.length === 1 && !fs.exists(ctx.resolvePath(sources[0]))) throw new ShellError(`cannot stat '${sources[0]}': No such file or directory`);
      if (sources.length > 1) throw new ShellError(fs.exists(dest) ? `target '${destArg}' is not a directory` : `target '${destArg}': No such file or directory`);
      throw new ShellError(move ? `cannot move '${sources[0]}' to '${destArg}': Not a directory` : `cannot create regular file '${destArg}': Not a directory`);
    }
    let code = 0;
    for (const s of sources) {
      const src = ctx.resolvePath(s);
      if (!fs.exists(src)) { ctx.stderr.write(`${ctx.name}: cannot stat '${s}': ${fs.whyMissing(src)}\n`); code = 1; continue; }
      if (!move && fs.isDirectory(src) && !(a.has('r') || a.has('R') || a.has('a'))) { ctx.stderr.write(`cp: -r not specified; omitting directory '${s}'\n`); code = 1; continue; }
      const target = destIsDir ? norm(dest, nameOf(src)) : dest;
      if (target === src) { ctx.stderr.write(`${ctx.name}: '${s}' and '${destIsDir ? trimEnd(destArg, '/') + '/' + nameOf(src) : destArg}' are the same file\n`); code = 1; continue; }
      if (target === '/dev/null' && !move) continue; // copying to /dev/null discards the data
      if (a.has('n') && fs.exists(target)) continue;
      const shown = destIsDir ? trimEnd(destArg, '/') + '/' + nameOf(src) : destArg;
      if (fs.isDirectory(target) && !fs.isDirectory(src)) { ctx.stderr.write(`${ctx.name}: cannot overwrite directory '${shown}' with non-directory\n`); code = 1; continue; }
      try {
        if (move) {
          if (fs.isDirectory(src) && fs.isDirectory(target)) {
            if (fs.list(target).length > 0) throw new ShellError(`${target}: Directory not empty`);
            fs.delete(target);
          }
          fs.move(src, target);
        } else if (fs.isDirectory(src) && fs.isDirectory(target)) {
          // copying a directory onto an existing one merges into it
          const merge = (from: string, to: string) => {
            for (const n of fs.list(from)) {
              const f = norm(from, n), t = norm(to, n);
              if (fs.isDirectory(f) && fs.isDirectory(t)) merge(f, t);
              else fs.copy(f, t);
            }
          };
          merge(src, target);
        } else {
          fs.copy(src, target, (abs, dir) => {
            const p = typed(new Error(abs + ': '), s, src)[0];
            ctx.stderr.write(dir ? `cp: cannot access '${p}': Permission denied\n` : `cp: cannot open '${p}' for reading: Permission denied\n`);
            code = 1;
          });
        }
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        if (e.message.endsWith(NO_SPACE)) { ctx.stderr.write(`${ctx.name}: error writing '${shown}': ${NO_SPACE}\n`); code = 1; continue; }
        const why = typed(e, s, src)[1];
        ctx.stderr.write(move ? `mv: cannot move '${s}' to '${shown}': ${why}\n`
          : `cp: cannot create ${fs.isDirectory(src) ? 'directory' : 'regular file'} '${shown}': ${why}\n`);
        code = 1;
        continue;
      }
      if (a.has('v')) line(ctx, `${move ? 'renamed ' : ''}'${s}' -> '${shown}'`);
    }
    return code;
  };
  reg('cp', copyOrMove, 'Copy files and directories');
  reg('mv', copyOrMove, 'Move or rename files');

  reg('find', ctx => new Find(ctx).run(), 'Search for files');

  reg('tree', ctx => {
    const a = Args.parse(ctx.args, 'adfFi', 'L');
    const maxDepth = a.value('L') !== undefined ? Number(a.value('L')) : Infinity;
    const root = a.positional[0] ?? '.';
    const abs = ctx.resolvePath(root);
    if (!ctx.fs.isDirectory(abs)) throw new ShellError(`${root} [error opening dir]`);
    let dirs = 0, files = 0;
    let out = root + '\n';
    const walk = (dir: string, prefix: string, depth: number) => {
      const entries = ctx.fs.list(dir).filter(n => (a.has('a') || !n.startsWith('.')) && (!a.has('d') || ctx.fs.isDirectory(norm(dir, n))));
      entries.forEach((n, i) => {
        const last = i === entries.length - 1;
        const child = norm(dir, n);
        out += prefix + (last ? '└── ' : '├── ') + n + '\n';
        if (ctx.fs.isDirectory(child)) {
          dirs++;
          if (depth < maxDepth && ctx.fs.canRead(child)) walk(child, prefix + (last ? '    ' : '│   '), depth + 1);
        } else files++;
      });
    };
    walk(abs, '', 1);
    out += `\n${dirs} director${dirs === 1 ? 'y' : 'ies'}` + (a.has('d') ? '\n' : `, ${files} file${files === 1 ? '' : 's'}\n`);
    ctx.stdout.write(out);
  }, 'Show a directory tree');

  reg('basename', ctx => {
    const a = Args.parse(ctx.args, 'az', 's', 'multiple=a,suffix=s,zero=z');
    if (a.positional.length === 0) throw new UsageException('missing operand');
    const suffix = a.value('s') ?? (!a.has('a') && a.positional.length === 2 ? a.positional[1] : undefined);
    const names = a.has('a') || a.value('s') !== undefined ? a.positional : a.positional.slice(0, 1);
    for (const n of names) {
      const t = trimEnd(n, '/');
      let b = t.length === 0 ? (n.length ? '/' : '') : t.slice(t.lastIndexOf('/') + 1);
      if (suffix !== undefined && b.endsWith(suffix) && b !== suffix) b = b.slice(0, -suffix.length);
      ctx.stdout.write(b + (a.has('z') ? '\0' : '\n'));
    }
  }, 'Strip directory and suffix from a path');

  reg('dirname', ctx => {
    const a = Args.parse(ctx.args, 'z', '', 'zero=z');
    if (a.positional.length === 0) throw new UsageException('missing operand');
    for (const n of a.positional) {
      const t = trimEnd(n, '/');
      const i = t.lastIndexOf('/');
      ctx.stdout.write((t.length === 0 ? (n.length ? '/' : '.') : i < 0 ? '.' : i === 0 ? '/' : trimEnd(t.slice(0, i), '/') || '/') + (a.has('z') ? '\0' : '\n'));
    }
  }, 'Strip the last component from a path');

  const realpath: CommandHandler = ctx => {
    const a = Args.parse(ctx.args, 'fesmqnz', '', 'canonicalize=f,canonicalize-existing=e,canonicalize-missing=m,quiet=q,no-newline=n');
    if (ctx.name === 'readlink' && !a.has('f') && !a.has('e') && !a.has('m')) return 1; // nothing is a symlink
    let code = 0;
    for (const p of a.positional.length ? a.positional : ['.']) {
      const abs = ctx.resolvePath(p);
      if (!a.has('m') && !ctx.fs.exists(abs) && (a.has('e') || ctx.name === 'realpath' || !ctx.fs.isDirectory(VirtualFileSystem.getParent(abs)))) {
        if (!a.has('q')) ctx.stderr.write(`${ctx.name}: ${quote(p)}: ${ctx.fs.whyMissing(abs)}\n`);
        code = 1;
        continue;
      }
      ctx.stdout.write(abs + (a.has('n') ? '' : '\n'));
    }
    return code;
  };
  reg('realpath', realpath, 'Print the absolute path');
  reg('readlink', realpath, 'Print resolved path (-f)');

  reg('stat', ctx => {
    const a = Args.parse(ctx.args, 'Lt', 'c', 'format=c,dereference=L,terse=t,printf=c');
    let code = 0;
    for (const p of a.positional) {
      const abs = ctx.resolvePath(p);
      if (!ctx.fs.exists(abs)) { ctx.stderr.write(`stat: cannot statx '${p}': ${ctx.fs.whyMissing(abs)}\n`); code = 1; continue; }
      const st = ctx.fs.stat(abs);
      const type = st.isDirectory ? 'directory' : st.size === 0 ? 'regular empty file' : 'regular file';
      const mode = st.mode.toString(8).padStart(3, '0');
      const rwx = VirtualFileSystem.formatMode(st.isDirectory, st.mode);
      const uid = st.owner === 'root' ? '0' : '1000';
      const mtime = strftime(st.modified, '%Y-%m-%d %H:%M:%S.') + String(st.modified % 1000).padStart(3, '0') + '000000 +0000';
      const fmt = a.value('c');
      if (fmt !== undefined) {
        let out = '';
        for (let i = 0; i < fmt.length; i++) {
          if (fmt[i] !== '%' || i + 1 >= fmt.length) { out += fmt[i]; continue; }
          const c = fmt[++i];
          let h = 0;
          for (const ch of abs) h = (Math.imul(h, 31) + ch.charCodeAt(0)) >>> 0;
          const v: Record<string, string> = {
            n: p, s: String(st.size), F: type, a: mode, A: rwx, U: st.owner, G: st.owner, u: uid, g: uid, y: mtime,
            Y: String(Math.floor(st.modified / 1000)), b: String(st.blocks), B: '512', i: String(h % 1_000_000), h: String(st.links), '%': '%',
          };
          out += v[c] ?? '%' + c;
        }
        line(ctx, out);
        continue;
      }
      ctx.stdout.write(`  File: ${p}\n  Size: ${String(st.size).padEnd(10)}\tBlocks: ${String(st.blocks).padEnd(10)} IO Block: 4096   ${type}\n` +
        `Access: (${mode.padStart(4, '0')}/${rwx})  Uid: (${uid.padStart(5)}/${st.owner.padStart(8)})   Gid: (${uid.padStart(5)}/${st.owner.padStart(8)})\n` +
        `Modify: ${mtime}\nChange: ${mtime}\n`);
    }
    return code;
  }, 'Show file status');

  reg('du', ctx => {
    const a = Args.parse(ctx.args, 'shacSkb', 'd', 'summarize=s,human-readable=h,all=a,total=c,max-depth=d');
    const maxDepth = a.has('s') ? 0 : a.value('d') !== undefined ? Number(a.value('d')) : Infinity;
    const fmt = (kb: number) => (!a.has('h') ? String(kb)
      : kb < 10 ? `${kb.toFixed(1)}K` : kb < 1024 ? `${kb}K` : kb < 1 << 20 ? `${fmt1(kb / 1024)}M` : `${fmt1(kb / 1048576)}G`);
    let total = 0, code = 0;
    const walk = (abs: string, display: string, depth: number): number => {
      if (!ctx.fs.isDirectory(abs)) {
        const kb = Math.ceil(ctx.fs.stat(abs).size / 4096) * 4;
        if ((a.has('a') && depth <= maxDepth) || depth === 0) line(ctx, `${fmt(kb)}\t${display}`);
        return kb;
      }
      let sum = 4;
      if (ctx.fs.canRead(abs)) for (const n of ctx.fs.list(abs)) sum += walk(norm(abs, n), trimEnd(display, '/') + '/' + n, depth + 1);
      else { ctx.stderr.write(`du: cannot read directory '${display}': Permission denied\n`); code = 1; }
      if (depth <= maxDepth) line(ctx, `${fmt(sum)}\t${display}`);
      return sum;
    };
    for (const p of a.positional.length ? a.positional : ['.']) {
      const abs = ctx.resolvePath(p);
      if (!ctx.fs.exists(abs)) { ctx.stderr.write(`du: cannot access '${p}': No such file or directory\n`); code = 1; continue; }
      total += walk(abs, p, 0);
    }
    if (a.has('c')) line(ctx, `${fmt(total)}\ttotal`);
    return code;
  }, 'Estimate disk usage');

  reg('df', ctx => {
    const h = ctx.args.some(x => x.startsWith('-') && x.includes('h'));
    // the disk is the filesystem's capacity and what its files really hold
    const size = ctx.fs.capacity, used = ctx.fs.usedBytes, avail = Math.max(0, size - used);
    const pct = size === 0 ? 0 : Math.ceil((used * 100) / size);
    const H = (b: number) => {
      if (b < 1024) return String(b);
      let v = b, u = 0;
      do { v /= 1024; u++; } while (Math.ceil(v) >= 1024 && u < 4);
      return (v < 10 ? (Math.ceil(v * 10) / 10).toFixed(1) : String(Math.ceil(v))) + 'KMGT'[u - 1]; // df -h rounds up
    };
    ctx.stdout.write(h
      ? `Filesystem      Size  Used Avail Use% Mounted on\n/dev/sda1      ${H(size).padStart(5)} ${H(used).padStart(5)} ${H(avail).padStart(5)} ${String(pct).padStart(3)}% /\ntmpfs           3.9G     0  3.9G   0% /dev/shm\n`
      : `Filesystem     1K-blocks    Used Available Use% Mounted on\n/dev/sda1      ${String(Math.floor(size / 1024)).padStart(10)} ${String(Math.ceil(used / 1024)).padStart(7)} ${String(Math.floor(avail / 1024)).padStart(9)} ${String(pct).padStart(3)}% /\ntmpfs            4007440       0   4007440   0% /dev/shm\n`);
  }, 'Report filesystem disk space');

  reg('chmod', ctx => {
    let recursive = false, verbose = false, changes = false, quiet = false;
    let spec: string | undefined;
    const files: string[] = [];
    for (const arg of ctx.args) {
      if (arg === '-R' || arg === '--recursive') recursive = true;
      else if (arg === '-v' || arg === '--verbose') verbose = true;
      else if (arg === '-c' || arg === '--changes') changes = true;
      else if (arg === '-f' || arg === '--silent' || arg === '--quiet') quiet = true;
      else if (arg.startsWith('--') && arg.length > 2) throw new UsageException(`unrecognized option '${arg}'`);
      else if (arg.length > 1 && arg[0] === '-' && [...arg].find(c => !'-rwxXstugoa+=,01234567'.includes(c)) !== undefined)
        throw new UsageException(`invalid option -- '${[...arg].find(c => !'-rwxXstugoa+=,01234567'.includes(c))}'`);
      else if (spec === undefined) spec = arg; // may look like an option, e.g. -x or -w
      else files.push(arg);
    }
    if (spec === undefined || files.length === 0) throw new UsageException(spec === undefined ? 'missing operand' : `missing operand after ${quoteL(spec)}`);
    let code = 0;
    const apply = (display: string, path: string) => {
      const st = ctx.fs.stat(path);
      const mode = parseMode(spec!, st.mode, st.isDirectory);
      try { ctx.fs.chmod(path, mode); } catch {
        if (!quiet) ctx.stderr.write(`chmod: changing permissions of '${display}': Operation not permitted\n`);
        code = 1;
        return;
      }
      const f = (m: number) => `${m.toString(8).padStart(4, '0')} (${VirtualFileSystem.formatMode(st.isDirectory, m).slice(1)})`;
      if (mode !== st.mode && (verbose || changes)) line(ctx, `mode of '${display}' changed from ${f(st.mode)} to ${f(mode)}`);
      else if (mode === st.mode && verbose) line(ctx, `mode of '${display}' retained as ${f(mode)}`);
      if (recursive && st.isDirectory && ctx.fs.canRead(path)) for (const n of ctx.fs.list(path)) apply(trimEnd(display, '/') + '/' + n, norm(path, n));
    };
    for (const f of files) {
      const path = ctx.resolvePath(f);
      if (!ctx.fs.exists(path)) { ctx.stderr.write(`chmod: cannot access '${f}': ${ctx.fs.whyMissing(path)}\n`); code = 1; continue; }
      apply(f, path);
    }
    return code;
  }, 'Change file mode bits');

  const chown: CommandHandler = ctx => {
    const operands = ctx.args.filter(x => !x.startsWith('-'));
    const files = operands.slice(1); // first operand is the owner
    if (files.length === 0) throw new UsageException('missing operand');
    const owner = operands[0].split(':')[0];
    let code = 0;
    for (const f of files) {
      const path = ctx.resolvePath(f);
      if (!ctx.fs.exists(path)) { ctx.stderr.write(`${ctx.name}: cannot access '${f}': ${ctx.fs.whyMissing(path)}\n`); code = 1; continue; }
      try { ctx.fs.setOwner(path, owner.length > 0 && ctx.name === 'chown' ? owner : ctx.fs.stat(path).owner, ctx.args.includes('-R')); } catch {
        ctx.stderr.write(`${ctx.name}: changing ${ctx.name === 'chown' ? 'ownership' : 'group'} of '${f}': Operation not permitted\n`);
        code = 1;
      }
    }
    return code;
  };
  reg('chown', chown, 'Change file owner');
  reg('chgrp', chown, 'Change file group');

  const pager: CommandHandler = ctx => {
    let code = 0;
    for (const [, text] of inputs(ctx, ctx.args.filter(x => !x.startsWith('-') && !x.startsWith('+')), () => { code = 1; })) ctx.stdout.write(text);
    return code;
  };
  reg('less', pager, 'View files (non-interactive: prints them)');
  reg('more', pager, 'View files (non-interactive: prints them)');

  reg('file', ctx => {
    let code = 0;
    for (const p of ctx.args.filter(x => !x.startsWith('-'))) {
      const abs = ctx.resolvePath(p);
      let desc: string;
      if (!ctx.fs.exists(abs)) { desc = `cannot open \`${p}' (No such file or directory)`; code = 1; }
      else if (ctx.fs.isDirectory(abs)) desc = 'directory';
      else desc = describeFile(ctx.fs.readBytes(abs));
      line(ctx, `${p}: ${desc}`);
    }
    return code;
  }, 'Determine file type');

  reg('mktemp', ctx => {
    const a = Args.parse(ctx.args, 'dqut', 'p', 'directory=d,quiet=q,tmpdir=p,dry-run=u');
    const template = a.positional[0] ?? 'tmp.XXXXXXXXXX';
    const chars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
    const m = /X{3,}$/.exec(template);
    if (!m) throw new ShellError(`too few X's in template '${template}'`);
    const name = template.slice(0, m.index) + Array.from({ length: m[0].length }, () => chars[ctx.shell.nextRandom(chars.length)]).join('');
    const path = template.includes('/') && a.value('p') === undefined && !a.has('t') ? ctx.resolvePath(name) : norm(a.value('p') ?? '/tmp', name);
    if (!a.has('u')) { if (a.has('d')) { ctx.fs.mkdir(path); ctx.fs.chmod(path, 0o700); } else { ctx.fs.writeText(path, ''); ctx.fs.chmod(path, 0o600); } }
    line(ctx, path);
  }, 'Create a temporary file or directory');
}

/** `file`'s description of some bytes. */
function describeFile(data: Uint8Array): string {
  if (data.length === 0) return 'empty';
  const startsWith = (...b: number[]) => b.every((x, i) => data[i] === x);
  if (startsWith(0x1f, 0x8b)) return 'gzip compressed data';
  if (startsWith(0x50, 0x4b, 0x03, 0x04)) return 'Zip archive data, at least v2.0 to extract';
  if (startsWith(0x25, 0x50, 0x44, 0x46)) return 'PDF document';
  if (startsWith(0x89, 0x50, 0x4e, 0x47)) return 'PNG image data';
  if (startsWith(0xff, 0xd8, 0xff)) return 'JPEG image data';
  if (startsWith(0x47, 0x49, 0x46, 0x38)) return 'GIF image data';
  if (data.length > 262 && String.fromCharCode(...data.subarray(257, 262)) === 'ustar') return 'POSIX tar archive';
  if (startsWith(0x7f, 0x45, 0x4c, 0x46)) return 'ELF 64-bit LSB pie executable, x86-64, version 1 (SYSV), dynamically linked, stripped';
  let t: string;
  try { t = new TextDecoder('utf-8', { fatal: true }).decode(data); } catch { return 'data'; }
  if (t.startsWith('#!')) return (t.includes('python') ? 'Python script' : 'Bourne-Again shell script') + ', ASCII text executable';
  const s = t.trimStart();
  if (s[0] === '{' || s[0] === '[') { try { JSON.parse(t); return 'JSON text data'; } catch { /* not JSON */ } }
  if (t.includes('\0')) return 'data';
  // eslint-disable-next-line no-control-regex
  return /[^\x00-\x7f]/.test(t) ? 'Unicode text, UTF-8 text' : 'ASCII text';
}
