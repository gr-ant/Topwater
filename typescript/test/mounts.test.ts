// Mounted sources (docs/mounts.md): tests/mounts/cases.json, which the C# and Python ports run too, then what only this port
// checks: hostile sources, limits, snapshots, recordings and the local folder source.
import { describe, expect, it } from 'vitest';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, symlinkSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Shell, ShellSnapshot, SnapshotError, Recording, startRecording, LocalFolderSource, MemorySource, type MountChange, type MountEntry, type MountSource } from '../src/index.js';
import { crc32 as crc, readZip, writeZip } from '../src/commands/zipfile.js';
import { inflateRaw } from '../src/inflate.js';

const CASES = JSON.parse(readFileSync(new URL('../../tests/mounts/cases.json', import.meta.url), 'utf8'));
const text = (b: Uint8Array) => new TextDecoder().decode(b);

function changeJson(c: MountChange) {
  const d: Record<string, string> = { kind: c.kind, path: c.path };
  if (c.to !== null) d.to = c.to;
  if (c.version !== null) d.version = c.version;
  if (c.content !== null) d.content = text(c.content);
  return d;
}

/** Runs a case's steps and returns what each produced, in the shape the file's `expect` holds. */
async function runCase(c: any): Promise<unknown[]> {
  const src = new MemorySource(CASES.files);
  const sh = new Shell();
  await sh.mount('/mnt/src', src, c.mode);
  const out: unknown[] = [];
  for (const step of c.steps) {
    if ('run' in step) {
      const r = await sh.run(step.run);
      out.push({ stdout: r.stdout, stderr: r.stderr, exitCode: r.exitCode });
    } else if ('changes' in step) out.push(sh.pendingChanges('/mnt/src').map(changeJson));
    else if ('apply' in step || 'applySubset' in step) {
      const pending = sh.pendingChanges('/mnt/src');
      const chosen = 'apply' in step ? undefined : step.applySubset.map((i: number) => pending[i]);
      const r = await sh.applyChanges('/mnt/src', chosen);
      out.push({ applied: r.applied.map(x => x.path), failed: r.failed?.path ?? null, conflict: r.conflict });
    } else if ('source' in step) out.push(Object.fromEntries(Object.entries(src.tree()).map(([p, v]) => [p, v === null ? null : text(v)])));
    else if ('sourceSet' in step) { src.set(step.sourceSet[0], step.sourceSet[1]); out.push(null); }
    else if ('sourceRemove' in step) { src.remove(step.sourceRemove); out.push(null); }
    else if ('discard' in step) { sh.discardChanges('/mnt/src'); out.push(null); }
  }
  return out;
}

describe('shared cases', () => {
  for (const c of CASES.cases)
    it(c.name, async () => {
      const got = await runCase(c);
      c.steps.forEach((step: any, i: number) => expect([step, got[i]]).toEqual([step, step.expect ?? null]));
    });
});

// ---------- what only this port checks ----------

/** A source that lists names no file can have, a duplicate, and an endless supply of entries. */
class Hostile implements MountSource {
  readonly type = 'hostile';
  readonly id = 'h';
  readonly capabilities = [];
  constructor(private readonly count = 5) {}
  list(): MountEntry[] {
    const bad: any[] = [{ name: '', isDir: false }, { name: '.', isDir: true }, { name: '..', isDir: true }, { name: 'a/b', isDir: false },
      { name: 'nul\0', isDir: false }, { name: 'x'.repeat(256), isDir: false }, { name: 'ok', isDir: false, size: 3, version: '1' },
      { name: 'ok', isDir: false, size: 9, version: '2' }, { name: 'neg', isDir: false, size: -1 }, 'not an entry'];
    return [...bad, ...Array.from({ length: this.count }, (_, i) => ({ name: `z${i}`, isDir: false, size: 1, version: 'v' }))];
  }
  stat() { return null; }
  read(path: string, maxBytes: number) { return new TextEncoder().encode(path === '/z0' ? 'x'.repeat(maxBytes + 1) : 'abc'); }
}

const baseBytes = () => new Shell().fs.usedBytes;

describe('mounts', () => {
  it('skips hostile names and bounds the listing', async () => {
    const sh = new Shell({ maxExtractEntries: 4 });
    await sh.mount('/mnt/h', new Hostile());
    expect((await sh.run('ls /mnt/h')).stdout).toBe('ok\nz0\nz1\nz2\n');
    expect(sh.mounts[0].warnings.map(w => w.split(': ').slice(1).join(': '))).toEqual(['listing stopped at 4 entries', "skipped 9 entries with names that can't be files"]);
    const r = await sh.run('cat /mnt/h/ok; cat /mnt/h/z0');
    expect([r.stdout, r.stderr]).toEqual(['abc', 'cat: /mnt/h/z0: File too large\n']);
    expect((await sh.run('mount -r /mnt/h')).stderr.split('mount: /mnt/h:').length - 1).toBe(2);
  });

  it('does not fetch a file bigger than the disk', async () => {
    const src = new MemorySource({ 'big.bin': new Uint8Array(5000).fill(120), 'small.txt': 's\n' });
    const sh = new Shell({ maxFileSystemBytes: baseBytes() + 4000 });
    await sh.mount('/mnt/s', src);
    const r = await sh.run('cat /mnt/s/big.bin > /dev/null; cat /mnt/s/small.txt');
    expect(r.stdout).toBe('s\n');
    expect(r.stderr).toContain('File too large');
  });

  it('evicts fetched files to make room', async () => {
    const src = new MemorySource({ a: 'a'.repeat(3000), b: 'b'.repeat(3000) });
    const sh = new Shell({ maxFileSystemBytes: baseBytes() + 4000 });
    await sh.mount('/mnt/s', src);
    expect((await sh.run('wc -c /mnt/s/a')).stdout).toBe('3000 /mnt/s/a\n');
    expect((await sh.run('wc -c /mnt/s/b')).stdout).toBe('3000 /mnt/s/b\n');
    expect((await sh.run('wc -c /mnt/s/a')).stdout).toBe('3000 /mnt/s/a\n');
    expect(src.calls.filter(c => c[0] === 'read')).toEqual([['read', '/a'], ['read', '/b'], ['read', '/a']]);
  });

  it('fails the command, not the session, when the source is slow', async () => {
    const src = new MemorySource({ 'slow.txt': 's\n', 'fast.txt': 'f\n' });
    const sh = new Shell({ maxCallDurationMs: 500 });
    await sh.mount('/mnt/s', src);
    src.hook = (_op, path) => path === '/slow.txt' ? new Promise(res => setTimeout(res, 5000)) : undefined;
    const r = await sh.run('cat /mnt/s/slow.txt');
    expect(r.stderr.includes('the source did not answer in time') || r.exitCode === 124).toBe(true);
    expect((await sh.run('cat /mnt/s/fast.txt')).stdout).toBe('f\n');
  });

  it('reports source failures and retries them', async () => {
    const src = new MemorySource({ 'a.txt': 'a\n' });
    const sh = new Shell();
    await sh.mount('/mnt/s', src, 'staged');
    let fail = true;
    src.hook = op => { if (fail && (op === 'read' || op === 'write')) throw new Error('the service is down'); };
    expect((await sh.run('cat /mnt/s/a.txt')).stderr).toBe('cat: /mnt/s/a.txt: the service is down\n');
    fail = false;
    expect((await sh.run('cat /mnt/s/a.txt')).stdout).toBe('a\n');
    await sh.run('echo b > /mnt/s/a.txt');
    fail = true;
    const r = await sh.applyChanges('/mnt/s');
    expect([r.failed?.path, r.error, r.conflict]).toEqual(['/a.txt', 'the service is down', false]);
    fail = false;
    expect((await sh.applyChanges('/mnt/s')).applied[0].path).toBe('/a.txt');
    expect(text(src.tree()['/a.txt']!)).toBe('b\n');
  });

  it('never gives the source a path outside it', async () => {
    const src = new MemorySource({ 'a.txt': 'a\n' });
    const sh = new Shell();
    await sh.mount('/mnt/s', src, 'staged');
    await sh.run('cd /mnt/s; cat ../../etc/hostname; ln -s /etc /mnt/s/etc; cat /mnt/s/etc/hostname; echo x > /mnt/s/etc/../b');
    expect(src.calls.every(([, p]) => p.startsWith('/') && !p.includes('..'))).toBe(true);
    expect(sh.pendingChanges('/mnt/s').map(c => c.path)).toEqual([]); // the link is not a change; ../b left the mount
  });

  it('checks mount points', async () => {
    const sh = new Shell();
    const src = new MemorySource();
    await expect(sh.mount('/', src)).rejects.toThrow();
    await expect(sh.mount('/etc', src)).rejects.toThrow(); // not empty
    await expect(sh.mount('/mnt/x', src, 'sideways' as never)).rejects.toThrow();
    await expect(sh.mount('/mnt/x', new Hostile(), 'staged')).rejects.toThrow(); // can't write
    await sh.mount('/mnt/x', src);
    await expect(sh.mount('/mnt/x/y', src)).rejects.toThrow();
    await expect(sh.mount('/mnt/x', src)).rejects.toThrow();
    sh.unmount('/mnt/x');
    expect(sh.mounts).toEqual([]);
    expect((await sh.run('ls -A /mnt/x')).stdout).toBe('');
  });

  it('snapshots keep the definition and the changes, not the files', async () => {
    const src = new MemorySource({ 'a.txt': 'secret\n', 'b.txt': 'b\n' });
    const sh = new Shell();
    await sh.mount('/mnt/s', src, 'staged');
    await sh.run('cat /mnt/s/a.txt; echo new > /mnt/s/b.txt; rm /mnt/s/a.txt; cd /mnt/s');
    const raw = await ShellSnapshot.capture(sh).save();
    const all = readZip(raw).map(e => e.method === 8 ? inflateRaw(e.data, 1 << 24) : e.data);
    expect(all.some(b => text(b).includes('secret'))).toBe(false);
    const loaded = new Shell();
    (await ShellSnapshot.load(raw)).restore(loaded);
    const info = loaded.mounts[0];
    expect([info.path, info.mode, info.type, info.id]).toEqual(['/mnt/s', 'staged', 'memory', 'memory']);
    expect(info.error).toBe('unavailable: no source for memory:memory');
    expect(loaded.pendingChanges('/mnt/s').map(c => c.kind)).toEqual(['write', 'delete']);
    expect((await loaded.run('ls /mnt/s')).stderr).toContain('Permission denied');
    await loaded.mount('/mnt/s', src, 'staged');
    expect((await loaded.run('ls /mnt/s; cat /mnt/s/b.txt')).stdout).toBe('b.txt\nnew\n');
    expect(loaded.pendingChanges('/mnt/s')).toEqual(sh.pendingChanges('/mnt/s'));
    // restoring into a shell that has the source lists it again at the next call
    const other = new Shell();
    await other.mount('/mnt/other', src);
    ShellSnapshot.capture(sh).restore(other);
    expect((await other.run('cat /mnt/s/b.txt')).stdout).toBe('new\n');
    // the source changed meanwhile: applying conflicts, against the version the change was made on
    src.set('b.txt', 'theirs\n');
    const r = await loaded.applyChanges('/mnt/s');
    expect([r.conflict, r.failed?.path]).toEqual([true, '/b.txt']);
  });

  it('fetches the working directory for a command run in a mount directory', async () => {
    const sh = new Shell();
    await sh.mount('/mnt/s', new MemorySource({ 'd/a.txt': 'hello\n' }));
    expect((await sh.run('cd /mnt/s/d && grep -r hello')).stdout).toBe('a.txt:hello\n');
  });

  it('refuses a relative mount point', async () => {
    await expect(new Shell().mount('mnt/s', new MemorySource())).rejects.toThrow();
  });

  it('still ends the call at the time limit during a refresh', async () => {
    const src = new MemorySource({ a: 'a' });
    const sh = new Shell({ maxCallDurationMs: 300 });
    await sh.mount('/mnt/s', src);
    src.hook = () => new Promise(res => setTimeout(res, 1000));
    expect((await sh.run('mount -r /mnt/s; echo after')).stdout).not.toContain('after');
  });

  it('keeps a working directory inside a mount across a restore', async () => {
    const src = new MemorySource({ 'd/a.txt': 'a\n' });
    const sh = new Shell();
    await sh.mount('/mnt/s', src);
    await sh.run('cd /mnt/s/d');
    const snap = ShellSnapshot.capture(sh);
    await sh.run('cd /');
    snap.restore(sh);
    expect((await sh.run('pwd; cat a.txt')).stdout).toBe('/mnt/s/d\na\n');
  });

  it('loads old snapshots', async () => {
    const loaded = new Shell();
    (await ShellSnapshot.load(await ShellSnapshot.capture(new Shell()).save())).restore(loaded);
    expect(loaded.mounts).toEqual([]);
  });

  it('refuses bad mounts in a snapshot', async () => {
    const sh = new Shell();
    await sh.mount('/mnt/s', new MemorySource({ a: 'a' }), 'staged');
    await sh.run('echo b > /mnt/s/a');
    const raw = await ShellSnapshot.capture(sh).save();
    const entries = readZip(raw);
    const manifest = entries.find(e => e.name === 'manifest.json')!;
    const m = JSON.parse(text(manifest.method === 8 ? inflateRaw(manifest.data, 1 << 24) : manifest.data));
    expect(m.mounts[0].changes[0]).toMatchObject({ kind: 'write', path: '/a', version: '1', size: 2 });
    for (const bad of [{ path: '/mnt/../etc' }, { mode: 'wild' }, { path: '/' }, { changes: [{ kind: 'write', path: '/a/../b', size: 0 }] },
      { changes: [{ kind: 'write', path: '/a', size: 3, blob: '0'.repeat(64) }] }, { changes: [{ kind: 'explode', path: '/a' }] }]) {
      const copy = structuredClone(m);
      Object.assign(copy.mounts[0], bad);
      const data = new TextEncoder().encode(JSON.stringify(copy));
      const zip = writeZip(entries.map(e => e.name === 'manifest.json' ? { ...e, method: 0, data, usize: data.length, crc: crc(data) } : e));
      await expect(ShellSnapshot.load(zip)).rejects.toThrow(SnapshotError);
    }
  });

  it('replays a recording without the source', async () => {
    const src = new MemorySource({ 'a.txt': 'a\n', 'd/b.txt': 'b\n' });
    const sh = new Shell();
    await sh.mount('/mnt/s', src, 'live');
    await sh.run('cat /mnt/s/a.txt'); // fetched before the recording starts
    const rec = startRecording(sh);
    await sh.run('cat /mnt/s/a.txt /mnt/s/d/b.txt; echo c > /mnt/s/c.txt; mount -r /mnt/s; ls /mnt/s');
    src.set('a.txt', 'theirs\n');
    await sh.run('echo mine > /mnt/s/a.txt');
    rec.stop();
    const loaded = await Recording.load(await rec.recording.save());
    const before = [...src.calls];
    const report = await loaded.replay();
    expect(report.differences).toEqual([]);
    expect(src.calls).toEqual(before);
    const replayed = loaded.createShell();
    const r = await replayed.run('mount -r /mnt/s; cat /mnt/s/d/b.txt /mnt/s/c.txt');
    expect([r.stdout, r.stderr]).toEqual(['b\n', 'cat: /mnt/s/c.txt: read /c.txt is not in the Topwater recording\n']);
  });

  it('shows a local folder, never past its root', async () => {
    const tmp = mkdtempSync(join(tmpdir(), 'topwater-mount-'));
    try {
      const root = join(tmp, 'vault');
      mkdirSync(join(root, 'notes'), { recursive: true });
      writeFileSync(join(root, 'notes', 'a.md'), '# A\n');
      writeFileSync(join(root, 'top.md'), 'top\n');
      writeFileSync(join(tmp, 'outside.txt'), 'private\n');
      symlinkSync(join(tmp, 'outside.txt'), join(root, 'link.md'));
      symlinkSync(tmp, join(root, 'up'));
      const sh = new Shell();
      await sh.mount('/mnt/v', new LocalFolderSource(root));
      expect((await sh.run('find /mnt/v | sort')).stdout).toBe('/mnt/v\n/mnt/v/notes\n/mnt/v/notes/a.md\n/mnt/v/top.md\n');
      expect((await sh.run('cat /mnt/v/notes/a.md')).stdout).toBe('# A\n');
      expect((await sh.run('echo x > /mnt/v/top.md')).stderr).toContain('Permission denied');
      await expect(sh.mount('/mnt/w', new LocalFolderSource(root), 'staged')).rejects.toThrow(); // read-only unless writable
      const local = new LocalFolderSource(root, { writable: true, id: 'vault' });
      expect(local.stat('/link.md')).toBeNull();
      expect(() => local.read('/up/outside.txt', 100)).toThrow();

      await sh.mount('/mnt/w', local, 'staged');
      await sh.run('echo more >> /mnt/w/top.md; mv /mnt/w/notes/a.md /mnt/w/notes/b.md; mkdir /mnt/w/new; echo n > /mnt/w/new/n.md');
      const r = await sh.applyChanges('/mnt/w');
      expect(r.failed).toBeNull();
      expect(readFileSync(join(root, 'top.md'), 'utf8')).toBe('top\nmore\n');
      expect(readFileSync(join(root, 'notes', 'b.md'), 'utf8')).toBe('# A\n');
      expect(existsSync(join(root, 'notes', 'a.md'))).toBe(false);
      expect(readFileSync(join(root, 'new', 'n.md'), 'utf8')).toBe('n\n');
      expect(readFileSync(join(tmp, 'outside.txt'), 'utf8')).toBe('private\n');
      await sh.run('echo again >> /mnt/w/top.md');
      writeFileSync(join(root, 'top.md'), 'edited on the host, longer\n');
      const r2 = await sh.applyChanges('/mnt/w');
      expect([r2.conflict, r2.failed?.path]).toEqual([true, '/top.md']);
      expect(readFileSync(join(root, 'top.md'), 'utf8')).toBe('edited on the host, longer\n');
    } finally { rmSync(tmp, { recursive: true, force: true }); }
  });
});

