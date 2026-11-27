// mount: the sources the host mounted (docs/mounts.md), the changes waiting in them, listing them again, dropping changes.
import type { Shell } from '../shell.js';
import { Args, line, specMessage, specOf, type CommandContext } from '../command.js';
import { VirtualFileSystem } from '../vfs.js';
import { SourceFailure, byCodePoint, type MountChange } from '../mounts.js';

export function registerMount(shell: Shell) {
  shell.register('mount', mount);
}

type Mount = ReturnType<Shell['mountTable']['get']>;

/** `TYPE:ID on DIR type TYPE (OPTIONS)`, the options naming what a writable mount's source can't do. */
function describe(m: Mount): string {
  const caps = m.capabilities();
  const opts = m.mode === 'read-only' ? ['ro'] : ['rw', m.mode, ...['delete', 'rename', 'mkdir'].filter(c => !caps.has(c)).map(c => 'no-' + c)];
  return `${m.type}:${m.id} on ${m.path} type ${m.type} (${opts.join(',')})` + (m.shown ? ` ${m.shown}` : '');
}

async function mount(ctx: CommandContext): Promise<number> {
  const a = Args.parse(ctx.args, specOf('mount'));
  const mounts = ctx.shell.mountTable;
  let code = 0;
  let chosen: Mount[] = [];
  for (const d of a.positional) {
    const m = mounts.items.get(VirtualFileSystem.normalize(ctx.shell.cwd, d));
    if (!m) { ctx.stderr.write('mount: ' + specMessage('mount', 'notMounted', { dir: d }) + '\n'); code = 1; }
    else chosen.push(m);
  }
  if (!a.positional.length) chosen = [...mounts.items.values()].sort((x, y) => byCodePoint(x.path, y.path));
  for (const m of chosen) {
    if (a.has('r')) {
      try { await mounts.refresh(m.path); } catch (e) {
        if (!(e instanceof SourceFailure)) throw e;
        ctx.stderr.write(`mount: ${m.path}: ${e.safe}\n`);
        code = 1;
      }
      for (const w of m.warnings) ctx.stderr.write('mount: ' + w + '\n');
    } else if (a.has('d')) mounts.discard(m.path, false);
    else if (a.has('c')) {
      for (const c of m.changes()) line(ctx, change(m.machine.bind(m), c));
    } else {
      line(ctx, describe(m));
      for (const w of [...m.warnings, ...m.held.length ? [specMessage('mount', 'held', { count: String(m.held.length) })] : []]) line(ctx, '  ' + w);
    }
  }
  return code;
}

const change = (machine: (p: string) => string, c: MountChange) =>
  c.kind === 'rename' ? `rename ${machine(c.path)} -> ${machine(c.to!)}` : `${c.kind} ${machine(c.path)}`;
