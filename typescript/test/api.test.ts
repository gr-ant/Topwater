import { describe, expect, it } from 'vitest';
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import { Shell } from '../src/index.js';
import { registerApi, type ApiEndpoint } from '../src/api.js';

const TOKEN = 's3cr3t-T0KEN-value';

interface Seen { method: string; url: URL; headers: Headers; body: string }

/** A fake fetch that records every request and answers with `respond`. */
function makeStub() {
  const stub = {
    requests: [] as Seen[],
    respond: (_req: Seen): Response | Promise<Response> => new Response('{"ok":true}', { status: 200, statusText: 'OK' }),
    fetch: (async (input: string | URL | Request, init?: RequestInit) => {
      expect(init?.redirect).toBe('manual');
      const seen: Seen = {
        method: init?.method ?? 'GET', url: new URL(String(input)), headers: new Headers(init?.headers),
        body: init?.body ? new TextDecoder().decode(init.body as Uint8Array) : '',
      };
      stub.requests.push(seen);
      return stub.respond(seen);
    }) as typeof fetch,
  };
  return stub;
}

function make(more?: (e: Record<string, ApiEndpoint>) => void) {
  const stub = makeStub();
  const endpoints: Record<string, ApiEndpoint> = {
    jira: { baseUrl: 'https://acme.atlassian.net/rest/api/3', description: 'Issue tracker', auth: () => `Bearer ${TOKEN}`, paths: ['/issue/', '/search'] },
    crm: { baseUrl: 'https://crm.example.com/v1/', description: 'CRM', auth: () => TOKEN, authHeader: 'X-Api-Key', methods: ['GET', 'POST'] },
  };
  more?.(endpoints);
  const sh = registerApi(new Shell(), 'api', endpoints, stub.fetch);
  return { sh, stub };
}

describe('api command', () => {
  it('gets with the credential and pipes the body', async () => {
    const { sh, stub } = make();
    const r = await sh.run("api get jira /issue/PROJ-1 --query 'fields=summary,status' -q 'x=a b&c=d#e' | jq -r .ok");
    expect([r.exitCode, r.stdout]).toEqual([0, 'true\n']);
    expect(stub.requests).toHaveLength(1);
    const req = stub.requests[0];
    expect(req.method).toBe('GET');
    expect(req.url.href).toBe('https://acme.atlassian.net/rest/api/3/issue/PROJ-1?fields=summary%2Cstatus&x=a%20b%26c%3Dd%23e');
    expect(req.headers.get('Authorization')).toBe(`Bearer ${TOKEN}`);

    await sh.run('api get crm /contacts');
    expect(stub.requests[1].headers.get('X-Api-Key')).toBe(TOKEN);
    expect(stub.requests[1].headers.has('Authorization')).toBe(false);
    expect(stub.requests[1].url.href).toBe('https://crm.example.com/v1/contacts');
  });

  it('help lists endpoints without the credential', async () => {
    const { sh } = make();
    const help = (await sh.run('api --help')).stdout;
    expect(help).toContain('jira  Issue tracker');
    expect(help).toContain('methods: GET  paths: /issue/, /search');
    expect(help).toContain('crm  CRM');
    expect(help).toContain('methods: GET, POST');
    expect(help).not.toContain(TOKEN);
    expect((await sh.run('help')).stdout).toContain('api');
  });

  it.each([
    'https://evil.com/x', '//evil.com/x', '/issue//evil.com', 'issue/1', '/\\evil.com/x', '\\\\evil.com\\x', '/issue/1@evil.com',
    '/issue/../../admin', '/issue/../admin', '/issue/%2e%2e/admin', '/issue/%2E%2E/%2e%2e/admin', '/issue/.%2e/admin',
    '/issue/%252e%252e/admin', '/issue/..;/admin', '/issue%2f..%2fadmin', '/issue/%zz', '/searchadmin', '/admin?x=/issue/',
    '/issue/1#frag', '/issue/１',
  ])('refuses path %j', async path => {
    const { sh, stub } = make();
    sh.environment.set('P', path);
    const r = await sh.run('api get jira "$P"');
    expect(r.exitCode).toBe(2);
    expect(r.stderr).toContain('is not allowed');
    expect(stub.requests).toEqual([]);
  });

  it.each(['/x/../../y', '/%2e%2e/other', '/..'])('without prefixes %j still cannot climb above the base', async path => {
    const { sh, stub } = make();
    sh.environment.set('P', path);
    expect((await sh.run('api get crm "$P"')).exitCode).toBe(2);
    expect(stub.requests).toEqual([]);
  });

  it('refuses control characters everywhere', async () => {
    const { sh, stub } = make();
    expect((await sh.run("api get jira $'/issue/1\\r\\nX-Evil: 1'")).exitCode).toBe(2);
    expect((await sh.run('api get jira /issue/%0d%0aX')).exitCode).toBe(2);
    expect((await sh.run("api get jira /issue/1 -H $'Accept: a\\r\\nAuthorization: x'")).exitCode).toBe(2);
    expect(stub.requests).toEqual([]);
  });

  it('resolves dot segments inside the allowed area', async () => {
    const { sh, stub } = make();
    expect((await sh.run('api get jira /issue/A/../B/./C')).exitCode).toBe(0);
    expect((await sh.run('api get jira /search')).exitCode).toBe(0);
    expect((await sh.run("api get jira '/search/x?jql=project%3DX'")).exitCode).toBe(0);
    expect(stub.requests.map(r => r.url.pathname)).toEqual(['/rest/api/3/issue/B/C', '/rest/api/3/search', '/rest/api/3/search/x']);
    expect(stub.requests[2].url.search).toBe('?jql=project%3DX');
  });

  it('allow-lists methods; GET is the default', async () => {
    const { sh, stub } = make();
    for (const cmd of ["api post jira /issue --data '{}'", 'api delete jira /issue/1', 'api put crm /contacts/1 -d x', "api 'GET /x HTTP/1.1' crm /a"]) {
      const r = await sh.run(cmd);
      expect(r.exitCode).toBe(2);
      expect(r.stderr).toContain('not allowed');
    }
    expect(stub.requests).toEqual([]);
    expect((await sh.run('api POST crm /contacts -d \'{"a":1}\'')).exitCode).toBe(0);
    expect([stub.requests[0].method, stub.requests[0].body, stub.requests[0].headers.get('Content-Type')]).toEqual(['POST', '{"a":1}', 'application/json']);
  });

  it('takes the body from a file or stdin', async () => {
    const { sh, stub } = make();
    sh.fs.writeText('/home/agent/c.json', '{"from":"file"}');
    expect((await sh.run('api post crm /contacts --data @c.json')).exitCode).toBe(0);
    expect((await sh.run("echo '{\"from\":\"stdin\"}' | api post crm /contacts --data - -H 'content-type: text/plain'")).exitCode).toBe(0);
    expect(stub.requests[0].body).toBe('{"from":"file"}');
    expect([stub.requests[1].body, stub.requests[1].headers.get('Content-Type')]).toEqual(['{"from":"stdin"}\n', 'text/plain']);
    expect((await sh.run('api post crm /contacts --data @nope.json')).exitCode).toBe(2);
    expect((await sh.run('api post crm /contacts --data @/root/secret')).exitCode).toBe(2);
    sh.fs.writeText('/home/agent/big', 'x'.repeat(1_000_001));
    expect((await sh.run('api post crm /contacts --data @big')).exitCode).toBe(2);
    expect(stub.requests).toHaveLength(2);
  });

  it.each([
    'Authorization: Bearer mine', 'X-Api-Key: mine', 'Cookie: a=b', 'Host: evil.com', 'Proxy-Authorization: x',
    'Transfer-Encoding: chunked', 'Content-Length: 5',
  ])('the agent cannot set %j', async header => {
    const { sh, stub } = make();
    sh.environment.set('H', header);
    expect((await sh.run('api get crm /contacts -H "$H"')).exitCode).toBe(2);
    expect(stub.requests).toEqual([]);
  });

  it('lets allowed headers through', async () => {
    const { sh, stub } = make();
    expect((await sh.run("api get crm /contacts -H 'accept: text/csv'")).exitCode).toBe(0);
    expect(stub.requests[0].headers.get('Accept')).toBe('text/csv');
  });

  it('scrubs echoed credentials from bodies, headers and status lines', async () => {
    const { sh, stub } = make();
    stub.respond = () => new Response(`{"you_sent":"Bearer ${TOKEN}","raw":"${TOKEN}"}`, { status: 401, statusText: `Bad ${TOKEN}`, headers: { 'X-Echo': TOKEN } });
    const r = await sh.run('api get jira /issue/1 -i');
    expect(r.exitCode).toBe(1);
    expect(r.stdout + r.stderr).not.toContain(TOKEN);
    expect(r.stdout).toContain('"raw":"[REDACTED]"');
    expect(r.stdout).toContain('X-Echo: [REDACTED]');
    expect(r.stderr).toContain('HTTP 401');
  });

  it('scrubs a credential echoed in a body that is still compressed', async () => {
    const { gzipSync, deflateSync } = await import('node:zlib');
    const { sh, stub } = make();
    for (const [encoding, pack] of [['gzip', gzipSync], ['deflate', deflateSync]] as const) {
      stub.respond = () => new Response(new Uint8Array(pack(`{"you_sent":"Bearer ${TOKEN}"}`)), { status: 200, headers: { 'Content-Encoding': encoding } });
      const r = await sh.run('api get jira /issue/1');
      expect(r.stdout, encoding).toBe('{"you_sent":"[REDACTED]"}\n');
    }
    // a body fetch already decoded keeps its Content-Encoding header and is read as it is
    stub.respond = () => new Response(`plain ${TOKEN}`, { status: 200, headers: { 'Content-Encoding': 'gzip' } });
    expect((await sh.run('api get jira /issue/1')).stdout).toBe('plain [REDACTED]\n');
    stub.respond = () => new Response(Uint8Array.of(0x1f, 0x8b, 8, 0, 1, 2), { status: 200, headers: { 'Content-Encoding': 'gzip' } });
    const r = await sh.run('api get jira /issue/1');
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toMatch(/^api: GET jira \/issue\/1 failed: /);
  });

  it('does not scrub values under 8 characters', async () => {
    const { sh, stub } = make(e => { e.short = { baseUrl: 'https://s.example', auth: () => 'abc' }; });
    stub.respond = () => new Response('abc abc', { status: 200 });
    expect((await sh.run('api get short /x')).stdout).toBe('abc abc\n');
  });

  it('still scrubs a credential cut by the size cap', async () => {
    const { sh, stub } = make(e => { e.big = { baseUrl: 'https://big.example', auth: () => TOKEN, authHeader: 'X-Key', maxResponseBytes: 100 }; });
    stub.respond = () => new Response('a'.repeat(95) + TOKEN + 'b'.repeat(5000), { status: 200 });
    const r = await sh.run('api get big /x');
    expect(r.exitCode).toBe(1);
    expect(r.stdout).not.toContain(TOKEN.slice(0, 5));
    expect(r.stdout).toHaveLength(101); // the cap plus a newline
    expect(r.stderr).toContain('truncated at 100 bytes');
  });

  it('exits 1 for errors and redirects, keeping the body', async () => {
    const { sh, stub } = make();
    stub.respond = () => new Response('{"error":"no such issue"}', { status: 404, statusText: 'Not Found' });
    const r = await sh.run('api get jira /issue/X | jq -r .error');
    expect(r.stdout).toBe('no such issue\n');
    expect(r.stderr).toContain('api: HTTP 404 Not Found');
    expect((await sh.run('api get jira /issue/X')).exitCode).toBe(1);

    stub.respond = () => new Response(null, { status: 302, statusText: 'Found', headers: { Location: 'https://evil.com/steal' } });
    const red = await sh.run('api get jira /issue/X');
    expect(red.exitCode).toBe(1);
    expect(red.stderr).toContain('HTTP 302 Found (redirect to https://evil.com/steal not followed)');
    expect(stub.requests).toHaveLength(3);
  });

  it('turns failures into one-line errors without leaking messages', async () => {
    const { sh, stub } = make();
    stub.respond = () => { throw new TypeError('fetch failed', { cause: new Error('getaddrinfo ENOTFOUND acme') }); };
    const r = await sh.run('api get jira /issue/1');
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toBe('api: GET jira /issue/1 failed: getaddrinfo ENOTFOUND acme\n');

    stub.respond = () => { throw new RangeError(`handler bug, header was Bearer ${TOKEN}`); };
    const r2 = await sh.run('api get jira /issue/1');
    expect([r2.exitCode, r2.stderr]).toEqual([1, 'api: GET jira /issue/1 failed: RangeError\n']);
  });

  it('enforces the timeout itself', async () => {
    const slow = ((_url: string, init?: RequestInit) => new Promise((_, reject) => init!.signal!.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError'))))) as unknown as typeof fetch;
    const sh = registerApi(new Shell(), 'api', { s: { baseUrl: 'https://s.example', timeoutMs: 100 } }, slow);
    const r = await sh.run('api get s /x');
    expect([r.exitCode, r.stderr]).toEqual([1, 'api: s did not answer within 0.1 s\n']);
  });

  it('does not leak the message of a failing auth callback', async () => {
    const { sh, stub } = make(e => { e.vault = { baseUrl: 'https://v.example', auth: () => { throw new Error(`vault said ${TOKEN}`); } }; });
    const r = await sh.run('api get vault /x');
    expect([r.exitCode, r.stderr]).toEqual([1, 'api: the credentials for vault are not available\n']);
    expect(stub.requests).toEqual([]);
  });

  it('accepts an async auth callback', async () => {
    const { sh, stub } = make(e => { e.a = { baseUrl: 'https://a.example', auth: async () => `Bearer ${TOKEN}` }; });
    await sh.run('api get a /x');
    expect(stub.requests[0].headers.get('Authorization')).toBe(`Bearer ${TOKEN}`);
  });

  it('usage errors exit 2 without a call', async () => {
    const { sh, stub } = make();
    for (const cmd of ['api get', 'api get nope /x', 'api get jira /issue/1 --bogus', 'api get jira /issue/1 -q novalue', 'api get jira /issue/1 extra'])
      expect((await sh.run(cmd)).exitCode).toBe(2);
    expect(stub.requests).toEqual([]);
  });

  it('refuses unsafe configuration at registration', () => {
    const reg = (e: ApiEndpoint) => registerApi(new Shell(), 'api', { x: e }, makeStub().fetch);
    expect(() => reg({ baseUrl: 'http://plain.example' })).toThrow();
    expect(() => reg({ baseUrl: 'https://user:pw@h.example' })).toThrow();
    expect(() => reg({ baseUrl: 'https://h.example/?k=v' })).toThrow();
    expect(() => reg({ baseUrl: '/relative' })).toThrow();
    expect(() => reg({ baseUrl: 'https://h.example', authHeader: 'Accept' })).toThrow();
    expect(() => reg({ baseUrl: 'https://h.example', authHeader: 'Host' })).toThrow();
    expect(() => reg({ baseUrl: 'https://h.example', methods: ['GET /'] })).toThrow();
    expect(() => reg({ baseUrl: 'http://localhost', allowHttp: true })).not.toThrow();
  });

  it('round-trips over a real socket without following redirects', async () => {
    const seen: string[] = [];
    const server = createServer((req, res) => {
      seen.push(`${req.url} ${req.headers.authorization}`);
      if (req.url!.endsWith('/moved')) { res.writeHead(301, { Location: `http://127.0.0.1:${(server.address() as AddressInfo).port}/elsewhere` }); res.end(); }
      else res.end(`["echo ${req.headers.authorization}"]`);
    });
    await new Promise<void>(r => server.listen(0, '127.0.0.1', r));
    try {
      const port = (server.address() as AddressInfo).port;
      const sh = registerApi(new Shell(), 'api', { local: { baseUrl: `http://127.0.0.1:${port}/api`, allowHttp: true, auth: () => `Bearer ${TOKEN}` } });
      const ok = await sh.run("api get local /items | jq -r '.[0]'");
      expect([ok.exitCode, ok.stdout]).toEqual([0, 'echo [REDACTED]\n']);
      const moved = await sh.run('api get local /moved');
      expect(moved.exitCode).toBe(1);
      expect(moved.stderr).toContain('HTTP 301');
      expect(moved.stderr).toContain('not followed');
      expect(seen).toEqual([`/api/items Bearer ${TOKEN}`, `/api/moved Bearer ${TOKEN}`]);
    } finally { server.close(); }
  });
});
