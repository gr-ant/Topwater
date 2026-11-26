import { describe, it, expect } from 'vitest';
import { RunContext } from '@openai/agents';
import { BashTool, Shell } from '../src/index.js';
import { bashTool as aiTool } from '../src/integrations/ai-sdk.js';
import { bashTool as agentsTool } from '../src/integrations/openai-agents.js';
import { bashTool as langchainTool } from '../src/integrations/langchain.js';
import { bashTool as mastraTool } from '../src/integrations/mastra.js';
import { bashMcpServer, bashOptions } from '../src/integrations/claude-agent-sdk.js';
import { serialRunner } from '../src/integrations/run.js';
import { createAgent, AIMessage } from 'langchain';
import { fakeModel } from '@langchain/core/testing';
import { Agent as MastraAgent } from '@mastra/core/agent';
import { MockLanguageModelV4 } from 'ai/test';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';
import { createServer } from 'node:http';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const aiRun = (t: ReturnType<typeof aiTool>, command: string) =>
  t.execute!({ command }, { toolCallId: 'c', messages: [] }) as Promise<string>;
// a peer that may be missing: the import path is a variable so the test file still loads without it
const claudeSdk = await import('@anthropic-ai/claude-agent-sdk' as string).catch(() => null);
const agentsRun = (t: ReturnType<typeof agentsTool>, command: string) =>
  t.invoke(new RunContext(), JSON.stringify({ command })) as Promise<string>;

describe('every adapter', () => {
  it('runs one call at a time per shell, across adapters', async () => {
    const shell = new Shell();
    const log: string[] = [];
    shell.register('probe', async ctx => {
      log.push(`start ${ctx.args[0]}`);
      await new Promise(r => setTimeout(r, 10));
      log.push(`end ${ctx.args[0]}`);
    });
    await Promise.all([langchainTool(shell).invoke({ command: 'probe 0' }), mastraTool(shell).execute!({ command: 'probe 1' }, {} as any), serialRunner(shell)('probe 2')]);
    expect(log).toHaveLength(6);
    for (let i = 0; i < 6; i += 2) expect(log[i + 1]).toBe(log[i].replace('start', 'end')); // each call ends before the next starts
  });
});

describe('Vercel AI SDK adapter', () => {
  it('exposes the core description and schema', () => {
    const t = aiTool();
    expect(t.description).toBe(BashTool.description);
    const schema = (t.inputSchema as any).jsonSchema;
    expect(schema.required).toEqual(['command']);
    expect(schema.properties.command.type).toBe('string');
  });
  it('keeps state and reports failures', async () => {
    const t = aiTool(new Shell());
    await aiRun(t, 'cd /tmp && echo hi > a.txt');
    expect(await aiRun(t, 'cat a.txt')).toBe('hi\n');
    expect(await aiRun(t, 'false')).toContain('[exit code 1]');
  });
});

describe('OpenAI Agents adapter', () => {
  it('exposes name, description and schema', () => {
    const t = agentsTool();
    expect(t.name).toBe(BashTool.name);
    expect(t.description).toBe(BashTool.description);
    expect((t.parameters as any).required).toEqual(['command']);
  });
  it('keeps state and reports failures', async () => {
    const t = agentsTool(new Shell());
    await agentsRun(t, 'cd /tmp && echo hi > a.txt');
    expect(await agentsRun(t, 'cat a.txt')).toBe('hi\n');
    expect(await agentsRun(t, 'false')).toContain('[exit code 1]');
  });
});

describe('LangChain adapter', () => {
  it('exposes name, description and schema', () => {
    const t = langchainTool();
    expect(t.name).toBe(BashTool.name);
    expect(t.description).toBe(BashTool.description);
    expect((t.schema as any).required).toEqual(['command']);
  });
  it('keeps state and reports failures', async () => {
    const t = langchainTool(new Shell());
    await t.invoke({ command: 'cd /tmp && echo hi > a.txt' });
    expect(await t.invoke({ command: 'cat a.txt' })).toBe('hi\n');
    expect(await t.invoke({ command: 'false' })).toContain('[exit code 1]');
  });
  it('runs in a LangGraph agent', async () => {
    const shell = new Shell();
    const model = fakeModel()
      .respondWithTools([{ name: 'bash', args: { command: 'echo hi > /tmp/a.txt; cat /tmp/a.txt' } }])
      .respond(new AIMessage('done'));
    const agent = createAgent({ model, tools: [langchainTool(shell)] });
    const { messages } = await agent.invoke({ messages: [{ role: 'user', content: 'go' }] });
    expect(messages.find(m => m.type === 'tool')?.content).toBe('hi\n');
    expect(messages.at(-1)?.content).toBe('done');
  });
});

describe('Mastra adapter', () => {
  it('exposes id, description and schema', () => {
    const t = mastraTool();
    expect(t.id).toBe(BashTool.name);
    expect(t.description).toBe(BashTool.description);
  });
  it('keeps state and reports failures', async () => {
    const t = mastraTool(new Shell());
    await t.execute!({ command: 'cd /tmp && echo hi > a.txt' }, {} as any);
    expect(await t.execute!({ command: 'cat a.txt' }, {} as any)).toBe('hi\n');
    expect(await t.execute!({ command: 'false' }, {} as any)).toContain('[exit code 1]');
  });
  it('runs in a Mastra agent', async () => {
    const shell = new Shell();
    const usage = { inputTokens: { total: 1, noCache: 1, cacheRead: 0, cacheWrite: 0 }, outputTokens: { total: 1, text: 1, reasoning: 0 } };
    const reply = (content: unknown[], unified: string) => ({ content, finishReason: { unified, raw: undefined }, usage, warnings: [] });
    const model = new MockLanguageModelV4({ doGenerate: [
      reply([{ type: 'tool-call', toolCallId: 'c1', toolName: 'bash', input: JSON.stringify({ command: 'echo hi > /tmp/a.txt; cat /tmp/a.txt' }) }], 'tool-calls'),
      reply([{ type: 'text', text: 'done' }], 'stop'),
    ] as any });
    const agent = new MastraAgent({ id: 'a', name: 'a', instructions: 'Use bash.', model, tools: { bash: mastraTool(shell) } });
    const result = await agent.generate('go');
    expect(result.text).toBe('done');
    expect((await shell.run('cat /tmp/a.txt')).stdout).toBe('hi\n');
  });
});

describe('Claude Agent SDK adapter', () => {
  async function connect(server: ReturnType<typeof bashMcpServer>) {
    const [a, b] = InMemoryTransport.createLinkedPair();
    const client = new Client({ name: 'test', version: '1.0.0' });
    await Promise.all([server.instance.connect(a), client.connect(b)]);
    const run = async (command: string) => ((await client.callTool({ name: 'bash', arguments: { command } })).content as any)[0].text;
    return { client, run };
  }
  it('serves the bash tool over MCP', async () => {
    const server = bashMcpServer();
    expect(server.type).toBe('sdk');
    expect(server.name).toBe('topwater');
    const { client } = await connect(server);
    const [t] = (await client.listTools()).tools;
    expect(t.name).toBe(BashTool.name);
    expect(t.description).toBe(BashTool.description);
    expect(t.inputSchema.required).toEqual(['command']);
  });
  it('bashOptions gives the model only the Topwater shell', () => {
    const options = bashOptions();
    expect(options.mcpServers.topwater.type).toBe('sdk');
    expect(options.tools).toEqual([]);
    expect(options.allowedTools).toEqual(['mcp__topwater__bash']);
    expect(options.settingSources).toEqual([]);
    expect(options.strictMcpConfig).toBe(true);
  });
  it('keeps state and reports failures', async () => {
    const { run } = await connect(bashMcpServer(new Shell()));
    await run('cd /tmp && echo hi > a.txt');
    expect(await run('cat a.txt')).toBe('hi\n');
    expect(await run('false')).toContain('[exit code 1]');
  });

  // needs the SDK, which is not a dev dependency (its native CLI is ~400 MB): `npm i --no-save @anthropic-ai/claude-agent-sdk`
  it.skipIf(!claudeSdk)('runs in a Claude agent against a local fake API', async () => {
    const shell = new Shell();
    const home = mkdtempSync(join(tmpdir(), 'topwater-claude-'));
    const results: unknown[] = [];
    const offered = new Set<string>();
    // the developer's own Claude Code config, which bashOptions must keep out: an MCP server and an allow rule
    const stub = join(home, 'hostexec.mjs'); // a stdio MCP server with one harmless tool
    const sdkDir = new URL('../node_modules/@modelcontextprotocol/sdk/dist/esm/server/', import.meta.url).href;
    writeFileSync(stub, `import { McpServer } from '${sdkDir}mcp.js'; import { StdioServerTransport } from '${sdkDir}stdio.js';
      const s = new McpServer({ name: 'hostexec', version: '1.0.0' }); s.registerTool('run', { description: 'stub' }, async () => ({ content: [{ type: 'text', text: 'host' }] }));
      await s.connect(new StdioServerTransport());`);
    const planted = { mcpServers: { hostexec: { type: 'stdio', command: process.execPath, args: [stub] } } };
    writeFileSync(join(home, '.claude.json'), JSON.stringify(planted));
    writeFileSync(join(home, '.mcp.json'), JSON.stringify(planted));
    mkdirSync(join(home, '.claude'));
    writeFileSync(join(home, '.claude', 'settings.json'), JSON.stringify({ permissions: { allow: ['Bash', 'mcp__hostexec'] } }));
    const api = createServer((req, res) => {
      let body = '';
      req.on('data', d => (body += d));
      req.on('end', () => {
        const b = body ? JSON.parse(body) : {};
        for (const t of b.tools ?? []) offered.add(t.name);
        if (!req.url!.startsWith('/v1/messages') || req.url!.includes('count_tokens')) return res.end(JSON.stringify({ input_tokens: 1 }));
        const result = b.messages?.at(-1)?.content?.find?.((c: any) => c.type === 'tool_result');
        if (result) results.push(result.content);
        const block = result
          ? { type: 'text', text: 'done' }
          : { type: 'tool_use', id: 'toolu_1', name: 'mcp__topwater__bash', input: { command: 'echo hi > /tmp/a.txt; cat /tmp/a.txt' } };
        const stop = result ? 'end_turn' : 'tool_use';
        const msg = { id: 'msg_1', type: 'message', role: 'assistant', model: b.model, content: [block], stop_reason: stop, stop_sequence: null, usage: { input_tokens: 1, output_tokens: 1 } };
        if (!b.stream) return res.end(JSON.stringify(msg));
        res.setHeader('content-type', 'text/event-stream');
        const ev = (type: string, data: object) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`);
        ev('message_start', { message: { ...msg, content: [], stop_reason: null } });
        ev('content_block_start', { index: 0, content_block: block.type === 'text' ? { type: 'text', text: '' } : { ...block, input: {} } });
        ev('content_block_delta', { index: 0, delta: block.type === 'text' ? { type: 'text_delta', text: block.text } : { type: 'input_json_delta', partial_json: JSON.stringify(block.input) } });
        ev('content_block_stop', { index: 0 });
        ev('message_delta', { delta: { stop_reason: stop, stop_sequence: null }, usage: { output_tokens: 1 } });
        ev('message_stop', {});
        res.end();
      });
    });
    await new Promise<void>(r => api.listen(0, '127.0.0.1', r));
    try {
      const env = { ...process.env, HOME: home, ANTHROPIC_BASE_URL: `http://127.0.0.1:${(api.address() as any).port}`, ANTHROPIC_API_KEY: 'test', CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1' };
      const options = { ...bashOptions(shell), cwd: home, env };
      let last: any;
      for await (const m of claudeSdk!.query({ prompt: 'go', options })) last = m;
      expect(last.result).toBe('done');
    } finally {
      api.close();
      rmSync(home, { recursive: true, force: true });
    }
    expect([...offered].filter(n => n !== 'DeferredToolPlaceholder')).toEqual(['mcp__topwater__bash']); // the placeholder is the CLI's own, not callable
    expect(JSON.stringify(results[0])).toContain('hi');
    expect((await shell.run('cat /tmp/a.txt')).stdout).toBe('hi\n');
  }, 60_000);
});
