// curl and wget against tests/web/cases.jsonl (recorded from real curl 8.5.0 and wget 1.21.4), through a fake transport
// that answers like tests/web/server.py; then what the policy guarantees, as WebAccessTests.cs checks it in the C# port.
import { afterEach, describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { createServer, type AddressInfo } from 'node:net';
import { deflateSync, gzipSync } from 'node:zlib';
import { Shell, Recording, startRecording, type WebPolicy, type WebTransport } from '../src/index.js';
import { isBlocked, parseIPv4, parseIPv6, pinnedTransport } from '../src/web.js';

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

// hidden, as real curl and wget fail for a host they cannot reach
const fixturePolicy = (transport: WebTransport): WebPolicy => ({ domains: ['api.test'], allowHttp: true, methods: ['GET', 'POST', 'PUT', 'DELETE', 'PATCH'], explainBlocked: false, transport });

async function make(policy?: (t: WebTransport) => WebPolicy) {
  const f = fixture();
  const sh = new Shell().allowWeb((policy ?? fixturePolicy)(f.transport));
  await sh.run('mkdir -p /tmp/w && cd /tmp/w');
  return { sh, f };
}

/** Serves the "raw" routes on a loopback port, byte for byte as tests/web/server.py does, then closes. */
async function rawServer() {
  const server = createServer(socket => {
    let text = '';
    socket.on('data', d => {
      text += d.toString('latin1');
      if (!text.includes('\r\n\r\n')) return;
      const route = routes[text.split(' ')[1]];
      if (!route.reset) { socket.end(Buffer.from(route.raw, 'latin1')); return; }
      socket.write(Buffer.from(route.raw, 'latin1'));
      setTimeout(() => socket.resetAndDestroy(), 200); // a reset (RST) instead of a close
    });
    socket.on('error', () => undefined);
  });
  await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
  return { server, port: (server.address() as AddressInfo).port };
}

/** The real connection path to rawServer: the wire cases are malformed responses a fake transport cannot send. */
async function wireShell(port: number, credentials?: WebPolicy['credentials']) {
  const sh = new Shell().allowWeb({ domains: [`api.test:${port}`], allowHttp: true, explainBlocked: false, credentials,
    transport: pinnedTransport({ resolve: () => ['127.0.0.1'], allowLoopback: true }) });
  await sh.run('mkdir -p /tmp/w && cd /tmp/w');
  return sh;
}

describe('tests/web cases', () => {
  for (const c of cases) it(c.id, async () => {
    let r;
    if (c.wire) {
      const { server, port } = await rawServer();
      try { r = await (await wireShell(port)).run(c.script.replaceAll('http://api.test/', `http://api.test:${port}/`)); } finally { server.close(); }
    } else r = await (await make()).sh.run(c.script);
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

  it('blocked hosts are explained by default', async () => {
    const { sh, f } = await make();
    const explain = new Shell().allowWeb({ domains: ['api.test'], transport: fixture().transport });
    let r = await explain.run('curl -sS https://evil.test/; curl -sS http://api.test/; curl -sS https://127.0.0.1/; curl -sS -X POST https://api.test/');
    expect(r.stderr).toBe(
      'curl: (6) Blocked by the web policy: evil.test is not an allowed host (allowed: api.test)\n' +
      'curl: (1) Blocked by the web policy: only https:// URLs are allowed\n' +
      'curl: (7) Blocked by the web policy: 127.0.0.1 is a private or reserved address\n' +
      'curl: (1) Blocked by the web policy: POST requests are not allowed\n');
    r = await sh.run('curl -sS -X PROPFIND http://api.test/echo; echo $?');
    expect(r.stdout).toBe('1\n');
    expect(r.stderr).toBe('curl: (1) Blocked by the web policy: PROPFIND requests are not allowed\n');
    expect(f.requests).toHaveLength(0);
  });

  it('is https only by default', async () => {
    const sh = new Shell().allowWeb({ domains: ['api.test'], explainBlocked: false, transport: fixture().transport });
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
      '::', '::1', '::ffff:10.0.0.1', '::7f00:1', '64:ff9b::a9fe:a9fe', '2002:7f00:1::', 'fc00::1', 'fe80::1', 'fec0::1', 'ff02::1', '2001::1', '2001:db8::1',
      '168.63.129.16', '192.88.99.1', '::ffff:0:8.8.8.8', '100::1', '100::ffff:ffff:ffff:ffff', '2001:20::1', '2001:2f::1', '3fff::1', '3fff:fff::1', '5f00::1', '5f00:ffff::1'];
    const open = ['8.8.8.8', '1.1.1.1', '172.32.0.1', '100.128.0.1', '2606:4700::1111', '2002:808:808::', '168.63.129.17', '192.88.98.1', '100:0:0:1::1',
      '2001:30::1', '3fff:1000::1', '5f01::1'];
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

describe('board 030', () => {
  const SECRET = 'SUPERSECRETTOKEN123';
  /** A host with a credential whose /echo shows the Authorization header it got, gzipped (or deflated) when asked, and honours Range. */
  function credentialHost(policy?: (t: WebTransport) => WebPolicy) {
    const f = fixture();
    f.override = req => {
      const h = Object.fromEntries(req.headers.map(([k, v]) => [k.toLowerCase(), v]));
      let text = enc.encode(`Authorization: ${h.authorization}\n`);
      const range = /^bytes=(\d+)-(\d+)$/.exec(h.range ?? '');
      if (range) text = text.slice(Number(range[1]), Number(range[2]) + 1);
      const encoding = /gzip/.test(h['accept-encoding'] ?? '') ? 'gzip' : /deflate/.test(h['accept-encoding'] ?? '') ? 'deflate' : null;
      if (encoding) text = new Uint8Array(encoding === 'gzip' ? gzipSync(text) : deflateSync(text));
      return { status: 200, reason: 'OK', headers: [...(encoding ? [['Content-Encoding', encoding]] as [string, string][] : []), ['Content-Length', String(text.length)]], body: text };
    };
    const sh = new Shell().allowWeb(policy?.(f.transport) ?? { domains: ['cred.test'], transport: f.transport, credentials: { 'cred.test': () => 'Bearer ' + SECRET } });
    return { sh, f };
  }

  for (const script of [
    'curl -sS --compressed https://cred.test/echo',
    "curl -sS -H 'Accept-Encoding: gzip' https://cred.test/echo | gunzip",
    "curl -sS -H 'Accept-Encoding: deflate' https://cred.test/echo",
    "curl -sS -H 'Accept-Encoding: gzip' -o p.gz https://cred.test/echo; zcat p.gz",
    "wget -q -O - --header 'Accept-Encoding: gzip' https://cred.test/echo | gunzip",
    `a=$(curl -s -H 'Range: bytes=0-20' https://cred.test/echo); b=$(curl -s -H 'Range: bytes=21-60' https://cred.test/echo); echo "$a$b"`,
  ]) it(`the credential does not leak: ${script}`, async () => {
    const { sh, f } = credentialHost();
    const r = await sh.run(script);
    expect(r.stdout + r.stderr).not.toContain(SECRET);
    expect(f.requests.length).toBeGreaterThan(0);
    for (const h of f.seen) expect(h.range).toBeUndefined();
  });

  it('a compressed response is decoded, recorded decoded and scrubbed', async () => {
    const { sh } = credentialHost();
    const rec = startRecording(sh);
    let r = await sh.run('curl -sS --compressed https://cred.test/echo');
    expect(r.stdout).toBe('Authorization: [REDACTED]\n');
    r = await sh.run("curl -sSi -H 'Accept-Encoding: gzip' https://cred.test/echo");
    expect(r.stdout).toBe('HTTP/1.1 200 OK\r\n\r\nAuthorization: [REDACTED]\n'); // no Content-Encoding or Content-Length left
    rec.stop();
    const file = await rec.recording.save();
    const loaded = await Recording.load(file);
    for (const x of loaded.calls.flatMap(c => c.web ?? [])) {
      const text = new TextDecoder('latin1').decode(x.body);
      expect(text).toBe('Authorization: [REDACTED]\n');
    }
    expect(new TextDecoder('latin1').decode(file)).not.toContain(SECRET);
  });

  it('an unknown or corrupt encoding from a credentialed host fails', async () => {
    const { sh, f } = credentialHost();
    f.override = () => ({ status: 200, reason: 'OK', headers: [['Content-Encoding', 'br']], body: enc.encode(SECRET) });
    let r = await sh.run('curl -sS https://cred.test/echo; echo $?');
    expect(r.stdout).toBe('56\n');
    expect(r.stderr).toBe('curl: (56) Failure when receiving data from the peer\n');
    f.override = () => ({ status: 200, reason: 'OK', headers: [['Content-Encoding', 'gzip']], body: Uint8Array.of(0x1f, 0x8b, 8, 0, 1, 2, 3) });
    r = await sh.run('curl -sS https://cred.test/echo; echo $?');
    expect(r.stdout).toBe('56\n');
    // a bomb stops at the cap
    f.override = () => ({ status: 200, reason: 'OK', headers: [['Content-Encoding', 'gzip']], body: new Uint8Array(gzipSync(new Uint8Array(1_000_000))) });
    const capped = credentialHost(t => ({ domains: ['cred.test'], maxResponseBytes: 10_000, transport: t, credentials: { 'cred.test': () => 'Bearer ' + SECRET } }));
    capped.f.override = f.override;
    r = await capped.sh.run('curl -s https://cred.test/echo | wc -c; echo ${PIPESTATUS[0]}');
    expect(r.stdout).toBe('10000\n63\n');
  });

  it('a blocked host names the allowed sites', async () => {
    const sh = new Shell().allowWeb({ domains: Array.from({ length: 12 }, (_, i) => `s${i + 1}.test`), transport: fixture().transport });
    let r = await sh.run('curl -sS https://evil.test/; wget -q https://evil.test/; echo $?');
    const allowed = 's1.test, s2.test, s3.test, s4.test, s5.test, s6.test, s7.test, s8.test, s9.test, s10.test, and 2 more';
    expect(r.stderr).toBe(`curl: (6) Blocked by the web policy: evil.test is not an allowed host (allowed: ${allowed})\n` +
      `wget: blocked by the web policy: evil.test is not an allowed host (allowed: ${allowed})\n`);
    expect(r.stdout).toBe('4\n');
    r = await new Shell().allowWeb({ transport: fixture().transport }).run('curl -sS https://evil.test/');
    expect(r.stderr).toBe('curl: (6) Blocked by the web policy: evil.test is not an allowed host (allowed: none)\n');
    r = await new Shell().run('curl -sS https://evil.test/'); // no policy: an offline machine
    expect(r.stderr).toBe('curl: (6) Could not resolve host: evil.test\n');
  });

  it('a listed host allows only its default port unless a port is listed', async () => {
    const f = fixture();
    const sh = new Shell().allowWeb({ domains: ['api.test', 'other.test:8443'], allowHttp: true, transport: f.transport });
    let r = await sh.run('curl -sS https://api.test:22/; echo $?; curl -s https://api.test:443/hello; curl -s http://api.test/hello; ' +
      'curl -s https://other.test:8443/hello; curl -sS https://other.test/; echo $?');
    expect(r.stdout).toBe('7\nhello\nhello\nhello\n7\n');
    expect(r.stderr).toBe('curl: (7) Blocked by the web policy: api.test:22 is not an allowed host (allowed: api.test, other.test:8443)\n' +
      'curl: (7) Blocked by the web policy: other.test is not an allowed host (allowed: api.test, other.test:8443)\n');
    expect(f.requests).toHaveLength(3);
    const hidden = new Shell().allowWeb({ domains: ['api.test'], transport: f.transport, explainBlocked: false });
    r = await hidden.run('curl -sS https://api.test:22/; wget https://api.test:22/; echo $?');
    expect(r.stderr).toContain('curl: (7) Failed to connect to api.test port 22 after 0 ms: Couldn\'t connect to server\n');
    expect(r.stdout).toBe('4\n');
    // wget -q still says why when it explains
    r = await sh.run('wget -q https://api.test:22/');
    expect(r.stderr).toBe('wget: blocked by the web policy: api.test:22 is not an allowed host (allowed: api.test, other.test:8443)\n');
  });

  it('a credential goes only to its port', async () => {
    const f = fixture();
    const sh = new Shell().allowWeb({
      domains: ['api.test', 'api.test:8443'], transport: f.transport,
      credentials: { 'api.test': () => 'Bearer one-secret-value', 'api.test:8443': () => 'Bearer two-secret-value' },
    });
    await sh.run('curl -s https://api.test/echo; curl -s https://api.test:8443/echo');
    expect(f.seen[0].authorization).toBe('Bearer one-secret-value');
    expect(f.seen[1].authorization).toBe('Bearer two-secret-value');
    const one = new Shell().allowWeb({ domains: ['api.test', 'api.test:8443'], transport: f.transport, credentials: { 'api.test': () => 'Bearer one-secret-value' } });
    await one.run('curl -s https://api.test:8443/echo');
    expect(f.seen[2].authorization).toBeUndefined();
    expect(() => new Shell().allowWeb({ domains: ['*.api.test'], credentials: { 'x.api.test': () => 'x' }, transport: f.transport })).toThrow();
    expect(() => new Shell().allowWeb({ domains: ['api.test:0'], transport: f.transport })).toThrow();
  });

  it("the agent's Authorization and Cookie stay with their origin", async () => {
    const f = fixture();
    f.override = req => req.url.startsWith('https://api.test/')
      ? { status: 302, reason: 'Found', headers: [['Location', 'https://api.test:8443/x']], body: new Uint8Array(0) }
      : { status: 200, reason: 'OK', headers: [], body: enc.encode('ok\n') };
    const sh = new Shell().allowWeb({ domains: ['api.test', 'api.test:8443'], transport: f.transport });
    const r = await sh.run("curl -sL -H 'Authorization: Bearer mine' -H 'Cookie: a=b' https://api.test/go");
    expect(r.stdout).toBe('ok\n');
    expect(f.seen[0].authorization).toBe('Bearer mine');
    expect(f.seen[1].authorization).toBeUndefined();
    expect(f.seen[1].cookie).toBeUndefined();
  });

  it('a replay never goes live, even when configure allows the web', async () => {
    const sh = new Shell();
    const rec = startRecording(sh);
    await sh.run('curl -sS https://api.test/hello; echo $?');
    rec.stop();
    const loaded = await Recording.load(await rec.recording.save());
    expect(loaded.web).toBeUndefined();
    const f = fixture();
    const shell = loaded.createShell(s => { s.allowWeb({ domains: ['api.test'], transport: f.transport }); });
    const r = await shell.run('curl -sS https://api.test/hello');
    expect(r.stderr).toBe('curl: (6) Could not resolve host: api.test\n');
    expect((await loaded.replay(loaded.createShell(s => { s.allowWeb({ domains: ['api.test'], transport: f.transport }); }))).differences).toEqual([]);
    expect(f.requests).toHaveLength(0);
  });

  it('the tool description follows the policy', async () => {
    const { BashTool } = await import('../src/index.js');
    const sh = new Shell();
    expect(BashTool.descriptionFor(sh)).toBe(BashTool.description);
    sh.allowWeb({ domains: ['api.github.com', '*.wikipedia.org'], methods: ['get', 'POST'], transport: fixture().transport });
    const expected = BashTool.description.replace('Python, network access and package installation are not. ',
      'Python and package installation are not; `curl` and `wget` reach only these sites (methods: GET, POST): api.github.com, *.wikipedia.org. ');
    expect(expected).not.toBe(BashTool.description);
    expect(BashTool.descriptionFor(sh)).toBe(expected);
    const { bashTool } = await import('../src/integrations/ai-sdk.js');
    expect(bashTool(sh).description).toBe(expected);
    expect(bashTool(sh, { description: 'Mine.' }).description).toBe('Mine.');
    sh.allowWeb({ domains: Array.from({ length: 23 }, (_, i) => `s${i + 1}.test`), transport: fixture().transport });
    expect(BashTool.descriptionFor(sh)).toContain('s20.test, and 3 more. Read Word');
    // a replay shell keeps the plain text
    const rec = startRecording(sh);
    await sh.run('true');
    rec.stop();
    expect(BashTool.descriptionFor(rec.recording.createShell())).toBe(BashTool.description);
  });

  it('refuses to fall back to fetch in Node without getBuiltinModule', () => {
    const proc = process as { getBuiltinModule?: unknown };
    const saved = proc.getBuiltinModule;
    proc.getBuiltinModule = undefined;
    try {
      expect(() => new Shell().allowWeb({ domains: ['api.test'] })).toThrow(/getBuiltinModule/);
      expect(() => new Shell().allowWeb({ domains: ['api.test'], transport: fixture().transport })).not.toThrow();
    } finally { proc.getBuiltinModule = saved; }
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

  it('retries on a virtual clock stop at the call\'s time limit (board 035 B1)', async () => {
    // the waits move the virtual clock and take no real time, so only the call's limit ends the tries
    const sh = new Shell({ startTime: Date.UTC(2026, 10, 25, 9), maxCallDurationMs: 500 });
    const t0 = Date.now();
    await sh.run('curl -s --retry 2000000000 http://x.test/');
    expect(Date.now() - t0).toBeLessThan(3000);
  });

  it('-w times on a virtual clock replay (board 035 B2)', async () => {
    const sh = new Shell({ startTime: Date.UTC(2026, 10, 25, 9), randomSeed: 3 }).allowWeb(fixturePolicy(fixture().transport));
    const rec = startRecording(sh);
    const r = await sh.run("curl -s -o /dev/null -w '%{time_total} %{time_starttransfer} %{speed_download}\\n' http://api.test/hello");
    expect(r.stdout).toBe('0.000000 0.000000 0\n');
    rec.stop();
    expect((await rec.recording.replay()).differences).toEqual([]);
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

const SECRET = 'Bearer s3cr3t-T0KEN-value', TOKEN = 's3cr3t-T0KEN-value';
/** Whether any 8 characters of the credential's token show. */
const leaks = (text: string) => [...Array(TOKEN.length - 7).keys()].some(i => text.includes(TOKEN.slice(i, i + 8)));

it('keeps nothing of a cut body from a host with a credential', async () => {
  const { server, port } = await rawServer();
  try {
    const sh = await wireShell(port, { [`api.test:${port}`]: () => SECRET });
    const rec = startRecording(sh);
    const r = await sh.run(`curl -sS --compressed http://api.test:${port}/secret-gzip-cut; echo rc=$?; curl -sS http://api.test:${port}/secret-cut; echo rc=$?; curl -sS http://api.test:${port}/reset-body; echo rc=$?`);
    rec.stop();
    expect(r.stdout).toBe('rc=18\nrc=18\nrc=56\n');
    expect(leaks(r.stdout + r.stderr)).toBe(false);
    expect(leaks(Buffer.from(await rec.recording.save()).toString('latin1'))).toBe(false);
  } finally { server.close(); }
});

it('never shows the credential of a compressed body cut at the cap', async () => {
  const body = enc.encode('x'.repeat(1000) + 'Authorization: ' + SECRET + '\n' + 'y'.repeat(200));
  // stored blocks of 40 bytes: a cut decodes to almost the same prefix
  const parts: number[] = [0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 0xff];
  for (let i = 0; i < body.length; i += 40) {
    const n = Math.min(40, body.length - i);
    parts.push(i + n >= body.length ? 1 : 0, n & 0xff, n >> 8, ~n & 0xff, (~n >> 8) & 0xff, ...body.subarray(i, i + n));
  }
  const gz = gzipSync(body);
  const data = new Uint8Array([...parts, ...gz.subarray(gz.length - 8)]); // the same CRC-32 and length trailer
  for (let cap = 1000; cap < data.length; cap += 3) {
    const sh = new Shell().allowWeb({ domains: ['api.test'], allowHttp: true, explainBlocked: false, maxResponseBytes: cap, credentials: { 'api.test': () => SECRET },
      transport: async () => ({ status: 200, reason: 'OK', headers: [['Content-Encoding', 'gzip']], body: data }) });
    const r = await sh.run("curl -sS -H 'Accept-Encoding: gzip' http://api.test/x | tail -c 60");
    expect(leaks(r.stdout + r.stderr), String(cap)).toBe(false);
  }
});

it('replays a body cut short as it came', async () => {
  const { server, port } = await rawServer();
  try {
    const sh = await wireShell(port);
    const rec = startRecording(sh);
    const first = await sh.run(`curl -sS http://api.test:${port}/short-length; curl -sS http://api.test:${port}/empty; wget -q -O- http://api.test:${port}/chunked-short; echo $?`);
    rec.stop();
    expect(first.stdout).toBe('shortok\n4\n');
    const loaded = await Recording.load(await rec.recording.save());
    expect(loaded.calls[0].web!.map(x => [x.error, x.status, new TextDecoder().decode(x.body)])).toEqual([['partial', 200, 'short'], ['empty', 0, ''], ['partial', 200, 'ok\n']]);
    const report = await loaded.replay();
    expect(report.differences).toEqual([]);
  } finally { server.close(); }
});

describe('the pinned connection', () => {
  const proxy = process.env.http_proxy;
  afterEach(() => {
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
      const resolve = (name: string) => { lookups++; return name === 'pinned.test' ? ['127.0.0.1'] : ['169.254.169.254']; };
      const domains = [`pinned.test:${port}`, 'meta.test'];
      let sh = new Shell().allowWeb({ domains, allowHttp: true, explainBlocked: false, transport: pinnedTransport({ resolve, allowLoopback: true }) });
      let r = await sh.run(`curl -sS http://pinned.test:${port}/`);
      expect(r.stderr).toBe('');
      expect(r.stdout).toBe('ok\n');
      expect(host).toBe(`pinned.test:${port}`);
      expect(lookups).toBe(1);

      r = await sh.run('curl -sS http://meta.test/; echo $?; wget -q http://meta.test/; echo $?');
      expect(r.stdout).toBe('6\n4\n');
      expect(r.stderr).toBe('curl: (6) Could not resolve host: meta.test\n');

      // without allowLoopback, loopback is refused like any internal address
      sh = new Shell().allowWeb({ domains, allowHttp: true, explainBlocked: false, transport: pinnedTransport({ resolve }) });
      r = await sh.run(`curl -sS http://pinned.test:${port}/`);
      expect(r.stderr).toBe('curl: (6) Could not resolve host: pinned.test\n');
      sh = new Shell().allowWeb({ domains: ['meta.test'], allowHttp: true, transport: pinnedTransport({ resolve }) });
      r = await sh.run('curl -sS http://meta.test/');
      expect(r.stderr).toBe('curl: (6) Blocked by the web policy: meta.test resolves to a private or reserved address\n');
    } finally {
      server.close();
    }
  });
});
