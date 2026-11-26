import { BashTool, type Shell } from '../index.js';

const queues = new WeakMap<Shell, Promise<unknown>>(); // per shell, so two adapters on one shell never interleave

/** Returns `BashTool.execute` for `shell`, queued so the shell runs one call at a time across every adapter. */
export function serialRunner(shell: Shell): (command: string) => Promise<string> {
  return command => {
    const out = (queues.get(shell) ?? Promise.resolve()).then(() => BashTool.execute(shell, command));
    queues.set(shell, out.catch(() => {}));
    return out;
  };
}
