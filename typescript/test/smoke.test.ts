import { it, expect } from 'vitest';
import { Shell, BashTool } from '../src/index.js';

it('runs a pipeline and keeps state between calls', async () => {
  const sh = new Shell();
  expect((await sh.run('x=5; echo "hi $x" | sed s/hi/bye/')).stdout).toBe('bye 5\n');
  expect((await sh.run('echo $x; pwd')).stdout).toBe('5\n/home/agent\n');
  expect(await BashTool.execute(sh, 'false')).toBe('[exit code 1]');
  expect(JSON.parse(BashTool.inputSchemaJson).required).toEqual(['command']);
});
