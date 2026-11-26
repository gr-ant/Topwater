// Ported from tests/Topwater.Tests/AgentFeatureTests.cs (the [Fact] tests) and BashCompatClock.cs.
import { describe, it, expect } from 'vitest';
import { Shell } from '../src/index.js';

async function out(sh: Shell, cmd: string) {
  const r = await sh.run(cmd);
  expect(r.exitCode, `\`${cmd}\` exited ${r.exitCode}: ${r.stderr}`).toBe(0);
  return r.stdout;
}

describe('agent features', () => {
  it('looks app commands up on PATH like programs', async () => {
    const sh = new Shell();
    sh.register('greet', ctx => { ctx.stdout.write(`app ${ctx.args.join(' ')}\n`); });
    expect(await out(sh, 'greet a; command -v greet')).toBe('app a\n/usr/bin/greet\n');
    // a script earlier on PATH shadows it, as a wrapper would
    await out(sh, "mkdir -p ~/bin && printf '#!/bin/sh\\necho wrapped; /usr/bin/greet \"$@\"\\n' > ~/bin/greet && chmod +x ~/bin/greet");
    expect(await out(sh, 'PATH=~/bin:$PATH greet b')).toBe('wrapped\napp b\n');
    expect((await sh.run('PATH=/nowhere greet')).exitCode).toBe(127);
    // shell builtins have no program file and keep working whatever PATH holds
    expect(await out(sh, 'PATH=/nowhere; cd /tmp; echo x')).toBe('x\n');
  });

  it.each([
    "printf '#!/usr/bin/env foo\\n' > b/foo",
    "printf '#!/usr/bin/timeout 5\\n' > b/foo",
    "printf '#!/usr/bin/env -S bar\\n' > b/foo; printf '#!/usr/bin/env -S foo\\n' > b/bar; chmod +x b/bar",
  ])('stops a #! loop at the script nesting limit: %s', async setup => {
    const sh = new Shell();
    await out(sh, `mkdir b; ${setup}; chmod +x b/foo`);
    const r = await sh.run('PATH=$PWD/b:$PATH foo');
    expect(r.exitCode).toBe(1);
    expect(r.toToolOutput()).toContain('[stopped: maximum script nesting level exceeded (100).');
    expect(await out(sh, 'echo ok')).toBe('ok\n');
  }, 60_000); // a deadline, so a regression fails the test instead of hanging it

  it('PATH lookup details', async () => {
    const sh = new Shell();
    await out(sh, "mkdir -p ~/bin b && printf 'echo hi\\n' > ~/bin/x && printf 'echo b\\n' > b/y && chmod +x ~/bin/x b/y");
    // a ~ at the start of a PATH entry is expanded during the search
    expect(await out(sh, 'PATH="~/bin:$PATH"; x; type x')).toBe('hi\nx is hashed (/home/agent/bin/x)\n');
    expect(await out(sh, 'hash -l')).toBe('builtin hash -p /home/agent/bin/x x\n');
    expect(await out(sh, 'hash -lt x')).toBe('builtin hash -p /home/agent/bin/x x\n');
    expect(await out(sh, 'hash b/y; hash -r; hash -l')).toBe('');
    for (const o of ['t', 'd', 'lt']) {
      const r = await sh.run('hash -' + o);
      expect([r.exitCode, r.stderr]).toEqual([1, `bash: line 1: hash: -${o.slice(-1)}: option requires an argument\n`]);
    }
    // a hashed program whose directory can no longer be searched
    const d = await sh.run('PATH=$PWD/b:$PATH; y; chmod 000 b; y; echo $?; chmod 755 b');
    expect([d.stdout, d.stderr]).toEqual(['b\n126\n', 'bash: line 1: /home/agent/b/y: Permission denied\n']);
  });

  it('runs scripts with arguments', async () => {
    const sh = new Shell();
    await out(sh, "cat > run.sh <<'EOF'\n#!/bin/bash\nset -e\necho \"script $1 $2 ($#)\"\nfor a in \"$@\"; do echo \"arg: $a\"; done\nEOF\nchmod +x run.sh");
    expect(await out(sh, "./run.sh x 'y z'")).toBe('script x y z (2)\narg: x\narg: y z\n');
    expect(await out(sh, 'bash run.sh a')).toBe('script a  (1)\narg: a\n');
    await out(sh, "printf '#!/usr/bin/env python3\\nprint(1)\\n' > p.py && chmod +x p.py");
    const r = await sh.run('./p.py');
    expect(r.exitCode).toBe(127);
    expect(r.stderr).toContain('python3\u2019: No such file or directory');
    expect((await sh.run('./missing.sh')).exitCode).toBe(127);
    expect((await sh.run('exit 2')).exitCode).toBe(2);
    const e = await sh.run('set -e; echo one; false; echo two');
    expect([e.stdout, e.exitCode]).toEqual(['one\n', 1]);
  });

  it('awk', async () => {
    const sh = new Shell();
    sh.fs.writeText('/home/agent/data.csv', 'name,dept,salary\nann,eng,120\nbob,ops,90\ncat,eng,100\n');
    const cases: [string, string][] = [
      ["awk -F, 'NR>1 {print $1}' data.csv", 'ann\nbob\ncat\n'],
      ["awk -F, 'NR>1 {s+=$3} END {print s}' data.csv", '310\n'],
      ["awk -F, 'NR>1 {t[$2]+=$3} END {for (d in t) print d, t[d]}' data.csv | sort", 'eng 220\nops 90\n'],
      ["awk -F, 'NR > 1 && $3 > 100 {printf \"%s:%.2f\\n\", $1, $3}' data.csv", 'ann:120.00\n'],
      ["awk 'END {print NR}' data.csv", '4\n'],
      ["awk -F, '/ops/ {print $1}' data.csv", 'bob\n'],
      ["printf 'a\\nb\\na\\nb\\n' | awk '!seen[$0]++'", 'a\nb\n'],
      ["echo 'a b c' | awk '{print NF, $NF}'", '3 c\n'],
      ["echo 'a-b' | awk '{sub(/a/, \"X\"); print}'", 'X-b\n'],
      ["echo hello | awk '{n=gsub(/l/, \"L\"); print n-1+2, toupper(\"hello\"), substr(\"hello\", 2, 3)}'", '3 HELLO ell\n'],
      ["echo '1 2 3' | awk -v OFS=- '{$1=$1; print}'", '1-2-3\n'],
      ["awk -v n=5 'BEGIN {print \"x=\" n}'", 'x=5\n'],
      ["awk 'BEGIN {n = split(\"a:b\", parts, \":\"); print n}'", '2\n'],
      ["printf '10\\n2\\n' | awk '{if ($1 > 5) print \"big\"; else print \"small\"}'", 'big\nsmall\n'],
      ["printf 'line1\\nline2\\nline3\\nline4\\n' | awk '/line2/,/line3/'", 'line2\nline3\n'],
      ["awk 'BEGIN {print 1/3}'", '0.333333\n'],
    ];
    for (const [cmd, expected] of cases) expect(await out(sh, cmd), cmd).toBe(expected);
    await out(sh, "awk -F, 'NR>1 {print $1 > \"names.txt\"}' data.csv");
    expect(sh.fs.readText('/home/agent/names.txt')).toBe('ann\nbob\ncat\n');
  });

  it('diff and utilities', async () => {
    const sh = new Shell();
    sh.fs.writeText('/home/agent/a.txt', 'one\ntwo\nthree\n');
    sh.fs.writeText('/home/agent/b.txt', 'one\nTWO\nthree\nfour\n');
    let r = await sh.run('diff a.txt b.txt');
    expect(r.exitCode).toBe(1);
    expect(r.stdout).toBe('2c2\n< two\n---\n> TWO\n3a4\n> four\n');
    r = await sh.run('diff -u a.txt b.txt');
    expect(r.stdout).toBe('--- a.txt\n+++ b.txt\n@@ -1,3 +1,4 @@\n one\n-two\n+TWO\n three\n+four\n');
    const cases: [string, string][] = [
      ['diff a.txt a.txt', ''],
      ['basename /var/log/app.tar.gz; dirname /var/log/app.tar.gz; basename report.txt .txt', 'app.tar.gz\n/var/log\nreport\n'],
      ['realpath a.txt', '/home/agent/a.txt\n'],
      ['seq 3', '1\n2\n3\n'],
      ['seq -s, 0 5 10', '0,5,10\n'],
      ['stat -c %s a.txt', '14\n'],
      ['file a.txt', 'a.txt: ASCII text\n'],
      ['tac a.txt', 'three\ntwo\none\n'],
      ['echo hello | rev', 'olleh\n'],
      ['paste -sd, a.txt', 'one,two,three\n'],
      ['nl a.txt', '     1\tone\n     2\ttwo\n     3\tthree\n'],
      ['echo -n hello | base64; echo aGVsbG8= | base64 -d', 'aGVsbG8=\nhello'],
      ['echo -n hello | sha256sum', '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824  -\n'],
      ["printf 'a bb\\nccc d\\n' | column -t", 'a    bb\nccc  d\n'],
      ['cat /dev/null; echo hi > /dev/null', ''],
      ['id; nproc', 'uid=1000(agent) gid=1000(agent) groups=1000(agent)\n4\n'],
      ["find . -mmin -5 -name '*.txt' | sort | sed 's|./||'", 'a.txt\nb.txt\n'],
      ['grep -rIl ELF /usr/bin; ls -d /var/tmp /opt >/dev/null', ''],
    ];
    for (const [cmd, expected] of cases) expect(await out(sh, cmd), cmd).toBe(expected);
    expect(await out(sh, 'mktemp')).toMatch(/^\/tmp\/tmp\./);
    expect(await out(sh, 'cat /etc/os-release')).toContain('ID=ubuntu');
    expect(await out(sh, 'ls /usr/bin')).toContain('grep\n');
    expect((await sh.run('timeout 0.05 sleep 5')).exitCode).toBe(124);
    expect(await out(sh, 'awk --version')).toMatch(/^mawk/);
  });

  it('closes the gaps found by a real model', async () => {
    const sh = new Shell();
    await out(sh, 'mkdir -p project/src && touch project/src/a.py project/src/b.py project/README.md');
    await out(sh, "cat << 'EOF' > ~/backup.sh\n#!/bin/bash\nset -e\nBACKUP_DIR=\"$HOME/backup\"\nmkdir -p \"$BACKUP_DIR\"\ncount=0\n" +
      'while IFS= read -r -d \'\' file; do\n    cp "$file" "$BACKUP_DIR/"\n    count=$((count + 1))\n' +
      'done < <(find "$HOME/project" -type f -name "*.py" -print0)\necho "$count"\nEOF\nchmod +x ~/backup.sh');
    expect(await out(sh, '~/backup.sh')).toBe('2\n');
    expect(await out(sh, 'ls backup')).toBe('a.py\nb.py\n');
    expect(await out(sh, 'ls /dev/fd')).toBe('');
    const cases: [string, string][] = [
      ["find project -name '*.md' -o -name '*.py' | sort", 'project/README.md\nproject/src/a.py\nproject/src/b.py\n'],
      ['find project -type d ! -name project', 'project/src\n'],
      ["echo x > project/src/a.py; echo x > project/src/b.py; find project -name '*.py' -exec cat {} \\;", 'x\nx\n'],
      ["find project -name '*.py' -exec cat {} +", 'x\nx\n'],
    ];
    for (const [cmd, expected] of cases) expect(await out(sh, cmd), cmd).toBe(expected);
    await out(sh, "find project -name '*.md' -delete");
    expect(sh.fs.exists('/home/agent/project/README.md')).toBe(false);
    const more: [string, string][] = [
      ['ls -R project', 'project:\nsrc\n\nproject/src:\na.py\nb.py\n'],
      ['sort --version', 'sort (GNU coreutils) 9.4\n'],
      ['type -t ls', 'file\n'],
      ["printf 'a,b' | { IFS= read -r -d , x; read -r y; echo \"$x|$y\"; }", 'a|b\n'],
      ["wc -l < <(printf '1\\n2\\n3\\n')", '3\n'],
      ["printf 'south 100\\neast 42\\nnorth 220\\n' | sort -k2,2nr", 'north 220\nsouth 100\neast 42\n'],
      ["printf 'a,10,y\\nb,2,x\\n' | sort -t, -k2n --debug", 'b,2,x\na,10,y\n'],
      ["mkdir -p project/node_modules; echo x > project/node_modules/z.py; echo x > project/n.txt; grep -r --include='*.py' --exclude=b.py --exclude-dir={node_modules,.git} x project", 'project/src/a.py:x\n'],
    ];
    for (const [cmd, expected] of more) expect(await out(sh, cmd), cmd).toBe(expected);
    expect(await out(sh, 'bash --version')).toMatch(/^GNU bash, version 5/);
    expect(await out(sh, 'ls /bin/sh /etc/issue')).toContain('/bin/sh');
  });

  it('the agent is a regular user', async () => {
    const sh = new Shell();
    await out(sh, "printf 'echo ran' > s.sh");
    let r = await sh.run('./s.sh');
    expect([r.exitCode, r.stderr]).toEqual([126, 'bash: line 1: ./s.sh: Permission denied\n']);
    expect(await out(sh, 'ls -l s.sh | cut -c1-10; chmod +x s.sh; ls -l s.sh | cut -c1-10; ./s.sh')).toBe('-rw-r--r--\n-rwxr-xr-x\nran\n');
    expect(await out(sh, 'chmod -v go-rx s.sh')).toBe("mode of 's.sh' changed from 0755 (rwxr-xr-x) to 0700 (rwx------)\n");
    expect(await out(sh, "chmod 640 s.sh; stat -c '%a %U' s.sh")).toBe('640 agent\n');
    expect(await out(sh, '[ -x /usr/bin/grep ] && [ ! -w /etc/passwd ] && [ -w ~ ] && echo ok')).toBe('ok\n');
    for (const [cmd, err] of [
      ['echo x > /etc/motd', 'bash: line 1: /etc/motd: Permission denied\n'],
      ['rm /usr/bin/grep', "rm: cannot remove '/usr/bin/grep': Permission denied\n"],
      ['chmod 777 /etc/passwd', "chmod: changing permissions of '/etc/passwd': Operation not permitted\n"],
      ['chown agent /etc/passwd', "chown: changing ownership of '/etc/passwd': Operation not permitted\n"],
      ['ls /root', "ls: cannot open directory '/root': Permission denied\n"],
      ['sudo cat /etc/shadow', 'agent is not in the sudoers file.\n'],
    ]) {
      r = await sh.run(cmd);
      expect(r.exitCode, cmd).not.toBe(0);
      expect(r.stderr, cmd).toBe(err);
    }
    expect(sh.fs.isFile('/usr/bin/grep')).toBe(true);
    expect(await out(sh, 'cat /etc/passwd')).toContain('root:x:0:0');
    expect(await out(sh, "stat -c '%A %U' /tmp")).toBe('drwxrwxrwt root\n');
    expect(await out(sh, 'echo x > /tmp/f && cat /tmp/f')).toBe('x\n');
    r = await sh.run('find / -name shadow');
    expect([r.exitCode, r.stderr]).toEqual([1, 'find: \u2018/proc/tty/driver\u2019: Permission denied\nfind: \u2018/root\u2019: Permission denied\n' +
      'find: \u2018/var/cache/apt/archives/partial\u2019: Permission denied\nfind: \u2018/var/cache/ldconfig\u2019: Permission denied\n']);
    sh.fs.writeText('/etc/app.conf', 'mode=prod');
    sh.fs.writeText('/home/agent/notes.txt', 'hi');
    expect(await out(sh, 'stat -c %U /etc/app.conf ~/notes.txt')).toBe('root\nagent\n');
    expect(await out(sh, "echo '!' >> ~/notes.txt; tr -d '\\n' < ~/notes.txt; echo")).toBe('hi!\n');
  });

  it('stops runaway loops', async () => {
    const r = await new Shell({ maxLoopIterations: 50 }).run('while true; do :; done');
    expect(r.exitCode).not.toBe(0);
    expect(r.stderr).toContain('loop aborted');
  });

  it('shell built-ins are not program files, and system files are old', async () => {
    const sh = new Shell().register('orders', () => 0);
    const bin = (await sh.run('ls /bin')).stdout.split('\n');
    for (const name of ['eval', 'cd', 'export', 'break', 'source', 'history']) expect(bin).not.toContain(name);
    for (const name of ['ls', 'grep', 'test', 'echo', 'orders']) expect(bin).toContain(name);
    expect((await sh.run("eval 'echo ok'; cd /tmp && export X=1 && type cd >/dev/null")).stdout).toBe('ok\n');
    await sh.run('echo hi > ~/new.txt');
    expect((await sh.run("find / -mmin -120 -not -path '/tmp*' -not -path '/var/tmp*' -not -path '/proc*' -not -path '/sys' -not -path '/run' 2>/dev/null | sort")).stdout)
      .toBe('/home/agent\n/home/agent/new.txt\n');
  });

  it('the machine has the usual clutter', async () => {
    const sh = new Shell().register('orders', () => 0);
    const o = async (c: string) => (await sh.run(c)).stdout;
    expect(await o('cat ~/.bashrc')).toContain('HISTSIZE=1000');
    expect(await o('ls -A ~')).toBe('.bash_logout\n.bashrc\n.profile\n');
    expect(await o('ls ~')).toBe('');
    for (const dir of ['proc', 'sys', 'lib', 'sbin', 'run', 'boot']) expect((await o('ls /')).split('\n')).toContain(dir);
    expect(await o('grep -c ^processor /proc/cpuinfo')).toBe('4\n');
    expect(await o('grep MemTotal /proc/meminfo')).toContain('8014880');
    expect(await o('cat /etc/hosts')).toContain('topwater');
    expect(await o('cat /etc/group')).toContain('agent:x:1000:');
    const grep = Number(await o('stat -c %s /usr/bin/grep')), orders = Number(await o('stat -c %s /usr/bin/orders'));
    expect(grep > 10_000 && orders > 10_000 && grep !== orders).toBe(true);
  });

  it('one UTC clock: file mtimes, date and uptime agree', async () => {
    const sh = new Shell();
    await sh.run('mkdir -p /tmp/w && cd /tmp/w');
    expect((await sh.run('touch f; now=$(date +%s); m=$(date -r f +%s); d=$(( now - m )); if [ $d -ge -2 ] && [ $d -le 2 ]; then echo ok; else echo "skew=$d"; fi')).stdout).toBe('ok\n');
    expect((await sh.run("h1=$(date -u +%H); h2=$(uptime | grep -oE '^ *[0-9]{2}' | tr -d ' '); [ \"$h1\" = \"$h2\" ] && echo ok || echo \"$h1 vs $h2\"")).stdout).toBe('ok\n');
  });
});
