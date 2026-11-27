// Processes and time: ps, wait, jobs, kill, ulimit, times, umask, sleep, timeout, time. See docs/architecture.md.
import type { Shell } from '../shell.js';
import { specMessage, ShellError, UsageException, DeadlineExceeded, ioOf, line, cannotRun, type CommandContext, type CommandHandler, type Deadline } from '../command.js';
import { ExitSignal } from '../shell/types.js';

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

function parseDuration(s: string): number {
  const m = /^(\d+(?:\.\d+)?|\.\d+)([smhd]?)$/.exec(s);
  if (!m) throw new ShellError(specMessage('sleep', 'invalidTimeInterval', { value: s }));
  const n = Number(m[1]);
  return n * ({ m: 60_000, h: 3_600_000, d: 86_400_000 } as Record<string, number>)[m[2]] || n * 1000;
}

/** Waits `ms` on the shell's clock: virtual time moves on at once; real sleeps wait, up to the call's (or a `timeout`'s) deadline. */
export async function sleepFor(ctx: CommandContext, ms: number) {
  if (ctx.shell.virtualNow !== undefined) { ctx.shell.sleepVirtual(ms); return; }
  const end = Date.now() + ms;
  for (;;) {
    ctx.checkTime();
    const left = Math.min(end - Date.now(), ctx.remainingMs() + 1);
    if (left <= 0) break;
    await new Promise(r => setTimeout(r, Math.min(left, 1000)));
  }
  ctx.checkTime();
}

export function registerProcess(shell: Shell) {
  const reg = (name: string, handler: CommandHandler) => shell.register(name, handler);

  reg('ps', ctx => ctx.stdout.write('    PID TTY          TIME CMD\n      1 pts/0    00:00:00 bash\n   4242 pts/0    00:00:00 ps\n'));

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

  reg('ulimit', ctx => {
    if (ctx.args.some(a => !a.startsWith('-'))) return 0; // setting a limit: accepted
    line(ctx, ctx.args.includes('-n') ? '1024' : ctx.args.includes('-s') ? '8192' : ctx.args.includes('-c') ? '0' : 'unlimited');
  });

  reg('times', ctx => ctx.stdout.write('0m0.010s 0m0.005s\n0m0.000s 0m0.000s\n'), );

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

  reg('sleep', async ctx => {
    if (ctx.args.length === 0) throw new UsageException(specMessage('sleep', 'missingOperand', {}));
    await sleepFor(ctx, ctx.args.reduce((t, a) => t + parseDuration(a), 0));
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
}
