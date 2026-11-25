import { describe, expect, it } from 'vitest';
import { Shell } from '../src/index.js';

const start = Date.UTC(2026, 0, 2, 3, 4, 5);
const make = (randomSeed = 7) => new Shell({ startTime: start, randomSeed });

const volatile = `date; echo $RANDOM $RANDOM $EPOCHSECONDS; mktemp; mktemp -d; touch f; ls -l f /usr/bin/ls
sleep 90; echo $SECONDS; uptime; jq -n now; printf '%(%F %T)T\\n' -1; find . -maxdepth 1 -name f -mmin -2
time sleep 2`;

describe('deterministic runs', () => {
  it('gives the same output for the same options', async () => {
    const a = await make().run(volatile), b = await make().run(volatile);
    expect(a.exitCode).toBe(0);
    expect(b.stdout).toBe(a.stdout);
    expect(b.stderr).toBe(a.stderr);
    expect(a.stdout.startsWith('Fri Jan  2 03:04:05 UTC 2026\n')).toBe(true);
    expect(a.stdout).toContain('2026-01-02 03:05:35');
    expect(a.stdout).toContain('\n90\n');
    expect(a.stderr).toContain('real\t0m2.000s');
    expect(a.stdout).toContain('./f');
    expect(a.stdout).toContain('Aug 13 09:13 /usr/bin/ls');
  });

  it('differs between seeds', async () => {
    const a = await make(1).run('echo $RANDOM $RANDOM; mktemp');
    const b = await make(2).run('echo $RANDOM $RANDOM; mktemp');
    expect(a.stdout).not.toBe(b.stdout);
  });

  it('reproduces the C# values', async () => {
    const sh = make(7);
    expect((await sh.run('echo $RANDOM $RANDOM')).stdout).toBe('3543 26140\n');
    expect((await sh.run('mktemp')).stdout).toBe('/tmp/tmp.E7eLYshlb4\n');
    expect((await sh.run('mktemp -d')).stdout).toBe('/tmp/tmp.ymAett9wPp\n');
  });

  it('sleeps without waiting', async () => {
    const t0 = Date.now();
    const r = await make().run('sleep 3600; date -u +%H:%M:%S; sleep 1d; date +%F');
    expect(Date.now() - t0).toBeLessThan(5000);
    expect(r.stdout).toBe('04:04:05\n2026-01-03\n');
  });

  it('fires timeouts in virtual time', async () => {
    const r = await make().run('timeout 5 sleep 10; echo $?; date +%T; timeout 10 sleep 1; echo $?; date +%T; timeout 3 timeout 8 sleep 20; echo $?; date +%T');
    expect(r.stdout).toBe('124\n03:04:10\n0\n03:04:11\n124\n03:04:14\n');
  });

  it('gives files virtual times', async () => {
    const sh = make();
    await sh.run('echo a > a; sleep 60; echo b > b');
    expect(sh.fs.stat('/home/agent/a').modified).toBe(start);
    expect(sh.fs.stat('/home/agent/b').modified).toBe(start + 60_000);
    expect((await sh.run('ls -t')).stdout).toBe('b\na\n');
  });

  it('reseeds when RANDOM is assigned', async () => {
    const r = await new Shell().run('RANDOM=5; a="$RANDOM $RANDOM"; RANDOM=5; b="$RANDOM $RANDOM"; [ "$a" = "$b" ] && echo same');
    expect(r.stdout).toBe('same\n');
  });

  it('is off by default', async () => {
    const sh = new Shell();
    expect((await sh.run('date +%Y')).stdout).toBe(new Date().getUTCFullYear() + '\n');
    expect(sh.virtualNow).toBeUndefined();
    expect(sh.randomState).toBeUndefined();
  });

  it('takes state only when the shell has the option', () => {
    const plain = new Shell(), det = make();
    plain.virtualNow = 5; plain.randomState = 5n;
    expect(plain.virtualNow).toBeUndefined();
    expect(plain.randomState).toBeUndefined();
    det.virtualNow = start + 1000; det.randomState = 42n;
    expect(det.virtualNow).toBe(start + 1000);
    expect(det.randomState).toBe(42n);
  });
});
