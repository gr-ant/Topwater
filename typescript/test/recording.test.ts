import { describe, expect, it } from 'vitest';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { z } from 'zod';
import { Shell, ShellSnapshot, SnapshotError, Recording, startRecording } from '../src/index.js';
import { buildZip, crc32, readZip, writeZip } from '../src/commands/zipfile.js';
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

  /** The snapshot with its manifest changed by `edit`. */
  const tamper = (bytes: Uint8Array, edit: (m: any) => void) => {
    const entries = readZip(bytes);
    const entry = entries.find(e => e.name === 'manifest.json')!;
    const manifest = JSON.parse(new TextDecoder().decode(inflateRaw(entry.data)));
    edit(manifest);
    const raw = new TextEncoder().encode(JSON.stringify(manifest));
    return writeZip(entries.map(e => e === entry ? { ...e, method: 0, usize: raw.length, data: raw, crc: 0 } : e));
  };
  const fileEntry = (m: any, path: string) => m.files.find((f: any) => f.path === path);

  it('links survive save, load and restore', async () => {
    const sh = new Shell();
    await sh.run('mkdir -p d/s; echo hi > d/f; ln -s f d/l; ln -s /etc d/etc; ln -s nowhere d/dang; ln -s . d/s/self; ln -s loop2 loop1; ln -s loop1 loop2; ln d/f d/h; ln -s f d/sl; ln d/sl d/sl2');
    const snap = ShellSnapshot.capture(sh);
    const bytes = await snap.save();
    const manifest = JSON.parse(new TextDecoder().decode(inflateRaw(readZip(bytes).find(e => e.name === 'manifest.json')!.data)));
    expect(manifest.version).toBe(2);
    expect(fileEntry(manifest, '/home/agent/d/l')).toMatchObject({ type: 'symlink', target: 'f' });
    expect(fileEntry(manifest, '/home/agent/d/h').link).toBe('/home/agent/d/f');
    const probe = 'readlink d/l d/etc d/dang loop1; cat d/l d/s/self/self/../f; stat -c \'%h %F\' d/f d/h d/sl2; echo more >> d/h; cat d/f; cat loop1 2>&1';
    const want = 'f\n/etc\nnowhere\nloop2\nhi\nhi\n2 regular file\n2 regular file\n2 symbolic link\nhi\nmore\ncat: loop1: Too many levels of symbolic links\n';
    const loaded = new Shell();
    (await ShellSnapshot.load(bytes)).restore(loaded);
    expect((await loaded.run(probe)).stdout).toBe(want);
    await sh.run('rm -r d loop1');
    snap.restore(sh);
    expect((await sh.run(probe)).stdout).toBe(want);
  });

  it('checks the links in a snapshot', async () => {
    const sh = new Shell();
    await sh.run('mkdir d; echo x > x; ln -s /etc l');
    const good = await ShellSnapshot.capture(sh).save();
    const rejected = (edit: (m: any) => void) => ShellSnapshot.load(tamper(good, edit));
    // an entry can't be put through a link (it would land in /etc), nor be a hard link to a directory, a later or a missing entry
    await expect(rejected(m => { fileEntry(m, '/home/agent/x').path = '/home/agent/l/x'; })).rejects.toThrow('parent is not a directory');
    await expect(rejected(m => { fileEntry(m, '/home/agent/x').link = '/home/agent/d'; })).rejects.toThrow('not an earlier file');
    await expect(rejected(m => { fileEntry(m, '/home/agent/x').link = '/home/agent/missing'; })).rejects.toThrow('not an earlier file');
    await expect(rejected(m => { fileEntry(m, '/home/agent/l').link = '/home/agent/x'; })).rejects.toThrow('not an earlier file');
    await expect(rejected(m => { fileEntry(m, '/home/agent/x').link = '../etc/passwd'; })).rejects.toThrow('bad link');
    await expect(rejected(m => { fileEntry(m, '/home/agent/l').target = 'a\0b'; })).rejects.toThrow('bad symlink target');
    await expect(rejected(m => { fileEntry(m, '/home/agent/l').target = 'a'.repeat(4097); })).rejects.toThrow('bad symlink target');
    await expect(rejected(m => { fileEntry(m, '/home/agent/x').type = 'symlink'; delete fileEntry(m, '/home/agent/x').blob; })).rejects.toThrow('bad symlink target');
    await expect(rejected(m => { fileEntry(m, '/home/agent/x').type = 'socket'; })).rejects.toThrow('unknown type "socket"');
    // a link to anywhere, even a loop, loads: it is only a path, resolved later with the agent's permissions
    const loaded = new Shell();
    (await rejected(m => { fileEntry(m, '/home/agent/l').target = 'l'; })).restore(loaded);
    expect(loaded.fs.readLink('/home/agent/l')).toBe('l');
  });

  it('loads a snapshot with links saved by the C# port', async () => {
    // made with: mkdir -p d/s; echo hi > d/f; ln -s f d/l; ln -s /etc d/etc; ln -s nowhere d/dang; ln -s . d/s/self; ln -s loop2 loop1;
    // ln -s loop1 loop2; ln d/f d/h; ln -s f d/sl; ln d/sl d/sl2; cd d
    const sh = new Shell();
    (await ShellSnapshot.load(readFileSync(new URL('../../tests/Topwater.Tests/snapshots/links-from-csharp.zip', import.meta.url)))).restore(sh);
    const r = await sh.run("pwd; readlink l etc dang s/self ../loop1 sl2; cat l s/self/self/../f; stat -c '%h %F' f h sl2; echo more >> h; cat f; cat ../loop1 2>&1");
    expect(r.stdout).toBe('/home/agent/d\nf\n/etc\nnowhere\n.\nloop2\nf\nhi\nhi\n2 regular file\n2 regular file\n2 symbolic link\nhi\nmore\n' +
      'cat: ../loop1: Too many levels of symbolic links\n');
  });

  it('loads the snapshots the other ports saved', async () => {
    // setup: see tests/Topwater.Tests/SnapshotTests.cs, ASnapshotSavedByAnotherPortLoads
    for (const file of ['from-python.zip', 'from-csharp.zip']) {
      const sh = new Shell();
      (await ShellSnapshot.load(readFileSync(new URL(`../../tests/Topwater.Tests/snapshots/${file}`, import.meta.url)))).restore(sh);
      const r = await sh.run('echo "$PWD $n $EXP [$plain] ${!arr[@]} ${arr[2]} ${map[key two]} $1|$2"; greet you; cat u.txt; whoami');
      expect(r.stdout, file).toBe('/home/agent/proj 5 exp "q" $x [multi\nline] 0 2 three v2 p1|p 2\nhi you\nhéllo wörld\nagent\n');
    }
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
    // a snapshot after every 2nd call, and always after the last
    expect(recording.calls.map(c => !!c.snapshot)).toEqual([false, true, true]);
    expect(recording.calls[0].files).toEqual(['/home/agent/out', '/home/agent/out/n.txt']); // the clock stood still: /home/agent's mtime too
    expect(recording.calls[1].files).toBeUndefined();
    expect(recording.calls.map(c => c.time)).toEqual([deterministic.startTime, deterministic.startTime, deterministic.startTime + 5000]);
    const bytes = await recording.save();
    expect(readZip(bytes).map(e => e.name).filter(n => !n.startsWith('blobs/'))).toEqual(['manifest.json', 'history.jsonl', 'checkpoints/0.json', 'checkpoints/2.json', 'checkpoints/3.json']);
    const lines = entryText(bytes, 'history.jsonl').trimEnd().split('\n');
    expect(lines.length).toBe(4);
    expect(JSON.parse(lines[0])).toMatchObject({ format: 'topwater-history', version: 1, metadata: { task: 'demo' }, options: { randomSeed: 42, startTime: '2026-03-04T05:06:07.0000000Z' } });
    expect(JSON.parse(lines[2])).toMatchObject({ index: 1, time: '2026-03-04T05:06:07.0000000Z' });
    expect(entryText(bytes, 'manifest.json')).toBe(entryText(bytes, 'checkpoints/3.json'));
    const loaded = await Recording.load(bytes);
    expect(loaded.calls.map(c => !!c.snapshot)).toEqual([false, true, true]);
    expect(loaded.calls[0].files).toEqual(recording.calls[0].files);
    expect((await loaded.replay()).matches).toBe(true);
    const at = await loaded.shellAt(2);
    expect((await at.run('echo $x; f 2')).stdout).toBe('7\nf2\n');
    loaded.calls[1] = { ...loaded.calls[1], stdout: 'changed\n' };
    const report = await loaded.replay();
    expect(report.differences.map(d => [d.index, d.field])).toEqual([[1, 'stdout']]);
    // its manifest is the end machine, for anything that only reads snapshots
    const end = new Shell();
    (await ShellSnapshot.load(bytes)).restore(end);
    expect((await end.run('pwd; f 3; cat out/n.txt')).stdout).toBe('/home/agent\nf3\n2\n');
  });

  it('opens a snapshot as a recording of no calls', async () => {
    const sh = new Shell();
    await sh.run('echo hi > a.txt');
    const rec = await Recording.load(await ShellSnapshot.capture(sh).save());
    expect(rec.calls.length).toBe(0);
    expect((await rec.createShell().run('cat a.txt')).stdout).toBe('hi\n');
  });

  it('refuses newer versions and side-car files it cannot open', async () => {
    await expect(Recording.load('{"format":"topwater-recording","version":2}\n')).rejects.toThrow(/newer/);
    await expect(Recording.load('nope\n')).rejects.toThrow(/not a valid Topwater recording/);
    await expect(Recording.load('{"format":"topwater-recording","version":1,"options":{},"start":{"file":"x.snapshots/start.zip"}}\n')).rejects.toThrow(/side-car/);
    const good = await smallRecording();
    await expect(Recording.load(editEntry(good, 'history.jsonl', t => t.replace('"version":1', '"version":2')))).rejects.toThrow(/history format version 2 is newer than this version of Topwater supports/);
  });

  it('refuses side-car paths that are not plain relative names, before opening anything', async () => {
    let opened = false;
    const open = async () => { opened = true; return new Uint8Array(); };
    for (const file of ['/etc/x.zip', '../x.zip', 'a/../../x.zip', 'C:x.zip', 'C:\\Windows\\x.zip', 'a\\..\\..\\x.zip', '\\\\server\\share\\x.zip', 'https://evil.example/x.zip', 'file:///etc/x.zip', '',
      '%2e%2e/x', '.%2e/x', ' ../x', '\t../x', 'x?y=1', 'x#f', '/etc/x', 'C:x', 'a\\b', 'https://x', '.../x', 'a//b', 'a/'])
      await expect(Recording.load(JSON.stringify({ format: 'topwater-recording', version: 1, options: {}, start: { file } }) + '\n', open), file).rejects.toThrow(/plain relative path|no snapshot/);
    expect(opened).toBe(false);
    // and a plain name is passed on as it is
    const names: string[] = [];
    const snap = await ShellSnapshot.capture(new Shell()).save();
    await Recording.load(JSON.stringify({ format: 'topwater-recording', version: 1, options: {}, start: { file: 'rec.snapshots/start-0_1.zip' } }) + '\n', async p => { names.push(p); return snap; });
    expect(names).toEqual(['rec.snapshots/start-0_1.zip']);
  });

  it('takes limits from the file only to lower them', async () => {
    const options = { maxLoopIterations: 1e15, maxCallDurationMs: 1e15, maxFileSystemBytes: 1e15, maxStringBytes: 1e15, maxExtractBytes: -5, maxExtractEntries: 10 };
    const d = new Shell().options;
    const bounded = { maxLoopIterations: d.maxLoopIterations, maxCallDurationMs: d.maxCallDurationMs, maxFileSystemBytes: d.maxFileSystemBytes, maxStringBytes: d.maxStringBytes, maxExtractBytes: 0, maxExtractEntries: 10 };
    const jsonl = JSON.stringify({ format: 'topwater-recording', version: 1, options, start: { embedded: Buffer.from(await ShellSnapshot.capture(new Shell()).save()).toString('base64') } }) + '\n';
    const topwater = editEntry(await smallRecording(), 'history.jsonl', t => t.replace(/"options":\{[^}]*\}/, `"options":${JSON.stringify(options)}`));
    for (const file of [jsonl, topwater]) {
      expect((await Recording.load(file)).options).toMatchObject(bounded);
      const tight = await Recording.load(file, undefined, { maxLoopIterations: 50 });
      expect(tight.createShell().options.maxLoopIterations).toBe(50);
    }
    // -1: no time limit, which the caller's limit still bounds
    const unlimited = editEntry(await smallRecording(), 'history.jsonl', t => t.replace(/"maxCallDurationMs":\d+/, '"maxCallDurationMs":-1'));
    expect((await Recording.load(unlimited)).options.maxCallDurationMs).toBe(d.maxCallDurationMs);
    expect((await Recording.load(unlimited, undefined, { maxCallDurationMs: Infinity })).options.maxCallDurationMs).toBe(Infinity);
  });

  it('caps the number and total size of checkpoints', async () => {
    const sh = new Shell({ maxFileSystemBytes: 65536 });
    sh.fs.writeText('/home/agent/big.txt', 'x'.repeat(30000));
    const { recording, stop } = startRecording(sh, { snapshotEvery: 1 });
    for (let i = 0; i < 12; i++) await sh.run('true');
    stop();
    // each checkpoint is within the limit, all of them together are not
    await expect(Recording.load(await recording.save(), undefined, { maxFileSystemBytes: 65536 })).rejects.toThrow(/maxFileSystemBytes/);
    const many = new Recording(recording.start, {});
    for (let i = 0; i < 1000; i++) many.calls.push({ index: i, command: 'true', stdout: '', stderr: '', exitCode: 0, elapsedMs: 0, snapshot: recording.start });
    await expect(Recording.load(await many.save())).rejects.toThrow(/more than 1000 checkpoints/);
  }, 120_000);

  it('checks the layout of a .topwater file', async () => {
    const good = await smallRecording(); // two calls, checkpoints 0 and 2
    const renamed = (from: string, to: string) => writeZip(readZip(good).map(e => e.name === from ? { ...e, name: to } : e));
    await expect(Recording.load(renamed('checkpoints/2.json', 'checkpoints/02.json'))).rejects.toThrow(/not a checkpoint name/);
    await expect(Recording.load(renamed('checkpoints/2.json', 'checkpoints/3.json'))).rejects.toThrow(/after the last call/);
    await expect(Recording.load(renamed('checkpoints/0.json', 'checkpoints/1.json'))).rejects.toThrow(/checkpoints\/0.json \(the start\) is missing/);
    await expect(Recording.load(editEntry(good, 'checkpoints/2.json', t => t.replace('"user":"agent"', '"user":"../x"')))).rejects.toThrow(/checkpoints\/2.json: bad user/);
    // the entry count is read from the end record before any entry is listed: 4 x maxExtractEntries + 1002 at most
    const crowded = buildZip(Array.from({ length: 1003 }, (_, i) => [`x${i}`, ''] as [string, string]));
    await expect(ShellSnapshot.load(crowded, { maxExtractEntries: 0 })).rejects.toThrow(/1003 entries, more than 1002/);
    await expect(Recording.load(crowded, undefined, { maxExtractEntries: 0 })).rejects.toThrow(/1003 entries, more than 1002/);
  });

  it('the shell\'s files a call touched: added, changed and removed, with links', async () => {
    const sh = new Shell();
    await sh.run('echo a > a; echo b > b; ln -s a s');
    const { recording, stop } = startRecording(sh);
    await sh.run('echo b > b');      // same text, new mtime? only when the clock moved; the content is the same
    await sh.run('chmod 600 a; rm s; ln -s b s; ln a h');
    await sh.run('true');
    stop();
    expect(recording.calls[1].files).toEqual(['/home/agent', '/home/agent/a', '/home/agent/h', '/home/agent/s']);
    expect(recording.calls[2].files).toBeUndefined();
  });
});

describe('.topwater files from every port', () => {
  // from-typescript.topwater: TOPWATER_WRITE_FIXTURES=1 npx vitest run test/recording.test.ts writes it again (from-csharp and
  // from-python are written by those ports with the same scenario).
  const scenario = [
    "mkdir -p work && cd work && printf 'buy milk\\nfix bug\\n' > todo.txt && wc -l todo.txt",
    'greet() { echo "hi $1"; }; export TEAM=core; nums=(3 1 2); ln -s todo.txt latest; greet you',
    'rm todo.txt; echo done > out.txt; ls; cat latest; exit 3',
  ];
  const dir = new URL('../../tests/Topwater.Tests/recordings/', import.meta.url);

  it.runIf(process.env.TOPWATER_WRITE_FIXTURES)('writes from-typescript.topwater', async () => {
    const sh = new Shell({ startTime: Date.UTC(2026, 10, 25, 9, 0, 0), randomSeed: 3 });
    const { recording, stop } = startRecording(sh, { metadata: { source: 'typescript port', task: 'cross-port fixture' }, snapshotEvery: 2 });
    for (const command of scenario) await sh.run(command);
    stop();
    writeFileSync(new URL('from-typescript.topwater', dir), await recording.save());
  });

  for (const file of ['from-typescript.topwater', 'from-csharp.topwater', 'from-python.topwater']) {
    it.runIf(existsSync(new URL(file, dir)))(`loads ${file}`, async () => {
      const bytes = new Uint8Array(readFileSync(new URL(file, dir)));
      const rec = await Recording.load(bytes);
      expect(rec.calls.map(c => c.command)).toEqual(scenario);
      expect(rec.metadata.source).toBeTruthy();
      expect(rec.calls[2].exitCode).toBe(3);
      expect(rec.calls[0].files).toEqual(expect.arrayContaining(['/home/agent/work', '/home/agent/work/todo.txt']));
      expect(rec.calls[2].files).toEqual(expect.arrayContaining(['/home/agent/work/todo.txt', '/home/agent/work/out.txt']));
      const at1 = await rec.shellAt(1);
      expect((await at1.run('cd /home/agent/work; greet x; echo $TEAM ${nums[@]}; readlink latest')).stdout).toBe('hi x\ncore 3 1 2\ntodo.txt\n'); // one echo: $TEAM and the array on one line
      const end = new Shell();
      (await ShellSnapshot.load(bytes)).restore(end);
      expect((await end.run('cat /home/agent/work/out.txt')).stdout).toBe('done\n');
      if (file === 'from-typescript.topwater') {
        expect(rec.metadata.source).toBe('typescript port');
        expect(rec.isDeterministic).toBe(true);
        expect((await rec.replay()).matches).toBe(true);
      }
      validate(bytes);
    });
  }
});

/** Entry `name` of a zip, as text. */
function entryText(zip: Uint8Array, name: string) {
  const e = readZip(zip).find(x => x.name === name)!;
  return new TextDecoder().decode(e.method === 0 ? e.data : inflateRaw(e.data));
}
/** The zip with entry `name` rewritten (stored). */
function editEntry(zip: Uint8Array, name: string, edit: (text: string) => string) {
  const raw = new TextEncoder().encode(edit(entryText(zip, name)));
  return writeZip(readZip(zip).map(e => e.name === name ? { ...e, method: 0, usize: raw.length, data: raw, crc: crc32(raw) } : e));
}
async function smallRecording() {
  const sh = new Shell(deterministic);
  const { recording, stop } = startRecording(sh);
  await sh.run('echo one > a');
  await sh.run('echo two >> a');
  stop();
  return recording.save();
}

/** Checks a .topwater file's manifest, checkpoints and history lines against the schemas in spec/topwater-file. */
function validate(zip: Uint8Array) {
  const spec = (name: string) => JSON.parse(readFileSync(new URL(`../../spec/topwater-file/${name}`, import.meta.url), 'utf8'));
  const manifest = spec('manifest.schema.json');
  // zod resolves only local references: give the history schema the manifest's definitions
  const history = JSON.parse(JSON.stringify(spec('history.schema.json')).replaceAll('manifest.schema.json#/$defs/', '#/$defs/'));
  history.$defs = { ...manifest.$defs, ...history.$defs };
  const machine = z.fromJSONSchema(manifest), line = z.fromJSONSchema(history);
  for (const e of readZip(zip)) {
    if (e.name === 'manifest.json' || e.name.startsWith('checkpoints/')) expect(machine.safeParse(JSON.parse(entryText(zip, e.name))).error, e.name).toBeUndefined();
    if (e.name === 'history.jsonl')
      for (const l of entryText(zip, e.name).trimEnd().split('\n')) expect(line.safeParse(JSON.parse(l)).error, l).toBeUndefined();
  }
}

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
