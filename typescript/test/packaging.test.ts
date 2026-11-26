import { readFileSync } from 'node:fs';
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
