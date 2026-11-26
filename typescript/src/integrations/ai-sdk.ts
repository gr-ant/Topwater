import { jsonSchema, tool } from 'ai';
import { BashTool, Shell } from '../index.js';
import { serialRunner } from './run.js';

/** A Vercel AI SDK "bash" tool backed by `shell`. Use as `tools: { bash: bashTool(shell) }`. */
export function bashTool(shell: Shell = new Shell(), options: { description?: string } = {}) {
  const run = serialRunner(shell);
  return tool({
    description: options.description ?? BashTool.descriptionFor(shell),
    inputSchema: jsonSchema<{ command: string }>(BashTool.inputSchema as any),
    execute: ({ command }) => run(command),
  });
}
