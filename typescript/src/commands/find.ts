// find: walks the tree and evaluates its expression; registered by files.ts. See docs/architecture.md.
import { specOf, specMessage, ShellError, line, type CommandContext } from '../command.js';
import { VirtualFileSystem, type FileStat } from '../vfs.js';
import { globRegex } from '../glob.js';
import { strftime } from '../format.js';
import { longEntries, nameOf, norm, trimEnd } from './files.js';

/** `follow`: whether tests see what a symbolic link leads to (-L, or -H for a starting point) rather than the link. */
interface Entry { abs: string; name: string; display: string; depth: number; start: string; follow: boolean }
type Pred = (e: Entry) => Promise<boolean> | boolean;
class Quit extends Error {}

const TIME_FORMATS: Record<string, string> = {
  Y: '%Y', m: '%m', d: '%d', H: '%H', M: '%M', S: '%S.0000000000', T: '%H:%M:%S.0000000000', F: '%Y-%m-%d', '+': '%Y-%m-%d+%H:%M:%S.0000000000',
  a: '%a', b: '%b', y: '%y', D: '%m/%d/%y', R: '%H:%M',
};

/** find with real expressions: ( ) ! -not -a -o, tests, and actions (-print -print0 -printf -exec -delete). */
export class Find {
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
    const ctx = this.ctx, fs = ctx.fs;
    // -version (or --version) anywhere in the expression: print the version, run nothing
    if (ctx.args.some(x => x === '-version' || x === '--version')) { ctx.stdout.write(specOf('find').version + '\n'); return 0; }
    // -P (the default) never follows symbolic links, -H only those named as starting points, -L all of them
    const options: string[] = [];
    while (options.length < ctx.args.length && ['-P', '-H', '-L'].includes(ctx.args[options.length])) options.push(ctx.args[options.length]);
    const mode = options.length > 0 ? options[options.length - 1][1] : 'P';
    const starts: string[] = [];
    let k = options.length;
    while (k < ctx.args.length && !ctx.args[k].startsWith('-') && ctx.args[k] !== '!' && ctx.args[k] !== '(') starts.push(ctx.args[k++]);
    this.args = ctx.args.slice(k);
    if (starts.length === 0) starts.push('.');
    const expr: Pred = this.args.length === 0 ? () => true : this.parseOr();
    if (this.p < this.args.length) throw new ShellError(specMessage('find', 'unexpected', { token: this.args[this.p] }));
    let code = 0;
    const above: { real: string; display: string }[] = []; // directories being walked, so -L stops at a link back up
    const walk = async (abs: string, display: string, depth: number, start: string, follow: boolean): Promise<void> => {
      ctx.checkTime(); // -L can make the walk far bigger than the tree
      this.prune = false;
      const dir = fs.isDirectory(abs, follow);
      const real = dir && follow ? fs.realPath(abs) ?? abs : abs;
      const loop = dir && follow ? above.find(x => x.real === real) : undefined;
      if (loop) {
        ctx.stderr.write(`find: ${specMessage('find', 'loop', { dir: display, loop: loop.display })}\n`);
        code = 1;
        return;
      }
      if (depth >= this.minDepth && (await expr({ abs, name: nameOf(abs), display, depth, start, follow })) && !this.hasAction) line(ctx, display);
      if (this.prune || depth >= this.maxDepth || !dir) return;
      let names: string[];
      try { names = fs.list(abs); } catch { ctx.stderr.write(`find: ${specMessage('find', 'cannotRead', { file: display, errno: 'Permission denied' })}\n`); code = 1; return; }
      above.push({ real, display });
      for (const name of names) await walk(norm(abs, name), display.endsWith('/') ? display + name : display + '/' + name, depth + 1, start, mode === 'L');
      above.pop();
    };
    try {
      for (const s of starts) {
        const abs = ctx.resolvePath(s);
        const follow = mode !== 'P' || s.endsWith('/');
        if (!fs.exists(abs, follow) && !fs.exists(abs, false)) { ctx.stderr.write(`find: ${specMessage('find', 'cannotRead', { file: s, errno: fs.whyMissing(abs, follow) })}\n`); code = 1; continue; }
        await walk(abs, s, 0, s, follow);
      }
    } catch (e) { if (!(e instanceof Quit)) throw e; }
    for (const { cmd, paths } of this.batches)
      if (paths.length > 0 && (await ctx.invoke(cmd.flatMap(x => (x === '{}' ? paths : [x])))) !== 0) code = 1;
    for (const path of [...this.deletes].sort((a, b) => b.length - a.length)) {
      try { if (ctx.fs.exists(path, false)) ctx.fs.delete(path, true); } catch (e) {
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
      if (++this.p >= this.args.length) throw new ShellError(specMessage('find', 'expectedExpression', { token: this.args[this.p - 1] }));
      const left = l, right = this.parseAnd();
      l = async e => (await left(e)) || (await right(e));
    }
    return l;
  }

  private parseAnd(): Pred {
    let l = this.parseNot();
    while (this.p < this.args.length && !this.is('-o') && !this.is('-or') && !this.is(')')) {
      if ((this.is('-a') || this.is('-and')) && ++this.p >= this.args.length) throw new ShellError(specMessage('find', 'expectedExpression', { token: this.args[this.p - 1] }));
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
      if (!this.is(')')) throw new ShellError(specMessage('find', 'expectedParen', {  }));
      this.p++;
      return inner;
    }
    return this.primary();
  }

  /** The entry's status: what a link leads to when following, unless it leads nowhere (then the link, as find -L does). */
  private stat(e: Entry): FileStat { return this.ctx.fs.stat(e.abs, e.follow && this.ctx.fs.exists(e.abs)); }

  /** find -printf: %p %f %h %P %s %d %y %m %M %u %g %k %t %T@ %Tk ... with optional width, plus \n \t \0 escapes. */
  private printf(fmt: string, e: Entry): string {
    const st = this.stat(e);
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
          case 'y': value = typeChar(st); break;
          case 'Y': value = typeChar(this.stat({ ...e, follow: true })); break;
          case 'l': value = st.linkTarget ?? ''; break;
          case 'm': value = (st.mode & 0o7777).toString(8); break;
          case 'M': value = VirtualFileSystem.formatMode(st); break;
          case 'u': case 'g': value = st.owner; break;
          case 'n': value = String(st.links); break;
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
      if (this.p >= this.args.length) throw new ShellError(specMessage('find', 'missingArgument', { predicate: tok }));
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
      case '-type': case '-xtype': {
        const t = arg();
        if (!['f', 'd', 'l'].includes(t) && !/^[fdl](,[fdl])*$/.test(t)) throw new ShellError(specMessage('find', 'unknownType', { predicate: tok, value: t }));
        const types = t.split(',');
        // -xtype: the other view of a link (what it leads to under -P, the link itself under -L)
        return e => types.includes(typeChar(this.stat(tok === '-type' ? e : { ...e, follow: !e.follow })));
      }
      case '-maxdepth': this.maxDepth = Number(arg()); return () => true;
      case '-mindepth': this.minDepth = Number(arg()); return () => true;
      case '-mmin': case '-mtime': {
        const spec = arg();
        const num = spec.replace(/^[+-]/, '');
        if (!/^(\d+\.?\d*|\.\d+)$/.test(num)) throw new ShellError(specMessage('find', 'invalidArgument', { value: spec, predicate: tok }));
        const unit = tok === '-mmin' ? 1 : 1440, n = Number(num);
        return e => {
          const age = (ctx.shell.now() - this.stat(e).modified) / 60_000 / unit;
          return spec[0] === '-' ? age < n : spec[0] === '+' ? age > n : Math.floor(age) === n;
        };
      }
      case '-newer': {
        const reference = arg(), refAbs = ctx.resolvePath(reference);
        if (!fs.exists(refAbs)) throw new ShellError(specMessage('find', 'cannotRead', { file: reference, errno: fs.whyMissing(refAbs) }));
        const refTime = fs.stat(refAbs).modified;
        return e => this.stat(e).modified > refTime; }
      case '-size': {
        const spec = arg();
        const m = /^([+-]?)(\d+)([cwbkMG]?)$/.exec(spec);
        const bad = /^[+-]?\d+(\D)/.exec(spec);
        if (bad && !m) throw new ShellError(specMessage('find', 'invalidSizeType', { unit: bad[1] }));
        if (!m) throw new ShellError(specMessage('find', 'invalidArgument', { value: spec, predicate: '-size' }));
        const unit = ({ c: 1, w: 2, k: 1024, M: 1 << 20, G: 1 << 30 } as Record<string, number>)[m[3]] ?? 512;
        const n = Number(m[2]);
        return e => {
          const st = this.stat(e);
          if (st.isDirectory) return false;
          const size = Math.ceil(st.size / unit);
          return m[1] === '+' ? size > n : m[1] === '-' ? size < n : size === n;
        };
      }
      case '-links': {
        const spec = arg();
        if (!/^[+-]?\d+$/.test(spec)) throw new ShellError(specMessage('find', 'invalidArgument', { value: spec, predicate: '-links' }));
        const n = Number(spec.replace(/^[+-]/, ''));
        return e => { const l = this.stat(e).links; return spec[0] === '+' ? l > n : spec[0] === '-' ? l < n : l === n; };
      }
      case '-empty': return e => { const st = this.stat(e); return st.isDirectory ? fs.list(e.abs).length === 0 : !st.isSymlink && st.size === 0; };
      case '-readable': return e => fs.canRead(e.abs);
      case '-writable': return e => fs.canWrite(e.abs);
      case '-executable': return e => fs.canExecute(e.abs);
      case '-user': { const u = arg(); return e => this.stat(e).owner === u; }
      case '-perm': {
        const spec = arg();
        const mode = parseInt(spec.replace(/^[-/]/, ''), 8);
        return e => {
          const m = this.stat(e).mode & 0o7777;
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
        if (this.p >= this.args.length) throw new ShellError(specMessage('find', 'missingArgument', { predicate: tok }));
        if (cmd.length === 0) throw new ShellError(specMessage('find', 'invalidArgument', { value: this.args[this.p], predicate: tok }));
        if (this.args[this.p++] === '+') {
          const batch = { cmd, paths: [] as string[] };
          this.batches.push(batch);
          return e => { batch.paths.push(e.display); return true; };
        }
        return async e => (await ctx.invoke(cmd.map(x => x.replaceAll('{}', e.display)))) === 0;
      }
    }
    throw new ShellError(specMessage('find', 'unknownPredicate', { predicate: tok }));
  }
}

const typeChar = (st: FileStat) => (st.isSymlink ? 'l' : st.isDirectory ? 'd' : 'f');
