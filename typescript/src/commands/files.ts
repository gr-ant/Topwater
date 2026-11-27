// File commands: ls cat mkdir touch rm rmdir cp mv find tree stat du df chmod chown file mktemp realpath ...
import { Args, specOf, specMessage, ShellError, UsageException, inputs, splitLines, quote, quoteAlways, quoteL, reason, line, type CommandContext, type CommandHandler } from '../command.js';
import { VirtualFileSystem, NO_SPACE, type FileStat } from '../vfs.js';
import { ERRNO } from '../spec.generated.js';
import { strftime } from '../format.js';
import { parseDate } from './system.js';
import type { Shell } from '../shell.js';
import { Find } from './find.js';

export const norm = VirtualFileSystem.normalize;
export const nameOf = VirtualFileSystem.getName;
export const trimEnd = (s: string, c: string) => { let e = s.length; while (e > 0 && s[e - 1] === c) e--; return s.slice(0, e); };

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
// cp -r d d/x copies d into d/x, entry by entry, until it meets a directory this copy created; there it stops.
// shortcut: entries go in name order, where GNU goes in the directory's own order.
function copyIntoItself(fs: CommandContext['fs'], from: string, to: string, created: Set<string>): boolean {
  if (!fs.exists(to)) { fs.mkdir(to); created.add(to); }
  for (const name of fs.list(from)) {
    const child = norm(from, name);
    if (created.has(child)) return false;
    if (!fs.isDirectory(child)) fs.copy(child, norm(to, name));
    else if (!copyIntoItself(fs, child, norm(to, name), created)) return false;
  }
  return true;
}
/** cp -p: the copy at `to` takes the mode and modification time of what it was copied from, entry by entry. */
function keepModeAndTimes(fs: CommandContext['fs'], from: string, to: string) {
  if (!fs.exists(from) || !fs.exists(to) || fs.isSymlink(to)) return;
  const st = fs.stat(from);
  if (fs.isDirectory(to)) for (const n of fs.list(to)) keepModeAndTimes(fs, norm(from, n), norm(to, n));
  try { fs.chmod(to, st.mode); fs.setModifiedTime(to, st.modified); } catch (e) { if (!(e instanceof ShellError)) throw e; }
}
const fail = (message: string): never => { throw new ShellError(message); };
const fmt1 = (v: number) => String(Math.round(v * 10) / 10);

/** ls -l's date: "Nov 25 16:52" within the last six months, else "Jan  2  2020" (also for future times);
 * or in a --time-style: full-iso, long-iso, iso, or +FORMAT (FORMAT for old times, NEWLINE FORMAT for recent ones). */
function lsDate(t: number, now: number, style?: string): string {
  const recent = t <= now && (now - t) / 1000 < 15778476;
  if (style === 'full-iso') return strftime(t, '%Y-%m-%d %H:%M:%S.%N %z');
  if (style === 'long-iso') return strftime(t, '%Y-%m-%d %H:%M');
  if (style === 'iso') return strftime(t, recent ? '%m-%d %H:%M' : '%Y-%m-%d ');
  if (style?.startsWith('+')) {
    const nl = style.indexOf('\n');
    return strftime(t, recent && nl >= 0 ? style.slice(nl + 1) : nl >= 0 ? style.slice(1, nl) : style.slice(1));
  }
  return strftime(t, recent ? '%b %e %H:%M' : '%b %e  %Y');
}

const TIME_STYLES = ['full-iso', 'long-iso', 'iso', 'locale', 'posix-full-iso', 'posix-long-iso', 'posix-iso', 'posix-locale'];

/** ls --time-style: a style name, a unique abbreviation of one, or +FORMAT; posix- styles act as the plain ones,
 * since the locale is not POSIX. */
function timeStyle(arg: string): string {
  if (arg.startsWith('+')) return arg;
  const hits = TIME_STYLES.includes(arg) ? [arg] : TIME_STYLES.filter(s => s.startsWith(arg));
  if (hits.length !== 1)
    throw new ShellError(specMessage('ls', 'badTimeStyle', { problem: hits.length === 0 ? 'invalid' : 'ambiguous', value: arg }), 2);
  return hits[0].startsWith('posix-') ? hits[0].slice(6) : hits[0];
}

/** ls -s: the space a file takes, in KiB (or -h units). */
const blockSize = (st: FileStat, human: boolean) => (human ? humanSize(st.blocks * 512) : String(st.blocks / 2));

/** ls -l rows, each column as wide as its widest entry (owners left-aligned, numbers right-aligned); `widthFrom`
 * are more paths that count toward the widths (ls sizes the columns for its file operands over all operands). */
export function longEntries(ctx: CommandContext, entries: [abs: string, name: string][], human: boolean, widthFrom: string[] = [],
  style?: string, blocks = false, follow = false, classify?: (st: FileStat) => string): string[] {
  const now = ctx.fs.clock();
  const size = (st: FileStat) => (human ? humanSize(st.size) : String(st.size));
  // broken: -L meets a link leading nowhere, shown as a row of question marks
  const rows = entries.map(([abs, name]) => {
    const broken = follow && !ctx.fs.exists(abs);
    return { st: ctx.fs.stat(abs, follow && !broken), name, abs, broken };
  });
  if (rows.length === 0) return [];
  const all = [...rows.filter(r => !r.broken).map(r => r.st), ...widthFrom.map(p => ctx.fs.stat(p, follow))];
  const width = (f: (st: FileStat) => string) => all.reduce((m, st) => Math.max(m, f(st).length), 1);
  const lw = width(st => String(st.links)), ow = width(st => st.owner), sw = width(size), bw = width(st => blockSize(st, human));
  const dw = width(st => lsDate(st.modified, now, style));
  return rows.map(({ st, name, abs, broken }) => {
    if (broken) return (blocks ? '?'.padStart(bw) + ' ' : '') + `l????????? ${'?'.padStart(lw)} ${'?'.padEnd(ow)} ${'?'.padEnd(ow)} ${'?'.padStart(sw)} ${'?'.padStart(dw)} ${name}`;
    // a link shows where it points; -F marks that target's type
    const shown = st.linkTarget !== null ? name + ' -> ' + st.linkTarget + (classify && ctx.fs.exists(abs) ? classify(ctx.fs.stat(abs)) : '') : name + (classify?.(st) ?? '');
    return (blocks ? blockSize(st, human).padStart(bw) + ' ' : '') + `${VirtualFileSystem.formatMode(st)} ${String(st.links).padStart(lw)} ` +
      `${st.owner.padEnd(ow)} ${st.owner.padEnd(ow)} ${size(st).padStart(sw)} ${lsDate(st.modified, now, style)} ${shown}`;
  });
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
/** `umask`: bits a clause without u/g/o/a leaves alone (chmod +w under umask 077 adds only u+w). */
export function parseMode(spec: string, mode: number, isDir: boolean, umask = 0): number {
  if (/^[0-7]{1,4}$/.test(spec)) return parseInt(spec, 8);
  for (const clause of spec.split(',')) {
    const m = /^([ugoa]*)([-+=])([rwxXt]*)$/.exec(clause);
    if (!m) throw new ShellError(specMessage('chmod', 'invalidMode', { mode: spec }));
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
    if (m[1] === '') mask &= ~umask;
    const st = sticky ? 0o1000 : 0;
    mode = m[2] === '+' ? mode | mask | st : m[2] === '-' ? mode & ~(mask | st) : (mode & ~clear) | mask | st;
  }
  return mode;
}

// ---------- registration ----------

export function registerFiles(shell: Shell) {
  const reg = (name: string, handler: CommandHandler) => shell.register(name, handler);

  reg('ls', ctx => {
    // \u0001: --time-style=STYLE, \u0002: --full-time
    const a = Args.parse(ctx.args, specOf('ls'));
    const paths = a.positional.length > 0 ? a.positional : ['.'];
    const lng = a.has('l') || a.has('\u0002'), human = a.has('h'), blocks = a.has('s'), deref = a.has('L');
    const ts = a.value('\u0001');
    const style = a.has('\u0002') ? 'full-iso' : lng && ts !== undefined ? timeStyle(ts) : undefined;
    let code = 0;
    const fs = ctx.fs;
    // -F: what each kind of entry gets; a link itself gets @ (unless -L follows it)
    const classify = (st: FileStat) => (st.isDirectory ? '/' : st.isSymlink ? '@' : st.mode & 0o111 ? '*' : '');
    const show = (entries: [string, string][], follow: boolean, widthFrom?: string[]) => {
      if (lng) { for (const l of longEntries(ctx, entries, human, widthFrom, style, blocks, follow, a.has('F') ? classify : undefined)) line(ctx, l); return; }
      const named = entries.map(([abs, name]): [string, string] => [abs, name + (a.has('F') && fs.exists(abs, follow) ? classify(fs.stat(abs, follow)) : '')]);
      if (!blocks) { for (const e of named) line(ctx, e[1]); return; }
      const sizes = named.map(e => (follow && !fs.exists(e[0]) ? '?' : blockSize(fs.stat(e[0], follow), human)));
      const w = Math.max(0, ...sizes.map(x => x.length));
      named.forEach((e, k) => line(ctx, sizes[k].padStart(w) + ' ' + e[1]));
    };
    // a link named on the command line is followed to a directory, unless -l, -d or -F shows the link itself
    const followOperand = (p: string) => deref || a.has('H') || p.endsWith('/') || !(lng || a.has('d') || a.has('F'));
    const exists = (p: string) => fs.exists(ctx.resolvePath(p), deref || p.endsWith('/'));
    const statOf = (p: string) => fs.stat(ctx.resolvePath(p), deref || a.has('H') || p.endsWith('/'));
    // operands are listed like directory entries: byte order (the C locale), or by -t / -S, reversed by -r
    const ordered = (ps: string[]) => {
      let list = [...ps].sort();
      if (a.has('t')) list = list.sort((x, y) => statOf(y).modified - statOf(x).modified);
      if (a.has('S')) list = list.sort((x, y) => statOf(y).size - statOf(x).size);
      return a.has('r') ? list.reverse() : list;
    };
    const files = ordered(paths.filter(p => exists(p) && (a.has('d') || !fs.isDirectory(ctx.resolvePath(p), followOperand(p)))));
    for (const p of paths.filter(p => !exists(p))) { ctx.stderr.write(`ls: ${specMessage('ls', 'cannotAccess', { file: p, errno: fs.whyMissing(ctx.resolvePath(p), deref || p.endsWith('/')) })}\n`); code = 2; }
    const groups = new Map<boolean, string[]>();
    for (const f of files) { const k = deref || a.has('H') || f.endsWith('/'); groups.set(k, [...(groups.get(k) ?? []), f]); }
    for (const [follow, group] of groups)
      show(group.map(f => [ctx.resolvePath(f), f]), follow, paths.filter(p => !files.includes(p) && exists(p)).map(p => ctx.resolvePath(p)));
    const dirs = ordered(paths.filter(p => !files.includes(p) && fs.isDirectory(ctx.resolvePath(p))));
    let first = files.length === 0;
    const listed = new Set<string>(); // real paths, so -RL stops at a link back up the tree
    const listDir = (abs: string, label: string) => {
      abs = fs.realPath(abs) ?? abs; // where it really is, so "." and ".." are the directories themselves
      if (a.has('R') && deref) {
        if (listed.has(abs)) { ctx.stderr.write(`ls: ${specMessage('ls', 'alreadyListed', { file: label })}\n`); code = 2; return; }
        listed.add(abs);
      }
      if (paths.length > 1 || a.has('R')) line(ctx, (first ? '' : '\n') + label + ':');
      first = false;
      if (!fs.canRead(abs)) { ctx.stderr.write(`ls: ${specMessage('ls', 'cannotOpenDirectory', { file: label, errno: 'Permission denied' })}\n`); code = 2; return; }
      let entries = fs.list(abs).filter(n => a.has('a') || a.has('A') || !n.startsWith('.'));
      if (deref && (lng || blocks)) { // a link leading nowhere: an error, then a row of question marks
        for (const n of entries.filter(n => !fs.exists(norm(abs, n)))) {
          ctx.stderr.write(`ls: ${specMessage('ls', 'cannotAccess', { file: (label === '.' ? '' : trimEnd(label, '/') + '/') + n, errno: fs.whyMissing(norm(abs, n)) })}\n`);
          code = 1;
        }
      }
      const st = (n: string) => fs.stat(norm(abs, n), deref && fs.exists(norm(abs, n)));
      if (a.has('t')) entries = [...entries].sort((x, y) => st(y).modified - st(x).modified);
      if (a.has('S')) entries = [...entries].sort((x, y) => st(y).size - st(x).size);
      if (a.has('a')) entries.unshift('.', '..');
      if (a.has('r')) entries.reverse();
      if (lng || blocks) {
        const kib = entries.reduce((sum, n) => sum + st(n).blocks / 2, 0);
        line(ctx, `total ${human ? humanSize(kib * 1024) : kib}`);
      }
      show(entries.map(n => [norm(abs, n), n]), deref);
      if (!a.has('R')) return;
      for (const n of entries) {
        if (n === '.' || n === '..') continue;
        const child = norm(abs, n);
        if (fs.isDirectory(child, deref)) listDir(child, trimEnd(label, '/') + '/' + n);
      }
    };
    for (const d of dirs) listDir(ctx.resolvePath(d), d);
    return code;
  });

  reg('cat', ctx => {
    const a = Args.parse(ctx.args, specOf('cat'));
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
  });

  reg('mkdir', ctx => {
    const a = Args.parse(ctx.args, specOf('mkdir'));
    if (a.positional.length === 0) throw new UsageException(specMessage('mkdir', 'missingOperand', {}));
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
        ctx.stderr.write('mkdir: ' + specMessage('mkdir', 'cannotCreateDirectory', { file: p, reason: typed(e, p, '')[1] }) + '\n');
        code = 1;
      }
    }
    return code;
  });

  reg('touch', ctx => {
    const a = Args.parse(ctx.args, specOf('touch'));
    if (a.positional.length === 0) throw new UsageException(specMessage('touch', 'missingFileOperand', {}));
    let when: number | undefined;
    const d = a.value('d'), r = a.value('r'), t = a.value('t');
    if (r !== undefined) {
      const abs = ctx.resolvePath(r);
      if (!ctx.fs.exists(abs)) throw new ShellError(specMessage('touch', 'failedToGetAttributes', { file: r, reason: ctx.fs.whyMissing(abs) }));
      when = ctx.fs.stat(abs).modified;
    }
    if (d !== undefined) {
      try { when = parseDate(d, when ?? ctx.fs.clock()); } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        throw new ShellError(specMessage('touch', 'invalidDateFormat', { value: d }));
      }
      // shortcut: refused beyond what a JavaScript Date holds (about year 275760; GNU goes further)
      if (!(Math.abs(when) <= 8.64e15)) throw new ShellError(specMessage('touch', 'invalidDateFormat', { value: d }));
    }
    if (t !== undefined) when = touchStamp(t, ctx.fs.clock()) ?? fail(specMessage('touch', 'invalidDateFormat', { value: t }));
    let code = 0;
    for (const p of a.positional) {
      const path = ctx.resolvePath(p);
      if (a.has('c') && !ctx.fs.exists(path)) continue;
      try {
        // an explicit time needs ownership, not write permission
        if (a.has('h') && ctx.fs.isSymlink(path)) ctx.fs.setModifiedTime(path, when ?? ctx.fs.clock(), false);
        else if (when === undefined) ctx.fs.touch(path);
        else {
          if (!ctx.fs.exists(path)) ctx.fs.touch(path);
          // touch opens the file for writing first, so an unwritable file is "Permission denied" even with a time given
          else if (ctx.fs.isFile(path) && !ctx.fs.canWrite(path) && ctx.fs.stat(path).owner !== ctx.fs.runAs) throw new ShellError(`${path}: ${ERRNO.EACCES}`);
          ctx.fs.setModifiedTime(path, when);
        }
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write('touch: ' + specMessage('touch', 'cannotTouch', { file: p, reason: typed(e, p, path)[1] }) + '\n');
        code = 1;
      }
    }
    return code;
  });

  reg('rm', ctx => {
    const a = Args.parse(ctx.args, specOf('rm'));
    const recursive = a.has('r') || a.has('R');
    if (a.positional.length === 0 && !a.has('f')) throw new UsageException(specMessage('rm', 'missingOperand', {}));
    let code = 0;
    for (const p of a.positional) {
      const abs = ctx.resolvePath(p);
      if (!ctx.fs.exists(abs, false)) {
        if (!a.has('f') || ctx.fs.whyMissing(abs, false) !== 'No such file or directory') { ctx.stderr.write(`rm: ${specMessage('rm', 'cannotRemove', { file: p, reason: ctx.fs.whyMissing(abs, false) })}\n`); code = 1; }
        continue;
      }
      if (ctx.fs.isDirectory(abs, p.endsWith('/')) && !recursive && !a.has('d')) { ctx.stderr.write(`rm: ${specMessage('rm', 'cannotRemove', { file: p, reason: 'Is a directory' })}\n`); code = 1; continue; }
      if (p.endsWith('/') && ctx.fs.isSymlink(abs) && !ctx.fs.isDirectory(abs)) { // "link/" names what it leads to, which is no directory
        if (!a.has('f')) { ctx.stderr.write(`rm: ${specMessage('rm', 'cannotRemove', { file: p, reason: 'Not a directory' })}\n`); code = 1; }
        continue;
      }
      if (recursive && p.endsWith('/') && ctx.fs.isSymlink(abs) && ctx.fs.isDirectory(abs)) {
        // like GNU rm: "link/" is the directory it leads to, which is emptied; the link itself is no directory to remove
        for (const n of ctx.fs.list(abs)) {
          try { ctx.fs.delete(abs + '/' + n, true); } catch (e) {
            if (!(e instanceof ShellError)) throw e;
            const [path, why] = typed(e, trimEnd(p, '/') + '/' + n, abs + '/' + n);
            ctx.stderr.write(`rm: ${specMessage('rm', 'cannotRemove', { file: path, reason: why })}\n`);
          }
        }
        if (!a.has('f')) { ctx.stderr.write(`rm: ${specMessage('rm', 'cannotRemove', { file: p, reason: 'Not a directory' })}\n`); code = 1; }
        continue;
      }
      if (recursive && /(^|\/)\.\.?\/*$/.test(p)) { ctx.stderr.write(`rm: ${specMessage('rm', 'refusingDots', { file: p })}\n`); code = 1; continue; }
      try {
        ctx.fs.delete(abs, recursive);
        if (a.has('v')) line(ctx, `removed ${ctx.fs.exists(abs) ? '' : ''}'${p}'`);
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        const [path, why] = typed(e, p, abs);
        ctx.stderr.write('rm: ' + specMessage('rm', 'cannotRemove', { file: path, reason: why }) + '\n');
        code = 1;
      }
    }
    return code;
  });

  reg('rmdir', ctx => {
    const a = Args.parse(ctx.args, specOf('rmdir'));
    if (a.positional.length === 0) throw new UsageException(specMessage('rmdir', 'missingOperand', {}));
    let code = 0;
    for (const arg of a.positional) {
      if (arg.endsWith('/') && ctx.fs.isSymlink(ctx.resolvePath(arg))) { ctx.stderr.write(`rmdir: ${specMessage('rmdir', 'failedToRemove', { file: arg, reason: 'Symbolic link not followed' })}\n`); code = 1; continue; }
      // -p: a/b/c, then a/b, then a
      for (let p = trimEnd(arg, '/'); p.length > 0; p = a.has('p') && p.lastIndexOf('/') > 0 ? trimEnd(p.slice(0, p.lastIndexOf('/')), '/') : '') {
        const abs = ctx.resolvePath(p);
        let why: string | null = !ctx.fs.isDirectory(abs, false) ? (ctx.fs.exists(abs, false) ? 'Not a directory' : ctx.fs.whyMissing(abs)) : null;
        if (why === null) {
          try { ctx.fs.delete(abs); if (a.has('v')) line(ctx, `rmdir: removing directory, ${quoteAlways(p)}`); } catch (e) {
            if (!(e instanceof ShellError)) throw e;
            why = typed(e, p, abs)[1];
          }
        }
        if (why !== null) { ctx.stderr.write('rmdir: ' + specMessage('rmdir', 'failedToRemove', { file: p, reason: why }) + '\n'); code = 1; break; }
      }
    }
    return code;
  });

  // FILE~ (simple), FILE.~N~ one past the highest (numbered), or numbered only when FILE already has numbered backups (existing)
  // shortcut: files inside a directory that cp -r merges into are overwritten without a backup
  const backupName = (fs: VirtualFileSystem, target: string, mode: string, suffix: string) => {
    const name = VirtualFileSystem.getName(target), prefix = name + '.~';
    const numbers = fs.list(VirtualFileSystem.getParent(target)).map(n => {
      const k = n.startsWith(prefix) && n.endsWith('~') && /^\d+$/.test(n.slice(prefix.length, -1)) ? parseInt(n.slice(prefix.length, -1), 10) : 0;
      return k > 0 ? k : 0;
    });
    const highest = Math.max(0, ...numbers);
    return mode === 'simple' || (mode === 'existing' && highest === 0) ? target + suffix : `${target}.~${highest + 1}~`;
  };

  const copyOrMove: CommandHandler = ctx => {
    const move = ctx.name === 'mv';
    // cp --backup[=CONTROL] takes an optional value, so it is read here, before the shared parser
    let control: string | undefined;
    const args = ctx.args.filter(x => {
      if (move || !(x === '--backup' || x.startsWith('--backup='))) return true;
      control = x.slice(9);
      return false;
    });
    const a = Args.parse(args, specOf(ctx.name));
    if (a.has('b')) control ??= '';
    if (control === '') control = ctx.shell.environment.get('VERSION_CONTROL') ?? 'existing';
    const backup = control === undefined || control === 'none' || control === 'off' ? null
      : control === 'simple' || control === 'never' ? 'simple'
      : control === 'existing' || control === 'nil' ? 'existing'
      : control === 'numbered' || control === 't' ? 'numbered'
      : (() => { throw new UsageException(specMessage('cp', 'invalidBackupType', { value: control })); })();
    const suffix = a.value('S') ?? ctx.shell.environment.get('SIMPLE_BACKUP_SUFFIX') ?? '~';
    const fs = ctx.fs;
    // cp follows a symbolic link it is given unless copying recursively (-r, -a) or told not to; -L follows them all
    const followTop = !move && !(a.has('P') || a.has('d') || a.has('a')) && (a.has('L') || a.has('H') || !(a.has('r') || a.has('R')));
    const positional = [...a.positional];
    const targetDir = a.value('t');
    if (targetDir !== undefined) positional.push(targetDir);
    if (positional.length < 2)
      throw new UsageException(positional.length === 0 ? specMessage(ctx.name, 'missingFileOperand') : specMessage(ctx.name, 'missingDestination', { file: positional[0] }));
    const sources = positional.slice(0, -1);
    const destArg = positional[positional.length - 1];
    const dest = ctx.resolvePath(destArg);
    const destIsDir = fs.isDirectory(dest) && !a.has('T');
    if ((sources.length > 1 || destArg.endsWith('/') && !fs.isDirectory(ctx.resolvePath(sources[0]))) && !destIsDir) {
      if (sources.length === 1 && !fs.exists(ctx.resolvePath(sources[0]))) throw new ShellError(specMessage(ctx.name, 'cannotStat', { file: sources[0], errno: 'No such file or directory' }));
      if (sources.length > 1) throw new ShellError(specMessage(ctx.name, 'targetFailed', { file: destArg, errno: fs.exists(dest) ? 'Not a directory' : 'No such file or directory' }));
      throw new ShellError(move ? specMessage('mv', 'cannotMove', { source: sources[0], dest: destArg, errno: 'Not a directory' }) : specMessage('cp', 'cannotCreate', { kind: 'regular file', file: destArg, errno: 'Not a directory' }));
    }
    let code = 0;
    for (const s of sources) {
      const src = ctx.resolvePath(s);
      if (!fs.exists(src, followTop)) { ctx.stderr.write(`${ctx.name}: ${specMessage(ctx.name, 'cannotStat', { file: s, errno: fs.whyMissing(src, followTop) })}\n`); code = 1; continue; }
      if (!move && fs.isDirectory(src, followTop) && !(a.has('r') || a.has('R') || a.has('a'))) { ctx.stderr.write(`cp: ${specMessage('cp', 'omittingDirectory', { file: s })}\n`); code = 1; continue; }
      const target = destIsDir ? norm(dest, nameOf(src)) : dest;
      if (target !== src && !move && followTop && fs.sameFile(src, target)) { // through a link or another hard link; the plain case is a clash below
        ctx.stderr.write(`cp: ${specMessage('cp', 'sameFile', { source: s, dest: destIsDir ? trimEnd(destArg, '/') + '/' + nameOf(src) : destArg })}\n`); code = 1; continue;
      }
      if (!move && fs.isFile(src, followTop) && fs.isSymlink(target) && !fs.exists(target)) {
        ctx.stderr.write(`cp: ${specMessage('cp', 'danglingSymlink', { file: destIsDir ? trimEnd(destArg, '/') + '/' + nameOf(src) : destArg })}\n`);
        code = 1;
        continue;
      }
      if (target === '/dev/null' && !move) continue; // copying to /dev/null discards the data
      const shown = destIsDir ? trimEnd(destArg, '/') + '/' + nameOf(src) : destArg;
      const srcDir = fs.isDirectory(src, followTop);
      // mv onto another name of the same file, or onto the file a link names, loses it; mv refuses as GNU mv does
      const same = target === src || (move && (fs.sameFile(src, target, false) || (fs.isSymlink(src) && !fs.isSymlink(target) && fs.sameFile(src, target))));
      const clashKey = same ? 'sameFile'
        : srcDir && target.startsWith(src + '/')
          ? 'intoItself'
        : !fs.exists(target) ? null
        : srcDir && !fs.isDirectory(target) ? 'overwriteNonDirectory'
        : !srcDir && fs.isDirectory(target) ? 'overwriteDirectory'
        : move && srcDir && fs.list(target).length > 0 ? 'notEmpty'
        : null;
      if (clashKey !== null) {
        ctx.stderr.write(`${ctx.name}: ${specMessage(ctx.name, clashKey, { source: s, dest: shown })}\n`);
        code = 1;
        // cp creates the copy and fills it until it reaches the copy itself
        if (!move && clashKey === 'intoItself') copyIntoItself(fs, src, target, new Set());
        continue;
      }
      if (a.has('n') && fs.exists(target)) continue;
      if (backup !== null && fs.isFile(target, false)) fs.move(target, backupName(fs, target, backup, suffix));
      if (move && srcDir && fs.isDirectory(target)) fs.delete(target); // an empty directory is replaced
      const broken = (abs: string, why: string) => {
        const p = typed(new Error(abs + ': '), s, src)[0];
        ctx.stderr.write(`cp: ${why === 'cyclic' ? specMessage('cp', 'cyclicLink', { file: p }) : specMessage('cp', 'cannotStat', { file: p, errno: why })}\n`);
        code = 1;
      };
      try {
        if (move) {
          fs.move(src, target);
        } else if (fs.isDirectory(src, followTop) && fs.isDirectory(target)) {
          // copying a directory onto an existing one merges into it
          const merge = (from: string, to: string) => {
            for (const n of fs.list(from)) {
              const f = norm(from, n), t = norm(to, n);
              if (fs.isDirectory(f, a.has('L')) && fs.isDirectory(t)) merge(f, t);
              else fs.copy(f, t, undefined, a.has('L'), a.has('L'), broken);
            }
          };
          merge(src, target);
        } else {
          fs.copy(src, target, (abs, dir) => {
            const p = typed(new Error(abs + ': '), s, src)[0];
            ctx.stderr.write(`cp: ${specMessage('cp', dir ? 'cannotAccess' : 'cannotOpen', { file: p, errno: 'Permission denied' })}\n`);
            code = 1;
          }, followTop, a.has('L'), broken, () => ctx.checkTime());
        }
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        if (e.message.includes(' into itself, ') || e.message.includes(' to a subdirectory of itself, ')) {
          ctx.stderr.write(`${ctx.name}: ${specMessage(ctx.name, 'intoItselfPlain', { source: s, dest: shown })}\n`);
          code = 1;
          continue;
        }
        if (e.message.endsWith(NO_SPACE)) { ctx.stderr.write(`${ctx.name}: ${specMessage(ctx.name, 'errorWriting', { file: shown, errno: NO_SPACE })}\n`); code = 1; continue; }
        const why = typed(e, s, src)[1];
        ctx.stderr.write(move ? `mv: ${specMessage('mv', 'cannotMove', { source: s, dest: shown, errno: why })}\n`
          : `cp: ${specMessage('cp', 'cannotCreate', { kind: fs.isDirectory(src) ? 'directory' : 'regular file', file: shown, errno: why })}\n`);
        code = 1;
        continue;
      }
      if (!move && (a.has('p') || a.has('a'))) keepModeAndTimes(fs, src, target);
      if (a.has('v')) line(ctx, `${move ? 'renamed ' : ''}'${s}' -> '${shown}'`);
    }
    return code;
  };
  reg('cp', copyOrMove);
  reg('mv', copyOrMove);

  reg('ln', ctx => {
    const a = Args.parse(ctx.args, specOf('ln'));
    const fs = ctx.fs;
    const sym = a.has('s');
    let ops = a.positional;
    let into = a.value('t');
    const last = ops[ops.length - 1];
    if (into === undefined && ((!a.has('T') && ops.length > 1 && fs.isDirectory(ctx.resolvePath(last), !a.has('n'))) || ops.length > 2)) into = last;
    if (ops.length === 0 || (ops.length === 1 && a.has('T') && into === undefined))
      throw new UsageException(ops.length === 0 ? specMessage('ln', 'missingFileOperand') : specMessage('ln', 'missingDestination', { file: ops[0] }));
    if (into !== undefined && a.value('t') === undefined) ops = ops.slice(0, -1);
    if (into !== undefined && !fs.isDirectory(ctx.resolvePath(into))) {
      const abs = ctx.resolvePath(into);
      throw new ShellError(a.value('t') !== undefined && !fs.exists(abs) ? specMessage('ln', 'failedToAccess', { file: into, errno: fs.whyMissing(abs) })
        : fs.exists(abs) ? specMessage('ln', 'targetNotDirectory', { file: into }) : specMessage('ln', 'targetFailed', { file: into, errno: 'No such file or directory' }));
    }
    // (what the link points to, the link's name): `ln f` links into ".", `ln f d` into d when it is a directory
    const pairs: [string, string][] = into !== undefined || ops.length === 1
      ? ops.map(t => [t, (into === undefined ? '' : trimEnd(into, '/') + '/') + nameOf(t)])
      : [[ops[0], ops[1]]];
    let code = 0;
    for (const [target, name] of pairs) {
      const shownName = into === undefined && ops.length === 1 ? './' + name : name;
      const abs = ctx.resolvePath(name), src = ctx.resolvePath(target);
      const failure = (why: string, arrow = true) =>
        `ln: ${specMessage('ln', 'createFailed', { kind: sym ? 'symbolic' : 'hard', file: shownName, arrow: !arrow ? '' : sym ? (target.length === 0 ? " -> ''" : '') : ' => ' + quoteAlways(target), errno: why })}\n`;
      try {
        if (!sym && !fs.exists(src, a.has('L'))) { ctx.stderr.write(`ln: ${specMessage('ln', 'failedToAccess', { file: target, errno: fs.whyMissing(src, a.has('L')) })}\n`); code = 1; continue; }
        if (!sym && fs.isDirectory(src)) { ctx.stderr.write(`ln: ${specMessage('ln', 'hardLinkDirectory', { file: target })}\n`); code = 1; continue; }
        if (fs.exists(abs, false)) {
          if (!a.has('f')) { ctx.stderr.write(failure('File exists', false)); code = 1; continue; }
          if (fs.isDirectory(abs, false)) { ctx.stderr.write(`ln: ${specMessage('ln', 'overwriteDirectory', { file: name })}\n`); code = 1; continue; }
          if (fs.sameFile(abs, sym ? norm(VirtualFileSystem.getParent(abs), target) : src)) { ctx.stderr.write(`ln: ${specMessage('ln', 'sameFile', { source: target, dest: name })}\n`); code = 1; continue; }
          fs.delete(abs);
        }
        if (!sym) fs.createHardLink(src, abs, a.has('L'));
        else {
          let stored = target;
          if (a.has('r') && target.length > 0) {
            // -r: from the link's real directory to the target's real path
            const to = (fs.realPath(src, false) ?? src).split('/').filter(Boolean);
            const dir = VirtualFileSystem.getParent(abs);
            const from = (fs.realPath(dir) ?? dir).split('/').filter(Boolean);
            let common = 0;
            while (common < to.length && common < from.length && to[common] === from[common]) common++;
            stored = [...Array(from.length - common).fill('..'), ...to.slice(common)].join('/') || '.';
          }
          fs.createSymlink(abs, stored);
        }
        if (a.has('v')) line(ctx, `${quoteAlways(shownName)} ${sym ? '->' : '=>'} ${quoteAlways(target)}`);
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(failure(reason(e), reason(e) !== 'File exists'));
        code = 1;
      }
    }
    return code;
  });

  reg('find', ctx => new Find(ctx).run());

  reg('tree', ctx => {
    const a = Args.parse(ctx.args, specOf('tree'));
    const maxDepth = a.value('L') !== undefined ? Number(a.value('L')) : Infinity;
    const root = a.positional[0] ?? '.';
    const abs = ctx.resolvePath(root);
    if (!ctx.fs.isDirectory(abs)) throw new ShellError(specMessage('tree', 'errorOpeningDir', { dir: root }));
    let dirs = 0, files = 0;
    let out = root + '\n';
    const walk = (dir: string, prefix: string, depth: number) => {
      const entries = ctx.fs.list(dir).filter(n => (a.has('a') || !n.startsWith('.')) && (!a.has('d') || ctx.fs.isDirectory(norm(dir, n), false)));
      entries.forEach((n, i) => {
        const last = i === entries.length - 1;
        const child = norm(dir, n);
        out += prefix + (last ? '└── ' : '├── ') + n + (ctx.fs.isSymlink(child) ? ' -> ' + ctx.fs.readLink(child) : '') + '\n';
        if (ctx.fs.isDirectory(child, false)) {
          dirs++;
          if (depth < maxDepth && ctx.fs.canRead(child)) walk(child, prefix + (last ? '    ' : '│   '), depth + 1);
        } else files++;
      });
    };
    walk(abs, '', 1);
    out += `\n${dirs} director${dirs === 1 ? 'y' : 'ies'}` + (a.has('d') ? '\n' : `, ${files} file${files === 1 ? '' : 's'}\n`);
    ctx.stdout.write(out);
  });

  reg('stat', ctx => {
    const a = Args.parse(ctx.args, specOf('stat'));
    let code = 0;
    for (const p of a.positional) {
      const abs = ctx.resolvePath(p);
      const follow = a.has('L');
      if (!ctx.fs.exists(abs, follow)) { ctx.stderr.write(`stat: ${specMessage('stat', 'cannotStat', { file: p, errno: ctx.fs.whyMissing(abs, follow) })}\n`); code = 1; continue; }
      const st = ctx.fs.stat(abs, follow);
      const type = st.isSymlink ? 'symbolic link' : st.isDirectory ? 'directory' : st.size === 0 ? 'regular empty file' : 'regular file';
      const named = st.linkTarget !== null ? `${quoteAlways(p)} -> ${quoteAlways(st.linkTarget)}` : quoteAlways(p);
      const mode = st.mode.toString(8).padStart(3, '0');
      const rwx = VirtualFileSystem.formatMode(st);
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
            n: p, N: named, s: String(st.size), F: type, a: mode, A: rwx, U: st.owner, G: st.owner, u: uid, g: uid, y: mtime,
            Y: String(Math.floor(st.modified / 1000)), b: String(st.blocks), B: '512', i: String(h % 1_000_000), h: String(st.links), '%': '%',
          };
          out += v[c] ?? '%' + c;
        }
        line(ctx, out);
        continue;
      }
      ctx.stdout.write(`  File: ${p}${st.isSymlink ? ' -> ' + st.linkTarget : ''}\n  Size: ${String(st.size).padEnd(10)}\tBlocks: ${String(st.blocks).padEnd(10)} IO Block: 4096   ${type}\n` +
        `Access: (${mode.padStart(4, '0')}/${rwx})  Uid: (${uid.padStart(5)}/${st.owner.padStart(8)})   Gid: (${uid.padStart(5)}/${st.owner.padStart(8)})\n` +
        `Modify: ${mtime}\nChange: ${mtime}\n`);
    }
    return code;
  });

  reg('du', ctx => {
    const a = Args.parse(ctx.args, specOf('du'));
    const maxDepth = a.has('s') ? 0 : a.value('d') !== undefined ? Number(a.value('d')) : Infinity;
    const fmt = (kb: number) => (!a.has('h') ? String(kb)
      : kb < 10 ? `${kb.toFixed(1)}K` : kb < 1024 ? `${kb}K` : kb < 1 << 20 ? `${fmt1(kb / 1024)}M` : `${fmt1(kb / 1048576)}G`);
    let total = 0, code = 0;
    const walk = (abs: string, display: string, depth: number): number => {
      if (!ctx.fs.isDirectory(abs, false)) {
        const kb = ctx.fs.stat(abs, false).blocks / 2;
        if ((a.has('a') && depth <= maxDepth) || depth === 0) line(ctx, `${fmt(kb)}\t${display}`);
        return kb;
      }
      let sum = 4;
      if (ctx.fs.canRead(abs)) for (const n of ctx.fs.list(abs)) sum += walk(norm(abs, n), trimEnd(display, '/') + '/' + n, depth + 1);
      else { ctx.stderr.write('du: ' + specMessage('du', 'cannotReadDirectory', { file: display }) + '\n'); code = 1; }
      if (depth <= maxDepth) line(ctx, `${fmt(sum)}\t${display}`);
      return sum;
    };
    for (const p of a.positional.length ? a.positional : ['.']) {
      const abs = ctx.resolvePath(p);
      if (!ctx.fs.exists(abs, false)) { ctx.stderr.write(`du: ${specMessage('du', 'cannotAccess', { file: p, errno: 'No such file or directory' })}\n`); code = 1; continue; }
      total += walk(abs, p, 0);
    }
    if (a.has('c')) line(ctx, `${fmt(total)}\ttotal`);
    return code;
  });

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
  });

  reg('chmod', ctx => {
    let recursive = false, verbose = false, changes = false, quiet = false;
    let spec: string | undefined, reference: string | undefined;
    const files: string[] = [];
    const args = [...ctx.args];
    while (args.length) {
      const arg = args.shift()!;
      if (arg === '--reference' || arg.startsWith('--reference=')) {
        if (arg === '--reference' && !args.length) throw new UsageException(specMessage('chmod', 'referenceNeedsArgument', {}));
        reference = arg.includes('=') ? arg.slice(12) : args.shift()!;
      } else if (arg === '-R' || arg === '--recursive') recursive = true;
      else if (arg === '-v' || arg === '--verbose') verbose = true;
      else if (arg === '-c' || arg === '--changes') changes = true;
      else if (arg === '-f' || arg === '--silent' || arg === '--quiet') quiet = true;
      else if (arg.startsWith('--') && arg.length > 2) throw new UsageException(specMessage('chmod', 'unrecognizedOption', { option: arg }));
      else if (arg.length > 1 && arg[0] === '-' && [...arg].find(c => !'-rwxXstugoa+=,01234567'.includes(c)) !== undefined)
        throw new UsageException(specMessage('chmod', 'invalidOption', { option: [...arg].find(c => !'-rwxXstugoa+=,01234567'.includes(c))! }));
      else if (spec === undefined && reference === undefined) spec = arg; // may look like an option, e.g. -x or -w
      else files.push(arg);
    }
    if (reference !== undefined) {
      // --reference: the mode bits of that file instead of a mode
      const rpath = ctx.resolvePath(reference);
      if (!ctx.fs.exists(rpath)) {
        ctx.stderr.write('chmod: ' + specMessage('chmod', 'cannotStatReference', { file: reference, reason: ctx.fs.whyMissing(rpath) }) + '\n');
        return 1;
      }
      spec = (ctx.fs.stat(rpath).mode & 0o7777).toString(8);
      if (files.length === 0) throw new UsageException(specMessage('chmod', 'missingOperand', {}));
    }
    if (spec === undefined || files.length === 0) throw new UsageException(spec === undefined ? specMessage('chmod', 'missingOperand', {}) : specMessage('chmod', 'missingOperandAfter', { operand: spec }));
    let code = 0;
    const apply = (display: string, path: string) => {
      const st = ctx.fs.stat(path);
      const mode = parseMode(spec!, st.mode, st.isDirectory, ctx.fs.umask);
      try { ctx.fs.chmod(path, mode); } catch {
        if (!quiet) ctx.stderr.write('chmod: ' + specMessage('chmod', 'cannotChangePermissions', { file: display }) + '\n');
        code = 1;
        return;
      }
      const f = (m: number) => `${m.toString(8).padStart(4, '0')} (${VirtualFileSystem.formatMode(st.isDirectory, m).slice(1)})`;
      if (mode !== st.mode && (verbose || changes)) line(ctx, `mode of '${display}' changed from ${f(st.mode)} to ${f(mode)}`);
      else if (mode === st.mode && verbose) line(ctx, `mode of '${display}' retained as ${f(mode)}`);
      if (recursive && st.isDirectory && ctx.fs.canRead(path)) for (const n of ctx.fs.list(path)) {
        const shown = trimEnd(display, '/') + '/' + n;
        // like chmod -R, links met on the way are left alone
        if (!ctx.fs.isSymlink(norm(path, n))) apply(shown, norm(path, n));
        else if (verbose) line(ctx, `neither symbolic link '${shown}' nor referent has been changed`);
      }
    };
    for (const f of files) {
      const path = ctx.resolvePath(f);
      if (!ctx.fs.exists(path)) { ctx.stderr.write('chmod: ' + specMessage('chmod', 'cannotAccess', { file: f, reason: ctx.fs.whyMissing(path) }) + '\n'); code = 1; continue; }
      apply(f, path);
    }
    return code;
  });

  const chown: CommandHandler = ctx => {
    const operands = ctx.args.filter(x => !x.startsWith('-'));
    const files = operands.slice(1); // first operand is the owner
    if (files.length === 0) throw new UsageException(specMessage(ctx.name, 'missingOperand', {}));
    const owner = operands[0].split(':')[0];
    let code = 0;
    for (const f of files) {
      const path = ctx.resolvePath(f);
      if (!ctx.fs.exists(path)) { ctx.stderr.write(`${ctx.name}: ` + specMessage(ctx.name, 'cannotAccess', { file: f, reason: ctx.fs.whyMissing(path) }) + '\n'); code = 1; continue; }
      try { ctx.fs.setOwner(path, owner.length > 0 && ctx.name === 'chown' ? owner : ctx.fs.stat(path).owner, ctx.args.includes('-R')); } catch {
        ctx.stderr.write(`${ctx.name}: changing ${ctx.name === 'chown' ? 'ownership' : 'group'} of '${f}': Operation not permitted\n`);
        code = 1;
      }
    }
    return code;
  };
  reg('chown', chown);
  reg('chgrp', chown);

  const pager: CommandHandler = ctx => {
    let code = 0;
    for (const [, text] of inputs(ctx, ctx.args.filter(x => !x.startsWith('-') && !x.startsWith('+')), () => { code = 1; })) ctx.stdout.write(text);
    return code;
  };
  reg('less', pager);
  reg('more', pager);

  reg('file', ctx => {
    let code = 0;
    for (const p of ctx.args.filter(x => !x.startsWith('-'))) {
      const abs = ctx.resolvePath(p);
      let desc: string;
      if (!ctx.fs.exists(abs)) { desc = specMessage('file', 'cannotOpen', { file: p }); code = 1; }
      else if (ctx.fs.isDirectory(abs)) desc = 'directory';
      else desc = describeFile(ctx.fs.readBytes(abs));
      line(ctx, `${p}: ${desc}`);
    }
    return code;
  });

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
