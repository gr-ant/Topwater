// Ported from tests/Topwater.Tests/PermissionTests.cs: what real Linux and coreutils do for a non-root user.
import { describe, it, expect } from 'vitest';
import { Shell } from '../src/index.js';
import data from './data/PermissionTests.json' with { type: 'json' };

/** The app puts a readable file in root's private home and a root-owned tree with one secret in /srv. */
function setup() {
  const sh = new Shell();
  sh.fs.writeText('/root/notes.txt', 'private\n');
  sh.fs.mkdir('/srv/app/sub', true);
  sh.fs.writeText('/srv/app/readme', 'hello\n');
  sh.fs.writeText('/srv/app/sub/data', 'data\n');
  sh.fs.writeText('/srv/app/secret', 's3cret\n');
  sh.fs.chmod('/srv/app/secret', 0o600);
  return sh;
}

async function expectErrors(sh: Shell, cases: [string, string][], exitCode?: number) {
  for (const [cmd, err] of cases) {
    const r = await sh.run(cmd);
    expect(r.stderr, cmd).toBe(err);
    if (exitCode !== undefined) expect(r.exitCode, cmd).toBe(exitCode); else expect(r.exitCode, cmd).not.toBe(0);
  }
}

describe('permissions', () => {
  for (const [cmd, err] of data['PermissionTests.UnsearchableDirectoryHidesItsEntries'] as string[][]) {
    if (cmd.startsWith('to-markdown')) continue; // documents are not ported
    it(`unsearchable: ${cmd}`, async () => {
      const sh = setup();
      const r = await sh.run(cmd);
      expect(r.exitCode).not.toBe(0);
      expect(r.stdout).toBe('');
      expect(r.stderr.replaceAll('bash: ', '')).toBe(err.replaceAll('bash: ', ''));
      expect(sh.fs.readText('/root/notes.txt')).toBe('private\n'); // the app still reads it
    });
  }

  it('tests inside an unsearchable directory are false', async () => {
    const sh = setup();
    expect((await sh.run('[ -e /root/notes.txt ]; a=$?; [ -r /root/notes.txt ]; b=$?; [[ -f /root/notes.txt ]]; c=$?; test -w /root/notes.txt; d=$?; [ -s /root/notes.txt ]; echo $a $b $c $d $?')).stdout).toBe('1 1 1 1 1\n');
    expect((await sh.run('[ -d /root ] && [ ! -r /root ] && echo ok')).stdout).toBe('ok\n');
  });

  it('a readable but unsearchable directory lists names but opens nothing', async () => {
    const sh = setup();
    sh.fs.chmod('/srv/app', 0o744);
    expect((await sh.run('ls /srv/app')).stdout).toBe('readme\nsecret\nsub\n');
    expect((await sh.run('cat /srv/app/readme')).stderr).toBe('cat: /srv/app/readme: Permission denied\n');
  });

  it('cp -r skips unreadable children', async () => {
    const sh = setup();
    let r = await sh.run('cp -r /srv/app ~/x');
    expect([r.exitCode, r.stderr]).toEqual([1, "cp: cannot open '/srv/app/secret' for reading: Permission denied\n"]);
    expect((await sh.run('ls ~/x; cat ~/x/readme ~/x/sub/data')).stdout).toBe('readme\nsub\nhello\ndata\n');
    r = await sh.run('cd /srv && cp -r app/ ~/y; cat ~/y/secret');
    expect(r.stderr).toBe("cp: cannot open 'app/secret' for reading: Permission denied\ncat: /home/agent/y/secret: No such file or directory\n");
    r = await sh.run('cp /srv/app/secret ~/s');
    expect([r.exitCode, r.stderr]).toEqual([1, "cp: cannot open '/srv/app/secret' for reading: Permission denied\n"]);
    expect(sh.fs.exists('/home/agent/s')).toBe(false);
  });

  it('cp onto an existing file keeps its owner', async () => {
    const sh = setup();
    sh.fs.writeText('/tmp/shared', 'old');
    sh.fs.chmod('/tmp/shared', 0o666);
    await sh.run('echo new > ~/n; cp ~/n /tmp/shared');
    expect((await sh.run("cat /tmp/shared; stat -c '%U %a' /tmp/shared")).stdout).toBe('new\nroot 666\n');
  });

  it('sticky /tmp', async () => {
    const sh = setup();
    sh.fs.writeText('/tmp/rootfile', 'x');
    sh.fs.chmod('/tmp/rootfile', 0o666);
    sh.fs.mkdir('/tmp/rootdir');
    sh.fs.chmod('/tmp/rootdir', 0o777);
    await expectErrors(sh, [
      ['rm /tmp/rootfile', "rm: cannot remove '/tmp/rootfile': Operation not permitted\n"],
      ['rm -rf /tmp/rootdir', "rm: cannot remove '/tmp/rootdir': Operation not permitted\n"],
      ['mv /tmp/rootfile ~/f', "mv: cannot move '/tmp/rootfile' to '/home/agent/f': Operation not permitted\n"],
      ['echo y > ~/f; mv ~/f /tmp/rootfile', "mv: cannot move '/home/agent/f' to '/tmp/rootfile': Operation not permitted\n"],
    ], 1);
    expect(sh.fs.readText('/tmp/rootfile')).toBe('x');
    expect(sh.fs.isDirectory('/tmp/rootdir')).toBe(true);
    expect((await sh.run('echo a > /tmp/mine && mv /tmp/mine /tmp/mine2 && rm /tmp/mine2 && echo b > /tmp/rootdir/f && rm /tmp/rootdir/f && echo ok')).stdout).toBe('ok\n');
  });

  it('directory write permission governs creating, removing and renaming', async () => {
    const sh = setup();
    await sh.run('mkdir -p ro/sub; echo b > ro/b; echo c > ro/sub/c; chmod 555 ro/sub; chmod 555 ro');
    await expectErrors(sh, [
      ['touch ro/new', "touch: cannot touch 'ro/new': Permission denied\n"],
      ['echo x > ~/ro/new', 'bash: /home/agent/ro/new: Permission denied\n'],
      ['mkdir ro/d', "mkdir: cannot create directory 'ro/d': Permission denied\n"],
      ['rm ro/b', "rm: cannot remove 'ro/b': Permission denied\n"],
      ['rm -r ro/sub', "rm: cannot remove 'ro/sub/c': Permission denied\n"],
      ['mv ro/b x', "mv: cannot move 'ro/b' to 'x': Permission denied\n"],
      ['mv ro/sub x', "mv: cannot move 'ro/sub' to 'x': Permission denied\n"],
      ['echo y > y; mv y ro/', "mv: cannot move 'y' to 'ro/y': Permission denied\n"],
    ]);
    expect((await sh.run('cat ro/b ro/sub/c')).stdout).toBe('b\nc\n');
    expect((await sh.run('chmod 755 ro; mv ro/sub other')).stderr).toBe("mv: cannot move 'ro/sub' to 'other': Permission denied\n");
    expect((await sh.run('chmod 755 ro/sub && mv ro/sub other && echo ok')).stdout).toBe('ok\n');
  });

  it('replaces an unwritable file in its own directory', async () => {
    const sh = setup();
    sh.fs.writeText('/home/agent/cfg', 'app');
    sh.fs.setOwner('/home/agent/cfg', 'root');
    expect((await sh.run('echo agent > ~/cfg')).stderr).toBe('bash: /home/agent/cfg: Permission denied\n');
    expect((await sh.run("stat -c '%U' ~ | tr '\\n' ' '; stat -c %U ~/cfg; rm ~/cfg && echo agent > ~/cfg && cat ~/cfg | tr '\\n' ' '; stat -c %U ~/cfg")).stdout)
      .toBe('agent root\nagent agent\n');
  });

  it('chmod and chown need ownership', async () => {
    const sh = setup();
    const r = await sh.run('chmod 666 /srv/app/secret; chown agent /srv/app/secret');
    expect(r.stderr).toBe("chmod: changing permissions of '/srv/app/secret': Operation not permitted\nchown: changing ownership of '/srv/app/secret': Operation not permitted\n");
    expect(sh.fs.stat('/srv/app/secret').mode).toBe(0o600);
  });

  it('the app is unrestricted while an agent command awaits; registered commands run as the agent', async () => {
    const sh = setup();
    const run = sh.run('sleep 0.3');
    sh.fs.writeText('/etc/app.conf', 'x');
    expect(sh.fs.readText('/root/notes.txt')).toBe('private\n');
    await run;
    expect(sh.fs.runAs).toBeNull();
    sh.register('peek', ctx => { ctx.stdout.write(`${ctx.fs.canRead('/root/notes.txt')}\n`); });
    expect((await sh.run('peek')).stdout).toBe('false\n');
  });
});
