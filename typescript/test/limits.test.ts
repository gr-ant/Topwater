// Ported from tests/Topwater.Tests/LimitTests.cs, SecondReviewTests.cs (runaway, deep nesting) and ShellLanguageTests.cs:
// every exhaustion recipe ends in bounded time with its message, and the shell stays usable.
import { describe, it, expect } from 'vitest';
import { Shell, type ShellResult } from '../src/index.js';
import limitData from './data/LimitTests.json' with { type: 'json' };
import secondData from './data/SecondReviewTests.json' with { type: 'json' };
import languageData from './data/ShellLanguageTests.json' with { type: 'json' };

const quick = (seconds = 1) => new Shell({ maxCallDurationMs: seconds * 1000 });
const tail = 'Running it again will hit the same limit. Files and variables changed before this point are kept.]';

async function hitsTimeLimit(sh: Shell, cmd: string, seconds = 1): Promise<ShellResult> {
  const start = Date.now();
  const r = await sh.run(cmd);
  expect(Date.now() - start).toBeLessThan((seconds + 8) * 1000);
  expect(r.exitCode).toBe(124);
  expect(r.toToolOutput().endsWith(`[stopped: this call exceeded the ${seconds} s time limit; split the work into shorter calls. ${tail}`)).toBe(true);
  expect((await sh.run('echo ok')).stdout).toBe('ok\n');
  return r;
}

async function hitsStringLimit(sh: Shell, cmd: string, message: string): Promise<ShellResult> {
  const start = Date.now();
  const r = await sh.run(cmd);
  expect(Date.now() - start).toBeLessThan(20_000);
  expect(r.exitCode, r.stderr).toBe(1);
  expect(r.stderr).toContain('bash: ' + message);
  const last = r.toToolOutput().split('\n').pop()!;
  expect(last.startsWith('[stopped: ' + message)).toBe(true);
  expect(last.endsWith(tail)).toBe(true);
  expect((await sh.run('echo ok')).stdout).toBe('ok\n');
  return r;
}

describe('time limit', () => {
  it('sleep forever stops at the call limit and keeps earlier output', async () => {
    const sh = quick();
    const r = await hitsTimeLimit(sh, 'x=kept; echo before; sleep 99999 2>/dev/null');
    expect(r.toToolOutput().startsWith('before\n')).toBe(true);
    expect((await sh.run('echo $x')).stdout).toBe('kept\n');
  });
  it('prints the configured duration', () => hitsTimeLimit(quick(1.5), 'sleep 60', 1.5));
  it('stops a shell loop of awk runs', () => hitsTimeLimit(quick(), "while :; do awk 'BEGIN { while (1) x++ }' 2>/dev/null; done"));
  it('a catastrophically backtracking regex finishes quickly (V8 falls back to its linear engine)', async () => {
    const start = Date.now();
    const r = await quick().run("x=$(printf 'a%.0s' {1..40})b; for i in {1..200}; do echo $x; done > s.txt; grep -cE '(a|aa)+$' s.txt; [[ $x =~ ^(a+)+$ ]] || echo no");
    expect(Date.now() - start).toBeLessThan(3000);
    expect(r.stdout).toBe('0\nno\n');
  });
  for (const [loop] of limitData['LimitTests.TimeoutInterruptsABuiltInLoop'] as string[][]) {
    it(`timeout interrupts ${loop}`, async () => {
      const start = Date.now();
      const r = await new Shell().run(`timeout 0.1 ${loop}; echo $?`);
      expect(Date.now() - start).toBeLessThan(6000);
      expect(r.stdout).toBe('124\n');
    });
  }
  it('the trailer survives the output caps', async () => {
    const text = (await quick().run('seq 1 20000; seq 1 3000 >&2; sleep 99')).toToolOutput();
    expect(text).toContain('[output truncated');
    expect(text.endsWith('Files and variables changed before this point are kept.]')).toBe(true);
  });
});

describe('loops', () => {
  it('the loop limit counts body runs', async () => {
    const sh = new Shell({ maxLoopIterations: 3 });
    expect((await sh.run("printf '1\\n2\\n3\\n' | while read -r l; do echo $l; done")).toToolOutput()).toBe('1\n2\n3\n');
    expect((await sh.run('while true; do :; done')).stderr).toContain('loop aborted after 3 iterations');
  });
  it('names which loop limit tripped', async () => {
    const sh = new Shell({ maxLoopIterations: 10 });
    const one = await sh.run('while :; do :; done 2>/dev/null');
    expect(one.stderr).toContain('loop aborted after 10 iterations (the limit for one loop)');
    expect(one.toToolOutput().split('\n').pop()!.startsWith('[stopped: loop aborted after 10 iterations')).toBe(true);
    const all = await sh.run('for i in {1..10}; do for j in {1..10}; do :; done; done');
    expect(all.stderr).toContain('loop aborted after 100 iterations in this call (the limit for all loops in one call)');
  });
});

describe('filesystem cap', () => {
  const smallDisk = () => new Shell({ maxFileSystemBytes: 1 << 20, maxLoopIterations: 5000 });
  const kilobyte = "x=$(printf '%01023d' 0); ";
  it('an append loop stops at the cap and rm frees space', async () => {
    const sh = smallDisk();
    const r = await sh.run(kilobyte + 'for i in {1..2000}; do echo $x >> f.txt; done; echo done');
    expect(r.stderr).toContain('bash: line 1: /home/agent/f.txt: No space left on device');
    expect(r.stdout.endsWith('done\n')).toBe(true);
    expect(sh.fs.usedBytes).toBeLessThanOrEqual(1 << 20);
    expect((await sh.run(kilobyte + 'echo $x >> f.txt; echo $?')).stdout).toBe('1\n');
    expect((await sh.run('df -h .')).stdout).toMatch(/\/dev\/sda1\s+1\.0M\s+1\.0M\s+\d+\s+100% \//);
    expect((await sh.run('rm f.txt; echo hi > g.txt; echo $?')).stdout).toBe('0\n');
    expect((await sh.run('df -h .')).stdout).toMatch(/\/dev\/sda1\s+1\.0M\s+\S+K\s+/);
  });
  it('an unbounded append loop ends at the loop limit', async () => {
    const r = await smallDisk().run(kilobyte + 'while :; do echo $x >> f.txt; done');
    expect(r.stderr).toContain('No space left on device');
    expect(r.toToolOutput()).toContain('loop aborted after 5000 iterations');
  });
  it('cp -r stops at the cap', async () => {
    const sh = smallDisk();
    const r = await sh.run(kilobyte + 'mkdir d; for i in {1..400}; do echo $x > d/$i; done; cp -r d d2; echo $?; cp -r d d3');
    expect(r.stdout).toBe('0\n');
    expect(r.stderr).toContain("cp: error writing 'd3': No space left on device");
    expect(r.exitCode).toBe(1);
    expect(sh.fs.exists('/home/agent/d3')).toBe(false);
  });
  it('host writes are counted but never refused', async () => {
    const sh = smallDisk();
    sh.fs.writeText('/data.txt', 'x'.repeat(2 << 20));
    expect(sh.fs.usedBytes).toBeGreaterThan(2 << 20);
    const r = await sh.run('df .; echo y > y.txt');
    expect(r.stdout).toMatch(/\/dev\/sda1\s+1024\s+20\d\d\s+0\s+/);
    expect(r.stderr).toContain('No space left on device');
  });
});

describe('string limit', () => {
  const smallStrings = () => new Shell({ maxStringBytes: 1 << 20 });
  it('bash string doubling stops at the default limit', () =>
    hitsStringLimit(new Shell(), 'x=a; while :; do x=$x$x; done 2>/dev/null', 'variables in total too large (limit 32 MB)'));
  for (const [cmd, message] of limitData['LimitTests.StringRecipesStopAtTheConfiguredLimit'] as string[][])
    it(`${cmd.slice(0, 70)}`, () => hitsStringLimit(smallStrings(), cmd, message), 30_000);
  it('a huge printf width is bounded', async () =>
    expect((await smallStrings().run("printf '%0999999999d' 1 | wc -c")).stdout).toBe('10000\n'));
  it('redirects are bounded by the disk, not the string limit', async () => {
    const r = await smallStrings().run("awk 'BEGIN { for (i = 0; i < 3000; i++) printf \"%01000d\\n\", 0 }' > f; cat f f > g; wc -c < g");
    expect(r.stdout).toBe('6006000\n');
    expect(r.exitCode).toBe(0);
  });
  it('variables can be rewritten near the limit', async () => {
    const sh = smallStrings();
    await sh.run("kb() { awk -v n=$1 'BEGIN { for (i = 0; i < n; i++) printf \"%01000d\", 0 }'; }");
    const r = await sh.run('x=$(kb 600); for i in 1 2 3; do x=${x}y; done; echo ${#x}; unset x; y=$(kb 900); echo ${#y}');
    expect(r.stdout).toBe('600003\n900000\n');
  });
});

describe('runaway constructs', () => {
  for (const [cmd] of secondData['RunawayTests.StopsInsteadOfHanging'] as string[][]) {
    it(`stops: ${cmd}`, async () => {
      const r = await new Shell().run(cmd);
      expect(r.exitCode).not.toBe(0);
      expect(r.stdout).not.toContain('survived');
      expect(r.stderr).not.toBe('');
    }, 60_000);
  }
  for (const [cmd] of languageData['ShellLanguageTests.RunawayConstructsEnd'] as string[][]) {
    it(`ends: ${cmd}`, async () => expect((await new Shell().run(cmd)).stderr).not.toContain('internal error'), 60_000);
  }
  for (const [open, close, depth] of secondData['DeepNestingTests.DeepNestingIsAnErrorNotACrash'] as [string, string, number][]) {
    it(`deep nesting ${open}x${depth}`, async () => {
      const body = depth === 1 ? '1' : 'true';
      const r = await new Shell().run(open.repeat(depth) + body + close.repeat(depth));
      if (depth === 1) expect(r.stdout).toBe('-1\n');
      else expect(r.stderr).not.toBe('');
    }, 60_000);
  }
  it('long output keeps the error and exit code', async () => {
    const text = (await new Shell().run('seq 1 20000; nosuchcmd')).toToolOutput();
    expect(text).toContain('command not found');
    expect(text.endsWith('[exit code 127]')).toBe(true);
  });
  it('jq def recursion is bounded', async () => {
    // a def that never stops recursing ends in an error, not a hang or a crash; 100 levels deep still works
    const r = await new Shell().run("jq -n 'def f: f; f'; echo $?; jq -n 'def r: if . < 99 then .+1 | r else . end; 0 | r'");
    expect(r.stdout).toBe('5\n99\n');
    expect(r.stderr).toContain('recursion too deep');
  });
});
