// `api`: authenticated HTTP calls the agent can make but not redirect, and whose credential it can never read.
// The agent picks an alias, an allowed method and a path under the endpoint's base URL; the host, scheme, port and
// credential are fixed by the application that registers the endpoints.

import { DeadlineExceeded, ShellError, type CommandContext } from './command.js';
import { encodeText, concatBytes } from './bytes.js';
import type { Shell } from './shell.js';

export interface ApiEndpoint {
  baseUrl: string;
  description?: string;
  /** Returns the credential header's value (e.g. `"Bearer " + token`). Called in host code for every request; never shown to the agent. */
  auth?: () => string | Promise<string>;
  /** The header `auth` fills, e.g. "X-Api-Key". */
  authHeader?: string;
  /** HTTP methods the agent may use. Read-only by default. */
  methods?: string[];
  /** Allowed path prefixes relative to the base URL, checked after resolving `.` and `..`. Undefined allows every path under it. */
  paths?: string[];
  /** Permit a plain http:// base URL (local test servers). The credential then travels unencrypted. */
  allowHttp?: boolean;
  timeoutMs?: number;
  /** Response bodies beyond this are dropped, with a note on stderr. */
  maxResponseBytes?: number;
}

// headers that cannot carry a credential, change the destination or smuggle a second request
// The reason phrase when a response carries none (HTTP/2 never does); Node's http.STATUS_CODES, trimmed to the common ones.
const REASONS: Record<number, string> = {
  200: 'OK', 201: 'Created', 202: 'Accepted', 204: 'No Content', 206: 'Partial Content',
  301: 'Moved Permanently', 302: 'Found', 303: 'See Other', 304: 'Not Modified', 307: 'Temporary Redirect', 308: 'Permanent Redirect',
  400: 'Bad Request', 401: 'Unauthorized', 403: 'Forbidden', 404: 'Not Found', 405: 'Method Not Allowed', 406: 'Not Acceptable',
  408: 'Request Timeout', 409: 'Conflict', 410: 'Gone', 412: 'Precondition Failed', 413: 'Payload Too Large', 415: 'Unsupported Media Type',
  422: 'Unprocessable Entity', 429: 'Too Many Requests',
  500: 'Internal Server Error', 501: 'Not Implemented', 502: 'Bad Gateway', 503: 'Service Unavailable', 504: 'Gateway Timeout',
};
const ALLOWED_HEADERS = ['Accept', 'Accept-Language', 'Content-Type', 'If-Match', 'If-None-Match'];
const MAX_REQUEST_BYTES = 1_000_000; // shortcut: fixed upload cap; make it an ApiEndpoint option when a host needs bigger uploads

const isToken = (s: string) => /^[A-Za-z]+$/.test(s);
const hasControl = (s: string) => /[\x00-\x1f\x7f-\x9f]/.test(s);
const printable = (s: string) => s.replace(/[\x00-\x1f\x7f-\x9f]/g, '?');
/** Like .NET's Uri.EscapeDataString: everything but unreserved characters. */
const escapeData = (s: string) => encodeURIComponent(s).replace(/[!'()*]/g, c => '%' + c.charCodeAt(0).toString(16).toUpperCase());
const badAuthHeader = (name: string) =>
  name.length === 0 || /[\x00-\x20\x7f-￿:]/.test(name) || ALLOWED_HEADERS.some(h => h.toLowerCase() === name.toLowerCase()) || name.toLowerCase() === 'host';

/** Every % starts a valid escape. */
const cleanlyEscaped = (s: string) => !/%(?![0-9A-Fa-f]{2})/.test(s);

/** Prefix match on whole segments: "/search" allows "/search" and "/search/x" but not "/searchadmin". */
const underPrefix = (path: string, prefix: string) =>
  path.startsWith(prefix) && (prefix.endsWith('/') || path.length === prefix.length || path[prefix.length] === '/');

/**
 * The URL for `path` under `base`, or null when the path could reach anything else: another origin, a place above
 * the base path, or outside the allowed prefixes.
 */
function buildUrl(base: URL, allowed: string[] | undefined, path: string, extraQuery: string[]): string | null {
  const q = path.indexOf('?');
  const rawPath = q < 0 ? path : path.slice(0, q), rawQuery = q < 0 ? '' : path.slice(q + 1);
  // a leading single slash and nothing that can start an authority, user info, fragment, or a second line
  if (!rawPath.startsWith('/') || rawPath.startsWith('//') || /[\\@#]|[\x00-\x1f\x7f-￿]/.test(path)) return null;
  if (rawQuery.includes('%') && !cleanlyEscaped(rawQuery)) return null;

  const segments: string[] = [];
  const parts = rawPath.slice(1).split('/');
  for (let i = 0; i < parts.length; i++) {
    const seg = parts[i];
    if (seg === '' && i < parts.length - 1) return null; // "//" anywhere could read as an authority
    if (seg.includes('%') && !cleanlyEscaped(seg)) return null;
    let decoded: string;
    try { decoded = decodeURIComponent(seg); } catch { return null; }
    // decoded once and checked, so %2e%2e, %2f and double-encoded (%252e) tricks cannot reach a server that decodes again
    if (/[/%\\]/.test(decoded) || hasControl(decoded)) return null;
    const dots = decoded.split(';')[0];
    if (dots === '.' || dots === '..') {
      if (dots !== decoded) return null; // "..;" is ".." to some Java servers
      if (dots === '..') { if (segments.length === 0) return null; segments.pop(); }
      if (i === parts.length - 1) segments.push(''); // "/a/." is the directory "/a/"
      continue;
    }
    segments.push(seg);
  }
  const relative = '/' + segments.join('/');
  if (allowed && allowed.length > 0 && !allowed.some(p => underPrefix(relative, p))) return null;

  const basePath = base.pathname.replace(/\/+$/, '');
  const queryText = [rawQuery, ...extraQuery].filter(s => s.length > 0).join('&');
  const text = base.origin + basePath + relative + (queryText ? '?' + queryText : '');
  let url: URL;
  try { url = new URL(text); } catch { return null; }
  // belt and braces: whatever the parsing above missed, the result must still be the same origin under the base path
  const same = url.protocol === base.protocol && url.host === base.host && url.username === '' && url.password === ''
    && (url.pathname === basePath || url.pathname.startsWith(basePath + '/'));
  return same ? url.href : null;
}

const titleCase = (name: string) => name.replace(/(^|-)([a-z])/g, (_, d, c) => d + c.toUpperCase());

/**
 * Registers `program` (e.g. "api") so the agent can call the given HTTP APIs by alias: `api get jira /issue/PROJ-1`.
 * The credential is added in host code and scrubbed from what comes back. Redirects are never followed (`redirect:
 * 'manual'`): a 3xx is reported with its Location and exit code 1. `fetchImpl` sends the requests (tests, proxies).
 */
export function registerApi(shell: Shell, program: string, endpoints: Record<string, ApiEndpoint>, fetchImpl: typeof fetch = fetch): Shell {
  const bases = new Map<string, URL>();
  for (const [alias, e] of Object.entries(endpoints)) {
    let url: URL | null = null;
    try { url = new URL(e.baseUrl); } catch { /* reported below */ }
    if (!url || (url.protocol !== 'https:' && !(e.allowHttp && url.protocol === 'http:')))
      throw new Error(`${alias}: the base URL must be an absolute https:// URL (set allowHttp for http://)`);
    if (url.username || url.password || url.search || url.hash || /[?#]/.test(e.baseUrl))
      throw new Error(`${alias}: the base URL must not contain user info, a query or a fragment`);
    if ((e.methods ?? ['GET']).some(m => !isToken(m)) || badAuthHeader(e.authHeader ?? 'Authorization'))
      throw new Error(`${alias}: invalid method or credential header`);
    bases.set(alias, url);
  }
  const first = Object.keys(endpoints)[0] ?? 'NAME';
  const methodsOf = (e: ApiEndpoint) => (e.methods ?? ['GET']).map(m => m.toUpperCase());

  const help = () => {
    let s = `Call this application's HTTP APIs. Credentials are added for you.\n\nUsage: ${program} METHOD ENDPOINT PATH [options]\n  e.g. ${program} get ${first} /path --query key=value | jq .\n\nEndpoints:\n`;
    for (const [alias, e] of Object.entries(endpoints))
      s += `  ${alias}  ${e.description ?? ''}\n      ${bases.get(alias)!.href.replace(/\/+$/, '')}  methods: ${methodsOf(e).join(', ')}` + (e.paths?.length ? `  paths: ${e.paths.join(', ')}\n` : '\n');
    s += '\nOptions:\n';
    s += '  -d, --data BODY        request body; @FILE reads a file, - reads stdin (Content-Type defaults to application/json)\n';
    s += '  -q, --query KEY=VALUE  add a query parameter (repeatable)\n';
    s += `  -H, --header 'NAME: VALUE'  one of ${ALLOWED_HEADERS.join(', ')}\n`;
    s += '  -i, --include          print the status line and response headers before the body\n';
    s += '\nThe response body goes to stdout. A status outside 2xx exits 1 with the status on stderr; redirects are not followed.\n';
    return s;
  };

  const run = async (ctx: CommandContext): Promise<number> => {
    const args = ctx.args;
    if (args.length === 0 || args.some(a => a === '--help' || a === '-h') || args[0] === 'help') { ctx.stdout.write(help()); return 0; }
    const usage = (message: string) => { ctx.stderr.write(`${program}: ${message}\nTry \`${program} --help\`.\n`); return 2; };

    const positional: string[] = [], query: string[] = [], headers: [string, string][] = [];
    let data: string | null = null, include = false;
    for (let i = 0; i < args.length; i++) {
      const arg = args[i];
      const eq = arg.indexOf('=');
      const hasInline = arg.startsWith('--') && eq > 0;
      const flag = hasInline ? arg.slice(0, eq) : arg;
      const next = (): string | null => hasInline ? arg.slice(eq + 1) : ++i < args.length ? args[i] : null;
      switch (flag) {
        case '-i': case '--include': include = true; break;
        case '-d': case '--data': {
          const v = next();
          if (v === null) return usage(`${flag} needs a value`);
          data = v;
          break;
        }
        case '-q': case '--query': {
          const kv = next();
          const at = kv?.indexOf('=') ?? -1;
          if (kv === null || at <= 0) return usage(`${flag} expects KEY=VALUE`);
          // escaped, so a value cannot add parameters, a fragment or a line break
          query.push(escapeData(kv.slice(0, at)) + '=' + escapeData(kv.slice(at + 1)));
          break;
        }
        case '-H': case '--header': {
          const hv = next();
          const colon = hv?.indexOf(':') ?? -1;
          if (hv === null || colon <= 0) return usage(`${flag} expects 'NAME: VALUE'`);
          const given = hv.slice(0, colon).trim(), value = hv.slice(colon + 1).trim();
          const name = ALLOWED_HEADERS.find(h => h.toLowerCase() === given.toLowerCase());
          if (!name) return usage(`header '${given}' is not allowed; allowed: ${ALLOWED_HEADERS.join(', ')}`);
          if (hasControl(value)) return usage('header values cannot contain control characters');
          headers.push([name, value]);
          break;
        }
        default:
          if (arg.startsWith('-') && arg.length > 1) return usage(`unknown option ${arg}`);
          positional.push(arg);
      }
    }
    if (positional.length !== 3) return usage(`expected METHOD ENDPOINT PATH, e.g. \`${program} get ${first} /path\``);
    const [methodArg, alias, path] = positional;
    const method = methodArg.toUpperCase();
    const ep = Object.hasOwn(endpoints, alias) ? endpoints[alias] : undefined;
    if (!ep) return usage(`unknown endpoint '${alias}'; endpoints: ${Object.keys(endpoints).join(', ')}`);
    if (!isToken(method) || !methodsOf(ep).includes(method)) return usage(`${method} is not allowed for ${alias}; allowed: ${methodsOf(ep).join(', ')}`);
    const url = buildUrl(bases.get(alias)!, ep.paths, path, query);
    if (url === null) return usage(`path '${printable(path)}' is not allowed for ${alias}` + (ep.paths?.length ? `; allowed: ${ep.paths.join(', ')}` : ''));

    let body: Uint8Array | null = null;
    if (data !== null) {
      try {
        body = data === '-' ? encodeText(ctx.stdinText) : data.startsWith('@') ? ctx.fs.readBytes(ctx.resolvePath(data.slice(1))) : new TextEncoder().encode(data);
      } catch (e) {
        if (e instanceof ShellError) return usage(e.message);
        throw e;
      }
      if (body.length > MAX_REQUEST_BYTES) return usage(`the request body is ${body.length} bytes; the limit is ${MAX_REQUEST_BYTES}`);
    }

    let credential: string;
    try { credential = (await ep.auth?.()) ?? ''; } catch {
      ctx.stderr.write(`${program}: the credentials for ${alias} are not available\n`); // the message may describe the secret
      return 1;
    }
    const sp = credential.indexOf(' ');
    const secrets = [...new Set([credential, sp > 0 ? credential.slice(sp + 1).trim() : ''].filter(s => s.length >= 8))].sort((a, b) => b.length - a.length); // a very short "secret" would shred ordinary output
    const scrub = (s: string) => secrets.reduce((t, secret) => t.split(secret).join('[REDACTED]'), s);

    const reqHeaders = new Headers();
    if (body !== null) reqHeaders.set('Content-Type', 'application/json');
    for (const [name, value] of headers) {
      try { reqHeaders.set(name, value); } catch { return usage(`invalid ${name} header`); }
    }
    if (credential.length > 0) {
      let ok = !hasControl(credential);
      if (ok) try { reqHeaders.set(ep.authHeader ?? 'Authorization', credential); } catch { ok = false; }
      if (!ok) { ctx.stderr.write(`${program}: the credentials for ${alias} are not a valid header value\n`); return 1; }
    }

    const timeoutMs = ep.timeoutMs ?? 30_000;
    const limitMs = Math.min(timeoutMs, ctx.remainingMs());
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), Math.max(0, limitMs));
    const maxBytes = ep.maxResponseBytes ?? 1_000_000;
    let response: Response, text: string, truncated: boolean;
    try {
      response = await fetchImpl(url, { method, headers: reqHeaders, body: body === null ? undefined : body as Uint8Array<ArrayBuffer>, redirect: 'manual', signal: controller.signal });
      // read past the cap by the longest secret, so a credential cut in half at the cap still gets scrubbed
      const limit = maxBytes + (secrets[0]?.length ?? 0);
      const chunks: Uint8Array[] = [];
      let total = 0;
      const reader = response.body?.getReader();
      while (reader && total <= limit) {
        const { done, value } = await reader.read();
        if (done) break;
        const take = value.subarray(0, Math.min(value.length, limit + 1 - total));
        chunks.push(take);
        total += take.length;
      }
      void reader?.cancel().catch(() => undefined);
      truncated = total > maxBytes;
      text = scrub(new TextDecoder().decode(concatBytes(chunks)));
      if (truncated && text.length > maxBytes) text = text.slice(0, maxBytes);
    } catch (e) {
      if (controller.signal.aborted) {
        ctx.checkTime(); // the call's own deadline ends the whole call
        ctx.stderr.write(`${program}: ${alias} did not answer within ${Number((timeoutMs / 1000).toFixed(1))} s\n`);
        return 1;
      }
      if (e instanceof DeadlineExceeded) throw e;
      const err = e as Error;
      const reason = err instanceof TypeError ? ((err.cause as Error | undefined)?.message ?? err.message) : err.constructor.name;
      ctx.stderr.write(scrub(`${program}: ${method} ${alias} ${printable(path)} failed: ${reason.replace(/\r\n|[\r\n]/g, ' ')}`) + '\n');
      return 1;
    } finally { clearTimeout(timer); }

    const status = response.status;
    const statusLine = `HTTP ${status} ${response.statusText || REASONS[status] || ''}`.trimEnd();
    if (include) {
      let s = statusLine + '\n';
      for (const [k, v] of response.headers) s += `${titleCase(k)}: ${v}\n`;
      ctx.stdout.write(scrub(s + '\n'));
    }
    ctx.stdout.write(text.length === 0 || text.endsWith('\n') ? text : text + '\n');
    if (truncated) ctx.stderr.write(`${program}: response truncated at ${maxBytes} bytes\n`);
    if (status >= 200 && status < 300) return truncated ? 1 : 0;
    ctx.stderr.write(scrub(status >= 300 && status < 400
      ? `${program}: ${statusLine} (redirect to ${response.headers.get('location') ?? 'nowhere'} not followed)`
      : `${program}: ${statusLine}`) + '\n');
    return 1;
  };

  return shell.register(program, run, `call HTTP APIs (${Object.keys(endpoints).join(', ')}); run \`${program} --help\``, `${program} METHOD ENDPOINT PATH [options]`);
}
