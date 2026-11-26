import { createTool } from '@mastra/core/tools';
import { BashTool, Shell } from '../index.js';
import { serialRunner } from './run.js';

/** A Mastra tool with id "bash" backed by `shell`. Use as `new Agent({ ..., tools: { bash: bashTool(shell) } })`. */
export function bashTool(shell: Shell = new Shell(), options: { description?: string } = {}) {
  const run = serialRunner(shell);
  return createTool({
    id: BashTool.name,
    description: options.description ?? BashTool.descriptionFor(shell),
    inputSchema: BashTool.inputSchema as any,
    execute: async input => run((input as { command: string }).command),
  });
}
