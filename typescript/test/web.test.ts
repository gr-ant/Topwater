// curl and wget against tests/web/cases.jsonl (recorded from real curl 8.5.0 and wget 1.21.4), through a fake transport
// that answers like tests/web/server.py; then what the policy guarantees, as WebAccessTests.cs checks it in the C# port.
import { afterEach, describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { createServer, type AddressInfo } from 'node:net';
import { gzipSync } from 'node:zlib';
import { Shell, Recording, startRecording, type WebPolicy, type WebTransport } from '../src/index.js';
import { isBlocked, parseIPv4, parseIPv6, webTestHooks } from '../src/web.js';

const root = new URL('../../', import.meta.url).pathname;
const routes = JSON.parse(readFileSync(root + 'tests/web/routes.json', 'utf8')).routes as Record<string, any>;
const cases = readFileSync(root + 'tests/web/cases.jsonl', 'utf8').split('\n').filter(l => l.trim()).map(l => JSON.parse(l));
const ECHOED = ['content-type', 'accept', 'authorization', 'user-agent', 'x-test'];
const enc = new TextEncoder();
type Req = Parameters<WebTransport>[0];
type Res = Awaited<ReturnType<WebTransport>>;

/** Answers like tests/web/server.py: the routes in routes.json, and /echo with the request it got. */
function fixture() {
  const f = {
    requests: [] as Req[],
    seen: [] as Record<string, string>[],
    override: undefined as ((req: Req) => Res | Promise<Res>) | undefined,
    transport: (async req => {
      f.requests.push(req);
      const headers: Record<string, string> = {};
      for (const [k, v] of req.headers) headers[k.toLowerCase()] = headers[k.toLowerCase()] === undefined ? v : headers[k.toLowerCase()] + ', ' + v;
      f.seen.push(headers);
      if (f.override) return f.override(req);
      const target = /^https?:\/\/[^/]+(\/.*)$/.exec(req.url)![1];
      const r = routes[target.split('?')[0]];
      let status: number, reason: string, hs: [string, string][], data: Uint8Array;
      if (!r) [status, reason, hs, data] = [404, 'Not Found', [['Content-Type', 'text/plain']], enc.encode('no route\n')];
      else if (r.echo) {
        const lines = [`${req.method} ${target}`, ...ECHOED.filter(h => h in headers).map(h => `${h}: ${headers[h]}`)];
        const head = enc.encode(lines.join('\n') + '\n\n'), body = req.body ?? new Uint8Array(0);
        data = new Uint8Array(head.length + body.length); data.set(head); data.set(body, head.length);
        [status, reason, hs] = [200, 'OK', [['Content-Type', 'text/plain']]];
      } else {
        data = r.bodyBase64 !== undefined ? Uint8Array.from(atob(r.bodyBase64), c => c.charCodeAt(0)) : enc.encode(r.body);
        [status, reason, hs] = [r.status, r.reason, r.headers];
      }
      return { status, reason, headers: [...hs, ['Content-Length', String(data.length)]], body: req.method === 'HEAD' ? new Uint8Array(0) : data };
    }) as WebTransport,
  };
  return f;
}

const fixturePolicy = (transport: WebTransport): WebPolicy => ({ domains: ['api.test'], allowHttp: true, methods: ['GET', 'POST', 'PUT', 'DELETE', 'PATCH'], transport });

async function make(policy?: (t: WebTransport) => WebPolicy) {
  const f = fixture();
  const sh = new Shell().allowWeb((policy ?? fixturePolicy)(f.transport));
  await sh.run('mkdir -p /tmp/w && cd /tmp/w');
  return { sh, f };
}

describe('tests/web cases', () => {
  for (const c of cases) it(c.id, async () => {
    const { sh } = await make();
    const r = await sh.run(c.script);
    expect(r.stdout).toBe(c.stdout);
    if (c.stderr !== null) expect(r.stderr).toBe(c.stderr);
    expect(r.exitCode).toBe(c.exitCode);
  });
});

describe('web policy', () => {
  it('without a policy there is no network', async () => {
    const r = await new Shell().run('curl -sS https://example.com/; echo $?; wget -q https://example.com/; echo $?');
    expect(r.stdout).toBe('6\n4\n');
    expect(r.stderr).toBe('curl: (6) Could not resolve host: example.com\n');
  });

  it('blocked hosts can be explained', async () => {
    const { sh, f } = await make();
    const explain = new Shell().allowWeb({ domains: ['api.test'], explainBlocked: true, transport: fixture().transport });
    let r = await explain.run('curl -sS https://evil.test/; curl -sS http://api.test/; curl -sS https://127.0.0.1/; curl -sS -X POST https://api.test/');
    expect(r.stderr).toBe(
      'curl: (6) Blocked by the web policy: evil.test is not an allowed host\n' +
      'curl: (1) Blocked by the web policy: only https:// URLs are allowed\n' +
      'curl: (7) Blocked by the web policy: 127.0.0.1 is a private or reserved address\n' +
      'curl: (1) Blocked by the web policy: POST requests are not allowed\n');
    r = await sh.run('curl -sS -X PROPFIND http://api.test/echo; echo $?');
    expect(r.stdout).toBe('1\n');
    expect(r.stderr).toBe('curl: (1) Blocked by the web policy: PROPFIND requests are not allowed\n');
    expect(f.requests).toHaveLength(0);
  });

  it('is https only by default', async () => {
    const sh = new Shell().allowWeb({ domains: ['api.test'], transport: fixture().transport });
    const r = await sh.run('curl -sS http://api.test/hello; echo $?; curl -s https://api.test/hello');
    expect(r.stdout).toBe('1\nhello\n');
    expect(r.stderr).toBe('curl: (1) Protocol "http" not supported or disabled in libcurl\n');
  });

  for (const [url, code] of [
    ['http://[::ffff:127.0.0.1]/', 7], ['http://[::ffff:a9fe:a9fe]/', 7], ['http://[fd00:ec2::254]/', 7], ['http://[fe80::1]/', 7], ['http://0/', 7],
    ['http://017700000001/', 7], ['http://127.1/', 7], ['http://100.100.100.200/', 7], ['http://api.test.evil.test/', 6], ['http://evil.test/#@api.test/', 6],
    ['http://api.test:80@evil.test/', 6],
  ] as const) it(`refuses ${url}`, async () => {
    const { sh, f } = await make();
    expect((await sh.run(`curl -s '${url}'; echo $?`)).stdout).toBe(`${code}\n`);
    expect(f.requests).toHaveLength(0);
  });

  it('knows the address ranges', () => {
    const ip = (a: string) => a.includes(':') ? parseIPv6(a)! : Uint8Array.from(a.split('.').map(Number));
    const blocked = ['0.0.0.0', '10.1.2.3', '100.64.0.1', '127.0.0.1', '169.254.169.254', '172.16.0.1', '172.31.255.255', '192.168.1.1', '224.0.0.1', '255.255.255.255',
      '::', '::1', '::ffff:10.0.0.1', '::7f00:1', '64:ff9b::a9fe:a9fe', '2002:7f00:1::', 'fc00::1', 'fe80::1', 'fec0::1', 'ff02::1', '2001::1', '2001:db8::1'];
    const open = ['8.8.8.8', '1.1.1.1', '172.32.0.1', '100.128.0.1', '2606:4700::1111', '2002:808:808::'];
    for (const a of blocked) expect(isBlocked(ip(a)), a).toBe(true);
    for (const a of open) expect(isBlocked(ip(a)), a).toBe(false);
    expect([...parseIPv4('2130706433')!]).toEqual([127, 0, 0, 1]);
    expect([...parseIPv4('0177.0.0.01')!]).toEqual([127, 0, 0, 1]);
    expect([...parseIPv4('0x7f.1')!]).toEqual([127, 0, 0, 1]);
    expect(parseIPv4('256.0.0.1')).toBeNull();
    expect(parseIPv4('1.2.3.4.5')).toBeNull();
    expect(parseIPv4('example.com')).toBeNull();
    // the IPv6 parser
    expect([...parseIPv6('::ffff:1.2.3.4')!]).toEqual([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 255, 1, 2, 3, 4]);
    expect([...parseIPv6('1:2:3:4:5:6:7:8')!]).toEqual([0, 1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0, 7, 0, 8]);
    expect([...parseIPv6('1::')!]).toEqual([0, 1, ...new Array(14).fill(0)]);
    for (const bad of ['1:2:3:4:5:6:7:8:9', '1::2::3', ':::', '12345::', 'fe80::1%eth0', '::1.2.3', 'g::1', '1:2:3:4:5:6:7']) expect(parseIPv6(bad), bad).toBeNull();
  });

  it('keeps the credential the host\'s and never shows it', async () => {
    const token = 's3cr3t-T0KEN-value';
    const f = fixture();
    const sh = new Shell().allowWeb({ domains: ['api.test', 'other.test'], allowHttp: true, transport: f.transport, credentials: { 'api.test': () => 'Bearer ' + token } });
    let r = await sh.run("curl -s -H 'Authorization: Bearer mine' -H 'Host: other.test' -u me:pw http://api.test/echo; curl -sv http://api.test/echo -o /dev/null");
    expect(r.stdout).toContain('authorization: [REDACTED]\n');
    expect(r.stdout + r.stderr).not.toContain(token);
    expect(r.stderr).not.toContain('Authorization');
    for (const h of f.seen) { expect(h.authorization).toBe('Bearer ' + token); expect(h.host ?? 'api.test').toBe('api.test'); }

    // a redirect to another allowed host gets neither the credential nor the agent's own Authorization
    f.seen.length = 0;
    f.override = req => req.url.startsWith('http://api.test/')
      ? { status: 302, reason: 'Found', headers: [['Location', 'http://other.test/x']], body: new Uint8Array(0) }
      : { status: 200, reason: 'OK', headers: [], body: enc.encode('ok\n') };
    r = await sh.run("curl -sL -H 'Authorization: Bearer mine' http://api.test/go");
    expect(r.stdout).toBe('ok\n');
    expect(f.seen[0].authorization).toBe('Bearer ' + token);
    expect(f.seen[1].authorization).toBeUndefined();
  });

  it('cuts responses at the cap', async () => {
    const sh = new Shell().allowWeb({ domains: ['api.test'], allowHttp: true, maxResponseBytes: 4, transport: fixture().transport });
    let r = await sh.run('curl -sS http://api.test/hello; echo " $?"; wget -q -O - http://api.test/hello; echo " $?"');
    expect(r.stdout).toBe('hell 63\nhell 1\n');
    expect(r.stderr).toBe('curl: (63) Maximum file size exceeded\nwget: the response is larger than the web policy allows (4 bytes)\n');
    // a gzip bomb stops at the cap too
    const f = fixture();
    const bomb = new Uint8Array(gzipSync(new Uint8Array(1_000_000)));
    f.override = () => ({ status: 200, reason: 'OK', headers: [['Content-Encoding', 'gzip']], body: bomb });
    sh.allowWeb({ domains: ['api.test'], allowHttp: true, maxResponseBytes: 10_000, transport: f.transport });
    r = await sh.run('curl -s --compressed http://api.test/gzip | wc -c; echo ${PIPESTATUS[0]}');
    expect(r.stdout).toBe('0\n63\n');
  });

  it('counts requests per call and per shell', async () => {
    const f = fixture();
    const sh = new Shell().allowWeb({ domains: ['api.test'], allowHttp: true, maxRequestsPerCall: 3, maxRequests: 5, maxRedirects: 50, transport: f.transport });
    let r = await sh.run('curl -sSL http://api.test/loop; echo $?');
    expect(r.stdout).toBe('1\n');
    expect(r.stderr).toBe('curl: (1) Blocked by the web policy: the limit of 3 requests in one call was reached\n');
    r = await sh.run('for i in 1 2 3; do curl -s http://api.test/hello; done; echo $?');
    expect(r.stdout).toBe('hello\nhello\n1\n');
    expect((await sh.run('curl -sS http://api.test/hello')).stderr).toContain('5 requests for this session');
    expect(f.requests).toHaveLength(5);
  });

  it('stops redirects at the policy limit', async () => {
    const { sh } = await make();
    let r = await sh.run('curl -sSL http://api.test/loop');
    expect(r.stderr).toBe('curl: (47) Maximum (10) redirects followed\n');
    r = await sh.run('wget -O - http://api.test/loop; echo $?');
    expect(r.stderr.endsWith('10 redirections exceeded.\n')).toBe(true);
    expect(r.stdout).toBe('8\n');
  });

  it('times out slow answers', async () => {
    const { sh, f } = await make();
    f.override = req => new Promise((_, reject) => req.signal.addEventListener('abort', () => reject(new Error('aborted'))));
    const started = Date.now();
    const r = await sh.run('curl -sS -m 0.2 http://api.test/hello; echo $?; wget -q -T 0.2 http://api.test/hello; echo $?');
    expect(r.stdout).toBe('28\n4\n');
    expect(r.stderr.startsWith('curl: (28) Operation timed out after 200 milliseconds with 0 bytes received\n')).toBe(true);
    expect(Date.now() - started).toBeLessThan(5000);
  });

  it('refuses huge headers', async () => {
    const { sh, f } = await make();
    f.override = () => ({ status: 200, reason: 'OK', headers: [['X-Big', 'a'.repeat(70_000)]], body: enc.encode('x') });
    expect((await sh.run('curl -sS http://api.test/hello; echo $?')).stdout).toBe('56\n');
  });

  it('refuses header injection', async () => {
    const { sh, f } = await make();
    const r = await sh.run("curl -sS -H $'X-Test: a\\r\\nX-Evil: b' http://api.test/echo; echo $?");
    expect(r.stdout).toBe('43\n');
    expect(f.requests).toHaveLength(0);
  });

  it('refuses bad policies', () => {
    expect(() => new Shell().allowWeb({ domains: ['exa mple.com'] })).toThrow();
    expect(() => new Shell().allowWeb({ domains: ['a.com'], credentials: { 'b.com': () => 'x' } })).toThrow();
    expect(() => new Shell().allowWeb({ maxRequests: -1 })).toThrow();
  });
});

describe('recorded web exchanges', () => {
  it('replay without the network', async () => {
    const { sh, f } = await make();
    const rec = startRecording(sh);
    await sh.run('curl -s http://api.test/json');
    await sh.run('curl -sL http://api.test/redirect -o page; wget -q http://api.test/gzip; curl -sS http://nope.test/');
    await sh.run('cat page; wc -c < gzip');
    rec.stop();
    const loaded = await Recording.load(await rec.recording.save());
    expect(loaded.web?.domains).toEqual(['api.test']);
    expect(loaded.calls[1].web).toHaveLength(3); // the redirect, the page, the gzip file; the refused host never reached the network
    expect(new TextDecoder().decode(loaded.calls[1].web![1].body)).toBe('hello\n');

    const before = f.requests.length;
    const report = await loaded.replay();
    expect(report.differences).toEqual([]);
    expect(f.requests.length).toBe(before);
    const shell = await loaded.shellAt(1);
    expect((await shell.run('cat /tmp/w/page')).stdout).toBe('hello\n');

    // asking for something that was not recorded is reported, not fetched
    const r = await loaded.createShell().run('curl -sS http://api.test/hello');
    expect(r.stderr).toBe('curl: (7) Topwater replay: GET http://api.test/hello is not in the recording\n');
  });

  it('replays the C# recording', async () => {
    const rec = await Recording.load(new Uint8Array(readFileSync(root + 'tests/Topwater.Tests/recordings/web-from-csharp.topwater')));
    expect(rec.calls.map(c => c.command)).toEqual([
      'curl -s http://api.test/json',
      'curl -sL -o page http://api.test/redirect; wget -q http://api.test/gzip; cat page; wc -c < gzip',
      'curl -sS http://nope.test/; echo $?',
    ]);
    expect(rec.web?.methods).toEqual(['GET', 'POST', 'PUT', 'DELETE', 'PATCH']);
    expect((await rec.replay()).differences).toEqual([]);
    // starting later serves the later calls' responses
    const at = await rec.shellAt(1);
    expect((await at.run('cat page')).stdout).toBe('hello\n');
  });

  it('keep no credential', async () => {
    const token = 's3cr3t-T0KEN-value';
    const sh = new Shell().allowWeb({ domains: ['api.test'], allowHttp: true, transport: fixture().transport, credentials: { 'api.test': () => 'Bearer ' + token } });
    const rec = startRecording(sh);
    await sh.run('curl -s http://api.test/echo');
    rec.stop();
    const file = await rec.recording.save();
    expect(new TextDecoder('latin1').decode(file)).not.toContain(token);
    const loaded = await Recording.load(file);
    expect(new TextDecoder().decode(loaded.calls[0].web![0].body)).toContain('authorization: [REDACTED]');
    expect(Object.keys(loaded.web!.credentials!)).toEqual(['api.test']);
    expect((await loaded.replay()).differences).toEqual([]);
  });

  it('refuse a bad blob or error kind', async () => {
    const { sh } = await make();
    const rec = startRecording(sh);
    await sh.run('curl -s http://api.test/hello');
    rec.stop();
    const good = rec.recording;
    good.calls[0].web![0] = { ...good.calls[0].web![0], error: 'nope' as never };
    await expect(Recording.load(await good.save())).rejects.toThrow(/unknown error/);
  });
});

describe('the pinned connection', () => {
  const proxy = process.env.http_proxy;
  afterEach(() => {
    webTestHooks.resolve = undefined;
    webTestHooks.allowLoopback = false;
    if (proxy === undefined) delete process.env.http_proxy; else process.env.http_proxy = proxy;
  });

  it('resolves names once and connects to the checked address', async () => {
    let host = '';
    const server = createServer(socket => {
      let text = '';
      socket.on('data', d => {
        text += d.toString('latin1');
        if (!text.includes('\r\n\r\n')) return;
        host = /^Host:\s*(.*)$/im.exec(text)?.[1].trim() ?? '';
        socket.end('HTTP/1.1 200 OK\r\nContent-Length: 3\r\nConnection: close\r\n\r\nok\n');
      });
    });
    await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
    const port = (server.address() as AddressInfo).port;
    try {
      let lookups = 0;
      process.env.http_proxy = 'http://127.0.0.1:1'; // the host's proxy settings are never used
      webTestHooks.resolve = name => { lookups++; return name === 'pinned.test' ? ['127.0.0.1'] : ['169.254.169.254']; };
      webTestHooks.allowLoopback = true;
      let sh = new Shell().allowWeb({ domains: ['pinned.test', 'meta.test'], allowHttp: true });
      let r = await sh.run(`curl -sS http://pinned.test:${port}/`);
      expect(r.stderr).toBe('');
      expect(r.stdout).toBe('ok\n');
      expect(host).toBe(`pinned.test:${port}`);
      expect(lookups).toBe(1);

      r = await sh.run('curl -sS http://meta.test/; echo $?; wget -q http://meta.test/; echo $?');
      expect(r.stdout).toBe('6\n4\n');
      expect(r.stderr).toBe('curl: (6) Could not resolve host: meta.test\n');

      webTestHooks.allowLoopback = false; // without the hook, loopback is refused like any internal address
      r = await sh.run(`curl -sS http://pinned.test:${port + 1}/`);
      expect(r.stderr).toBe('curl: (6) Could not resolve host: pinned.test\n');
      sh = new Shell().allowWeb({ domains: ['meta.test'], allowHttp: true, explainBlocked: true });
      r = await sh.run('curl -sS http://meta.test/');
      expect(r.stderr).toBe('curl: (6) Blocked by the web policy: meta.test resolves to a private or reserved address\n');
    } finally {
      server.close();
    }
  });
});
