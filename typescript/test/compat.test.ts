// The recorded tables from the C# tests (test/data, extracted by scripts/extract-tests.mjs): each row is what real
// bash / GNU tools printed. Run them against this port with the same checks the C# theories use.
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { Shell } from '../src/index.js';

type Row = (string | number | boolean | null)[];
const dataDir = new URL('./data/', import.meta.url).pathname;
const tables: Record<string, Record<string, Row[]>> = {};
for (const f of readdirSync(dataDir).filter(f => f.endsWith('.json'))) tables[f.replace(/\.json$/, '')] = JSON.parse(readFileSync(dataDir + f, 'utf8'));

const REVIEW_SETUP = readFileSync(new URL('./data/review-setup.sh', import.meta.url), 'utf8');

/** How each theory checks its rows (mirrors the C# test methods). */
const checks: Record<string, (row: Row) => Promise<void>> = {
  // BashCompat.Check: fresh shell in an empty directory; stdout, exit code, and whether stderr was written
  compat: async ([script, stdout, exitCode, quiet]) => {
    const sh = new Shell();
    await sh.run('mkdir -p /tmp/w && cd /tmp/w');
    const r = await sh.run(script as string);
    expect(r.stdout).toBe(stdout);
    expect(r.exitCode).toBe(exitCode);
    expect(r.stderr.length === 0, `bash ${quiet ? 'wrote nothing to' : 'wrote to'} stderr; this shell wrote: ${r.stderr}`).toBe(quiet);
  },
  stdoutQuiet: async ([cmd, expected]) => {
    const r = await new Shell().run(cmd as string);
    expect(r.stderr).toBe('');
    expect(r.stdout).toBe(expected);
  },
  stdoutOk: async ([cmd, expected]) => {
    const r = await new Shell().run(cmd as string);
    expect(r.exitCode, r.stderr).toBe(0);
    expect(r.stdout).toBe(expected);
  },
  stdout: async ([cmd, expected]) => {
    const r = await new Shell().run(cmd as string);
    expect(r.stdout).toBe(expected);
  },
  toolOutput: async ([cmd, expected]) => {
    const sh = new Shell();
    await sh.run(REVIEW_SETUP);
    expect((await sh.run(cmd as string)).toToolOutput()).toBe(expected);
  },
  stops: async ([cmd]) => {
    const r = await new Shell({ maxCallDurationMs: 20_000 }).run(cmd as string);
    expect(r.stdout).not.toContain('survived\nsurvived'); // it ended, one way or another
  },
};

const plan: Record<string, string> = {
  'ArrayTests.MatchesBash': 'stdoutQuiet',
  'ArrayEdgeTests.MatchesBash': 'stdoutQuiet',
  'AgentFeatureTests.ShellLanguage': 'stdoutOk',
  'ShellTests.Syntax': 'stdoutOk',
  'SecondReviewTests.MatchesBash': 'stdout',
  'ReviewFixTests.MatchesBash': 'toolOutput',
};

for (const [file, theories] of Object.entries(tables)) {
  for (const [theory, rows] of Object.entries(theories)) {
    const mode = file.startsWith('BashCompat') ? 'compat' : plan[theory];
    if (!mode) continue;
    describe(`${file} ${theory}`, () => {
      rows.forEach((row, i) => it(`#${i} ${String(row[0]).slice(0, 80).replace(/\n/g, '\\n')}`, () => checks[mode](row), 30_000));
    });
  }
}
