import { describe, it, expect } from 'vitest';
import { RunContext } from '@openai/agents';
import { BashTool, Shell } from '../src/index.js';
import { bashTool as aiTool } from '../src/integrations/ai-sdk.js';
import { bashTool as agentsTool } from '../src/integrations/openai-agents.js';

const aiRun = (t: ReturnType<typeof aiTool>, command: string) =>
  t.execute!({ command }, { toolCallId: 'c', messages: [] }) as Promise<string>;
const agentsRun = (t: ReturnType<typeof agentsTool>, command: string) =>
  t.invoke(new RunContext(), JSON.stringify({ command })) as Promise<string>;

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
