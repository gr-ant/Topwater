// Directories and path names: cd, pwd, pushd, popd, dirs, basename, dirname, realpath, readlink, mktemp. See docs/architecture.md.
import type { Shell } from '../shell.js';
import { Args, specOf, specMessage, ShellError, UsageException, line, reason, type CommandContext, type CommandHandler } from '../command.js';
import { VirtualFileSystem } from '../vfs.js';
import { norm, trimEnd } from './files.js';

export function registerPaths(shell: Shell) {
  const reg = (name: string, handler: CommandHandler) => shell.register(name, handler);

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

  reg('basename', ctx => {
    const a = Args.parse(ctx.args, specOf('basename'));
    if (a.positional.length === 0) throw new UsageException(specMessage('basename', 'missingOperand', {}));
    const suffix = a.value('s') ?? (!a.has('a') && a.positional.length === 2 ? a.positional[1] : undefined);
    const names = a.has('a') || a.value('s') !== undefined ? a.positional : a.positional.slice(0, 1);
    for (const n of names) {
      const t = trimEnd(n, '/');
      let b = t.length === 0 ? (n.length ? '/' : '') : t.slice(t.lastIndexOf('/') + 1);
      if (suffix !== undefined && b.endsWith(suffix) && b !== suffix) b = b.slice(0, -suffix.length);
      ctx.stdout.write(b + (a.has('z') ? '\0' : '\n'));
    }
  });

  reg('dirname', ctx => {
    const a = Args.parse(ctx.args, specOf('dirname'));
    if (a.positional.length === 0) throw new UsageException(specMessage('dirname', 'missingOperand', {}));
    for (const n of a.positional) {
      const t = trimEnd(n, '/');
      const i = t.lastIndexOf('/');
      ctx.stdout.write((t.length === 0 ? (n.length ? '/' : '.') : i < 0 ? '.' : i === 0 ? '/' : trimEnd(t.slice(0, i), '/') || '/') + (a.has('z') ? '\0' : '\n'));
    }
  });

  const realpath: CommandHandler = ctx => {
    const a = Args.parse(ctx.args, specOf(ctx.name));
    const readlink = ctx.name === 'readlink', plain = readlink && !a.has('f') && !a.has('e') && !a.has('m');
    // readlink is silent about failures unless -v; realpath reports them unless -q
    const report = readlink ? a.has('v') : !a.has('q');
    if (a.positional.length === 0 && readlink) throw new UsageException(specMessage('readlink', 'missingOperand'));
    const fs = ctx.fs;
    const end = a.has('z') ? '\0' : readlink && a.has('n') && a.positional.length === 1 ? '' : '\n';
    let code = 0;
    for (const p of a.positional.length ? a.positional : ['.']) {
      const abs = ctx.resolvePath(p);
      let result: string | null, why = '';
      if (plain) {
        try { result = fs.readLink(abs); } catch (e) { result = null; why = reason(e); }
      } else if (!readlink && a.has('s')) result = norm(ctx.shell.cwd, p); // -s: no links resolved
      else { result = fs.realPath(abs, a.has('e')) ?? (a.has('m') ? abs : null); why = fs.whyMissing(abs); }
      if (result === null) { if (report) ctx.stderr.write(`${ctx.name}: ${specMessage(ctx.name, 'failed', { file: p, errno: why ?? '' })}\n`); code = 1; continue; }
      ctx.stdout.write(result + end);
    }
    return code;
  };
  reg('realpath', realpath);
  reg('readlink', realpath);

  reg('mktemp', ctx => {
    const a = Args.parse(ctx.args, specOf('mktemp'));
    const template = a.positional[0] ?? 'tmp.XXXXXXXXXX';
    const chars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
    const m = /X{3,}$/.exec(template);
    if (!m) throw new ShellError(specMessage('mktemp', 'tooFewX', { template }));
    const name = template.slice(0, m.index) + Array.from({ length: m[0].length }, () => chars[ctx.shell.nextRandom(chars.length)]).join('');
    const path = template.includes('/') && a.value('p') === undefined && !a.has('t') ? ctx.resolvePath(name) : norm(a.value('p') ?? '/tmp', name);
    if (!a.has('u')) { if (a.has('d')) { ctx.fs.mkdir(path); ctx.fs.chmod(path, 0o700); } else { ctx.fs.writeText(path, ''); ctx.fs.chmod(path, 0o600); } }
    line(ctx, path);
  });
}
