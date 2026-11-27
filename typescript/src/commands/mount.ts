// mount: the sources the host mounted (docs/mounts.md), the changes waiting in them, and listing them again.
import type { Shell } from '../shell.js';
import { Args, line, specMessage, specOf, type CommandContext } from '../command.js';
import { VirtualFileSystem } from '../vfs.js';
import { byCodePoint, isControl, type MountChange, type MountInfo } from '../mounts.js';

export function registerMount(shell: Shell) {
  shell.register('mount', mount);
}

const describe = (info: MountInfo) =>
  `${info.type}:${info.id} on ${info.path} type ${info.type} (${info.mode === 'read-only' ? 'ro' : 'rw,' + info.mode})` + (info.error ? ` ${info.error}` : '');

async function mount(ctx: CommandContext): Promise<number> {
  const a = Args.parse(ctx.args, specOf('mount'));
  const mounts = ctx.shell.mountTable;
  let code = 0;
  let chosen = [];
  for (const d of a.positional) {
    const m = mounts.items.get(VirtualFileSystem.normalize(ctx.shell.cwd, d));
    if (!m) { ctx.stderr.write('mount: ' + specMessage('mount', 'notMounted', { dir: d }) + '\n'); code = 1; }
    else chosen.push(m);
  }
  if (!a.positional.length) chosen = [...mounts.items.values()].sort((x, y) => byCodePoint(x.path, y.path));
  for (const m of chosen) {
    if (a.has('r')) {
      try { await mounts.refresh(m.path); } catch (e) { // the source's own error, for the agent
        if (isControl(e)) throw e;
        ctx.stderr.write(`mount: ${m.path}: ${e instanceof Error ? e.message : String(e)}\n`);
        code = 1;
      }
      for (const w of m.warnings) ctx.stderr.write('mount: ' + w + '\n');
    } else if (a.has('c')) {
      for (const c of m.changes()) line(ctx, change(m.machine.bind(m), c));
    } else line(ctx, describe(m.info()));
  }
  return code;
}

const change = (machine: (p: string) => string, c: MountChange) =>
  c.kind === 'rename' ? `rename ${machine(c.path)} -> ${machine(c.to!)}` : `${c.kind} ${machine(c.path)}`;
