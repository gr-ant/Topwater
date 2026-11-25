import { jsonSchema, tool } from 'ai';
import { BashTool, Shell } from '../index.js';

/** A Vercel AI SDK "bash" tool backed by `shell`. Use as `tools: { bash: bashTool(shell) }`. */
export function bashTool(shell: Shell = new Shell(), options: { description?: string } = {}) {
  let queue: Promise<unknown> = Promise.resolve(); // one session runs one command at a time
  return tool({
    description: options.description ?? BashTool.description,
    inputSchema: jsonSchema<{ command: string }>(BashTool.inputSchema as any),
    execute: ({ command }) => {
      const out = queue.then(() => BashTool.execute(shell, command));
      queue = out.catch(() => {});
      return out;
    },
  });
}
