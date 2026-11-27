import { execFile, execFileSync } from 'node:child_process';
import { mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { expect, test } from 'vitest';

// the npm package carries jq's help text (spec.generated.ts), so it ships the notice: a copy, since npm packs only this folder
test('THIRD-PARTY-NOTICES.md is the repository copy', () => {
  expect(readFileSync(new URL('../THIRD-PARTY-NOTICES.md', import.meta.url), 'utf8'))
    .toBe(readFileSync(new URL('../../THIRD-PARTY-NOTICES.md', import.meta.url), 'utf8'));
  expect(JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')).files).toContain('THIRD-PARTY-NOTICES.md');
});

test('LICENSE is the repository copy and the package builds before packing', () => {
  expect(readFileSync(new URL('../LICENSE', import.meta.url), 'utf8'))
    .toBe(readFileSync(new URL('../../LICENSE', import.meta.url), 'utf8'));
  const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
  expect(pkg.files).toContain('LICENSE');
  expect(pkg.scripts.prepack).toBe('npm run build');
});

// every built module imports alone, as the first import of a fresh process: this is what finds import cycles
// (docs/architecture.md §3.3 and §9); the package entry points all go through index.js, but tests and bundlers don't
test('each built module imports alone', async () => {
  const root = fileURLToPath(new URL('..', import.meta.url));
  const out = mkdtempSync(join(root, 'node_modules', '.import-alone-'));
  try {
    execFileSync(process.execPath, [join(root, 'node_modules/typescript/bin/tsc'), '-p', join(root, 'tsconfig.json'), '--outDir', out, '--declaration', 'false']);
    const modules = readdirSync(out, { recursive: true, encoding: 'utf8' }).filter(f => f.endsWith('.js'));
    expect(modules.length).toBeGreaterThan(60);
    const failed = (await Promise.all(modules.map(m => new Promise<string | null>(done =>
      execFile(process.execPath, ['--input-type=module', '-e', `await import(${JSON.stringify(pathToFileURL(join(out, m)).href)})`],
        (err, _stdout, stderr) => done(err ? `${m}: ${stderr.trim().split('\n').find(l => /Error/.test(l)) ?? err.message}` : null)))))).filter(Boolean);
    expect(failed).toEqual([]);
  } finally {
    rmSync(out, { recursive: true, force: true });
  }
}, 60_000);
