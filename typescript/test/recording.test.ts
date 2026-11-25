import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { Shell, ShellSnapshot, SnapshotError, Recording, startRecording } from '../src/index.js';
import { readZip, writeZip } from '../src/commands/zipfile.js';
import { inflateRaw } from '../src/inflate.js';

const fromCSharp = readFileSync(new URL('../../tests/Topwater.Tests/recordings/basics.jsonl', import.meta.url), 'utf8');
const deterministic = { startTime: Date.UTC(2026, 2, 4, 5, 6, 7), randomSeed: 42 };

async function richShell() {
  const sh = new Shell(deterministic);
  sh.fs.writeBytes('/home/agent/blob.bin', new Uint8Array([0, 1, 2, 0x7f, 0x80, 0xfe, 0xff, 10]));
  sh.fs.mkdir('/data', true);
  sh.fs.writeText('/data/report.txt', 'héllo wörld\nline 2\n');
  sh.fs.setOwner('/data', 'bob', true);
  await sh.run(`mkdir -p proj/src && cd proj
    printf 'hello\\n' > a.txt; chmod 600 a.txt
    declare -i n=5; export EXP=exported; plain='multi
line'
    arr=(one 'two words' three); unset 'arr[1]'; arr+=(four); declare -A m=([k]=v [x]=y)
    greet() { echo "hi $1"; }; alias ll='ls -l'; echo $RANDOM >/dev/null`);
  return sh;
}

const probe = 'pwd; echo "$n $EXP $plain ${#arr[@]} ${arr[3]} ${m[k]}"; greet you; type ll; ls -l a.txt; cat /data/report.txt; od -An -tx1 ~/blob.bin; echo $RANDOM; date +%T';

describe('snapshots in the C# format', () => {
  it('round-trips files and the session through save and load', async () => {
    const sh = await richShell();
    const snap = ShellSnapshot.capture(sh);
    const before = await sh.run(probe);
    const bytes = await snap.save();
    const names = readZip(bytes).map(e => e.name);
    expect(names).toContain('manifest.json');
    expect(names.some(n => /^blobs\/[0-9a-f]{64}$/.test(n))).toBe(true);
    const manifest = JSON.parse(new TextDecoder().decode(inflateRaw(readZip(bytes).find(e => e.name === 'manifest.json')!.data)));
    expect(manifest).toMatchObject({ format: 'topwater-snapshot', version: 1, user: 'agent', cwd: '/home/agent/proj' });
    expect(manifest.functions[0]).toMatchObject({ name: 'greet' });
    expect(manifest.files.find((f: any) => f.path === '/home/agent/blob.bin')).toMatchObject({ binary: true, size: 8, mode: '0644' });

    // run something after the snapshot, restore, and the same probe gives the same answer (including $RANDOM and the clock)
    await sh.run('rm -rf ~/proj; unset arr; greet() { echo changed; }; cd /');
    const loaded = await ShellSnapshot.load(bytes);
    const fresh = new Shell(deterministic);
    loaded.restore(fresh);
    snap.restore(sh);
    const after = await fresh.run(probe);
    expect(after.stdout).toBe(before.stdout);
    expect((await sh.run(probe)).stdout).toBe(before.stdout);
    expect(fresh.fs.stat('/data/report.txt').owner).toBe('bob');
  });

  it('refuses malformed and hostile snapshots', async () => {
    const good = await ShellSnapshot.capture(new Shell()).save();
    const entries = readZip(good);
    const manifestEntry = entries.find(e => e.name === 'manifest.json')!;
    const manifest = JSON.parse(new TextDecoder().decode(inflateRaw(manifestEntry.data)));
    const rebuild = (m: unknown) => {
      const raw = new TextEncoder().encode(JSON.stringify(m));
      return writeZip(entries.map(e => e === manifestEntry ? { ...e, method: 0, usize: raw.length, data: raw, crc: 0 } : e));
    };
    await expect(ShellSnapshot.load(rebuild({ ...manifest, version: 99 }))).rejects.toThrow(/newer than this version/);
    await expect(ShellSnapshot.load(new Uint8Array([1, 2, 3]))).rejects.toThrow(SnapshotError);
    const withFiles = (f: (files: any[]) => any[]) => rebuild({ ...manifest, files: f(structuredClone(manifest.files)) });
    await expect(ShellSnapshot.load(withFiles(fs => [...fs, { path: '/home/../etc/x', type: 'file', mode: '0644', owner: 'agent', mtime: manifest.created, size: 0 }]))).rejects.toThrow(/bad path/);
    await expect(ShellSnapshot.load(withFiles(fs => [...fs, { path: '/no/parent', type: 'dir', mode: '0755', owner: 'agent', mtime: manifest.created }]))).rejects.toThrow(/parent/);
    await expect(ShellSnapshot.load(withFiles(fs => fs.map(f => f.blob ? { ...f, blob: 'a'.repeat(64) } : f)))).rejects.toThrow(/blob/);
    await expect(ShellSnapshot.load(withFiles(fs => fs.map(f => f.size ? { ...f, size: f.size + 1 } : f)))).rejects.toThrow(/bytes/);
    await expect(ShellSnapshot.load(rebuild({ ...manifest, user: '../../etc' }))).rejects.toThrow(/bad user/);
    await expect(ShellSnapshot.load(rebuild({ ...manifest, functions: [{ name: 'f', body: '{ :; }; rm -rf /' }] }))).rejects.toThrow(/not one function/);
    await expect(ShellSnapshot.load(good, { maxFileSystemBytes: 10 })).rejects.toThrow(/MaxFileSystemBytes/);
  });
});

describe('recordings in the C# format', () => {
  it('reads a recording made by the C# port', async () => {
    const rec = await Recording.load(fromCSharp);
    expect(rec.isDeterministic).toBe(true);
    expect(rec.calls.length).toBe(6);
    expect(rec.calls.map(c => !!c.snapshot)).toEqual([false, false, true, false, false, true]);
    expect(rec.metadata.task).toMatch(/sales per region/);
    const start = rec.createShell();
    expect((await start.run('head -2 ~/data/sales.csv')).stdout).toBe('date,region,amount\n2026-01-03,north,120.50\n');
    // the exact machine after call 2, from the C# snapshot
    const at2 = await rec.shellAt(2);
    expect((await at2.run('cat ~/report.txt; cat /tmp/tmp.*; date')).stdout).toBe('north 220.00\nsouth 80.00\neast 42.00\nhi\nWed Nov 25 09:00:00 UTC 2026\n');
  });

  it('re-executes C# calls deterministically: the same clock and random numbers', async () => {
    const rec = await Recording.load(fromCSharp);
    const seen: string[] = [];
    await rec.shellAt(3, undefined, (call, actual) => { if (call.index >= 2) { seen.push(actual.stdout); expect(actual.stdout).toBe(call.stdout); } });
    expect(seen.length).toBe(1); // call 3 only: call 2 came from its snapshot
    const fromStart: string[] = [];
    rec.calls[2] = { ...rec.calls[2], snapshot: undefined };
    await rec.shellAt(2, undefined, (call, actual) => fromStart.push(actual.stdout));
    expect(fromStart[2]).toBe(rec.calls[2].stdout); // "seed: 23745" and the mktemp name match the C# run
  });

  it('records, saves, loads and replays a TypeScript session', async () => {
    const sh = new Shell(deterministic);
    sh.fs.writeText('/home/agent/notes.txt', 'alpha\nbeta\n');
    const { recording, stop } = startRecording(sh, { metadata: { task: 'demo' }, snapshotEvery: 2 });
    await sh.run('mkdir out && grep -c a notes.txt > out/n.txt; echo $RANDOM');
    await sh.run('f() { echo "f$1"; }; x=7; sleep 5; date +%T');
    await sh.run('f 1; echo $x; cat out/n.txt; nosuch');
    stop();
    await sh.run('echo not recorded');
    expect(recording.calls.length).toBe(3);
    const text = await recording.save();
    const lines = text.trimEnd().split('\n');
    expect(lines.length).toBe(4);
    expect(JSON.parse(lines[0])).toMatchObject({ format: 'topwater-recording', version: 1, metadata: { task: 'demo' }, options: { randomSeed: 42, startTime: '2026-03-04T05:06:07.0000000Z' } });
    expect(JSON.parse(lines[2]).snapshot.embedded).toBeTruthy();
    const loaded = await Recording.load(text);
    expect((await loaded.replay()).matches).toBe(true);
    const at = await loaded.shellAt(2);
    expect((await at.run('echo $x; f 2')).stdout).toBe('7\nf2\n');
    loaded.calls[1] = { ...loaded.calls[1], stdout: 'changed\n' };
    const report = await loaded.replay();
    expect(report.differences.map(d => [d.index, d.field])).toEqual([[1, 'stdout']]);
  });

  it('refuses newer versions and side-car files it cannot open', async () => {
    await expect(Recording.load('{"format":"topwater-recording","version":2}\n')).rejects.toThrow(/newer/);
    await expect(Recording.load('nope\n')).rejects.toThrow(/not a valid Topwater recording/);
    await expect(Recording.load('{"format":"topwater-recording","version":1,"options":{},"start":{"file":"x.snapshots/start.zip"}}\n')).rejects.toThrow(/side-car/);
  });

  it('refuses side-car paths that are not plain relative names, before opening anything', async () => {
    let opened = false;
    const open = async () => { opened = true; return new Uint8Array(); };
    for (const file of ['/etc/x.zip', '../x.zip', 'a/../../x.zip', 'C:x.zip', 'C:\\Windows\\x.zip', 'a\\..\\..\\x.zip', '\\\\server\\share\\x.zip', 'https://evil.example/x.zip', 'file:///etc/x.zip', ''])
      await expect(Recording.load(JSON.stringify({ format: 'topwater-recording', version: 1, options: {}, start: { file } }) + '\n', open)).rejects.toThrow(/plain relative path|no snapshot/);
    expect(opened).toBe(false);
  });

  it('takes limits from the file only to lower them', async () => {
    const header = JSON.parse((await new Recording(ShellSnapshot.capture(new Shell()), {}).save()).split('\n')[0]);
    Object.assign(header.options, { maxLoopIterations: 1e15, maxCallDurationMs: 1e15, maxFileSystemBytes: 1e15, maxStringBytes: 1e15, maxExtractBytes: -5, maxExtractEntries: 10 });
    const text = JSON.stringify(header) + '\n';
    const raised = await Recording.load(text);
    const d = new Shell().options;
    expect(raised.options).toMatchObject({ maxLoopIterations: d.maxLoopIterations, maxCallDurationMs: d.maxCallDurationMs, maxFileSystemBytes: d.maxFileSystemBytes, maxStringBytes: d.maxStringBytes, maxExtractBytes: 0, maxExtractEntries: 10 });
    const tight = await Recording.load(text, undefined, { maxLoopIterations: 50 });
    expect(tight.createShell().options.maxLoopIterations).toBe(50);
  });

  it('caps the number and total size of snapshots', async () => {
    const sh = new Shell({ maxFileSystemBytes: 65536 });
    sh.fs.writeText('/home/agent/big.txt', 'x'.repeat(30000));
    const { recording, stop } = startRecording(sh, { snapshotEvery: 1 });
    for (let i = 0; i < 12; i++) await sh.run('true');
    stop();
    const lines = (await recording.save()).trimEnd().split('\n');
    // each snapshot is within the limit, all of them together are not
    await expect(Recording.load(lines.join('\n'), undefined, { maxFileSystemBytes: 65536 })).rejects.toThrow(/maxFileSystemBytes/);
    await expect(Recording.load(lines[0] + '\n' + (lines[1] + '\n').repeat(1000))).rejects.toThrow(/more than 1000 snapshots/);
  }, 60_000);
});

import { diffLines } from '../browser/diff.js';

describe('the viewer\'s line diff', () => {
  it('gives unified hunks with context', () => {
    const a = Array.from({ length: 20 }, (_, i) => `line ${i + 1}`).join('\n') + '\n';
    const b = a.replace('line 3\n', 'line three\n').replace('line 18\n', '').concat('line 21\n');
    const hunks = diffLines(a, b)!;
    expect(hunks.length).toBe(2);
    expect(hunks[0]).toMatchObject({ oldStart: 1, newStart: 1 });
    expect(hunks[0].lines.filter(l => l.op !== ' ').map(l => l.op + l.text)).toEqual(['-line 3', '+line three']);
    expect(hunks[1].lines.filter(l => l.op !== ' ').map(l => l.op + l.text)).toEqual(['-line 18', '+line 21']);
    expect(diffLines('same\n', 'same\n')).toEqual([]);
    expect(diffLines('', 'new\n')![0].lines).toEqual([{ op: '+', text: 'new', newNo: 1 }]);
    expect(diffLines('x\n'.repeat(3000), 'y\n'.repeat(3000))).toBeNull(); // too big to diff
  });
});
