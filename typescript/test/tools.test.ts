// Ported from tests/Topwater.Tests/ToolCommandTests.cs and SecondReviewTests.cs (SchemaMapperReviewTests).
import { describe, it, expect } from 'vitest';
import { Shell, type ToolSpec, type McpClientLike } from '../src/index.js';
import toolData from './data/ToolCommandTests.json' with { type: 'json' };
import secondData from './data/SecondReviewTests.json' with { type: 'json' };

const SCHEMA = {
  type: 'object', required: ['repo'], properties: {
    repo: { type: 'string', description: 'owner/name' },
    state: { type: 'string', enum: ['open', 'closed'], default: 'open' },
    limit: { type: 'integer' },
    labels: { type: 'array', items: { type: 'string' } },
    include_drafts: { type: 'boolean' },
    filter: { type: 'object' },
    ratio: { type: ['number', 'null'] },
  },
};

function make() {
  const calls: [string, string][] = [];
  const tools: ToolSpec[] = [
    { name: 'list_issues', description: 'List issues in a repository.\nSecond line.', inputSchema: SCHEMA },
    { name: 'whoami', description: 'Show the current user', inputSchema: { type: 'object' } },
    { name: 'fail', inputSchema: {} },
  ];
  const shell = new Shell().registerTools('gh', tools, (tool, args) => {
    calls.push([tool, JSON.stringify(args)]);
    return tool === 'fail' ? { text: 'boom', isError: true } : { text: '[{"n":1},{"n":2}]' };
  }, 'GitHub tools');
  return { shell, calls };
}

describe('schema tools', () => {
  it('lists tools and documents each', async () => {
    const { shell: sh } = make();
    const help = await sh.run('gh --help');
    expect(help.stdout).toContain('GitHub tools');
    expect(help.stdout).toContain('list-issues  List issues in a repository.');
    expect(help.stdout).not.toContain('Second line');
    expect((await sh.run('gh')).stdout).toBe(help.stdout);
    expect((await sh.run('help')).stdout).toContain('gh');
    const one = (await sh.run('gh list-issues --help')).stdout;
    for (const s of ['Usage: gh list-issues --repo TEXT [options]', '--repo TEXT', 'owner/name (required)', '(one of: open, closed) (default: open)',
      '--labels TEXT', '(repeatable)', '--limit INT', '--include-drafts ', '--json JSON']) expect(one).toContain(s);
  });

  it('parses options by type', async () => {
    const { shell: sh, calls } = make();
    const r = await sh.run(`gh list-issues --repo a/b --limit 5 --labels bug --labels "help wanted" --include-drafts --filter '{"x":1}' --ratio=0.5 | jq length`);
    expect(r.stdout).toBe('2\n');
    expect(calls[0]).toEqual(['list_issues', '{"repo":"a/b","limit":5,"labels":["bug","help wanted"],"include_drafts":true,"filter":{"x":1},"ratio":0.5}']);
    await sh.run(`gh list_issues --repo a/b --no-include-drafts --labels '["x","y"]' --include_drafts false`);
    expect(calls[1][1]).toBe('{"repo":"a/b","include_drafts":false,"labels":["x","y"]}');
    await sh.run(`echo '{"repo":"from/stdin","limit":2}' | gh list-issues --json - --limit 9`);
    expect(calls[2][1]).toBe('{"repo":"from/stdin","limit":9}');
    expect((await sh.run('gh whoami')).exitCode).toBe(0);
    expect(calls[3][1]).toBe('{}');
  });

  for (const [command, message] of toolData['ToolCommandTests.BadUsageExplainsItselfWithoutCallingTheTool'] as string[][]) {
    it(`bad usage: ${command}`, async () => {
      const { shell: sh, calls } = make();
      const r = await sh.run(command);
      expect(r.exitCode).toBe(2);
      expect(r.stderr).toContain(message);
      expect(calls).toEqual([]);
    });
  }

  it('tool errors go to stderr with exit 1', async () => {
    const r = await make().shell.run('gh fail || echo handled');
    expect([r.stderr, r.stdout]).toEqual(['boom\n', 'handled\n']);
  });
});

describe('schema shapes from Pydantic/FastMCP', () => {
  const schema = {
    type: 'object', $defs: { Level: { type: 'string', enum: ['1', '2'] } }, properties: {
      tags: { anyOf: [{ type: 'array', items: { type: 'string' } }, { type: 'null' }], description: 'Tags' },
      id: { anyOf: [{ type: 'integer' }, { type: 'string' }] },
      level: { $ref: '#/$defs/Level' },
      ratio: { type: 'number' }, limit: { type: 'integer' }, cache: { type: 'boolean' }, title: { type: 'string' }, odd: true,
    },
  };
  async function call(command: string) {
    let sent: string | undefined;
    const sh = new Shell().registerTools('t', [{ name: 'run', description: 'Run it', inputSchema: schema }], (_, args) => { sent = JSON.stringify(args); return { text: 'ok' }; });
    return { result: await sh.run(command), sent };
  }
  for (const [command, expected] of secondData['SchemaMapperReviewTests.SendsWhatWasMeant'] as string[][]) {
    it(command, async () => {
      const { result, sent } = await call(command);
      expect(result.exitCode, result.stderr).toBe(0);
      expect(sent).toBe(expected);
    });
  }
  it('rejects non-finite numbers and still prints help', async () => {
    const bad = await call('t run --ratio NaN');
    expect(bad.result.exitCode).toBe(2);
    expect(bad.sent).toBeUndefined();
    const help = (await call('t run --help')).result;
    expect(help.exitCode).toBe(0);
    for (const s of ['--tags TEXT', 'Tags (repeatable)', '(one of: 1, 2)', '--odd']) expect(help.stdout).toContain(s);
  });
});

describe('MCP', () => {
  it('mounts an MCP client', async () => {
    const client: McpClientLike = {
      async listTools() {
        return {
          tools: [
            { name: 'get_forecast', description: 'Weather forecast for a city', inputSchema: { type: 'object', required: ['city'], properties: { city: { type: 'string', description: 'City name' }, days: { type: 'integer', description: 'Days ahead', default: 1 } } } },
            { name: 'admin_reset', description: 'Dangerous', inputSchema: { type: 'object' } },
          ],
        };
      },
      async callTool({ arguments: args }) {
        const a = args as { city: string; days?: number };
        if (a.city === 'Atlantis') return { content: [{ type: 'text', text: 'no such city' }], isError: true };
        return { content: [{ type: 'text', text: JSON.stringify({ city: a.city, days: a.days ?? 1, high: 21 }) }] };
      },
      getServerVersion: () => ({ name: 'weather' }),
    };
    const sh = new Shell();
    expect(await sh.mountMcp('weather', client, { include: n => !n.startsWith('admin') })).toEqual(['get_forecast']);
    const help = (await sh.run('weather --help')).stdout;
    expect(help).toContain('get-forecast  Weather forecast for a city');
    expect(help).not.toContain('admin');
    const toolHelp = (await sh.run('weather get-forecast --help')).stdout;
    for (const s of ['--city TEXT', 'City name (required)', '--days INT']) expect(toolHelp).toContain(s);
    expect((await sh.run("weather get-forecast --city Lisbon --days 3 | jq -r '\"\\(.city) \\(.days) \\(.high)\"'")).stdout).toBe('Lisbon 3 21\n');
    const bad = await sh.run('weather get-forecast --city Atlantis');
    expect(bad.exitCode).toBe(1);
    expect(bad.stderr).not.toBe('');
    expect((await sh.run('weather admin-reset')).exitCode).toBe(2);
  });

  it('a server that throws is a failed command, not a crash', async () => {
    const sh = new Shell();
    await sh.mountMcp('x', { listTools: async () => ({ tools: [{ name: 'go', inputSchema: {} }] }), callTool: async () => { throw new Error('connection closed'); } });
    const r = await sh.run('x go || echo handled');
    expect(r.stdout).toBe('handled\n');
    expect(r.stderr).toContain('connection closed');
  });
});
