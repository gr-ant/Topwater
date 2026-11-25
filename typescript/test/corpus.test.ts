// The shared corpus (../../tests/corpus/*.jsonl, recorded from real bash and GNU tools; see its README).
// Cases this port cannot pass yet are listed with a reason in tests/corpus/skip/typescript.json.
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { Shell } from '../src/index.js';

const dir = new URL('../../tests/corpus/', import.meta.url).pathname;
type Case = { id: string; script: string; setup?: string; stdout?: string; toolOutput?: string; exitCode?: number | null; quiet?: boolean | null };
const areas: Record<string, Case[]> = {};
for (const f of readdirSync(dir).filter(f => f.endsWith('.jsonl')).sort())
  areas[f.replace(/\.jsonl$/, '')] = readFileSync(dir + f, 'utf8').split('\n').filter(l => l.trim()).map(l => JSON.parse(l));
const skips: Record<string, string> = JSON.parse(readFileSync(dir + 'skip/typescript.json', 'utf8'));

it('the skip list names only real cases or areas', () => {
  const ids = new Set(Object.values(areas).flat().map(c => c.id));
  expect(Object.keys(skips).filter(k => !ids.has(k) && !(k in areas))).toEqual([]);
});

for (const [area, cases] of Object.entries(areas)) {
  describe(area, () => {
    for (const c of cases) {
      const name = `${c.id} ${c.script.slice(0, 60).replace(/\n/g, '\\n')}`;
      const reason = skips[c.id] ?? skips[area];
      if (reason) { it.skip(`${name} [skipped: ${reason}]`, () => {}); continue; }
      it(name, async () => {
        const sh = new Shell();
        await sh.run('mkdir -p /tmp/w && cd /tmp/w');
        if (c.setup != null) await sh.run(c.setup);
        const r = await sh.run(c.script);
        if (c.toolOutput != null) return expect(r.toToolOutput()).toBe(c.toolOutput);
        expect(r.stdout).toBe(c.stdout);
        if (typeof c.exitCode === 'number') expect(r.exitCode, `stderr: ${r.stderr}`).toBe(c.exitCode);
        if (typeof c.quiet === 'boolean') expect(r.stderr.length === 0, `bash ${c.quiet ? 'wrote nothing to' : 'wrote to'} stderr; this shell wrote: ${r.stderr}`).toBe(c.quiet);
      }, 30_000);
    }
  });
}
