// Mounted sources (docs/mounts.md): tests/mounts/cases.json, which the C# and Python ports run too, then what only this port
// checks: hostile sources, limits, snapshots, recordings, the local folder source and the files every port writes.
import { describe, expect, it } from 'vitest';
import { execFileSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import {
  BashTool, Shell, ShellSnapshot, SnapshotError, Recording, startRecording, LocalFolderSource, MemorySource, MountSourceError,
  type MountChange, type MountEntry, type MountSource,
} from '../src/index.js';
import { LIST_LIMITS } from '../src/mounts.js';
import { crc32 as crc, readZip, writeZip } from '../src/commands/zipfile.js';
import { inflateRaw } from '../src/inflate.js';

const MOUNTS_DIR = fileURLToPath(new URL('../../tests/mounts/', import.meta.url));
const CASES = JSON.parse(readFileSync(MOUNTS_DIR + 'cases.json', 'utf8'));
const text = (b: Uint8Array) => new TextDecoder().decode(b);
const sleep = (ms: number) => new Promise(res => setTimeout(res, ms));

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
    } else if ('reads' in step) {
      out.push(src.calls.filter(([op]) => op === 'read').map(([, p]) => p));
      src.calls.length = 0;
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
  asked = 0;
  constructor(private readonly count = 5) {}
  list(_path: string, maxEntries: number): MountEntry[] {
    this.asked = maxEntries;
    const bad: any[] = [{ name: '', isDir: false }, { name: '.', isDir: true }, { name: '..', isDir: true }, { name: 'a/b', isDir: false },
      { name: 'nul\0', isDir: false }, { name: 'x'.repeat(256), isDir: false }, { name: 'ok', isDir: false, size: 3, version: '1' },
      { name: 'ok', isDir: false, size: 9, version: '2' }, { name: 'neg', isDir: false, size: -1 }, 'not an entry'];
    return [...bad, ...Array.from({ length: this.count }, (_, i) => ({ name: `z${i}`, isDir: false, size: 1, version: 'v' }))];
  }
  stat() { return null; }
  read(path: string, maxBytes: number) { return new TextEncoder().encode(path === '/z0' ? 'x'.repeat(maxBytes + 1) : 'abc'); }
}

const baseBytes = () => new Shell().fs.usedBytes;
const load = async (raw: Uint8Array) => { const sh = new Shell(); (await ShellSnapshot.load(raw)).restore(sh); return sh; };
const withListBudget = async (n: number, body: () => Promise<void>) => {
  const saved = LIST_LIMITS.entries;
  LIST_LIMITS.entries = n;
  try { await body(); } finally { LIST_LIMITS.entries = saved; }
};
const tempDir = () => mkdtempSync(join(tmpdir(), 'topwater-mount-'));
/** Every file and directory below `d`, relative and sorted. */
function tree(d: string): string[] {
  const out: string[] = [];
  const walk = (p: string) => { for (const n of readdirSync(p)) { const f = join(p, n); out.push(relative(d, f)); if (statSync(f).isDirectory()) walk(f); } };
  walk(d);
  return out.sort();
}
function hostFolder(tmp: string) {
  const root = join(tmp, 'notes');
  mkdirSync(join(root, 'docs'), { recursive: true });
  writeFileSync(join(root, 'docs', 'keep.md'), 'keep\n');
  writeFileSync(join(root, 'a.md'), 'hello\n');
  return root;
}

describe('mounts', () => {
  it('skips hostile names and bounds the listing', async () => {
    const sh = new Shell({ maxExtractEntries: 4 });
    const h = new Hostile();
    await sh.mount('/mnt/h', h);
    expect((await sh.run('ls /mnt/h')).stdout).toBe('ok\nz0\nz1\nz2\n');
    expect(sh.mounts[0].warnings.map(w => w.split(': ').slice(1).join(': '))).toEqual(["skipped 9 entries with names that can't be files", 'listing stopped at 4 entries']);
    expect(h.asked).toBe(4); // the source is told how many entries are wanted
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
    src.hook = (_op, path) => path === '/slow.txt' ? sleep(5000) : undefined;
    const r = await sh.run('cat /mnt/s/slow.txt');
    expect(r.stderr.includes('the source did not answer in time') || r.exitCode === 124).toBe(true);
    expect((await sh.run('cat /mnt/s/fast.txt')).stdout).toBe('f\n');
  });

  it('aborts the signal at the time limit', async () => {
    let signal: AbortSignal | undefined;
    class Slow extends MemorySource {
      override async read(path: string, maxBytes: number, options?: { signal?: AbortSignal }) {
        signal = options?.signal;
        await sleep(2000);
        return super.read(path, maxBytes);
      }
    }
    const sh = new Shell({ maxCallDurationMs: 300 });
    await sh.mount('/mnt/s', new Slow({ 'a.txt': 'a\n' }));
    await sh.run('cat /mnt/s/a.txt');
    expect(signal?.aborted).toBe(true);
  });

  it('reports source failures and retries them, with the full text only for the host', async () => {
    const src = new MemorySource({ 'a.txt': 'a\n' });
    const sh = new Shell();
    await sh.mount('/mnt/s', src, 'staged');
    let fail = true;
    src.hook = op => { if (fail && (op === 'read' || op === 'write')) throw new Error('GET https://api/x?sig=SECRET 503'); };
    expect((await sh.run('cat /mnt/s/a.txt')).stderr).toBe('cat: /mnt/s/a.txt: the source failed (run the command again to retry)\n');
    fail = false;
    expect((await sh.run('cat /mnt/s/a.txt')).stdout).toBe('a\n');
    await sh.run('echo b > /mnt/s/a.txt');
    fail = true;
    const r = await sh.applyChanges('/mnt/s');
    expect([r.failed?.path, r.error, r.conflict]).toEqual(['/a.txt', 'GET https://api/x?sig=SECRET 503', false]);
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
    await expect(sh.mount('mnt/s', src)).rejects.toThrow(); // relative
    await sh.mount('/mnt/x', src);
    await expect(sh.mount('/mnt/x/y', src)).rejects.toThrow();
    await expect(sh.mount('/mnt/x', src)).rejects.toThrow();
    sh.unmount('/mnt/x');
    expect(sh.mounts).toEqual([]);
    expect((await sh.run('ls -A /mnt/x')).stdout).toBe('');
  });

  it('snapshots keep the definition and the changes, not the files; a file\'s changes wait for the host', async () => {
    const src = new MemorySource({ 'a.txt': 'secret\n', 'b.txt': 'b\n' });
    const sh = new Shell();
    await sh.mount('/mnt/s', src, 'staged');
    await sh.run('cat /mnt/s/a.txt; echo new > /mnt/s/b.txt; rm /mnt/s/a.txt; cd /mnt/s');
    const raw = await ShellSnapshot.capture(sh).save();
    const all = readZip(raw).map(e => e.method === 8 ? inflateRaw(e.data, 1 << 24) : e.data);
    expect(all.some(b => text(b).includes('secret'))).toBe(false);
    const manifest = readZip(raw).find(e => e.name === 'manifest.json')!;
    expect(JSON.parse(text(inflateRaw(manifest.data, 1 << 24))).version).toBe(3);
    const loaded = await load(raw);
    const info = loaded.mounts[0];
    expect([info.path, info.mode, info.type, info.id, info.held]).toEqual(['/mnt/s', 'staged', 'memory', 'memory', 2]);
    expect(info.error).toBe('unavailable: no source for memory:memory');
    expect(loaded.pendingChanges('/mnt/s')).toEqual([]);
    expect(loaded.snapshotChanges('/mnt/s').map(c => c.kind)).toEqual(['write', 'delete']);
    expect((await loaded.run('ls /mnt/s')).stderr).toContain('Permission denied');
    await loaded.mount('/mnt/s', src, 'staged');
    expect((await loaded.run('ls /mnt/s; cat /mnt/s/b.txt')).stdout).toBe('a.txt\nb.txt\nb\n');
    expect(loaded.pendingChanges('/mnt/s')).toEqual([]);
    expect((await loaded.run('mount')).stdout).toContain('2 changes from a saved session wait for the host');
    await loaded.acceptSnapshotChanges('/mnt/s');
    expect((await loaded.run('ls /mnt/s; cat /mnt/s/b.txt')).stdout).toBe('b.txt\nnew\n');
    expect(loaded.pendingChanges('/mnt/s')).toEqual(sh.pendingChanges('/mnt/s'));
    // a snapshot taken in this process comes back as it was, with no source call
    const other = new Shell();
    await other.mount('/mnt/other', src);
    await other.mount('/mnt/s', src, 'staged');
    other.unmount('/mnt/other');
    const before = src.calls.length;
    ShellSnapshot.capture(sh).restore(other);
    expect((await other.run('ls /mnt/s')).stdout).toBe('b.txt\n');
    expect(other.pendingChanges('/mnt/s')).toEqual(sh.pendingChanges('/mnt/s'));
    expect(src.calls.slice(before).filter(c => c[0] === 'list')).toEqual([]);
    // the source changed meanwhile: applying conflicts, against the version the change was made on
    src.set('b.txt', 'theirs\n');
    const r = await loaded.applyChanges('/mnt/s');
    expect([r.conflict, r.failed?.path]).toEqual([true, '/b.txt']);
  });

  it('fetches the working directory for a recursive grep run in a mount directory', async () => {
    const sh = new Shell();
    await sh.mount('/mnt/s', new MemorySource({ 'd/a.txt': 'hello\n' }));
    expect((await sh.run('cd /mnt/s/d && grep -r hello')).stdout).toBe('a.txt:hello\n');
  });

  it('names a deleted file by its renamed directory', async () => {
    const src = new MemorySource({ 'd/a.txt': 'a\n', 'd/b.txt': 'b\n' });
    const sh = new Shell();
    await sh.mount('/mnt/s', src, 'staged');
    await sh.run('rm /mnt/s/d/b.txt; mv /mnt/s/d /mnt/s/e');
    expect(sh.pendingChanges('/mnt/s').map(c => [c.kind, c.path, c.to])).toEqual([['rename', '/d', '/e'], ['delete', '/e/b.txt', null]]);
    expect((await sh.applyChanges('/mnt/s')).failed).toBeNull();
    expect(Object.keys(src.tree())).toEqual(['/e', '/e/a.txt']);
  });

  it('still ends the call at the time limit during a refresh', async () => {
    const src = new MemorySource({ a: 'a' });
    const sh = new Shell({ maxCallDurationMs: 300 });
    await sh.mount('/mnt/s', src);
    src.hook = () => sleep(1000);
    expect((await sh.run('mount -r /mnt/s; echo after')).stdout).not.toContain('after');
  });

  it('keeps a working directory inside a mount across a restore', async () => {
    const sh = new Shell();
    await sh.mount('/mnt/s', new MemorySource({ 'd/a.txt': 'a\n' }));
    await sh.run('cd /mnt/s/d');
    const snap = ShellSnapshot.capture(sh);
    await sh.run('cd /');
    snap.restore(sh);
    expect((await sh.run('pwd; cat a.txt')).stdout).toBe('/mnt/s/d\na\n');
  });

  it('loads old snapshots', async () => {
    expect((await load(await ShellSnapshot.capture(new Shell()).save())).mounts).toEqual([]);
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

  it('holds at most 1000 mounts in a snapshot', async () => {
    // the overlap check is quadratic: 20,000 mounts in a small file took minutes to load
    const entries = readZip(await ShellSnapshot.capture(new Shell()).save());
    const manifest = entries.find(e => e.name === 'manifest.json')!;
    const m = JSON.parse(text(manifest.method === 8 ? inflateRaw(manifest.data, 1 << 24) : manifest.data));
    m.version = 3;
    m.mounts = Array.from({ length: 1001 }, (_, i) => ({ path: `/mnt/m${i}`, mode: 'read-only', type: 'memory', id: 'memory' }));
    const data = new TextEncoder().encode(JSON.stringify(m));
    const zip = writeZip(entries.map(e => e.name === 'manifest.json' ? { ...e, method: 0, data, usize: data.length, crc: crc(data) } : e));
    await expect(ShellSnapshot.load(zip)).rejects.toThrow('more than 1000 mounts');
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
    expect((await loaded.replay()).differences).toEqual([]);
    expect(src.calls).toEqual(before);
    const r = await loaded.createShell().run('mount -r /mnt/s; cat /mnt/s/d/b.txt /mnt/s/c.txt');
    expect([r.stdout, r.stderr]).toEqual(['b\n', 'cat: /mnt/s/c.txt: read /c.txt is not in the Topwater recording (run the command again to retry)\n']);
  });

  it('shows a local folder, never past its root', async () => {
    const tmp = tempDir();
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
      expect(() => local.read('/up/outside.txt', 100)).toThrow(MountSourceError);
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

// ---------- review 046: regression tests ----------

describe('review 046', () => {
  const HOSTILE = readFileSync(MOUNTS_DIR + 'hostile.snap');
  for (const [mode, writable, unmount] of [['staged', true, false], ['read-only', true, false], ['read-only', false, false], ['staged', true, true]] as const)
    it(`a hostile snapshot never writes to a real source (${mode}, writable ${writable}, unmounted ${unmount})`, async () => {
      const tmp = tempDir();
      try {
        const root = hostFolder(tmp);
        const sh = new Shell();
        await sh.mount('/mnt/notes', new LocalFolderSource(root, { writable, id: 'notes' }), mode);
        if (unmount) sh.unmount('/mnt/notes');
        (await ShellSnapshot.load(HOSTILE)).restore(sh);
        for (const c of ['true', 'echo hi > /mnt/notes/x.md; ls /mnt/notes', 'mount']) await sh.run(c);
        expect(tree(root)).toEqual(['a.md', 'docs', join('docs', 'keep.md')]);
        const m = sh.mounts[0];
        expect(m.mode).toBe(unmount ? 'live' : mode);
        expect(m.error !== null).toBe(unmount);
        if (!unmount) {
          expect(m.held).toBe(3);
          expect([[], ['/x.md']]).toContainEqual(sh.pendingChanges('/mnt/notes').map(c => c.path)); // only the agent's own
          expect((await sh.run('ls /mnt/notes/docs')).stdout).toContain('keep.md');
        }
      } finally { rmSync(tmp, { recursive: true, force: true }); }
    });

  it('a shell made from a recording never reaches the real source', async () => {
    const src = new MemorySource({ 'a.md': 'one\n' }, 'm');
    const sh = new Shell();
    await sh.mount('/mnt/m', src, 'live');
    const rec = startRecording(sh);
    await sh.run('cat /mnt/m/a.md');
    rec.stop();
    const real = new MemorySource({ 'a.md': 'one\n' }, 'm');
    const loaded = await Recording.load(await rec.recording.save());
    const replayShell = loaded.createShell();
    await replayShell.mount('/mnt/real', real, 'live'); // the host has the real source of the same type and id
    loaded.start.restore(replayShell);
    const n = real.calls.length;
    await replayShell.run('echo replayed > /mnt/m/r.md');
    expect(real.calls.slice(n)).toEqual([]);
  });

  it('unmount forgets the source', async () => {
    const sh = new Shell();
    await sh.mount('/mnt/s', new MemorySource({ 'a.md': 'x\n' }), 'staged');
    const snap = ShellSnapshot.capture(sh);
    sh.unmount('/mnt/s');
    snap.restore(sh);
    expect(sh.mounts[0].error).toBe('unavailable: no source for memory:memory');
  });

  it('a FIFO never blocks the call', async () => {
    const tmp = tempDir();
    try {
      const root = join(tmp, 'v');
      mkdirSync(root);
      writeFileSync(join(root, 'a.md'), 'a\n');
      execFileSync('mkfifo', [join(root, 'fifo')]);
      const sh = new Shell({ maxCallDurationMs: 5000 });
      await sh.mount('/mnt/v', new LocalFolderSource(root));
      expect((await sh.run('ls /mnt/v')).stdout).toBe('a.md\n'); // not listed
      unlinkSync(join(root, 'a.md'));
      execFileSync('mkfifo', [join(root, 'a.md')]); // a listed file replaced by a FIFO
      const t = Date.now();
      const r = await sh.run('cat /mnt/v/a.md');
      expect(Date.now() - t).toBeLessThan(2000);
      expect(r.stderr).toContain('not a regular file');
    } finally { rmSync(tmp, { recursive: true, force: true }); }
  });

  it('backslash names never reach a folder, and a failed change holds up nothing', async () => {
    const tmp = tempDir();
    try {
      const root = hostFolder(tmp);
      const sh = new Shell();
      await sh.mount('/mnt/n', new LocalFolderSource(root, { writable: true, id: 'n' }), 'live');
      const r = await sh.run("echo evil > '/mnt/n/..\\..\\outside\\win.txt'");
      expect(r.stderr).toContain('not a name a file can have here');
      expect(r.stderr).not.toContain(root);
      expect(tree(root)).toEqual(['a.md', 'docs', join('docs', 'keep.md')]);
      const r2 = await sh.run('echo ok > /mnt/n/good.md'); // reported once, and the next change still applies
      expect(r2.stderr).toBe('');
      expect(readFileSync(join(root, 'good.md'), 'utf8')).toBe('ok\n');
    } finally { rmSync(tmp, { recursive: true, force: true }); }
  });

  it('source errors reach the agent only when meant for it', async () => {
    const src = new MemorySource({ 'a.md': 'a\n', 'b.md': 'b\n' });
    const sh = new Shell();
    await sh.mount('/mnt/s', src);
    src.hook = (_op, path) => {
      if (path === '/a.md') throw new MountSourceError('a.md: the page is locked');
      if (path === '/b.md') throw new Error('GET https://api/x?sig=SECRET 503');
    };
    const r = await sh.run('cat /mnt/s/a.md /mnt/s/b.md');
    expect(r.stderr).toContain('the page is locked');
    expect(r.stderr).not.toContain('SECRET');
    expect(r.stderr).toContain('the source failed');
  });

  it('local folder errors show no host path', async () => {
    const tmp = tempDir();
    try {
      const root = hostFolder(tmp);
      const sh = new Shell();
      await sh.mount('/mnt/n', new LocalFolderSource(root, { writable: true, id: 'n' }), 'live');
      chmodSync(join(root, 'docs'), 0o500);
      let r;
      try { r = await sh.run('echo x > /mnt/n/docs/new.md'); } finally { chmodSync(join(root, 'docs'), 0o755); }
      expect(r.stdout + r.stderr).not.toContain(tmp);
      if (process.getuid?.() !== 0) expect(r.stderr).toContain('mount: /mnt/n/docs/new.md');
    } finally { rmSync(tmp, { recursive: true, force: true }); }
  });

  it('long names write and leave no temporary file', async () => {
    const tmp = tempDir();
    try {
      const root = hostFolder(tmp);
      const sh = new Shell();
      await sh.mount('/mnt/n', new LocalFolderSource(root, { writable: true, id: 'n' }), 'live');
      const name = 'y'.repeat(250);
      expect((await sh.run(`echo x > /mnt/n/${name}`)).stderr).toBe('');
      expect(readFileSync(join(root, name), 'utf8')).toBe('x\n');
      expect(readdirSync(root).filter(f => f.endsWith('.tmp'))).toEqual([]);
    } finally { rmSync(tmp, { recursive: true, force: true }); }
  });

  it('a large source is listed in parts', () => withListBudget(5, async () => {
    const files: Record<string, string> = {};
    for (let i = 0; i < 4; i++) for (let j = 0; j < 3; j++) files[`d${i}/f${j}.txt`] = `${i}${j}\n`;
    const src = new MemorySource(files);
    const sh = new Shell();
    await sh.mount('/mnt/s', src);
    expect((await sh.run('ls /mnt/s/d0')).stdout).toBe('f0.txt\nf1.txt\nf2.txt\n'); // named: listed before the command
    expect((await sh.run('echo /mnt/s/*/f1.txt')).stdout).toBe('/mnt/s/d0/f1.txt\n'); // the glob looked into unlisted ones: listed after
    expect((await sh.run('echo /mnt/s/*/f1.txt')).stdout).toBe('/mnt/s/d0/f1.txt /mnt/s/d1/f1.txt /mnt/s/d2/f1.txt /mnt/s/d3/f1.txt\n');
    const fresh = new Shell();
    await fresh.mount('/mnt/s', src);
    const awk = `awk 'BEGIN { while ((getline l < "/mnt/s/d3/f0.txt") > 0) print l }'`; // a path inside a program: nothing named it
    expect((await fresh.run(awk)).stdout).toBe(''); // d3 was not listed: listed after the command
    expect((await fresh.run(awk)).stdout).toBe(''); // f0.txt was not fetched: fetched after the command
    expect((await fresh.run(awk)).stdout).toBe('30\n');
    expect((await sh.run('cat /mnt/s/d3/f2.txt; find /mnt/s -type f | wc -l')).stdout).toBe('32\n12\n');
    const deep = new Shell();
    await deep.mount('/mnt/s', new MemorySource({ 'a/b/c/f.txt': 'deep\n', ...Object.fromEntries([0, 1, 2, 3, 4].map(i => [`z${i}.txt`, ''])) }));
    expect((await deep.run('cat /mnt/s/a/b/c/f.txt')).stdout).toBe('deep\n'); // every unlisted directory on the way is listed first
  }));

  it('a restore from a file does not list everything in one call', () => withListBudget(3, async () => {
    const files: Record<string, string> = {};
    for (let i = 0; i < 10; i++) files[`d${i}/f.txt`] = 'x';
    const src = new MemorySource(files);
    const sh = new Shell();
    await sh.mount('/mnt/s', src);
    const loaded = await load(await ShellSnapshot.capture(sh).save());
    await loaded.mount('/mnt/s', src); // adopts the unavailable mount
    const before = src.calls.length;
    await loaded.run('true');
    expect(src.calls.slice(before).filter(c => c[0] === 'list')).toEqual([]);
  }));

  it('shows unknown sizes after fetching', async () => {
    class Pages extends MemorySource {
      override async list(path: string, maxEntries: number) {
        return (await super.list(path, maxEntries)).map(e => ({ ...e, size: e.isDir ? 0 : null }));
      }
    }
    const sh = new Shell();
    await sh.mount('/mnt/p', new Pages({ 'page.md': 'twelve bytes' }));
    expect((await sh.run('stat -c %s /mnt/p/page.md')).stdout).toBe('0\n');
    expect((await sh.run('cat /mnt/p/page.md > /dev/null; stat -c %s /mnt/p/page.md')).stdout).toBe('12\n');
  });

  it('a rename takes the version the source returns', async () => {
    class Moves extends MemorySource {
      override async rename(path: string, to: string, expectedVersion: string | null) {
        await super.rename(path, to, expectedVersion);
        const data = this.items.get(to)![0];
        this.items.set(to, [data, this.version()]); // like Drive: a move changes the ETag
        return this.items.get(to)![1];
      }
    }
    const src = new Moves({ 'a.md': 'a\n' });
    const sh = new Shell();
    await sh.mount('/mnt/s', src, 'live');
    await sh.run('mv /mnt/s/a.md /mnt/s/b.md');
    const r = await sh.run('echo more >> /mnt/s/b.md');
    expect(r.stderr).toBe('');
    expect(text(src.tree()['/b.md']!)).toBe('a\nmore\n');
  });

  it('mount names what the source cannot do', async () => {
    const src = new MemorySource({ 'a.md': 'a\n' });
    src.capabilities = ['write'];
    const sh = new Shell();
    await sh.mount('/mnt/s', src, 'staged');
    expect((await sh.run('mount')).stdout).toBe('memory:memory on /mnt/s type memory (rw,staged,no-delete,no-rename,no-mkdir)\n');
  });

  it('the tool description names the mounts', async () => {
    const sh = new Shell();
    await sh.mount('/mnt/vault', new MemorySource({}, 'v'));
    await sh.mount('/mnt/notes', new MemorySource({}, 'n'), 'staged');
    const d = BashTool.descriptionFor(sh);
    expect(d).toContain('`/mnt/vault` is a folder from outside, read-only.');
    expect(d).toContain('`/mnt/notes` is a folder from outside; your changes');
  });

  it('a replaced folder whose files moved out', async () => {
    const src = new MemorySource({ 'a/x.md': 'x\n', 'a/y.md': 'y\n', 'b/z.md': 'z\n', 'c.md': 'c\n', 'd/e/f.md': 'f\n' });
    const sh = new Shell();
    await sh.mount('/mnt/s', src, 'staged');
    await sh.run('echo n > /mnt/s/b/w4.md; mv /mnt/s/a /mnt/s/tmpx && mv /mnt/s/d /mnt/s/a && mv /mnt/s/tmpx /mnt/s/d; mkdir /mnt/s/d/n7; ' +
      'mv /mnt/s/d/y.md /mnt/s/m0; rm -r /mnt/s/d; echo n > /mnt/s/a/w3.md');
    expect((await sh.applyChanges('/mnt/s')).failed).toBeNull();
    expect(Object.fromEntries(Object.entries(src.tree()).map(([p, v]) => [p, v === null ? null : text(v)]))).toEqual({
      '/a': null, '/a/e': null, '/a/e/f.md': 'f\n', '/a/w3.md': 'n\n', '/b': null, '/b/w4.md': 'n\n', '/b/z.md': 'z\n', '/c.md': 'c\n', '/m0': 'y\n' });
  });

  it('random change sets apply to what the mount shows', async () => {
    for (let seed = 0; seed < 200; seed++) {
      let state = seed * 2654435761 + 1;
      const rnd = () => { state = (state * 1103515245 + 12345) % 2147483648; return state / 2147483648; };
      const int = (a: number, b: number) => a + Math.floor(rnd() * (b - a + 1));
      const choice = <T,>(xs: T[]) => xs[Math.floor(rnd() * xs.length)];
      const src = new MemorySource({ 'a/x.md': 'x\n', 'a/y.md': 'y\n', 'b/z.md': 'z\n', 'c.md': 'c\n', 'd/e/f.md': 'f\n' });
      const sh = new Shell();
      await sh.mount('/mnt/s', src, 'staged');
      for (let k = int(1, 8); k > 0; k--) {
        const files = (await sh.run('cd /mnt/s && find . -mindepth 1')).stdout.split(/\s+/).filter(Boolean);
        const dirs = [...(await Promise.all(files.map(async p => (await sh.run(`test -d /mnt/s/${p} && echo y`)).stdout ? p : null))).filter((p): p is string => p !== null), '.'];
        const pick = files.length ? choice(files) : '.';
        const op = choice(['mkdir', 'mv', 'mv', 'rm', 'edit', 'new', 'swap']);
        if (op === 'mkdir') await sh.run(`mkdir /mnt/s/${choice(dirs)}/n${int(0, 9)}`);
        else if (op === 'mv' && files.length) await sh.run(`mv /mnt/s/${pick} /mnt/s/${choice(dirs)}/m${int(0, 9)} 2>/dev/null`);
        else if (op === 'rm' && files.length) await sh.run(`rm -r /mnt/s/${pick}`);
        else if (op === 'edit' && files.length) await sh.run(`test -f /mnt/s/${pick} && echo e >> /mnt/s/${pick}`);
        else if (op === 'new') await sh.run(`echo n > /mnt/s/${choice(dirs)}/w${int(0, 9)}.md 2>/dev/null`);
        else if (op === 'swap' && files.length > 1) {
          const a = choice(files), b = choice(files.filter(f => f !== a));
          await sh.run(`mv /mnt/s/${a} /mnt/s/tmpx 2>/dev/null && mv /mnt/s/${b} /mnt/s/${a} 2>/dev/null && mv /mnt/s/tmpx /mnt/s/${b} 2>/dev/null`);
        }
      }
      const r = await sh.applyChanges('/mnt/s');
      expect([seed, r.failed]).toEqual([seed, null]);
      const want: Record<string, string | null> = {};
      for (const p of (await sh.run('cd /mnt/s && find . -mindepth 1 | sort')).stdout.split(/\s+/).filter(Boolean)) {
        const full = '/mnt/s/' + p.slice(2);
        want['/' + p.slice(2)] = (await sh.run(`test -d ${full} && echo d`)).stdout ? null : (await sh.run(`cat ${full}`)).stdout;
      }
      const got = Object.fromEntries(Object.entries(src.tree()).map(([p, v]) => [p, v === null ? null : text(v)]));
      expect([seed, got]).toEqual([seed, want]);
    }
  }, 300_000);
});

// ---------- files from every port ----------

const FIXTURE_COMMANDS = ['cat /mnt/s/a.txt; echo more >> /mnt/s/a.txt; mv /mnt/s/d/b.txt /mnt/s/d/c.txt; rm /mnt/s/old.txt; mkdir /mnt/s/e',
  'echo y > /mnt/l/x.txt', 'echo z > /mnt/l/x.txt'];

/** The scenario each port records into tests/mounts/mounts-from-<port>.topwater (TOPWATER_WRITE_FIXTURES=1 rewrites it). */
async function makeFixture(): Promise<Uint8Array> {
  const src = new MemorySource({ 'a.txt': 'a\n', 'd/b.txt': 'b\n', 'old.txt': 'o\n' });
  const live = new MemorySource({ 'x.txt': 'x\n' }, 'live');
  const sh = new Shell();
  await sh.mount('/mnt/s', src, 'staged');
  await sh.mount('/mnt/l', live, 'live');
  const rec = startRecording(sh, { metadata: { source: 'typescript port' } });
  await sh.run(FIXTURE_COMMANDS[0]);
  await sh.run(FIXTURE_COMMANDS[1]);
  live.set('x.txt', 'theirs\n');
  await sh.run(FIXTURE_COMMANDS[2]);
  rec.stop();
  return rec.recording.save();
}

async function checkFixture(raw: Uint8Array) {
  const sh = await load(raw);
  expect(sh.mounts.sort((a, b) => a.path < b.path ? -1 : 1).map(m => [m.path, m.mode, m.type, m.id, m.held]))
    .toEqual([['/mnt/l', 'live', 'memory', 'live', 1], ['/mnt/s', 'staged', 'memory', 'memory', 4]]);
  expect(sh.snapshotChanges('/mnt/s').map(c => [c.kind, c.path, c.to]))
    .toEqual([['rename', '/d/b.txt', '/d/c.txt'], ['mkdir', '/e', null], ['write', '/a.txt', null], ['delete', '/old.txt', null]]);
  expect(text(sh.snapshotChanges('/mnt/l')[0].content!)).toBe('z\n');
  const rec = await Recording.load(raw);
  expect(rec.calls.map(c => c.command)).toEqual(FIXTURE_COMMANDS);
  expect(rec.mountStart.some(x => x.op === 'list')).toBe(true);
  expect(rec.calls[0].mounts?.some(x => x.op === 'read')).toBe(true);
  expect(rec.calls[1].mounts?.some(x => x.op === 'write')).toBe(true);
  expect(rec.calls[2].stderr).toContain('changed in the source');
  expect((await rec.replay()).differences).toEqual([]);
}

describe('files from every port', () => {
  it("this port's mount fixture is current", async () => {
    const path = MOUNTS_DIR + 'mounts-from-typescript.topwater';
    if (process.env.TOPWATER_WRITE_FIXTURES === '1' || !existsSync(path)) writeFileSync(path, await makeFixture());
    await checkFixture(readFileSync(path));
  });
  for (const port of ['csharp', 'python'])
    it(`reads the mount fixture from ${port}`, async ({ skip }) => {
      const path = MOUNTS_DIR + `mounts-from-${port}.topwater`;
      if (!existsSync(path)) skip();
      await checkFixture(readFileSync(path));
    });

  it('the documented source typechecks and runs', async () => {
    const doc = readFileSync(new URL('../../docs/mounts.md', import.meta.url), 'utf8');
    const code = /```ts\n([\s\S]*?)```/.exec(doc)![1];
    const dir = mkdtempSync(fileURLToPath(new URL('../.doc-', import.meta.url)));
    try {
      const file = join(dir, 'wiki.ts');
      writeFileSync(file, code.replace("from 'topwater'", "from '../src/index.js'") + `
export async function run() {
  const shell = new Shell();
  const wiki = new WikiSource('team', { 'plan.md': '# Plan\\nDraft\\n' });
  await shell.mount('/mnt/wiki', wiki, 'staged');
  await shell.run("sed -i 's/Draft/Final/' /mnt/wiki/plan.md; echo '# Notes' > /mnt/wiki/notes.md");
  const result = await shell.applyChanges('/mnt/wiki');
  return { result, pages: (wiki as unknown as { pages: Map<string, Uint8Array> }).pages };
}
`);
      const tsc = fileURLToPath(new URL('../node_modules/.bin/tsc', import.meta.url));
      execFileSync(tsc, ['--ignoreConfig', '--noEmit', '--strict', '--target', 'es2022', '--lib', 'es2023', '--module', 'nodenext', '--moduleResolution', 'nodenext', '--types', 'node', '--skipLibCheck', file], { stdio: 'pipe' });
      const { run } = await import(pathToFileURL(file).href);
      const { result, pages } = await run();
      expect(result.failed).toBeNull();
      expect(text(pages.get('plan.md'))).toBe('# Plan\nFinal\n');
      expect(text(pages.get('notes.md'))).toBe('# Notes\n');
    } finally { rmSync(dir, { recursive: true, force: true }); }
  }, 60_000);
});
