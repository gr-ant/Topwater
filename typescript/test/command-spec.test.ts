// Drift checks against spec/commands/*.json (read here directly, so a stale spec.generated.ts fails too): the built-ins
// are exactly the spec's, and their help listing, --help, --version, options and usage errors come from it.
// The C# and Python ports run the same checks (CommandSpecTests.cs, test_command_spec.py).
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { Shell } from '../src/index.js';

type Option = { short?: string; long?: string[]; arg?: string; ports?: string[] };
type Spec = {
  name: string; summary: string; parser: string; ports?: string[]; help?: string[]; version?: string; standardOptions?: boolean;
  shellBuiltin?: boolean; reportsAs?: string; usageExitCode?: number; tryHelp?: string[]; options?: Option[];
};
const dir = new URL('../../spec/', import.meta.url).pathname;
const defaults = JSON.parse(readFileSync(dir + 'shell.json', 'utf8')).defaults as { version: string; help: string[]; tryHelp: string[] };
const specs = new Map<string, Spec>(readdirSync(dir + 'commands').filter(f => f.endsWith('.json')).sort()
  .map(f => JSON.parse(readFileSync(dir + 'commands/' + f, 'utf8')) as Spec)
  .filter(c => (c.ports ?? ['ts']).includes('ts')).map(c => [c.name, c]));

const lines = (ls: string[], name: string, summary = '') => ls.map(l => l.replaceAll('{name}', name).replaceAll('{summary}', summary) + '\n').join('');
const q = (s: string) => "'" + s.replaceAll("'", "'\\''") + "'";
function* options(c: Spec): Generator<[arg: string, valued: boolean]> {
  for (const o of c.options ?? []) {
    if (!(o.ports ?? ['ts']).includes('ts')) continue;
    if (o.short) yield ['-' + o.short, o.arg !== undefined];
    for (const l of o.long ?? []) yield ['--' + l, o.arg !== undefined];
  }
}

it('the built-ins are exactly the spec commands', () => {
  const registered = new Set(new Shell().commands.keys());
  expect([...specs.keys()].filter(n => !registered.has(n))).toEqual([]);  // in the spec, not registered
  expect([...registered].filter(n => !specs.has(n))).toEqual([]);        // registered, not in the spec (add it, or mark the port missing)
});

describe('help and version come from the spec', () => {
  for (const [name, c] of specs) it(name, async () => {
    const cmd = new Shell().commands.get(name)!;
    const help = c.help ? lines(c.help, name) : undefined;
    expect(cmd.description).toBe(c.summary);
    expect(cmd.usage).toBe(help);
    if (c.standardOptions === false) return;
    const sh = new Shell();
    expect((await sh.run(`${q(name)} --help`)).stdout).toBe(help ?? lines(defaults.help, name, c.summary));
    expect((await sh.run(`${q(name)} --version`)).stdout).toBe((c.version ?? defaults.version.replaceAll('{name}', name)) + '\n');
  });
});

describe('options and usage errors come from the spec', () => {
  for (const [name, c] of specs) {
    if (c.parser !== 'args') continue;
    it(name, async () => {
      for (const [arg, valued] of options(c)) {
        const r = await new Shell().run(`cd /tmp && ${q(name)} ${arg}${valued ? ' 1' : ''} </dev/null`);
        expect(r.stderr, arg).not.toMatch(/invalid option|unrecognized option/);
      }
      const bad = await new Shell().run(`${q(name)} --no-such-option </dev/null`);
      if (c.shellBuiltin) return expect(bad.stderr).toContain("unrecognized option '--no-such-option'");
      const shown = c.reportsAs ?? name;
      expect(bad.stderr).toBe(`${shown}: unrecognized option '--no-such-option'\n` + lines(c.tryHelp ?? defaults.tryHelp, shown));
      expect(bad.exitCode).toBe(c.usageExitCode ?? 1);
    }, 30_000);
  }
});

describe('options outside the spec are rejected', () => {
  for (const [name, c] of specs) {
    if (c.parser !== 'args') continue;
    it(name, async () => {
      const accepted = new Set([...options(c)].map(([arg]) => arg));
      const candidates = new Set([...'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ'].map(ch => '-' + ch));
      for (const o of c.options ?? []) {  // also the ones listed for other ports only
        if (o.short) candidates.add('-' + o.short);
        for (const l of o.long ?? []) candidates.add('--' + l);
      }
      const sh = new Shell();
      for (const arg of [...candidates].filter(a => !accepted.has(a)).sort()) {
        const r = await sh.run(`cd /tmp && ${q(name)} ${arg} </dev/null`);
        expect(r.stderr, `${name} ${arg}`).toMatch(/invalid option|unrecognized option/);
      }
    }, 60_000);
  }
});
