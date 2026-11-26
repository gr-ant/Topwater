import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { BashTool, Shell } from '../index.js';
import { serialRunner } from './run.js';

/**
 * An in-process MCP server named "topwater" with one "bash" tool backed by `shell`, in the shape the Claude Agent SDK's
 * `createSdkMcpServer` returns. The model sees the tool as `mcp__topwater__bash`. Prefer `bashOptions`, which also
 * turns off the host's tools.
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

/**
 * Claude Agent SDK `query` options that give the model only the Topwater shell: `query({ prompt, options: bashOptions(shell) })`.
 * `tools: []` removes Claude Code's own tools (its Bash runs on the host); `settingSources: []` and `strictMcpConfig`
 * keep the developer's Claude Code settings, allow rules and MCP servers out of the session.
 */
export function bashOptions(shell: Shell = new Shell(), options: { description?: string } = {}) {
  return {
    mcpServers: { topwater: bashMcpServer(shell, options) },
    tools: [] as string[],
    allowedTools: [`mcp__topwater__${BashTool.name}`],
    settingSources: [] as never[],
    strictMcpConfig: true,
  };
}
