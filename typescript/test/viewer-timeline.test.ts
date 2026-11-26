// The recording viewer's timelines (browser/timeline.ts): machines at any call, branches typed after rewinding, comparing
// two moments, and a branch saved as its own .topwater file.
import { describe, expect, it } from 'vitest';
import { readFileSync, writeFileSync } from 'node:fs';
import { Recording, Shell, startRecording } from '../src/index.js';
import { Timelines, diffMachines } from '../browser/timeline.js';

const deterministic = { startTime: Date.UTC(2026, 2, 4, 5, 6, 7), randomSeed: 42 };

async function recorded(...commands: string[]) {
  const sh = new Shell(deterministic);
  const { recording, stop } = startRecording(sh);
  for (const c of commands) await sh.run(c);
  stop();
  return Recording.load(await recording.save()); // as the viewer gets it: only the last call has a checkpoint
}

describe('viewer timelines', () => {
  it('rebuilds any call and compares two moments with symlinks and modes', async () => {
    const t = new Timelines(await recorded('echo one > a.txt; mkdir d', 'ln -s a.txt link; echo two >> a.txt', 'chmod 755 a.txt; rm -r d; ln -sfn d link'));
    expect((await t.stateAt(0, 0)).entries.some(e => e.path === '/home/agent/a.txt')).toBe(true);
    const step2 = await t.compare({ b: 0, i: 0 }, { b: 0, i: 1 });
    expect(step2.map(c => [c.path, c.kind, c.detail])).toEqual([
      ['/home/agent/a.txt', 'modified', undefined],
      ['/home/agent/link', 'added', '→ a.txt'],
    ]);
    expect(step2[1].isLink).toBe(true);
    const step3 = await t.compare({ b: 0, i: 1 }, { b: 0, i: 2 });
    expect(step3).toEqual(expect.arrayContaining([
      expect.objectContaining({ path: '/home/agent/a.txt', kind: 'permissions', detail: '0644 agent → 0755 agent' }),
      expect.objectContaining({ path: '/home/agent/d', kind: 'removed', isDirectory: true }),
      expect.objectContaining({ path: '/home/agent/link', kind: 'modified', detail: '→ a.txt became → d' }),
    ]));
    // calls re-run here matched the recording
    expect([...t.reran.values()].every(r => r.matches)).toBe(true);
  });

  it('branches from any call, keeps the recording as it was, and saves a branch that loads as a recording', async () => {
    const rec = await recorded('echo one > a.txt', 'echo two > b.txt', 'rm a.txt');
    const t = new Timelines(rec);
    const b1 = t.fork(0, 0);
    expect(t.length(b1)).toBe(1);
    const { call } = await t.run(b1, 'X=7; echo three > c.txt; cat a.txt');
    expect(call).toMatchObject({ index: 1, stdout: 'one\n', exitCode: 0 });
    expect(call.files).toContain('/home/agent/c.txt');
    expect((await t.run(b1, 'echo $X')).call.stdout).toBe('7\n'); // the same shell: variables carry on
    expect([t.length(0), t.length(b1), t.owner(b1, 0), t.owner(b1, 1)]).toEqual([3, 3, 0, 1]);
    await expect(t.run(0, 'true')).rejects.toThrow();

    // a branch of the branch, and comparing across them
    const b2 = t.fork(b1, 1);
    await t.run(b2, 'rm c.txt');
    expect(t.call(b2, 0)).toBe(rec.calls[0]);
    const across = await t.compare({ b: 0, i: 2 }, { b: b1, i: 2 });
    expect(across.map(c => `${c.kind} ${c.path}`)).toEqual(expect.arrayContaining(['added /home/agent/a.txt', 'removed /home/agent/b.txt', 'added /home/agent/c.txt']));
    // a branch from a turn the parent inherited is filed under the branch that holds that turn
    expect(t.branches[t.fork(b2, 0)].parent).toBe(0);
    expect(t.branches[t.fork(b2, -1)].parent).toBe(0);

    const saved = await Recording.load(await t.export(b2, 'test.topwater'));
    expect(saved.calls.map(c => c.command)).toEqual(['echo one > a.txt', 'X=7; echo three > c.txt; cat a.txt', 'rm c.txt']);
    expect(saved.calls.map(c => c.index)).toEqual([0, 1, 2]);
    expect(saved.metadata).toMatchObject({ forkedFrom: 'test.topwater', forkedAfterCall: '2' });
    expect(saved.calls.map(c => !!c.snapshot)).toEqual([false, true, true]); // the fork and the end
    expect((await (await saved.shellAt(2)).run('ls; echo $X')).stdout).toBe('a.txt\n7\n');
    expect((await saved.replay()).matches).toBe(true);
    // the original recording is untouched
    expect(rec.calls).toHaveLength(3);
  });

  it('lists what the machines differ by, not what the files lists name: a host write, a short list, no list', async () => {
    const sh = new Shell(deterministic);
    const { recording, stop } = startRecording(sh);
    await sh.run('echo 1 > a');
    sh.fs.as(sh.options.user).writeText('/home/agent/upload.csv', 'x,y\n'); // the host hands the agent a file between calls
    await sh.run('wc -l upload.csv; echo 2 > b');
    await sh.run('echo 3 > c');
    stop();
    const t = new Timelines(await Recording.load(await recording.save()));
    const paths = async (i: number) => (await t.compare({ b: 0, i: i - 1 }, { b: 0, i })).map(c => c.path);
    expect(await paths(2)).toEqual(expect.arrayContaining(['/home/agent/c', '/home/agent/upload.csv'])); // turn 2 was re-run here without it; turn 3 is the checkpoint
    // the same recording with call 2 listing only b and call 3 listing nothing
    const short = await Recording.load(await recording.save());
    short.calls[1].files = ['/home/agent/b'];
    short.calls[2].files = undefined;
    const u = new Timelines(short);
    expect((await u.compare({ b: 0, i: 1 }, { b: 0, i: 2 })).map(c => c.path)).toEqual(expect.arrayContaining(['/home/agent/c', '/home/agent/upload.csv']));
    const [x, y] = [await u.stateAt(0, -1), await u.stateAt(0, 2)];
    expect(await u.compare({ b: 0, i: -1 }, { b: 0, i: 2 })).toEqual(diffMachines(x.entries, y.entries));
  });

  it('reads a hard link\'s content through its first name on a checkpoint', async () => {
    const t = new Timelines(await recorded('echo x > a; ln a b', 'chmod 600 b'));
    const end = await t.stateAt(0, 1);
    expect(end.entries.find(e => e.path === '/home/agent/b')).toMatchObject({ hardLink: '/home/agent/a', text: null, data: null });
    const changes = await t.compare({ b: 0, i: 0 }, { b: 0, i: 1 });
    expect(changes.find(c => c.path === '/home/agent/b')).toMatchObject({ kind: 'permissions' });
  });

  it('rebuilds branches given back after a restart by re-running them', async () => {
    const rec = await recorded('echo one > a.txt');
    const t = new Timelines(rec), b = t.fork(0, 0);
    await t.run(b, 'echo two >> a.txt');
    const again = new Timelines(await recorded('echo one > a.txt'));
    again.branches.push({ ...t.branches[b], calls: t.branches[b].calls.map(c => ({ ...c, snapshot: undefined })) });
    expect((await again.stateAt(b, 1)).entries.find(e => e.path === '/home/agent/a.txt')?.text).toBe('one\ntwo\n');
    expect(again.reran.get(again.key(b, 1))?.matches).toBe(true);
  });
});

// branch-from-viewer.topwater: a branch the viewer saves, for the C# and Python suites to open. Written again with
// TOPWATER_WRITE_FIXTURES=1 npx vitest run test/viewer-timeline.test.ts
describe('branch-from-viewer.topwater', () => {
  const dir = new URL('../../tests/Topwater.Tests/recordings/', import.meta.url);
  it.runIf(process.env.TOPWATER_WRITE_FIXTURES)('writes it', async () => {
    const t = new Timelines(await Recording.load(new Uint8Array(readFileSync(new URL('from-typescript.topwater', dir)))));
    const b = t.fork(0, 1);
    await t.run(b, 'greet branch > branch.txt; cat latest');
    await t.run(b, 'chmod 700 branch.txt; ls');
    writeFileSync(new URL('branch-from-viewer.topwater', dir), await t.export(b, 'from-typescript.topwater'));
  });
  it('loads', async () => {
    const rec = await Recording.load(new Uint8Array(readFileSync(new URL('branch-from-viewer.topwater', dir))));
    expect(rec.calls.map(c => c.command).slice(2)).toEqual(['greet branch > branch.txt; cat latest', 'chmod 700 branch.txt; ls']);
    expect(rec.calls[2].stdout).toBe('buy milk\nfix bug\n');
    expect(rec.metadata).toMatchObject({ task: 'cross-port fixture', forkedFrom: 'from-typescript.topwater', forkedAfterCall: '2' });
    expect((await (await rec.shellAt(3)).run('cat /home/agent/work/branch.txt; stat -c %a /home/agent/work/branch.txt')).stdout).toBe('hi branch\n700\n');
    expect((await rec.replay()).matches).toBe(true);
  });
});
