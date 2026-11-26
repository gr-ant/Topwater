import { tool } from '@openai/agents';
import { BashTool, Shell } from '../index.js';
import { serialRunner } from './run.js';

/** An OpenAI Agents SDK function tool named "bash" backed by `shell`. Use as `new Agent({ tools: [bashTool(shell)] })`. */
export function bashTool(shell: Shell = new Shell(), options: { description?: string } = {}) {
  const run = serialRunner(shell);
  return tool({
    name: BashTool.name,
    description: options.description ?? BashTool.description,
    parameters: { ...BashTool.inputSchema, required: ['command'], additionalProperties: false } as any,
    strict: true,
    execute: ({ command }: { command: string }) => run(command),
  });
}
