// git: identity, and hostile repositories (corrupt or oversized objects, bad indexes, links inside .git, tree entries
// that point outside the work tree). The everyday behaviour is pinned by tests/corpus/git.jsonl. Mirrors
// python/tests/test_git.py.
import { describe, expect, it } from 'vitest';
import { createHash } from 'node:crypto';
import * as zlib from 'node:zlib';
import { readFileSync } from 'node:fs';
import { Shell, type ShellOptions } from '../src/index.js';
import { sha1 } from '../src/sha1.js';
import { zlibSync } from '../src/compress.js';
import { CACHE_BYTES, ObjectCache } from '../src/commands/gitpack.js';

const IDENT = 'export GIT_AUTHOR_NAME=A GIT_AUTHOR_EMAIL=a@example.com GIT_COMMITTER_NAME=A GIT_COMMITTER_EMAIL=a@example.com ' +
  "GIT_AUTHOR_DATE='2026-01-02T03:04:05Z' GIT_COMMITTER_DATE='2026-01-02T03:04:05Z'";

async function repo(options: Partial<ShellOptions> = {}): Promise<Shell> {
  const sh = new Shell(options);
  const r = await sh.run(`${IDENT}; mkdir -p /tmp/w && cd /tmp/w && git init -q && echo a > f && git add f && git commit -qm one`);
  expect(r.exitCode, r.stderr).toBe(0);
  return sh;
}

/** Writes an object the way a hostile repository could (raw: the stored bytes, whatever they hash to). */
function put(sh: Shell, kind: string, body: Uint8Array, raw?: Uint8Array): string {
  const data = Buffer.concat([Buffer.from(`${kind} ${body.length}\0`), body]);
  const sha = createHash('sha1').update(data).digest('hex');
  sh.fs.mkdir(`/tmp/w/.git/objects/${sha.slice(0, 2)}`, true);
  sh.fs.writeBytes(`/tmp/w/.git/objects/${sha.slice(0, 2)}/${sha.slice(2)}`, raw ?? zlib.deflateSync(data));
  return sha;
}

interface PackObject { kind: string; data: Uint8Array; name: string }
const oid = (kind: string, data: Uint8Array) => createHash('sha1').update(Buffer.concat([Buffer.from(`${kind} ${data.length}\0`), data])).digest('hex');

/** A pack and its v2 index holding whole objects (no deltas), like tests/git/make_packs.py's build(). */
function buildPack(objs: PackObject[]): [Uint8Array, Uint8Array] {
  const TYPES: Record<string, number> = { commit: 1, tree: 2, blob: 3, tag: 4 };
  const parts: Buffer[] = [Buffer.from('PACK'), Buffer.alloc(8)];
  parts[1].writeUInt32BE(2, 0);
  parts[1].writeUInt32BE(objs.length, 4);
  let len = 12;
  const offsets: number[] = [];
  for (const o of objs) {
    offsets.push(len);
    let size = o.data.length;
    const head = [(TYPES[o.kind] << 4) | (size & 15)];
    size >>= 4;
    while (size) { head[head.length - 1] |= 0x80; head.push(size & 0x7f); size >>= 7; }
    const entry = Buffer.concat([Buffer.from(head), zlib.deflateSync(o.data)]);
    parts.push(entry);
    len += entry.length;
  }
  let pack = Buffer.concat(parts);
  pack = Buffer.concat([pack, createHash('sha1').update(pack).digest()]);
  const order = objs.map((_, i) => i).sort((a, b) => objs[a].name < objs[b].name ? -1 : 1);
  const fan = Buffer.alloc(1024);
  const counts = new Array(256).fill(0);
  for (const o of objs) counts[parseInt(o.name.slice(0, 2), 16)]++;
  for (let b = 0, t = 0; b < 256; b++) { t += counts[b]; fan.writeUInt32BE(t, 4 * b); }
  const tail = Buffer.alloc(8 * objs.length);
  order.forEach((i, k) => tail.writeUInt32BE(offsets[i], 4 * objs.length + 4 * k));
  let idx = Buffer.concat([Buffer.from([0xff, 0x74, 0x4f, 0x63, 0, 0, 0, 2]), fan, ...order.map(i => Buffer.from(objs[i].name, 'hex')), tail, pack.subarray(pack.length - 20)]);
  idx = Buffer.concat([idx, createHash('sha1').update(idx).digest()]);
  return [pack, idx];
}

const PACKS: { name: string; files: Record<string, string>; script: string; exitCode: number; stdout?: string; stderr?: string;
  timeLimit?: number; cacheBound?: boolean }[] =
  JSON.parse(readFileSync(new URL('../../tests/git/packs.json', import.meta.url), 'utf8'));

describe('packfiles (tests/git/packs.json, shared with the other ports)', () => {
  for (const c of PACKS) {
    it(c.name, async () => {
      const sh = await repo({ maxCallDurationMs: (c.cacheBound ? 60 : c.timeLimit ?? 10) * 1000 });
      sh.fs.mkdir('/tmp/w/.git/objects/pack', true);
      for (const [name, data] of Object.entries(c.files)) sh.fs.writeBytes(`/tmp/w/.git/objects/pack/${name}`, zlib.gunzipSync(Buffer.from(data, 'base64')));
      ObjectCache.peak = 0;
      const start = Date.now();
      const r = await sh.run(c.script);
      // the cache-budget case hashes hundreds of MB of objects: slow under load, and plain JavaScript SHA-1 runs about 200 MB/s
      expect(Date.now() - start).toBeLessThan(c.cacheBound ? 30_000 : ((c.timeLimit ?? 2) + 3) * 1000);
      if (c.cacheBound) {
        expect(ObjectCache.peak).toBeGreaterThan(0);
        expect(ObjectCache.peak).toBeLessThanOrEqual(CACHE_BYTES);
      }
      expect(r.exitCode, r.stderr).toBe(c.exitCode);
      if (c.stdout !== undefined) expect(r.stdout).toBe(c.stdout);
      if (c.stderr !== undefined) {
        expect(r.stderr).toContain(c.stderr);
        expect(r.stderr.startsWith('fatal: ')).toBe(true);
      }
    }, 60_000);
  }
});

describe('packed-refs and upstreams', () => {
  it('ignores packed-refs names that leave refs/', async () => {
    const sh = await repo();
    await sh.run("s=$(git rev-parse HEAD); printf '%s refs/heads/../../outside\\n%s refs/remotes/../x\\n%s refs/remotes/o/cr\\r\\n"
      + "%s refs/tags/.hidden\\n%s refs/heads/ok\\n' $s $s $s $s $s > .git/packed-refs");
    let r = await sh.run('git branch -a; git log --oneline --decorate -1; git tag');
    expect(r.exitCode).toBe(0);
    for (const bad of ['..', 'hidden', '\r']) expect(r.stdout).not.toContain(bad);
    expect(r.stdout).toContain('ok');
    r = await sh.run('git checkout x; echo $?; cat .git/config');
    expect(r.stdout.startsWith('1\n')).toBe(true);
    expect(r.stdout).not.toContain('remote = ..');
  });

  it('explains @{u} failures and refuses ambiguous remote branch names', async () => {
    const sh = await repo();
    let r = await sh.run('git rev-parse @{u}; git rev-parse nosuch@{u}; git checkout -q --detach; git rev-parse HEAD@{upstream}');
    expect(r.stderr).toBe("fatal: no upstream configured for branch 'master'\nfatal: no such branch: 'nosuch'\nfatal: HEAD does not point to a branch\n");
    await sh.run('git checkout -q master; for r in origin up; do git config remote.$r.fetch "+refs/heads/*:refs/remotes/$r/*"; mkdir -p .git/refs/remotes/$r; git rev-parse HEAD > .git/refs/remotes/$r/b; done');
    r = await sh.run('git checkout b');
    expect([r.exitCode, r.stderr]).toEqual([128, "fatal: 'b' matched multiple (2) remote tracking branches\n"]);
    await sh.run('rm -r .git/refs/remotes/up; echo f > b');
    r = await sh.run('git checkout b');
    expect(r.exitCode).toBe(128);
    expect(r.stderr).toContain('could be both a local file and a tracking branch');
  });
});

const commitBody = (tree: string) => Buffer.from(`tree ${tree}\nauthor A <a@b> 1 +0000\ncommitter A <a@b> 1 +0000\n\nx\n`);
const commitWithTree = (sh: Shell, entries: Uint8Array) => put(sh, 'commit', commitBody(put(sh, 'tree', entries)));

describe('sha1', () => {
  it('matches the published test vectors and node:crypto', () => {
    const enc = new TextEncoder();
    expect(sha1(enc.encode(''))).toBe('da39a3ee5e6b4b0d3255bfef95601890afd80709');
    expect(sha1(enc.encode('abc'))).toBe('a9993e364706816aba3e25717850c26c9cd0d89d');
    expect(sha1(enc.encode('abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq'))).toBe('84983e441c3bd26ebaae4aa1f95129e5e54670f1');
    expect(sha1(enc.encode('a'.repeat(1_000_000)))).toBe('34aa973cd4c4daa4f61eeb2bdbad27316534016f');
    for (let n = 0; n < 200; n++) {
      const b = Buffer.from(Array.from({ length: n }, (_, i) => (i * 31 + n) & 255));
      expect(sha1(b)).toBe(createHash('sha1').update(b).digest('hex'));
    }
  });
});

describe('zlibSync', () => {
  it('round-trips through node:zlib', () => {
    const rep = (n: number) => Buffer.from('the quick brown fox jumps over the lazy dog. '.repeat(Math.ceil(n / 45)).slice(0, n));
    const noise = (n: number) => Buffer.from(Array.from({ length: n }, (_, i) => (i * 2654435761 >>> 13) & 255));
    for (const n of [0, 1, 2, 3, 100, 5000, 70000, 300000]) for (const d of [noise(n), rep(n), Buffer.alloc(n)]) {
      expect(Buffer.from(zlib.inflateSync(zlibSync(d)))).toEqual(d);
    }
    expect(zlibSync(rep(1 << 20)).length).toBeLessThan(20_000);
  });
});

describe('git', () => {
  it('a commit without an identity fails like real git', async () => {
    const r = await new Shell().run('cd /tmp && git init -q r && cd r && git commit -q --allow-empty -m x');
    expect(r.exitCode).toBe(128);
    expect(r.stderr).toContain('*** Please tell me who you are.');
    expect(r.stderr.endsWith("fatal: unable to auto-detect email address (got 'agent@topwater.(none)')\n")).toBe(true);
  });

  it('the host can preset an identity through the environment or .gitconfig', async () => {
    let sh = new Shell();
    for (const k of ['AUTHOR', 'COMMITTER']) { sh.environment.set(`GIT_${k}_NAME`, 'Host Set'); sh.environment.set(`GIT_${k}_EMAIL`, 'host@example.com'); }
    let r = await sh.run("cd /tmp && git init -q r && cd r && git commit -q --allow-empty -m x && git log --format='%an <%ae>'");
    expect(r.stdout).toBe('Host Set <host@example.com>\n');
    sh = new Shell();
    sh.fs.writeText('/home/agent/.gitconfig', '[user]\n\tname = File Set\n\temail = file@example.com\n');
    r = await sh.run("cd /tmp && git init -q r && cd r && git commit -q --allow-empty -m x && git log --format='%an <%ae>'");
    expect(r.stdout).toBe('File Set <file@example.com>\n');
  });

  it('two deterministic shells make the same commit ids without GIT_*_DATE', async () => {
    const ids = await Promise.all([1, 2].map(async () => {
      const sh = new Shell({ startTime: Date.UTC(2026, 0, 2, 3, 4, 5), randomSeed: 1 });
      const r = await sh.run('cd /tmp && git init -q r && cd r && git config user.name A && git config user.email a@b && echo x > f && git add f && git commit -qm one && git rev-parse HEAD && git log --format=%ad');
      expect(r.exitCode, r.stderr).toBe(0);
      return r.stdout;
    }));
    expect(ids[0]).toBe(ids[1]);
    expect(ids[0]).toContain('Fri Jan 2 03:04:05 2026 +0000');
  });

  it('an object whose contents do not match its name is corrupt', async () => {
    const sh = await repo();
    const head = (await sh.run('git rev-parse HEAD')).stdout.trim();
    const path = `/tmp/w/.git/objects/${head.slice(0, 2)}/${head.slice(2)}`;
    sh.fs.writeBytes(path, zlib.deflateSync(Buffer.from('commit 5\0hello')));
    const r = await sh.run('git log');
    expect(r.exitCode).toBe(128);
    expect(r.stderr).toBe(`fatal: object ${head} is corrupt\n`);
    sh.fs.writeBytes(path, Buffer.from('not zlib at all'));
    expect((await sh.run('git status')).exitCode).toBe(128);
  });

  it('an oversized object stops at the string limit', async () => {
    const sh = await repo({ maxStringBytes: 1 << 20 });
    const sha = put(sh, 'blob', new Uint8Array(4 << 20)); // inflates past the limit: never held whole
    const r = await sh.run(`git cat-file -s ${sha}`);
    expect(r.exitCode).toBe(128);
    expect(r.stderr).toContain('too large');
  });

  it('odd revisions and patterns fail cleanly', async () => {
    const sh = await repo();
    let r = await sh.run("git rev-parse $'HEAD\\n'");
    expect(r.exitCode).toBe(128);
    expect(r.stderr.startsWith('fatal: ambiguous argument')).toBe(true);
    r = await sh.run("git log --oneline -- '[z-a]'; git status -s -- '[z-a]'");
    expect([r.exitCode, r.stdout, r.stderr]).toEqual([0, '', '']);
  });

  it('a garbage index is reported, not trusted', async () => {
    const sh = await repo();
    sh.fs.writeBytes('/tmp/w/.git/index', Buffer.concat([Buffer.from('DIRC\0\0\0\x02\0\0\0\x05', 'latin1'), Buffer.alloc(40, 0xff)]));
    const r = await sh.run('git status');
    expect(r.exitCode).toBe(128);
    expect(r.stderr.startsWith('fatal: index file corrupt')).toBe(true);
  });

  it('git never follows a symbolic link inside .git', async () => {
    const sh = await repo();
    await sh.run('mkdir -p /tmp/elsewhere/heads && echo 0123 > /tmp/elsewhere/heads/x && rm -rf .git/refs && ln -s /tmp/elsewhere .git/refs');
    const r = await sh.run('git branch evil');
    expect(r.exitCode).toBe(128);
    expect(r.stderr).toContain('symbolic link');
    expect((await sh.run('cat /tmp/elsewhere/heads/x')).stdout).toBe('0123\n');
  });

  it('tree entries that leave the work tree are refused', async () => {
    const sh = await repo();
    const blob = put(sh, 'blob', Buffer.from('owned\n'));
    for (const name of ['..', '.git', 'a/b']) {
      const c = commitWithTree(sh, Buffer.concat([Buffer.from(`100644 ${name}\0`), Buffer.from(blob, 'hex')]));
      const r = await sh.run(`git checkout -q ${c}`);
      expect(r.exitCode, name).toBe(128);
      expect(r.stderr, name).toContain('invalid path');
    }
    expect(sh.fs.exists('/tmp/owned')).toBe(false);
    expect((await sh.run('git status -s')).stdout).toBe('');
  });

  it('checkout replaces a symbolic link directory instead of writing through it', async () => {
    const sh = await repo();
    await sh.run('mkdir d && echo x > d/f && git add d && git commit -qm d && rm -rf d && mkdir /tmp/outside && ln -s /tmp/outside d');
    expect((await sh.run('git status -s')).stdout).toBe(' D d/f\n?? d\n');
    await sh.run('rm d && ln -s /tmp/outside d && git checkout -- d/f');
    expect(sh.fs.list('/tmp/outside')).toEqual([]);
    expect(sh.fs.isSymlink('/tmp/w/d')).toBe(false);
    expect((await sh.run('cat d/f')).stdout).toBe('x\n');
  });

  it('deeply nested trees end with an error', async () => {
    const sh = await repo();
    const blob = put(sh, 'blob', Buffer.from('x\n'));
    let tree = put(sh, 'tree', Buffer.concat([Buffer.from('100644 f\0'), Buffer.from(blob, 'hex')]));
    for (let i = 0; i < 300; i++) tree = put(sh, 'tree', Buffer.concat([Buffer.from('40000 d\0'), Buffer.from(tree, 'hex')]));
    const c = put(sh, 'commit', commitBody(tree));
    const r = await sh.run(`git show --stat ${c}`);
    expect(r.exitCode).toBe(128);
    expect(r.stderr).toContain('nested too deeply');
  });

  it('a long history walk respects the call time limit', async () => {
    const sh = await repo({ maxCallDurationMs: 1000 });
    let r = await sh.run('for i in $(seq 400); do git commit -q --allow-empty -m $i; done');
    expect([0, 124]).toContain(r.exitCode);
    r = await sh.run('git log --oneline | wc -l');
    expect([0, 124]).toContain(r.exitCode);
  }, 30_000);

  it('a call cut off by the time limit never leaves a ref or index entry naming a missing object', async () => {
    let aborted = 0;
    for (const ms of [150, 300, 450]) {
      const sh = await repo({ maxCallDurationMs: ms });
      const r = await sh.run('for i in $(seq 2000); do seq $i > f$i; git add -A; git commit -qm $i; git tag t$i; git stash -q 2>/dev/null; done');
      if (r.exitCode === 124) aborted++;
      // every object the refs, reflogs and index name is there and readable
      const check = await sh.run('git log --all --format=%H >/dev/null && git status >/dev/null && git ls-files -s | while read m s rest; do git cat-file -e $s || echo missing $s; done; ' +
        'cat .git/logs/HEAD | while read o n rest; do [ $n = 0000000000000000000000000000000000000000 ] || git cat-file -e $n || echo missing $n; done');
      expect(check.exitCode, check.stderr).toBe(0);
      expect(check.stdout).toBe('');
    }
    expect(aborted).toBeGreaterThan(0);
  }, 60_000);

  it('a packed repository takes new loose objects, and gc and repack are not available', async () => {
    const sh = await repo();
    await sh.run('echo b > g && git add g && git commit -qm two');
    // pack everything the way git gc would leave it: objects only in a pack
    const names = (await sh.run('find .git/objects -type f | cut -d/ -f3,4 | tr -d /')).stdout.split(/\s+/).filter(Boolean);
    const objs = names.map(sha => {
      const raw = zlib.inflateSync(sh.fs.readBytes(`/tmp/w/.git/objects/${sha.slice(0, 2)}/${sha.slice(2)}`));
      const sp = raw.indexOf(0x20), nul = raw.indexOf(0);
      return { kind: raw.subarray(0, sp).toString(), data: raw.subarray(nul + 1), name: sha };
    });
    const [pack, idx] = buildPack(objs);
    await sh.run('rm -rf .git/objects/?? && mkdir -p .git/objects/pack');
    sh.fs.writeBytes('/tmp/w/.git/objects/pack/pack-1.pack', pack);
    sh.fs.writeBytes('/tmp/w/.git/objects/pack/pack-1.idx', idx);
    let r = await sh.run('git log --oneline && git status -s && echo c > h && git add h && git commit -qm three && git log --oneline | wc -l' +
      ' && find .git/objects -type f | grep -vc pack && git rev-parse --short HEAD~2 | wc -c');
    expect(r.exitCode, r.stderr).toBe(0);
    expect(r.stdout.split('\n').slice(-4)).toEqual(['3', '3', '8', '']);
    r = await sh.run('git gc; git repack');
    expect(r.exitCode).toBe(128);
    expect(r.stderr).toContain('not available');
  });

  it('reads a 5000-object pack quickly', async () => {
    const sh = await repo();
    const objs: PackObject[] = [];
    let text = '';
    for (let n = 0; n < 5000; n++) {
      text += `line ${n}\n`;
      const data = Buffer.from(text.slice(-2000));
      objs.push({ kind: 'blob', data, name: oid('blob', data) });
    }
    const [pack, idx] = buildPack(objs);
    sh.fs.mkdir('/tmp/w/.git/objects/pack', true);
    sh.fs.writeBytes('/tmp/w/.git/objects/pack/pack-1.pack', pack);
    sh.fs.writeBytes('/tmp/w/.git/objects/pack/pack-1.idx', idx);
    const start = Date.now();
    const r = await sh.run(`git cat-file -s ${objs[4999].name}; git cat-file -t ${objs[10].name.slice(0, 8)}`);
    expect(r.stdout).toBe('2000\nblob\n');
    expect(Date.now() - start).toBeLessThan(2000);
  });

  it('graph refuses history it cannot draw', async () => {
    const sh = await repo();
    const r = await sh.run('git checkout -qb side && git commit -q --allow-empty -m s && git checkout -q master && ' +
      'git commit -q --allow-empty -m m && git merge -q side -m merged && git log --graph --oneline');
    expect(r.exitCode).toBe(128);
    expect(r.stderr).toContain("git log --format='%h %p %s'");
    expect(r.stdout).toBe('');
    expect((await sh.run('git log --graph --oneline --all side')).exitCode).toBe(128);
    expect((await sh.run('git log --graph --oneline side')).stdout.split('* ').length - 1).toBe(2);
  });

  it('a path too deep in the index is refused quickly', async () => {
    const sh = await repo({ maxCallDurationMs: 2000 });
    const blob = (await sh.run('git rev-parse HEAD:f')).stdout.trim();
    const name = Buffer.from(Array(5000).fill('d').join('/') + '/f');
    const head = Buffer.alloc(40);
    head.writeUInt32BE(0o100644, 24);
    head.writeUInt32BE(2, 36);
    const flags = Buffer.alloc(2);
    flags.writeUInt16BE(0xfff);
    let entry = Buffer.concat([head, Buffer.from(blob, 'hex'), flags, name]);
    entry = Buffer.concat([entry, Buffer.alloc(8 - entry.length % 8)]);
    const count = Buffer.alloc(8);
    count.writeUInt32BE(2, 0);
    count.writeUInt32BE(1, 4);
    const body = Buffer.concat([Buffer.from('DIRC'), count, entry]);
    sh.fs.writeBytes('/tmp/w/.git/index', Buffer.concat([body, createHash('sha1').update(body).digest()]));
    for (const cmd of ['git status', 'git commit -qm x', 'git checkout -- .']) {
      const r = await sh.run(cmd);
      expect(r.exitCode, cmd).toBe(128);
      expect(r.stderr, cmd).toContain('directories deep');
    }
  });

  it('a symbolic HEAD cannot lead outside .git', async () => {
    const sh = await repo();
    sh.fs.writeText('/tmp/w/.git/HEAD', 'ref: refs/heads/../../../../victim/newref\n');
    const r = await sh.run('git commit -q --allow-empty -m x');
    expect(r.exitCode).toBe(128);
    expect(sh.fs.exists('/tmp/victim')).toBe(false);
    sh.fs.writeText('/tmp/w/.git/HEAD', 'ref: ../../victim\n');
    expect((await sh.run('git status')).stderr).toContain('not a git repository');
  });

  it('agent patterns stop at the time limit', async () => {
    const sh = await repo({ maxCallDurationMs: 1000 });
    const star = '*a'.repeat(30) + 'b';
    sh.fs.writeText('/tmp/w/.gitignore', star + '\n');
    sh.fs.writeText('/tmp/w/' + 'a'.repeat(200), 'x');
    for (const cmd of ['git status', `git add -- '${star}'`, "git log --grep='(a+)+b'"]) {
      const start = Date.now();
      await sh.run(cmd);
      expect(Date.now() - start, cmd).toBeLessThan(10_000);
    }
    const r = await sh.run("git log --grep='('");
    expect(r.exitCode).toBe(128);
    expect(r.stderr).toContain('invalid regular expression');
  }, 60_000);

  it('a long ignore file over many files stays under the time limit', async () => {
    const sh = await repo({ maxCallDurationMs: 1000 });
    sh.fs.writeText('/tmp/w/.gitignore', Array.from({ length: 20_000 }, (_, i) => `pat${i}*x\n`).join(''));
    await sh.run('mkdir many && for i in $(seq 3000); do : > many/f$i; done');
    const start = Date.now();
    await sh.run('git add .');
    expect(Date.now() - start).toBeLessThan(10_000);
  }, 60_000);

  it('editor settings and identity cleanup', async () => {
    const sh = await repo();
    let r = await sh.run('EDITOR=vim git commit --allow-empty');
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toContain('no editor can run');
    r = await sh.run('GIT_EDITOR=true git commit -q --amend && git log --format=%s');
    expect(r.stdout).toBe('one\n');
    r = await sh.run("GIT_AUTHOR_NAME=$'Evil\\nName <x>' git commit -q --allow-empty -m n && git log -1 --format='%an' && git status -s");
    expect(r.exitCode).toBe(0);
    expect(r.stdout).toBe('EvilName x\n');
  });

  it('writes zlib objects real tools can read', async () => {
    const sh = await repo();
    const head = (await sh.run('git rev-parse HEAD')).stdout.trim();
    const raw = sh.fs.readBytes(`/tmp/w/.git/objects/${head.slice(0, 2)}/${head.slice(2)}`);
    const data = zlib.inflateSync(raw);
    expect(createHash('sha1').update(data).digest('hex')).toBe(head);
  });

  it('apply refuses paths outside the work tree', async () => {
    const sh = await repo();
    for (const name of ['../escape', '.git/config', '/etc/passwd']) {
      sh.fs.writeBytes('/tmp/p.diff', new TextEncoder().encode(`--- a/${name}\n+++ b/${name}\n@@ -0,0 +1 @@\n+x\n`.replace('a//', 'a/').replace('b//', 'b/')));
      const r = await sh.run('cd /tmp/w && git apply /tmp/p.diff; echo $?');
      expect(r.stdout.trim().split('\n').pop(), name).not.toBe('0');
    }
    expect(sh.fs.exists('/tmp/escape')).toBe(false);
    expect((await sh.run('cat /tmp/w/.git/config')).stdout.split('x\n').length - 1).toBe(0);
  });

  it('bisect run with skips still finds the commit', async () => {
    const sh = await repo();
    const r = await sh.run('cd /tmp/w && for i in 2 3 4 5 6; do echo $i > f; git commit -qam c$i; done; git bisect start HEAD HEAD~5 >/dev/null; ' +
      "git bisect run sh -c 'n=$(cat f); [ $n = 3 ] && exit 125; [ $n -lt 5 ]' | tail -1; git log -1 --format=%s");
    expect(r.stdout, r.stderr).toBe('bisect found first bad commit\nc5\n');
  });
});
