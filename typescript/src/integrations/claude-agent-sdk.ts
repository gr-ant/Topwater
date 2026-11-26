import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { BashTool, Shell } from '../index.js';
import { serialRunner } from './run.js';

/**
 * An in-process MCP server named "topwater" with one "bash" tool backed by `shell`, in the shape the Claude Agent SDK's
 * `createSdkMcpServer` returns. Use as `query({ prompt, options: { mcpServers: { topwater: bashMcpServer(shell) } } })`;
 * the model sees the tool as `mcp__topwater__bash`.
 */
export function bashMcpServer(shell: Shell = new Shell(), options: { description?: string; name?: string } = {}) {
  const run = serialRunner(shell);
  const name = options.name ?? 'topwater';
  const instance = new McpServer({ name, version: '1.0.0' });
  instance.registerTool(
    BashTool.name,
    {
      description: options.description ?? BashTool.description,
      inputSchema: { command: z.string().describe(BashTool.inputSchema.properties.command.description) },
    },
    async ({ command }) => ({ content: [{ type: 'text' as const, text: await run(command) }] }),
  );
  return { type: 'sdk' as const, name, instance };
}
