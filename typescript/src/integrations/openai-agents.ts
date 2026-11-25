import { tool } from '@openai/agents';
import { BashTool, Shell } from '../index.js';

/** An OpenAI Agents SDK function tool named "bash" backed by `shell`. Use as `new Agent({ tools: [bashTool(shell)] })`. */
export function bashTool(shell: Shell = new Shell(), options: { description?: string } = {}) {
  let queue: Promise<unknown> = Promise.resolve(); // one session runs one command at a time
  return tool({
    name: BashTool.name,
    description: options.description ?? BashTool.description,
    parameters: { ...BashTool.inputSchema, required: ['command'], additionalProperties: false } as any,
    strict: true,
    execute: ({ command }: { command: string }) => {
      const out = queue.then(() => BashTool.execute(shell, command));
      queue = out.catch(() => {});
      return out;
    },
  });
}
