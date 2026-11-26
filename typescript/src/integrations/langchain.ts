import { tool } from '@langchain/core/tools';
import { BashTool, Shell } from '../index.js';
import { serialRunner } from './run.js';

/** A LangChain.js tool named "bash" backed by `shell`, for LangGraph agents and `createAgent({ tools: [bashTool(shell)] })`. */
export function bashTool(shell: Shell = new Shell(), options: { description?: string } = {}) {
  const run = serialRunner(shell);
  return tool(({ command }: { command: string }) => run(command), {
    name: BashTool.name,
    description: options.description ?? BashTool.descriptionFor(shell),
    schema: BashTool.inputSchema as any,
  });
}
