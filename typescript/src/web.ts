// Web access for curl and wget (Shell.allowWeb): the policy, the URL parser, the address checks and the session that
// sends each request (or serves a recording's). Without a policy the machine has no network: every host fails to resolve.
import { DeadlineExceeded, specMessage, type CommandContext } from './command.js';
import { concatBytes } from './bytes.js';
import { REASONS, secretsOf } from './api.js';
import { OutputLimitError, gunzip, inflateRaw } from './inflate.js';

/** The web access a shell's `curl` and `wget` get through `shell.allowWeb`. */
export interface WebPolicy {
  /** Host names the agent may reach: `api.github.com` exactly, or `*.wikipedia.org` for its subdomains (not the name itself),
   * on the scheme's default port; `name:8443` (or `*.name:8443`) allows that port instead. */
  domains?: string[];
  /** HTTP methods the agent may use (default GET); GET also allows HEAD. */
  methods?: string[];
  /** Permit plain http:// as well as https://. */
  allowHttp?: boolean;
  /** A response body is cut here (read as it streams in, never whole) and the command fails. Default 10 000 000. */
  maxResponseBytes?: number;
  /** Requests (each redirect counts) in one tool call. Default 20. */
  maxRequestsPerCall?: number;
  /** Requests over the shell's life. Default 500. */
  maxRequests?: number;
  /** Time limit for each request (ms); the call's own limit still applies. Default 30 000. */
  timeoutMs?: number;
  /** The most redirects one command follows; each is checked against this policy like the first URL. Default 10. */
  maxRedirects?: number;
  /** The Authorization header for a host (`host` on the scheme's default port, or `host:port`; the host an exact name in
   * `domains`), called in host code for every request to it. The agent never sees it, cannot replace it, and it is scrubbed
   * from what comes back (gzip and deflate bodies are decoded first). */
  credentials?: Record<string, () => string | Promise<string>>;
  /** Say "blocked by the web policy" and why (default), or false: fail like an unknown host (or an unreachable address). */
  explainBlocked?: boolean;
  /** Sends the requests instead of the network (tests, proxies). It must not follow redirects. The private-address check
   * happens where the name is resolved, so a transport that connects somewhere itself must do it. */
  transport?: WebTransport;
}

/** How a request failed below the policy; a transport throws an error with one of these as its `kind` (anything else is recv). */
export type WebErrorKind = 'resolve' | 'blocked' | 'connect' | 'timeout' | 'tls' | 'recv';
const KINDS: readonly string[] = ['resolve', 'blocked', 'connect', 'timeout', 'tls', 'recv'];

/** Sends one request without following redirects, reading at most `maxBytes + 1` bytes of body. */
export type WebTransport = (req: {
  method: string; url: string; headers: [string, string][]; body?: Uint8Array; timeoutMs: number; maxBytes: number; signal: AbortSignal;
}) => Promise<{ status: number; reason: string; headers: [string, string][]; body: Uint8Array }>;

/** One request a recorded call made and what came back (or how it failed: `error`), so a replay never touches the network. */
export interface WebExchange {
  method: string;
  url: string;
  status: number;
  reason: string;
  headers: [string, string][];
  body: Uint8Array;
  error?: WebErrorKind;
}

/** @internal Why a request failed; each command turns it into its own message and exit status. */
export type WebError = 'badUrl' | 'badHostname' | 'protocol' | 'resolve' | 'connect' | 'timeout' | 'tls' | 'recv' | 'tooLarge' | 'redirects' | 'policy' | 'replay' | 'badHeader';

/** @internal */
export class WebFailure extends Error {
  constructor(readonly kind: WebError, readonly host = '', readonly port = 0, public blocked: string | null = null, public detail = '') { super(kind); }
}

const isAlnum = (c: string) => /^[A-Za-z0-9]$/.test(c);
const hostChars = (s: string) => /^[A-Za-z0-9._-]*$/.test(s);

/** @internal A URL as Topwater reads it, the same in every port: http or https, a host (IPv4 in any inet_aton form is
 * canonical dotted quad), a port and the path with its query. User info is kept for basic auth, never as the host. */
export class WebUrl {
  constructor(readonly scheme: string, readonly host: string, readonly port: number, readonly pathAndQuery: string,
    readonly userInfo: string | null, readonly ip: Uint8Array | null) {}

  get defaultPort() { return this.port === (this.scheme === 'https' ? 443 : 80); }
  get hostForUrl() { return this.host.includes(':') ? `[${this.host}]` : this.host; }
  toString() { return `${this.scheme}://${this.hostForUrl}${this.defaultPort ? '' : ':' + this.port}${this.pathAndQuery}`; }

  /** Parses `text`; a missing scheme means http, as in curl. Throws WebFailure. */
  static parse(text: string): WebUrl {
    const sep = text.indexOf('://');
    const scheme = sep < 0 ? 'http' : text.slice(0, sep).toLowerCase();
    const rest = sep < 0 ? text : text.slice(sep + 3);
    if (sep >= 0 && (scheme.length === 0 || ![...scheme].every(c => isAlnum(c) || c === '+' || c === '-' || c === '.'))) throw new WebFailure('badUrl');
    if (scheme !== 'http' && scheme !== 'https') throw new WebFailure('protocol', scheme);
    if (/[\x00-\x20\x7f-￿]/.test(rest)) throw new WebFailure('badUrl');
    const end = rest.search(/[/?#]/);
    let authority = end < 0 ? rest : rest.slice(0, end), tail = end < 0 ? '' : rest.slice(end);
    const hash = tail.indexOf('#');
    if (hash >= 0) tail = tail.slice(0, hash);
    if (!tail.startsWith('/')) tail = '/' + tail;
    let user: string | null = null;
    const at = authority.indexOf('@'); // the first @, as curl reads it: "a@b@host" has the host "b@host", which is refused
    if (at >= 0) { user = authority.slice(0, at); authority = authority.slice(at + 1); }
    let portText = '';
    if (authority.startsWith('[')) {
      const close = authority.indexOf(']');
      if (close < 0) throw new WebFailure('badUrl');
      const host = authority.slice(1, close).toLowerCase();
      const after = authority.slice(close + 1);
      if (after.length > 0) { if (!after.startsWith(':')) throw new WebFailure('badUrl'); portText = after.slice(1); }
      const v6 = parseIPv6(host);
      if (!v6) throw new WebFailure('badUrl');
      return new WebUrl(scheme, host, parsePort(portText, scheme), tail, user, v6);
    }
    const colon = authority.indexOf(':');
    let host = (colon < 0 ? authority : authority.slice(0, colon)).toLowerCase();
    if (colon >= 0) portText = authority.slice(colon + 1);
    if (host.endsWith('.')) host = host.slice(0, -1);
    if (host.length === 0 || host.length > 253 || !hostChars(host) || host.includes('..')) throw new WebFailure('badHostname');
    const v4 = parseIPv4(host);
    return new WebUrl(scheme, v4 ? v4.join('.') : host, parsePort(portText, scheme), tail, user, v4);
  }

  /** A redirect's Location against this URL: absolute, scheme-relative, absolute-path or relative. */
  resolve(location: string): WebUrl {
    if (location.includes('://')) return WebUrl.parse(location);
    const origin = `${this.scheme}://${this.hostForUrl}${this.defaultPort ? '' : ':' + this.port}`;
    if (location.startsWith('//')) return WebUrl.parse(this.scheme + ':' + location);
    if (location.startsWith('/')) return WebUrl.parse(origin + location);
    const path = this.pathAndQuery.split('?')[0];
    return WebUrl.parse(origin + path.slice(0, path.lastIndexOf('/') + 1) + location);
  }
}

function parsePort(text: string, scheme: string): number {
  if (text.length === 0) return scheme === 'https' ? 443 : 80;
  const p = Number(text);
  if (text.length > 5 || !/^[0-9]+$/.test(text) || p < 1 || p > 65535) throw new WebFailure('badUrl');
  return p;
}

/** @internal IPv4 the way inet_aton reads it: 1 to 4 parts, each decimal, 0x hex or 0 octal (127.1, 2130706433, 0x7f.0.0.1). */
export function parseIPv4(s: string): Uint8Array | null {
  const parts = s.split('.');
  if (parts.length > 4) return null;
  const nums: number[] = [];
  for (let p of parts) {
    let radix = 10;
    if (p.startsWith('0x') || p.startsWith('0X')) { radix = 16; p = p.slice(2); if (p.length === 0) { nums.push(0); continue; } }
    else if (p.length > 1 && p[0] === '0') { radix = 8; p = p.slice(1); }
    if (p.length === 0 || p.length > 12) return null;
    let v = 0;
    for (const c of p) {
      const d = c >= '0' && c <= '9' ? c.charCodeAt(0) - 48 : c >= 'a' && c <= 'f' ? c.charCodeAt(0) - 87 : c >= 'A' && c <= 'F' ? c.charCodeAt(0) - 55 : 99;
      if (d >= radix) return null;
      v = v * radix + d; // at most 16^12 < 2^53: exact
    }
    nums.push(v);
  }
  const last = nums[nums.length - 1];
  for (let i = 0; i < nums.length - 1; i++) if (nums[i] > 255) return null;
  if (last >= 2 ** (8 * (5 - nums.length))) return null;
  let value = last;
  for (let i = 0; i < nums.length - 1; i++) value += nums[i] * 2 ** (8 * (3 - i));
  return Uint8Array.of(Math.floor(value / 2 ** 24) & 255, Math.floor(value / 2 ** 16) & 255, Math.floor(value / 256) & 255, value & 255);
}

/** @internal An IPv6 address (no zone, an IPv4 tail allowed) as its 16 bytes, or null. */
export function parseIPv6(s: string): Uint8Array | null {
  if (!s.includes(':') || s.includes('%')) return null;
  let text = s;
  const tail: number[] = [];
  const lastColon = text.lastIndexOf(':');
  if (text.slice(lastColon + 1).includes('.')) {
    const v4 = text.slice(lastColon + 1).split('.');
    if (v4.length !== 4 || !v4.every(p => /^(0|[1-9][0-9]{0,2})$/.test(p) && Number(p) <= 255)) return null;
    tail.push(Number(v4[0]) << 8 | Number(v4[1]), Number(v4[2]) << 8 | Number(v4[3]));
    text = text.slice(0, lastColon + 1) + (text.endsWith('::' + v4.join('.')) ? '' : '0');
    if (text.endsWith(':0')) text = text.slice(0, -2); // the placeholder only marked the field
    else if (!text.endsWith('::')) return null;
  }
  const halves = text.split('::');
  if (halves.length > 2) return null;
  const fields = (h: string) => h === '' ? [] : h.split(':');
  const head = fields(halves[0]), rest = halves.length === 2 ? fields(halves[1]) : [];
  if (![...head, ...rest].every(f => /^[0-9a-fA-F]{1,4}$/.test(f))) return null;
  const words = [...head, ...rest].map(f => parseInt(f, 16));
  const have = words.length + tail.length;
  if (halves.length === 1 ? have !== 8 : have > 7) return null;
  const all = [...head.map(f => parseInt(f, 16)), ...new Array(8 - have).fill(0), ...rest.map(f => parseInt(f, 16)), ...tail];
  const out = new Uint8Array(16);
  all.forEach((w, i) => { out[2 * i] = w >> 8; out[2 * i + 1] = w & 255; });
  return out;
}

/** @internal Loopback, private, link-local (169.254.169.254, the cloud metadata service), CGNAT, multicast, reserved,
 * unspecified, documentation and benchmarking ranges, Azure's host endpoint and 6to4 relays; for IPv6 also ULA,
 * site-local, Teredo, discard, ORCHIDv2, SRv6, IPv4-translated, and any IPv4 it embeds. */
export function isBlocked(a: Uint8Array): boolean {
  if (a.length === 4) {
    const [x, y] = a;
    return x === 0 || x === 10 || x === 127 || x >= 224 || x === 100 && (y & 0xC0) === 64 || x === 169 && y === 254 || x === 172 && (y & 0xF0) === 16
      || x === 192 && y === 168 || x === 192 && y === 0 && (a[2] === 0 || a[2] === 2) || x === 198 && (y & 0xFE) === 18 || x === 198 && y === 51 && a[2] === 100
      || x === 203 && y === 0 && a[2] === 113 || x === 168 && y === 63 && a[2] === 129 && a[3] === 16 || x === 192 && y === 88 && a[2] === 99;
  }
  if (a.length !== 16) return true;
  const zero = (from: number, to: number) => a.subarray(from, to).every(b => b === 0);
  if (zero(0, 10) && a[10] === 0xff && a[11] === 0xff) return isBlocked(a.subarray(12));          // ::ffff:a.b.c.d
  if (zero(0, 12)) return true;                                                                    // ::, ::1, ::a.b.c.d
  if (zero(0, 8) && a[8] === 0xff && a[9] === 0xff && zero(10, 12)) return true;                   // ::ffff:0:a.b.c.d, IPv4-translated
  if (a[0] === 0x01 && zero(1, 8)) return true;                                                    // 100::/64 discard
  if (a[0] === 0 && a[1] === 0x64 && a[2] === 0xff && a[3] === 0x9b) return isBlocked(a.subarray(12)); // 64:ff9b::/96 NAT64
  if (a[0] === 0x20 && a[1] === 0x02) return isBlocked(a.subarray(2, 6));                          // 2002::/16 6to4
  if (a[0] === 0x20 && a[1] === 0x01 && (a[2] === 0 && a[3] === 0 || a[2] === 0x0d && a[3] === 0xb8)) return true; // Teredo, documentation
  if (a[0] === 0x20 && a[1] === 0x01 && a[2] === 0 && (a[3] & 0xF0) === 0x20) return true;         // 2001:20::/28 ORCHIDv2
  if (a[0] === 0x3f && a[1] === 0xff && (a[2] & 0xF0) === 0 || a[0] === 0x5f && a[1] === 0) return true; // 3fff::/20 documentation, 5f00::/16 SRv6
  return (a[0] & 0xFE) === 0xFC || a[0] === 0xFE && ((a[1] & 0xC0) === 0x80 || (a[1] & 0xC0) === 0xC0) || a[0] === 0xFF; // ULA, link/site-local, multicast
}

/** @internal Test-only: let a test reach a loopback server through the real resolver and connection path. Not on
 * WebPolicy and not exported from the package, so no application setting can switch the address check off. */
export const webTestHooks: { resolve?: (name: string) => string[]; allowLoopback: boolean } = { allowLoopback: false };

/** @internal */
export interface WebResponse { url: WebUrl; method: string; status: number; reason: string; headers: [string, string][]; body: Uint8Array; truncated: boolean }
export const header = (r: { headers: [string, string][] }, name: string) => r.headers.find(h => h[0].toLowerCase() === name.toLowerCase())?.[1];

const MAX_HEADER_BYTES = 64 * 1024;
const MAX_REQUEST_BYTES = 1_000_000; // shortcut: fixed upload cap like registerApi's; make it a policy option when a host needs more
const DROPPED = new Set(['host', 'content-length', 'transfer-encoding', 'connection', 'keep-alive', 'upgrade', 'te', 'trailer', 'proxy-authorization', 'proxy-connection']);

type Policy = Required<Omit<WebPolicy, 'transport'>> & { transport?: WebTransport };

/** @internal One shell's web access: the policy, its counters, and the exchanges of the current call (for recordings), or
 * the recorded exchanges a replay serves instead of the network. */
export class WebSession {
  readonly policy: Policy;
  private readonly sites: { host: string; wild: boolean; port: number | null }[] = [];
  private readonly methods = new Set<string>();
  private readonly credentials: { host: string; port: number | null; auth: () => string | Promise<string> }[] = [];
  private log: WebExchange[] = [];
  private replayAt = 0;
  private total = 0;
  private thisCall = 0;

  constructor(policy: WebPolicy, private readonly replay?: WebExchange[]) {
    this.policy = {
      domains: policy.domains ?? [], methods: policy.methods ?? ['GET'], allowHttp: policy.allowHttp ?? false, maxResponseBytes: policy.maxResponseBytes ?? 10_000_000,
      maxRequestsPerCall: policy.maxRequestsPerCall ?? 20, maxRequests: policy.maxRequests ?? 500, timeoutMs: policy.timeoutMs ?? 30_000,
      maxRedirects: policy.maxRedirects ?? 10, credentials: policy.credentials ?? {}, explainBlocked: policy.explainBlocked ?? true, transport: policy.transport,
    };
    const p = this.policy;
    for (const d of p.domains) {
      const site = hostPort(d);
      const wild = site?.host.startsWith('*.') ?? false;
      const bare = wild ? site!.host.slice(2) : site?.host ?? '';
      if (bare.length === 0 || !hostChars(bare)) throw new Error(`${d}: a domain is a host name or *.name (its subdomains), optionally with :port`);
      this.sites.push({ host: bare, wild, port: site!.port });
    }
    for (const m of p.methods) {
      if (!/^[A-Za-z]+$/.test(m)) throw new Error(`${m}: not an HTTP method`);
      this.methods.add(m.toUpperCase());
    }
    if (this.methods.has('GET')) this.methods.add('HEAD');
    for (const [key, auth] of Object.entries(p.credentials)) {
      const site = hostPort(key);
      if (!site || !this.sites.some(s => !s.wild && s.host === site.host)) throw new Error(`${key}: credentials need an exact host name in domains (host or host:port)`);
      this.credentials.push({ ...site, auth });
    }
    for (const k of ['maxRedirects', 'maxRequests', 'maxRequestsPerCall', 'maxResponseBytes', 'timeoutMs'] as const)
      if (!(typeof p[k] === 'number' && p[k] >= 0 && !Number.isNaN(p[k]))) throw new Error('limits cannot be negative');
  }

  /** A new tool call: its request count starts again, and its exchanges are collected afresh. */
  beginCall() { this.thisCall = 0; this.log = []; }
  takeLog(): WebExchange[] { const l = this.log; this.log = []; return l; }

  /** A replay of a recording: what it serves was recorded, the policy only describes it. */
  get isReplay() { return this.replay !== undefined; }

  private matches(s: { host: string; wild?: boolean; port: number | null }, url: WebUrl, anyPort = false) {
    return (s.wild ? !url.ip && url.host.endsWith('.' + s.host) : url.host === s.host) && (anyPort || (s.port === null ? url.defaultPort : url.port === s.port));
  }

  /** "{host} is not an allowed host (allowed: ...)", the host as in the URL (with a port that is not the scheme's). */
  private notAllowed(url: WebUrl) {
    const d = this.policy.domains.map(s => s.trim());
    const allowed = d.length === 0 ? 'none' : d.slice(0, 10).join(', ') + (d.length > 10 ? `, and ${d.length - 10} more` : '');
    return reason('reasonHost', { host: url.hostForUrl + (url.defaultPort ? '' : ':' + url.port), allowed });
  }

  /** Checks the URL against the policy, then sends one request (no redirects). Throws WebFailure. */
  async send(url: WebUrl, method: string, headers: [string, string][], body: Uint8Array | null, timeoutMs: number, ctx: CommandContext): Promise<WebResponse> {
    const p = this.policy;
    const explain = (why: string) => p.explainBlocked ? why : null;
    if (url.ip && isBlocked(url.ip)) throw new WebFailure('connect', url.host, url.port, explain(reason('reasonPrivate', { host: url.host })));
    if (!this.sites.some(s => this.matches(s, url))) // a name not listed at all does not resolve; a listed one on another port does not connect
      throw new WebFailure(!url.ip && !this.sites.some(s => this.matches(s, url, true)) ? 'resolve' : 'connect', url.host, url.port, explain(this.notAllowed(url)));
    if (url.scheme === 'http' && !p.allowHttp) throw new WebFailure('protocol', 'http', 0, explain(reason('reasonHttps')));
    if (!this.methods.has(method)) throw new WebFailure('policy', '', 0, reason('reasonMethod', { method }));
    if (body && body.length > MAX_REQUEST_BYTES) throw new WebFailure('policy', '', 0, reason('reasonBody', { limit: String(MAX_REQUEST_BYTES) }));
    if (headers.some(([k, v]) => !/^[\x21-\x39\x3b-\x7e]+$/.test(k) || /[\r\n\0]/.test(v)) || headers.reduce((n, [k, v]) => n + k.length + v.length + 4, 0) > 16 * 1024)
      throw new WebFailure('badHeader');
    if (this.thisCall >= p.maxRequestsPerCall) throw new WebFailure('policy', '', 0, reason('reasonCallLimit', { limit: String(p.maxRequestsPerCall) }));
    if (this.total >= p.maxRequests) throw new WebFailure('policy', '', 0, reason('reasonSessionLimit', { limit: String(p.maxRequests) }));
    this.thisCall++; this.total++;

    let sent = headers.filter(([k]) => !DROPPED.has(k.toLowerCase()) && !k.toLowerCase().startsWith('proxy-'));
    let secrets: string[] = [];
    const auth = (this.credentials.find(c => c.port !== null && this.matches(c, url)) ?? this.credentials.find(c => this.matches(c, url)))?.auth;
    if (auth) {
      // the agent cannot replace it, nor fetch the body in pieces that each slip past the scrub
      sent = sent.filter(([k]) => !['authorization', 'range', 'if-range'].includes(k.toLowerCase()));
      let credential: string;
      try { credential = (await auth()) ?? ''; } catch { throw new WebFailure('policy', '', 0, reason('reasonNoCredential', { host: url.host })); } // the message may describe the secret
      if (/[\x00-\x1f\x7f-\x9f]/.test(credential)) throw new WebFailure('policy', '', 0, reason('reasonBadCredential', { host: url.host }));
      if (credential.length > 0) sent.push(['Authorization', credential]);
      secrets = secretsOf(credential);
    }

    const exchange = this.replay ? this.nextRecorded(method, url.toString()) : await this.exchange(url, method, sent, body, timeoutMs, auth ? secrets : null, ctx);
    this.log.push(exchange);
    if (exchange.error) {
      const kind: WebError = exchange.error === 'blocked' ? 'resolve' : exchange.error;
      throw new WebFailure(kind, url.host, url.port, exchange.error === 'blocked' ? explain(reason('reasonResolvesPrivate', { host: url.host })) : null,
        kind === 'timeout' ? String(Math.floor(timeoutMs)) : '');
    }
    const truncated = exchange.body.length > p.maxResponseBytes;
    return { url, method, status: exchange.status, reason: exchange.reason, headers: [...exchange.headers], body: truncated ? exchange.body.subarray(0, p.maxResponseBytes) : exchange.body, truncated };
  }

  private nextRecorded(method: string, url: string): WebExchange {
    const next = this.replay![this.replayAt];
    if (next && next.method === method && next.url === url) { this.replayAt++; return next; }
    throw new WebFailure('replay', '', 0, null, `${method} ${url}`);
  }

  /** `secrets` is null for a host without a credential; with one, a gzip or deflate body is decoded before the scrub. */
  private async exchange(url: WebUrl, method: string, headers: [string, string][], body: Uint8Array | null, timeoutMs: number, secrets: string[] | null, ctx: CommandContext): Promise<WebExchange> {
    const failed = (error: WebErrorKind): WebExchange => ({ method, url: url.toString(), status: 0, reason: '', headers: [], body: new Uint8Array(0), error });
    const controller = new AbortController();
    const limit = Math.min(timeoutMs, ctx.remainingMs());
    const timer = setTimeout(() => controller.abort(), Math.max(0, limit));
    // past the cap by the longest secret, so a credential cut in half at the cap is still scrubbed
    const maxBytes = this.policy.maxResponseBytes + (secrets?.[0]?.length ?? 0);
    try {
      const transport = this.policy.transport ?? defaultTransport();
      const r = await transport({ method, url: url.toString(), headers, body: body ?? undefined, timeoutMs: limit, maxBytes, signal: controller.signal });
      if (r.headers.reduce((n, [k, v]) => n + k.length + v.length + 4, 0) > MAX_HEADER_BYTES) return failed('recv');
      let data = method === 'HEAD' ? new Uint8Array(0) : r.body.length > maxBytes + 1 ? r.body.subarray(0, maxBytes + 1) : r.body;
      let responseHeaders = r.headers;
      if (secrets) {
        // the scrub must see the bytes the agent will read, not a compressed form of them
        const encoding = header(r, 'Content-Encoding')?.trim().toLowerCase();
        if (encoding !== undefined && encoding !== '' && encoding !== 'identity') {
          if (encoding !== 'gzip' && encoding !== 'x-gzip' && encoding !== 'deflate') return failed('recv');
          if (data.length > 0) {
            try { data = decompress(data, encoding !== 'deflate', maxBytes + 1); } catch { return failed('recv'); }
          }
          responseHeaders = responseHeaders.filter(([k]) => !['content-encoding', 'content-length'].includes(k.toLowerCase()));
        }
      }
      const scrub = (s: string) => (secrets ?? []).reduce((t, secret) => t.split(secret).join('[REDACTED]'), s);
      return { method, url: url.toString(), status: r.status, reason: r.reason || REASONS[r.status] || '', headers: responseHeaders.map(([k, v]) => [k, scrub(v)]), body: scrubBytes(data, secrets ?? []) };
    } catch (e) {
      if (e instanceof DeadlineExceeded) throw e;
      if (controller.signal.aborted) { ctx.checkTime(); return failed('timeout'); } // the call's own deadline ends the whole call
      const kind = (e as { kind?: unknown } | null)?.kind;
      return failed(typeof kind === 'string' && KINDS.includes(kind) ? kind as WebErrorKind : 'recv');
    } finally { clearTimeout(timer); }
  }

  /**
   * Sends `method` to `url`, following redirects when `follow` (up to `maxRedirects`, never more than the policy's), each
   * hop checked again. `onHop` sees every response, the final one included. Throws WebFailure.
   */
  async fetch(url: WebUrl, method: string, forced: string | null, headers: [string, string][], body: Uint8Array | null, follow: boolean,
    maxRedirects: number, timeoutMs: number, onHop: ((r: WebResponse) => void) | null, ctx: CommandContext): Promise<{ response: WebResponse; redirects: number }> {
    const limit = maxRedirects < 0 ? this.policy.maxRedirects : Math.min(maxRedirects, this.policy.maxRedirects);
    const first = url;
    for (let redirects = 0; ; redirects++) {
      const sameOrigin = url.scheme === first.scheme && url.host === first.host && url.port === first.port;
      const hopHeaders = sameOrigin ? headers : headers.filter(([k]) => k.toLowerCase() !== 'authorization' && k.toLowerCase() !== 'cookie');
      const response = await this.send(url, forced ?? method, hopHeaders, body, timeoutMs, ctx);
      onHop?.(response);
      const location = header(response, 'Location');
      if (!follow || ![301, 302, 303, 307, 308].includes(response.status) || !location) return { response, redirects };
      if (redirects >= limit) throw new WebFailure('redirects', url.host, url.port, null, String(limit));
      url = url.resolve(location);
      if (response.status === 303 && method !== 'HEAD' || (response.status === 301 || response.status === 302) && method === 'POST') {
        method = 'GET';
        body = null;
        headers = headers.filter(([k]) => k.toLowerCase() !== 'content-type');
      }
    }
  }
}

const reason = (key: string, values: Record<string, string> = {}) => specMessage('curl', key, values);

/** "name" or "name:port" (trimmed, lower-case, trailing dots dropped), or null when the port is not 1 to 65535. */
function hostPort(text: string): { host: string; port: number | null } | null {
  const t = text.trim().toLowerCase(), colon = t.indexOf(':');
  const host = (colon < 0 ? t : t.slice(0, colon)).replace(/\.+$/, ''), port = colon < 0 ? '' : t.slice(colon + 1);
  if (colon < 0) return { host, port: null };
  return /^[0-9]{1,5}$/.test(port) && Number(port) >= 1 && Number(port) <= 65535 ? { host, port: Number(port) } : null;
}

/** @internal gzip or zlib-wrapped deflate, cut at `max` bytes out (an output past `max` keeps its first `max` bytes).
 * Throws on a corrupt stream. */
export function decompress(data: Uint8Array, gzip: boolean, max: number): Uint8Array {
  const room = max + 65_536; // more than one block or match adds, so a stream stopped at the limit has decoded `max` bytes
  try {
    if (gzip) return gunzip(data, room).subarray(0, max);
    if (data.length < 2 || (data[0] & 0x0f) !== 8 || (data[0] * 256 + data[1]) % 31 !== 0 || data[1] & 0x20) throw new Error('not zlib');
    return inflateRaw(data.subarray(2), room).subarray(0, max);
  } catch (e) {
    if (e instanceof OutputLimitError && e.partial) return e.partial.subarray(0, max);
    throw e;
  }
}

function scrubBytes(data: Uint8Array, secrets: string[]): Uint8Array {
  const redacted = new TextEncoder().encode('[REDACTED]');
  for (const secret of secrets) {
    const needle = new TextEncoder().encode(secret);
    const parts: Uint8Array[] = [];
    let from = 0;
    for (let i = indexOf(data, needle, 0); i >= 0; i = indexOf(data, needle, from)) { parts.push(data.subarray(from, i), redacted); from = i + needle.length; }
    if (parts.length) data = concatBytes([...parts, data.subarray(from)]);
  }
  return data;
}

function indexOf(hay: Uint8Array, needle: Uint8Array, from: number): number {
  outer: for (let i = hay.indexOf(needle[0], from); i >= 0 && i + needle.length <= hay.length; i = hay.indexOf(needle[0], i + 1)) {
    for (let j = 1; j < needle.length; j++) if (hay[i + j] !== needle[j]) continue outer;
    return i;
  }
  return -1;
}

const fail = (kind: WebErrorKind, message = kind) => Object.assign(new Error(message), { kind });

// Node modules looked up at run time, so bundles for browsers carry no Node import.
const builtin = (id: string): any => (globalThis as { process?: { getBuiltinModule?: (id: string) => unknown } }).process?.getBuiltinModule?.(id);

/** @internal The network: node:http(s) with pinned addresses in Node, fetch in a browser. Throws in a Node without
 * process.getBuiltinModule: fetch there would resolve names itself, past the private-address check. */
export function defaultTransport(): WebTransport {
  if (!(globalThis as { process?: { versions?: { node?: string } } }).process?.versions?.node) return fetchTransport;
  if (!['node:https', 'node:http', 'node:dns'].every(id => builtin(id)))
    throw new Error('allowWeb needs process.getBuiltinModule (Node 22.3 or later) to check and pin addresses; or give the policy a transport');
  return nodeTransport;
}

/** The addresses `host` resolves to, once; refused when any of them is internal. */
async function resolvePinned(host: string, ip: Uint8Array | null): Promise<{ address: string; family: number }[]> {
  let addresses: { address: string; family: number }[];
  if (ip) addresses = [{ address: ip.length === 4 ? ip.join('.') : host, family: ip.length === 4 ? 4 : 6 }];
  else if (webTestHooks.resolve) addresses = webTestHooks.resolve(host).map(a => ({ address: a, family: a.includes(':') ? 6 : 4 }));
  else {
    try { addresses = await builtin('node:dns').promises.lookup(host, { all: true, verbatim: true }); } catch { throw fail('resolve'); }
  }
  if (addresses.length === 0) throw fail('resolve');
  for (const a of addresses) {
    const bytes = a.family === 6 ? parseIPv6(a.address) : parseIPv4(a.address);
    const mapped = bytes && bytes.length === 16 && bytes.subarray(0, 10).every(b => b === 0) && bytes[10] === 0xff && bytes[11] === 0xff ? bytes.subarray(12) : bytes;
    const loopback = !!mapped && (mapped.length === 4 ? mapped[0] === 127 : mapped.every((b, i) => b === (i === 15 ? 1 : 0)));
    if ((!mapped || isBlocked(mapped)) && !(webTestHooks.allowLoopback && loopback)) throw fail('blocked');
  }
  return addresses;
}

const TLS = /^(ERR_TLS|ERR_SSL|CERT_|UNABLE_TO_|DEPTH_ZERO|SELF_SIGNED|HOSTNAME_MISMATCH|ERR_CERT)/;

/** Resolves the name once, refuses it if any address is internal, and connects to an address it checked: the TLS handshake
 * and Host header still use the name, and nothing resolves it a second time (DNS rebinding). No agent (so no pooling and no
 * proxy from the environment), no cookies. */
const nodeTransport: WebTransport = async req => {
  const url = WebUrl.parse(req.url);
  const addresses = await resolvePinned(url.host, url.ip);
  if (req.signal.aborted) throw fail('timeout');
  const lib = builtin(url.scheme === 'https' ? 'node:https' : 'node:http');
  const headers: [string, string][] = [['Host', url.hostForUrl + (url.defaultPort ? '' : ':' + url.port)], ...req.headers];
  if (req.body && !req.headers.some(([k]) => k.toLowerCase() === 'content-length')) headers.push(['Content-Length', String(req.body.length)]);
  return new Promise((resolve, reject) => {
    let settled = false;
    const done = (f: () => void) => { if (!settled) { settled = true; f(); } };
    const r = lib.request({
      host: url.host, servername: url.ip ? undefined : url.host, port: url.port, path: url.pathAndQuery, method: req.method,
      headers: headers.flat(), setHost: false, agent: false, maxHeaderSize: MAX_HEADER_BYTES, signal: req.signal,
      lookup: (_name: string, opts: { all?: boolean }, cb: (...a: unknown[]) => void) =>
        opts?.all ? cb(null, addresses) : cb(null, addresses[0].address, addresses[0].family),
    }, (res: any) => {
      const raw: string[] = res.rawHeaders;
      const list: [string, string][] = [];
      for (let i = 0; i + 1 < raw.length; i += 2) list.push([raw[i], raw[i + 1]]);
      const chunks: Uint8Array[] = [];
      let total = 0;
      const finish = () => done(() => resolve({ status: res.statusCode, reason: res.statusMessage ?? '', headers: list, body: concatBytes(chunks) }));
      res.on('data', (c: Uint8Array) => {
        const take = c.subarray(0, Math.min(c.length, req.maxBytes + 1 - total));
        chunks.push(take);
        total += take.length;
        if (total > req.maxBytes) { finish(); res.destroy(); r.destroy(); }
      });
      res.on('end', finish);
      res.on('error', (e: any) => done(() => reject(classify(e, req.signal))));
      res.on('aborted', () => done(() => reject(fail(req.signal.aborted ? 'timeout' : 'recv'))));
    });
    r.on('error', (e: any) => done(() => reject(classify(e, req.signal))));
    if (req.body) r.write(req.body);
    r.end();
  });
};

function classify(e: any, signal: AbortSignal) {
  const code = String(e?.code ?? '');
  if (signal.aborted || code === 'ABORT_ERR' || code === 'ETIMEDOUT') return fail('timeout');
  if (code === 'ENOTFOUND' || code === 'EAI_AGAIN' || code === 'EAI_NONAME') return fail('resolve');
  if (code === 'ECONNREFUSED' || code === 'EHOSTUNREACH' || code === 'ENETUNREACH' || code === 'EADDRNOTAVAIL') return fail('connect');
  if (TLS.test(code)) return fail('tls');
  return fail('recv'); // HPE_HEADER_OVERFLOW (headers past 64 KiB), a reset, a malformed response
}

/** In a browser: fetch, never following redirects (an opaque redirect is a recv failure), the body streamed up to the cap.
 * The browser resolves and connects, so the private-address check of resolved names cannot happen here (IP literals are
 * still checked by the policy); headers come back lower-cased, and CORS decides what a page may read. */
const fetchTransport: WebTransport = async req => {
  let res: Response;
  try {
    res = await fetch(req.url, { method: req.method, headers: req.headers, body: req.body as Uint8Array<ArrayBuffer> | undefined, redirect: 'manual', signal: req.signal, credentials: 'omit' });
  } catch { throw fail(req.signal.aborted ? 'timeout' : 'connect'); }
  if (res.type === 'opaqueredirect') throw fail('recv');
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    const reader = res.body?.getReader();
    while (reader && total <= req.maxBytes) {
      const { done, value } = await reader.read();
      if (done) break;
      const take = value.subarray(0, Math.min(value.length, req.maxBytes + 1 - total));
      chunks.push(take);
      total += take.length;
    }
    void reader?.cancel().catch(() => undefined);
  } catch { throw fail(req.signal.aborted ? 'timeout' : 'recv'); }
  return { status: res.status, reason: res.statusText, headers: [...res.headers], body: concatBytes(chunks) };
};
