// python3 (commands/python.ts over wasi.ts): the command in the shell, its limits, and hostile programs, as
// python/tests/test_python.py. The interpreter comes from the repository's assets/python-wasm.
import { describe, expect, test, vi } from 'vitest';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { gunzipSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { MemorySource, PythonUnavailable, Shell, ShellSnapshot, startRecording, type ShellOptions, type WebPolicy } from '../src/index.js';
import * as wasi from '../src/wasi.js';

const ARTIFACTS = new URL('../../assets/python-wasm/', import.meta.url);
const shell = (options: Partial<ShellOptions> = {}) => new Shell(options).allowPython({ artifacts: ARTIFACTS });
const run = async (script: string, options: Partial<ShellOptions> = {}) => (await shell(options)).run(script);
const T = 60_000;
const q = (s: string) => `'${s.replace(/'/g, "'\\''")}'`; // a shell word

// ---------- the artifacts ----------

test('python.wasm.gz decompresses to the pinned module (tools/python_wasm_gz.py)', () => {
  const wasm = gunzipSync(readFileSync(new URL('python.wasm.gz', ARTIFACTS)));
  expect(createHash('sha256').update(wasm).digest('hex')).toBe(wasi.ARTIFACT_SHA256['python.wasm']);
  const manifest = readFileSync(new URL('MANIFEST', ARTIFACTS), 'utf8');
  expect(manifest).toContain(`${wasi.ARTIFACT_SHA256['python314.zip']}  python314.zip`);
});

test('the module is limited and shared before compiling', () => {
  const wasm = new Uint8Array(gunzipSync(readFileSync(new URL('python.wasm.gz', ARTIFACTS))));
  const patched = wasi.sharedMemory(wasm, 96 << 20);
  const mem = WebAssembly.Module.exports(new WebAssembly.Module(patched)).find(e => e.kind === 'memory');
  expect(mem?.name).toBe('memory');
  expect(() => wasi.sharedMemory(wasm, 8 << 20)).toThrow(PythonUnavailable); // below the snapshot's own 40 MB
});

// ---------- the command ----------

describe('the command', () => {
  test('opt-in only', async () => {
    expect(new Shell().commands.has('python3')).toBe(false);
    const r = await new Shell().run("python3 -c 'print(1)'");
    expect(r.exitCode).toBe(127);
    expect(r.stderr).toContain('command not found');
  });

  test('program argv, stdin and exit codes', async () => {
    let r = await run("python3 -c 'import sys; print(sys.argv[1:], sys.stdin.read().upper())' a 'b c' <<< hi");
    expect([r.stdout, r.exitCode]).toEqual(["['a', 'b c'] HI\n\n", 0]);
    expect((await run("python3 -c 'raise SystemExit(7)'")).exitCode).toBe(7);
    r = await run("python3 -c 'import sys; sys.exit(\"bye\")'");
    expect([r.exitCode, r.stderr]).toEqual([1, 'bye\n']);
    r = await run("python3 -c '1/0'");
    expect(r.exitCode).toBe(1);
    expect(r.stderr.endsWith('ZeroDivisionError: division by zero\n')).toBe(true);
  }, T);

  test("files are the machine's files", async () => {
    const sh = await shell();
    await sh.run("mkdir -p /tmp/w && cd /tmp/w && printf 'a\\nb\\n' > in.txt");
    expect((await sh.run("python3 -c 'open(\"out.txt\", \"w\").write(open(\"in.txt\").read().upper())' && cat out.txt")).stdout).toBe('A\nB\n');
    // bytes survive both ways
    await sh.run("printf '\\377\\000\\001' > bin");
    expect((await sh.run("python3 -c 'print(list(open(\"bin\", \"rb\").read()))'")).stdout).toBe('[255, 0, 1]\n');
    await sh.run("python3 -c 'open(\"bin2\", \"wb\").write(bytes([0, 200, 255]))'");
    expect([...sh.fs.readBytes('/tmp/w/bin2')]).toEqual([0, 200, 255]);
    // a file left open is saved at exit, as a real process's is
    expect((await sh.run("python3 -c 'f = open(\"u\", \"w\"); f.write(\"kept\")'; cat u")).stdout).toBe('kept');
    // owner and mode come from the agent's user and umask, as for any command
    expect((await sh.run("ls -l out.txt | cut -d' ' -f1,3")).stdout).toBe('-rw-r--r-- agent\n');
  }, T);

  test('open files are one file', async () => {
    // what the program wrote and flushed is the file's, by any name, before it is closed (as one inode's contents are)
    const prog = `
import os, shutil, tempfile
f = open('a', 'w'); f.write('hello'); f.flush()
print(repr(open('a').read()), os.stat('a').st_size, os.path.getsize('a'))
shutil.copy('a', 'b'); print(repr(open('b').read()))
t = open('t.tmp', 'w'); t.write('new'); t.flush(); os.replace('t.tmp', 'c'); t.write('er'); t.close()
print(repr(open('c').read()), os.path.exists('t.tmp'))
g = open('d', 'w'); g.write('AAAA'); g.flush(); h = open('d', 'a'); h.write('bb'); h.close(); g.close()
print(repr(open('d').read()))
with tempfile.TemporaryFile(dir='.') as tf:
    tf.write(b'x' * 10); tf.flush()
u = open('u', 'w'); u.write('gone'); os.unlink('u'); u.write('!'); u.close()
os.mkdir('dir'); v = open('dir/v', 'w'); v.write('moved'); os.rename('dir', 'dir2'); v.close()
print(repr(open('dir2/v').read()), os.path.exists('dir'))
`;
    const r = await run(`mkdir -p /tmp/o && cd /tmp/o && python3 -c ${q(prog)}; ls`);
    expect([r.stdout, r.stderr]).toEqual(["'hello' 5 5\n'hello'\n'newer' False\n'AAAAbb'\n'moved' False\na\nb\nc\nd\ndir2\n", '']);
  }, T);

  test('output keeps its order', async () => {
    const prog = "import sys\nfor i in (1, 2):\n    print('e%d' % i, file=sys.stderr, flush=True)\n    print('o%d' % i, flush=True)";
    expect((await run(`python3 -c ${q(prog)} 2>&1`)).stdout).toBe('e1\no1\ne2\no2\n');
    expect((await run(`x=$(python3 -c ${q(prog)} 2>&1); echo "$x"`)).stdout).toBe('e1\no1\ne2\no2\n');
  }, T);

  test('renames and links answer as Linux', async () => {
    const prog = `
import errno, os
def t(f, *a):
    try:
        f(*a); print('ok')
    except OSError as e:
        print(type(e).__name__, errno.errorcode[e.errno])
open('f1', 'w').write('1')
t(os.rename, 'f1', 'f1'); print(open('f1').read())
os.makedirs('full/e')
t(os.rename, 'full', 'full/e'); print(os.path.isdir('full/e'))
t(os.rename, 'full', 'full/e/x')
t(os.link, 'full', 'l')
`;
    const r = await run(`cd /tmp && python3 -c ${q(prog)}`);
    expect([r.stdout, r.stderr]).toEqual(['ok\n1\nOSError EINVAL\nTrue\nOSError EINVAL\nPermissionError EPERM\n', '']);
  }, T);

  test('abort', async () => {
    const r = await run("python3 -c 'import os; os.abort()'");
    expect([r.exitCode, r.stderr]).toEqual([134, 'python3: fatal error: the program called abort()\n']);
  }, T);

  test('scripts, modules, shebang and substitution', async () => {
    const sh = await shell();
    await sh.run('mkdir -p /tmp/w && cd /tmp/w');
    await sh.run("printf 'import sys\\nprint(\"script\", sys.argv)\\n' > s.py");
    expect((await sh.run('python3 s.py x')).stdout).toBe("script ['s.py', 'x']\n");
    await sh.run("printf '#!/usr/bin/env python3\\nprint(\"shebang\")\\n' > t.py && chmod +x t.py");
    expect((await sh.run('./t.py')).stdout).toBe('shebang\n');
    await sh.run("printf 'print(\"module\", __name__)\\n' > m.py");
    expect((await sh.run('python3 -m m')).stdout).toBe('module __main__\n');
    expect((await sh.run("echo 'print(2**10)' | python3")).stdout).toBe('1024\n');
    expect((await sh.run("x=$(python3 -c 'print(\"in\")'); echo \"[$x]\"")).stdout).toBe('[in]\n');
    expect((await sh.run("python3 -c 'print(\"a\\nb\")' | wc -l")).stdout).toBe('2\n');
    const r = await sh.run('python3 nope.py');
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain("can't open file");
  }, T);

  test('the environment is the exported variables', async () => {
    const r = await run("export A=1; B=2; C=3 python3 -c 'import os; print(os.environ.get(\"A\"), os.environ.get(\"B\"), os.environ.get(\"C\"), os.getcwd())'");
    expect(r.stdout).toBe('1 None 3 /home/agent\n');
  }, T);

  test('no state between calls', async () => {
    const sh = await shell();
    await sh.run("python3 -c 'import builtins, sys; builtins.LEFT = 1; sys.path.append(\"/x\")'");
    expect((await sh.run("python3 -c 'import builtins, sys; print(hasattr(builtins, \"LEFT\"), \"/x\" in sys.path)'")).stdout).toBe('False False\n');
  }, T);

  test('snapshots keep working', async () => {
    const sh = await shell();
    await sh.run("echo 'print(5)' > /tmp/p.py");
    const snap = ShellSnapshot.capture(sh);
    await sh.run('rm /tmp/p.py');
    snap.restore(sh);
    expect((await sh.run('python3 /tmp/p.py')).stdout).toBe('5\n');
  }, T);

  test('a recording replays with python enabled', async () => {
    const sh = await shell();
    const { recording, stop } = startRecording(sh);
    await sh.run("python3 -c 'open(\"/tmp/r\", \"w\").write(\"rec\")'");
    stop();
    const again = await recording.shellAt(0, s => void s.allowPython({ artifacts: ARTIFACTS })); // runs call 0 again
    expect((await again.run("python3 -c 'print(open(\"/tmp/r\").read())'")).stdout).toBe('rec\n');
  }, T);

  test('help and version come from the spec, other positions reach the guest', async () => {
    const sh = await shell();
    expect((await sh.run('python3 --version')).stdout).toBe('Python 3.14.8\n');
    expect((await sh.run('python3 -h')).stdout).toBe((await sh.run('python3 --help')).stdout);
    expect((await sh.run('python3 -h')).stdout.startsWith('usage: python3')).toBe(true);
  }, T);

  test('an unavailable add-on fails in host code', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'topwater-py-'));
    try {
      await expect(new Shell().allowPython({ artifacts: dir })).rejects.toThrow(PythonUnavailable);
      for (const name of ['python.wasm.gz', 'python314.zip']) writeFileSync(join(dir, name), 'not the interpreter');
      await expect(new Shell().allowPython({ artifacts: dir })).rejects.toThrow(/gzip/);
      writeFileSync(join(dir, 'python.wasm.gz'), readFileSync(new URL('python.wasm.gz', ARTIFACTS)));
      await expect(new Shell().allowPython({ artifacts: dir })).rejects.toThrow(/python314.zip: not the expected file/);
      // the command itself says so, and the shell goes on
      const sh = new Shell();
      await sh.allowPython({ artifacts: dir }).catch(() => {});
      const r = await sh.run("python3 -c 'print(1)'; echo next");
      expect(r.stdout).toBe('next\n');
      expect(r.stderr).toMatch(/^python3: the Python add-on is not available: python314.zip/);
    } finally { rmSync(dir, { recursive: true, force: true }); }
  }, T);

  test('without the add-on or shared memory it is unavailable, never a hang', async () => {
    await expect(new Shell().allowPython()).rejects.toThrow(/npm install topwater-python/);
    vi.stubGlobal('crossOriginIsolated', false); // a page served without COOP/COEP
    try {
      await expect(new Shell().allowPython({ artifacts: fileURLToPath(ARTIFACTS) + '?' })).rejects.toThrow(/Cross-Origin-Opener-Policy/);
    } finally { vi.unstubAllGlobals(); }
  });
});

// ---------- mounts ----------

describe('mounts', () => {
  test('a read-only mount refuses writes; a file not fetched yet is fetched while the program waits', async () => {
    const sh = await shell();
    const src = new MemorySource({ 'a.txt': 'source\n' });
    await sh.mount('/mnt/ro', src);
    expect((await sh.run("python3 -c 'print(open(\"/mnt/ro/a.txt\").read(), end=\"\")'")).stdout).toBe('source\n');
    expect(src.calls.some(([op, p]) => op === 'read' && p === '/a.txt')).toBe(true);
    let r = await sh.run("python3 -c 'open(\"/mnt/ro/a.txt\", \"w\").write(\"x\")'");
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toContain('PermissionError');
    r = await sh.run("python3 -c 'open(\"/mnt/ro/new.txt\", \"w\")'");
    expect(r.stderr).toContain('PermissionError');
    expect(src.calls.every(([op]) => ['list', 'stat', 'read'].includes(op))).toBe(true);
  }, T);

  test('a slow source is awaited', async () => {
    const sh = await shell();
    const src = new MemorySource({ 'd/slow.txt': 'late\n' });
    src.hook = async op => { if (op === 'read') await new Promise(r => setTimeout(r, 300)); };
    await sh.mount('/mnt/s', src);
    const r = await sh.run("python3 -c 'import os; print(sorted(os.listdir(\"/mnt/s/d\")), open(\"/mnt/s/d/slow.txt\").read(), end=\"\")'");
    expect([r.stdout, r.stderr]).toEqual(["['slow.txt'] late\n", '']);
  }, T);

  test('a slow source ends at the time limit', async () => {
    const sh = await shell();
    const src = new MemorySource({ 'a.txt': 'source\n' });
    await sh.mount('/mnt/m', src);
    src.hook = async op => { if (op === 'read') await new Promise(r => setTimeout(r, 3000)); };
    const r = await sh.run("timeout 1 python3 -c 'print(open(\"/mnt/\" + \"m/a.txt\").read())'; echo $?");
    expect(r.stdout.endsWith('124\n')).toBe(true);
  }, T);

  test('staged mount writes become changes', async () => {
    const sh = await shell();
    const src = new MemorySource({ 'a.txt': 'old\n' });
    await sh.mount('/mnt/st', src, 'staged');
    await sh.run("python3 -c 'open(\"/mnt/st/a.txt\", \"a\").write(\"more\\n\"); open(\"/mnt/st/b.txt\", \"w\").write(\"b\\n\")'");
    const changes = sh.pendingChanges('/mnt/st').map(c => [c.kind, c.path, new TextDecoder().decode(c.content ?? new Uint8Array())]);
    expect(changes.sort()).toEqual([['write', '/a.txt', 'old\nmore\n'], ['write', '/b.txt', 'b\n']]);
    expect(src.calls.every(([op]) => op !== 'write')).toBe(true); // nothing reached the source
  }, T);
});

// ---------- the web ----------

const transport: WebPolicy['transport'] = async req => {
  if (req.url.endsWith('/redirect')) return { status: 302, reason: 'Found', headers: [['Location', 'https://api.test/data']], body: new Uint8Array() };
  const body = new TextDecoder().decode(req.body ?? new Uint8Array());
  return { status: 200, reason: 'OK', headers: [['Content-Type', 'application/json']], body: new TextEncoder().encode(JSON.stringify({ url: req.url, body })) };
};

describe('the web', () => {
  test('HTTP goes through the web policy', async () => {
    let r = await run("python3 -c 'import urllib.request; urllib.request.urlopen(\"https://example.com\")'");
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toContain('Could not resolve host: example.com');
    const sh = (await shell()).allowWeb({ domains: ['api.test'], methods: ['GET', 'POST'], transport });
    r = await sh.run("python3 -c 'import urllib.request, json; print(json.load(urllib.request.urlopen(\"https://api.test/redirect\"))[\"url\"])'");
    expect(r.stdout, r.stderr).toBe('https://api.test/data\n');
    r = await sh.run("python3 -c 'import requests; print(requests.post(\"https://api.test/p\", data=b\"hi\").json()[\"body\"])'");
    expect(r.stdout, r.stderr).toBe('hi\n');
    r = await sh.run("python3 -c 'import urllib.request; urllib.request.urlopen(\"https://other.test/\")'");
    expect(r.stderr).toContain('other.test is not an allowed host');
    r = await sh.run("python3 -c 'import urllib.request; urllib.request.urlopen(\"http://api.test/\")'");
    expect(r.stderr).toContain('only https:// URLs are allowed');
  }, T);

  test('requests count against the policy', async () => {
    const sh = (await shell()).allowWeb({ domains: ['api.test'], maxRequestsPerCall: 2, transport });
    const r = await sh.run("python3 -c '\nimport urllib.request\nfor _ in range(3): urllib.request.urlopen(\"https://api.test/x\")'");
    expect(r.stderr).toContain('the limit of 2 requests in one call was reached');
  }, T);

  test('the bridge rejects bad records', () => {
    const u32 = (n: number) => { const b = new Uint8Array(4); new DataView(b.buffer).setUint32(0, n, true); return b; };
    const field = (s: string) => { const b = new TextEncoder().encode(s); return [...u32(b.length), ...b]; };
    const record = (fields: string[], count = 0) => new Uint8Array([...fields.slice(0, 2).flatMap(field), ...u32(count), ...fields.slice(2).flatMap(field)]);
    expect(wasi.decodeRequest(record(['get', 'https://a/', '']))).toEqual({ method: 'GET', url: 'https://a/', headers: [], body: new Uint8Array() });
    for (const [bad, why] of [[record(['TRACE', 'https://a/', '']), 'unsupported'], [record(['GET', 'https://a/ x', '']), 'spaces'],
      [record(['GET', 'https://a/', ''], 1000), 'too many'], [new Uint8Array([...record(['GET', 'https://a/', '']), 120]), 'trailing'],
      [record(['GET', 'https://a/', 'Bad Name', 'v', ''], 1), 'header name'], [record(['GET', 'https://a/', 'X', 'a\r\nb', ''], 1), 'header value'],
      [new Uint8Array([255, 255, 255, 255]), 'too long']] as const)
      expect(() => wasi.decodeRequest(bad)).toThrow(why);
  });
});

// ---------- hostile programs ----------

describe('hostile programs', () => {
  test('a busy loop and a sleep end at the time limit', async () => {
    expect((await run("timeout 1 python3 -c 'while True: pass'; echo $?")).stdout).toBe('124\n');
    expect((await run("timeout 1 python3 -c 'import time; time.sleep(60)'; echo $?")).stdout).toBe('124\n');
    const r = await run("python3 -c 'while True: pass'", { maxCallDurationMs: 1000 });
    expect(r.exitCode).toBe(124);
    expect(r.trailer).toBeDefined();
  }, T);

  test('time limits hold however calls follow each other', async () => {
    let r = await run("for i in 1 2 3; do timeout 1 python3 -c 'while True: pass'; echo $?; done");
    expect(r.stdout).toBe('124\n124\n124\n');
    r = await run("for i in 1 2 3; do timeout 1 python3 -c 'import time; time.sleep(5)'; echo $?; done");
    expect(r.stdout).toBe('124\n124\n124\n');
    r = await run("for i in $(seq 40); do python3 -c pass; done; " +
      "timeout 5 python3 -c 'import time\nt = time.monotonic()\nwhile time.monotonic() - t < 2: pass'; echo $?");
    expect(r.stdout).toBe('0\n');
  }, 120_000);

  test('a sleep never ends early', async () => {
    const r = await run("python3 -c 'import time\nfor _ in range(5):\n    t = time.monotonic(); w = time.time(); time.sleep(0.05); print(time.monotonic() - t >= 0.05, time.time() - w >= 0.049)'");
    expect(r.stdout).toBe('True True\n'.repeat(5));
  }, T);

  test('the shell thread stays free while the guest spins', async () => {
    const sh = await shell();
    let ticks = 0;
    const timer = setInterval(() => ticks++, 10);
    try { await sh.run("timeout 1 python3 -c 'while True: pass'"); } finally { clearInterval(timer); }
    expect(ticks).toBeGreaterThan(50);
  }, T);

  test('memory is capped', async () => {
    const sh = await new Shell().allowPython({ artifacts: ARTIFACTS, maxMemory: 96 << 20 });
    let r = await sh.run("python3 -c 'x = bytearray(200 << 20)'");
    expect(r.exitCode).toBe(1);
    expect(r.stderr.endsWith('MemoryError\n')).toBe(true);
    r = await sh.run("python3 -c '\nx = []\nwhile True: x.append(bytearray(1 << 20))'");
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toContain('MemoryError');
    expect((await sh.run("python3 -c 'print(1)'")).stdout).toBe('1\n'); // the next call starts fresh
  }, T);

  test('recursion and stack', async () => {
    let r = await run("python3 -c 'def f(): f()\nf()'");
    expect(r.exitCode).toBe(1);
    expect(r.stderr.endsWith('RecursionError: maximum recursion depth exceeded\n')).toBe(true);
    r = await run("python3 -c 'import json; json.loads(\"[\" * 200000)'; echo next");
    expect(r.stdout).toBe('next\n');
    expect(r.stderr).toMatch(/RecursionError|ran out of stack/);
  }, T);

  test('huge files hit the disk cap', async () => {
    const sh = await shell({ maxFileSystemBytes: 8 << 20 });
    let r = await sh.run("python3 -c '\nf = open(\"/tmp/big\", \"wb\")\nwhile True: f.write(b\"x\" * 65536)'");
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toContain('No space left on device');
    expect(sh.fs.usedBytes).toBeLessThanOrEqual(8 << 20);
    r = await sh.run("python3 -c 'import os; os.truncate(\"/tmp/big\", 1 << 40)'");
    expect(r.exitCode).toBe(1);
  }, T);

  test('a write that repeats one buffer a thousand times is refused before it is copied', async () => {
    // Python has no os.writev here, so this is what a hand-made module could send: 1000 vectors over the same 64 MB
    const memory = new WebAssembly.Memory({ initial: 1025 });
    const g = new wasi.Guest(new Shell().agentFs, [], [], new Uint8Array(), 1 << 20, 256 << 20, new Uint8Array(), Infinity, async () => [0, new Uint8Array()]);
    g.memory = memory;
    const dv = new DataView(memory.buffer);
    for (let i = 0; i < 1000; i++) { dv.setUint32(i * 8, 0, true); dv.setUint32(i * 8 + 4, 64 << 20, true); }
    await expect(wasi.IMPORTS['wasi_snapshot_preview1.fd_write'](g, 1, 0, 1000, 8000)).rejects.toThrow(wasi.OutputFull);
  });

  test('an output flood ends the call', async () => {
    const r = await run("python3 -c 'while True: print(\"x\" * 1000)'", { maxStringBytes: 1 << 20 });
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toContain('output too large');
  }, T);

  test('paths stay in the machine', async () => {
    const sh = await shell();
    const hostFile = fileURLToPath(import.meta.url); // exists on the host, not in the machine
    expect((await sh.run(`python3 -c 'open("${hostFile}")'`)).stderr).toContain('FileNotFoundError');
    expect((await sh.run("python3 -c 'print(open(\"/../../../etc/hostname\").read(), end=\"\")'")).stdout).toBe('topwater\n');
    await sh.run('ln -s /etc/hostname /tmp/link && ln -s /tmp/loop /tmp/loop');
    expect((await sh.run("python3 -c 'print(open(\"/tmp/link\").read(), end=\"\")'")).stdout).toBe('topwater\n');
    expect((await sh.run("python3 -c 'open(\"/tmp/loop\")'")).stderr).toContain('[Errno 32]'); // ELOOP, in WASI's numbering
  }, T);

  test('the stdlib zip is read-only and invisible to commands', async () => {
    const sh = await shell();
    let r = await sh.run(`python3 -c 'open("${wasi.STDLIB_PATH}", "ab")'`);
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toContain('OSError');
    r = await sh.run(`python3 -c 'import os; os.remove("${wasi.STDLIB_PATH}")'`);
    expect(r.exitCode).toBe(1);
    expect((await sh.run("python3 -c 'import json; print(json.dumps(1))'")).stdout).toBe('1\n');
    expect((await sh.run('ls /usr/local/lib')).stdout).toBe('');
  }, T);

  test('descriptor abuse fails cleanly', async () => {
    expect((await run("python3 -c 'import os; os.read(999, 1)'")).stderr).toContain('Bad file descriptor');
    const r = await run("python3 -c '\nfs = [open(\"/etc/hostname\") for _ in range(5000)]'");
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toContain('OSError');
    expect((await run("python3 -c 'import os; os.close(3); os.close(0); os.close(1); print(\"lost\")'; echo after")).stdout).toBe('after\n');
  }, T);

  test('no processes, threads or sockets', async () => {
    for (const [code, error] of [['import subprocess; subprocess.run(["ls"])', 'OSError'],
      ['import socket; socket.socket().connect(("1.1.1.1", 80))', 'OSError'],
      ['import threading; threading.Thread(target=print).start()', 'RuntimeError']]) {
      const r = await run(`python3 -c '${code}'`);
      expect(r.exitCode, code).toBe(1);
      expect(r.stderr, code).toContain(error);
    }
  }, T);

  test('permissions hold', async () => {
    expect((await run("python3 -c 'open(\"/etc/passwd\", \"a\").write(\"x\")'")).stderr).toContain('PermissionError');
    expect((await run("python3 -c 'import os; os.listdir(\"/root\")'")).stderr).toContain('PermissionError');
  }, T);
});
