// Ported from tests/Topwater.Tests/SymlinkTests.cs: symbolic and hard links where they could hurt. Loops never hang or
// overflow anything, a link grants no access its target doesn't, and moving or copying through links can't cut a
// directory off the tree.
import { describe, it, expect } from 'vitest';
import { Shell, ShellSnapshot } from '../src/index.js';

async function looped() {
  const sh = new Shell({ maxCallDurationMs: 20_000 });
  // a link back to its own directory, one to the root, one to itself and a two-link cycle
  await sh.run('mkdir -p /tmp/w/d/s && cd /tmp/w && echo hi > d/f && ln -s .. d/s/up && ln -s / d/root && ln -s self d/self && ln -s b d/a && ln -s a d/b');
  return sh;
}

describe('symbolic links and loops', () => {
  for (const cmd of [
    'find -L d > /dev/null', 'find d -follow > /dev/null', 'cp -rL d c', 'cp -r d c && find c -type l | sort', 'du -a d', 'ls -RL d > /dev/null', 'ls -R d',
    'tar cf t.tar d && tar tf t.tar', 'zip -qr z.zip d', 'tree d', 'shopt -s globstar; echo d/**', 'rm -r d', 'cat d/a d/self', 'realpath d/s/up/s/up/s/up/a', 'cd d/self',
  ]) {
    it(`ends quickly: ${cmd}`, async () => {
      const sh = await looped();
      const start = Date.now();
      const r = await sh.run('cd /tmp/w; ' + cmd);
      expect(Date.now() - start, cmd).toBeLessThan(10_000);
      expect(r.stderr).not.toContain('timed out');
      // and the machine still saves and loads
      await ShellSnapshot.load(await ShellSnapshot.capture(sh).save());
    });
  }

  // two links per level to the level below: no loop, but 2^30 paths; a regression fails at the test's own timeout
  for (const cmd of ['find -L out | wc -l', 'cp -rL out o2', 'grep -R x out | wc -l', 'zip -qr z.zip out', 'chmod -R u+w out', 'ls -RL out | wc -l']) {
    it(`link fan-out stops at the time limit: ${cmd}`, async () => {
      const sh = new Shell({ maxCallDurationMs: 2000 });
      await sh.run('mkdir -p /tmp/w/out/d0 && cd /tmp/w && echo x > out/d0/f && for i in $(seq 1 30); do mkdir out/d$i; ln -s ../d$((i-1)) out/d$i/a; ln -s ../d$((i-1)) out/d$i/b; done');
      const start = Date.now();
      await sh.run('cd /tmp/w; ' + cmd);
      expect(Date.now() - start, cmd).toBeLessThan(10_000);
    }, 30_000);
  }

  // cp -rL meets a directory it is already copying (through a link higher up, then down a plain child): cyclic, in either creation order
  for (const setup of ['mkdir -p w/a; cd w; ln -s /tmp/w/w a/abs; mkdir a/b', 'mkdir -p w/a/b; cd w; ln -s /tmp/w/w a/abs']) {
    it(`copying through links to a parent ends: ${setup}`, async () => {
      const sh = new Shell({ maxCallDurationMs: 5000 });
      const r = await sh.run(`mkdir -p /tmp/w && cd /tmp/w && ${setup}; echo x > a/b/f; ln -s .. a/b/up; ln -s . a/self; ln -s a l; ln -s l l2; cp -rL a zz; echo rc=$?; find zz | wc -l`);
      expect(r.stdout).toBe('rc=1\n4\n'); // zz, zz/abs, zz/b, zz/b/f
      expect(r.stderr.split('\n').filter(l => l.includes('cannot copy cyclic symbolic link')).length).toBe(5);
    }, 30_000);
  }

  it('a link grants nothing its target does not', async () => {
    const sh = new Shell();
    sh.fs.writeText('/root/secret', 's\n');
    sh.fs.writeText('/etc/app.conf', 'conf\n');
    const r = await sh.run('cd /tmp; ln -s /root/secret s; cat s; ln -s /etc/app.conf c; echo x >> c; ln /etc/app.conf h; ln -s /etc e; touch e/new; cat c');
    expect(r.stdout).toBe('conf\n');
    for (const message of ['cat: s: Permission denied', 'c: Permission denied', "ln: failed to create hard link 'h' => '/etc/app.conf': Operation not permitted", "touch: cannot touch 'e/new': Permission denied"])
      expect(r.stderr).toContain(message);
    expect(sh.fs.readText('/etc/app.conf')).toBe('conf\n');
    expect(sh.fs.exists('/etc/new')).toBe(false);
  });

  it('no directory moves into itself through a link', async () => {
    const sh = new Shell();
    const r = await sh.run('cd /tmp && mkdir -p d/sub && ln -s d/sub in && mv d in/; mv d in/x; cp -r d in/; ls -R d | head -5');
    expect(r.stdout).toBe('d:\nsub\n\nd/sub:\n');
    expect(r.stderr).toContain("mv: cannot move 'd' to a subdirectory of itself, 'in/d'");
    expect(r.stderr).toContain("cp: cannot copy a directory, 'd', into itself, 'in/d'");
  });
});

describe('hard links', () => {
  it('share one file and its bytes', async () => {
    const sh = new Shell({ maxFileSystemBytes: 64 << 20 });
    await sh.run("cd /tmp && printf '%10000s' '' > big");
    const used = sh.fs.usedBytes;
    const r = await sh.run("cd /tmp && for i in 1 2 3; do ln big h$i; done; stat -c %h big; rm big h1 h2; stat -c '%h %s' h3");
    expect(r.stdout).toBe('4\n1 10000\n');
    expect(sh.fs.usedBytes).toBe(used);
    await sh.run('rm /tmp/h3');
    expect(sh.fs.usedBytes).toBe(used - 10000);
  });
});
