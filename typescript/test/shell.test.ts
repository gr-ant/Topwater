// Ported from tests/Topwater.Tests/ShellTests.cs and ShellLanguageTests.cs (the [Fact] tests).
import { describe, it, expect } from 'vitest';
import { Shell, BashTool } from '../src/index.js';

async function out(sh: Shell, cmd: string) {
  const r = await sh.run(cmd);
  expect(r.exitCode, `\`${cmd}\` exited ${r.exitCode}: ${r.stderr}`).toBe(0);
  return r.stdout;
}

describe('shell', () => {
  it('reports output live to onOutput, in order, and still returns all of it', async () => {
    const seen: string[] = [];
    const r = await new Shell().run('echo a; echo b >&2; echo c | tr c C', (t, s) => seen.push(`${s}:${t}`));
    expect(seen).toEqual(['stdout:a\n', 'stderr:b\n', 'stdout:C\n']);
    expect(r.stdout).toBe('a\nC\n');
  });

  it('views of one filesystem share files but not the acting user', async () => {
    const sh = new Shell();
    const agent = sh.fs.as('agent');
    agent.writeText('/home/agent/up.txt', 'x');
    expect(sh.fs.stat('/home/agent/up.txt').owner).toBe('agent');
    expect(() => agent.writeText('/etc/x', 'y')).toThrow(/Permission denied/);
    expect(sh.fs.runAs).toBeNull();
    expect(sh.fs.usedBytes).toBe(agent.usedBytes);
  });

  it('keeps the filesystem across calls', async () => {
    const sh = new Shell();
    await out(sh, 'mkdir -p proj/src && cd proj');
    await out(sh, "echo 'line one' > src/a.txt && echo 'line two' >> src/a.txt");
    expect(await out(sh, 'pwd')).toBe('/home/agent/proj\n');
    expect(await out(sh, 'cat src/a.txt')).toBe('line one\nline two\n');
    await out(sh, 'cp src/a.txt src/b.txt && mv src/b.txt c.txt && touch src/z.md');
    expect(await out(sh, 'ls')).toBe('c.txt\nsrc\n');
    expect(await out(sh, 'ls src/*')).toBe('src/a.txt\nsrc/z.md\n');
    expect(await out(sh, "find . -name '*.txt' -path '*src*'")).toBe('./src/a.txt\n');
    expect(await out(sh, 'tree')).toBe('.\n├── c.txt\n└── src\n    ├── a.txt\n    └── z.md\n\n1 directory, 3 files\n');
    await out(sh, 'rm -r src');
    expect(sh.fs.exists('/home/agent/proj/src')).toBe(false);
  });

  it('heredocs and redirects', async () => {
    const sh = new Shell();
    await out(sh, "NAME=world\ncat > hi.txt <<EOF\nhello $NAME\nEOF\ncat > raw.txt <<'EOF'\nhello $NAME\nEOF");
    expect(sh.fs.readText('/home/agent/hi.txt')).toBe('hello world\n');
    expect(sh.fs.readText('/home/agent/raw.txt')).toBe('hello $NAME\n');
    const r = await sh.run('cat nope 2>&1 > out.txt; cat missing > all.txt 2>&1');
    expect(r.stdout).toContain('No such file');
    expect(sh.fs.readText('/home/agent/all.txt')).toContain('No such file');
    expect(await out(sh, 'cat missing 2>/dev/null; true')).toBe('');
    expect(await out(sh, "wc -l < hi.txt; wc -l <<< 'x' > /dev/null; printf 'a\\nb\\nc\\n' | wc -l")).toBe('1\n3\n');
  });

  it('text tools', async () => {
    const sh = new Shell();
    sh.fs.writeText('/home/agent/log.txt', 'info start\nERROR disk full\ninfo retry\nerror again\ninfo done\n');
    expect(await out(sh, 'grep -in error log.txt')).toBe('2:ERROR disk full\n4:error again\n');
    expect(await out(sh, "grep -ci 'error' log.txt")).toBe('2\n');
    expect(await out(sh, "grep -B1 -i 'start\\|again' log.txt | grep -v ERROR")).toBe('info start\n--\ninfo retry\nerror again\n');
    expect(await out(sh, "grep -E 'disk|nothing' log.txt")).toBe('ERROR disk full\n');
    expect(await out(sh, "grep -rc info . | grep -v '/\\.'")).toBe('./log.txt:3\n');
    expect(await out(sh, 'tail -1 log.txt')).toBe('info done\n');
    expect(await out(sh, 'head -n 2 log.txt')).toBe('info start\nERROR disk full\n');
    expect(await out(sh, "sed -n '2,3p' log.txt")).toBe('ERROR disk full\ninfo retry\n');
    expect(await out(sh, "sed 's/info/INFO/g' log.txt | head -1")).toBe('INFO start\n');
    expect(await out(sh, "sed -E 's/^(\\w+) /[\\1] /' log.txt | head -1")).toBe('[info] start\n');
    await out(sh, "sed -i '/^info/d' log.txt");
    expect(await out(sh, 'cat log.txt')).toBe('ERROR disk full\nerror again\n');
    expect(await out(sh, "printf 'b\\na\\nb\\n' | sort | uniq -c | sort -rn")).toBe('      2 b\n      1 a\n');
    expect(await out(sh, 'echo a,b,c | cut -d, -f2-')).toBe('b,c\n');
    expect(await out(sh, 'echo hello | tr a-z A-Z')).toBe('HELLO\n');
    expect(await out(sh, 'echo hi | tee copy.txt && cat copy.txt >/dev/null')).toBe('hi\n');
    expect(await out(sh, 'ls *.txt | grep log | xargs grep -h disk')).toBe('ERROR disk full\n');
  });

  it('jq subset', async () => {
    const sh = new Shell();
    sh.fs.writeText('/home/agent/o.json', '{"orders":[{"id":1,"status":"open","total":5},{"id":2,"status":"shipped","total":7.5}]}');
    expect(await out(sh, "jq '.orders | length' o.json")).toBe('2\n');
    expect(await out(sh, "jq -r '.orders[].status' o.json")).toBe('open\nshipped\n');
    expect(await out(sh, "jq '.orders[] | select(.status == \"shipped\") | .id' o.json")).toBe('2\n');
    expect(await out(sh, "cat o.json | jq -c '[.orders[].id]'")).toBe('[1,2]\n');
    expect(await out(sh, "jq 'map(.total) | add' <<< \"$(jq -c .orders o.json)\"")).toBe('12.5\n');
    expect(await out(sh, 'jq -c keys o.json')).toBe('["orders"]\n');
  });

  it('errors are bash-like', async () => {
    const sh = new Shell();
    let r = await sh.run('frobnicate --now');
    expect(r.exitCode).toBe(127);
    expect(r.stderr).toContain('frobnicate: command not found');
    r = await sh.run("echo 'unterminated");
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain('unexpected EOF');
    r = await sh.run('coproc cat');
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain('not supported');
    r = await sh.run('cat nope.txt');
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toBe('cat: nope.txt: No such file or directory\n');
    expect(r.toToolOutput()).toBe('cat: nope.txt: No such file or directory\n[exit code 1]');
    expect((await sh.run('ls -y')).stderr).toBe("ls: invalid option -- 'y'\nTry 'ls --help' for more information.\n");
  });

  it('fakes a system', async () => {
    const sh = new Shell({ user: 'dev', hostname: 'box' });
    expect(await out(sh, 'whoami; hostname; uname; echo $HOME')).toBe('dev\nbox\nLinux\n/home/dev\n');
    expect(await out(sh, 'date +%F')).toMatch(/^\d{4}-\d{2}-\d{2}\n$/);
    expect(await out(sh, 'which grep')).toBe('/usr/bin/grep\n');
    expect(await out(sh, 'history')).toContain('1  whoami; hostname');
    await out(sh, "alias ll='ls -la'");
    expect(await out(sh, 'll')).toContain('..');
  });

  it('registers host commands', async () => {
    const sh = new Shell();
    sh.register('deploy', async ctx => {
      ctx.stdout.write(`deploying ${ctx.args.join(',')}\n`);
      return ctx.args.length === 0 ? 1 : 0;
    }, 'Deploy a service');
    expect(await out(sh, 'deploy api')).toBe('deploying api\n');
    expect((await sh.run('deploy')).exitCode).toBe(1);
    expect(await out(sh, 'which deploy')).toBe('/usr/bin/deploy\n');
    expect(await out(sh, 'help')).toContain('deploy');
    sh.register('boom', () => { throw new Error('kaput'); });
    const r = await sh.run('boom || echo handled');
    expect([r.stdout, r.stderr]).toEqual(['handled\n', 'boom: kaput\n']);
  });

  it('formats tool output', async () => {
    const sh = new Shell();
    expect(await BashTool.execute(sh, 'true')).toBe('(no output)');
    expect((await sh.run('seq_missing')).toToolOutput().endsWith('[exit code 127]')).toBe(true);
    sh.fs.writeText('/big.txt', 'x'.repeat(40_000));
    expect(await BashTool.execute(sh, 'cat /big.txt')).toContain('output truncated');
  });

  it('exec redirection lasts for the call, and scripts keep their own', async () => {
    const sh = new Shell();
    let r = await sh.run("exec 2>&1; ls /nope; printf 'exec >inner.log\\necho hidden\\n' > s.sh; bash s.sh; echo shown; cat inner.log");
    expect(r.stdout).toBe("ls: cannot access '/nope': No such file or directory\nshown\nhidden\n");
    expect(r.stderr).toBe('');
    r = await sh.run('echo err >&2');
    expect(r.stderr).toBe('err\n');
  });

  it('options reset between calls but variables persist', async () => {
    const sh = new Shell();
    await sh.run('set -euxf -o noclobber; declare -i n=2; readonly R=1; shopt -s nocasematch');
    const r = await sh.run('echo $- ; n+=3; echo $n; [[ A == a ]] && echo nocase; R=2');
    expect(r.stdout).toBe('hBc\n5\nnocase\n');
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toContain('R: readonly variable');
  });

  it('a child bash sees only exported variables', async () => {
    const sh = new Shell();
    sh.environment.set('API_URL', 'https://x');
    expect((await sh.run('bash -c \'echo "[$API_URL]"\'')).stdout).toBe('[https://x]\n');
    expect((await sh.run('export -n API_URL; bash -c \'echo "[$API_URL]"\'')).stdout).toBe('[]\n');
  });

  it('counts lines from 1 in each call', async () => {
    const sh = new Shell();
    await sh.run('true\ntrue\ntrue\ntrue\ntrue');
    expect((await sh.run('while read l; do :; done < /nofile')).stderr).toBe('bash: line 1: /nofile: No such file or directory\n');
    expect((await sh.run('true\ntrue\nif true; then :; fi > /nope/z')).stderr).toBe('bash: line 3: /nope/z: No such file or directory\n');
  });

  it('keeps a long run of quantifiers fast', async () => {
    const sh = new Shell();
    const t = Date.now();
    // a 1 MB run of * on one atom: one quantifier, built in one pass
    const r = await sh.run('p=\'*\'; for i in {1..20}; do p=$p$p; done; echo aaa | grep -cE "a$p"; [[ aaa =~ a$p ]]; echo $?; echo aaa | sed -E "s/a$p/X/"');
    expect(r.stdout).toBe('1\n0\nX\n');
    expect(Date.now() - t).toBeLessThan(10_000);
    expect((await sh.run('p=$(printf \'\\\\{1\\\\}%.0s\' {1..40}); echo aaa | grep -c "a$p"')).stderr).toBe('grep: Regular expression too big\n');
  });
});
