import { BashTool, type Shell } from '../index.js';

/** Returns `BashTool.execute` for `shell` that runs calls one at a time, as one session must. */
export function serialRunner(shell: Shell): (command: string) => Promise<string> {
  let queue: Promise<unknown> = Promise.resolve();
  return command => {
    const out = queue.then(() => BashTool.execute(shell, command));
    queue = out.catch(() => {});
    return out;
  };
}
